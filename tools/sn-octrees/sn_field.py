"""Python reference of Subnautica's terrain field (CONTRACT 3.1 "Content mapping"; the oracle side of S4).

The C# twin is src/DeepEngineers.Terrain (SnField, TileSampler); the two share no code, only these rules:
 samples s(v) = +1 for a far solid voxel, -1 for a far empty voxel or no data (missing batch file, empty octree,
 outside the SN world), (density - 126) / 126 for a near-surface voxel;
 position P(v) = (-2048, -3040, -2048) + v + SAMPLE_OFFSET (the voxel centre, HYPOTHESIS; S4.A-T2 decides);
 field D(p) = trilinear interpolation of s over the 8 samples around p (lerp(a, b, t) = a + (b - a) t; x, y, z);
 "all far and one sign" when the 8 samples are far and share a sign (then D = +-1);
 content SE byte = (byte)((d * 0.5f + 0.5f) * 255f) in float32, d = lod_distance(D, same, lod) =
 +-1 when same, else clamp(D / 2^lod, -1, 1).

Batches come from tools/sn-octrees/sn_octree_batch.py (rasterize_at0), read in place from the user's install.

API (used by tests/s4/at0_compare.py and the S4 analyzer):
 Field(root).d(x, y, z) -> (d, all_far_same_sign) SN world metres
 Field(root).d_many(points) -> (d array, same array)
 Field(root).column_crossings(x, z, y_top, y_bottom, step=0.05) -> descending crossing ys (bisection-refined)
 Field(root).column_surfaces(...) -> [(y, "top"|"bottom")] (top = empty above, solid below)
 content_byte(d), lod_distance(d, same, lod)

Selftest: python -I tools/sn-octrees/sn_field.py --selftest
"""
import collections
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
import sn_octree_batch as ob  # noqa: E402

SAMPLE_OFFSET = 0.5
ORIGIN = (-2048.0, -3040.0, -2048.0)
N = 160
VOXELS = (4096, 3200, 4096)
BATCHES = (26, 20, 26)

def default_root():
    sub = os.environ.get("DEEPENG_SUBNAUTICA") or r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica"
    return os.path.join(sub, "Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "Build18", "CompiledOctreesCache")

def content_byte(d):
    """SE content byte of a distance in LOD units (float32 arithmetic, truncation; d = 0 -> 127)."""
    d = np.float32(d)
    if not d > np.float32(-1.0):
        return 0
    if d >= np.float32(1.0):
        return 255
    return int((d * np.float32(0.5) + np.float32(0.5)) * np.float32(255.0))

def content_bytes(d):
    """Vectorised content_byte over a float32 array."""
    d = np.asarray(d, np.float32)
    v = ((d * np.float32(0.5) + np.float32(0.5)) * np.float32(255.0))
    out = np.where(d > np.float32(-1.0), np.where(d >= np.float32(1.0), np.float32(255.0), v), np.float32(0.0))
    return out.astype(np.int64)

def lod_distance(d, same, lod):
    """+-1 when the 8 samples are far and of one sign, else clamp(D / 2^lod, -1, 1) (float32 like the C#)."""
    d = np.float32(d)
    if same:
        return np.float32(1.0) if d >= 0 else np.float32(-1.0)
    v = d if lod == 0 else d / np.float32(1 << lod)
    return np.float32(min(1.0, max(-1.0, float(v))))

def trilinear(s, f):
    """s: (..., 8) samples in order 000, 100, 010, 110, 001, 101, 011, 111 (x fastest); f: (..., 3)."""
    fx, fy, fz = f[..., 0], f[..., 1], f[..., 2]
    c00 = s[..., 0] + (s[..., 1] - s[..., 0]) * fx
    c10 = s[..., 2] + (s[..., 3] - s[..., 2]) * fx
    c01 = s[..., 4] + (s[..., 5] - s[..., 4]) * fx
    c11 = s[..., 6] + (s[..., 7] - s[..., 6]) * fx
    c0 = c00 + (c10 - c00) * fy
    c1 = c01 + (c11 - c01) * fy
    return c0 + (c1 - c0) * fz

CORNERS = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [1, 1, 0], [0, 0, 1], [1, 0, 1], [0, 1, 1], [1, 1, 1]])

