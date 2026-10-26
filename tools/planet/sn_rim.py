"""Design 5: the Subnautica map's rim per azimuth, from the install's
terrain (the 4 m column maps of tools/sn-octree-scan: top.i16, lowfloor.i16, read in place, kept outside the repository).

Along each SN heading theta (0 = north = +z, 90 = east = +x) from the site outward:
 rEdge(theta) the last column with Subnautica terrain that is followed by at least EDGE_VOID_M of columns without terrain;
 rimY(theta) the median top (SN y) of the terrain columns between rEdge - RIM_BAND[1] and rEdge - RIM_BAND[0]: the seabed at the
 map's edge before its last drop;
both smoothed in azimuth by a circular Gaussian. The planet's floor beyond rEdge continues at rimY and blends into the ring's
biome depths over the layout's crater.rim.blendM (sculpt_de4546b.Design.evaluate, design 5); under Subnautica's terrain the cap
keeps the planet capMarginM below each column's lowest floor (SnCap).

Deterministic from the install's octrees (the same files give the same table; the input pin is scan_hash: fnv1a64 of the two
column maps): at release the installer runs it on the user's own install (nothing derived ships).
Usage: python tools/planet/sn_rim.py <scan dir> [--json | --hash]
"""
import json
import math
import os
import sys

import numpy as np

NONE = -32768
STEP_DEG = 0.5
EDGE_VOID_M = 200.0
RIM_BAND = (50.0, 200.0)
SIGMA_DEG = 4.0
R_MAX = 2900.0           # the map's corner (rhoInf 2,048 m)

def load(folder):
    top = np.fromfile(os.path.join(folder, "top.i16"), dtype=np.int16).reshape(1024, 1024).astype(np.float64)
    lf = np.fromfile(os.path.join(folder, "lowfloor.i16"), dtype=np.int16).reshape(1024, 1024).astype(np.float64)
    has = top != NONE
    top[~has] = np.nan
    lf = np.where(lf == 32767, top, lf)
    lf[~has] = np.nan
    return top, lf, has

def column(top, x, z):
    i = np.floor((np.asarray(x) + 2048.0) / 4.0).astype(np.int64)
    j = np.floor((np.asarray(z) + 2048.0) / 4.0).astype(np.int64)
    ok = (i >= 0) & (i < 1024) & (j >= 0) & (j < 1024)
    out = np.full(np.shape(i), np.nan)
    out[ok] = top[j[ok], i[ok]]
    return out

def rim_table(top, step_deg=STEP_DEG):
    """(theta deg array, rEdge m, rimY m): raw per azimuth, then a circular median over +-SMOOTH_DEG."""
    th = np.arange(0.0, 360.0, step_deg)
    rs = np.arange(0.0, R_MAX, 2.0)
    r_edge = np.zeros(th.size)
    rim_y = np.zeros(th.size)
    for k, t in enumerate(th):
        sx, sz = math.sin(math.radians(t)), math.cos(math.radians(t))
        tops = column(top, rs * sx, rs * sz)
        has = np.isfinite(tops)
        last = -1
        idx = np.nonzero(has)[0]
        for n in idx[::-1]:
            # the last terrain column followed by EDGE_VOID_M of void
            ahead = has[n + 1:n + 1 + int(EDGE_VOID_M / 2.0)]
            if not ahead.any():
                last = n
                break
        if last < 0:
            r_edge[k], rim_y[k] = 0.0, -40.0
            continue
        re = rs[last]
        band = (rs >= re - RIM_BAND[1]) & (rs <= re - RIM_BAND[0]) & has
        r_edge[k] = re
        rim_y[k] = float(np.median(tops[band])) if band.any() else float(tops[last])
    return th, gauss_circular(r_edge, SIGMA_DEG / step_deg), gauss_circular(rim_y, SIGMA_DEG / step_deg)

def gauss_circular(v, sigma):
    """A circular Gaussian smoothing (sigma in samples, +-3 sigma, wrap-around)."""
    h = int(math.ceil(3.0 * sigma))
    k = np.exp(-0.5 * (np.arange(-h, h + 1) / sigma) ** 2)
    k /= k.sum()
    n = v.size
    return np.array([float(np.dot(k, np.take(v, range(i - h, i + h + 1), mode="wrap"))) for i in range(n)])

def scan_hash(folder):
    """The rim's and the cap's input pin: fnv1a64 over top.i16 then lowfloor.i16, hex."""
    h = 0xcbf29ce484222325
    for name in ("top.i16", "lowfloor.i16"):
        with open(os.path.join(folder, name), "rb") as f:
            data = f.read()
        arr = np.frombuffer(data, dtype=np.uint8)
        # fnv1a64 byte by byte (vectorised in blocks would change nothing: the order is the file's)
        for b in arr.tobytes():
            h ^= b
            h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return "%016x" % h

