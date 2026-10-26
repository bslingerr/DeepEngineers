"""S4.A-T0, the Python side: run the oracle over the 10 batches and compare the C# runner's output with it.

Oracle: tools/sn-octrees/sn_octree_batch.py --at0 (rasters, summary, D = 0 crossings), read in place from the user's
CompiledOctreesCache. C# runner: tests/DeepEngineers.Terrain.Tests (At0Tests) writes at0-<b>.cs.json next to the
oracle files and at0-results.json. Derived data stays outside the repository (DEEPENG_AT0_DIR, default
%LOCALAPPDATA%\\DeepEngineers\\s4\\at0); only tests/out/s4/at0/at0-compare.json is written inside it.

Checks per batch (ids S4.A-T0.<bx>-<by>-<bz>.*):
 raster the C# rasters' SHA-256 (type, density, leaf size) equal the oracle's and the runner saw them byte-identical
 summary file facts, leaf-size and near-leaf-size counts, solid and near counts, crossing counts equal
 probes D at the C# probe points recomputed with tools/sn-octrees/sn_field.py (over the oracle raster):
 |D_cs - D_py| <= 1e-6 and the same "all far, one sign" flags
 iso-g0 content iso-surface (SE bytes, iso byte 127) vs the oracle's D = 0 on Subnautica's sample grid: P95 <= 0.05 m;
 the C#'s sampled per-crossing errors re-computed here agree within 1e-6 m
 iso-g1 the same on the grid offset by (0.37, 0.21, 0.45) m: ROADMAP 0.05 m reported, guard 0.20 m
The iso metric (identical in At0Oracle.cs): for a content crossing at tq on an axis edge from A, the zero t* of the
oracle's D on that axis line (t in [-1, 2]; D piecewise linear between integer sample planes; a zero is a strict sign
change or an exact 0) nearest to tq; e = |t* - tq| * |dD/daxis| / |grad D| at t*; no zero -> e = 1 (unmatched).
Edges where D crosses but the content does not count as missed (e = distance of the zero to the nearer edge end,
scaled alike). P95 is nearest-rank (sorted[ceil(0.95 n) - 1]).

Usage:
 python -I tests/s4/at0_compare.py oracle [--root CACHE] [--out DIR] [--batches 12-18-12,...] [--force]
 python -I tests/s4/at0_compare.py compare [--out DIR] [--batches...]
 python -I tests/s4/at0_compare.py all [--run-cs]... oracle, optionally `dotnet test... --filter TestCategory=AT0`, compare
 python -I tests/s4/at0_compare.py --selftest offline (synthetic batch): S4.M1a-d must turn the comparison red
Exit code 0 when every required row passes.
"""
import argparse
import json
import math
import os
import shutil
import struct
import subprocess
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
for p in (os.path.join(REPO, "tools", "sn-octrees"), os.path.join(REPO, "tests", "s2")):
    if p not in sys.path:
        sys.path.insert(0, p)
import sn_octree_batch as ob  # noqa: E402
import sn_field as sf  # noqa: E402
from s2_common import row, table, verdict  # noqa: E402

N = 160
BATCHES = [
    ((12, 18, 12), "Safe Shallows"), ((7, 17, 10), "densest file"), ((14, 18, 16), "kelp forest"),
    ((10, 17, 11), "Jellyshroom cave entrance"), ((6, 17, 5), "Grand Reef CoralArch"), ((7, 13, 15), "Lost River region"),
    ((10, 11, 13), "lava zone"), ((14, 8, 9), "deepest floor"), ((14, 19, 18), "Mountain Island top"),
    ((18, 15, 7), "shallowest cut ceiling"),
]
ISO_P95_MAX = 0.05
ISO_G1_GUARD = 0.20
PROBE_TOL = 1e-6
SAMPLE_TOL = 1e-6
OFFSET = (0.37, 0.21, 0.45)
ISO_BYTE = 127.0

def default_out():
    d = os.environ.get("DEEPENG_AT0_DIR") or os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
                                                         "DeepEngineers", "s4", "at0")
    return d

def bname(b):
    return f"{b[0]}-{b[1]}-{b[2]}"

