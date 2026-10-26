"""S8a conventions, the Python twin of the Wire converter core (src/DeepEngineers.Wire/S8aConventions.cs, S8aConvert.cs,
S8aFeed.cs, S8aChart.cs, S8aWindowPlan.cs, S8aStore.cs): the canonical text and its hash (`ext s8a terrain 5`,
0x3188251ab11e2fec, 155 lines, 14,696 bytes; CONTRACT §5.4 "S8a conventions"), the sample encoding, the feed codec and
queue rules, the chart rule and keys, the window plan, the store keys, the manifest and the overlay file. The octree
writer's twin is tools/sn-octrees/sn_octree_write.py. Shares no code with the C#; the vectors it writes under
tests/s8a/vectors/ are read by the C# tests (S8a*Tests in tests/DeepEngineers.Wire.Tests).

Usage (repository root; stdlib + numpy):
 python -I tools/planet/s8a_ref.py --selftest every rule's hand checks, the pin, the CONTRACT block, vectors current
 python -I tools/planet/s8a_ref.py --vectors (re)writes tests/s8a/vectors/{convert,feed,chart,window,store}.tsv,
 manifest.txt and overlay.vx2
 python -I tools/planet/s8a_ref.py --text prints the canonical text
 python -I tools/planet/s8a_ref.py --pin prints hash, lines, bytes (S8a.C3)
"""
import math
import os
import struct
import sys
import zlib

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
VEC_DIR = os.path.join(ROOT, "tests", "s8a", "vectors")
CONTRACT = os.path.join(ROOT, "docs", "CONTRACT.md")

M64 = 0xFFFFFFFFFFFFFFFF

# =====================================================================================================================
# canonical text (CONTRACT §5.4 "S8a conventions")
# =====================================================================================================================
HEADER = "ext s8a terrain 6"
PINNED_HASH = 0x3188251ab11e2fec
CANON_LINES, CANON_BYTES = 155, 14696

C2S8A = dict(Version=1, RowSize=32, HeaderSize=64, MaxPerTick=64, MaxRows=1024, Offset=0x50000, RowsOffset=0x50040)
C2FLAGS = dict(DrillOn=1, WindowOn=2, TreadmillOn=4, NearPending=8, NearEmpty=16, Resync=32, OffSite=64)
C7S8A = dict(Version=1, Size=48, Offset=0x8C)
C7FLAGS = dict(StoreOpen=1, GetPathPatched=2, LoaderPatched=4, CenterWidened=8, Refused=16)
CHART = dict(Site=0, Step0Batches=5, StepBatches=12, BoxInnerM=3200, AnchorMaxM=3840, ReturnM=4160, Chart0OutM=4800,
             BoxOuterM=7040, StepGapMs=2000)
NOTICE_FLAGS = dict(AllSolid=1, AllEmpty=2, Reuse=4, Final=8, Restore=16)
NOTICE_KIND = dict(BatchWritten=1, BatchCleared=2, ChartStep=3, StoreReset=4)
NOTICE_LAYER = dict(Drill=1, Window=2)
OVERLAY = dict(Version=1, CellVoxels=16, MaxCells=65536)
QUEUE = dict(MaxReloadsInFlight=8, MaxCellReloadsPerNotice=64, MaxPending=4096)
SNWORLD = dict(OriginY=-3040, OriginX=-2048, OriginZ=-2048, FileVersion=4, OctreesPerBatch=5, BatchesY=20, BatchesX=26,
               BatchesZ=26, OctreeVoxels=32, OctreesY=100, NearZero=126, OctreesX=128, OctreesZ=128, BatchVoxels=160)
SPAWN = dict(RadiusM=150, DespawnM=250)
STORE = dict(CoalesceMsPerMB=50, CoalesceMs=250, KeepWindowM=1600, MaxMB=2048, GraceMs=10000)
WINDOW = dict(UniverseMin=-35, CraterMin=0, LookAheadBatches=1, RingBatches=5, StreamCenterDownM=8, NearBoxM=16,
              CraterMax=25, UniverseMax=60, NearSurfaceM=150, BiomeRadiusM=200, LookAheadMinSpeedCmS=500)
GROUPS = [("C2S8a", C2S8A), ("C2S8aFlags", C2FLAGS), ("C7S8a", C7S8A), ("C7S8aFlags", C7FLAGS), ("Chart", CHART),
          ("NoticeFlags", NOTICE_FLAGS), ("NoticeKind", NOTICE_KIND), ("NoticeLayer", NOTICE_LAYER), ("Overlay", OVERLAY),
          ("Queue", QUEUE), ("SnWorld", SNWORLD), ("Spawn", SPAWN), ("Store", STORE), ("Window", WINDOW)]

FIELDS = [
    ("s8a", "version", 0x00, 1, "u8"), ("s8a", "flags", 0x01, 1, "u8"), ("s8a", "rowCount", 0x02, 2, "u16"),
    ("s8a", "firstSeq", 0x04, 4, "u32"), ("s8a", "stream", 0x08, 4, "u32"), ("s8a", "dropped", 0x0C, 4, "u32"),
    ("s8a", "tickLow", 0x10, 4, "u32"), ("s8a", "resyncEpoch", 0x14, 4, "u32"), ("s8a", "storeKey", 0x18, 8, "u64"),
    ("s8a", "overlayGen", 0x20, 4, "u32"), ("s8a", "chartSeq", 0x24, 4, "u32"), ("s8a", "cameraBiome", 0x28, 1, "u8"),
    ("s8a", "pad", 0x29, 1, "u8"), ("s8a", "nearPendingCount", 0x2A, 2, "u16"), ("s8a", "seabedBelowCamM", 0x2C, 4, "f32"),
    ("s8a", "windowGen", 0x30, 4, "u32"), ("s8a", "drillGen", 0x34, 4, "u32"), ("s8a", "chartKey", 0x38, 8, "u64"),
    ("notice", "seq", 0x00, 4, "u32"), ("notice", "kind", 0x04, 1, "u8"), ("notice", "layer", 0x05, 1, "u8"),
    ("notice", "flags", 0x06, 2, "u16"), ("notice", "bx", 0x08, 2, "i16"), ("notice", "by", 0x0A, 2, "i16"),
    ("notice", "bz", 0x0C, 2, "i16"), ("notice", "pad", 0x0E, 2, "u16"), ("notice", "version", 0x10, 4, "u32"),
    ("notice", "min", 0x14, 3, "u8[3]"), ("notice", "max", 0x17, 3, "u8[3]"), ("notice", "octrees", 0x1A, 2, "u16"),
    ("notice", "cells", 0x1C, 4, "u32"),
    ("c7s8a", "version", 0x8C, 1, "u8"), ("c7s8a", "flags", 0x8D, 1, "u8"), ("c7s8a", "reloadsPending", 0x8E, 2, "u16"),
    ("c7s8a", "ackSeq", 0x90, 4, "u32"), ("c7s8a", "appliedSeq", 0x94, 4, "u32"), ("c7s8a", "stream", 0x98, 4, "u32"),
    ("c7s8a", "resyncEpoch", 0x9C, 4, "u32"), ("c7s8a", "chartSeqApplied", 0xA0, 4, "u32"),
    ("c7s8a", "batchesServed", 0xA4, 4, "u32"), ("c7s8a", "reloadFailures", 0xA8, 4, "u32"),
    ("c7s8a", "lastLatencyMs", 0xAC, 2, "u16"), ("c7s8a", "nearPendingSeen", 0xAE, 2, "u16"),
    ("c7s8a", "storeKey", 0xB0, 8, "u64"), ("c7s8a", "pad", 0xB8, 4, "u32"),
]

