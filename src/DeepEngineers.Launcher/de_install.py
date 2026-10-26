"""DeepEngineers installer and uninstaller. Python 3.10 stdlib; started by the release folder's
Install.cmd / Uninstall.cmd (install.ps1 finds Python 3.10+ and passes the options on).

 python -I launcher/de_install.py install [--world-name NAME] [--pulsar-dir DIR] [--offline-dir DIR...]
 [--steam-id ID] [--no-preflight] [--dry-run] [--wait] [--shortcuts]
 python -I launcher/de_install.py check (the detection steps only; writes nothing)
 python -I launcher/de_install.py uninstall [--remove-worlds] [--force-worldmod] [--remove-backups] [--keep-data]
 [--dry-run]
 python -I launcher/de_install.py status (the manifest, entry by entry, against the disk)

What `install` does, one PASS/WARN/FAIL line per step (a second run repairs: every file is compared by SHA-256 and
only a missing or different one is written again):
 release every file of the release folder against release.json (SHA-256); a changed folder stops here
 processes no Space Engineers, Pulsar (Legacy) or Subnautica process (the loaders hold their DLLs open)
 steam the Steam folder (registry, else the usual folders) and its libraries (libraryfolders.vdf)
 se, sn both games found through their app manifests; the pinned Steam builds (de_pins.py: SE 24675677 =
 1.210.014; Subnautica 20241558 with changeset 83031); a different build STOPS the install with what to do
 updates both games set to "only update when I launch it" (WARN otherwise; the README says why)
 terrain Subnautica's CompiledOctreesCache equals the SE terrain gate's pinned listing (else STOP: the SE side would
 run in the terrain fail-safe, saving blocked)
 workshop Water Mod 2200451495 (version 3.23, the plugin's pin) and Text HUD API 758597413 present in the SE
 library's workshop/content/244850 (FAIL with the subscribe links otherwise). --wait (install.ps1's
 default, S9 one-click): a missing item's page opens in Steam (steam://url/CommunityFilePage/<id>), the
 player presses Subscribe, and the step polls the folder until Steam has downloaded it (15 min at most)
 options SE's Experimental mode option is on (WARN otherwise: SE refuses to load the template world's 20-minute
 day without it; the installer never changes the option)
 hostsave a Freedom Subnautica save (gameinfo.json gameMode 1) for the renderer; recorded in paths.json, which the
 release launcher turns into DEEPENGINEERS_SN_HOST_SESSION/SLOT (FAIL with the steps to create one).
 --wait: Subnautica is started through Steam (steam://run/264710) and the step waits (30 min at most)
 while the player makes the save by hand (guided, not automated: no game run has tested a scripted one),
 then until no game is running before anything is written
 pulsar Pulsar 2.4.3: an existing copy whose Legacy.exe is the pinned one, or --pulsar-dir, or the pinned zip
 (downloaded now, SHA-256 checked, cached in %LOCALAPPDATA%\\DeepEngineers\\downloads) unpacked into
 %LOCALAPPDATA%\\DeepEngineers\\Pulsar (portable; launched with -noUpdate)
 bepinex Tobey's BepInEx pack 5.4.23-pack.3.1.1 (BepInEx 5.4.23.5 x64): already installed (its marker files match),
 or the pinned zip unpacked into the Subnautica folder file by file (every file recorded; a file it would
 replace is backed up first). An other BepInEx is kept and reported (WARN)
 plugins plugin.dll + DeepEngineers.Wire.dll -> <Pulsar>\\Legacy\\Local\\DeepEngineers.SE\\;
 DeepEngineers.SN.dll + DeepEngineers.Wire.dll -> <Subnautica>\\BepInEx\\plugins\\DeepEngineers\\
 profile <Pulsar>\\Legacy\\Profiles\\Current.xml lists DeepEngineers.SE.dll under <Local> (the file is written
 when Pulsar has none yet: HYPOTHESIS, Pulsar takes a profile that exists before its first start)
 worldmod the DeepEngineers.World folder -> %APPDATA%\\SpaceEngineers\\Mods\\DeepEngineers.World (verified like
 tools/worldmod/deploy_worldmod.ps1: every file by SHA-256, extra files listed, never deleted)
 world the template world -> %APPDATA%\\SpaceEngineers\\Saves\\<SteamID64>\\<name> (SessionName and a new WorldId
 written; an existing folder of that name is never touched)
 shortcuts --shortcuts (install.ps1's default): "DeepEngineers" on the Desktop and in the Start menu (Play.cmd: backup
 then run) and "Uninstall DeepEngineers" in the Start menu, as.lnk files recorded like any other file
 (S9 one-click); the folders come from Windows (a redirected or localised Desktop is found)
 manifest %LOCALAPPDATA%\\DeepEngineers\\install\\install-manifest.json (every file, folder and entry created or
 changed, with SHA-256 and the backup of anything replaced) + paths.json for the launcher
 preflight `launcher.py backup` then `launcher.py preflight` (the S0 checks, CONTRACT 7)
Admin rights: none, unless the Steam library that holds Subnautica is not writable for the user (the default Steam
folders are); the bepinex and plugins steps then FAIL with "run Install.cmd as administrator".

`uninstall` reads the manifest backwards: files it created are deleted, files it replaced are put back, the profile
line is removed, folders it created are removed when empty, the Pulsar copy it unpacked is removed, the BepInEx it
unpacked is removed unless another mod uses it (then kept and named); the settings go back through the launcher
(`restore-settings --yes`: Water Mod volume, SpaceEngineers.cfg audio keys, Subnautica registry; `restore-registry --yes`
when only the registry backup exists). Worlds are the player's: the template copy is removed only when unchanged or
with --remove-worlds, and the world mod stays while any world lists it (CONTRACT 7). Then %LOCALAPPDATA%\\DeepEngineers
(logs, caches, the manifest) except backups/ (unless --remove-backups) and except what the folder held before the
first install.
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
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
for _p in (HERE, os.path.normpath(os.path.join(HERE, "..", "..", "tests", "wire"))):
    if os.path.isdir(_p) and _p not in sys.path:
        sys.path.insert(0, _p)

import de_pins as P  # noqa: E402
import de_steam as D  # noqa: E402
import launcher_paths as LP  # noqa: E402

GAME_PROCESSES = ("Legacy.exe", "SpaceEngineers.exe", "Subnautica.exe")
WORLD_MOD = "DeepEngineers.World"
SE_PLUGIN_DIR = ("Legacy", "Local", "DeepEngineers.SE")
SN_PLUGIN_DIR = ("BepInEx", "plugins", "DeepEngineers")
PROFILE = ("Legacy", "Profiles", "Current.xml")
PROFILE_LINE = "<string>DeepEngineers.SE.dll</string>"
PROFILE_NEW = ('<?xml version="1.0"?>\n'
               '<Profile xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
               'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
               '  <Name>Current</Name>\n  <GitHub />\n  <DevFolder />\n  <Local>\n    %s\n  </Local>\n  <Mods />\n'
               '</Profile>' % PROFILE_LINE)
SE_PLUGIN_FILES = ("plugin.dll", "DeepEngineers.Wire.dll")
SN_PLUGIN_FILES = ("DeepEngineers.SN.dll", "DeepEngineers.Wire.dll")
TEMPLATE_DIR = ("world-template", "DeepEngineers")
WORLDMOD_DIR = ("worldmod", WORLD_MOD)
NAME_BAD = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
# files BepInEx and the pack's plugins write next to themselves at run time (removed with a BepInEx we installed)
LOADER_GENERATED = re.compile(r"^BepInEx/(LogOutput\.log(\.\d+)?|cache/.*|config/.*|ErrorLog\.log|"
                              r"plugins/\.tobey\.bepinex\.pack/.*|patchers/\.tobey\.bepinex\.pack/.*|"
                              r"plugins/DeepEngineers/.*)$", re.I)
MANIFEST_FORMAT = 1
# S9-integrator: %LOCALAPPDATA%\DeepEngineers can hold an earlier DeepEngineers setup's data before the
# first install (the development PC: logs, the statics census, the original registry backup). The first install
# records those top-level names in the manifest ("localBefore") and uninstall keeps them. Not counted: the folders the
# installer owns or handles itself, and a logs\ folder holding only the installer's own logs (Install.cmd -Check).
LOCAL_OWN = ("install", "downloads", "pulsar", "backups", "python")   # python: install.ps1's interpreter (S9 one-click)
INSTALLER_LOG = re.compile(r"^(install|check|status|uninstall)-\d{8}-\d{6}\.jsonl$", re.I)

def local_entries_before(local: str) -> list:
    """Top-level names in the DeepEngineers data folder that this installer did not make (S9-integrator)."""
    try:
        names = os.listdir(local)
    except OSError:
        return []
    out = []
    for n in names:
        if n.lower() in LOCAL_OWN:
            continue
        if n.lower() == "logs":
            try:
                if all(INSTALLER_LOG.match(x) for x in os.listdir(os.path.join(local, n))):
                    continue
            except OSError:
                continue
        out.append(n)
    return sorted(out, key=str.lower)

def open_in_shell(url: str):
    """Opens a steam:// or https:// address the way a double-click would (S9 one-click: the Workshop page, Subnautica)."""
    if os.name != "nt":
        raise OSError("no Windows shell to open %s" % url)
    os.startfile(url)  # noqa: S606 (fixed steam:// addresses built from the pins)

def wait_until(ctx, pred, seconds: float):
    """pred every ctx.poll_s seconds until it returns something true (returned) or `seconds` pass (None)."""
    end = ctx.clock() + seconds
    while True:
        got = pred()
        if got:
            return got
        if ctx.clock() >= end:
            return None
        ctx.sleep(ctx.poll_s)

def utc() -> str:
    return datetime.datetime.utcnow().isoformat(timespec="seconds") + "Z"

def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()

def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

def norm(p: str) -> str:
    return os.path.normcase(os.path.normpath(p))

def unblock(path: str):
    """Removes the Mark of the Web (NTFS stream Zone.Identifier) that Explorer's zip extraction copies onto every file.NET Framework refuses Assembly.LoadFrom of a DLL marked as downloaded (HYPOTHESIS for Pulsar's loader)."""
    if os.name != "nt":
        return
    try:
        os.remove(path + ":Zone.Identifier")
    except OSError:
        pass

# ----------------------------------------------------------------------------------------------------------------
# Report
# ----------------------------------------------------------------------------------------------------------------

class Abort(Exception):
    pass

