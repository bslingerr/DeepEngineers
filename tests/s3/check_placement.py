"""S3.C2 placement oracle (ROADMAP S3): Subnautica's drawn block corners vs the wire convention, recomputed independently.

 python -I tests/s3/check_placement.py <session folder | file.jsonl...> [--json] [--tol-mm 1.0] [--lenient]
 python -I tests/s3/check_placement.py --selftest # synthetic records + seeded failures (S3.M1, 2 mm, s dropped)

For every Subnautica corner record, the matching SE pose record gives the SE world matrix M of the entity (grid) and
the pivot P* the SE plugin used; this tool recomputes (o, q, s) = wire_ref.wire_pose(site, M, P*) (tools/worldmap/
wire_ref.py, independent of WireMath.cs) and Apply(o, q, s, k, l) = o + S*(q*(k*Mirror(l))) for the 8 SE-local AABB
corners l, and compares with the 8 SN-world corners Subnautica logged from its own transforms: S3.C2 passes when every
corner is within 1 mm. Separately (S3.C2.affine) the affine Apply is compared with the exact map F(M*(k*l)) against the
bound max(s, 1)*d^2/(sqrt3 (r - d)) + (theta/sin theta - 1)*s*d (wire_ref.affine_vs_f_bound_at; d = |M*(k*l) - P*|,
r = |P* - C|, theta the larger anchor angle of P* and the point).

S3 JSONL SCHEMA ("s3/1"; the S2 conventions of tests/s2/s2_common.py: one JSON object per line, the event name in "ev"
(SE plugin, stand-ins) or "event" (SN plugin), the payload top-level, inside "data", or as a JSON object written after
the event name in a "msg"/"text" line, e.g. `s3.pose {"entityId"...}`). Numbers in round-trip precision ("R"): the SE
matrix and P* are ~1.5e6 m from the SE origin, so fewer digits cost millimetres.
 SE s3.pose entityId, tick (C2 tick of the snapshot that carried this pose), seMatrix {right, up, backward,
 translation} (doubles, SE world; or a list of 12 rows R,U,B,T or 16 M11..M44), pivot [3] (P*, SE
 world, doubles), k (PositionComp.Scale, default 1), site {C [3], Rs} (default SITE_V1: the test
 centre and RsExpected), optional wire {o [3], q [4], s} (what was written to C2, for the INFO row),
 optional localCorners [8][3] (SE-local, entity space, corner order below) or localMin/localMax.
 SN s3.placement entityId, seTick (the C2 tick of the pose applied; aliases snapshotTick, poseTick, presentedSeTick,
 tick), corners [8][3], localCorners [8][3] or localMin/localMax (older lines),
 optional pose {o, q, s, k} as applied.
 Corner order (MeshConventions.AabbCorner / wire_ref.aabb_corner): index i, bit 0 -> max x, bit 1 -> max y,
 bit 2 -> max z of the SE-local box. Aliases accepted: s3.sePose/s3.placement.se/s3.grid (SE) and
 s3.snPlacement/s3.placement.sn/s3.corners (SN); matrix/worldMatrix, pStar/P*, cornersSn/worldCorners,
 cornersSe/localCornersSe, aabbMin/aabbMax/boxMin/boxMax, bounds [6], entity/id/gridId.
Matching: an SN record takes the SE record of the same entity with the largest tick <= its own (else the earliest).

ROWS (required unless marked *)
 S3.C2 worst |corner_SN - Apply| over every matched record and corner <= tol (1 mm)
 S3.C2.affine worst |Apply - F| / bound <= 1 (+1e-9 m) over every SE pose record with corners: the wire convention's
 own error against the exact map, from SE's logged matrix and P* only
 S3.C2.match SN corner records matched to an SE pose (count > 0)
 S3.C2.local* SE- and SN-side SE-local corners agree (1e-4 m) when both are logged
 S3.C2.wire* the SE plugin's own (o, q, s) vs this recomputation (float32 wire tolerance: 2 mm, 1e-6, 1e-6)
Exit code 0 when every required row passes (MISSING fails unless --lenient). With a folder argument the result is also
written to <folder>/check_placement-results.json.
"""
from __future__ import annotations

