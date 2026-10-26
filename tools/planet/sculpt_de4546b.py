"""The designed seabed of the dive planet DE_4546B (S8a Stage C, CONTRACT 3.3 "Sculpt", 5.4 "sculpt.encode", "sculpt.site").

Writes our own planet data (never a game file): six 2048 x 2048 16-bit grayscale heightmaps <face>.png and six
2048 x 2048 RGBA material maps <face>_mat.png for src/DeepEngineers.WorldMod/Data/PlanetDataFiles/DE_4546B, plus the
CustomMaterialTable and OreMappings rows of Data/PlanetGeneratorDefinitions.sbc, from the authored layout
tools/planet/layout_4546b.json (Subnautica's biome layout around the authentic crater, scaled x3 to a 12 km ring, then
the Crater Edge down to the void floor at SN y -3,000) and, beyond a moat, the whole planet (Stage E, layout 'planet': an
ocean floor of 100-1,500 m with shelves, plains, ridges, trenches and island chains on every face; ore fields everywhere at SE's
EarthLike density, rows S8a.C2.globe and S8a.N10.fields). The planet is authored once and shipped in the mod; a --seed
varies the small detail layer only (+-2 m boulders and ripples), never the layout.

Encoding (CONTRACT "sculpt.encode"; VERIFIED in the decompiled SE 1.210.014 and MEASURED with SE's own native voxel
generator, see --native-check):
 * MyPlanetShapeProvider (decomp se/Sandbox.Game/Sandbox.Engine.Voxels.Planet/MyPlanetShapeProvider.cs:252-282,:465-483,:500-512): surface radius = R + bicubic(h) * heightRatio + minHillHeight with R = size / 2 = 60,000
 (MyWorldGenerator.cs:962, MyPlanetStorageProvider.cs:151-165), minHillHeight = HillParams.Min * R = -3,000,
 heightRatio = (Max - Min) * R = 10,200, h = value / 65535 (MyHeightmapFace.cs:229-235; the 16-bit PNG is loaded as
 ImageSharp Gray16 by MyImage.Load, VRage.Render/VRage.Render.Image/MyImage.cs:30-78, and copied row by row,
 MyPlanetTextureMapProvider.cs:220-261); GetValueForPositionCacheless samples texcoord * (Resolution - 1) with a
 Catmull-Rom (Tau 0.5) 4 x 4 patch (319-400). The voxels come from VrPlanetShape (VRage.Native.dll,
 MyPlanetShapeProvider.cs:279,:1102-1107): tools/planet/sculpt_native_check.py runs it on our PNGs and finds the
 same surface within 1.1 m (Stage C step 1). Hence for an SE surface at SN altitude y (sea sphere Rs = 60,000.00390625):
 h = clamp(round((60000.00390625 + y - 57000) / 10200 * 65535), 0, 65535) (round = half up)
 * pixel p of a 2048 face <-> texcoord p / 2047 (heights); the material/biome/ore maps are sampled at pixel
 floor(2048 * texcoord) (MyCubemapData.cs:40-45), which holds texcoord p / 2047 for p < 2047; faces 0 front, 1 back,
 2 left, 3 right, 4 up, 5 down (MyCubemapHelpers.GetNameForFace); MyCubemapHelpers.CalculateSampleTexcoord
 (MyCubemapHelpers.cs:115-190) maps a planet-local direction to (face, texcoord).
 * _mat (Rgba32, MyPlanetTextureMapProvider.cs:332-357: byte 0 R -> material map, 1 G -> biome map, 2 B -> ore map):
 R = CustomMaterialTable value, G = the biome (S8aMaterialMap.BiomeNames index + 1; 0 = none), B = an OreMappings
 Value (0 = no ore), A = 255. SE reads G only with PlanetMaps Biome="true" and B only with Ores="true"
 (274-330); G is inert in SE without MaterialGroups/EnvironmentItems (MyPlanetMaterialProvider.cs:1021-1069) and is
 read back by the window's biome rule; B selects the OreMappings rows of that Value: a voxel whose depth below the
 surface s (metres, LOD 0: SurfaceDepth + 0.5) satisfies Start <= s <= Start + Depth takes the row's Type
 (MyPlanetMaterialProvider.cs:427-452,:749-801).
 * The site anchor U0 = (0, 0, 1) is face 1 (back) at texcoord (0.5, 0.5); site v1: E0 = -X, N0 = -Y, psi 0, O 0.

Site rule (CONTRACT "sculpt.site"): every pixel whose site-v1 rhoInf <= 2,450 m is 0 (the bicubic surface is exactly SN y
-3,000.004 within rhoInf 2,300 m, under the provider tiles); the moat stays at the floor to 2,900 m, the crater wall rises
to the ring by 3,700 m; every surface outside the Mountains island stays at least 2 m below the sea (bicubic included);
the island may rise to +60 m (Grass above -2 m).

Usage (repository root; sculpt_planet.py is the same tool under the roadmap's name):
 python -I tools/planet/sculpt_planet.py --dry-run [--seed N] compute, print the plan, write nothing
 python -I tools/planet/sculpt_planet.py --preview <dir> [--seed N] biome map with legend, depth, materials, ores
 python -I tools/planet/sculpt_planet.py --out <modDir> --sbc [--owner-go] write the 12 PNGs (+ the.sbc rows)
 writing into the repository's world mod needs --owner-go
 python -I tools/planet/sculpt_planet.py --verify <modDir> [--seed N] S8a.C2.sculpt /.flat /.limits /.ores
 python -I tools/planet/sculpt_planet.py --depth-at X Z [--files <modDir>] SE's bicubic seabed (SN y) at SN (X, Z)
 python -I tools/planet/sculpt_planet.py --depth-at-list FILE [--files <modDir>] [--json] the same per line "x z" / JSON [[x, z],...]
 python -I tools/planet/sculpt_planet.py --ore-sites [--json] the ore deposits of the design (centre, depth, size)
 python -I tools/planet/sculpt_planet.py --native-check [<modDir>] S8a.C2.native: SE's own voxel generator on the files
 python -I tools/planet/sculpt_planet.py --check-world <save> read-only: grids the new seabed would bury
 python -I tools/planet/sculpt_planet.py --selftest small-resolution checks (seconds)
 --layout FILE overrides tools/planet/layout_4546b.json; --res N for tests; --files defaults to the repository's world mod.

numpy (as tools/sn-octrees); PNGs written with zlib (filter 0, level 9, no ancillary chunks: deterministic). The preview's
legend uses Pillow when installed (previews only; the planet files never do).
"""
import argparse
import copy
import json
import math
import os
import random
import re
import shutil
import struct
import sys
import tempfile
import time
import zlib

import numpy as np

# ------------------------------------------------------------------------------------------------- constants
SUBTYPE = "DE_4546B"
FACES = ("front", "back", "left", "right", "up", "down")      # MyCubemapHelpers.GetNameForFace(0..5)
RESOLUTION = 2048
R = 60000.0                                                    # storage radius (site v1)
HILL_MIN, HILL_MAX = -0.05, 0.12
MIN_HILL = HILL_MIN * R                                        # -3000
HEIGHT_RATIO = (HILL_MAX - HILL_MIN) * R                       # 10200
INNER = R + MIN_HILL                                           # 57000 = MinimumRadius
RS = 60000.00390625                                            # Water Mod sea radius (site v1, float32)
FLOOR_Y = INNER - RS                                           # -3000.00390625
FLOOR_DEPTH = -FLOOR_Y
ZLIB_LEVEL = 9
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# CustomMaterialTable (CONTRACT 3.3): name -> R value; the order is the.sbc row order
MATERIALS = (("Sand_02", 1), ("Stone_01", 2), ("Stone_02", 3), ("Stone_03", 4), ("Stone_04", 5), ("Stone_05", 6), ("Grass", 7))
MAT = {name: value for name, value in MATERIALS}
MATERIAL_MAX_DEPTH = 5
# CONTRACT "biome.names" (S8aMaterialMap.BiomeNames, ext s8a terrain 2): the G channel holds index + 1
BIOME_NAMES = ("safeShallows", "grassyPlateaus", "kelpForest", "dunes", "bloodKelp", "sparseReef", "mountains", "void",
               "mushroomForest", "grandReef", "underwaterIslands", "crashZone", "lostRiver_Canyon")
VOID_G = BIOME_NAMES.index("void") + 1

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
REPO_MOD_DIR = os.path.join(REPO, "src", "DeepEngineers.WorldMod")
DEFAULT_LAYOUT = os.path.join(HERE, "layout_4546b.json")
#  folders this tool never writes (the deployed mod, SE's saves, SNAppData, a game install), whatever the flags
FORBIDDEN_OUT = (os.path.join("SpaceEngineers", "Mods"), os.path.join("SpaceEngineers", "Saves"), os.path.join("steamapps", "common"), "SNAppData")
DEFAULT_SEED = 4546
# the 20 SN (x, z) columns of the in-game height measurement (launcher part c2g, analyzer row S8a.C2.ingame, --native-check): the
# crater, the moat, the wall, the ring, the island, the edge and the project's two field reports of (5.28 km: the shelf
# at the water line, 4.32 km: a rock island), each on an axis and a diagonal
C2G_POINTS = ((0.0, 0.0), (1500.0, -800.0), (2700.0, 0.0), (0.0, 3200.0), (-3400.0, -600.0), (4320.0, 0.0), (0.0, -4320.0),
              (3054.7, 3054.7), (5280.0, 0.0), (0.0, 5280.0), (-5280.0, 0.0), (3733.5, -3733.5), (6500.0, 2500.0), (-7000.0, -3000.0),
              (2250.0, 4350.0), (-4800.0, 6200.0), (9000.0, 1000.0), (-2600.0, -9500.0), (11800.0, -1500.0), (13000.0, 0.0))

def _under(path, root):
    p, r = os.path.normcase(os.path.abspath(path)), os.path.normcase(os.path.abspath(root))
    return p == r or p.startswith(r + os.sep)

def scratch_root(path):
    """True under a scratch folder (the temp directory, %LOCALAPPDATA%\\Temp, tests/out): an --out there needs no project approval."""
    roots = [tempfile.gettempdir(), os.path.join(os.environ.get("LOCALAPPDATA", ""), "Temp"), os.path.join(REPO, "tests", "out")]
    return any(r and os.path.isabs(r) and _under(path, r) for r in roots)

def refuse_out(out, owner_go, res):
    """Why an --out target is refused (None = allowed). ROADMAP S8a project action 1: the 12 PNGs (and the.sbc) go into SE's Mods, SE's Saves, SNAppData or a game install never;
 anywhere outside a scratch folder only with --owner-go (the planet of every world that uses the mod changes once deployed)."""
    target = os.path.normcase(os.path.abspath(out)) + os.sep
    for forbidden in FORBIDDEN_OUT:
        if os.path.normcase(os.sep + forbidden + os.sep) in target:
            return "%s lies under %s: the deployed mod, SE's saves, SNAppData and the game installs are never written by this tool" % (out, forbidden)
    if not owner_go and not scratch_root(out):
        return ("writing %s changes the planet of every world that uses %s once deployed (ROADMAP S8a owner action 1); add --owner-go after the "
                "owner approved the design (a folder under the temp directory or tests/out needs no approval)" % (out, SUBTYPE))
    if res != RESOLUTION and _under(out, REPO_MOD_DIR):
        return "the world mod takes %d px faces only" % RESOLUTION
    return None

# ------------------------------------------------------------------------------------------------- layout
def load_layout(path=None):
    with open(path or DEFAULT_LAYOUT, encoding="utf-8") as fh:
        lay = json.load(fh)
    check_layout(lay)
    return lay

def check_layout(lay):
    """Raises ValueError when the layout names something the planet cannot carry."""
    keys = [b["key"] for b in lay["biomes"]]
    if len(set(keys)) != len(keys):
        raise ValueError("duplicate biome keys")
    for b in lay["biomes"]:
        if b["snName"] not in BIOME_NAMES:
            raise ValueError("biome %s: snName %r is not in biome.names %s" % (b["key"], b["snName"], BIOME_NAMES))
        for k in ("flat", "slope"):
            if b["materials"][k] not in MAT:
                raise ValueError("biome %s: material %r is not in the CustomMaterialTable" % (b["key"], b["materials"][k]))
    for c in lay.get("canyons", []):
        if c["biome"] not in keys:
            raise ValueError("canyon biome %r unknown" % c["biome"])
    vals = [r["value"] for r in lay["ores"]["rows"]] + [r["outcropValue"] for r in lay["ores"]["rows"] if r.get("outcropValue")]
    if len(set(vals)) != len(vals) or not all(1 <= v <= 255 for v in vals):
        raise ValueError("ore values must be distinct and 1..255: %s" % vals)
    for r in lay["ores"]["rows"]:
        for bk in r.get("biomes") or []:
            if bk not in keys:
                raise ValueError("ore %s: biome %r unknown" % (r["ore"], bk))

def layout_hash(lay):
    return fnv1a64(json.dumps(lay, sort_keys=True, separators=(",", ":")).encode("utf-8"))

def biome_g(lay, key):
    b = [x for x in lay["biomes"] if x["key"] == key][0]
    return BIOME_NAMES.index(b["snName"]) + 1

# ------------------------------------------------------------------------------------------------- hashing
FNV_OFFSET, FNV_PRIME = 0xcbf29ce484222325, 0x100000001b3

def fnv1a64(data, h=FNV_OFFSET):
    for b in data:
        h ^= b
        h = (h * FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h

def fnv1a64_fast(data):
    """FNV-1a 64 over a large byte string (identical to fnv1a64)."""
    h = FNV_OFFSET
    prime = FNV_PRIME
    mask = 0xFFFFFFFFFFFFFFFF
    for b in memoryview(data):
        h = ((h ^ b) * prime) & mask
    return h

def sculpt_hash(files):
    """CONTRACT "store.key": sculptHash = fnv1a64 over 'name<TAB>bytes<TAB>fnv1a64 x16<LF>' of the 12 planet data
 files in ordinal order. `files` = {name: bytes}."""
    lines = []
    for name in sorted(files.keys()):
        data = files[name]
        lines.append("%s\t%d\t%016x\n" % (name, len(data), fnv1a64_fast(data)))
    return fnv1a64("".join(lines).encode("utf-8")), lines

# ------------------------------------------------------------------------------------------------- geometry
def face_dirs(face, res, j0=0, j1=None, i0=0, i1=None):
    """Unit planet-local directions of pixels (rows j0..j1-1, columns i0..i1-1) of a face: pixel p <-> texcoord p/(res-1)."""
    if j1 is None:
        j1 = res
    if i1 is None:
        i1 = res
    return face_dirs_at(face, res, np.arange(j0, j1), np.arange(i0, i1))

def face_dirs_at(face, res, jj, ii):
    """Unit planet-local directions of the face pixels at rows jj x columns ii (1-D index arrays): a (len(jj), len(ii)) grid."""
    a = (2.0 * np.asarray(ii, np.float64) / (res - 1) - 1.0)[None, :].repeat(len(jj), axis=0)   # 2u - 1
    b = (2.0 * np.asarray(jj, np.float64) / (res - 1) - 1.0)[:, None].repeat(len(ii), axis=1)   # 2v - 1
    one = np.ones_like(a)
    if face == 0:
        x, y, z = -a, -b, -one
    elif face == 1:
        x, y, z = a, -b, one
    elif face == 2:
        x, y, z = one, -b, -a
    elif face == 3:
        x, y, z = -one, -b, a
    elif face == 4:
        x, y, z = -a, one, -b
    elif face == 5:
        x, y, z = a, -one, -b
    else:
        raise ValueError(face)
    n = np.sqrt(x * x + y * y + z * z)
    return x / n, y / n, z / n

def sample_texcoord(x, y, z):
    """MyCubemapHelpers.CalculateSampleTexcoord (scalar; float64): planet-local position -> (face, tx, ty)."""
    ax, ay, az = abs(x), abs(y), abs(z)
    if ax > ay:
        if ax > az:
            ty = -y / ax
            if x > 0:
                tx, face = -z / ax, 2
            else:
                tx, face = z / ax, 3
        else:
            ty = -y / az
            if z > 0:
                tx, face = x / az, 1
            else:
                tx, face = -x / az, 0
    elif ay > az:
        ty = -z / ay
        if y > 0:
            tx, face = -x / ay, 4
        else:
            tx, face = x / ay, 5
    else:
        ty = -y / az
        if z > 0:
            tx, face = x / az, 1
        else:
            tx, face = -x / az, 0
    return face, (tx + 1.0) * 0.5, (ty + 1.0) * 0.5

def sn_xz(x, y, z):
    """Site-v1 F for planet-local directions (C = 0): SN horizontal (x east, z north). U0 = +Z, E0 = -X, N0 = -Y,
 psi 0, O 0 (SiteMap.SeToSn)."""
    le, ln, lu = -x, -y, z
    h = np.hypot(le, ln)
    theta = np.arctan2(h, lu)
    rho = RS * theta
    with np.errstate(invalid="ignore", divide="ignore"):
        sx = np.where(h > 0, rho * le / np.where(h > 0, h, 1.0), 0.0)
        sz = np.where(h > 0, rho * ln / np.where(h > 0, h, 1.0), 0.0)
    return sx, sz

def sn_to_dir(sx, sz):
    """Inverse of sn_xz at y = 0 (SiteMap.SnToSe for site v1, C = 0), unit direction."""
    rho = math.hypot(sx, sz)
    th = rho / RS
    if rho > 0:
        t = (-sx / rho, -sz / rho, 0.0)          # E0 * (x/rho) + N0 * (z/rho)
    else:
        t = (0.0, 0.0, 0.0)
    c, s = math.cos(th), math.sin(th)
    return (t[0] * s, t[1] * s, c + t[2] * s)

def encode_height(y):
    """sculpt.encode: SN altitude y (array) -> uint16 (round half up, clamped)."""
    v = (RS + np.asarray(y, np.float64) - INNER) / HEIGHT_RATIO * 65535.0
    return np.clip(np.floor(v + 0.5), 0, 65535).astype(np.uint16)

def decode_height(h):
    """uint16 -> SN altitude of the pixel's own surface (no interpolation)."""
    return INNER + np.asarray(h, np.float64) / 65535.0 * HEIGHT_RATIO - RS

def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)

def site_box(res, radius_m):
    """(j0, j1, i0, i1) of face 1 holding every pixel within radius_m (SN, site v1) of the anchor."""
    half = int(math.ceil(math.tan(radius_m / RS) * (res - 1) / 2.0)) + 3
    c = (res - 1) / 2.0
    lo = max(0, int(math.floor(c - half)))
    hi = min(res, int(math.ceil(c + half)) + 1)
    return lo, hi, lo, hi

# ------------------------------------------------------------------------------------------------- noise
_GX = np.array([1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0], np.float64)
_GY = np.array([1, 1, -1, -1, 0, 0, 0, 0, 1, -1, 1, -1], np.float64)
_GZ = np.array([0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1], np.float64)
_M1, _M2, _M3 = np.uint64(0x9E3779B97F4A7C15), np.uint64(0xC2B2AE3D27D4EB4F), np.uint64(0x165667B19E3779F9)
_F1, _F2 = np.uint64(0xBF58476D1CE4E5B9), np.uint64(0x94D049BB133111EB)

def _mix64(h):
    with np.errstate(over="ignore"):
        h = h ^ (h >> np.uint64(30))
        h = h * _F1
        h = h ^ (h >> np.uint64(27))
        h = h * _F2
        h = h ^ (h >> np.uint64(31))
    return h

def _hash3(ix, iy, iz, seed):
    with np.errstate(over="ignore"):
        h = (ix.astype(np.uint64) * _M1) ^ (iy.astype(np.uint64) * _M2) ^ (iz.astype(np.uint64) * _M3) ^ np.uint64(seed)
    return (_mix64(h) % np.uint64(12)).astype(np.intp)

def hash_unit(ix, iz, seed, k):
    """Uniform [0, 1) per integer cell (ix, iz) and stream k (deterministic)."""
    with np.errstate(over="ignore"):
        h = (np.asarray(ix).astype(np.int64).astype(np.uint64) * _M1) ^ (np.asarray(iz).astype(np.int64).astype(np.uint64) * _M2) \
            ^ np.uint64(seed & 0xFFFFFFFFFFFFFFFF) ^ (np.uint64(k) * _M3)
    return (_mix64(h) >> np.uint64(11)).astype(np.float64) * (1.0 / 9007199254740992.0)

