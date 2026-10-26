"""Rasterize one Subnautica terrain batch (160 m cube) at 1 m, read-only, and check the distance band.

Independent oracle for the SE plugin's octree sampler (see docs/recon/probe-collision.md, 2.1 and 9; S4.A-T0).
It shares no code with the C# reader (src/DeepEngineers.Terrain), only the format facts of CONTRACT 3.1.
Semantics used:
 node = [type u8, density u8, firstChild u16 LE]; firstChild == 0 -> leaf (the walk also stops at size 1);
 children ordered x*4+y*2+z; density 0 = 'far' voxel (solid iff type > 0); density != 0 = near-surface signed
 distance (density - 126) / 126 in voxels, positive inside, solid iff density >= 126.

Usage:
 python -I sn_octree_batch.py "<...>/CompiledOctreesCache" bx by bz [OUT_DIR]
 Prints leaf sizes, surface heights, overhang columns, SE 8 m cells crossing the surface and how many
 solid/empty crossings carry sub-voxel distance information. With OUT_DIR (outside the repo) it also saves the
 boolean solid grid for later comparisons.
 python -I sn_octree_batch.py "<...>/CompiledOctreesCache" bx by bz --at0 OUT_DIR
 S4.A-T0 export (OUT_DIR outside the repository; default of the test runners:
 %LOCALAPPDATA%\\DeepEngineers\\s4\\at0). Writes, per batch:
 at0-<bx>-<by>-<bz>.bin 48-byte little-endian header, then type[n^3], density[n^3], leafSize[n^3] (u8 each,
 index (x*n + y)*n + z; no data = 0, 0, 0). Header: magic b"DEAT0\\0\\0\\0", u32 version 1,
 i32 bx, i32 by, i32 bz, u32 n = 160, u32 flags (bit 0 file present, bit 1 bad),
 u64 file bytes, u32 sample offset in millimetres (500), u32 0.
 at0-<bx>-<by>-<bz>.json the summary (keys of SnBatchSummary plus format, version, iso, oracle, oracleSha256).
 at0-<bx>-<by>-<bz>-iso.bin float32 x, y, z triplets (batch-local SN metres, sample k at k + 0.5) of the D = 0
 crossings on the sample-grid edges (s >= 0 is inside; t = s_a / (s_a - s_b)),
 sorted by x, then y, then z.
"""
import collections
import hashlib
import json
import os
import struct
import sys

import numpy as np

WORLD_OCT = (128, 100, 128)
ORIGIN = np.array([-2048, -3040, -2048])
N = 160
SAMPLE_OFFSET = 0.5          # CONTRACT 3.1: P(v) = origin + v + 0.5 (HYPOTHESIS; S4.A-T2 decides)
AT0_MAGIC = b"DEAT0\0\0\0"
AT0_VERSION = 1
MUTANTS = ("M1a", "M1b", "M1c", "M1d")

def rasterize(path, bx, by, bz):
    buf = np.fromfile(path, dtype=np.uint8)
    solid = np.zeros((N, N, N), bool)
    dist = np.full((N, N, N), np.nan, np.float32)
    leaf_sizes, near_sizes = collections.Counter(), collections.Counter()

    def walk(data, nid, x, y, z, size):
        t, d = int(data[nid, 0]), int(data[nid, 1])
        fc = int(data[nid, 2]) | (int(data[nid, 3]) << 8)
        if fc == 0 or size == 1:
            leaf_sizes[size] += 1
            sl = (slice(x, x + size), slice(y, y + size), slice(z, z + size))
            if d == 0:
                solid[sl] = t > 0
            else:
                near_sizes[size] += 1
                dist[sl] = (d - 126) / 126.0
                solid[sl] = d >= 126
            return
        h = size // 2
        for i in range(8):
            walk(data, fc + i, x + ((i >> 2) & 1) * h, y + ((i >> 1) & 1) * h, z + (i & 1) * h, h)

    off = 4
    for ix in range(5):
        for iy in range(5):
            for iz in range(5):
                o = (bx * 5 + ix, by * 5 + iy, bz * 5 + iz)
                if not all(0 <= o[k] < WORLD_OCT[k] for k in range(3)):
                    continue
                n = int(buf[off]) | (int(buf[off + 1]) << 8)
                off += 2
                data = buf[off:off + 4 * n].reshape(-1, 4)
                off += 4 * n
                if n:
                    walk(data, 0, ix * 32, iy * 32, iz * 32, 32)
    assert off == buf.size, "file not consumed exactly"
    return solid, dist, leaf_sizes, near_sizes