import glob
import json
import math
import os
import re
import struct
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.normpath(os.path.join(_HERE, "..", ".."))
_WORLDMAP = os.path.join(_ROOT, "tools", "worldmap")
if _WORLDMAP not in sys.path:
    sys.path.insert(0, _WORLDMAP)
import wire_ref as W  # noqa: E402
import site_v1  # noqa: E402
import worldmap_ref as wm  # noqa: E402

SE_EVENTS = ("s3.pose", "s3.sePose", "s3.placement.se", "s3.grid")
SN_EVENTS = ("s3.placement", "s3.snPlacement", "s3.placement.sn", "s3.corners")
TOL_C2_M = 1e-3
AFFINE_SLACK_M = 1e-9
LOCAL_TOL_M = 1e-4
WIRE_TOL_O_M, WIRE_TOL_Q, WIRE_TOL_S = 2e-3, 1e-6, 1e-6
_EMBED = re.compile(r"\b(s3\.[A-Za-z0-9_.]+)\s+(\{.*\})\s*$")
BAD = ("FAIL", "MISSING")
AFFINE_WHAT = ("affine Apply vs the exact map F within max(s,1) d^2/(sqrt3 (r-d)) + (theta/sin theta - 1) s d (the "
               "wire convention checked on SE's logged matrix and P*; it reads no Subnautica output)")

# ----------------------------------------------------------------------------------------------------------- reading
def read_jsonl(paths):
    out = []
    for path in paths:
        if not path or not os.path.exists(path):
            continue
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line.startswith("{"):
                    continue
                try:
                    rec = json.loads(line)
                except ValueError:
                    continue
                if isinstance(rec, dict):
                    out.append(rec)
    return out

def normalise(rec):
    """Raw record -> (event name, payload dict) for s3.* events, else None."""
    name = rec.get("ev") or rec.get("event")
    data = None
    if isinstance(name, str) and name.startswith("s3."):
        data = dict(rec["data"]) if isinstance(rec.get("data"), dict) else \
            {k: v for k, v in rec.items() if k not in ("ev", "event", "data")}
    text = rec.get("msg") if isinstance(rec.get("msg"), str) else rec.get("text")
    if isinstance(text, str):
        m = _EMBED.search(text)
        if m:
            try:
                emb = json.loads(m.group(2))
            except ValueError:
                emb = None
            if isinstance(emb, dict):
                name = m.group(1)
                data = dict(data or {})
                data.update(emb)
    if data is None or not isinstance(name, str):
        return None
    return name, data

def _first(d, *names):
    for n in names:
        if n in d and d[n] is not None:
            return d[n]
    return None

def _vec(v, n=3):
    if isinstance(v, dict):
        v = [v.get(c) for c in "xyzw"[:n]]
    if not isinstance(v, (list, tuple)) or len(v) != n:
        return None
    try:
        return tuple(float(x) for x in v)
    except (TypeError, ValueError):
        return None

def _matrix(v):
    """{right, up, backward, translation} | 12 rows | 16 (M11..M44) | 4x4 nested -> (R, U, B, T) or None."""
    if isinstance(v, dict):
        parts = [_vec(_first(v, *names)) for names in (("right", "Right"), ("up", "Up"), ("backward", "Backward"),
                                                       ("translation", "Translation", "t"))]
        return tuple(parts) if all(p is not None for p in parts) else None
    if isinstance(v, (list, tuple)):
        if len(v) == 4 and all(isinstance(r, (list, tuple)) for r in v):
            v = [x for r in v for x in r]
        try:
            f = [float(x) for x in v]
        except (TypeError, ValueError):
            return None
        if len(f) == 12:
            return (tuple(f[0:3]), tuple(f[3:6]), tuple(f[6:9]), tuple(f[9:12]))
        if len(f) == 16:
            return (tuple(f[0:3]), tuple(f[4:7]), tuple(f[8:11]), tuple(f[12:15]))
    return None

def _corners(d, prefix_names, minmax_names):
    c = _first(d, *prefix_names)
    if isinstance(c, (list, tuple)) and len(c) == 8:
        pts = [_vec(p) for p in c]
        if all(p is not None for p in pts):
            return pts
    lo = _vec(_first(d, *minmax_names[0]) or [])
    hi = _vec(_first(d, *minmax_names[1]) or [])
    if lo is None or hi is None:
        b = _first(d, "bounds")
        if isinstance(b, (list, tuple)) and len(b) == 6:
            lo, hi = tuple(float(x) for x in b[:3]), tuple(float(x) for x in b[3:])
    if lo is not None and hi is not None:
        return W.aabb_corners(lo, hi)
    return None

