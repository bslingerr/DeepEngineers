"""DeepEngineers world map: independent reference implementation (double precision, stdlib only).

Maps Space Engineers (SE) world positions (VRage, right-handed, Y up, metres, doubles, planet-centred
sphere) to Subnautica (SN) world positions (Unity, left-handed, Y up, metres, floats, flat 4096 m tile)
and back. This file is the oracle for the C# implementations on both sides: it shares no code with them.

Map ("sea-level azimuthal equidistant + altitude", see docs/recon/probe-world-mapping.md):
 SN y = |P - C| - Rs (altitude above the Water Mod sea sphere)
 SN horizontal = Rs * theta along the azimuth (theta = angle between U0 and P - C)
 then yaw psi about +Y and offset O (SN coordinates of the anchor).
The site's anchor frame (E0, U0, N0) must be orthonormal and right-handed as an ENU triple:
E0 x N0 = U0 (VRage cross product). SN axes: x = east, y = up, z = north.

Usage:
 python worldmap_ref.py selftest round trips, frame checks, numbers for the doc
 python worldmap_ref.py se2sn x y z with the example site below
 python worldmap_ref.py sn2se x y z
 python -I worldmap_ref.py heights x z [y_top y_bottom] [--octrees DIR] [--offset 0.5]
 S4 reference: the terrain crossings of the SN column (x, z), read in place
 from Subnautica's octrees (section "S4 reference heights" below)
 python -I worldmap_ref.py s4check [--octrees DIR]
 S4 reference self-check: the column oracle against the independent batch
 rasteriser tools/sn-octrees/sn_octree_batch.py (numpy) on batch 12-18-12,
 an SE column round trip through F, and the CONTRACT 3.1 evidence numbers
 python -I worldmap_ref.py s7asun [--vectors OUT|-]
 S7a sun mapping reference (section "S7a sun mapping reference"):
 self-check, or the vectors S7aSunVectorTests read
 (tests/s7a/vectors/s7a_sun.tsv)
"""
import math
import os
import random
import sys

def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def mul(a, k): return (a[0] * k, a[1] * k, a[2] * k)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a): return math.sqrt(dot(a, a))
def unit(a):
    n = norm(a)
    return (a[0] / n, a[1] / n, a[2] / n)

class Site:
    """One dive site: where the Subnautica tile sits on the SE ocean planet."""

    def __init__(self, C, Rs, U0, N0, psi_deg=0.0, O=(0.0, 0.0, 0.0)):
        self.C = tuple(map(float, C))          # planet centre = MyPlanet.PositionComp.GetPosition
        self.Rs = float(Rs)                    # Water Mod sea radius = MinimumRadius * WaterSettings.Radius
        self.U0 = unit(U0)
        n = sub(N0, mul(self.U0, dot(N0, self.U0)))
        self.N0 = unit(n)
        self.E0 = cross(self.N0, self.U0)      # E = N x U (so E x N = U)
        self.psi = math.radians(psi_deg)       # yaw of SN north relative to N0, positive = clockwise seen from above
        self.O = tuple(map(float, O))          # SN coordinates of the anchor point (default origin)

 # --- yaw helpers in the SN horizontal plane (x east, z north) ---
    def _yaw(self, x, z, sign):
        c, s = math.cos(self.psi), math.sin(sign * self.psi)
        return (c * x + s * z, -s * x + c * z)

    # --- positions ---
    def se_to_sn(self, P):
        v = sub(P, self.C)
        le, lu, ln = dot(v, self.E0), dot(v, self.U0), dot(v, self.N0)
        h = math.hypot(le, ln)
        r = math.sqrt(le * le + lu * lu + ln * ln)
        theta = math.atan2(h, lu)
        rho = self.Rs * theta
        x, z = (rho * le / h, rho * ln / h) if h > 0 else (0.0, 0.0)
        x, z = self._yaw(x, z, +1)
        return (x + self.O[0], r - self.Rs + self.O[1], z + self.O[2])

    def sn_to_se(self, Q):
        x, z = self._yaw(Q[0] - self.O[0], Q[2] - self.O[2], -1)
        y = Q[1] - self.O[1]
        rho = math.hypot(x, z)
        theta = rho / self.Rs
        r = self.Rs + y
        if rho > 0:
            t = add(mul(self.E0, x / rho), mul(self.N0, z / rho))
        else:
            t = (0.0, 0.0, 0.0)
        u = add(mul(self.U0, math.cos(theta)), mul(t, math.sin(theta)))
        return add(self.C, mul(u, r))

    # --- local frame at P: anchor frame transported along the great circle from the anchor ---
    def frame_at(self, P):
        v = sub(P, self.C)
        r = norm(v)
        U = mul(v, 1.0 / r)
        le, ln = dot(v, self.E0), dot(v, self.N0)
        h = math.hypot(le, ln)
        if h == 0:
            return self.E0, U, self.N0
        t = add(mul(self.E0, le / h), mul(self.N0, ln / h))
        theta = math.atan2(h, dot(v, self.U0))
        c, s = math.cos(theta), math.sin(theta)

        def transport(w):  # rotate tangent vector w in the plane (t, U0) by theta
            wt = dot(w, t)
            return add(w, mul(add(mul(t, c - 1.0), mul(self.U0, -s)), wt))
        return transport(self.E0), U, transport(self.N0)

    def dir_se_to_sn(self, P, w):
        E, U, N = self.frame_at(P)
        x, z = self._yaw(dot(w, E), dot(w, N), +1)
        return (x, dot(w, U), z)

    def dir_sn_to_se(self, P, d):
        E, U, N = self.frame_at(P)
        x, z = self._yaw(d[0], d[2], -1)
        return add(add(mul(E, x), mul(U, d[1])), mul(N, z))

    def scale_at(self, P):
        """SN metres per SE metre, horizontally (radial-azimuth direction; transverse differs by theta/sin(theta))."""
        return self.Rs / norm(sub(P, self.C))

# Example site used by the doc's numbers and first test. Planet centre deliberately far from the SE
# origin to exercise doubles. Sun-friendly frame: U0 = +Z, E0 = -X, N0 = -Y (E x N = U). With a world
# BaseSunDirection of +Z, SE's SunRotationAxis becomes +Y, the sun is overhead at the anchor at t = 0 and
# then moves towards +X = -E0 (west), so it rises in the east (MySectorWeatherComponent rotates by
# +2*pi*t/T with Matrix.CreateFromAxisAngle, which turns +Z towards +X about +Y).
EXAMPLE = Site(C=(-1000000.0, 250000.0, 1500000.0), Rs=63500.0, U0=(0, 0, 1), N0=(0, -1, 0))

LANDMARKS_SN = {  # Subnautica world coordinates, VERIFIED from local game data / decompiled constants
    "anchor (SN origin)": (0.0, 0.0, 0.0),
    "floating island signal (signals.csv)": (-832.0, 4.0, -991.0),
    "Sunbeam shoot-down SFX position (VFXSunbeam)": (411.0, 0.0, 1213.0),
    "first live test point: 124 m above Grand Reef CoralArch signal": (-1029.0, -150.0, -1116.0),
    "Mountain Island peak (octree scan)": (346.0, 155.0, 906.0),
    "Primary Containment Facility centre (PrisonManager bounds)": (325.0, -1603.0, -455.0),
    "deepest floor found (octree scan)": (198.0, -1657.0, -518.0),
    "tile corner, 500 m deep": (2048.0, -500.0, 2048.0),
    "void, 6 km east, 200 m deep": (6000.0, -200.0, 0.0),
}