FORMATS = {
    "biome.names": "0 safeShallows, 1 grassyPlateaus, 2 kelpForest, 3 dunes, 4 bloodKelp, 5 sparseReef, 6 mountains, 7 void, 8 mushroomForest, 9 grandReef, 10 underwaterIslands, 11 crashZone, 12 lostRiver_Canyon; 255 none (Subnautica's own answer)",
    "biome.rule": "cameraBiome from the window column under the camera: the planet's biome map (the material map's G, PlanetMaps Biome) at the surface point when it holds 1..13: G - 1; else by the surface material: Grass 6; Sand_02 0 when the seabed is <= 60 m deep, else 1; Stone_01 2; Stone_02 3; Stone_03 4; Stone_04 5; Stone_05 or deeper than 1000 m 7; GetBiome(p) outside the crater box returns names[cameraBiome] within BiomeRadiusM of the camera, else void",
    "chart.anchor": "chart k+1: anchor = the camera's point on the sea sphere; E0', N0' = FrameAt_k(camera), so mapdir is continuous at the camera; psi kept; O' = F_k(camera) - T horizontally (O'.y = 0), so F_k+1(camera) = F_k(camera) - T exactly; a T = 0 step re-centres and keeps the camera's SN position",
    "chart.apply": "SE swaps SiteBinding.Map at the tick it queues the ChartStep notice (C1, C2 and the S8a header of that tick are all under chart k+1); Subnautica never interpolates across a chartSeq change; SE ignores Subnautica-positioned input (C9 positions, C12 batches) while C7 chartSeqApplied != chartSeq",
    "chart.step": "chart 0, TreadmillOn only: site-v1 rhoInf > Chart0OutM -> chart 1 with T = Step0Batches*160 times the sign of the larger of |x|, |z| along that axis; chart k >= 1 with the camera at SN (x, z): |x| > BoxOuterM -> T = StepBatches*160*sign(x) along x, likewise z; |x| < BoxInnerM and |z| < BoxInnerM -> T = -StepBatches*160*sign along the larger axis; arc distance from the anchor > AnchorMaxM -> T = 0; one step per tick, an automatic step no sooner than StepGapMs after the previous one; back to chart 0 when the camera's site-v1 rhoInf <= ReturnM",
    "chart.offsite": "C2 flag OffSite while the chart index is >= 1; SE keeps its picture policy (Subnautica's picture below the altitude band, as on chart 0); Subnautica hides the Subnautica map's above-water landmarks (the Aurora, the crater box's terrain above the sea, the horizon skirt) while OffSite is set and restores them when it clears; on a ChartStep Subnautica serves the previous chart's window files under the shifted ids (new id = old id - T) until the new chart's notices arrive",
    "convert.copyonwrite": "drill layer: SN voxel v is recomputed iff its 8-sample SE stencil intersects an edited SE range of the tile overlay; else it keeps its base value (the newest store version of the batch, else the install file); T = the base type when the base voxel is solid, else the mapped SE material",
    "convert.material": "SE voxel material subtype -> SN block type: Sand_02 1, Stone_01 111, Stone_02 150, Stone_03 190, Stone_04 141, Stone_05 84, Grass 219, Iron_02 95, Nickel_01 96, Silicon_01 107, Cobalt_01 108, Magnesium_01 117, Silver_01 118, Gold_01 119, Platinum_01 121, Uraninite_01 122, any other 84; names resolved to indices once per session",
    "convert.orelook": "Subnautica's palette slots 95, 96, 107, 108, 117, 118, 119, 121 and 122 (null in the installed palette) hold this mod's ore block types for Iron_02, Nickel_01, Silicon_01, Cobalt_01, Magnesium_01, Silver_01, Gold_01, Platinum_01 and Uraninite_01 in that order: a WorldStreamer.Start prefix fills each (filled, the layer of the template rock GreyRock, its material cloned with this mod's generated ore texture); a slot the game's palette already fills is left alone (the ore then shows that type)",
    "convert.sample": "SN voxel v (world index) -> P = (OriginX, OriginY, OriginZ) + v + 0.5; w = F^-1(P) (site v1 for the drill layer, the current chart for the window); D = trilinear over the 8 LOD0 samples (content/127.5 - 1) around w - PositionLeftBottomCorner; s = clamp(D, -1, 1); |s| >= 1: density 0, type = s > 0 ? T : 0; else density = floor(s*126) + 126, type = s >= 0 ? T : 0",
    "feed.ack": "rows = SE's unacknowledged notices from C7 ackSeq + 1 of the same stream, seq consecutive from firstSeq, at most MaxPerTick new rows per tick and MaxRows in all; Subnautica applies seq > its last in order, writes ackSeq (reload scheduled) and appliedSeq (batch loaded and the collision level built over the row's box); tickLow = low 32 bits of the C2 tick; stream changes when seq restarts",
    "feed.resync": "a full queue (MaxPending) drops the oldest unacknowledged notices into dropped, bumps resyncEpoch and sets Resync; Subnautica then re-reads both manifests and reloads every loaded batch whose served version differs; StoreReset (new storeKey, new overlay or new chartKey) clears that layer's lookup first",
    "guard.lift": "while DrillOn every S4 edit guard lets the edit through on a tile: it lands in MyOctreeStorage's own leaves over the provider, never in the provider; each edited range is mirrored by WriteRange into every other tile containing it and marks the overlay cells it touches; DrillOn off = S4's guards",
    "octree.batch": "int32 version 4; octrees x-outer, y, z-inner; crater ids store only octrees inside (OctreesX, OctreesY, OctreesZ), window ids all 125; each a u16 node count then 4-byte nodes (type, density, u16 firstChild, 0 = leaf); child c = 4*xbit + 2*ybit + zbit",
    "octree.write": "32^3 raster -> nodes bottom-up: CanCollapse (8 leaf children, not mixed below/above the surface, near children all equal) -> a leaf with the first near child's value, else child 0's; otherwise interior = CacheDownsampledType (density = integer mean of near children, else 0; type = ComputeMostCommonChildType); layout CompactOctree.SetInternal (pre-order, 8-node blocks from 1)",
    "overlay.file": "DE_S8a_overlay.vx2 in the SE save = deflate(magic DES8AOV1, u32 version, u32 siteVersion, u32 cellCount, cells sorted by key: u8 i, j, k, pad, u8 cx, cy, cz, pad, content[4096], material[4096] x fastest) + u64 fnv1a64 of the uncompressed bytes; cell = 16^3 LOD0 voxels of tile (i, j, k)",
    "overlay.persist": "postfix MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>) when it returns true: snapshot.CompressedVoxelSnapshots[DE_S8a_overlay] = the file; restored in BeforeStart after the tiles by WriteRange(Content|Material, notify) under the restore flag",
    "sculpt.encode": "heightmap h = clamp(round((60000.00390625 + y - 57000) / 10200 * 65535), 0, 65535) for an SE surface at SN altitude y; pixel p of a 2048 face <-> texcoord p / 2047; faces 0 front, 1 back, 2 left, 3 right, 4 up, 5 down (MyCubemapHelpers); _mat R = CustomMaterialTable value (Sand_02 1 .. Stone_05 6, Grass 7), G = biome.names index + 1 (0 none), B = OreMappings Value (0 none), A = 255; PlanetMaps Material, Ores and Biome on",
    "sculpt.site": "every pixel whose site-v1 rhoInf <= 2450 m is 0, so the bicubic surface is exactly SN y -3000 within rhoInf 2300 m (the provider tiles); tools/planet/layout_4546b.json shapes the rest once (biome seeds scaled x3 to a 12 km ring, per-biome depth bands and relief, canyons, the Mountains island, the ore depth-rarity table; a seed varies the detail layer only); every surface outside the island stays at least 2 m below the sea, the island tops at most +70 m",
    "spawn.place": "window columns only, keyed by fnv1a64(chartKey, bx, bz, column, row index); seabed = the top solid voxel of the served file; within RadiusM of the camera, gone beyond DespawnM; MakeEntityTransient, never saved; moved by -T on a chart step",
    "spawn.table": "per hectare of seabed by depth d, Subnautica TechTypes: d <= 60 Peeper 3 Bladderfish 3 Boomerang 2 BluePalm 6 PurpleTentacle 4 CoralShellPlate 3; d <= 200 Hoopfish 3 Spadefish 2 Reginald 2 RedGrass 8 PurpleFan 3 SmallKoosh 2; d <= 500 Eyeye 2 HoleFish 2 Mesmer 1 BloodGrass 6; d <= 1000 Spinefish 2 GarryFish 1 BloodGrass 2; deeper none",
    "store.key": "storeKey = fnv1a64(utf8 lower(world folder path at load) + '|' + siteVersion); chartKey = fnv1a64(utf8 'chart|' + U0, N0, psiDeg, O as R round-trip doubles joined by '|' + '|' + sculptHash x16); sculptHash = fnv1a64 over 'name<TAB>bytes<TAB>fnv1a64 x16<LF>' of the 12 planet data files in ordinal order; with the ring field on (Stage F2) the window's and the charts' sculptHash is fnv1a64(utf8 sculptHash x16 + '|ring|' + ringId x16), ringId = fnv1a64(utf8 'ring|' + rules x16 + '|' + library x16 + '|' + index x16): rules = fnv1a64 of the ring rules text (RingRules.Text), library and index = fnv1a64 of library-v1.bin and index-v1.tsv in the brush store ('missing' for a file not built)",
    "ring.field": "Stage F2 (DEEPENGINEERS_S8A_RING=on): MyPlanetStorageProvider.ReadRange(ref MyVoxelDataRequest, bool) of the site planet serves the ring field (RingField version 2, twin tools/planet/s8a_ring3d.py) for content reads at LOD <= 3 whose box meets SN radius 3530..12270 m and the field's reach over and under the heightmap seabed; content = RingField.ServeBox at the planet-local voxel positions (storage position * 2^lod - StorageSize / 2, site v1 with C = 0, the arctan a polynomial), materials SE's own; every other read, a failure and a field not ready: SE's own generator (the heightmap), which is also the field's base",
    "store.manifest": "manifest.v1.txt per layer folder: line 1 'de-s8a-store 1 <drill|window> <key x16> <overlayHash|sculptHash x16>'; then '<bx> <by> <bz> <version> <bytes> <fnv1a64 x16> <flags> <file path relative to the terrain folder>' per served batch, sorted by bx, by, bz; rewritten at most once per second and at unload (temp file then replace)",
    "store.path": "%LOCALAPPDATA%\\DeepEngineers\\terrain\\<storeKey x16>\\drill\\ and %LOCALAPPDATA%\\DeepEngineers\\terrain\\<storeKey x16>\\window\\<chartKey x16>\\, files compiled-batch-<bx>-<by>-<bz>.v<version>.optoctrees and manifest.v1.txt; a version is never overwritten; nothing inside either game install or SNAppData",
    "window.center": "BatchOctreesStreamer constructor prefix, minLod 0 only: settings.centerMin = (UniverseMin, 0, UniverseMin), settings.centerMax = (UniverseMax, 20, UniverseMax); octreeBounds unchanged",
    "window.loader": "BatchOctrees.LoadOctrees prefix for ids outside the crater box (bx or bz outside CraterMin..CraterMax): read GetPath(id), version >= FileVersion, all 125 octrees in file order (Octree.Read, then UnloadChildren(streamer.minLod)); a missing or bad file answers false (empty water); crater ids run the original",
    "window.ready": "NearPending while a window id meeting the camera's +-NearBoxM box is neither written nor known empty and SE's surface is within NearSurfaceM of the camera; Subnautica's areaBuilt = its own value and not NearPending and every window notice meeting that box applied; NearEmpty (all such ids empty or absent) lets areaBuilt ignore unbuilt empty clipmap cells",
    "window.set": "c = the octree ring centre as WorldStreamer.UpdateStreamingCenter computes it (camera - StreamCenterDownM in y, FloorDiv 160, clamped to the widened centre); ids within RingBatches^3 around c plus LookAheadBatches ahead along the horizontal velocity when |v| >= LookAheadMinSpeedCmS; crater ids excluded; nearest first",
}