# ------------------------------------------------------------------------------------------------- the iso metric
class OracleField:
    """D and its gradient over one batch raster, in sample-index space u (sample k at u = k)."""

    def __init__(self, s):
        self.s = s

    def _cell(self, u):
        c = math.floor(u)
        c = min(max(c, 0), N - 2)
        return c, u - c

    def _corners(self, ux, uy, uz):
        cx, fx = self._cell(ux)
        cy, fy = self._cell(uy)
        cz, fz = self._cell(uz)
        S = self.s
        return (S[cx, cy, cz], S[cx + 1, cy, cz], S[cx, cy + 1, cz], S[cx + 1, cy + 1, cz],
                S[cx, cy, cz + 1], S[cx + 1, cy, cz + 1], S[cx, cy + 1, cz + 1], S[cx + 1, cy + 1, cz + 1], fx, fy, fz)

    def d(self, ux, uy, uz):
        s000, s100, s010, s110, s001, s101, s011, s111, fx, fy, fz = self._corners(ux, uy, uz)
        c00 = s000 + (s100 - s000) * fx
        c10 = s010 + (s110 - s010) * fx
        c01 = s001 + (s101 - s001) * fx
        c11 = s011 + (s111 - s011) * fx
        c0 = c00 + (c10 - c00) * fy
        c1 = c01 + (c11 - c01) * fy
        return float(c0 + (c1 - c0) * fz)

    def grad(self, ux, uy, uz):
        s000, s100, s010, s110, s001, s101, s011, s111, fx, fy, fz = self._corners(ux, uy, uz)

        def lerp(a, b, t):
            return a + (b - a) * t
        gx = lerp(lerp(s100 - s000, s110 - s010, fy), lerp(s101 - s001, s111 - s011, fy), fz)
        gy = lerp(lerp(s010 - s000, s110 - s100, fx), lerp(s011 - s001, s111 - s101, fx), fz)
        gz = lerp(lerp(s001 - s000, s101 - s100, fx), lerp(s011 - s010, s111 - s110, fx), fy)
        return float(gx), float(gy), float(gz)

    def _d_at(self, a, axis, t):
        p = list(a)
        p[axis] += t
        return self.d(*p)

    def nearest_zero(self, a, axis, lo, hi, tq):
        av = a[axis]
        if not hi >= lo:
            return None
        best, best_dist = None, math.inf
        t_prev, d_prev = lo, self._d_at(a, axis, lo)
        if d_prev == 0:
            best, best_dist = lo, abs(lo - tq)
        k = math.floor(av + lo) + 1
        while True:
            t = k - av
            last = not (t < hi)
            if last:
                t = hi
            if t > t_prev:
                d = self._d_at(a, axis, t)
                z = None
                if d == 0:
                    z = t
                elif (d_prev > 0 and d < 0) or (d_prev < 0 and d > 0):
                    z = t_prev + (t - t_prev) * d_prev / (d_prev - d)
                if z is not None and abs(z - tq) < best_dist:
                    best, best_dist = z, abs(z - tq)
                t_prev, d_prev = t, d
            if last:
                break
            k += 1
        return best

    def _scaled(self, a, axis, tz, dist):
        p = list(a)
        p[axis] += tz
        g = self.grad(*p)
        gn = math.sqrt(g[0] ** 2 + g[1] ** 2 + g[2] ** 2)
        return dist * abs(g[axis]) / gn if gn > 1e-12 else dist

    def edge_error(self, a, axis, tq):
        lo, hi = max(-1.0, -a[axis]), min(2.0, (N - 1) - a[axis])
        z = self.nearest_zero(a, axis, lo, hi, tq)
        if z is None:
            return 1.0, False
        return self._scaled(a, axis, z, abs(z - tq)), True

    def missed_error(self, a, axis):
        z = self.nearest_zero(a, axis, 0.0, 1.0, 0.5)
        if z is None:
            return 0.0
        return self._scaled(a, axis, z, min(z, 1.0 - z))

def grid_d(S, off):
    """D (trilinear, the shared lerp order) on the (N-1)^3 grid u = k + off, 0 <= off < 1, vectorised."""
    fx, fy, fz = off
    c00 = S[:-1, :-1, :-1] + (S[1:, :-1, :-1] - S[:-1, :-1, :-1]) * fx
    c10 = S[:-1, 1:, :-1] + (S[1:, 1:, :-1] - S[:-1, 1:, :-1]) * fx
    c01 = S[:-1, :-1, 1:] + (S[1:, :-1, 1:] - S[:-1, :-1, 1:]) * fx
    c11 = S[:-1, 1:, 1:] + (S[1:, 1:, 1:] - S[:-1, 1:, 1:]) * fx
    c0 = c00 + (c10 - c00) * fy
    c1 = c01 + (c11 - c01) * fy
    return c0 + (c1 - c0) * fz

