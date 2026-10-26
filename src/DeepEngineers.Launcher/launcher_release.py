"""launcher.py verbs added by the S9 installer: `paths` and `backup`. Python 3.10 stdlib.

 python -I launcher.py paths [--json]
 the mode (dev | release), the resolved game, loader, data and backup folders, the plugins' environment inputs
 (launcher_paths.py) and the pins (de_pins.py); read-only
 python -I launcher.py backup [--sets se-appdata,subnautica-saves,subnautica-locallow] [--scope de|full] [--keep N]
 the zip step of Play.cmd: one `um backup`-compatible zip per set (relative paths + _um_manifest.json, the format
 of launcher.py create_saves_backup) in BACKUPS/<set>/<yyyymmdd-hhmmss>.zip, so that preflight's "backup newer
 than the last DE session" rule (CONTRACT 7) holds without the universal-modder CLI. Scope `de` (release default)
 backs up what a DE session can touch: SpaceEngineers.cfg, Storage/WaterClientSettings.xml, every Saves/<id>/
 LastSession.sbl and every world folder that lists the DeepEngineers.World mod (without SE's own Backup/
 subfolders); scope `full` (dev default) backs up %APPDATA%/SpaceEngineers without its regenerable caches and logs.
 subnautica-saves is launcher.py's own create_saves_backup; subnautica-locallow is the Unity log folder (an empty
 zip with a note when it does not exist yet). Release mode keeps the newest --keep zips per set (default 3); dev

The dev-only verbs (scripted sessions, deploy, manifest) need the repository: in a release folder launcher.py refuses
them with dev_verb_refusal.
"""
from __future__ import annotations

import datetime
import glob
import hashlib
import json
import os
import sys
import re
import zipfile

VERBS = ("paths", "backup", "update")   # S9 round 2: update
DEV_VERBS = ("deploy", "manifest", "s1", "s2", "s2b", "s5a", "s3", "s4")
SETS = ("se-appdata", "subnautica-saves", "subnautica-locallow")
FULL_EXCLUDE_DIRS = {"shadercache2", "cache", "temp", "profileoptimization", "workshopbrowser", "promo", "screenshots"}
FULL_EXCLUDE_FILES = re.compile(r"(\.log$|^mipMapTextureCache2\.B5$)", re.I)
STAMP = re.compile(r"^\d{8}-\d{6}\.zip$")
WORLD_MOD_NAME = "DeepEngineers.World"

def dev_verb_refusal(cmd: str) -> str:
    return ("'%s' is a development verb: it needs the DeepEngineers repository (build outputs, test harness). "
            "In a release folder use Install.cmd (install or repair), Play.cmd and Uninstall.cmd." % cmd)

def add_parsers(sub, L):
    p = sub.add_parser("paths", help="S9: resolved folders, mode and pins (read-only)")
    p.add_argument("--json", action="store_true")
    p = sub.add_parser("backup", help="S9: zip backups of what a DE session can touch (Play.cmd's step)")
    p.add_argument("--sets", default=",".join(SETS))
    p.add_argument("--scope", choices=("de", "full"), default=None,
                   help="se-appdata scope (default: de in a release folder, full in the repository)")
    p.add_argument("--keep", type=int, default=None, help="newest zips kept per set (release default 2; 0 = all)")
    p.add_argument("--max-age-days", type=float, default=None,
                   help="S9 round 2: skip a set whose newest zip is younger than this (Play.cmd: 7; the owner's start.bat: 7)")
    import de_update
    de_update.add_parser(sub)

def run(L, log, a) -> int:
    if a.cmd == "paths":
        return paths(L, log, a)
    if a.cmd == "backup":
        return backup(L, log, a)
    if a.cmd == "update":
        return update(L, log, a)
    return 2

def update(L, log, a) -> int:
    """S9 round 2: the GitHub Releases check and update (de_update.py); release folders only."""
    import de_update
    if L._PATHS["mode"] != "release":
        log.say("update   only a release folder updates itself (this is the repository)")
        return 0
    return de_update.run_update(L._PATHS["releaseRoot"], L._PATHS["local"], check_only=a.check, yes=a.yes, auto=a.auto,
                                log=log.say, python_exe=sys.executable)