class Report:
    def __init__(self, log_dir: str = None, quiet: bool = False, command: str = "install"):
        self.rows = []
        self.quiet = quiet
        self.f = None
        if log_dir:
            try:
                os.makedirs(log_dir, exist_ok=True)
                self.path = os.path.join(log_dir, "%s-%s.jsonl" % (
                    command, datetime.datetime.now().strftime("%Y%m%d-%H%M%S")))
                self.f = open(self.path, "a", encoding="utf-8")
            except OSError:
                self.f = None

    def row(self, status: str, step: str, text: str):
        self.rows.append((status, step, text))
        if not self.quiet:
            print("%-4s %-10s %s" % (status, step, text), flush=True)
        if self.f:
            self.f.write(json.dumps({"utc": utc(), "status": status, "step": step, "text": text}) + "\n")
            self.f.flush()

    def ok(self, step, text):
        self.row("PASS", step, text)

    def warn(self, step, text):
        self.row("WARN", step, text)

    def fail(self, step, text):
        self.row("FAIL", step, text)

    def info(self, step, text):
        self.row("INFO", step, text)

    def failed(self, step: str = None) -> bool:
        return any(r[0] == "FAIL" and (step is None or r[1] == step) for r in self.rows)

    def close(self):
        if self.f:
            self.f.close()
            self.f = None

# ----------------------------------------------------------------------------------------------------------------
# Manifest
# ----------------------------------------------------------------------------------------------------------------

class Manifest:
    """install-manifest.json: every file (origin created | replaced | present, SHA-256, backup), folder, profile entry,
 loader-written file and owned tree, in install order (uninstall walks it backwards)."""

    def __init__(self, path: str):
        self.path = path
        self.doc = {"format": MANIFEST_FORMAT, "product": P.PRODUCT, "entries": []}
        if os.path.isfile(path):
            with open(path, encoding="utf-8") as f:
                doc = json.load(f)
            if doc.get("format") != MANIFEST_FORMAT:
                raise Abort("unknown manifest format %r in %s" % (doc.get("format"), path))
            self.doc = doc

    @property
    def entries(self) -> list:
        return self.doc["entries"]

    def find(self, kind: str, path: str):
        n = norm(path)
        for e in self.entries:
            if e["kind"] == kind and norm(e["path"]) == n:
                return e
        return None

    def add(self, entry: dict) -> dict:
        old = self.find(entry["kind"], entry["path"])
        if old is not None:
            # the first origin and backup stay (uninstall must restore the pre-install state); the rest is refreshed
            for k, v in entry.items():
                if k not in ("origin", "backup", "recorded"):
                    old[k] = v
            old["updated"] = utc()
            return old
        entry.setdefault("recorded", utc())
        self.entries.append(entry)
        return entry

    def remove(self, entry: dict):
        self.entries.remove(entry)

    def save(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        self.doc["updated"] = utc()
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8", newline="\n") as f:
            json.dump(self.doc, f, indent=1, sort_keys=True)
        os.replace(tmp, self.path)

# ----------------------------------------------------------------------------------------------------------------
# Context
# ----------------------------------------------------------------------------------------------------------------

class Ctx:
    """Every root the installer touches, injectable (tests run on fake folders)."""

    def __init__(self, release_root: str, env=None, *, steam_root=None, pulsar_dir=None, world_name=None,
                 steam_id=None, offline_dir=None, dry_run=False, specs=None, check_processes=True,
                 run_preflight=True, use_registry=True, python=None, terrain=None, wait=False, shortcuts=False,
                 shortcut_dirs=None, shortcut_maker=None, open_url=None, sleep=None, clock=None, poll_s=5.0,
                 wait_workshop_s=900.0, wait_host_s=1800.0):
        self.env = dict(os.environ if env is None else env)
        self.release_root = os.path.normpath(release_root)
        self.local = LP.local_data(self.env)
        self.local_before = local_entries_before(self.local)   # S9-integrator: recorded by the first install
        self.install = os.path.join(self.local, "install")
        self.downloads = os.path.join(self.local, "downloads")
        self.replaced = os.path.join(self.install, "replaced")
        self.appdata = self.env.get("APPDATA") or os.path.expanduser("~")
        self.se_appdata = os.path.join(self.appdata, "SpaceEngineers")
        self.steam_root_arg = steam_root
        self.pulsar_arg = pulsar_dir
        self.world_name = world_name
        self.steam_id_arg = steam_id
        self.offline_dirs = [offline_dir] if isinstance(offline_dir, str) else list(offline_dir or [])
        self.dry_run = dry_run
        self.specs = specs or {"pulsar": P.PULSAR, "bepinex": P.BEPINEX_PACK}
        # the terrain gate's pinned listing (tests pass a small fake one)
        self.terrain = terrain or {"files": P.SN_TERRAIN_FILES, "bytes": P.SN_TERRAIN_BYTES, "rows": P.SN_TERRAIN_ROWS,
                                   "hash": P.SN_TERRAIN_HASH, "index": P.SN_TERRAIN_INDEX_LINES}
        self.check_processes = check_processes
        self.run_preflight = run_preflight
        self.use_registry = use_registry
        self.python = python or sys.executable
        # S9 one-click: the waits and the shortcuts are install.ps1's defaults, off for direct calls and tests
        self.wait = wait
        self.shortcuts = shortcuts
        self.shortcut_dirs = dict(shortcut_dirs or {})
        self.shortcut_maker = shortcut_maker
        self.open_url = open_url or open_in_shell
        self.sleep = sleep or time.sleep
        self.clock = clock or time.monotonic
        self.poll_s = poll_s
        self.wait_workshop_s = wait_workshop_s
        self.wait_host_s = wait_host_s
        self.manifest_path = os.path.join(self.install, LP.MANIFEST_FILE)
        self.paths_path = os.path.join(self.install, LP.PATHS_FILE)
        self.release = LP.load_json(os.path.join(self.release_root, LP.RELEASE_MARKER))
        # filled by the steps
        self.steam_root = None
        self.se = None
        self.sn = None
        self.pulsar = None
        self.pulsar_owned = False
        self.host = None
        self.steam_id = None
        self.world_dir = None
        self.libraries = []

    def rel(self, *parts) -> str:
        return os.path.join(self.release_root, *parts)

# ----------------------------------------------------------------------------------------------------------------
# File operations (all recorded)
# ----------------------------------------------------------------------------------------------------------------

def ensure_dir(ctx: Ctx, man: Manifest, path: str, group: str):
    """Creates the folder and its missing parents, recording each one created (outermost first)."""
    missing = []
    p = os.path.normpath(path)
    while p and not os.path.isdir(p):
        missing.append(p)
        parent = os.path.dirname(p)
        if parent == p:
            break
        p = parent
    for d in reversed(missing):
        if not ctx.dry_run:
            os.makedirs(d, exist_ok=True)
        if man.find("dir", d) is None:
            man.add({"kind": "dir", "path": d, "group": group})

def backup_original(ctx: Ctx, path: str, group: str) -> str:
    drive, tail = os.path.splitdrive(os.path.normpath(path))
    dst = os.path.join(ctx.replaced, group, (drive.replace(":", "") or "root"), tail.lstrip("\\/"))
    if not ctx.dry_run:
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(path, dst)
    return dst

def place(ctx: Ctx, man: Manifest, dst: str, group: str, want_sha: str, writer) -> str:
    """Puts one file in place. writer(tmp_path) writes the wanted content. Returns created | replaced | repaired |
 unchanged | present. A file that exists, is not ours and differs is backed up before it is replaced."""
    entry = man.find("file", dst)
    if os.path.isfile(dst):
        have = sha256_file(dst)
        if have == want_sha:
            if entry is None:
                man.add({"kind": "file", "path": dst, "group": group, "origin": "present", "sha256": want_sha,
                         "size": os.path.getsize(dst)})
                return "present"
            entry["sha256"] = want_sha
            if not ctx.dry_run:
                unblock(dst)
            return "unchanged"
        if entry is None:
            bak = backup_original(ctx, dst, group)
            entry = man.add({"kind": "file", "path": dst, "group": group, "origin": "replaced", "backup": bak,
                             "originalSha256": have})
            status = "replaced"
        elif entry.get("origin") == "present" and have == entry.get("sha256"):
            # it was there before the install, identical then; now it is replaced: keep the pre-install copy
            entry.update(origin="replaced", backup=backup_original(ctx, dst, group), originalSha256=have)
            status = "replaced"
        else:
            status = "repaired"
    else:
        ensure_dir(ctx, man, os.path.dirname(dst), group)
        status = "repaired" if entry is not None else "created"
        if entry is None:
            entry = man.add({"kind": "file", "path": dst, "group": group, "origin": "created"})
    if not ctx.dry_run:
        tmp = dst + ".de-tmp"
        writer(tmp)
        os.replace(tmp, dst)
        unblock(dst)
        got = sha256_file(dst)
        if got != want_sha:
            raise OSError("verification failed for %s (sha256 %s, expected %s)" % (dst, got, want_sha))
    entry["sha256"] = want_sha
    entry["size"] = os.path.getsize(dst) if os.path.isfile(dst) else None
    return status

def place_file(ctx, man, src: str, dst: str, group: str) -> str:
    return place(ctx, man, dst, group, sha256_file(src), lambda tmp: shutil.copyfile(src, tmp))

def place_bytes(ctx, man, data: bytes, dst: str, group: str) -> str:
    def write(tmp):
        with open(tmp, "wb") as f:
            f.write(data)
    return place(ctx, man, dst, group, sha256_bytes(data), write)

def count(statuses) -> str:
    c = {}
    for s in statuses:
        c[s] = c.get(s, 0) + 1
    return ", ".join("%d %s" % (v, k) for k, v in sorted(c.items()))

# ----------------------------------------------------------------------------------------------------------------
# Downloads
# ----------------------------------------------------------------------------------------------------------------

def fetch(ctx: Ctx, spec: dict, rep: Report, step: str):
    """The pinned zip, verified by SHA-256: --offline-dir, the download cache, or the pinned URL. None on failure."""
    name = spec["file"]
    for cand in [os.path.join(d, name) for d in ctx.offline_dirs] + [os.path.join(ctx.downloads, name)]:
        if os.path.isfile(cand):
            if sha256_file(cand) == spec["sha256"]:
                return cand
            rep.warn(step, "%s has a different SHA-256 than the pinned %s: ignored" % (cand, spec["version"]))
    if ctx.dry_run:
        rep.info(step, "would download %s (%s bytes) from %s" % (name, spec.get("size"), spec["url"]))
        return None
    os.makedirs(ctx.downloads, exist_ok=True)
    dst = os.path.join(ctx.downloads, name)
    tmp = dst + ".part"
    manual = ("download %s by hand from %s into %s (or pass --offline-dir), then run Install.cmd again; the file "
              "must have SHA-256 %s" % (name, spec.get("page") or spec["url"], ctx.downloads, spec["sha256"]))
    limit = int(spec.get("size") or 0) + (1 << 20) if spec.get("size") else 1 << 30
    try:
        req = urllib.request.Request(spec["url"], headers={"User-Agent": "DeepEngineers-installer"})
        h = hashlib.sha256()
        total = 0
        with urllib.request.urlopen(req, timeout=60) as r, open(tmp, "wb") as f:
            while True:
                block = r.read(1 << 16)
                if not block:
                    break
                total += len(block)
                if total > limit:
                    raise OSError("more than %d bytes: not the pinned file" % limit)
                h.update(block)
                f.write(block)
    except (OSError, urllib.error.URLError, ValueError) as e:
        try:
            os.remove(tmp)
        except OSError:
            pass
        rep.fail(step, "download of %s failed (%s): %s" % (spec["url"], e, manual))
        return None
    if h.hexdigest() != spec["sha256"]:
        os.remove(tmp)
        rep.fail(step, "downloaded %s has SHA-256 %s, not the pinned %s: deleted, nothing installed; %s" % (
            name, h.hexdigest(), spec["sha256"], manual))
        return None
    os.replace(tmp, dst)
    rep.info(step, "downloaded %s (%d bytes, SHA-256 verified)" % (name, total))
    return dst

def safe_members(z: zipfile.ZipFile) -> list:
    """(member name, relative path) of the files, refusing absolute or escaping paths (zip slip)."""
    out = []
    for info in z.infolist():
        name = info.filename.replace("\\", "/")
        if name.endswith("/"):
            continue
        parts = [p for p in name.split("/") if p not in ("", ".")]
        if not parts or any(p == ".." for p in parts) or ":" in parts[0] or name.startswith("/"):
            raise Abort("unsafe path in %s: %r" % (z.filename, info.filename))
        out.append((info.filename, os.path.join(*parts)))
    return out

# ----------------------------------------------------------------------------------------------------------------
# Detection steps
# ----------------------------------------------------------------------------------------------------------------

def running_games() -> list:
    try:
        import section_py as S
    except ImportError:
        return []
    low = {n.lower() for n in GAME_PROCESSES}
    return [p for p in S.list_processes() if p["name"].lower() in low]

def step_release(ctx: Ctx, rep: Report):
    files = (ctx.release or {}).get("files")
    if not files:
        rep.warn("release", "no release.json file list in %s: the folder is not checked" % ctx.release_root)
        return
    bad = []
    for f in files:
        p = ctx.rel(*f["path"].split("/"))
        if not os.path.isfile(p):
            bad.append(f["path"] + " (missing)")
        elif sha256_file(p) != f["sha256"]:
            bad.append(f["path"] + " (changed)")
    if bad:
        rep.fail("release", "the release folder differs from its release.json: %s. Download the release again." %
                 ", ".join(bad[:8]))
        raise Abort("release folder")
    rep.ok("release", "%d files match release.json (DeepEngineers %s, %s)" % (
        len(files), ctx.release.get("version"), ctx.release.get("flavour")))
    if ctx.release.get("flavour") != "release":
        # S9-integrator: the flavour switch decides what the plugins allow (BuildFlavor); only
 # make_release.py --flavour release takes the DEEPENG_RELEASE build, and only that one passes check_release.py
        rep.warn("release", "this folder's plugins are the %r flavour, not the public release build (developer "
                            "commands and test hooks are on): use it for testing only" % ctx.release.get("flavour"))

def memory_gb():
    """(total, available) physical memory in GB through GlobalMemoryStatusEx; (None, None) when it cannot be read.
 DEEPENG_INSTALL_FAKE_MEMORY_GB="total,available" overrides it (the tests' fake machines)."""
    fake = os.environ.get("DEEPENG_INSTALL_FAKE_MEMORY_GB")
    if fake:
        try:
            a, b = fake.split(",")
            return float(a), float(b)
        except ValueError:
            return None, None
    try:
        import ctypes

        class MemoryStatusEx(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong), ("ullTotalPhys", ctypes.c_ulonglong),
                        ("ullAvailPhys", ctypes.c_ulonglong), ("ullTotalPageFile", ctypes.c_ulonglong),
                        ("ullAvailPageFile", ctypes.c_ulonglong), ("ullTotalVirtual", ctypes.c_ulonglong),
                        ("ullAvailVirtual", ctypes.c_ulonglong), ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
        m = MemoryStatusEx()
        m.dwLength = ctypes.sizeof(MemoryStatusEx)
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m)):
            return None, None
        return m.ullTotalPhys / 2 ** 30, m.ullAvailPhys / 2 ** 30
    except Exception:
        return None, None

