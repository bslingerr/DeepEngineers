"""S8a Stage F: the ring's 3D field (the base, the 3D rules, the brush stamping): the Python twin of
src/DeepEngineers.Terrain/RingField.cs (Stage F2; F1 was this tool alone, offline).

The field is a signed distance f(x, y, z) in metres, positive in rock, over Subnautica coordinates (site v1), a
deterministic function of the layout (the planet's face-1 heightmap and biome map, i.e. the deployed world mod's
back.png and back_mat.png, which the layout made), the brush library (tools/planet/s8a_brushes.py) and the seed:

* the base: f = H(x, z) - y, H the seabed of SE's own heightmap at the column

 so the field meets SE's planet wherever no stamp and no 3D rule applies (the heightmap stays the fallback floor);
* the 3D rules per biome (the biome map's G channel at the nearest pixel): worm caves 4-130 m under the seabed in Blood Kelp
 and the Lost River canyons, arches 6-42 m over the Grand Reef, faded in over SN radius 3,600-3,800 m and out over
 12,000-12,200 m (outside, the field is the heightmap exactly);
* the brushes, placed once over the ring (one candidate per StampCellM cell in row order, x outer, kept with the biome's
 density; a brush of the biome's classes, never one placed within RepeatM; any yaw, mirrored or not, scaled 0.9-1.15 for
 the 32 m brushes and 0.9-2.

Every step is plain IEEE double arithmetic (+ - * / sqrt floor, no transcendental function: sin/cos of the site angle are a
Taylor polynomial, the yaw a hashed unit vector), evaluated in the same order here and in C#, so both give the same doubles:
the vectors (tests/s8a/vectors/ring3d/) hold content bytes of a few boxes that RingFieldTests checks byte for byte.

Usage (repository root; numpy, scipy, Pillow; the library built first: s8a_brushes.py build):
 python tools/planet/s8a_ring3d.py preview [--out DIR] ring map with placements, three sections, the stamped sheet
 python tools/planet/s8a_ring3d.py verify S8a.F.library,.forbidden,.repeat,.seam,.blend
 python tools/planet/s8a_ring3d.py vectors writes tests/s8a/vectors/ring3d/ (synthetic library; + install set)
 python tools/planet/s8a_ring3d.py --selftest stamping and the field on a synthetic library
"""
import argparse
import math
import os
import struct
import sys
import time
import zlib

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
import sculpt_de4546b as SC  # noqa: E402
import s8a_brushes as B  # noqa: E402

FIELD_VERSION = 2
SEED = 4546
RES = 2048
INNER = 57000.0                      # R + MinHill (site v1)
HR = 10200.0                         # HeightRatio
RS = 60000.00390625                  # the sea radius (site v1)
STAMP_CELL_M = 96.0
REPEAT_M = 300.0
FALLOFF_M = 6.0
SINK_M = 1.0
SCALE_32 = (900, 1150)               # per mille
SCALE_64 = (900, 2000)
RING_RHO = (3800.0, 12000.0)
HALF_M = 12600.0
STAMP_ABOVE_Y = -6.0                 # no stamp where the seabed is above this
DISC = 550                           # per mille: the stamp weight is 1 inside this share of the brush's radius
CAVE = (48.0, 2, 130, 40.0, 4.0, 10.0, 90.0, 130.0)     # wavelength, octaves, threshold per mille, gain, band in/full/out/end (m under the seabed)
ARCH = (90.0, 2, 860, 60.0, 6.0, 12.0, 32.0, 42.0)      # wavelength, octaves, threshold per mille, gain, band (m over the seabed)
GRID_M = 128.0                       # the placements' lookup grid
FADE = (3600.0, 3800.0, 12000.0, 12200.0)   # the 3D rules fade in and out over these SN radii (outside them the field is the heightmap)
# per ring biome: (density per mille of a stamp per cell, the classes it takes, its 3D rules)
RULES = (
    ("safeShallows", 300, ("boulder", "floor"), ()),
    ("kelpForest", 400, ("wall", "boulder", "overhang"), ()),
    ("grassyPlateaus", 300, ("wall", "floor", "overhang"), ()),
    ("mushroomForest", 600, ("boulder", "pillar"), ()),
    ("sparseReef", 450, ("boulder", "pillar", "overhang"), ()),
    ("grandReef", 500, ("overhang", "pillar", "wall", "cave"), ("arches",)),
    ("dunes", 40, ("boulder",), ()),
    ("mountains", 400, ("wall", "boulder", "overhang"), ()),
    ("underwaterIslands", 500, ("pillar", "overhang", "wall"), ()),
    ("bloodKelp", 450, ("cave", "overhang", "wall"), ("caves",)),
    ("crashZone", 200, ("boulder", "floor"), ()),
    ("lostRiver", 500, ("cave", "wall"), ("caves",)),
    ("craterEdge", 0, (), ()),
)
RULE_OF = {k: (d, c, f) for k, d, c, f in RULES}
BIOME_KEY = {"lostRiver_Canyon": "lostRiver", "void": "craterEdge"}
KEYS = tuple(BIOME_KEY.get(n, n) for n in SC.BIOME_NAMES)              # G - 1 -> rule key
CLASS_COLOURS = {"wall": (230, 120, 60), "boulder": (240, 220, 90), "overhang": (90, 200, 240), "cave": (220, 70, 200), "pillar": (120, 240, 120), "floor": (200, 200, 200)}
PLANET_DIR = os.path.join(REPO, "src", "DeepEngineers.WorldMod", "Data", "PlanetDataFiles", "DE_4546B")
VECTOR_DIR = os.path.join(REPO, "tests", "s8a", "vectors", "ring3d")

# Taylor coefficients of sin and cos (|t| <= 0.25: the ring's site angle is <= 0.21 rad; error < 1e-20)
S3, S5, S7, S9, S11, S13, S15 = -1.0 / 6.0, 1.0 / 120.0, -1.0 / 5040.0, 1.0 / 362880.0, -1.0 / 39916800.0, 1.0 / 6227020800.0, -1.0 / 1307674368000.0
C2, C4, C6, C8, C10, C12, C14, C16 = -0.5, 1.0 / 24.0, -1.0 / 720.0, 1.0 / 40320.0, -1.0 / 3628800.0, 1.0 / 479001600.0, -1.0 / 87178291200.0, 1.0 / 20922789888000.0
MASK = 0xFFFFFFFFFFFFFFFF
M1, M2, M3 = 0x9E3779B97F4A7C15, 0xC2B2AE3D27D4EB4F, 0x165667B19E3779F9
F1, F2 = 0xBF58476D1CE4E5B9, 0x94D049BB133111EB
U53 = 1.0 / 9007199254740992.0

# ------------------------------------------------------------------------------------------------- hashing
def fnv64(data, h=0xcbf29ce484222325):
    for b in data:
        h = ((h ^ b) * 0x100000001b3) & MASK
    return h

def mix64(h):
    h ^= h >> 30
    h = (h * F1) & MASK
    h ^= h >> 27
    h = (h * F2) & MASK
    h ^= h >> 31
    return h

def hash_u(*v):
    """Uniform [0, 1) of integers v: FNV-1a 64 over their int64 little-endian bytes, top 53 bits."""
    return (B._hash(*v) >> 11) * U53