def _entity(d):
    v = _first(d, "entityId", "entity", "id", "gridId")
    return None if v is None else str(v)

def _int(v):
    try:
        return int(v)
    except (TypeError, ValueError):
        return None

def make_site(d):
    s = d.get("site") if isinstance(d.get("site"), dict) else d
    c = _vec(_first(s, "C", "c", "planetCentre", "planetCenter", "centre", "center") or [])
    rs = _first(s, "Rs", "rs", "seaRadius")
    if c is None and rs is None:
        return site_v1.SITE_V1, "SITE_V1 (default)"
    c = c if c is not None else site_v1.TEST_C
    rs = float(rs) if rs is not None else site_v1.RS_EXPECTED
    a = site_v1.ANCHOR
    return wm.Site(C=c, Rs=rs, U0=a["U0"], N0=a["N0"], psi_deg=a["psi_deg"], O=a["O"]), "C=%s Rs=%r" % (list(c), rs)

class SePose:
    def __init__(self, d, order):
        self.d = d
        self.order = order
        self.entity = _entity(d)
        self.tick = _int(_first(d, "tick", "seTick"))
        self.m = _matrix(_first(d, "seMatrix", "matrix", "worldMatrix"))
        self.pivot = _vec(_first(d, "pivot", "pStar", "P*", "pivotSe") or [])
        k = _first(d, "k", "scale")
        self.k = float(k) if isinstance(k, (int, float)) else 1.0
        self.site, self.site_text = make_site(d)
        self.local = _corners(d, ("localCorners", "cornersSe", "localCornersSe"),
                              (("localMin", "aabbMin", "boxMin"), ("localMax", "aabbMax", "boxMax")))
        self.wire = d.get("wire") if isinstance(d.get("wire"), dict) else None
        self.ok = self.m is not None and self.pivot is not None
        self.pose = W.wire_pose(self.site, self.m, self.pivot) if self.ok else None

class SnPlacement:
    def __init__(self, d, order):
        self.d = d
        self.order = order
        self.entity = _entity(d)
        self.tick = _int(_first(d, "seTick", "snapshotTick", "poseTick", "presentedSeTick", "tick"))
        c = _first(d, "corners", "cornersSn", "worldCorners", "snCorners")
        self.corners = [_vec(p) for p in c] if isinstance(c, (list, tuple)) and len(c) == 8 else None
        if self.corners is not None and any(p is None for p in self.corners):
            self.corners = None
        self.local = _corners(d, ("localCorners", "cornersSe", "localCornersSe"),
                              (("localMin", "aabbMin", "boxMin"), ("localMax", "aabbMax", "boxMax")))
        self.pose = d.get("pose") if isinstance(d.get("pose"), dict) else None

def collect(records):
    se, sn = [], []
    for i, rec in enumerate(records):
        got = normalise(rec)
        if got is None:
            continue
        name, data = got
        if name in SE_EVENTS:
            se.append(SePose(data, i))
        elif name in SN_EVENTS:
            sn.append(SnPlacement(data, i))
    return se, sn

def match(sn_rec, se_list):
    cands = [p for p in se_list if p.ok and p.entity == sn_rec.entity]
    if not cands:
        return None
    if sn_rec.tick is not None and all(p.tick is not None for p in cands):
        before = [p for p in cands if p.tick <= sn_rec.tick]
        if before:
            return max(before, key=lambda p: (p.tick, p.order))
        return min(cands, key=lambda p: (p.tick, p.order))
    before = [p for p in cands if p.order <= sn_rec.order]
    return before[-1] if before else cands[0]

# ----------------------------------------------------------------------------------------------------------- checks
def row(rows, cid, status, measured, threshold, what, required=True, **detail):
    r = {"id": cid, "status": status, "measured": measured, "threshold": threshold, "what": what, "part": "s3",
         "required": required}
    if detail:
        r["detail"] = detail
    rows.append(r)
    return r

def _apply(pose, k, l, mutant=None):
    o, q, s = pose
    if mutant == "no_s":
        return W.sn_apply_k(o, q, 1.0, k, l)
    return W.sn_apply_k(o, q, s, k, l)