def gradient_noise(x, y, z, seed):
    """3D gradient noise (Perlin layout, hashed lattice), about [-1, 1]; deterministic float64 arithmetic."""
    fx0, fy0, fz0 = np.floor(x), np.floor(y), np.floor(z)
    fx, fy, fz = x - fx0, y - fy0, z - fz0
    ix, iy, iz = fx0.astype(np.int64), fy0.astype(np.int64), fz0.astype(np.int64)
    u = fx * fx * fx * (fx * (fx * 6.0 - 15.0) + 10.0)
    v = fy * fy * fy * (fy * (fy * 6.0 - 15.0) + 10.0)
    w = fz * fz * fz * (fz * (fz * 6.0 - 15.0) + 10.0)

    def g(dx, dy, dz, ox, oy, oz):
        k = _hash3(ix + ox, iy + oy, iz + oz, seed)
        return _GX[k] * dx + _GY[k] * dy + _GZ[k] * dz

    n000 = g(fx, fy, fz, 0, 0, 0)
    n100 = g(fx - 1, fy, fz, 1, 0, 0)
    n010 = g(fx, fy - 1, fz, 0, 1, 0)
    n110 = g(fx - 1, fy - 1, fz, 1, 1, 0)
    n001 = g(fx, fy, fz - 1, 0, 0, 1)
    n101 = g(fx - 1, fy, fz - 1, 1, 0, 1)
    n011 = g(fx, fy - 1, fz - 1, 0, 1, 1)
    n111 = g(fx - 1, fy - 1, fz - 1, 1, 1, 1)
    x00 = n000 + u * (n100 - n000)
    x10 = n010 + u * (n110 - n010)
    x01 = n001 + u * (n101 - n001)
    x11 = n011 + u * (n111 - n011)
    y0 = x00 + v * (x10 - x00)
    y1 = x01 + v * (x11 - x01)
    return y0 + w * (y1 - y0)

class Field:
    """A noise source with per-octave seeds and offsets from a seed and a name (deterministic)."""

    def __init__(self, seed, name, octaves):
        rng = random.Random("%d|%s" % (seed, name))
        self.seeds = [rng.getrandbits(63) for _ in range(octaves)]
        self.offsets = [(rng.uniform(-1e4, 1e4), rng.uniform(-1e4, 1e4), rng.uniform(-1e4, 1e4)) for _ in range(octaves)]

    def fbm(self, px, py, pz, wavelength, octaves, gain=0.5, lacunarity=2.0):
        total = np.zeros_like(px)
        amp, f, norm = 1.0, 1.0 / wavelength, 0.0
        for o in range(octaves):
            ox, oy, oz = self.offsets[o]
            total += amp * gradient_noise(px * f + ox, py * f + oy, pz * f + oz, self.seeds[o])
            norm += amp
            amp *= gain
            f *= lacunarity
        return total / norm

    def ridged(self, px, py, pz, wavelength, octaves, gain=0.5, lacunarity=2.0):
        total = np.zeros_like(px)
        amp, f, norm = 1.0, 1.0 / wavelength, 0.0
        for o in range(octaves):
            ox, oy, oz = self.offsets[o]
            n = 1.0 - np.abs(gradient_noise(px * f + ox, py * f + oy, pz * f + oz, self.seeds[o]))
            total += amp * n * n
            norm += amp
            amp *= gain
            f *= lacunarity
        return total / norm

def nz(seed, name, P, wavelength, octaves=2):
    """Signed noise in [-1, 1] (fBm scaled by 1.7 and clipped: about uniform over [-0.7, 0.7], tails to 1)."""
    return np.clip(1.7 * Field(seed, name, octaves).fbm(P[0], P[1], P[2], wavelength, octaves), -1.0, 1.0)

def rz(seed, name, P, wavelength, octaves=3):
    """Ridged noise in [0, 1] (ridge lines near 1)."""
    return Field(seed, name, octaves).ridged(P[0], P[1], P[2], wavelength, octaves)

# ------------------------------------------------------------------------------------------------- biome styles
# depth d in metres below the sea (positive); P = planet-local positions (x, y, z) * RS; sx, sz = SN coordinates (site v1)
def style_flats(b, s, P, sx, sz):
    lo, hi = b["band"]
    d = lo + 12.0 + (hi - lo - 30.0) * (0.5 + 0.5 * nz(s, b["key"] + "/base", P, 1800.0))
    rocks = np.clip((rz(s, b["key"] + "/rocks", P, 160.0, 2) - 0.78) / 0.22, 0.0, 1.0)
    return d - 6.0 * rocks

def style_trenches(b, s, P, sx, sz):
    d = 45.0 + 25.0 * (0.5 + 0.5 * nz(s, b["key"] + "/base", P, 1500.0))
    t = (1.0 - np.abs(nz(s, b["key"] + "/trench", P, 900.0))) ** 10
    return d + 55.0 * t

def style_plateaus(b, s, P, sx, sz):
    n1 = nz(s, b["key"] + "/step", P, 1700.0)
    st = smoothstep(-0.05, 0.05, n1)
    top = 62.0 + 12.0 * (0.5 + 0.5 * nz(s, b["key"] + "/top", P, 900.0))
    low = 155.0 + 20.0 * (0.5 + 0.5 * nz(s, b["key"] + "/low", P, 1100.0))
    lip = 6.0 * np.exp(-((n1 + 0.07) / 0.03) ** 2)          # a raised rim on the plateau's edge (terrace lip)
    return top + (low - top) * st - lip

def style_hills(b, s, P, sx, sz):
    return np.clip(175.0 + 70.0 * nz(s, b["key"] + "/hills", P, 1100.0, 3), 100.0, 250.0)

def style_reef(b, s, P, sx, sz):
    d = 182.0 + 12.0 * nz(s, b["key"] + "/base", P, 1300.0)
    out = np.clip((rz(s, b["key"] + "/reef", P, 650.0, 3) - 0.62) / 0.3, 0.0, 1.0)
    return d - 115.0 * out ** 1.3

def style_drop(b, s, P, sx, sz):
    d = 340.0 + 150.0 * nz(s, b["key"] + "/base", P, 2600.0)
    arch = np.clip((rz(s, b["key"] + "/arch", P, 550.0, 3) - 0.66) / 0.3, 0.0, 1.0)
    return np.clip(d - 130.0 * arch ** 1.5, 180.0, 500.0)

def style_dunes(b, s, P, sx, sz):
    d = 250.0 + 110.0 * nz(s, b["key"] + "/base", P, 3200.0)
    a = 0.6
    phase = (sx * math.cos(a) + sz * math.sin(a)) / 260.0 * 2.0 * math.pi + 2.5 * nz(s, b["key"] + "/warp", P, 1600.0)
    dune = (0.5 + 0.5 * np.sin(phase + 0.6 * np.sin(phase))) ** 3     # a crest steeper on one side
    return np.clip(d - 34.0 * dune, 100.0, 400.0)

def style_mountains(b, s, P, sx, sz):
    d = 430.0 + 130.0 * nz(s, b["key"] + "/base", P, 2200.0)
    m = np.clip((rz(s, b["key"] + "/ridge", P, 1500.0, 3) - 0.5) / 0.4, 0.0, 1.0)
    return np.clip(d - 360.0 * m ** 1.4, 25.0, 600.0)

def style_pillars(b, s, P, sx, sz):
    d = 470.0 + 20.0 * nz(s, b["key"] + "/base", P, 1400.0)
    cell = 520.0
    cx, cz = np.floor(sx / cell), np.floor(sz / cell)
    best = d.copy()
    seed = s ^ 0x5EED
    for oi in (-1, 0, 1):
        for oj in (-1, 0, 1):
            ix, iz = cx + oi, cz + oj
            present = hash_unit(ix, iz, seed, 0) < 0.7
            px = (ix + 0.2 + 0.6 * hash_unit(ix, iz, seed, 1)) * cell
            pz = (iz + 0.2 + 0.6 * hash_unit(ix, iz, seed, 2)) * cell
            rad = 110.0 + 90.0 * hash_unit(ix, iz, seed, 3)
            top = 105.0 + 60.0 * hash_unit(ix, iz, seed, 4)
            p = np.clip(1.0 - np.hypot(sx - px, sz - pz) / rad, 0.0, 1.0)
            p = smoothstep(0.0, 0.3, p) * present
            best = np.minimum(best, d - p * (d - top))
    return best

def style_blood(b, s, P, sx, sz):
    d = 520.0 + 130.0 * nz(s, b["key"] + "/base", P, 2300.0)
    t = (1.0 - np.abs(nz(s, b["key"] + "/trench", P, 1200.0))) ** 12
    return np.clip(d + 120.0 * t, 300.0, 700.0)

def style_sand_slope(b, s, P, sx, sz):
    d = 70.0 + 210.0 * (0.5 + 0.5 * nz(s, b["key"] + "/base", P, 3800.0))
    mound = np.clip((rz(s, b["key"] + "/debris", P, 400.0, 2) - 0.8) / 0.2, 0.0, 1.0)
    return d - 15.0 * mound

STYLES = {"flats": style_flats, "trenches": style_trenches, "plateaus": style_plateaus, "hills": style_hills,
          "reef": style_reef, "drop": style_drop, "dunes": style_dunes, "mountains": style_mountains,
          "pillars": style_pillars, "bloodKelp": style_blood, "sandSlope": style_sand_slope}

# ------------------------------------------------------------------------------------------------- the design
class Design:
    """The layout's fields at planet-local unit directions (arrays). evaluate is the one evaluation the faces, --check-world,
 --ore-sites and the selftest share; detail_seed is the --seed (the small detail layer only)."""

    def __init__(self, layout, detail_seed=DEFAULT_SEED):
        self.lay = layout
        self.seed = int(layout["layoutSeed"])
        self.detail_seed = int(detail_seed)
        self.scale = float(layout["scale"])
        self.keys = [b["key"] for b in layout["biomes"]]
        self.by_key = {b["key"]: b for b in layout["biomes"]}
        self.voronoi = [b for b in layout["biomes"] if b.get("seeds")]
        e = layout["edge"]
        self.region_m = e["radiusM"] + e["outerM"] + e["wiggleM"] + 400.0
        # design 4: f(sn_x, sn_z) -> the highest SN y the planet may reach under Subnautica's terrain (+inf elsewhere); None = no cap
        self.sn_cap = None
        # design 5: the Subnautica map's rim per heading (tools/planet/sn_rim.py Rim.at(sn_x, sn_z) -> (rEdge, rimY)); None = the
 # layout's fallback rim
        self.sn_rim = None
        self.planet = Planet(self)

    def evaluate(self, x, y, z):
        """-> dict: depth (m, positive down), biome (index into layout biomes), crater (blend 0..1), edge (0..1),
 canyon (profile 0..1), island (r / radius), rinf, rho, sx, sz."""
        lay, s = self.lay, self.seed
        P = (x * RS, y * RS, z * RS)
        sx, sz = sn_xz(x, y, z)
        rinf = np.maximum(np.abs(sx), np.abs(sz))
        rho = np.hypot(sx, sz)
        shape = np.shape(x)
        depth = np.full(shape, FLOOR_DEPTH)
        biome = np.full(shape, self.keys.index("craterEdge"), np.int16)
        crater = np.zeros(shape)
        edge = np.ones(shape)
        canyon = np.zeros(shape)
        island = np.full(shape, 9.0)
        out = {"depth": depth, "biome": biome, "crater": crater, "edge": edge, "canyon": canyon, "island": island,
               "rinf": rinf, "rho": rho, "sx": sx, "sz": sz}
        cr, ed = lay["crater"], lay["edge"]
        inner = cr.get("innerFloor")
        rim = cr.get("rim")
        # design 4: the crater has a floor everywhere, so every pixel is
 # evaluated; design 5 the same (no crater at all)
        sel = (rho <= self.region_m) & ((rinf > cr["moatRhoInfM"]) if not (inner or rim) else True)
        if not np.any(sel):
            return out
        Ps = (P[0][sel], P[1][sel], P[2][sel])
        X, Z = sx[sel], sz[sel]
        # 1. regions: nearest seed (map metres) after a fixed warp; soft weights for the heights
        vo = lay["voronoi"]
        mx = X / self.scale + vo["warpM"] * nz(s, "warp/x", Ps, vo["warpWavelengthM"])
        mz = Z / self.scale + vo["warpM"] * nz(s, "warp/z", Ps, vo["warpWavelengthM"])
        dist = np.empty((len(self.voronoi), X.size))
        for k, b in enumerate(self.voronoi):
            sd = np.array(b["seeds"], np.float64)
            dist[k] = np.min(np.hypot(mx[None, :] - sd[:, 0:1], mz[None, :] - sd[:, 1:2]), axis=0)
        dmin = dist.min(axis=0)
        hard = np.argmin(dist, axis=0)
        w = np.exp(-(dist - dmin[None, :]) / vo["softM"])
        w[(dist - dmin[None, :]) > 6.0 * vo["softM"]] = 0.0
        #): a boundary steps by up to cliffM within ~cliffSoftM (a visible cliff),
 # the rest of the depth difference ramps over softM
        cs = vo.get("cliffSoftM")
        wc = np.exp(-(dist - dmin[None, :]) / cs) if cs else None
        d = np.zeros(X.size)
        dc = np.zeros(X.size)
        wsum = np.zeros(X.size)
        wcsum = np.zeros(X.size)
        rel = np.zeros(X.size)
        for k, b in enumerate(self.voronoi):
            m = w[k] > 0.004
            if not np.any(m):
                continue
            db = STYLES[b["style"]](b, s, (Ps[0][m], Ps[1][m], Ps[2][m]), X[m], Z[m])
            d[m] += w[k][m] * db
            wsum[m] += w[k][m]
            rel[m] += w[k][m] * float(b.get("relief", 1.0))
            if wc is not None:
                dc[m] += wc[k][m] * db
                wcsum[m] += wc[k][m]
        d /= wsum
        rel /= wsum
        if wc is not None:
            dc /= np.maximum(wcsum, 1e-300)
            d = d + np.clip(dc - d, -vo["cliffM"], vo["cliffM"])
        bid = np.array([self.keys.index(b["key"]) for b in self.voronoi], np.int16)[hard]
        # 2. small relief everywhere (layout seed) and the detail layer (--seed)
        sr = lay["smallRelief"]
        d -= sr["amplitudeM"] * nz(s, "smallRelief", Ps, sr["wavelengthM"], int(sr["octaves"]))
        hl = lay.get("hills")
        if hl:
            d -= rel * hl["amplitudeM"] * nz(s, "hills", Ps, hl["wavelengthM"], int(hl["octaves"]))
        bo = lay.get("boulders")
        if bo:
            bumps = np.clip((rz(s, "boulders", Ps, bo["wavelengthM"], 2) - bo["threshold"]) / (1.0 - bo["threshold"]), 0.0, 1.0)
            d -= rel * bo["heightM"] * bumps
        dt = lay["detail"]
        d -= dt["amplitudeM"] * nz(self.detail_seed, "detail", Ps, dt["wavelengthM"], 2)
        # 3. canyons (the Lost River entrances)
        cprof = np.zeros(X.size)
        for c in lay.get("canyons", []):
            pts = np.array(c["path"], np.float64) * self.scale
            seg = np.hypot(np.diff(pts[:, 0]), np.diff(pts[:, 1]))
            total = float(seg.sum())
            best = np.full(X.size, np.inf)
            along = np.zeros(X.size)
            acc = 0.0
            for k in range(len(pts) - 1):
                ax_, az_ = pts[k]
                bx_, bz_ = pts[k + 1]
                vx, vz = bx_ - ax_, bz_ - az_
                L2 = vx * vx + vz * vz
                t = np.clip(((X - ax_) * vx + (Z - az_) * vz) / L2, 0.0, 1.0)
                dd = np.hypot(X - (ax_ + t * vx), Z - (az_ + t * vz))
                better = dd < best
                best = np.where(better, dd, best)
                along = np.where(better, (acc + t * seg[k]) / total, along)
                acc += seg[k]
            q = best / (0.5 * c["widthM"])
            prof = (1.0 - smoothstep(0.5, 1.0, q)) * smoothstep(0.0, 0.08, along) * smoothstep(0.0, 0.08, 1.0 - along)
            dc = c["depthM"][0] + (c["depthM"][1] - c["depthM"][0]) * along + 25.0 * nz(s, "canyon/floor", Ps, 400.0)
            d = d + prof * np.maximum(0.0, dc - d)
            take = prof > cprof
            cprof = np.where(take, prof, cprof)
            bid = np.where(prof > 0.5, np.int16(self.keys.index(c["biome"])), bid)
        # 4. the Mountains island (the only land above the sea)
        isl = lay["island"]
        icx, icz = isl["centre"][0] * self.scale, isl["centre"][1] * self.scale
        rn = np.hypot(X - icx, Z - icz) / isl["radiusM"]
        sh = isl["shoreFraction"]
        top = isl["topY"]
        yi = np.where(rn < sh, top * (1.0 - (rn / sh) ** 1.7),
                      np.where(rn < 1.0, -70.0 * (rn - sh) / (1.0 - sh), -70.0 - 260.0 * (rn - 1.0)))
        yi = yi + 6.0 * nz(s, "island/rock", Ps, 220.0) * (rn < 1.0)
        d = np.minimum(d, -yi)
        bid = np.where(rn < 1.0, np.int16(self.keys.index(isl["biome"])), bid)
        # 5. limits: at least minDepthM below the sea away from the island
        d = np.where(rn < 1.2, d, np.maximum(d, lay["limits"]["minDepthM"]))
        # 6. the crater wall (site-v1 rhoInf; a wiggle on the wall only) and the Crater Edge down to the void floor
        if rim:
            # design 5: no crater wall, no inner floor, no edge drop.
 # Beyond the Subnautica map's terrain the floor continues at the map's own rim depth for that heading and blends into
 # the ring's biome depths over blendM; under Subnautica's terrain the cap keeps it capMarginM below the column's lowest
 # floor; the ring's outer edge blends into the planet in sculpt_face (planet.edgeBlend)
            if self.sn_rim is not None:
                r_edge, rim_y = self.sn_rim.at(X, Z)
            else:
                r_edge = np.full(X.shape, rim["fallbackEdgeM"])
                rim_y = np.full(X.shape, rim["fallbackY"])
            rho_s = rho[sel]
            t_in = smoothstep(r_edge, r_edge + rim["blendM"], rho_s)
            d = -rim_y + (d + rim_y) * t_in
            if self.sn_cap is not None:
                d = np.maximum(d, -self.sn_cap(X, Z))
            eb = (lay.get("planet") or {}).get("edgeBlend")
            e = edge_blend(eb, s, Ps, rho_s) if eb else np.zeros(X.size)
            depth[sel] = d
            biome[sel] = bid
            crater[sel] = 1.0
            edge[sel] = e
            canyon[sel] = cprof
            island[sel] = rn
            return out
        # design 4: a round crater wall (rho, the wall's own radii) instead of the square (rhoInf)
        R_ = rho[sel] if (inner and inner.get("roundWall")) else rinf[sel]
        rb = R_ + cr["wiggleM"] * nz(s, "crater/wiggle", Ps, cr["wiggleWavelengthM"])
        if inner and inner.get("roundWall"):
            bl = smoothstep(inner["roundWall"][0], inner["roundWall"][1], rb)
        else:
            bl = smoothstep(cr["moatRhoInfM"], cr["wallTopRhoInfM"], rb)
        rw = rho[sel] + ed["wiggleM"] * nz(s, "edge/wiggle", Ps, ed["wiggleWavelengthM"])
        e = smoothstep(ed["radiusM"] - ed["innerM"], ed["radiusM"] + ed["outerM"], rw)
        if inner:
            # design 4: the crater wall rises from the inner floor (a Crater Edge depth, continuous across the Subnautica map's
 # border), not from the -3,000 m floor; under Subnautica's terrain the floor stays below its lowest floor (sn_cap)
            fl = inner["depthM"]
            floor_d = fl[0] + (fl[1] - fl[0]) * (0.5 + 0.5 * nz(s, "crater/innerFloor", Ps, inner["wavelengthM"], 2))
            d = floor_d + (d - floor_d) * bl
            if self.sn_cap is not None:
                cap_y = self.sn_cap(X, Z)
                d = np.maximum(d, -cap_y)
        else:
            d = FLOOR_DEPTH + (d - FLOOR_DEPTH) * bl
        er = (lay.get("planet") or {}).get("edgeRise")
        if er:
            # design 4: the Crater Edge drops to the trough (the planet's own field),
 # the cliff keeps the Crater Edge's biome, the trough floor the planet's trough biome
            tro = er["troughDepthM"]
            trough = tro[0] + (tro[1] - tro[0]) * (0.5 + 0.5 * nz(s, "planet/trough", Ps, er["troughWavelengthM"], 2))
            d = d + (trough - d) * e
            tb = self.keys.index(lay["planet"]["biomes"]["trough"])
            bid = np.where(bl < 0.5, np.int16(self.keys.index("craterEdge")),
                           np.where(e > 0.85, np.int16(tb), np.where(e > 0.5, np.int16(self.keys.index("craterEdge")), bid)))
        else:
            d = d + (FLOOR_DEPTH - d) * e
            bid = np.where((bl < 0.5) | (e > 0.5), np.int16(self.keys.index("craterEdge")), bid)
        depth[sel] = d
        biome[sel] = bid
        crater[sel] = bl
        edge[sel] = e
        canyon[sel] = cprof
        island[sel] = rn
        return out

