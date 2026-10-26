"""Where does Subnautica's terrain data stop? Classify the faces of solid octrees that touch a missing batch file.

Input: the per-octree state map written by sn_octree_stats.py (OUT_DIR/octree_state.npy, shape 128 x 100 x 128,
values -1 no batch file, 0 empty, 1 uniform solid, 2 contains surface). It is derived from game files: keep it
outside the repository. This script writes nothing; it prints statistics only.

Why it matters (docs/recon/gap-2.md): Subnautica's chunk mesher starts every chunk from a cleared raster
(VoxelandChunkWorkspace.SetSize -> RasterWorkspace.SetSize clears types and densities to 0) and skips octrees
whose batch file is missing (BatchOctrees.LoadOctrees returns false -> ClearOctrees -> Octree.RasterizeNative does
nothing for an empty array). A missing batch therefore rasterises as empty, and a solid octree next to it gets a
flat face exactly on the batch boundary. These "cut faces" are what Subnautica draws under and around the crater.
An SE "plug" (Subnautica data only, missing = empty) reproduces them; a "fill to floor" does not.

Usage: python -I sn_crust_cuts.py <octree_state.npy> [floor_sn_y] (floor default -3000 = site v1 planet floor, for the fill estimate)
"""
import collections
import sys

import numpy as np

OY, S = -3040, 32

def main(path, floor_y=-3000.0):
    st = np.load(path)
    X, Y, Z = st.shape
    solid = (st == 1) | (st == 2)
    rows = sorted(set((np.nonzero(st != -1)[1] // 5).tolist()))
    print(f"batch rows with files: {rows[0]}..{rows[-1]} -> data y {OY + rows[0] * 160}..{OY + (rows[-1] + 1) * 160}")
    xs, ys, zs = np.nonzero(solid)
    km2 = S * S / 1e6
    for name, (dx, dy, dz) in {"down": (0, -1, 0), "up": (0, 1, 0), "-x": (-1, 0, 0), "+x": (1, 0, 0),
                               "-z": (0, 0, -1), "+z": (0, 0, 1)}.items():
        nx, ny, nz = xs + dx, ys + dy, zs + dz
        inb = (nx >= 0) & (nx < X) & (ny >= 0) & (ny < Y) & (nz >= 0) & (nz < Z)
        nb = np.full(xs.shape, -2)
        nb[inb] = st[nx[inb], ny[inb], nz[inb]]
        m = nb == -1
        if m.any():
            face_y = OY + ys[m] * S + (0 if dy <= 0 else S)
            if dy == 0:
                rng = f"walls spanning y {(OY + ys[m] * S).min()}..{(OY + ys[m] * S).max() + S}"
            else:
                rng = f"faces at y {face_y.min()}..{face_y.max()}"
            print(f"{name:>4}: {m.sum():6d} cut faces ({m.sum() * km2:.2f} km2) against a missing batch, {rng};"
                  f" uniform-solid {int((m & (st[xs, ys, zs] == 1)).sum())}")
    # crust underside per 32 m column
    cls = collections.Counter()
    cut = collections.Counter()
    low = np.full((X, Z), -1)
    for x in range(X):
        for z in range(Z):
            col = np.nonzero(solid[x, :, z])[0]
            if col.size == 0:
                continue
            y0 = int(col.min())
            low[x, z] = y0
            below = st[x, y0 - 1, z] if y0 > 0 else -2
            if below == -1:
                cls["cut (missing batch below)"] += 1
                cut[OY + y0 * S] += 1
            elif below == 0:
                cls["authored underside (empty octree below)"] += 1
            else:
                cls[f"other ({below})"] += 1
    print("column undersides:", dict(cls))
    print("cut depth histogram (y: columns):", dict(sorted(cut.items())))
    # what a fill-to-floor would invent: rock volume, and exposed outer wall area below each column's underside
    terr = low >= 0
    under = np.where(terr, OY + low * S, 0.0)
    vol = float(((under - floor_y) * terr).sum()) * S * S
    wall = 0.0
    wall_shallow = 0.0
    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        nb = np.zeros_like(terr)          # neighbour column is terrain (it would be filled too: no exposed wall)
        sx = slice(max(dx, 0), X + min(dx, 0))
        tx = slice(max(-dx, 0), X + min(-dx, 0))
        sz = slice(max(dz, 0), Z + min(dz, 0))
        tz = slice(max(-dz, 0), Z + min(-dz, 0))
        nb[tx, tz] = terr[sx, sz]
        exposed = terr & ~nb               # terrain column next to a void column: the fill shows a wall there
        wall += float(np.clip(under - floor_y, 0, None)[exposed].sum()) * S
        wall_shallow += float(np.clip(under - max(floor_y, -1700.0), 0, None)[exposed].sum()) * S
    print(f"fill to y {floor_y:.0f}: invented rock {vol / 1e9:.1f} km3; exposed fill walls {wall / 1e6:.2f} km2,"
          f" of which {wall_shallow / 1e6:.2f} km2 above -1700 m (reachable by vanilla-depth vehicles)")

if __name__ == "__main__":
    main(sys.argv[1], float(sys.argv[2]) if len(sys.argv) > 2 else -3000.0)
