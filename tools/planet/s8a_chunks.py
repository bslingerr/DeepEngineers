"""S8a planet v2, route B, first slice:
one real-map biome chunk as a macro-brush, the sample.

A chunk is a piece of the Subnautica map cut from the user's own install (read-only): the compiled octrees of a window of the map
(Subnautica's own node bytes, copied unchanged) between a floor 64 m under its lowest seabed and its highest surface, the column tops
derived from them, and its entities from the install's baked entity cells (the cell roots' children with their Transform: class id,
position, rotation, scale; the EntitySlotsPlaceholder slots with their biome, allowed types and density), every landmark left out
(the Aurora, lifepods, wrecks, Precursor and Degasi bases, their debris and loot: prefab paths matching LANDMARK_PATTERNS, and every
entity within LANDMARK_RADIUS_M of a landmark root) and the families that must stay unique (fragments, PDAs, data boxes, atmosphere
volumes). Derived data on the user's machine only, never shipped: %LOCALAPPDATA%\\DeepEngineers\\terrain\\chunks\\
chunk-<name>-v1.bin + chunk-<name>-v1.tsv (format: docs/S8A-PLANET-V2.md section 12), written by `build`.

The sample places one chunk (Grassy Plateaus, 640 m) at a fixed planet point about 20 km from the site through the F2 field: in the
point's own tangent frame (gnomonic, no stretching), a quarter turn, re-based by at most +-50 m to the planet's seabed there, blended
into SE's heightmap over BLEND_M inside its square. The placement text sample-v1.txt (anchor, frame, turns, re-base) sits beside the
chunk; its vectors are written with repr, so the C# (DeepEngineers.Terrain.S8aChunkField) reads the very same bits. The field
here is the C# field's twin: plain IEEE double arithmetic in the same order (no transcendental call per voxel), so the served bytes
agree byte for byte (vectors: tests/s8a/vectors/sample/).

Usage (repository root; numpy; Pillow for the preview; read-only on the install):
 python tools/planet/s8a_chunks.py build cut the chunk, write it and the placement into the store
 python tools/planet/s8a_chunks.py verify offline rows S8a.NS.cut,.roundtrip,.placement (the store's chunk)
 python tools/planet/s8a_chunks.py vectors tests/s8a/vectors/sample/ (synthetic chunk; the store's chunk's serve hashes)
 python tools/planet/s8a_chunks.py preview [--out D] relief, seam profile, entities by family (tests/out/s8a/stageNS-preview/)
 python tools/planet/s8a_chunks.py stats the chunk's numbers (octrees, bytes, entities by family)
 python tools/planet/s8a_chunks.py --selftest the codec, the frame, the field's seam and rotation on a synthetic chunk
"""
import argparse
import collections
import glob
import json
import math
import os
import struct
import sys
import zlib

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(REPO, "tools", "sn-octrees"))
import sn_octree_write as W  # noqa: E402 (parse_batch, decode_octree, encode_octree)

LIBRARY_VERSION = 1
SAMPLE_VERSION = 1
S = 32
NEAR_ZERO = 126
NONE_TOP = -2147483648
TOP_UNIT = 256.0                       # tops in 1/256 m (int32)
ORIGIN = (-2048, -3040, -2048)
RES = 2048
INNER = 57000.0
HR = 10200.0
RS = 60000.00390625
# the chunk: the Grassy Plateaus window with the largest GP share at 640 m (66 %; 83 % at 512 m), octree-aligned
CHUNK_NAME = "gp1"
CHUNK_BIOME = "grassyPlateaus"
CHUNK_X0, CHUNK_Z0, CHUNK_W = -960, -512, 640
FLOOR_BELOW_M = 64                     # the stored range starts two octrees under the lowest surface octree
# the placement: a design-3 shelf point at 20 km (site v1 SN), Grassy Plateaus in the planet's biome map, seabed median -121 m
SAMPLE_SN = (4158.0, -19563.0)
SAMPLE_TURNS = 1
SAMPLE_MIRROR = 0                      # mirroring has no entity equivalent (docs/S8A-PLANET-V2.md section 10): off for the sample
BLEND_M = 80.0
REBASE_LIMIT_M = 50.0
REACH_MARGIN_M = 16.0
# what never comes along (CONTRACT §3.3 "Stage F" and route B)
LANDMARK_PATTERNS = ("aurora", "wreck", "lifepod", "escapepod", "precursor", "degasi", "starship", "base/", "abandonedbase")
UNIQUE_PATTERNS = ("fragment", "databox", "/pda", "pdas/", " pda", "atmosphere/", "timecapsule", "supply", "encyclopedia")
LANDMARK_RADIUS_M = 48.0
ENTITY_ABOVE_M = 60.0
FAMILIES = ("flora", "prop", "creature", "outcrop", "light", "vfx", "other", "slot")
# Subnautica's cell levels (LargeWorldEntity.CellLevel: Near 0, Medium 1, Far 2, VeryFar 3) -> the spawn radius on the planet
LEVEL_RADIUS_M = {0: 100.0, 1: 150.0, 2: 250.0, 3: 400.0}
PLANET_DIR = os.path.join(REPO, "src", "DeepEngineers.WorldMod", "Data", "PlanetDataFiles", "DE_4546B")
VECTOR_DIR = os.path.join(REPO, "tests", "s8a", "vectors", "sample")
PREVIEW_DIR = os.path.join(REPO, "tests", "out", "s8a", "stageNS-preview")

# ------------------------------------------------------------------------------------------------- paths
def sn_dir():
    return os.environ.get("DEEPENG_SUBNAUTICA") or W.STEAM_DEFAULT

def build18():
    return os.path.join(sn_dir(), "Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "Build18")

def store_dir():
    return os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers", "terrain", "chunks")

def chunk_paths(name=CHUNK_NAME, root=None):
    d = root or store_dir()
    return (os.path.join(d, "chunk-%s-v%d.bin" % (name, LIBRARY_VERSION)), os.path.join(d, "chunk-%s-v%d.tsv" % (name, LIBRARY_VERSION)),
            os.path.join(d, "sample-v%d.txt" % SAMPLE_VERSION))

def fnv64(data, h=0xcbf29ce484222325):
    for b in data:
        h = ((h ^ b) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def fnv64_np(data):
    """fnv1a64 of bytes, vectorised in 4 KB blocks (the same value as fnv64)."""
    a = np.frombuffer(data, np.uint8)
    h = 0xcbf29ce484222325
    for b in a.tolist():
        h = ((h ^ b) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

# ------------------------------------------------------------------------------------------------- families
def family_of(path):
    """The entity's family from its prefab path (prefabs.db): what the oracle counts and the spawner reports."""
    p = (path or "").lower()
    if "/creatures/" in p or "leviathan" in p or "/fish" in p:
        return "creature"
    if "/lights/" in p:
        return "light"
    if "/vfx/" in p:
        return "vfx"
    if "resource" in p or "outcrop" in p or "drillable" in p or "limestone" in p or "sandstone" in p or "shale" in p:
        return "outcrop"
    if "/flora/" in p or "coral" in p or "kelp" in p or "plant" in p or "mushroom" in p or "seaweed" in p or "grass" in p:
        return "flora"
    if "/doodads/" in p or "rock" in p or "/environment/" in p or "/natural/" in p:
        return "prop"
    return "other"

def excluded_reason(path):
    p = (path or "").lower()
    if any(k in p for k in LANDMARK_PATTERNS):
        return "landmark"
    if any(k in p for k in UNIQUE_PATTERNS):
        return "unique"
    return None

# ------------------------------------------------------------------------------------------------- protobuf helpers
def _pb():
    import sn_prefab_colliders as P  # noqa: E402 (our census reader: prefabs.db, the protobuf object trees)
    return P

def _f32(b):
    return struct.unpack("<f", b)[0]

def _vec(P, b, n, default):
    f = {ff: vv for ff, _wt, vv in P.pb_fields(b)}
    return tuple(_f32(f[k]) if k in f else default[k - 1] for k in range(1, n + 1))

def _transform(P, data):
    """Transform data (ProtobufSerializerPrecompiled Serialize149935601: 1 localPosition, 2 localRotation, 3 localScale)."""
    f = {ff: vv for ff, _w, vv in P.pb_fields(data)}
    pos = _vec(P, f[1], 3, (0.0, 0.0, 0.0)) if 1 in f else (0.0, 0.0, 0.0)
    rot = _vec(P, f[2], 4, (0.0, 0.0, 0.0, 1.0)) if 2 in f else (0.0, 0.0, 0.0, 1.0)
    scl = _vec(P, f[3], 3, (1.0, 1.0, 1.0)) if 3 in f else (1.0, 1.0, 1.0)
    return pos, rot, scl

def _slots(P, data):
    """EntitySlotsPlaceholder {1 version, 2 slotsData[]}; EntitySlotData {1 version, 2 biomeType, 3 allowedTypes, 4 density,
 5 localPosition, 6 localRotation} (EntitySlotsPlaceholder.cs:16-21, EntitySlotData.cs:22-43)."""
    out = []
    for ff, wt, vv in P.pb_fields(data):
        if ff != 2 or wt != 2:
            continue
        g = {}
        for f2, w2, v2 in P.pb_fields(vv):
            g[f2] = v2
        pos = _vec(P, g[5], 3, (0.0, 0.0, 0.0)) if 5 in g else (0.0, 0.0, 0.0)
        rot = _vec(P, g[6], 4, (0.0, 0.0, 0.0, 1.0)) if 6 in g else (0.0, 0.0, 0.0, 1.0)
        out.append({"biome": int(g.get(2, 0)), "allowed": int(g.get(3, 0)), "density": _f32(g[4]) if 4 in g else 1.0, "pos": pos, "rot": rot})
    return out

def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)

def qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    tx, ty, tz = 2.0 * (y * vz - z * vy), 2.0 * (z * vx - x * vz), 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))