def analyze(records, tol_m=TOL_C2_M, mutant=None):
    """S3.C2 rows from raw JSONL records (mutant: None | 'no_s' for the affine-check selftest)."""
    rows = []
    se, sn = collect(records)
    se_ok = [p for p in se if p.ok]
    worst = None            # (err, entity, tick, corner)
    matched = 0
    unmatched = 0
    local_worst = None
    for rec in sn:
        if rec.corners is None:
            continue
        p = match(rec, se_ok)
        if p is None:
            unmatched += 1
            continue
        local = rec.local or p.local
        if local is None:
            unmatched += 1
            continue
        matched += 1
        if rec.local is not None and p.local is not None:
            dl = max(W.norm(W.sub(a, b)) for a, b in zip(rec.local, p.local))
            local_worst = dl if local_worst is None else max(local_worst, dl)
        for i, (l, c) in enumerate(zip(local, rec.corners)):
            err = W.norm(W.sub(c, _apply(p.pose, p.k, l, mutant)))
            if worst is None or err > worst[0]:
                worst = (err, rec.entity, rec.tick, i, p.tick)
    if matched == 0:
        row(rows, "S3.C2", "MISSING", "no matched corner record", "<= %.1f mm" % (tol_m * 1000),
            "SN corners vs Apply(o, q, s) recomputed from the logged SE matrix and P*",
            se_records=len(se), se_usable=len(se_ok), sn_records=len(sn), unmatched=unmatched)
    else:
        e, ent, tick, corner, se_tick = worst
        row(rows, "S3.C2", "PASS" if e <= tol_m else "FAIL", "%.4f mm" % (e * 1000), "<= %.1f mm" % (tol_m * 1000),
            "SN corners vs Apply(o, q, s) recomputed from the logged SE matrix and P* (wire_ref.wire_pose)",
            worst_entity=ent, worst_sn_tick=tick, worst_se_tick=se_tick, worst_corner=corner, matched=matched,
            unmatched=unmatched)
    row(rows, "S3.C2.match", "PASS" if matched > 0 else "MISSING", "%d matched, %d unmatched" % (matched, unmatched),
        "> 0", "SN corner records joined to an SE pose (same entity, latest SE tick <= SN tick)")
    # affine vs F (SE side only)
    worst_ratio = None
    worst_err = worst_bound = 0.0
    n_aff = 0
    for p in se_ok:
        local = p.local
        if local is None:
            same = [r for r in sn if r.entity == p.entity and r.local is not None]
            local = same[0].local if same else None
        if local is None:
            continue
        n_aff += 1
        for l in local:
            P = W.se_transform(p.m, W.mul(l, p.k))
            err = W.norm(W.sub(_apply(p.pose, p.k, l, mutant), p.site.se_to_sn(P)))
            bound = W.affine_vs_f_bound_at(p.site, p.pivot, P)
            ratio = err / (bound + AFFINE_SLACK_M)
            if worst_ratio is None or ratio > worst_ratio:
                worst_ratio, worst_err, worst_bound = ratio, err, bound
    if worst_ratio is None:
        row(rows, "S3.C2.affine", "MISSING", "no SE pose with corners", "ratio <= 1",
            AFFINE_WHAT)
    else:
        row(rows, "S3.C2.affine", "PASS" if worst_ratio <= 1.0 else "FAIL",
            "%.4f mm / bound %.4f mm (ratio %.3f)" % (worst_err * 1000, worst_bound * 1000, worst_ratio), "ratio <= 1",
            AFFINE_WHAT,
            poses=n_aff)
    if local_worst is not None:
        row(rows, "S3.C2.local", "PASS" if local_worst <= LOCAL_TOL_M else "FAIL", "%.2e m" % local_worst,
            "<= %.0e m" % LOCAL_TOL_M, "SE and SN logged the same SE-local corners", required=False)
    # the SE plugin's own wire pose vs this recomputation
    wo = wq = ws = None
    for p in se_ok:
        if not p.wire:
            continue
        o = _vec(p.wire.get("o") or [])
        q = _vec(p.wire.get("q") or [], 4)
        s = p.wire.get("s")
        if o is None or q is None or not isinstance(s, (int, float)):
            continue
        o2, q2, s2 = p.pose
        n = math.sqrt(sum(c * c for c in q))
        q = tuple(c / n for c in q) if n > 0 else q
        wo = max(wo or 0.0, W.norm(W.sub(o, o2)))
        wq = max(wq or 0.0, W.qdist(q, q2))
        ws = max(ws or 0.0, abs(float(s) - s2))
    if wo is not None:
        good = wo <= WIRE_TOL_O_M and wq <= WIRE_TOL_Q and ws <= WIRE_TOL_S
        row(rows, "S3.C2.wire", "PASS" if good else "FAIL", "o %.2e m, q %.1e, s %.1e" % (wo, wq, ws),
            "2e-3 m, 1e-6, 1e-6", "SE plugin's (o, q, s) vs wire_ref.wire_pose of its logged matrix and P*", required=False)
    return rows

