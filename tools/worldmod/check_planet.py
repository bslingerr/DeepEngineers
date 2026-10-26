"""Checks the DeepEngineers.World mod folder (DE_4546B, site version 1) against the S1 spec. Prints PASS/FAIL.

What is checked (each line "CHECK <id> PASS|FAIL|SKIP <detail>", then "CHECK PLANET PASS|FAIL"):
 files.* the folder holds exactly the files SE needs for a local data-only mod: metadata.mod,
 Data/PlanetGeneratorDefinitions.sbc, Data/WaterConfig.xml, Data/PlanetDataFiles/DE_4546B/<face>.png and
 <face>_mat.png for the six faces; no modinfo.sbmi (SE writes it only when a mod is published), no code
 sbc.* the planet definition parses, has exactly one PlanetGeneratorDefinition DE_4546B, uses only element and
 attribute names that MyObjectBuilder_PlanetGeneratorDefinition (and its nested types) declare, and only
 the elements of this spec (XmlSerializer silently ignores a misspelt element, which would leave the SE
 default in place); values equal the site constants of tools/worldmap/site_v1.py
 derived.* radii, octree size, centre offset and atmosphere height that SE derives from those values
 water.* Data/WaterConfig.xml: one PlanetConfig for DE_4546B whose WaterSettings set every contract value
 explicitly (a missing element keeps the Water Mod default: WaveHeight 1, TideHeight 2, CrushDamage 0.5,
 CurrentSpeed 0.5, EnableFish true); Radius parses to float32 bits 0x3F86BCA2 and Rs = 60000.00390625
 png.* every PNG is valid (signature, chunk CRCs, IHDR, zlib stream, scanline filters); heightmaps 2048 x 2048,
 16-bit grayscale, all samples 0; material maps 2048 x 2048, 8-bit RGB or RGBA (not grayscale: SE reads
 _mat as Rgba32), R = a material value the.sbc maps, G = B = 0
 decomp.* the embedded field names below against the decompiled SE sources
 and the installed Water Mod sources (read only)
 stock.* (optional, --se-content) the stock EarthLike face formats and the voxel material names we use (read
 only; nothing is copied)

Schema facts embedded below are names only, VERIFIED(scratchpad, never in the
repository):, MyObjectBuilder_DefinitionBase.cs,
MyObjectBuilder_Base.cs, MyPlanetAtmosphere.cs, MyPlanetMaps.cs, MyPlanetMaterialDefinition.cs, VRageMath
SerializableRange.cs; and in the Water Mod 3.23 sources: WaterSettings.cs, BaseConfig.cs,
WaterConfigAPI.cs.

Usage (repository root):
 python -I tools/worldmod/check_planet.py [--mod <dir>] [--decomp-se <dir>|auto] [--water-mod <dir>|auto]
 [--se-content <dir>|auto] [--centre X,Y,Z] [--json]
--centre prints where an integer planet centre must be written in the save and what IMyVoxelMaps.SpawnPlanet needs.
Stdlib only.
"""
import argparse
import json
import math
import os
import re
import struct
import sys
import zlib
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "worldmap"))  # -I drops the script dir; our own folder
import site_v1  # noqa: E402 (the single source of the site constants)

DEFAULT_MOD_DIR = os.path.join(REPO, "src", "DeepEngineers.WorldMod")
DEFAULT_DECOMP_SE = os.environ.get("DEEPENG_DECOMP_SE", "")   # a decompiled-source folder, if one is used locally
DEFAULT_WATER_MOD = r"C:\Program Files (x86)\Steam\steamapps\workshop\content\244850\2200451495\Data\Scripts\Water\Water"
DEFAULT_SE_CONTENT = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content"

XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"
SUBTYPE = site_v1.PLANET["generatorSubtypeId"]
FACES = ("front", "back", "left", "right", "up", "down")   # MyCubemapHelpers.GetNameForFace(0..5)
SPAWN_SIZE = 2.0 * site_v1.PLANET["storageRadius_R"]       # spawn-menu "Select Size" value (diameter)
HEIGHTMAP_RESOLUTION = 2048
MATERIAL_RESOLUTION = 2048

# ---------------------------------------------------------------------------------------------------------------------
# Schema facts (XML names as SE's XmlSerializer reads them)
# ---------------------------------------------------------------------------------------------------------------------
# MyObjectBuilder_Base (SubtypeName) + MyObjectBuilder_DefinitionBase: element names; Icons -> <Icon>, DLCs -> <DLC>
SE_BASE_ELEMENTS = {"SubtypeName", "Id", "DisplayName", "Description", "Icon", "Public", "AvailableInSurvival",
                    "DescriptionArgs", "DLC"}
SE_BASE_ATTRIBUTES = {"Enabled"}
# MyObjectBuilder_PlanetGeneratorDefinition: every serialised public member, by XML element name
SE_PLANET_ELEMENTS = {
    "PlanetMaps", "HasAtmosphere", "CloudLayers", "HillParams", "GravityFalloffPower", "MaterialsMaxDepth",
    "MaterialsMinDepth", "HostileAtmosphereColorShift", "CustomMaterialTable", "DistortionTable",
    "DefaultSurfaceMaterial", "DefaultSubSurfaceMaterial", "SurfaceGravity", "Atmosphere", "AtmosphereSettings",
    "FolderName", "ComplexMaterials", "SoundRules", "MusicCategories", "OreMappings", "EnvironmentItems",
    "MaterialBlending", "SurfaceDetail", "AnimalSpawnInfo", "NightAnimalSpawnInfo", "SectorDensity", "InheritFrom",
    "Environment", "MapProvider", "MesherPostprocessing", "MinimumSurfaceLayerDepth", "StationBlockingMaterials",
    "DefaultSurfaceTemperature", "WeatherGenerators", "WeatherFrequencyMin", "WeatherFrequencyMax",
    "PersistentWeather", "GlobalWeather", "Difficulty", "RadiationGain", "RadiationStartHeight", "RadiationEndHeight",
    "AllowMeteors", "SolarRadiationProtectionFactor", "AllowContractSpawns", "AllowSurfaceEconomyStations",
    "AllowOrbitalEconomyStations",
}
SE_ATMOSPHERE_ELEMENTS = {"Breathable", "OxygenDensity", "Density", "LimitAltitude", "MaxWindSpeed"}  # MyPlanetAtmosphere
SE_PLANET_MAPS_ATTRIBUTES = {"Material", "Ores", "Biome", "Occlusion"}                               # MyPlanetMaps
SE_RANGE_ATTRIBUTES = {"Min", "Max"}                                                                # SerializableRange
SE_MATERIAL_ATTRIBUTES = {"Material", "Value", "MaxDepth"}                                          # MyPlanetMaterialDefinition
SE_MATERIAL_ELEMENTS = {"Layers"}
SE_ORE_ATTRIBUTES = {"Value", "Type", "Start", "Depth", "TargetColor", "ColorInfluence"}            # MyPlanetOreMapping
ORE_ITEM = "Ore"                         # [XmlArrayItem("Ore")] OreMappings
BIOME_NAMES_COUNT = 13                   # CONTRACT "biome.names" (ext s8a terrain 2): the _mat G channel holds index + 1 (0 = none)
CUSTOM_MATERIAL_ITEM = "Material"        # [XmlArrayItem("Material")] CustomMaterialTable
CLOUD_LAYER_ITEM = "CloudLayer"          # [XmlArrayItem("CloudLayer")] CloudLayers

