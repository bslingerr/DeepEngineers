"""Makes a throwaway SURVIVAL copy of an SE save: the S2b.N4 oxygen check (the suit consumes no oxygen in Creative) and
the S6 combat sessions (ROADMAP S6: "the SE combat lab world must be Survival with destructible blocks"; S6.M4 runs on
a Creative variant of the same copy).

 python -I tools/lab/make_survival_copy.py [--source "DE Lab"] [--name "DE Lab Survival"] [--saves-root <Saves>/<id>]
 [--force | --in-place] [--keep-bottles] [--destructible]
 [--game-mode Survival|Creative] [--autosave-off] [--point-last-session]
 [--dry-run]
 python -I tools/lab/make_survival_copy.py --restore-last-session [--source "DE Lab"] [--dry-run]
 python -I tools/lab/make_survival_copy.py --check "DE Lab Survival" (read-only report)

 S6:
 python -I tools/lab/make_survival_copy.py --name "DE Lab Survival" --in-place --destructible --keep-bottles --point-last-session
 (or a fresh copy: --force instead of --in-place; the copy made for S2b carries no marker: --in-place edits it)
 python -I tools/lab/make_survival_copy.py --name "DE Lab S6 Creative" --game-mode Creative --destructible --keep-bottles --point-last-session
 (the S6.M4 "creative" group); after the sessions: --restore-last-session

- Refuses while Space Engineers runs (the save folders are live); --dry-run and --check only read.
- --saves-root defaults to the one %APPDATA%\\SpaceEngineers\\Saves\\<steamid> folder that holds --source.
- Copies <source> to <name> (without its Backup folder) and writes DE_THROWAWAY.txt into the copy; refuses an existing
 <name> unless --force (which replaces only a copy carrying that marker, or with --force-unmarked one this tool's
 older version made) or --in-place (edits the existing copy, never the source or "DE Lab"). By text edits only (the
 files use xsi:type="xsd:string", so they are never re-serialised: see make_lab_world.py), in Sandbox.sbc and
 Sandbox_config.sbc: <GameMode> = --game-mode (default Survival), <SessionName> = <name>; --destructible:
 <DestructibleBlocks>true</DestructibleBlocks> asserted, set, or added inside <Settings> (MyCubeGrid.
 BlocksDestructionEnabled needs it, CONTRACT §4.1); --autosave-off: <AutoSaveInMinutes>0 (MySession saves only when it
 is not 0; the world configuration file overrides the checkpoint's Settings at load, see make_terrain_copy.py).
- Unless --keep-bottles: removes every OxygenBottle inventory item of the character entity in SANDBOX_0_0_0_.sbs (SE
 refills the suit from a carried bottle below 50 % in Survival, which breaks the N4 rate fit) and deletes
 SANDBOX_0_0_0_.sbsB5 so SE reads the edited.sbs.
 "convert from XML" message box, MyLoadingNeedXMLException; real run): run it right before an attended
 session, never before an unattended launch. With --keep-bottles the sector is untouched and no click is needed (the

- --point-last-session: LastSession.sbl -> the copy (the previous file kept once as LastSession.sbl.bak-s6);
 --restore-last-session points it back at --source. The launcher's s6 refuses unless LastSession points at its --world.
Prints every change. Stdlib only.
"""
import argparse
import glob
import os
import re
import shutil
import subprocess
import sys

SE_PROCESSES = ("legacy.exe", "spaceengineers.exe")
LAB = "DE Lab"
MARKER = "DE_THROWAWAY.txt"

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

def _set_element(text, tag, value, changes, add_in_settings=False):
    m = re.search(r"<%s>([^<]*)</%s>" % (tag, tag), text)
    if m is None:
        if not add_in_settings:
            return text
        s = re.search(r"<Settings>", text)
        if s is None:
            sys.exit("no <Settings> element: cannot add <%s>" % tag)
        changes.append("%s added = %s" % (tag, value))
        return text[:s.end()] + "\n    <%s>%s</%s>" % (tag, value, tag) + text[s.end():]
    if m.group(1).strip() != value:
        changes.append("%s %r -> %s" % (tag, m.group(1), value))
        return text[:m.start(1)] + value + text[m.end(1):]
    return text

