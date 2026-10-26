"""S8a biomes and spawns, the Python twin of src/DeepEngineers.Wire/S8aBiomes.cs (+ the converter core's S8aMaterialMap
table and biome rule): CONTRACT §5.4 "S8a conventions" format biome.names, biome.rule, convert.material, spawn.place,
spawn.table; ROADMAP "### S8a" S8a.N10. Stdlib only; reads nothing of either game.

 python -I tools/planet/s8a_biomes.py selftest # lines = data = CONTRACT block, rules, keys, plan statistics, vectors
 python -I tools/planet/s8a_biomes.py vectors # (re)writes tests/s8a/vectors/biomes.tsv (read by S8aBiomesTests)
 python -I tools/planet/s8a_biomes.py check # the vector file equals a fresh regeneration (exit 1 when not)
 python -I tools/planet/s8a_biomes.py contract # the five lines and the constants occur in docs/CONTRACT.md's block

The analyzer (tests/s8a/analyze_s8a.py) imports this module for the bands, the table densities, the expected block type
of a cameraBiome and the crater box. Interpretations (the canonical text leaves them open) are documented in
S8aBiomes.cs and docs/S8A-STATUS.md: the spawn key is fnv1a64 over 24 little-endian bytes (u64 chartKey, i32 bx, i32
bz, i32 column = lx + 160*lz, i32 row = the table row index 0..18); a column spawns a row when (m >> 11) / 2^53 <
perHectare / 10,000 * scale with m = splitmix64's finaliser of the key; placement from m's low bits; bands d <= 60 / 200 / 500 / 1000.
"""
from __future__ import annotations

import math
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
VECTORS = os.path.join(ROOT, "tests", "s8a", "vectors", "biomes.tsv")
CONTRACT = os.path.join(ROOT, "docs", "CONTRACT.md")

# ---- the canonical lines (byte for byte the lines of `ext s8a terrain 4`) ------------------------------------------
LINE_BIOME_NAMES = ("format biome.names 0 safeShallows, 1 grassyPlateaus, 2 kelpForest, 3 dunes, 4 bloodKelp, 5 sparseReef, 6 "
                    "mountains, 7 void, 8 mushroomForest, 9 grandReef, 10 underwaterIslands, 11 crashZone, 12 "
                    "lostRiver_Canyon; 255 none (Subnautica's own answer)")
LINE_BIOME_RULE = ("format biome.rule cameraBiome from the window column under the camera: the planet's biome map (the "
                   "material map's G, PlanetMaps Biome) at the surface point when it holds 1..13: G - 1; else by the surface "
                   "material: Grass 6; Sand_02 0 when the seabed is <= 60 m deep, else 1; Stone_01 2; Stone_02 3; Stone_03 4; "
                   "Stone_04 5; Stone_05 or deeper than 1000 m 7; GetBiome(p) outside the crater box returns "
                   "names[cameraBiome] within BiomeRadiusM of the camera, else void")
LINE_MATERIAL = ("format convert.material SE voxel material subtype -> SN block type: Sand_02 1, Stone_01 111, Stone_02 "
                 "150, Stone_03 190, Stone_04 141, Stone_05 84, Grass 219, Iron_02 95, Nickel_01 96, Silicon_01 107, "
                 "Cobalt_01 108, Magnesium_01 117, Silver_01 118, Gold_01 119, Platinum_01 121, Uraninite_01 122, any other "
                 "84; names resolved to indices once per session")
LINE_SPAWN_PLACE = ("format spawn.place window columns only, keyed by fnv1a64(chartKey, bx, bz, column, row index); seabed = the "
                    "top solid voxel of the served file; within RadiusM of the camera, gone beyond DespawnM; "
                    "MakeEntityTransient, never saved; moved by -T on a chart step")
LINE_SPAWN_TABLE = ("format spawn.table per hectare of seabed by depth d, Subnautica TechTypes: d <= 60 Peeper 3 Bladderfish 3 "
                    "Boomerang 2 BluePalm 6 PurpleTentacle 4 CoralShellPlate 3; d <= 200 Hoopfish 3 Spadefish 2 Reginald 2 "
                    "RedGrass 8 PurpleFan 3 SmallKoosh 2; d <= 500 Eyeye 2 HoleFish 2 Mesmer 1 BloodGrass 6; d <= 1000 "
                    "Spinefish 2 GarryFish 1 BloodGrass 2; deeper none")