def selftest():
    s = EXAMPLE
    ok = True
    # frame sanity
    assert abs(dot(cross(s.E0, s.N0), s.U0) - 1) < 1e-12
    # 1) round trip SN -> SE -> SN over the whole usable SN domain
    rnd = random.Random(4546)
    worst = 0.0
    for _ in range(200000):
        q = (rnd.uniform(-8192, 8192), rnd.uniform(-3040, 300), rnd.uniform(-8192, 8192))
        q2 = s.se_to_sn(s.sn_to_se(q))
        worst = max(worst, norm(sub(q, q2)))
    print(f"round trip SN->SE->SN worst error: {worst:.3e} m")
    ok &= worst < 1e-6
    # 2) local frame is orthonormal, U = radial, and matches the numerical Jacobian of the map
 # Theory: the map's differential is diag(Rs/r, Rs*theta/(r*sin theta)) in the radial/transverse
 # basis, so the transported frame is off by at most (theta/sin(theta) - 1)/2 rad and the isotropic
 # scale Rs/r is off by at most theta/sin(theta) - 1. Bounds for the tile (|x|,|z| <= 2048).
    th_max = math.hypot(2048, 2048) / s.Rs
    aniso = th_max / math.sin(th_max) - 1
    ang_bound = math.degrees(aniso / 2) * 1.01 + 1e-6
    worst_ang = 0.0
    worst_scale = 0.0
    for _ in range(2000):
        q = (rnd.uniform(-2048, 2048), rnd.uniform(-1920, 155), rnd.uniform(-2048, 2048))
        P = s.sn_to_se(q)
        E, U, N = s.frame_at(P)
        assert abs(dot(E, U)) < 1e-12 and abs(dot(E, N)) < 1e-12 and abs(dot(U, N)) < 1e-12
        eps = 0.01
        for axis, (sn_axis, expect_scale) in (("E", ((1, 0, 0), True)), ("N", ((0, 0, 1), True)), ("U", ((0, 1, 0), False))):
            w = {"E": E, "N": N, "U": U}[axis]
            q1 = s.se_to_sn(add(P, mul(w, eps)))
            d = mul(sub(q1, q), 1 / eps)
            dn = unit(d)
            ang = math.degrees(math.acos(max(-1.0, min(1.0, dot(dn, sn_axis)))))
            worst_ang = max(worst_ang, ang)
            if expect_scale:
                worst_scale = max(worst_scale, abs(norm(d) - s.scale_at(P)) / s.scale_at(P))
    print(f"frame vs numerical Jacobian (tile): worst axis misalignment {worst_ang:.4f} deg (bound {ang_bound:.4f}), "
          f"worst relative |scale - Rs/r| {worst_scale:.2e} (bound {aniso:.2e})")
    ok &= worst_ang <= ang_bound and worst_scale <= aniso * 1.01 + 1e-9
    # 2b) yawed + offset site and direction round trips
    s2 = Site(C=(123456.5, -98765.25, 4321.0), Rs=60000.0, U0=(0.3, 0.8, -0.52), N0=(0, 0, 1), psi_deg=30.0, O=(100.0, 0.0, -50.0))
    worst2 = worst_dir = 0.0
    for _ in range(50000):
        q = (rnd.uniform(-8192, 8192), rnd.uniform(-3040, 300), rnd.uniform(-8192, 8192))
        P = s2.sn_to_se(q)
        worst2 = max(worst2, norm(sub(q, s2.se_to_sn(P))))
        d = unit((rnd.uniform(-1, 1), rnd.uniform(-1, 1), rnd.uniform(-1, 1)))
        worst_dir = max(worst_dir, norm(sub(d, s2.dir_se_to_sn(P, s2.dir_sn_to_se(P, d)))))
    print(f"yawed site: position round trip {worst2:.3e} m, direction round trip {worst_dir:.3e}")
    ok &= worst2 < 1e-6 and worst_dir < 1e-12
    # 3) depth is exact: SE Water Mod depth (r - Rs) equals SN y everywhere
    for name, q in LANDMARKS_SN.items():
        P = s.sn_to_se(q)
        r = norm(sub(P, s.C))
        assert abs((r - s.Rs) - q[1]) < 1e-6
    # 4) landmark table for the doc
    print("\nExample site landmarks (SN -> SE world, metres):")
    for name, q in LANDMARKS_SN.items():
        P = s.sn_to_se(q)
        print(f"  {name:58s} SN {q!s:28s} -> SE ({P[0]:.3f}, {P[1]:.3f}, {P[2]:.3f})  scale {s.scale_at(P):.5f}")
    # 5) float32 transport error of SN coordinates (what the bridge sends)
    import struct
    worst_f = 0.0
    for _ in range(100000):
        q = (rnd.uniform(-8192, 8192), rnd.uniform(-3040, 300), rnd.uniform(-8192, 8192))
        qf = tuple(struct.unpack("f", struct.pack("f", c))[0] for c in q)
        worst_f = max(worst_f, norm(sub(q, qf)))
    print(f"\nfloat32 quantisation of SN coordinates within +-8192 m: worst {worst_f*1000:.3f} mm")
    print("\nSELFTEST", "PASS" if ok else "FAIL")
    return ok

def numbers():
    print("\nCurvature: drop below the tangent plane and gravity tilt at horizontal distance d")
    print(f"{'R km':>7} {'d m':>6} {'drop m (exact)':>15} {'d^2/2R':>8} {'tilt deg':>9}")
    for R in (60000.0, 63500.0, 120000.0, 1000000.0):
        for d in (500.0, 1000.0, 2000.0, 2048.0, 2896.3):
            th = d / R
            drop = R * (1 - math.cos(th))
            print(f"{R/1000:7.1f} {d:6.0f} {drop:15.3f} {d*d/(2*R):8.3f} {math.degrees(th):9.3f}")
    print("\nHorizontal scale SN/SE = Rs/(Rs - h) at depth h, and rigid-draw mismatch at the end of a hull of length L")
    print(f"{'R km':>7} {'h m':>6} {'scale-1':>9} {'L=50 m':>8} {'L=100 m':>8} {'L=200 m':>8} | affine residual L=200: (L/2)^2/2R")
    for R in (60000.0, 63500.0, 120000.0):
        for h in (0.0, 200.0, 500.0, 1000.0, 1657.0, 1920.0):
            k = R / (R - h) - 1
            print(f"{R/1000:7.1f} {h:6.0f} {k:9.5f} {25*k:8.3f} {50*k:8.3f} {100*k:8.3f} | {100*100/(2*R):.3f} m")
    print("\nTransverse anisotropy theta/sin(theta) - 1 at the tile corner (d = 2896 m)")
    for R in (60000.0, 63500.0, 120000.0):
        th = 2896.3 / R
        print(f"  R {R/1000:.1f} km: {th/math.sin(th)-1:.2e}")