def canonical_text():
    consts = []
    for g, d in GROUPS:
        for name, v in d.items():
            consts.append((g, name, v))
    consts.sort(key=lambda e: (e[0].encode("ascii"), e[2], e[1].encode("ascii")))
    lines = [HEADER]
    lines += ["const %s %s %d" % (g, n, v) for g, n, v in consts]
    lines += ["field %s %s 0x%x %d %s" % f for f in FIELDS]
    lines += sorted(("format %s %s" % (k, v) for k, v in FORMATS.items()), key=lambda s: s.encode("ascii"))
    return "\n".join(lines) + "\n"

def fnv1a64(data):
    h = 0xcbf29ce484222325
    for b in data:
        h = ((h ^ b) * 0x100000001b3) & M64
    return h

def fnv_utf8(s):
    return fnv1a64(s.encode("utf-8"))

def contract_block():
    if not os.path.exists(CONTRACT):
        return None
    doc = open(CONTRACT, "r", encoding="utf-8").read().replace("\r\n", "\n")
    i = doc.find("```\n" + HEADER + "\n")
    if i < 0:
        return None
    i += 4
    j = doc.find("```", i)
    return doc[i:j]

# =====================================================================================================================
# doubles in vectors: IEEE bits as 16 hex digits
# =====================================================================================================================

def dbits(x):
    return "%016x" % struct.unpack("<Q", struct.pack("<d", float(x)))[0]

def bitsd(h):
    return struct.unpack("<d", struct.pack("<Q", int(h, 16)))[0]

def r_text(v):
    """S8aChart.R: G15 when it round-trips, else G17; zero '0';.NET exponent style (E+XX / E-XX)."""
    if v == 0.0:
        return "0"
    if math.isnan(v):
        return "NaN"
    if math.isinf(v):
        return "Infinity" if v > 0 else "-Infinity"
    s = "%.15g" % v
    if float(s) != v:
        s = "%.17g" % v
    return s.replace("e", "E")

# =====================================================================================================================
# conversion rules (S8aConvert)
# =====================================================================================================================
ORIGIN = (SNWORLD["OriginX"], SNWORLD["OriginY"], SNWORLD["OriginZ"])

def content_to_d(c):
    return c / 127.5 - 1.0

def encode_sample(s, t):
    if math.isnan(s):
        s = -1.0
    s = max(-1.0, min(1.0, s))
    if s >= 1.0 or s <= -1.0:
        return (t if s > 0 else 0), 0
    d = int(math.floor(s * 126.0)) + 126
    return (t if s >= 0 else 0), d

def sample_centre(vx, vy, vz, off=0.5):
    return (ORIGIN[0] + vx + off, ORIGIN[1] + vy + off, ORIGIN[2] + vz + off)

MATERIALS = (("Sand_02", 1), ("Stone_01", 111), ("Stone_02", 150), ("Stone_03", 190), ("Stone_04", 141),
             ("Stone_05", 84), ("Grass", 219),
             # Stage C (ext s8a terrain 2): the planet's ore rows (S8aMaterialMap, census-picked types
 # ext s8a terrain 6: this mod's own ore block types in the palette's null slots (format convert.orelook)
             ("Iron_02", 95), ("Nickel_01", 96), ("Silicon_01", 107), ("Cobalt_01", 108), ("Magnesium_01", 117),
             ("Silver_01", 118), ("Gold_01", 119), ("Platinum_01", 121), ("Uraninite_01", 122))
BIOME_NAMES = ("safeShallows", "grassyPlateaus", "kelpForest", "dunes", "bloodKelp", "sparseReef", "mountains", "void",
               "mushroomForest", "grandReef", "underwaterIslands", "crashZone", "lostRiver_Canyon")

def sn_type(name):
    for n, t in MATERIALS:
        if n == name:
            return t
    return 84

def biome_of(name, depth):
    if name is None or math.isnan(depth):
        return 255
    if depth > 1000.0:
        return 7
    return {"Grass": 6, "Sand_02": 0 if depth <= 60.0 else 1, "Stone_01": 2, "Stone_02": 3, "Stone_03": 4,
            "Stone_04": 5}.get(name, 7)

def biome_from_map(g):
    """biome.rule's biome-map half (Stage C): G 1..13 -> G - 1, else 255 (the material rule decides)."""
    return g - 1 if 1 <= g <= len(BIOME_NAMES) else 255

def camera_biome(name, depth, g=0):
    b = biome_from_map(g)
    return b if b != 255 else biome_of(name, depth)

def floor_div(a, b):
    return a // b

# =====================================================================================================================
# site map (C# SiteMap operation for operation, sqrt instead of hypot) and charts (S8aChart)
# =====================================================================================================================

def v_add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def v_sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def v_mul(a, k): return (a[0] * k, a[1] * k, a[2] * k)
def v_dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def v_cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def v_len(a): return math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])

def v_norm(a):
    ln = v_len(a)
    return (a[0] / ln, a[1] / ln, a[2] / ln)