def _tree_objects(P, b, i, header):
    """[(id, parent, classId, transform, components {type: data})], next index (ProtobufSerializer object tree)."""
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
        comps = {}
        tr = ((0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))
        for _ in range(cc):
            chh, i = P.lp(b, i)
            d, i = P.lp(b, i)
            tn = P.first(P.pb_fields(chh), 1, b"").decode("utf-8", "replace")
            if tn == "UnityEngine.Transform":
                tr = _transform(P, d)
            else:
                comps[tn] = d
        cid = fd.get(7, b"")
        out.append((fd.get(6, b"").decode("utf-8", "replace"), fd.get(8, b"").decode("utf-8", "replace") if 8 in fd else None,
                    cid.decode("utf-8", "replace") if isinstance(cid, bytes) else "", tr, comps))
    return out, i

def cell_entities(P, data):
    """Per baked cells file: [(level, classId, world pos, world rot, scale, components)] of the cell roots' children (depth 1)."""
    i = 0
    hdr, i = P.lp(data, i)
    hf = P.pb_fields(hdr)
    version, ncells = P.first(hf, 1, 0), P.first(hf, 2, 0)
    out = []
    for _ in range(ncells):
        ch, i = P.lp(data, i)
        cf = P.pb_fields(ch)
        level = P.first(cf, 2, 0)
        dl, ll, wl = P.first(cf, 3, 0), P.first(cf, 4, 0), P.first(cf, 5, 0)
        objs = []
        for length, header in ((dl, True), (ll if version >= 9 else 0, True)):
            if length:
                o, _j = _tree_objects(P, data[i:i + length], 0, header)
                objs += o
                i += length
        if version >= 9 and wl:
            blob = data[i:i + wl]
            j = 0
            while j < len(blob):
                o, j = _tree_objects(P, blob, j, False)
                objs += o
            i += wl
        by_id = {o[0]: o for o in objs}
        for uid, parent, cid, (lp_, lr, ls), comps in objs:
            if not parent or parent not in by_id:
                continue
            root = by_id[parent]
            if root[1]:                       # deeper than a cell root's child: part of its entity
                continue
            (rp, rr, _rs) = root[3]
            wp = qrot(rr, lp_)
            wp = (wp[0] + rp[0], wp[1] + rp[1], wp[2] + rp[2])
            out.append((int(level), cid, wp, qmul(rr, lr), ls, comps))
    return out

# ------------------------------------------------------------------------------------------------- the cut (read-only on the install)
def _batch_reader(octree_dir):
    cache = {}

    def octree(ox, oy, oz):
        bx, by, bz = ox // 5, oy // 5, oz // 5
        key = (bx, by, bz)
        if key not in cache:
            path = os.path.join(octree_dir, "compiled-batch-%d-%d-%d.optoctrees" % key)
            if not os.path.isfile(path):
                cache[key] = None
            else:
                data = open(path, "rb").read()
                _v, entries = W.parse_batch(data, bx, by, bz)
                cache[key] = (data, entries)
        got = cache[key]
        if got is None:
            return None
        data, entries = got
        e = entries[((ox % 5) * 5 + (oy % 5)) * 5 + (oz % 5)]
        if e is None:
            return None
        off, n = e
        return bytes(data[off:off + 4 * n])
    return octree

def _uniform(nodes):
    """(type, density) of a one-node octree, else None."""
    if nodes is None or len(nodes) == 0:
        return (0, 0)
    if len(nodes) == 4 or (nodes[2] | (nodes[3] << 8)) == 0:
        return (nodes[0], nodes[1])
    return None

def _svals(T, D):
    """Subnautica's sample per voxel in [-1, 1] (positive in rock): a near voxel's (density - 126) / 126, a far voxel +-1 by its type."""
    return np.where(D != 0, (D.astype(np.float64) - NEAR_ZERO) / NEAR_ZERO, np.where(T > 0, 1.0, -1.0))

def column_tops(T, D):
    """Tops [z, x] (metres from the raster's floor) of a column stack [y, z, x] (y up): the highest rock-to-water crossing, the
 sub-voxel place from the two samples; NaN without rock."""
    sv = _svals(T, D)
    solid = sv >= 0.0
    ny = solid.shape[0]
    above_empty = np.concatenate([~solid[1:], np.ones((1,) + solid.shape[1:], bool)], axis=0)
    cross = solid & above_empty
    has = cross.any(axis=0)
    j = (ny - 1) - np.argmax(cross[::-1], axis=0)
    zz, xx = np.meshgrid(np.arange(solid.shape[1]), np.arange(solid.shape[2]), indexing="ij")
    s0 = sv[j, zz, xx]
    s1 = np.where(j + 1 < ny, sv[np.minimum(j + 1, ny - 1), zz, xx], -1.0)
    den = s0 - s1
    frac = np.where(den != 0, s0 / np.where(den != 0, den, 1.0), 0.0)
    top = j + 0.5 + frac
    return np.where(has, top, np.nan)

