"""S8a Stage F: the terrain brush library.

A brush is a solid form at the 32-64 m scale taken from Subnautica's own terrain (the install's compiled octree batches of the
Subnautica map, read at run time, read-only): one octree (32 m) or a 2 x 2 x 2 group (64 m) that holds a recognisable form,
classified by shape (wall, boulder, overhang, cave, pillar, floor) and tagged by the biome it comes from (the install's
biome map: Build18/biomeMap.bin + biomes.csv, the game's own lookup VERIFIED subnautica/Assembly-CSharp/WorldBuilding/
BiomeMapData.cs:36-38,:106-116: width = byte[-2] * 64, index = (z / f) * width + x / f, f = 4096 / width, x and z in
blocks = SN + 2048). Never copied (CONTRACT §3.3 "Stage F"): the octrees within LandmarkRadiusM of a landmark's root (the
Aurora, lifepods, wrecks, Precursor and Degasi bases: their roots from the install's baked entity cells, read-only) and the
octrees below DeepLimitY (the Lost River's and the lava zones' unique places).

The library is our own derived data, never the install's files: each brush is re-encoded as a signed distance field (metres,
positive in rock; int8 in 1/16 m, clamped to +-7.9 m: the converted surface voxels give the sub-voxel distance, the rest
the Euclidean distance transform of the solid mask) plus the Subnautica block type per voxel, with its provenance id
(batch, octree, group). It is cached in the store (%LOCALAPPDATA%\\DeepEngineers\\terrain\\brushes\\library-v1.bin and
index-v1.json), rebuilt by `build`.

Usage (repository root; numpy, scipy; Pillow for the sheets; read-only on the install):
 python tools/planet/s8a_brushes.py build [--target N] [--seed S] scan the install, write the library into the store
 python tools/planet/s8a_brushes.py list counts per biome and class, the provenance of each
 python tools/planet/s8a_brushes.py preview [--out DIR] contact sheets (top-down relief + a vertical slice)
 python tools/planet/s8a_brushes.py verify S8a.F.library and S8a.F.forbidden
 python tools/planet/s8a_brushes.py --selftest the classifier and the codec on synthetic forms
"""
import argparse
import glob
import json
import math
import os
import struct
import sys
import time
import zlib

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "sn-octrees"))
import sn_octree_write as W  # noqa: E402 (our own twin: parse_batch, decode_octree)

S = 32
NEAR_ZERO = 126
SDF_UNIT = 1.0 / 16.0
SDF_CLAMP_M = 7.9
CLASSES = ("wall", "boulder", "overhang", "cave", "pillar", "floor")
LANDMARK_PATTERNS = ("aurora", "wreck", "lifepod", "escapepod", "precursor", "degasi", "starship", "base/")
LANDMARK_RADIUS_M = 48.0
DEEP_LIMIT_Y = -600.0
SURFACE_BY = range(15, 20)             # SN y -640.. +160: the seabed of the Subnautica map
MIN_NODES = 260                        # an octree this complex holds a surface form
PER_BATCH = 4                          # candidates kept per batch (the most complex, then a seeded pick)
POOL_MAX = 3200                        # candidates decoded in all (batches in a seeded order until the pool is full)
LIBRARY_VERSION = 1
# the ring's biomes (layout_4546b.json keys) and the install's names that stand for them
BIOME_OF_SN = {"safeShallows": "safeShallows", "kelpForest": "kelpForest", "grassyPlateaus": "grassyPlateaus", "underwaterIslands": "underwaterIslands",
               "mushroomForest": "mushroomForest", "kooshZone": "sparseReef", "grandReef": "grandReef", "crashZone": "crashZone", "sparseReef": "sparseReef",
               "dunes": "dunes", "bloodKelp": "bloodKelp", "bloodKelpTwo": "bloodKelp", "mountains": "mountains", "seaTreaderPath": "grassyPlateaus",
               "CragField": "mountains"}

# ------------------------------------------------------------------------------------------------- paths
def sn_dir():
    return os.environ.get("DEEPENG_SUBNAUTICA") or W.STEAM_DEFAULT