class Site:
    def __init__(self, C, Rs, U0, N0, psi_deg, O):
        self.C, self.Rs = C, Rs
        self.U0 = v_norm(U0)
        n = v_sub(N0, v_mul(self.U0, v_dot(N0, self.U0)))
        self.N0 = v_norm(n)
        self.E0 = v_cross(self.N0, self.U0)
        self.psi = psi_deg * (math.pi / 180.0)
        self.O = O
        self.c, self.s = math.cos(self.psi), math.sin(self.psi)

    def _yaw(self, x, z, sign):
        c, s = self.c, (self.s if sign > 0 else -self.s)
        return c * x + s * z, -s * x + c * z

    def se_to_sn(self, P):
        v = v_sub(P, self.C)
        le, lu, ln = v_dot(v, self.E0), v_dot(v, self.U0), v_dot(v, self.N0)
        h = math.sqrt(le * le + ln * ln)
        r = math.sqrt(le * le + lu * lu + ln * ln)
        theta = math.atan2(h, lu)
        rho = self.Rs * theta
        x = z = 0.0
        if h > 0:
            x, z = rho * le / h, rho * ln / h
        xy, zy = self._yaw(x, z, +1)
        return (xy + self.O[0], r - self.Rs + self.O[1], zy + self.O[2])

    def sn_to_se(self, Q):
        x, z = self._yaw(Q[0] - self.O[0], Q[2] - self.O[2], -1)
        y = Q[1] - self.O[1]
        rho = math.sqrt(x * x + z * z)
        theta = rho / self.Rs
        r = self.Rs + y
        t = v_add(v_mul(self.E0, x / rho), v_mul(self.N0, z / rho)) if rho > 0 else (0.0, 0.0, 0.0)
        u = v_add(v_mul(self.U0, math.cos(theta)), v_mul(t, math.sin(theta)))
        return v_add(self.C, v_mul(u, r))

    def frame_at(self, P):
        v = v_sub(P, self.C)
        r = v_len(v)
        u = v_mul(v, 1.0 / r)
        le, ln = v_dot(v, self.E0), v_dot(v, self.N0)
        h = math.sqrt(le * le + ln * ln)
        if h == 0:
            return self.E0, u, self.N0
        t = v_add(v_mul(self.E0, le / h), v_mul(self.N0, ln / h))
        theta = math.atan2(h, v_dot(v, self.U0))
        c, s = math.cos(theta), math.sin(theta)
        k = v_add(v_mul(t, c - 1.0), v_mul(self.U0, -s))
        e = v_add(self.E0, v_mul(k, v_dot(self.E0, t)))
        n = v_add(self.N0, v_mul(k, v_dot(self.N0, t)))
        return e, u, n

TEST_C = (-1000000.0, 250000.0, 1500000.0)
RS = 60000.00390625

def site_v1(C=TEST_C):
    return Site(C, RS, (0.0, 0.0, 1.0), (0.0, -1.0, 0.0), 0.0, (0.0, 0.0, 0.0))

def chart_step(k, cam_se, tx, tz):
    e, u, n = k.frame_at(cam_se)
    fk = k.se_to_sn(cam_se)
    psi_deg = k.psi * (180.0 / math.pi)
    o = (fk[0] - tx * 160.0, 0.0, fk[2] - tz * 160.0)
    return Site(k.C, k.Rs, u, n, psi_deg, o)

def _sign(v):
    return -1 if v < 0 else 1

def chart_decide(chart_seq, cam_k, anchor_k, cam_site, treadmill):
    """-> (tx, tz, toSite, step, reason) as S8aChart.Decide."""
    site_rho = max(abs(cam_site[0]), abs(cam_site[2]))
    if chart_seq == 0:
        if not treadmill or not site_rho > CHART["Chart0OutM"]:
            return 0, 0, False, False, ""
        if abs(cam_site[0]) >= abs(cam_site[2]):
            return CHART["Step0Batches"] * _sign(cam_site[0]), 0, False, True, "site-out"
        return 0, CHART["Step0Batches"] * _sign(cam_site[2]), False, True, "site-out"
    if not treadmill:
        return 0, 0, True, False, "treadmill-off"
    if site_rho <= CHART["ReturnM"]:
        return 0, 0, True, False, "return"
    x, z = cam_k[0], cam_k[2]
    ax, az = abs(x), abs(z)
    if ax > CHART["BoxOuterM"] or az > CHART["BoxOuterM"]:
        tx = CHART["StepBatches"] * _sign(x) if ax > CHART["BoxOuterM"] else 0
        tz = CHART["StepBatches"] * _sign(z) if az > CHART["BoxOuterM"] else 0
        reason = "outer-xz" if tx and tz else ("outer-x" if tx else "outer-z")
        return tx, tz, False, True, reason
    if ax < CHART["BoxInnerM"] and az < CHART["BoxInnerM"]:
        if ax >= az:
            return -CHART["StepBatches"] * _sign(x), 0, False, True, "inner"
        return 0, -CHART["StepBatches"] * _sign(z), False, True, "inner"
    dx, dz = x - anchor_k[0], z - anchor_k[2]
    if math.sqrt(dx * dx + dz * dz) > CHART["AnchorMaxM"]:
        return 0, 0, False, True, "anchor"
    return 0, 0, False, False, ""

def chart_key_text(u0, n0, psi_deg, o, sculpt_hash):
    vals = list(u0) + list(n0) + [psi_deg] + list(o)
    return "chart|" + "|".join(r_text(v) for v in vals) + "|" + "%016x" % sculpt_hash

def chart_key(u0, n0, psi_deg, o, sculpt_hash):
    return fnv_utf8(chart_key_text(u0, n0, psi_deg, o, sculpt_hash))

# =====================================================================================================================
# window plan (S8aWindowPlan)
# =====================================================================================================================
f32 = np.float32

def in_crater_box(bx, bz):
    return WINDOW["CraterMin"] <= bx <= WINDOW["CraterMax"] and WINDOW["CraterMin"] <= bz <= WINDOW["CraterMax"]

def is_window_id(bx, by, bz):
    return (WINDOW["UniverseMin"] <= bx <= WINDOW["UniverseMax"] and WINDOW["UniverseMin"] <= bz <= WINDOW["UniverseMax"]
            and 0 <= by < SNWORLD["BatchesY"] and not in_crater_box(bx, bz))

def _clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v

def stream_centre(cam):
    wx, wy, wz = f32(cam[0]), f32(cam[1]) + f32(-WINDOW["StreamCenterDownM"]), f32(cam[2])
    lx, ly, lz = wx - f32(ORIGIN[0]), wy - f32(ORIGIN[1]), wz - f32(ORIGIN[2])
    px, py, pz = int(lx), int(ly), int(lz)                          # truncation toward zero, as (int) in C#
    cx, cy, cz = px // 160, py // 160, pz // 160                    # Int3.FloorDiv
    return (_clamp(cx, WINDOW["UniverseMin"], WINDOW["UniverseMax"]), _clamp(cy, 0, SNWORLD["BatchesY"]),
            _clamp(cz, WINDOW["UniverseMin"], WINDOW["UniverseMax"]))

def batch_of(p):
    vx, vy, vz = (math.floor(p[i] - ORIGIN[i]) for i in range(3))
    return vx // 160, vy // 160, vz // 160

def batch_bounds(i):
    mn = tuple(ORIGIN[a] + 160.0 * i[a] for a in range(3))
    return mn, tuple(m + 160.0 for m in mn)

def box_d2(p, i):
    mn, mx = batch_bounds(i)
    s = 0.0
    for a in range(3):
        d = mn[a] - p[a] if p[a] < mn[a] else (p[a] - mx[a] if p[a] > mx[a] else 0.0)
        s += d * d
    return s

def centre_d2(p, i):
    mn, _ = batch_bounds(i)
    d = [p[a] - (mn[a] + 80.0) for a in range(3)]
    return d[0] * d[0] + d[1] * d[1] + d[2] * d[2]

def _ring(c, ids):
    h = WINDOW["RingBatches"] // 2
    for x in range(c[0] - h, c[0] - h + WINDOW["RingBatches"]):
        for y in range(c[1] - h, c[1] - h + WINDOW["RingBatches"]):
            for z in range(c[2] - h, c[2] - h + WINDOW["RingBatches"]):
                if is_window_id(x, y, z) and (x, y, z) not in ids:
                    ids.append((x, y, z))

SHARE = 0.38268343236508978

def plan(cam, vel):
    c = stream_centre(cam)
    ids = []
    _ring(c, ids)
    vx, vz = vel[0], vel[2]
    speed = math.sqrt(vx * vx + vz * vz)
    if speed * 100.0 >= WINDOW["LookAheadMinSpeedCmS"]:
        sx = (-1 if vx < 0 else 1) if abs(vx) >= SHARE * speed else 0
        sz = (-1 if vz < 0 else 1) if abs(vz) >= SHARE * speed else 0
        if sx or sz:
            L = WINDOW["LookAheadBatches"]
            a = (_clamp(c[0] + sx * L, WINDOW["UniverseMin"], WINDOW["UniverseMax"]), c[1],
                 _clamp(c[2] + sz * L, WINDOW["UniverseMin"], WINDOW["UniverseMax"]))
            if a != c:
                _ring(a, ids)
    ids.sort(key=lambda i: (box_d2(cam, i), centre_d2(cam, i), i[0], i[1], i[2]))
    return ids