def edit_world_text(text, name, game_mode="Survival", destructible=False, autosave_off=False, path="?"):
    """(new text, changes) for Sandbox.sbc / Sandbox_config.sbc."""
    changes = []
    if re.search(r"<GameMode>([^<]*)</GameMode>", text) is None:
        sys.exit("GameMode missing in " + path)
    text = _set_element(text, "GameMode", game_mode, changes)
    m = re.search(r"<SessionName>([^<]*)</SessionName>", text)
    if m is not None and m.group(1) != name:
        changes.append("SessionName %r -> %r" % (m.group(1), name))
        text = text[:m.start(1)] + name + text[m.end(1):]
    if destructible:
        text = _set_element(text, "DestructibleBlocks", "true", changes, add_in_settings=True)
    if autosave_off:
        text = _set_element(text, "AutoSaveInMinutes", "0", changes, add_in_settings=True)
    return text, changes

def set_world_file(path, name, game_mode="Survival", destructible=False, autosave_off=False, dry=False):
    if not os.path.exists(path):
        return ["absent"]
    text, changes = edit_world_text(read(path), name, game_mode, destructible, autosave_off, path)
    if changes and not dry:
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    return changes or ["already as wanted"]

def remove_bottles(sbs_path):
    """Removes every <MyObjectBuilder_InventoryItem> block whose PhysicalContent subtype is OxygenBottle inside the
 character entity. Returns the number removed."""
    with open(sbs_path, "r", encoding="utf-8-sig", newline="") as f:
        text = f.read()
    start = text.find('<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_Character">')
    if start < 0:
        sys.exit("no character entity in " + sbs_path)
    end = text.find("</MyObjectBuilder_EntityBase>", start)
    block = text[start:end]
    pat = re.compile(r"\s*<MyObjectBuilder_InventoryItem>(?:(?!</MyObjectBuilder_InventoryItem>).)*?OxygenBottle(?:(?!</MyObjectBuilder_InventoryItem>).)*?</MyObjectBuilder_InventoryItem>", re.S)
    new_block, n = pat.subn("", block)
    if n:
        text = text[:start] + new_block + text[end:]
        with open(sbs_path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    return n

def last_session_text(folder, name):
    rel = os.path.basename(folder)
    return ('<?xml version="1.0"?>\n<MyObjectBuilder_LastSession xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
            'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n  <Path>%s</Path>\n  <IsContentWorlds>false'
            '</IsContentWorlds>\n  <IsOnline>false</IsOnline>\n  <IsLobby>false</IsLobby>\n  <GameName>%s</GameName>\n'
            '  <ServerPort>0</ServerPort>\n  <RelativePath>%s</RelativePath>\n</MyObjectBuilder_LastSession>' % (
                folder, name, rel))

def point_last_session(root, folder, name, dry):
    path = os.path.join(root, "LastSession.sbl")
    bak = path + ".bak-s6"
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
            vals = []
            for tag in ("GameMode", "DestructibleBlocks", "AutoSaveInMinutes", "SessionName"):
                m = re.search(r"<%s>([^<]*)</%s>" % (tag, tag), t)
                vals.append("%s %s" % (tag, m.group(1) if m else "-"))
            print("  %-19s %s" % (fn, "; ".join(vals)))
    print("  marker %s: %s" % (MARKER, "present" if os.path.exists(os.path.join(folder, MARKER)) else "absent"))
    print("  SANDBOX_0_0_0_.sbsB5 %s" % ("present" if os.path.exists(os.path.join(folder, "SANDBOX_0_0_0_.sbsB5"))
                                         else "ABSENT: SE will ask the owner to convert from XML once"))
    p = os.path.join(root, "LastSession.sbl")
    m = re.search(r"<Path>([^<]*)</Path>", read(p)) if os.path.exists(p) else None
    print("  LastSession.sbl -> %s%s" % (m.group(1) if m else "-", " (this world)" if m and os.path.normcase(
        m.group(1).rstrip("\\/")) == os.path.normcase(folder) else ""))
    return 0

def main(argv=None):
    ap = argparse.ArgumentParser(description="throwaway Survival (or Creative) copy of an SE world (S2b.N4, S6)")
    ap.add_argument("--source", default=LAB)
    ap.add_argument("--name", default="DE Lab Survival")
    ap.add_argument("--saves-root", default=None, help="<APPDATA>/SpaceEngineers/Saves/<steamid> (default: found)")
    ap.add_argument("--force", action="store_true", help="replace an existing copy that carries the marker")
    ap.add_argument("--force-unmarked", action="store_true", help="with --force: also replace a copy without the marker "
                                                                  "(one this tool's older version made)")
    ap.add_argument("--in-place", action="store_true", help="edit the existing copy <name> (no new copy)")
    ap.add_argument("--keep-bottles", action="store_true", help="do not touch the sector (no owner click needed)")
    ap.add_argument("--destructible", action="store_true", help="DestructibleBlocks true (S6: block damage applies)")
    ap.add_argument("--game-mode", choices=("Survival", "Creative"), default="Survival",
                    help="Survival (default); Creative for the S6.M4 world (characters cannot die there)")
    ap.add_argument("--autosave-off", action="store_true", help="AutoSaveInMinutes 0 in the copy")
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
    if a.name.strip().lower() in (a.source.strip().lower(), LAB.lower()):
        sys.exit("the copy must not be named like the source or the lab world")
    src, dst = os.path.join(root, a.source), os.path.join(root, a.name)
    if not os.path.isdir(src) and not a.in_place:
        sys.exit("source not found: " + src)
    if a.in_place:
        if not os.path.isdir(dst):
            sys.exit("--in-place: no copy named %s under %s" % (a.name, root))
        print("editing the existing copy", dst)
    else:
        if os.path.isdir(dst):
            if not a.force:
                sys.exit("destination exists (use --force to replace that copy, or --in-place to edit it): " + dst)
            if not os.path.exists(os.path.join(dst, MARKER)) and not a.force_unmarked:
                sys.exit("refusing --force: %s carries no %s marker (an older copy: --force-unmarked, or --in-place)" % (
                    dst, MARKER))
        print("copy %s -> %s (without Backup)%s" % (src, dst, " [replacing the existing copy]" if os.path.isdir(dst)
                                                     else ""))
        if not a.dry_run:
            if os.path.isdir(dst):
                shutil.rmtree(dst)
            shutil.copytree(src, dst, ignore=shutil.ignore_patterns("Backup"))
            with open(os.path.join(dst, MARKER), "w", encoding="utf-8") as f:
                f.write("throwaway copy made by tools/lab/make_survival_copy.py from %s; --force may replace it\n" %
                        a.source)
    base = dst if (a.in_place or not a.dry_run) else src
    for fname in ("Sandbox.sbc", "Sandbox_config.sbc"):
        print("%s: %s" % (fname, "; ".join(set_world_file(os.path.join(base, fname), a.name, a.game_mode,
                                                         a.destructible, a.autosave_off, dry=a.dry_run))))
    if a.keep_bottles:
        print("sector untouched (--keep-bottles): no owner click needed; the precheck will report the bottle")
    elif a.dry_run:
        print("dry run: the character's OxygenBottle items would be removed and SANDBOX_0_0_0_.sbsB5 deleted "
              "(one owner click at the next load)")
    else:
        n = remove_bottles(os.path.join(dst, "SANDBOX_0_0_0_.sbs"))
        print("OxygenBottle items removed from the character: %d" % n)
        b5 = os.path.join(dst, "SANDBOX_0_0_0_.sbsB5")
        if os.path.exists(b5):
            os.remove(b5)
            print("deleted", b5, "-> SE shows the 'convert from XML' box once at the next load (owner clicks OK)")
    if a.point_last_session:
        point_last_session(root, dst, a.name, a.dry_run)
    print("dry run: nothing written" if a.dry_run else "done: %s (%s%s)" % (
        dst, a.game_mode, ", destructible blocks" if a.destructible else ""))
    return 0

if __name__ == "__main__":
    sys.exit(main())
