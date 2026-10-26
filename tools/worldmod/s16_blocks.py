"""S16 checks of the world mod's pressure hull definitions (src/DeepEngineers.WorldMod/Data/CubeBlocks_S16.sbc), called by
tools/worldmod/check_planet.py (`run`, S16 hook) and tests/worldmod/test_worldmod.py. Rows (Report ids):

 s16.parse the file parses; root <Definitions> holding exactly Components, Blueprints, BlueprintClassEntries,
 CubeBlocks, CategoryClasses, BlockVariantGroups
 s16.schema.known every element and attribute is one the game's serializer knows

 s16.blocks four plain CubeBlock definitions DE_PressureHullMk<1..2><Large|Small> (depth 11), CubeSize = the suffix, 1x1x1,
 the tier and size the Wire rules read (tools/s16/s16_ref.py tier_of), a shared BlockPairName per tier
 s16.blocks.components every component is a stock component or this file's alloy, the tier's alloy is in the list and is
 the critical component, no Computer (a non-terminal block with a Computer is a definition error:
 MyDefinitionManager.InitCubeBlocks), counts > 0
 s16.alloys two components DE_HullAlloyMk1..2 and two blueprints making one each from exactly the tier's
 ingots (the twin's TIERS: Iron,Nickel,Silicon / Cobalt,Magnesium,Silver / Gold,Platinum,Uranium),
 each blueprint in the assembler class Components
 s16.category one GUI category naming the four blocks; two variant groups, Large + Small of one tier each
 s16.assets (with --se-content) every Model, construction model and Icon path exists in the game's Content
 (the mod ships no model or texture: ProcessContentFilePath falls back to Content)
 decomp.s16 the embedded names equal the decompiled builders' members
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
if os.path.join(ROOT, "tools", "s16") not in sys.path:
    sys.path.insert(0, os.path.join(ROOT, "tools", "s16"))
import s16_ref as T   # noqa: E402 (the tier table: the Wire twin)

FILE_REL = "Data/CubeBlocks_S16.sbc"
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"
ROOT_ARRAYS = ["Components", "Blueprints", "BlueprintClassEntries", "CubeBlocks", "CategoryClasses", "BlockVariantGroups"]
ARRAY_ITEMS = {"Components": "Component", "Blueprints": "Blueprint", "BlueprintClassEntries": "Entry", "CubeBlocks": "Definition",
               "CategoryClasses": "Category", "BlockVariantGroups": "BlockVariantGroup"}
BASE = {"Id", "DisplayName", "Description", "Icon", "Public", "Enabled", "AvailableInSurvival", "DLC", "DescriptionArgs"}
COMPONENT = BASE | {"Size", "Mass", "Volume", "Model", "PhysicalMaterial", "MaxIntegrity", "DropProbability", "Health",
                    "CanPlayerOrder", "CanPlayerOffer"}
BLUEPRINT = BASE | {"Prerequisites", "Result", "BaseProductionTimeInSeconds"}
CUBE = BASE | {"CubeSize", "BlockTopology", "Size", "ModelOffset", "Model", "Components", "CriticalComponent", "MountPoints",
               "BuildProgressModels", "BlockPairName", "MirroringY", "MirroringZ", "EdgeType", "BuildTimeSeconds", "IsAirTight", "PCU"}
CATEGORY = BASE | {"Name", "ItemIds"}
VARIANT = BASE | {"Blocks"}
ITEM_ATTRS = {"Amount", "TypeId", "SubtypeId"}
ENTRY_ATTRS = {"Class", "BlueprintSubtypeId"}
COMPONENT_REF_ATTRS = {"Subtype", "Count"}
CRITICAL_ATTRS = {"Subtype", "Index"}
MOUNT_ATTRS = {"Side", "StartX", "StartY", "EndX", "EndY", "Default"}
BPM_ATTRS = {"BuildPercentUpperBound", "File"}
DEFID_ELEMS = {"TypeId", "SubtypeId"}
DEFID_ATTRS = {"Type", "Subtype"}
STOCK_COMPONENTS = {"SteelPlate", "Construction", "InteriorPlate", "Girder", "SmallTube", "LargeTube", "MetalGrid", "Motor",
                    "BulletproofGlass", "Computer", "Display", "Detector", "Medical", "RadioCommunication", "Reactor", "Thrust",
                    "Superconductor", "GravityGenerator", "SolarCell", "PowerCell", "Explosives"}
SIDES = {"Front", "Back", "Left", "Right", "Top", "Bottom"}

def _alloy(tier):
    return "%s%d" % (T.ALLOY_PREFIX, tier)

def _id(el):
    i = el.find("Id")
    if i is None:
        return None, None
    if i.get("Type") is not None:
        return i.get("Type"), i.get("Subtype")
    return (i.findtext("TypeId") or "").strip(), (i.findtext("SubtypeId") or "").strip()

def _unknown(el, allowed, where, out):
    for c in el:
        if c.tag not in allowed:
            out.append("%s: <%s>" % (where, c.tag))

def _attrs(el, allowed, where, out):
    for a in el.attrib:
        if a not in allowed and a != XSI:
            out.append("%s: @%s" % (where, a))

def check(rep, mod, decomp_se=None, se_content=None):
    path = os.path.join(mod, *FILE_REL.split("/"))
    if not os.path.isfile(path):
        rep.add("s16.parse", False, "missing: %s" % FILE_REL)
        return
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as e:
        rep.add("s16.parse", False, str(e))
        return
    kids = [c.tag for c in root]
    rep.add("s16.parse", root.tag == "Definitions" and kids == ROOT_ARRAYS, "root <%s>, arrays %s" % (root.tag, kids))

    # ---- schema (names)
    bad = []
    for arr in root:
        item = ARRAY_ITEMS.get(arr.tag)
        if item is None:
            bad.append("root: <%s>" % arr.tag)
            continue
        for el in arr:
            if el.tag != item:
                bad.append("%s: <%s> (item name %s)" % (arr.tag, el.tag, item))
                continue
            where = "%s/%s %s" % (arr.tag, item, _id(el)[1])
            if arr.tag == "Components":
                _unknown(el, COMPONENT, where, bad)
                sz = el.find("Size")
                if sz is not None:
                    _unknown(sz, {"X", "Y", "Z"}, where + " Size", bad)
            elif arr.tag == "Blueprints":
                _unknown(el, BLUEPRINT, where, bad)
                for it in el.iter("Item"):
                    _attrs(it, ITEM_ATTRS, where + " Item", bad)
                pre = el.find("Prerequisites")
                if pre is not None:
                    _unknown(pre, {"Item"}, where + " Prerequisites", bad)
                res = el.find("Result")
                if res is not None:
                    _attrs(res, ITEM_ATTRS, where + " Result", bad)
            elif arr.tag == "BlueprintClassEntries":
                _attrs(el, ENTRY_ATTRS, where, bad)
            elif arr.tag == "CubeBlocks":
                _unknown(el, CUBE, where, bad)
                for k, allowed, child in (("Components", COMPONENT_REF_ATTRS, "Component"), ("MountPoints", MOUNT_ATTRS, "MountPoint"),
                                          ("BuildProgressModels", BPM_ATTRS, "Model")):
                    box = el.find(k)
                    if box is None:
                        continue
                    _unknown(box, {child}, where + " " + k, bad)
                    for c in box:
                        _attrs(c, allowed, where + " " + k + "/" + c.tag, bad)
                cc = el.find("CriticalComponent")
                if cc is not None:
                    _attrs(cc, CRITICAL_ATTRS, where + " CriticalComponent", bad)
                for k in ("Size", "ModelOffset"):
                    v = el.find(k)
                    if v is not None:
                        _attrs(v, {"x", "y", "z"}, where + " " + k, bad)
            elif arr.tag == "CategoryClasses":
                _unknown(el, CATEGORY, where, bad)
                ids = el.find("ItemIds")
                if ids is not None:
                    _unknown(ids, {"string"}, where + " ItemIds", bad)
            elif arr.tag == "BlockVariantGroups":
                _unknown(el, VARIANT, where, bad)
                bl = el.find("Blocks")
                if bl is not None:
                    _unknown(bl, {"Block"}, where + " Blocks", bad)
                    for b in bl:
                        _attrs(b, DEFID_ATTRS, where + " Block", bad)
            i = el.find("Id")
            if i is not None:
                _unknown(i, DEFID_ELEMS, where + " Id", bad)
                _attrs(i, DEFID_ATTRS, where + " Id", bad)
    rep.add("s16.schema.known", not bad, "every name known to the serializer" if not bad else "unknown: %s" % "; ".join(bad[:8]))

    # ---- blocks
    blocks = root.find("CubeBlocks")
    blocks = list(blocks) if blocks is not None else []
    want = {"DE_PressureHullMk%d%s" % (t, s) for t, _sub, _m, _i in T.TIERS for s in ("Large", "Small")}
    got, probs, pairs = set(), [], {}
    for el in blocks:
        typ, sub = _id(el)
        got.add(sub)
        if typ != "CubeBlock":
            probs.append("%s TypeId %s" % (sub, typ))
        if el.get(XSI) is not None:
            probs.append("%s has xsi:type (a plain CubeBlock definition has none)" % sub)
        tier = T.tier_of(sub)
        size = "Large" if sub.endswith("Large") else "Small"
        if tier == 0:
            probs.append("%s is not a tier subtype of the Wire table" % sub)
        if (el.findtext("CubeSize") or "").strip() != size:
            probs.append("%s CubeSize %s" % (sub, el.findtext("CubeSize")))
        sz = el.find("Size")
        if sz is None or (sz.get("x"), sz.get("y"), sz.get("z")) != ("1", "1", "1"):
            probs.append("%s is not 1x1x1" % sub)
        if not (el.findtext("Model") or "").strip():
            probs.append("%s has no Model" % sub)
        pairs.setdefault((el.findtext("BlockPairName") or "").strip(), set()).add(sub)
        for mp in el.iter("MountPoint"):
            if mp.get("Side") not in SIDES:
                probs.append("%s mount point side %s" % (sub, mp.get("Side")))
    if got != want:
        probs.append("subtypes: missing %s, extra %s" % (sorted(want - got), sorted(got - want)))
    for name, subs in pairs.items():
        if len(subs) != 2 or {s[:-5] for s in subs} != {name}:
            probs.append("pair %r holds %s" % (name, sorted(subs)))
    rep.add("s16.blocks", not probs, ("%d CubeBlock definitions, Mk1-%d x Large/Small, 1x1x1, paired" % (2 * len(T.TIERS), len(T.TIERS))) if not probs else "; ".join(probs[:6]))

    # ---- components of the blocks
    alloys = {(_id(c)[1]) for c in (root.find("Components") or [])}
    probs = []
    for el in blocks:
        sub = _id(el)[1]
        tier = T.tier_of(sub)
        comps = [(c.get("Subtype"), c.get("Count")) for c in (el.find("Components") or [])]
        names = [c[0] for c in comps]
        if not comps:
            probs.append("%s has no components" % sub)
        for n, cnt in comps:
            if n not in STOCK_COMPONENTS and n not in alloys:
                probs.append("%s: component %s is neither stock nor this file's" % (sub, n))
            try:
                if int(cnt) <= 0:
                    probs.append("%s: %s count %s" % (sub, n, cnt))
            except (TypeError, ValueError):
                probs.append("%s: %s count %r" % (sub, n, cnt))
        if "Computer" in names:
            probs.append("%s: a Computer on a non-terminal CubeBlock (MyDefinitionManager.InitCubeBlocks error)" % sub)
        if tier and _alloy(tier) not in names:
            probs.append("%s: its alloy %s is missing" % (sub, _alloy(tier)))
        for other in [t for t, _sub, _m, _i in T.TIERS]:
            if other != tier and _alloy(other) in names:
                probs.append("%s: uses %s of another tier" % (sub, _alloy(other)))
        cc = el.find("CriticalComponent")
        if cc is None or cc.get("Subtype") != _alloy(tier) or cc.get("Index") != "0":
            probs.append("%s: the critical component is not %s index 0" % (sub, _alloy(tier)))
    rep.add("s16.blocks.components", not probs, "stock components + the tier's alloy (critical), no Computer" if not probs else "; ".join(probs[:6]))

    # ---- alloys and blueprints
    probs = []
    comps = {(_id(c)[1]): c for c in (root.find("Components") or [])}
    bps = {(_id(b)[1]): b for b in (root.find("Blueprints") or [])}
    entries = {(e.get("Class"), e.get("BlueprintSubtypeId")) for e in (root.find("BlueprintClassEntries") or [])}
    for t, _sub, _m, ingots in T.TIERS:
        a = _alloy(t)
        c = comps.get(a)
        if c is None or _id(c)[0] != "Component":
            probs.append("component %s missing" % a)
        else:
            for k in ("Mass", "Volume", "MaxIntegrity", "Model", "Icon"):
                if not (c.findtext(k) or "").strip():
                    probs.append("%s has no %s" % (a, k))
        b = bps.get(a)
        if b is None or _id(b)[0] != "BlueprintDefinition":
            probs.append("blueprint %s missing" % a)
            continue
        items = [(it.get("TypeId"), it.get("SubtypeId")) for it in b.iter("Item")]
        if sorted(items) != sorted(("Ingot", i) for i in ingots.split(",")):
            probs.append("%s is made of %s, the table says %s ingots" % (a, items, ingots))
        res = b.find("Result")
        if res is None or (res.get("TypeId"), res.get("SubtypeId"), res.get("Amount")) != ("Component", a, "1"):
            probs.append("%s result %r" % (a, res.attrib if res is not None else None))
        if ("Components", a) not in entries:
            probs.append("%s not in the assembler class Components" % a)
    if set(comps) != {_alloy(t) for t, _sub, _m, _i in T.TIERS}:
        probs.append("components %s" % sorted(comps))
    rep.add("s16.alloys", not probs, ("Mk1-%d alloys from %s" % (len(T.TIERS), " / ".join(i for _t, _s, _m, i in T.TIERS))) if not probs else "; ".join(probs[:6]))

    # ---- category and variant groups
    probs = []
    cats = list(root.find("CategoryClasses") or [])
    if len(cats) != 1:
        probs.append("%d categories" % len(cats))
    else:
        ids = {(s.text or "").strip() for s in cats[0].iter("string")}
        if ids != {"CubeBlock/" + w for w in want}:
            probs.append("category items %s" % sorted(ids))
        if not (cats[0].findtext("Name") or "").strip():
            probs.append("category without Name (categories merge by Name)")
    groups = list(root.find("BlockVariantGroups") or [])
    if len(groups) != len(T.TIERS):
        probs.append("%d variant groups" % len(groups))
    for g in groups:
        subs = {b.get("Subtype") for b in g.iter("Block")}
        tiers = {T.tier_of(s) for s in subs}
        if len(subs) != 2 or len(tiers) != 1 or 0 in tiers or {b.get("Type") for b in g.iter("Block")} != {"MyObjectBuilder_CubeBlock"}:
            probs.append("group %s holds %s" % (_id(g)[1], sorted(subs)))
    rep.add("s16.category", not probs, ("category DeepEngineers (%d blocks), %d variant groups" % (2 * len(T.TIERS), len(T.TIERS))) if not probs else "; ".join(probs))

    # ---- assets (read-only check against the game's Content)
    paths = []
    for el in root.iter():
        if el.tag in ("Model", "Icon") and (el.text or "").strip():
            paths.append(el.text.strip())
        if el.tag == "Model" and el.get("File"):
            paths.append(el.get("File"))
    shipped = [f for f in _files(mod) if f.lower().endswith((".mwm", ".dds", ".png")) and not f.startswith("Data/PlanetDataFiles/")
               and f not in ("Textures/DE_4546B_Water.dds", "Textures/DE_4546B_WaterFar.dds")]   # S15 hook: the sea's generated texture (tools/s15/gen_water_texture.py), not a game asset
    if shipped:
        rep.add("s16.assets", False, "the mod ships model/texture files %s (the S16 blocks reuse the game's by path)" % shipped)
    elif se_content:
        missing = [p for p in paths if not os.path.isfile(os.path.join(se_content, *p.replace("\\", "/").split("/")))]
        rep.add("s16.assets", not missing, "%d model/icon paths found in the game's Content" % len(paths) if not missing else "missing in Content: %s" % missing[:6])
    else:
        rep.skip("s16.assets", "SE content not given (--se-content): %d paths not checked" % len(paths))

    if decomp_se:
        check_decomp(rep, decomp_se)

def _files(mod):
    out = []
    for r, _d, fs in os.walk(mod):
        for f in fs:
            out.append(os.path.relpath(os.path.join(r, f), mod).replace(os.sep, "/"))
    return out

DECOMP_FILES = {
    "base": "VRage.Game/VRage.Game/MyObjectBuilder_DefinitionBase.cs",
    "physmodel": "VRage.Game/VRage.Game/MyObjectBuilder_PhysicalModelDefinition.cs",
    "physitem": "VRage.Game/VRage.Game/MyObjectBuilder_PhysicalItemDefinition.cs",
    "component": "VRage.Game/VRage.Game/MyObjectBuilder_ComponentDefinition.cs",
    "blueprint": "VRage.Game/VRage.Game/MyObjectBuilder_BlueprintDefinition.cs",
    "bpitem": "VRage.Game/VRage.Game/BlueprintItem.cs",
    "bpentry": "VRage.Game/VRage.Game/BlueprintClassEntry.cs",
    "cube": "VRage.Game/VRage.Game/MyObjectBuilder_CubeBlockDefinition.cs",
    "category": "VRage.Game/VRage.Game/MyObjectBuilder_GuiBlockCategoryDefinition.cs",
    "variant": "VRage.Game/VRage.Game.ObjectBuilders.Definitions/MyObjectBuilder_BlockVariantGroup.cs",
    "defid": "VRage.Game/VRage.ObjectBuilders/SerializableDefinitionId.cs",
    "defs": "VRage.Game/VRage.Game/MyObjectBuilder_Definitions.cs",
}

def check_decomp(rep, decomp_se):
    import check_planet as cp   # cs_xml_members (the S1 reader of serialised members)
    files = {k: os.path.join(decomp_se, *v.split("/")) for k, v in DECOMP_FILES.items()}
    missing = [k for k, v in files.items() if not os.path.isfile(v)]
    if missing:
        rep.skip("decomp.s16", "decompiled sources not found under %s (%s)" % (decomp_se, ", ".join(missing)))
        return
    m = {k: cp.cs_xml_members(v, "\t") for k, v in files.items() if k != "defs"}
    nested = cp.cs_xml_members(files["cube"], "\t\t")
    el = lambda *ks: {n for k in ks for n, t in m[k].items() if t == "element"}       # noqa: E731
    at = lambda *ks: {n for k in ks for n, t in m[k].items() if t == "attribute"}     # noqa: E731
    probs = []
    for name, used, have in (("component", COMPONENT, el("base", "physmodel", "physitem", "component")),
                             ("blueprint", BLUEPRINT, el("base", "blueprint")),
                             ("cube block", CUBE, el("base", "physmodel", "cube")),
                             ("category", CATEGORY, el("base", "category")),
                             ("variant group", VARIANT, el("base", "variant"))):
        extra = used - have - {"Enabled"}
        if extra:
            probs.append("%s: %s not in the builders" % (name, sorted(extra)))
    for name, used, have in (("blueprint item", ITEM_ATTRS, at("bpitem")), ("class entry", ENTRY_ATTRS, at("bpentry")),
                             ("definition id", DEFID_ATTRS, at("defid")),
                             ("cube nested", COMPONENT_REF_ATTRS | CRITICAL_ATTRS | MOUNT_ATTRS | BPM_ATTRS,
                              {n for n, t in nested.items() if t == "attribute"})):
        extra = used - have
        if extra:
            probs.append("%s: %s not in the builders" % (name, sorted(extra)))
    if not DEFID_ELEMS <= el("defid"):
        probs.append("definition id elements %s" % sorted(el("defid")))
    with open(files["defs"], encoding="utf-8", errors="replace") as f:
        src = f.read()
    for arr, item in ARRAY_ITEMS.items():
        if not re.search(r'XmlArrayItem\("%s"[^\]]*\)\]\s*(?:\[[^\]]*\]\s*)*public\s+[\w<>\[\]]+\s+%s\b' % (item, arr), src):
            probs.append("Definitions.%s is not an array of <%s>" % (arr, item))
    with open(files["cube"], encoding="utf-8", errors="replace") as f:
        cube = f.read()
    for arr, item in (("Components", "Component"), ("MountPoints", "MountPoint"), ("BuildProgressModels", "Model")):
        if not re.search(r'XmlArrayItem\("%s"[^\]]*\)\]\s*(?:\[[^\]]*\]\s*)*public\s+[\w<>\[\].]+\s+%s\b' % (item, arr), cube):
            probs.append("CubeBlockDefinition.%s is not an array of <%s>" % (arr, item))
    rep.add("decomp.s16", not probs, "embedded S16 names match the decompiled builders (component, blueprint, cube block, "
            "category, variant group, ids, root arrays)" if not probs else "; ".join(probs[:6]))