# ------------------------------------------------------------------------------------------------- the planet (Stage E)
def _coarse_axis(res, k):
    """Coarse sample indices (every k pixels and the last) and, per pixel, the left coarse index and its weight."""
    idx = np.arange(0, res, k)
    if idx[-1] != res - 1:
        idx = np.append(idx, res - 1)
    p = np.arange(res, dtype=np.float64)
    left = np.clip(np.searchsorted(idx, p, side="right") - 1, 0, len(idx) - 2)
    w = (p - idx[left]) / (idx[left + 1] - idx[left])
    return idx, left, w

def off_seam(d, keep):
    """A direction whose face coordinates (2 t - 1) both lie within +-keep (away from the cube's face seams)."""
    _f, tx, ty = sample_texcoord(d[0], d[1], d[2])
    return abs(2.0 * tx - 1.0) <= keep and abs(2.0 * ty - 1.0) <= keep

def dirs_rho(x, y, z):
    """SN rho (site v1, azimuthal equidistant: RS x the angle from the anchor U0 = +Z) of unit directions."""
    return RS * np.arctan2(np.hypot(x, y), z)

class Planet:
    """Stage E: the ocean floor beyond the ring (layout 'planet'): the moat, shelves, plains, ridges, trenches and island
 chains. face returns one face's fields; the ring (Design.evaluate) replaces them within Design.region_m on face 1."""

    def __init__(self, design):
        self.design = design
        self.pl = design.lay.get("planet")
        self.s = design.seed
        self.on = self.pl is not None
        if not self.on:
            self.islands = np.zeros((0, 8))
            self.zone_rho = float("inf")
            return
        keys = design.keys
        self.bid = {k: keys.index(v) for k, v in self.pl["biomes"].items()}
        self.rel_of = np.array([float(b.get("relief", 1.0)) for b in design.lay["biomes"]], np.float64)
        er = self.pl.get("edgeRise")
        eb = self.pl.get("edgeBlend")
        if eb:
            # design 5: the planet proper (its ore rows) where the ring has blended into it
            self.zone_rho = eb["endM"]
        elif er:
            # design 4: the planet proper (its ore rows) from the middle of the rise
            self.zone_rho = design.region_m + 0.5 * er["riseM"] + er["wiggleM"]
        else:
            m = self.pl["moat"]
            self.zone_rho = m["floorToM"] + m["wallM"] + m["wiggleM"]
        self.islands = self._islands()

    def _islands(self):
        """(n, 8): unit centre x, y, z, radius m, top y, platform depth m, chain, face; centres off the seams and beyond keepOutM."""
        ch = self.pl["chains"]
        rng = random.Random("%d|planet/chains" % self.s)
        out = []

        def unit(v):
            n = math.sqrt(sum(a * a for a in v))
            return [a / n for a in v]
        for c in range(int(ch["count"])):
            d = None
            for _ in range(2000):
                d = unit([rng.gauss(0.0, 1.0) for _ in range(3)])
                if RS * math.acos(max(-1.0, min(1.0, d[2]))) >= ch["keepOutM"] and off_seam(d, ch["seamKeep"]):
                    break
            t = [rng.gauss(0.0, 1.0) for _ in range(3)]
            dot = sum(a * b for a, b in zip(t, d))
            t = unit([a - dot * b for a, b in zip(t, d)])
            pos = 0.0
            for _ in range(rng.randint(int(ch["islands"][0]), int(ch["islands"][1]))):
                ang = pos / RS
                p = [math.cos(ang) * a + math.sin(ang) * b for a, b in zip(d, t)]
                r, top, pd = rng.uniform(*ch["radiusM"]), rng.uniform(*ch["topY"]), rng.uniform(*ch["platformDepthM"])
                if off_seam(p, ch["seamKeep"]) and RS * math.acos(max(-1.0, min(1.0, p[2]))) >= ch["keepOutM"]:
                    out.append((p[0], p[1], p[2], r, top, pd, c, sample_texcoord(*p)[0]))
                pos += rng.uniform(*ch["spacingM"])
        return np.array(out, np.float64).reshape(-1, 8)

    def coarse(self, x, y, z):
        """The large fields (wavelengths of kilometres) at unit directions: depth d (m, down), shelf, ridge, trench, moat wiggle."""
        pl, s = self.pl, self.s
        P = (x * RS, y * RS, z * RS)
        ba = pl["basins"]
        c = nz(s, "planet/basin", P, ba["wavelengthM"], 3)
        S = smoothstep(ba["shelfLevel"] - ba["slopeWidth"], ba["shelfLevel"] + ba["slopeWidth"], c)
        sd, pdm = ba["shelfDepthM"], ba["plainDepthM"]
        shelf = sd[0] + (sd[1] - sd[0]) * (0.5 + 0.5 * nz(s, "planet/shelf", P, ba["shelfWavelengthM"], 2))
        plain = pdm[0] + (pdm[1] - pdm[0]) * (0.5 + 0.5 * nz(s, "planet/plain", P, ba["plainWavelengthM"], 2))
        d = plain + (shelf - plain) * S
        ri = pl["ridges"]
        rr = np.clip((rz(s, "planet/ridge", P, ri["wavelengthM"], 3) - ri["threshold"]) / ri["width"], 0.0, 1.0) * (1.0 - S)
        d = d - ri["heightM"] * rr ** 1.3
        tr = pl["trenches"]
        mask = smoothstep(tr["maskLevel"][0], tr["maskLevel"][1], nz(s, "planet/trenchMask", P, tr["maskWavelengthM"], 1))
        keep = None
        if "keepOutM" in tr or "keepOutM" in pl.get("deepBasins", {}):
            # design 4: the site's surroundings stay shelf and reef (deep basins and trenches only beyond keepOutM)
            keep = dirs_rho(x, y, z)
        if "keepOutM" in tr:
            mask = mask * smoothstep(tr["keepOutM"], tr["keepOutM"] + 6000.0, keep)
        tt = np.clip((rz(s, "planet/trench", P, tr["wavelengthM"], 2) - tr["threshold"]) / tr["width"], 0.0, 1.0) * mask
        dd = np.zeros(np.shape(d))
        db = pl.get("deepBasins")
        if db:
            # design 4: deep basins only where this masked field calls for them (the Grand Reef's deep look)
            dmask = smoothstep(db["maskLevel"][0], db["maskLevel"][1], nz(s, "planet/deepMask", P, db["maskWavelengthM"], 1))
            if "keepOutM" in db:
                dmask = dmask * smoothstep(db["keepOutM"], db["keepOutM"] + 6000.0, keep)
            dd = np.clip((nz(s, "planet/deep", P, db["wavelengthM"], 2) - db["threshold"]) / db["width"], 0.0, 1.0) * dmask
            dfl = db["depthM"][0] + (db["depthM"][1] - db["depthM"][0]) * (0.5 + 0.5 * nz(s, "planet/deepFloor", P, db["floorWavelengthM"], 2))
            d = d + (dfl - d) * smoothstep(0.0, 1.0, dd)
        d = d + (pl["floorM"][1] - d) * smoothstep(0.0, 1.0, tt)
        er = pl.get("edgeRise")
        if pl.get("edgeBlend"):
            # design 5: no trough, no moat (the ring blends into this field in sculpt_face)
            trough = np.zeros(np.shape(d))
            wig = np.zeros(np.shape(d))
        elif er:
            tro = er["troughDepthM"]
            trough = tro[0] + (tro[1] - tro[0]) * (0.5 + 0.5 * nz(s, "planet/trough", P, er["troughWavelengthM"], 2))
            wig = er["wiggleM"] * nz(s, "planet/rise", P, er["wiggleWavelengthM"], 2)
        else:
            mo = pl["moat"]
            trough = np.full(np.shape(d), FLOOR_DEPTH)
            wig = mo["wiggleM"] * nz(s, "planet/moat", P, mo["wiggleWavelengthM"], 2)
        return {"d": d, "shelf": S, "ridge": rr, "trench": tt, "wig": wig, "deep": dd, "trough": trough}

    def island_profile(self, rn, top, pd):
        """SN y of an island at rn = distance / radius: land to top inside shoreFraction, the shore to -0.6 pd at rn 1, the
 platform to -pd at platformRadius, then the seamount slope (slopePerRadiusM per radius)."""
        ch = self.pl["chains"]
        sh, plat = ch["shoreFraction"], ch["platformRadius"]
        return np.where(rn < sh, top * (1.0 - (rn / sh) ** 1.7),
                        np.where(rn < 1.0, -0.6 * pd * (rn - sh) / (1.0 - sh),
                                 np.where(rn < plat, -0.6 * pd - 0.4 * pd * (rn - 1.0) / (plat - 1.0),
                                          -pd - ch["slopePerRadiusM"] * (rn - plat))))

    def face(self, face, res):
        """The planet's fields of one face: depth (m, down, float64), biome (int16), island (rn, float32), trench, ridge
 (0..1 float32) and rho (SN rho, float32)."""
        lay, pl, s = self.design.lay, self.pl, self.s
        out = {"depth": np.full((res, res), FLOOR_DEPTH), "biome": np.full((res, res), self.design.keys.index("craterEdge"), np.int16),
               "island": np.full((res, res), 99.0, np.float32), "trench": np.zeros((res, res), np.float32),
               "ridge": np.zeros((res, res), np.float32), "rho": np.zeros((res, res), np.float32)}
        if not self.on:
            for j0 in range(0, res, 256):
                x, y, z = face_dirs(face, res, j0, min(res, j0 + 256))
                out["rho"][j0:j0 + x.shape[0]] = dirs_rho(x, y, z)
            return out
        k = max(1, res // 256)
        idx, left, w = _coarse_axis(res, k)
        xc, yc, zc = face_dirs_at(face, res, idx, idx)
        C = self.coarse(xc, yc, zc)
        sr, hl, dt, fl = lay["smallRelief"], lay["hills"], lay["detail"], pl["floorM"]
        mo, ch = pl.get("moat"), pl["chains"]
        er = pl.get("edgeRise")
        isl = self.islands[self.islands[:, 7] == face] if len(self.islands) else self.islands
        b = self.bid
        for j0 in range(0, res, 256):
            j1 = min(res, j0 + 256)
            jl, wj = left[j0:j1], w[j0:j1][:, None]
            wi = w[None, :]

            def up(A):
                a0, a1 = A[jl], A[jl + 1]
                return (a0[:, left] * (1.0 - wi) + a0[:, left + 1] * wi) * (1.0 - wj) + (a1[:, left] * (1.0 - wi) + a1[:, left + 1] * wi) * wj
            x, y, z = face_dirs(face, res, j0, j1)
            P = (x * RS, y * RS, z * RS)
            d = up(C["d"])
            S, rr, tt = up(C["shelf"]), up(C["ridge"]), up(C["trench"])
            bid = np.full(x.shape, b["plains"], np.int16)
            bid[S > 0.5] = b["shelf"]
            bid[rr > 0.25] = b["ridges"]
            bid[tt > 0.4] = b["trench"]
            if "basin" in b:
                bid[up(C["deep"]) > 0.4] = b["basin"]
            rel = self.rel_of[bid]
            d = d - rel * (sr["amplitudeM"] * nz(s, "planet/small", P, sr["wavelengthM"], 1)
                           + hl["amplitudeM"] * nz(s, "planet/hills", P, hl["wavelengthM"], 1))
            d = d - dt["amplitudeM"] * nz(self.design.detail_seed, "detail", P, dt["wavelengthM"], 1)
            d = np.clip(d, fl[0], fl[1])
            rn_min = np.full(x.shape, 99.0)
            for ux, uy, uz, r, top, pd, _c, _f in isl:
                reach = (ch["platformRadius"] + (pd + fl[1]) / ch["slopePerRadiusM"]) * r
                dot = x * ux + y * uy + z * uz
                sel = dot > math.cos(min(math.pi, reach / RS))
                if not np.any(sel):
                    continue
                rn = RS * np.arccos(np.clip(dot[sel], -1.0, 1.0)) / r
                d[sel] = np.minimum(d[sel], -self.island_profile(rn, top, pd))
                rn_min[sel] = np.minimum(rn_min[sel], rn)
            bid[rn_min < ch["platformRadius"]] = b["shelf"]
            bid[rn_min < 1.0] = b["islands"]
            rho = dirs_rho(x, y, z)
            if pl.get("edgeBlend"):
                pass                                    # design 5: the planet field as it is, right up to the ring (sculpt_face blends)
            elif er:
                # design 4: from the end of the ring's region the trough rises over riseM to the planet floor (no moat)
                r0 = self.design.region_m
                t = smoothstep(r0, r0 + er["riseM"], rho + np.maximum(0.0, up(C["wig"])) * smoothstep(r0, r0 + 600.0, rho))
                trough = up(C["trough"])
                d = trough + (d - trough) * t
                bid[t < 0.5] = b["trough"]
            else:
                t = smoothstep(mo["floorToM"], mo["floorToM"] + mo["wallM"], rho + up(C["wig"]))
                d = FLOOR_DEPTH + (d - FLOOR_DEPTH) * t
                bid[t < 0.5] = b["moat"]
            out["depth"][j0:j1] = d
            out["biome"][j0:j1] = bid
            out["island"][j0:j1] = rn_min
            out["trench"][j0:j1] = tt
            out["ridge"][j0:j1] = rr
            out["rho"][j0:j1] = rho
        return out

# ------------------------------------------------------------------------------------------------- sculpt
class FaceResult:
    __slots__ = ("face", "heights", "materials", "biomes", "ores", "box", "fields", "slope", "bid", "planet", "ring", "isl",
                 "biome_counts")

def _cr_weights(u):
    """Catmull-Rom (Tau 0.5, MyPlanetShapeProvider.CR) weights of the 4 samples at fraction u (arrays ok)."""
    u2, u3 = u * u, u * u * u
    return np.stack([-0.5 * u3 + u2 - 0.5 * u, 1.5 * u3 - 2.5 * u2 + 1.0, -1.5 * u3 + 2.0 * u2 + 0.5 * u,
                     0.5 * u3 - 0.5 * u2], axis=-1)

_SUB = np.linspace(0.0, 1.0, 5)
_WSUB = _cr_weights(_SUB)                                          # (5, 4)

def spline_guard(heights, limit_y, margin=0.5, iterations=24):
    """In place: lower pixels so that SE's bicubic surface (whose Catmull-Rom overshoot reaches ~7 % of a step next
 to a cliff) stays below the limit - margin. limit_y is a scalar or a per-pixel array (SN y); a cell takes the largest
 limit of its 4 x 4 patch (the island's shore keeps the island's). Only cells whose patch could reach the limit are
 examined; each violating cell's inner 2 x 2 pixels go down by the excess. Within a face; across face seams the game
 wraps (not modelled; nothing of the design lies near a seam)."""
    res = heights.shape[0]
    lim = np.broadcast_to(np.asarray(limit_y, np.float64), heights.shape)
    limp = np.pad(lim, ((1, 2), (1, 2)), mode="edge")
    cell_lim = limp[0:res, 0:res].copy()
    for jj in range(4):
        for ii in range(4):
            np.maximum(cell_lim, limp[jj:jj + res, ii:ii + res], out=cell_lim)
    cell_lim = cell_lim - margin
    for _ in range(iterations):
        y = decode_height(heights)
        yp = np.pad(y, ((1, 2), (1, 2)), mode="edge")
        m = yp[0:res, 0:res].copy()
        n = m.copy()
        for jj in range(4):
            for ii in range(4):
                np.maximum(m, yp[jj:jj + res, ii:ii + res], out=m)
                np.minimum(n, yp[jj:jj + res, ii:ii + res], out=n)
        cy, cx = np.nonzero(m + 0.16 * (m - n) > cell_lim)
        if cy.size == 0:
            return
        g = np.empty((cy.size, 4, 4), np.float64)
        for jj in range(4):
            for ii in range(4):
                g[:, jj, ii] = yp[cy + jj, cx + ii]
        vals = np.einsum("aj,njk,bk->nab", _WSUB, g, _WSUB)
        smax = vals.reshape(cy.size, -1).max(axis=1)
        viol = smax > cell_lim[cy, cx]
        if not viol.any():
            return
        excess = smax[viol] - cell_lim[cy, cx][viol] + 0.2
        vy, vx = cy[viol], cx[viol]
        target = np.full(heights.shape, np.inf)
        for dy in (0, 1):
            for dx in (0, 1):
                py = np.minimum(vy + dy, res - 1)
                px = np.minimum(vx + dx, res - 1)
                np.minimum.at(target, (py, px), y[py, px] - excess)
        sel = np.isfinite(target)
        lowered = encode_height(np.minimum(target[sel], y[sel] - 0.2))
        heights[sel] = np.minimum(heights[sel], lowered)

def surface_slope(res, y, j0=0, i0=0):
    """|grad y| in metres per metre from decoded pixel heights of face rows j0.., columns i0.. (cube-map pixel pitch)."""
    a_i = 2.0 * np.arange(i0, i0 + y.shape[1], dtype=np.float64) / (res - 1) - 1.0
    a_j = 2.0 * np.arange(j0, j0 + y.shape[0], dtype=np.float64) / (res - 1) - 1.0
    da = 2.0 / (res - 1)
    A = a_i[None, :]
    B = a_j[:, None]
    q = 1.0 + A * A + B * B
    pitch_x = RS * da * np.sqrt(1.0 + B * B) / q
    pitch_y = RS * da * np.sqrt(1.0 + A * A) / q
    gy, gx = np.gradient(y)
    return np.hypot(gx / pitch_x, gy / pitch_y).astype(np.float32)

def sculpt_face(face, design, res=RESOLUTION, keep=False):
    """Heights (uint16), materials (R), biomes (G) and ores (B) of one face: the planet everywhere (Stage E), the ring within
 Design.region_m of the site (face 1); with keep, the ring box's fields and the face's zones too."""
    lay = design.lay
    pf = design.planet.face(face, res)
    depth, bid, isl = pf["depth"], pf["biome"], pf["island"]
    ring = np.zeros((res, res), bool)
    edge = np.zeros((res, res), np.float32)
    out = FaceResult()
    out.face, out.box, out.fields, out.slope = face, None, None, None
    f = None
    if face == 1:
        j0, j1, i0, i1 = site_box(res, design.region_m)
        x, y, z = face_dirs(1, res, j0, j1, i0, i1)
        f = design.evaluate(x, y, z)
        inside = f["rho"] <= design.region_m
        eb = (lay.get("planet") or {}).get("edgeBlend")
        if eb:
            # design 5: the ring blends into the planet's own field (no trough, no moat): depth and biome; where the blend leaves
 # a seabed between the drop-off band's depths, the Crater Edge biome marks a reef drop-off (never deeper than maxM)
            e = f["edge"]
            pd = depth[j0:j1, i0:i1].copy()
            pb = bid[j0:j1, i0:i1].copy()
            fd = f["depth"] + (pd - f["depth"]) * e
            fb = np.where(e >= 0.5, pb, f["biome"]).astype(np.int16)
            do = eb.get("dropOff")
            if do:
                band = (e > 0.15) & (e < 0.85)
                fd = np.where(band, np.minimum(fd, do["maxM"] + (fd - do["maxM"]) * (1.0 - e)), fd)
                fb = np.where(band & (fd >= do["minM"]) & (fd <= do["maxM"]), np.int16(design.keys.index(do["biome"])), fb)
            f["depth"], f["biome"] = fd, fb
        depth[j0:j1, i0:i1][inside] = f["depth"][inside]
        bid[j0:j1, i0:i1][inside] = f["biome"][inside]
        isl[j0:j1, i0:i1][inside] = np.minimum(isl[j0:j1, i0:i1][inside], f["island"][inside])
        ring[j0:j1, i0:i1] = inside & (f["crater"] >= 0.5) & (f["edge"] <= 0.85)
        edge[j0:j1, i0:i1] = np.where(inside, f["edge"], 0.0)
        out.box = (j0, j1, i0, i1)
    heights = encode_height(-depth)
    if f is not None:
        hb = heights[j0:j1, i0:i1]
        if not (lay["crater"].get("innerFloor") or lay["crater"].get("rim")):
            hb[f["rinf"] <= lay["crater"]["flatRhoInfM"]] = 0                  # sculpt.site: exactly 0
    spline_guard(heights, np.where(isl < 1.25, lay["limits"]["islandMaxY"], -lay["limits"]["minDepthM"]))
    yy = decode_height(heights)
    slope = surface_slope(res, yy)
    mats, bios = surface_maps(design, bid, slope, yy, isl, depth)
    inner = lay["crater"].get("innerFloor")
    rim = lay["crater"].get("rim")
    if f is not None and rim and rim.get("tileBands"):
        # design 5: around the Subnautica map (to the rim's blend end) the planet takes the tiles' depth-band material
        re_ = design.sn_rim.at(f["sx"], f["sz"])[0] if design.sn_rim is not None else rim["fallbackEdgeM"]
        mb = mats[j0:j1, i0:i1]
        yb = yy[j0:j1, i0:i1]
        cm = f["rho"] <= re_ + rim["blendM"]
        mb[cm] = tile_band_material(yb[cm])
    if f is not None and inner and inner.get("tileBands"):
        # design 4: inside the crater the planet takes the tiles' depth-band material (TileSampler MaterialBands, CONTRACT 3.1),
 # so the Subnautica map's border has one material on both sides
        mb = mats[j0:j1, i0:i1]
        yb = yy[j0:j1, i0:i1]
        cm = f["rinf"] <= lay["crater"]["moatRhoInfM"]
        mb[cm] = tile_band_material(yb[cm])
    zone = pf["rho"] >= design.planet.zone_rho                                  # the planet proper (beyond the moat's wall)
    ores = ore_fields(design, face, res, yy, slope, bid, ring, zone & (yy < -1.0), edge, pf["trench"], pf["ridge"])
    if keep:
        if f is not None:
            out.fields = f
            out.slope = slope[j0:j1, i0:i1]
        out.bid, out.planet, out.ring, out.isl = bid, zone, ring, isl
    out.biome_counts = np.bincount(bid.ravel().astype(np.intp), minlength=len(design.keys))
    out.heights, out.materials, out.biomes, out.ores = heights, mats, bios, ores
    return out

def surface_maps(design, bid, slope, y, isl, depth):
    """R (material) and G (biome) from the final surface y (SN, decoded), its slope and the biome ids."""
    lay = design.lay
    flat_v = np.array([MAT[b["materials"]["flat"]] for b in lay["biomes"]], np.uint8)
    slope_v = np.array([MAT[b["materials"]["slope"]] for b in lay["biomes"]], np.uint8)
    smin = np.array([b["materials"]["slopeMin"] for b in lay["biomes"]], np.float32)
    g_of = np.array([BIOME_NAMES.index(b["snName"]) + 1 for b in lay["biomes"]], np.uint8)
    b = bid.astype(np.intp)
    mats = np.where(slope >= smin[b], slope_v[b], flat_v[b]).astype(np.uint8)
    mats[(isl < 1.0) & (y > lay["island"]["grassAboveY"])] = MAT["Grass"]
    il = lay["island"]
    if il.get("landEverywhere"):
        # design 4: every surface above grassAboveY is island ground, grass or the island rock
        land = y > il["grassAboveY"]
        mats[land] = np.where(slope[land] >= il["rockSlopeMin"], MAT[il["rockMaterial"]], MAT["Grass"]).astype(np.uint8)
    mats[depth >= FLOOR_DEPTH - 1.0] = MAT["Stone_05"]
    return mats, g_of[b]

def edge_blend(eb, seed, P, rho):
    """Design 5: the ring's blend into the planet (0 inside, 1 at endM and beyond); the start wiggles, the end does not."""
    wig = eb["wiggleM"] * nz(seed, "planet/edgeBlend", P, eb["wiggleWavelengthM"], 2)
    return smoothstep(eb["startM"] + wig, eb["endM"], rho)

# The tiles' depth bands (src/DeepEngineers.Terrain/TileSampler.cs MaterialBands, CONTRACT 3.1): lower bound SN y, material
TILE_BANDS = ((2.0, "Grass"), (-40.0, "Sand_02"), (-200.0, "Stone_01"), (-500.0, "Stone_02"), (-900.0, "Stone_03"), (-1300.0, "Stone_04"),
              (-1e30, "Stone_05"))

def tile_band_material(y):
    """The R value of the tiles' depth band at SN y (array)."""
    out = np.full(np.shape(y), MAT[TILE_BANDS[-1][1]], np.uint8)
    for lo, name in reversed(TILE_BANDS[:-1]):
        out = np.where(y >= lo, np.uint8(MAT[name]), out)
    return out

def ore_bands(row):
    """(ring band, planet band, ring biomes, planet biomes) of an ore row (None biomes = any)."""
    pb = row["planetBiomes"] if "planetBiomes" in row else None
    return row["depthM"], row.get("planetDepthM", row["depthM"]), row.get("biomes"), pb

def ore_eligible(design, row, y, bid, ring, planet, edge):
    """The pixels where an ore row may lie: the ring (crater wall blend >= 0.5, Crater Edge blend <= 0.85) with the row's
 band and biomes (craterEdge = the edge's wall, blend 0.2..0.85), or the planet beyond the moat's wall with its planet
 band and biomes; never land."""
    keys = design.keys
    depth = -y
    rb, pband, rbio, pbio = ore_bands(row)
    out = np.zeros(y.shape, bool)
    for zone, band, bios in ((ring, rb, rbio), (planet, pband, pbio)):
        ok = zone & (depth >= band[0]) & (depth <= band[1])
        if bios:
            allowed = np.zeros(y.shape, bool)
            for bk in bios:
                if bk == "craterEdge":
                    allowed |= (edge > 0.2) & (edge <= 0.85)
                else:
                    allowed |= bid == keys.index(bk)
            ok &= allowed
        out |= ok
    return out & (y < -1.0)

FIELD_OFFSETS = ((0, 0), (0, 1), (1, 0), (1, 1), (0, -1), (-1, 0))

def vein_eligible(design, cls, y, bid, ring, planet):
    """The pixels where a vein of class cls may lie: its depth band, the ring or the planet with the class's biomes there (None:
 any), never land."""
    keys = design.keys
    depth = -y
    out = np.zeros(y.shape, bool)
    for zone, bios in ((ring, cls.get("ringBiomes")), (planet, cls.get("planetBiomes"))):
        ok = zone & (depth >= cls["bandM"][0]) & (depth <= cls["bandM"][1])
        if bios:
            allowed = np.zeros(y.shape, bool)
            for bk in bios:
                if bk in keys:
                    allowed |= bid == keys.index(bk)
            ok &= allowed
        out |= ok
    return out & (y < -1.0)

def ore_veins(design, face, res, y, bid, ring, planet):
    """B channel by the vein rule (layout ores.veinRule, design 5): per class (rarest first) a jittered lattice of spacingM; each
 lattice cell's candidate pixel, where the class may lie, is kept with probability keep and starts a vein of veinPx pixels (its
 eligible neighbours, B still 0) of one ore of the class (drawn by hash among the class's rows); the vein's first pixel takes
 the row's outcrop value when the row has one (Start 0: every vein shows at the seabed), the rest the row's value."""
    lay = design.lay
    vr = lay["ores"]["veinRule"]
    rows = lay["ores"]["rows"]
    by_ore = {r["ore"]: r for r in rows}
    s = design.seed
    out = np.zeros(y.shape, np.uint8)
    if not (ring.any() or planet.any()):
        return out
    pitch = RS * (math.pi / 2.0) / (res - 1)
    sf = (s * 1000003 + face * 7919 + 29) & 0x7FFFFFFFFFFFFFFF
    for ci_, cls in enumerate(vr["classes"]):
        el = vein_eligible(design, cls, y, bid, ring, planet)
        if not el.any():
            continue
        G = max(1.0, cls["spacingM"] / pitch)
        nc = int(math.ceil(res / G))
        cj, ci = np.meshgrid(np.arange(nc), np.arange(nc), indexing="ij")
        cj, ci = cj.ravel(), ci.ravel()
        salt = 1000 * (ci_ + 1)
        jj = np.minimum(np.floor((cj + hash_unit(cj, ci, sf, salt + 1)) * G).astype(np.intp), res - 1)
        ii = np.minimum(np.floor((ci + hash_unit(cj, ci, sf, salt + 2)) * G).astype(np.intp), res - 1)
        kept = el[jj, ii] & (hash_unit(cj, ci, sf, salt + 3) < cls["keep"]) & (out[jj, ii] == 0)
        jj, ii, cj, ci = jj[kept], ii[kept], cj[kept], ci[kept]
        ores = [o for o in cls["ores"] if o in by_ore]
        osel = np.minimum(np.floor(hash_unit(cj, ci, sf, salt + 4) * len(ores)).astype(np.intp), len(ores) - 1)
        v0, v1 = int(cls["veinPx"][0]), int(cls["veinPx"][1])
        npx = v0 + np.floor(hash_unit(cj, ci, sf, salt + 5) * (v1 - v0 + 1)).astype(np.intp)
        for k in range(jj.size):
            row = by_ore[ores[osel[k]]]
            first = True
            for o, (oy, ox) in enumerate(FIELD_OFFSETS[:max(1, npx[k])]):
                j, i = jj[k] + oy, ii[k] + ox
                if not (0 <= j < res and 0 <= i < res) or not el[j, i] or out[j, i] != 0:
                    continue
                out[j, i] = (row.get("outcropValue") or row["value"]) if first else row["value"]
                first = False
    return out

def ore_fields(design, face, res, y, slope, bid, ring, planet, edge, trench, ridge):
    """B channel (Stage E 'fields', layout ores.fieldRule): per cell of cellPx pixels one candidate field at the best-scoring
 of 'candidates' pixels (slope, cliff foot, trench and ridge bonus); kept with probability (base + feature x score) x
 (the weight of the rows eligible there / bandNorm); its row drawn by weight (share, x ringScale in the ring); 'deposits'
 blobs of depositPx pixels within fieldRadiusM, each pixel written where its row is eligible and B is still 0 (fields in
 cell order). Outcrop: outcropShare of the pixels (per 46 m cell hash) take the row's outcropValue."""
    lay = design.lay
    if lay["ores"].get("veinRule"):
        return ore_veins(design, face, res, y, bid, ring, planet)
    fr = lay["ores"]["fieldRule"]
    rows = lay["ores"]["rows"]
    s = design.seed
    out = np.zeros(y.shape, np.uint8)
    if not (ring.any() or planet.any()):
        return out
    elig = [ore_eligible(design, r, y, bid, ring, planet, edge) for r in rows]
    pitch = RS * (math.pi / 2.0) / (res - 1)
    G = max(2, int(round(fr["cellPx"] * 46.0 / pitch)))
    nc = res // G
    cj, ci = np.meshgrid(np.arange(nc), np.arange(nc), indexing="ij")
    cj, ci = cj.ravel(), ci.ravel()
    sf = (s * 1000003 + face * 7919 + 17) & 0x7FFFFFFFFFFFFFFF
    # the score: slope (walls, flanks), cliff foot (the neighbours' mean above the pixel), trench and ridge
    yp = np.pad(y, 1, mode="edge")
    foot = np.clip(((yp[:-2, 1:-1] + yp[2:, 1:-1] + yp[1:-1, :-2] + yp[1:-1, 2:]) * 0.25 - y) / 5.0, 0.0, 1.0)
    score = np.clip(0.6 * np.clip(slope / 0.35, 0.0, 1.0) + 0.4 * foot + 0.3 * (trench > 0.2) + 0.3 * (ridge > 0.2), 0.0, 1.0)
    best = np.full(cj.size, -1.0)
    bj = np.zeros(cj.size, np.intp)
    bi = np.zeros(cj.size, np.intp)
    for k in range(int(fr["candidates"])):
        jj = np.minimum(cj * G + np.floor(hash_unit(cj, ci, sf, 10 + 2 * k) * G).astype(np.intp), res - 1)
        ii = np.minimum(ci * G + np.floor(hash_unit(cj, ci, sf, 11 + 2 * k) * G).astype(np.intp), res - 1)
        sc = np.where(ring[jj, ii] | planet[jj, ii], score[jj, ii], -1.0)
        better = sc > best
        best, bj, bi = np.where(better, sc, best), np.where(better, jj, bj), np.where(better, ii, bi)
    W = np.zeros((cj.size, len(rows)))
    in_ring = ring[bj, bi]
    for r_, row in enumerate(rows):
        wr = row["share"] * np.where(in_ring, float(row.get("ringScale", 1.0)), 1.0)
        W[:, r_] = elig[r_][bj, bi] * wr
    wsum = W.sum(axis=1)
    p = np.clip(fr["base"] + fr["feature"] * np.maximum(best, 0.0), 0.0, 1.0) * np.clip(wsum / fr["bandNorm"], 0.0, 1.0)
    kept = (hash_unit(cj, ci, sf, 1) < p) & (best >= 0.0) & (wsum > 0.0)
    if not kept.any():
        return out
    cj, ci, bj, bi, W, wsum = cj[kept], ci[kept], bj[kept], bi[kept], W[kept], wsum[kept]
    u = hash_unit(cj, ci, sf, 2) * wsum
    rsel = np.minimum((np.cumsum(W, axis=1) < u[:, None]).sum(axis=1), len(rows) - 1)
    n0, n1 = int(fr["deposits"][0]), int(fr["deposits"][1])
    nd = n0 + np.floor(hash_unit(cj, ci, sf, 3) * (n1 - n0 + 1)).astype(np.intp)
    radius = (fr["fieldRadiusM"][0] + hash_unit(cj, ci, sf, 4) * (fr["fieldRadiusM"][1] - fr["fieldRadiusM"][0])) / pitch
    m0, m1 = int(fr["depositPx"][0]), int(fr["depositPx"][1])
    pj, pi, pr = [], [], []
    for dd in range(n1):
        act = dd < nd
        ang = 2.0 * math.pi * hash_unit(cj, ci, sf, 100 + 3 * dd)
        rad = radius * np.sqrt(hash_unit(cj, ci, sf, 101 + 3 * dd))
        cy = np.rint(bj + rad * np.sin(ang)).astype(np.intp)
        cx = np.rint(bi + rad * np.cos(ang)).astype(np.intp)
        npx = m0 + np.floor(hash_unit(cj, ci, sf, 102 + 3 * dd) * (m1 - m0 + 1)).astype(np.intp)
        for o, (oy, ox) in enumerate(FIELD_OFFSETS[:m1]):
            a = act & (o < npx)
            pj.append(np.where(a, cy + oy, -1))
            pi.append(cx + ox)
            pr.append(rsel)
    pj = np.stack(pj, axis=1).ravel()
    pi = np.stack(pi, axis=1).ravel()
    pr = np.stack(pr, axis=1).ravel()
    ok = (pj >= 0) & (pj < res) & (pi >= 0) & (pi < res)
    pj, pi, pr = pj[ok], pi[ok], pr[ok]
    good = np.zeros(pj.size, bool)
    for r_ in range(len(rows)):
        m = pr == r_
        good[m] = elig[r_][pj[m], pi[m]]
    pj, pi, pr = pj[good], pi[good], pr[good]
    lin = pj * res + pi
    _u, first = np.unique(lin, return_index=True)
    pj, pi, pr = pj[first], pi[first], pr[first]
    vals = np.array([r["value"] for r in rows], np.uint8)[pr]
    oc_v = np.array([r.get("outcropValue") or 0 for r in rows], np.uint8)[pr]
    oc_s = np.array([float(r.get("outcropShare") or 0.0) for r in rows])[pr]
    hu = hash_unit(pj.astype(np.int64) + face * 1000003, pi.astype(np.int64), s ^ 0x5EED, 5)
    vals = np.where((oc_v > 0) & (hu < oc_s), oc_v, vals)
    out[pj, pi] = vals
    return out

def ore_values(row):
    """The B values of an ore row: its value and, when it outcrops, its outcrop value."""
    return (row["value"], row["outcropValue"]) if row.get("outcropValue") else (row["value"],)

def sculpt_all(design, res=RESOLUTION, keep=False, log=print):
    faces = []
    for fi in range(6):
        t0 = time.time()
        faces.append(sculpt_face(fi, design, res, keep=keep))
        if log:
            log("  face %d %-5s %.1f s" % (fi, FACES[fi], time.time() - t0))
    return faces

# ------------------------------------------------------------------------------------------------- PNG
def _chunk(tag, data):
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

def _png(width, height, bit_depth, color_type, rows_bytes):
    raw = b"".join(b"\x00" + r for r in rows_bytes)
    comp = zlib.compressobj(ZLIB_LEVEL, zlib.DEFLATED, 15, 9, zlib.Z_DEFAULT_STRATEGY)
    idat = comp.compress(raw) + comp.flush()
    ihdr = struct.pack(">IIBBBBB", width, height, bit_depth, color_type, 0, 0, 0)
    return PNG_SIGNATURE + _chunk(b"IHDR", ihdr) + _chunk(b"IDAT", idat) + _chunk(b"IEND", b"")

def png_gray16(h):
    be = np.ascontiguousarray(h.astype(">u2"))
    return _png(h.shape[1], h.shape[0], 16, 0, [be[r].tobytes() for r in range(h.shape[0])])

def png_rgba_material(m, g=None, b=None):
    rgba = np.zeros((m.shape[0], m.shape[1], 4), np.uint8)
    rgba[..., 0] = m
    if g is not None:
        rgba[..., 1] = g
    if b is not None:
        rgba[..., 2] = b
    rgba[..., 3] = 255
    return _png(m.shape[1], m.shape[0], 8, 6, [rgba[r].tobytes() for r in range(m.shape[0])])

def png_rgb(img):
    img = np.ascontiguousarray(np.clip(img, 0, 255).astype(np.uint8))
    return _png(img.shape[1], img.shape[0], 8, 2, [img[r].tobytes() for r in range(img.shape[0])])

def png_decode(data):
    """(width, height, bit_depth, color_type, samples array) for non-interlaced PNGs with any filter type."""
    if data[:8] != PNG_SIGNATURE:
        raise ValueError("not a PNG")
    pos, idat, ihdr = 8, [], None
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", body)
        elif tag == b"IDAT":
            idat.append(body)
        elif tag == b"IEND":
            break
        pos += 12 + length
    w, h, bd, ct, _, _, il = ihdr
    if il != 0:
        raise ValueError("interlaced")
    ch = {0: 1, 2: 3, 4: 2, 6: 4}[ct]
    bpp = ch * bd // 8
    stride = w * bpp
    raw = np.frombuffer(zlib.decompress(b"".join(idat)), np.uint8).reshape(h, stride + 1)
    ft = raw[:, 0]
    rows = raw[:, 1:].astype(np.int32)
    if np.any(ft != 0):
        out = np.zeros((h, stride), np.int32)
        prev = np.zeros(stride, np.int32)
        for r in range(h):
            line = rows[r].copy()
            t = ft[r]
            if t == 1:
                for i in range(bpp, stride):
                    line[i] = (line[i] + line[i - bpp]) & 255
            elif t == 2:
                line = (line + prev) & 255
            elif t == 3:
                for i in range(stride):
                    left = line[i - bpp] if i >= bpp else 0
                    line[i] = (line[i] + ((left + prev[i]) >> 1)) & 255
            elif t == 4:
                for i in range(stride):
                    a_ = line[i - bpp] if i >= bpp else 0
                    b_ = prev[i]
                    c_ = prev[i - bpp] if i >= bpp else 0
                    p = a_ + b_ - c_
                    pa, pb, pc = abs(p - a_), abs(p - b_), abs(p - c_)
                    line[i] = (line[i] + (a_ if pa <= pb and pa <= pc else (b_ if pb <= pc else c_))) & 255
            out[r] = line
            prev = line
        rows = out
    rows = rows.astype(np.uint8)
    if bd == 16:
        s = rows.reshape(h, w * ch, 2)
        samples = (s[..., 0].astype(np.uint16) << 8) | s[..., 1].astype(np.uint16)
        samples = samples.reshape(h, w, ch) if ch > 1 else samples.reshape(h, w)
    else:
        samples = rows.reshape(h, w, ch) if ch > 1 else rows.reshape(h, w)
    return w, h, bd, ct, samples

# ------------------------------------------------------------------------------------------------- files
def planet_files(faces):
    out = {}
    for fr in faces:
        out[FACES[fr.face] + ".png"] = png_gray16(fr.heights)
        out[FACES[fr.face] + "_mat.png"] = png_rgba_material(fr.materials, fr.biomes, fr.ores)
    return out

def planet_dir(mod_dir):
    return os.path.join(mod_dir, "Data", "PlanetDataFiles", SUBTYPE)

def read_heightmaps(mod_dir):
    """{face index: uint16 array} of a mod folder's six heightmaps (None when a file is missing)."""
    out = {}
    for f, name in enumerate(FACES):
        p = os.path.join(planet_dir(mod_dir), name + ".png")
        if not os.path.isfile(p):
            return None
        with open(p, "rb") as fh:
            out[f] = png_decode(fh.read())[4]
    return out

def ore_rows_xml(lay):
    color_inf = int(lay["ores"].get("colorInfluence", 15))
    rows = [(r["value"], r["type"], r["startM"], r["depthBelowM"], r["targetColor"]) for r in lay["ores"]["rows"]]
    rows += [(r["outcropValue"], r["type"], 0.0, r["startM"] + r["depthBelowM"], r["targetColor"]) for r in lay["ores"]["rows"] if r.get("outcropValue")]
    return "".join('      <Ore Value="%d" Type="%s" Start="%g" Depth="%g" TargetColor="%s" ColorInfluence="%d" />\n' % (
        v, ty, st, dp, col, color_inf) for v, ty, st, dp, col in sorted(rows))

def sbc_with_tables(text, lay):
    """The.sbc text with its CustomMaterialTable (seven rows), its OreMappings (the layout's ore rows, inserted after the
 CustomMaterialTable when absent) and PlanetMaps (Material, Ores and Biome on) replaced; other text unchanged."""
    rows = "".join('      <Material Material="%s" Value="%d" MaxDepth="%d" />\n' % (n, v, MATERIAL_MAX_DEPTH) for n, v in MATERIALS)
    new = "<CustomMaterialTable>\n" + rows + "    </CustomMaterialTable>"
    out, n = re.subn(r"<CustomMaterialTable>.*?</CustomMaterialTable>", new, text, count=1, flags=re.S)
    if n != 1:
        raise ValueError("no CustomMaterialTable element in the .sbc")
    ores = "<OreMappings>\n" + ore_rows_xml(lay) + "    </OreMappings>"
    if re.search(r"<OreMappings>.*?</OreMappings>", out, re.S):
        out = re.sub(r"<OreMappings>.*?</OreMappings>", ores, out, count=1, flags=re.S)
    else:
        out = out.replace("</CustomMaterialTable>", "</CustomMaterialTable>\n    " + ores, 1)
    out, n = re.subn(r"<PlanetMaps [^>]*/>", '<PlanetMaps Material="true" Ores="true" Biome="true" Occlusion="false" />', out, count=1)
    if n != 1:
        raise ValueError("no PlanetMaps element in the .sbc")
    # the header comment of the repository's.sbc (S1) said there were no ore mappings; keep it true
    out = out.replace("No SurfaceDetail, EnvironmentItems, OreMappings or WeatherGenerators.",
                      "No SurfaceDetail, EnvironmentItems or WeatherGenerators; the OreMappings, the CustomMaterialTable and the\n"
                      "  biome map (_mat G) are the designed planet's (tools/planet/layout_4546b.json, S8a Stage C).", 1)
    return out

def sbc_table(text):
    """{value: material} of the.sbc's CustomMaterialTable."""
    m = re.search(r"<CustomMaterialTable>(.*?)</CustomMaterialTable>", text, re.S)
    out = {}
    if m:
        for mm in re.finditer(r'<Material\s+Material="([^"]+)"\s+Value="(\d+)"', m.group(1)):
            out[int(mm.group(2))] = mm.group(1)
    return out

def sbc_ores(text):
    """{value: (type, start, depth)} of the.sbc's OreMappings."""
    m = re.search(r"<OreMappings>(.*?)</OreMappings>", text, re.S)
    out = {}
    if m:
        for mm in re.finditer(r'<Ore\s+Value="(\d+)"\s+Type="([^"]+)"\s+Start="([^"]+)"\s+Depth="([^"]+)"', m.group(1)):
            out[int(mm.group(1))] = (mm.group(2), float(mm.group(3)), float(mm.group(4)))
    return out

def sbc_planet_maps(text):
    m = re.search(r"<PlanetMaps ([^>]*)/>", text)
    if not m:
        return {}
    return {k: v == "true" for k, v in re.findall(r'(\w+)="(\w+)"', m.group(1))}

# ------------------------------------------------------------------------------------------------- statistics
def design_stats(design, faces):
    """Per biome: pixels, depth range and mean of the final surface; ore pixels and deposits per row; the island top."""
    fr = [f for f in faces if f.face == 1][0]
    j0, j1, i0, i1 = fr.box
    y = decode_height(fr.heights[j0:j1, i0:i1])
    f = fr.fields
    bid = f["biome"]
    ring = (f["crater"] >= 0.5) & (f["edge"] <= 0.5)
    st = {"biomes": {}, "ores": {}}
    for k, key in enumerate(design.keys):
        sel = ring & (bid == k) if key != "craterEdge" else (f["edge"] > 0.5) & (f["rho"] <= design.lay["edge"]["radiusM"] + 600)
        d = -y[sel]
        st["biomes"][key] = {"pixels": int(sel.sum()), "depthMin": float(d.min()) if d.size else None,
                             "depthMax": float(d.max()) if d.size else None, "depthMean": float(d.mean()) if d.size else None}
    ob = fr.ores[j0:j1, i0:i1]
    for row in design.lay["ores"]["rows"]:
        m = np.isin(ob, ore_values(row))
        labels = label4(m)
        st["ores"][row["ore"]] = {"pixels": int(m.sum()), "deposits": int(labels.max()) if m.any() else 0,
                                  "depthMin": float(-y[m].max()) if m.any() else None, "depthMax": float(-y[m].min()) if m.any() else None}
    st["islandTopY"] = float(y[f["island"] < 1.0].max()) if np.any(f["island"] < 1.0) else None
    st["aboveSea"] = int(np.count_nonzero(y > 0.0))
    return st

def label4(mask):
    """4-connected component labels of a boolean array (0 = background); a small iterative flood fill."""
    lab = np.zeros(mask.shape, np.int32)
    n = 0
    ys, xs = np.nonzero(mask)
    for y0, x0 in zip(ys.tolist(), xs.tolist()):
        if lab[y0, x0]:
            continue
        n += 1
        stack = [(y0, x0)]
        lab[y0, x0] = n
        while stack:
            cy, cx = stack.pop()
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                yy, xx = cy + dy, cx + dx
                if 0 <= yy < mask.shape[0] and 0 <= xx < mask.shape[1] and mask[yy, xx] and not lab[yy, xx]:
                    lab[yy, xx] = n
                    stack.append((yy, xx))
    return lab

def ore_sites(design, faces):
    """The deposits: [{ore, type, value, x, z, seabedY, slope, pixels, startM, depthBelowM}]: the centre is the component's pixel
 nearest its centroid; (x, z) and seabedY are that height pixel's centre (the bicubic surface equals the pixel there) and lie
 inside the ore map pixel SE samples there (floor(2048 t) holds t = p / 2047, MyCubemapData.cs:40-45). The vein lies where
 SE's perpendicular depth below the surface d satisfies Start + 0.5 <= d <= Start + Depth + 0.5 (MyPlanetMaterialProvider.cs:754-762,
 SurfaceDepth = vertical depth x cos(slope),:1010): a vertical probe reaches it at (Start + 0.5) x sqrt(1 + slope^2)."""
    fr = [f for f in faces if f.face == 1][0]
    j0, j1, i0, i1 = fr.box
    y = decode_height(fr.heights[j0:j1, i0:i1])
    f = fr.fields
    ob = fr.ores[j0:j1, i0:i1]
    out = []
    for row in design.lay["ores"]["rows"]:
        lab = label4(np.isin(ob, ore_values(row)))
        for k in range(1, int(lab.max()) + 1):
            ys, xs = np.nonzero(lab == k)
            cy, cx = int(round(ys.mean())), int(round(xs.mean()))
            if lab[cy, cx] != k:
                cy, cx = int(ys[0]), int(xs[0])
            outcrop = int(np.count_nonzero(ob[ys, xs] == row.get("outcropValue", -1)))
            out.append({"ore": row["ore"], "type": row["type"], "value": row["value"], "outcropPixels": outcrop, "x": round(float(f["sx"][cy, cx]), 1),
                        "z": round(float(f["sz"][cy, cx]), 1), "seabedY": round(float(y[cy, cx]), 2),
                        "slope": round(float(fr.slope[cy, cx]), 3) if fr.slope is not None else None, "pixels": int(ys.size),
                        "startM": row["startM"], "depthBelowM": row["depthBelowM"]})
    return out

def label_components(mask):
    """(labels, count) of the 4-connected components of a boolean array (scipy.ndimage when installed, else label4)."""
    try:
        from scipy import ndimage  # noqa: E402 (optional: speed only)
        lab, n = ndimage.label(mask)
        return lab, int(n)
    except ImportError:
        lab = label4(mask)
        return lab, int(lab.max())

def component_centroids(lab, n):
    """(n, 2) centroids (row, column) and the pixel counts of labels 1..n."""
    ys, xs = np.nonzero(lab)
    lv = lab[ys, xs]
    cnt = np.bincount(lv, minlength=n + 1)[1:].astype(np.float64)
    cy = np.bincount(lv, ys, minlength=n + 1)[1:] / np.maximum(cnt, 1.0)
    cx = np.bincount(lv, xs, minlength=n + 1)[1:] / np.maximum(cnt, 1.0)
    return np.stack([cy, cx], axis=1), cnt

def nn_distances(pts, sample=3000, seed=1):
    """Nearest-neighbour distances (point units) of up to `sample` points (a fixed subsample) to all the others."""
    n = len(pts)
    if n < 2:
        return np.zeros(0)
    q_idx = np.arange(n) if n <= sample else np.sort(np.random.default_rng(seed).choice(n, sample, replace=False))
    out = np.empty(len(q_idx))
    for a in range(0, len(q_idx), 64):
        qi = q_idx[a:a + 64]
        d2 = ((pts[qi][:, None, :] - pts[None, :, :]) ** 2).sum(axis=-1)
        d2[np.arange(len(qi)), qi] = np.inf
        out[a:a + len(qi)] = np.sqrt(d2.min(axis=1))
    return out

GLOBE_BINS = (0.0, 100.0, 200.0, 300.0, 600.0, 1000.0, 1400.0, 1500.5, 1e9)

def planet_report(design, faces, heights=None, ores=None):
    """The numbers of S8a.C2.globe and S8a.N10.fields from the faces (their zones), with the files' heights and ores when given:
 per face the floor's in-band share, islands (land components), deposits, Clark-Evans R; overall the depth histogram,
 the ore share of the planet's seabed against SE's EarthLike and the share of deposits whose nearest neighbour lies
 within 250 m. Clark-Evans R = mean nearest-neighbour distance / (0.5 / sqrt(density)): 1 random, < 1 clustered, > 1 even."""
    lay = design.lay
    fr_ = lay["ores"]["fieldRule"]
    fl = (lay.get("planet") or {}).get("floorM", [100.0, 1500.0])
    res = faces[0].heights.shape[0]
    pitch = RS * (math.pi / 2.0) / (res - 1)
    rep = {"faces": [], "islands": 0, "pitchM": pitch}
    ch = (lay.get("planet") or {}).get("chains", {})
    area_rn = ch.get("platformRadius", 2.2) + 0.4                              # islands, shores and platforms are not the floor
    hist = np.zeros(len(GLOBE_BINS) - 1, np.int64)
    ore_px = seabed_px = 0
    r_num = r_den = 0.0
    near = total = 0
    ring = {"deposits": 0, "R": None}
    for fr in faces:
        h = heights[fr.face] if heights is not None else fr.heights
        ob = ores[fr.face] if ores is not None else fr.ores
        y = decode_height(h)
        floor = fr.planet & (fr.isl >= area_rn)
        depth = -y[floor]
        inband = float(np.mean((depth >= fl[0] - 0.5) & (depth <= fl[1] + 0.5))) if depth.size else 1.0
        hist += np.histogram(depth, GLOBE_BINS)[0]
        _l, n_land = label_components((y > 0.0) & fr.planet)
        rep["islands"] += n_land
        seabed = fr.planet & (y < -1.0)
        om = (ob > 0) & seabed
        lab, n = label_components(om)
        R = None
        if n >= 30:
            c, _cnt = component_centroids(lab, n)
            nn = nn_distances(c)
            A = float(seabed.sum())
            R = float(nn.mean() / (0.5 / math.sqrt(n / A)))
            r_num += R * n
            r_den += n
            near += int(np.count_nonzero(nn * pitch <= 250.0))
            total += nn.size
        if fr.ring.any():
            rl, rn_ = label_components((ob > 0) & fr.ring)
            ring["deposits"] = rn_
            if rn_ >= 30:
                c, _cnt = component_centroids(rl, rn_)
                ring["R"] = float(nn_distances(c).mean() / (0.5 / math.sqrt(rn_ / float(fr.ring.sum()))))
        ore_px += int(om.sum())
        seabed_px += int(seabed.sum())
        rep["faces"].append({"face": fr.face, "floorPixels": int(floor.sum()), "inBand": inband, "islands": n_land, "deposits": n,
                             "orePixels": int(om.sum()), "R": R})
    gb = (lay.get("planet") or {}).get("globeBands")
    if gb:
        #  the bands of a design come from its layout (design 3 keeps GLOBE_BINS below)
        cnt = {k: 0 for k in gb}
        tot = 0
        for fr in faces:
            h = heights[fr.face] if heights is not None else fr.heights
            dpt = -decode_height(h)[fr.planet & (fr.isl >= area_rn)]
            tot += dpt.size
            for k, b in gb.items():
                cnt[k] += int(np.count_nonzero((dpt >= b[0]) & (dpt <= b[1])))
        rep["bands"] = {k: (cnt[k] / max(1, tot), gb[k]) for k in gb}
    vr = lay["ores"].get("veinRule")
    if vr:
        # design 5: veins per km2 per class (connected pixels of the class's values) over the class's eligible seabed
        rows_by = {r["ore"]: r for r in lay["ores"]["rows"]}
        classes = []
        for cls in vr["classes"]:
            vals = set()
            for o in cls["ores"]:
                if o in rows_by:
                    vals.update(ore_values(rows_by[o]))
            veins = 0
            elig_px = 0
            for fr in faces:
                ob = ores[fr.face] if ores is not None else fr.ores
                h = heights[fr.face] if heights is not None else fr.heights
                yv = decode_height(h)
                _lab, n = label_components(np.isin(ob, list(vals)))
                veins += n
                elig_px += int(vein_eligible(design, cls, yv, fr.bid, fr.ring, fr.planet).sum()) if getattr(fr, "bid", None) is not None else 0
            km2 = elig_px * pitch * pitch / 1e6
            classes.append({"name": cls["name"], "veins": veins, "eligibleKm2": round(km2, 1), "perKm2": round(veins / km2, 2) if km2 > 0 else None,
                            "target": cls.get("targetPerKm2")})
        rep["veins"] = classes
    rep["hist"] = {"%g-%g" % (GLOBE_BINS[k], GLOBE_BINS[k + 1]): int(hist[k]) for k in range(len(hist))}
    floor_total = max(1, int(hist.sum()))
    rep["shelves"] = float((hist[1] + hist[2]) / floor_total)                 # 100-300 m
    rep["plains"] = float((hist[4] + hist[5] + hist[6]) / floor_total)        # 600-1,500 m
    rep["trenches"] = float(hist[6] / floor_total)                            # 1,400-1,500 m
    rep["oreShare"] = ore_px / max(1, seabed_px)
    rep["earthShare"] = fr_["earthShare"]
    rep["R"] = r_num / r_den if r_den else None
    rep["earthR"] = fr_.get("earthClarkEvansR")
    rep["near250"] = near / total if total else None
    rep["ring"] = ring
    rep["chains"] = int(lay.get("planet", {}).get("chains", {}).get("count", 0))
    return rep

def globe_rows(design, rep):
    """(globe ok, line), (fields ok, line) for S8a.C2.globe and S8a.N10.fields."""
    fl = (design.lay.get("planet") or {}).get("floorM", [100.0, 1500.0])
    every = all(f["floorPixels"] > 0 and f["inBand"] >= 0.995 for f in rep["faces"])
    if rep.get("bands"):
        b = rep["bands"]
        g_ok = every and rep["islands"] >= rep["chains"] and all(share >= band[2] for share, band in b.values())
        g = ("S8a.C2.globe every face sculpted (floor within %g-%g m off the islands: %s); %d islands above the sea (>= %d chains); %s; histogram %s" % (
            fl[0], fl[1], ", ".join("%d %.4f" % (f["face"], f["inBand"]) for f in rep["faces"]), rep["islands"], rep["chains"],
            ", ".join("%s %g-%g m %.1f %% (>= %.1f %%)" % (k, band[0], band[1], 100 * share, 100 * band[2]) for k, (share, band) in b.items()), rep["hist"]))
    else:
        g_ok = every and rep["islands"] >= rep["chains"] and rep["shelves"] >= 0.10 and rep["plains"] >= 0.30 and rep["trenches"] >= 0.002
    if rep.get("bands"):
        pass
    else:
        g = ("S8a.C2.globe every face sculpted (floor within %g-%g m off the islands: %s); %d islands above the sea (>= %d chains); "
         "shelves 100-300 m %.1f %%, plains 600-1,500 m %.1f %%, trench floors 1,400-1,500 m %.2f %% of the floor; histogram %s" % (
             fl[0], fl[1], ", ".join("%d %.4f" % (f["face"], f["inBand"]) for f in rep["faces"]), rep["islands"], rep["chains"],
             100 * rep["shelves"], 100 * rep["plains"], 100 * rep["trenches"], rep["hist"]))
    R, share, near = rep["R"], rep["oreShare"], rep["near250"]
    if rep.get("veins") is not None:
        # design 5: the veins row (the vein table within x0.5-x2, the ore share within SE's EarthLike +-35 %)
        okc = all(c["perKm2"] is not None and c["target"] and 0.5 * c["target"] <= c["perKm2"] <= 2.0 * c["target"] for c in rep["veins"] if c["eligibleKm2"] >= 1.0)
        f_ok = okc and 0.65 <= share / rep["earthShare"] <= 1.35
        f = ("S8a.N10.veins per class (veins per km2 of eligible seabed, target x0.5-x2): %s; ore share of the planet's seabed %.2f %% (SE's EarthLike %.2f %%, "
             "within +-35 %%); Clark-Evans R %s (veins on a jittered lattice: about 1.3, even)" % (
                 ", ".join("%s %s/km2 (target %s, %d veins on %.0f km2)" % (c["name"], c["perKm2"], c["target"], c["veins"], c["eligibleKm2"]) for c in rep["veins"]),
                 100 * share, 100 * rep["earthShare"], "%.2f" % R if R is not None else "n/a"))
        return (g_ok, g), (f_ok, f)
    f_ok = R is not None and R <= 0.6 and 0.65 <= share / rep["earthShare"] <= 1.35 and near is not None and near >= 0.8
    f = ("S8a.N10.fields deposits clustered: Clark-Evans R %s (<= 0.6; SE's EarthLike %.2f), %s of the deposits have a neighbour "
         "within 250 m (>= 80 %%); ore share of the planet's seabed %.2f %% (SE's EarthLike %.2f %%, within +-35 %%); ring R %s, %d deposits" % (
             "%.2f" % R if R is not None else "n/a", rep["earthR"] or float("nan"), "%.0f %%" % (100 * near) if near is not None else "n/a",
             100 * share, 100 * rep["earthShare"], "%.2f" % rep["ring"]["R"] if rep["ring"]["R"] is not None else "n/a", rep["ring"]["deposits"]))
    return (g_ok, g), (f_ok, f)

def sample_texcoord_v(x, y, z):
    """MyCubemapHelpers.CalculateSampleTexcoord for arrays: (face, tx, ty)."""
    ax, ay, az = np.abs(x), np.abs(y), np.abs(z)
    mx = (ax > ay) & (ax > az)
    my = ~(ax > ay) & (ay > az)
    with np.errstate(divide="ignore", invalid="ignore"):
        face = np.where(mx, np.where(x > 0, 2, 3), np.where(my, np.where(y > 0, 4, 5), np.where(z > 0, 1, 0)))
        tx = np.where(mx, np.where(x > 0, -z / ax, z / ax), np.where(my, np.where(y > 0, -x / ay, x / ay), np.where(z > 0, x / az, -x / az)))
        ty = np.where(mx, -y / ax, np.where(my, -z / ay, -y / az))
    return face, (tx + 1.0) * 0.5, (ty + 1.0) * 0.5

def globe_view(imgs, centre, up, size):
    """An orthographic view (size x size) of the six face images (float RGB) looking at `centre` with `up` on top."""
    c = np.array(centre, np.float64)
    c /= np.linalg.norm(c)
    u = np.array(up, np.float64)
    u -= c * float(np.dot(u, c))
    u /= np.linalg.norm(u)
    r = np.cross(u, c)
    s = np.linspace(-1.0, 1.0, size)
    X, Y = np.meshgrid(s, -s)
    inside = X * X + Y * Y <= 1.0
    Z = np.sqrt(np.clip(1.0 - X * X - Y * Y, 0.0, 1.0))
    d = X[..., None] * r + Y[..., None] * u + Z[..., None] * c
    face, tx, ty = sample_texcoord_v(d[..., 0], d[..., 1], d[..., 2])
    img = np.zeros((size, size, 3), np.float64) + 12.0
    for f, im in imgs.items():
        n = im.shape[0]
        m = inside & (face == f)
        img[m] = im[np.clip(np.rint(ty[m] * (n - 1)).astype(np.intp), 0, n - 1), np.clip(np.rint(tx[m] * (n - 1)).astype(np.intp), 0, n - 1)]
    img[inside] *= (0.45 + 0.55 * Z[inside])[:, None]
    return img

def write_globe_previews(design, faces, out_dir):
    """sculpt-globe.png (four orthographic views: the site, its antipode, east and west limbs) and sculpt-ores-face1.png (face 1
 at half resolution: the ring, the moat and the planet's ore fields)."""
    written = []
    res = faces[0].heights.shape[0]
    step = max(1, res // 1024)
    imgs = {}
    for fr in faces:
        y = decode_height(fr.heights[::step, ::step])
        imgs[fr.face] = _depth_colour(-y) * (0.45 + 0.55 * _hillshade(y, RS * 2.0 / (res - 1) * step)[..., None])
    size = 640
    views = [((0, 0, 1), (0, -1, 0)), ((0, 0, -1), (0, -1, 0)), ((-1, 0, 0), (0, -1, 0)), ((1, 0, 0), (0, -1, 0))]
    canvas = np.zeros((2 * size, 2 * size, 3), np.float64)
    for k, (c, u) in enumerate(views):
        canvas[(k // 2) * size:(k // 2 + 1) * size, (k % 2) * size:(k % 2 + 1) * size] = globe_view(imgs, c, u, size)
    p = os.path.join(out_dir, "sculpt-globe.png")
    stops = [("land (above the sea)", (160, 180, 115)), ("0 m", (150, 235, 230)), ("200 m", (30, 130, 190)), ("500 m", (20, 70, 150)),
             ("1,000 m", (15, 35, 100)), ("1,500 m", (10, 20, 60)), ("3,000 m (moat, void floor)", (4, 6, 18))]
    _legend(canvas, stops, "DE_4546B globe: top left the site (north up), top right its antipode, bottom the east and west limbs", p)
    written.append(p)
    fr1 = [f for f in faces if f.face == 1][0]
    y = decode_height(fr1.heights[::step * 2, ::step * 2])
    ob = fr1.ores[::step * 2, ::step * 2]
    base = np.repeat((40 + 160 * np.clip(1.0 - (-y) / 1500.0, 0.0, 1.0))[..., None], 3, axis=2) * (0.5 + 0.5 * _hillshade(y, RS * 4.0 / (res - 1) * step)[..., None])
    grown = ob.copy()
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            sh = np.roll(np.roll(ob, dy, 0), dx, 1)
            grown = np.where((grown == 0) & (sh > 0), sh, grown)
    for row in design.lay["ores"]["rows"]:
        base[np.isin(grown, ore_values(row))] = ORE_COLOURS.get(row["ore"], (255, 0, 255))
    p = os.path.join(out_dir, "sculpt-ores-face1.png")
    pitch_km = RS * (math.pi / 2.0) / 1000.0
    _legend(base[::-1, ::-1], [("%s %s B=%d, ring %s m, planet %s m" % (r["ore"], r["type"], r["value"], r["depthM"], ore_bands(r)[1]),
                               ORE_COLOURS.get(r["ore"], (255, 0, 255))) for r in design.lay["ores"]["rows"]],
            "face 1 (%.0f km square, east right, north up): the ring at the centre, the moat, the planet's ore fields (drawn 3 px wide)" % pitch_km, p)
    written.append(p)
    return written

ORE_NAME_OF = {"Iron_02": "Iron", "Nickel_01": "Nickel", "Silicon_01": "Silicon", "Cobalt_01": "Cobalt", "Magnesium_01": "Magnesium",
               "Silver_01": "Silver", "Gold_01": "Gold", "Platinum_01": "Platinum", "Uraninite_01": "Uranium"}

def look_points_from_files(mod_dir, per_biome=2, rmin=13500.0, rmax=40000.0, inside_m=2000.0, candidates=4000, max_depth=None, exclude=()):
    """The look part's far points: per Subnautica biome named by the deployed planet's face-1
 biome map (G) beyond the ring, the first per_biome points of a seeded golden-angle spiral between rmin and rmax (SN m from the
 site) whose G is the same at the point and at 8 points on circles of inside_m / 2 and inside_m around it (well inside the
 biome), never above the sea. a test run (nl 20261024-033738): max_depth keeps only seabeds no deeper than that (Subnautica's noon
 light, so a black frame is never the biome's own darkness) and the biomes in exclude (design 3's void at -3,000 m) are left out.
 [{biome, x, z, seabedY, rho}], deterministic from the files; None when they are missing."""
    files, _missing = read_mod_files(mod_dir)
    if files is None:
        return None
    _w, _h, _bd, _ct, hs = png_decode(files["back.png"])
    _w, _h, _bd, _ct, ms = png_decode(files["back_mat.png"])
    res = hs.shape[0]

    def pixel(sx, sz):
        d = sn_to_dir(sx, sz)
        f, tx, ty = sample_texcoord(d[0], d[1], d[2])
        if f != 1:
            return None
        return min(res - 1, max(0, int(round(ty * (res - 1))))), min(res - 1, max(0, int(round(tx * (res - 1)))))
    golden = math.pi * (3.0 - math.sqrt(5.0))
    phi = (math.sqrt(5.0) - 1.0) / 2.0
    out, count = [], {}
    # narrow biomes (ridges, trenches) are rarely 2 km wide: later passes halve the inside distance for the biomes still short
    for pass_ in range(3):
        inside = inside_m / (2 ** pass_)
        _look_pass(out, count, per_biome, rmin, rmax, inside, candidates, golden, phi, pixel, ms, hs, max_depth, exclude)
    return out

def _look_pass(out, count, per_biome, rmin, rmax, inside_m, candidates, golden, phi, pixel, ms, hs, max_depth=None, exclude=()):
    for k in range(candidates):
        r = rmin + (rmax - rmin) * ((k * phi) % 1.0)
        a = k * golden
        sx, sz = r * math.sin(a), r * math.cos(a)
        pc = pixel(sx, sz)
        if pc is None:
            continue
        g = int(ms[pc[0], pc[1], 1])
        if g < 1 or g > len(BIOME_NAMES):
            continue
        name = BIOME_NAMES[g - 1]
        if count.get(name, 0) >= per_biome or name in exclude:
            continue
        y = float(decode_height(hs[pc[0], pc[1]]))
        if y > -12.0 or (max_depth is not None and y < -float(max_depth)):
            continue
        ok = True
        for rr in (0.5 * inside_m, inside_m):
            for q in range(8):
                b_ = 2.0 * math.pi * q / 8.0
                pq = pixel(sx + rr * math.sin(b_), sz + rr * math.cos(b_))
                if pq is None or int(ms[pq[0], pq[1], 1]) != g:
                    ok = False
                    break
            if not ok:
                break
        if not ok:
            continue
        if any(abs(o["x"] - sx) < 1.0 and abs(o["z"] - sz) < 1.0 for o in out):
            continue
        count[name] = count.get(name, 0) + 1
        out.append({"biome": name, "x": round(sx, 1), "z": round(sz, 1), "seabedY": round(y, 1), "rho": round(r), "insideM": inside_m})

def ore_sites_from_files(mod_dir, radius_m=13050.0):
    """The deposits of a deployed world mod:
 face 1's ore map (B) and heights within radius_m of the site, B values read through the mod's.sbc OreMappings (a Type's
 rows, its outcrop row included, are one ore); the same rows as ore_sites (the deposit's deepest row's Start/Depth). None when
 the files or the.sbc are missing."""
    files, _missing = read_mod_files(mod_dir)
    sbc_path = os.path.join(mod_dir, "Data", "PlanetGeneratorDefinitions.sbc")
    if files is None or not os.path.isfile(sbc_path):
        return None
    with open(sbc_path, encoding="utf-8") as fh:
        rows = sbc_ores(fh.read())
    if not rows:
        return []
    _w, _h, _bd, _ct, hs = png_decode(files["back.png"])
    _w, _h, _bd, _ct, ms = png_decode(files["back_mat.png"])
    res = hs.shape[0]
    j0, j1, i0, i1 = site_box(res, radius_m)
    y = decode_height(hs[j0:j1, i0:i1])
    ob = ms[j0:j1, i0:i1, 2]
    x, yy, z = face_dirs(1, res, j0, j1, i0, i1)
    sx, sz = sn_xz(x, yy, z)
    slope = surface_slope(res, y, j0, i0)
    by_type = {}
    for v, (ty, st, dp) in rows.items():
        by_type.setdefault(ty, []).append((v, st, dp))
    out = []
    for ty, vals in sorted(by_type.items()):
        lab = label4(np.isin(ob, [v for v, _s, _d in vals]))
        st, dp = max(((s_, d_) for _v, s_, d_ in vals), key=lambda sd: sd[0])        # the deep row (the outcrop row starts at 0)
        for k in range(1, int(lab.max()) + 1):
            ys, xs = np.nonzero(lab == k)
            cy, cx = int(round(ys.mean())), int(round(xs.mean()))
            if lab[cy, cx] != k:
                cy, cx = int(ys[0]), int(xs[0])
            out.append({"ore": ORE_NAME_OF.get(ty, ty), "type": ty, "value": int(vals[0][0]), "x": round(float(sx[cy, cx]), 1), "z": round(float(sz[cy, cx]), 1),
                        "seabedY": round(float(y[cy, cx]), 2), "slope": round(float(slope[cy, cx]), 3), "pixels": int(ys.size), "startM": st, "depthBelowM": dp,
                        "source": "files"})
    return out

# ------------------------------------------------------------------------------------------------- checks
def catmull_rom_at(heights, px, py):
    """SE's bicubic (Tau 0.5) of a face at pixel coordinates (px, py) (texcoord * (res - 1)), within the face
 (indices clamped at the border; the game wraps to the neighbour face, irrelevant near the site)."""
    res = heights.shape[0]
    x0, y0 = int(math.floor(px)), int(math.floor(py))
    s, t = px - x0, py - y0

    def w(u):
        return (-0.5 * u ** 3 + u * u - 0.5 * u, 1.5 * u ** 3 - 2.5 * u * u + 1.0,
                -1.5 * u ** 3 + 2.0 * u * u + 0.5 * u, 0.5 * u ** 3 - 0.5 * u * u)

    wx, wy = w(s), w(t)
    acc = 0.0
    for jj in range(4):
        yy = min(res - 1, max(0, y0 - 1 + jj))
        row = 0.0
        for ii in range(4):
            xx = min(res - 1, max(0, x0 - 1 + ii))
            row += wx[ii] * (heights[yy, xx] / 65535.0)
        acc += wy[jj] * row
    return INNER + acc * HEIGHT_RATIO - RS

def surface_y_sn(faces_by_index, sx, sz):
    """SE's bicubic seabed at an SN column (x, z) under site v1 (SN y): MyPlanet.GetClosestSurfacePointGlobal's radius
 along the column's direction (MyPlanetShapeProvider.ProjectToSurface,:500-512) minus the sea."""
    d = sn_to_dir(sx, sz)
    face, tx, ty = sample_texcoord(*d)
    h = faces_by_index[face]
    res = h.shape[0]
    return catmull_rom_at(h, tx * (res - 1), ty * (res - 1))

def check_flat(heights_by_face, lay, n_points=2000, seed=7):
    """S8a.C2.flat: every pixel with rhoInf <= flatRhoInfM is 0, and the bicubic surface at n_points columns with
 rhoInf <= 2,300 m is SN y -3,000 +- 0.01 m (all 16 neighbours 0 makes it exactly FLOOR_Y)."""
    res = heights_by_face[1].shape[0]
    bad_pixels = 0
    flat = lay["crater"]["flatRhoInfM"]
    j0, j1, i0, i1 = site_box(res, flat * 1.5)
    x, y, z = face_dirs(1, res, j0, j1, i0, i1)
    sx, sz = sn_xz(x, y, z)
    sel = np.maximum(np.abs(sx), np.abs(sz)) <= flat
    bad_pixels += int(np.count_nonzero(heights_by_face[1][j0:j1, i0:i1][sel]))
    rng = random.Random(seed)
    worst = 0.0
    for _ in range(n_points):
        sx_, sz_ = rng.uniform(-2300.0, 2300.0), rng.uniform(-2300.0, 2300.0)
        yb = surface_y_sn(heights_by_face, sx_, sz_)
        worst = max(worst, abs(yb - (-3000.0)))
    return bad_pixels, worst

def max_surface(heights, island_zone=None, sub=4, top_n=4000):
    """(highest pixel y, highest bicubic y) of a face, outside island_zone (a bool array) or everywhere; the bicubic is
 sampled around the highest pixels (Catmull-Rom overshoot)."""
    y = decode_height(heights)
    if island_zone is not None:
        y = np.where(island_zone, -1e9, y)
    best_pix = float(y.max())
    thr = best_pix - 25.0
    js, is_ = np.nonzero(y >= thr)
    order = np.argsort(-y[js, is_])[:top_n]
    top = float(heights.shape[0] - 1)
    best_spline = -1e9
    for k in order.tolist():
        j, i = int(js[k]), int(is_[k])
        for a in range(sub):
            for b_ in range(sub):
                px = min(top, max(0.0, i - 0.5 + a / sub))
                py = min(top, max(0.0, j - 0.5 + b_ / sub))
                best_spline = max(best_spline, catmull_rom_at(heights, px, py))
    return best_pix, best_spline

# ------------------------------------------------------------------------------------------------- preview
def _depth_colour(depth):
    """Depth (m, positive down) -> RGB: sand-green land, cyan shallows, blue mid, navy deep, near-black at the floor."""
    stops = [(-80.0, (120, 150, 80)), (0.0, (200, 210, 150)), (0.01, (150, 235, 230)), (60.0, (70, 200, 215)), (200.0, (30, 130, 190)),
             (500.0, (20, 70, 150)), (1000.0, (15, 35, 100)), (1500.0, (10, 20, 60)), (3000.0, (4, 6, 18))]
    d = np.asarray(depth, np.float64)
    out = np.zeros(d.shape + (3,), np.float64)
    out[d <= stops[0][0]] = stops[0][1]
    for k in range(len(stops) - 1):
        d0, c0 = stops[k]
        d1, c1 = stops[k + 1]
        sel = (d > d0) & (d <= d1)
        t = (d[sel] - d0) / (d1 - d0)
        for c in range(3):
            out[..., c][sel] = c0[c] + (c1[c] - c0[c]) * t
    out[d > stops[-1][0]] = stops[-1][1]
    return out

def _hillshade(y, pitch):
    gy, gx = np.gradient(y, pitch)
    nx, ny, nz_ = -gx, -gy, np.ones_like(y)
    n = np.sqrt(nx * nx + ny * ny + nz_ * nz_)
    lx, ly, lz = -0.5, -0.6, 0.62
    ll = math.sqrt(lx * lx + ly * ly + lz * lz)
    return np.clip((nx * lx + ny * ly + nz_ * lz) / (n * ll), 0.0, 1.0)

MAT_COLOURS = {1: (226, 208, 150), 2: (110, 140, 90), 3: (200, 120, 150), 4: (150, 110, 170), 5: (90, 120, 150),
               6: (55, 60, 75), 7: (90, 190, 70)}
BIOME_COLOURS = {"safeShallows": (120, 230, 220), "kelpForest": (60, 160, 60), "grassyPlateaus": (210, 190, 90),
                 "mushroomForest": (170, 90, 200), "sparseReef": (230, 140, 120), "grandReef": (90, 120, 240),
                 "dunes": (230, 220, 170), "mountains": (150, 150, 150), "underwaterIslands": (110, 90, 70),
                 "bloodKelp": (170, 30, 40), "crashZone": (240, 160, 40), "lostRiver": (40, 210, 160), "craterEdge": (15, 15, 30)}
ORE_COLOURS = {"Iron": (230, 120, 60), "Nickel": (150, 200, 120), "Silicon": (240, 240, 210), "Cobalt": (60, 110, 255),
               "Magnesium": (255, 255, 120), "Silver": (210, 220, 230), "Gold": (255, 200, 0), "Platinum": (180, 255, 255),
               "Uranium": (120, 255, 60)}

def _legend(img, entries, title, out_path):
    """Writes img (H, W, 3 floats) with a legend column (Pillow; without it the PNG has no text and a.txt legend is written)."""
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        with open(out_path, "wb") as fh:
            fh.write(png_rgb(img))
        with open(os.path.splitext(out_path)[0] + "-legend.txt", "w", encoding="utf-8") as fh:
            fh.write(title + "\n" + "".join("%s %s\n" % (name, rgb) for name, rgb in entries))
        return
    h, w = img.shape[:2]
    lw = 460
    im = Image.new("RGB", (w + lw, max(h, 30 + 22 * len(entries))), (250, 250, 250))
    im.paste(Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)), (0, 0))
    dr = ImageDraw.Draw(im)
    dr.text((w + 10, 8), title, fill=(0, 0, 0))
    for k, (name, rgb) in enumerate(entries):
        y0 = 30 + 22 * k
        dr.rectangle([w + 10, y0, w + 28, y0 + 16], fill=tuple(int(c) for c in rgb), outline=(0, 0, 0))
        dr.text((w + 36, y0 + 2), name, fill=(0, 0, 0))
    im.save(out_path, format="PNG")

def write_previews(design, faces, out_dir, log=print):
    """The project's look at the design (the face-1 box: east right, north up): biomes with a legend, depth with hillshade,
 materials, ores over the depth map, plus the depth profile along the cardinal axes (text) and the stats (JSON)."""
    os.makedirs(out_dir, exist_ok=True)
    fr = [f for f in faces if f.face == 1][0]
    res = fr.heights.shape[0]
    j0, j1, i0, i1 = fr.box
    y = decode_height(fr.heights[j0:j1, i0:i1])
    f = fr.fields
    pitch = RS * 2.0 / (res - 1)
    shade = _hillshade(y, pitch)
    # face 1: column i grows with tx = x/|z| = -SN east, row j with ty = -y/|z| = +SN north (site v1: E0 = -X, N0 = -Y):
 # both axes flipped so SN +x (east) is right and +z (north) is up
    flip = lambda a: a[::-1, ::-1]   # noqa: E731
    km = (j1 - j0) * pitch / 1000.0
    written = []
    lay = design.lay
    # biomes
    bio = np.zeros(y.shape + (3,), np.float64)
    for k, key in enumerate(design.keys):
        bio[f["biome"] == k] = BIOME_COLOURS.get(key, (128, 128, 128))
    bio = bio * (0.55 + 0.45 * shade[..., None])
    entries = [("%s -> GetBiome %s (G %d), %d-%d m" % (b["key"], b["snName"], BIOME_NAMES.index(b["snName"]) + 1, b["band"][0], b["band"][1]),
                BIOME_COLOURS.get(b["key"], (128, 128, 128))) for b in lay["biomes"]]
    p = os.path.join(out_dir, "sculpt-biomes.png")
    _legend(flip(bio), entries, "DE_4546B biomes, %.1f km square, east right, north up" % km, p)
    written.append(p)
    # depth
    col = _depth_colour(-y) * (0.35 + 0.65 * shade[..., None])
    stops = [("land (above the sea)", (160, 180, 115)), ("0 m", (150, 235, 230)), ("60 m", (70, 200, 215)), ("200 m", (30, 130, 190)),
             ("500 m", (20, 70, 150)), ("1,000 m", (15, 35, 100)), ("1,500 m", (10, 20, 60)), ("3,000 m (void floor)", (4, 6, 18))]
    p = os.path.join(out_dir, "sculpt-depth.png")
    _legend(flip(col), stops, "seabed depth (SE bicubic pixels), hillshade", p)
    written.append(p)
    # materials
    mats = fr.materials[j0:j1, i0:i1]
    mc = np.zeros(mats.shape + (3,), np.float64)
    for v, rgb in MAT_COLOURS.items():
        mc[mats == v] = rgb
    inv = {v: n for n, v in MATERIALS}
    p = os.path.join(out_dir, "sculpt-materials.png")
    _legend(flip(mc * (0.45 + 0.55 * shade[..., None])), [("%d %s" % (v, inv[v]), rgb) for v, rgb in sorted(MAT_COLOURS.items())],
            "surface materials (CustomMaterialTable R)", p)
    written.append(p)
    # ores over a grey depth map; deposits drawn 3 px larger so single pixels show
    base = np.repeat((40 + 160 * np.clip(1.0 - (-y) / 1000.0, 0.0, 1.0))[..., None], 3, axis=2) * (0.5 + 0.5 * shade[..., None])
    ob = fr.ores[j0:j1, i0:i1]
    grown = ob.copy()
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            sh = np.roll(np.roll(ob, dy, 0), dx, 1)
            grown = np.where((grown == 0) & (sh > 0), sh, grown)
    oc = base.copy()
    for row in lay["ores"]["rows"]:
        oc[np.isin(grown, ore_values(row))] = ORE_COLOURS.get(row["ore"], (255, 0, 255))
    p = os.path.join(out_dir, "sculpt-ores.png")
    _legend(flip(oc), [("%s %s B=%d, %d-%d m, %s" % (r["ore"], r["type"], r["value"], r["depthM"][0], r["depthM"][1], r["rarity"]),
                        ORE_COLOURS.get(r["ore"], (255, 0, 255))) for r in lay["ores"]["rows"]],
            "ore deposits (B channel; drawn 3 px wide)", p)
    written.append(p)
    # overview of the six faces (depth), downsampled
    step = max(1, res // 256)
    tile = res // step
    canvas = np.zeros((tile * 3, tile * 4, 3), np.float64)
    pos = {4: (0, 1), 3: (1, 0), 1: (1, 1), 2: (1, 2), 0: (1, 3), 5: (2, 1)}
    by = {fr_.face: fr_ for fr_ in faces}
    for fi, (r, cc) in pos.items():
        yy = decode_height(by[fi].heights[::step, ::step])
        canvas[r * tile:(r + 1) * tile, cc * tile:(cc + 1) * tile] = _depth_colour(-yy) * (0.4 + 0.6 * _hillshade(yy, pitch * step)[..., None])
    p = os.path.join(out_dir, "sculpt-overview.png")
    with open(p, "wb") as fh:
        fh.write(png_rgb(canvas))
    written.append(p)
    # profiles along the four axes (every 250 m) and the stats
    lines = ["SN seabed y (SE bicubic) along the axes from the site, every 250 m"]
    hb = {fr_.face: fr_.heights for fr_ in faces}
    for name, (ux, uz) in (("east +x", (1, 0)), ("north +z", (0, 1)), ("west -x", (-1, 0)), ("south -z", (0, -1))):
        vals = ["%6.0f" % surface_y_sn(hb, ux * r, uz * r) for r in range(2000, 13501, 250)]
        lines.append("%-9s %s" % (name, " ".join(vals)))
    p = os.path.join(out_dir, "sculpt-profiles.txt")
    with open(p, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")
    written.append(p)
    if design.planet.on:
        written += write_globe_previews(design, faces, out_dir)
    st = design_stats(design, faces)
    if design.planet.on and faces[0].planet is not None:
        st["planet"] = planet_report(design, faces)
    p = os.path.join(out_dir, "sculpt-stats.json")
    with open(p, "w", encoding="utf-8") as fh:
        json.dump(st, fh, indent=1)
    written.append(p)
    if log:
        for p in written:
            log("  preview %s" % p)
        log("  site box: face 1 rows %d..%d, columns %d..%d = about %.1f km square; east right, north up" % (j0, j1 - 1, i0, i1 - 1, km))
    return written

# ------------------------------------------------------------------------------------------------- modes
def summarize(design, faces, log=print):
    st = design_stats(design, faces)
    for key, b in st["biomes"].items():
        if b["pixels"]:
            log("  %-18s %7d px  depth %6.0f..%6.0f m (mean %5.0f)" % (key, b["pixels"], b["depthMin"], b["depthMax"], b["depthMean"]))
        else:
            log("  %-18s       0 px" % key)
    for ore, o in st["ores"].items():
        log("  ore %-10s %5d px in %4d deposits%s" % (ore, o["pixels"], o["deposits"], "" if not o["pixels"] else
                                                       ", seabed %.0f..%.0f m" % (o["depthMin"], o["depthMax"])))
    log("  island top SN y %.1f; %d pixels above the sea" % (st["islandTopY"] if st["islandTopY"] is not None else float("nan"), st["aboveSea"]))
    if design.planet.on and faces[0].planet is not None:
        counts = sum(f.biome_counts for f in faces)
        for b in design.lay["biomes"]:
            if b.get("zone") == "planet":
                log("  %-18s %8d px (planet)" % (b["key"], int(counts[design.keys.index(b["key"])])))
        rep = planet_report(design, faces)
        st["planet"] = rep
        for good, line in globe_rows(design, rep):
            log("  %s %s" % ("OK  " if good else "FAIL", line))
    if design.lay["crater"].get("rim"):
        #  (design 5): no pixel of the biome map names Subnautica's void (its fog: the dead-zone look)
        nv = sum(int(np.count_nonzero(f.biomes == VOID_G)) for f in faces)
        log("  %s S8a.C2.novoid no pixel's biome G is void (%d)" % ("OK  " if nv == 0 else "FAIL", nv))
    return st

def island_zone_of(fr, lay):
    zone = np.zeros(fr.heights.shape, bool)
    if fr.box is not None and fr.fields is not None:
        j0, j1, i0, i1 = fr.box
        zone[j0:j1, i0:i1] = fr.fields["island"] < 1.25
    return zone

DESIGN3_PIN_128 = 0x6a662091d47c605d      # design 3 (layout_4546b.json) at 128 px,

# planet v2 R8 (docs/S8A-PLANET-V2.md section 2, the ore density): the layout's runtime rules beside the 12 PNGs, read by the SE plugin
# at world up (Wire S8aPlanetRules: the same file name, magic and keys); a layout without ores.crater (design 3) writes none
PLANET_RULES_FILE = "de-planet-rules.txt"
PLANET_RULES_MAGIC = "de-planet-rules 1"

def planet_rules_text(lay):
    """The rules file's text for a layout, or None (no ores.crater: today's rules, the file is removed)."""
    cr = (lay.get("ores") or {}).get("crater")
    if not cr:
        return None
    cube = bool(cr.get("cubeRule", True))
    per = float(cr.get("depositsPerKm2", 0.0))
    if per < 0:
        raise ValueError("ores.crater.depositsPerKm2 < 0")
    return "%s\ncrater.cubeRule=%s\ncrater.depositsPerKm2=%s\n" % (PLANET_RULES_MAGIC, "true" if cube else "false", format(per, "g"))

def do_selftest():
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print("%-4s %s %s" % ("OK" if cond else "FAIL", name, detail))

    #  the --out gate
    appdata = os.environ.get("APPDATA") or r"C:\Users\user\AppData\Roaming"
    tmp = tempfile.mkdtemp(prefix="sculpt_gate_")
    try:
        check("gate: SE Mods refused even with --owner-go", refuse_out(os.path.join(appdata, "SpaceEngineers", "Mods", "DeepEngineers.World"), True, RESOLUTION) is not None)
        check("gate: SE Saves refused", refuse_out(os.path.join(appdata, "SpaceEngineers", "Saves", "x", "DE Lab"), True, RESOLUTION) is not None)
        check("gate: a game install refused", refuse_out(r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content", True, RESOLUTION) is not None)
        check("gate: SNAppData refused", refuse_out(r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica\SNAppData\x", True, RESOLUTION) is not None)
        check("gate: the repository mod needs --owner-go", refuse_out(REPO_MOD_DIR, False, RESOLUTION) is not None and refuse_out(REPO_MOD_DIR, True, RESOLUTION) is None)
        check("gate: the repository mod takes 2048 px only", refuse_out(REPO_MOD_DIR, True, 256) is not None)
        check("gate: another folder needs --owner-go", refuse_out(os.path.join(REPO, "docs", "x"), False, RESOLUTION) is not None)
        check("gate: a scratch folder needs none", refuse_out(os.path.join(tmp, "mod"), False, 256) is None and refuse_out(os.path.join(REPO, "tests", "out", "x"), False, 256) is None)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    # encoding
    check("encode floor", int(encode_height(np.array([FLOOR_Y]))[0]) == 0)
    check("encode -3000", int(encode_height(np.array([-3000.0]))[0]) == 0, "(0.0039 m above the floor rounds to 0)")
    h = encode_height(np.array([-60.0, -420.0, -15.0, 45.0]))
    back = decode_height(h)
    check("encode round trip", np.all(np.abs(back - np.array([-60.0, -420.0, -15.0, 45.0])) <= HEIGHT_RATIO / 65535.0 / 2 + 1e-9), "%r" % back.tolist())
    check("unit", abs(HEIGHT_RATIO / 65535.0 - 0.15564) < 1e-4, "%.5f m per unit" % (HEIGHT_RATIO / 65535.0))
    # face mapping = CalculateSampleTexcoord inverse
    rng = random.Random(1)
    worst = 0.0
    for f in range(6):
        res = 33
        x, y, z = face_dirs(f, res)
        for _ in range(40):
            i, j = rng.randrange(1, res - 1), rng.randrange(1, res - 1)
            face, tx, ty = sample_texcoord(float(x[j, i]), float(y[j, i]), float(z[j, i]))
            worst = max(worst, abs(tx - i / (res - 1)), abs(ty - j / (res - 1)))
            if face != f:
                worst = 9.0
    check("face mapping", worst < 1e-12, "max texcoord error %.2e" % worst)
    face, tx, ty = sample_texcoord(0.0, 0.0, 1.0)
    check("anchor on face 1 centre", face == 1 and tx == 0.5 and ty == 0.5)
    xb, yb, zb = face_dirs(1, 65, 10, 20, 30, 40)
    xa, ya, za = face_dirs(1, 65)
    check("face_dirs box = the face's slice", np.array_equal(xb, xa[10:20, 30:40]) and np.array_equal(zb, za[10:20, 30:40]))
    # site-v1 F against the repository's reference
    try:
        sys.path.insert(0, os.path.join(REPO, "tools", "worldmap"))
        import site_v1  # noqa: E402
        site = site_v1.SITE_V1
        worst = 0.0
        for _ in range(50):
            sx, sz = rng.uniform(-20000, 20000), rng.uniform(-20000, 20000)
            d = sn_to_dir(sx, sz)
            ax, az = sn_xz(np.array([d[0]]), np.array([d[1]]), np.array([d[2]]))
            P = site.sn_to_se((sx, 0.0, sz))
            q = site.se_to_sn(P)
            worst = max(worst, abs(float(ax[0]) - q[0]), abs(float(az[0]) - q[2]), abs(float(ax[0]) - sx), abs(float(az[0]) - sz))
            rel = (P[0] - site.C[0], P[1] - site.C[1], P[2] - site.C[2])
            n = math.sqrt(rel[0] ** 2 + rel[1] ** 2 + rel[2] ** 2)
            worst = max(worst, RS * max(abs(rel[0] / n - d[0]), abs(rel[1] / n - d[1]), abs(rel[2] / n - d[2])))
        check("site v1 map", worst < 1e-6, "max |delta| %.2e m vs tools/worldmap/site_v1.py" % worst)
    except ImportError as e:
        check("site v1 map", False, "tools/worldmap/site_v1.py not importable: %s" % e)
    # the layout and the biome names (the Wire table's Python twin)
    lay = load_layout()
    try:
        import s8a_biomes  # noqa: E402
        check("biome names = s8a_biomes.NAMES (biome.names)", tuple(s8a_biomes.NAMES) == BIOME_NAMES, "%s" % (s8a_biomes.NAMES,))
        check("ore subtypes in the converter table", all(r["type"] in s8a_biomes.SUBTYPES for r in lay["ores"]["rows"]))
    except ImportError as e:
        check("biome names = s8a_biomes.NAMES", False, str(e))
    bad = copy.deepcopy(lay)
    bad["biomes"][0]["snName"] = "nowhere"
    try:
        check_layout(bad)
        check("layout: an unknown snName is refused", False)
    except ValueError:
        check("layout: an unknown snName is refused", True)
    # a small design: crater rule, determinism, the seed varies the detail only, limits, maps, island
    res = 513
    da = Design(lay, 11)
    faces_a = sculpt_all(da, res, keep=True, log=None)
    faces_b = sculpt_all(Design(lay, 11), res, keep=True, log=None)
    same = all(np.array_equal(a.heights, b.heights) and np.array_equal(a.materials, b.materials) and np.array_equal(a.biomes, b.biomes)
               and np.array_equal(a.ores, b.ores) for a, b in zip(faces_a, faces_b))
    check("deterministic", same)
    faces_c = sculpt_all(Design(lay, 12), res, keep=True, log=None)
    dh = np.abs(decode_height(faces_a[1].heights) - decode_height(faces_c[1].heights))
    check("--seed changes the detail only (<= detail amplitude x 2 + guard)", 0 < dh.max() <= 2 * lay["detail"]["amplitudeM"] + 1.0
          and np.array_equal(faces_a[1].biomes, faces_c[1].biomes), "max %.2f m" % dh.max())
    hb = {f.face: f.heights for f in faces_a}
    bad_px, _ = check_flat(hb, lay, n_points=0)
    check("crater pixels 0", bad_px == 0)
    check("anchor pixel 0", int(faces_a[1].heights[res // 2, res // 2]) == 0)
    prep = planet_report(da, faces_a)
    check("every face sculpted (Stage E: the floor within floorM off the islands)", all(f["floorPixels"] > 0 and f["inBand"] >= 0.995 for f in prep["faces"]),
          "%s" % [(f["face"], round(f["inBand"], 4)) for f in prep["faces"]])
    check("island chains above the sea", prep["islands"] >= prep["chains"], "%d land components, %d chains" % (prep["islands"], prep["chains"]))
    check("ore fields on the planet", all(f["orePixels"] > 0 for f in prep["faces"]), "%s" % [f["orePixels"] for f in prep["faces"]])
    rare_ok = True
    for fr_ in faces_a:
        for row in lay["ores"]["rows"]:
            pb = ore_bands(row)[3]
            if pb:
                m = np.isin(fr_.ores, ore_values(row)) & fr_.planet & ~fr_.ring
                rare_ok &= bool(np.all(np.isin(fr_.bid[m], [da.keys.index(k) for k in pb])))
    check("rare ores on the planet only in their planet biomes (trenches)", rare_ok)
    values = set(int(v) for f in faces_a for v in np.unique(f.materials))
    check("materials in table", values <= set(v for _, v in MATERIALS), "values %s" % sorted(values))
    gvals = set(int(v) for f in faces_a for v in np.unique(f.biomes))
    check("biome G values are names + 1", gvals <= set(range(1, len(BIOME_NAMES) + 1)), "%s" % sorted(gvals))
    ovals = set(int(v) for f in faces_a for v in np.unique(f.ores)) - {0}
    check("ore B values are ore rows", ovals <= set(v for r in lay["ores"]["rows"] for v in ore_values(r)), "%s" % sorted(ovals))
    check("deeper ores outcrop", any(v in ovals for r in lay["ores"]["rows"] if r.get("outcropValue") for v in (r["outcropValue"],)), "outcrop values %s" % sorted(
        v for v in ovals if any(v == r.get("outcropValue") for r in lay["ores"]["rows"])))
    pix = spline = -1e9
    for fr_ in faces_a:
        p_, s_ = max_surface(fr_.heights, fr_.isl < 1.25, sub=2)
        pix, spline = max(pix, p_), max(spline, s_)
    check("below SN y -2 outside the islands", pix <= -lay["limits"]["minDepthM"] and spline <= -lay["limits"]["minDepthM"] + 0.5,
          "pixel %.2f bicubic %.2f" % (pix, spline))
    zone = island_zone_of(faces_a[1], lay)
    ipix, _ = max_surface(faces_a[1].heights, ~zone, sub=1, top_n=20)
    check("the ring's island rises above the sea", ipix > 0.0 and ipix <= lay["limits"]["islandMaxY"] + 10, "top %.1f" % ipix)
    itop = max(float(decode_height(fr_.heights).max()) for fr_ in faces_a)
    check("no land above islandMaxY + 10", itop <= lay["limits"]["islandMaxY"] + 10, "top %.1f" % itop)
    check("floor reachable", abs(min(float(decode_height(f.heights).min()) for f in faces_a) - FLOOR_Y) < 1e-6)
    # a full-resolution box check of the biome bands at a coarse grid (cheap): every Voronoi biome present in the ring
    st = design_stats(da, faces_a)
    present = [k for k, b in st["biomes"].items() if b["pixels"] > 0]
    ring_keys = [b["key"] for b in lay["biomes"] if b.get("zone") != "planet"]
    check("every ring biome present (res %d)" % res, set(present) >= set(ring_keys) - {"lostRiver"}, "missing %s" % sorted(set(ring_keys) - set(present)))
    counts = sum(fr_.biome_counts for fr_ in faces_a)
    planet_keys = [b["key"] for b in lay["biomes"] if b.get("zone") == "planet"]
    check("every planet biome present (res %d)" % res, all(counts[da.keys.index(k)] > 0 for k in planet_keys),
          "%s" % {k: int(counts[da.keys.index(k)]) for k in planet_keys})
    # PNG round trip and sculptHash format
    files = planet_files(faces_a)
    w, hh, bd, ct, s = png_decode(files["back.png"])
    check("png16 round trip", (w, hh, bd, ct) == (res, res, 16, 0) and np.array_equal(s, faces_a[1].heights))
    w, hh, bd, ct, s = png_decode(files["back_mat.png"])
    check("rgba round trip", (bd, ct) == (8, 6) and np.array_equal(s[..., 0], faces_a[1].materials) and np.array_equal(s[..., 1], faces_a[1].biomes)
          and np.array_equal(s[..., 2], faces_a[1].ores) and int(s[..., 3].min()) == 255)
    shash, lines = sculpt_hash(files)
    check("sculptHash lines", len(lines) == 12 and lines[0].startswith("back.png\t"), "0x%016x" % shash)
    check("fnv1a64", fnv1a64(b"a") == 0xaf63dc4c8601ec8c and fnv1a64_fast(b"foobar") == fnv1a64(b"foobar"))
    # the.sbc rewrite
    sbc = ('<PlanetMaps Material="true" Ores="false" Biome="false" Occlusion="false" />\n    <CustomMaterialTable>\n'
           '      <Material Material="Sand_02" Value="1" MaxDepth="5" />\n    </CustomMaterialTable>')
    t2 = sbc_with_tables(sbc, lay)
    check("sbc table", sbc_table(t2) == {v: n for n, v in MATERIALS}, "%r" % sbc_table(t2))
    want_ores = {r["value"]: (r["type"], float(r["startM"]), float(r["depthBelowM"])) for r in lay["ores"]["rows"]}
    want_ores.update({r["outcropValue"]: (r["type"], 0.0, float(r["startM"] + r["depthBelowM"])) for r in lay["ores"]["rows"] if r.get("outcropValue")})
    check("sbc ores", sbc_ores(t2) == want_ores, "%d rows" % len(sbc_ores(t2)))
    check("sbc planet maps", sbc_planet_maps(t2) == {"Material": True, "Ores": True, "Biome": True, "Occlusion": False})
    check("sbc rewrite idempotent", sbc_with_tables(t2, lay) == t2)
    try:
        with open(os.path.join(REPO_MOD_DIR, "Data", "PlanetGeneratorDefinitions.sbc"), encoding="utf-8") as fh:
            repo_sbc = fh.read()
        t3 = sbc_with_tables(repo_sbc, lay)
        check("repo sbc rewrite", sbc_table(t3) == {v: n for n, v in MATERIALS} and len(sbc_ores(t3)) == len(want_ores))
    except OSError as e:
        check("repo sbc rewrite", False, str(e))
    # depth-at agrees with the pixel at a pixel centre (bicubic interpolates there)
    j = res // 2 + 20
    i = res // 2 - 15
    d = face_dirs(1, res, j, j + 1, i, i + 1)
    sx, sz = sn_xz(*d)
    yb = surface_y_sn(hb, float(sx[0, 0]), float(sz[0, 0]))
    check("depth-at at a pixel centre = the pixel", abs(yb - float(decode_height(hb[1][j, i]))) < 1e-6, "%.4f vs %.4f" % (yb, float(decode_height(hb[1][j, i]))))
    #  design 3 pinned now that the tool grew design 4 and 5 rules (all keyed on layout entries design 3
 # lacks): its 128 px files hash as when the design-5 rules went in
    d3 = Design(load_layout(), DEFAULT_SEED)
    h3, _l3 = sculpt_hash(planet_files(sculpt_all(d3, 128, keep=False, log=None)))
    check("design 3 pinned (128 px sculptHash)", h3 == DESIGN3_PIN_128, "0x%016x (pin 0x%016x)" % (h3, DESIGN3_PIN_128))
    # planet v2 R8: design 3 writes no rules file (today's crater ores); design 5's switches the cube rule off and caps the deposits
    r3 = planet_rules_text(load_layout())
    d5p = os.path.join(HERE, "layout_4546b_d5.json")
    r5 = planet_rules_text(load_layout(d5p)) if os.path.isfile(d5p) else None
    check("planet rules: design 3 none, design 5 cube rule off and 4 deposits per km2", r3 is None and r5 == PLANET_RULES_MAGIC + "\ncrater.cubeRule=false\ncrater.depositsPerKm2=4\n",
          "%r / %r" % (r3, r5))
    print("SCULPT SELFTEST %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

PLANET_HALF_SIZE_M = 131072.5   # DE_4546B's storage: the MyObjectBuilder_Planet <Position> is the min corner, C = corner + half (tools/lab/set_planet_position.py)

def world_grids(save_dir):
    """(planet centre C or None, [(entity id, name, static, position)]) of an SE save folder, read-only (text scan of SANDBOX_0_0_0_.sbs)."""
    p = os.path.join(save_dir, "SANDBOX_0_0_0_.sbs")
    with open(p, encoding="utf-8-sig", errors="replace") as fh:
        text = fh.read()
    centre = None
    for m in re.finditer(r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_Planet">(.*?)</MyObjectBuilder_EntityBase>', text, re.S):
        blk = m.group(1)
        sn = re.search(r"<StorageName>([^<]*)</StorageName>", blk)
        if not sn or not sn.group(1).startswith(SUBTYPE):
            continue
        pos = re.search(r'<Position\s+x="([^"]+)"\s+y="([^"]+)"\s+z="([^"]+)"\s*/>', blk)
        if pos:
            centre = tuple(float(pos.group(i)) + PLANET_HALF_SIZE_M for i in (1, 2, 3))
        break
    grids = []
    for m in re.finditer(r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">', text):
        end = text.find("</MyObjectBuilder_EntityBase>", m.end())
        blk = text[m.end():end]
        gid = re.search(r"<EntityId>(-?\d+)</EntityId>", blk)
        pos = re.search(r'<Position\s+x="([^"]+)"\s+y="([^"]+)"\s+z="([^"]+)"\s*/>', blk)
        static = re.search(r"<IsStatic>(true|false)</IsStatic>", blk)
        name = re.search(r"<DisplayName>([^<]*)</DisplayName>", blk)
        if pos:
            grids.append((gid.group(1) if gid else "?", name.group(1) if name else "", bool(static and static.group(1) == "true"),
                          tuple(float(pos.group(i)) for i in (1, 2, 3))))
    return centre, grids

def do_check_world(save_dir, design, margin_m):
    """ROADMAP S8a project action 1 (read-only): every grid of the save outside the crater (site-v1 rhoInf > the crater's
 flat radius) whose centre would lie below the new seabed (the design's depth at its direction, + margin) is listed."""
    if not os.path.isdir(save_dir):
        print("MISSING save folder %s" % save_dir)
        return 2
    centre, grids = world_grids(save_dir)
    if centre is None:
        print("no %s planet in %s" % (SUBTYPE, save_dir))
        return 2
    flat = design.lay["crater"]["flatRhoInfM"]
    print("check-world %s: planet C=(%.1f, %.1f, %.1f), %d grids, detail seed %d, margin %.1f m" % (save_dir, centre[0], centre[1], centre[2], len(grids), design.detail_seed, margin_m))
    buried, outside = [], 0
    for gid, name, static, pos in grids:
        rel = (pos[0] - centre[0], pos[1] - centre[1], pos[2] - centre[2])
        r = math.sqrt(rel[0] ** 2 + rel[1] ** 2 + rel[2] ** 2)
        if r == 0:
            continue
        f = design.evaluate(np.array([rel[0] / r]), np.array([rel[1] / r]), np.array([rel[2] / r]))
        alt = r - RS
        seabed = -float(f["depth"][0])
        if float(f["rinf"][0]) <= flat:
            continue
        outside += 1
        if alt <= seabed + margin_m:
            buried.append((gid, name, static, alt, seabed, float(f["sx"][0]), float(f["sz"][0]), float(f["rinf"][0])))
    for gid, name, static, alt, seabed, sx, sz, rinf in buried:
        print("  BURIED grid %s %r%s at SN (%.0f, %.0f) rhoInf %.0f m: altitude %.1f m, new seabed %.1f m (%.1f m below it)" % (
            gid, name, " (static)" if static else "", sx, sz, rinf, alt, seabed, seabed - alt))
    print("check-world: %d grids outside the crater, %d would be buried (margin %.1f m): %s" % (outside, len(buried), margin_m, "OK" if not buried else "REVIEW"))
    return 0 if not buried else 1

def read_mod_files(mod_dir):
    pdir = planet_dir(mod_dir)
    files = {}
    for name in FACES:
        for kind in ("", "_mat"):
            fn = name + kind + ".png"
            p = os.path.join(pdir, fn)
            if not os.path.isfile(p):
                return None, p
            with open(p, "rb") as fh:
                files[fn] = fh.read()
    return files, None

def do_verify(mod_dir, design, res):
    """S8a.C2.sculpt (the files = the design re-derived),.flat (the crater),.limits (nothing above -2 m but the island),.ores (every deposit within its row's depth band and biomes; B, G, R values declared in the.sbc)."""
    files, missing = read_mod_files(mod_dir)
    if files is None:
        print("MISSING", missing)
        return 1
    ok = True
    heights, mats, bios, ores = {}, {}, {}, {}
    for f, name in enumerate(FACES):
        for kind in ("", "_mat"):
            fn = name + kind + ".png"
            w, h, bd, ct, s = png_decode(files[fn])
            if kind == "":
                good = (w, h, bd, ct) == (res, res, 16, 0)
                heights[f] = s
            else:
                good = (w, h, bd, ct) == (res, res, 8, 6) and int(s[..., 3].min()) == 255
                mats[f], bios[f], ores[f] = s[..., 0], s[..., 1], s[..., 2]
            ok &= good
            print("%-4s %-14s %dx%d %d-bit type %d" % ("OK" if good else "FAIL", fn, w, h, bd, ct))
    if not ok:
        print("SCULPT VERIFY FAIL")
        return 1
    table, ore_rows, maps = {}, {}, {}
    sbc_path = os.path.join(mod_dir, "Data", "PlanetGeneratorDefinitions.sbc")
    if os.path.isfile(sbc_path):
        with open(sbc_path, encoding="utf-8") as fh:
            text = fh.read()
        table, ore_rows, maps = sbc_table(text), sbc_ores(text), sbc_planet_maps(text)
    values = set(int(v) for f in range(6) for v in np.unique(mats[f]))
    ovals = set(int(v) for f in range(6) for v in np.unique(ores[f])) - {0}
    gvals = set(int(v) for f in range(6) for v in np.unique(bios[f])) - {0}
    decl = values <= set(table.keys()) and ovals <= set(ore_rows.keys()) and (not ovals or maps.get("Ores")) and (not gvals or maps.get("Biome"))
    print("%-4s material values %s, ore values %s, biome values %s declared in the .sbc (table %s, ores %s, PlanetMaps %s)" % (
        "OK" if decl else "FAIL", sorted(values), sorted(ovals), sorted(gvals), table, sorted(ore_rows), maps))
    ok &= decl
    print("re-deriving the design (layout 0x%016x, detail seed %d) ..." % (layout_hash(design.lay), design.detail_seed))
    faces = sculpt_all(design, res, keep=True, log=None)
    worst = 0
    mat_diff = bio_diff = ore_diff = 0
    for fr in faces:
        worst = max(worst, int(np.max(np.abs(fr.heights.astype(np.int32) - heights[fr.face].astype(np.int32)))))
        mat_diff += int(np.count_nonzero(fr.materials != mats[fr.face]))
        bio_diff += int(np.count_nonzero(fr.biomes != bios[fr.face]))
        ore_diff += int(np.count_nonzero(fr.ores != ores[fr.face]))
    sculpt_ok = worst <= 1 and mat_diff == 0 and bio_diff == 0 and ore_diff == 0
    print("%-4s S8a.C2.sculpt heights within 1 unit of the design (worst %d); pixels differing: material %d, biome %d, ore %d" % (
        "OK" if sculpt_ok else "FAIL", worst, mat_diff, bio_diff, ore_diff))
    ok &= sculpt_ok
    bad, dev = check_flat(heights, design.lay)
    flat_ok = bad == 0 and dev <= 0.01
    print("%-4s S8a.C2.flat pixels within rhoInf %.0f m non-zero: %d; bicubic at 2,000 columns within 2,300 m: max "
          "|y + 3000| = %.5f m" % ("OK" if flat_ok else "FAIL", design.lay["crater"]["flatRhoInfM"], bad, dev))
    ok &= flat_ok
    lim = -design.lay["limits"]["minDepthM"]
    worst_pix, worst_spline = -1e9, -1e9
    ipix = ispline = -1e9
    for fr in faces:
        zone = fr.isl < 1.25
        p_, s_ = max_surface(heights[fr.face], zone)
        worst_pix, worst_spline = max(worst_pix, p_), max(worst_spline, s_)
        if zone.any():
            a_, b_ = max_surface(heights[fr.face], ~zone, sub=2, top_n=200)
            ipix, ispline = max(ipix, a_), max(ispline, b_)
    lim_ok = worst_pix <= lim and worst_spline <= lim + 0.5 and ispline <= design.lay["limits"]["islandMaxY"] + 10.0
    print("%-4s S8a.C2.limits outside the islands nothing above SN y %.0f (pixel %.2f, bicubic %.2f); island tops pixel %.1f, bicubic %.1f (limit %.0f)" % (
        "OK" if lim_ok else "FAIL", lim, worst_pix, worst_spline, ipix, ispline, design.lay["limits"]["islandMaxY"] + 10.0))
    ok &= lim_ok
    # the ore table: every deposit pixel within its row's band (the ring's or the planet's), in the ring or on the planet
    bad_ore = []
    for fr in faces:
        yf = decode_height(heights[fr.face])
        of = ores[fr.face]
        for row in design.lay["ores"]["rows"]:
            m = np.isin(of, ore_values(row))
            if not m.any():
                continue
            rb, pband, _rb, _pb = ore_bands(row)
            for zone, band, zname in ((fr.ring, rb, "ring"), (fr.planet & ~fr.ring, pband, "planet")):
                d = -yf[m & zone]
                if d.size and (d.min() < band[0] - 1.0 or d.max() > band[1] + 1.0):
                    bad_ore.append("face %d %s %s %.0f..%.0f m (band %s)" % (fr.face, zname, row["ore"], d.min(), d.max(), band))
            stray = int(np.count_nonzero(m & ~fr.ring & ~fr.planet))
            if stray:
                bad_ore.append("face %d %s: %d pixels outside the ring and the planet" % (fr.face, row["ore"], stray))
    ores_ok = not bad_ore and (not ovals or all(ore_rows.get(v, (None,))[0] == r["type"] for r in design.lay["ores"]["rows"] for v in ore_values(r)))
    print("%-4s S8a.C2.ores every deposit within its row's seabed depth band; the .sbc rows name the layout's types%s" % (
        "OK" if ores_ok else "FAIL", (": " + "; ".join(bad_ore)) if bad_ore else ""))
    ok &= ores_ok
    if design.planet.on:
        rep_ = planet_report(design, faces, heights, ores)
        for good, line in globe_rows(design, rep_):
            print("%-4s %s" % ("OK" if good else "FAIL", line))
            ok &= good
    sh, _ = sculpt_hash(files)
    print("sculptHash 0x%016x" % sh)
    print("SCULPT VERIFY %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def read_points(path):
    """Points [(x, z)] from a file: JSON [[x, z],...] / {"points": [...]} or lines 'x z' (also 'x,z'; '#' comments)."""
    with open(path, encoding="utf-8") as fh:
        text = fh.read()
    t = text.strip()
    if t.startswith("[") or t.startswith("{"):
        data = json.loads(t)
        if isinstance(data, dict):
            data = data.get("points", [])
        return [(float(p[0]), float(p[1])) for p in data]
    out = []
    for ln in text.splitlines():
        ln = ln.split("#", 1)[0].replace(",", " ").split()
        if len(ln) >= 2:
            out.append((float(ln[0]), float(ln[1])))
    return out

def depth_at_points(mod_dir, points):
    """[{x, z, y, depth, slope, face, tx, ty}] of SE's bicubic seabed (what MyPlanet.GetClosestSurfacePointGlobal gives) at SN columns,
 from the six heightmaps of mod_dir; plus the files' sculptHash. None when a file is missing."""
    files, missing = read_mod_files(mod_dir)
    if files is None:
        return None, missing
    hb = {f: png_decode(files[FACES[f] + ".png"])[4] for f in range(6)}
    out = []
    for x, z in points:
        d = sn_to_dir(x, z)
        face, tx, ty = sample_texcoord(*d)
        y = surface_y_sn(hb, x, z)
        e = 2.0
        slope = max(abs(surface_y_sn(hb, x + e, z) - surface_y_sn(hb, x - e, z)), abs(surface_y_sn(hb, x, z + e) - surface_y_sn(hb, x, z - e))) / (2 * e)
        out.append({"x": x, "z": z, "y": round(y, 4), "depth": round(-y, 4), "slope": round(slope, 4), "face": face, "tx": round(tx, 7), "ty": round(ty, 7)})
    return out, "%016x" % sculpt_hash(files)[0]

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--seed", type=int, default=None, help="detail seed (default %d): varies the +-2 m detail layer only" % DEFAULT_SEED)
    ap.add_argument("--layout", help="layout JSON (default tools/planet/layout_4546b.json)")
    ap.add_argument("--rules", help=argparse.SUPPRESS)
    ap.add_argument("--res", type=int, default=RESOLUTION, help="face resolution (2048 for the game; smaller for tests)")
    ap.add_argument("--dry-run", action="store_true", help="compute and report; write nothing")
    ap.add_argument("--preview", metavar="DIR", help="write preview images into DIR")
    ap.add_argument("--out", metavar="MODDIR", help="write the 12 PNGs into MODDIR/Data/PlanetDataFiles/DE_4546B")
    ap.add_argument("--sbc", action="store_true", help="with --out: also rewrite MODDIR/Data/PlanetGeneratorDefinitions.sbc (tables, ores, PlanetMaps)")
    ap.add_argument("--owner-go", action="store_true", help="required to write into the repository's world mod")
    ap.add_argument("--verify", metavar="MODDIR", help="S8a.C2 checks of the files in MODDIR")
    ap.add_argument("--depth-at", nargs=2, type=float, metavar=("X", "Z"), help="SE's bicubic seabed (SN y) at SN (X, Z) from --files")
    ap.add_argument("--depth-at-list", metavar="FILE", help="the same for every point of FILE")
    ap.add_argument("--files", metavar="MODDIR", default=REPO_MOD_DIR, help="--depth-at's heightmaps (default: the repository's world mod)")
    ap.add_argument("--json", action="store_true", help="--depth-at*, --ore-sites: JSON output")
    ap.add_argument("--ore-sites", action="store_true", help="the design's ore deposits")
    ap.add_argument("--native-check", nargs="?", const=REPO_MOD_DIR, metavar="MODDIR", help="S8a.C2.native: SE's own voxel generator (VRage.Native.dll, read-only) on MODDIR's heightmaps")
    ap.add_argument("--check-world", metavar="SAVE", help="read-only: the grids of an SE save folder outside the crater that the new seabed would bury (owner action 1)")
    ap.add_argument("--margin", type=float, default=2.0, help="--check-world: a grid counts as buried when its centre lies this far (m) below the new seabed or less")
    ap.add_argument("--sn-scan", metavar="DIR", help="design 5: tools/sn-octree-scan's column maps of the install (the rim and the cap under the tiles)")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--print-layout", action="store_true")
    a = ap.parse_args(argv)

    if a.selftest:
        return do_selftest()
    if a.rules:
        print("--rules is gone (Stage C): the planet is the authored layout (--layout FILE, default %s)" % DEFAULT_LAYOUT)
        return 2
    if a.depth_at or a.depth_at_list:
        pts = [tuple(a.depth_at)] if a.depth_at else read_points(a.depth_at_list)
        rows, shash = depth_at_points(a.files, pts)
        if rows is None:
            print("MISSING %s" % shash)
            return 1
        if a.json:
            print(json.dumps({"files": os.path.abspath(a.files), "sculptHash": shash, "points": rows}))
        else:
            print("files %s (sculptHash 0x%s)" % (os.path.abspath(a.files), shash))
            for r in rows:
                print("SN (%9.2f, %9.2f): seabed y %9.3f (depth %8.3f m) face %d texcoord (%.6f, %.6f)" % (r["x"], r["z"], r["y"], r["depth"], r["face"], r["tx"], r["ty"]))
        return 0
    if a.native_check:
        import sculpt_native_check  # noqa: E402 (our own module beside this one)
        return sculpt_native_check.main([a.native_check])
    lay = load_layout(a.layout)
    design = Design(lay, a.seed if a.seed is not None else DEFAULT_SEED)
    if a.print_layout:
        print(json.dumps(lay, indent=2, sort_keys=True))
        return 0
    if a.verify:
        return do_verify(a.verify, design, a.res)
    if a.check_world:
        return do_check_world(a.check_world, design, a.margin)
    if a.ore_sites:
        faces = [sculpt_face(1, design, a.res, keep=True)]          # the ring's deposits (face 1)
        sites = ore_sites(design, faces)
        if a.json:
            print(json.dumps(sites))
        else:
            for s_ in sites:
                print("%-9s %-13s B=%-3d SN (%8.1f, %8.1f) seabed y %8.1f, %d px, ore %g..%g m below the seabed" % (
                    s_["ore"], s_["type"], s_["value"], s_["x"], s_["z"], s_["seabedY"], s_["pixels"], s_["startM"], s_["startM"] + s_["depthBelowM"]))
            print("%d deposits" % len(sites))
        return 0
    if not (a.dry_run or a.preview or a.out):
        ap.error("nothing to do: --dry-run, --preview DIR, --out MODDIR, --verify MODDIR, --depth-at X Z, --ore-sites, --native-check, --check-world SAVE or --selftest")
    if a.out and not a.dry_run:
        why = refuse_out(a.out, a.owner_go, a.res)
        if why:
            print("REFUSED: " + why)
            return 2

    t0 = time.time()
    if a.sn_scan:
        import sn_rim as SR  # noqa: E402
        cap = SR.SnCap(a.sn_scan)
        design.sn_cap, design.sn_rim = cap.cap, cap.rim
        print("sn scan %s: input pin %s (rim from %d headings; %d protected columns, %d rim columns the floor may bury)" % (
            a.sn_scan, cap.hash, cap.rim.th.size, int(cap.protected.sum()), int(cap.rim_fill.sum())))
    print("design %s layout 0x%016x detail seed %d res %d" % (SUBTYPE, layout_hash(lay), design.detail_seed, a.res))
    faces = sculpt_all(design, a.res, keep=True)
    summarize(design, faces)
    files = planet_files(faces)
    shash, lines = sculpt_hash(files)
    total = sum(len(v) for v in files.values())
    print("  12 files, %.1f MB; sculptHash 0x%016x (the window's chart keys change with it)" % (total / 1e6, shash))
    if a.preview:
        write_previews(design, faces, a.preview)
    if a.out:
        pdir = planet_dir(a.out)
        if a.dry_run:
            for name in sorted(files):
                print("  would write %s (%d B)" % (os.path.join(pdir, name), len(files[name])))
            rt = planet_rules_text(lay)
            rp = os.path.join(pdir, PLANET_RULES_FILE)
            if rt is not None:
                print("  would write %s (%s)" % (rp, rt.strip().replace("\n", "; ")))
            elif os.path.isfile(rp):
                print("  would remove %s (the layout has no ores.crater)" % rp)
            if a.sbc:
                print("  would rewrite %s: CustomMaterialTable %s; OreMappings %d rows; PlanetMaps Material, Ores, Biome on" % (
                    os.path.join(a.out, "Data", "PlanetGeneratorDefinitions.sbc"), ", ".join("%s %d" % m for m in MATERIALS), len(lay["ores"]["rows"])))
        else:
            os.makedirs(pdir, exist_ok=True)
            for name in sorted(files):
                p = os.path.join(pdir, name)
                tmp = p + ".tmp"
                with open(tmp, "wb") as fh:
                    fh.write(files[name])
                os.replace(tmp, p)
                print("  wrote %s (%d B)" % (p, len(files[name])))
            # planet v2 R8: the runtime rules beside the PNGs (or none: a stale file from another layout is removed)
            rt = planet_rules_text(lay)
            rp = os.path.join(pdir, PLANET_RULES_FILE)
            if rt is not None:
                with open(rp + ".tmp", "w", encoding="utf-8", newline="\n") as fh:
                    fh.write(rt)
                os.replace(rp + ".tmp", rp)
                print("  wrote %s (%s)" % (rp, rt.strip().replace("\n", "; ")))
            elif os.path.isfile(rp):
                os.remove(rp)
                print("  removed %s (the layout has no ores.crater)" % rp)
            if a.sbc:
                sp = os.path.join(a.out, "Data", "PlanetGeneratorDefinitions.sbc")
                with open(sp, encoding="utf-8") as fh:
                    text = fh.read()
                new = sbc_with_tables(text, lay)
                with open(sp, "w", encoding="utf-8", newline="") as fh:
                    fh.write(new)
                print("  rewrote %s (CustomMaterialTable %d rows, OreMappings %d rows, PlanetMaps)" % (sp, len(MATERIALS), len(lay["ores"]["rows"])))
            for ln in lines:
                print("  sculptHash line: " + ln.rstrip("\n").replace("\t", "  "))
    elif a.dry_run:
        print("dry run: nothing written")
    print("done in %.1f s" % (time.time() - t0))
    return 0

if __name__ == "__main__":
    sys.exit(main())