def p95(values):
    if len(values) == 0:
        return 0.0
    v = np.sort(np.asarray(values, np.float64))
    return float(v[max(0, int(math.ceil(0.95 * len(v))) - 1)])

# ------------------------------------------------------------------------------------------------- oracle step
def run_oracle(root, out, batches, force=False):
    sha = ob.oracle_sha256()
    for b, name in batches:
        stem = os.path.join(out, f"at0-{bname(b)}")
        fresh = False
        if not force and all(os.path.isfile(stem + x) for x in (".bin", ".json", "-iso.bin")):
            try:
                with open(stem + ".json", encoding="utf-8") as f:
                    fresh = json.load(f).get("oracleSha256") == sha
            except (OSError, ValueError):
                fresh = False
        if fresh:
            print(f"oracle {bname(b)} ({name}): fresh")
            continue
        doc = ob.write_at0(root, b[0], b[1], b[2], out)
        print(f"oracle {bname(b)} ({name}): solid={doc['solid']} near={doc['near']} crossings={doc['crossings']['total']} "
              f"iso={doc['iso']['count']}")

def run_cs():
    cmd = ["dotnet", "test", os.path.join(REPO, "tests", "DeepEngineers.Terrain.Tests"), "-c", "Release",
           "-nologo", "-v", "q", "--filter", "TestCategory=AT0"]
    print("running", " ".join(cmd))
    return subprocess.call(cmd, cwd=REPO)

# ------------------------------------------------------------------------------------------------- comparison
SUMMARY_KEYS = ("batch", "worldMin", "filePresent", "fileBytes", "bad", "leafSizes", "nearSizes", "solid", "near", "crossings")
SHA_KEYS = ("typeSha256", "densitySha256", "leafSizeSha256")

def compare_batch(rows, b, name, oracle_doc, typ, den, cs):
    """Appends the rows of one batch; returns True when every required row passes."""
    tag = f"S4.A-T0.{bname(b)}"
    if cs is None:
        row(rows, tag, "MISSING", "no at0-%s.cs.json" % bname(b), "C# runner output", f"{name}: run the C# A-T0 runner")
        return False
    summ = cs.get("summary", {})
    sha_ok = all(summ.get(k) == oracle_doc.get(k) for k in SHA_KEYS) and bool(cs.get("rasterEqual"))
    row(rows, tag + ".raster", "PASS" if sha_ok else "FAIL", "equal" if sha_ok else (cs.get("rasterDiff") or "sha differs"),
        "byte-identical", f"{name}: type, density, leaf-size rasters")
    diffs = [k for k in SUMMARY_KEYS if summ.get(k) != oracle_doc.get(k)]
    row(rows, tag + ".summary", "PASS" if not diffs and cs.get("summaryEqual") else "FAIL",
        "equal" if not diffs else "differs: " + ",".join(diffs), "equal", f"{name}: leaf sizes, solid, near, crossings")

    s = ob.sample_values(typ, den)
    origin = np.array(oracle_doc["worldMin"], np.float64)

    def loader(bx, by, bz):
        return (typ, den) if (bx, by, bz) == tuple(b) else None
    fld = sf.Field(root="(oracle raster)", loader=loader)
    pts = np.array([p[:3] for p in cs["probes"]["points"]], np.float64) if cs["probes"]["points"] else np.zeros((0, 3))
    if len(pts):
        d_py, same_py = fld.d_many(pts)
        d_cs = np.array([p[3] for p in cs["probes"]["points"]])
        same_cs = np.array([p[4] > 0 for p in cs["probes"]["points"]])
        err = float(np.max(np.abs(d_py - d_cs)))
        same_ok = bool(np.all(same_py == same_cs))
    else:
        err, same_ok = 0.0, True
    row(rows, tag + ".probes", "PASS" if err <= PROBE_TOL and same_ok else "FAIL", f"max |dD| {err:.2e} n={len(pts)}",
        f"<= {PROBE_TOL:g}", f"{name}: D at the C# probes vs sn_field.py")

    iso = cs.get("iso", {})
    of = OracleField(s)
    worst = 0.0
    for smp in iso.get("samples", []):
        grid, axis, ax, ay, az, tq, e_cs = smp
        e_py, _ = of.edge_error((ax, ay, az), int(axis), tq)
        worst = max(worst, abs(e_py - e_cs))
    g0 = float(iso.get("p95g0", 1.0))
    g1 = float(iso.get("p95g1", 1.0))
    ok0 = g0 <= ISO_P95_MAX and worst <= SAMPLE_TOL and float(iso.get("iso", 0)) == ISO_BYTE
    row(rows, tag + ".iso-g0", "PASS" if ok0 else "FAIL", f"P95 {g0:.4f} m (resample check {worst:.1e})",
        f"<= {ISO_P95_MAX} m", f"{name}: content iso (byte {iso.get('iso')}) vs D = 0, Subnautica's sample grid")
    row(rows, tag + ".iso-g1", "PASS" if g1 <= ISO_P95_MAX else "FAIL", f"P95 {g1:.4f} m (guard {ISO_G1_GUARD})",
        f"<= {ISO_P95_MAX} m (ROADMAP)", f"{name}: offset grid (0.37, 0.21, 0.45); open: owner/orchestrator decision",
        required=False)
    row(rows, tag + ".iso-g1guard", "PASS" if g1 <= ISO_G1_GUARD else "FAIL", f"P95 {g1:.4f} m", f"<= {ISO_G1_GUARD} m",
        f"{name}: offset-grid regression guard")
    return sha_ok and not diffs and err <= PROBE_TOL and same_ok and ok0 and g1 <= ISO_G1_GUARD

