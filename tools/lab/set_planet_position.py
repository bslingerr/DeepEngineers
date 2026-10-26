"""DE_LAB_WORLD.md section 5 by script: moves the spawned DE_4546B planet of a save to the integer centre and parks
the character 50 m above the water over the anchor.

 python -I tools/lab/set_planet_position.py --save "DE Lab" --saves-root <Saves>/<steamid> [--dry-run]

- Refuses while Space Engineers runs (the save is live).
- Backs up the save folder next to itself ("<name> - backup <stamp>") before any write.
- SANDBOX_0_0_0_.sbs: the MyObjectBuilder_Planet whose <StorageName> starts with DE_4546B gets
 <Position x="-1131072.5" y="118927.5" z="1368927.5"/> (the minimum corner; centre C = (-1000000, 250000, 1500000)
 because SE adds half a voxel of a 262,144-voxel storage: guide section 5). The MyObjectBuilder_Character entity gets
 <Position x="-1000000" y="250000" z="1560050"/> (50 m above the water over the SN anchor).
- Deletes SANDBOX_0_0_0_.sbsB5 (the binary copy SE loads first; SE rebuilds it from the.sbs).
Text edits keep every other byte of the file: the file is edited by regular expressions on the exact entity blocks,
not re-serialised. Stdlib only.

sbsB5 SE throws
MyLoadingNeedXMLException, unloads and shows a message box asking to convert the world from XML; "-continue" then lands
at the Menu.
unattended launch. If a B5 is put back from the backup folder, SE loads it and ignores (then overwrites) the edited.sbs.
"""
import argparse
import datetime
import os
import re
import shutil
import subprocess
import sys

PLANET_POS = (-1131072.5, 118927.5, 1368927.5)
CHAR_POS = (-1000000, 250000, 1560050)
STORAGE_PREFIX = "DE_4546B"
SE_PROCESSES = ("legacy.exe", "spaceengineers.exe")

def se_running():
    try:
        out = subprocess.run(["tasklist", "/fo", "csv", "/nh"], capture_output=True, text=True, timeout=30).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    return [l.split(",")[0].strip().strip('"').lower() for l in out.splitlines()
            if l.split(",")[0].strip().strip('"').lower() in SE_PROCESSES]

def fmt(v):
    return ("%.1f" % v) if isinstance(v, float) and v != int(v) else str(int(v))

def set_position(block: str, pos) -> str:
    """Replaces the first <Position.../> inside an entity block (its PositionAndOrientation)."""
    new = '<Position x="%s" y="%s" z="%s" />' % tuple(fmt(c) for c in pos)
    m = re.search(r'<Position\s+x="[^"]*"\s+y="[^"]*"\s+z="[^"]*"\s*/>', block)
    if not m:
        raise SystemExit("no <Position .../> in the entity block")
    return block[:m.start()] + new + block[m.end():], block[m.start():m.end()]

def entity_blocks(text: str, xsi_type: str):
    """Yields (start, end) of every <MyObjectBuilder_EntityBase xsi:type="<xsi_type>">... </MyObjectBuilder_EntityBase>."""
    pat = re.compile(r'<MyObjectBuilder_EntityBase\s+xsi:type="%s">' % re.escape(xsi_type))
    for m in pat.finditer(text):
        end = text.find("</MyObjectBuilder_EntityBase>", m.end())
        if end < 0:
            raise SystemExit("unterminated entity block at " + str(m.start()))
        yield m.start(), end + len("</MyObjectBuilder_EntityBase>")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--save", default="DE Lab")
    ap.add_argument("--saves-root", required=True)
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()

    running = se_running()
    if running is None or running:
        sys.exit("Space Engineers is running or the process list is unreadable: refusing")
    folder = os.path.join(a.saves_root, a.save)
    sbs = os.path.join(folder, "SANDBOX_0_0_0_.sbs")
    if not os.path.isfile(sbs):
        sys.exit("not found: " + sbs)
    text = open(sbs, encoding="utf-8").read()

    planets = [(s, e) for s, e in entity_blocks(text, "MyObjectBuilder_Planet")
               if re.search(r"<StorageName>%s[^<]*</StorageName>" % re.escape(STORAGE_PREFIX), text[s:e])]
    if len(planets) != 1:
        sys.exit("expected exactly one DE_4546B planet entity, found %d (spawn it first: guide section 4)" % len(planets))
    chars = list(entity_blocks(text, "MyObjectBuilder_Character"))
    if len(chars) != 1:
        sys.exit("expected exactly one character entity, found %d" % len(chars))

    ps, pe = planets[0]
    new_planet, old_planet_pos = set_position(text[ps:pe], PLANET_POS)
    cs, ce = chars[0]
    new_char, old_char_pos = set_position(text[cs:ce], CHAR_POS)
    print("planet:", old_planet_pos, "->", new_planet[new_planet.find("<Position"):new_planet.find("/>", new_planet.find("<Position")) + 2])
    print("character:", old_char_pos, "->", new_char[new_char.find("<Position"):new_char.find("/>", new_char.find("<Position")) + 2])
    if a.dry_run:
        print("dry run: nothing written")
        return 0

    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    backup = folder + " - backup " + stamp
    shutil.copytree(folder, backup, ignore=shutil.ignore_patterns("Backup"))
    print("backup:", backup)

    # apply from the end so earlier offsets stay valid
    edits = sorted([(ps, pe, new_planet), (cs, ce, new_char)], key=lambda t: t[0], reverse=True)
    for s, e, repl in edits:
        text = text[:s] + repl + text[e:]
    with open(sbs, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    b5 = os.path.join(folder, "SANDBOX_0_0_0_.sbsB5")
    if os.path.exists(b5):
        os.remove(b5)
        print("deleted", b5)
    print("done: SE will show 'file format outdated' once at the next load (click OK)")
    return 0

if __name__ == "__main__":
    sys.exit(main())