# ----------------------------------------------------------------------------------------------------------------
# paths
# ----------------------------------------------------------------------------------------------------------------

def paths(L, log, a) -> int:
    P = L._PATHS
    doc = {
        "mode": P["mode"], "releaseRoot": P["releaseRoot"], "source": P["source"], "pathsFile": P["pathsFile"],
        "seBin64": L.SE_BIN64, "subnautica": L.SUBNAUTICA, "pulsar": L.PULSAR, "seAppData": L.SE_APPDATA,
        "local": L.LOCAL, "logs": L.LOG_DIR, "backups": L.BACKUPS, "registryBackup": L.REGISTRY_BACKUP,
        "registryBackupCopy": L.REGISTRY_BACKUP_REPO, "worldModSrc": L.WORLD_MOD_SRC, "worldModDst": L.WORLD_MOD_DST,
        "waterModData": L.WATER_MOD_DATA, "plugins": P["plugins"],
        "env": {k: os.environ.get(k) for k in ("DEEPENG_SUBNAUTICA", "DEEPENGINEERS_SN_HOST_SESSION",
                                               "DEEPENGINEERS_SN_HOST_SLOT")},
    }
    try:
        import de_pins
        doc["pins"] = de_pins.as_dict()
    except ImportError:
        doc["pins"] = None
    if a.json:
        print(json.dumps(doc, indent=1, sort_keys=True))
    else:
        for k in sorted(doc):
            if k not in ("pins", "env"):
                log.say("%-18s %s" % (k, doc[k]))
        for k, v in doc["env"].items():
            log.say("env %-30s %s" % (k, v if v else "(not set)"))
    log.event("paths", **{k: v for k, v in doc.items() if k != "pins"})
    return 0

# ----------------------------------------------------------------------------------------------------------------
# backup
# ----------------------------------------------------------------------------------------------------------------

def _stamp(now=None) -> str:
    return (now or datetime.datetime.now()).strftime("%Y%m%d-%H%M%S")

def is_de_world(world_dir: str) -> bool:
    for name in ("Sandbox_config.sbc", "Sandbox.sbc"):
        p = os.path.join(world_dir, name)
        try:
            with open(p, encoding="utf-8", errors="replace") as f:
                if WORLD_MOD_NAME in f.read():
                    return True
        except OSError:
            continue
    return False

def se_appdata_files(root: str, scope: str) -> list:
    """(absolute, relative) pairs of the se-appdata set."""
    out = []
    if not os.path.isdir(root):
        return out
    if scope == "full":
        for base, dirs, files in os.walk(root):
            if base == root:
                dirs[:] = [d for d in dirs if d.lower() not in FULL_EXCLUDE_DIRS]
            for n in sorted(files):
                if base == root and FULL_EXCLUDE_FILES.search(n):
                    continue
                full = os.path.join(base, n)
                out.append((full, os.path.relpath(full, root)))
        return out
    for rel in ("SpaceEngineers.cfg", os.path.join("Storage", "WaterClientSettings.xml")):
        p = os.path.join(root, rel)
        if os.path.isfile(p):
            out.append((p, rel))
    saves = os.path.join(root, "Saves")
    for user in sorted(glob.glob(os.path.join(saves, "*"))):
        if not os.path.isdir(user):
            continue
        last = os.path.join(user, "LastSession.sbl")
        if os.path.isfile(last):
            out.append((last, os.path.relpath(last, root)))
        for world in sorted(glob.glob(os.path.join(user, "*"))):
            if not os.path.isdir(world) or not is_de_world(world):
                continue
            for base, dirs, files in os.walk(world):
                if base == world:
                    dirs[:] = [d for d in dirs if d.lower() != "backup"]
                for n in sorted(files):
                    full = os.path.join(base, n)
                    out.append((full, os.path.relpath(full, root)))
    return out