def compare(out, batches, results_path=None):
    rows = []
    for b, name in batches:
        stem = os.path.join(out, f"at0-{bname(b)}")
        if not os.path.isfile(stem + ".json"):
            row(rows, f"S4.A-T0.{bname(b)}", "MISSING", "no oracle output", "oracle files", f"{name}: run `oracle` first")
            continue
        with open(stem + ".json", encoding="utf-8") as f:
            oracle_doc = json.load(f)
        _, typ, den, _ = ob.read_at0_bin(stem + ".bin")
        cs = None
        if os.path.isfile(stem + ".cs.json"):
            with open(stem + ".cs.json", encoding="utf-8") as f:
                cs = json.load(f)
        compare_batch(rows, b, name, oracle_doc, typ, den, cs)
    print(table(rows))
    ok = verdict(rows)
    print("S4.A-T0", "PASS" if ok else "FAIL", f"({sum(r['status'] == 'PASS' for r in rows)}/{len(rows)} rows PASS)")
    if results_path:
        os.makedirs(os.path.dirname(results_path), exist_ok=True)
        with open(results_path, "w", encoding="utf-8") as f:
            json.dump({"verdict": "PASS" if ok else "FAIL", "rows": rows}, f, indent=1)
    return ok

# ------------------------------------------------------------------------------------------------- selftest
def encode_batch(bx, by, bz, typ, den):
    """A batch file in Subnautica's format from dense rasters (uniform cubes collapse; all-zero octrees have 0 nodes)."""
    out = bytearray(struct.pack("<i", 4))
    for ix in range(5):
        for iy in range(5):
            for iz in range(5):
                o = (bx * 5 + ix, by * 5 + iy, bz * 5 + iz)
                if not (o[0] < 128 and o[1] < 100 and o[2] < 128):
                    continue
                x0, y0, z0 = ix * 32, iy * 32, iz * 32
                t32, d32 = typ[x0:x0 + 32, y0:y0 + 32, z0:z0 + 32], den[x0:x0 + 32, y0:y0 + 32, z0:z0 + 32]
                if not t32.any() and not d32.any():
                    out += struct.pack("<H", 0)
                    continue
                nodes = [None]
                queue = [(0, x0, y0, z0, 32)]
                qi = 0
                while qi < len(queue):
                    nid, x, y, z, size = queue[qi]
                    qi += 1
                    tt, dd = typ[x:x + size, y:y + size, z:z + size], den[x:x + size, y:y + size, z:z + size]
                    if size == 1 or ((tt == tt.flat[0]).all() and (dd == dd.flat[0]).all()):
                        nodes[nid] = (int(tt.flat[0]), int(dd.flat[0]), 0)
                        continue
                    fc = len(nodes)
                    nodes.extend([None] * 8)
                    nodes[nid] = (int(tt.flat[0]), int(dd.flat[0]), fc)
                    h = size // 2
                    for c in range(8):
                        queue.append((fc + c, x + ((c >> 2) & 1) * h, y + ((c >> 1) & 1) * h, z + (c & 1) * h, h))
                out += struct.pack("<H", len(nodes))
                for t, d, fc in nodes:
                    out += struct.pack("<BBH", t, d, fc)
    return bytes(out)

