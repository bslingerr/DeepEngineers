"""Makes a throwaway copy of an SE world for the S4 throwaway parts (ROADMAP S4 rule 10: G2-E2, G2-E3b, G2-E5, N2,
S4.M4, S4.M10, S4.M9), with autosave OFF, and points SE's LastSession at it (and back). Stdlib only.

 python -I tools/lab/make_terrain_copy.py [--source "DE Lab"] [--name "DE Lab S4"] [--saves-root <Saves>/<steamid>]
 [--force] [--without-world-mod] [--point-last-session] [--dry-run]
 python -I tools/lab/make_terrain_copy.py --restore-last-session [--source "DE Lab"] [--dry-run]
 python -I tools/lab/make_terrain_copy.py --check "DE Lab S4" (read-only report)

- Refuses while Space Engineers runs (Legacy.exe / SpaceEngineers.exe: the save folders are live). Run it with SE
 closed, by the orchestrator.
- --saves-root defaults to the one %APPDATA%\\SpaceEngineers\\Saves\\<steamid> folder that holds --source.
- Copies <source> to <name> without its Backup folder; refuses an existing <name> unless --force (which deletes that
 copy first: never the source, never "DE Lab").
- Text edits only (the.sbc files are never re-serialised, see make_lab_world.py): <AutoSaveInMinutes>0</...> in
 Sandbox.sbc and Sandbox_config.sbc

 and <SessionName> = <name>.
-.sbsB5 HANDLING: the sector SANDBOX_0_0_0_.sbs is NOT edited, so the copy keeps the source's SANDBOX_0_0_0_.sbsB5
 (the binary twin SE loads first: MyLocalCache.LoadSector reads "<sector>B5" when it exists, VERIFIED) and the load
 Deleting the B5 (only needed after an edit of the.sbs, as make_survival_copy.py and
 set_planet_position.: this tool never does that. The checkpoint files (Sandbox.sbc, Sandbox_config.sbc)
 are XML-only, so their edits need no click.
- --without-world-mod (S4.G2-E5): also removes the <ModItem> whose <Name> is DeepEngineers.World from both.sbc files
 (the world mod that defines DE_4546B); default name then "DE Lab S4 nomod". Such a copy is only for the g2e5 part.
- --point-last-session: LastSession.sbl (next to the worlds) gets <Path>/<GameName>/<RelativePath> of the copy; the
 current file is kept once as LastSession.sbl.bak-s4 (never overwritten by a later call). --restore-last-session points
 it back at --source (default "DE Lab"). The launcher's `s4 --world <name>` refuses unless LastSession points at <name>
 and autosave is 0.
- After an S4.M4 session the copy holds provider-backed storages (id 45460): delete it (it cannot load without the
 plugin)
Prints every change.
"""
import argparse
import glob
import os
import re
import shutil
import subprocess
import sys

SE_PROCESSES = ("legacy.exe", "spaceengineers.exe")
WORLD_MOD = "DeepEngineers.World"
LAB = "DE Lab"

def se_running():
    try:
        out = subprocess.run(["tasklist", "/fo", "csv", "/nh"], capture_output=True, text=True, timeout=30).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    return [ln.split(",")[0].strip().strip('"').lower() for ln in out.splitlines()
            if ln.split(",")[0].strip().strip('"').lower() in SE_PROCESSES]

def saves_root_for(source):
    base = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "SpaceEngineers", "Saves")
    hits = [os.path.dirname(p) for p in glob.glob(os.path.join(base, "*", source)) if os.path.isdir(p)]
    if len(hits) != 1:
        sys.exit("found %d Saves/<steamid>/%s folders under %s: pass --saves-root" % (len(hits), source, base))
    return hits[0]

def read(path):
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        return f.read()