def step_memory(ctx: Ctx, rep: Report):
    """Information only. The figures go into the report; nothing stops here."""
    total, avail = memory_gb()
    if total is None:
        rep.info("memory", "could not read the physical memory")
        return
    rep.info("memory", "%.0f GB of RAM, %.1f GB free" % (total, avail))

def step_processes(ctx: Ctx, rep: Report):
    if not ctx.check_processes:
        rep.info("processes", "not checked (test run)")
        return
    games = running_games()
    if games:
        rep.fail("processes", "close %s first (the loaders hold their files open), then run this again" % ", ".join(
            "%s (pid %d)" % (p["name"], p["pid"]) for p in games))
        raise Abort("games running")
    rep.ok("processes", "no Space Engineers, Pulsar or Subnautica process")

def step_steam(ctx: Ctx, rep: Report):
    env = dict(ctx.env)
    if ctx.steam_root_arg:
        env["DEEPENG_STEAM_ROOT"] = ctx.steam_root_arg
    root = D.steam_root(env) if (ctx.use_registry or env.get("DEEPENG_STEAM_ROOT")) else None
    if not root:
        rep.fail("steam", "Steam not found (registry HKCU\\Software\\Valve\\Steam SteamPath, Program Files): install "
                          "Steam and both games, or pass --steam-root")
        raise Abort("steam")
    ctx.steam_root = root
    libs = D.library_folders(root)
    ctx.libraries = libs
    rep.ok("steam", "%s (%d librar%s: %s)" % (root, len(libs), "y" if len(libs) == 1 else "ies", "; ".join(libs)))

def step_games(ctx: Ctx, rep: Report):
    stop = []
    ctx.se = D.find_app(ctx.libraries, P.SE_APP_ID)
    ctx.sn = D.find_app(ctx.libraries, P.SN_APP_ID)
    for step, app, name, exe in (("se", ctx.se, "Space Engineers", ("Bin64", "SpaceEngineers.exe")),
                                 ("sn", ctx.sn, "Subnautica", ("Subnautica.exe",))):
        if app is None or not app.path or not os.path.isfile(os.path.join(app.path, *exe)):
            rep.fail(step, "%s is not installed in any Steam library (%s): install it from Steam, then run this again"
                     % (name, "appmanifest_%s.acf" % (P.SE_APP_ID if step == "se" else P.SN_APP_ID)))
            stop.append(step)
    if stop:
        raise Abort("games missing")
    se_ok = ctx.se.build_id == P.SE_BUILD_ID
    if se_ok:
        rep.ok("se", "Space Engineers %s, Steam build %s (pinned) in %s" % (P.SE_GAME_VERSION_TEXT, ctx.se.build_id,
                                                                           ctx.se.path))
    else:
        rep.fail("se", "Space Engineers is Steam build %s; this DeepEngineers release needs build %s (SE %s). Wait for "
                       "a DeepEngineers release for this game version (the plugin would switch its display off on "
                       "another version); keep SE on \"only update when I launch it\"" % (
                           ctx.se.build_id or "?", P.SE_BUILD_ID, P.SE_GAME_VERSION_TEXT))
    cs = D.sn_changeset(ctx.sn.path)
    sn_ok = ctx.sn.build_id == P.SN_BUILD_ID and cs == P.SN_CHANGESET
    if sn_ok:
        rep.ok("sn", "Subnautica changeset %s, Steam build %s (pinned) in %s" % (cs, ctx.sn.build_id, ctx.sn.path))
    else:
        rep.fail("sn", "Subnautica is Steam build %s, changeset %s; this release needs build %s, changeset %s. Wait for "
                       "a DeepEngineers release for this game version; keep Subnautica on \"only update when I "
                       "launch it\"" % (ctx.sn.build_id or "?", cs or "?", P.SN_BUILD_ID, P.SN_CHANGESET))
    for name, app in (("Space Engineers", ctx.se), ("Subnautica", ctx.sn)):
        if app.auto_update != 1:
            rep.warn("updates", "%s is set to \"%s\": set it to \"only update this game when I launch it\" (Steam > "
                                "game > Properties > Updates) so an update cannot break the pinned build" % (
                                    name, D.AUTO_UPDATE_TEXT.get(app.auto_update, app.auto_update)))
        if app.state_flags not in (4, 6):
            rep.warn("updates", "%s app state flags %d: an update or download may be pending (let Steam finish)" % (
                name, app.state_flags))
    if not (se_ok and sn_ok):
        raise Abort("game builds")
    if not any(r[1] == "updates" for r in rep.rows):
        rep.ok("updates", "both games update only when launched")

def step_terrain(ctx: Ctx, rep: Report):
    tp = ctx.terrain
    r = D.terrain_source(ctx.sn.path, P.SN_OCTREE_DIR, tp["index"], P.SN_BUILD_ID)
    ok, field, found, expected = D.terrain_matches(r, tp["files"], tp["bytes"], tp["rows"], tp["hash"], P.SN_BUILD_ID)
    if ok:
        rep.ok("terrain", "Subnautica terrain source: %d batch files, %d bytes, listing hash 0x%016x (pinned)" % (
            r["files"], r["bytes"], r["hash"]))
        return
    rep.fail("terrain", "Subnautica's terrain data differs from the pinned build (%s: %s, expected %s): verify the "
                        "game files in Steam (Properties > Installed Files > Verify)" % (field, found, expected))
    raise Abort("terrain source")

WORKSHOP_PAGE = "steam://url/CommunityFilePage/%s"

def workshop_ready(ctx: Ctx, item: str) -> bool:
    """The item's folder is there (the Water Mod: with its version file readable, i.e. Steam finished the download)."""
    d = D.workshop_item_dir(ctx.se.library, P.SE_APP_ID, item)
    if not D.workshop_item_present(d):
        return False
    return item != P.WATER_MOD_ID or D.water_mod_version(d, P.WATER_MOD_VERSION_FILE)[0] is not None