class Rim:
    """rEdge and rimY at any heading (linear in theta between the table's steps)."""

    def __init__(self, th, r_edge, rim_y):
        self.th, self.re, self.ry = np.asarray(th), np.asarray(r_edge), np.asarray(rim_y)
        self.step = float(self.th[1] - self.th[0])

    def at(self, sx, sz):
        t = (np.degrees(np.arctan2(sx, sz)) + 360.0) % 360.0
        f = t / self.step
        i0 = np.floor(f).astype(np.int64) % self.th.size
        i1 = (i0 + 1) % self.th.size
        w = f - np.floor(f)
        return self.re[i0] * (1 - w) + self.re[i1] * w, self.ry[i0] * (1 - w) + self.ry[i1] * w

CAP_MARGIN_M = 30.0          # layout crater.capMarginM
CAP_FOOTPRINT_M = 96.0       # the min-filter radius: the Catmull-Rom 4 x 4 patch of 46 m pixels (a pixel weighs on columns 2 pixels away)
RIM_FILL_M = 300.0           # the rim's last 300 m (the crater edge's drop) the continued floor may bury

class SnCap:
    """The cap under Subnautica's terrain (design 5): the planet stays CAP_MARGIN_M below each protected column's lowest floor.
 Protected: every column of the interior (more than RIM_FILL_M inside the rim's edge for its heading), caves and overhangs
 included. The rim's last RIM_FILL_M (the crater edge's drop into the void: its outward faces, overhangs and feet) the
 continued floor may bury, so the crater is gone as a landform; the window serves those batches merged (Subnautica's terrain
 or the planet's) so both pictures agree."""

    def __init__(self, folder, rim=None, margin=CAP_MARGIN_M, rim_fill_m=RIM_FILL_M):
        self.top, self.lowfloor, self.has = load(folder)
        self.lowfloor = np.where(self.has, self.lowfloor, np.inf)
        self.rim = rim if rim is not None else Rim(*rim_table(self.top))
        xs = -2048.0 + 4.0 * np.arange(1024) + 2.0
        X, Z = np.meshgrid(xs, xs)
        re_, _ry = self.rim.at(X, Z)
        rho = np.hypot(X, Z)
        cave = self.has & (self.lowfloor < np.where(self.has, self.top, 0.0) - 5.0)
        interior = self.has & (rho < re_ - rim_fill_m)
        self.protected = interior
        self.rim_fill = self.has & ~interior
        self.rim_caves = int((cave & ~interior).sum())
        allowed = np.where(interior, self.lowfloor - margin, np.inf)
        from scipy.ndimage import minimum_filter  # noqa: E402 (the preview and the tool; the C# port does its own)
        k = int(CAP_FOOTPRINT_M / 4.0)
        self.allowed = minimum_filter(np.where(np.isfinite(allowed), allowed, 1e9), size=2 * k + 1, mode="nearest")
        self.allowed[self.allowed >= 1e8] = np.inf
        self.hash = scan_hash(folder)

    @staticmethod
    def index(x, z):
        i = np.floor((np.asarray(x) + 2048.0) / 4.0).astype(np.int64)
        j = np.floor((np.asarray(z) + 2048.0) / 4.0).astype(np.int64)
        ok = (i >= 0) & (i < 1024) & (j >= 0) & (j < 1024)
        return np.clip(i, 0, 1023), np.clip(j, 0, 1023), ok

    def cap(self, x, z):
        i, j, ok = self.index(x, z)
        return np.where(ok, self.allowed[j, i], np.inf)

    def protected_at(self, x, z):
        i, j, ok = self.index(x, z)
        return ok & self.protected[j, i]

    def rim_fill_at(self, x, z):
        i, j, ok = self.index(x, z)
        return ok & self.rim_fill[j, i]

    def lowfloor_at(self, x, z):
        i, j, ok = self.index(x, z)
        return np.where(ok, self.lowfloor[j, i], np.inf)

    def top_at(self, x, z):
        i, j, ok = self.index(x, z)
        return np.where(ok, self.top[j, i], np.nan)

def main(argv=None):
    argv = argv if argv is not None else sys.argv[1:]
    top, _lf, _has = load(argv[0])
    th, re, ry = rim_table(top)
    if "--hash" in argv:
        print(scan_hash(argv[0]))
        return 0
    if "--json" in argv:
        print(json.dumps({"stepDeg": STEP_DEG, "theta": th.tolist(), "rEdge": [round(v, 1) for v in re], "rimY": [round(v, 1) for v in ry]}))
    else:
        print("rEdge m: min %.0f, median %.0f, max %.0f" % (re.min(), np.median(re), re.max()))
        print("rimY m: min %.0f, p10 %.0f, median %.0f, p90 %.0f, max %.0f" % (ry.min(), np.percentile(ry, 10), np.median(ry), np.percentile(ry, 90), ry.max()))
        for t in range(0, 360, 30):
            k = int(t / STEP_DEG)
            print("  heading %3d: rEdge %5.0f m, rimY %6.0f m" % (t, re[k], ry[k]))
    return 0

if __name__ == "__main__":
    sys.exit(main())