class Field:
    """Subnautica's field D over the user's CompiledOctreesCache (read-only, batches rasterised lazily)."""

    def __init__(self, root=None, cache=8, loader=None, mutant=None):
        self.root = root or default_root()
        self.cache = collections.OrderedDict()
        self.cache_max = cache
        self.loader = loader          # loader(bx, by, bz) -> (type, density) uint8 [N,N,N] or None (no data)
        self.mutant = mutant          # "M1a" flips the sign of every sample (selftest of the comparisons)

    def _arrays(self, bx, by, bz):
        key = (bx, by, bz)
        if key in self.cache:
            self.cache.move_to_end(key)
            return self.cache[key]
        val = None
        if 0 <= bx < BATCHES[0] and 0 <= by < BATCHES[1] and 0 <= bz < BATCHES[2]:
            if self.loader is not None:
                val = self.loader(bx, by, bz)
            else:
                path = ob.batch_path(self.root, bx, by, bz)
                if os.path.isfile(path):
                    typ, den, _, _, _, info = ob.rasterize_at0(path, bx, by, bz)
                    val = None if info["bad"] else (typ, den)
        self.cache[key] = val
        if len(self.cache) > self.cache_max:
            self.cache.popitem(last=False)
        return val

    def samples(self, vx, vy, vz):
        """s and far flags of world voxels (int arrays of one shape)."""
        vx, vy, vz = (np.asarray(a, np.int64) for a in (vx, vy, vz))
        s = np.full(vx.shape, -1.0)
        far = np.ones(vx.shape, bool)
        inw = (vx >= 0) & (vx < VOXELS[0]) & (vy >= 0) & (vy < VOXELS[1]) & (vz >= 0) & (vz < VOXELS[2])
        if inw.any():
            bx, by, bz = vx // N, vy // N, vz // N
            keys = np.stack([bx[inw], by[inw], bz[inw]], axis=1)
            uniq = np.unique(keys, axis=0)
            for k in uniq:
                arr = self._arrays(int(k[0]), int(k[1]), int(k[2]))
                if arr is None:
                    continue
                typ, den = arr
                m = inw & (bx == k[0]) & (by == k[1]) & (bz == k[2])
                lx, ly, lz = vx[m] - k[0] * N, vy[m] - k[1] * N, vz[m] - k[2] * N
                t, d = typ[lx, ly, lz], den[lx, ly, lz]
                s[m] = np.where(d == 0, np.where(t > 0, 1.0, -1.0), (d.astype(np.float64) - 126.0) / 126.0)
                far[m] = d == 0
        if self.mutant == "M1a":
            s = -s
        return s, far

    def d_many(self, points):
        """D and the all-far-same-sign flag at SN world points (k, 3)."""
        p = np.asarray(points, np.float64).reshape(-1, 3)
        u = p - np.array(ORIGIN) - SAMPLE_OFFSET
        i0 = np.floor(u)
        f = u - i0
        i0 = i0.astype(np.int64)
        idx = i0[:, None, :] + CORNERS[None, :, :]
        s, far = self.samples(idx[..., 0], idx[..., 1], idx[..., 2])
        d = trilinear(s, f)
        allfar = far.all(axis=1)
        same = allfar & ((s > 0).all(axis=1) | (s <= 0).all(axis=1))
        return d, same

    def d(self, x, y, z):
        d, same = self.d_many([[x, y, z]])
        return float(d[0]), bool(same[0])

    def column_crossings(self, x, z, y_top, y_bottom, step=0.05):
        """SN y of every D = 0 crossing (inside = D >= 0) on the vertical line (x, z), from y_top down to y_bottom."""
        return [y for y, _ in self.column_surfaces(x, z, y_top, y_bottom, step)]

    def column_surfaces(self, x, z, y_top, y_bottom, step=0.05):
        """[(y, "top"|"bottom")]: "top" = empty above, solid below (a seabed seen from above)."""
        if y_bottom > y_top:
            y_top, y_bottom = y_bottom, y_top
        n = max(1, int(math.ceil((y_top - y_bottom) / step)))
        ys = y_top - np.arange(n + 1) * ((y_top - y_bottom) / n)
        # add the sample planes (D is piecewise linear between them) so no crossing pair is missed
        k0 = math.ceil(y_bottom - ORIGIN[1] - SAMPLE_OFFSET)
        k1 = math.floor(y_top - ORIGIN[1] - SAMPLE_OFFSET)
        planes = ORIGIN[1] + SAMPLE_OFFSET + np.arange(k0, k1 + 1, dtype=np.float64)
        ys = np.unique(np.concatenate([ys, planes]))[::-1]
        pts = np.stack([np.full(ys.shape, float(x)), ys, np.full(ys.shape, float(z))], axis=1)
        d, _ = self.d_many(pts)
        inside = d >= 0
        out = []
        for i in np.nonzero(inside[:-1] != inside[1:])[0]:
            hi, lo = float(ys[i]), float(ys[i + 1])
            hi_in = bool(inside[i])
            for _ in range(60):
                mid = 0.5 * (hi + lo)
                if (self.d(x, mid, z)[0] >= 0) == hi_in:
                    hi = mid
                else:
                    lo = mid
                if hi - lo < 1e-9:
                    break
            out.append((0.5 * (hi + lo), "bottom" if hi_in else "top"))
        return out

