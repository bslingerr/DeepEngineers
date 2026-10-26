"""S14 point 3 checks of the world mod's Water Mod block configs (src/DeepEngineers.WorldMod/Data/WaterConfig.xml,
<BlockConfigs>), called by tools/worldmod/check_planet.py (`run`, S14 hook) and tests/worldmod/test_worldmod.py. Rows (Report ids):

 s14.weapons.
 s14.weapons.entries exactly the Water Mod 3.23's 20 weapon entries (its own Data/WaterConfig.xml lines 38-57, embedded
 below), no duplicate (the Water Mod keeps the last one of a definition), no other block
 s14.weapons.values every entry FunctionalUnderwater="true" and every other attribute equal to the Water Mod's
 (Volume drives the block's buoyancy: a changed number would move ships)
 s14.weapons.known every attribute is a Water Mod BlockConfig member (BaseConfig TypeId/SubtypeId + BlockConfig's
 XmlAttributes; with --water-mod the names are read from the mod's)
 s14.weapons.watermod (with --water-mod) the embedded table equals the installed Water Mod's own entries (FunctionalUnderwater
 false there): the override is still about the mod as installed; SKIP without
"""
import os
import re
import xml.etree.ElementTree as ET

FILE_REL = "Data/WaterConfig.xml"
# (TypeId, SubtypeId, Volume, FunctionalAboveWater, PlayDamageEffect, IsPressurized): Water Mod 3.23 Data/WaterConfig.xml
WATER_MOD_WEAPONS = [
    ("LargeGatlingTurret", "", "23"),
    ("LargeGatlingTurret", "SmallGatlingTurret", "3.375"),
    ("LargeMissileTurret", "", "23"),
    ("LargeMissileTurret", "SmallMissileTurret", "3.375"),
    ("InteriorTurret", "LargeInteriorTurret", "1.125"),
    ("SmallMissileLauncher", "", "0.5"),
    ("SmallMissileLauncher", "LargeMissileLauncher", "31.25"),
    ("SmallMissileLauncherReload", "SmallRocketLauncherReload", "1.125"),
    ("SmallGatlingGun", "", "0.3125"),
    ("SmallGatlingGun", "SmallBlockAutocannon", "0.3125"),
    ("SmallMissileLauncherReload", "SmallBlockMediumCalibreGun", "0.625"),
    ("SmallMissileLauncher", "LargeBlockLargeCalibreGun", "29.375"),
    ("SmallMissileLauncherReload", "LargeRailgun", "210"),
    ("SmallMissileLauncherReload", "SmallRailgun", "1.875"),
    ("LargeMissileTurret", "LargeCalibreTurret", "31.25"),
    ("LargeMissileTurret", "LargeBlockMediumCalibreTurret", "23.125"),
    ("LargeMissileTurret", "SmallBlockMediumCalibreTurret", "1.375"),
    ("LargeGatlingTurret", "AutoCannonTurret", "1.25"),
    ("SmallMissileLauncher", "SmallMissileLauncherWarfare2", "0.5"),
    ("SmallGatlingGun", "SmallGatlingGunWarfare2", "0.3125"),
]
COMMON = {"FunctionalAboveWater": "true", "PlayDamageEffect": "false", "IsPressurized": "false"}
# BaseConfig (TypeId, SubtypeId) + BlockConfig's [XmlAttribute] names (Water Mod 3.23)
KNOWN_ATTRIBUTES = {"TypeId", "SubtypeId", "Volume", "FunctionalUnderwater", "FunctionalAboveWater", "MaximumPressure",
                    "MaxFunctionalPressure", "PlayDamageEffect", "IsPressurized"}
EXPECTED_COUNT = 20

def _norm_bool(v):
    return (v or "").strip().lower()

def _entries(root):
    return [b for b in root.findall("BlockConfigs/*")]

