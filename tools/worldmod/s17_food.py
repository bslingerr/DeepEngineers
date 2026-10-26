"""S17 checks of the world mod's fish definitions (src/DeepEngineers.WorldMod/Data/PhysicalItems_DE_Food.sbc and
Blueprints_DE_Food.sbc), called by tools/worldmod/check_planet.py (`run`, S17 hook) and tests/worldmod/test_worldmod.py.
Rows (Report ids):

 s17.parse both files parse; roots <Definitions> holding exactly PhysicalItems / Blueprints + BlueprintClassEntries
 s17.schema.known every element and attribute is one SE's serializer knows for these definitions (embedded lists below)
 s17.items per kind of the twin (tools/s17/s17_ref.py KINDS) exactly a raw and a cooked ConsumableItem
 (MyObjectBuilder_ConsumableItemDefinition) with the twin's SubtypeIds, DisplayName "<name> (raw|cooked)",
 a description starting "DeepEngineers: a 4546B fish", the meal pack's mass, volume and size, exactly one
 Stat: Food = RawMicro / CookedMicro per TimeS (no Health stat, no water), at most SE's largest ration 0.0335
 s17.blueprints per kind one blueprint DE_CookFish_<Kind>: exactly 1 raw -> 1 cooked of that kind in CookS s, and its
 entry in the Food Processor's class FoodProcessorRations; nothing else
 s17.assets (with --se-content) every Icon and Model path exists in the game's Content (nothing copied), the class
 FoodProcessorRations exists in BlueprintClasses_Food.sbc and MealPack_KelpCrisp's Food is the raw value
"""
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
if os.path.join(ROOT, "tools", "s17") not in sys.path:
    sys.path.insert(0, os.path.join(ROOT, "tools", "s17"))
import s17_ref as T   # noqa: E402 (the kind table and values: the Wire twin)

ITEMS_REL = "Data/PhysicalItems_DE_Food.sbc"
BLUEPRINTS_REL = "Data/Blueprints_DE_Food.sbc"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
ITEM_ELEMS = {"Id", "DisplayName", "Description", "Icon", "Size", "Mass", "Volume", "Model", "PhysicalMaterial", "Stats",
              "UseSound", "IconSymbol"}
BLUEPRINT_ELEMS = {"Id", "DisplayName", "Icon", "Prerequisites", "Results", "BaseProductionTimeInSeconds"}
LARGEST_RATION = 0.0335

def _num(e, path):
    try:
        return float(e.findtext(path))
    except (TypeError, ValueError):
        return None

def _parse(mod, rel):
    try:
        return ET.parse(os.path.join(mod, rel)).getroot(), None
    except (OSError, ET.ParseError) as ex:
        return None, str(ex)