def verdict(rows, strict=True):
    bad = BAD if strict else ("FAIL",)
    return not any(r.get("required", True) and r["status"] in bad for r in rows)

def table(rows):
    out = ["%-14s %-8s %-40s %-14s %s" % ("check", "status", "measured", "threshold", "what"), "-" * 130]
    for r in rows:
        out.append("%-14s %-8s %-40s %-14s %s" % (r["id"], r["status"] + ("" if r.get("required", True) else "*"),
                                                  str(r["measured"])[:40], str(r["threshold"])[:14], r["what"]))
    return "\n".join(out)

def inputs(args):
    files, folder = [], None
    for a in args:
        if os.path.isdir(a):
            folder = a
            files += sorted(glob.glob(os.path.join(a, "*.jsonl")))
        else:
            files.append(a)
    return files, folder

# ----------------------------------------------------------------------------------------------------------- selftest
def _f32(x):
    return struct.unpack("f", struct.pack("f", x))[0]

def _f32v(v):
    return tuple(_f32(c) for c in v)

def synthetic(mutant=None, offset=(0.0, 0.0, 0.0), entity="73014444032", site=None):
    """SE + SN records of the S3a lab block as the plugins would log them. mutant: None | 'S3.M1' (Subnautica drops the
 Z mirror); offset: added to every SN corner (metres)."""
    site = site or site_v1.SITE_V1
    recs = []
    lo, hi = (-1.25, -1.25, -1.25), (1.25, 1.25, 1.25)
    local = W.aabb_corners(lo, hi)
    m = W.lab_block(site)
    # two SE poses: camera P1 (6 m in front) at tick 100, camera above (P3, 10 m up) at tick 200
    for tick, cam_l in ((100, (0.0, 0.0, -7.25)), (200, (0.5, 11.25, 0.0))):
        cam = W.se_transform(m, cam_l)
        P = W.pivot_nearest(m, lo, hi, cam)
        o, q, s = W.wire_pose(site, m, P)
        recs.append({"ev": "s3.pose", "entityId": entity, "tick": tick,
                     "seMatrix": {"right": list(m[0]), "up": list(m[1]), "backward": list(m[2]), "translation": list(m[3])},
                     "pivot": list(P), "k": 1.0, "site": {"C": list(site.C), "Rs": site.Rs},
                     "wire": {"o": list(_f32v(o)), "q": list(_f32v(q)), "s": _f32(s)}, "localCorners": [list(c) for c in local]})
        # Subnautica: the float32 wire pose, its own float transforms (emulated: float32 corner results)
        o32, q32, s32 = _f32v(o), _f32v(q), _f32(s)
        corners = []
        for c in local:
            if mutant == "S3.M1":
                w = W.add(o32, W.scale_h(W.qrot(q32, c), s32))            # Mirror dropped
            else:
                w = W.sn_apply_k(o32, q32, s32, 1.0, c)
            corners.append(list(_f32v(W.add(w, offset))))
        recs.append({"event": "s3.placement", "data": {"entityId": entity, "seTick": tick + 3, "corners": corners,
                                                       "localCorners": [list(c) for c in local],
                                                       "pose": {"o": list(o32), "q": list(q32), "s": s32, "k": 1.0}}})
    # a text-embedded duplicate of the first SE record (the plugins' "msg" form) must parse the same
    recs.append({"msg": "s3.pose " + json.dumps(recs[0], separators=(",", ":"))})
    return recs