def cut(name=CHUNK_NAME, x0=CHUNK_X0, z0=CHUNK_Z0, w=CHUNK_W, octree_dir=None, log=print):
    """The chunk from the install (read-only): its octrees, column tops and entities. Returns the chunk dict."""
    octree_dir = octree_dir or W.default_octree_dir()
    if (x0 - ORIGIN[0]) % S or (z0 - ORIGIN[2]) % S or w % S:
        raise ValueError("the window must sit on the 32 m octree grid")
    ox0, oz0, n = (x0 - ORIGIN[0]) // S, (z0 - ORIGIN[2]) // S, w // S
    rd = _batch_reader(octree_dir)
    # 1. the surface rows: per octree column the highest octree that is not uniform water
    tops_row = np.zeros((n, n), np.int64)
    for ix in range(n):
        for iz in range(n):
            r = 99
            while r >= 0:
                u = _uniform(rd(ox0 + ix, r, oz0 + iz))
                if u is None or not (u[1] == 0 and u[0] == 0):
                    break
                r -= 1
            tops_row[ix, iz] = r
    oy_hi = int(tops_row.max())
    lowest_surface = int(tops_row.min())
    oy0 = lowest_surface - FLOOR_BELOW_M // S
    ny = oy_hi - oy0 + 1
    log("chunk %s: octrees x %d..%d z %d..%d rows %d..%d (%d x %d x %d)" % (name, ox0, ox0 + n - 1, oz0, oz0 + n - 1, oy0, oy_hi, n, ny, n))
    nodes, counts = [], []
    below_not_solid = 0
    for ix in range(n):
        for iy in range(ny):
            for iz in range(n):
                b = rd(ox0 + ix, oy0 + iy, oz0 + iz)
                b = b if b is not None else b""
                nodes.append(b)
                counts.append(len(b) // 4)
        for iz in range(n):
            u = _uniform(rd(ox0 + ix, oy0 - 1, oz0 + iz))
            if u is None or not (u[1] == 0 and u[0] > 0):
                below_not_solid += 1                # a cave under the stored floor: cut away (rock below the range)
 # 2. the column tops (1 m) from the decoded rasters, a column stack at a time
    NX = n * S
    tops = np.full((NX, NX), NONE_TOP, np.int64)            # [z, x]
    for ix in range(n):
        for iz in range(n):
            T = np.zeros((ny * S, S, S), np.uint8)
            D = np.zeros((ny * S, S, S), np.uint8)
            for iy in range(ny):
                b = nodes[(ix * ny + iy) * n + iz]
                t, d = W.decode_octree(b, 0, len(b) // 4) if b else (np.zeros((S, S, S), np.uint8), np.zeros((S, S, S), np.uint8))
                # decode_octree is [z, y, x]; the stack is [y, z, x]
                T[iy * S:(iy + 1) * S] = np.transpose(t, (1, 0, 2))
                D[iy * S:(iy + 1) * S] = np.transpose(d, (1, 0, 2))
            tp = column_tops(T, D)
            q = np.where(np.isnan(tp), NONE_TOP, np.rint(np.nan_to_num(tp) * TOP_UNIT)).astype(np.int64)
            tops[iz * S:(iz + 1) * S, ix * S:(ix + 1) * S] = q
    y_floor = ORIGIN[1] + oy0 * S
    valid = tops != NONE_TOP
    med_top = float(np.median(tops[valid])) / TOP_UNIT + y_floor if valid.any() else float("nan")
    # 3. the entities
    ents, excluded = entities_of(x0, z0, w, y_floor, ORIGIN[1] + (oy_hi + 1) * S + ENTITY_ABOVE_M, log=log)
    return {"name": name, "biome": CHUNK_BIOME, "x0": x0, "z0": z0, "w": w, "ox0": ox0, "oy0": oy0, "oz0": oz0, "nx": n, "ny": ny, "nz": n,
            "nodes": nodes, "counts": counts, "tops": tops.astype(np.int32), "yFloor": y_floor, "medianTop": med_top, "belowNotSolid": below_not_solid,
            "entities": ents, "excluded": excluded, "octreeDir": octree_dir}

def entities_of(x0, z0, w, ylo, yhi, log=print):
    """The window's entities (baked and slots) and the ones left out, from the install's baked cells (read-only)."""
    P = _pb()
    paths = P.sn_paths(sn_dir())
    db = P.read_prefabs_db(open(paths["prefabsDb"], "rb").read())
    import s8a_brushes as B  # noqa: E402 (the landmark roots: the same scan the brush library excludes)
    roots = [r for r in B.landmark_roots(log=log)]
    bx0, bx1 = (x0 - ORIGIN[0]) // 160, (x0 + w - 1 - ORIGIN[0]) // 160
    bz0, bz1 = (z0 - ORIGIN[2]) // 160, (z0 + w - 1 - ORIGIN[2]) // 160
    ents, excluded = [], []
    near_roots = [r for r in roots if x0 - LANDMARK_RADIUS_M <= r[0] < x0 + w + LANDMARK_RADIUS_M and z0 - LANDMARK_RADIUS_M <= r[2] < z0 + w + LANDMARK_RADIUS_M]
    for bx in range(bx0, bx1 + 1):
        for bz in range(bz0, bz1 + 1):
            for fn in sorted(glob.glob(os.path.join(paths["cells"], "baked-batch-cells-%d-*-%d.bin" % (bx, bz)))):
                for level, cid, wp, wr, ls, comps in cell_entities(P, open(fn, "rb").read()):
                    if not (x0 <= wp[0] < x0 + w and z0 <= wp[2] < z0 + w and ylo <= wp[1] < yhi):
                        continue
                    path = db.get(cid, "")
                    if "EntitySlotsPlaceholder" in comps:
                        for sl in _slots(P, comps["EntitySlotsPlaceholder"]):
                            p = qrot(wr, sl["pos"])
                            p = (p[0] + wp[0], p[1] + wp[1], p[2] + wp[2])
                            r = qmul(wr, sl["rot"])
                            why = None
                            for lx, ly, lz, lp_ in near_roots:
                                if (p[0] - lx) ** 2 + (p[1] - ly) ** 2 + (p[2] - lz) ** 2 <= LANDMARK_RADIUS_M ** 2:
                                    why = "near-landmark"
                                    break
                            rec = {"kind": "slot", "biome": sl["biome"], "allowed": sl["allowed"], "density": sl["density"], "pos": p, "rot": r, "level": 0, "family": "slot"}
                            if why:
                                rec["why"] = why
                                excluded.append(rec)
                            else:
                                ents.append(rec)
                        continue
                    rec = {"kind": "baked", "cid": cid, "path": path, "pos": wp, "rot": wr, "scale": ls, "level": level, "family": family_of(path)}
                    why = excluded_reason(path) if path else "no-prefab"
                    if why is None:
                        for lx, ly, lz, lp_ in near_roots:
                            if (wp[0] - lx) ** 2 + (wp[1] - ly) ** 2 + (wp[2] - lz) ** 2 <= LANDMARK_RADIUS_M ** 2:
                                why = "near-landmark"
                                break
                    if why:
                        rec["why"] = why
                        excluded.append(rec)
                    else:
                        ents.append(rec)
    ents.sort(key=lambda e: (round(e["pos"][0], 3), round(e["pos"][2], 3), round(e["pos"][1], 3), e.get("cid", ""), e["kind"]))
    log("entities: %d kept (%s), %d left out (%s)" % (len(ents), dict(collections.Counter(e["family"] for e in ents)), len(excluded),
                                                      dict(collections.Counter(e["why"] for e in excluded))))
    return ents, excluded

# ------------------------------------------------------------------------------------------------- the library files
def _r(v):
    return repr(float(np.float32(v)))

def write_chunk(ch, root=None, log=print):
    """chunk-<name>-v1.bin (octree node counts, the node bytes, the tops; zlib) and chunk-<name>-v1.tsv (header, entities)."""
    binp, tsvp, _sp = chunk_paths(ch["name"], root)
    os.makedirs(os.path.dirname(binp), exist_ok=True)
    nx, ny, nz = ch["nx"], ch["ny"], ch["nz"]
    head = b"DECHUNK1" + struct.pack("<H3i3i", LIBRARY_VERSION, nx, ny, nz, ch["ox0"], ch["oy0"], ch["oz0"])
    counts = struct.pack("<%dH" % len(ch["counts"]), *ch["counts"])
    nodes = zlib.compress(b"".join(ch["nodes"]), 6)
    tops = zlib.compress(np.ascontiguousarray(ch["tops"], "<i4").tobytes(), 6)
    blob = head + counts + struct.pack("<I", len(nodes)) + nodes + struct.pack("<I", len(tops)) + tops
    open(binp, "wb").write(blob)
    lines = ["de-chunk-index %d" % LIBRARY_VERSION, "name\t%s" % ch["name"], "biome\t%s" % ch["biome"],
             "window\t%d\t%d\t%d" % (ch["x0"], ch["z0"], ch["w"]), "octrees\t%d\t%d\t%d\t%d\t%d\t%d" % (ch["ox0"], ch["oy0"], ch["oz0"], nx, ny, nz),
             "yFloor\t%d" % ch["yFloor"], "medianTop\t%s" % repr(round(ch["medianTop"], 3)), "landmarkRadiusM\t%d" % int(LANDMARK_RADIUS_M),
             "nodesBytes\t%d" % sum(len(b) for b in ch["nodes"]), "belowNotSolid\t%d" % ch.get("belowNotSolid", 0)]
    for e in ch["entities"]:
        p, q = e["pos"], e["rot"]
        if e["kind"] == "baked":
            s = e["scale"]
            lines.append("baked\t%s\t%s\t%d\t%s" % (e["cid"], e["family"], e["level"], "\t".join(_r(v) for v in (p[0], p[1], p[2], q[0], q[1], q[2], q[3], s[0], s[1], s[2]))))
        else:
            lines.append("slot\t%d\t%d\t%s\t%s" % (e["biome"], e["allowed"], _r(e["density"]), "\t".join(_r(v) for v in (p[0], p[1], p[2], q[0], q[1], q[2], q[3]))))
    for e in ch["excluded"]:
        p = e["pos"]
        lines.append("excluded\t%s\t%s\t%s\t%s" % (e.get("cid", "slot"), e["why"], (e.get("path") or "").replace("\t", " "), "\t".join(_r(v) for v in p)))
    open(tsvp, "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
    log("chunk written: %s (%d bytes), %s (%d entities)" % (binp, len(blob), tsvp, len(ch["entities"])))
    return binp, tsvp

class Chunk:
    """A chunk read back (the C# S8aChunk's twin): node bytes per octree, the tops, the entity records."""

    def __init__(self, binp, tsvp=None):
        blob = open(binp, "rb").read()
        if blob[:8] != b"DECHUNK1":
            raise ValueError("not a chunk: %s" % binp)
        ver, nx, ny, nz, ox0, oy0, oz0 = struct.unpack_from("<H3i3i", blob, 8)
        off = 8 + 2 + 24
        n = nx * ny * nz
        counts = struct.unpack_from("<%dH" % n, blob, off)
        off += 2 * n
        ln = struct.unpack_from("<I", blob, off)[0]
        raw = zlib.decompress(blob[off + 4:off + 4 + ln])
        off += 4 + ln
        lt = struct.unpack_from("<I", blob, off)[0]
        tops = np.frombuffer(zlib.decompress(blob[off + 4:off + 4 + lt]), "<i4").reshape(nz * S, nx * S)
        self.version, self.nx, self.ny, self.nz, self.ox0, self.oy0, self.oz0 = ver, nx, ny, nz, ox0, oy0, oz0
        self.counts = np.array(counts, np.int64)
        self.offsets = np.concatenate([[0], np.cumsum(self.counts * 4)[:-1]]).astype(np.int64)
        self.raw = raw
        self.nodes = np.frombuffer(raw, np.uint8)
        self.tops = tops
        self.top_m = np.where(tops == NONE_TOP, np.nan, tops.astype(np.float64) / TOP_UNIT)
        self.y_floor = float(ORIGIN[1] + oy0 * S)
        self.x0 = float(ORIGIN[0] + ox0 * S)
        self.z0 = float(ORIGIN[2] + oz0 * S)
        self.size_x, self.size_y, self.size_z = nx * S, ny * S, nz * S
        self.bin_fnv = fnv64_np(blob)
        self.entities, self.excluded, self.head = [], [], {}
        self.tsv_fnv = None
        if tsvp:
            data = open(tsvp, "rb").read()
            self.tsv_fnv = fnv64_np(data)
            for ln_ in data.decode("utf-8").splitlines():
                f = ln_.split("\t")
                if f[0] == "baked":
                    v = [float(x) for x in f[4:14]]
                    self.entities.append({"kind": "baked", "cid": f[1], "family": f[2], "level": int(f[3]), "pos": tuple(v[0:3]), "rot": tuple(v[3:7]), "scale": tuple(v[7:10])})
                elif f[0] == "slot":
                    v = [float(x) for x in f[4:11]]
                    self.entities.append({"kind": "slot", "biome": int(f[1]), "allowed": int(f[2]), "density": float(f[3]), "family": "slot", "level": 0,
                                          "pos": tuple(v[0:3]), "rot": tuple(v[3:7])})
                elif f[0] == "excluded":
                    self.excluded.append({"cid": f[1], "why": f[2], "path": f[3], "pos": tuple(float(x) for x in f[4:7])})
                elif len(f) >= 2:
                    self.head[f[0]] = f[1:]

    def octree_nodes(self, k):
        o, c = int(self.offsets[k]), int(self.counts[k])
        return self.raw[o:o + 4 * c]

    def lookup(self, X, Y, Z):
        """(type, density) at integer voxel coordinates (arrays, chunk voxels from its corner; y from its floor): one descent of the
 octree per voxel (decode_octree's leaf rule); below the stored range rock (type 1, far), above it and outside water."""
        X = np.asarray(X, np.int64)
        Y = np.asarray(Y, np.int64)
        Z = np.asarray(Z, np.int64)
        T = np.zeros(X.shape, np.int64)
        D = np.zeros(X.shape, np.int64)
        inside = (X >= 0) & (X < self.size_x) & (Z >= 0) & (Z < self.size_z) & (Y >= 0) & (Y < self.size_y)
        T[(Y < 0)] = 1
        ix, iy, iz = X // S, Y // S, Z // S
        k = np.where(inside, (ix * self.ny + iy) * self.nz + iz, 0)
        cnt = self.counts[k]
        base = self.offsets[k]
        lx, ly, lz = X - ix * S, Y - iy * S, Z - iz * S
        node = np.zeros(X.shape, np.int64)
        x0 = np.zeros(X.shape, np.int64)
        y0 = np.zeros(X.shape, np.int64)
        z0 = np.zeros(X.shape, np.int64)
        size = np.full(X.shape, S, np.int64)
        live = inside & (cnt > 0)
        nb = self.nodes
        while live.any():
            q = base + 4 * node
            qq = np.where(live, q, 0)
            t = nb[qq].astype(np.int64)
            d = nb[qq + 1].astype(np.int64)
            fc = nb[qq + 2].astype(np.int64) | (nb[qq + 3].astype(np.int64) << 8)
            leaf = live & ((fc == 0) | (size == 1) | (fc + 8 > cnt))
            T = np.where(leaf, t, T)
            D = np.where(leaf, d, D)
            live = live & ~leaf
            h = size >> 1
            xb = (lx - x0) >= h
            yb = (ly - y0) >= h
            zb = (lz - z0) >= h
            c = 4 * xb.astype(np.int64) + 2 * yb.astype(np.int64) + zb.astype(np.int64)
            node = np.where(live, fc + c, node)
            x0 = np.where(live & xb, x0 + h, x0)
            y0 = np.where(live & yb, y0 + h, y0)
            z0 = np.where(live & zb, z0 + h, z0)
            size = np.where(live, h, size)
        return T, D

    def svals(self, X, Y, Z):
        """Subnautica's sample at integer voxels (the C# S8aChunk.S): near (density - 126) / 126, far +-1; below the range +1."""
        T, D = self.lookup(X, Y, Z)
        Y = np.asarray(Y)
        s = np.where(D != 0, (D.astype(np.float64) - 126.0) / 126.0, np.where(T > 0, 1.0, -1.0))
        return np.where(np.asarray(Y) < 0, 1.0, s)

    def tri(self, X, Y, Z):
        """Trilinear of the samples over the voxel centres (S8aChunk.Trilinear: x, then y, then z), horizontally clamped to the chunk."""
        gx, gy, gz = X - 0.5, Y - 0.5, Z - 0.5
        i0, j0, k0 = np.floor(gx), np.floor(gy), np.floor(gz)
        fx, fy, fz = gx - i0, gy - j0, gz - k0
        i0, j0, k0 = i0.astype(np.int64), j0.astype(np.int64), k0.astype(np.int64)
        ia = np.clip(i0, 0, self.size_x - 1)
        ib = np.clip(i0 + 1, 0, self.size_x - 1)
        ka = np.clip(k0, 0, self.size_z - 1)
        kb = np.clip(k0 + 1, 0, self.size_z - 1)
        ja, jb = j0, j0 + 1
        v000 = self.svals(ia, ja, ka)
        v100 = self.svals(ib, ja, ka)
        v010 = self.svals(ia, jb, ka)
        v110 = self.svals(ib, jb, ka)
        v001 = self.svals(ia, ja, kb)
        v101 = self.svals(ib, ja, kb)
        v011 = self.svals(ia, jb, kb)
        v111 = self.svals(ib, jb, kb)
        c00 = v000 + fx * (v100 - v000)
        c10 = v010 + fx * (v110 - v010)
        c01 = v001 + fx * (v101 - v001)
        c11 = v011 + fx * (v111 - v011)
        c0 = c00 + fy * (c10 - c00)
        c1 = c01 + fy * (c11 - c01)
        return c0 + fz * (c1 - c0)

    def top_at(self, X, Z):
        """The column top (metres from the floor) bilinear over the column centres, NaN where a corner has none (S8aChunk.TopAt)."""
        gx, gz = X - 0.5, Z - 0.5
        i0 = np.clip(np.floor(gx), 0, self.size_x - 2)
        k0 = np.clip(np.floor(gz), 0, self.size_z - 2)
        fx = np.minimum(np.maximum(gx - i0, 0.0), 1.0)
        fz = np.minimum(np.maximum(gz - k0, 0.0), 1.0)
        i0, k0 = i0.astype(np.int64), k0.astype(np.int64)
        t = self.top_m
        t00, t10, t01, t11 = t[k0, i0], t[k0, i0 + 1], t[k0 + 1, i0], t[k0 + 1, i0 + 1]
        a = t00 + fx * (t10 - t00)
        b = t01 + fx * (t11 - t01)
        return a + fz * (b - a)

    def field(self, X, Y, Z):
        """The chunk's own signed distance (m, positive in rock) at chunk-local points: the near band from the samples, beyond it
 the vertical distance to the column top (rock under it, water over it), +-1 under an overhang (S8aChunk.Field)."""
        X, Y, Z = (np.asarray(v, np.float64) for v in (X, Y, Z))
        s = self.tri(X, Y, Z)
        top = self.top_at(X, Z)
        rock = np.where(np.isnan(top) | (Y >= top), 1.0, np.maximum(1.0, np.nan_to_num(top) - Y))
        water = np.where(~np.isnan(top) & (Y > top), -np.maximum(1.0, Y - np.nan_to_num(top)), -1.0)
        return np.where((s > -1.0) & (s < 1.0), s, np.where(s >= 1.0, rock, water))

# ------------------------------------------------------------------------------------------------- the placement
def frame_at_sn(x, z):
    """The anchor (planet-local unit vector, the vanilla generator frame: E0 -X, N0 -Y, U0 +Z) of a site-v1 SN column and its
 tangent frame (E, N): site v1's frame carried along the great circle from the site (so the chunk's axes are SN's there)."""
    rho = math.hypot(x, z)
    th = rho / RS
    de, dn = (x / rho, z / rho) if rho > 0 else (1.0, 0.0)
    E0, N0, U0 = (-1.0, 0.0, 0.0), (0.0, -1.0, 0.0), (0.0, 0.0, 1.0)
    t = tuple(de * E0[i] + dn * N0[i] for i in range(3))
    c, s = math.cos(th), math.sin(th)
    A = tuple(c * U0[i] + s * t[i] for i in range(3))
    tp = tuple(c * t[i] - s * U0[i] for i in range(3))
    sv = tuple(-dn * E0[i] + de * N0[i] for i in range(3))
    E = tuple(de * tp[i] - dn * sv[i] for i in range(3))
    N = tuple(dn * tp[i] + de * sv[i] for i in range(3))
    return A, E, N

TURN_CS = ((1, 0), (0, 1), (-1, 0), (0, -1))

def turn(k, x, z):
    """Native offsets -> tangent (u, v): M_k(x, z) = (c x + s z, -s x + c z); Unity's yaw of 90 k degrees maps the same way."""
    c, s = TURN_CS[k % 4]
    return c * x + s * z, -s * x + c * z

def unturn(k, u, v):
    c, s = TURN_CS[k % 4]
    return c * u - s * v, s * u + c * v

def base_planet():
    import s8a_ring3d as R  # noqa: E402
    h, g = R.read_planet(PLANET_DIR)
    return R.Base(h, g)

def column_px(base, px, py):
    """The seabed (SN y) at face-1 pixel coordinates (RingBase.ColumnPx: s8a_ring3d.Base.column after texel)."""
    import s8a_ring3d as R  # noqa: E402
    px = np.asarray(px, np.float64)
    py = np.asarray(py, np.float64)
    i0 = np.floor(px).astype(np.int64)
    j0 = np.floor(py).astype(np.int64)
    ok = (i0 >= 1) & (j0 >= 1) & (i0 <= RES - 3) & (j0 <= RES - 3)
    i0c = np.clip(i0, 1, RES - 3)
    j0c = np.clip(j0, 1, RES - 3)
    wx = R.cr_weights(px - i0)
    wy = R.cr_weights(py - j0)
    hv = base.hv
    rows = []
    for dj in range(4):
        r = wx[0] * hv[j0c - 1 + dj, i0c - 1] + wx[1] * hv[j0c - 1 + dj, i0c] + wx[2] * hv[j0c - 1 + dj, i0c + 1] + wx[3] * hv[j0c - 1 + dj, i0c + 2]
        rows.append(r)
    hh = wy[0] * rows[0] + wy[1] * rows[1] + wy[2] * rows[2] + wy[3] * rows[3]
    return np.where(ok, INNER + hh * HR - RS, np.nan)

def px_of_local(px, py, pz):
    """Face-1 pixel coordinates of planet-local points (MyCubemapHelpers face 1: tx = X / Z, ty = -Y / Z)."""
    tx = px / pz
    ty = -py / pz
    return (tx + 1.0) * 0.5 * (RES - 1.0), (ty + 1.0) * 0.5 * (RES - 1.0)

def make_placement(chunk, base, sn=SAMPLE_SN, turns=SAMPLE_TURNS):
    """The sample's placement: anchor and frame, turns, the re-base (planet seabed median over the footprint - the chunk's median
 top, at most +-REBASE_LIMIT_M, whole metres), the blend band."""
    A, E, N = frame_at_sn(*sn)
    half = chunk.size_x / 2.0
    us = np.arange(-half + 8.0, half, 16.0)
    U, V = np.meshgrid(us, us, indexing="ij")
    d = [A[i] + (U.ravel() / RS) * E[i] + (V.ravel() / RS) * N[i] for i in range(3)]
    nrm = np.sqrt(d[0] ** 2 + d[1] ** 2 + d[2] ** 2)
    px, py = px_of_local(d[0] / nrm * RS, d[1] / nrm * RS, d[2] / nrm * RS)
    H = column_px(base, px, py)
    med_base = float(np.nanmedian(H))
    med_top = float(np.nanmedian(chunk.top_m)) + chunk.y_floor
    rebase = float(max(-REBASE_LIMIT_M, min(REBASE_LIMIT_M, round(med_base - med_top))))
    return {"chunk": chunk.head.get("name", [CHUNK_NAME])[0], "sn": sn, "A": A, "E": E, "N": N, "turns": turns, "mirror": SAMPLE_MIRROR,
            "rebase": rebase, "blend": min(BLEND_M, half / 4.0), "half": half, "chunkFnv": chunk.bin_fnv, "indexFnv": chunk.tsv_fnv or 0,
            "medianBase": med_base, "medianTop": med_top}

def placement_text(pl):
    lines = ["de-sample %d" % SAMPLE_VERSION, "chunk %s" % pl["chunk"], "chunkFnv %016x" % pl["chunkFnv"], "indexFnv %016x" % pl["indexFnv"],
             "sn %s %s" % (repr(float(pl["sn"][0])), repr(float(pl["sn"][1]))),
             "anchor %s %s %s" % tuple(repr(float(v)) for v in pl["A"]), "east %s %s %s" % tuple(repr(float(v)) for v in pl["E"]),
             "north %s %s %s" % tuple(repr(float(v)) for v in pl["N"]), "turns %d" % pl["turns"], "mirror %d" % pl["mirror"],
             "rebaseM %s" % repr(float(pl["rebase"])), "blendM %s" % repr(float(pl["blend"])), "halfM %s" % repr(float(pl["half"]))]
    return "\n".join(lines) + "\n"

def read_placement(path):
    pl = {}
    for ln in open(path, encoding="utf-8").read().splitlines():
        f = ln.split(" ")
        if f[0] in ("anchor", "east", "north"):
            pl[{"anchor": "A", "east": "E", "north": "N"}[f[0]]] = tuple(float(v) for v in f[1:4])
        elif f[0] == "sn":
            pl["sn"] = (float(f[1]), float(f[2]))
        elif f[0] in ("turns", "mirror"):
            pl[f[0]] = int(f[1])
        elif f[0] in ("rebaseM", "blendM", "halfM"):
            pl[{"rebaseM": "rebase", "blendM": "blend", "halfM": "half"}[f[0]]] = float(f[1])
        elif f[0] in ("chunkFnv", "indexFnv"):
            pl[f[0]] = int(f[1], 16)
        elif f[0] == "chunk":
            pl["chunk"] = f[1]
    return pl

# ------------------------------------------------------------------------------------------------- the sample's field (S8aChunkField's twin)
def smooth(e0, e1, x):
    t = np.minimum(np.maximum((x - e0) / (e1 - e0), 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t)

def content(f, lod_m=1.0):
    return ((np.minimum(np.maximum(f / lod_m, -1.0), 1.0) * 0.5 + 0.5) * 255.0).astype(np.int64).astype(np.uint8)

class Sample:
    """The placed chunk: the field at planet-local points (S8aChunkField.Sample), the served bytes (ServeBox)."""

    def __init__(self, chunk, placement, base):
        self.c = chunk
        self.p = placement
        self.base = base
        self.A, self.E, self.N = placement["A"], placement["E"], placement["N"]

    def tangent(self, px, py, pz):
        """(u, v, y, d) of planet-local points: gnomonic tangent coordinates at the anchor on the sea sphere, altitude, P.A."""
        A, E, N = self.A, self.E, self.N
        d = px * A[0] + py * A[1] + pz * A[2]
        pe = px * E[0] + py * E[1] + pz * E[2]
        pn = px * N[0] + py * N[1] + pz * N[2]
        u = RS * pe / d
        v = RS * pn / d
        y = np.sqrt(px * px + py * py + pz * pz) - RS
        return u, v, y, d

    def field_points(self, px, py, pz, weight_out=None):
        px, py, pz = (np.asarray(a, np.float64) for a in (px, py, pz))
        u, v, y, d = self.tangent(px, py, pz)
        k = self.p["turns"]
        c, s = TURN_CS[k % 4]
        dx = c * u - s * v
        dz = s * u + c * v
        half = self.p["half"]
        e = np.minimum(half - np.abs(dx), half - np.abs(dz))
        qx, qy = px_of_local(px, py, pz)
        H = column_px(self.base, qx, qy)
        fb = H - y
        f = fb.copy()
        ins = (e > 0.0) & (d > 0.0)
        if ins.any():
            X = half + dx[ins]
            Z = half + dz[ins]
            Y = (y[ins] - self.p["rebase"]) - self.c.y_floor
            fc = self.c.field(X, Y, Z)
            w = smooth(0.0, self.p["blend"], e[ins])
            f[ins] = w * fc + (1.0 - w) * fb[ins]
            if weight_out is not None:
                weight_out[ins] = w
        return f, H

    def serve_box(self, ax, ay, az, sx, sy, sz, lod):
        """The content bytes S8aRing serves for a planet-local box in the sample's zone, x fastest then y then z."""
        ks, js, iz = np.meshgrid(np.arange(sz), np.arange(sy), np.arange(sx), indexing="ij")
        px = ax + iz.ravel() * float(lod)
        py = ay + js.ravel() * float(lod)
        pz = az + ks.ravel() * float(lod)
        f, H = self.field_points(px, py, pz)
        cc = content(f, float(lod))
        cc[np.isnan(H)] = 0
        return cc

    def local_of_tangent(self, u, v, y):
        """Planet-local point of tangent coordinates and an altitude (the inverse gnomonic: direction along A + u/Rs E + v/Rs N)."""
        A, E, N = self.A, self.E, self.N
        dd = [A[i] + (u / RS) * E[i] + (v / RS) * N[i] for i in range(3)]
        n = math.sqrt(dd[0] ** 2 + dd[1] ** 2 + dd[2] ** 2)
        r = RS + y
        return tuple(dd[i] / n * r for i in range(3))

    def native_to_local(self, x, y, z):
        """A native SN point of the chunk -> planet-local (the entities' map: turned, re-based)."""
        cx, cz = self.c.x0 + self.p["half"], self.c.z0 + self.p["half"]
        u, v = turn(self.p["turns"], x - cx, z - cz)
        return self.local_of_tangent(u, v, y + self.p["rebase"])

    def zone_bounds(self):
        """The planet-local bounds of the sample's zone (footprint corners at the lowest and highest altitudes, padded)."""
        half = self.p["half"]
        ylo = min(self.c.y_floor + self.p["rebase"], float(np.nanmin(column_px(self.base, *self._foot_px())))) - REACH_MARGIN_M
        yhi = max(self.c.y_floor + self.c.size_y + self.p["rebase"], float(np.nanmax(column_px(self.base, *self._foot_px())))) + REACH_MARGIN_M
        pts = [self.local_of_tangent(su * half, sv * half, yy) for su in (-1, 1) for sv in (-1, 1) for yy in (ylo, yhi)]
        return tuple(min(p[i] for p in pts) for i in range(3)), tuple(max(p[i] for p in pts) for i in range(3)), ylo, yhi

    def _foot_px(self):
        half = self.p["half"]
        g = np.linspace(-half, half, 9)
        U, V = np.meshgrid(g, g, indexing="ij")
        pts = np.array([self.local_of_tangent(u, v, 0.0) for u, v in zip(U.ravel(), V.ravel())])
        return px_of_local(pts[:, 0], pts[:, 1], pts[:, 2])

    def top_native(self, xs, zs):
        """The chunk's own top (SN y after the re-base) at native columns (the third witness of NS.terrain): no blend."""
        X = np.asarray(xs, np.float64) - self.c.x0
        Z = np.asarray(zs, np.float64) - self.c.z0
        return self.c.top_at(X, Z) + self.c.y_floor + self.p["rebase"]

def site_sn_of_tangent(pl, u, v):
    """Site-v1 SN (x, z) of a tangent point of a placement at sea level (the launcher's teleports on chart 0; atan2: not served)."""
    A, E, N = pl["A"], pl["E"], pl["N"]
    d = [A[i] + (u / RS) * E[i] + (v / RS) * N[i] for i in range(3)]
    le, lu, ln = -d[0], d[2], -d[1]
    h = math.hypot(le, ln)
    rho = RS * math.atan2(h, lu)
    return (rho * le / h, rho * ln / h) if h > 0 else (0.0, 0.0)

def sample_points(n=2, spacing=180.0, log=None):
    """The sample's look and entity spots: tangent points in the core (past the blend band) whose native column and 60 m round it
 are Grassy Plateaus in the install's biome map (read-only), nearest the centre first, at least `spacing` apart; with the centre.
 90 m from every entity left out for a landmark (the wrecks' holes). [{"u", "v", "x", "z" (site-v1 SN), "seabed" (the chunk's own top,
 re-based), "nativeX", "nativeZ", "nativeSeabed" (the real map's own seabed there, from the install's octrees), "biome"}]."""
    cb, ct, cs = chunk_paths()
    pl = read_placement(cs)
    ch = Chunk(cb, ct)
    half, blend = pl["half"], pl["blend"]
    holes = np.array([e["pos"] for e in ch.excluded if e["why"] in ("landmark", "near-landmark")] or [(1e9, 0.0, 1e9)])
    try:
        import s8a_brushes as B  # noqa: E402
        bm = B.BiomeMap(build18())
    except Exception:
        bm = None
    cx, cz = ch.x0 + half, ch.z0 + half

    def spot(u, v, biome):
        dx, dz = unturn(pl["turns"], u, v)
        X, Z = half + dx, half + dz
        native = float(ch.top_at(np.array([X]), np.array([Z]))[0]) + ch.y_floor
        x, z = site_sn_of_tangent(pl, u, v)
        return {"u": u, "v": v, "x": x, "z": z, "seabed": native + pl["rebase"], "nativeX": cx + dx, "nativeZ": cz + dz, "nativeSeabed": native, "biome": biome}
    out = [spot(0.0, 0.0, bm.name_at(cx, cz) if bm else None)]
    lim = half - blend - 40.0
    cands = []
    for u in np.arange(-lim, lim + 1, 20.0):
        for v in np.arange(-lim, lim + 1, 20.0):
            dx, dz = unturn(pl["turns"], u, v)
            nx, nz = cx + dx, cz + dz
            if float(np.min(np.hypot(holes[:, 0] - nx, holes[:, 2] - nz))) < 90.0:
                continue                                  # the landmarks' holes (their entities left out) are not the look
            if bm is not None:
                names = [bm.name_at(nx + 60.0 * math.cos(a), nz + 60.0 * math.sin(a)) for a in np.arange(0, 2 * math.pi, math.pi / 4)] + [bm.name_at(nx, nz)]
                if any(nm != CHUNK_BIOME for nm in names):
                    continue
            cands.append((math.hypot(u, v), float(u), float(v)))
    cands.sort()
    for _d, u, v in cands:
        if all(math.hypot(u - p["u"], v - p["v"]) >= spacing for p in out):
            out.append(spot(u, v, CHUNK_BIOME))
        if len(out) >= n + 1:
            break
    return out

# ------------------------------------------------------------------------------------------------- serve spots and vectors
def serve_spots(sm, count=4):
    """Planet-local boxes over the sample: two at LOD 0 (inside the chunk, on the seam), one at LOD 2, one at LOD 3, multiples of 8 m
 on the LOD grid (the vanilla frame's storage grid is even), each holding rock and water."""
    half = sm.p["half"]
    spots = []
    for name, u, v, lod, n in (("inner-lod0", -0.25 * half, 0.2 * half, 1, 24), ("seam-lod0", half - 0.5 * sm.p["blend"], 0.1 * half, 1, 24),
                               ("mid-lod2", 0.3 * half, -0.3 * half, 4, 16), ("wide-lod3", 0.0, 0.0, 8, 16)):
        X = half + unturn(sm.p["turns"], u, v)[0]
        Z = half + unturn(sm.p["turns"], u, v)[1]
        t = float(sm.c.top_at(np.array([X]), np.array([Z]))[0])
        y = (t + sm.c.y_floor + sm.p["rebase"]) if not math.isnan(t) else -120.0
        P = sm.local_of_tangent(u, v, y)
        best = None
        for dy in (0.0, -8.0, 8.0, -16.0, 16.0, -32.0, 32.0):
            mn = [8.0 * lod * math.floor((P[i] - n * lod / 2.0 + (dy if i == 2 else 0.0)) / (8.0 * lod)) for i in range(3)]
            box = (name, mn[0], mn[1], mn[2], n, n, n, lod)
            cc = sm.serve_box(*box[1:])
            share = float((cc > 127).mean())
            if best is None:
                best = box
            if 0.15 < share < 0.85:
                best = box
                break
        spots.append(best)
        if len(spots) >= count:
            break
    return spots

def serve_lines(sm, spots):
    lines = []
    for name, ax, ay, az, sx, sy, sz, lod in spots:
        cc = sm.serve_box(ax, ay, az, sx, sy, sz, lod)
        lines.append("serve\t%s\t%d\t%d\t%d\t%d\t%d\t%d\t%d\t%016x\t%d\t%d" % (name, ax, ay, az, sx, sy, sz, lod, fnv64_np(cc.tobytes()),
                                                                          int((cc == 255).sum()), int((cc == 0).sum())))
    return lines

def point_lines(sm, n=24):
    """Pointwise field values (repr) at points over the footprint and the seam: the C# test compares them bit for bit."""
    half = sm.p["half"]
    out = []
    for k in range(n):
        u = -half * 1.05 + (2.1 * half) * ((k * 7919) % n) / float(n - 1)
        v = -half * 1.05 + (2.1 * half) * ((k * 104729 + 3) % n) / float(n - 1)
        X = half + unturn(sm.p["turns"], u, v)[0]
        Z = half + unturn(sm.p["turns"], u, v)[1]
        t = float(sm.c.top_at(np.array([min(max(X, 0.5), sm.c.size_x - 0.5)]), np.array([min(max(Z, 0.5), sm.c.size_z - 0.5)]))[0])
        y = (t + sm.c.y_floor + sm.p["rebase"] if not math.isnan(t) else -120.0) + ((k % 5) - 2) * 0.37
        P = sm.local_of_tangent(u, v, y)
        f, H = sm.field_points(np.array([P[0]]), np.array([P[1]]), np.array([P[2]]))
        out.append("point\t%s\t%s\t%s\t%s\t%s" % (repr(P[0]), repr(P[1]), repr(P[2]), repr(float(f[0])), repr(float(H[0]))))
    return out

def synthetic_chunk(root, name="synth"):
    """A 128 x 96 x 128 m chunk of synthetic octrees (a plateau with a cliff, a pillar, an overhang and a tunnel; written by our own
 octree writer) with a few entity records: the committed vectors' chunk (no game data)."""
    nx, ny, nz = 4, 3, 4
    nodes, counts = [], []
    NX, NY = nx * S, ny * S
    zz, yy, xx = np.meshgrid(np.arange(NX) + 0.5, np.arange(NY) + 0.5, np.arange(NX) + 0.5, indexing="ij")
    ground = 40.0 + 14.0 * (xx > 70) + 3.0 * np.sin(zz / 9.0)
    sdf = ground - yy
    sdf = np.maximum(sdf, 6.0 - np.sqrt((xx - 30.0) ** 2 + (zz - 90.0) ** 2) + np.where(yy < 75, 0.0, -100.0))      # a pillar to y 75
    sdf = np.maximum(sdf, np.minimum(np.minimum(4.0 - np.abs(yy - 64.0), 96.0 - xx), xx - 72.0))                      # an overhang slab
    sdf = np.minimum(sdf, np.sqrt((yy - 30.0) ** 2 + (xx - 64.0) ** 2) - 5.0)                                         # a tunnel along z
    T = np.where(sdf >= 0, 1, 0).astype(np.uint8)
    near = np.abs(sdf) < 1.0
    D = np.where(near, np.clip(np.floor(np.clip(sdf, -1, 0.999) * 126.0) + 126.0, 1, 252), 0).astype(np.uint8)
    T = np.where(near & (sdf < 0), 0, T).astype(np.uint8)
    for ix in range(nx):
        for iy in range(ny):
            for iz in range(nz):
                t = np.ascontiguousarray(T[iz * S:(iz + 1) * S, iy * S:(iy + 1) * S, ix * S:(ix + 1) * S])
                d = np.ascontiguousarray(D[iz * S:(iz + 1) * S, iy * S:(iy + 1) * S, ix * S:(ix + 1) * S])
                b = bytes(W.encode_octree(t, d))
                nodes.append(b)
                counts.append(len(b) // 4)
    tops = np.full((NX, NX), NONE_TOP, np.int64)
    for ix in range(nx):
        for iz in range(nz):
            t = np.transpose(T[iz * S:(iz + 1) * S, :, ix * S:(ix + 1) * S], (1, 0, 2))
            d = np.transpose(D[iz * S:(iz + 1) * S, :, ix * S:(ix + 1) * S], (1, 0, 2))
            tp = column_tops(t, d)
            tops[iz * S:(iz + 1) * S, ix * S:(ix + 1) * S] = np.where(np.isnan(tp), NONE_TOP, np.rint(np.nan_to_num(tp) * TOP_UNIT))
    ox0, oy0, oz0 = 10, 90, 20                 # the floor at SN y -160: the plateau near the sample's planet seabed
    x0, z0 = ORIGIN[0] + ox0 * S, ORIGIN[2] + oz0 * S
    yf = ORIGIN[1] + oy0 * S
    ents = [{"kind": "baked", "cid": "synthetic-%d" % k, "family": FAMILIES[k % 5], "level": k % 4, "pos": (x0 + 10.0 + 9.0 * k, yf + 41.0, z0 + 7.0 * k),
             "rot": (0.0, math.sin(0.1 * k), 0.0, math.cos(0.1 * k)), "scale": (1.0, 1.0, 1.0)} for k in range(12)]
    ents.append({"kind": "slot", "biome": 1, "allowed": 3, "density": 1.0, "pos": (x0 + 64.0, yf + 41.0, z0 + 64.0), "rot": (0.0, 0.0, 0.0, 1.0), "family": "slot", "level": 0})
    ch = {"name": name, "biome": "synthetic", "x0": x0, "z0": z0, "w": NX, "ox0": ox0, "oy0": oy0, "oz0": oz0, "nx": nx, "ny": ny, "nz": nz,
          "nodes": nodes, "counts": counts, "tops": tops.astype(np.int32), "yFloor": yf, "medianTop": float(np.median(tops)) / TOP_UNIT + yf,
          "entities": ents, "excluded": []}
    return write_chunk(ch, root, log=lambda *_a: None)

def write_vectors(out_dir=VECTOR_DIR, log=print):
    """The committed vectors (a synthetic chunk at the sample's anchor) and, when the store holds the chunk, its serve hashes."""
    os.makedirs(out_dir, exist_ok=True)
    binp, tsvp = synthetic_chunk(out_dir)
    base = base_planet()
    ch = Chunk(binp, tsvp)
    pl = make_placement(ch, base, sn=SAMPLE_SN, turns=SAMPLE_TURNS)
    txt = placement_text(pl)
    open(os.path.join(out_dir, "synthetic-sample.txt"), "w", encoding="utf-8", newline="\n").write(txt)
    sm = Sample(ch, read_placement(os.path.join(out_dir, "synthetic-sample.txt")), base)
    head = ["de-sample-vectors %d" % SAMPLE_VERSION, "chunk\t%016x\t%016x" % (ch.bin_fnv, ch.tsv_fnv), "sample\t%016x" % fnv64(txt.encode("utf-8"))]
    lines = head + point_lines(sm) + serve_lines(sm, serve_spots(sm))
    p = os.path.join(out_dir, "sample-vectors.tsv")
    open(p, "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
    log("vectors %s: %d points, %d boxes" % (p, sum(1 for ln in lines if ln.startswith("point")), sum(1 for ln in lines if ln.startswith("serve"))))
    written = [p]
    cb, ct, cs = chunk_paths()
    if os.path.isfile(cb) and os.path.isfile(ct) and os.path.isfile(cs):
        rc = Chunk(cb, ct)
        stxt = open(cs, "rb").read()
        rs = Sample(rc, read_placement(cs), base)
        l2 = ["de-sample-vectors %d" % SAMPLE_VERSION, "chunk\t%016x\t%016x" % (rc.bin_fnv, rc.tsv_fnv), "sample\t%016x" % fnv64(stxt)]
        l2 += point_lines(rs) + serve_lines(rs, serve_spots(rs))
        p2 = os.path.join(out_dir, "sample-install-vectors.tsv")
        open(p2, "w", encoding="utf-8", newline="\n").write("\n".join(l2) + "\n")
        log("vectors %s: the store's chunk" % p2)
        written.append(p2)
    return written

# ------------------------------------------------------------------------------------------------- offline rows
def verify(log=print, roundtrip_n=None):
    """S8a.NS.cut (no landmark or unique prefab among the kept entities, none within the landmark radius of a root), S8a.NS.roundtrip
 (every octree's bytes in the chunk = the install's, decode -> our writer -> the same bytes, the file read back = written),
 S8a.NS.placement (re-base within +-50 m, the anchor frame orthonormal, the seabed under the sample lit). [(ok, line)]"""
    rows = []
    cb, ct, cs = chunk_paths()
    if not (os.path.isfile(cb) and os.path.isfile(ct) and os.path.isfile(cs)):
        return [(False, "S8a.NS.cut MISSING no chunk in %s (python tools/planet/s8a_chunks.py build)" % store_dir())]
    ch = Chunk(cb, ct)
    P = _pb()
    db = P.read_prefabs_db(open(P.sn_paths(sn_dir())["prefabsDb"], "rb").read())
    import s8a_brushes as B  # noqa: E402
    roots = B.landmark_roots(log=lambda *_a: None)
    bad = []
    for e in ch.entities:
        if e["kind"] != "baked":
            continue
        path = db.get(e["cid"], "")
        if excluded_reason(path):
            bad.append((e["cid"], path))
    near = 0
    x0, z0, w = ch.x0, ch.z0, ch.size_x
    rr = [r for r in roots if x0 - 60 <= r[0] < x0 + w + 60 and z0 - 60 <= r[2] < z0 + w + 60]
    for e in ch.entities:
        p = e["pos"]
        for lx, ly, lz, _lp in rr:
            if (p[0] - lx) ** 2 + (p[1] - ly) ** 2 + (p[2] - lz) ** 2 <= LANDMARK_RADIUS_M ** 2:
                near += 1
                break
    why = collections.Counter(x["why"] for x in ch.excluded)
    rows.append((not bad and near == 0, "S8a.NS.cut %d entities kept (%s), %d left out %s; landmark roots in the window %d; kept with a landmark/unique "
                 "prefab %d%s; kept within %.0f m of a root %d" % (len(ch.entities), dict(collections.Counter(e["family"] for e in ch.entities)),
                                                                   len(ch.excluded), dict(why), len(rr), len(bad), (" e.g. %s" % (bad[0],)) if bad else "",
                                                                   LANDMARK_RADIUS_M, near)))
    # the round trip: the chunk's bytes against the install, then decode -> encode
    rd = _batch_reader(W.default_octree_dir())
    n = ch.nx * ch.ny * ch.nz
    idx = list(range(n)) if roundtrip_n is None else sorted(set(int(k * (n - 1) / max(1, roundtrip_n - 1)) for k in range(roundtrip_n)))
    differ_install = differ_writer = checked = 0
    first = None
    for k in idx:
        ix, rem = divmod(k, ch.ny * ch.nz)
        iy, iz = divmod(rem, ch.nz)
        b = ch.octree_nodes(k)
        inst = rd(ch.ox0 + ix, ch.oy0 + iy, ch.oz0 + iz) or b""
        if bytes(b) != inst:
            differ_install += 1
            first = first or ("install", k)
        if b:
            t, d = W.decode_octree(b, 0, len(b) // 4)
            if bytes(W.encode_octree(t, d)) != bytes(b):
                differ_writer += 1
                first = first or ("writer", k)
        checked += 1
    rows.append((differ_install == 0 and differ_writer == 0, "S8a.NS.roundtrip %d octrees of %d checked: %d differ from the install's bytes, %d differ "
                 "after decode -> our writer%s; chunk fnv %016x" % (checked, n, differ_install, differ_writer, (" first %s" % (first,)) if first else "", ch.bin_fnv)))
    pl = read_placement(cs)
    A, E, N = pl["A"], pl["E"], pl["N"]
    dots = [abs(sum(A[i] * E[i] for i in range(3))), abs(sum(A[i] * N[i] for i in range(3))), abs(sum(E[i] * N[i] for i in range(3))),
            abs(math.sqrt(sum(v * v for v in A)) - 1), abs(math.sqrt(sum(v * v for v in E)) - 1), abs(math.sqrt(sum(v * v for v in N)) - 1)]
    sm = Sample(ch, pl, base_planet())
    tops = ch.top_m[np.isfinite(ch.top_m)] + ch.y_floor + pl["rebase"]
    stale = pl.get("chunkFnv") != ch.bin_fnv
    rows.append((abs(pl["rebase"]) <= REBASE_LIMIT_M and max(dots) < 1e-12 and float(np.median(tops)) > -450.0 and not stale,
                 "S8a.NS.placement site-v1 SN (%.0f, %.0f) rho %.0f m, turns %d, re-base %+.0f m (limit %.0f), frame error %.1e, the sample's seabed "
                 "p5/p50/p95 %.0f/%.0f/%.0f m, blend %.0f m%s" % (pl["sn"][0], pl["sn"][1], math.hypot(*pl["sn"]), pl["turns"], pl["rebase"], REBASE_LIMIT_M,
                                                                   max(dots), np.percentile(tops, 5), np.median(tops), np.percentile(tops, 95), pl["blend"],
                                                                   "; STALE: the placement names another chunk" if stale else "")))
    _ = sm
    for ok, line in rows:
        log(("PASS " if ok else "FAIL ") + line)
    return rows

def expected_entities(ch, pl, sm=None):
    """The chunk's entity records placed (planet-local), with family and level: what the oracle compares Subnautica's spawns with."""
    sm = sm or Sample(ch, pl, None)
    out = []
    for e in ch.entities:
        p = sm.native_to_local(*e["pos"])
        out.append((p, e["family"], e.get("level", 0)))
    return out

# ------------------------------------------------------------------------------------------------- preview
def preview(out_dir=PREVIEW_DIR, log=print):
    from PIL import Image, ImageDraw  # noqa: E402
    cb, ct, cs = chunk_paths()
    ch = Chunk(cb, ct)
    pl = read_placement(cs)
    base = base_planet()
    sm = Sample(ch, pl, base)
    os.makedirs(out_dir, exist_ok=True)
    # 1. the served seabed over the footprint and 200 m around it (top-down, 4 m), hillshaded, the square and the band outlined
    half = pl["half"]
    ext = half + 200.0
    g = np.arange(-ext, ext, 4.0)
    U, V = np.meshgrid(g, g, indexing="xy")
    hs = np.full(U.shape, np.nan)
    for r in range(U.shape[0]):
        ys = np.arange(40.0, -520.0, -2.0)
        pts = np.array([[sm.local_of_tangent(U[r, c], V[r, c], 0.0) for c in range(U.shape[1])]])[0]
        nrm = np.sqrt((pts ** 2).sum(axis=1))
        dirs = pts / nrm[:, None]
        # a column scan per pixel: the first rock from the top (2 m steps then 0.25 m)
        PX = dirs[:, None, 0] * (RS + ys[None, :])
        PY = dirs[:, None, 1] * (RS + ys[None, :])
        PZ = dirs[:, None, 2] * (RS + ys[None, :])
        f, _H = sm.field_points(PX.ravel(), PY.ravel(), PZ.ravel())
        f = f.reshape(PX.shape)
        first = np.argmax(f > 0, axis=1)
        hs[r] = np.where((f > 0).any(axis=1), ys[first], np.nan)
    lo, hi = np.nanpercentile(hs, 1), np.nanpercentile(hs, 99)
    gx, gz = np.gradient(np.nan_to_num(hs, nan=lo), 4.0)
    shade = np.clip(0.55 + 0.4 * (-gx + gz) / 1.5, 0.1, 1.0)
    val = np.clip((hs - lo) / max(1e-6, hi - lo), 0, 1)
    rgb = np.stack([shade * (60 + 150 * val), shade * (90 + 120 * val), shade * (120 + 60 * (1 - val))], axis=-1)
    im = Image.fromarray(np.nan_to_num(rgb).astype(np.uint8)[::-1]).resize((U.shape[1] * 2, U.shape[0] * 2), Image.NEAREST)
    dr = ImageDraw.Draw(im)
    sc = 2.0 / 4.0
    for hh, col in ((half, (255, 255, 255)), (half - pl["blend"], (255, 200, 80))):
        a, b = (ext - hh) * sc, (ext + hh) * sc
        dr.rectangle([a, a, b, b], outline=col)
    dr.text((6, 6), "sample: GP chunk %s at site-v1 (%.0f, %.0f), turns %d, re-base %+.0f m; seabed %.0f..%.0f m" % (pl.get("chunk"), pl["sn"][0], pl["sn"][1], pl["turns"],
                                                                                                             pl["rebase"], lo, hi), fill=(255, 255, 255))
    p1 = os.path.join(out_dir, "ns-relief.png")
    im.save(p1)
    # 2. the seam profile: along u through the footprint centre, the served seabed against the chunk's own and the planet's
    us = np.arange(-ext, ext, 2.0)
    served, own, plan = [], [], []
    for u in us:
        P = sm.local_of_tangent(u, 0.0, 0.0)
        n = math.sqrt(sum(c * c for c in P))
        ys = np.arange(40.0, -520.0, -0.5)
        PX = P[0] / n * (RS + ys)
        PY = P[1] / n * (RS + ys)
        PZ = P[2] / n * (RS + ys)
        f, H = sm.field_points(PX, PY, PZ)
        served.append(ys[np.argmax(f > 0)] if (f > 0).any() else np.nan)
        plan.append(float(H[0]))
        X = half + unturn(pl["turns"], u, 0.0)[0]
        Z = half + unturn(pl["turns"], u, 0.0)[1]
        own.append(float(ch.top_at(np.array([X]), np.array([Z]))[0]) + ch.y_floor + pl["rebase"] if 0 <= X <= ch.size_x and 0 <= Z <= ch.size_z else np.nan)
    Wd, Hd = 1000, 360
    im2 = Image.new("RGB", (Wd, Hd), (18, 26, 40))
    d2 = ImageDraw.Draw(im2)
    allv = np.array([v for v in served + own + plan if np.isfinite(v)])
    y0, y1 = float(allv.min()) - 10, float(allv.max()) + 10

    def pxy(i, y):
        return 10 + (Wd - 20) * i / (len(us) - 1), 20 + (Hd - 40) * (y1 - y) / (y1 - y0)
    for arr, col in ((plan, (120, 120, 255)), (own, (255, 160, 60)), (served, (240, 240, 240))):
        pts = [pxy(i, v) for i, v in enumerate(arr) if np.isfinite(v)]
        if len(pts) > 1:
            d2.line(pts, fill=col, width=2)
    for uu in (-half, -half + pl["blend"], half - pl["blend"], half):
        x = 10 + (Wd - 20) * (uu + ext) / (2 * ext)
        d2.line([(x, 10), (x, Hd - 10)], fill=(90, 90, 90))
    d2.text((12, 4), "seam profile along u (v = 0): white served, orange the chunk's own top, blue the planet's heightmap; %.0f..%.0f m" % (y0, y1), fill=(230, 230, 230))
    p2 = os.path.join(out_dir, "ns-seam.png")
    im2.save(p2)
    # 3. the entities by family over the chunk (native, turned as placed)
    im3 = Image.new("RGB", (640, 640), (12, 18, 28))
    d3 = ImageDraw.Draw(im3)
    cols = {"flora": (90, 220, 110), "prop": (200, 200, 200), "creature": (250, 90, 90), "outcrop": (250, 210, 60), "light": (120, 200, 255), "vfx": (180, 120, 250),
            "other": (140, 140, 140), "slot": (255, 120, 220)}
    for e in ch.entities:
        u, v = turn(pl["turns"], e["pos"][0] - (ch.x0 + half), e["pos"][2] - (ch.z0 + half))
        x, y = (u + half) * 640 / ch.size_x, 640 - (v + half) * 640 / ch.size_z
        d3.point((x, y), fill=cols.get(e["family"], (255, 255, 255)))
    for e in ch.excluded:
        u, v = turn(pl["turns"], e["pos"][0] - (ch.x0 + half), e["pos"][2] - (ch.z0 + half))
        x, y = (u + half) * 640 / ch.size_x, 640 - (v + half) * 640 / ch.size_z
        d3.rectangle([x - 1, y - 1, x + 1, y + 1], outline=(255, 0, 0))
    d3.text((6, 6), "entities as placed: " + ", ".join("%s %d" % (k, v) for k, v in collections.Counter(e["family"] for e in ch.entities).most_common()) +
            "; red: left out %d" % len(ch.excluded), fill=(255, 255, 255))
    p3 = os.path.join(out_dir, "ns-entities.png")
    im3.save(p3)
    nums = {"chunk": {"name": pl.get("chunk"), "octrees": [ch.nx, ch.ny, ch.nz], "nodesBytes": len(ch.raw), "binFnv": "%016x" % ch.bin_fnv,
                      "window": [ch.x0, ch.z0, ch.size_x], "yFloor": ch.y_floor},
            "placement": {k: (list(v) if isinstance(v, tuple) else v) for k, v in pl.items() if k not in ("chunkFnv", "indexFnv")},
            "seabed": {"p5": float(np.nanpercentile(hs, 5)), "p50": float(np.nanmedian(hs)), "p95": float(np.nanpercentile(hs, 95))},
            "entities": dict(collections.Counter(e["family"] for e in ch.entities)), "excluded": dict(collections.Counter(x["why"] for x in ch.excluded))}
    open(os.path.join(out_dir, "ns-numbers.json"), "w", encoding="utf-8").write(json.dumps(nums, indent=1))
    log("preview: %s, %s, %s" % (p1, p2, p3))
    return [p1, p2, p3]

# ------------------------------------------------------------------------------------------------- build, stats
def build(log=print):
    ch = cut(log=log)
    binp, tsvp = write_chunk(ch, log=log)
    c = Chunk(binp, tsvp)
    pl = make_placement(c, base_planet())
    _b, _t, sp = chunk_paths()
    open(sp, "w", encoding="utf-8", newline="\n").write(placement_text(pl))
    log("placement %s: site-v1 (%.0f, %.0f), turns %d, re-base %+.0f m (planet median %.1f, chunk median top %.1f)" % (sp, pl["sn"][0], pl["sn"][1], pl["turns"],
                                                                                                                 pl["rebase"], pl["medianBase"], pl["medianTop"]))
    return binp, tsvp, sp

def stats(log=print):
    cb, ct, cs = chunk_paths()
    ch = Chunk(cb, ct)
    lv = collections.Counter((e["family"], e.get("level", 0)) for e in ch.entities)
    log("chunk %s: %d x %d x %d octrees, nodes %.1f MB, file %.1f MB; entities %d: %s" % (ch.head.get("name"), ch.nx, ch.ny, ch.nz, len(ch.raw) / 1048576.0,
                                                                                   os.path.getsize(cb) / 1048576.0, len(ch.entities), dict(lv)))
    log("left out: %s" % dict(collections.Counter(x["why"] for x in ch.excluded)))

# ------------------------------------------------------------------------------------------------- selftest
def selftest():
    import tempfile
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        print(("PASS " if cond else "FAIL ") + name + ((" " + detail) if detail else ""))
        ok = ok and bool(cond)

    d = tempfile.mkdtemp(prefix="s8a_chunks_")
    binp, tsvp = synthetic_chunk(d)
    ch = Chunk(binp, tsvp)
    # the codec: every octree decodes and re-encodes to its bytes; the point lookup = the decoded raster
    rt = all(bytes(W.encode_octree(*W.decode_octree(ch.octree_nodes(k), 0, int(ch.counts[k])))) == bytes(ch.octree_nodes(k)) for k in range(len(ch.counts)))
    check("codec round trip (synthetic octrees)", rt)
    t, dd = W.decode_octree(ch.octree_nodes(5), 0, int(ch.counts[5]))
    ix, rem = divmod(5, ch.ny * ch.nz)
    iy, iz = divmod(rem, ch.nz)
    zz, yy, xx = np.meshgrid(np.arange(S), np.arange(S), np.arange(S), indexing="ij")
    T2, D2 = ch.lookup(xx.ravel() + ix * S, yy.ravel() + iy * S, zz.ravel() + iz * S)
    check("point lookup = decoded raster", np.array_equal(T2, t.ravel()) and np.array_equal(D2, dd.ravel()))
    # the frame: orthonormal, the anchor where sn_to_dir puts it, E along SN +x at the anchor (site v1's azimuthal map)
    A, E, N = frame_at_sn(*SAMPLE_SN)
    import sculpt_de4546b as SC  # noqa: E402
    ad = SC.sn_to_dir(*SAMPLE_SN)
    check("anchor = sculpt.sn_to_dir", max(abs(A[i] - ad[i]) for i in range(3)) < 1e-15)
    pE = SC.sn_to_dir(SAMPLE_SN[0] + 1.0, SAMPLE_SN[1])
    de = [pE[i] - ad[i] for i in range(3)]
    nde = math.sqrt(sum(v * v for v in de))
    check("east = SN +x at the anchor", sum(de[i] / nde * E[i] for i in range(3)) > 0.9999, "cos %.6f" % sum(de[i] / nde * E[i] for i in range(3)))
    # turns: the field's map and the entities' map are inverse, and Unity's yaw does the same
    inv = all(abs(unturn(k, *turn(k, 3.0, -7.0))[0] - 3.0) < 1e-12 and abs(unturn(k, *turn(k, 3.0, -7.0))[1] + 7.0) < 1e-12 for k in range(4))
    q = (0.0, math.sin(math.pi / 4), 0.0, math.cos(math.pi / 4))      # Euler(0, 90, 0)
    v = qrot(q, (1.0, 0.0, 0.0))
    check("turn inverse; Unity yaw 90 = turn 1", inv and abs(v[0] - turn(1, 1.0, 0.0)[0]) < 1e-12 and abs(v[2] - turn(1, 1.0, 0.0)[1]) < 1e-12)
    # the field: continuous across the seam (the band's outer edge meets the heightmap), the chunk's surface inside
    base = base_planet()
    pl = make_placement(ch, base)
    open(os.path.join(d, "s.txt"), "w").write(placement_text(pl))
    pl2 = read_placement(os.path.join(d, "s.txt"))
    check("placement text round trip", all(pl2[k] == pl[k] for k in ("A", "E", "N", "turns", "rebase", "blend", "half")))
    sm = Sample(ch, pl2, base)
    half = pl2["half"]
    steps = []
    for u in np.arange(half - 3.0, half + 3.0, 0.5):
        P = sm.local_of_tangent(u, 3.0, -100.0)
        n = math.sqrt(sum(c * c for c in P))
        ys = np.arange(-20.0, -260.0, -0.25)
        f, H = sm.field_points(P[0] / n * (RS + ys), P[1] / n * (RS + ys), P[2] / n * (RS + ys))
        steps.append(float(ys[np.argmax(f > 0)]) - float(H[0]))
    check("seam: the outer edge serves the heightmap", max(abs(s) for s in steps) <= 0.5, "max |served - heightmap| %.2f m" % max(abs(s) for s in steps))
    # inside (past the band) the served top is the chunk's own top
    X, Z = 40.5, 40.5
    u, v = turn(pl2["turns"], X - half, Z - half)
    P = sm.local_of_tangent(u, v, 0.0)
    n = math.sqrt(sum(c * c for c in P))
    ys = np.arange(30.0, -300.0, -0.125)
    f, H = sm.field_points(P[0] / n * (RS + ys), P[1] / n * (RS + ys), P[2] / n * (RS + ys))
    served = float(ys[np.argmax(f > 0)])
    own = float(sm.top_native(np.array([ch.x0 + X]), np.array([ch.z0 + Z]))[0])
    check("inside: served top = the chunk's own top", abs(served - own) <= 0.6, "served %.2f own %.2f" % (served, own))
    # content of a box is deterministic
    box = (8.0 * math.floor(P[0] / 8.0), 8.0 * math.floor(P[1] / 8.0), 8.0 * math.floor((P[2] - 300) / 8.0), 8, 8, 8, 1)
    check("serve deterministic", np.array_equal(sm.serve_box(*box), sm.serve_box(*box)))
    print("selftest %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("cmd", nargs="?", choices=("build", "verify", "vectors", "preview", "stats"))
    ap.add_argument("--out", default=None)
    ap.add_argument("--roundtrip-n", type=int, default=None, help="verify: check only N octrees (default all)")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.cmd == "build":
        build()
        return 0
    if a.cmd == "verify":
        rows = verify(roundtrip_n=a.roundtrip_n)
        return 0 if all(ok for ok, _l in rows) else 1
    if a.cmd == "vectors":
        write_vectors()
        return 0
    if a.cmd == "preview":
        preview(a.out or PREVIEW_DIR)
        return 0
    if a.cmd == "stats":
        stats()
        return 0
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main())