def step_workshop(ctx: Ctx, rep: Report):
    absent = [(i, n) for i, n in P.WORKSHOP_ITEMS if not workshop_ready(ctx, i)]
    if absent and ctx.wait and not ctx.dry_run:
        # S9 one-click: the player's own Steam subscribes and downloads; never a copy in the release
        for item, name in absent:
            rep.info("workshop", "%s is not downloaded yet: its Workshop page opens in Steam now. Press the green "
                                 "Subscribe button there (%s)" % (name, P.workshop_url(item)))
            try:
                ctx.open_url(WORKSHOP_PAGE % item)
            except OSError as e:
                rep.warn("workshop", "could not open Steam (%s): open %s in a browser, log in and press Subscribe" % (
                    e, P.workshop_url(item)))
        rep.info("workshop", "waiting up to %d minutes for Steam to download %s into %s. If nothing arrives after a "
                             "few minutes, start Space Engineers once from Steam (the main menu is enough) and quit it" % (
                                 ctx.wait_workshop_s // 60, " and ".join(n for _i, n in absent),
                                 os.path.join(ctx.se.library, "steamapps", "workshop", "content", P.SE_APP_ID)))
        wait_until(ctx, lambda: all(workshop_ready(ctx, i) for i, _n in absent), ctx.wait_workshop_s)
    missing = []
    for item, name in P.WORKSHOP_ITEMS:
        d = D.workshop_item_dir(ctx.se.library, P.SE_APP_ID, item)
        if not D.workshop_item_present(d):
            missing.append((item, name))
            continue
        if item == P.WATER_MOD_ID:
            ver, path, err = D.water_mod_version(d, P.WATER_MOD_VERSION_FILE)
            if ver != P.WATER_MOD_VERSION:
                rep.fail("workshop", "Water Mod version %s (%s); the plugin is pinned to %s and would refuse the dive "
                                     "site with another version" % (ver or "unknown", err or path, P.WATER_MOD_VERSION))
                continue
            rep.ok("workshop", "Water Mod %s (Workshop %s)" % (ver, item))
        else:
            rep.ok("workshop", "%s (Workshop %s)" % (name, item))
    for item, name in missing:
        rep.fail("workshop", "%s is not downloaded: subscribe to it (%s), start Space Engineers once from Steam so it "
                             "downloads, then run this again" % (name, P.workshop_url(item)))

def se_experimental_mode(se_appdata: str):
    """SpaceEngineers.cfg's ExperimentalMode (True/False), None when the file or the key is absent. Read-only."""
    try:
        with open(os.path.join(se_appdata, "SpaceEngineers.cfg"), encoding="utf-8-sig", errors="replace") as f:
            text = f.read()
    except OSError:
        return None
    m = re.search(r"<Key>ExperimentalMode</Key>\s*<Value[^>]*>\s*(?:<Value[^>]*>)?\s*(True|False)\b", text, re.I)
    return None if not m else m.group(1).lower() == "true"

def step_experimental(ctx: Ctx, rep: Report):
    """The template world's 20-minute day (CONTRACT 3) is an "experimental" world setting for SE
 (MyObjectBuilder_SessionSettings.GetExperimentalReason: SunRotationIntervalMinutes <= 29), and SE refuses to load an
 experimental world while the game option Experimental mode is off (MySessionLoader, SaveGameErrorExperimental).
 The installer never changes the option (CONTRACT 1.1); it tells the player."""
    on = se_experimental_mode(ctx.se_appdata)
    if on:
        rep.ok("options", "Space Engineers' Experimental mode is on (the DeepEngineers world needs it)")
    elif on is None:
        rep.fail("options", "SpaceEngineers.cfg not found or without ExperimentalMode: start Space Engineers once and "
                            "turn on Options > Game > Experimental mode, then run Install.cmd again (SE refuses to load "
                            "the DeepEngineers world without it: its 20-minute day is an experimental setting)")
    else:
        rep.fail("options", "turn on Space Engineers' Options > Game > Experimental mode, then run Install.cmd again: "
                            "SE refuses to load the DeepEngineers world without it (its 20-minute day is an experimental "
                            "setting). The installer never changes game options itself.")

SE_WINDOW_MODES = {"0": "Window", "1": "Fullscreen window", "2": "Fullscreen"}

def se_window_mode(se_appdata: str):
    """SpaceEngineers.cfg's WindowMode ("0" Window, "1" Fullscreen window, "2" Fullscreen), None when absent. Read-only."""
    try:
        with open(os.path.join(se_appdata, "SpaceEngineers.cfg"), encoding="utf-8-sig", errors="replace") as f:
            text = f.read()
    except OSError:
        return None
    m = re.search(r"<Key>WindowMode</Key>\s*<Value[^>]*>\s*(?:<Value[^>]*>)?\s*([0-9]+)", text, re.I)
    return None if not m else m.group(1)

def step_window_mode(ctx: Ctx, rep: Report):
    """Second-PC install: SE in exclusive Fullscreen (WindowMode 2) is never composited by the desktop, so
 Subnautica's picture cannot show through it (design F), and the fallback that lowers SE below Subnautica minimises
 it instead (DXGI minimises an exclusive-fullscreen window that loses the foreground: the player sees Subnautica
 only and cannot move). The dev PC runs "Fullscreen window" (1). The installer never changes the option."""
    mode = se_window_mode(ctx.se_appdata)
    if mode == "1":
        rep.ok("options", "Space Engineers' window mode is Fullscreen window (the mode DeepEngineers needs)")
    elif mode is None:
        rep.warn("options", "SpaceEngineers.cfg has no WindowMode: set Space Engineers' Options > Display > Window mode "
                            "to \"Fullscreen window\" before playing (exclusive Fullscreen hides Subnautica's picture)")
    elif mode == "2":
        rep.fail("options", "Space Engineers' window mode is Fullscreen (exclusive): set Options > Display > Window mode "
                            "to \"Fullscreen window\", then run Install.cmd again. In exclusive fullscreen Windows cannot "
                            "show Subnautica through SE's window, and the fallback mode minimises SE instead. The "
                            "installer never changes game options itself.")
    else:
        rep.warn("options", "Space Engineers' window mode is %s: \"Fullscreen window\" (Options > Display) is the mode "
                            "DeepEngineers is built for; a window must cover the whole primary monitor" % (
                                SE_WINDOW_MODES.get(mode, mode)))

HOST_SAVE_FILES = ("gameinfo.json", "global-objects.bin", "scene-objects.bin", "screenshot.jpg")
# Subnautica's slot list reads gameinfo.json AND screenshot.jpg of every slot; a missing file makes File.ReadAllBytes throw,
# the slot's load fails and the menu skips the slot (SaveLoadManager.LoadSlotsAsync / RegisterSaveGame, UserStoragePC
# LoadFilesAsyncImpl).: the first shipped save had no screenshot and never appeared in the menu
HOST_SAVE_SHOT = "screenshot.jpg"

def free_slot_name(saved_games: str) -> str:
    """The first slotNNNN folder name absent from the SavedGames folder (Subnautica numbers its slots this way)."""
    i = 0
    while os.path.exists(os.path.join(saved_games, "slot%04d" % i)):
        i += 1
    return "slot%04d" % i

def step_hostsave(ctx: Ctx, rep: Report, man: Manifest):
    prev = LP.load_json(ctx.paths_path)
    slots = D.sn_host_slots(ctx.sn.path)
    slot, why = D.choose_host_slot(slots, P.SN_CHANGESET, P.SN_HOST_GAME_MODE_FREEDOM,
                                   slot_wanted=prev.get("snHostSlot") if prev.get("snHostSlot") and any(
                                       s["slot"] == prev.get("snHostSlot") for s in slots) else None)
    if slot is None:
        #: the release ships a fresh Freedom save (hostsave/slot: gameinfo.json, global-objects.bin,
 # scene-objects.bin; no cell cache, the game fills it in); it goes into the first free slot folder, recorded
 # like any installed file, and the install never starts Subnautica
        src = ctx.rel("hostsave", "slot")
        if all(os.path.isfile(os.path.join(src, n)) for n in HOST_SAVE_FILES):
            base = os.path.join(ctx.sn.path, "SNAppData", "SavedGames")
            name = free_slot_name(base)
            if ctx.dry_run:
                rep.info("hostsave", "%s: would install the fresh Freedom save shipped with DeepEngineers as %s" % (why, name))
            else:
                for n in HOST_SAVE_FILES:
                    s_path = os.path.join(src, n)
                    place(ctx, man, os.path.join(base, name, n), "hostsave", sha256_file(s_path),
                          lambda tmp, s_path=s_path: shutil.copyfile(s_path, tmp))
                rep.info("hostsave", "%s: the fresh Freedom save shipped with DeepEngineers installed as %s" % (why, name))
                slot, why = D.choose_host_slot(D.sn_host_slots(ctx.sn.path), P.SN_CHANGESET,
                                               P.SN_HOST_GAME_MODE_FREEDOM, slot_wanted=name)
        else:
            rep.warn("hostsave", "the release folder has no hostsave/slot save: %s" % src)
    if slot is None:
        # S9 round 2: the Subnautica plugin creates the Freedom save itself at the first Play (HostSave.CreateSave:
 # a new Freedom game, the intro once, one save, never again); the manual way stays in the README
        rep.info("hostsave", "%s. DeepEngineers makes it at the first Play: Subnautica starts a new Freedom game, plays its "
                             "intro once (about 3 minutes) and saves it; nothing to do by hand" % why)
        return
    shot = os.path.join(ctx.sn.path, "SNAppData", "SavedGames", slot["slot"], HOST_SAVE_SHOT)
    shot_src = ctx.rel("hostsave", "slot", HOST_SAVE_SHOT)
    if not os.path.isfile(shot) and os.path.isfile(shot_src):
        # a slot without its screenshot is invisible to Subnautica's menu (the 1.0.0 shipped save): add ours
        if ctx.dry_run:
            rep.info("hostsave", "%s has no %s (Subnautica's menu skips it): would add one" % (slot["slot"], HOST_SAVE_SHOT))
        else:
            place(ctx, man, shot, "hostsave", sha256_file(shot_src), lambda tmp: shutil.copyfile(shot_src, tmp))
            rep.info("hostsave", "%s had no %s, so Subnautica's menu skipped it: added" % (slot["slot"], HOST_SAVE_SHOT))
    ctx.host = slot
    note = "" if slot["changeSet"] == P.SN_CHANGESET else " (made by changeset %s)" % slot["changeSet"]
    rep.ok("hostsave", "renderer host save %s, session %s, Freedom%s (%s)" % (slot["slot"], slot["session"], note, why))

def wait_games_closed(ctx: Ctx, rep: Report):
    """--wait: a game the player started for the Workshop download or the host save must close before any write."""
    if not (ctx.wait and ctx.check_processes) or ctx.dry_run or not running_games():
        return
    rep.info("processes", "waiting for %s to close: quit the game, the install continues by itself" % ", ".join(
        p["name"] for p in running_games()))
    if wait_until(ctx, lambda: not running_games(), ctx.wait_host_s) is None:
        rep.fail("processes", "%s still running: close it, then run Install.cmd again" % ", ".join(
            p["name"] for p in running_games()))
        raise Abort("games running")
    rep.ok("processes", "no Space Engineers, Pulsar or Subnautica process")

