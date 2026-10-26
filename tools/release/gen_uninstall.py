"""Writes docs/install/UNINSTALL.md from the installer's manifest format. Python 3.10 stdlib; ``python -I``.

 python -I tools/release/gen_uninstall.py [--out docs/install/UNINSTALL.md] [--check]
 python -I tools/release/gen_uninstall.py --manifest <install-manifest.json> --out <file> (one machine's list)

The installer (src/DeepEngineers.Launcher/de_install.py) records every change in
%LOCALAPPDATA%\\DeepEngineers\\install\\install-manifest.json (format 1): entries of the kinds in KINDS below, each
with a group (the install step), a path and, for files, an origin (created | replaced | present), a SHA-256 and the
backup of a replaced file. Uninstall.cmd walks that manifest backwards. This generator writes the page a player reads
to understand it or to do it by hand:
 * the generic page (default): every kind and group, the folders the installer uses, the loader's file list
 (docs/install/subnautica-bepinex-files.txt), the world mod's files (src/DeepEngineers.WorldMod) and the template
 world's files (tools/release/world-template), the settings and the checks of S9.N5;
 * with --manifest: the same page plus the exact entries of one install, newest first, with the user-profile roots
 written as %LOCALAPPDATA%, %APPDATA% and %USERPROFILE%.
It stops (exit 2) when de_install.py records a manifest kind this page does not describe, so the two cannot drift.
--check exits 1 when the file on disk differs from what would be written.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
DEFAULT_OUT = REPO / "docs" / "install" / "UNINSTALL.md"
MANIFEST_FORMAT = 1

# kind -> (what it is, what Uninstall.cmd does, what to do by hand)
KINDS = {
    "file": ("a file the installer wrote",
             "created: deleted; replaced: the saved original put back (from install\\replaced\\); present (it was "
             "already there, identical): left",
             "delete it, or copy the saved original back over it"),
    "dir": ("a folder the installer created", "removed when empty", "delete it once it is empty"),
    "tree": ("the Pulsar folder the installer unpacked (portable SE plugin loader)",
             "created: deleted with everything in it; present (your own Pulsar): left",
             "delete %LOCALAPPDATA%\\DeepEngineers\\Pulsar\\ (your own Pulsar folder stays)"),
    "loader-dir": ("Subnautica's BepInEx folder, when the installer put BepInEx there",
                   "the files BepInEx writes by itself (log, cache, config) removed, then the empty folders; kept "
                   "as it is when other mods use BepInEx",
                   "delete <Subnautica>\\BepInEx\\ if no other mod needs it"),
    "loader-file": ("a file BepInEx writes for our plugin", "deleted", "delete it"),
    "world-local": ("the Star System world copy the installer built from your own game files (New Game > Star System > Start "
                    "lands on 4546B)", "deleted", "delete %LOCALAPPDATA%\DeepEngineers\worlds\DeepEngineers 4546B\\"),
    "entry": ("the line <string>DeepEngineers.SE.dll</string> under <Local> in Pulsar's profile",
              "the line removed (the rest of the file untouched)",
              "open <Pulsar>\\Legacy\\Profiles\\Current.xml in a text editor and remove that one line"),
    "world": ("the Space Engineers world copied from the template",
              "removed when you never played it; a played world is kept (--remove-worlds deletes it)",
              "delete the world in Space Engineers (Load Game, Delete) when you no longer want it"),
}

# install order of de_install.py; uninstall goes backwards
GROUPS = [
    ("pulsar", "Pulsar (the SE plugin loader)",
     ["%LOCALAPPDATA%\\DeepEngineers\\Pulsar\\ (only when the installer unpacked it; an existing Pulsar is only used)"]),
    ("bepinex", "BepInEx for Subnautica (Tobey's pack 5.4.23-pack.3.1.1)",
     ["<Subnautica>\\<each file below>, when the installer unpacked the pack (a file it replaced is put back)"]),
    ("plugins", "the two DeepEngineers plugins",
     ["<Pulsar>\\Legacy\\Local\\DeepEngineers.SE\\plugin.dll", "<Pulsar>\\Legacy\\Local\\DeepEngineers.SE\\DeepEngineers.Wire.dll",
      "<Subnautica>\\BepInEx\\plugins\\DeepEngineers\\DeepEngineers.SN.dll",
      "<Subnautica>\\BepInEx\\plugins\\DeepEngineers\\DeepEngineers.Wire.dll",
      "<Subnautica>\\BepInEx\\config\\deepengineers.sn.cfg (written by BepInEx)"]),
    ("profile", "Pulsar's profile line", ["<Pulsar>\\Legacy\\Profiles\\Current.xml: <string>DeepEngineers.SE.dll</string>"]),
    ("worldmod", "the world mod DeepEngineers.World (the planet DE_4546B)",
     ["%APPDATA%\\SpaceEngineers\\Mods\\DeepEngineers.World\\<each file below>"]),
    ("world", "the DeepEngineers world", ["%APPDATA%\\SpaceEngineers\\Saves\\<your SteamID64>\\<world name, default "
                                          "DeepEngineers>\\<each file below>"]),
    # S9 one-click: de_install.py step_shortcuts
    ("shortcuts", "the Desktop and Start-menu entries",
     ["<Desktop>\\DeepEngineers.lnk", "<Start menu>\\Programs\\DeepEngineers\\DeepEngineers.lnk",
      "<Start menu>\\Programs\\DeepEngineers\\Uninstall DeepEngineers.lnk (and the folder when empty)"]),
]

def recorded_kinds(de_install: Path) -> set:
    try:
        text = de_install.read_text(encoding="utf-8")
    except OSError:
        return set()
    return set(re.findall(r"[\"']kind[\"']\s*:\s*[\"']([\w-]+)[\"']", text))

def list_files(root: Path, prefix: str = "") -> list:
    if not root.is_dir():
        return []
    out = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        for f in sorted(filenames):
            rel = (Path(dirpath) / f).relative_to(root).as_posix().replace("/", "\\")
            out.append(prefix + rel)
    return out

def tokenise(path: str, env=None) -> str:
    env = os.environ if env is None else env
    roots = []
    for name in ("LOCALAPPDATA", "APPDATA", "USERPROFILE"):
        v = env.get(name)
        if v:
            roots.append((os.path.normcase(os.path.normpath(v)), "%" + name + "%"))
    p = os.path.normpath(path)
    for root, token in roots:                   # LOCALAPPDATA and APPDATA before USERPROFILE (they are inside it)
        if os.path.normcase(p).startswith(root + os.sep) or os.path.normcase(p) == root:
            return token + p[len(root):]
    return p

def render(repo: Path, manifest: dict | None = None, env=None) -> str:
    L = []
    w = L.append
    w("# Uninstalling DeepEngineers")
    w("")
    w("Generated by `tools/release/gen_uninstall.py` from the installer's manifest format (format %d, "
      "`src/DeepEngineers.Launcher/de_install.py`); regenerate it rather than editing it." % MANIFEST_FORMAT)
    w("")
    w("DeepEngineers is built to leave every saved setting of both games as it was; the few values that can still "
      "change as a side effect are saved by the launcher before the first session and put back (section 1). "
      "Everything the installer adds is listed, as it is installed, in "
      "`%LOCALAPPDATA%\\DeepEngineers\\install\\install-manifest.json`, and the uninstaller undoes exactly that list, "
      "newest change first. Paths below use these names:")
    w("")
    w("- `<Subnautica>`: your Subnautica folder (Steam, Manage, Browse local files).")
    w("- `<Pulsar>`: the Pulsar folder the installer used: `%LOCALAPPDATA%\\DeepEngineers\\Pulsar` when it unpacked "
      "Pulsar, or your own Pulsar folder.")
    w("- `%APPDATA%`, `%LOCALAPPDATA%`: type them in the Explorer address bar as they are.")
    w("")
    w("## 1. The automatic way")
    w("")
    w("1. Close Space Engineers and Subnautica normally. A game that is still open keeps its plugin locked.")
    w("2. Run `Uninstall.cmd` from the DeepEngineers folder. It prints one PASS, WARN or FAIL line per step, then "
      "`UNINSTALL PASS` or `UNINSTALL FAIL`.")
    w("3. Options (add them after `Uninstall.cmd`): `-DryRun` shows what would be done and changes nothing; "
      "`-RemoveWorlds` also deletes a DeepEngineers world you played; `-RemoveBackups` also deletes "
      "`%LOCALAPPDATA%\\DeepEngineers\\backups`; `-KeepData` keeps `%LOCALAPPDATA%\\DeepEngineers` (logs, caches).")
    w("4. `python -I launcher\\de_install.py status` lists the manifest entry by entry against what is on disk, before "
      "or after.")
    w("")
    w("What it does, in this order:")
    w("")
    w("1. **Settings.** If DeepEngineers ever ran a session here, the launcher puts back what it saved before the "
      "first one: the Subnautica window values in the registry (`HKCU\\Software\\Unknown Worlds\\Subnautica`, "
      "`Screenmanager` keys), the Water Mod's `Volume` in `%APPDATA%\\SpaceEngineers\\Storage\\WaterClientSettings.xml` "
      "and the audio keys of `%APPDATA%\\SpaceEngineers\\SpaceEngineers.cfg` (`launcher.py restore-settings --yes`, or "
      "`restore-registry --yes` when only the registry was saved). If `%LOCALAPPDATA%\\DeepEngineers\\restore.json` "
      "exists, a session ended badly: start Space Engineers once with DeepEngineers so the plugin writes the Water "
      "Mod volume back, or set it again in game with `/wvolume`.")
    w("2. **Worlds first.** A DeepEngineers world you played is kept. While any world uses the planet `DE_4546B`, the "
      "world mod stays too: **never open such a world without the plugin and the world mod** (the crater's seabed "
      "comes from the plugin; ships resting on it would sink).")
    w("3. **The manifest, newest entry first** (table below).")
    w("4. **DeepEngineers' own data**: `%LOCALAPPDATA%\\DeepEngineers\\` (the manifest, `paths.json`, downloads, logs, "
      "caches, the saved settings), except `backups\\` unless `-RemoveBackups`, and except what that folder held "
      "before the first install (an earlier DeepEngineers setup's files, named in the manifest's `localBefore`). "
      "Uninstall.cmd then deletes `python\\` (the interpreter the installer downloaded; it ran the steps above) "
      "unless `-KeepData` or something was kept.")
    # S9-integrator: de_install.py keeps the data folder's earlier content ("localBefore")
    w("")
    w("## 2. What the manifest records")
    w("")
    w("| Kind | What it is | Uninstall.cmd | By hand |")
    w("|---|---|---|---|")
    for k, (what, auto, hand) in KINDS.items():
        w("| `%s` | %s | %s | %s |" % (k, _md(what), _md(auto), _md(hand)))
    w("")
    w("## 3. By hand, step by step (newest first)")
    w("")
    w("Close both games first. Then, in this order:")
    w("")
    # S8S9 review R4: the by-hand BepInEx list comes from the pinned pack's own zip (the development PC's listing
 # also held Nautilus, another mod's library, which a stranger following the list would have deleted)
    pack_zip = repo / "third_party" / "bepinex-subnautica" / "Tobey.s.BepInEx.Pack.for.Subnautica.zip"
    loader = list_files_from_zip(pack_zip) if pack_zip.is_file() else list_files_from_manifest(repo / "docs" / "install" / "subnautica-bepinex-files.txt")
    worldmod = list_files(repo / "src" / "DeepEngineers.WorldMod")
    world = list_files(repo / "tools" / "release" / "world-template" / "DeepEngineers")
    extra = {"bepinex": loader, "worldmod": worldmod, "world": world}
    for n, (group, title, paths) in enumerate(reversed(GROUPS), 1):
        w("%d. **%s** (group `%s`)" % (n, title, group))
        for p in paths:
            w("   - `%s`" % p)
        files = extra.get(group)
        if files:
            w("   - the files (%d%s):" % (len(files), "; the pinned pack's own list: your manifest records what your "
                                                      "install actually wrote, and nothing of another mod" if group == "bepinex" else ""))
            w("")
            w("     ```")
            for f in files:
                w("     " + f)
            w("     ```")
            w("")
        if group == "world":
            w("   - Keep it, and the world mod, while you want to play it.")
        if group == "bepinex":
            w("   - Then `<Subnautica>\\BepInEx\\` itself if no other mod uses it (it also holds `LogOutput.log` and a "
              "cache). Steam's \"Verify integrity of game files\" does not remove these extra files: `winhttp.dll` "
              "keeps loading BepInEx until it is deleted.")
    w("%d. **DeepEngineers' data**: delete `%%LOCALAPPDATA%%\\DeepEngineers\\` after the settings of section 1 are "
      "back (its `registry-backup.json` and `settings-backup.json` hold the original values); keep what the "
      "manifest's `localBefore` names (it was there before the install)." % (len(GROUPS) + 1))  # S9-integrator
    w("")
    w("## 4. What stays yours")
    w("")
    w("- Your Workshop subscriptions (Water Mod 2200451495, Text HUD API 758597413): unsubscribe in Steam if you no "
      "longer want them.")
    w("- The Subnautica Freedom save the renderer used: it is a normal save of yours (Steam Cloud keeps a copy).")
    w("- Steam's update setting of both games (\"Only update this game when I launch it\").")
    w("- Space Engineers worlds you made with the planet `DE_4546B`.")
    w("")
    w("## 5. Check")
    w("")
    w("- `python -I launcher\\de_install.py status` says not installed (no manifest).")
    w("- Subnautica started from Steam: no `BepInEx\\LogOutput.log` line `Loading [DeepEngineers.SN` (or no BepInEx "
      "at all); the folder holds only the game's own files and your saves.")
    w("- Space Engineers started from Steam loads a world without the planet and its log "
      "(`%APPDATA%\\SpaceEngineers\\SpaceEngineers_*.log`) has no line starting with `DE.`.")
    w("- `WaterClientSettings.xml`, the `SpaceEngineers.cfg` audio keys and the Subnautica registry values equal the "
      "values saved before the first session.")
    if manifest is not None:
        w("")
        w("## 6. This install's manifest (newest first)")
        w("")
        w("Installed %s; %d entries." % (manifest.get("installed", "?"), len(manifest.get("entries", []))))
        w("")
        w("| # | Kind | Group | Origin | Path |")
        w("|---|---|---|---|---|")
        entries = list(manifest.get("entries", []))
        for i, e in enumerate(reversed(entries), 1):
            w("| %d | `%s` | %s | %s | `%s` |" % (i, e.get("kind"), e.get("group", ""), e.get("origin", ""),
                                                 tokenise(str(e.get("path", "")), env)))
    w("")
    return "\n".join(L)

def list_files_from_zip(path):
    """The files of a zip (folders left out), sorted, as the by-hand list shows them."""
    import zipfile
    with zipfile.ZipFile(str(path)) as z:
        return sorted(n.replace("/", "\\") for n in z.namelist() if not n.endswith("/"))

def list_files_from_manifest(path: Path) -> list:
    try:
        lines = path.read_text(encoding="utf-8-sig").splitlines()
    except OSError:
        return []
    return [x.strip() for x in lines if x.strip() and not x.lstrip().startswith("#")]

def _md(s: str) -> str:
    return s.replace("|", "\\|").replace("<", "&lt;").replace(">", "&gt;")

def run(argv=None, out=print) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--repo", default=str(REPO))
    ap.add_argument("--out", default=None, help="default docs/install/UNINSTALL.md")
    ap.add_argument("--manifest", default=None, help="an install-manifest.json: adds that install's exact entries")
    ap.add_argument("--check", action="store_true", help="exit 1 when the file on disk differs")
    a = ap.parse_args(argv)
    repo = Path(a.repo)
    kinds = recorded_kinds(repo / "src" / "DeepEngineers.Launcher" / "de_install.py")
    unknown = sorted(kinds - set(KINDS))
    if unknown:
        out("de_install.py records manifest kinds this page does not describe: %s (add them to KINDS)" % ", ".join(unknown))
        return 2
    manifest = None
    if a.manifest:
        try:
            manifest = json.loads(Path(a.manifest).read_text(encoding="utf-8"))
        except (OSError, ValueError) as e:
            out("manifest unreadable: %s" % e)
            return 2
        if manifest.get("format") != MANIFEST_FORMAT:
            out("unknown manifest format %r (this generator knows %d)" % (manifest.get("format"), MANIFEST_FORMAT))
            return 2
        bad = sorted({e.get("kind") for e in manifest.get("entries", [])} - set(KINDS))
        if bad:
            out("manifest kinds this page does not describe: %s" % ", ".join(map(str, bad)))
            return 2
    text = render(repo, manifest)
    target = Path(a.out) if a.out else (repo / "docs" / "install" / "UNINSTALL.md")
    if a.check:
        try:
            same = target.read_text(encoding="utf-8") == text
        except OSError:
            same = False
        out("%s %s" % (target.name, "up to date" if same else "differs from the generator (run gen_uninstall.py)"))
        return 0 if same else 1
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8", newline="\n")
    out("wrote %s (%d lines)" % (target, text.count("\n") + 1))
    return 0

if __name__ == "__main__":
    sys.exit(run())