def check(rep, mod, se_content=None):
    items_root, e1 = _parse(mod, ITEMS_REL)
    bp_root, e2 = _parse(mod, BLUEPRINTS_REL)
    ok_parse = items_root is not None and bp_root is not None
    if ok_parse:
        ok_parse = items_root.tag == "Definitions" and [c.tag for c in items_root] == ["PhysicalItems"] and \
            bp_root.tag == "Definitions" and [c.tag for c in bp_root] == ["Blueprints", "BlueprintClassEntries"]
    rep.add("s17.parse", ok_parse, "both files parse with the expected roots" if ok_parse else
            "parse/roots: %s" % (e1 or e2 or "unexpected root children"))
    if not ok_parse:
        return
    # schema
    unknown = []
    for it in items_root.find("PhysicalItems"):
        unknown += ["PhysicalItem/" + c.tag for c in it if c.tag not in ITEM_ELEMS]
        for st in it.iter("Stat"):
            unknown += ["Stat@" + a for a in st.attrib if a not in ("Name", "Value", "Time")]
    for bp in bp_root.find("Blueprints"):
        unknown += ["Blueprint/" + c.tag for c in bp if c.tag not in BLUEPRINT_ELEMS]
        for i in bp.iter("Item"):
            unknown += ["Item@" + a for a in i.attrib if a not in ("Amount", "TypeId", "SubtypeId")]
    for en in bp_root.find("BlueprintClassEntries"):
        unknown += ["Entry@" + a for a in en.attrib if a not in ("Class", "BlueprintSubtypeId")]
    rep.add("s17.schema.known", not unknown, "every element and attribute known" if not unknown else "unknown: %s" % sorted(set(unknown))[:8])
    # items
    want = {}
    for name, _tt, disp in T.KINDS:
        want[T.raw_subtype(name)] = (disp + " (raw)", T.FOOD["RawMicro"] / 1e6)
        want[T.cooked_subtype(name)] = (disp + " (cooked)", T.FOOD["CookedMicro"] / 1e6)
    problems, seen, paths = [], [], set()
    for it in items_root.find("PhysicalItems"):
        sub = it.findtext("Id/SubtypeId")
        seen.append(sub)
        w = want.get(sub)
        if w is None:
            problems.append("unexpected %s" % sub)
            continue
        if it.get(XSI) != "MyObjectBuilder_ConsumableItemDefinition" or it.findtext("Id/TypeId") != "ConsumableItem":
            problems.append("%s type" % sub)
        if it.findtext("DisplayName") != w[0]:
            problems.append("%s DisplayName %r" % (sub, it.findtext("DisplayName")))
        if not (it.findtext("Description") or "").startswith("DeepEngineers: a 4546B fish"):
            problems.append("%s description" % sub)
        if _num(it, "Mass") != T.ITEM["MassG"] / 1000.0 or _num(it, "Volume") != T.ITEM["VolumeMl"] / 1000.0 or \
                any(_num(it, "Size/" + a) != T.ITEM["SizeMm"] / 1000.0 for a in "XYZ"):
            problems.append("%s mass/volume/size" % sub)
        stats = list(it.iter("Stat"))
        if len(stats) != 1 or stats[0].get("Name") != "Food":
            problems.append("%s stats %s (exactly one Food stat)" % (sub, [s.get("Name") for s in stats]))
        else:
            try:
                v, t = float(stats[0].get("Value")), int(stats[0].get("Time"))
            except (TypeError, ValueError):
                v, t = None, None
            if v is None or abs(v - w[1]) > 1e-9 or t != T.FOOD["TimeS"] or v > LARGEST_RATION:
                problems.append("%s Food %s/%s s (want %g/%d s)" % (sub, stats[0].get("Value"), stats[0].get("Time"), w[1], T.FOOD["TimeS"]))
        for tag in ("Icon", "Model"):
            if it.findtext(tag):
                paths.add(it.findtext(tag))
    missing = sorted(set(want) - set(seen))
    if missing:
        problems.append("missing %s" % missing[:4])
    if len(seen) != len(set(seen)):
        problems.append("duplicate SubtypeIds")
    rep.add("s17.items", not problems, "%d items (%d kinds raw + cooked), Food raw %g / cooked %g per %d s" %
            (len(seen), len(T.KINDS), T.FOOD["RawMicro"] / 1e6, T.FOOD["CookedMicro"] / 1e6, T.FOOD["TimeS"]) if not problems else "; ".join(problems[:6]))
    # blueprints
    problems = []
    bps = {bp.findtext("Id/SubtypeId"): bp for bp in bp_root.find("Blueprints")}
    entries = [(en.get("Class"), en.get("BlueprintSubtypeId")) for en in bp_root.find("BlueprintClassEntries")]
    want_bp = {T.blueprint(n): n for n, _t, _d in T.KINDS}
    if set(bps) != set(want_bp) or len(bps) != len(list(bp_root.find("Blueprints"))):
        problems.append("blueprints %s" % sorted(set(bps) ^ set(want_bp))[:4])
    for sub, bp in bps.items():
        n = want_bp.get(sub)
        if n is None:
            continue
        pe, re_ = bp.find("Prerequisites"), bp.find("Results")
        pre = [(i.get("Amount"), i.get("TypeId"), i.get("SubtypeId")) for i in (list(pe) if pe is not None else [])]
        res = [(i.get("Amount"), i.get("TypeId"), i.get("SubtypeId")) for i in (list(re_) if re_ is not None else [])]
        if pre != [("1", "ConsumableItem", T.raw_subtype(n))] or res != [("1", "ConsumableItem", T.cooked_subtype(n))]:
            problems.append("%s: %s -> %s" % (sub, pre, res))
        if _num(bp, "BaseProductionTimeInSeconds") != T.FOOD["CookS"]:
            problems.append("%s time" % sub)
        if bp.findtext("Icon"):
            paths.add(bp.findtext("Icon"))
    if sorted(entries) != sorted((T.FOOD_PROCESSOR_CLASS, b) for b in want_bp):
        problems.append("class entries %s" % sorted(set(entries) ^ {(T.FOOD_PROCESSOR_CLASS, b) for b in want_bp})[:4])
    rep.add("s17.blueprints", not problems, "%d blueprints raw -> cooked in %d s, class %s" % (len(bps), T.FOOD["CookS"], T.FOOD_PROCESSOR_CLASS)
            if not problems else "; ".join(problems[:6]))
    # assets and stock values (read-only)
    if se_content:
        missing = sorted(p for p in paths if not os.path.isfile(os.path.join(se_content, p.replace("\\", os.sep))))
        stock = []
        try:
            cls = ET.parse(os.path.join(se_content, "Data", "BlueprintClasses_Food.sbc")).getroot()
            if T.FOOD_PROCESSOR_CLASS not in [c.findtext("Id/SubtypeId") for c in cls.iter("Class")]:
                stock.append("no class %s" % T.FOOD_PROCESSOR_CLASS)
            food = ET.parse(os.path.join(se_content, "Data", "PhysicalItems_Food.sbc")).getroot()
            for it in food.iter("PhysicalItem"):
                if it.findtext("Id/SubtypeId") == "MealPack_KelpCrisp":
                    v = [float(s.get("Value")) for s in it.iter("Stat") if s.get("Name") == "Food"]
                    if v != [T.FOOD["RawMicro"] / 1e6]:
                        stock.append("MealPack_KelpCrisp Food %s" % v)
        except (OSError, ET.ParseError, ValueError, TypeError) as ex:
            stock.append(str(ex))
        rep.add("s17.assets", not missing and not stock, "%d icon/model paths in Content; class and KelpCrisp value as pinned" % len(paths)
                if not (missing or stock) else "missing in Content: %s %s" % (missing[:4], stock))