LINES = (LINE_BIOME_NAMES, LINE_BIOME_RULE, LINE_MATERIAL, LINE_SPAWN_PLACE, LINE_SPAWN_TABLE)

# ---- the constants these rules use (S8aConventions; checked against the CONTRACT block by `contract`) ----------------
CONST = {("SnWorld", "OriginX"): -2048, ("SnWorld", "OriginY"): -3040, ("SnWorld", "OriginZ"): -2048,
         ("SnWorld", "BatchVoxels"): 160, ("SnWorld", "BatchesY"): 20, ("SnWorld", "NearZero"): 126,
         ("Window", "CraterMin"): 0, ("Window", "CraterMax"): 25, ("Window", "UniverseMin"): -35, ("Window", "UniverseMax"): 60,
         ("Window", "BiomeRadiusM"): 200, ("Spawn", "RadiusM"): 150, ("Spawn", "DespawnM"): 250}
ORIGIN_X, ORIGIN_Y, ORIGIN_Z = -2048, -3040, -2048
BATCH = 160
CRATER_MIN, CRATER_MAX = 0, 25
UNIVERSE_MIN, UNIVERSE_MAX = -35, 60
BIOME_RADIUS_M = 200.0
SPAWN_RADIUS_M, DESPAWN_M = 150.0, 250.0

# ---- biome.names, biome.rule ---------------------------------------------------------------------------------------
NAMES = ("safeShallows", "grassyPlateaus", "kelpForest", "dunes", "bloodKelp", "sparseReef", "mountains", "void",
         "mushroomForest", "grandReef", "underwaterIslands", "crashZone", "lostRiver_Canyon")   # 8..12: Stage C
NONE = 255
VOID = 7
SHALLOW_SAND_M, VOID_DEPTH_M = 60.0, 1000.0

# ---- convert.material (the converter core's S8aMaterialMap) --------------------------------------------------------
SUBTYPES = ("Sand_02", "Stone_01", "Stone_02", "Stone_03", "Stone_04", "Stone_05", "Grass",
            # Stage C: the planet ores (census-picked Subnautica types
            "Iron_02", "Nickel_01", "Silicon_01", "Cobalt_01", "Magnesium_01", "Silver_01", "Gold_01", "Platinum_01", "Uraninite_01")
SN_TYPES = (1, 111, 150, 190, 141, 84, 219, 95, 96, 107, 108, 117, 118, 119, 121, 122)
FALLBACK = 84

# ---- spawn.table ---------------------------------------------------------------------------------------------------
BAND_MAX_DEPTH = (60.0, 200.0, 500.0, 1000.0)
FLORA = frozenset(("BluePalm", "PurpleTentacle", "CoralShellPlate", "RedGrass", "PurpleFan", "SmallKoosh", "BloodGrass"))
_SPEC = ((0, "Peeper", 3), (0, "Bladderfish", 3), (0, "Boomerang", 2), (0, "BluePalm", 6), (0, "PurpleTentacle", 4),
         (0, "CoralShellPlate", 3), (1, "Hoopfish", 3), (1, "Spadefish", 2), (1, "Reginald", 2), (1, "RedGrass", 8),
         (1, "PurpleFan", 3), (1, "SmallKoosh", 2), (2, "Eyeye", 2), (2, "HoleFish", 2), (2, "Mesmer", 1),
         (2, "BloodGrass", 6), (3, "Spinefish", 2), (3, "GarryFish", 1), (3, "BloodGrass", 2))
ROWS = tuple({"index": i, "band": b, "techType": n, "perHectare": float(d), "flora": n in FLORA} for i, (b, n, d) in enumerate(_SPEC))
HECTARE_M2 = 10000.0
HOVER_MIN_M, HOVER_SPAN_M, FLORA_SINK_M = 2.0, 6.0, 0.1
FNV_OFFSET, FNV_PRIME, MASK64 = 0xcbf29ce484222325, 0x100000001b3, (1 << 64) - 1