# ----------------------------------------------------------------------------------------------------------------
# Install steps
# ----------------------------------------------------------------------------------------------------------------

def pulsar_marker_ok(path: str, spec: dict) -> bool:
    exe = os.path.join(path, spec["marker"])
    return os.path.isfile(exe) and sha256_file(exe) == spec["marker_sha256"]

def step_pulsar(ctx: Ctx, rep: Report, man: Manifest):
    spec = ctx.specs["pulsar"]
    prev = LP.load_json(ctx.paths_path)
    tree = next((e for e in man.entries if e["kind"] == "tree" and e.get("group") == "pulsar"), None)
    if ctx.pulsar_arg:
        target, owned = os.path.normpath(ctx.pulsar_arg), False
    elif tree is not None:
        target, owned = tree["path"], True
    elif prev.get("pulsar") and not prev.get("pulsarOwned") and os.path.isfile(os.path.join(prev["pulsar"], "Legacy.exe")):
        target, owned = prev["pulsar"], False
    else:
        target, owned = os.path.join(ctx.local, "Pulsar"), True
    ctx.pulsar, ctx.pulsar_owned = target, owned
    if not owned:
        if not os.path.isfile(os.path.join(target, "Legacy.exe")):
            rep.fail("pulsar", "no Legacy.exe in %s (--pulsar-dir must name a Pulsar folder)" % target)
            return
        if pulsar_marker_ok(target, spec):
            rep.ok("pulsar", "existing Pulsar %s in %s (pinned Legacy.exe)" % (spec["version"], target))
        else:
            rep.warn("pulsar", "existing Pulsar in %s is not the tested %s (Legacy.exe differs): used as it is" % (
                target, spec["version"]))
        return
    if pulsar_marker_ok(target, spec):
        if tree is None and os.path.isdir(target):
            rep.ok("pulsar", "Pulsar %s already in %s" % (spec["version"], target))
            man.add({"kind": "tree", "path": target, "group": "pulsar", "origin": "present",
                     "zipSha256": spec["sha256"]})
            return
        rep.ok("pulsar", "Pulsar %s in %s (unchanged)" % (spec["version"], target))
        return
    if os.path.isdir(target) and tree is None and os.listdir(target):
        rep.fail("pulsar", "%s exists, is not empty and is not the pinned Pulsar: move it away or pass --pulsar-dir" %
                 target)
        return
    z = fetch(ctx, spec, rep, "pulsar")
    if z is None:
        if not ctx.dry_run:
            return
        rep.info("pulsar", "would unpack Pulsar %s into %s" % (spec["version"], target))
        return
    statuses = []
    with zipfile.ZipFile(z) as zf:
        members = safe_members(zf)
        if not ctx.dry_run:
            existed = os.path.isdir(target)
            os.makedirs(target, exist_ok=True)
            if tree is None:
                man.add({"kind": "tree", "path": target, "group": "pulsar", "origin": "present" if existed else "created",
                         "zipSha256": spec["sha256"]})
        for member, rel in members:
            dst = os.path.join(target, rel)
            data = zf.read(member)
            if os.path.isfile(dst) and sha256_file(dst) == sha256_bytes(data):
                statuses.append("unchanged")
                continue
            if not ctx.dry_run:
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                with open(dst, "wb") as f:
                    f.write(data)
                unblock(dst)
            statuses.append("would write" if ctx.dry_run else "written")
    rep.ok("pulsar", "Pulsar %s %s %s (%s)" % (spec["version"], "would be unpacked into" if ctx.dry_run else
                                                 "unpacked into", target, count(statuses)))

def bepinex_state(sn_dir: str, spec: dict) -> str:
    """pinned | other | absent."""
    marks = spec["markers"]
    present = [rel for rel in marks if os.path.isfile(os.path.join(sn_dir, *rel.split("/")))]
    if not present and not os.path.isdir(os.path.join(sn_dir, "BepInEx", "core")):
        return "absent"
    if len(present) == len(marks) and all(sha256_file(os.path.join(sn_dir, *rel.split("/"))) == h
                                          for rel, h in marks.items()):
        return "pinned"
    return "other"

def step_bepinex(ctx: Ctx, rep: Report, man: Manifest):
    spec = ctx.specs["bepinex"]
    sn_dir = ctx.sn.path
    ours = [e for e in man.entries if e.get("group") == "bepinex" and e["kind"] == "file"]
    state = bepinex_state(sn_dir, spec)
    if state == "pinned" and not ours:
        rep.ok("bepinex", "%s (BepInEx %s) already installed in %s" % (spec["name"], spec["bepinex"], sn_dir))
        return
    if state == "other" and not ours:
        rep.warn("bepinex", "another BepInEx is installed in %s (not %s): kept as it is; DeepEngineers was tested "
                            "with BepInEx %s x64" % (sn_dir, spec["version"], spec["bepinex"]))
        if not os.path.isfile(os.path.join(sn_dir, "steam_appid.txt")):
            # the launcher starts Subnautica directly (CONTRACT 7); without the pack's steam_appid.txt (264710) Steam's
 # DRM restarts the game through Steam and the launcher loses the process it started (HYPOTHESIS)
            rep.warn("bepinex", "no steam_appid.txt in %s: the launcher starts Subnautica directly and needs it (a file "
                                "holding 264710, as Tobey's pack ships it)" % sn_dir)
        return
    z = fetch(ctx, spec, rep, "bepinex")
    if z is None:
        if ctx.dry_run:
            rep.info("bepinex", "would unpack %s into %s" % (spec["name"], sn_dir))
        return
    statuses = []
    try:
        with zipfile.ZipFile(z) as zf:
            for member, rel in safe_members(zf):
                statuses.append(place_bytes(ctx, man, zf.read(member), os.path.join(sn_dir, rel), "bepinex"))
    except PermissionError as e:
        rep.fail("bepinex", "cannot write into %s (%s): run Install.cmd as administrator for this step" % (sn_dir, e))
        return
    if man.find("loader-dir", os.path.join(sn_dir, "BepInEx")) is None and any(
            e["kind"] == "dir" and norm(e["path"]) == norm(os.path.join(sn_dir, "BepInEx")) for e in man.entries):
        man.add({"kind": "loader-dir", "path": os.path.join(sn_dir, "BepInEx"), "group": "bepinex"})
    rep.ok("bepinex", "%s (BepInEx %s x64) in %s (%s)" % (spec["name"], spec["bepinex"], sn_dir, count(statuses)))

def step_plugins(ctx: Ctx, rep: Report, man: Manifest):
    src = ctx.rel("plugins")
    plan = [(os.path.join(src, f), os.path.join(ctx.pulsar, *SE_PLUGIN_DIR, f), "plugins-se") for f in SE_PLUGIN_FILES]
    # S9-integrator: a dry run on a PC without BepInEx plans the plugin like the Pulsar check below
 # (the bepinex step only said it would unpack the pack); a real run still needs the folder
    if not os.path.isdir(os.path.join(ctx.sn.path, "BepInEx")) and not ctx.dry_run:
        rep.fail("plugins", "BepInEx is not installed in %s: the Subnautica plugin needs it (see the bepinex step)" %
                 ctx.sn.path)
        plan_sn = []
    else:
        plan_sn = [(os.path.join(src, f), os.path.join(ctx.sn.path, *SN_PLUGIN_DIR, f), "plugins-sn")
                   for f in SN_PLUGIN_FILES]
    if not ctx.pulsar or (not os.path.isdir(ctx.pulsar) and not ctx.dry_run):
        rep.fail("plugins", "no Pulsar folder: the SE plugin is not installed (see the pulsar step)")
        plan = []
    statuses = []
    for s, d, group in plan + plan_sn:
        if not os.path.isfile(s):
            rep.fail("plugins", "missing in the release folder: %s" % s)
            continue
        try:
            statuses.append(place_file(ctx, man, s, d, group))
        except PermissionError as e:
            rep.fail("plugins", "cannot write %s (%s): run Install.cmd as administrator for this step" % (d, e))
    if plan_sn:
        man.add({"kind": "loader-file", "path": os.path.join(ctx.sn.path, "BepInEx", "config", "deepengineers.sn.cfg"),
                 "group": "plugins-sn"})
    if statuses and not rep.failed("plugins"):
        rep.ok("plugins", "SE plugin -> %s; Subnautica plugin -> %s (%s)" % (
            os.path.join(ctx.pulsar, *SE_PLUGIN_DIR), os.path.join(ctx.sn.path, *SN_PLUGIN_DIR), count(statuses)))

def step_profile(ctx: Ctx, rep: Report, man: Manifest):
    if not ctx.pulsar or not os.path.isdir(ctx.pulsar):
        if ctx.dry_run:
            rep.info("profile", "would write %s" % os.path.join(ctx.pulsar or "<Pulsar>", *PROFILE))
            return
        rep.fail("profile", "no Pulsar folder")
        return
    path = os.path.join(ctx.pulsar, *PROFILE)
    if not os.path.isfile(path):
        st = place_bytes(ctx, man, PROFILE_NEW.encode("utf-8"), path, "profile")
        rep.ok("profile", "Pulsar profile written with DeepEngineers.SE.dll enabled (%s): %s" % (st, path))
        return
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    if PROFILE_LINE in text:
        if man.find("entry", path) is None and man.find("file", path) is None:
            man.add({"kind": "entry", "path": path, "group": "profile", "text": PROFILE_LINE, "origin": "present"})
        rep.ok("profile", "DeepEngineers.SE.dll already enabled in %s" % path)
        return
    if re.search(r"<Local\s*/>", text):
        new = re.sub(r"<Local\s*/>", "<Local>\n    %s\n  </Local>" % PROFILE_LINE, text, count=1)
        how = "local-empty"
    elif "<Local>" in text:
        new = text.replace("<Local>", "<Local>\n    %s" % PROFILE_LINE, 1)
        how = "inserted"
    else:
        rep.fail("profile", "%s has no <Local> element: enable DeepEngineers.SE.dll in Pulsar's plugin menu" % path)
        return
    bak = backup_original(ctx, path, "profile")
    if not ctx.dry_run:
        tmp = path + ".de-tmp"
        with open(tmp, "w", encoding="utf-8", newline="") as f:
            f.write(new)
        os.replace(tmp, path)
    man.add({"kind": "entry", "path": path, "group": "profile", "text": PROFILE_LINE, "how": how, "origin": "created",
             "backup": bak})
    rep.ok("profile", "DeepEngineers.SE.dll enabled in %s" % path)

def tree_files(root: str) -> list:
    out = []
    for base, _dirs, files in os.walk(root):
        for n in sorted(files):
            full = os.path.join(base, n)
            out.append((full, os.path.relpath(full, root)))
    return sorted(out, key=lambda x: x[1].lower())