def write_zip(dst_dir: str, source: str, pairs, note: str, now=None) -> tuple:
    """A um-compatible zip: the files under their relative paths + _um_manifest.json. Returns (path, count)."""
    os.makedirs(dst_dir, exist_ok=True)
    stamp = _stamp(now)
    dst = os.path.join(dst_dir, stamp + ".zip")
    n = 1
    while os.path.exists(dst):
        dst = os.path.join(dst_dir, "%s-%d.zip" % (stamp, n))
        n += 1
    tmp = dst + ".part"
    files = {}
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as z:
        for full, rel in pairs:
            try:
                with open(full, "rb") as f:
                    data = f.read()
            except OSError:
                continue
            arc = rel.replace(os.sep, "/")
            files[arc] = {"size": len(data), "sha1": hashlib.sha1(data).hexdigest()}
            z.writestr(arc, data)
        z.writestr("_um_manifest.json", json.dumps({"source": source, "created": stamp, "note": note,
                                                    "files": files}, indent=1))
    os.replace(tmp, dst)
    return dst, len(files)

def prune(dst_dir: str, keep: int) -> list:
    if keep <= 0 or not os.path.isdir(dst_dir):
        return []
    zips = sorted(n for n in os.listdir(dst_dir) if STAMP.match(n) or re.match(r"^\d{8}-\d{6}-\d+\.zip$", n))
    gone = []
    for n in zips[:-keep]:
        try:
            os.remove(os.path.join(dst_dir, n))
            gone.append(n)
        except OSError:
            pass
    return gone

def backup(L, log, a) -> int:
    release = L._PATHS["mode"] == "release"
    scope = a.scope or L._PATHS["backupScope"]
    keep = a.keep if a.keep is not None else (2 if release else 0)
    max_age = getattr(a, "max_age_days", None)
    wanted = [s.strip() for s in a.sets.split(",") if s.strip()]
    bad = [s for s in wanted if s not in SETS]
    if bad:
        log.say("unknown backup set(s): %s (known: %s)" % (", ".join(bad), ", ".join(SETS)))
        return 2
    running = L.processes_named(L.GAME_PROCESSES)
    if running:
        log.say("WARN games running (%s): files they hold open may be skipped" % ", ".join(
            "%s %d" % (p["name"], p["pid"]) for p in running))
    ok = True
    for name in wanted:
        dst_dir = os.path.join(L.BACKUPS, name)
        if max_age is not None:
            newest, when = L.latest_backup(name)   # when: a naive local datetime (launcher.backup_time)
            if newest is not None and when is not None and (datetime.datetime.now() - when).total_seconds() < max_age * 86400:
                log.say("PASS backup %-20s %s is %.1f days old (under %g): kept, no new zip" % (
                    name, os.path.basename(newest), (datetime.datetime.now() - when).total_seconds() / 86400, max_age), ev="backup_fresh", set=name)
                continue
        try:
            if name == "subnautica-saves":
                path, info = L.create_saves_backup(log)
                if path is None:      # no saves folder yet: an empty, annotated zip keeps preflight's rule honest
                    path, cnt = write_zip(dst_dir, L.sn_saves_dir(), [], "no Subnautica saves folder yet (%s)" % info)
                    info = "empty (%s)" % info
            elif name == "se-appdata":
                pairs = se_appdata_files(L.SE_APPDATA, scope)
                path, cnt = write_zip(dst_dir, L.SE_APPDATA, pairs, "DeepEngineers launcher.py backup, scope %s" % scope)
                info = "%d files, scope %s" % (cnt, scope)
            else:
                src = os.path.join(os.path.expanduser("~"), "AppData", "LocalLow", "Unknown Worlds", "Subnautica")
                pairs = []
                if os.path.isdir(src):
                    for base, _d, files in os.walk(src):
                        for n in sorted(files):
                            full = os.path.join(base, n)
                            pairs.append((full, os.path.relpath(full, src)))
                path, cnt = write_zip(dst_dir, src, pairs, "DeepEngineers launcher.py backup" + (
                    "" if pairs else " (folder absent or empty: Subnautica has not written a log yet)"))
                info = "%d files" % cnt
            gone = prune(dst_dir, keep) if release else []
            log.say("PASS backup %-20s %s (%s)%s" % (name, path, info, ("; pruned %d older" % len(gone)) if gone else ""),
                    ev="backup_created", set=name, path=path)
        except OSError as e:
            ok = False
            log.say("FAIL backup %-20s %s" % (name, e), ev="backup_failed", set=name)
    return 0 if ok else 1