# ---------------------------------------------------------------------------------------------------------- S4.A-T0
def batch_path(root, bx, by, bz):
    return os.path.join(root, f"compiled-batch-{bx}-{by}-{bz}.optoctrees")

def _check_counts(buf, bx, by, bz):
    """Octree (offset, count) list in x-outer, y, z-inner order, or a 'bad' reason (version < 4, not consumed exactly)."""
    if buf.size < 4:
        return None, f"short file ({buf.size} bytes)"
    ver = int(buf[:4].view(np.int32)[0])
    if ver < 4:
        return None, f"version {ver} < 4"
    off, out = 4, []
    for ix in range(5):
        for iy in range(5):
            for iz in range(5):
                o = (bx * 5 + ix, by * 5 + iy, bz * 5 + iz)
                if not all(0 <= o[k] < WORLD_OCT[k] for k in range(3)):
                    continue                     # out-of-world octrees are not stored
                if off + 2 > buf.size:
                    return None, "truncated"
                n = int(buf[off]) | (int(buf[off + 1]) << 8)
                off += 2
                if off + 4 * n > buf.size:
                    return None, "truncated nodes"
                out.append(((ix, iy, iz), off, n))
                off += 4 * n
    if off != buf.size:
        return None, f"not consumed exactly ({off} of {buf.size} bytes)"
    return out, None

def rasterize_at0(path, bx, by, bz, mutant=None):
    """type, density and leaf-size rasters (uint8 [x][y][z]) of one batch, the leaf statistics and the file facts.

 mutant (selftest only): "M1b" shifts the rasters one voxel along x (voxel x reads x + 1), "M1c" swaps x and z,
 "M1d" uses the child order z*4 + y*2 + x. "M1a" (sign flip) does not touch the rasters (see sample_values).
 """
    typ = np.zeros((N, N, N), np.uint8)
    den = np.zeros((N, N, N), np.uint8)
    leaf = np.zeros((N, N, N), np.uint8)
    leaf_sizes, near_sizes = collections.Counter(), collections.Counter()
    present = os.path.isfile(path)
    info = {"file_present": present, "file_bytes": os.path.getsize(path) if present else 0, "bad": None}
    if present:
        buf = np.fromfile(path, dtype=np.uint8)
        octs, bad = _check_counts(buf, bx, by, bz)
        info["bad"] = bad
        if bad is None:
            def walk(data, n, nid, x, y, z, size):
                t, d = int(data[nid, 0]), int(data[nid, 1])
                fc = int(data[nid, 2]) | (int(data[nid, 3]) << 8)
                if fc == 0 or size == 1:
                    leaf_sizes[size] += 1
                    if d != 0:
                        near_sizes[size] += 1
                    sl = (slice(x, x + size), slice(y, y + size), slice(z, z + size))
                    typ[sl] = t
                    den[sl] = d
                    leaf[sl] = size
                    return
                h = size // 2
                for i in range(8):
                    if mutant == "M1d":
                        cx, cy, cz = i & 1, (i >> 1) & 1, (i >> 2) & 1
                    else:
                        cx, cy, cz = (i >> 2) & 1, (i >> 1) & 1, i & 1
                    walk(data, n, fc + i, x + cx * h, y + cy * h, z + cz * h, h)

            for (ix, iy, iz), off, n in octs:
                if n:
                    walk(buf[off:off + 4 * n].reshape(-1, 4), n, 0, ix * 32, iy * 32, iz * 32, 32)
    if mutant == "M1b":
        for a in (typ, den, leaf):
            a[:-1] = a[1:].copy()
            a[-1] = 0
    elif mutant == "M1c":
        typ, den, leaf = (np.ascontiguousarray(a.transpose(2, 1, 0)) for a in (typ, den, leaf))
    return typ, den, leaf, leaf_sizes, near_sizes, info

def solid_of(typ, den):
    return np.where(den == 0, typ > 0, den >= 126)

def sample_values(typ, den, mutant=None):
    """s(v): far +1 solid / -1 empty (no data = -1), near (d - 126) / 126; float64. Mutant M1a negates it."""
    s = np.where(den == 0, np.where(typ > 0, 1.0, -1.0), (den.astype(np.float64) - 126.0) / 126.0)
    return -s if mutant == "M1a" else s

