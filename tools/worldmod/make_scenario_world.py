"""Builds the "DeepEngineers 4546B" scenario world: Space Engineers' own Star System plus our planet.

 python -I tools/worldmod/make_scenario_world.py [--out DIR] [--to-saves NAME] [--to-local] [--local-mod] [--force]

What it does (read-only on the game install):
- Takes the player's own Star System custom world (<SE>\\Content\\CustomWorlds\\Star System: the XML sector, the planets'
 voxel storages, the checkpoint and the world configuration) and the DeepEngineers template world
 (tools/release/world-template/DeepEngineers: our planet's entity, its voxel storage and the Water Mod's sea).
- Writes a new world: Star System's eight planets as they are, plus DE_4546B with its map side facing the inner
 system (centre 0 / 0 / -1,820,000 m: the map is on the planet's +Z side, Earth at the origin is 1,820 km away along +Z), the three mods the sea needs in BOTH world files (SE overrides the
 checkpoint's Mods, Settings and SessionName with Sandbox_config.sbc when it exists: decomp MyLocalCache.LoadCheckpoint),
 the 20-minute day the plugin's time sync wants (SunRotationIntervalMinutes 20, EnableSunRotation, ExperimentalMode), the
 session name and description, and the Water Mod's sea of DE_4546B: a ScriptManagerData dictionary item (Key JWater2,
 bound to the planet's EntityId) that the Water Mod otherwise creates only when a planet is added to a running session. The binary sector
 (.sbsB5) is left out so SE reads the XML one and writes its own.
- The result ships as a Workshop world the project publishes from Load Game; --to-saves NAME copies it under the player's
 saves for that and for Load Game tests, --to-local into %LOCALAPPDATA%\\DeepEngineers\\worlds. Never copies the game's
 thumbnail; nothing of the result goes into the release (built on the player's machine from his own install). Stdlib only.
"""
import argparse
import glob
import os
import re
import shutil
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SE_CONTENT = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content"
STAR_SYSTEM = os.path.join(SE_CONTENT, "CustomWorlds", "Star System")
# S9 round 2: the installer runs this from the release folder on the player's PC: --se-content and
# --template name the player's game and the shipped template, --local-root the %LOCALAPPDATA%\DeepEngineers folder
TEMPLATE = os.path.join(REPO, "tools", "release", "world-template", "DeepEngineers")
NAME = "DeepEngineers 4546B"
DESCRIPTION = ("Space Engineers' Star System with planet 4546B added: an ocean world rendered by Subnautica, 1,820 km "
               "from Earth. Start anywhere in the system, or in the 4546B lifepod above the Safe Shallows. The sea, its "
               "creatures, ores and crush depths exist on 4546B only. Needs the DeepEngineers plugins and the Water Mod.")
# The world mod as published on the Workshop by the project: the published world refers to it by id,
# so players get it as a dependency; --local-mod keeps the local folder "DeepEngineers.World" instead (the dev worlds).
WORLD_MOD_ID = "3826432715"
MODS = (("DeepEngineers.World", WORLD_MOD_ID + ".sbm", WORLD_MOD_ID), ("Water Mod", "2200451495.sbm", "2200451495"),
        ("Text HUD API", "758597413.sbm", "758597413"))
MODS_LOCAL = (("DeepEngineers.World", "DeepEngineers.World", "0"),) + MODS[1:]
SETTINGS = (("SunRotationIntervalMinutes", "20"), ("EnableSunRotation", "true"), ("ExperimentalMode", "true"))
CENTRE = (0.0, 0.0, -1820000.0)   # the planet's centre in the scenario world (see the placement note in main)
HALF = 131072.5                   # the template entity's Position is its centre minus this on each axis

def mods_xml(local=False):
    out = []
    for friendly, name, pid in (MODS_LOCAL if local else MODS):
        out.append('    <ModItem FriendlyName="%s">\n      <Name>%s</Name>\n      <PublishedFileId>%s</PublishedFileId>\n'
                   '      <PublishedServiceName>Steam</PublishedServiceName>\n%s    </ModItem>\n'
                   % (friendly, name, pid, "      <IsDependency>true</IsDependency>\n" if pid == "758597413" else ""))
    return "  <Mods>\n" + "".join(out) + "  </Mods>"