def selftest():
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        print(("PASS " if cond else "FAIL ") + name + (" " + detail if detail else ""))
        ok = ok and bool(cond)

    check("content_byte(0) == 127", content_byte(0.0) == 127)
    check("content_byte(1) == 255", content_byte(1.0) == 255)
    check("content_byte(-1) == 0", content_byte(-1.0) == 0)
    check("content_byte(1/126) == 128", content_byte(1.0 / 126.0) == 128)
    check("content_bytes vectorised", list(content_bytes([-1.0, 0.0, 1.0 / 126, 1.0])) == [0, 127, 128, 255])
    check("lod_distance clamps", float(lod_distance(3.0, False, 1)) == 1.0 and float(lod_distance(0.5, False, 1)) == 0.25)
    check("lod_distance same", float(lod_distance(0.2, True, 2)) == 1.0 and float(lod_distance(-0.2, True, 0)) == -1.0)

    # a synthetic two-batch world: batch (12, 18, 12) solid below local y = 40, near band of +-1 voxel
    def loader(bx, by, bz):
        if (bx, by, bz) != (12, 18, 12):
            return None
        typ = np.zeros((N, N, N), np.uint8)
        den = np.zeros((N, N, N), np.uint8)
        yc = np.arange(N) + SAMPLE_OFFSET
        dist = 40.25 - yc                         # metres inside
        for j in range(N):
            if abs(dist[j]) < 1.0:
                den[:, j, :] = int(math.floor(dist[j] * 126)) + 126
                typ[:, j, :] = 3
            elif dist[j] > 0:
                typ[:, j, :] = 3
        return typ, den

    fld = Field(root="(synthetic)", loader=loader)
    wx, wz = -128 + 50.3, -128 + 70.7
    surf = -160 + 40.25
    ys = fld.column_crossings(wx, wz, -100.0, -159.0)
    check("one crossing in the synthetic column", len(ys) == 1, str(ys))
    # the encoded densities quantise the distance to 1/126; the crossing lies within that of the plane
    check("crossing within 1/126 m of the plane", len(ys) == 1 and abs(ys[0] - surf) <= 1.0 / 126 + 1e-9,
          f"{ys} vs {surf}")
    surfs = fld.column_surfaces(wx, wz, -100.0, -159.0)
    check("the crossing is a top surface", surfs and surfs[0][1] == "top")
    d, same = fld.d(wx, -159.0 + 30.0, wz)
    check("deep inside is +1, all far", d == 1.0 and same)
    d, same = fld.d(wx, -10.0, wz)
    check("outside the batch is -1 (no data)", d == -1.0 and same)
    m = Field(root="(synthetic)", loader=loader, mutant="M1a")
    check("M1a flips D", m.d(wx, -159.0 + 30.0, wz)[0] == -1.0)
    print("SN_FIELD SELFTEST", "PASS" if ok else "FAIL")
    return 0 if ok else 1

if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--selftest":
        sys.exit(selftest())
    if len(sys.argv) >= 3:
        f = Field()
        x, z = float(sys.argv[1]), float(sys.argv[2])
        print(f.column_surfaces(x, z, 160.0, -1920.0))
    else:
        print(__doc__)