# =====================================================================================================================
# S4 reference heights (ROADMAP S4: A-T1, A-T2, N1, WM-6; tests/s4/analyze_s4.py; CONTRACT 3.1 "Content mapping")
# =====================================================================================================================
# The SE terrain tiles serve Subnautica's own octrees (CONTRACT 3.1). Their reference surface is the zero set of
# D(p), the trilinear interpolation of the samples s(v) over the 8 voxel samples around the SN point p, with the sample
# of voxel v at P(v) = SN_ORIGIN + v + SAMPLE_OFFSET (HYPOTHESIS: the voxel centre; S4.A-T2 decides). s(v) = +1 for a far
# solid voxel, -1 for a far empty voxel or no data (missing batch, empty octree, outside the SN world), and
# (density - 126) / 126 for a near-surface voxel (positive inside).
#
# A vertical SE ray through the column (x, z) is radial (both ends F^-1(x, y, z) share the azimuth), so it maps to the
# SN vertical line at (x, z): its reference hit is the first "entry" crossing (empty above, solid below) of D along that
# line. With x and z fixed, trilinear interpolation is linear in y between two consecutive sample planes
# y = SN_ORIGIN.y + v + SAMPLE_OFFSET, so the crossings below are exact roots of D (no step search), and the reader skips
# whole octree leaves (one constant sample per leaf), which keeps 5,000 columns at a few seconds in pure Python.
#
# OctreeColumns is a stdlib reader written from the format facts of CONTRACT 3.1 ("Octree format facts"): it shares no
# code with the C# reader (src/DeepEngineers.Terrain) nor with the other Python readers; `s4check` compares it with
# tools/sn-octrees/sn_octree_batch.py voxel for voxel. Files are opened read-only, memory-mapped, never copied.

SN_ORIGIN = (-2048.0, -3040.0, -2048.0)
SN_VOXELS = (4096, 3200, 4096)
SN_OCTREES = (128, 100, 128)
SAMPLE_OFFSET = 0.5          # CONTRACT 3.1: SnField.SampleOffset (C#) = SAMPLE_OFFSET (sn_field.py) = this
NEAR_ZERO = 126
OCTREE_SUBPATH = os.path.join("Subnautica_Data", "StreamingAssets", "SNUnmanagedData", "Build18", "CompiledOctreesCache")
STEAM_SUBNAUTICA = r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica"
_NEG_INF_V = -(1 << 30)

def default_octree_dir(subnautica_dir=None):
    """CONTRACT 3.1 "Source manifest" folder: the given dir, else DEEPENG_SUBNAUTICA, else Steam's default path."""
    base = subnautica_dir or os.environ.get("DEEPENG_SUBNAUTICA") or STEAM_SUBNAUTICA
    return os.path.join(base, OCTREE_SUBPATH)