def edit_world_file(text, changes, local=False):
    """SessionName, Description, the mods and the settings in a checkpoint or a configuration file."""
    n = 0
    text, k = re.subn(r"<SessionName>[^<]*</SessionName>", "<SessionName>%s</SessionName>" % NAME, text, count=1); n += k
    if re.search(r"<Description>[^<]*</Description>", text):
        text, k = re.subn(r"<Description>[^<]*</Description>", "<Description>%s</Description>" % DESCRIPTION, text, count=1); n += k
    else:
        text, k = re.subn(r"(<SessionName>[^<]*</SessionName>\n)", r"\1    <Description>%s</Description>\n" % DESCRIPTION, text, count=1); n += k
    if re.search(r"<Mods\s*/>", text):
        text, k = re.subn(r"[ \t]*<Mods\s*/>", mods_xml(local), text, count=1); n += k
    else:
        text, k = re.subn(r"[ \t]*<Mods>.*?</Mods>", mods_xml(local), text, count=1, flags=re.S); n += k
    for key, val in SETTINGS:
        if re.search(r"<%s>[^<]*</%s>" % (key, key), text):
            text, k = re.subn(r"<%s>[^<]*</%s>" % (key, key), "<%s>%s</%s>" % (key, val, key), text, count=1); n += k
        else:
            text, k = re.subn(r"(\n(\s*)</Settings>)", r"\n\2  <%s>%s</%s>\1" % (key, val, key), text, count=1); n += k
    changes.append("%d edit(s)" % n)
    return text

def water_item(checkpoint_text):
    """The Water Mod's sea of DE_4546B in the template's checkpoint: a ScriptManagerData dictionary item (Key JWater2, a
 WaterModComponentObjectBuilder as an escaped string, bound to the planet's EntityId)."""
    m = re.search(r"[ \t]*<item>\s*<Key>JWater2</Key>.*?</item>\n", checkpoint_text, re.S)
    if not m:
        sys.exit("no JWater2 water item in the template checkpoint")
    return m.group(0)

def add_water(checkpoint_text, item):
    """Puts the water item into the checkpoint's ScriptManagerData dictionary (empty in Star System's)."""
    m = re.search(r"<ScriptManagerData>.*?</ScriptManagerData>", checkpoint_text, re.S)
    if not m:
        sys.exit("no ScriptManagerData in the Star System checkpoint")
    block = m.group(0)
    if "JWater2" in block:
        return checkpoint_text
    if re.search(r"<dictionary\s*/>", block):
        new_block = re.sub(r"[ \t]*<dictionary\s*/>", lambda _m: "        <dictionary>\n" + item + "        </dictionary>", block, count=1)
    else:
        new_block = block.replace("<dictionary>", "<dictionary>\n" + item, 1)
    return checkpoint_text.replace(block, new_block, 1)

def planet_entity(sector_text):
    """Our planet's entity block in the template's sector (one MyObjectBuilder_Planet)."""
    m = re.search(r"[ \t]*<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_Planet\">.*?</MyObjectBuilder_EntityBase>\n", sector_text, re.S)
    if not m:
        sys.exit("no planet entity in the template sector")
    return m.group(0)