def check(rep, mod, water_mod=None):
    path = os.path.join(mod, FILE_REL)
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as e:
        rep.add("s14.weapons.parse", False, str(e))
        return
    items = _entries(root)
    rep.add("s14.weapons.parse", len(root.findall("BlockConfigs")) == 1 and len(items) > 0,
            "%d <BlockConfigs>, %d entries" % (len(root.findall("BlockConfigs")), len(items)))
    want = {(t, s): v for t, s, v in WATER_MOD_WEAPONS}
    got = [(b.get("TypeId"), b.get("SubtypeId")) for b in items]
    dup = sorted({k for k in got if got.count(k) > 1})
    bad_tag = [b.tag for b in items if b.tag != "BlockConfig"]
    extra = sorted(set(got) - set(want))
    missing = sorted(set(want) - set(got))
    ok = not dup and not bad_tag and not extra and not missing and len(items) == EXPECTED_COUNT == len(WATER_MOD_WEAPONS)
    rep.add("s14.weapons.entries", ok, "the Water Mod's %d weapon entries" % EXPECTED_COUNT if ok else
            "duplicates %r, other blocks %r, missing %r, other tags %r" % (dup, extra, missing, bad_tag))
    problems = []
    for b in items:
        k = (b.get("TypeId"), b.get("SubtypeId"))
        if k not in want:
            continue
        if _norm_bool(b.get("FunctionalUnderwater")) != "true":
            problems.append("%s/%s FunctionalUnderwater=%r" % (k[0], k[1], b.get("FunctionalUnderwater")))
        if b.get("Volume") != want[k]:
            problems.append("%s/%s Volume=%r (the Water Mod's %s)" % (k[0], k[1], b.get("Volume"), want[k]))
        for a, v in COMMON.items():
            if _norm_bool(b.get(a)) != v:
                problems.append("%s/%s %s=%r (the Water Mod's %s)" % (k[0], k[1], a, b.get(a), v))
        for a in ("MaximumPressure", "MaxFunctionalPressure"):
            if b.get(a) is not None:
                problems.append("%s/%s sets %s (the Water Mod's entry leaves it at its default)" % (k[0], k[1], a))
    rep.add("s14.weapons.values", not problems, "FunctionalUnderwater true, every other attribute the Water Mod's" if not problems else
            "; ".join(problems[:6]) + (" (+%d)" % (len(problems) - 6) if len(problems) > 6 else ""))
    known = set(KNOWN_ATTRIBUTES)
    src_note = "embedded list"
    if water_mod:
        bc = os.path.join(water_mod, "Configs", "BlockConfig.cs")
        base = os.path.join(water_mod, "Configs", "BaseConfig.cs")
        try:
            names = set()
            for p in (bc, base):
                with open(p, encoding="utf-8-sig") as f:
                    names |= set(re.findall(r'XmlAttribute\("([A-Za-z]+)"\)', f.read()))
            if names:
                known = names
                src_note = "the mod's Configs/BlockConfig.cs + BaseConfig.cs"
        except OSError:
            pass
    unknown = sorted({a for b in items for a in b.attrib if a not in known})
    rep.add("s14.weapons.known", not unknown, "every attribute a BlockConfig member (%s)" % src_note if not unknown else
            "unknown to the Water Mod (ignored by its XmlSerializer): %s" % ", ".join(unknown))
    if not water_mod:
        rep.skip("s14.weapons.watermod", "no --water-mod: the embedded Water Mod 3.23 table stands")
        return
    wm_cfg = os.path.normpath(os.path.join(water_mod, "..", "..", "..", "WaterConfig.xml"))
    try:
        wroot = ET.parse(wm_cfg).getroot()
    except (ET.ParseError, OSError) as e:
        rep.skip("s14.weapons.watermod", "the Water Mod's own %s unreadable: %s" % (wm_cfg, e))
        return
    theirs = {(b.get("TypeId"), b.get("SubtypeId")): b for b in wroot.findall("BlockConfigs/BlockConfig")}
    diffs = []
    for t, s, v in WATER_MOD_WEAPONS:
        b = theirs.get((t, s))
        if b is None:
            diffs.append("%s/%s not in the Water Mod" % (t, s))
            continue
        if _norm_bool(b.get("FunctionalUnderwater")) != "false":
            diffs.append("%s/%s already functional under water there" % (t, s))
        if b.get("Volume") != v:
            diffs.append("%s/%s Volume %r there" % (t, s, b.get("Volume")))
        for a, val in COMMON.items():
            if _norm_bool(b.get(a)) != val:
                diffs.append("%s/%s %s %r there" % (t, s, a, b.get(a)))
    rep.add("s14.weapons.watermod", not diffs, "the installed Water Mod lists the same %d weapons, off under water" % len(WATER_MOD_WEAPONS)
            if not diffs else "; ".join(diffs[:6]))