# The elements this spec uses (ROADMAP S1 world mod; CONTRACT section 3). Anything else known to SE fails as
# "not in the spec", anything unknown fails as "unknown to SE".
SPEC_PLANET_ELEMENTS = {
    "Id", "DisplayName", "Description", "Public", "PlanetMaps", "CustomMaterialTable", "DefaultSurfaceMaterial",
    "DefaultSubSurfaceMaterial", "SurfaceGravity", "GravityFalloffPower", "HasAtmosphere", "Atmosphere", "CloudLayers",
    "HillParams",
    "AtmosphereSettings",
    "OreMappings",          # S8a Stage C: the designed planet's ore depth-rarity table (the _mat B channel)
}
# MyAtmosphereSettings (S7t): the members EarthLike's definition uses; vectors are X/Y/Z children
SE_ATMOSPHERE_SETTINGS_ELEMENTS = {
    "RayleighScattering", "MieScattering", "MieColorScattering", "RayleighHeight", "RayleighHeightSpace",
    "RayleighTransitionModifier", "MieHeight", "MieG", "Intensity", "SeaLevelModifier", "AtmosphereTopModifier",
    "FogIntensity", "Scale", "SunColor", "SunSpecularColor",
}
SE_CLOUD_LAYER_ELEMENTS = {
    "Model", "Textures", "RelativeAltitude", "RotationAxis", "AngularVelocity", "InitialRotation", "ScalingEnabled",
    "FadeOutRelativeAltitudeStart", "FadeOutRelativeAltitudeEnd", "ApplyFogRelativeDistance", "MaxPlanetHillRadius",
    "Color",   # S15 hook: MyCloudLayerSettings.Color, Vector4 X Y Z W
}
SE_VECTOR_ELEMENTS = {"X", "Y", "Z"}
SPEC_FORBIDDEN = {"SurfaceDetail", "EnvironmentItems", "WeatherGenerators"}  # ROADMAP S1: "no..." (OreMappings allowed since S8a Stage C)

# Water Mod 3.23
WM_ROOT = "WaterConfig"                                                                   # [XmlRoot("WaterConfig")]
WM_ROOT_ELEMENTS = {"BlockConfigs", "PlanetConfigs", "CharacterConfigs", "RespawnPodConfigs", "WaterTextures",
                    "MaterialConfigs", "FishConfigs", "WeatherConfigs"}
WM_PLANET_CONFIG_ITEM = "PlanetConfig"
WM_BASE_ATTRIBUTES = {"TypeId", "SubtypeId"}                                              # BaseConfig
WM_PLANET_CONFIG_ELEMENTS = {"WaterSettings", "ColorIntensity", "Specularity", "SpecularIntensity",
                             "AmbientColorIntensity", "DistantRadiusOffset", "DistantRadiusScaler",
                             "DistantSoftnessOffset", "DistantSoftnessScaler", "DistantSoftnessMultiplier"}
WM_WATER_SETTINGS_ELEMENTS = {"Radius", "WaveHeight", "WaveSpeed", "Buoyancy", "WaveScale", "EnableFish",
                              "EnableSeagulls", "Texture", "Transparent", "Lit", "FogColor", "CollectionRate",
                              "TideHeight", "TideSpeed", "EnableFoam", "CrushDamage", "PlayerDrag", "MaterialId",
                              "CurrentSpeed", "CurrentScale", "Volumetric"}
WM_PLANET_TYPE_ID = "MyObjectBuilder_PlanetGeneratorDefinition"   # as in the Water Mod's own PlanetConfigs
# Contract values (CONTRACT section 3) -> WaterSettings element, expected value, Water Mod default if omitted
WATER_SPEC = (
    ("Radius", "settingsRadius", 1.0),
    ("WaveHeight", "waveHeight", 1.0),
    ("TideHeight", "tideHeight", 2.0),
    ("Volumetric", "volumetric", False),
    ("CrushDamage", "crushDamage", 0.5),
    ("CurrentSpeed", "currentSpeed", 0.5),
    ("EnableFish", "enableFish", True),
)
RADIUS_BITS = 0x3F86BCA2

def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]

def f32_bits(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]

def parse_xml_bool(text):
    t = (text or "").strip()
    if t in ("true", "1"):
        return True
    if t in ("false", "0"):
        return False
    raise ValueError("not an xs:boolean: %r" % text)

def parse_xml_float(text):
    """xs:float as XmlConvert.ToSingle reads it (decimal text rounded to float32;.NET parses via double then rounds,
 which gives the same float for these values: they are nowhere near a float rounding midpoint)."""
    t = (text or "").strip()
    if not re.fullmatch(r"[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?|[+-]?INF|NaN", t):
        raise ValueError("not an xs:float: %r" % text)
    return f32(float(t.replace("INF", "inf")))

# ---------------------------------------------------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------------------------------------------------
class Report:
    def __init__(self):
        self.results = []   # (id, status, detail)

    def add(self, cid, ok, detail=""):
        self.results.append((cid, "PASS" if ok else "FAIL", detail))
        return ok

    def skip(self, cid, detail):
        self.results.append((cid, "SKIP", detail))

    def info(self, cid, detail):
        self.results.append((cid, "INFO", detail))

    @property
    def failed(self):
        return [r for r in self.results if r[1] == "FAIL"]

    def ids(self, status="FAIL"):
        return {r[0] for r in self.results if r[1] == status}

# ---------------------------------------------------------------------------------------------------------------------
# PNG (independent reader: chunks, CRCs, zlib, all five scanline filters)
# ---------------------------------------------------------------------------------------------------------------------
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}

class PngError(Exception):
    pass