def crossings(typ, den):
    solid = solid_of(typ, den)
    far = den == 0
    sgn = np.sign(den.astype(np.int16) - 126)
    tot = hard = both = consistent = 0
    for ax in range(3):
        a = [slice(None)] * 3
        b = [slice(None)] * 3
        a[ax], b[ax] = slice(1, None), slice(None, -1)
        a, b = tuple(a), tuple(b)
        tr = solid[a] != solid[b]
        tot += int(tr.sum())
        hard += int((tr & far[a] & far[b]).sum())
        bn = tr & ~far[a] & ~far[b]
        both += int(bn.sum())
        consistent += int((bn & (sgn[a] != sgn[b])).sum())
    return {"total": tot, "noDistance": hard, "bothDistance": both, "signConsistent": consistent}

def iso_points(s):
    """D = 0 crossings on the sample-grid edges (batch-local SN metres), float32 (k, 3), sorted by x, y, z."""
    pts = []
    for ax in range(3):
        a = [slice(None)] * 3
        b = [slice(None)] * 3
        a[ax], b[ax] = slice(None, -1), slice(1, None)
        sa, sb = s[tuple(a)], s[tuple(b)]
        m = (sa >= 0) != (sb >= 0)
        idx = np.nonzero(m)
        t = sa[m] / (sa[m] - sb[m])
        p = np.stack([i.astype(np.float64) + SAMPLE_OFFSET for i in idx], axis=1)
        p[:, ax] += t
        pts.append(p)
    p = np.concatenate(pts).astype(np.float32) if pts else np.zeros((0, 3), np.float32)
    order = np.lexsort((p[:, 2], p[:, 1], p[:, 0]))
    return np.ascontiguousarray(p[order])

def sha256_hex(a):
    return hashlib.sha256(np.ascontiguousarray(a).tobytes()).hexdigest()

def summary_at0(bx, by, bz, typ, den, leaf, leaf_sizes, near_sizes, info):
    return {
        "batch": [bx, by, bz],
        "worldMin": (ORIGIN + np.array([bx, by, bz]) * N).tolist(),
        "filePresent": bool(info["file_present"]),
        "fileBytes": int(info["file_bytes"]),
        "bad": info["bad"] is not None,
        "leafSizes": {str(k): int(v) for k, v in sorted(leaf_sizes.items())},
        "nearSizes": {str(k): int(v) for k, v in sorted(near_sizes.items())},
        "solid": int(solid_of(typ, den).sum()),
        "near": int((den != 0).sum()),
        "crossings": crossings(typ, den),
        "typeSha256": sha256_hex(typ),
        "densitySha256": sha256_hex(den),
        "leafSizeSha256": sha256_hex(leaf),
    }