def rules_text(seed=SEED):
    """The canonical text of the rules (RingRules.Text in C#; the vectors carry its FNV)."""
    lines = ["de-ring-rules %d" % FIELD_VERSION, "seed %d" % seed, "cellM %d" % STAMP_CELL_M, "repeatM %d" % REPEAT_M, "falloffM %d" % FALLOFF_M,
             "sinkM %d" % SINK_M, "scale32 %d %d" % SCALE_32, "scale64 %d %d" % SCALE_64, "rho %d %d" % RING_RHO, "halfM %d" % HALF_M,
             "stampAboveY %d" % STAMP_ABOVE_Y, "disc %d" % DISC, "gridM %d" % GRID_M, "fade %d %d %d %d" % FADE,
             "cave %d %d %d %d %d %d %d %d" % CAVE, "arch %d %d %d %d %d %d %d %d" % ARCH]
    for key, d, cl, fe in RULES:
        lines.append("rule %s %d %s %s" % (key, d, ",".join(cl) or "-", ",".join(fe) or "-"))
    for g, name in enumerate(SC.BIOME_NAMES):
        lines.append("biome %d %s %s" % (g + 1, name, KEYS[g]))
    return "\n".join(lines) + "\n"

# ------------------------------------------------------------------------------------------------- noise
class Noise:
    """fBm / ridged gradient noise (sculpt_de4546b.gradient_noise, its lattice hash) with seeds and offsets from FNV / splitmix."""

    def __init__(self, seed, name, octaves):
        self.seeds, self.offsets = [], []
        for o in range(octaves):
            h = fnv64(("%d|%s|%d" % (seed, name, o)).encode("utf-8"))
            self.seeds.append(h >> 1)
            self.offsets.append(tuple(-1e4 + 2e4 * ((mix64(h ^ (((k + 1) * M3) & MASK)) >> 11) * U53) for k in range(3)))
        self.octaves = octaves

    def fbm(self, px, py, pz, wavelength):
        total = np.zeros(np.shape(px))
        amp, f, norm = 1.0, 1.0 / wavelength, 0.0
        for o in range(self.octaves):
            ox, oy, oz = self.offsets[o]
            total = total + amp * SC.gradient_noise(px * f + ox, py * f + oy, pz * f + oz, self.seeds[o])
            norm = norm + amp
            amp = amp * 0.5
            f = f * 2.0
        return total / norm

    def ridged(self, px, py, pz, wavelength):
        total = np.zeros(np.shape(px))
        amp, f, norm = 1.0, 1.0 / wavelength, 0.0
        for o in range(self.octaves):
            ox, oy, oz = self.offsets[o]
            n = 1.0 - np.abs(SC.gradient_noise(px * f + ox, py * f + oy, pz * f + oz, self.seeds[o]))
            total = total + amp * n * n
            norm = norm + amp
            amp = amp * 0.5
            f = f * 2.0
        return total / norm

