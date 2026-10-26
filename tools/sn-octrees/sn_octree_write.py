"""S8a converter core, Python twin: Subnautica compiled octree batches written by the engine's own rules.

The same bytes for the same raster as the C# writer (src/DeepEngineers.Wire/S8aOctree.cs); the two share no code, only
the rules of CONTRACT §3.3 "Conversion" and §5.4 "S8a conventions" (format octree.write, octree.batch), VERIFIED in the
decompiled Subnautica 1.22.83031:
 OctNode.Collapse / CanCollapse / CacheDownsampledType /
 ComputeMostCommonChildType / IsBelowSurface / IsNearAndBelowSurface
 SetInternal (pre-order, 8-node blocks from id 1), Set, Write (u16 count)
 SaveCompactOctrees (int32 4, then per in-bounds root the octree)
Rules: a node whose 8 children are leaves, not mixed below/above the surface, with all near (density != 0) children
equal, collapses into a leaf with the first near child's value (child 0's when none is near); otherwise it is interior
with density = integer mean of the near children (0 and child 0's type when none) and type = the engine's
ComputeMostCommonChildType (child 0's own count never compared: the engine's quirk, kept). Child c = 4*xbit + 2*ybit +
zbit. Batch file: int32 version (>= 4), octrees x-outer, y, z-inner; crater ids (bx, bz in 0..25) store only the octrees
inside 128 x 100 x 128, window ids all 125.

Raster convention (both twins): uint8 arrays of shape (32, 32, 32) indexed [z, y, x] (flat index x + 32*y + 1024*z).

Usage (repository root; stdlib + numpy; read-only on the install):
 python -I tools/sn-octrees/sn_octree_write.py --selftest
 python -I tools/sn-octrees/sn_octree_write.py --roundtrip N [--dir <CompiledOctreesCache>] [--seed S]
 [--json <out.json>] [--mutant S8a.M13]
 S8a.C1 (Python half): N batch files (12-18-12, 7-17-10, 14-8-9 always, the rest a seeded sample): parse -> raster
 -> write -> bytes identical per octree and per file; the writer-rule census on every parsed install octree and on
 every octree written (.rules).
 Exit 0 when both rows pass (with --mutant S8a.M13 both must fail: exit 0 when they do, the mutant check).
 python -I tools/sn-octrees/sn_octree_write.py --vectors <tests/s8a/vectors/octree.tsv>
 Writes the synthetic raster vectors the C# tests read (generator: splitmix64, see synthetic_raster).
"""
import argparse
import json
import os
import random
import struct
import sys
import time

import numpy as np

S = 32
NEAR_ZERO = 126
FILE_VERSION = 4
OCTREES_PER_BATCH = 5
WORLD_OCT = (128, 100, 128)
CRATER_MIN, CRATER_MAX = 0, 25
MAX_NODES = 37449
STEAM_DEFAULT = r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica"
REQUIRED = ("12-18-12", "7-17-10", "14-8-9")

# ---------------------------------------------------------------------------------------------------------------------
# batch ids and the file layout
# ---------------------------------------------------------------------------------------------------------------------

def is_crater_id(bx, by, bz):
    return CRATER_MIN <= bx <= CRATER_MAX and CRATER_MIN <= bz <= CRATER_MAX

def octree_coords(i):
    return i // 25, (i // 5) % 5, i % 5

def is_stored(bx, by, bz, i):
    if not is_crater_id(bx, by, bz):
        return True
    ix, iy, iz = octree_coords(i)
    ox, oy, oz = bx * 5 + ix, by * 5 + iy, bz * 5 + iz
    return 0 <= ox < WORLD_OCT[0] and 0 <= oy < WORLD_OCT[1] and 0 <= oz < WORLD_OCT[2]

