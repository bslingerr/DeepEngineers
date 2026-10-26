"""S9: builds the DeepEngineers release folder from the repository and a finished build. Builds nothing,
downloads nothing, writes only under --out.

 python -I tools/release/make_release.py [--out DIR] [--configuration Release] [--se-bin DIR] [--sn-bin DIR]
 [--version X] [--docs-dir DIR] [--allow-missing-docs] [--zip] [--force]

The folder (a player unzips it anywhere and runs Install.cmd, then Play.cmd):

 DeepEngineers-<version>/
 Install.cmd install.ps1 install or repair (launcher/de_install.py install; -Check, -WorldName,...)
 Play.cmd play.ps1 backups, checks, Subnautica then Space Engineers (launcher.py backup + run)
 Preset.cmd the performance preset question again (play.ps1 -Preset; launcher.py s10 --preset ask)
 Uninstall.cmd undo everything the install manifest recorded (install.ps1 -Uninstall)
 README.md NOTICE.md UNINSTALL.md
 release.json version, flavour, the pins of launcher/de_pins.py, every file with SHA-256
 SHA256SUMS.txt the same hashes for people (sha256sum -c compatible)
 plugins/ plugin.dll, DeepEngineers.Wire.dll (from the SE build), DeepEngineers.SN.dll (SN build; its
 Wire.dll must equal the SE one: one build); no pdb
 worldmod/DeepEngineers.World/ the local world mod (metadata.mod, Data/...; the PNGs as generated, checked with
 tools/worldmod/gen_planet_files.py --verify)
 world-template/DeepEngineers/ the template world (tools/release/make_template_world.py --check must pass)
 hostsave/slot/ a fresh Freedom save (gameinfo.json, global-objects.bin, scene-objects.bin, and
 screenshot.jpg, a generated picture: Subnautica's menu skips a slot without one)
 launcher/ launcher.py, launcher_paths.py, launcher_release.py, launcher_s8.py (S8: exit path, renderer
 restart, settings restore), de_install.py, de_steam.py, de_pins.py, layout_py.py, section_py.py
 (tests/wire): the release launcher needs no other module (the smoke run below proves the imports)

Checks before it writes release.json: the release-mode launcher starts (`launcher.py paths --json` reports mode
release) and de_install.py imports. Whether the DLLs carry --configuration in their AssemblyConfiguration attribute
is recorded (builtWithConfiguration); the flavour switch (DEEPENG_RELEASE) is the release build pass, and --flavour
records what the caller built (default "unverified"). `--flavour release` takes the plugins from the release-flavour
outputs of `build.ps1 -Flavor release` (src/<plugin>/bin/ReleaseFlavor/<configuration>/...) unless --se-bin/--sn-bin
name other folders; the release lint (check_release.py) compares the shipped DLLs with exactly those files.

 powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Flavor release
 python -I tools/release/make_release.py --flavour release --zip
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
LAUNCHER = os.path.join(REPO, "src", "DeepEngineers.Launcher")
WIRE_PY = os.path.join(REPO, "tests", "wire")
WORLDMOD_TOOLS = os.path.join(REPO, "tools", "worldmod")
LAUNCHER_FILES = ((LAUNCHER, "launcher.py"), (LAUNCHER, "launcher_paths.py"), (LAUNCHER, "launcher_release.py"),
                  (LAUNCHER, "launcher_s8.py"), (LAUNCHER, "de_install.py"), (LAUNCHER, "de_steam.py"),
                  (LAUNCHER, "de_pins.py"), (LAUNCHER, "de_update.py"),           # S9 round 2: the updater
                  (WORLDMOD_TOOLS, "make_scenario_world.py"),                      # S9 round 2: the Star System world copy
                  (WIRE_PY, "layout_py.py"), (WIRE_PY, "section_py.py"))
ROOT_FILES = ("Install.cmd", "install.ps1", "Play.cmd", "play.ps1", "Preset.cmd", "Uninstall.cmd")
DOC_FILES = ("README.md", "NOTICE.md", "UNINSTALL.md")
SE_FILES = ("plugin.dll", "DeepEngineers.Wire.dll")
SN_FILES = ("DeepEngineers.SN.dll",)
NOT_SHIPPED = re.compile(r"(\.pdb|\.pyc|\.user|\.log)$|(^|[\\/])(__pycache__|obj|bin)([\\/]|$)", re.I)

def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()

def project_version() -> str:
    try:
        with open(os.path.join(REPO, "Directory.Build.props"), encoding="utf-8") as f:
            m = re.search(r"<Version>([^<]+)</Version>", f.read())
        return m.group(1).strip() if m else "0.0.0"
    except OSError:
        return "0.0.0"

def built_with(dll: str, configuration: str):
    """True when the assembly's AssemblyConfigurationAttribute (the SDK writes $(Configuration) into it) is
 `configuration` (its custom-attribute blob: prolog 01 00, the packed length, the UTF-8 text, 00 00 named args);
 None when the attribute is absent."""
    try:
        with open(dll, "rb") as f:
            data = f.read()
    except OSError:
        return None
    if b"AssemblyConfigurationAttribute" not in data or not 0 < len(configuration) < 128:
        return None
    return (b"\x01\x00" + bytes([len(configuration)]) + configuration.encode("utf-8") + b"\x00\x00") in data

def find_docs(docs_dir):
    """{name: path} of README/NOTICE/UNINSTALL: --docs-dir, else tools/release/docs, else docs/release, else the
 repository root (README.md, NOTICE.md) and docs/install/UNINSTALL.md."""
    dirs = [docs_dir] if docs_dir else [os.path.join(HERE, "docs"), os.path.join(REPO, "docs", "release")]
    out = {}
    for d in dirs:
        if d and os.path.isdir(d):
            for n in DOC_FILES:
                if n not in out and os.path.isfile(os.path.join(d, n)):
                    out[n] = os.path.join(d, n)
    if not docs_dir:
        for n, p in (("README.md", os.path.join(REPO, "docs", "README.md")),  # S9-release-docs: the public README
                     ("README.md", os.path.join(REPO, "README.md")), ("NOTICE.md", os.path.join(REPO, "NOTICE.md")),
                     ("UNINSTALL.md", os.path.join(REPO, "docs", "install", "UNINSTALL.md"))):
            if n not in out and os.path.isfile(p):
                out[n] = p
    return out

def copy(src: str, dst: str):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)

def copy_tree(src: str, dst: str) -> int:
    n = 0
    for base, dirs, files in os.walk(src):
        dirs[:] = sorted(d for d in dirs if not NOT_SHIPPED.search(d + os.sep))
        for f in sorted(files):
            full = os.path.join(base, f)
            if NOT_SHIPPED.search(full):
                continue
            copy(full, os.path.join(dst, os.path.relpath(full, src)))
            n += 1
    return n

def file_list(root: str, skip=()) -> list:
    out = []
    for base, _dirs, files in os.walk(root):
        for f in files:
            full = os.path.join(base, f)
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            if rel in skip:
                continue
            out.append({"path": rel, "size": os.path.getsize(full), "sha256": sha256(full)})
    return sorted(out, key=lambda x: x["path"].lower())

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="DeepEngineers release folder (S9)")
    ap.add_argument("--out", default=None)
    ap.add_argument("--configuration", default="Release")
    ap.add_argument("--se-bin", default=None)
    ap.add_argument("--sn-bin", default=None)
    ap.add_argument("--version", default=None)
    ap.add_argument("--flavour", default=None, help="what the build is (release | dev); recorded in release.json")
    ap.add_argument("--docs-dir", default=None)
    ap.add_argument("--allow-missing-docs", action="store_true")
    ap.add_argument("--zip", action="store_true")
    ap.add_argument("--force", action="store_true", help="replace an existing release folder (one with release.json)")
    ap.add_argument("--python", default=sys.executable)
    a = ap.parse_args(argv)
    version = a.version or project_version()
    out = os.path.abspath(a.out or os.path.join(REPO, "tests", "out", "release", "DeepEngineers-%s" % version))
    flavour_dir = ("ReleaseFlavor", a.configuration) if a.flavour == "release" else (a.configuration,)
    se_bin = a.se_bin or os.path.join(REPO, "src", "DeepEngineers.SE", "bin", *flavour_dir, "net48")
    sn_bin = a.sn_bin or os.path.join(REPO, "src", "DeepEngineers.SN", "bin", *flavour_dir, "net472")
    fails = []

    def fail(text):
        fails.append(text)
        print("FAIL", text)

    # inputs
    for d, names in ((se_bin, SE_FILES), (sn_bin, SN_FILES + ("DeepEngineers.Wire.dll",))):
        for n in names:
            if not os.path.isfile(os.path.join(d, n)):
                fail("build output missing: %s (build the %s configuration first)" % (os.path.join(d, n),
                                                                                       a.configuration))
    if fails:
        return 1
    if sha256(os.path.join(se_bin, "DeepEngineers.Wire.dll")) != sha256(os.path.join(sn_bin, "DeepEngineers.Wire.dll")):
        fail("the SE and SN DeepEngineers.Wire.dll differ: both plugins must come from one build")
    gen = subprocess.run([a.python, "-I", os.path.join(REPO, "tools", "worldmod", "gen_planet_files.py"), "--verify"],
                         capture_output=True, text=True)
    if gen.returncode != 0:
        fail("world mod PNGs differ from the generator's spec: %s" % (gen.stdout + gen.stderr).strip()[-400:])
    sys.path.insert(0, HERE)
    import make_template_world as T  # noqa: E402
    probs = T.check(T.DEFAULT_OUT)
    if probs:
        fail("template world: %s (python -I tools/release/make_template_world.py)" % "; ".join(probs))
    docs = find_docs(a.docs_dir)
    missing_docs = [n for n in DOC_FILES if n not in docs]
    if missing_docs:
        (print if a.allow_missing_docs else fail)("%sdocs missing: %s (the release builder writes them; --docs-dir)" % (
            "WARN " if a.allow_missing_docs else "", ", ".join(missing_docs)))
    if fails:
        return 1
    # the folder
    if os.path.exists(out):
        if not a.force or not (os.path.isfile(os.path.join(out, "release.json")) or not os.listdir(out)):
            print("FAIL %s exists (--force replaces a previous release folder)" % out)
            return 1
        shutil.rmtree(out)
    os.makedirs(out)
    for n in ROOT_FILES:
        copy(os.path.join(HERE, "root", n), os.path.join(out, n))
    for n, p in docs.items():
        copy(p, os.path.join(out, n))
    for n in SE_FILES:
        copy(os.path.join(se_bin, n), os.path.join(out, "plugins", n))
    for n in SN_FILES:
        copy(os.path.join(sn_bin, n), os.path.join(out, "plugins", n))
    n_mod = copy_tree(os.path.join(REPO, "src", "DeepEngineers.WorldMod"), os.path.join(out, "worldmod", "DeepEngineers.World"))
    n_world = copy_tree(T.DEFAULT_OUT, os.path.join(out, "world-template", "DeepEngineers"))
    # the fresh Freedom save the installer puts into a free Subnautica slot (3 files, no cell cache: the game fills in)
    n_host = copy_tree(os.path.join(HERE, "hostsave", "slot"), os.path.join(out, "hostsave", "slot"))
    for d, n in LAUNCHER_FILES:
        copy(os.path.join(d, n), os.path.join(out, "launcher", n))
    sys.path.insert(0, LAUNCHER)
    import de_pins  # noqa: E402
    cfg = {"se": built_with(os.path.join(se_bin, "plugin.dll"), a.configuration),
           "sn": built_with(os.path.join(sn_bin, "DeepEngineers.SN.dll"), a.configuration)}
    doc = {"product": "DeepEngineers", "version": version, "configuration": a.configuration,
           "flavour": a.flavour or "unverified", "builtWithConfiguration": cfg,
           "created": datetime.datetime.utcnow().isoformat(timespec="seconds") + "Z",
           "generator": "tools/release/make_release.py", "pins": de_pins.as_dict(),
           "files": file_list(out)}
    with open(os.path.join(out, "release.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=1, sort_keys=True)
    # smoke: the release-mode launcher and the installer import and resolve
    env = dict(os.environ)
    r = subprocess.run([a.python, "-I", "-B", os.path.join(out, "launcher", "launcher.py"), "paths", "--json"],
                       capture_output=True, text=True, env=env, cwd=out)
    mode = None
    try:
        mode = json.loads(r.stdout)["mode"]
    except (ValueError, KeyError):
        pass
    if r.returncode != 0 or mode != "release":
        fail("release launcher did not start in release mode (exit %d): %s" % (r.returncode, (r.stdout + r.stderr)[-600:]))
    r2 = subprocess.run([a.python, "-I", "-B", os.path.join(out, "launcher", "de_install.py"), "--help"],
                        capture_output=True, text=True, cwd=out)
    if r2.returncode != 0:
        fail("de_install.py does not start: %s" % (r2.stdout + r2.stderr)[-600:])
    for junk in ("__pycache__",):
        p = os.path.join(out, "launcher", junk)
        if os.path.isdir(p):
            shutil.rmtree(p)
    sums = file_list(out, skip=("SHA256SUMS.txt",))
    with open(os.path.join(out, "SHA256SUMS.txt"), "w", encoding="utf-8", newline="\n") as f:
        for e in sums:
            f.write("%s *%s\n" % (e["sha256"], e["path"]))
    if a.zip and not fails:
        zpath = out + ".zip"
        with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
            for e in file_list(out):
                z.write(os.path.join(out, *e["path"].split("/")), os.path.basename(out) + "/" + e["path"])
        print("zip %s (%d bytes, sha256 %s)" % (zpath, os.path.getsize(zpath), sha256(zpath)))
    print("RELEASE %s %s: %d files (plugins %d, world mod %d, template world %d, host save %d, launcher %d, docs %d); "
          "configuration %s (DLLs built with it: %s), flavour %s" % (
              "PASS" if not fails else "FAIL", out, len(doc["files"]), len(SE_FILES) + len(SN_FILES), n_mod, n_world,
              n_host, len(LAUNCHER_FILES), len(docs), a.configuration, cfg, doc["flavour"]))
    return 0 if not fails else 1

if __name__ == "__main__":
    sys.exit(main())