def step_worldmod(ctx: Ctx, rep: Report, man: Manifest):
    src = ctx.rel(*WORLDMOD_DIR)
    dst = os.path.join(ctx.se_appdata, "Mods", WORLD_MOD)
    files = tree_files(src)
    if not files:
        rep.fail("worldmod", "the release folder has no %s" % src)
        return
    statuses = [place_file(ctx, man, s, os.path.join(dst, rel), "worldmod") for s, rel in files]
    want = {rel.lower() for _s, rel in files}
    extra = [rel for _f, rel in tree_files(dst) if rel.lower() not in want] if os.path.isdir(dst) else []
    if extra:
        rep.warn("worldmod", "extra files in %s (not in this release; left in place): %s" % (dst, ", ".join(extra)))
    rep.ok("worldmod", "%s: %d files verified by SHA-256 (%s)" % (dst, len(files), count(statuses)))

def valid_world_name(name: str):
    name = (name or "").strip().rstrip(". ")
    if not name or len(name) > 64 or NAME_BAD.search(name) or name.upper() in ("CON", "PRN", "AUX", "NUL"):
        return None
    return name

def xml_escape(s: str) -> str:
    return (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")
            .replace("'", "&apos;"))

def patch_world_text(name: str, text: str, session_name: str, world_id: str) -> str:
    if name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        text = re.sub(r"<SessionName>[^<]*</SessionName>", "<SessionName>%s</SessionName>" % xml_escape(session_name),
                      text, count=1)
    if name == "Sandbox.sbc":
        text = re.sub(r"<WorldId>[^<]*</WorldId>", "<WorldId>%s</WorldId>" % world_id, text, count=1)
    return text

def step_world(ctx: Ctx, rep: Report, man: Manifest):
    src = ctx.rel(*TEMPLATE_DIR)
    if not os.path.isdir(src):
        rep.fail("world", "the release folder has no template world (%s)" % src)
        return
    saves = os.path.join(ctx.se_appdata, "Saves")
    if ctx.steam_id_arg:
        sid, how = ctx.steam_id_arg, "--steam-id"
    else:
        active = D.active_steam_user_from_registry() if ctx.use_registry else None
        sid, how = D.choose_steam_user(saves, active, D.steam_user_ids(ctx.steam_root))
    if not sid or not sid.isdigit():
        rep.fail("world", "cannot tell which Steam user's saves folder to use (%s): start Steam, log in, or pass "
                          "--steam-id" % how)
        return
    ctx.steam_id = sid
    existing = next((e for e in man.entries if e["kind"] == "world"), None)
    name = ctx.world_name or (existing or {}).get("name") or "DeepEngineers"
    good = valid_world_name(name)
    if good is None:
        rep.fail("world", "%r is not a usable folder name (no < > : \" / \\ | ? *, at most 64 characters)" % name)
        return
    dst = os.path.join(saves, sid, good)
    ctx.world_dir = dst
    if os.path.isdir(dst):
        mine = man.find("world", dst) is not None
        rep.ok("world", "world %r exists in %s: %s" % (
            good, os.path.dirname(dst), "kept as it is (installed earlier; worlds are never overwritten)" if mine else
            "not ours, left untouched; choose another name with -WorldName for a fresh DeepEngineers world"))
        return
    world_id = str(uuid.uuid4())
    statuses = []
    for s, rel in tree_files(src):
        with open(s, "rb") as f:
            data = f.read()
        base = os.path.basename(rel)
        if base in ("Sandbox.sbc", "Sandbox_config.sbc"):
            data = patch_world_text(base, data.decode("utf-8-sig"), good, world_id).encode("utf-8")
        statuses.append(place_bytes(ctx, man, data, os.path.join(dst, rel), "world"))
    man.add({"kind": "world", "path": dst, "group": "world", "name": good, "worldId": world_id, "steamId": sid,
             "origin": "created"})
    rep.ok("world", "world %r -> %s (%s; Steam user %s from %s). In SE: Load Game > %s; the first load asks to "
                    "convert the world from XML (click OK) and can take a minute" % (
                        good, dst, count(statuses), sid, how, good))

# S9 one-click: (where, path under it, release file it starts, description)
SHORTCUTS = (("desktop", "DeepEngineers.lnk", "Play.cmd",
              "DeepEngineers: backups, checks, then Subnautica (hidden renderer) and Space Engineers"),
             ("programs", os.path.join("DeepEngineers", "DeepEngineers.lnk"), "Play.cmd",
              "DeepEngineers: backups, checks, then Subnautica (hidden renderer) and Space Engineers"),
             ("programs", os.path.join("DeepEngineers", "Uninstall DeepEngineers.lnk"), "Uninstall.cmd",
              "Remove everything the DeepEngineers installer recorded (UNINSTALL.md)"))
# WScript.Shell writes the.lnk files into a temporary folder (the installer then places them like any file) and reads
# the target of a shortcut already at the destination; [Environment]:GetFolderPath finds a redirected or localised
# Desktop and Start menu. Job and result travel as UTF-8 JSON files (no console code page in between).
SHORTCUT_PS = r"""
$ErrorActionPreference = 'Stop'
$job = Get-Content -LiteralPath $args[0] -Raw -Encoding UTF8 | ConvertFrom-Json
$base = @{ desktop = [Environment]::GetFolderPath('Desktop'); programs = [Environment]::GetFolderPath('Programs') }
if ($job.desktopDir) { $base.desktop = $job.desktopDir }
if ($job.programsDir) { $base.programs = $job.programsDir }
$sh = New-Object -ComObject WScript.Shell
$out = @()
foreach ($l in $job.links) {
    $dest = Join-Path $base[$l.where] $l.rel
    $existing = ''
    if (Test-Path -LiteralPath $dest) { try { $existing = $sh.CreateShortcut($dest).TargetPath } catch { $existing = '' } }
    $s = $sh.CreateShortcut($l.tmp)
    $s.TargetPath = $l.target
    $s.WorkingDirectory = $l.workdir
    $s.Description = $l.description
    $s.Save()
    $out += @{ dest = $dest; existingTarget = $existing }
}
ConvertTo-Json -InputObject @{ links = $out } -Depth 4 | Set-Content -LiteralPath $args[1] -Encoding UTF8
"""

def make_shortcuts_ps(job: dict, workdir: str) -> dict:
    ps1 = os.path.join(workdir, "shortcuts.ps1")
    jin, jout = os.path.join(workdir, "job.json"), os.path.join(workdir, "result.json")
    with open(ps1, "w", encoding="ascii", newline="\r\n") as f:
        f.write(SHORTCUT_PS)
    with open(jin, "w", encoding="utf-8") as f:
        json.dump(job, f)
    r = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ps1,
                        jin, jout], capture_output=True, timeout=120)
    if r.returncode != 0 or not os.path.isfile(jout):
        raise OSError("the PowerShell shortcut helper exited %d: %s" % (
            r.returncode, (r.stdout + r.stderr).decode("utf-8", "replace").strip()[-300:]))
    with open(jout, encoding="utf-8-sig") as f:
        return json.load(f)

STARSYSTEM_WORLD = "DeepEngineers 4546B"

def step_starsystem(ctx: Ctx, rep: Report, man: Manifest):
    """S9 round 2: the
 Space Engineers plugin swaps the Star System scenario folder for %LOCALAPPDATA%\DeepEngineers\worlds\<STARSYSTEM_WORLD>
 (S9StarSystem); that copy is built here from the player's own Star System files plus the template's planet entity
 (tools/worldmod/make_scenario_world.py, shipped in launcher/), listing the local world mod, the Water Mod and the
 Text HUD API (the two Workshop items Space Engineers fetches itself at the world's load)."""
    tool = os.path.join(HERE, "make_scenario_world.py")
    template = ctx.rel(*TEMPLATE_DIR)
    content = os.path.join(ctx.se.path, "Content") if ctx.se and ctx.se.path else None
    dst = os.path.join(ctx.local, "worlds", STARSYSTEM_WORLD)
    if not os.path.isfile(tool):
        rep.warn("starsystem", "the release folder has no launcher/make_scenario_world.py: the Star System card keeps the game's own world (Load Game > %s still works)" % (ctx.world_name or "DeepEngineers"))
        return
    if not content or not os.path.isdir(os.path.join(content, "CustomWorlds", "Star System")):
        rep.warn("starsystem", "Space Engineers' own Star System world was not found under %s: the card keeps the game's own world (Load Game > %s still works)" % (content, ctx.world_name or "DeepEngineers"))
        return
    if ctx.dry_run:
        rep.info("starsystem", "would build %s from %s and the template (dry run)" % (dst, os.path.join(content, "CustomWorlds", "Star System")))
        return
    cmd = [sys.executable, "-I", "-B", tool, "--se-content", content, "--template", template, "--local-root", ctx.local,
           "--to-local", "--local-mod", "--force", "--out", os.path.join(ctx.local, "worlds", "_build", STARSYSTEM_WORLD)]
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
    except (OSError, subprocess.SubprocessError) as e:
        rep.fail("starsystem", "the world copy could not be built (%s): the card keeps the game's own world" % e)
        return
    last = (out.stdout or "").strip().splitlines()[-1:] or [""]
    if out.returncode != 0 or not os.path.isfile(os.path.join(dst, "Sandbox.sbc")):
        rep.fail("starsystem", "the world copy failed (exit %d): %s" % (out.returncode, ((out.stderr or "").strip().splitlines() or last or ["?"])[-1]))
        return
    man.add({"kind": "world-local", "path": dst, "group": "world", "name": STARSYSTEM_WORLD})
    rep.ok("starsystem", "Star System card -> %s (SE's planets plus 4546B and its capsule spawn; %s)" % (dst, last[0]))