def oracle_sha256():
    with open(os.path.abspath(__file__), "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()

def refuse_inside_repo(out):
    repo = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    o = os.path.abspath(out)
    if o == repo or o.startswith(repo + os.sep):
        sys.exit("refusing to write derived game data inside the repository")

def write_at0(root, bx, by, bz, out):
    """Exports one batch for S4.A-T0 (see the module docstring); returns the summary dict."""
    refuse_inside_repo(out)
    os.makedirs(out, exist_ok=True)
    typ, den, leaf, leaf_sizes, near_sizes, info = rasterize_at0(batch_path(root, bx, by, bz), bx, by, bz)
    summ = summary_at0(bx, by, bz, typ, den, leaf, leaf_sizes, near_sizes, info)
    stem = os.path.join(out, f"at0-{bx}-{by}-{bz}")
    flags = (1 if info["file_present"] else 0) | (2 if info["bad"] else 0)
    header = AT0_MAGIC + struct.pack("<IiiiIIQII", AT0_VERSION, bx, by, bz, N, flags, int(info["file_bytes"]),
                                     int(round(SAMPLE_OFFSET * 1000)), 0)
    assert len(header) == 48
    with open(stem + ".bin.tmp", "wb") as f:
        f.write(header)
        f.write(typ.tobytes())
        f.write(den.tobytes())
        f.write(leaf.tobytes())
    os.replace(stem + ".bin.tmp", stem + ".bin")
    iso = iso_points(sample_values(typ, den))
    with open(stem + "-iso.bin.tmp", "wb") as f:
        f.write(iso.astype("<f4").tobytes())
    os.replace(stem + "-iso.bin.tmp", stem + "-iso.bin")
    doc = {"format": "DEAT0", "version": AT0_VERSION}
    doc.update(summ)
    doc["iso"] = {"count": int(iso.shape[0]), "file": os.path.basename(stem + "-iso.bin")}
    doc["oracle"] = "tools/sn-octrees/sn_octree_batch.py"
    doc["oracleSha256"] = oracle_sha256()
    if info["bad"]:
        doc["badReason"] = info["bad"]
    with open(stem + ".json.tmp", "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)
    os.replace(stem + ".json.tmp", stem + ".json")
    return doc

def read_at0_bin(path):
    """(header dict, type, density, leaf) of an at0-*.bin file."""
    with open(path, "rb") as f:
        raw = f.read()
    if raw[:8] != AT0_MAGIC:
        raise ValueError("not a DEAT0 file: " + path)
    ver, bx, by, bz, n, flags, fbytes, som, _ = struct.unpack("<IiiiIIQII", raw[8:48])
    k = n * n * n
    body = np.frombuffer(raw, np.uint8, offset=48)
    if body.size != 3 * k:
        raise ValueError("bad DEAT0 size: " + path)
    hdr = {"version": ver, "batch": [bx, by, bz], "n": n, "flags": flags, "fileBytes": fbytes, "sampleOffsetMilli": som}
    return hdr, body[:k].reshape(n, n, n), body[k:2 * k].reshape(n, n, n), body[2 * k:].reshape(n, n, n)

def main():
    if len(sys.argv) >= 7 and sys.argv[5] == "--at0":
        root, bx, by, bz, out = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), sys.argv[6]
        doc = write_at0(root, bx, by, bz, out)
        print(f"at0 {bx}-{by}-{bz}: present={doc['filePresent']} bytes={doc['fileBytes']} solid={doc['solid']} "
              f"near={doc['near']} crossings={doc['crossings']['total']} iso={doc['iso']['count']} -> {out}")
        return
    root, bx, by, bz = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
    solid, dist, leaf_sizes, near_sizes = rasterize(os.path.join(root, f"compiled-batch-{bx}-{by}-{bz}.optoctrees"), bx, by, bz)
    print("batch world min corner (m):", (ORIGIN + np.array([bx, by, bz]) * N).tolist())
    print("leaf sizes:", dict(sorted(leaf_sizes.items())), " near-surface leaf sizes:", dict(sorted(near_sizes.items())))
    has = solid.any(axis=1)
    top = N - 1 - np.argmax(solid[:, ::-1, :], axis=1)
    if has.any():
        wy = ORIGIN[1] + by * N + top[has]
        print("top surface world y min/median/max:", int(wy.min()), float(np.median(wy)), int(wy.max()))
    trans = (solid[:, 1:, :] != solid[:, :-1, :]).sum(axis=1)
    print("columns with more than one surface in this batch:", int((trans > 2).sum()), "of", N * N)
    cells = solid.reshape(20, 8, 20, 8, 20, 8).transpose(0, 2, 4, 1, 3, 5).reshape(20, 20, 20, -1)
    print("SE 8 m geometry cells containing surface:", int((cells.any(-1) & ~cells.all(-1)).sum()), "of 8000")
    far = np.isnan(dist)
    tot = hard = both = consistent = 0
    for ax in range(3):
        a = [slice(None)] * 3
        b = [slice(None)] * 3
        a[ax], b[ax] = slice(1, None), slice(None, -1)
        a, b = tuple(a), tuple(b)
        tr = solid[a] != solid[b]
        tot += int(tr.sum())
        hard += int((tr & far[a] & far[b]).sum())
        bn = tr & ~far[a] & ~far[b]
        both += int(bn.sum())
        consistent += int((bn & (np.sign(dist[a]) != np.sign(dist[b]))).sum())
    print(f"solid/empty crossings {tot}: without distance info on either side {hard}, "
          f"with distance on both sides {both} (sign-consistent {consistent})")
    if len(sys.argv) > 5:
        out = sys.argv[5]
        refuse_inside_repo(out)
        os.makedirs(out, exist_ok=True)
        np.save(os.path.join(out, f"solid_{bx}_{by}_{bz}.npy"), solid)

if __name__ == "__main__":
    main()