def edit_world_file(text, name, without_mod):
    """(new text, changes) for Sandbox.sbc / Sandbox_config.sbc."""
    changes = []
    m = re.search(r"<AutoSaveInMinutes>([^<]*)</AutoSaveInMinutes>", text)
    if m is None:
        # the setting is a member of <Settings>: add it right after the opening tag
        s = re.search(r"<Settings>", text)
        if s is None:
            raise SystemExit("no <Settings> element")
        text = text[:s.end()] + "\n    <AutoSaveInMinutes>0</AutoSaveInMinutes>" + text[s.end():]
        changes.append("AutoSaveInMinutes added = 0")
    elif m.group(1).strip() != "0":
        changes.append("AutoSaveInMinutes %s -> 0" % m.group(1))
        text = text[:m.start(1)] + "0" + text[m.end(1):]
    m = re.search(r"<SessionName>([^<]*)</SessionName>", text)
    if m is not None and m.group(1) != name:
        changes.append("SessionName %r -> %r" % (m.group(1), name))
        text = text[:m.start(1)] + name + text[m.end(1):]
    if without_mod:
        pat = re.compile(r"[ \t]*<ModItem\b[^>]*>(?:(?!</ModItem>).)*?<Name>%s</Name>(?:(?!</ModItem>).)*?</ModItem>\r?\n?"
                         % re.escape(WORLD_MOD), re.S)
        text, n = pat.subn("", text)
        changes.append("world mod %s removed (%d item)" % (WORLD_MOD, n))
    return text, changes

def last_session_text(folder, name):
    rel = os.path.basename(folder)
    return ('<?xml version="1.0"?>\n<MyObjectBuilder_LastSession xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
            'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n  <Path>%s</Path>\n  <IsContentWorlds>false'
            '</IsContentWorlds>\n  <IsOnline>false</IsOnline>\n  <IsLobby>false</IsLobby>\n  <GameName>%s</GameName>\n'
            '  <ServerPort>0</ServerPort>\n  <RelativePath>%s</RelativePath>\n</MyObjectBuilder_LastSession>' % (
                folder, name, rel))

def point_last_session(root, folder, name, dry):
    path = os.path.join(root, "LastSession.sbl")
    bak = path + ".bak-s4"
    cur = None
    if os.path.exists(path):
        m = re.search(r"<Path>([^<]*)</Path>", read(path))
        cur = m.group(1) if m else None
    print("LastSession.sbl: %s -> %s" % (cur, folder))
    if dry:
        return
    if os.path.exists(path) and not os.path.exists(bak):
        shutil.copy2(path, bak)
        print("  kept the previous file as", bak)
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(last_session_text(folder, name))

def check(root, name):
    folder = os.path.join(root, name)
    print("world:", folder, "(exists)" if os.path.isdir(folder) else "(MISSING)")
    if not os.path.isdir(folder):
        return 1
    for fn in ("Sandbox.sbc", "Sandbox_config.sbc"):
        p = os.path.join(folder, fn)
        if os.path.exists(p):
            t = read(p)
            a = re.search(r"<AutoSaveInMinutes>([^<]*)</AutoSaveInMinutes>", t)
            print("  %-19s AutoSaveInMinutes %s; world mod %s" % (fn, a.group(1) if a else "-",
                                                                 "<Name>%s</Name>" % WORLD_MOD in t))
    print("  SANDBOX_0_0_0_.sbsB5 %s" % ("present" if os.path.exists(os.path.join(folder, "SANDBOX_0_0_0_.sbsB5"))
                                         else "ABSENT: SE will ask the owner to convert from XML once"))
    p = os.path.join(root, "LastSession.sbl")
    m = re.search(r"<Path>([^<]*)</Path>", read(p)) if os.path.exists(p) else None
    print("  LastSession.sbl -> %s%s" % (m.group(1) if m else "-", " (this world)" if m and os.path.normcase(
        m.group(1).rstrip("\\/")) == os.path.normcase(folder) else ""))
    here = os.path.dirname(os.path.abspath(__file__))
    sys.path.insert(0, os.path.normpath(os.path.join(here, "..", "..", "tests", "s4")))
    try:
        import analyze_s4
        sc = analyze_s4.scan_world(folder)
        print("  tile traces: vx2 %s, storage names %s, tile voxel maps %s; grids %d; gears locked %d" % (
            sc["tileVx2"], sc["storageNames"][:3], sc["tileVoxelMaps"][:3], len(sc["grids"]),
            sum(1 for g in sc["gears"] if g.get("lockMode") == "Locked")))
    except Exception as e:     # the report never fails on the scan
        print("  scan not available: %s" % e)
    return 0

MARKER = "DE_THROWAWAY.txt"        # written into every copy; --force replaces only folders that carry it
# S8a  SE's save (MySessionSnapshot.Save) deletes every top-level file of the folder it did not stage, so
# the marker also lives in a subfolder (subfolders survive the cleanup); every check accepts either form
MARKER_DIR = "DE_THROWAWAY"