def near_box(cam):
    r = WINDOW["NearBoxM"]
    lo = batch_of((cam[0] - r, cam[1] - r, cam[2] - r))
    hi = batch_of((cam[0] + r, cam[1] + r, cam[2] + r))
    return [(x, y, z) for x in range(lo[0], hi[0] + 1) for y in range(lo[1], hi[1] + 1) for z in range(lo[2], hi[2] + 1)
            if is_window_id(x, y, z)]

# =====================================================================================================================
# feed codec and queue (S8aFeed)
# =====================================================================================================================
NOTICE_FMT = "<IBBHhhhHI3B3BHI"        # 32 bytes
HEADER_FMT = "<BBHIIIIIQIIBBHfIIQ"     # 64 bytes
C7_FMT = "<BBHIIIIIIIHHQI"             # 48 bytes
assert struct.calcsize(NOTICE_FMT) == 32 and struct.calcsize(HEADER_FMT) == 64 and struct.calcsize(C7_FMT) == 48

def notice(seq=0, kind=1, layer=1, flags=0, bx=0, by=0, bz=0, version=0, mn=(0, 0, 0), mx=(0, 0, 0), octrees=0, cells=0):
    return dict(seq=seq, kind=kind, layer=layer, flags=flags, bx=bx, by=by, bz=bz, version=version, mn=tuple(mn),
                mx=tuple(mx), octrees=octrees, cells=cells)

def pack_notice(n):
    return struct.pack(NOTICE_FMT, n["seq"], n["kind"], n["layer"], n["flags"], n["bx"], n["by"], n["bz"], 0,
                       n["version"], *n["mn"], *n["mx"], n["octrees"], n["cells"])

def unpack_notice(b):
    v = struct.unpack(NOTICE_FMT, b)
    return notice(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[8], v[9:12], v[12:15], v[15], v[16])

def pack_header(h):
    return struct.pack(HEADER_FMT, h.get("version", 0), h.get("flags", 0), h.get("rowCount", 0), h.get("firstSeq", 0),
                       h.get("stream", 0), h.get("dropped", 0), h.get("tickLow", 0), h.get("resyncEpoch", 0),
                       h.get("storeKey", 0), h.get("overlayGen", 0), h.get("chartSeq", 0), h.get("cameraBiome", 0), 0,
                       h.get("nearPendingCount", 0), h.get("seabedBelowCamM", 0.0), h.get("windowGen", 0),
                       h.get("drillGen", 0), h.get("chartKey", 0))

def pack_c7(c):
    return struct.pack(C7_FMT, c.get("version", 0), c.get("flags", 0), c.get("reloadsPending", 0), c.get("ackSeq", 0),
                       c.get("appliedSeq", 0), c.get("stream", 0), c.get("resyncEpoch", 0), c.get("chartSeqApplied", 0),
                       c.get("batchesServed", 0), c.get("reloadFailures", 0), c.get("lastLatencyMs", 0),
                       c.get("nearPendingSeen", 0), c.get("storeKey", 0), 0)

def notice_valid(n):
    k, l = n["kind"], n["layer"]
    if k < 1 or k > 4:
        return False
    if k in (1, 2, 4):
        return l in (1, 2)
    return l == 0

class FeedWriter:
    """S8aFeedWriter's rules: seq from 1; rows = oldest unacknowledged, <= MaxRows, <= MaxPerTick never sent before; a
 full queue drops every queued notice into dropped, bumps resyncEpoch and raises Resync until echoed."""

    def __init__(self, stream):
        self.stream, self.q, self.next_seq, self.sent_up_to = stream, [], 1, 0
        self.dropped, self.resync_epoch, self.resync_pending = 0, 0, False

    def enqueue(self, n):
        if not notice_valid(n):
            return None
        if len(self.q) == QUEUE["MaxPending"]:
            self.dropped += len(self.q)
            self.q = []
            self.resync_epoch += 1
            self.resync_pending = True
        n = dict(n)
        n["seq"] = self.next_seq
        self.next_seq = (self.next_seq + 1) & 0xFFFFFFFF
        self.q.append(n)
        return n["seq"]

    def write(self, tick, state, write_rows=True):
        rows = []
        if write_rows and self.q:
            first = self.q[0]["seq"]
            already = 0
            if self.sent_up_to != 0 and ((self.sent_up_to - first) & 0xFFFFFFFF) < 0x80000000:
                already = min(((self.sent_up_to - first) & 0xFFFFFFFF) + 1, len(self.q))
            n = min(len(self.q), C2S8A["MaxRows"], already + C2S8A["MaxPerTick"])
            rows = self.q[:n]
            last = rows[-1]["seq"]
            if self.sent_up_to == 0 or ((last - self.sent_up_to) & 0xFFFFFFFF) - 1 < 0x7FFFFFFF:
                self.sent_up_to = last
        h = dict(state)
        h["version"] = 1
        h["rowCount"] = len(rows)
        h["firstSeq"] = self.q[0]["seq"] if self.q else self.next_seq
        h["stream"] = self.stream
        h["dropped"] = self.dropped
        h["tickLow"] = tick & 0xFFFFFFFF
        h["resyncEpoch"] = self.resync_epoch
        f = h.get("flags", 0)
        h["flags"] = (f | C2FLAGS["Resync"]) if self.resync_pending else (f & ~C2FLAGS["Resync"] & 0xFF)
        return pack_header(h), b"".join(pack_notice(r) for r in rows)

    def ack(self, stream, ack_seq, epoch=None):
        if stream != self.stream:
            return
        while self.q and ((ack_seq - self.q[0]["seq"]) & 0xFFFFFFFF) < 0x80000000:
            self.q.pop(0)
        if epoch is not None and self.resync_pending and epoch == self.resync_epoch:
            self.resync_pending = False

def feed_scenario():
    """The scripted feed both twins run (S8aFeedTests.Scenario_MatchesTheTwin): -> list of (label, header bytes, rows bytes)."""
    out = []
    w = FeedWriter(7)
    st = dict(flags=C2FLAGS["DrillOn"] | C2FLAGS["WindowOn"], storeKey=0x1122334455667788, overlayGen=3, chartSeq=0,
              cameraBiome=255, nearPendingCount=2, seabedBelowCamM=42.5, windowGen=9, drillGen=4, chartKey=0x0A0B0C0D0E0F1011)
    for i in range(100):
        w.enqueue(notice(kind=1, layer=1 + (i % 2), flags=i % 3, bx=i - 40, by=i % 20, bz=60 - i, version=i + 1,
                         mn=(i % 160, 0, 3), mx=(159, i % 160, 150), octrees=i % 126, cells=i * 1000))
    out.append(("t1",) + w.write(1000, st))                    # 64 new rows
    out.append(("t2",) + w.write(1001, st))                    # 100 rows (64 + 36 new)
    w.ack(7, 50)
    out.append(("t3-ack50",) + w.write(1002, st))
    w.ack(8, 90)                                               # wrong stream: ignored
    out.append(("t4-wrongstream",) + w.write((1 << 32) + 5, st))
    for i in range(4096):                                      # 50 queued + 4,096 -> overflow at the 4,047th
        w.enqueue(notice(kind=2, layer=2, bx=-35 + i % 96, by=i % 20, bz=60 - i % 96, version=i + 1000, mn=(0, 0, 0),
                         mx=(159, 159, 159)))
    w.enqueue(notice(kind=3, layer=0, bx=24, by=0, bz=-24, version=5))
    w.enqueue(notice(kind=4, layer=1, version=77))
    out.append(("t5-overflow",) + w.write(2000, st))
    w.ack(7, w.q[0]["seq"] + 9, epoch=0)                         # wrong epoch: Resync stays
    out.append(("t6-epoch0",) + w.write(2001, st))
    w.ack(7, w.q[0]["seq"] + 19, epoch=w.resync_epoch)
    out.append(("t7-echo",) + w.write(2002, dict(st, flags=st["flags"] | C2FLAGS["Resync"])))
    out.append(("t8-norows",) + w.write(2003, st, write_rows=False))
    return out

# =====================================================================================================================
# store keys, names, manifest, overlay (S8aStore)
# =====================================================================================================================

def store_key(world_path, site_version):
    return fnv_utf8(world_path.lower() + "|" + str(site_version))

def batch_file_name(bx, by, bz, version):
    return "compiled-batch-%d-%d-%d.v%d.optoctrees" % (bx, by, bz, version)