def f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]

def name(biome):
    return NAMES[biome] if isinstance(biome, int) and 0 <= biome < len(NAMES) else None

def camera_biome(subtype, depth):
    """biome.rule as the converter core implements it (S8aMaterialMap.BiomeOf): deeper than 1,000 m 7; an unnamed
 material 7; no material or NaN depth 255."""
    if subtype is None or depth is None or (isinstance(depth, float) and math.isnan(depth)):
        return NONE
    if depth > VOID_DEPTH_M:
        return VOID
    return {"Grass": 6, "Sand_02": 0 if depth <= SHALLOW_SAND_M else 1, "Stone_01": 2, "Stone_02": 3, "Stone_03": 4,
            "Stone_04": 5}.get(subtype, VOID)

def biome_from_map(g):
    """biome.rule's biome-map half (Stage C, S8aMaterialMap.BiomeFromMap): G 1..len(NAMES) -> G - 1, else 255."""
    return g - 1 if isinstance(g, int) and 1 <= g <= len(NAMES) else NONE

def camera_biome_map(subtype, depth, g):
    """biome.rule with the planet's biome map (S8aBiomes.CameraBiome(subtype, depth, biomeMapValue))."""
    b = biome_from_map(g)
    return b if b != NONE else camera_biome(subtype, depth)

def sn_type(subtype):
    for s, t in zip(SUBTYPES, SN_TYPES):
        if s == subtype:
            return t
    return FALLBACK

def expected_sn_type(biome, depth=None):
    """The block type the window holds under the camera for cameraBiome `biome`; -1 when not determined (255, 7)."""
    return {0: sn_type("Sand_02"), 1: sn_type("Sand_02"), 2: sn_type("Stone_01"), 3: sn_type("Stone_02"),
            4: sn_type("Stone_03"), 5: sn_type("Stone_04"), 6: sn_type("Grass")}.get(biome, -1)

def batch_x(x):
    return int(math.floor((x - ORIGIN_X) / BATCH))

def batch_z(z):
    return int(math.floor((z - ORIGIN_Z) / BATCH))

def in_crater_box(x, z):
    bx, bz = batch_x(x), batch_z(z)
    return CRATER_MIN <= bx <= CRATER_MAX and CRATER_MIN <= bz <= CRATER_MAX

def is_window_column(bx, bz):
    uni = UNIVERSE_MIN <= bx <= UNIVERSE_MAX and UNIVERSE_MIN <= bz <= UNIVERSE_MAX
    crater = CRATER_MIN <= bx <= CRATER_MAX and CRATER_MIN <= bz <= CRATER_MAX
    return uni and not crater

def biome_at(p, c, biome):
    """GetBiome beyond the crater box: names[biome] within BiomeRadiusM (3D) of the camera, else void; None = no override."""
    if in_crater_box(p[0], p[2]):
        return None
    n = name(biome)
    if n is None:
        return None
    d2 = sum((p[i] - c[i]) ** 2 for i in range(3))
    return n if d2 <= BIOME_RADIUS_M * BIOME_RADIUS_M else NAMES[VOID]

# ---- spawn.table / spawn.place ---------------------------------------------------------------------------------------
def band_of(depth):
    if depth is None or (isinstance(depth, float) and math.isnan(depth)):
        return -1
    for b, m in enumerate(BAND_MAX_DEPTH):
        if depth <= m:
            return b
    return -1

def rows_of_band(band):
    return [r for r in ROWS if r["band"] == band]

def density_of_band(band, disabled=()):
    return sum(r["perHectare"] for r in ROWS if r["band"] == band and r["index"] not in disabled)

def _fnv_bytes(h, data):
    for b in data:
        h ^= b
        h = (h * FNV_PRIME) & MASK64
    return h

def spawn_key(chart_key, bx, bz, column, row):
    data = struct.pack("<Qiiii", chart_key & MASK64, bx, bz, column, row)
    return _fnv_bytes(FNV_OFFSET, data)