def parse_batch(data, bx, by, bz):
    """-> (version, entries[125]) with entries[i] = (offset, nodeCount) or None when not stored. Raises ValueError."""
    if len(data) < 4:
        raise ValueError("short file (%d bytes)" % len(data))
    version = struct.unpack_from("<i", data, 0)[0]
    if version < FILE_VERSION:
        raise ValueError("version %d < %d" % (version, FILE_VERSION))
    off = 4
    entries = [None] * 125
    for i in range(125):
        if not is_stored(bx, by, bz, i):
            continue
        if off + 2 > len(data):
            raise ValueError("truncated at octree %d" % i)
        n = struct.unpack_from("<H", data, off)[0]
        off += 2
        if off + 4 * n > len(data):
            raise ValueError("truncated nodes at octree %d" % i)
        entries[i] = (off, n)
        off += 4 * n
    if off != len(data):
        raise ValueError("not consumed exactly (%d of %d bytes)" % (off, len(data)))
    return version, entries

def serialize_batch(version, octrees, bx, by, bz):
    """octrees[i] = node bytes (4*count) for every stored i."""
    out = bytearray(struct.pack("<i", version))
    for i in range(125):
        if not is_stored(bx, by, bz, i):
            continue
        nodes = octrees[i]
        out += struct.pack("<H", len(nodes) // 4)
        out += nodes
    return bytes(out)

def fnv1a64(data):
    h = 0xcbf29ce484222325
    for b in data:
        h = ((h ^ b) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def fnv1a64_np(data):
    """fnv1a64 of a bytes object (fast path through Python ints; same result as fnv1a64)."""
    return fnv1a64(data)

# ---------------------------------------------------------------------------------------------------------------------
# decode (raster from nodes) and the writer rules
# ---------------------------------------------------------------------------------------------------------------------

def decode_octree(data, offset, count):
    """Nodes -> (T, D) uint8 (32, 32, 32) [z, y, x]; a node is a leaf when firstChild is 0, at size 1, or when its
 children would lie past the count (as the S4 reader); 0 nodes = far empty."""
    T = np.zeros((S, S, S), np.uint8)
    D = np.zeros((S, S, S), np.uint8)
    if count <= 0:
        return T, D
    stack = [(0, 0, 0, 0, S)]
    while stack:
        node, x0, y0, z0, size = stack.pop()
        q = offset + 4 * node
        t, d = data[q], data[q + 1]
        fc = data[q + 2] | (data[q + 3] << 8)
        if fc == 0 or size == 1 or fc + 8 > count:
            T[z0:z0 + size, y0:y0 + size, x0:x0 + size] = t
            D[z0:z0 + size, y0:y0 + size, x0:x0 + size] = d
            continue
        h = size >> 1
        for c in range(8):
            stack.append((fc + c, x0 + h * ((c >> 2) & 1), y0 + h * ((c >> 1) & 1), z0 + h * (c & 1), h))
    return T, D

def _children(a, xb, yb, zb):
    return a[zb::2, yb::2, xb::2]

def encode_octree(T, D, m13=False):
    """(T, D) uint8 (32, 32, 32) [z, y, x] -> node bytes (the engine's rules; S8a.M13: interior = child 0's value)."""
    T = np.ascontiguousarray(T, np.uint8)
    D = np.ascontiguousarray(D, np.uint8)
    t0, d0 = int(T.flat[0]), int(D.flat[0])
    if (T == t0).all() and (D == d0).all():
        return bytes((t0, d0, 0, 0))
    levels = [(T, D, None)]
    t, d, leaf = T, D, None
    for L in range(1, 6):
        n = S >> L
        ct = np.empty((8, n, n, n), np.uint8)
        cd = np.empty((8, n, n, n), np.uint8)
        cl = np.ones((8, n, n, n), bool)
        for c in range(8):
            xb, yb, zb = (c >> 2) & 1, (c >> 1) & 1, c & 1
            ct[c] = _children(t, xb, yb, zb)
            cd[c] = _children(d, xb, yb, zb)
            if leaf is not None:
                cl[c] = _children(leaf, xb, yb, zb)
        below = np.where(cd == 0, ct > 0, cd >= NEAR_ZERO)
        mixed = below.any(0) & (~below).any(0)
        near = cd != 0
        first = np.argmax(near, axis=0)                       # 0 when no child is near
        tf = np.take_along_axis(ct, first[None], 0)[0]
        df = np.take_along_axis(cd, first[None], 0)[0]
        alleq = ((~near) | ((ct == tf[None]) & (cd == df[None]))).all(0)
        collapse = cl.all(0) & ~mixed & alleq
        if m13:
            ti, di = ct[0], cd[0]
        else:
            cnt = near.sum(0)
            ssum = (cd.astype(np.int32) * near).sum(0)
            di = np.where(cnt > 0, ssum // np.maximum(cnt, 1), 0).astype(np.uint8)
            nab = (ct > 0) & (cd >= NEAR_ZERO)
            fm = []
            for i in range(8):
                fmi = np.full((n, n, n), i, np.int8)
                for j in range(i - 1, -1, -1):
                    fmi = np.where(ct[j] == ct[i], np.int8(j), fmi)
                fm.append(fmi)
            arr = [sum((nab[i] & (fm[i] == j)).astype(np.int32) for i in range(8)) for j in range(8)]
            num = np.zeros((n, n, n), np.int32)
            b = ct[0].copy()
            for k in range(1, 8):
                cond = (arr[k] > num) | ((arr[k] == num) & (ct[k] > b))
                num = np.where(cond, arr[k], num)
                b = np.where(cond, ct[k], b)
            ti = np.where(cnt > 0, b, ct[0]).astype(np.uint8)
        t = np.where(collapse, tf, ti).astype(np.uint8)
        d = np.where(collapse, df, di).astype(np.uint8)
        leaf = collapse
        levels.append((t, d, leaf))
    # pre-order layout (CompactOctree.SetInternal): blocks of 8 from id 1, child 0's subtree first
    lt = [lv[0].tolist() for lv in levels]
    ld = [lv[1].tolist() for lv in levels]
    ll = [None] + [lv[2].tolist() for lv in levels[1:]]
    interior = sum(int((~lv[2]).sum()) for lv in levels[1:])
    count = 1 + 8 * interior
    out = bytearray(4 * count)
    nxt = 1
    stack = [(5, 0, 0, 0, 0)]                                 # level, nx, ny, nz, node id
    while stack:
        L, nx, ny, nz, nid = stack.pop()
        q = 4 * nid
        out[q] = lt[L][nz][ny][nx]
        out[q + 1] = ld[L][nz][ny][nx]
        if L == 0 or ll[L][nz][ny][nx]:
            continue
        fc = nxt
        nxt += 8
        out[q + 2] = fc & 0xFF
        out[q + 3] = fc >> 8
        for c in range(7, -1, -1):
            stack.append((L - 1, 2 * nx + ((c >> 2) & 1), 2 * ny + ((c >> 1) & 1), 2 * nz + (c & 1), fc + c))
    assert nxt == count, (nxt, count)
    return bytes(out)

# ---------------------------------------------------------------------------------------------------------------------
# writer-rule census (S8a.C1.rules)
# ---------------------------------------------------------------------------------------------------------------------

def _below(t, d):
    return (t > 0) if d == 0 else d >= NEAR_ZERO

def _most_common(ch):
    arr = [0] * 8
    for i in range(8):
        t, d = ch[i]
        if not (t > 0 and d >= NEAR_ZERO):
            continue
        for j in range(i + 1):
            if t == ch[j][0]:
                arr[j] += 1
                break
    num, b = 0, ch[0][0]
    for k in range(1, 8):
        if arr[k] > num or (arr[k] == num and ch[k][0] > b):
            num, b = arr[k], ch[k][0]
    return b

def _downsampled(ch):
    s = c = 0
    for t, d in ch:
        if d != 0:
            s += d
            c += 1
    if c == 0:
        return ch[0][0], 0
    return _most_common(ch), s // c

def _can_collapse(leafs, ch):
    if not all(leafs):
        return False
    any_out = any_in = False
    for t, d in ch:
        b = _below(t, d)
        any_out |= not b
        any_in |= b
        if any_out and any_in:
            return False
    nears = [k for k in range(8) if ch[k][1] != 0]
    if not nears:
        return True
    k0 = nears[0]
    return all(ch[k] == ch[k0] for k in nears)

def check_rules(data, offset, count, st):
    st["octrees"] += 1
    if count <= 0:
        st["empty"] += 1
        return
    nxt = [1]
    stack = [(0, 0)]
    while stack:
        node, depth = stack.pop()
        q = offset + 4 * node
        fc = data[q + 2] | (data[q + 3] << 8)
        if fc == 0:
            continue
        if depth >= 5 or fc + 8 > count:
            st["layoutBad"] += 1
            continue
        if fc != nxt[0]:
            st["layoutBad"] += 1
        nxt[0] = fc + 8
        ch, leafs = [], []
        for c in range(8):
            qc = offset + 4 * (fc + c)
            ch.append((data[qc], data[qc + 1]))
            leafs.append((data[qc + 2] | (data[qc + 3] << 8)) == 0)
        st["interior"] += 1
        if (data[q], data[q + 1]) == _downsampled(ch):
            st["downsampleOk"] += 1
        else:
            st["downsampleBad"] += 1
        if _can_collapse(leafs, ch):
            st["collapsible"] += 1
        for c in range(7, -1, -1):
            stack.append((fc + c, depth + 1))
    if nxt[0] != count:
        st["countBad"] += 1

def new_rule_stats():
    return dict(octrees=0, empty=0, interior=0, downsampleOk=0, downsampleBad=0, collapsible=0, layoutBad=0, countBad=0)

def rules_clean(st):
    return st["downsampleBad"] == 0 and st["collapsible"] == 0 and st["layoutBad"] == 0 and st["countBad"] == 0

# ---------------------------------------------------------------------------------------------------------------------
# synthetic rasters (the vectors both twins generate from a seed)
# ---------------------------------------------------------------------------------------------------------------------
M64 = 0xFFFFFFFFFFFFFFFF

class SplitMix64:
    def __init__(self, seed):
        self.s = seed & M64

    def next(self):
        self.s = (self.s + 0x9E3779B97F4A7C15) & M64
        z = self.s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & M64
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & M64
        return z ^ (z >> 31)

TYPES = (0, 1, 84, 150)
KINDS = ("random", "blocky4", "blocky8", "terrain", "uniform")

def synthetic_raster(kind, seed):
    """-> (T, D) [z, y, x]. Same algorithm as S8aOctreeVectorTests.Synthetic (C#):
 random: per voxel (z, y, x order) r = next; t = TYPES[r & 3]; m = (r >> 2) & 3; d = 0 when m is 0 or 3,
 126 + (r >> 8) % 126 when m is 1, 1 + (r >> 8) % 125 when m is 2.
 blockyK: one draw per K^3 block (block z, y, x order), the random rule, the block filled with it.
 terrain: h = 8 + (x*7 + z*13 + seed) % 11 (seed as a non-negative int mod 2^31); dist = h - y;
 dist >= 2: (84 if y < 6 else 150, 0); dist <= -2: (0, 0); else d = 126 + 50*dist clamped to 1..251,
 t = 150 if d >= 126 else 0.
 uniform: one draw, the random rule, everywhere."""
    T = np.zeros((S, S, S), np.uint8)
    D = np.zeros((S, S, S), np.uint8)
    rng = SplitMix64(seed)

    def rule(r):
        t = TYPES[r & 3]
        m = (r >> 2) & 3
        if m == 1:
            d = 126 + (r >> 8) % 126
        elif m == 2:
            d = 1 + (r >> 8) % 125
        else:
            d = 0
        return t, d

    if kind == "random":
        for z in range(S):
            for y in range(S):
                for x in range(S):
                    T[z, y, x], D[z, y, x] = rule(rng.next())
    elif kind in ("blocky4", "blocky8"):
        k = 4 if kind == "blocky4" else 8
        for bz in range(S // k):
            for by in range(S // k):
                for bx in range(S // k):
                    t, d = rule(rng.next())
                    T[bz * k:(bz + 1) * k, by * k:(by + 1) * k, bx * k:(bx + 1) * k] = t
                    D[bz * k:(bz + 1) * k, by * k:(by + 1) * k, bx * k:(bx + 1) * k] = d
    elif kind == "terrain":
        sd = seed % (1 << 31)
        for z in range(S):
            for x in range(S):
                h = 8 + (x * 7 + z * 13 + sd) % 11
                for y in range(S):
                    dist = h - y
                    if dist >= 2:
                        T[z, y, x], D[z, y, x] = (84 if y < 6 else 150), 0
                    elif dist <= -2:
                        T[z, y, x], D[z, y, x] = 0, 0
                    else:
                        d = max(1, min(251, 126 + 50 * dist))
                        T[z, y, x], D[z, y, x] = (150 if d >= 126 else 0), d
    elif kind == "uniform":
        t, d = rule(rng.next())
        T[:], D[:] = t, d
    else:
        raise ValueError(kind)
    return T, D

VECTOR_CASES = [("random", 1), ("random", 7), ("random", 4546), ("blocky4", 2), ("blocky4", 99), ("blocky8", 3),
                ("blocky8", 1234), ("terrain", 0), ("terrain", 17), ("terrain", 2025), ("uniform", 5), ("uniform", 6)]

def write_vectors(path, m13=False):
    lines = ["# S8a octree writer vectors (tools/sn-octrees/sn_octree_write.py --vectors); kind seed nodes bytes fnv1a64",
             "# raster: synthetic_raster(kind, seed); encode by the engine's rules; read by S8aOctreeVectorTests (C#)"]
    for kind, seed in VECTOR_CASES:
        T, D = synthetic_raster(kind, seed)
        b = encode_octree(T, D, m13)
        lines.append("%s\t%d\t%d\t%d\t%016x" % (kind, seed, len(b) // 4, len(b), fnv1a64(b)))
    data = "\n".join(lines) + "\n"
    if path:
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        with open(path, "w", newline="\n") as f:
            f.write(data)
    return data

# ---------------------------------------------------------------------------------------------------------------------
# the round trip on the install (read-only)
# ---------------------------------------------------------------------------------------------------------------------

def default_octree_dir():
    sn = os.environ.get("DEEPENG_SUBNAUTICA") or STEAM_DEFAULT
    return os.path.join(sn, "Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "Build18", "CompiledOctreesCache")

def pick_files(octree_dir, n, seed):
    files = sorted(f for f in os.listdir(octree_dir) if f.startswith("compiled-batch-") and f.endswith(".optoctrees"))
    req = ["compiled-batch-%s.optoctrees" % k for k in REQUIRED if "compiled-batch-%s.optoctrees" % k in files]
    rest = [f for f in files if f not in req]
    rnd = random.Random(seed)
    k = max(0, min(len(rest), n - len(req)))
    return req + sorted(rnd.sample(rest, k))

def batch_id(name):
    bx, by, bz = name[len("compiled-batch-"):-len(".optoctrees")].split("-")
    return int(bx), int(by), int(bz)

def roundtrip(octree_dir, n, seed=4546, m13=False, verbose=True):
    t0 = time.time()
    res = dict(dir=octree_dir, files=0, filesIdentical=0, filesDifferent=0, octrees=0, octreesIdentical=0,
               octreesDifferent=0, subdivided=0, nodes=0, mutant="S8a.M13" if m13 else "", examples=[])
    rules = new_rule_stats()
    written = new_rule_stats()
    for name in pick_files(octree_dir, n, seed):
        bx, by, bz = batch_id(name)
        with open(os.path.join(octree_dir, name), "rb") as f:
            data = f.read()
        version, entries = parse_batch(data, bx, by, bz)
        out = [None] * 125
        file_ok = True
        for i, e in enumerate(entries):
            if e is None:
                continue
            off, cnt = e
            check_rules(data, off, cnt, rules)
            T, D = decode_octree(data, off, cnt)
            nb = encode_octree(T, D, m13)
            check_rules(nb, 0, len(nb) // 4, written)
            out[i] = nb
            res["octrees"] += 1
            res["nodes"] += cnt
            if cnt > 1:
                res["subdivided"] += 1
            if nb == data[off:off + 4 * cnt]:
                res["octreesIdentical"] += 1
            else:
                res["octreesDifferent"] += 1
                file_ok = False
                if len(res["examples"]) < 5:
                    res["examples"].append("%s octree %d: %d nodes -> %d" % (name, i, cnt, len(nb) // 4))
        again = serialize_batch(version, out, bx, by, bz)
        res["files"] += 1
        if again == data and file_ok:
            res["filesIdentical"] += 1
        else:
            res["filesDifferent"] += 1
        if verbose:
            print("  %-36s %9d bytes %s" % (name, len(data), "identical" if again == data else "DIFFERENT"))
    res["rules"] = rules
    res["rulesWritten"] = written
    res["seconds"] = round(time.time() - t0, 1)
    rt_pass = res["files"] > 0 and res["filesDifferent"] == 0 and res["octreesDifferent"] == 0
    ru_pass = rules_clean(rules) and rules_clean(written) and rules["octrees"] > 0
    res["rows"] = {"S8a.C1.roundtrip": "PASS" if rt_pass else "FAIL", "S8a.C1.rules": "PASS" if ru_pass else "FAIL"}
    return res

# ---------------------------------------------------------------------------------------------------------------------
# selftest (no install needed)
# ---------------------------------------------------------------------------------------------------------------------

def selftest():
    fails = []

    def check(cond, what):
        if not cond:
            fails.append(what)

    # 1. uniform rasters are one node
    T = np.zeros((S, S, S), np.uint8)
    D = np.zeros((S, S, S), np.uint8)
    check(encode_octree(T, D) == bytes((0, 0, 0, 0)), "empty raster = one far-empty node")
    T[:] = 84
    check(encode_octree(T, D) == bytes((84, 0, 0, 0)), "solid raster = one far-solid node")
    # 2. one different voxel: full path to the voxel (5 interior levels: 41 nodes), layout pre-order
    T[:] = 84
    D[:] = 0
    T[0, 0, 0], D[0, 0, 0] = 0, 0
    b = encode_octree(T, D)
    check(len(b) // 4 == 41, "one empty voxel in rock: 41 nodes (got %d)" % (len(b) // 4))
    st = new_rule_stats()
    check_rules(b, 0, len(b) // 4, st)
    check(rules_clean(st) and st["interior"] == 5, "rules clean on a written octree: %r" % st)
    # 3. decode(encode(r)) re-encodes to the same bytes (idempotent), and the census is clean, on every vector case
    for kind, seed in VECTOR_CASES:
        T, D = synthetic_raster(kind, seed)
        b = encode_octree(T, D)
        T2, D2 = decode_octree(b, 0, len(b) // 4)
        check(encode_octree(T2, D2) == b, "idempotent %s/%d" % (kind, seed))
        st = new_rule_stats()
        check_rules(b, 0, len(b) // 4, st)
        check(rules_clean(st), "rules clean %s/%d: %r" % (kind, seed, st))
        if kind == "random":
            check(len(b) // 4 == MAX_NODES or len(b) // 4 > 30000, "random raster nearly fully subdivided (%d)" % (len(b) // 4))
    # 4. CacheDownsampledType and the tie rule of ComputeMostCommonChildType on hand cases
    ch = [(5, 200), (7, 200), (7, 200), (5, 200), (0, 0), (0, 0), (0, 0), (0, 0)]
    check(_most_common(ch) == 7, "type 5 count is at child 0 (never compared): 7 wins with 2")
    ch = [(9, 130), (3, 130), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0)]
    check(_most_common(ch) == 3, "child 1's type 3 (count 1 > 0) wins over child 0's 9")
    ch = [(9, 0), (3, 0), (4, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0)]
    check(_most_common(ch) == 9, "no near-and-below child: counts 0; a larger type replaces on ties")
    check(_downsampled([(1, 10), (2, 21), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0)]) == (2, 15),
          "integer mean floors; no near-and-below child: child 1's larger type replaces child 0's on the 0 = 0 tie")
    # 5. M13 changes the bytes of a subdivided octree and its census goes red
    T, D = synthetic_raster("terrain", 17)
    good = encode_octree(T, D)
    bad = encode_octree(T, D, m13=True)
    check(good != bad, "S8a.M13 changes interior nodes")
    st = new_rule_stats()
    check_rules(bad, 0, len(bad) // 4, st)
    check(st["downsampleBad"] > 0, "S8a.M13 census red")
    # 6. batch layout: crater edge batch 25-*-25 stores only in-bounds octrees; window ids all 125
    stored = sum(1 for i in range(125) if is_stored(25, 18, 25, i))
    check(stored == 3 * 5 * 3, "crater edge batch 25-18-25 stores 45 octrees (got %d)" % stored)
    check(sum(1 for i in range(125) if is_stored(-3, 18, 4, i)) == 125, "window id stores 125")
    octs = [bytes((0, 0, 0, 0)) if is_stored(25, 18, 25, i) else None for i in range(125)]
    f = serialize_batch(4, octs, 25, 18, 25)
    v, e = parse_batch(f, 25, 18, 25)
    check(v == 4 and sum(1 for x in e if x) == 45 and len(f) == 4 + 45 * 6, "crater edge file layout")
    # 7. vectors file matches when present
    here = os.path.dirname(os.path.abspath(__file__))
    vec = os.path.normpath(os.path.join(here, "..", "..", "tests", "s8a", "vectors", "octree.tsv"))
    if os.path.exists(vec):
        with open(vec, "r") as fh:
            check(fh.read() == write_vectors(None), "tests/s8a/vectors/octree.tsv is current")
    for f_ in fails:
        print("FAIL", f_)
    print("sn_octree_write selftest:", "PASS" if not fails else "FAIL (%d)" % len(fails))
    return 0 if not fails else 1

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--roundtrip", type=int, metavar="N")
    ap.add_argument("--dir", default=None)
    ap.add_argument("--seed", type=int, default=4546)
    ap.add_argument("--json", default=None)
    ap.add_argument("--mutant", default="")
    ap.add_argument("--vectors", default=None)
    ap.add_argument("--quiet", action="store_true")
    a = ap.parse_args(argv)
    if a.mutant and a.mutant != "S8a.M13":
        print("unknown mutant %s (this tool owns S8a.M13 only)" % a.mutant)
        return 2
    m13 = a.mutant == "S8a.M13"
    if a.selftest:
        return selftest()
    if a.vectors:
        write_vectors(a.vectors)
        print("wrote", a.vectors)
        return 0
    if a.roundtrip:
        d = a.dir or default_octree_dir()
        if not os.path.isdir(d):
            print("CompiledOctreesCache not found: %s (set DEEPENG_SUBNAUTICA or --dir)" % d)
            return 3
        res = roundtrip(d, a.roundtrip, a.seed, m13, verbose=not a.quiet)
        r = res["rules"]
        print("S8a.C1 python: files %d identical %d different %d | octrees %d identical %d different %d (subdivided %d, nodes %d) | %.1f s"
              % (res["files"], res["filesIdentical"], res["filesDifferent"], res["octrees"], res["octreesIdentical"],
                 res["octreesDifferent"], res["subdivided"], res["nodes"], res["seconds"]))
        for label, r in (("install", res["rules"]), ("written", res["rulesWritten"])):
            print("S8a.C1 rules (%s): interior %d downsampleOk %d downsampleBad %d collapsible %d layoutBad %d countBad %d"
                  % (label, r["interior"], r["downsampleOk"], r["downsampleBad"], r["collapsible"], r["layoutBad"], r["countBad"]))
        for k, v in res["rows"].items():
            print(k, v)
        for ex in res["examples"]:
            print("  example", ex)
        if a.json:
            os.makedirs(os.path.dirname(os.path.abspath(a.json)), exist_ok=True)
            with open(a.json, "w") as f:
                json.dump(res, f, indent=1)
        ok = all(v == "PASS" for v in res["rows"].values())
        if m13:
            return 0 if all(v == "FAIL" for v in res["rows"].values()) else 1
        return 0 if ok else 1
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main())