def rel_drill(store, bx, by, bz, v):
    return "%016x\\drill\\%s" % (store, batch_file_name(bx, by, bz, v))

def rel_window(store, chart, bx, by, bz, v):
    return "%016x\\window\\%016x\\%s" % (store, chart, batch_file_name(bx, by, bz, v))

def manifest_text(layer, key, h, entries):
    lines = ["de-s8a-store 1 %s %016x %016x" % (layer, key, h)]
    for e in sorted(entries, key=lambda e: (e[0], e[1], e[2])):
        lines.append("%d %d %d %d %d %016x %d %s" % e)
    return "\n".join(lines) + "\n"

def sculpt_hash(files):
    """files: list of (name, bytes, fnv)."""
    s = "".join("%s\t%d\t%016x\n" % f for f in sorted(files, key=lambda f: f[0].encode("utf-8")))
    return fnv_utf8(s)

def overlay_cells(seed, count):
    """Deterministic cells (S8aStoreTests regenerates them): splitmix64; per cell keys from one draw, then 4,096 content
 and 4,096 material bytes, each the low byte of one draw."""
    rng = SplitMix64(seed)
    cells = {}
    while len(cells) < count:
        r = rng.next()
        key = (r & 3, (r >> 2) & 3, (r >> 4) & 1, (r >> 8) & 63, (r >> 16) & 63, (r >> 24) & 63)
        content = bytes(rng.next() & 0xFF for _ in range(4096))
        material = bytes(rng.next() & 0xFF for _ in range(4096))
        cells.setdefault(key, (content, material))
    return cells

def overlay_payload(site_version, cells):
    out = bytearray(b"DES8AOV1")
    out += struct.pack("<III", OVERLAY["Version"], site_version, len(cells))
    for key in sorted(cells):
        i, j, k, cx, cy, cz = key
        out += bytes((i, j, k, 0, cx, cy, cz, 0))
        out += cells[key][0]
        out += cells[key][1]
    return bytes(out)

def overlay_file(site_version, cells):
    p = overlay_payload(site_version, cells)
    co = zlib.compressobj(9, zlib.DEFLATED, -15)
    body = co.compress(p) + co.flush()
    return body + struct.pack("<Q", fnv1a64(p)), fnv1a64(p)

def overlay_decode(data):
    stored = struct.unpack("<Q", data[-8:])[0]
    p = zlib.decompress(data[:-8], -15)
    if fnv1a64(p) != stored:
        raise ValueError("hash")
    if p[:8] != b"DES8AOV1":
        raise ValueError("magic")
    ver, site, n = struct.unpack_from("<III", p, 8)
    cells = {}
    off = 20
    for _ in range(n):
        key = (p[off], p[off + 1], p[off + 2], p[off + 4], p[off + 5], p[off + 6])
        cells[key] = (p[off + 8:off + 8 + 4096], p[off + 8 + 4096:off + 8 + 8192])
        off += 8 + 8192
    return ver, site, cells, stored

class SplitMix64:
    def __init__(self, seed):
        self.s = seed & M64

    def next(self):
        self.s = (self.s + 0x9E3779B97F4A7C15) & M64
        z = self.s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & M64
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & M64
        return z ^ (z >> 31)

# =====================================================================================================================
# vectors
# =====================================================================================================================
SAMPLE_S = (-2.0, -1.0, -0.99999, -0.9921, -0.9920634920634921, -0.5, -1e-12, 0.0, 1e-12, 0.0079365, 0.25, 0.5,
            0.999, 0.99999999, 1.0, 1.5, float("nan"))
CENTRES = ((0, 0, 0), (12, 34, 56), (4095, 3199, 4095), (-5600, 2900, 9900), (-1, -1, -1))
BIOME_CASES = (("Grass", 30.0), ("Sand_02", 60.0), ("Sand_02", 60.000001), ("Stone_01", 10.0), ("Stone_02", 999.0),
               ("Stone_03", 1000.0), ("Stone_04", 1000.5), ("Stone_05", 100.0), ("Other_9", 40.0), (None, 10.0),
               ("Grass", float("nan")))
R_VALUES = (1.0, -1.0, 0.5, 0.1, 1.0 / 3.0, 60000.00390625, 1e-5, 1.2345678901234567e-05, 123456789012345680.0,
            -2.5e-300, 6.123233995736766e-17, 0.7071067811865476, 2.0 ** 0.5, 1e15, 1e16, -7040.0, 3200.25)

def chart_cases():
    s = site_v1()
    cases = []
    # cameras (SN under the site map) and the step T in batches
    for cam_sn, t in (((5000.0, -60.0, 120.0), (10, 0)), ((-300.0, -80.0, -5200.0), (0, -10)),
                      ((4700.0, -30.0, 4900.5), (0, 10)), ((2500.0, 20.0, -2600.0), (0, 0))):
        cam_se = s.sn_to_se(cam_sn)
        k1 = chart_step(s, cam_se, t[0], t[1])
        cases.append((cam_se, t, k1))
    return s, cases