def build18():
    return os.path.join(sn_dir(), "Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "Build18")

def store_dir():
    return os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers", "terrain", "brushes")

def library_paths(root=None):
    d = root or store_dir()
    return os.path.join(d, "library-v%d.bin" % LIBRARY_VERSION), os.path.join(d, "index-v%d.json" % LIBRARY_VERSION)

# ------------------------------------------------------------------------------------------------- the biome map
class BiomeMap:
    """The install's biome map (BiomeMapData.Load): names per 4-block column cell; name_at(SN x, z)."""

    def __init__(self, folder=None):
        folder = folder or build18()
        data = open(os.path.join(folder, "biomeMap.bin"), "rb").read()
        self.w, self.h = data[-2] * 64, data[-1] * 64
        self.idx = np.frombuffer(data, np.uint8, self.w * self.h).reshape(self.h, self.w)
        names = [ln.strip() for ln in open(os.path.join(folder, "biomes.csv"), encoding="utf-8").read().splitlines()]
        self.names = names[1:]                         # the header row "name"
        self.f = 4096 // self.w

    def name_at(self, x, z):
        bx, bz = int(math.floor(x + 2048.0)), int(math.floor(z + 2048.0))
        if not (0 <= bx < 4096 and 0 <= bz < 4096):
            return None
        i = int(self.idx[bz // self.f, bx // self.f])
        return self.names[i] if i < len(self.names) else None

    def majority(self, x0, z0, size):
        votes = {}
        for k in range(5):
            for m in range(5):
                n = self.name_at(x0 + (k + 0.5) * size / 5.0, z0 + (m + 0.5) * size / 5.0)
                if n:
                    votes[n] = votes.get(n, 0) + 1
        return max(votes.items(), key=lambda kv: kv[1])[0] if votes else None

# ------------------------------------------------------------------------------------------------- landmarks (read-only scan)
def landmark_roots(limit_files=None, log=print):
    """[(x, y, z, path)] of the baked entity cells' objects whose prefab path names a landmark (LANDMARK_PATTERNS), plus the
 Aurora's root (its lightmapped scene is not in the cells: SN (764.4, -70.2, -138.6), read once from the scene bundle)."""
    import sn_prefab_colliders as P  # noqa: E402 (our census reader: prefabs.db, the protobuf object trees)
    paths = P.sn_paths(sn_dir())
    db = P.read_prefabs_db(open(paths["prefabsDb"], "rb").read())
    want = {cid for cid, p in db.items() if any(k in p.lower() for k in LANDMARK_PATTERNS)}
    out = [(764.4, -70.2, -138.6, "scene:aurora (root)")]
    files = sorted(glob.glob(os.path.join(paths["cells"], "baked-batch-cells-*.bin")))
    if limit_files:
        files = files[:limit_files]
    for fn in files:
        try:
            data = open(fn, "rb").read()
            for root, objs in _cell_objects(P, data):
                for cid, pos in objs:
                    if cid in want:
                        out.append((pos[0], pos[1], pos[2], db[cid]))
        except Exception as e:      # a file the walker cannot read is reported
            log("  landmarks: %s not read (%s)" % (os.path.basename(fn), e))
    return out

def _v3(P, b):
    f = {ff: vv for ff, _wt, vv in P.pb_fields(b)}
    return tuple(struct.unpack("<f", f[k])[0] if k in f else 0.0 for k in (1, 2, 3))

def _cell_objects(P, data):
    """Per cell (root position, [(classId, world position)]): the object trees of a baked cells file with the Transform data
 (field 1 localPosition) of every object; the root's position is the cell centre in SN world, children local to it (the
 rotation of an intermediate parent ignored: landmarks are judged within LANDMARK_RADIUS_M)."""
    i = 0
    hdr, i = P.lp(data, i)
    hf = P.pb_fields(hdr)
    version, ncells = P.first(hf, 1, 0), P.first(hf, 2, 0)
    for _ in range(ncells):
        ch, i = P.lp(data, i)
        cf = P.pb_fields(ch)
        dl, ll, wl = P.first(cf, 3, 0), P.first(cf, 4, 0), P.first(cf, 5, 0)
        objs = []
        for length, header in ((dl, True), (ll if version >= 9 else 0, True)):
            if length:
                objs += _tree(P, data[i:i + length], header)
                i += length
        if version >= 9 and wl:
            blob = data[i:i + wl]
            j = 0
            while j < len(blob):
                o, j = _tree_at(P, blob, j, False)
                objs += o
            i += wl
        pos, par = {}, {}
        for uid, parent, cid, lp_ in objs:
            pos[uid], par[uid] = lp_, parent
        world = []
        for uid, parent, cid, lp_ in objs:
            p = lp_
            q, k = parent, 0
            while q and q in pos and k < 16:
                p = (p[0] + pos[q][0], p[1] + pos[q][1], p[2] + pos[q][2])
                q, k = par.get(q), k + 1
            world.append((cid, p))
        yield (None, world)

def _tree(P, blob, header):
    o, _j = _tree_at(P, blob, 0, header)
    return o

def _tree_at(P, b, i, header):
    if header:
        _, i = P.lp(b, i)
    loop, i = P.lp(b, i)
    count = P.first(P.pb_fields(loop), 1, 0)
    out = []
    for _ in range(count):
        god, i = P.lp(b, i)
        fd = {f: v for f, _w, v in P.pb_fields(god)}
        cl, i = P.lp(b, i)
        cc = P.first(P.pb_fields(cl), 1, 0)
        lp_ = (0.0, 0.0, 0.0)
        for _ in range(cc):
            chh, i = P.lp(b, i)
            d, i = P.lp(b, i)
            if P.first(P.pb_fields(chh), 1, b"") == b"UnityEngine.Transform":
                f = {ff: vv for ff, _w, vv in P.pb_fields(d)}
                if 1 in f:
                    lp_ = _v3(P, f[1])
        cid = fd.get(7, b"")
        out.append((fd.get(6, b"").decode("utf-8", "replace"), fd.get(8, b"").decode("utf-8", "replace") if 8 in fd else None,
                    cid.decode("utf-8", "replace") if isinstance(cid, bytes) else "", lp_))
    return out, i

# ------------------------------------------------------------------------------------------------- the field of a brush
def solid_of(T, D):
    """IsBelowSurface per voxel (sn_octree_write's rule): a near voxel (density != 0) is rock from NearZero up, a far one by its type."""
    return np.where(D == 0, T > 0, D >= NEAR_ZERO)

def sdf_of(T, D):
    """Signed distance (m, positive in rock) of a raster [z, y, x]: the converted surface voxels' own distance (density - 126) / 126,
 elsewhere the Euclidean distance transform of the solid mask (voxel centres, -0.5 m to meet the surface)."""
    from scipy import ndimage  # noqa: E402
    solid = solid_of(T, D)
    din = ndimage.distance_transform_edt(solid)
    dout = ndimage.distance_transform_edt(~solid)
    s = np.where(solid, din - 0.5, -(dout - 0.5))
    near = D != 0
    s = np.where(near, (D.astype(np.float64) - NEAR_ZERO) / NEAR_ZERO, s)
    return np.clip(s, -SDF_CLAMP_M, SDF_CLAMP_M)

def quantize(sdf):
    return np.clip(np.rint(sdf / SDF_UNIT), -127, 127).astype(np.int8)

def dequantize(q):
    return q.astype(np.float32) * SDF_UNIT

# ------------------------------------------------------------------------------------------------- shape classes
def features(solid):
    """Shape features of a solid mask [z, y, x] (y up): fill, top-surface relief, slope, overhang and cave shares, a pillar's
 mid cross-section, a boulder's bump; the class is the strongest (CLASSES)."""
    n = solid.shape[0]
    fill = float(solid.mean())
    any_col = solid.any(axis=1)                                        # [z, x]
    top = np.where(any_col, n - 1 - np.argmax(solid[:, ::-1, :], axis=1), -1).astype(np.float64)
    gz, gx = np.gradient(np.where(any_col, top, np.nan))
    g = np.hypot(gx, gz)
    fin = np.isfinite(g)
    slope = float(g[fin].mean()) if fin.any() else 0.0
    steep = float((g[fin] > 1.5).mean()) if fin.any() else 0.0
    relief = float(np.nanmax(np.where(any_col, top, np.nan)) - np.nanmin(np.where(any_col, top, np.nan))) if any_col.any() else 0.0
    # overhang: a column with rock above water above rock (or above the brush's floor)
    s = solid.astype(np.int8)
    down = np.diff(s, axis=1)                                          # +1: rock starts going up, -1: rock ends going up
    starts = (down == 1).sum(axis=1)                                   # [z, x] rock runs beginning above water
    overhang = float(((starts >= 1) & (s[:, 0, :] == 1)).mean() + (starts >= 2).mean())
    # cave: water enclosed vertically (rock above and below in its column) and on two horizontal sides within 6 voxels (a tunnel
 # is open only along its axis)
    water = ~solid
    above = np.flip(np.maximum.accumulate(np.flip(solid, axis=1), axis=1), axis=1)   # rock somewhere above (inclusive)
    below = np.maximum.accumulate(solid, axis=1)                                     # rock somewhere below (inclusive)
    enclosed = water & above & below
    k = 6
    sides = np.zeros(solid.shape, np.int8)
    for ax in (0, 2):
        for sgn in (1, -1):
            sh = np.zeros(solid.shape, bool)
            for d in range(1, k + 1):
                sh |= _shift(solid, sgn * d, ax)
            sides += sh
    cave = float((enclosed & (sides >= 2)).sum() / max(1, water.sum()))
    # pillar: a narrow mid-height cross-section standing on the floor and reaching high
    mid = solid[:, n // 2, :]
    mid_area = float(mid.mean())
    pillar = float(mid_area < 0.18 and mid_area > 0.01 and relief >= 0.6 * n and fill < 0.45)
    # boulder: a compact bump above the median surface (a small share of the area, not touching the brush's border)
    if any_col.any():
        med = float(np.median(top[any_col]))
        hi = (top - med) > 4
        border = np.concatenate([hi[0, :], hi[-1, :], hi[:, 0], hi[:, -1]])
        bump = float((top.max() - med)) if hi.mean() < 0.35 and border.mean() < 0.1 else 0.0
    else:
        bump = 0.0
    top_water = float((~solid[:, n - 1, :]).mean())
    bottom_rock = float(solid[:, 0, :].mean())
    f = {"fill": fill, "slope": slope, "steep": steep, "relief": relief, "topWater": top_water, "bottomRock": bottom_rock, "overhang": overhang, "cave": cave, "midArea": mid_area, "pillar": pillar, "bump": bump}
    f["class"] = classify(f, n)
    return f

def classify(f, n):
    # a form stands on ground with water above it
    if f.get("topWater", 1.0) < 0.3 or f.get("bottomRock", 1.0) < 0.5:
        return None
    scores = {
        "cave": f["cave"] * 6.0 if f["cave"] > 0.04 else 0.0,
        "overhang": f["overhang"] * 1.5 if f["overhang"] > 0.12 else 0.0,
        "pillar": 3.0 if f["pillar"] else 0.0,
        "wall": 0.6 + f["steep"] if f["steep"] > 0.15 and f["relief"] > 0.45 * n else 0.0,
        "boulder": f["bump"] / (0.25 * n) if f["bump"] > 0.15 * n else 0.0,
        "floor": 0.3 if f["slope"] < 0.5 and f["steep"] < 0.05 and 0.15 < f["fill"] < 0.85 else 0.0,
    }
    best = max(scores.items(), key=lambda kv: kv[1])
    return best[0] if best[1] > 0 else None

def _shift(a, d, axis):
    """a shifted by d along axis without wrapping (the vacated side False)."""
    out = np.zeros_like(a)
    src = [slice(None)] * 3
    dst = [slice(None)] * 3
    if d > 0:
        src[axis], dst[axis] = slice(0, a.shape[axis] - d), slice(d, None)
    else:
        src[axis], dst[axis] = slice(-d, None), slice(0, a.shape[axis] + d)
    out[tuple(dst)] = a[tuple(src)]
    return out

def ground_level(solid):
    """The brush's ground (voxels from the bottom): the median top surface over its border columns (where it meets the base)."""
    n = solid.shape[0]
    any_col = solid.any(axis=1)
    top = np.where(any_col, n - 1 - np.argmax(solid[:, ::-1, :], axis=1), 0)
    border = np.concatenate([top[0, :], top[-1, :], top[:, 0], top[:, -1]])
    return float(np.median(border)) + 0.5

# ------------------------------------------------------------------------------------------------- build
def _octree_box(bx, by, bz, i):
    ix, iy, iz = W.octree_coords(i)
    x0 = bx * 160 + ix * 32 - 2048.0
    y0 = by * 160 + iy * 32 - 3040.0
    z0 = bz * 160 + iz * 32 - 2048.0
    return x0, y0, z0

def _near_landmark(lm, x0, y0, z0, size):
    for x, y, z, _p in lm:
        dx = max(x0 - x, 0.0, x - (x0 + size))
        dy = max(y0 - y, 0.0, y - (y0 + size))
        dz = max(z0 - z, 0.0, z - (z0 + size))
        if dx * dx + dy * dy + dz * dz <= LANDMARK_RADIUS_M * LANDMARK_RADIUS_M:
            return True
    return False

def _hash(*v):
    h = 0xcbf29ce484222325
    for x in v:
        for b in struct.pack("<q", int(x)):
            h = ((h ^ b) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def build(target=320, seed=4546, root=None, log=print, octree_dir=None, max_batches=None):
    t0 = time.time()
    octree_dir = octree_dir or W.default_octree_dir()
    bm = BiomeMap()
    log("biome map %dx%d (factor %d), %d names" % (bm.w, bm.h, bm.f, len(bm.names)))
    lm = landmark_roots(log=log)
    log("landmarks: %d roots (%.1f s)" % (len(lm), time.time() - t0))
    files = []
    for by in SURFACE_BY:
        for bx in range(0, 26):
            for bz in range(0, 26):
                p = os.path.join(octree_dir, "compiled-batch-%d-%d-%d.optoctrees" % (bx, by, bz))
                if os.path.exists(p):
                    files.append((bx, by, bz, p))
    files.sort(key=lambda f: _hash(seed, f[0], f[1], f[2]))
    if max_batches:
        files = files[:max_batches]
    pool, skipped = [], {"landmark": 0, "deep": 0, "simple": 0}
    for bx, by, bz, p in files:
        if len(pool) >= POOL_MAX:
            break
        data = open(p, "rb").read()
        _v, entries = W.parse_batch(data, bx, by, bz)
        cands = []
        for i, e in enumerate(entries):
            if e is None:
                continue
            if e[1] < MIN_NODES:
                skipped["simple"] += 1
                continue
            x0, y0, z0 = _octree_box(bx, by, bz, i)
            if y0 < DEEP_LIMIT_Y:                         # the whole box above the deep limit (S8a.F.forbidden's rule)
                skipped["deep"] += 1
                continue
            if _near_landmark(lm, x0, y0, z0, 32.0):
                skipped["landmark"] += 1
                continue
            cands.append((e[1], i, e))
        cands.sort(key=lambda c: (-c[0], _hash(seed, bx, by, bz, c[1])))
        for _n, i, e in cands[:PER_BATCH]:
            pool.append((bx, by, bz, i, p, e))
    log("candidates: %d octrees from %d batches (skipped %s; %.1f s)" % (len(pool), len(files), skipped, time.time() - t0))
    brushes = []
    cache = {}

    def raster(bx, by, bz, i, path):
        key = (bx, by, bz, i)
        if key not in cache:
            data = cache.get(path)
            if data is None:
                data = cache[path] = open(path, "rb").read()
            _v, entries = W.parse_batch(data, bx, by, bz)
            e = entries[i]
            cache[key] = W.decode_octree(data, e[0], e[1]) if e else (np.zeros((S, S, S), np.uint8), np.zeros((S, S, S), np.uint8))
        return cache[key]

    for bx, by, bz, i, path, e in pool:
        T, D = raster(bx, by, bz, i, path)
        solid = solid_of(T, D)
        f = features(solid)
        if f["class"] is None:
            continue
        x0, y0, z0 = _octree_box(bx, by, bz, i)
        sn_biome = bm.majority(x0, z0, 32.0)
        biome = BIOME_OF_SN.get(sn_biome)
        if biome is None:
            continue
        brushes.append({"size": 32, "prov": [bx, by, bz, i], "origin": [x0, y0, z0], "snBiome": sn_biome, "biome": biome, "class": f["class"],
                        "features": {k: round(v, 3) for k, v in f.items() if k != "class"}, "_T": T, "_D": D, "score": _score(f)})
        if len(cache) > 400:
            cache.clear()
    log("classified: %d single-octree forms (%.1f s)" % (len(brushes), time.time() - t0))
    # 2 x 2 x 2 groups for the large forms: the group of the best octrees of those classes, when the group stays in its batch
    groups = []
    big = sorted((b for b in brushes if b["class"] in ("cave", "overhang", "pillar", "wall")), key=lambda b: -b["score"])[:90]
    for b in big:
        bx, by, bz, i = b["prov"]
        ix, iy, iz = W.octree_coords(i)
        gx, gy, gz = min(ix, 3), min(iy, 3), min(iz, 3)
        path = os.path.join(octree_dir, "compiled-batch-%d-%d-%d.optoctrees" % (bx, by, bz))
        T8 = np.zeros((64, 64, 64), np.uint8)
        D8 = np.zeros((64, 64, 64), np.uint8)
        ok = True
        for dx in (0, 1):
            for dy in (0, 1):
                for dz in (0, 1):
                    j = (gx + dx) * 25 + (gy + dy) * 5 + (gz + dz)
                    if not W.is_stored(bx, by, bz, j):
                        ok = False
                        continue
                    t_, d_ = raster(bx, by, bz, j, path)
                    T8[dz * 32:(dz + 1) * 32, dy * 32:(dy + 1) * 32, dx * 32:(dx + 1) * 32] = t_
                    D8[dz * 32:(dz + 1) * 32, dy * 32:(dy + 1) * 32, dx * 32:(dx + 1) * 32] = d_
        if not ok:
            continue
        x0 = bx * 160 + gx * 32 - 2048.0
        y0 = by * 160 + gy * 32 - 3040.0
        z0 = bz * 160 + gz * 32 - 2048.0
        if y0 < DEEP_LIMIT_Y or _near_landmark(lm, x0, y0, z0, 64.0):
            continue
        f = features(solid_of(T8, D8))
        if f["class"] is None:
            continue
        groups.append({"size": 64, "prov": [bx, by, bz, -(gx * 25 + gy * 5 + gz) - 1], "origin": [x0, y0, z0], "snBiome": b["snBiome"], "biome": b["biome"],
                       "class": f["class"], "features": {k: round(v, 3) for k, v in f.items() if k != "class"}, "_T": T8, "_D": D8, "score": _score(f) + 0.5})
    log("groups: %d 64 m forms" % len(groups))
    chosen = select(brushes + groups, target)
    lib, idx = encode_library(chosen, lm)
    lp, ip = library_paths(root)
    os.makedirs(os.path.dirname(lp), exist_ok=True)
    with open(lp + ".tmp", "wb") as fh:
        fh.write(lib)
    os.replace(lp + ".tmp", lp)
    with open(ip + ".tmp", "w", encoding="utf-8") as fh:
        json.dump(idx, fh, indent=1)
    os.replace(ip + ".tmp", ip)
    write_index_tsv(idx, os.path.join(os.path.dirname(lp), "index-v%d.tsv" % LIBRARY_VERSION))
    log("library: %d brushes, %.1f MB -> %s (%.1f s)" % (len(chosen), len(lib) / 1e6, lp, time.time() - t0))
    return idx

def _score(f):
    return f["relief"] / 32.0 + f["overhang"] + 4.0 * f["cave"] + f["slope"] * 0.3 + f["bump"] / 16.0

def select(cands, target):
    """A balanced pick: per (biome, class) round robin by score, so every biome keeps every class it has, up to target."""
    by = {}
    seen = set()
    for b in sorted(cands, key=lambda b: -b["score"]):
        key = tuple(b["prov"])
        if key in seen:
            continue                                   # one brush per provenance (two octrees of one group made the same group)
        seen.add(key)
        by.setdefault((b["biome"], b["class"]), []).append(b)
    out = []
    while len(out) < target and any(by.values()):
        for k in sorted(by):
            if by[k] and len(out) < target:
                out.append(by[k].pop(0))
    return out

def encode_library(brushes, landmarks):
    """library bytes: magic, version, count, then per brush: u32 id, u16 size, then zlib(int8 sdf + uint8 types) with its length."""
    out = bytearray(b"DEBRUSH1" + struct.pack("<HI", LIBRARY_VERSION, len(brushes)))
    idx = {"version": LIBRARY_VERSION, "unitM": SDF_UNIT, "clampM": SDF_CLAMP_M, "landmarkRadiusM": LANDMARK_RADIUS_M, "deepLimitY": DEEP_LIMIT_Y,
           "landmarks": len(landmarks), "landmarkRoots": [[round(x, 1), round(y, 1), round(z, 1), p] for x, y, z, p in landmarks], "brushes": []}
    for k, b in enumerate(brushes):
        T, D = b["_T"], b["_D"]
        q = quantize(sdf_of(T, D))
        blob = zlib.compress(q.tobytes() + T.tobytes(), 6)
        out += struct.pack("<IHI", k, b["size"], len(blob)) + blob
        solid = solid_of(T, D)
        idx["brushes"].append({"id": k, "size": b["size"], "biome": b["biome"], "snBiome": b["snBiome"], "class": b["class"], "prov": b["prov"],
                               "origin": b["origin"], "ground": round(ground_level(solid), 2), "features": b["features"], "score": round(b["score"], 3)})
    return bytes(out), idx

def write_index_tsv(idx, path):
    """The index for C# (RingBrushLibrary, Stage F2): landmark roots (0.1 m), then per brush id, size, biome, class, ground (0.01 m), origin (0.1 m)."""
    lines = ["de-brush-index %d" % idx.get("version", LIBRARY_VERSION), "landmarkRadiusM %d" % int(idx.get("landmarkRadiusM", LANDMARK_RADIUS_M)),
             "deepLimitY %d" % int(idx.get("deepLimitY", DEEP_LIMIT_Y))]
    for r in idx.get("landmarkRoots", []):
        lines.append("landmark\t%d\t%d\t%d\t%s" % (int(round(r[0] * 10)), int(round(r[1] * 10)), int(round(r[2] * 10)), r[3]))
    for b in idx["brushes"]:
        lines.append("brush\t%d\t%d\t%s\t%s\t%d\t%d\t%d\t%d" % (b["id"], b["size"], b["biome"], b["class"], int(round(b["ground"] * 100)),
                                                              int(round(b["origin"][0] * 10)), int(round(b["origin"][1] * 10)), int(round(b["origin"][2] * 10))))
    with open(path + ".tmp", "wb") as fh:
        fh.write(("\n".join(lines) + "\n").encode("utf-8"))
    os.replace(path + ".tmp", path)
    return path

def load_library(root=None):
    """(index, {id: (sdf float32 [z, y, x] in m, types uint8)}) or (None, None)."""
    lp, ip = library_paths(root)
    if not (os.path.exists(lp) and os.path.exists(ip)):
        return None, None
    idx = json.load(open(ip, encoding="utf-8"))
    data = open(lp, "rb").read()
    assert data[:8] == b"DEBRUSH1"
    _ver, n = struct.unpack_from("<HI", data, 8)
    off = 14
    fields = {}
    for _ in range(n):
        k, size, ln = struct.unpack_from("<IHI", data, off)
        off += 10
        raw = zlib.decompress(data[off:off + ln])
        off += ln
        m = size ** 3
        fields[k] = (dequantize(np.frombuffer(raw[:m], np.int8).reshape(size, size, size)), np.frombuffer(raw[m:2 * m], np.uint8).reshape(size, size, size))
    return idx, fields

# ------------------------------------------------------------------------------------------------- list, preview, verify
def counts(idx):
    c = {}
    for b in idx["brushes"]:
        c.setdefault(b["biome"], {}).setdefault(b["class"], 0)
        c[b["biome"]][b["class"]] += 1
    return c

def verify(idx, ring_biomes=None):
    """S8a.F.library (>= 200 brushes, every ring biome with >= 5) and S8a.F.forbidden (no brush within LandmarkRadiusM of a
 landmark root or below DeepLimitY): [(ok, line)]."""
    c = counts(idx)
    total = len(idx["brushes"])
    ring_biomes = ring_biomes or ("safeShallows", "kelpForest", "grassyPlateaus", "mushroomForest", "sparseReef", "grandReef", "dunes", "mountains",
                                  "underwaterIslands", "bloodKelp", "crashZone")
    thin = [b for b in ring_biomes if sum(c.get(b, {}).values()) < 5]
    lib_ok = total >= 200 and not thin
    lm = [tuple(r[:3]) + (r[3],) for r in idx.get("landmarkRoots", [])]
    bad = [b["id"] for b in idx["brushes"] if _near_landmark(lm, b["origin"][0], b["origin"][1], b["origin"][2], float(b["size"])) or b["origin"][1] < DEEP_LIMIT_Y]
    return [(lib_ok, "S8a.F.library %d brushes; per biome %s%s" % (total, {k: sum(v.values()) for k, v in sorted(c.items())},
                                                                   "; fewer than 5: %s" % thin if thin else "")),
            (not bad, "S8a.F.forbidden %d landmark roots (radius %.0f m), deep limit %.0f m: %s" % (idx.get("landmarks", 0), LANDMARK_RADIUS_M, DEEP_LIMIT_Y,
                                                                                                  "none inside" if not bad else "brushes %s inside" % bad[:8]))]

def preview(out_dir, root=None, per_sheet=60):
    """Contact sheets: per brush a tile (top-down shaded relief from the field | the vertical slice through the middle), labelled id,
 class and biome; sheets of per_sheet brushes ordered by biome then class."""
    from PIL import Image, ImageDraw  # noqa: E402
    idx, fields = load_library(root)
    if idx is None:
        raise SystemExit("no library: run build first")
    os.makedirs(out_dir, exist_ok=True)
    order = sorted(idx["brushes"], key=lambda b: (b["biome"], b["class"], b["id"]))
    tile, pad, cols = 128, 4, 6
    written = []
    for s0 in range(0, len(order), per_sheet):
        part = order[s0:s0 + per_sheet]
        rows = (len(part) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * (2 * tile + 3 * pad), rows * (tile + 18 + pad)), (245, 245, 245))
        dr = ImageDraw.Draw(sheet)
        for k, b in enumerate(part):
            sdf, types = fields[b["id"]]
            n = sdf.shape[0]
            solid = sdf > 0
            any_col = solid.any(axis=1)
            top = np.where(any_col, n - 1 - np.argmax(solid[:, ::-1, :], axis=1), 0).astype(np.float64)
            gz, gx = np.gradient(top)
            shade = np.clip(0.55 + 0.25 * (-gx - gz) / 2.0, 0.2, 1.0)
            hcol = 60 + 170 * top / max(1, n - 1)
            img1 = np.stack([hcol * shade, hcol * shade * 0.95, hcol * shade * 0.8], -1)[::-1, :]          # north (+z) up
            sl = solid[n // 2, ::-1, :]                                                                       # slice z = mid: rows y (top up), x
            img2 = np.where(sl[..., None], np.array([120, 105, 90]), np.array([40, 90, 150])).astype(np.float64)
            im1 = Image.fromarray(np.clip(img1, 0, 255).astype(np.uint8)).resize((tile, tile), Image.NEAREST)
            im2 = Image.fromarray(np.clip(img2, 0, 255).astype(np.uint8)).resize((tile, tile), Image.NEAREST)
            cx, cy = (k % cols) * (2 * tile + 3 * pad) + pad, (k // cols) * (tile + 18 + pad) + pad
            sheet.paste(im1, (cx, cy))
            sheet.paste(im2, (cx + tile + pad, cy))
            dr.text((cx, cy + tile + 2), "#%d %s %s %dm" % (b["id"], b["class"], b["biome"], b["size"]), fill=(0, 0, 0))
        p = os.path.join(out_dir, "brushes-%02d.png" % (s0 // per_sheet + 1))
        sheet.save(p)
        written.append(p)
    return written

# ------------------------------------------------------------------------------------------------- selftest
def selftest():
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print("%-4s %s %s" % ("OK" if cond else "FAIL", name, detail))
    n = 32
    zz, yy, xx = np.meshgrid(np.arange(n), np.arange(n), np.arange(n), indexing="ij")
    floor = yy < 8
    boulder = floor | ((xx - 16) ** 2 + (yy - 8) ** 2 + (zz - 16) ** 2 <= 49)
    wall = yy < (4 + xx * 2.5)
    pillar = floor | (((xx - 16) ** 2 + (zz - 16) ** 2 <= 9) & (yy < 30))
    over = floor | ((yy >= 18) & (yy < 26) & (xx < 24)) | (xx < 4)
    cave = (yy < 26) & ~(((xx - 16) ** 2 + (yy - 12) ** 2 <= 25) & (zz > 4))
    for name, m, want in (("boulder", boulder, "boulder"), ("wall", wall, "wall"), ("pillar", pillar, "pillar"), ("overhang", over, "overhang"), ("cave", cave, "cave")):
        f = features(m)
        check("class %s" % name, f["class"] == want, "%s %s" % (f["class"], {k: round(v, 2) for k, v in f.items() if k != "class"}))
    T = np.where(boulder, 1, 0).astype(np.uint8)
    D = np.zeros((n, n, n), np.uint8)
    s = sdf_of(T, D)
    check("sdf sign", bool(((s > 0) == boulder).all()))
    q = quantize(s)
    check("sdf codec within 1/32 m", float(np.abs(dequantize(q) - s).max()) <= SDF_UNIT / 2 + 1e-6)
    check("ground of a floor brush", abs(ground_level(floor) - 8.0) < 0.6, "%.2f" % ground_level(floor))
    try:
        bm = BiomeMap()
        check("biome map (install, read-only)", bm.w == 1024 and bm.name_at(0.0, 0.0) is not None, "%dx%d f %d origin %s" % (bm.w, bm.h, bm.f, bm.name_at(0.0, 0.0)))
    except OSError as e:
        print("SKIP biome map: %s" % e)
    lib, idx = encode_library([{"_T": T, "_D": D, "size": n, "biome": "kelpForest", "snBiome": "kelpForest", "class": "boulder", "prov": [1, 2, 3, 4],
                                "origin": [0.0, -50.0, 0.0], "features": {}, "score": 1.0}], [])
    tmp = os.path.join(os.environ.get("TEMP", "."), "s8a_brushes_selftest")
    os.makedirs(tmp, exist_ok=True)
    lp, ip = library_paths(tmp)
    open(lp, "wb").write(lib)
    json.dump(idx, open(ip, "w", encoding="utf-8"))
    i2, f2 = load_library(tmp)
    check("library round trip", i2["brushes"][0]["class"] == "boulder" and np.array_equal((f2[0][0] > 0), boulder) and np.array_equal(f2[0][1], T))
    print("S8A BRUSHES SELFTEST %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("cmd", nargs="?", choices=("build", "list", "preview", "verify"))
    ap.add_argument("--target", type=int, default=320)
    ap.add_argument("--seed", type=int, default=4546)
    ap.add_argument("--out", default=os.path.join(REPO, "tests", "out", "s8a", "stageF-preview"))
    ap.add_argument("--max-batches", type=int, default=None)
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.cmd == "build":
        build(a.target, a.seed, max_batches=a.max_batches)
        return 0
    idx, _f = load_library() if a.cmd in ("list", "verify") else (None, None)
    if a.cmd == "list":
        if idx is None:
            print("no library: run build first")
            return 1
        for biome, cl in sorted(counts(idx).items()):
            print("%-18s %3d  %s" % (biome, sum(cl.values()), ", ".join("%s %d" % kv for kv in sorted(cl.items()))))
        print("%d brushes" % len(idx["brushes"]))
        return 0
    if a.cmd == "verify":
        if idx is None:
            print("MISSING no library")
            return 1
        rows = verify(idx)
        for good, line in rows:
            print("%-4s %s" % ("OK" if good else "FAIL", line))
        return 0 if all(g for g, _l in rows) else 1
    if a.cmd == "preview":
        for p in preview(a.out):
            print("preview %s" % p)
        return 0
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main())
