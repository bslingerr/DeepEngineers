"""Read-only statistics over Subnautica's baked terrain (CompiledOctreesCache/*.optoctrees).

Independent Python reader, written from the behaviour described in docs/recon/probe-collision.md
(section 2.1). It is the oracle for the C# octree reader used by the Space Engineers plugin: it shares
no code with it. It reads the user's own install and writes statistics only; never point OUT_DIR inside
the repository (derived data must stay local).

Usage:
 python -I sn_octree_stats.py "<Subnautica>/Subnautica_Data/StreamingAssets/SNUnmanagedData/Build18/CompiledOctreesCache" <OUT_DIR>
Writes OUT_DIR/octree_stats.json and OUT_DIR/octree_state.npy (per 32 m octree: -1 no file, 0 empty,
1 uniform solid, 2 contains surface). Exit code 1 if any file is not consumed exactly.
"""
import json
import os
import re
import sys

import numpy as np

WORLD_OCT = (128, 100, 128)   # octrees of 32 voxels: 4096 x 3200 x 4096 (index.txt)
OCT = 32
BATCH = 5                     # octrees per batch side (160 m)
ORIGIN = (-2048, -3040, -2048)
PAT = re.compile(r"compiled-batch-(\d+)-(\d+)-(\d+)\.optoctrees$")

def main(root, out_dir):
    repo = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    if os.path.abspath(out_dir).startswith(repo):
        sys.exit("refusing to write derived game data inside the repository")
    os.makedirs(out_dir, exist_ok=True)
    state = np.full(WORLD_OCT, -1, dtype=np.int8)
    nodes = np.zeros(WORLD_OCT, dtype=np.int32)
    near = np.zeros(WORLD_OCT, dtype=np.int32)
    versions, total_bytes, bad = {}, 0, []
    for fn in sorted(os.listdir(root)):
        m = PAT.match(fn)
        if not m:
            continue
        bx, by, bz = map(int, m.groups())
        buf = np.fromfile(os.path.join(root, fn), dtype=np.uint8)
        total_bytes += buf.size
        ver = int(buf[:4].view(np.int32)[0])
        versions[ver] = versions.get(ver, 0) + 1
        off = 4
        for ix in range(BATCH):          # x outer, then y, z inner (Int3.Range order)
            for iy in range(BATCH):
                for iz in range(BATCH):
                    o = (bx * BATCH + ix, by * BATCH + iy, bz * BATCH + iz)
                    if not all(0 <= o[k] < WORLD_OCT[k] for k in range(3)):
                        continue         # out-of-world octrees are not stored
                    n = int(buf[off]) | (int(buf[off + 1]) << 8)
                    off += 2
                    data = buf[off:off + 4 * n].reshape(-1, 4)
                    off += 4 * n
                    nodes[o] = n
                    if n == 0:
                        state[o] = 0
                        continue
                    t, d = data[:, 0], data[:, 1]
                    leaf = (data[:, 2] == 0) & (data[:, 3] == 0)
                    if n == 1:
                        inside = (t[0] > 0) if d[0] == 0 else (d[0] >= 126)
                        state[o] = 1 if (inside and d[0] == 0) else (0 if d[0] == 0 else 2)
                    else:
                        state[o] = 2
                    near[o] = int((leaf & (d != 0)).sum())
        if off != buf.size:
            bad.append(fn)
    mixed = state == 2
    ys = np.where(mixed.any(axis=(0, 2)))[0]
    terr = ((state == 1) | (state == 2))
    low = [int(np.where(terr[x, :, z])[0].min()) for x in range(WORLD_OCT[0]) for z in range(WORLD_OCT[2]) if terr[x, :, z].any()]
    res = {
        "files": sum(versions.values()), "versions": versions, "total_bytes": total_bytes,
        "files_not_consumed_exactly": bad,
        "octrees_in_world": int(np.prod(WORLD_OCT)), "octrees_without_file": int((state == -1).sum()),
        "octrees_uniform_empty": int((state == 0).sum()), "octrees_uniform_solid": int((state == 1).sum()),
        "octrees_with_surface": int(mixed.sum()), "total_nodes": int(nodes.sum()), "max_nodes_one_octree": int(nodes.max()),
        "near_surface_leaves": int(near.sum()),
        "surface_world_y_range_m": [ORIGIN[1] + int(ys.min()) * OCT, ORIGIN[1] + (int(ys.max()) + 1) * OCT],
        "columns_with_terrain": len(low), "columns_total": WORLD_OCT[0] * WORLD_OCT[2],
        "lowest_solid_world_y_range_m": [ORIGIN[1] + min(low) * OCT, ORIGIN[1] + max(low) * OCT],
    }
    np.save(os.path.join(out_dir, "octree_state.npy"), state)
    with open(os.path.join(out_dir, "octree_stats.json"), "w") as f:
        json.dump(res, f, indent=1)
    print(json.dumps(res, indent=1))
    return 1 if bad else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