def step_shortcuts(ctx: Ctx, rep: Report, man: Manifest):
    if not ctx.shortcuts:
        return
    if ctx.dry_run:
        rep.info("shortcuts", "would create DeepEngineers on the Desktop and in the Start menu (+ Uninstall DeepEngineers)")
        return
    tmp = tempfile.mkdtemp(prefix="de-shortcuts-")
    try:
        links = [{"where": w, "rel": rel, "target": ctx.rel(target), "workdir": ctx.release_root, "description": text,
                  "tmp": os.path.join(tmp, "%d.lnk" % i)} for i, (w, rel, target, text) in enumerate(SHORTCUTS)]
        res = (ctx.shortcut_maker or make_shortcuts_ps)(
            {"desktopDir": ctx.shortcut_dirs.get("desktop"), "programsDir": ctx.shortcut_dirs.get("programs"),
             "links": links}, tmp)
        got = res["links"] if isinstance(res["links"], list) else [res["links"]]   # one element: ConvertTo-Json unwraps
        statuses, dests = [], []
        for link, g in zip(links, got):
            dest = g["dest"]
            dests.append(dest)
            entry = man.find("file", dest)
            if os.path.isfile(dest) and norm(g.get("existingTarget") or "?") == norm(link["target"]):
                # ours already (same target): left as it is, recorded (Windows rewrites.lnk details by itself)
                if entry is None:
                    man.add({"kind": "file", "path": dest, "group": "shortcuts", "origin": "created",
                             "sha256": sha256_file(dest), "size": os.path.getsize(dest)})
                else:
                    entry["sha256"] = sha256_file(dest)
                statuses.append("unchanged")
                continue
            with open(link["tmp"], "rb") as f:
                statuses.append(place_bytes(ctx, man, f.read(), dest, "shortcuts"))
        if len(got) != len(links):
            raise OSError("the helper made %d of %d shortcuts" % (len(got), len(links)))
        rep.ok("shortcuts", "%s (%s)" % ("; ".join(dests), count(statuses)))
    except (OSError, subprocess.SubprocessError, ValueError, KeyError, TypeError) as e:
        rep.warn("shortcuts", "the Desktop and Start-menu entries could not be made (%s): start DeepEngineers with "
                              "Play.cmd in %s" % (e, ctx.release_root))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

def write_paths(ctx: Ctx, rep: Report):
    doc = {"version": 1, "written": utc(), "steamRoot": ctx.steam_root, "seInstall": ctx.se.path,
           "seBin64": os.path.join(ctx.se.path, "Bin64"), "seLibrary": ctx.se.library, "subnautica": ctx.sn.path,
           "pulsar": ctx.pulsar, "pulsarOwned": ctx.pulsar_owned, "seAppData": ctx.se_appdata,
           "steamUserId": ctx.steam_id, "world": ctx.world_dir,
           "snHostSession": (ctx.host or {}).get("session"), "snHostSlot": (ctx.host or {}).get("slot"),
           "release": ctx.release_root, "releaseVersion": (ctx.release or {}).get("version")}
    if ctx.dry_run:
        rep.info("manifest", "would write %s" % ctx.paths_path)
        return
    os.makedirs(ctx.install, exist_ok=True)
    tmp = ctx.paths_path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=1, sort_keys=True)
    os.replace(tmp, ctx.paths_path)

def step_preflight(ctx: Ctx, rep: Report):
    launcher = ctx.rel("launcher", "launcher.py")
    if not ctx.run_preflight:
        rep.info("preflight", "skipped (--no-preflight)")
        return
    if ctx.dry_run or not os.path.isfile(launcher):
        rep.info("preflight", "not run (%s)" % ("dry run" if ctx.dry_run else "no launcher.py"))
        return
    for verb in (["backup"], ["preflight"]):
        print("---- launcher.py %s" % " ".join(verb), flush=True)
        rc = subprocess.call([ctx.python, "-I", "-B", launcher] + verb, cwd=ctx.release_root)
        if rc != 0:
            rep.fail("preflight", "launcher.py %s exit %d (see the lines above; a FAIL there is fixed before Play)" % (
                " ".join(verb), rc))
            return
    rep.ok("preflight", "backups made and the S0 checks passed (launcher.py preflight)")

def install(ctx: Ctx, rep: Report) -> int:
    man = Manifest(ctx.manifest_path)
    if ctx.dry_run:
        rep.info("dry-run", "nothing is written: the lines below say what an install would do")
    try:
        step_release(ctx, rep)
        step_processes(ctx, rep)
        step_memory(ctx, rep)      # R2: before any write
        step_steam(ctx, rep)
        step_games(ctx, rep)
        step_terrain(ctx, rep)
    except Abort:
        rep.row("STOP", "install", "nothing was installed or changed")
        return 2
    step_workshop(ctx, rep)
    step_experimental(ctx, rep)
    step_window_mode(ctx, rep)
    step_hostsave(ctx, rep, man)
    try:
        wait_games_closed(ctx, rep)   # S9 one-click: a game the player started during the waits
    except Abort:
        rep.row("STOP", "install", "nothing was installed or changed")
        return 2
    man.doc.setdefault("installed", utc())
    man.doc.setdefault("localBefore", ctx.local_before)   # S9-integrator: kept by uninstall (first install only)
    man.doc["release"] = {"root": ctx.release_root, "version": (ctx.release or {}).get("version")}
    try:
        step_pulsar(ctx, rep, man)
        step_bepinex(ctx, rep, man)
        step_plugins(ctx, rep, man)
        step_profile(ctx, rep, man)
        step_worldmod(ctx, rep, man)
        step_world(ctx, rep, man)
        step_starsystem(ctx, rep, man)   # S9 round 2: New Game > Star System > Start lands on 4546B
        step_shortcuts(ctx, rep, man)
    except (OSError, Abort, zipfile.BadZipFile) as e:
        rep.fail("install", "stopped: %s (what was done so far is in the manifest; run Install.cmd again to repair)" % e)
    finally:
        if not ctx.dry_run:
            man.save()
    write_paths(ctx, rep)
    rep.ok("manifest", "%s (%d entries)%s" % (ctx.manifest_path, len(man.entries), " (dry run: not written)"
                                              if ctx.dry_run else ""))
    step_preflight(ctx, rep)
    fails = [r for r in rep.rows if r[0] == "FAIL"]
    print("", flush=True)
    print("INSTALL %s (%d FAIL, %d WARN)%s" % ("PASS" if not fails else "FAIL", len(fails),
                                              sum(1 for r in rep.rows if r[0] == "WARN"),
                                              "" if not fails else ": fix the FAIL lines and run Install.cmd again"),
          flush=True)
    return 0 if not fails else 1

def check(ctx: Ctx, rep: Report) -> int:
    ctx.dry_run = True
    man = Manifest(ctx.manifest_path)
    try:
        step_release(ctx, rep)
        step_processes(ctx, rep)
        step_memory(ctx, rep)      # R2: before any write
        step_steam(ctx, rep)
        step_games(ctx, rep)
        step_terrain(ctx, rep)
    except Abort:
        return 2
    step_workshop(ctx, rep)
    step_experimental(ctx, rep)
    step_window_mode(ctx, rep)
    step_hostsave(ctx, rep, man)
    return 1 if rep.failed() else 0

# ----------------------------------------------------------------------------------------------------------------
# Uninstall
# ----------------------------------------------------------------------------------------------------------------

def _remove_file(ctx, path: str):
    if not ctx.dry_run:
        os.remove(path)

def _rmdir_if_empty(ctx, path: str) -> bool:
    try:
        if os.path.isdir(path) and not os.listdir(path):
            if not ctx.dry_run:
                os.rmdir(path)
            return True
    except OSError:
        pass
    return False

def other_de_worlds(ctx: Ctx, kept_worlds) -> list:
    """Worlds outside this install that list DeepEngineers.World (they need the world mod)."""
    out = []
    saves = os.path.join(ctx.se_appdata, "Saves")
    kept = {norm(w) for w in kept_worlds}
    for user in (os.scandir(saves) if os.path.isdir(saves) else ()):
        if not user.is_dir():
            continue
        for w in os.scandir(user.path):
            if not w.is_dir() or norm(w.path) in kept:
                continue
            for n in ("Sandbox_config.sbc", "Sandbox.sbc"):
                try:
                    with open(os.path.join(w.path, n), encoding="utf-8", errors="replace") as f:
                        if WORLD_MOD in f.read():
                            out.append(w.path)
                            break
                except OSError:
                    continue
    return out

def loader_foreign_files(bep_root: str, man: Manifest) -> list:
    """Files under <Subnautica>\\BepInEx that are neither recorded nor written by the loader for itself, our plugin
 or the pack: another mod's files (their presence keeps the BepInEx we installed)."""
    out = []
    if not os.path.isdir(bep_root):
        return out
    sn_dir = os.path.dirname(bep_root)
    recorded = {norm(e["path"]) for e in man.entries if e["kind"] == "file"}
    for full, rel in tree_files(bep_root):
        relp = ("BepInEx/" + rel).replace("\\", "/")
        if norm(full) in recorded or LOADER_GENERATED.match(relp):
            continue
        out.append(os.path.relpath(full, sn_dir))
    return out