def selftest():
    ok = True

    def expect(name, rows, cid, status):
        nonlocal ok
        r = next((x for x in rows if x["id"] == cid), None)
        got = r["status"] if r else "absent"
        good = got == status
        ok &= good
        print("[%s] %s: %s is %s (expected %s) %s" % ("PASS" if good else "FAIL", name, cid, got, status,
                                                      r["measured"] if r else ""))

    rows = analyze(synthetic())
    print(table(rows))
    expect("clean S3a block", rows, "S3.C2", "PASS")
    expect("clean S3a block", rows, "S3.C2.affine", "PASS")
    expect("clean S3a block", rows, "S3.C2.match", "PASS")
    expect("clean S3a block", rows, "S3.C2.local", "PASS")
    expect("clean S3a block", rows, "S3.C2.wire", "PASS")
    expect("seeded S3.M1 (Z mirror dropped)", analyze(synthetic(mutant="S3.M1")), "S3.C2", "FAIL")
    expect("seeded 2 mm offset", analyze(synthetic(offset=(0.002, 0.0, 0.0))), "S3.C2", "FAIL")
    expect("seeded 2 mm vertical offset", analyze(synthetic(offset=(0.0, -0.002, 0.0))), "S3.C2", "FAIL")
    expect("0.5 mm offset stays inside 1 mm", analyze(synthetic(offset=(0.0003, 0.0003, 0.0003))), "S3.C2", "PASS")
    expect("seeded s dropped (affine check measures)", analyze(synthetic(), mutant="no_s"), "S3.C2.affine", "FAIL")
    expect("no SN records", analyze([r for r in synthetic() if (r.get("ev") or r.get("event")) != "s3.placement"]),
           "S3.C2", "MISSING")
    # tick matching: an SN record at tick 103 must use the SE pose of tick 100, not 200 (different P*)
    recs = synthetic()
    shifted = [r for r in recs if r.get("event") == "s3.placement"]
    shifted[0]["data"]["seTick"] = 203        # pose of tick 100 now claimed at 203 -> matched to tick 200 -> wrong P*
    rows_m = analyze(recs)
    r = next(x for x in rows_m if x["id"] == "S3.C2")
    moved = float(r["measured"].split()[0])
    good = moved > 0.0 and r["detail"]["worst_se_tick"] == 200
    ok &= good
    print("[%s] tick matching: an SN record claiming tick 203 joins the SE pose of tick 200 (corner error then %.4f mm)"
          % ("PASS" if good else "FAIL", moved))
    # the lab numbers for the record
    site = site_v1.SITE_V1
    m = W.lab_block(site)
    cam = W.se_transform(m, (0.0, 0.0, -7.25))
    P = W.pivot_nearest(m, (-1.25,) * 3, (1.25,) * 3, cam)
    o, q, s = W.wire_pose(site, m, P)
    print("lab block: o=%s s=%.10f; rigid-draw error at a corner %.2f mm (what a missing Frame scale would cost)"
          % (tuple(round(c, 4) for c in o), s, max(W.rigid_draw_error(q, s, 1.0, c) for c in W.aabb_corners((-1.25,) * 3, (1.25,) * 3)) * 1000))
    print("CHECK_PLACEMENT SELFTEST " + ("PASS" if ok else "FAIL"))
    return ok

def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    if "--selftest" in argv:
        return 0 if selftest() else 1
    as_json = "--json" in argv
    lenient = "--lenient" in argv
    tol = TOL_C2_M
    if "--tol-mm" in argv:
        i = argv.index("--tol-mm")
        tol = float(argv[i + 1]) / 1000.0
        del argv[i:i + 2]
    args = [a for a in argv if not a.startswith("--")]
    if not args:
        print(__doc__)
        return 2
    files, folder = inputs(args)
    rows = analyze(read_jsonl(files), tol_m=tol)
    good = verdict(rows, strict=not lenient)
    res = {"tool": "check_placement", "ok": good, "files": files, "rows": rows}
    if folder:
        with open(os.path.join(folder, "check_placement-results.json"), "w", encoding="utf-8") as f:
            json.dump(res, f, indent=1, default=str)
    if as_json:
        print(json.dumps(res, indent=1, default=str))
    else:
        print(table(rows))
        print("S3.C2 " + ("PASS" if good else "FAIL"))
    return 0 if good else 1

if __name__ == "__main__":
    sys.exit(main())