def marked(folder):
    return os.path.isdir(os.path.join(folder, MARKER_DIR)) or os.path.exists(os.path.join(folder, MARKER))

def main(argv=None):
    ap = argparse.ArgumentParser(description="throwaway SE world copy for the S4 save/edit tests (autosave off)")
    ap.add_argument("--source", default=LAB)
    ap.add_argument("--name", default=None, help='default "DE Lab S4" ("DE Lab S4 nomod" with --without-world-mod)')
    ap.add_argument("--saves-root", default=None, help="<APPDATA>/SpaceEngineers/Saves/<steamid> (default: found)")
    ap.add_argument("--force", action="store_true", help="replace an existing copy of that name")
    ap.add_argument("--without-world-mod", action="store_true", help="S4.G2-E5: drop the DeepEngineers.World mod item")
    ap.add_argument("--point-last-session", action="store_true", help="LastSession.sbl -> the copy (for -continue)")
    ap.add_argument("--restore-last-session", action="store_true", help="LastSession.sbl -> --source")
    ap.add_argument("--check", default=None, metavar="NAME", help="read-only report on a world")
    ap.add_argument("--dry-run", action="store_true", help="print what would change; write nothing")
    a = ap.parse_args(argv)
    root = a.saves_root or saves_root_for(a.source)
    if a.check:
        return check(root, a.check)
    if not a.dry_run:
        running = se_running()
        if running is None or running:
            sys.exit("Space Engineers is running or the process list is unreadable: refusing")
    if a.restore_last_session:
        point_last_session(root, os.path.join(root, a.source), a.source, a.dry_run)
        print("dry run: nothing written" if a.dry_run else "done")
        return 0
    name = a.name or ("DE Lab S4 nomod" if a.without_world_mod else "DE Lab S4")
    if name.strip().lower() == a.source.strip().lower() or name.strip().lower() == LAB.lower():
        sys.exit("the copy must not be named like the source or the lab world")
    src, dst = os.path.join(root, a.source), os.path.join(root, name)
    if not os.path.isdir(src):
        sys.exit("source not found: " + src)
    if os.path.isdir(dst) and not a.force:
        sys.exit("destination exists (use --force to replace that copy): " + dst)
    if os.path.isdir(dst) and a.force and not marked(dst):
        sys.exit("refusing --force: %s carries no %s marker (file or subfolder), so this tool did not make it (review finding 11)" % (dst, MARKER))
    print("copy %s -> %s (without Backup)%s" % (src, dst, " [replacing the existing copy]" if os.path.isdir(dst) else ""))
    edits = {}
    for fn in ("Sandbox.sbc", "Sandbox_config.sbc"):
        p = os.path.join(src, fn)
        if not os.path.exists(p):
            print("%s: absent in the source" % fn)
            continue
        text, changes = edit_world_file(read(p), name, a.without_world_mod)
        edits[fn] = text
        print("%s: %s" % (fn, "; ".join(changes) or "already as wanted"))
    print("SANDBOX_0_0_0_.sbs / .sbsB5: copied unchanged (no owner click at the load)")
    if a.dry_run:
        if a.point_last_session:
            point_last_session(root, dst, name, True)
        print("dry run: nothing written")
        return 0
    if os.path.isdir(dst):
        shutil.rmtree(dst)
    shutil.copytree(src, dst, ignore=shutil.ignore_patterns("Backup"))
    note = "throwaway copy made by tools/lab/make_terrain_copy.py from %s; --force may replace it\n" % a.source
    with open(os.path.join(dst, MARKER), "w", encoding="utf-8") as f:
        f.write(note)
    os.makedirs(os.path.join(dst, MARKER_DIR), exist_ok=True)
    with open(os.path.join(dst, MARKER_DIR, MARKER), "w", encoding="utf-8") as f:
        f.write(note)
    for fn, text in edits.items():
        with open(os.path.join(dst, fn), "w", encoding="utf-8", newline="") as f:
            f.write(text)
    if a.point_last_session:
        point_last_session(root, dst, name, False)
    print("done: %s (autosave off%s)" % (dst, ", no world mod" if a.without_world_mod else ""))
    return 0

if __name__ == "__main__":
    sys.exit(main())