def uninstall(ctx: Ctx, rep: Report, remove_worlds=False, force_worldmod=False, remove_backups=False,
              keep_data=False) -> int:
    try:
        step_processes(ctx, rep)
    except Abort:
        return 2
    if not os.path.isfile(ctx.manifest_path):
        rep.fail("manifest", "no install manifest at %s: nothing recorded to remove (see UNINSTALL.md for the manual "
                             "steps)" % ctx.manifest_path)
        return 1
    man = Manifest(ctx.manifest_path)
    rep.ok("manifest", "%s (%d entries, installed %s)" % (ctx.manifest_path, len(man.entries),
                                                           man.doc.get("installed")))
    # 1. settings: the Subnautica registry values (launcher backup), a pending Water Mod volume restore
    launcher = ctx.rel("launcher", "launcher.py")
    has_settings = os.path.isfile(os.path.join(ctx.local, "settings-backup.json")) and os.path.isfile(
        ctx.rel("launcher", "launcher_s8.py"))
    has_registry = os.path.isfile(os.path.join(ctx.local, "registry-backup.json"))
    # S8-settings (launcher_s8.py restore-settings): the Water Mod volume, the SpaceEngineers.cfg audio keys and the
 # Subnautica registry values from the launcher's backups; restore-registry alone when only that backup exists
    verb = ["restore-settings", "--yes"] if has_settings else (["restore-registry", "--yes"] if has_registry else None)
    if not ctx.run_preflight or not os.path.isfile(launcher) or verb is None:
        rep.info("settings", "no settings backup (DeepEngineers never started a session here): nothing to restore")
    elif ctx.dry_run:
        rep.info("settings", "would run launcher.py %s" % " ".join(verb))
    else:
        rc = subprocess.call([ctx.python, "-I", "-B", launcher] + verb, cwd=ctx.release_root)
        (rep.ok if rc == 0 else rep.fail)("settings", "settings restored from the launcher's backups (launcher.py %s)" %
                                          " ".join(verb) if rc == 0 else "launcher.py %s exit %d" % (" ".join(verb), rc))
    pending = os.path.join(ctx.local, "restore.json")
    if os.path.isfile(pending):
        rep.warn("settings", "%s is pending (a session did not end cleanly): start Space Engineers once with "
                             "DeepEngineers before uninstalling so the plugin writes the Water Mod volume back, or set "
                             "it again in game with /wvolume" % pending)
    # 2. the worlds decide what stays
    kept_worlds = []
    for e in man.entries:
        if e["kind"] != "world" or not os.path.isdir(e["path"]):
            continue
        files = [x for x in man.entries if x["kind"] == "file" and x.get("group") == "world" and
                 norm(x["path"]).startswith(norm(e["path"]) + os.sep)]
        changed = any(not os.path.isfile(x["path"]) or sha256_file(x["path"]) != x.get("sha256") for x in files)
        extra = len(tree_files(e["path"])) != len(files)
        if (changed or extra) and not remove_worlds:
            kept_worlds.append(e["path"])
            rep.warn("world", "world %r was played (files changed): kept in %s (--remove-worlds deletes it)" % (
                e.get("name"), e["path"]))
    others = other_de_worlds(ctx, [e["path"] for e in man.entries if e["kind"] == "world"])
    keep_worldmod = (kept_worlds or others) and not force_worldmod
    if keep_worldmod:
        rep.warn("worldmod", "kept: %d world(s) use DeepEngineers.World (%s); never open them without it, delete them "
                             "first or pass --force-worldmod" % (len(kept_worlds) + len(others),
                                                                 "; ".join((kept_worlds + others)[:4])))
    bep_dirs = [e for e in man.entries if e["kind"] == "loader-dir"]
    keep_loader = False
    for e in bep_dirs:
        foreign = loader_foreign_files(e["path"], man)
        if foreign:
            keep_loader = True
            rep.warn("bepinex", "kept: other mods use the BepInEx DeepEngineers installed (%s)" % ", ".join(foreign[:6]))
    # 3. the entries, newest first
    done, kept = [], []
    for e in reversed(list(man.entries)):
        g, k, p = e.get("group"), e["kind"], e["path"]
        if (g == "world" and any(norm(p) == norm(w) or norm(p).startswith(norm(w) + os.sep) for w in kept_worlds)) \
                or (g == "worldmod" and keep_worldmod) or (g == "bepinex" and keep_loader):
            kept.append(p)
            continue
        try:
            if k == "file":
                if e.get("origin") == "replaced" and e.get("backup") and os.path.isfile(e["backup"]):
                    if not ctx.dry_run:
                        shutil.copy2(e["backup"], p)
                    done.append("restored " + p)
                elif e.get("origin") == "created" and os.path.isfile(p):
                    _remove_file(ctx, p)
                    done.append("deleted " + p)
                # origin present: it was there before the install, left as it is
            elif k == "loader-file":
                if os.path.isfile(p):
                    _remove_file(ctx, p)
                    done.append("deleted " + p)
            elif k == "world-local":
                if os.path.isdir(p):
                    if not ctx.dry_run:
                        shutil.rmtree(p, ignore_errors=True)
                    done.append("deleted " + p)
            elif k == "entry" and e.get("origin") == "created" and os.path.isfile(p):
                with open(p, encoding="utf-8-sig") as f:
                    text = f.read()
                if e.get("how") == "local-empty":
                    new = re.sub(r"<Local>\s*%s\s*</Local>" % re.escape(e["text"]), "<Local />", text, count=1)
                else:
                    new = re.sub(r"\n[ \t]*%s" % re.escape(e["text"]), "", text, count=1)
                if new == text:
                    new = text.replace(e["text"], "", 1)
                if not ctx.dry_run:
                    with open(p, "w", encoding="utf-8", newline="") as f:
                        f.write(new)
                done.append("profile line removed from " + p)
            elif k == "loader-dir":
                for full, rel in sorted(tree_files(p), reverse=True):
                    relp = ("BepInEx/" + rel).replace("\\", "/")
                    if LOADER_GENERATED.match(relp):
                        _remove_file(ctx, full)
                for base, _dirs, _files in sorted(os.walk(p), key=lambda x: -len(x[0])):
                    _rmdir_if_empty(ctx, base)
            elif k == "dir":
                if _rmdir_if_empty(ctx, p):
                    done.append("removed folder " + p)
            elif k == "tree" and e.get("origin") == "created" and os.path.isdir(p):
                if not ctx.dry_run:
                    shutil.rmtree(p)
                done.append("removed " + p)
            elif k == "world" and os.path.isdir(p):
                if _rmdir_if_empty(ctx, p):
                    done.append("removed world folder " + p)
        except OSError as ex:
            rep.fail("remove", "%s: %s" % (p, ex))
    rep.ok("remove", "%d changes undone%s" % (len(done), ("; %d kept (above)" % len(kept)) if kept else ""))
    for line in done:
        rep.info("remove", line)
    # 4. DeepEngineers' own data
    if keep_data:
        rep.info("data", "kept %s (--keep-data)" % ctx.local)
    elif os.path.isdir(ctx.local):
        # S9-integrator: what was in the folder before the first install (an earlier DeepEngineers setup) stays
        before = {n.lower() for n in (man.doc.get("localBefore") or [])}
        kept_before = sorted((e.name for e in os.scandir(ctx.local) if e.name.lower() in before), key=str.lower)
        if kept_before:
            rep.info("data", "kept %d item(s) that were in %s before the install: %s" % (
                len(kept_before), ctx.local, ", ".join(kept_before[:12])))
        # S8S9 review R1: install\ holds install\replaced\ (the only copy of every file the install replaced)
 # and the manifest; it stays while any remove step failed or any entry was deliberately kept, with the manifest
 # rewritten to those entries so a later Uninstall.cmd can finish. It goes only when everything was undone.
        unfinished = bool(kept) or rep.failed("remove")
        if unfinished:
            try:
                keep_norm = {norm(k) for k in kept}
                man.doc["entries"] = [e for e in man.entries if norm(e.get("path", "")) in keep_norm]
                if not ctx.dry_run:
                    man.save()
            except OSError as ex:
                rep.fail("data", "could not rewrite the manifest: %s" % ex)
            rep.warn("data", "kept %s\\install (the replaced originals and the manifest): %s; run Uninstall.cmd again%s to finish" % (
                ctx.local, "a restore failed" if rep.failed("remove") else "%d entr%s kept" % (len(kept), "y" if len(kept) == 1 else "ies"),
                " with -RemoveWorlds / -ForceWorldMod" if kept else ""))
        for entry in os.scandir(ctx.local):
            if entry.name.lower() == "backups" and not remove_backups:
                continue
            if entry.name.lower() == "install" and unfinished:
                continue
            if entry.name.lower() in before:
                continue
            if entry.name.lower() == "python":
                continue   # S9 one-click: the interpreter running this; install.ps1 removes it once this has exited
            if ctx.dry_run:
                continue
            try:
                if entry.is_dir():
                    shutil.rmtree(entry.path)
                else:
                    os.remove(entry.path)
            except OSError as ex:
                rep.fail("data", "%s: %s" % (entry.path, ex))
        if not ctx.dry_run:
            _rmdir_if_empty(ctx, ctx.local)
        bk = os.path.join(ctx.local, "backups")
        rep.ok("data", "removed %s%s%s" % (ctx.local, "" if remove_backups or not os.path.isdir(bk) else
                                           " except its backups/ (delete them by hand when no longer wanted)",
                                           " and what was there before the install" if kept_before else ""))
    rep.info("steam", "Workshop subscriptions (Water Mod, Text HUD API) and the Subnautica host save are yours: "
                      "unsubscribe or delete them in Steam / the game if they are no longer wanted")
    fails = [r for r in rep.rows if r[0] == "FAIL"]
    print("", flush=True)
    print("UNINSTALL %s (%d FAIL, %d WARN)" % ("PASS" if not fails else "FAIL", len(fails),
                                              sum(1 for r in rep.rows if r[0] == "WARN")), flush=True)
    return 0 if not fails else 1

def status(ctx: Ctx, rep: Report) -> int:
    if not os.path.isfile(ctx.manifest_path):
        rep.info("status", "not installed (no %s)" % ctx.manifest_path)
        return 1
    man = Manifest(ctx.manifest_path)
    bad = 0
    for e in man.entries:
        if e["kind"] == "file":
            if not os.path.isfile(e["path"]):
                bad += 1
                rep.warn("status", "missing %s" % e["path"])
            elif e.get("sha256") and sha256_file(e["path"]) != e["sha256"] and e.get("group") != "world":
                bad += 1
                rep.warn("status", "changed %s" % e["path"])
    rep.row("PASS" if not bad else "WARN", "status", "%d entries, %d missing or changed (Install.cmd repairs them)" % (
        len(man.entries), bad))
    return 0 if not bad else 1

# ----------------------------------------------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------------------------------------------

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="DeepEngineers installer (S9)")
    ap.add_argument("--release-root", default=os.path.normpath(os.path.join(HERE, "..")))
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("install", "check"):
        p = sub.add_parser(name)
        p.add_argument("--world-name", default=None)
        p.add_argument("--pulsar-dir", default=None)
        p.add_argument("--offline-dir", action="append", default=[],
                       help="a folder holding the pinned zips (repeatable); checked before the download cache")
        p.add_argument("--steam-root", default=None)
        p.add_argument("--steam-id", default=None)
        p.add_argument("--no-preflight", action="store_true")
        p.add_argument("--dry-run", action="store_true")
        p.add_argument("--wait", action="store_true",
                       help="open the Workshop pages / start Subnautica and wait for the downloads and the host save")
        p.add_argument("--shortcuts", action="store_true", help="the Desktop and Start-menu entries")
    p = sub.add_parser("uninstall")
    p.add_argument("--remove-worlds", action="store_true")
    p.add_argument("--force-worldmod", action="store_true")
    p.add_argument("--remove-backups", action="store_true")
    p.add_argument("--keep-data", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    sub.add_parser("status")
    a = ap.parse_args(sys.argv[1:] if argv is None else argv)
    for stream in (sys.stdout, sys.stderr):
        # S9 one-click: a path with letters outside the code page (a Cyrillic user name) must not crash a redirected
 # output; a real console window already takes Unicode (PEP 528)
        try:
            stream.reconfigure(errors="backslashreplace")
        except (AttributeError, ValueError):
            pass
    ctx = Ctx(a.release_root, steam_root=getattr(a, "steam_root", None), pulsar_dir=getattr(a, "pulsar_dir", None),
              world_name=getattr(a, "world_name", None), steam_id=getattr(a, "steam_id", None),
              offline_dir=getattr(a, "offline_dir", None) or [], dry_run=getattr(a, "dry_run", False),
              run_preflight=not getattr(a, "no_preflight", False),
              wait=getattr(a, "wait", False) and a.cmd == "install", shortcuts=getattr(a, "shortcuts", False) and
              a.cmd == "install")
    rep = Report(os.path.join(ctx.local, "logs") if a.cmd != "uninstall" else None, command=a.cmd)
    try:
        if a.cmd == "install":
            return install(ctx, rep)
        if a.cmd == "check":
            return check(ctx, rep)
        if a.cmd == "uninstall":
            return uninstall(ctx, rep, a.remove_worlds, a.force_worldmod, a.remove_backups, a.keep_data)
        return status(ctx, rep)
    finally:
        rep.close()

if __name__ == "__main__":
    sys.exit(main())