def mix(key):
    """splitmix64's finaliser over the key (FNV-1a's top bits are not uniform enough for p ~ 1e-4 draws)."""
    z = (key + 0x9E3779B97F4A7C15) & MASK64
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK64
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK64
    return z ^ (z >> 31)

def draw(key):
    return (mix(key) >> 11) / 9007199254740992.0

def spawns(key, per_hectare, scale=1.0):
    return draw(key) < per_hectare / HECTARE_M2 * scale

def placement(key):
    key = mix(key)
    jx = (key & 0x3FF) / 1024.0
    jz = ((key >> 20) & 0x3FF) / 1024.0
    yaw = ((key >> 30) & 0x1FF) * (360.0 / 512.0)
    hover = HOVER_MIN_M + HOVER_SPAN_M * (((key >> 40) & 0xFF) / 255.0)
    return jx, jz, yaw, hover

def plan(chart_key, bx, bz, top_of, scale=1.0, disabled=()):
    """spawn.place over one batch column whose seabed SN y of local column (lx, lz) is top_of(lx, lz) (float32, None
 when unknown); the same order as S8aSeabedColumns.Plan (lz outer, lx inner, rows in table order)."""
    out = []
    bands = [rows_of_band(b) for b in range(len(BAND_MAX_DEPTH))]
    for lz in range(BATCH):
        for lx in range(BATCH):
            top = top_of(lx, lz)
            if top is None:
                continue
            band = band_of(-top)
            if band < 0:
                continue
            col = lx + BATCH * lz
            for r in bands[band]:
                if r["index"] in disabled:
                    continue
                k = spawn_key(chart_key, bx, bz, col, r["index"])
                if not spawns(k, r["perHectare"], scale):
                    continue
                jx, jz, yaw, hover = placement(k)
                out.append({"key": k, "row": r["index"], "column": col, "band": band,
                            "x": ORIGIN_X + bx * BATCH + lx + jx, "z": ORIGIN_Z + bz * BATCH + lz + jz,
                            "seabedY": top, "y": (top - FLORA_SINK_M) if r["flora"] else (top + hover), "yaw": yaw})
    return out

# ---- lines rendered from the data ----------------------------------------------------------------------------------
def render_names():
    return "format biome.names " + ", ".join("%d %s" % (i, n) for i, n in enumerate(NAMES)) + "; %d none (Subnautica's own answer)" % NONE

def render_material():
    return ("format convert.material SE voxel material subtype -> SN block type: " +
            ", ".join("%s %d" % (s, t) for s, t in zip(SUBTYPES, SN_TYPES)) +
            ", any other %d; names resolved to indices once per session" % FALLBACK)

def render_table():
    parts = []
    for b, m in enumerate(BAND_MAX_DEPTH):
        parts.append("d <= %d " % m + " ".join("%s %d" % (r["techType"], r["perHectare"]) for r in rows_of_band(b)))
    return "format spawn.table per hectare of seabed by depth d, Subnautica TechTypes: " + "; ".join(parts) + "; deeper none"

def contract_block(path=CONTRACT):
    """The lines of CONTRACT §5.4 "S8a conventions"'s canonical block (between ``` after 'ext s8a terrain')."""
    with open(path, encoding="utf-8") as f:
        text = f.read()
    i = text.find("```\next s8a terrain")
    if i < 0:
        return None
    j = text.find("```", i + 4)
    return text[i + 4:j].strip("\n").split("\n")

def contract_check(verbose=True):
    lines = contract_block()
    ok = lines is not None
    if not ok:
        print("  CONTRACT block 'ext s8a terrain' not found")
        return False
    have = set(lines)
    for ln in LINES:
        hit = ln in have
        ok &= hit
        if verbose or not hit:
            print("  %-26s %s" % (ln.split(" ")[1], "in the CONTRACT block" if hit else "MISSING from the CONTRACT block"))
    for (g, n), v in CONST.items():
        want = "const %s %s %d" % (g, n, v)
        hit = want in have
        ok &= hit
        if not hit:
            print("  constant %s MISSING (%s)" % (want, [x for x in lines if x.startswith("const %s %s " % (g, n))]))
    if verbose:
        print("  %d constants present" % len(CONST))
    return ok