def synthetic_rasters():
    """The asymmetric test surface y_s = 80 + 0.21 x - 0.07 z + 6 sin(x / 17) (batch-local), solid below, near band
 +-1 voxel along the normal; octree column (4, *, 4) has no data. Same shape as SyntheticOctrees.StandardSurface."""
    c = np.arange(N) + 0.5
    X, Y, Z = np.meshgrid(c, c, c, indexing="ij")
    ys = 80.0 + 0.21 * X - 0.07 * Z + 6.0 * np.sin(X / 17.0)
    gx = 0.21 + 6.0 / 17.0 * np.cos(X / 17.0)
    dist = (ys - Y) / np.sqrt(1.0 + gx * gx + 0.07 ** 2)
    near = np.abs(dist) < 1.0
    den = np.where(near, np.clip(np.floor(dist * 126).astype(np.int64) + 126, 1, 251), 0).astype(np.uint8)
    mat = np.where(Y < 60, 7, 9).astype(np.uint8)
    typ = np.where(near | (dist > 0), mat, 0).astype(np.uint8)
    nodata = (X >= 128) & (Z >= 128)
    typ[nodata] = 0
    den[nodata] = 0
    return typ, den

def make_candidate(root, b, mutant, oracle_s, n_probes=512, stride=7):
    """A C#-runner-like record produced by the Python oracle itself, optionally with an S4.M1 mutant (selftest)."""
    path = ob.batch_path(root, *b)
    typ, den, leaf, ls, ns, info = ob.rasterize_at0(path, *b, mutant=mutant)
    summ = ob.summary_at0(*b, typ, den, leaf, ls, ns, info)
    world_min = np.array(summ["worldMin"], np.float64)
    fld = sf.Field(root=root, mutant="M1a" if mutant == "M1a" else None,
                   loader=lambda bx, by, bz: (typ, den) if (bx, by, bz) == tuple(b) else None)
    rng = np.random.default_rng(7)
    loc = 0.5 + rng.random((n_probes, 3)) * 158.99
    pts = world_min + loc
    d, same = fld.d_many(pts)
    probes = [[*map(float, pts[i]), float(np.float32(d[i])), 1 if same[i] else 0] for i in range(n_probes)]
    cand_s = ob.sample_values(typ, den, mutant)
    of = OracleField(oracle_s)
    samples, e0, e1 = [], [], []
    for grid, off, m in ((0, (0.0, 0.0, 0.0), N), (1, OFFSET, N - 1)):
        Dc = grid_d(cand_s, off) if grid else cand_s
        Do = grid_d(oracle_s, off) if grid else oracle_s
        bytes_ = sf.content_bytes(Dc.astype(np.float32)).astype(np.float64)
        errs = e0 if grid == 0 else e1
        for ax in range(3):
            a = [slice(None)] * 3
            bb = [slice(None)] * 3
            a[ax], bb[ax] = slice(None, -1), slice(1, None)
            ba, b2 = bytes_[tuple(a)], bytes_[tuple(bb)]
            ca, cb = ba >= ISO_BYTE, b2 >= ISO_BYTE
            oa, ob_ = Do[tuple(a)] >= 0, Do[tuple(bb)] >= 0
            cross = np.argwhere(ca != cb)
            for k, idx in enumerate(cross):
                if k % stride:
                    continue
                A = tuple(float(idx[i]) + off[i] for i in range(3))
                va, vb = ba[tuple(idx)], b2[tuple(idx)]
                tq = (va - ISO_BYTE) / (va - vb)
                e, _ = of.edge_error(A, ax, tq)
                errs.append(e)
                if len(samples) < 400:
                    samples.append([grid, ax, *A, tq, e])
            for k, idx in enumerate(np.argwhere((ca == cb) & (oa != ob_))):
                if k % stride:
                    continue
                errs.append(of.missed_error(tuple(float(idx[i]) + off[i] for i in range(3)), ax))
    iso = {"iso": ISO_BYTE, "p95g0": p95(e0), "p95g1": p95(e1), "p95": p95(e0 + e1), "samples": samples}
    return {"summary": summ, "rasterEqual": True, "summaryEqual": True, "probes": {"points": probes}, "iso": iso,
            "mutants": mutant or ""}