def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(REPO, "out", "scenario", NAME))
    ap.add_argument("--to-saves", default=None, help="also copy the world under <APPDATA>/SpaceEngineers/Saves/<steamid>/<NAME> (Load Game, Publish)")
    ap.add_argument("--to-local", action="store_true", help="copy the world to %%LOCALAPPDATA%%/DeepEngineers/worlds/<NAME>")
    ap.add_argument("--local-mod", action="store_true", help="the local mod folder DeepEngineers.World instead of the Workshop item %s (dev worlds)" % WORLD_MOD_ID)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--se-content", default=None, help="the game's Content folder (default: the Steam install on this PC)")
    ap.add_argument("--template", default=None, help="the template world folder (default: the repository's tools/release/world-template/DeepEngineers)")
    ap.add_argument("--local-root", default=None, help="the folder for --to-local (default: %%LOCALAPPDATA%%/DeepEngineers)")
    a = ap.parse_args(argv)
    global STAR_SYSTEM, TEMPLATE
    if a.se_content:
        STAR_SYSTEM = os.path.join(a.se_content, "CustomWorlds", "Star System")
    if a.template:
        TEMPLATE = a.template
    for p in (STAR_SYSTEM, TEMPLATE):
        if not os.path.isdir(p):
            sys.exit("missing: " + p)
    src_sector = open(os.path.join(STAR_SYSTEM, "SANDBOX_0_0_0_.sbs"), encoding="utf-8-sig").read()
    tpl_sector = open(os.path.join(TEMPLATE, "SANDBOX_0_0_0_.sbs"), encoding="utf-8-sig").read()
    ours = planet_entity(tpl_sector)
    if "DE_4546B" not in ours:
        sys.exit("the template's planet entity is not DE_4546B")
    # the entity's Name is what SE prints in the Spawn Points list ("<Name> <pod>") and on GPS marks; the plugin finds the
 # planet by its generator and its StorageName, never by this name
    ours = re.sub(r"<Name>[^<]*</Name>", "<Name>4546B</Name>", ours, count=1)
    # Placement: the Subnautica map sits on the planet's +Z side (site anchor U0 = +Z: the map origin is centre + Rs
 # along +Z), so the centre goes on Earth's -Z axis at 1,820 km: a ship from Earth, the Moon or the inner system arrives at
 # the map side. Earth at the origin: 1,820 km; Moon 1,710 km; Mars 3,600 km; Triton 3,280 km; Pertam 4,100 km; Alien 7,550 km.
 # The entity's Position is the storage's min corner: centre - 131,072.5 m on each axis (the template's own offset).
    pos = (CENTRE[0] - HALF, CENTRE[1] - HALF, CENTRE[2] - HALF)
    ours, k = re.subn(r'<Position x="[^"]*" y="[^"]*" z="[^"]*" />', '<Position x="%s" y="%s" z="%s" />' % tuple(repr(v) for v in pos), ours, count=1)
    if k != 1:
        sys.exit("the planet entity's Position was not replaced")
    if "DE_4546B" in src_sector:
        sys.exit("Star System already holds a DE_4546B entity?")
    sector = src_sector.replace("  </SectorObjects>", ours + "  </SectorObjects>", 1)
    if sector == src_sector:
        sys.exit("</SectorObjects> not found in the Star System sector")
    planets = len(re.findall(r'xsi:type="MyObjectBuilder_Planet"', sector))
    out = a.out
    if os.path.isdir(out):
        if not a.force:
            sys.exit("exists (use --force): " + out)
        shutil.rmtree(out)
    os.makedirs(out)
    changes = []
    water = water_item(open(os.path.join(TEMPLATE, "Sandbox.sbc"), encoding="utf-8-sig").read())
    for fn in ("Sandbox.sbc", "Sandbox_config.sbc"):
        text = open(os.path.join(STAR_SYSTEM, fn), encoding="utf-8-sig").read()
        text = edit_world_file(text, changes, a.local_mod)
        if fn == "Sandbox.sbc":
            text = add_water(text, water)
            if "JWater2" not in text:
                sys.exit("the water item did not land in the checkpoint")
            changes[-1] += " + the sea (JWater2)"
        with open(os.path.join(out, fn), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    with open(os.path.join(out, "SANDBOX_0_0_0_.sbs"), "w", encoding="utf-8", newline="\n") as f:
        f.write(sector)
    copied = []
    for fn in sorted(os.listdir(STAR_SYSTEM)):
        if fn.endswith(".vx2"):
            shutil.copy2(os.path.join(STAR_SYSTEM, fn), os.path.join(out, fn)); copied.append(fn)
    for fn in sorted(os.listdir(TEMPLATE)):
        if fn.endswith(".vx2"):
            shutil.copy2(os.path.join(TEMPLATE, fn), os.path.join(out, fn)); copied.append(fn)
    print("world: %s" % out)
    print("planets in the sector: %d (Star System's %d + DE_4546B)" % (planets, planets - 1))
    print("voxel storages copied: %s" % ", ".join(copied))
    print("world files edited: Sandbox.sbc %s, Sandbox_config.sbc %s" % tuple(changes))
    local_dst = os.path.join(a.local_root or os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers"), "worlds", NAME)
    for label, flag, dst in (("saves", a.to_saves, None), ("local", a.to_local, local_dst)):
        if not flag:
            continue
        if label == "saves":
            roots = glob.glob(os.path.join(os.environ.get("APPDATA", ""), "SpaceEngineers", "Saves", "*", ""))
            roots = [r for r in roots if os.path.basename(os.path.dirname(r)).startswith("7656") and os.path.isfile(os.path.join(r, "LastSession.sbl"))]
            if not roots:
                sys.exit("no Saves/<SteamID64> folder with a LastSession.sbl")
            dst = os.path.join(roots[0], a.to_saves)
        if os.path.isdir(dst):
            if not a.force:
                sys.exit("exists (use --force): " + dst)
            shutil.rmtree(dst)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copytree(out, dst)
        print("copied to %s: %s" % (label, dst))
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