# ---- vectors ---------------------------------------------------------------------------------------------------------
def _num(x):
    return repr(float(x))

def _seabed_fn(kind):
    """Synthetic seabeds of the plan vectors (float32 heights, exactly representable)."""
    if kind == "slope":
        return lambda lx, lz: f32(-(20.0 + 0.875 * lx + 0.375 * lz))           # 20.. 219 m: bands 0, 1, 2
    if kind == "deep":
        return lambda lx, lz: f32(-(450.0 + 4.0 * lx))                           # 450.. 1086 m: bands 2, 3, none
    if kind == "holes":
        return lambda lx, lz: None if (lx * 7 + lz * 3) % 5 == 0 else f32(-(30.0 + 0.25 * lz))
    raise ValueError(kind)

PLAN_CASES = (("slope", 0x1234abcd5678ef01, 30, -3, 1.0, ()), ("slope", 0x1234abcd5678ef01, 30, -3, 10.0, ()),
              ("deep", 0x0fedcba987654321, -20, 40, 1.0, ()), ("holes", 0x00000000deadbeef, 26, 0, 1.0, (3, 5)))

def vector_lines():
    out = ["# S8a biomes and spawns vectors (tools/planet/s8a_biomes.py vectors); read by tests/DeepEngineers.Wire.Tests/S8aBiomesTests.cs"]
    for key, ln in zip(("biome.names", "biome.rule", "convert.material", "spawn.place", "spawn.table"), LINES):
        out.append("line\t%s\t%s" % (key, ln))
    for i, n in enumerate(NAMES):
        out.append("name\t%d\t%s" % (i, n))
    for s in SUBTYPES + ("Basalt", "Ice_01", ""):
        out.append("material\t%s\t%d" % (s, sn_type(s)))
    for s in SUBTYPES + ("Basalt",):
        for d in (10.0, 60.0, 60.5, 200.0, 999.0, 1000.0, 1000.5, 1400.0):
            out.append("biome\t%s\t%s\t%d" % (s, _num(d), camera_biome(s, d)))
    for g, s, d in ((0, "Sand_02", 30.0), (1, "Stone_05", 30.0), (9, "Sand_02", 30.0), (13, None, 30.0), (14, "Grass", 20.0),
                    (8, "Iron_02", 2500.0), (255, "Stone_01", 70.0), (0, None, 10.0), (12, "Stone_04", 1500.0)):
        out.append("biomemap	%d	%s	%s	%d" % (g, "-" if s is None else s, _num(d), camera_biome_map(s, d, g)))
    for b in list(range(len(NAMES))) + [255]:
        for d in (30.0, 1200.0):
            out.append("expect\t%d\t%s\t%d" % (b, _num(d), expected_sn_type(b, d)))
    for x, z in ((0.0, 0.0), (-2048.0, -2048.0), (-2048.5, 0.0), (2111.9, 2111.9), (2112.0, 0.0), (5000.0, -40.0),
                 (-7000.0, 7000.0), (2047.0, -2049.0)):
        out.append("crater\t%s\t%s\t%d" % (_num(x), _num(z), 1 if in_crater_box(x, z) else 0))
    for bx, bz in ((0, 0), (25, 25), (26, 0), (-1, 10), (-35, -35), (-36, 0), (60, 61), (40, 40)):
        out.append("window\t%d\t%d\t%d" % (bx, bz, 1 if is_window_column(bx, bz) else 0))
    for p, c, b in (((3000.0, -50.0, 0.0), (3000.0, -10.0, 0.0), 2), ((3000.0, -50.0, 0.0), (3300.0, -10.0, 0.0), 2),
                    ((0.0, -50.0, 0.0), (0.0, -10.0, 0.0), 2), ((3000.0, -50.0, 0.0), (3000.0, -10.0, 0.0), 255),
                    ((3000.0, -50.0, 0.0), (3000.0, 150.0, 0.0), 6), ((-2100.0, -5.0, 9.0), (-2100.0, -5.0, 9.0), 0)):
        r = biome_at(p, c, b)
        out.append("biomeat\t%s\t%d\t%s" % ("\t".join(_num(v) for v in p + c), b, r if r is not None else "-"))
    for d in (-5.0, 0.0, 60.0, 60.01, 200.0, 200.5, 500.0, 999.9, 1000.0, 1000.01, 3000.0):
        out.append("band\t%s\t%d" % (_num(d), band_of(d)))
    for b in range(len(BAND_MAX_DEPTH)):
        out.append("density\t%d\t%s" % (b, _num(density_of_band(b))))
    for ck, bx, bz, col, row in ((0, 0, 0, 0, 0), (0x1234abcd5678ef01, 30, -3, 12345, 7), (MASK64, -35, 60, 25599, 18),
                                 (0x0fedcba987654321, -20, 40, 160, 3), (0x00000000deadbeef, 26, 0, 1, 12)):
        k = spawn_key(ck, bx, bz, col, row)
        jx, jz, yaw, hover = placement(k)
        out.append("key\t%016x\t%d\t%d\t%d\t%d\t%016x\t%s\t%s\t%s\t%s\t%s" % (ck, bx, bz, col, row, k, _num(draw(k)), _num(jx), _num(jz),
                                                                         _num(yaw), _num(hover)))
    for pid, (kind, ck, bx, bz, scale, disabled) in enumerate(PLAN_CASES):
        pts = plan(ck, bx, bz, _seabed_fn(kind), scale, disabled)
        fold = 0
        for p in pts:
            fold ^= p["key"]
        per_row = [0] * len(ROWS)
        for p in pts:
            per_row[p["row"]] += 1
        out.append("plan\t%d\t%s\t%016x\t%d\t%d\t%s\t%s\t%d\t%016x\t%s" % (pid, kind, ck, bx, bz, _num(scale),
                                                                       ",".join(str(x) for x in disabled) or "-", len(pts), fold,
                                                                       ",".join(str(x) for x in per_row)))
        for i, p in enumerate(pts[:6]):
            out.append("planpt\t%d\t%d\t%016x\t%d\t%d\t%s\t%s\t%s\t%s\t%s" % (pid, i, p["key"], p["row"], p["column"], _num(p["x"]),
                                                                          _num(p["y"]), _num(p["z"]), _num(p["seabedY"]), _num(p["yaw"])))
    return out