def smoothstep(e0, e1, x):
    t = np.minimum(np.maximum((x - e0) / (e1 - e0), 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t)

# ------------------------------------------------------------------------------------------------- the base
def read_planet(planet_dir=PLANET_DIR):
    """(heights uint16 [row, col], biome G uint8 [row, col]) of face 1 (back.png, back_mat.png)."""
    from PIL import Image  # noqa: E402
    h = np.array(Image.open(os.path.join(planet_dir, "back.png"))).astype(np.uint16)
    m = np.array(Image.open(os.path.join(planet_dir, "back_mat.png")))
    if h.shape != (RES, RES) or m.shape[:2] != (RES, RES):
        raise ValueError("face 1 is not %d x %d" % (RES, RES))
    return np.ascontiguousarray(h), np.ascontiguousarray(m[..., 1].astype(np.uint8))

def sin_cos(t):
    t2 = t * t
    s = t * (1.0 + t2 * (S3 + t2 * (S5 + t2 * (S7 + t2 * (S9 + t2 * (S11 + t2 * (S13 + t2 * S15)))))))
    c = 1.0 + t2 * (C2 + t2 * (C4 + t2 * (C6 + t2 * (C8 + t2 * (C10 + t2 * (C12 + t2 * (C14 + t2 * C16)))))))
    return s, c

def texel(x, z):
    """Face-1 pixel coordinates (column, row) of SN columns (x, z): site v1 (U0 +Z, E0 -X, N0 -Y, psi 0, O 0), the inverse
 azimuthal map (SiteMap.SnToSe) then MyCubemapHelpers.CalculateSampleTexcoord's face 1 (tx = X / Z, ty = -Y / Z)."""
    x = np.asarray(x, np.float64)
    z = np.asarray(z, np.float64)
    rho = np.sqrt(x * x + z * z)
    s, c = sin_cos(rho / RS)
    safe = np.where(rho > 0, rho, 1.0)
    ex = np.where(rho > 0, -x / safe, 0.0)
    ez = np.where(rho > 0, -z / safe, 0.0)
    dx, dy, dz = ex * s, ez * s, c
    tx = dx / dz
    ty = -dy / dz
    return (tx + 1.0) * 0.5 * (RES - 1.0), (ty + 1.0) * 0.5 * (RES - 1.0)

def cr_weights(u):
    u2 = u * u
    u3 = u * u * u
    return (-0.5 * u3 + u2 - 0.5 * u, 1.5 * u3 - 2.5 * u2 + 1.0, -1.5 * u3 + 2.0 * u2 + 0.5 * u, 0.5 * u3 - 0.5 * u2)

class Base:
    def __init__(self, heights, biomes):
        self.h = heights
        self.g = biomes
        self.hv = heights.astype(np.float64) / 65535.0

    def column(self, x, z):
        """(H: SN y of the seabed, k: the rule key index into KEYS or -1) of columns (arrays)."""
        px, py = texel(x, z)
        i0 = np.floor(px).astype(np.int64)
        j0 = np.floor(py).astype(np.int64)
        wx = cr_weights(px - i0)
        wy = cr_weights(py - j0)
        rows = []
        for dj in range(4):
            r = self.hv[j0 - 1 + dj, i0 - 1]
            r = wx[0] * r + wx[1] * self.hv[j0 - 1 + dj, i0] + wx[2] * self.hv[j0 - 1 + dj, i0 + 1] + wx[3] * self.hv[j0 - 1 + dj, i0 + 2]
            rows.append(r)
        hh = wy[0] * rows[0] + wy[1] * rows[1] + wy[2] * rows[2] + wy[3] * rows[3]
        H = INNER + hh * HR - RS
        g = self.g[(py + 0.5).astype(np.int64), (px + 0.5).astype(np.int64)].astype(np.int64)
        k = np.where((g >= 1) & (g <= len(KEYS)), g - 1, -1)
        return H, k

# ------------------------------------------------------------------------------------------------- the library
def library_fnv(root=None):
    lp, _ip = B.library_paths(root)
    tp = index_tsv_path(root)
    if not (os.path.exists(lp) and os.path.exists(tp)):
        return None, None
    return fnv64(open(lp, "rb").read()), fnv64(open(tp, "rb").read())

def index_tsv_path(root=None):
    lp, _ip = B.library_paths(root)
    return os.path.join(os.path.dirname(lp), "index-v%d.tsv" % B.LIBRARY_VERSION)

def write_index_tsv(idx, path):
    """s8a_brushes.write_index_tsv (the index C# reads)."""
    return B.write_index_tsv(idx, path)

class Library:
    """The brushes as C# reads them: int8 sdf [z, y, x] (1/16 m) per id, the index rows (ground in 1/100 m)."""

    def __init__(self, idx, raw):
        self.idx = idx
        self.q = raw                                                # {id: int8 [z, y, x]}
        self.brushes = sorted(idx["brushes"], key=lambda b: b["id"])
        self.by_id = {b["id"]: b for b in self.brushes}
        self.ground = {b["id"]: int(round(b["ground"] * 100)) / 100.0 for b in self.brushes}

    @staticmethod
    def load(root=None):
        lp, ip = B.library_paths(root)
        if not (os.path.exists(lp) and os.path.exists(ip)):
            return None
        import json  # noqa: E402
        idx = json.load(open(ip, encoding="utf-8"))
        data = open(lp, "rb").read()
        assert data[:8] == b"DEBRUSH1"
        _ver, n = struct.unpack_from("<HI", data, 8)
        off, raw = 14, {}
        for _ in range(n):
            k, size, ln = struct.unpack_from("<IHI", data, off)
            off += 10
            blob = zlib.decompress(data[off:off + ln])
            off += ln
            raw[k] = np.frombuffer(blob[:size ** 3], np.int8).reshape(size, size, size)
        tp = index_tsv_path(root)
        if not os.path.exists(tp):
            write_index_tsv(idx, tp)
        return Library(idx, raw)

# ------------------------------------------------------------------------------------------------- the field
class Ring3D:
    def __init__(self, base=None, library=None, seed=SEED, planet_dir=PLANET_DIR):
        if base is None:
            base = Base(*read_planet(planet_dir))
        self.base = base
        self.lib = library if library is not None else Library.load()
        if self.lib is None:
            raise SystemExit("no brush library: python tools/planet/s8a_brushes.py build")
        self.seed = seed
        self.cave = Noise(seed, "ring3d/cave1", CAVE[1]), Noise(seed, "ring3d/cave2", CAVE[1])
        self.arch = Noise(seed, "ring3d/arch", ARCH[1])
        self.placements = []
        self.grid = {}
        self.cave_keys = np.array([("caves" in RULE_OF.get(k, (0, (), ()))[2]) for k in KEYS] + [False])
        self.arch_keys = np.array([("arches" in RULE_OF.get(k, (0, (), ()))[2]) for k in KEYS] + [False])

    # -------------------------------------------------------------- placements
    def place(self, log=print):
        """The placements over the ring: cells in row order (x outer, z inner); RingField.Place in C#."""
        t0 = time.time()
        n = int(math.ceil(HALF_M / STAMP_CELL_M))
        cells = [(a, b) for a in range(-n, n) for b in range(-n, n)]
        CX = np.array([c[0] for c in cells], np.int64)
        CZ = np.array([c[1] for c in cells], np.int64)
        jx = np.array([hash_u(self.seed, a, b, 1) for a, b in cells])
        jz = np.array([hash_u(self.seed, a, b, 2) for a, b in cells])
        X = (CX + 0.2 + 0.6 * jx) * STAMP_CELL_M
        Z = (CZ + 0.2 + 0.6 * jz) * STAMP_CELL_M
        r = np.sqrt(X * X + Z * Z)
        sel = (r >= RING_RHO[0]) & (r <= RING_RHO[1])
        X, Z, CX, CZ = X[sel], Z[sel], CX[sel], CZ[sel]
        H, K = self.base.column(X, Z)
        by_class = {}
        for b in self.lib.brushes:
            by_class.setdefault(b["class"], []).append(b)
        placed, grid = [], {}
        cell_r = int(math.ceil(REPEAT_M / STAMP_CELL_M))
        for k in range(X.size):
            key = KEYS[K[k]] if K[k] >= 0 else None
            dens, classes, _r = RULE_OF.get(key, (0, (), ()))
            cx, cz = int(CX[k]), int(CZ[k])
            if not classes or hash_u(self.seed, cx, cz, 3) >= dens / 1000.0 or H[k] > STAMP_ABOVE_Y:
                continue
            own = [b for c in classes for b in by_class.get(c, []) if b["biome"] == key]
            pool = own if len(own) >= 3 else own + [b for c in classes for b in by_class.get(c, []) if b["biome"] != key]
            if not pool:
                continue
            pool = sorted(pool, key=lambda b: b["id"])
            off = int(hash_u(self.seed, cx, cz, 7) * len(pool))
            pool = pool[off:] + pool[:off]
            xk, zk = float(X[k]), float(Z[k])
            near = set()
            for dx in range(-cell_r, cell_r + 1):
                for dz in range(-cell_r, cell_r + 1):
                    for j in grid.get((cx + dx, cz + dz), ()):
                        p = placed[j]
                        ddx, ddz = p["x"] - xk, p["z"] - zk
                        if math.sqrt(ddx * ddx + ddz * ddz) < REPEAT_M:
                            near.add(p["brush"])
            choice = next((b for b in pool if b["id"] not in near), None)
            if choice is None:
                continue
            lo, hi = SCALE_64 if choice["size"] >= 64 else SCALE_32
            s = lo / 1000.0 + (hi / 1000.0 - lo / 1000.0) * hash_u(self.seed, cx, cz, 4)
            ya, yb = 2.0 * hash_u(self.seed, cx, cz, 5) - 1.0, 2.0 * hash_u(self.seed, cx, cz, 8) - 1.0
            yl = math.sqrt(ya * ya + yb * yb)
            cy, sy = (ya / yl, yb / yl) if yl >= 1e-3 else (1.0, 0.0)
            g = self.lib.ground[choice["id"]]
            p = {"i": len(placed), "x": xk, "z": zk, "h": float(H[k]), "y0": float(H[k]) - g * s - SINK_M, "cos": cy, "sin": sy,
                 "mirror": hash_u(self.seed, cx, cz, 6) < 0.5, "scale": s, "brush": choice["id"], "size": choice["size"], "ground": g,
                 "class": choice["class"], "biome": key}
            grid.setdefault((cx, cz), []).append(len(placed))
            placed.append(p)
        self.placements = placed
        self.grid = {}
        for p in placed:
            self.grid.setdefault((int(math.floor(p["x"] / GRID_M)), int(math.floor(p["z"] / GRID_M))), []).append(p["i"])
        if log:
            log("placements: %d over the ring (%.1f s)" % (len(placed), time.time() - t0))
        return placed

    def placements_hash(self):
        h = 0xcbf29ce484222325
        for p in self.placements:
            h = fnv64(struct.pack("<dddddd", p["x"], p["z"], p["h"], p["scale"], p["cos"], p["sin"]) + struct.pack("<iii", p["brush"], 1 if p["mirror"] else 0, p["size"]), h)
        return h

    def placements_near(self, x0, x1, z0, z1):
        """Placements whose footprint may reach the box [x0, x1] x [z0, z1], ascending."""
        out = []
        for gx in range(int(math.floor((x0 - GRID_M) / GRID_M)), int(math.floor((x1 + GRID_M) / GRID_M)) + 1):
            for gz in range(int(math.floor((z0 - GRID_M) / GRID_M)), int(math.floor((z1 + GRID_M) / GRID_M)) + 1):
                for i in self.grid.get((gx, gz), ()):
                    p = self.placements[i]
                    R = p["size"] * 0.5 * p["scale"] + 1.0
                    if p["x"] + R >= x0 and p["x"] - R <= x1 and p["z"] + R >= z0 and p["z"] - R <= z1:
                        out.append(i)
        return [self.placements[i] for i in sorted(out)]

    # -------------------------------------------------------------- the field at points
    def features(self, X, Y, Z, H, K):
        """The base f = H - Y with the 3D rules (caves, arches) by the column's rule key."""
        f = H - Y
        kk = np.where(K >= 0, K, len(KEYS))
        rho = np.sqrt(X * X + Z * Z)
        edge = smoothstep(FADE[0], FADE[1], rho) * (1.0 - smoothstep(FADE[2], FADE[3], rho))
        cm = self.cave_keys[kk]
        if np.any(cm):
            wl, _o, thr, gain, b0, b1, b2, b3 = CAVE
            depth = H - Y
            band = smoothstep(b0, b1, depth) * (1.0 - smoothstep(b2, b3, depth)) * edge
            sel = cm & (band > 0)
            if np.any(sel):
                n1 = np.clip(1.7 * self.cave[0].fbm(X[sel], Y[sel], Z[sel], wl), -1.0, 1.0)
                n2 = np.clip(1.7 * self.cave[1].fbm(X[sel], Y[sel], Z[sel], wl), -1.0, 1.0)
                w = np.sqrt(n1 * n1 + n2 * n2)
                carve = (w - thr / 1000.0) * gain
                fs, bs = f[sel], band[sel]
                f[sel] = np.minimum(fs, fs * (1.0 - bs) + carve * bs)
        am = self.arch_keys[kk]
        if np.any(am):
            wl, _o, thr, gain, b0, b1, b2, b3 = ARCH
            h = Y - H
            band = smoothstep(b0, b1, h) * (1.0 - smoothstep(b2, b3, h)) * edge
            sel = am & (band > 0)
            if np.any(sel):
                a = self.arch.ridged(X[sel], Y[sel], Z[sel], wl)
                arch = (a - thr / 1000.0) * gain
                fs, bs = f[sel], band[sel]
                up = arch > fs
                f_new = np.where(up, np.maximum(fs, arch * bs + fs * (1.0 - bs)), fs)
                f[sel] = f_new
        return f

    def stamp(self, f, X, Y, Z, H, plist, weight_out=None):
        """f plus every placement's brush draped over the seabed (ascending placement order; RingField.Stamp in C#)."""
        for p in plist:
            q = self.lib.q[p["brush"]]
            n = q.shape[0]
            half = n / 2.0
            s = p["scale"]
            dx, dz = X - p["x"], Z - p["z"]
            lx = (p["cos"] * dx + p["sin"] * dz) / s + half
            lz = (-p["sin"] * dx + p["cos"] * dz) / s + half
            ly = p["ground"] + (Y - H) / s
            if p["mirror"]:
                lx = n - lx
            ex, ez = lx - half, lz - half
            rr = np.sqrt(ex * ex + ez * ez) / half
            inside = (rr < 1.0) & (ly >= 0.0) & (ly <= n - 1.0)
            if not np.any(inside):
                continue
            lxi, lyi, lzi, rri = lx[inside], ly[inside], lz[inside], rr[inside]
            m = FALLOFF_M / s
            wy = np.minimum(np.maximum(np.minimum(lyi, (n - 1.0) - lyi) / m, 0.0), 1.0)
            wh = 1.0 - smoothstep(DISC / 1000.0, 1.0, rri)
            w = wh * (wy * wy * (3.0 - 2.0 * wy))
            b = trilinear(q, lzi, lyi, lxi) * s
            clamp = 7.9 * s
            hy = H[inside] - Y[inside]
            d = b - np.minimum(np.maximum(hy, -clamp), clamp)
            f[inside] = f[inside] + w * d
            if weight_out is not None:
                weight_out[inside] = np.maximum(weight_out[inside], w)
        return f

    def field_points(self, X, Y, Z, H=None, K=None, weight_out=None, plist=None):
        """f at points (flat arrays); H, K per point (their columns) when given."""
        X, Y, Z = (np.asarray(a, np.float64).ravel() for a in (X, Y, Z))
        if H is None:
            H, K = self.base.column(X, Z)
        f = self.features(X, Y, Z, H, K)
        if plist is None:
            plist = self.placements_near(float(X.min()), float(X.max()), float(Z.min()), float(Z.max()))
        return self.stamp(f, X, Y, Z, H, plist, weight_out)

    def field_box(self, x0, y0, z0, nx, ny, nz, step=1.0, weight=False):
        """f on the grid x0 + (i + 0.5) step,... as [nx, nz, ny] (y last), the seabed and keys per column, the stamp weights."""
        xs = x0 + (np.arange(nx) + 0.5) * step
        zs = z0 + (np.arange(nz) + 0.5) * step
        ys = y0 + (np.arange(ny) + 0.5) * step
        CX, CZ = np.meshgrid(xs, zs, indexing="ij")
        Hc, Kc = self.base.column(CX.ravel(), CZ.ravel())
        X = np.repeat(CX.ravel(), ny)
        Z = np.repeat(CZ.ravel(), ny)
        Y = np.tile(ys, nx * nz)
        H = np.repeat(Hc, ny)
        K = np.repeat(Kc, ny)
        w = np.zeros(X.shape) if weight else None
        plist = self.placements_near(x0, x0 + nx * step, z0, z0 + nz * step)
        f = self.field_points(X, Y, Z, H, K, w, plist)
        return xs, zs, ys, f.reshape(nx, nz, ny), Hc.reshape(nx, nz), Kc.reshape(nx, nz), (w.reshape(nx, nz, ny) if weight else None)

    @staticmethod
    def surface(f, ys):
        """The first rock from the top per column (SN y), NaN without rock."""
        solid = f > 0
        anyc = solid.any(axis=2)
        k = f.shape[2] - 1 - np.argmax(solid[:, :, ::-1], axis=2)
        return np.where(anyc, ys[np.clip(k, 0, len(ys) - 1)] + 0.5 * (ys[1] - ys[0]), np.nan)

def trilinear(q, z, y, x):
    """The int8 field (1/16 m) at fractional [z, y, x] (clamped to the brush), interpolated x, then y, then z."""
    n = q.shape[0]
    x = np.minimum(np.maximum(x, 0.0), n - 1.0)
    y = np.minimum(np.maximum(y, 0.0), n - 1.0)
    z = np.minimum(np.maximum(z, 0.0), n - 1.0)
    x0 = np.minimum(np.floor(x).astype(np.int64), n - 2)
    y0 = np.minimum(np.floor(y).astype(np.int64), n - 2)
    z0 = np.minimum(np.floor(z).astype(np.int64), n - 2)
    fx, fy, fz = x - x0, y - y0, z - z0

    def v(zz, yy, xx):
        return q[zz, yy, xx].astype(np.float64) / 16.0
    c00 = v(z0, y0, x0) + fx * (v(z0, y0, x0 + 1) - v(z0, y0, x0))
    c10 = v(z0, y0 + 1, x0) + fx * (v(z0, y0 + 1, x0 + 1) - v(z0, y0 + 1, x0))
    c01 = v(z0 + 1, y0, x0) + fx * (v(z0 + 1, y0, x0 + 1) - v(z0 + 1, y0, x0))
    c11 = v(z0 + 1, y0 + 1, x0) + fx * (v(z0 + 1, y0 + 1, x0 + 1) - v(z0 + 1, y0 + 1, x0))
    c0 = c00 + fy * (c10 - c00)
    c1 = c01 + fy * (c11 - c01)
    return c0 + fz * (c1 - c0)

# arctan Taylor coefficients (|t| <= tan 0.3: the served zone's site angle is <= 0.21 rad; error < 1e-19)
ATAN = tuple(((-1.0) ** k) / (2.0 * k + 1.0) for k in range(1, 15))     # -1/3, 1/5,..., 1/29
MAX_SITE_T = 0.30933624960962325                                         # tan(0.3): beyond, a point is outside the served zone

def atan_small(t):
    """arctan for small t as a Horner polynomial (RingField.AtanSmall): t (1 + t2 (A3 + t2 (A5 +... + t2 A29)))."""
    t2 = t * t
    acc = ATAN[-1]
    for a in ATAN[-2::-1]:
        acc = a + t2 * acc
    return t * (1.0 + t2 * acc)

def sn_of_planet_local(px, py, pz):
    """Site v1 (C = 0) SN coordinates of planet-local points:
 E0 = -X, U0 = +Z, N0 = -Y (SiteMap.SeToSn) with the arctan as atan_small: (x, y, z, inside the served angle)."""
    le, lu, ln = -px, pz, -py
    h = np.sqrt(le * le + ln * ln)
    r = np.sqrt(le * le + lu * lu + ln * ln)
    ok = (lu > 0) & (h <= MAX_SITE_T * np.where(lu > 0, lu, 0.0))
    t = h / np.where(lu > 0, lu, 1.0)
    rho = RS * atan_small(t)
    safe = np.where(h > 0, h, 1.0)
    x = np.where(h > 0, rho * le / safe, 0.0)
    z = np.where(h > 0, rho * ln / safe, 0.0)
    return x, r - RS, z, ok

def serve_box(R, ax, ay, az, sx, sy, sz, lod):
    """The content bytes S8aRing serves for a planet-local box (voxel (i, j, k) at (ax + i lod, ay + j lod, az + k lod)), x fastest:
 RingField.ServeBox (the ReadRange prefix's own loop)."""
    ks, js, iz = np.meshgrid(np.arange(sz), np.arange(sy), np.arange(sx), indexing="ij")
    px = ax + iz.ravel() * float(lod)
    py = ay + js.ravel() * float(lod)
    pz = az + ks.ravel() * float(lod)
    x, y, z, ok = sn_of_planet_local(px, py, pz)
    plist = R.placements_near(float(x.min()) - 2.0, float(x.max()) + 2.0, float(z.min()) - 2.0, float(z.max()) + 2.0)
    f = R.field_points(x, y, z, plist=plist)
    H, _k = R.base.column(x, z)
    c = content(f, float(lod))
    c[np.isnan(H) | ~ok] = 0
    return c, len(plist)

def serve_spots(R):
    """Planet-local boxes over four kinds of spot (a stamp, a cave band, a coarse LOD over stamps, an arch band), multiples of 8 m."""
    out = []
    for bk, lod, n, dy in (("mushroomForest", 1, 24, 6.0), ("bloodKelp", 1, 32, -10.0), ("grandReef", 4, 16, 10.0), ("kelpForest", 8, 12, 0.0)):
        ps = [p for p in R.placements if p["biome"] == bk]
        best = None
        for p in ps[len(ps) // 3:len(ps) // 3 + 12]:
            d = SC.sn_to_dir(p["x"], p["z"])
            rr = RS + p["h"] + dy
            mn = [8.0 * math.floor((v * rr - n * lod / 2.0) / 8.0) for v in d]
            box = ("%s-lod%d" % (bk, {1: 0, 4: 2, 8: 3}[lod]), mn[0], mn[1], mn[2], n, n, n, lod)
            if best is None:
                best = box
            c, _n = serve_box(R, *box[1:])
            share = float((c > 127).mean())
            if 0.2 < share < 0.8:          # a box that holds both rock and water (a surface, a tunnel)
                best = box
                break
        if best is not None:
            out.append(best)
    return out

def serve_vectors(R, spots):
    lines = []
    for name, ax, ay, az, sx, sy, sz, lod in spots:
        c, near = serve_box(R, ax, ay, az, sx, sy, sz, lod)
        lines.append("serve\t%s\t%d\t%d\t%d\t%d\t%d\t%d\t%d\t%016x\t%d\t%d\t%d" % (name, ax, ay, az, sx, sy, sz, lod, fnv64(c.tobytes()),
                                                                                  int((c == 255).sum()), int((c == 0).sum()), near))
    return lines

def content(f, lod_m=1.0):
    """SE's content byte of a signed distance (MyPlanetShapeProvider.CalculateDistanceFieldInternal,: clamp(-d / lod, -1, 1) * 0.5 + 0.5, x 255), in double."""
    return ((np.minimum(np.maximum(f / lod_m, -1.0), 1.0) * 0.5 + 0.5) * 255.0).astype(np.int64).astype(np.uint8)

# ------------------------------------------------------------------------------------------------- previews
def _shade(h, pitch):
    gx, gz = np.gradient(np.nan_to_num(h, nan=np.nanmin(h) if np.isfinite(h).any() else 0.0), pitch)
    return np.clip(0.6 + 0.35 * (-gx - gz) / 2.0, 0.15, 1.0)

def preview(out_dir, ring=None, log=print):
    from PIL import Image, ImageDraw  # noqa: E402
    os.makedirs(out_dir, exist_ok=True)
    R = ring or Ring3D()
    if not R.placements:
        R.place(log=log)
    written = []
    # 1. the ring from above: the deployed seabed (face 1 pixels around the site) with the placements by class
    pitch = 46.0
    xs = np.arange(-HALF_M, HALF_M, pitch) + pitch / 2.0
    CX, CZ = np.meshgrid(xs, xs[::-1], indexing="xy")          # east right, north up
    H, _K = R.base.column(CX.ravel(), CZ.ravel())
    y = H.reshape(CX.shape)
    img = SC._depth_colour(-y) * (0.45 + 0.55 * SC._hillshade(y, pitch)[..., None])
    scale = 2
    im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    Wd, Hd = im.size
    im = im.resize((Wd * scale, Hd * scale), Image.NEAREST)
    dr = ImageDraw.Draw(im)
    for p in R.placements:
        u = (p["x"] + HALF_M) / pitch * scale
        v = (HALF_M - p["z"]) / pitch * scale
        col = CLASS_COLOURS.get(p["class"], (255, 0, 255))
        dr.rectangle([u - 1, v - 1, u + 1, v + 1], fill=col)
    entries = [("%s (%d)" % (c, sum(1 for p in R.placements if p["class"] == c)), CLASS_COLOURS[c]) for c in B.CLASSES]
    p_ = os.path.join(out_dir, "ring-placements.png")
    SC._legend(np.asarray(im).astype(np.float64), entries, "the ring: the deployed seabed, %d brush placements by class (%.1f km square)" % (
        len(R.placements), 2 * HALF_M / 1000.0), p_)
    written.append(p_)
    # 2. three cross-sections through placement-rich spots of three biomes
    for bk in ("grandReef", "bloodKelp", "mushroomForest"):
        ps = [p for p in R.placements if p["biome"] == bk]
        if not ps:
            continue
        ps.sort(key=lambda p: B._hash(R.seed, int(p["x"]), int(p["z"])))
        c = ps[0]
        x0, x1 = c["x"] - 240.0, c["x"] + 240.0
        xs = np.arange(x0, x1, 0.75) + 0.375
        Hs, Ks = R.base.column(xs, np.full(xs.shape, c["z"]))
        ytop, ybot = float(np.nanmax(Hs) + 80.0), float(np.nanmin(Hs) - 140.0)
        ys = np.arange(ybot, ytop, 0.75) + 0.375
        X = np.repeat(xs, len(ys))
        Y = np.tile(ys, len(xs))
        Z = np.full(X.shape, c["z"])
        wout = np.zeros(X.shape)
        f = R.field_points(X, Y, Z, np.repeat(Hs, len(ys)), np.repeat(Ks, len(ys)), wout,
                           R.placements_near(x0, x1, c["z"] - 1, c["z"] + 1)).reshape(len(xs), len(ys))
        wout = wout.reshape(len(xs), len(ys))
        Yg = np.broadcast_to(ys[None, :], f.shape)
        rock = f > 0
        col = np.where(rock[..., None], np.array([115, 100, 85]) * (1.0 - 0.3 * wout[..., None]) + np.array([200, 120, 40]) * 0.3 * wout[..., None],
                       np.array([30, 70, 130]) * (0.6 + 0.4 * ((Yg - ybot) / (ytop - ybot)))[..., None])
        base_line = np.abs(Yg - Hs[:, None]) < 0.4
        col[base_line] = (255, 255, 255)
        imgs = np.transpose(col, (1, 0, 2))[::-1]
        p_ = os.path.join(out_dir, "section-%s.png" % bk)
        SC._legend(imgs, [("rock (orange tint: a stamped brush)", (150, 110, 70)), ("water", (30, 80, 150)), ("the heightmap seabed (base)", (255, 255, 255))],
                   "%s section along x through SN (%.0f, %.0f): %.0f m wide, %.0f m tall, 0.75 m voxels" % (bk, c["x"], c["z"], x1 - x0, ytop - ybot), p_)
        written.append(p_)
    # 3. the stamped result, three biomes x four 96 m tiles: the top surface of the field (relief) above a vertical slice
    tiles = []
    for bk in ("grandReef", "bloodKelp", "mushroomForest"):
        ps = [p for p in R.placements if p["biome"] == bk]
        ps.sort(key=lambda p: B._hash(R.seed + 7, int(p["x"]), int(p["z"])))
        picked, used = [], set()
        for c in ps:
            if c["brush"] not in used and len(picked) < 4:
                picked.append(c)
                used.add(c["brush"])
        for c in picked:
            x0, z0 = c["x"] - 48.0, c["z"] - 48.0
            xs, zs, ys, f, Hb, _k, wout = R.field_box(x0, c["h"] - 70.0, z0, 96, 150, 96, 1.0, weight=True)
            s_ = R.surface(f, ys)
            sh = _shade(s_, 1.0)
            hcol = SC._depth_colour(-np.nan_to_num(s_, nan=-3000.0)) * sh[..., None]
            wm = wout.max(axis=2)
            hcol = hcol * (1.0 - 0.25 * wm[..., None]) + np.array([255, 160, 60]) * 0.25 * wm[..., None]
            top = np.transpose(hcol, (1, 0, 2))[::-1]
            sl = f[:, 48, :] > 0
            slc = np.where(sl[..., None], np.array([120, 105, 90]), np.array([40, 90, 150])).astype(np.float64)
            slice_img = np.transpose(slc, (1, 0, 2))[::-1]
            tiles.append((bk, c, np.concatenate([top, np.full((4, top.shape[1], 3), 255.0), slice_img], axis=0)))
    if tiles:
        th, tw = max(t[2].shape[0] for t in tiles), max(t[2].shape[1] for t in tiles)
        rows = []
        for k in range(0, len(tiles), 4):
            row = [np.pad(t[2], ((0, th - t[2].shape[0]), (0, tw - t[2].shape[1] + 6), (0, 0)), constant_values=245.0) for t in tiles[k:k + 4]]
            while len(row) < 4:
                row.append(np.full((th, tw + 6, 3), 245.0))
            rows.append(np.concatenate(row, axis=1))
            rows.append(np.full((6, rows[-1].shape[1], 3), 245.0))
        sheet = np.concatenate(rows, axis=0)
        im = Image.fromarray(np.clip(sheet, 0, 255).astype(np.uint8)).resize((sheet.shape[1] * 3, sheet.shape[0] * 3), Image.NEAREST)
        dr = ImageDraw.Draw(im)
        for k, (bk, c, t) in enumerate(tiles):
            dr.text(((k % 4) * (tw + 6) * 3 + 4, (k // 4) * (th + 6) * 3 + 4), "%s brush #%d %s x%.2f (%.0f, %.0f)" % (bk, c["brush"], c["class"], c["scale"], c["x"], c["z"]),
                    fill=(255, 255, 255))
        p_ = os.path.join(out_dir, "stamped-sheet.png")
        im.save(p_)
        written.append(p_)
    return written

# ------------------------------------------------------------------------------------------------- the oracle rows (offline)
def repeat_row(placements):
    by = {}
    for p in placements:
        by.setdefault(p["brush"], []).append((p["x"], p["z"]))
    worst = float("inf")
    for pts in by.values():
        a = np.array(pts)
        if len(a) < 2:
            continue
        d = np.sqrt(((a[:, None, :] - a[None, :, :]) ** 2).sum(-1))
        np.fill_diagonal(d, np.inf)
        worst = min(worst, float(d.min()))
    return worst >= REPEAT_M, "S8a.F.repeat %d placements of %d brushes; nearest same brush %.0f m (>= %.0f)" % (
        len(placements), len(by), worst if worst < float("inf") else -1, REPEAT_M)

def verify(R=None, log=print):
    R = R or Ring3D()
    if not R.placements:
        R.place(log=log)
    rows = list(B.verify(R.lib.idx))
    rows.append(repeat_row(R.placements))
    # seam: a window batch seam (SN x = -2048 + 160 k) on the steepest wall placement: two half boxes give the whole box's
 # surface (the field is pointwise: true by construction) and the step across it is like any step there
    walls = [p for p in R.placements if p["class"] in ("wall", "overhang")] or R.placements
    p0 = walls[len(walls) // 2]
    seam_x = -2048.0 + 160.0 * math.floor((p0["x"] + 2048.0) / 160.0 + 0.5)
    zc = p0["z"]
    y0 = p0["h"] - 60.0
    _xa, _za, ys, fa, _ha, _ka, _w = R.field_box(seam_x - 32.0, y0, zc - 16.0, 32, 130, 32)
    _xb, _zb, _ys, fb, _hb, _kb, _w2 = R.field_box(seam_x, y0, zc - 16.0, 32, 130, 32)
    _xc, _zc, _ys2, fc, _hc, _kc, _w3 = R.field_box(seam_x - 32.0, y0, zc - 16.0, 64, 130, 32)
    sa, sb, sc = R.surface(fa, ys), R.surface(fb, ys), R.surface(fc, ys)
    same = np.array_equal(np.nan_to_num(np.concatenate([sa, sb], axis=0)), np.nan_to_num(sc))
    across = np.abs(np.nan_to_num(sb[0, :]) - np.nan_to_num(sa[-1, :]))
    steps = np.abs(np.diff(np.nan_to_num(sc), axis=0))
    p95_all = float(np.percentile(steps, 95)) if steps.size else 0.0
    rows.append((same and float(np.percentile(across, 95)) <= p95_all + 1.0, "S8a.F.seam a batch seam at SN x %.0f near a %s: the two sides give the whole box's surface (%s); step across P95 %.2f m, any step P95 %.2f m" % (
        seam_x, p0["class"], "identical" if same else "DIFFERENT", float(np.percentile(across, 95)), p95_all)))
    # blend: the surface steps along brush borders against the steps inside brushes
    border_steps, inner_steps = [], []
    for p in R.placements[:: max(1, len(R.placements) // 25)][:25]:
        half = p["size"] * p["scale"] / 2.0 + 8.0
        nn = int(math.ceil(2 * half))
        xs, zs, ys, f, Hb, _k, w = R.field_box(p["x"] - half, p["h"] - 70.0, p["z"] - half, nn, 200, nn, 1.0, weight=True)
        s_ = R.surface(f, ys)
        wm = w.max(axis=2)
        st = np.abs(np.diff(np.nan_to_num(s_, nan=p["h"] - 70.0), axis=0))
        edge = (wm[1:, :] > 0) != (wm[:-1, :] > 0)
        inner = (wm[1:, :] >= 0.99) & (wm[:-1, :] >= 0.99)
        border_steps += list(st[edge])
        inner_steps += list(st[inner])
    b95 = float(np.percentile(border_steps, 95)) if border_steps else 0.0
    i95 = float(np.percentile(inner_steps, 95)) if inner_steps else 0.0
    rows.append((b95 <= max(2.0 * i95, 2.0), "S8a.F.blend %d border columns: step P95 %.2f m (inside the brushes %.2f m; bound max(2 x inside, 2 m))" % (len(border_steps), b95, i95)))
    return rows

# ------------------------------------------------------------------------------------------------- vectors (C# twin)
def synthetic_library():
    """Ten brushes (eight 32 m, two 64 m) of simple shapes, tagged so every ring biome's pool has some: deterministic."""
    shapes = []
    for n in (32, 64):
        zz, yy, xx = np.meshgrid(np.arange(n), np.arange(n), np.arange(n), indexing="ij")
        c = n // 2
        g = n // 4
        if n == 32:
            shapes.append(("boulder", n, (yy < g) | ((xx - c) ** 2 + (yy - g) ** 2 + (zz - c) ** 2 <= 49)))
            shapes.append(("boulder", n, (yy < g) | ((xx - c) ** 2 + (yy - g) ** 2 * 2 + (zz - c) ** 2 <= 81)))
            shapes.append(("pillar", n, (yy < g) | (((xx - c) ** 2 + (zz - c) ** 2 <= 9) & (yy < 28))))
            shapes.append(("wall", n, yy < (g + np.maximum(0, xx - c) * 2)))
            shapes.append(("overhang", n, (yy < g) | ((yy >= 16) & (yy < 22) & (xx < 22)) | ((xx < 6) & (yy < 22))))
            shapes.append(("cave", n, (yy < 24) & ~(((xx - c) ** 2 + (yy - 12) ** 2 <= 25) & (zz > 4))))
            shapes.append(("floor", n, yy < (g + ((xx // 4 + zz // 4) % 2))))
            shapes.append(("boulder", n, (yy < g) | ((np.abs(xx - c) < 6) & (np.abs(zz - c) < 4) & (yy < g + 7))))
        else:
            shapes.append(("wall", n, (yy < g) | ((xx < 24) & (yy < g + 30))))
            shapes.append(("overhang", n, (yy < g) | ((yy >= 34) & (yy < 42) & (xx < 44)) | ((xx < 10) & (yy < 42))))
    biomes = ("mushroomForest", "grandReef", "bloodKelp", "kelpForest", "sparseReef", "mountains", "safeShallows", "grassyPlateaus",
              "underwaterIslands", "crashZone")
    idx = {"version": B.LIBRARY_VERSION, "landmarkRadiusM": B.LANDMARK_RADIUS_M, "deepLimitY": B.DEEP_LIMIT_Y, "landmarks": 1,
           "landmarkRoots": [[764.4, 0.0, -138.6, "aurora"]], "brushes": []}
    raw = {}
    blob = bytearray(b"DEBRUSH1" + struct.pack("<HI", B.LIBRARY_VERSION, len(shapes)))
    for k, (cls, n, solid) in enumerate(shapes):
        T = solid.astype(np.uint8)
        q = B.quantize(B.sdf_of(T, np.zeros_like(T)))
        raw[k] = q
        z = zlib.compress(q.tobytes() + T.tobytes(), 6)
        blob += struct.pack("<IHI", k, n, len(z)) + z
        idx["brushes"].append({"id": k, "size": n, "biome": biomes[k % len(biomes)], "snBiome": biomes[k % len(biomes)], "class": cls,
                               "prov": [0, 0, 0, k], "origin": [-1000.0 + 40.0 * k, -200.0, 500.0], "ground": round(B.ground_level(solid), 2),
                               "features": {}, "score": 1.0})
    return bytes(blob), idx, raw

def vector_spots(R, count=4):
    """Boxes (x0, y0, z0, nx, ny, nz) around the first placements of four biomes (and one plain-base box in the Dunes)."""
    spots = []
    for bk in ("grandReef", "bloodKelp", "mushroomForest", "kelpForest", "sparseReef", "mountains"):
        ps = [p for p in R.placements if p["biome"] == bk]
        if not ps:
            continue
        p = ps[0]
        spots.append(("%s-%d" % (bk, p["i"]), float(math.floor(p["x"]) - 16), float(math.floor(p["h"]) - 40), float(math.floor(p["z"]) - 16), 32, 72, 32))
        if len(spots) >= count:
            break
    big = [p for p in R.placements if p["size"] >= 64 and p["scale"] > 1.6]
    if big:
        p = big[0]
        spots.append(("big-%d" % p["i"], float(math.floor(p["x"]) - 24), float(math.floor(p["h"]) - 30), float(math.floor(p["z"]) - 24), 48, 96, 48))
    xs, zs = np.meshgrid(np.arange(-12000.0, 12000.0, 400.0), np.arange(-12000.0, 12000.0, 400.0), indexing="ij")
    rr = np.sqrt(xs * xs + zs * zs)
    H, K = R.base.column(xs.ravel(), zs.ravel())
    for k in range(H.size):
        if RING_RHO[0] + 200 < rr.ravel()[k] < RING_RHO[1] - 200 and K[k] >= 0 and KEYS[K[k]] == "dunes":
            x, z = float(xs.ravel()[k]), float(zs.ravel()[k])
            spots.append(("dunes-base", x - 16, float(math.floor(H[k])) - 20, z - 16, 32, 40, 32))
            break
    return spots

def vectors_for(R, spots):
    lines = []
    for name, x0, y0, z0, nx, ny, nz in spots:
        # C# order: x fastest, then y, then z (MyStorageData's linear order)
        xs = x0 + np.arange(nx) + 0.5
        ys = y0 + np.arange(ny) + 0.5
        zs = z0 + np.arange(nz) + 0.5
        Zg, Yg, Xg = np.meshgrid(zs, ys, xs, indexing="ij")
        X, Y, Z = Xg.ravel(), Yg.ravel(), Zg.ravel()
        plist = R.placements_near(x0, x0 + nx, z0, z0 + nz)
        f = R.field_points(X, Y, Z, plist=plist)
        c = content(f, 1.0)
        solid = int((c == 255).sum())
        empty = int((c == 0).sum())
        probe = [int(len(f) * k / 7) for k in range(7)]
        lines.append("box\t%s\t%d\t%d\t%d\t%d\t%d\t%d\t%016x\t%d\t%d\t%d\t%s" % (name, x0, y0, z0, nx, ny, nz, fnv64(c.tobytes()), solid, empty, len(plist),
                                                                         ",".join("%d:%s" % (i, repr(float(f[i]))) for i in probe)))
    return lines

def write_vectors(out_dir=VECTOR_DIR, log=print):
    os.makedirs(out_dir, exist_ok=True)
    blob, idx, raw = synthetic_library()
    lib_path = os.path.join(out_dir, "synthetic-library.bin")
    open(lib_path, "wb").write(blob)
    write_index_tsv(idx, os.path.join(out_dir, "synthetic-index.tsv"))
    heights, biomes = read_planet()
    base = Base(heights, biomes)
    R = Ring3D(base, Library(idx, raw))
    R.place(log=None)
    head = ["de-ring-vectors %d" % FIELD_VERSION, "rules\t%016x" % fnv64(rules_text().encode("utf-8")),
            "heights\t%016x" % fnv64(heights.tobytes()), "biomes\t%016x" % fnv64(biomes.tobytes())]
    lines = head + ["library\t%016x\t%016x" % (fnv64(blob), fnv64(open(os.path.join(out_dir, "synthetic-index.tsv"), "rb").read())),
                    "placements\t%d\t%016x" % (len(R.placements), R.placements_hash())]
    pts = [(1000.0 * k - 9000.0, 3000.0 + 700.0 * k) for k in range(12)]
    Hs, Ks = base.column(np.array([p[0] for p in pts]), np.array([p[1] for p in pts]))
    lines += ["column\t%s\t%s\t%016x\t%d\t%s" % (repr(x), repr(z), struct.unpack("<Q", struct.pack("<d", float(h)))[0], int(k), repr(float(h)))
              for (x, z), h, k in zip(pts, Hs, Ks)]
    lines += vectors_for(R, vector_spots(R))
    lines += serve_vectors(R, serve_spots(R))
    p = os.path.join(out_dir, "ring3d-vectors.tsv")
    open(p, "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
    log("vectors %s: %d placements, %d boxes" % (p, len(R.placements), sum(1 for ln in lines if ln.startswith("box"))))
    written = [p]
    # the install set: the store's library (when built), so the C# test can check the field the game serves
    lib = Library.load()
    if lib is not None:
        R2 = Ring3D(base, lib)
        R2.place(log=None)
        lf, tf = library_fnv()
        l2 = head + ["library\t%016x\t%016x" % (lf, tf), "placements\t%d\t%016x" % (len(R2.placements), R2.placements_hash())]
        l2 += vectors_for(R2, vector_spots(R2))
        l2 += serve_vectors(R2, serve_spots(R2))
        p2 = os.path.join(out_dir, "ring3d-install-vectors.tsv")
        open(p2, "w", encoding="utf-8", newline="\n").write("\n".join(l2) + "\n")
        log("vectors %s: %d placements (the store's library)" % (p2, len(R2.placements)))
        written.append(p2)
    return written

# ------------------------------------------------------------------------------------------------- selftest
def selftest():
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print("%-4s %s %s" % ("OK" if cond else "FAIL", name, detail))
    th = np.array([0.0, 0.05, 0.13, 0.21, 0.25])
    s, c = sin_cos(th)
    check("sin/cos polynomial", float(np.max(np.abs(s - np.sin(th)))) < 1e-15 and float(np.max(np.abs(c - np.cos(th)))) < 1e-15)
    heights, biomes = read_planet()
    base = Base(heights, biomes)
    # the base at a pixel centre equals the pixel's own height (Catmull-Rom interpolates)
    px, py = texel(np.array([5000.0]), np.array([-3000.0]))
    i, j = int(round(float(px[0]))), int(round(float(py[0])))
    check("texel inside face 1", 0 < i < RES - 1 and 0 < j < RES - 1, "(%d, %d)" % (i, j))
    xs, zs = SC.sn_xz(*SC.face_dirs_at(1, RES, np.array([j]), np.array([i])))
    H, _k = base.column(np.array([float(xs[0, 0])]), np.array([float(zs[0, 0])]))
    check("seabed = the pixel at its centre", abs(float(H[0]) - float(SC.decode_height(heights[j, i]))) < 0.02, "%.3f vs %.3f" % (float(H[0]), float(SC.decode_height(heights[j, i]))))
    blob, idx, raw = synthetic_library()
    R = Ring3D(base, Library(idx, raw))
    pl = R.place(log=None)
    check("placements made", len(pl) > 100, "%d" % len(pl))
    good, line = repeat_row(pl)
    check("no brush twice within RepeatM", good, line)
    big = [p for p in pl if p["size"] == 64]
    check("64 m groups scaled 0.9-2.0", big and max(p["scale"] for p in big) > 1.5 and min(p["scale"] for p in big) >= 0.9, "%d, max %.2f" % (len(big), max([p["scale"] for p in big] or [0])))
    small = [p for p in pl if p["size"] == 32]
    check("32 m brushes scaled 0.9-1.15", small and max(p["scale"] for p in small) <= 1.15 and min(p["scale"] for p in small) >= 0.9)
    R2 = Ring3D(base, Library(idx, raw))
    R2.place(log=None)
    check("deterministic", R.placements_hash() == R2.placements_hash())
    p = next(p for p in pl if p["class"] == "pillar")
    xs, zs, ys, f, Hb, _k, w = R.field_box(p["x"] - 20, p["h"] - 10, p["z"] - 20, 40, 60, 40, 1.0, weight=True)
    s_ = R.surface(f, ys)
    hc = float(np.nanmax(s_[18:22, 18:22]))
    check("a pillar stands above the seabed at its placement", hc > float(Hb[20, 20]) + 6.0, "%.1f over %.1f" % (hc, float(Hb[20, 20])))
    edge = float(np.nanmax(np.abs(s_[0, :] - Hb[0, :])))
    check("the box's edge meets the base", edge < 1.01, "%.2f m" % edge)
    # pointwise: the whole box equals its two halves
    a = R.field_box(p["x"] - 20, p["h"] - 10, p["z"] - 20, 20, 60, 40)[3]
    b = R.field_box(p["x"], p["h"] - 10, p["z"] - 20, 20, 60, 40)[3]
    check("pointwise field (seams by construction)", np.array_equal(np.concatenate([a, b], axis=0), f))
    rt = rules_text()
    check("rules text", rt.startswith("de-ring-rules 2\n") and "rule bloodKelp 450 cave,overhang,wall caves" in rt)
    print("S8A RING3D SELFTEST %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("cmd", nargs="?", choices=("preview", "verify", "vectors", "index"))
    ap.add_argument("--out", default=os.path.join(REPO, "tests", "out", "s8a", "stageF-preview"))
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.cmd == "preview":
        R = Ring3D()
        R.place()
        for p in preview(a.out, R):
            print("preview %s" % p)
        for good, line in verify(R):
            print("%-4s %s" % ("OK" if good else "FAIL", line))
        return 0
    if a.cmd == "verify":
        rows = verify()
        for good, line in rows:
            print("%-4s %s" % ("OK" if good else "FAIL", line))
        return 0 if all(g for g, _l in rows) else 1
    if a.cmd == "vectors":
        for p in write_vectors():
            print("wrote %s" % p)
        return 0
    if a.cmd == "index":
        import json  # noqa: E402
        lp, ip = B.library_paths()
        if not os.path.exists(ip):
            print("no library index: python tools/planet/s8a_brushes.py build")
            return 1
        print("wrote %s" % write_index_tsv(json.load(open(ip, encoding="utf-8")), index_tsv_path()))
        return 0
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main())