def png_read(data, header_only=False):
    """Returns dict(width, height, bit_depth, color_type, interlace, chunks, samples) with samples = the unfiltered
 scanline bytes (no filter bytes), PNG byte order."""
    if data[:8] != PNG_SIGNATURE:
        raise PngError("bad signature")
    pos, chunks, idat, ihdr = 8, [], [], None
    while True:
        if pos + 12 > len(data):
            raise PngError("truncated chunk at %d" % pos)
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if len(body) != length:
            raise PngError("truncated %r" % tag)
        (crc,) = struct.unpack(">I", data[pos + 8 + length:pos + 12 + length])
        if crc != zlib.crc32(tag + body) & 0xFFFFFFFF:
            raise PngError("CRC mismatch in %r" % tag)
        chunks.append(tag.decode("latin-1"))
        if tag == b"IHDR":
            if ihdr is not None or length != 13:
                raise PngError("bad IHDR")
            ihdr = struct.unpack(">IIBBBBB", body)
        elif tag == b"IDAT":
            idat.append(body)
        elif tag == b"IEND":
            break
        pos += 12 + length
        if header_only and ihdr is not None:
            break
    if ihdr is None or chunks[0] != "IHDR":
        raise PngError("IHDR is not the first chunk")
    w, h, bd, ct, cm, fm, il = ihdr
    info = {"width": w, "height": h, "bit_depth": bd, "color_type": ct, "compression": cm, "filter": fm,
            "interlace": il, "chunks": chunks}
    if header_only:
        return info
    if ct not in CHANNELS or cm != 0 or fm != 0 or il not in (0, 1):
        raise PngError("unsupported IHDR %r" % (ihdr,))
    if il != 0:
        raise PngError("interlaced PNG (this checker reads non-interlaced files only)")
    if not idat or chunks[-1] != "IEND":
        raise PngError("missing IDAT or IEND")
    try:
        raw = zlib.decompress(b"".join(idat))
    except zlib.error as e:
        raise PngError("zlib: %s" % e)
    bits_pp = CHANNELS[ct] * bd
    row_len = (w * bits_pp + 7) // 8
    bpp = max(1, bits_pp // 8)
    if len(raw) != h * (row_len + 1):
        raise PngError("decompressed size %d != %d" % (len(raw), h * (row_len + 1)))
    out = bytearray(h * row_len)
    prev = bytearray(row_len)
    for y in range(h):
        ft = raw[y * (row_len + 1)]
        line = bytearray(raw[y * (row_len + 1) + 1:(y + 1) * (row_len + 1)])
        if ft == 0:
            pass
        elif ft == 1:
            for i in range(bpp, row_len):
                line[i] = (line[i] + line[i - bpp]) & 0xFF
        elif ft == 2:
            for i in range(row_len):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:
            for i in range(row_len):
                left = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif ft == 4:
            for i in range(row_len):
                a = line[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 0xFF
        else:
            raise PngError("bad filter type %d in row %d" % (ft, y))
        out[y * row_len:(y + 1) * row_len] = line
        prev = line
    info["samples"] = bytes(out)
    return info

# ---------------------------------------------------------------------------------------------------------------------
# Checks
# ---------------------------------------------------------------------------------------------------------------------
def expected_rel_files():
    base = ["metadata.mod", "Data/PlanetGeneratorDefinitions.sbc", "Data/WaterConfig.xml",
            "Data/CubeBlocks_S16.sbc",   # S16 hook: the pressure hull upgrade blocks (tools/worldmod/s16_blocks.py)
            "Data/PhysicalItems_DE_Food.sbc", "Data/Blueprints_DE_Food.sbc",   # S17 hook: the 4546B fish as SE food and their cooking (tools/worldmod/s17_food.py)
            "Data/TransparentMaterials.sbc", "Textures/DE_4546B_Water.dds", "Textures/DE_4546B_WaterFar.dds",   # S15 hook: the sea's own materials, near and far (tools/worldmod/s15_water.py)
            "Data/PlanetDataFiles/%s/de-planet-rules.txt" % SUBTYPE,   # S8a planet v2 R8: the layout's runtime rules
            "Data/RespawnShips_DE.sbc", "Data/Prefabs/DE_Lifepod.sbc"]
    for face in FACES:
        base.append("Data/PlanetDataFiles/%s/%s.png" % (SUBTYPE, face))
        base.append("Data/PlanetDataFiles/%s/%s_mat.png" % (SUBTYPE, face))
    return base

def check_files(rep, mod):
    if not os.path.isdir(mod):
        rep.add("files.present", False, "mod folder not found: %s" % mod)
        return False
    have = set()
    for root, _dirs, files in os.walk(mod):
        for f in files:
            have.add(os.path.relpath(os.path.join(root, f), mod).replace(os.sep, "/"))
    want = set(expected_rel_files())
    missing = sorted(want - have)
    extra = sorted(have - want)
    rep.add("files.present", not missing, "all %d files" % len(want) if not missing else "missing: %s" % ", ".join(missing))
    forbidden = [f for f in extra if f.lower() == "modinfo.sbmi" or f.lower().endswith((".cs", ".dll"))]
    rep.add("files.noPublishOrCode", not forbidden,
            "no modinfo.sbmi, .cs or .dll" if not forbidden else "present: %s (modinfo.sbmi is written by SE only when a "
            "mod is published; this is a data-only local mod)" % ", ".join(forbidden))
    rep.add("files.exact", not extra, "no extra files" if not extra else "unexpected: %s" % ", ".join(extra))
    return not missing

def check_metadata(rep, mod):
    try:
        root = ET.parse(os.path.join(mod, "metadata.mod")).getroot()
        ver = root.findtext("ModVersion")
        ok = root.tag == "ModMetadata" and ver is not None and re.fullmatch(r"\d+(\.\d+){1,3}", ver.strip()) is not None
        rep.add("metadata.mod", ok, "ModMetadata ModVersion %s" % ver)
    except (ET.ParseError, OSError) as e:
        rep.add("metadata.mod", False, str(e))

def _child_names(el):
    return [c.tag for c in el if isinstance(c.tag, str)]

def _known_unknown(el, known_elems, known_attrs, spec_elems=None, path=""):
    """Unknown element/attribute -> FAIL; known but outside the spec -> FAIL."""
    unknown, unspec = [], []
    for name in _child_names(el):
        if name not in known_elems:
            unknown.append(path + name)
        elif spec_elems is not None and name not in spec_elems:
            unspec.append(path + name)
    for a in el.attrib:
        if a == XSI_TYPE or a.startswith("{http://www.w3.org/2000/xmlns/}"):
            continue
        if a not in known_attrs:
            unknown.append(path + "@" + a)
    return unknown, unspec

def check_sbc(rep, mod):
    """Returns the parsed definition values (dict) or None."""
    path = os.path.join(mod, "Data", "PlanetGeneratorDefinitions.sbc")
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as e:
        rep.add("sbc.parse", False, str(e))
        return None
    rep.add("sbc.parse", root.tag == "Definitions", "root <%s>" % root.tag)
    defs = [d for d in root if d.tag == "Definition"]
    others = [c.tag for c in root if isinstance(c.tag, str) and c.tag != "Definition"]
    planets = [d for d in defs if d.get(XSI_TYPE) == "PlanetGeneratorDefinition"]
    ok = len(defs) == 1 and len(planets) == 1 and not others
    if not ok:
        rep.add("sbc.definition", False, "expected exactly one <Definition xsi:type=\"PlanetGeneratorDefinition\"> and "
                "nothing else; found %d Definition (%d planet), others %r" % (len(defs), len(planets), others))
        return None
    d = planets[0]
    tid, sid = d.findtext("Id/TypeId"), d.findtext("Id/SubtypeId")
    rep.add("sbc.definition", tid == "PlanetGeneratorDefinition" and sid == SUBTYPE,
            "Id %s/%s (expected PlanetGeneratorDefinition/%s)" % (tid, sid, SUBTYPE))

    # ---- names (typos are silent in SE) ----
    unknown, unspec = _known_unknown(d, SE_BASE_ELEMENTS | SE_PLANET_ELEMENTS,
                                     SE_BASE_ATTRIBUTES, SPEC_PLANET_ELEMENTS)
    dup = sorted({n for n in _child_names(d) if _child_names(d).count(n) > 1})
    nested = [
        ("Id", {"TypeId", "SubtypeId"}, set()),
        ("PlanetMaps", set(), SE_PLANET_MAPS_ATTRIBUTES),
        ("HillParams", set(), SE_RANGE_ATTRIBUTES),
        ("Atmosphere", SE_ATMOSPHERE_ELEMENTS, set()),
        ("DefaultSurfaceMaterial", SE_MATERIAL_ELEMENTS, SE_MATERIAL_ATTRIBUTES),
        ("DefaultSubSurfaceMaterial", SE_MATERIAL_ELEMENTS, SE_MATERIAL_ATTRIBUTES),
        ("CustomMaterialTable", {CUSTOM_MATERIAL_ITEM}, set()),
        ("OreMappings", {ORE_ITEM}, set()),
        ("CloudLayers", {CLOUD_LAYER_ITEM}, set()),
        ("AtmosphereSettings", SE_ATMOSPHERE_SETTINGS_ELEMENTS, set()),
    ]
    for name, elems, attrs in nested:
        for el in d.findall(name):
            u, _ = _known_unknown(el, elems, attrs, None, name + "/")
            unknown += u
    for el in d.findall("CustomMaterialTable/" + CUSTOM_MATERIAL_ITEM):
        u, _ = _known_unknown(el, SE_MATERIAL_ELEMENTS, SE_MATERIAL_ATTRIBUTES, None,
                              "CustomMaterialTable/Material/")
        unknown += u
    for el in d.findall("CloudLayers/" + CLOUD_LAYER_ITEM):
        u, _ = _known_unknown(el, SE_CLOUD_LAYER_ELEMENTS, set(), None, "CloudLayers/CloudLayer/")
        unknown += u
    for el in d.findall("OreMappings/" + ORE_ITEM):
        u, _ = _known_unknown(el, set(), SE_ORE_ATTRIBUTES, None, "OreMappings/Ore/")
        unknown += u
    for vec in ("AtmosphereSettings/RayleighScattering", "AtmosphereSettings/MieColorScattering", "AtmosphereSettings/SunColor",
                "AtmosphereSettings/SunSpecularColor", "CloudLayers/CloudLayer/RotationAxis"):
        for el in d.findall(vec):
            u, _ = _known_unknown(el, SE_VECTOR_ELEMENTS, set(), None, vec + "/")
            unknown += u
    for el in d.findall("CloudLayers/CloudLayer/Color"):   # S15 hook: a Vector4
        u, _ = _known_unknown(el, SE_VECTOR_ELEMENTS | {"W"}, set(), None, "CloudLayers/CloudLayer/Color/")
        unknown += u
    for leaf in ("DisplayName", "Description", "Public", "SurfaceGravity", "GravityFalloffPower", "HasAtmosphere"):
        for el in d.findall(leaf):
            if len(el) or el.attrib:
                unknown.append(leaf + " (must be a plain value)")
    for el in d.findall("Atmosphere/*"):
        if len(el) or el.attrib:
            unknown.append("Atmosphere/%s (must be a plain value)" % el.tag)
    rep.add("sbc.schema.known", not unknown and not dup,
            "every name is a MyObjectBuilder_PlanetGeneratorDefinition member" if not unknown and not dup else
            "unknown to SE (a typo is ignored by SE): %s%s" % (", ".join(unknown), ("; duplicated: %s" % dup) if dup else ""))
    forbidden = [n for n in _child_names(d) if n in SPEC_FORBIDDEN]
    rep.add("sbc.schema.forbidden", not forbidden,
            "no SurfaceDetail, EnvironmentItems, WeatherGenerators" if not forbidden else
            "forbidden by ROADMAP S1: %s" % ", ".join(forbidden))
    unspec = [n for n in unspec if n not in forbidden]
    rep.add("sbc.schema.spec", not unspec, "only the S1 elements" if not unspec else
            "not in the S1 spec (would change SE defaults; update the spec and this checker first): %s" % ", ".join(unspec))

    vals = {}
    errors = []

    def num(path_, attr=None):
        el = d.find(path_)
        if el is None:
            errors.append("%s missing" % path_)
            return None
        try:
            return parse_xml_float(el.get(attr) if attr else el.text)
        except ValueError as e:
            errors.append("%s: %s" % (path_, e))
            return None

    def boolean(path_, attr=None):
        el = d.find(path_)
        if el is None:
            errors.append("%s missing" % path_)
            return None
        try:
            return parse_xml_bool(el.get(attr) if attr else el.text)
        except ValueError as e:
            errors.append("%s: %s" % (path_, e))
            return None

    P = site_v1.PLANET
    A = P["atmosphere"]
    vals["hillMin"], vals["hillMax"] = num("HillParams", "Min"), num("HillParams", "Max")
    rep.add("sbc.hillParams", vals["hillMin"] == f32(P["hillMin"]) and vals["hillMax"] == f32(P["hillMax"]),
            "Min %r Max %r (expected %r / %r)" % (vals["hillMin"], vals["hillMax"], P["hillMin"], P["hillMax"]))
    vals["surfaceGravity"] = num("SurfaceGravity")
    rep.add("sbc.surfaceGravity", vals["surfaceGravity"] == f32(P["surfaceGravity"]),
            "%r (expected %r)" % (vals["surfaceGravity"], P["surfaceGravity"]))
    vals["gravityFalloffPower"] = num("GravityFalloffPower")
    rep.add("sbc.gravityFalloffPower", vals["gravityFalloffPower"] == f32(P["gravityFalloffPower"]),
            "%r (expected %r; written explicitly although 7 is the SE default)" % (vals["gravityFalloffPower"],
                                                                                  P["gravityFalloffPower"]))
    vals["hasAtmosphere"] = boolean("HasAtmosphere")
    atmo = {"Breathable": boolean("Atmosphere/Breathable"), "OxygenDensity": num("Atmosphere/OxygenDensity"),
            "Density": num("Atmosphere/Density"), "LimitAltitude": num("Atmosphere/LimitAltitude"),
            "MaxWindSpeed": num("Atmosphere/MaxWindSpeed")}
    want_atmo = {"Breathable": A["breathable"], "OxygenDensity": f32(A["oxygenDensity"]), "Density": f32(A["density"]),
                 "LimitAltitude": f32(A["limitAltitude"]), "MaxWindSpeed": f32(A["maxWindSpeed"])}
    rep.add("sbc.atmosphere", vals["hasAtmosphere"] is True and atmo == want_atmo,
            "HasAtmosphere %r, %r (expected True, %r)" % (vals["hasAtmosphere"], atmo, want_atmo))
    vals["atmosphere"] = atmo
    pub = boolean("Public")
    rep.add("sbc.public", pub is True, "Public %r (the spawn menu lists only public generators)" % pub)
    clouds = d.find("CloudLayers")
    # S1 kept an explicit empty <CloudLayers/> (MyDefinitionManager.ProcessPlanetGeneratorDefition iterates the CloudLayers
 # of every mod planet without a null check); S7t T5 fills it with EarthLike's layers: present, and
 # every item a CloudLayer with a Model and Textures
    clouds_ok = clouds is not None and all(c.tag == CLOUD_LAYER_ITEM and c.find("Model") is not None and
                                           c.find("Textures") is not None for c in clouds)
    rep.add("sbc.cloudLayers", clouds_ok,
            ("explicit <CloudLayers> with %d EarthLike layer(s) (S7t T5)" % len(clouds)) if clouds_ok else
            "<CloudLayers> missing, or an item that is not a CloudLayer with Model and Textures")
    folder = d.findtext("FolderName")
    rep.add("sbc.folder", folder is None, "FolderName absent: data folder = SubtypeId %s" % SUBTYPE if folder is None
            else "FolderName %r present (the PNG folder must then be renamed; keep it absent)" % folder)

    # materials: PlanetMaps Material (+ Ores with an OreMappings table, + Biome: the designed planet of S8a Stage C); default
 # surface/subsurface; the custom material table maps the _mat R values, the ore table the B values
    ores = {}
    for o in d.findall("OreMappings/" + ORE_ITEM):
        try:
            v = int(o.get("Value"))
            st, dp = parse_xml_float(o.get("Start")), parse_xml_float(o.get("Depth"))
        except (TypeError, ValueError):
            v = None
        if v is None or not 1 <= v <= 255 or not o.get("Type") or v in ores or st < 0 or dp <= 0:
            errors.append("OreMappings entry %r invalid or duplicated" % (o.attrib,))
        else:
            ores[v] = (o.get("Type"), st, dp)
    vals["oreValues"] = ores
    pm = d.find("PlanetMaps")
    maps = {k: (parse_xml_bool(pm.get(k)) if pm is not None and pm.get(k) is not None else False)
            for k in ("Material", "Ores", "Biome", "Occlusion")} if pm is not None else None
    vals["planetMaps"] = maps or {}
    maps_ok = maps is not None and maps["Material"] is True and maps["Occlusion"] is False and maps["Ores"] == bool(ores)
    rep.add("sbc.planetMaps", maps_ok,
            "PlanetMaps %r with %d OreMappings rows (Material on; Ores on exactly when the OreMappings table has rows; Biome optional: "
            "the designed planet's G channel; no occlusion map)" % (maps, len(ores)))
    ds, dss = d.find("DefaultSurfaceMaterial"), d.find("DefaultSubSurfaceMaterial")
    ok = ds is not None and bool(ds.get("Material")) and dss is not None and bool(dss.get("Material"))
    rep.add("sbc.defaultMaterials", ok, "surface %s, subsurface %s (MyPlanetMaterialProvider dereferences "
            "DefaultSurfaceMaterial unconditionally)" % (ds.get("Material") if ds is not None else None,
                                                          dss.get("Material") if dss is not None else None))
    table = {}
    for m in d.findall("CustomMaterialTable/" + CUSTOM_MATERIAL_ITEM):
        try:
            v = int(m.get("Value"))
        except (TypeError, ValueError):
            v = None
        if v is None or not 0 <= v <= 255 or not m.get("Material") or v in table:
            errors.append("CustomMaterialTable entry %r invalid or duplicated" % (m.attrib,))
        else:
            table[v] = m.get("Material")
    rep.add("sbc.customMaterials", len(table) >= 1, "values %r" % table)
    vals["materialValues"] = table
    vals["materialNames"] = sorted({*table.values(), ds.get("Material") if ds is not None else "",
                                    dss.get("Material") if dss is not None else ""} - {""})
    rep.add("sbc.values", not errors, "all values parse" if not errors else "; ".join(errors))
    return vals

def check_derived(rep, vals):
    """What SE derives (MyPlanetStorageProvider/MyPlanetShapeProvider/MyPlanet/MyVoxelBase), float32 like SE."""
    R = f32(SPAWN_SIZE / 2.0)
    if vals is None or vals.get("hillMin") is None or vals.get("hillMax") is None:
        rep.add("derived.radii", False, "no hill params")
        return
    min_hill = f32(vals["hillMin"] * R)
    max_hill = f32(vals["hillMax"] * R)
    inner, outer = f32(R + min_hill), f32(R + max_hill)
    rep.add("derived.radii", (R, inner, outer) == (site_v1.R, site_v1.INNER, site_v1.OUTER),
            "R %.1f MinimumRadius %.1f MaximumRadius %.1f (site_v1: %.1f / %.1f / %.1f)" %
            (R, inner, outer, site_v1.R, site_v1.INNER, site_v1.OUTER))
    octree = 32
    while octree < f32(2.0 * outer):                      # MyVoxelCoordSystems.FindBestOctreeSize(2 * outer)
        octree *= 2
    rep.add("derived.storage", octree == 262144, "storage size %d (centre = saved min corner + %.1f per axis, the "
            "+0.5 being MyPerGameSettings.OffsetVoxelMapByHalfVoxel)" % (octree, octree / 2 + 0.5))
    atmo_alt = max_hill * (vals["atmosphere"]["LimitAltitude"] or 0.0)
    rep.add("derived.atmosphere", abs(atmo_alt - 14400.0) < 1e-3,
            "AtmosphereAltitude %.1f m above R (air density 1.0 at r = R)" % atmo_alt)
    play = (site_v1.DEPTH["abyssLimitSnY"], 160.0)        # [abyss limit, highest island top] in SN y
    lo, hi = site_v1.RS_EXPECTED + play[0], site_v1.RS_EXPECTED + play[1]
    rep.add("derived.gravityBand", inner <= lo and hi <= outer,
            "1 g on [%.0f, %.0f]; play volume [%.0f, %.0f]" % (inner, outer, lo, hi))

def check_water(rep, mod, vals):
    path = os.path.join(mod, "Data", "WaterConfig.xml")
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as e:
        rep.add("water.parse", False, str(e))
        return
    rep.add("water.parse", root.tag == WM_ROOT, "root <%s>" % root.tag)
    unknown, unspec = _known_unknown(root, WM_ROOT_ELEMENTS, set(), {"PlanetConfigs", "BlockConfigs", "WaterTextures", "MaterialConfigs"})   # S14 hook: BlockConfigs = the weapons under water (s14_weapons.py); S15 hook: WaterTextures (s15_water.py)
    pcs = [p for p in root.findall("PlanetConfigs/*")]
    bad_items = [p.tag for p in pcs if p.tag != WM_PLANET_CONFIG_ITEM]
    ours = [p for p in pcs if p.tag == WM_PLANET_CONFIG_ITEM and p.get("SubtypeId") == SUBTYPE]
    for p in pcs:
        u, s = _known_unknown(p, WM_PLANET_CONFIG_ELEMENTS, WM_BASE_ATTRIBUTES,
                              {"WaterSettings", "DistantSoftnessOffset", "SpecularIntensity"}, "PlanetConfig/")   # S15 hook: the surface's soft fade capped, the specular off (the sea seam; s15_water.py)
        unknown += u
        unspec += s
        for ws in p.findall("WaterSettings"):
            u, s = _known_unknown(ws, WM_WATER_SETTINGS_ELEMENTS, set(),
                                  {e for e, _k, _d in WATER_SPEC} | {"Texture", "Transparent", "MaterialId"}, "WaterSettings/")   # S15 hook: Texture = the sea's material, Transparent false = the opaque sea, MaterialId = DE_4546B_Sea (s15_water.py)
            unknown += u
            unspec += s
            names = _child_names(ws)
            unknown += ["WaterSettings/%s (duplicated)" % n for n in sorted(set(names)) if names.count(n) > 1]
    rep.add("water.schema.known", not unknown and not bad_items,
            "every name is a Water Mod config member" if not unknown and not bad_items else
            "unknown to the Water Mod (ignored by its XmlSerializer): %s" % ", ".join(unknown + bad_items))
    rep.add("water.schema.spec", not unspec, "only the contract values" if not unspec else
            "not in the S1 spec: %s" % ", ".join(unspec))
    ok = len(pcs) == 1 and len(ours) == 1 and ours[0].get("TypeId") == WM_PLANET_TYPE_ID
    rep.add("water.planetConfig", ok, "one PlanetConfig TypeId=%s SubtypeId=%s" % (WM_PLANET_TYPE_ID, SUBTYPE) if ok else
            "expected exactly one <PlanetConfig TypeId=\"%s\" SubtypeId=\"%s\">; found %r" %
            (WM_PLANET_TYPE_ID, SUBTYPE, [p.attrib for p in pcs]))
    if not ours:
        return
    ws = ours[0].find("WaterSettings")
    if ws is None:
        rep.add("water.settings", False, "no <WaterSettings>: the Water Mod adds no sea for a PlanetConfig without it")
        return
    W = site_v1.WATER
    problems, shown = [], []
    for elem, key, default in WATER_SPEC:
        el = ws.find(elem)
        want = W[key]
        if el is None:
            problems.append("%s missing (the Water Mod default %r would apply)" % (elem, default))
            continue
        try:
            got = parse_xml_bool(el.text) if isinstance(want, bool) else parse_xml_float(el.text)
        except ValueError as e:
            problems.append("%s: %s" % (elem, e))
            continue
        exp = want if isinstance(want, bool) else f32(want)
        shown.append("%s=%r" % (elem, got))
        if got != exp:
            problems.append("%s=%r expected %r" % (elem, got, exp))
    rep.add("water.settings", not problems, ", ".join(shown) if not problems else "; ".join(problems))
    el = ws.find("Radius")
    if el is not None:
        try:
            r = parse_xml_float(el.text)
            bits = f32_bits(r)
            rs = f32(site_v1.INNER * r)
            mid_lo = (struct.unpack("<f", struct.pack("<I", bits - 1))[0] + r) / 2
            mid_hi = (struct.unpack("<f", struct.pack("<I", bits + 1))[0] + r) / 2
            dec = float(el.text.strip())
            margin = min(dec - mid_lo, mid_hi - dec) / (mid_hi - mid_lo)
            rep.add("water.radius.bits", bits == RADIUS_BITS == f32_bits(site_v1.WATER["settingsRadius"]),
                    "text %r -> float32 0x%08X = %.20g (expected 0x%08X); decimal sits %.0f%% of an ulp away from a "
                    "rounding midpoint" % (el.text.strip(), bits, r, RADIUS_BITS, 100 * margin))
            rep.add("water.radius.rs", rs == site_v1.RS_EXPECTED == 60000.00390625,
                    "Rs = float32(MinimumRadius %.1f x %.9g) = %.8f (site_v1 RS_EXPECTED %.8f)" %
                    (site_v1.INNER, r, rs, site_v1.RS_EXPECTED))
        except ValueError as e:
            rep.add("water.radius.bits", False, str(e))

SCULPT_RS = 60000.00390625            # the sea sphere (site v1)
SCULPT_FLAT_RHO_M = 2450.0            # CONTRACT "sculpt.site": pixels within this site-v1 rhoInf are 0
SCULPT_MAX_SAMPLE = 19725             # encode(+70): the designed planet's island tops (Stage C, "sculpt.site"); elsewhere the sculpt tool's --verify holds -2 m
SCULPT_MIN_NONZERO = 1000             # a sculpted face has a real seabed (>= 1,000 non-zero samples); one stray sample is damage (S1's seeded failure)
SCULPT_CRATER_PX = 64                 # the crater's pixels lie within this many px of face 1's centre (2,450 m / 60,000 m / 2 * 2047 = 41.8)
SCULPT_GLOBE_SHARE = 0.5              # Stage E (the whole planet): every face's seabed is sculpted (> 50 % non-zero samples)

def sculpt_rule(face, samples, w, h):
    """(ok, text) of a sculpted 16-bit heightmap face: the crater floor (face 1 "back" holds the site anchor at texcoord
 (0.5, 0.5); MyCubemapHelpers: face 1 is +Z, tx = x/|z|, ty = -y/|z|, texcoord = (t + 1) / 2, column i = tx, row j = ty)
 exactly 0 within rhoInf 2,450 m, and no sample above SN y -10 anywhere."""
    import math
    import struct
    worst = 0
    nonzero = 0
    row_fmt = ">%dH" % w
    for j in range(h):
        vals = struct.unpack_from(row_fmt, samples, j * w * 2)
        m = max(vals)
        if m > worst:
            worst = m
        nonzero += w - vals.count(0)
    share = nonzero / float(w * h)
    # a sculpted face encodes a seabed on many pixels (the designed planet: the 12 km ring of face 1, about 5 %); a face that is
 # neither flat nor has a real seabed is damaged (the S1 seeded failure "one non-zero sample" stays red)
    if nonzero < SCULPT_MIN_NONZERO:
        return False, "sculpted? only %d non-zero samples (a sculpted face has >= %d, a flat one 0)" % (nonzero, SCULPT_MIN_NONZERO)
    bad_crater = 0
    if face == "back":
        c = (w - 1) / 2.0
        for j in range(max(0, int(c) - SCULPT_CRATER_PX), min(h, int(c) + SCULPT_CRATER_PX + 1)):
            for i in range(max(0, int(c) - SCULPT_CRATER_PX), min(w, int(c) + SCULPT_CRATER_PX + 1)):
                tx, ty = 2.0 * i / (w - 1) - 1.0, 2.0 * j / (h - 1) - 1.0
                x, y, z = tx, -ty, 1.0
                n = math.sqrt(x * x + y * y + z * z)
                le, ln, lu = -x / n, -y / n, z / n          # site v1: E0 = -X, N0 = -Y, U0 = +Z
                hh = math.hypot(le, ln)
                rho = SCULPT_RS * math.atan2(hh, lu)
                sx, sz = (rho * le / hh, rho * ln / hh) if hh > 0 else (0.0, 0.0)
                if max(abs(sx), abs(sz)) <= SCULPT_FLAT_RHO_M and struct.unpack_from(">H", samples, (j * w + i) * 2)[0] != 0:
                    bad_crater += 1
    ok = worst <= SCULPT_MAX_SAMPLE and bad_crater == 0
    return ok, "sculpted: %.1f %% non-zero, max sample %d (limit %d = SN y +70)%s" % (100.0 * share, worst, SCULPT_MAX_SAMPLE,
                                                                   ", crater floor 0 within rhoInf %.0f m" % SCULPT_FLAT_RHO_M if face != "back" or bad_crater == 0
                                                                   else ", %d crater pixels NON-ZERO within rhoInf %.0f m" % (bad_crater, SCULPT_FLAT_RHO_M))

def _nonzero_share(samples, w, h):
    """The share of non-zero 16-bit samples (big-endian rows) of a heightmap."""
    import struct
    fmt = ">%dH" % w
    nz_ = 0
    for j in range(h):
        nz_ += w - struct.unpack_from(fmt, samples, j * w * 2).count(0)
    return nz_ / float(w * h)

def check_pngs(rep, mod, vals):
    pdir = os.path.join(mod, "Data", "PlanetDataFiles", SUBTYPE)
    sizes = set()
    shares = {}
    mapped = set((vals or {}).get("materialValues", {}).keys())
    ore_vals = set((vals or {}).get("oreValues", {}).keys())
    pmaps = (vals or {}).get("planetMaps", {})
    for face in FACES:
        for kind in ("height", "mat"):
            name = face + (".png" if kind == "height" else "_mat.png")
            cid = "png.%s" % name
            path = os.path.join(pdir, name)
            try:
                with open(path, "rb") as f:
                    data = f.read()
                info = png_read(data)
            except (OSError, PngError) as e:
                rep.add(cid, False, str(e))
                continue
            w, h, bd, ct = info["width"], info["height"], info["bit_depth"], info["color_type"]
            extra_chunks = [c for c in info["chunks"] if c not in ("IHDR", "IDAT", "IEND")]
            s = info["samples"]
            if kind == "height":
                sizes.add((w, h))
                ok_fmt = (w, h, bd, ct) == (HEIGHTMAP_RESOLUTION, HEIGHTMAP_RESOLUTION, 16, 0)
                flat = s == bytes(len(s))
                if ok_fmt:
                    shares[face] = 0.0 if flat else _nonzero_share(s, w, h)
                # S8a (the sculpted planet, CONTRACT "sculpt.site"): a sculpted face passes when every pixel whose site-v1
 # rhoInf <= 2,450 m is 0 (the provider tiles' floor stays exactly -3,000 m) and no sample lies above SN y +70
 # (encode(+70) = 19725, the island); a flat face (all 0, the pre-S8a planet and the design's other faces) passes as before
                ok_val, how = (True, "all samples 0") if flat else sculpt_rule(face, s, w, h) if ok_fmt else (False, "NON-ZERO samples")
                rep.add(cid, ok_fmt and ok_val,
                        "%dx%d %d-bit colour type %d%s, %s" % (w, h, bd, ct, (" chunks " + ",".join(extra_chunks)) if extra_chunks else "", how)
                        + ("" if ok_fmt else " (expected %dx%d 16-bit grayscale, colour type 0)" % (HEIGHTMAP_RESOLUTION, HEIGHTMAP_RESOLUTION)))
            else:
                ok_fmt = w == h == MATERIAL_RESOLUTION and bd == 8 and ct in (2, 6)
                ch = CHANNELS.get(ct, 1)
                detail = "%dx%d %d-bit colour type %d" % (w, h, bd, ct)
                ok_val = False
                if ok_fmt:
                    # S8a (the sculpted planet, sculpt_de4546b.py): R per pixel = a CustomMaterialTable value; G = 0, or with
 # PlanetMaps Biome a biome.names index + 1 (1..13); B = 0, or with PlanetMaps Ores an OreMappings Value; A = 255;
 # a uniform map (the pre-S8a planet) is the one-value case of the same rule
                    n = w * h
                    rvals = sorted(set(s[0::ch]))
                    gvals = set(s[1::ch])
                    bvals = set(s[2::ch])
                    g_ok = gvals <= {0} or (pmaps.get("Biome") is True and gvals <= set(range(0, BIOME_NAMES_COUNT + 1)))
                    b_ok = bvals <= {0} or (pmaps.get("Ores") is True and bvals <= ore_vals | {0})
                    a_ok = ch == 3 or s[3::ch] == b"\xff" * n
                    unmapped = [r for r in rvals if r not in mapped]
                    ok_val = g_ok and b_ok and a_ok and not unmapped
                    names = (vals or {}).get("materialValues", {})
                    detail += ", %s R in %s, G %s %s, B %s %s%s; R mapped by CustomMaterialTable: %s" % (
                        "uniform" if len(rvals) == 1 else "sculpted", rvals, sorted(gvals)[:14], "ok" if g_ok else "NO (biome map off or out of range)",
                        sorted(bvals)[:12], "ok" if b_ok else "NO (ore map off or a value outside OreMappings)",
                        "" if ch == 3 else ", A=255 %s" % ("ok" if a_ok else "NO"),
                        ", ".join("%d=%s" % (r, names.get(r)) for r in rvals) if not unmapped else "NO (unmapped %s)" % unmapped)
                else:
                    detail += " (expected %dx%d 8-bit RGB or RGBA: SE reads _mat as Rgba32; a grayscale _mat would be "\
                              "loaded as one channel and fail)" % (MATERIAL_RESOLUTION, MATERIAL_RESOLUTION)
                rep.add(cid, ok_fmt and ok_val, detail)
    rep.add("png.facesSameSize", len(sizes) == 1, "heightmap sizes %r" % sorted(sizes))

    # design and every face must carry its seabed; the ring-only design (back alone sculpted) is the pre-Stage E planet
    if len(shares) == len(FACES):
        others = [f for f in FACES if f != "back" and shares[f] > 0.0]
        if others:
            low = {f: round(v, 3) for f, v in shares.items() if v < SCULPT_GLOBE_SHARE}
            rep.add("png.globe", not low, "the whole planet: non-zero share per face %s%s" % (
                {f: round(v, 3) for f, v in shares.items()}, "" if not low else "; NOT sculpted: %s (> %.0f %% expected)" % (low, 100 * SCULPT_GLOBE_SHARE)))
        else:
            rep.add("png.globe", True, "the ring-only design (face back sculpted %.1f %%, the others flat: pre-Stage E)" % (100 * shares.get("back", 0.0)))

# ---------------------------------------------------------------------------------------------------------------------
# Optional cross-checks against read-only external sources
# ---------------------------------------------------------------------------------------------------------------------
_PUBLIC_MEMBER = re.compile(r"public\s+(?!class\b|struct\b|enum\b|interface\b|static\b|const\b|override\b|virtual\b|"
                            r"abstract\b|readonly\b|event\b|delegate\b)([\w<>\[\],.?]+)\s+(\w+)\s*(;|=|\{|$)")

def cs_xml_members(path, indent):
    """XML names of the serialised public members declared at `indent` (the class-member level) in a C# source file:
 {xml name: 'element'|'attribute'}. Methods, constructors, static/const members and [XmlIgnore] members are
 dropped; // comments are ignored (the Water Mod keeps a commented-out attribute on Volumetric). Read-only
 properties are reported as elements (callers compare only the names they need)."""
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        lines = [ln.split("//")[0].rstrip("\r\n").rstrip() for ln in f]
    out, attrs = {}, []
    for ln in lines:
        lead = ln[:len(ln) - len(ln.lstrip())]
        s = ln.strip()
        if lead != indent:
            continue                                   # nested types, bodies, blank lines
        if s.startswith("["):
            attrs.append(s)
            continue
        joined = " ".join(attrs)
        attrs = []
        m = _PUBLIC_MEMBER.match(s)
        if not m or "XmlIgnore" in joined:
            continue
        name = m.group(2)
        am = re.search(r"XmlAttribute\s*\(\s*(?:AttributeName\s*=\s*)?\"(\w+)\"", joined)
        if am:
            out[am.group(1)] = "attribute"
        elif re.search(r"XmlAttribute\b", joined):
            out[name] = "attribute"
        else:
            em = re.search(r"XmlElement\s*\(\s*\"(\w+)\"", joined)
            out[em.group(1) if em else name] = "element"
    return out

def check_decomp(rep, decomp_se, water_mod):
    def find(root, rel_candidates):
        for rel in rel_candidates:
            p = os.path.join(root, *rel.split("/"))
            if os.path.isfile(p):
                return p
        return None

    if decomp_se:
        files = {
            "planet": find(decomp_se, ["VRage.Game/VRage.Game/MyObjectBuilder_PlanetGeneratorDefinition.cs"]),
            "base": find(decomp_se, ["VRage.Game/VRage.Game/MyObjectBuilder_DefinitionBase.cs"]),
            "atmo": find(decomp_se, ["VRage.Game/VRage.Game/MyPlanetAtmosphere.cs"]),
            "maps": find(decomp_se, ["VRage.Game/VRage.Game/MyPlanetMaps.cs"]),
            "mat": find(decomp_se, ["VRage.Game/VRage.Game/MyPlanetMaterialDefinition.cs"]),
            "range": find(decomp_se, ["VRage.Math/VRageMath/SerializableRange.cs"]),
        }
        missing = [k for k, v in files.items() if v is None]
        if missing:
            rep.skip("decomp.se", "decompiled sources not found under %s (%s)" % (decomp_se, ", ".join(missing)))
        else:
            planet = cs_xml_members(files["planet"], "\t")
            base = cs_xml_members(files["base"], "\t")
            atmo = cs_xml_members(files["atmo"], "\t")
            maps = cs_xml_members(files["maps"], "\t")
            mat = cs_xml_members(files["mat"], "\t")
            rng = cs_xml_members(files["range"], "\t")
            problems = []
            want_elems = SE_PLANET_ELEMENTS
            got_elems = {k for k, v in planet.items() if v == "element"}
            if want_elems != got_elems:
                problems.append("planet elements differ: only embedded %s, only decomp %s" %
                                (sorted(want_elems - got_elems), sorted(got_elems - want_elems)))
            base_e = {k for k, v in base.items() if v == "element"}
            base_a = {k for k, v in base.items() if v == "attribute"}
            if not (SE_BASE_ELEMENTS - {"SubtypeName"}) <= base_e or not SE_BASE_ATTRIBUTES <= base_a:
                problems.append("definition base differs: decomp elements %s attributes %s" % (sorted(base_e), sorted(base_a)))
            if {k for k, v in atmo.items() if v == "element"} != SE_ATMOSPHERE_ELEMENTS:
                problems.append("MyPlanetAtmosphere differs: %r" % atmo)
            if {k for k, v in maps.items() if v == "attribute"} != SE_PLANET_MAPS_ATTRIBUTES:
                problems.append("MyPlanetMaps differs: %r" % maps)
            if {k for k, v in mat.items() if v == "attribute"} != SE_MATERIAL_ATTRIBUTES or "Layers" not in mat:
                problems.append("MyPlanetMaterialDefinition differs: %r" % mat)
            if not SE_RANGE_ATTRIBUTES <= {k for k, v in rng.items() if v == "attribute"}:
                problems.append("SerializableRange differs: %r" % rng)
            with open(files["planet"], encoding="utf-8", errors="replace") as f:
                src = f.read()
            for item, field in ((CUSTOM_MATERIAL_ITEM, "CustomMaterialTable"), (CLOUD_LAYER_ITEM, "CloudLayers")):
                if not re.search(r'XmlArrayItem\("%s"\)\]\s*public\s+[\w<>\[\]]+\s+%s\b' % (item, field), src):
                    problems.append("array item name of %s is not %s" % (field, item))
            rep.add("decomp.se", not problems, "embedded names match %d planet members + base, atmosphere, maps, "
                    "material, range" % len(got_elems) if not problems else "; ".join(problems))
    if water_mod:
        files = {k: os.path.join(water_mod, *v.split("/")) for k, v in
                 {"ws": "WaterSettings.cs", "pc": "Configs/PlanetConfig.cs", "bc": "Configs/BaseConfig.cs",
                  "api": "Configs/WaterConfigAPI.cs"}.items()}
        missing = [k for k, v in files.items() if not os.path.isfile(v)]
        if missing:
            rep.skip("decomp.waterMod", "Water Mod sources not found under %s" % water_mod)
            return
        ws = cs_xml_members(files["ws"], " " * 8)
        pc = cs_xml_members(files["pc"], " " * 8)
        bc = cs_xml_members(files["bc"], " " * 8)
        api = cs_xml_members(files["api"], " " * 8)
        problems = []
        if set(ws) != WM_WATER_SETTINGS_ELEMENTS:
            problems.append("WaterSettings differs: only embedded %s, only source %s" %
                            (sorted(WM_WATER_SETTINGS_ELEMENTS - set(ws)), sorted(set(ws) - WM_WATER_SETTINGS_ELEMENTS)))
        if set(pc) != WM_PLANET_CONFIG_ELEMENTS:
            problems.append("PlanetConfig differs: %s" % sorted(set(pc) ^ WM_PLANET_CONFIG_ELEMENTS))
        if {k for k, v in bc.items() if v == "attribute"} != WM_BASE_ATTRIBUTES:
            problems.append("BaseConfig differs: %r" % bc)
        if set(api) != WM_ROOT_ELEMENTS:
            problems.append("WaterConfigAPI differs: %s" % sorted(set(api) ^ WM_ROOT_ELEMENTS))
        with open(files["api"], encoding="utf-8-sig", errors="replace") as f:
            if 'XmlRoot("%s")' % WM_ROOT not in f.read():
                problems.append("XmlRoot is not %s" % WM_ROOT)
        rep.add("decomp.waterMod", not problems, "embedded names match WaterSettings (%d), PlanetConfig, BaseConfig, "
                "WaterConfigAPI" % len(ws) if not problems else "; ".join(problems))

def check_stock(rep, content, vals):
    pdir = os.path.join(content, "Data", "PlanetDataFiles", "EarthLike")
    if not os.path.isdir(pdir):
        rep.skip("stock.format", "SE content not found at %s" % content)
        return
    problems, mat_types = [], {}
    for face in FACES:
        for name, wants in ((face + ".png", ((16, 0),)), (face + "_mat.png", ((8, 2), (8, 6)))):
            try:
                with open(os.path.join(pdir, name), "rb") as f:
                    info = png_read(f.read(64), header_only=True)
            except (OSError, PngError) as e:
                problems.append("%s: %s" % (name, e))
                continue
            got = (info["bit_depth"], info["color_type"])
            if got not in wants or info["width"] != info["height"]:
                problems.append("%s is %dx%d %r" % (name, info["width"], info["height"], got))
            if name.endswith("_mat.png"):
                mat_types[face] = {2: "RGB", 6: "RGBA"}.get(got[1], str(got[1]))
    rep.add("stock.format", not problems, "EarthLike heightmaps are square 16-bit grayscale; its _mat files are square "
            "8-bit RGB or RGBA (%s), so SE reads both; ours are RGBA" % ", ".join("%s %s" % kv for kv in mat_types.items())
            if not problems else "; ".join(problems))
    names = set()
    for fn in ("VoxelMaterials_planetary.sbc", "VoxelMaterials_asteroids.sbc"):
        p = os.path.join(content, "Data", fn)
        if os.path.isfile(p):
            for el in ET.parse(p).getroot().iter("SubtypeId"):
                names.add((el.text or "").strip())
    used = (vals or {}).get("materialNames", [])
    missing = [n for n in used if n not in names]
    rep.add("stock.voxelMaterials", bool(names) and not missing,
            "stock voxel materials exist: %s" % ", ".join(used) if not missing else "not stock: %s" % ", ".join(missing))

# ---------------------------------------------------------------------------------------------------------------------
def spawn_numbers(centre):
    """Where an integer centre C goes in the save, and what IMyVoxelMaps.SpawnPlanet needs (both take the min corner;
 SE adds storage/2 and the half voxel)."""
    half = 262144 / 2 + 0.5
    corner = tuple(c - half for c in centre)
    rs = site_v1.RS_EXPECTED
    anchor = (centre[0], centre[1], centre[2] + rs)          # U0 = +Z: SN (0, 0, 0)
    above = (centre[0], centre[1], centre[2] + rs + 50.0)    # SN (0, 50, 0)
    return {"centre": centre, "savedMinCorner": corner, "spawnPlanetPos": corner, "anchorSn000": anchor,
            "characterSn0_50_0": above, "spawnSize": SPAWN_SIZE}

def run(mod, decomp_se=None, water_mod=None, se_content=None):
    rep = Report()
    files_ok = check_files(rep, mod)
    if os.path.isfile(os.path.join(mod, "metadata.mod")):
        check_metadata(rep, mod)
    vals = check_sbc(rep, mod) if os.path.isfile(os.path.join(mod, "Data", "PlanetGeneratorDefinitions.sbc")) else None
    check_derived(rep, vals)
    if os.path.isfile(os.path.join(mod, "Data", "WaterConfig.xml")):
        check_water(rep, mod, vals)
    if files_ok or os.path.isdir(os.path.join(mod, "Data", "PlanetDataFiles", SUBTYPE)):
        check_pngs(rep, mod, vals)
    if HERE not in sys.path:
        sys.path.insert(0, HERE)   # S16 hook: -I drops the script dir
    import s16_blocks   # noqa: E402 S16 hook: s16.* rows
    s16_blocks.check(rep, mod, decomp_se, se_content)
    import s14_weapons   # noqa: E402 S14 hook: s14.weapons.* rows (the Water Mod weapon override; with --water-mod against the installed mod)
    s14_weapons.check(rep, mod, water_mod)
    import s15_water   # noqa: E402 S15 hook: s15.* rows (the sea's own material, its texture, the Water Mod config naming it)
    s15_water.check(rep, mod)
    import s17_food   # noqa: E402 S17 hook: s17.* rows (the fish items and blueprints; s17.assets with --se-content)
    s17_food.check(rep, mod, se_content)
    if decomp_se or water_mod:
        check_decomp(rep, decomp_se, water_mod)
    if se_content:
        check_stock(rep, se_content, vals)
    return rep

def _resolve(value, default):
    if value is None:
        return None
    return default if value == "auto" else value

def main(argv=None):
    ap = argparse.ArgumentParser(description="Check the DeepEngineers.World mod folder (DE_4546B, site v1).")
    ap.add_argument("--mod", default=DEFAULT_MOD_DIR)
    ap.add_argument("--decomp-se", help="decompiled SE root (…/decomp/se) or 'auto'")
    ap.add_argument("--water-mod", help="Water Mod script folder (…/Data/Scripts/Water/Water) or 'auto'")
    ap.add_argument("--se-content", help="SE Content folder or 'auto' (read only)")
    ap.add_argument("--centre", default="%d,%d,%d" % tuple(int(c) for c in site_v1.TEST_C),
                    help="integer planet centre X,Y,Z to print save/spawn numbers for (default: the CONTRACT test centre)")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args(argv)
    rep = run(a.mod, _resolve(a.decomp_se, DEFAULT_DECOMP_SE), _resolve(a.water_mod, DEFAULT_WATER_MOD),
              _resolve(a.se_content, DEFAULT_SE_CONTENT))
    centre = tuple(float(v) for v in a.centre.split(","))
    if len(centre) != 3 or any(c != math.floor(c) for c in centre):
        print("--centre must be three integers", file=sys.stderr)
        return 2
    sp = spawn_numbers(centre)
    n_fail = len(rep.failed)
    n_checks = len([r for r in rep.results if r[1] in ("PASS", "FAIL")])
    if a.json:
        print(json.dumps({"siteVersion": site_v1.SITE_VERSION, "mod": a.mod, "pass": n_fail == 0,
                          "checks": [{"id": i, "status": s, "detail": d} for i, s, d in rep.results],
                          "spawn": sp}, indent=1))
    else:
        for cid, status, detail in rep.results:
            print("CHECK %-26s %s %s" % (cid, status, detail))
        fmt = lambda v: "(%s)" % ", ".join(re.sub(r"\.0$", "", repr(float(c))) for c in v)  # noqa: E731
        print("INFO  spawn size %d; centre C %s -> saved <Position> (min corner) %s; IMyVoxelMaps.SpawnPlanet pos %s; "
              "anchor SN(0,0,0) %s; SN(0,50,0) %s" % (sp["spawnSize"], fmt(sp["centre"]), fmt(sp["savedMinCorner"]),
                                                      fmt(sp["spawnPlanetPos"]), fmt(sp["anchorSn000"]),
                                                      fmt(sp["characterSn0_50_0"])))
        print("CHECK PLANET %s (%d checks, %d failed) siteVersion=%d mod=%s" %
              ("PASS" if n_fail == 0 else "FAIL", n_checks, n_fail, site_v1.SITE_VERSION, a.mod))
    return 0 if n_fail == 0 else 1

if __name__ == "__main__":
    sys.exit(main())