class OctreeColumns:
    """Read-only point and column sampler over Subnautica's CompiledOctreesCache (stdlib, lazily memory-mapped).

 sample(vx, vy, vz) -> (s, far, lo): the sample of world voxel v, whether it is a far voxel, and the lowest voxel y of
 the run (leaf, empty octree, missing batch or out-of-world span) that holds the same sample below vy in this voxel
 column. d(x, y, z) -> (D, allFarSameSign). crossings(x, z, y_top, y_bottom) -> [(y, kind)] descending, kind +1 = entry
 (empty above, solid below: what a downward ray hits), -1 = exit (solid above)."""

    def __init__(self, root=None, sample_offset=SAMPLE_OFFSET, max_open=192):
        import mmap  # noqa: F401 (fail early on an odd interpreter)
        self.root = root or default_octree_dir()
        self.o = float(sample_offset)
        self.max_open = max_open
        self._batches = {}          # (bx, by, bz) -> None (missing/bad) | (mmap, file, table)
        self._order = []
        self.stats = {"batchesOpened": 0, "batchesMissing": 0, "batchesBad": [], "descents": 0}
        if not os.path.isdir(self.root):
            raise FileNotFoundError("octree folder not found: %s" % self.root)

    def close(self):
        for v in self._batches.values():
            if v:
                try:
                    v[0].close()
                    v[1].close()
                except (OSError, ValueError):
                    pass
        self._batches.clear()
        self._order = []

    # ------------------------------------------------------------------------------------------------- batches
    def _batch(self, key):
        got = self._batches.get(key, 0)
        if got != 0:
            return got
        import mmap
        bx, by, bz = key
        path = os.path.join(self.root, "compiled-batch-%d-%d-%d.optoctrees" % (bx, by, bz))
        val = None
        if os.path.exists(path):
            f = open(path, "rb")
            try:
                mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            except ValueError:          # empty file
                f.close()
                mm = None
            if mm is not None:
                table = self._index(mm, bx, by, bz)
                if table is None:
                    self.stats["batchesBad"].append(os.path.basename(path))
                    mm.close()
                    f.close()
                else:
                    val = (mm, f, table)
                    self.stats["batchesOpened"] += 1
        else:
            self.stats["batchesMissing"] += 1
        self._batches[key] = val
        self._order.append(key)
        if len(self._order) > self.max_open:
            old = self._order.pop(0)
            v = self._batches.pop(old, None)
            if v:
                v[0].close()
                v[1].close()
        return val

    @staticmethod
    def _index(mm, bx, by, bz):
        """125 entries (index ix*25 + iy*5 + iz) of (node offset, node count) or None (out of world); None for the
 whole batch when the version is < 4 or the counts do not consume the file exactly (Subnautica's rule)."""
        size = len(mm)
        if size < 4 or int.from_bytes(mm[0:4], "little", signed=True) < 4:
            return None
        off = 4
        table = [None] * 125
        for ix in range(5):                 # x outer, then y, z inner (Int3.Range order)
            for iy in range(5):
                for iz in range(5):
                    ox, oy, oz = bx * 5 + ix, by * 5 + iy, bz * 5 + iz
                    if not (ox < SN_OCTREES[0] and oy < SN_OCTREES[1] and oz < SN_OCTREES[2]):
                        continue
                    if off + 2 > size:
                        return None
                    n = mm[off] | (mm[off + 1] << 8)
                    off += 2
                    table[ix * 25 + iy * 5 + iz] = (off, n)
                    off += 4 * n
        return table if off == size else None

    # ------------------------------------------------------------------------------------------------- samples
    def sample(self, vx, vy, vz):
        """(s, far, lo) for world voxel v (see the class docstring)."""
        if not (0 <= vx < SN_VOXELS[0] and 0 <= vz < SN_VOXELS[2]) or vy < 0:
            return -1.0, True, _NEG_INF_V
        if vy >= SN_VOXELS[1]:
            return -1.0, True, SN_VOXELS[1]
        ox, oy, oz = vx >> 5, vy >> 5, vz >> 5
        b = self._batch((ox // 5, oy // 5, oz // 5))
        if b is None:
            return -1.0, True, (oy // 5) * 160
        mm, _f, table = b
        ent = table[(ox % 5) * 25 + (oy % 5) * 5 + (oz % 5)]
        if ent is None or ent[1] == 0:
            return -1.0, True, oy << 5
        base = ent[0]
        lx, ly, lz = vx & 31, vy & 31, vz & 31
        nid, size, x0, y0, z0 = 0, 32, 0, 0, 0
        self.stats["descents"] += 1
        while True:
            p = base + 4 * nid
            t, d, fc = mm[p], mm[p + 1], mm[p + 2] | (mm[p + 3] << 8)
            if fc == 0 or size == 1:
                break
            h = size >> 1
            xb, yb, zb = lx >= x0 + h, ly >= y0 + h, lz >= z0 + h
            nid = fc + (xb << 2 | yb << 1 | zb)          # child order 4*x + 2*y + z (CONTRACT 3.1)
            x0, y0, z0 = x0 + h * xb, y0 + h * yb, z0 + h * zb
            size = h
        lo = (oy << 5) + y0
        if d == 0:
            return (1.0 if t > 0 else -1.0), True, lo
        return (d - NEAR_ZERO) / float(NEAR_ZERO), False, lo

    def _stencil(self, x, z):
        fx = x - SN_ORIGIN[0] - self.o
        fz = z - SN_ORIGIN[2] - self.o
        vx0, vz0 = math.floor(fx), math.floor(fz)
        tx, tz = fx - vx0, fz - vz0
        cols = ((vx0, vz0, (1 - tx) * (1 - tz)), (vx0 + 1, vz0, tx * (1 - tz)), (vx0, vz0 + 1, (1 - tx) * tz),
                (vx0 + 1, vz0 + 1, tx * tz))
        return [c for c in cols if c[2] != 0.0]

    def plane_y(self, v):
        return SN_ORIGIN[1] + v + self.o

    def d(self, x, y, z):
        """(D(p), all 8 stencil samples far and of one sign) at the SN point p."""
        fy = y - SN_ORIGIN[1] - self.o
        vy0 = math.floor(fy)
        ty = fy - vy0
        acc, signs, allfar = 0.0, set(), True
        for vx, vz, w in self._stencil(x, z):
            for vy, wy in ((vy0, 1 - ty), (vy0 + 1, ty)):
                s, far, _lo = self.sample(vx, vy, vz)
                allfar &= far
                signs.add(s > 0)
                if wy:
                    acc += w * wy * s
        return acc, allfar and len(signs) == 1

    def crossings(self, x, z, y_top, y_bottom):
        """Every zero of D on the SN segment (x, y_top) -> (x, y_bottom), descending: [(y, kind)], kind +1 = entry
 (D <= 0 above, D > 0 below), -1 = exit. Exact for trilinear D (linear in y between sample planes)."""
        if y_top < y_bottom:
            y_top, y_bottom = y_bottom, y_top
        cols = self._stencil(x, z)
        v = math.ceil(y_top - SN_ORIGIN[1] - self.o)
        v_bot = math.floor(y_bottom - SN_ORIGIN[1] - self.o)
        state = [list(self.sample(vx, v, vz)) for vx, vz, _w in cols]
        dcur = sum(w * st[0] for (_vx, _vz, w), st in zip(cols, state))
        out = []
        while True:
            lo = max(st[2] for st in state)
            if lo <= v_bot:
                break
            vn = lo - 1
            for i, (vx, vz, _w) in enumerate(cols):
                if state[i][2] > vn:
                    state[i] = list(self.sample(vx, vn, vz))
            dn = sum(w * st[0] for (_vx, _vz, w), st in zip(cols, state))
            ya = self.plane_y(lo)
            if (dcur > 0) != (dn > 0):
                y = ya - dcur / (dcur - dn)
                if y_bottom <= y <= y_top:
                    out.append((y, 1 if dn > 0 else -1))
            dcur, v = dn, vn
        return out

    def first_entry(self, x, z, y_top, y_bottom):
        for y, kind in self.crossings(x, z, y_top, y_bottom):
            if kind > 0:
                return y
        return None

class SyntheticField:
    """An analytic SN terrain for self-tests (no octrees): surface height h(x, z) = base + sx*x + sz*z, plus optional
 overhang boxes [(x0, x1, z0, z1, ytop, ybottom)] of rock floating above the surface (a cave roof). D = signed vertical
 distance, positive inside. crossings is exact (analytic)."""

    def __init__(self, base=-17.2, sx=0.0, sz=0.0, overhangs=()):
        self.base, self.sx, self.sz = float(base), float(sx), float(sz)
        self.overhangs = list(overhangs)

    def height(self, x, z):
        return self.base + self.sx * x + self.sz * z

    def d(self, x, y, z):
        dd = self.height(x, z) - y
        for x0, x1, z0, z1, yt, yb in self.overhangs:
            if x0 <= x <= x1 and z0 <= z <= z1:
                dd = max(dd, min(yt - y, y - yb))
        return dd, abs(dd) > 2.0

    def crossings(self, x, z, y_top, y_bottom):
        if y_top < y_bottom:
            y_top, y_bottom = y_bottom, y_top
        pts = [(self.height(x, z), 1)]
        for x0, x1, z0, z1, yt, yb in self.overhangs:
            if x0 <= x <= x1 and z0 <= z <= z1 and yb > self.height(x, z):
                pts += [(yt, 1), (yb, -1)]
        return sorted([p for p in pts if y_bottom <= p[0] <= y_top], key=lambda p: -p[0])

    def first_entry(self, x, z, y_top, y_bottom):
        for y, kind in self.crossings(x, z, y_top, y_bottom):
            if kind > 0:
                return y
        return None

def se_column(site, x, z, y_top, y_bottom):
    """The SE world segment of the vertical SN column (x, z) from y_top down to y_bottom (radial: the A-T1 ray)."""
    return site.sn_to_se((x, y_top, z)), site.sn_to_se((x, y_bottom, z))

def column_of_se_point(site, P):
    """(x, y, z) SN of an SE world point: the column (x, z) its reference height is read at, and its own SN y."""
    return site.se_to_sn(P)

def reference_at_se_point(site, P, oracle, y_top, y_bottom):
    """S4 reference for an SE hit point P: maps P through F, reads the crossings of its SN column. Returns
 {"sn": (x, y, z), "crossings": [...], "ref": first entry y or None, "delta": y - ref or None}."""
    q = site.se_to_sn(P)
    cr = oracle.crossings(q[0], q[2], y_top, y_bottom)
    ref = next((y for y, k in cr if k > 0), None)
    return {"sn": q, "crossings": cr, "ref": ref, "delta": (q[1] - ref) if ref is not None else None}

def s4_check(octrees=None):
    """`s4check`: (1) the column oracle's per-voxel samples equal tools/sn-octrees/sn_octree_batch.py's raster of batch
 12-18-12 (6,000 random voxels and 40 whole columns, crossings recomputed from the raster); (2) an SE vertical ray
 through F lands on the same SN column; (3) the CONTRACT 3.1 evidence numbers (Safe Shallows (0, 0) and the grandReef
 spot (-1029, -1116), centre and corner conventions). Returns True when (1) and (2) hold; (3) is printed."""
    ok = True
    oracle = OctreeColumns(octrees)
    print("octrees: %s" % oracle.root)
    here = os.path.dirname(os.path.abspath(__file__))
    sys.path.insert(0, os.path.normpath(os.path.join(here, "..", "sn-octrees")))
    try:
        import numpy as np  # noqa: F401
        import sn_octree_batch as B
    except ImportError as e:
        print("numpy or sn_octree_batch not importable (%s): voxel comparison skipped" % e)
        B = None
    bx, by, bz = 12, 18, 12
    if B is not None:
        path = os.path.join(oracle.root, "compiled-batch-%d-%d-%d.optoctrees" % (bx, by, bz))
        solid, dist, _ls, _ns = B.rasterize(path, bx, by, bz)
        import numpy as np
        ref = np.where(np.isnan(dist), np.where(solid, 1.0, -1.0), dist)
        rnd = random.Random(4546)
        bad = 0
        v0 = (bx * 160, by * 160, bz * 160)
        for _ in range(6000):
            lx, ly, lz = rnd.randrange(160), rnd.randrange(160), rnd.randrange(160)
            s, far, _lo = oracle.sample(v0[0] + lx, v0[1] + ly, v0[2] + lz)
            if abs(s - float(ref[lx, ly, lz])) > 1e-6 or far != bool(np.isnan(dist[lx, ly, lz])):
                bad += 1
        print("voxel samples vs sn_octree_batch.rasterize (batch %d-%d-%d): %d of 6000 differ" % (bx, by, bz, bad))
        ok &= bad == 0
        worst = 0.0
        cnt = 0
        for _ in range(40):
            lx, lz = rnd.randrange(1, 158), rnd.randrange(1, 158)
            x = SN_ORIGIN[0] + v0[0] + lx + rnd.random()
            z = SN_ORIGIN[2] + v0[2] + lz + rnd.random()
            y_top, y_bot = SN_ORIGIN[1] + v0[1] + 150.0, SN_ORIGIN[1] + v0[1] + 10.0
            mine = oracle.crossings(x, z, y_top, y_bot)
            # the same trilinear roots from the raster, plane by plane (no leaf skipping)
            fx, fz = x - SN_ORIGIN[0] - oracle.o - v0[0], z - SN_ORIGIN[2] - oracle.o - v0[2]
            ix, iz = math.floor(fx), math.floor(fz)
            tx, tz = fx - ix, fz - iz
            col = [(1 - tx) * (1 - tz) * ref[ix, j, iz] + tx * (1 - tz) * ref[ix + 1, j, iz] +
                   (1 - tx) * tz * ref[ix, j, iz + 1] + tx * tz * ref[ix + 1, j, iz + 1] for j in range(160)]
            theirs = []
            for j in range(158, 9, -1):
                da, db = col[j], col[j - 1]
                ya = SN_ORIGIN[1] + v0[1] + j + oracle.o
                if (da > 0) != (db > 0):
                    y = ya - da / (da - db)
                    if y_bot <= y <= y_top:
                        theirs.append((y, 1 if db > 0 else -1))
            if len(theirs) != len(mine) or any(k1 != k2 for (_a, k1), (_b, k2) in zip(mine, theirs)):
                worst = float("inf")
            else:
                for (a, _k1), (b2, _k2) in zip(mine, theirs):
                    worst = max(worst, abs(a - b2))
            cnt += len(mine)
        print("column crossings vs the raster (40 columns, %d crossings): worst %.3g m" % (cnt, worst))
        ok &= worst < 1e-6                     # the raster's float32 distances
 # (1b) the core build pass independent reference (tools/sn-octrees/sn_field.py, numpy), when present
    try:
        import sn_field
    except ImportError:
        sn_field = None
    if sn_field is not None and hasattr(sn_field, "Field"):
        f = sn_field.Field(oracle.root)
        rnd2 = random.Random(46)
        worst2, n2, kinds = 0.0, 0, True
        for _ in range(20):
            x, z = rnd2.uniform(-80, 80), rnd2.uniform(-80, 80)
            mine = oracle.crossings(x, z, 20.0, -400.0)
            theirs = f.column_surfaces(x, z, 20.0, -400.0) if hasattr(f, "column_surfaces") else \
                [(y, None) for y in f.column_crossings(x, z, 20.0, -400.0)]
            if len(mine) != len(theirs):
                worst2 = float("inf")
                continue
            for (a, k), (b, kk) in zip(mine, theirs):
                worst2 = max(worst2, abs(a - b))
                kinds &= kk is None or (k > 0) == (kk == "top")
                n2 += 1
        print("column crossings vs tools/sn-octrees/sn_field.py (20 columns, %d crossings): worst %.3g m%s" % (
            n2, worst2, "" if kinds else ", KINDS DIFFER"))
        ok &= worst2 < 1e-6 and kinds
    else:
        print("tools/sn-octrees/sn_field.py not importable: that cross-check skipped")
    # (2) SE ray through F
    import importlib
    sys.path.insert(0, here)
    sv = importlib.import_module("site_v1")
    top, bot = se_column(sv.SITE_V1, 3.25, -7.5, 20.0, -400.0)
    q_top, q_bot = sv.SITE_V1.se_to_sn(top), sv.SITE_V1.se_to_sn(bot)
    drift = max(abs(q_top[0] - 3.25), abs(q_top[2] + 7.5), abs(q_bot[0] - 3.25), abs(q_bot[2] + 7.5))
    mid = sv.SITE_V1.se_to_sn(add(top, mul(sub(bot, top), 0.37)))
    drift = max(drift, abs(mid[0] - 3.25), abs(mid[2] + 7.5))
    print("SE vertical ray through F stays on its SN column: worst %.2e m" % drift)
    ok &= drift < 1e-6
    # (3) evidence numbers (CONTRACT 3.1 "Content mapping")
    for name, (x, z, measured) in (("Safe Shallows (0, 0)", (0.0, 0.0, -17.212)),
                                   ("grandReef (-1029, -1116)", (-1029.0, -1116.0, -235.138))):
        centre = oracle.first_entry(x, z, 20.0, -600.0)
        corner = OctreeColumns(oracle.root, 0.0).first_entry(x, z, 20.0, -600.0)
        print("%-26s centre %s  corner %s  (S3 layer-30 measured %.3f)" % (
            name, "%.3f" % centre if centre is not None else "-", "%.3f" % corner if corner is not None else "-",
            measured))
    print("S4CHECK", "PASS" if ok else "FAIL")
    oracle.close()
    return ok

def _cli_s4(argv):
    def opt(name, default=None):
        if name in argv:
            i = argv.index(name)
            v = argv[i + 1]
            del argv[i:i + 2]
            return v
        return default
    octrees = opt("--octrees")
    off = float(opt("--offset", SAMPLE_OFFSET))
    cmd = argv[0]
    if cmd == "s4check":
        return 0 if s4_check(octrees) else 1
    x, z = float(argv[1]), float(argv[2])
    y_top = float(argv[3]) if len(argv) > 3 else 200.0
    y_bot = float(argv[4]) if len(argv) > 4 else -1950.0
    oracle = OctreeColumns(octrees, off)
    cr = oracle.crossings(x, z, y_top, y_bot)
    print("column (%g, %g) offset %g: %d crossing(s) in [%g, %g]" % (x, z, off, len(cr), y_bot, y_top))
    for y, k in cr:
        print("  %10.4f  %s" % (y, "entry (seabed / top)" if k > 0 else "exit (ceiling / crust underside)"))
    oracle.close()
    return 0

# ----------------------------------------------------------------------------------------------------------------
# S7a sun mapping reference (CONTRACT 5.4 "S7a conventions": the `format pole`, `format sky` and `format time` lines;
# ARCHITECTURE 5.12). Independent of the C# Wire.SunMath it checks (S7aSunVectorTests read the vectors written by
# `s7asun --vectors tests/s7a/vectors/s7a_sun.tsv`). SE side at the camera P: U = unit(P - C), M = unit(U - a(a.U)),
# W = unit(a x U) (VRage cross), H = atan2(s.W, s.M), the pole q = M_sn x W_sn (component cross product of the two
# directions mapped with dir_se_to_sn). Subnautica side: c = frac(H/2pi + 0.5) (GetDayNightCycleTime's scale), the
# day scalar through DayNightCycle's remap and its inverse, uSkyManager.SunDirection / NorthPoleOffset from q, and the
# sun disk SunDir = -(Euler(0, SD, NPO) * Euler(15*Timeline - 90, 0, 0) * forward) (uSkyManager.InitSunAndMoon,
# Unity's Euler order z, x, y).
# ----------------------------------------------------------------------------------------------------------------
S7A_POLE_VALID_MAX_DOT = 0.999
S7A_HOLD_DEG = 2.0            # SunHoldMaxCdeg / 100
S7A_SYNC_EPS_DEG = 1e-6
S7A_JUMP_MIN_DAYS = 0.25
SN_RISE, SN_SET = 0.125, 0.875

def s7a_frac(x):
    return x - math.floor(x)

def s7a_pack_cdeg(deg):
    """i16 centidegrees, rounded half away from zero, clamped to +-32767."""
    if deg != deg:
        return 0
    x = deg * 100.0
    ax = abs(x)
    r = math.floor(ax)
    if ax - r >= 0.5:
        r += 1
    r = int(r) if x >= 0 else -int(r)
    return max(-32767, min(32767, r))

def _clamp1(x):
    return -1.0 if x < -1.0 else (1.0 if x > 1.0 else x)

def se_sky(site, P, s, a):
    """SE's sun at the camera P (s = SunDirectionNormalized, a = SunRotationAxis; both renormalised): a dict with H,
 elev, decl, lat, sunrise (radians), sunSn, q (pole, SN axes), az/el (degrees), azc/elc (centidegrees), valid."""
    s, a = unit(s), unit(a)
    U = unit(sub(P, site.C))
    au = dot(a, U)
    valid = abs(au) <= S7A_POLE_VALID_MAX_DOT
    if valid:
        M = unit(sub(U, mul(a, au)))
        W = unit(cross(a, U))
    else:
        t = (1.0, 0.0, 0.0) if abs(a[0]) < 0.9 else (0.0, 1.0, 0.0)
        M = unit(sub(t, mul(a, dot(a, t))))
        W = unit(cross(a, M))
    H = math.atan2(dot(s, W), dot(s, M))
    decl = math.asin(_clamp1(dot(s, a)))
    lat = math.asin(_clamp1(au))
    sunrise = math.pi / 2 if decl == 0.0 else math.acos(_clamp1(-math.tan(decl) * math.tan(lat)))
    q = cross(site.dir_se_to_sn(P, M), site.dir_se_to_sn(P, W))
    qn = unit(q)
    az, el = math.degrees(math.atan2(qn[0], qn[2])), math.degrees(math.asin(_clamp1(qn[1])))
    return {"H": H, "elev": math.asin(_clamp1(dot(s, U))), "decl": decl, "lat": lat, "sunrise": sunrise,
            "sunSn": site.dir_se_to_sn(P, s), "q": q, "az": az, "el": el, "azc": s7a_pack_cdeg(az),
            "elc": s7a_pack_cdeg(el), "valid": valid}

def pole_from_cdeg(azc, elc):
    az, el = math.radians(azc / 100.0), math.radians(elc / 100.0)
    return (math.cos(el) * math.sin(az), math.sin(el), math.cos(el) * math.cos(az))

def cycle_time(H):
    return s7a_frac(H / (2.0 * math.pi) + 0.5)

def sn_sky(q):
    """(SunDirection, NorthPoleOffset) in degrees: n = unit(up - q(up.q)), r = -(q x n), SD = atan2(-r.x, -r.z),
 NPO = atan2(-n.x cos SD + n.z sin SD, n.y)."""
    q = unit(q)
    n = unit(sub((0.0, 1.0, 0.0), mul(q, q[1])))
    r = mul(cross(q, n), -1.0)
    sd = math.atan2(-r[0], -r[2])
    npo = math.atan2(-n[0] * math.cos(sd) + n[2] * math.sin(sd), n[1])
    return math.degrees(sd), math.degrees(npo)

def _rx(deg, v):
    t = math.radians(deg)
    c, s = math.cos(t), math.sin(t)
    return (v[0], c * v[1] - s * v[2], s * v[1] + c * v[2])

def _ry(deg, v):
    t = math.radians(deg)
    c, s = math.cos(t), math.sin(t)
    return (c * v[0] + s * v[2], v[1], -s * v[0] + c * v[2])

def _rz(deg, v):
    t = math.radians(deg)
    c, s = math.cos(t), math.sin(t)
    return (c * v[0] - s * v[1], s * v[0] + c * v[1], v[2])

def unity_euler(x, y, z, v):
    """Quaternion.Euler(x, y, z) * v: z first, then x, then y (Unity's documented order)."""
    return _ry(y, _rx(x, _rz(z, v)))

def sn_sun_dir(c, sd, npo):
    """uSkyManager.SunDir for cycle time c: -(Euler(0, SD, NPO) * Euler(Timeline*360/24 - 90, 0, 0) * forward)."""
    timeline = c * 24.0
    v = unity_euler(0.0, sd, npo, unity_euler(timeline * 360.0 / 24.0 - 90.0, 0.0, 0.0, (0.0, 0.0, 1.0)))
    return mul(v, -1.0)

def cycle_from_day_scalar(s, rise=SN_RISE, sset=SN_SET):
    """DayNightCycle.GetDayNightCycleTime of a day scalar (doubles)."""
    span = sset - rise
    if rise < s < sset:
        return (s - rise) / span * 0.5 + 0.25
    night = 1.0 - span
    if s < sset:
        s += 1.0
    c = (s - sset) / night * 0.5 + 0.75
    return c - 1.0 if c > 1.0 else c

def day_scalar_from_cycle(c, rise=SN_RISE, sset=SN_SET):
    c = s7a_frac(c)
    span = sset - rise
    if 0.25 <= c <= 0.75:
        return rise + (c - 0.25) * 2.0 * span
    cp = c + 1.0 if c < 0.25 else c
    return s7a_frac(sset + (cp - 0.75) * 2.0 * (1.0 - span))

def s7a_step(s_now, c_now, s_t, c_t, force):
    """(kind, advanceDays): none | hold | advance | jump (CONTRACT `format time`)."""
    ahead = s7a_frac(c_now - c_t) * 360.0
    if ahead <= S7A_SYNC_EPS_DEG or ahead >= 360.0 - S7A_SYNC_EPS_DEG:   # in sync either side (float noise)
        return "none", 0.0
    if ahead <= S7A_HOLD_DEG:
        return "hold", 0.0
    adv = s7a_frac(s_t - s_now)
    if adv <= 0.0:
        return "none", 0.0
    return ("jump" if force or adv > S7A_JUMP_MIN_DAYS else "advance"), adv

def sun_light_elevation_deg(c, max_deg=65.0):
    """uSkyManager.InitSunAndMoon's SunLight elevation with NPO 0: 90 - |angle| (S3.C9's formula)."""
    x = s7a_frac(c)
    t = x * 24.0
    if t < 6.0:
        ang = -max_deg * min(1.0, max(0.0, x * 4.0))
    elif t > 18.0:
        ang = max_deg + (0.0 - max_deg) * min(1.0, max(0.0, (x - 0.75) * 4.0))
    else:
        ang = -max_deg + 2.0 * max_deg * min(1.0, max(0.0, x * 2.0 - 0.5))
    return 90.0 - abs(ang)

def angle_deg(a, b):
    return math.degrees(math.atan2(norm(cross(a, b)), dot(a, b)))

def _rand_unit(rnd):
    while True:
        v = (rnd.uniform(-1, 1), rnd.uniform(-1, 1), rnd.uniform(-1, 1))
        n = norm(v)
        if 0.1 < n <= 1.0:
            return mul(v, 1.0 / n)

def _rand_perp(rnd, a):
    while True:
        v = _rand_unit(rnd)
        p = sub(v, mul(a, dot(a, v)))
        if norm(p) > 0.1:
            return unit(p)

def _site_v1():
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # -I drops the script dir; this is our own folder
    import site_v1  # noqa: E402
    return site_v1.SITE_V1

def _random_site(rnd):
    U0 = _rand_unit(rnd)
    return Site(C=(rnd.uniform(-2e6, 2e6), rnd.uniform(-2e6, 2e6), rnd.uniform(-2e6, 2e6)), Rs=rnd.uniform(30000, 120000),
                U0=U0, N0=_rand_perp(rnd, U0), psi_deg=rnd.uniform(-180, 180),
                O=(rnd.uniform(-500, 500), 0.0, rnd.uniform(-500, 500)))

def _random_cam(rnd, site, rho_max=8000.0):
    r, t = rnd.uniform(0, rho_max), rnd.uniform(0, 2 * math.pi)
    return site.sn_to_se((site.O[0] + r * math.cos(t), rnd.uniform(-2000, 2000), site.O[2] + r * math.sin(t)))

def _chain(site, P, s, a, quantised=False):
    """The whole chain: SE sky at P -> c, (SD, NPO) from the pole (exact or the C6 centidegrees) -> Subnautica's disk."""
    k = se_sky(site, P, s, a)
    q = pole_from_cdeg(k["azc"], k["elc"]) if quantised else k["q"]
    sd, npo = sn_sky(q)
    c = cycle_time(k["H"])
    d = sn_sun_dir(c, sd, npo)
    return k, c, sd, npo, d, angle_deg(d, k["sunSn"])

def s7asun_selftest():
    ok = True
    rnd = random.Random(7071)
    # 1) exact mapping: Subnautica's disk lies along SE's sun direction (declination 0 = DE worlds' |base.axis| < 1e-3)
    worst = worst_q = 0.0
    for _ in range(20000):
        site = _random_site(rnd)
        a = _rand_unit(rnd)
        s = _rand_perp(rnd, a)
        P = _random_cam(rnd, site)
        if not se_sky(site, P, s, a)["valid"]:
            continue
        worst = max(worst, _chain(site, P, s, a)[5])
        worst_q = max(worst_q, _chain(site, P, s, a, quantised=True)[5])
    print("s7asun: disk vs SE sun over 20,000 random sites, axes and times: worst %.3e deg exact, %.4f deg with the "
          "0.01 deg pole (bound 0.02)" % (worst, worst_q))
    ok &= worst < 1e-5 and worst_q <= 0.02
    # 2) the day scalar remap and its inverse; IsDay <=> c in (0.25, 0.75)
    worst_c = 0.0
    for i in range(10001):
        c = i / 10000.0
        for rise, sset in ((SN_RISE, SN_SET), (0.2, 0.7), (0.05, 0.95)):
            s_ = day_scalar_from_cycle(c, rise, sset)
            worst_c = max(worst_c, abs(s7a_frac(cycle_from_day_scalar(s_, rise, sset) - c + 0.5) - 0.5))
            if abs(c - 0.25) > 1e-9 and abs(c - 0.75) > 1e-9:
                ok &= (rise < s_ < sset) == (0.25 < s7a_frac(c) < 0.75)
    print("s7asun: day scalar round trip worst %.3e cycles" % worst_c)
    ok &= worst_c < 1e-12
    # 2b) IsDay <=> SE sun above the camera's horizon on site v1 (axis SE +Y, declination 0)
    site1 = _site_v1()
    a1 = (0.0, 1.0, 0.0)
    bad_day = 0
    for _ in range(5000):
        ang = rnd.uniform(-math.pi, math.pi)
        s = (math.sin(ang), 0.0, math.cos(ang))
        P = _random_cam(rnd, site1, 7800.0)
        k = se_sky(site1, P, s, a1)
        if abs(math.degrees(k["elev"])) > 1e-6:
            bad_day += (k["elev"] > 0) != (SN_RISE < day_scalar_from_cycle(cycle_time(k["H"])) < SN_SET)
    print("s7asun: IsDay vs SE elevation > 0 on site v1: %d mismatches of 5,000" % bad_day)
    ok &= bad_day == 0
    # 3) site v1 numbers (CONTRACT: SD = -90 and NPO = 0 at the anchor, |NPO| <= 7.45 deg at rho_inf = 7,800 m)
    k0 = se_sky(site1, site1.sn_to_se((0.0, 0.0, 0.0)), (0.0, 0.0, 1.0), a1)
    sd0, npo0 = sn_sky(k0["q"])
    npo_max = 0.0
    for t in range(360):
        th = math.radians(t)
        for rr in (7800.0, 7800.0 * math.sqrt(2.0)):
            x, z = rr * math.cos(th), rr * math.sin(th)
            if max(abs(x), abs(z)) > 7800.0 + 1e-6:
                continue
            npo_max = max(npo_max, abs(sn_sky(se_sky(site1, site1.sn_to_se((x, 0.0, z)), (0.0, 0.0, 1.0), a1)["q"])[1]))
    print("s7asun: site v1 anchor SD %.6f NPO %.6f (pole az %.2f el %.2f), rho_inf=7,800 m |NPO| max %.3f deg; noon at "
          "the anchor: H %.6f c %.6f" % (sd0, npo0, k0["az"], k0["el"], npo_max, k0["H"], cycle_time(k0["H"])))
    ok &= abs(sd0 + 90.0) < 1e-9 and abs(npo0) < 1e-9 and npo_max <= 7.46
    # 4) Subnautica's sunrise on site v1 is at SN +x (CONTRACT: the sun rises at SN +x and sets at SN -x)
    ok &= angle_deg(sn_sun_dir(0.25, sd0, npo0), (1.0, 0.0, 0.0)) < 1e-9
    # 5) the step rule
    c5 = cycle_from_day_scalar(0.5)
    ok &= s7a_step(0.5, c5, 0.5, c5, False)[0] == "none"
    ok &= s7a_step(day_scalar_from_cycle(c5 + 1.0 / 360.0), c5 + 1.0 / 360.0, 0.5, c5, False)[0] == "hold"
    ok &= s7a_step(day_scalar_from_cycle(c5 + 3.0 / 360.0), c5 + 3.0 / 360.0, 0.5, c5, False)[0] == "jump"
    ok &= s7a_step(0.5, c5, 0.51, cycle_from_day_scalar(0.51), False)[0] == "advance"
    ok &= s7a_step(0.5, c5, 0.51, cycle_from_day_scalar(0.51), True)[0] == "jump"
    # 6) the SunLight law: equal to the disk at noon, 25 deg above it at sunrise and sunset
    ok &= abs(sun_light_elevation_deg(0.5) - 90.0) < 1e-12 and abs(sun_light_elevation_deg(0.25) - 25.0) < 1e-12
    ok &= abs(sun_light_elevation_deg(0.75) - 25.0) < 1e-12
    ok &= s7a_pack_cdeg(-0.125) == -13 and s7a_pack_cdeg(0.125) == 13 and s7a_pack_cdeg(400.0) == 32767
    print("s7asun selftest %s" % ("PASS" if ok else "FAIL"))
    return ok

def _f(x):
    return "%.17g" % x

def _v(v):
    return ",".join(_f(x) for x in v)

def s7asun_vectors(path):
    """The TSV read by tests/DeepEngineers.Wire.Tests/S7aSunVectorTests.cs: one record per line, `kind` then
 key=value fields (tab separated); doubles as %.17g, vectors comma separated."""
    rnd = random.Random(1717)
    lines = ["# S7a sun mapping vectors: python -I tools/worldmap/worldmap_ref.py s7asun --vectors "
             "tests/s7a/vectors/s7a_sun.tsv (CONTRACT 5.4 'S7a conventions'); kinds sky, cyc, step, light, pack"]

    def sky_line(tag, site, P, s, a):
        k, c, sd, npo, d, err = _chain(site, P, s, a)
        _k, _c, sdq, npoq, dq, errq = _chain(site, P, s, a, quantised=True)
        f = [("tag", tag), ("C", _v(site.C)), ("Rs", _f(site.Rs)), ("U0", _v(site.U0)), ("N0", _v(site.N0)),
             ("psi", _f(math.degrees(site.psi))), ("O", _v(site.O)), ("P", _v(P)), ("s", _v(s)), ("a", _v(a)),
             ("H", _f(k["H"])), ("elev", _f(k["elev"])), ("decl", _f(k["decl"])), ("lat", _f(k["lat"])),
             ("sunrise", _f(k["sunrise"])), ("sunSn", _v(k["sunSn"])), ("q", _v(k["q"])), ("azc", str(k["azc"])),
             ("elc", str(k["elc"])), ("valid", "1" if k["valid"] else "0"), ("c", _f(c)), ("sd", _f(sd)),
             ("npo", _f(npo)), ("sdq", _f(sdq)), ("npoq", _f(npoq)), ("snDir", _v(d)), ("snDirQ", _v(dq)),
             ("err", _f(err)), ("errQ", _f(errq))]
        lines.append("sky\t" + "\t".join("%s=%s" % kv for kv in f))

    for _ in range(300):
        site = _random_site(rnd)
        a = _rand_unit(rnd)
        s = _rand_perp(rnd, a) if rnd.random() < 0.8 else _rand_unit(rnd)   # 20 % with a declination (sky only)
        sky_line("random", site, _random_cam(rnd, site), s, a)
    site1 = _site_v1()
    for _ in range(80):
        ang = rnd.uniform(-math.pi, math.pi)
        sky_line("v1", site1, _random_cam(rnd, site1, 7800.0), (math.sin(ang), 0.0, math.cos(ang)), (0.0, 1.0, 0.0))
    sky_line("v1-anchor-noon", site1, site1.sn_to_se((0.0, 0.0, 0.0)), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0))
    sky_line("v1-anchor-sunrise", site1, site1.sn_to_se((0.0, 0.0, 0.0)), (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0))
    sky_line("v1-corner", site1, site1.sn_to_se((7800.0, 300.0, -7800.0)), (0.6, 0.0, 0.8), (0.0, 1.0, 0.0))
    for _ in range(200):
        c = rnd.random()
        rise, sset = (SN_RISE, SN_SET) if rnd.random() < 0.7 else (rnd.uniform(0.05, 0.3), rnd.uniform(0.6, 0.95))
        s_ = day_scalar_from_cycle(c, rise, sset)
        lines.append("cyc\tc=%s\trise=%s\tset=%s\ts=%s\tcBack=%s" % (_f(c), _f(rise), _f(sset), _f(s_),
                                                                      _f(cycle_from_day_scalar(s_, rise, sset))))
    for c in (0.0, 0.125, 0.25, 0.5, 0.75, 0.875, 0.999):
        s_ = day_scalar_from_cycle(c)
        lines.append("cyc\tc=%s\trise=%s\tset=%s\ts=%s\tcBack=%s" % (_f(c), _f(SN_RISE), _f(SN_SET), _f(s_),
                                                                      _f(cycle_from_day_scalar(s_))))
    for i in range(120):
        s_now = rnd.random()
        if i % 4 == 0:
            s_t = s7a_frac(s_now - rnd.uniform(0.0, 0.006))      # Subnautica slightly ahead: hold or jump
        elif i % 4 == 1:
            s_t = s7a_frac(s_now + rnd.uniform(0.0, 0.01))       # behind: advance
        else:
            s_t = rnd.random()
        force = rnd.random() < 0.3
        kind, adv = s7a_step(s_now, cycle_from_day_scalar(s_now), s_t, cycle_from_day_scalar(s_t), force)
        lines.append("step\tsNow=%s\tcNow=%s\tsT=%s\tcT=%s\tforce=%d\tresult=%s\tadv=%s" % (
            _f(s_now), _f(cycle_from_day_scalar(s_now)), _f(s_t), _f(cycle_from_day_scalar(s_t)), force, kind, _f(adv)))
    for i in range(49):
        c = i / 48.0
        lines.append("light\tc=%s\tmax=65\telev=%s" % (_f(c), _f(sun_light_elevation_deg(c))))
    for deg in (0.0, 0.004, 0.005, -0.005, 0.015, 1.005, -1.005, 89.995, -89.995, 179.99, -179.999, 400.0, -400.0,
                0.125, -0.125, 12.345):
        lines.append("pack\tdeg=%s\tcdeg=%d" % (_f(deg), s7a_pack_cdeg(deg)))
    text = "\n".join(lines) + "\n"
    if path == "-":
        sys.stdout.write(text)
    else:
        d = os.path.dirname(os.path.abspath(path))
        os.makedirs(d, exist_ok=True)
        with open(path, "w", encoding="ascii", newline="\n") as f:
            f.write(text)
        print("s7asun: %d vector lines -> %s" % (len(lines) - 1, path))
    return 0

def _cli_s7asun(argv):
    if "--vectors" in argv:
        i = argv.index("--vectors")
        return s7asun_vectors(argv[i + 1] if i + 1 < len(argv) else "-")
    return 0 if s7asun_selftest() else 1

if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "s7asun":
        sys.exit(_cli_s7asun(sys.argv[2:]))
    if len(sys.argv) >= 2 and sys.argv[1] in ("heights", "s4check"):
        sys.exit(_cli_s4(sys.argv[1:]))
    if len(sys.argv) >= 2 and sys.argv[1] == "selftest":
        good = selftest()
        numbers()
        sys.exit(0 if good else 1)
    if len(sys.argv) == 5 and sys.argv[1] in ("se2sn", "sn2se"):
        p = tuple(float(a) for a in sys.argv[2:5])
        out = EXAMPLE.se_to_sn(p) if sys.argv[1] == "se2sn" else EXAMPLE.sn_to_se(p)
        print("%.6f %.6f %.6f" % out)
        sys.exit(0)
    print(__doc__)