def write_vectors(dir_=VEC_DIR):
    os.makedirs(dir_, exist_ok=True)
    files = {}
    # ---- convert.tsv
    L = ["# S8a convert vectors (tools/planet/s8a_ref.py --vectors); doubles as IEEE bits (16 hex)"]
    for s in SAMPLE_S:
        for t in (0, 1, 150):
            ot, od = encode_sample(s, t)
            L.append("sample\t%s\t%d\t%d\t%d" % (dbits(s), t, ot, od))
    for c in range(256):
        L.append("content\t%d\t%s" % (c, dbits(content_to_d(c))))
    for v in CENTRES:
        for off in (0.5, 0.0):
            p = sample_centre(*v, off=off)
            L.append("centre\t%d\t%d\t%d\t%s\t%s\t%s\t%s" % (v + (dbits(off),) + tuple(dbits(x) for x in p)))
    for name, t in MATERIALS + (("Stone", 84), ("Ice_01", 84), ("sand_02", 84)):
        L.append("material\t%s\t%d" % (name, sn_type(name)))
    for name, depth in BIOME_CASES:
        L.append("biome\t%s\t%s\t%d" % ("-" if name is None else name, dbits(depth), biome_of(name, depth)))
    files["convert.tsv"] = L
    # ---- feed.tsv
    L = ["# S8a feed vectors: notice/header/c7 bytes (hex) and the scripted writer scenario (label, header hex, rows fnv, rows)"]
    n1 = notice(seq=0x01020304, kind=1, layer=2, flags=0x0105, bx=-35, by=19, bz=60, version=0xA0B0C0D0, mn=(1, 2, 3),
                mx=(157, 158, 159), octrees=125, cells=0xFEDCBA98)
    n2 = notice(seq=9, kind=3, layer=0, bx=24, by=0, bz=-24, version=4)
    L.append("notice\t%s" % pack_notice(n1).hex())
    L.append("notice\t%s" % pack_notice(n2).hex())
    h = dict(version=1, flags=0x2B, rowCount=1024, firstSeq=0xFFFFFFF0, stream=0x01000001, dropped=4096, tickLow=0x89ABCDEF,
             resyncEpoch=3, storeKey=0x8877665544332211, overlayGen=12, chartSeq=2, cameraBiome=7, nearPendingCount=3,
             seabedBelowCamM=-1.5, windowGen=300, drillGen=17, chartKey=0xDEADBEEFCAFEF00D)
    L.append("header\t%s" % pack_header(h).hex())
    c7 = dict(version=1, flags=0x1F, reloadsPending=8, ackSeq=1000, appliedSeq=990, stream=7, resyncEpoch=2,
              chartSeqApplied=1, batchesServed=123456, reloadFailures=3, lastLatencyMs=640, nearPendingSeen=2,
              storeKey=0x0102030405060708)
    L.append("c7\t%s" % pack_c7(c7).hex())
    for label, hb, rb in feed_scenario():
        L.append("scenario\t%s\t%s\t%016x\t%d" % (label, hb.hex(), fnv1a64(rb), len(rb) // 32))
    files["feed.tsv"] = L
    # ---- chart.tsv
    L = ["# S8a chart vectors: R text, chart keys, decisions, steps (doubles as IEEE bits; steps compared to 1e-6 m)"]
    for v in R_VALUES:
        L.append("r\t%s\t%s" % (dbits(v), r_text(v)))
    for u0, n0, psi, o, sh in (((0.0, 0.0, 1.0), (0.0, -1.0, 0.0), 0.0, (0.0, 0.0, 0.0), 0),
                               ((0.0, 0.0, 1.0), (0.0, -1.0, 0.0), 0.0, (0.0, 0.0, 0.0), 0x0123456789ABCDEF),
                               ((0.6, 0.0, 0.8), (0.0, -1.0, 0.0), 12.5, (3200.0, 0.0, -160.0), 0xFFFFFFFFFFFFFFFF),
                               ((-0.0123, 0.0456, 0.99888), (0.1, -0.99, 0.045), -33.25, (-7040.5, 0.0, 1e-5), 42)):
        L.append("key\t%s\t%s\t%s\t%s\t%016x\t%s\t%016x" % (
            ",".join(dbits(x) for x in u0), ",".join(dbits(x) for x in n0), dbits(psi), ",".join(dbits(x) for x in o),
            sh, chart_key_text(u0, n0, psi, o, sh), chart_key(u0, n0, psi, o, sh)))
    for seq, cam_k, anc, cam_site, tm in (
            (0, (4801.0, -50, 0.0), (0, 0, 0), (4801.0, -50, 0.0), True), (0, (4801.0, -50, 0.0), (0, 0, 0), (4801.0, -50, 0.0), False),
            (0, (-100.0, -5, -4900.0), (0, 0, 0), (-100.0, -5, -4900.0), True), (0, (4799.0, 0, 4799.0), (0, 0, 0), (4799.0, 0, 4799.0), True),
            (0, (-5000.0, 0, 5000.0), (0, 0, 0), (-5000.0, 0, 5000.0), True),
            (1, (7041.0, -40, 100.0), (3400.0, 0, 0.0), (9000.0, 0, 100.0), True),
            (1, (-7041.0, -40, -7100.0), (-3400.0, 0, 0.0), (-9000.0, 0, -9100.0), True),
            (2, (3199.0, -40, -100.0), (3500.0, 0, 0.0), (12000.0, 0, 0.0), True),
            (2, (1000.0, -40, -3100.0), (3500.0, 0, 0.0), (12000.0, 0, 0.0), True),
            (2, (5000.0, -40, 3300.0), (1000.0, 0, -500.0), (12000.0, 0, 0.0), True),
            (2, (5000.0, -40, 3300.0), (4000.0, 0, 2000.0), (12000.0, 0, 0.0), True),
            (3, (5000.0, -40, 3300.0), (4000.0, 0, 2000.0), (4100.0, 0, 0.0), True),
            (3, (5000.0, -40, 3300.0), (4000.0, 0, 2000.0), (8000.0, 0, 0.0), False)):
        tx, tz, to_site, step, reason = chart_decide(seq, cam_k, anc, cam_site, tm)
        L.append("decide\t%d\t%s\t%s\t%s\t%d\t%d\t%d\t%d\t%d\t%s" % (
            seq, ",".join(dbits(x) for x in cam_k), ",".join(dbits(x) for x in anc), ",".join(dbits(x) for x in cam_site),
            1 if tm else 0, tx, tz, 1 if to_site else 0, 1 if step else 0, reason or "-"))
    s, cases = chart_cases()
    for cam_se, t, k1 in cases:
        after = k1.se_to_sn(cam_se)
        L.append("step\t%s\t%d\t%d\t%s\t%s\t%s\t%s" % (
            ",".join(dbits(x) for x in cam_se), t[0], t[1], ",".join(dbits(x) for x in k1.U0),
            ",".join(dbits(x) for x in k1.N0), ",".join(dbits(x) for x in k1.O), ",".join(dbits(x) for x in after)))
    files["chart.tsv"] = L
    # ---- window.tsv
    L = ["# S8a window vectors: stream centre, plan (nearest first), near box; cameras and velocities as IEEE bits"]
    cams = ((0.0, -50.0, 0.0), (2200.0, -100.0, 30.0), (-2100.5, -3000.0, 2111.9), (-7700.0, -20.0, 7800.0),
            (-0.4, -12.0, -2048.2), (2112.0, 150.0, -2048.0), (4600.0, -60.0, 0.0), (-3000.25, -500.75, -6000.5))
    vels = ((0.0, 0.0, 0.0), (20.0, 0.0, 0.0), (-3.0, 1.0, 4.5), (-56.0, -10.0, 56.0), (1.0, 0.0, -80.0))
    for cam in cams:
        L.append("centre\t%s\t%d,%d,%d" % ((",".join(dbits(x) for x in cam),) + stream_centre(cam)))
        for vel in vels:
            ids = plan(cam, vel)
            L.append("plan\t%s\t%s\t%s" % (",".join(dbits(x) for x in cam), ",".join(dbits(x) for x in vel),
                                           ";".join("%d,%d,%d" % i for i in ids) or "-"))
        L.append("near\t%s\t%s" % (",".join(dbits(x) for x in cam), ";".join("%d,%d,%d" % i for i in near_box(cam)) or "-"))
    files["window.tsv"] = L
    # ---- store.tsv, manifest.txt, overlay.vx2
    L = ["# S8a store vectors: store keys, file names, relative paths, sculpt hash, manifest, overlay"]
    for path, sv in ((r"C:\Users\user\AppData\Roaming\SpaceEngineers\Saves\76561197960265729\DE Lab S8a", 1),
                     (r"C:\SAVES\Ünïcode Wörld", 1), ("", 0), (r"D:\x", 2)):
        L.append("storekey\t%s\t%d\t%016x" % (path, sv, store_key(path, sv)))
    for bx, by, bz, v in ((0, 7, 25, 1), (-35, 0, 60, 4294967295), (12, 18, 12, 7)):
        L.append("file\t%d\t%d\t%d\t%d\t%s\t%s\t%s" % (bx, by, bz, v, batch_file_name(bx, by, bz, v),
                                                     rel_drill(0x00000000DEADBEEF, bx, by, bz, v),
                                                     rel_window(0x1, 0xFEDCBA9876543210, bx, by, bz, v)))
    sculpt_files = [("%s%s.png" % (f, m), 8209 + i * 100, fnv_utf8(f + m)) for i, (f, m) in
                    enumerate((f, m) for f in ("front", "back", "left", "right", "up", "down") for m in ("", "_mat"))]
    L.append("sculpt\t%s\t%016x" % (";".join("%s,%d,%016x" % f for f in sculpt_files), sculpt_hash(sculpt_files)))
    entries = [(12, 18, 12, 3, 1234567, 0x1111111111111111, 0, rel_drill(0xAB, 12, 18, 12, 3)),
               (-3, 17, 40, 1, 454, 0x2222222222222222, 2, rel_window(0xAB, 0xCD, -3, 17, 40, 1)),
               (0, 7, 25, 2, 8000, 0x3333333333333333, 1, rel_drill(0xAB, 0, 7, 25, 2)),
               (-3, 2, 40, 9, 9, 0x4444444444444444, 16, rel_drill(0xAB, -3, 2, 40, 9))]
    mtext = manifest_text("drill", 0xAB, 0x5555555555555555, entries)
    L.append("manifest\t%016x\t%d" % (fnv_utf8(mtext), len(mtext.encode("utf-8"))))
    cells = overlay_cells(4546, 5)
    payload = overlay_payload(1, cells)
    vx2, h = overlay_file(1, cells)
    L.append("overlay\t4546\t5\t1\t%016x\t%d" % (h, len(payload)))
    L.append("overlay\t1\t0\t1\t%016x\t%d" % (fnv1a64(overlay_payload(1, {})), len(overlay_payload(1, {}))))
    files["store.tsv"] = L
    texts = {k: "\n".join(v) + "\n" for k, v in files.items()}
    texts["manifest.txt"] = mtext
    return texts, vx2

def save_vectors(dir_=VEC_DIR):
    texts, vx2 = write_vectors(dir_)
    for name, t in texts.items():
        with open(os.path.join(dir_, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(t)
    with open(os.path.join(dir_, "overlay.vx2"), "wb") as f:
        f.write(vx2)
    return sorted(list(texts) + ["overlay.vx2"])

# =====================================================================================================================
# selftest
# =====================================================================================================================

def selftest():
    fails = []

    def check(c, what):
        if not c:
            fails.append(what)

    text = canonical_text()
    check(text.encode("ascii") is not None, "ascii")
    check(text.count("\n") == CANON_LINES, "lines %d" % text.count("\n"))
    check(len(text.encode("ascii")) == CANON_BYTES, "bytes %d" % len(text.encode("ascii")))
    check(fnv1a64(text.encode("ascii")) == PINNED_HASH, "hash 0x%016x" % fnv1a64(text.encode("ascii")))
    blk = contract_block()
    if blk is not None:
        check(blk == text, "CONTRACT §5.4 block = the twin's text")
    # sizes and places
    check(C2S8A["RowsOffset"] == C2S8A["Offset"] + C2S8A["HeaderSize"], "rows follow the header")
    check(C2S8A["RowsOffset"] + C2S8A["MaxRows"] * C2S8A["RowSize"] <= 0x80000, "inside the C2 region")
    check(C2S8A["Offset"] >= 0x40000 + 1024 * 64, "past S7's event window")
    check(C7S8A["Offset"] + C7S8A["Size"] <= 0x8C + 0x74, "inside pad_8c")
    # conversion rules
    check(encode_sample(1.0, 5) == (5, 0) and encode_sample(-1.0, 5) == (0, 0), "far at |s| >= 1")
    check(encode_sample(0.0, 5) == (5, 126) and encode_sample(-1e-9, 5) == (0, 125), "the surface at 126")
    check(encode_sample(-0.995, 5) == (0, 0), "below -125/126 a near voxel is far empty")
    check(encode_sample(0.999, 5) == (5, 251), "the top near density 251")
    check(content_to_d(0) == -1.0 and content_to_d(255) == 1.0, "content ends")
    check(sn_type("Stone_02") == 150 and sn_type("Nope") == 84, "material table")
    check(biome_of("Sand_02", 60.0) == 0 and biome_of("Sand_02", 61.0) == 1 and biome_of("Grass", 1001.0) == 7, "biome rule")
    # charts: continuity of the step at the camera and the decision rule
    s, cases = chart_cases()
    for cam_se, t, k1 in cases:
        before = s.se_to_sn(cam_se)
        after = k1.se_to_sn(cam_se)
        check(abs(after[0] - (before[0] - 160.0 * t[0])) < 1e-6 and abs(after[2] - (before[2] - 160.0 * t[1])) < 1e-6
              and abs(after[1] - before[1]) < 1e-6, "F_k+1(cam) = F_k(cam) - T (%r)" % (t,))
        e0, u0, n0 = s.frame_at(cam_se)
        check(max(abs(a - b) for a, b in zip(k1.N0, n0)) < 1e-12 and max(abs(a - b) for a, b in zip(k1.E0, e0)) < 1e-9,
              "frame continuous at the camera")
    check(chart_decide(0, (4801.0, 0, 10.0), (0, 0, 0), (4801.0, 0, 10.0), True)[:2] == (CHART["Step0Batches"], 0), "0 -> 1 along x")
    check(chart_decide(1, (7041.0, 0, -7041.0), (0, 0, 0), (9000.0, 0, 0), True)[:2] == (CHART["StepBatches"], -CHART["StepBatches"]), "outer both axes")
    check(chart_decide(1, (100.0, 0, -200.0), (0, 0, 0), (9000.0, 0, 0), True)[:2] == (0, CHART["StepBatches"]), "inner along z (-StepBatches*sign(-200))")
    check(chart_decide(1, (100.0, 0, -200.0), (0, 0, 0), (4160.0, 0, 0), True)[2], "return at site rho 4160")
    check(r_text(1e-5) == "1E-05" and r_text(0.1) == "0.1" and r_text(-0.0) == "0" and r_text(1.0 / 3.0) == "0.333333333333333"
          or r_text(1.0 / 3.0) == "0.33333333333333331", "R text")
    check(chart_key_text((0.0, 0.0, 1.0), (0.0, -1.0, 0.0), 0.0, (0.0, 0.0, 0.0), 0) ==
          "chart|0|0|1|0|-1|0|0|0|0|0|0000000000000000", "site chart key text")
    # window plan
    check(stream_centre((0.0, -50.0, 0.0)) == (12, 18, 12), "ring centre at the site origin")
    check(stream_centre((-2048.4, 0.0, 0.0))[0] == 0, "truncation: local -0.4 -> 0 -> batch 0")
    check(stream_centre((-2049.0, 0.0, 0.0))[0] == -1, "local -1 -> batch -1")
    ids = plan((0.0, -50.0, 0.0), (0.0, 0.0, 0.0))
    check(ids == [], "no window ids around the site origin (crater box)")
    # window.set + the universe by in [0, BatchesY): a camera 60 m deep centres the ring on by 18, so its top row (by 20)
 # lies above the universe and the ring holds 100 ids
    ids = plan((4600.0, -60.0, 0.0), (0.0, 0.0, 0.0))
    check(len(ids) == 100 and all(not in_crater_box(i[0], i[2]) and 0 <= i[1] < 20 for i in ids),
          "a ring 60 m deep loses its top row (by 20 is above the universe): 100 ids")
    ids = plan((4600.0, -400.0, 0.0), (0.0, 0.0, 0.0))
    check(len(ids) == 125 and all(not in_crater_box(i[0], i[2]) for i in ids), "a full ring beyond the crater (400 m deep)")
    ids2 = plan((4600.0, -400.0, 0.0), (20.0, 0.0, 0.0))
    check(len(ids2) == 150 and set(ids) < set(ids2), "look-ahead adds one column of 25 ids")
    check(ids[0] == batch_of((4600.0, -400.0, 0.0)), "the camera's batch first")
    check(plan((4600.0, -400.0, 0.0), (3.0, 0.0, 3.0)) == ids, "no look-ahead below LookAheadMinSpeedCmS")
    # feed
    sc = feed_scenario()
    hdr = {l: struct.unpack(HEADER_FMT, h) for l, h, r in sc}
    rows = {l: len(r) // 32 for l, h, r in sc}
    check(rows["t1"] == 64 and rows["t2"] == 100 and rows["t3-ack50"] == 50, "MaxPerTick and acks %r" % rows)
    check(hdr["t3-ack50"][3] == 51 and hdr["t4-wrongstream"][6] == 5, "firstSeq after ack; tickLow low 32 bits")
    check(hdr["t5-overflow"][1] & 32 and hdr["t5-overflow"][5] == 4096 and hdr["t5-overflow"][7] == 1, "overflow: dropped, epoch, Resync")
    check(hdr["t6-epoch0"][1] & 32 and not hdr["t7-echo"][1] & 32, "Resync until echoed")
    check(rows["t8-norows"] == 0, "no rows without an S8a Subnautica")
    n = notice(seq=1, kind=1, layer=1, bx=-35, by=0, bz=60, version=2, mn=(1, 2, 3), mx=(4, 5, 6), octrees=7, cells=8)
    check(unpack_notice(pack_notice(n)) == n, "notice round trip")
    # store
    check(store_key("C:\\A", 1) == fnv_utf8("c:\\a|1"), "store key lower + '|' + site version")
    check(batch_file_name(-3, 2, 40, 9) == "compiled-batch--3-2-40.v9.optoctrees", "versioned file name")
    cells = overlay_cells(1, 3)
    data, h = overlay_file(1, cells)
    v, sv, back, h2 = overlay_decode(data)
    check(v == 1 and sv == 1 and back == cells and h == h2, "overlay round trip")
    # vectors current
    texts, vx2 = write_vectors(VEC_DIR)
    for name, t in texts.items():
        p = os.path.join(VEC_DIR, name)
        if os.path.exists(p):
            with open(p, "r", encoding="utf-8", newline="") as f:
                check(f.read() == t, "tests/s8a/vectors/%s is current" % name)
        else:
            fails.append("missing tests/s8a/vectors/%s (run --vectors)" % name)
    for f_ in fails:
        print("FAIL", f_)
    print("s8a_ref selftest:", "PASS" if not fails else "FAIL (%d)" % len(fails))
    return 0 if not fails else 1

def main(argv):
    if "--text" in argv:
        sys.stdout.write(canonical_text())
        return 0
    if "--pin" in argv:
        t = canonical_text().encode("ascii")
        print(HEADER + " hash 0x%016x lines %d bytes %d pinned 0x%016x %s" % (
            fnv1a64(t), t.count(b"\n"), len(t), PINNED_HASH, "PASS" if fnv1a64(t) == PINNED_HASH else "FAIL"))
        blk = contract_block()
        print("CONTRACT block:", "absent" if blk is None else ("PASS" if blk == t.decode("ascii") else "FAIL"))
        return 0 if fnv1a64(t) == PINNED_HASH and (blk is None or blk == t.decode("ascii")) else 1
    if "--vectors" in argv:
        for n in save_vectors():
            print("wrote tests/s8a/vectors/%s" % n)
        return 0
    if "--selftest" in argv:
        return selftest()
    print(__doc__)
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