def selftest():
    ok = True
    rows_all = []

    def check(name, cond, detail=""):
        nonlocal ok
        print(("PASS " if cond else "FAIL ") + name + (" " + detail if detail else ""))
        ok = ok and bool(cond)

    # metric unit checks
    s = np.full((N, N, N), -1.0)
    s[:, :80, :] = 1.0
    s[:, 80, :] = 0.25                      # slope 1 across the band: zero at u_y = 80.25 on y edges
    s[:, 81, :] = -0.75
    of = OracleField(s)
    e, m = of.edge_error((10.0, 80.0, 10.0), 1, 0.25)
    check("metric: exact crossing scores 0", m and abs(e) < 1e-12, str(e))
    e, m = of.edge_error((10.0, 80.0, 10.0), 1, 0.35)
    check("metric: 0.1 m along the normal scores 0.1", m and abs(e - 0.1) < 1e-12, str(e))
    e, m = of.edge_error((10.3, 80.0, 10.0), 0, 0.5)
    check("metric: an edge parallel to the surface finds no zero", not m and e == 1.0)
    check("p95 nearest rank", p95(list(range(1, 101))) == 95.0 and p95([]) == 0.0)

    tmp = tempfile.mkdtemp(prefix="deepeng-at0-selftest-")
    try:
        root = os.path.join(tmp, "cache")
        out = os.path.join(tmp, "out")
        os.makedirs(root)
        b = (12, 18, 12)
        typ, den = synthetic_rasters()
        with open(ob.batch_path(root, *b), "wb") as f:
            f.write(encode_batch(*b, typ, den))
        doc = ob.write_at0(root, *b, out)
        _, t2, d2, _ = ob.read_at0_bin(os.path.join(out, "at0-12-18-12.bin"))
        check("encode -> oracle round trip: rasters equal", np.array_equal(t2, typ) and np.array_equal(d2, den))
        check("oracle summary has crossings", doc["crossings"]["total"] > 10000 and doc["iso"]["count"] == doc["crossings"]["total"])
        oracle_s = ob.sample_values(t2, d2)
        for mutant in (None, "M1a", "M1b", "M1c", "M1d"):
            cand = make_candidate(root, b, mutant, oracle_s)
            rows = []
            green = compare_batch(rows, b, "synthetic", doc, t2, d2, cand)
            red_rows = [r["id"].split(".")[-1] for r in rows if r["status"] != "PASS" and r.get("required", True)]
            if mutant is None:
                check("clean candidate is green", green, f"iso g0 {cand['iso']['p95g0']:.4f} g1 {cand['iso']['p95g1']:.4f} red={red_rows}")
            else:
                check(f"S4.{mutant} turns the comparison red", not green, "red rows: " + ",".join(red_rows))
            rows_all.extend(rows)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print("AT0_COMPARE SELFTEST", "PASS" if ok else "FAIL")
    return 0 if ok else 1

# ------------------------------------------------------------------------------------------------- CLI
def parse_batches(spec):
    if not spec:
        return BATCHES
    names = {b: n for b, n in BATCHES}
    out = []
    for tok in spec.split(","):
        b = tuple(int(x) for x in tok.strip().split("-"))
        out.append((b, names.get(b, "batch " + tok.strip())))
    return out

def main(argv):
    argv = [("--" + a.replace("=", " ", 1)).split(" ", 1) if "=" in a and not a.startswith("-") else [a] for a in argv]
    argv = [x for pair in argv for x in pair]
    if "--selftest" in argv:
        return selftest()
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("cmd", choices=("oracle", "compare", "all"))
    ap.add_argument("--root", default=sf.default_root())
    ap.add_argument("--out", default=default_out())
    ap.add_argument("--batches", default="")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--run-cs", action="store_true")
    ap.add_argument("--results", default=os.path.join(REPO, "tests", "out", "s4", "at0", "at0-compare.json"))
    a = ap.parse_args(argv)
    ob.refuse_inside_repo(a.out)
    batches = parse_batches(a.batches)
    if a.cmd in ("oracle", "all"):
        if not os.path.isdir(a.root):
            print("CompiledOctreesCache not found:", a.root, "(set DEEPENG_SUBNAUTICA)")
            return 2
        os.makedirs(a.out, exist_ok=True)
        run_oracle(a.root, a.out, batches, a.force)
    if a.cmd == "all" and a.run_cs:
        rc = run_cs()
        print("C# runner exit code", rc)
    if a.cmd in ("compare", "all"):
        return 0 if compare(a.out, batches, a.results) else 1
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