def write_vectors(path=VECTORS):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    lines = vector_lines()
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print("wrote %s (%d lines)" % (path, len(lines)))
    return 0

def check_vectors(path=VECTORS):
    want = vector_lines()
    try:
        with open(path, encoding="utf-8") as f:
            have = f.read().rstrip("\n").split("\n")
    except OSError as e:
        print("vector file unreadable: %s" % e)
        return 1
    if have != want:
        diff = next((i for i in range(min(len(have), len(want))) if have[i] != want[i]), min(len(have), len(want)))
        print("vector file differs from a fresh regeneration at line %d (have %d lines, want %d)" % (diff + 1, len(have), len(want)))
        return 1
    print("vector file matches (%d lines)" % len(want))
    return 0

# ---- self-test -------------------------------------------------------------------------------------------------------
def selftest():
    ok = True

    def chk(cond, what):
        nonlocal ok
        print("  %-64s %s" % (what, "PASS" if cond else "FAIL"))
        ok &= bool(cond)

    chk(render_names() == LINE_BIOME_NAMES, "biome.names rendered from the data = the pinned line")
    chk(render_material() == LINE_MATERIAL, "convert.material rendered from the data = the pinned line")
    chk(render_table() == LINE_SPAWN_TABLE, "spawn.table rendered from the data = the pinned line")
    chk(contract_check(verbose=False), "the five lines and %d constants are in CONTRACT's block" % len(CONST))
    chk(camera_biome("Sand_02", 60.0) == 0 and camera_biome("Sand_02", 60.5) == 1 and camera_biome("Grass", 1000.5) == 7
        and camera_biome("Stone_04", 300.0) == 5 and camera_biome("Basalt", 30.0) == 7 and camera_biome(None, 3.0) == 255,
        "biome.rule (Sand split at 60 m, 7 deeper than 1,000 m)")
    chk(camera_biome_map("Sand_02", 30.0, 10) == 9 and camera_biome_map("Sand_02", 30.0, 0) == 0 and camera_biome_map("Grass", 5.0, 14) == 6
        and camera_biome_map(None, 5.0, 13) == 12, "biome.rule: the biome map wins when it holds 1..13, else the material rule")
    chk(sn_type("Iron_02") == 95 and sn_type("Uraninite_01") == 122, "the ore rows")
    chk(sn_type("Stone_02") == 150 and sn_type("Unknown") == 84 and expected_sn_type(3) == 150 and expected_sn_type(7) == -1,
        "material table and the expected type of a biome")
    chk(in_crater_box(0.0, 0.0) and in_crater_box(2111.9, -2048.0) and not in_crater_box(2112.0, 0.0)
        and not in_crater_box(-2048.1, 0.0), "the crater box is SN x, z in [-2048, 2112)")
    chk(biome_at((3000.0, -50.0, 0.0), (3000.0, -10.0, 0.0), 2) == "kelpForest" and
        biome_at((3000.0, -50.0, 0.0), (3300.0, -10.0, 0.0), 2) == "void" and biome_at((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), 2) is None,
        "GetBiome beyond the crater box (radius 200 m, else void)")
    chk([band_of(d) for d in (10.0, 60.0, 61.0, 200.0, 499.0, 1000.0, 1001.0)] == [0, 0, 1, 1, 2, 3, -1], "depth bands")
    chk(abs(density_of_band(0) - 21) < 1e-12 and abs(density_of_band(1) - 20) < 1e-12 and abs(density_of_band(2) - 11) < 1e-12
        and abs(density_of_band(3) - 5) < 1e-12, "band densities 21 / 20 / 11 / 5 per hectare")
    k = spawn_key(0, 0, 0, 0, 0)
    chk(k == _fnv_bytes(FNV_OFFSET, bytes(24)), "key = fnv1a64 over 24 zero bytes for zeros")
    chk(spawn_key(1, 0, 0, 0, 0) != spawn_key(0, 1, 0, 0, 0) and spawn_key(0, 0, 0, 1, 0) != spawn_key(0, 0, 0, 0, 1),
        "every key part changes the key")
    # plan statistics: a flat 40 m seabed over 64 batch columns (164 ha): every row within 4 sd of its expectation
    top = lambda lx, lz: f32(-40.0)
    counts = [0] * len(ROWS)
    cols = 0
    for bx in range(30, 38):
        for bz in range(30, 38):
            for p in plan(0x5eed, bx, bz, top):
                counts[p["row"]] += 1
            cols += 1
    ha = cols * BATCH * BATCH / HECTARE_M2
    worst = 0.0
    for r in rows_of_band(0):
        exp = r["perHectare"] * ha
        worst = max(worst, abs(counts[r["index"]] - exp) / math.sqrt(exp))
    chk(worst < 4.0 and all(counts[r["index"]] == 0 for r in ROWS if r["band"] != 0),
        "plan statistics on %.0f ha: worst row %.2f sd, other bands empty" % (ha, worst))
    scaled = sum(1 for _ in plan(0x5eed, 30, 30, top, 10.0))
    base = sum(1 for _ in plan(0x5eed, 30, 30, top, 1.0))
    chk(8.0 <= scaled / max(1, base) <= 12.0, "density scale x10 (S8a.M11) gives %.1fx the points" % (scaled / max(1, base)))
    pts = plan(0x5eed, 30, 30, top)
    chk(all(30 * BATCH + ORIGIN_X <= p["x"] < 31 * BATCH + ORIGIN_X and not in_crater_box(p["x"], p["z"]) for p in pts),
        "points stay inside their batch column, outside the crater box")
    chk(all((p["y"] < p["seabedY"]) == (ROWS[p["row"]]["flora"]) for p in pts), "flora below the seabed point, creatures above")
    chk(check_vectors() == 0, "the vector file is current")
    print("s8a_biomes selftest %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv):
    cmd = argv[0] if argv else "selftest"
    if cmd == "selftest":
        return selftest()
    if cmd == "vectors":
        return write_vectors()
    if cmd == "check":
        return check_vectors()
    if cmd == "contract":
        return 0 if contract_check() else 1
    print(__doc__)
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
