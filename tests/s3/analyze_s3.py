"""S3 "First block" oracle table: joins the SE and Subnautica logs, the launcher's session log and the captures of one
collected session folder (tests/out/s3/<stamp>/, written by `launcher.py s3`) into the rows of ROADMAP S3 (C1-C14,
G1-E2, N1-N3). Stdlib only; the image rows come from tests/s3/image_checks.py, S3.C2 from tests/s3/check_placement.py
when it is present (this file keeps an independent fallback built on tools/worldmap/wire_ref.py).

 python -I tests/s3/analyze_s3.py DIR [--s1-results S1_RESULTS.json] [--baseline-logs DIR] [--lenient]
 python -I tests/s3/analyze_s3.py --selftest

S3 LOG CONTRACT (what the rows read; one JSON object per line; the event name in "ev" (SE plugin, stand-ins) or "event"
(SN plugin); the payload top-level or in "data"; or a text line in "msg"/"text" starting with the prefix below and made of
key=value tokens, vectors as (x, y, z); grid ids decimal or 0x-hex). Both forms are accepted for every event:
SE side (se-*.jsonl, or the stand-in's se-standin.jsonl):
 s3.place | DE.S3 place gridId, labSn, headingDeg, seMatrix (12 floats: rows Right, Up, Backward, Translation),
 pivot, o, q, s, k, bounds (grid-local SE AABB: 3 min, 3 max), parts, verts, tris (totals
 of the grid's parts: S3.C1), faces / glyph / plate (stand-in and S3b extras)
 s3.pose | DE.S3 pose tick, gridId, seMatrix, pivot, o, q, s, k (whenever the published pose changes: P* follows
 the camera)
 s3.model | DE.S3 model modelKey, verts, tris (MyModel.GetVerticesCount/GetTrianglesCount in S3b)
 s3.gps | DE.S3 gps tick, qpc, px (SE GPS marker of the block centre, top-left pixels; S3.N2)
 s3.pixel | DE.S3 pixel label, tick, sePx, pointSe, pointSn (S3.N3; also the de.s3 / de.pixel reply)
Subnautica side (sn-*.jsonl):
 s3.mirror | DE.Mirror built grid, parts, verts, tris (ROADMAP C1 literal)
 s3.winding | DE.Mirror winding model, agree, total, frac (raw measure, before any flip), flipped
 s3.material | DE.Material grid, shader, supported, keywords, planned (optional), layer, skyApplier, sky,
 frames (frames since the SkyApplier started; the sky is judged at >= 2)
 s3.corners | DE.S3 corners grid, tick (SE tick of the pose used), c0..c7 (SN world position of the SE-local
 corner i of the grid AABB, i = bit0 x, bit1 y, bit2 z, through the plugin's own
 mirrored mesh and renderer.localToWorldMatrix), o, q, s (the pose it used)
 s3.occlusion| DE.S3 occlusion corner, hitLayer (first hit of the camera ray to a buried corner), buried
 s3.shear | DE.S3 shear s, steps, maxErrM, mode (frame | rigid), axis
 s3.n2 | DE.S3 n2 frame, qpc (onPreCull), seTick (presented), px (Camera.WorldToScreenPoint of the
 block centre, top-left)
 s3.pixel | DE.S3 pixel label, seTick, px (S3.N3; DE.SnPixel of S1 is accepted too)
 s3.frames | DE.S3 frames label (without | with), n, meanMs (S3.C11)
 s3.teardown | DE.S3 teardown phase (before | after), roots, meshes, baseline (S3.C12)
 s3.lab | DE.S3 lab seamothMin, seamothMax (Renderer.bounds of the reference Seamoth), seamothPos,
 seabedY
Launcher session (s3-session.jsonl): start, part, reply (every C11 command and reply), place, lab, seabed, capture
{name, target, spot, time, png, reply}, labrun {session, spot, tag, captures, status, cpu [{labOk, cpu}]}, cpu {label,
seconds}, perf {label, samples}, snFrameTime, n2 {phase}, pixel {label, look, se, sn}, teardown, end.
The Subnautica plugin's own forms are read too: "DE.Mirror built... gridVerts= gridTris=" (C1),
"DE.Mirror model key=... winding=99.9% flipped=" (C3), "DE.Mirror material created|final grid= shader= supported=
keywords=[..] planned=[..] layer= skyApplier= applySky= framesSinceCreate=" (C4, final lines), "DE.Mirror placed entity=0x..
tick= seLocalMin=x,y,z seLocalMax=x,y,z seLocalCorners=x,y,z;.. cornersSn=x,y,z;..." (C2: 8 drawn vertices, the most extreme
along each corner direction, with their independent SE-local positions; handed to check_placement.py as s3.placement
records),
"DE.S3 SHEAR s= frames= maxDevLinearM= (MUTANT S3.M7)" (G1-E2), "DE.S3 FRAMETIME... meanMs= cpuMsPerFrame=
mirrorVisible=" (C11f judges the main-thread CPU per frame: wall frame times are flat under vsync or a frame cap),
"DE.Mirror texture fidelity role= target= maxErr= verdict=" (S3.N1d), "DE.Mirror model... tangentU= binormalV=" (S3.N1t),
"DE.S3 TEARDOWN" then "DE.S3 COUNT rootsAlive= preserved= rootMarkersInScenes= ourMeshesAlive=" (C12), and the lab runs'
captures <out>/captures/<session>/<label>_f<frame>.png with <png>.json (the Unity camera) and <png>.s3.json (block pose,
buried-corner rays for C5, Seamoth bounds, the sun's forward for C9/N1, lab.seabedY). Without Subnautica's per-frame
s3.n2 lines S3.N2 projects the block centre through Subnautica's S1 camera trace (s1.cam / DE.CamTrace, de.trace on).
"""
from __future__ import annotations

import glob
import json
import math
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tests", "wire"),
           os.path.join(ROOT, "tools", "worldmap"), os.path.join(ROOT, "tests", "standins")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C          # noqa: E402 (rows, table, verdict, key=value parser)
import image_checks as IC      # noqa: E402

S1N2_DEFAULT_MS = 5.08         # S1. p95 7.65 ms)
SHEAR_SCALES = (1.0284, 1.0345)
SHEAR_MAX_ERR_M = 1e-4
C2_TOL_M = 1e-3
C11_MAX_DELTA_MS = 0.3
N2_REST_PX = 2.0
N3_LEVEL_PX = 0.5
# S3 mutants and the rows each must turn red (ROADMAP S3 "Seeded failures"); the launcher's runbook groups use this
MUTANT_ROWS = {"S3.M1": ("S3.C2", "S3.C3"), "S3.M2": ("S3.C3",), "S3.M3": ("S3.C4",), "S3.M4": ("S3.N2m",),
               "S3.M5": ("S3.C10",), "S3.M6": ("S3.C12",), "S3.M7": ("S3.G1-E2",), "S3.M8": ("S3.N1d",)}
N1D_MAX_ERR = 1.0 / 255.0 + 1e-4      # TextureRepack.FidelityMaxError
N1T_MIN = 0.99

TEXT_RULES = (("DE.Mirror built", "s3.mirror"), ("DE.Mirror winding", "s3.winding"), ("DE.Material", "s3.material"),
              ("DE.S3 corners", "s3.corners"), ("DE.S3 occlusion", "s3.occlusion"), ("DE.S3 teardown", "s3.teardown"),
              ("DE.S3 shear", "s3.shear"), ("DE.S3 n2", "s3.n2"), ("DE.S3 gps", "s3.gps"), ("DE.S3 pixel", "s3.pixel"),
              ("DE.S3 place", "s3.place"), ("DE.S3 pose", "s3.pose"), ("DE.S3 model", "s3.model"),
              ("DE.S3 frames", "s3.frames"), ("DE.S3 lab", "s3.lab"), ("DE.SnPixel", "s3.pixel"),
              # the Subnautica plugin's own S3 lines
              ("DE.Mirror model", "s3.winding"), ("DE.Mirror material", "s3.material"), ("DE.Mirror placed", "s3.corners"),
              ("DE.S3 SHEAR", "s3.shear"), ("DE.S3 FRAMETIME", "s3.frames"), ("DE.S3 COUNT", "s3.count"),
              ("DE.S3 CAPTURE", "s3.capture"), ("DE.S3 PROBE", "s3.probe"), ("DE.S3 TEARDOWN", "s3.teardownDone"),
              ("DE.Mirror texture fidelity", "s3.fidelity"))

def _pct(v):
    if isinstance(v, (int, float)) and not isinstance(v, bool):
        return float(v)
    if isinstance(v, str) and v.endswith("%"):
        try:
            return float(v[:-1])
        except ValueError:
            return None
    return None

def _words(v):
    """A keyword list written as [A B C], A,B,C or "A B" -> set of words."""
    if isinstance(v, (list, tuple)):
        return set(str(x) for x in v)
    t = str(v or "").strip().strip("[]")
    return set(w for w in re.split(r"[,; ]+", t) if w)

def _triples(v):
    """"x,y,z;x,y,z;..." or a list -> [(x, y, z),...]."""
    if isinstance(v, list):
        if len(v) == 3 and all(isinstance(x, (int, float)) for x in v):
            return [tuple(float(x) for x in v)]
        return [tuple(float(x) for x in t) for t in v if isinstance(t, (list, tuple)) and len(t) == 3]
    out = []
    for part in str(v or "").split(";"):
        xs = [x for x in part.strip().strip("()").split(",") if x.strip()]
        if len(xs) == 3:
            try:
                out.append(tuple(float(x) for x in xs))
            except ValueError:
                return []
    return out

def adapt(name, data, text):
    """The Subnautica plugin's line formats onto the field names the rows read (both forms stay accepted)."""
    t = text or ""
    if name == "s3.winding":
        w = _pct(data.get("winding"))
        if w is not None and data.get("frac") is None:
            data["frac"] = w / 100.0
        data.setdefault("model", data.get("key"))
    elif name == "s3.material":
        data["keywords"] = sorted(_words(data.get("keywords")))
        data["planned"] = sorted(_words(data.get("planned")))
        data.setdefault("sky", data.get("applySky"))
        data.setdefault("frames", data.get("framesSinceCreate"))
        data["phase"] = t.split()[2] if t.startswith("DE.Mirror material ") and len(t.split()) > 2 else None
    elif name == "s3.corners":
        if data.get("cornersSn") is not None and not isinstance(data.get("corners"), list):
            data["corners"] = [list(c) for c in _triples(data["cornersSn"])]
        data.setdefault("grid", data.get("entity"))
        for k in ("seLocalMin", "seLocalMax"):
            tr = _triples(data.get(k)) if data.get(k) is not None else []
            if tr:
                data[k] = list(tr[0])
        if data.get("seLocalCorners") is not None and not isinstance(data.get("localCorners"), list):
            tr = _triples(data["seLocalCorners"])
            if len(tr) == 8:
                data["localCorners"] = [list(c) for c in tr]
    elif name == "s3.shear":
        data.setdefault("steps", data.get("frames"))
        data.setdefault("maxErrM", data.get("maxDevLinearM"))
        if "MUTANT S3.M7" in t:
            data["mode"] = "rigid"
    elif name == "s3.frames" and data.get("label") is None and data.get("mirrorVisible") is not None:
        data["label"] = "with" if C.flag(data, "mirrorVisible") else "without"
    return data

# ----------------------------------------------------------------------------------------------------------------
# Parsing
# ----------------------------------------------------------------------------------------------------------------

def parse_record(rec: dict):
    """One JSONL record -> (event name, data) for the S3 events, else None."""
    name = rec.get("ev") or rec.get("event")
    data = None
    if isinstance(name, str) and name.startswith("s3."):
        data = dict(rec["data"]) if isinstance(rec.get("data"), dict) else \
            {k: v for k, v in rec.items() if k not in ("ev", "event", "data")}
    text = rec.get("msg") if isinstance(rec.get("msg"), str) else rec.get("text")
    if isinstance(text, str):
        for prefix, ev in sorted(TEXT_RULES, key=lambda r: -len(r[0])):
            if text.startswith(prefix + " ") or text == prefix:
                kv = C._kv(text[len(prefix):])
                if data is None:
                    name, data = ev, kv
                else:
                    for k, v in kv.items():
                        data.setdefault(k, v)
                break
    if data is None or not isinstance(name, str) or not name.startswith("s3."):
        return None
    data = adapt(name, data, text if isinstance(text, str) else "")
    data["_t"] = rec.get("t")
    data["_level"] = rec.get("level")
    return name, data

def gid(v):
    if isinstance(v, bool) or v is None:
        return None
    if isinstance(v, int):
        return v
    if isinstance(v, float):
        return int(v)
    s = str(v).strip()
    try:
        return int(s, 16) if s.lower().startswith("0x") else int(s)
    except ValueError:
        return None

def vec(d, *names, n=3):
    for k in names:
        v = d.get(k) if isinstance(d, dict) else None
        if isinstance(v, (list, tuple)) and len(v) >= n and all(isinstance(x, (int, float)) for x in v[:n]):
            return tuple(float(x) for x in v[:n])
    return None

def num(d, *names):
    return C.num(d, *names)

class Logs:
    def __init__(self, folder: str):
        self.folder = folder
        self.se_files = sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        self.sn_files = sorted(f for f in glob.glob(os.path.join(folder, "sn-*.jsonl")))
        self.se = [e for e in (parse_record(r) for r in C.read_jsonl(self.se_files)) if e]
        self.sn = [e for e in (parse_record(r) for r in C.read_jsonl(self.sn_files)) if e]
        self.sn_raw = C.read_jsonl(self.sn_files)
        self.se_raw = C.read_jsonl(self.se_files)
        sp = os.path.join(folder, "s3-session.jsonl")
        self.session = C.read_jsonl(sp) if os.path.exists(sp) else []

    def se_ev(self, name):
        return [d for n, d in self.se if n == name]

    def sn_ev(self, name):
        return [d for n, d in self.sn if n == name]

    def sess(self, name):
        return [r for r in self.session if r.get("ev") == name]

    def start(self):
        return next((r for r in self.session if r.get("ev") == "start"), {})

def site_from(logs: Logs):
    import worldmap_ref as wm
    import site_v1
    centre = None
    for r in logs.se_raw:
        if r.get("ev") == "s1.site" and isinstance(r.get("C"), list):
            centre = tuple(float(x) for x in r["C"])
        elif isinstance(r.get("msg"), str) and r["msg"].startswith("DE.Site OK"):
            m = re.search(r"C=\(([-0-9.eE+]+),\s*([-0-9.eE+]+),\s*([-0-9.eE+]+)\)", r["msg"])
            if m:
                centre = tuple(float(x) for x in m.groups())
    anc = site_v1.ANCHOR
    return wm.Site(C=centre or tuple(site_v1.TEST_C), Rs=site_v1.RS_EXPECTED, U0=anc["U0"], N0=anc["N0"],
                   psi_deg=anc["psi_deg"], O=anc["O"])

def rows12(m):
    if m is None or len(m) != 12:
        return None
    return (tuple(m[0:3]), tuple(m[3:6]), tuple(m[6:9]), tuple(m[9:12]))

# ----------------------------------------------------------------------------------------------------------------
# Rows
# ----------------------------------------------------------------------------------------------------------------

def last_place(logs: Logs, before_name=None):
    pl = logs.se_ev("s3.place")
    return pl[-1] if pl else None

def c1_rows(logs: Logs, rows):
    pl = logs.se_ev("s3.place")
    what = "geometry arrived: DE.Mirror built verts/tris = the producer's counts"
    if not pl:
        C.row(rows, "S3.C1", "MISSING", "no s3.place (SE)", "equal", what, "geometry")
        return
    p = pl[-1]
    g = gid(p.get("gridId", p.get("grid")))
    ev, et = num(p, "verts"), num(p, "tris")
    if ev is None:
        models = logs.se_ev("s3.model")
        ev = sum(num(m, "verts") or 0 for m in models) or None
        et = sum(num(m, "tris") or 0 for m in models) or None
    mirrors = [m for m in logs.sn_ev("s3.mirror") if g is None or gid(m.get("grid")) in (g, None)]
    if not mirrors:
        C.row(rows, "S3.C1", "MISSING", "no DE.Mirror built (SN); SE %s/%s" % (ev, et), "equal", what, "geometry")
        return
    m = mirrors[-1]
    # the model totals (sum of the parts' model counts, what SE sums from MyModel) when the line has them; the chunk
 # totals otherwise
    sv, st = num(m, "gridModelVerts", "gridVerts", "verts"), num(m, "gridModelTris", "gridTris", "tris")
    # drawn triangles are exact (each model triangle once, Wire's coverage rule): the chunk total must equal the model
 # total; vertices may differ (a chunk copies only the vertices its submesh uses)
    drawn = num(m, "gridTris") if num(m, "gridModelTris") is not None else None
    ok = sv == ev and st == et and ev is not None and (drawn is None or drawn == st)
    C.row(rows, "S3.C1", "PASS" if ok else "FAIL", "SN %s/%s vs SE %s/%s%s" % (sv, st, ev, et, "" if drawn is None else
          ", drawn tris %s" % drawn), "equal", what + "; drawn triangles = model triangles", "geometry",
          grid=hex(g) if g else None, mirrorLines=len(mirrors), drawnTris=drawn, uncoveredTris=num(m, "uncoveredTris"),
          overlapTris=num(m, "overlapTris"))

def _pose_at(logs: Logs, tick):
    poses = logs.se_ev("s3.pose") or logs.se_ev("s3.place")
    best = None
    for p in poses:
        t = num(p, "tick")
        if tick is None or t is None or t <= tick:
            best = p
    return best or (poses[-1] if poses else None)

def c2_fallback(logs: Logs, rows, site=None):
    """S3.C2 recomputed here with wire_ref (used when tests/s3/check_placement.py is absent or not runnable)."""
    import wire_ref
    what = "placement: SN corners vs Apply(o, q, s) from the SE matrix and P* (wire_ref), 1 mm"
    cl = logs.sn_ev("s3.corners") + logs.sn_ev("s3.placement")
    if not cl:
        C.row(rows, "S3.C2", "MISSING", "no DE.S3 corners (SN)", "<= 1 mm", what, "geometry")
        return
    site = site or site_from(logs)
    worst, worst_set, worst_aff, worst_ratio, n = 0.0, 0.0, 0.0, 0.0, 0
    for c in cl:
        corners = [vec(c, "c%d" % i) for i in range(8)]
        if None in corners and isinstance(c.get("corners"), list):
            corners = [tuple(x) for x in c["corners"]]
        if None in corners or len(corners) != 8:
            continue
        p = _pose_at(logs, num(c, "seTick", "tick"))
        m, P = rows12(p.get("seMatrix")) if p else None, vec(p, "pivot") if p else None
        b = p.get("bounds") if p else None
        if (not isinstance(b, (list, tuple)) or len(b) != 6) and p and vec(p, "localMin") and vec(p, "localMax"):
            b = list(vec(p, "localMin")) + list(vec(p, "localMax"))
        if m is None or P is None or not isinstance(b, (list, tuple)) or len(b) != 6:
            continue
        o, q, s = wire_ref.wire_pose(site, m, P)
        lo, hi = b[:3], b[3:]
        for i, sg in enumerate(IC.CORNER_SIGNS):
            l_ = tuple(lo[k] if sg[k] < 0 else hi[k] for k in range(3))
            exp = wire_ref.sn_apply(o, q, s, l_)
            err = math.dist(exp, corners[i])
            worst = max(worst, err)
            exact = site.se_to_sn(wire_ref.se_transform(m, l_))
            d = math.dist(wire_ref.se_transform(m, l_), P)
            r = math.dist(P, site.C)
            th = math.hypot(exact[0], exact[2]) / site.Rs
            bound = d * d / (math.sqrt(3) * (r - d)) + (th / math.sin(th) - 1.0 if th > 0 else 0.0) * s * d
            aff = math.dist(exp, exact)
            worst_aff = max(worst_aff, aff)
            worst_ratio = max(worst_ratio, aff / bound if bound > 0 else 0.0)
        exp_all = [wire_ref.sn_apply(o, q, s, tuple(lo[k] if sg[k] < 0 else hi[k] for k in range(3)))
                   for sg in IC.CORNER_SIGNS]
        worst_set = max(worst_set, max(min(math.dist(a, b2) for b2 in exp_all) for a in corners))
        n += 1
    if not n:
        C.row(rows, "S3.C2", "MISSING", "corners without a usable SE pose", "<= 1 mm", what, "geometry")
        return
    note = ""
    if worst > C2_TOL_M and worst_set <= C2_TOL_M:
        note = " (matches only as a set: corner order differs, or the mirror is missing; a symmetric box hides S3.M1)"
    C.row(rows, "S3.C2", "PASS" if worst <= C2_TOL_M else "FAIL", "%.3g mm (%d lines)%s" % (worst * 1e3, n, note),
          "<= 1 mm", what + " [fallback: analyze_s3]", "geometry", worstM=worst, worstAsSetM=worst_set)
    C.row(rows, "S3.C2b", "PASS" if worst_ratio <= 1.02 else "FAIL", "%.3g mm, %.2f of the bound" % (
        worst_aff * 1e3, worst_ratio), "<= bound", "placement: affine vs exact F within d^2/(sqrt3 r) + (theta/sin "
                                                   "theta - 1) s d", "geometry")

def placement_records(logs: Logs) -> list:
    """The Subnautica plugin's "DE.Mirror placed entity=0x.. tick=.. seLocalMin=.. seLocalMax=.. cornersSn=x,y,z;.." lines
 as check_placement.py's SN s3.placement records (entityId in decimal, as the SE s3.pose rows write it)."""
    out = []
    for d in logs.sn_ev("s3.corners"):
        g = gid(d.get("entity", d.get("grid")))
        cs = d.get("corners")
        if g is None or not isinstance(cs, list) or len(cs) != 8:
            continue
        rec = {"entityId": str(g), "seTick": num(d, "tick", "seTick"), "corners": cs}
        if isinstance(d.get("localCorners"), list) and len(d["localCorners"]) == 8:
            rec["localCorners"] = d["localCorners"]        # the drawn vertices' SE-local positions (finding 2)
        elif isinstance(d.get("seLocalMin"), list) and isinstance(d.get("seLocalMax"), list):
            rec["localMin"], rec["localMax"] = d["seLocalMin"], d["seLocalMax"]
        out.append({"event": "s3.placement", "data": rec})
    return out

def c2_rows(logs: Logs, rows, site=None):
    """S3.C2 from tests/s3/check_placement.py (the wire tool's oracle: analyze(records) over the SE s3.pose and the
 Subnautica s3.placement records of every JSONL of the folder); this file's own recomputation runs too and is shown
 as the not-required "-fb" rows (it also reads the DE.S3 corners text form). Without check_placement.py the
 fallback rows are the required ones."""
    cp = os.path.join(HERE, "check_placement.py")
    got = None
    if os.path.exists(cp):
        try:
            import check_placement      # noqa: E402
            recs = C.read_jsonl(logs.se_files + logs.sn_files)
            if not any((r.get("ev") or r.get("event")) == "s3.placement" for r in recs):
                recs += placement_records(logs)
            got = check_placement.analyze(recs)
        except Exception as e:  # noqa: BLE001 - reported in the row, the fallback still runs
            C.row(rows, "S3.C2", "FAIL", "check_placement.analyze error %s" % type(e).__name__, "-", str(e)[:100],
                  "geometry")
            got = []
    if got is not None:
        for r in got:
            rows.append(dict(r, part="geometry", required=r.get("required", True)))
        sub = []
        c2_fallback(logs, sub, site)
        for r in sub:
            rows.append(dict(r, id=r["id"] + "-fb", required=False))
        return
    c2_fallback(logs, rows, site)

def c3_rows(logs: Logs, rows):
    what = "winding: > 99 % of triangles agree with their vertex normals (raw measure, no automatic flip)"
    w = logs.sn_ev("s3.winding")
    if not w:
        C.row(rows, "S3.C3", "MISSING", "no DE.Mirror winding (SN)", "> 99 %", what, "geometry")
        return
    worst, flipped = 1.0, False
    for d in w:
        f = num(d, "frac", "agreeFrac")
        if f is None and num(d, "agree") is not None and num(d, "total"):
            f = num(d, "agree") / float(num(d, "total"))
        if f is not None and f > 1.0:
            f /= 100.0
        if f is not None:
            worst = min(worst, f)
        flipped = flipped or bool(C.flag(d, "flipped"))
    ok = worst > 0.99 and not flipped
    C.row(rows, "S3.C3", "PASS" if ok else "FAIL", "worst %.2f %%%s (%d models)" % (100 * worst, ", auto-flipped" if
          flipped else "", len(w)), "> 99 %", what, "geometry")

def c4_rows(logs: Logs, rows):
    what = "material: MarmosetUBER, supported, planned keywords, layer 0, SkyApplier with a sky after 2 frames"
    ms = logs.sn_ev("s3.material")
    # the plugin logs each chunk's material when it is made and again when its maps are final: judge the final lines
    final = [d for d in ms if d.get("phase") == "final"]
    ms = final or ms
    if not ms:
        C.row(rows, "S3.C4", "MISSING", "no DE.Mirror material / DE.Material (SN)", "all", what, "material")
        return
    bad = []
    for d in ms:
        shader = str(d.get("shader", ""))
        kws = _words(d.get("keywords"))
        planned = _words(d.get("planned"))
        sky = d.get("sky")
        frames = num(d, "frames", "skyFrames")
        if shader != "MarmosetUBER":
            bad.append("shader %s" % shader)
        if C.flag(d, "supported", "isSupported") is not True:
            bad.append("not supported")
        if planned and not planned <= kws:
            bad.append("keywords missing %s" % sorted(planned - kws))
        if num(d, "layer") != 0:
            bad.append("layer %s" % d.get("layer"))
        if C.flag(d, "skyApplier") is not True:
            bad.append("no SkyApplier")
        if sky in (None, "", "null", "None") or (frames is not None and frames < 2):
            bad.append("sky %s after %s frames" % (sky, frames))
    C.row(rows, "S3.C4", "PASS" if not bad else "FAIL", "; ".join(bad)[:34] if bad else "%d materials ok" % len(ms),
          "all", what, "material", problems=bad)

def c5_ray_rows(logs: Logs, rows):
    what = "terrain occludes (analytic): camera rays to the buried corners hit layer 30 first"
    oc = logs.sn_ev("s3.occlusion")
    # the lab sidecars (<png>.s3.json, block.buried: one ray per buried corner, terrainFirst)
    for sc in sidecars_s3(logs.folder):
        if sc.get("pose") not in ("P2", None):
            continue
        for b in ((sc.get("block") or {}).get("buried") or ()):
            oc.append({"corner": b.get("corner"), "hitLayer": 30 if b.get("terrainFirst") else -1, "buried": True})
    if not oc:
        C.row(rows, "S3.C5r", "MISSING", "no DE.S3 occlusion (SN)", "layer 30", what, "views")
        return
    bad = [d for d in oc if C.flag(d, "buried") is not False and num(d, "hitLayer") != 30]
    C.row(rows, "S3.C5r", "PASS" if not bad else "FAIL", "%d/%d rays hit layer 30" % (len(oc) - len(bad), len(oc)),
          "layer 30", what, "views")

def sidecars_s3(folder: str) -> list:
    out = []
    for p in sorted(glob.glob(os.path.join(folder, "captures", "**", "*.s3.json"), recursive=True)):
        try:
            with open(p, encoding="utf-8") as f:
                d = json.load(f)
            d["_path"] = p
            out.append(d)
        except (OSError, ValueError):
            pass
    return out

def c11_rows(logs: Logs, rows, baseline_dir=None):
    what = "no harm: no new error lines in LogOutput.log / Player.log; ours: 0"
    files = [p for p in (os.path.join(logs.folder, "BepInEx-LogOutput.log"), os.path.join(logs.folder, "Player.log"))
             if os.path.exists(p)]
    rx = re.compile(r"\[(Error|Fatal)\b|Exception\b|^\s*at DeepEngineers|NullReference")
    ours_rx = re.compile(r"DeepEngineers|\bDE\.[A-Z]")
    sig = lambda s: re.sub(r"[0-9a-fx]{4,}|\d+", "#", s.strip())[:160]  # noqa: E731
    base = set()
    if baseline_dir:
        for n in ("BepInEx-LogOutput.log", "Player.log"):
            p = os.path.join(baseline_dir, n)
            if os.path.exists(p):
                with open(p, encoding="utf-8", errors="replace") as f:
                    base |= {sig(x) for x in f if rx.search(x)}
    ours, new, total = [], [], 0
    for p in files:
        with open(p, encoding="utf-8", errors="replace") as f:
            for line in f:
                if rx.search(line):
                    total += 1
                    if ours_rx.search(line):
                        ours.append(line.strip()[:160])
                    elif baseline_dir and sig(line) not in base:
                        new.append(line.strip()[:160])
    # the SN JSONL's own error lines (level error) count as ours
    for r in logs.sn_raw:
        if r.get("level") in ("error", "fatal"):
            ours.append(str(r.get("text", ""))[:160])
    if not files and not logs.sn_raw:
        C.row(rows, "S3.C11", "MISSING", "no game logs collected", "0 new", what, "perf")
    else:
        status = "PASS" if not ours and not new else "FAIL"
        C.row(rows, "S3.C11", status, "ours %d, new %d (all %d)%s" % (len(ours), len(new), total, "" if baseline_dir
              else ", no baseline"), "0 new", what, "perf", ours=ours[:10], new=new[:10], files=files)
    # frame time with and without the block
 #  wall frame times (unscaledDeltaTime, C7 frameMsP50) sit on the frame cap under vsync
 # or a target frame rate, so a cost that still fits the frame never shows; the delta is judged on the main thread's
 # CPU per frame (DE.S3 FRAMETIME cpuMsPerFrame: GetThreadTimes, waits not counted)
    what2 = "no harm: main-thread CPU per frame with/without the block, delta < 0.3 ms over 600 frames"
    fr, wall = {}, {}
    for d in logs.sn_ev("s3.frames"):
        lab = str(d.get("label", ""))
        if num(d, "cpuMsPerFrame") is not None:
            fr[lab] = (num(d, "cpuMsPerFrame"), num(d, "n", "frames"))
        if num(d, "meanMs") is not None:
            wall[lab] = (num(d, "meanMs"), num(d, "n", "frames"))
    if not ("with" in wall and "without" in wall):
        for r in logs.sess("perf"):
            xs = [x for x in (r.get("samples") or []) if isinstance(x, (int, float)) and x > 0]
            if xs:
                wall[str(r.get("label"))] = (sum(xs) / len(xs), len(xs))
    wall_note = ""
    if "with" in wall and "without" in wall:
        wall_note = "; wall %+.3f ms (%.3f vs %.3f)" % (wall["with"][0] - wall["without"][0], wall["with"][0],
                                                       wall["without"][0])
    if "with" in fr and "without" in fr:
        dlt = fr["with"][0] - fr["without"][0]
        C.row(rows, "S3.C11f", "PASS" if dlt < C11_MAX_DELTA_MS else "FAIL", "CPU %+.3f ms (%.3f vs %.3f)%s" % (
            dlt, fr["with"][0], fr["without"][0], wall_note), "< 0.3 ms", what2, "perf",
            samples={k: v[1] for k, v in fr.items()}, wall={k: v[0] for k, v in wall.items()})
    else:
        C.row(rows, "S3.C11f", "MISSING", "no main-thread CPU per frame (wall times only%s: flat under a frame cap)" %
              wall_note if wall else "frame times %s" % sorted(fr), "< 0.3 ms", what2, "perf")

def n1d_rows(logs: Logs, rows):
    """S3.N1d: every converted map's 8-bit conversion target keeps the sampled values
 (DE.Mirror texture fidelity: the same small blit read back from the conversion's own 8-bit target and from a float
 target, max |difference| <= 1/255). S3.M8 (an sRGB target for _ng) turns it red whatever the sun does; N1b cannot
 see it (the sRGB encode is monotonic)."""
    what = "conversion fidelity: 8-bit conversion target = float readback of the same blit within 1/255 (every map)"
    fl = logs.sn_ev("s3.fidelity")
    if not fl:
        C.row(rows, "S3.N1d", "MISSING", "no DE.Mirror texture fidelity line (SN)", "<= 1/255", what, "n1")
        return
    bad, worst, roles = [], 0.0, set()
    for d in fl:
        e = num(d, "maxErr")
        roles.add(str(d.get("role")))
        if e is None or str(d.get("verdict", "")).upper() not in ("PASS", "FAIL"):
            bad.append("%s: no measure" % d.get("role"))
            continue
        worst = max(worst, e)
        if e > N1D_MAX_ERR:
            bad.append("%s %s maxErr %.4f" % (d.get("role"), d.get("target"), e))
    C.row(rows, "S3.N1d", "PASS" if not bad else "FAIL", "worst %.5f over %d maps (%s)%s" % (
        worst, len(fl), ",".join(sorted(roles)), "; " + "; ".join(bad)[:60] if bad else ""), "<= 1/255", what, "n1",
        problems=bad)

def n1t_rows(logs: Logs, rows):
    """S3.N1t: the models' stored tangent frames against their UVs
 (DE.Mirror model... tangentU= binormalV=). The N1 glyph cube stores tangent = +u and binormal = +v; SE's armor
 models with the same census mean N1b's verdict on the glyph carries over to them (S3b shows SE's real models)."""
    what = "tangent frames: tangent along +u and binormal along +v in > 99 % of triangles (as the N1 glyph cube)"
    got = []
    for d in logs.sn_ev("s3.winding"):
        t, b = _pct(d.get("tangentU")), _pct(d.get("binormalV"))
        if t is None and isinstance(d.get("tangentFrac"), (int, float)):
            t, b = 100.0 * d["tangentFrac"], 100.0 * (d.get("binormalFrac") or 0.0)
        elif t is not None and t <= 1.0 and not str(d.get("tangentU", "")).endswith("%"):
            t, b = 100.0 * t, 100.0 * (b or 0.0)
        if t is None or b is None:
            continue
        got.append((str(d.get("model") or d.get("key")), t, b))
    if not got:
        C.row(rows, "S3.N1t", "MISSING", "no tangent census in the model lines", "> 99 %", what, "n1", required=False)
        return
    off = [g for g in got if g[1] < 100 * N1T_MIN or g[2] < 100 * N1T_MIN]
    C.row(rows, "S3.N1t", "PASS" if not off else "FAIL", "%d/%d models along (+u, +v)%s" % (
        len(got) - len(off), len(got), "; off: " + ",".join("%s %.0f/%.0f %%" % g for g in off[:3]) if off else ""),
        "> 99 %", what, "n1", required=False, models=got[:20])

def c12_rows(logs: Logs, rows):
    what = "teardown: after quit to menu and reload, our mirror roots gone and our mesh count back to baseline"
    # the plugin's de.s3 teardown, then de.s3 count (the first COUNT after the last TEARDOWN, in log order): our roots,
 # preserved roots and meshes must all be gone; the first COUNT of the session is the baseline (information)
    cnt = logs.sn_ev("s3.count")
    last_td = max((i for i, (n, _d) in enumerate(logs.sn) if n == "s3.teardownDone"), default=None)
    after = [d for i, (n, d) in enumerate(logs.sn) if n == "s3.count" and last_td is not None and i > last_td]
    if after:
        d = after[0]
        roots, pres, meshes = num(d, "rootsAlive"), num(d, "preserved"), num(d, "ourMeshesAlive")
        strays = num(d, "rootMarkersInScenes")
        ok = roots == 0 and (pres or 0) == 0 and meshes == 0 and (strays or 0) == 0
        C.row(rows, "S3.C12", "PASS" if ok else "FAIL", "roots %s, preserved %s, markers %s, our meshes %s" % (
            roots, pres, strays, meshes), "all 0", what + " (de.s3 teardown + count)", "teardown",
            meshesTotal=num(d, "meshesTotal"), baseline=num(cnt[0], "meshesTotal") if len(cnt) > 1 else None)
        return
    td = [d for d in logs.sn_ev("s3.teardown") if str(d.get("phase", "after")) == "after"]
    if not td:
        C.row(rows, "S3.C12", "MISSING", "no DE.S3 teardown phase=after (SN)", "roots 0, meshes = baseline", what,
              "teardown")
        return
    d = td[-1]
    roots, meshes, base = num(d, "roots"), num(d, "meshes"), num(d, "baseline")
    ok = roots == 0 and meshes is not None and meshes == base
    C.row(rows, "S3.C12", "PASS" if ok else "FAIL", "roots %s, meshes %s / %s" % (roots, meshes, base),
          "roots 0, meshes = baseline", what, "teardown")

def rigid_error(s, half=1.25, axis=(1.0, 0.0, 0.0), steps=360):
    """The error of drawing the Entity rigidly (no (s, 1, s) Frame) against Apply, max over the cube corners and the
 360 one-degree rotations about `axis` (what S3.M7 must show)."""
    a = IC.unit(axis)
    worst = 0.0
    for k in range(steps):
        ang = math.radians(k)
        q = (a[0] * math.sin(ang / 2), a[1] * math.sin(ang / 2), a[2] * math.sin(ang / 2), math.cos(ang / 2))
        for sg in IC.CORNER_SIGNS:
            v = IC.qrot(q, (sg[0] * half, sg[1] * half, -sg[2] * half))
            worst = max(worst, (s - 1.0) * math.hypot(v[0], v[2]))
    return worst

def g1e2_rows(logs: Logs, rows):
    what = "shear: renderer corners vs emulated Apply, Frame (s, 1, s) + Entity 360 x 1 deg, every frame < 1e-4 m"
    sh = logs.sn_ev("s3.shear")
    if not sh:
        C.row(rows, "S3.G1-E2", "MISSING", "no DE.S3 shear (SN)", "< 1e-4 m", what, "shear")
        return
    got = {}
    for d in sh:
        s = num(d, "s")
        if s is None:
            continue
        key = min(SHEAR_SCALES, key=lambda x: abs(x - s))
        if abs(key - s) < 5e-4:
            got[key] = d
    missing = [s for s in SHEAR_SCALES if s not in got]
    worst = max((num(d, "maxErrM", "maxErr") or 0.0) for d in got.values()) if got else None
    steps_ok = all((num(d, "steps") or 0) >= 360 for d in got.values())
    ok = not missing and worst is not None and worst < SHEAR_MAX_ERR_M and steps_ok
    C.row(rows, "S3.G1-E2", "PASS" if ok else ("MISSING" if missing else "FAIL"), "max %s m%s" % (
        "%.2e" % worst if worst is not None else "-", "; missing s %s" % missing if missing else ""), "< 1e-4 m", what,
        "shear", perScale={str(k): num(v, "maxErrM", "maxErr") for k, v in got.items()}, stepsOk=steps_ok)
    # S3.M7 calibration: the deviation a rigid child must show (information; red-as-designed check in the M7 run)
    for s, d in sorted(got.items()):
        ax = vec(d, "axis") or (1.0, 0.0, 0.0)
        pred = rigid_error(s, num(d, "half") or 1.25, ax)
        meas = num(d, "maxErrM", "maxErr")
        if str(d.get("mode", "frame")).lower() == "rigid" or (meas is not None and meas > 0.5 * pred):
            C.row(rows, "S3.M7x", "INFO" if meas is not None and abs(meas - pred) <= 0.1 * pred else "FAIL",
                  "%.4f m vs rigid %.4f m" % (meas or 0.0, pred), "within 10 %", "S3.M7: the deviation equals the "
                  "rigid-draw error at s %.4f (the check measures something)" % s, "shear", required=False)

def _interp_time_at_px(series, x):
    """Times at which the SE marker's x crossed `x` (linear between ticks): [(t, i)]."""
    out = []
    for i in range(1, len(series)):
        (t0, x0), (t1, x1) = series[i - 1], series[i]
        if (x0 - x) * (x1 - x) <= 0 and x0 != x1:
            out.append((t0 + (t1 - t0) * (x - x0) / (x1 - x0), i))
    return out

def computed_n2_frames(logs: Logs, gps):
    """Subnautica's block-centre pixel per frame when the plugin logged no s3.n2 lines: the drawn centre Apply(o, q, s,
 0) (the SE side's pose) projected through Subnautica's own per-frame camera trace (s1.cam of the stand-in host,
 DE.CamTrace of the plugin; tests/s1/check_wm.py parses both), at its qpcUsed. The camera path, its interpolation and
 its timing are exercised; the WorldToScreenPoint of the drawn mesh is not (S1's computed-pixel convention)."""
    s1 = os.path.join(ROOT, "tests", "s1")
    if s1 not in sys.path:
        sys.path.insert(0, s1)
    import check_wm      # noqa: E402
 # the placement in force during the N2 part: the last pose record before the first s3.gps (SE file order). The real
 # SE exporter publishes the drawn pose as s3.pose (o, q, s, k); its "DE.S3 placed" log line parses as s3.place
 # without o/q/s, so a pose
 # record wins over a place record
    pl = None
    for name, d in logs.se:
        if name == "s3.gps":
            break
        if name == "s3.pose" and vec(d, "o") is not None:
            pl = d
        elif name == "s3.place" and (pl is None or vec(pl, "o") is None):
            pl = d
    if pl is None or vec(pl, "o") is None or vec(pl, "q", n=4) is None or num(pl, "s") is None:
        return []
    ctr = IC.sn_apply(vec(pl, "o"), vec(pl, "q", n=4), num(pl, "s"), (0.0, 0.0, 0.0))
    scr = next((vec(g, "screen", n=2) for g in gps if vec(g, "screen", n=2)), (2560.0, 1440.0))
    out = []
    for e in check_wm.s1_events(logs.sn_raw):
        if e.get("_ev") != "s1.cam" or str(e.get("outcome", "")).lower() in ("snap", "torn"):
            continue
        pos, rot, fov, q = vec(e, "camPos"), vec(e, "camRot", n=4), num(e, "fov"), num(e, "qpcUsed")
        if pos is None or rot is None or fov is None or q is None:
            continue
        sc = vec(e, "screen", n=2) or scr
        cam = IC.Camera(pos, rot, fov, int(sc[0]), int(sc[1]))
        pr = cam.project(ctr)
        if pr is None:
            continue
        out.append({"qpc": q, "seTick": num(e, "tickRot", "tickNew"), "px": [pr[0], pr[1]], "frame": num(e, "frame"),
                    "computed": True})
    return out

def n2_rows(logs: Logs, rows, ref_ms=None, freq=None):
    what_r = "gold wall at rest: |SE GPS marker - Subnautica block centre| <= 2 px"
    what_m = "gold wall moving: lag of the drawn block behind SE's marker = the S1.N2 latency within one SN frame"
    gps = [d for d in logs.se_ev("s3.gps") if vec(d, "px", n=2) and num(d, "qpc") is not None]
    snf = [d for d in logs.sn_ev("s3.n2") if vec(d, "px", n=2) and num(d, "qpc") is not None]
    computed = False
    if gps and not snf:
        snf = computed_n2_frames(logs, gps)
        computed = bool(snf)
    if computed:
        what_r += " [computed from Subnautica's camera trace]"
        what_m += " [computed from Subnautica's camera trace]"
    if not gps or not snf:
        for cid, w in (("S3.N2r", what_r), ("S3.N2m", what_m)):
            C.row(rows, cid, "MISSING", "SE gps %d, SN n2 %d" % (len(gps), len(snf)), "-", w, "n2")
        return
    freq = freq or C.qpc_frequency([{"qpcFrequency": num(logs.start(), "qpcFrequency")}] if
                                   num(logs.start(), "qpcFrequency") else [])
    marks = logs.sess("n2")
    mv = [(num(m, "qpc"), str(m.get("phase"))) for m in marks if num(m, "qpc") is not None]
    t_move0 = next((q for q, ph in mv if ph == "motion-begin"), None)
    t_move1 = next((q for q, ph in mv if ph == "motion-end"), None)
    series = sorted((num(d, "qpc") / freq, vec(d, "px", n=2)[0]) for d in gps)
    by_tick = {num(d, "tick"): vec(d, "px", n=2) for d in gps if num(d, "tick") is not None}
    rest, lags, frame_dt = [], [], []
    prev_q = None
    for d in sorted(snf, key=lambda e: num(e, "qpc")):
        q = num(d, "qpc")
        if prev_q is not None:
            frame_dt.append((q - prev_q) / freq)
        prev_q = q
        moving = t_move0 is not None and t_move1 is not None and t_move0 <= q <= t_move1
        px = vec(d, "px", n=2)
        tick = num(d, "seTick", "tick")
        if not moving:
            g = by_tick.get(tick)
            if g is not None:
                rest.append(math.dist(g, px))
            continue
        t = q / freq
        # the time SE's marker was where Subnautica drew the block: one crossing of the marker's track inside a
 # [-120 ms, +30 ms] window, where the marker moves fast (>= 500 px/s; near the sine's turning points a mirrored
 # crossing could otherwise be taken); ambiguous samples are skipped
        cands = []
        for te, i in _interp_time_at_px(series, px[0]):
            if t - 0.12 <= te <= t + 0.03:
                (t0, x0), (t1, x1) = series[i - 1], series[i]
                if abs((x1 - x0) / max(t1 - t0, 1e-9)) >= 500.0:
                    cands.append(te)
        if len(cands) == 1:
            lags.append((t - cands[0]) * 1000.0)
    if rest:
        med = sorted(rest)[len(rest) // 2]
        C.row(rows, "S3.N2r", "PASS" if med <= N2_REST_PX else "FAIL", "median %.2f px (%d frames)" % (med, len(rest)),
              "<= 2 px", what_r, "n2", maxPx=max(rest))
    else:
        C.row(rows, "S3.N2r", "MISSING", "no rest frames joined by SE tick", "<= 2 px", what_r, "n2")
    ref = ref_ms if ref_ms is not None else S1N2_DEFAULT_MS
    fdt = sorted(frame_dt)[len(frame_dt) // 2] * 1000.0 if frame_dt else None
    if lags and fdt:
        med = sorted(lags)[len(lags) // 2]
        ok = abs(med - ref) <= fdt
        C.row(rows, "S3.N2m", "PASS" if ok else "FAIL", "lag %.2f ms vs %.2f +- %.2f" % (med, ref, fdt),
              "S1.N2 +- 1 SN frame", what_m + " (HYPOTHESIS thresholds)", "n2", samples=len(lags), refMs=ref,
              frameMs=fdt, p10=sorted(lags)[len(lags) // 10], p90=sorted(lags)[(9 * len(lags)) // 10])
    else:
        C.row(rows, "S3.N2m", "MISSING", "moving samples %d" % len(lags), "S1.N2 +- 1 SN frame", what_m, "n2")

def n3_rows(logs: Logs, rows, site=None):
    """S3.N3: per pair, |SE pixel - Subnautica pixel| <= 0.5 px + the known (s - 1)/2 |sin 2 alpha| offset of the line
 of sight's elevation alpha (ARCHITECTURE 5.4 "Known limitation": 0 for a level line of sight, so "level looks" get
 0.5 px); alpha from the pair's SN point and SN camera (the SE reply's camSn, else Subnautica's camPos)."""
    what = "analytic projection: SE ViewProjection pixel of a corner vs Subnautica WorldToScreenPoint of the mirrored corner"
    sn_px = {}
    for d in logs.sn_ev("s3.pixel"):
        lab = d.get("label")
        p = vec(d, "px", "snPx", n=2)
        if lab is not None and p:
            sn_px[str(lab)] = (p, d)
    by_look = {}
    missing = 0
    for r in logs.sess("pixel"):
        lab = str(r.get("label"))
        se = r.get("se") or {}
        sp = vec(se, "sePx", "px", n=2)
        sn = sn_px.get(lab)
        if sn is None and isinstance(r.get("sn"), dict) and vec(r["sn"], "snPx", "px", n=2):
            sn = (vec(r["sn"], "snPx", "px", n=2), r["sn"])
        if sp is None or sn is None:
            missing += 1
            continue
        e = math.dist(sp, sn[0])
        pt = vec(se, "pointSn", "sn") or vec(sn[1], "pointSn")
        cam = vec(se, "camSn") or vec(sn[1], "camPos")
        alpha = None
        if pt and cam and math.dist(pt, cam) > 0:
            alpha = math.asin(max(-1.0, min(1.0, (pt[1] - cam[1]) / math.dist(pt, cam))))
        elif num(r, "pitchDeg") is not None:
            alpha = math.radians(num(r, "pitchDeg"))
        s = num(r, "s") or num(se, "s") or 1.0
        scr = se.get("screen") if isinstance(se.get("screen"), list) else [2560, 1440]
        k = (scr[1] / 2.0) / math.tan(math.radians(num(se, "vFovDeg") or 70.0) / 2.0)
        allow = N3_LEVEL_PX + (abs(s - 1.0) / 2.0 * abs(math.sin(2.0 * alpha)) * k if alpha is not None else 0.0)
        by_look.setdefault(str(r.get("look", "level")), []).append((e, allow, lab, alpha))
    if not by_look:
        C.row(rows, "S3.N3", "MISSING", "%d pairs without both pixels" % missing, "<= 0.5 px + offset", what, "n3")
        return
    for look, cid in (("level", "S3.N3"), ("tilt", "S3.N3t")):
        pr = by_look.get(look)
        if not pr:
            C.row(rows, cid, "MISSING", "no %s pairs" % look, "<= 0.5 px + offset", what, "n3", required=look == "level")
            continue
        bad = [p for p in pr if p[0] > p[1]]
        worst = max(pr, key=lambda p: p[0] - p[1])
        C.row(rows, cid, "PASS" if not bad else "FAIL", "%d/%d; worst %.3f px vs %.3f" % (
            len(pr) - len(bad), len(pr), worst[0], worst[1]), "0.5 px + (s-1)/2 |sin 2a|", what + (
            " (camera looking 30 deg down)" if look == "tilt" else " (level camera)"), "n3", missing=missing,
            worst=worst[2], alphaDeg=math.degrees(worst[3]) if worst[3] is not None else None)

# ----------------------------------------------------------------------------------------------------------------
# The image manifest (scene + captures) for image_checks.run_manifest
# ----------------------------------------------------------------------------------------------------------------

def cube_faces(half=1.25):
    """The stand-in cube's faces (fake_se_producer.s3_cube conventions: right = up x n, up +Y / -Z / +Z)."""
    out = []
    for n, up in (((0, 0, 1), (0, 1, 0)), ((0, 0, -1), (0, 1, 0)), ((1, 0, 0), (0, 1, 0)), ((-1, 0, 0), (0, 1, 0)),
                  ((0, 1, 0), (0, 0, -1)), ((0, -1, 0), (0, 0, 1))):
        n, up = tuple(float(x) for x in n), tuple(float(x) for x in up)
        out.append({"face": len(out), "centre": IC.mul(n, half), "normal": n, "right": IC.cross(up, n), "up": up})
    return out

def build_manifest(logs: Logs) -> dict:
    spots = {}
    for r in logs.sess("place"):
        rep = r.get("reply") or {}
        spot = str(r.get("spot", "deep"))
        o, q, s = vec(rep, "o"), vec(rep, "q", n=4), num(rep, "s")
        if o is None or q is None or s is None:
            continue
        lab = vec(rep, "labSn") or vec(r, "labSn")
        sp = spots.setdefault(spot, {})
        b = rep.get("bounds")
        half = (b[3] - b[0]) / 2.0 if isinstance(b, list) and len(b) == 6 and rep.get("parts", 1) == 1 else 1.25
        sp["block"] = {"o": o, "q": q, "s": s, "half": half, "offset": [0.0, 0.0, 0.0]}
        sb = num(r, "seabedY")              # the launcher's seabed under the block (a lifted placement sits above it)
        sp["seabedY"] = {"block": sb if sb is not None else (lab[1] if lab else None)}
        top = (lab[1] + half) if lab else None
        sp["depthM"] = {"block": -top if top is not None else None}
        g = rep.get("glyph")
        sp["glyph"] = {"offset": list(g["partLocal"][9:12])} if isinstance(g, dict) and g.get("partLocal") else None
        sp["faces"] = rep.get("faces") or cube_faces(half)
        pl = rep.get("plate")
        if isinstance(pl, dict) and isinstance(pl.get("cells"), list):
            sp["plate"] = {"cells": pl["cells"], "half": pl.get("half", half), "exportPng": pl.get("exportPng"),
                           "faceUv": pl.get("faceUv")}
    for r in logs.sess("lab"):
        rep = r.get("reply") or {}
        spot = str(r.get("spot", "deep"))
        lo, hi = vec(rep, "seamothMin"), vec(rep, "seamothMax")
        if (lo is None or hi is None) and isinstance(rep.get("seamothBounds"), (list, dict)):
            sb = rep["seamothBounds"]
            if isinstance(sb, dict):
                lo, hi = vec(sb, "min"), vec(sb, "max")
            elif len(sb) == 6:
                lo, hi = tuple(sb[:3]), tuple(sb[3:])
        if lo and hi:
            sp = spots.setdefault(spot, {})
            sp["seamoth"] = {"lo": list(lo), "hi": list(hi)}
            sb_y = num(rep, "seamothSeabedY", "seabedY")
            sp.setdefault("seabedY", {})["seamoth"] = sb_y if sb_y is not None else (sp.get("seabedY") or {}).get("block")
    # Subnautica's own lab lines fill what the replies did not carry
    for d in logs.sn_ev("s3.lab"):
        lo, hi = vec(d, "seamothMin"), vec(d, "seamothMax")
        spot = str(d.get("spot", "deep"))
        if lo and hi and "seamoth" not in spots.get(spot, {}):
            spots.setdefault(spot, {})["seamoth"] = {"lo": list(lo), "hi": list(hi)}
    caps = []
    for r in logs.sess("capture"):
        png = r.get("png")
        if png and not os.path.isabs(png):
            png = os.path.join(logs.folder, png)
        caps.append({"name": r.get("name"), "target": r.get("target", "both"), "spot": r.get("spot", "deep"),
                     "time": r.get("time"), "png": png})
    cpu = {}
    for r in logs.sess("cpu"):
        if num(r, "seconds") is not None:
            cpu[str(r.get("label"))] = num(r, "seconds")
    lab_captures(logs, spots, caps, cpu)
    return {"scene": {"spots": spots, "faces": cube_faces()}, "captures": caps, "cpuSeconds": cpu}

_LAB_FILE = re.compile(r"^(?P<label>.+)_f\d+\.png$", re.IGNORECASE)

def lab_capture_name(spot: str, tag: str, label: str):
    """Subnautica lab-run label -> the image checks' capture names: P1/P2/P4 as they are, P3 (also P3-a), P3b (the
 P3+1s repeat: P3-b), P3_NNN (the burst: P3-burst); a "noon"/"midnight" run's P1 is P1-noon/P1-midnight."""
    m = re.match(r"^(P[1-4])", label or "", re.IGNORECASE)
    if not m:
        return []
    pose = m.group(1).upper()
    if tag in ("noon", "midnight"):
        return ["P1-" + tag] if pose == "P1" else []
    if spot == "n1":
        return ["N1lab"]
    if re.match(r"^P3_\d+$", label, re.IGNORECASE):
        return ["P3-burst"]
    if label.upper() == "P3B":
        return ["P3-b"]
    if label.upper() == "P3":
        return ["P3", "P3-a"]
    return [pose] if label.upper() == pose else []

def lab_captures(logs: Logs, spots: dict, caps: list, cpu: dict):
    """Captures of Subnautica's lab runs (session "labrun" events; files <out>/captures/<session>/<label>_f<frame>.png
 with <png>.json and <png>.s3.json), the scene facts their S3 sidecars carry (Seamoth bounds, block pose, seabed, the
 sun) where the replies did not, and the C10 CPU time (sampled when the P3 and P3b captures finished)."""
    for r in logs.sess("labrun"):
        session, spot, tag = r.get("session"), str(r.get("spot", "deep")), str(r.get("tag") or "")
        d = os.path.join(logs.folder, "captures", str(session))
        if not session or not os.path.isdir(d):
            continue
        for fn in sorted(os.listdir(d)):
            m = _LAB_FILE.match(fn)
            if not m:
                continue
            png = os.path.join(d, fn)
            for name in lab_capture_name(spot, tag, m.group("label")):
                caps.append({"name": name, "target": "both", "spot": spot, "time": None, "png": png, "lab": session})
            try:
                with open(png + ".s3.json", encoding="utf-8") as f:
                    sc = json.load(f)
            except (OSError, ValueError):
                continue
            sp = spots.setdefault(spot, {})
            sm = sc.get("seamoth") or {}
            if "seamoth" not in sp and vec(sm, "boundsMin") and vec(sm, "boundsMax"):
                sp["seamoth"] = {"lo": list(vec(sm, "boundsMin")), "hi": list(vec(sm, "boundsMax"))}
            lab = sc.get("lab") or {}
            if num(lab, "seabedY") is not None:
                sp.setdefault("seabedY", {}).setdefault("block", num(lab, "seabedY"))
                sp["seabedY"].setdefault("seamoth", num(lab, "seabedY"))
            pose = (sc.get("block") or {}).get("pose") or {}
            if "block" not in sp and vec(pose, "o") and vec(pose, "q", n=4) and num(pose, "s") is not None:
                sp["block"] = {"o": vec(pose, "o"), "q": vec(pose, "q", n=4), "s": num(pose, "s"), "half": 1.25,
                               "offset": [0.0, 0.0, 0.0]}
            sun = sc.get("sun") or {}
            if "sun" not in sp and vec(sun, "forward"):
                f = vec(sun, "forward")
                sp["sun"] = list(IC.unit((-f[0], -f[1], -f[2])))
        samples = {num(c, "labOk"): num(c, "cpu") for c in (r.get("cpu") or []) if num(c, "cpu") is not None}
        caps_list = [str(c) for c in (r.get("captures") or [])]
        if spot == "shallow" and caps_list[:2] == ["P3", "P3+1s"] and samples.get(1) is not None and \
                samples.get(2) is not None:
            cpu["c10"] = samples[2] - samples[1]

def image_rows(logs: Logs, rows):
    m = build_manifest(logs)
    with open(os.path.join(logs.folder, "s3-image-manifest.json"), "w", encoding="utf-8") as f:
        json.dump(m, f, indent=1, default=str)
    if not m["captures"]:
        for cid in ("S3.C5", "S3.C6", "S3.C7", "S3.C8", "S3.C9", "S3.C10", "S3.N1g", "S3.N1b", "S3.C13", "S3.C14"):
            C.row(rows, cid, "MISSING", "no captures", "-", "image check", "views",
                  required=cid not in ("S3.C13", "S3.C14", "S3.N1g", "S3.N1b"))
        return
    try:
        got = IC.run_manifest(m)
    except Exception as e:  # noqa: BLE001 - an analyser bug must not hide the other rows
        C.row(rows, "S3.images", "FAIL", "image_checks error %s" % type(e).__name__, "-", str(e)[:120], "views")
        return
    s3b = bool((logs.start() or {}).get("mode") == "S3b")
    for r in got:
        req = True
        if r["id"] in ("S3.C13", "S3.C14"):
            req = s3b
        if r["id"] in ("S3.N1g", "S3.N1b"):
            req = any(c.get("name") == "N1" for c in m["captures"])
        if "required" in r:
            req = req and bool(r["required"])
        C.row(rows, r["id"], r["status"], r["measured"], r["threshold"], r["what"], "views", required=req,
              **(r.get("detail") or {}))

# ----------------------------------------------------------------------------------------------------------------

def evaluate(folder: str, s1_results=None, baseline_logs=None) -> list:
    logs = Logs(folder)
    rows = []
    ref_ms = None
    if s1_results and os.path.exists(s1_results):
        try:
            with open(s1_results, encoding="utf-8") as f:
                st = (json.load(f).get("latency") or {}).get("step") or {}
            ref_ms = num(st, "p50Ms", "p50", "medianMs")
        except (OSError, ValueError):
            ref_ms = None
    site = site_from(logs)
    for name, fn in (("C1", lambda: c1_rows(logs, rows)), ("C2", lambda: c2_rows(logs, rows, site)),
                     ("C3", lambda: c3_rows(logs, rows)), ("C4", lambda: c4_rows(logs, rows)),
                     ("C5r", lambda: c5_ray_rows(logs, rows)), ("images", lambda: image_rows(logs, rows)),
                     ("N1d", lambda: n1d_rows(logs, rows)), ("N1t", lambda: n1t_rows(logs, rows)),
                     ("C11", lambda: c11_rows(logs, rows, baseline_logs)), ("C12", lambda: c12_rows(logs, rows)),
                     ("G1-E2", lambda: g1e2_rows(logs, rows)), ("N2", lambda: n2_rows(logs, rows, ref_ms)),
                     ("N3", lambda: n3_rows(logs, rows, site))):
        try:
            fn()
        except Exception as e:  # noqa: BLE001
            import traceback
            C.row(rows, "S3.%s" % name, "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:100] + " | " +
                  traceback.format_exc().strip().splitlines()[-1][:100], name)
    st = logs.start()
    end = next((r for r in reversed(logs.session) if r.get("ev") == "end"), None)
    if end is not None:
        caps = logs.sess("capture")
        C.row(rows, "S3.harness", "INFO", "%d captures (%d written), unsupported %s" % (
            len(caps), sum(1 for c in caps if c.get("written")), ",".join(end.get("unsupported") or []) or "none"),
            "-", "session plumbing: commands %s; Subnautica actions refused as unknown leave their rows MISSING" %
            json.dumps(end.get("stats"))[:120], "session", required=False)
    mut = st.get("mutants") or []
    for m in mut:
        want = MUTANT_ROWS.get(m, ())
        red = [r["id"] for r in rows if r["id"] in want and r["status"] == "FAIL"]
        absent = [w for w in want if not any(r["id"] == w and r["status"] in ("PASS", "FAIL") for r in rows)]
        status = "PASS" if want and len(red) == len(want) else ("MISSING" if absent else "FAIL")
        C.row(rows, "%s.red" % m, status, "red: %s%s" % (",".join(red) or "none", "; not measured: %s" %
              ",".join(absent) if absent else ""), "red: %s" % ",".join(want), "seeded failure %s turns its rows red" % m,
              "mutants", required=True)
    return rows

def write_results(folder, rows, strict=True, extra=None) -> bool:
    ok = C.verdict(rows, strict)
    text = C.table(rows)
    res = {"tool": "analyze_s3", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "s3-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "s3-table.txt"), "w", encoding="utf-8") as f:
        f.write(text + "\n")
    return ok

# ----------------------------------------------------------------------------------------------------------------
# Self-test: a synthetic session folder written from the stand-in's own S3a scene (fake_se_producer.S3aScene), a
# faithful Subnautica side computed with wire_ref, and small rendered captures; then each S3 mutant
# ----------------------------------------------------------------------------------------------------------------

_RENDER_CACHE = {}

def synth_lab_runs(folder, sess, mutant, rep, pose, seabed, c_b, c_s, axes, sm_lo, sm_hi, sun):
    """Subnautica's lab runs as the plugin writes them: its plan (heading 0: F = +Z, R = +X; M the block-Seamoth
 midpoint), one picture per pose framing both objects, files <session>/<label>_f<frame>.png +.json +.s3.json."""
    o, q, s_ = pose
    stamp = "selftest"

    def render(cam, world, key):
        k = ("lab",) + key + (cam.pos, cam.rot, cam.w, cam.h, cam.fov, round(world.t, 3), world.night)
        img = _RENDER_CACHE.get(k)
        if img is None:
            img = _RENDER_CACHE[k] = world.render(cam)
        return img

    def write(session, label, frame, cam, world, img, pose_name, seab, block_pose, smb, buried=None):
        d = os.path.join(folder, "captures", session)
        os.makedirs(d, exist_ok=True)
        png = os.path.join(d, "%s_f%d.png" % (label, frame))
        IC.write_png(png, img)
        with open(png + ".json", "w", encoding="utf-8") as f:
            json.dump({"unityCamX": cam.pos[0], "unityCamY": cam.pos[1], "unityCamZ": cam.pos[2], "unityQx": cam.rot[0],
                       "unityQy": cam.rot[1], "unityQz": cam.rot[2], "unityQw": cam.rot[3], "unityFov": cam.fov,
                       "screenW": cam.w, "screenH": cam.h}, f)
        blk = {"pose": {"o": list(block_pose[0]), "q": list(block_pose[1]), "s": block_pose[2]}}
        if buried:
            blk["buried"] = buried
        with open(png + ".s3.json", "w", encoding="utf-8") as f:
            json.dump({"slice": "S3", "pose": pose_name, "label": label, "session": session, "lab": {"seabedY": seab},
                       "sun": {"forward": [-world.sun[0], -world.sun[1], -world.sun[2]]},
                       "seamoth": {"boundsMin": list(smb[0]), "boundsMax": list(smb[1])}, "block": blk}, f)
        return png
    # deep spot: P1, P2, P4 (one run), P1 at noon and at midnight (two runs)
    M = tuple((a + b) / 2.0 for a, b in zip(c_b, c_s))
    blk_box = IC._Box("block", c_b, axes, (1.25 * s_, 1.25, 1.25 * s_), (170, 170, 175))
    sm_box = IC._Box("seamoth", c_s, ((1, 0, 0), (0, 1, 0), (0, 0, 1)), (1.1, 0.8, 2.0), (215, 215, 220))
    W = IC._World(seabed, sun, [blk_box, sm_box])
    Wn = IC._World(seabed, sun, [blk_box, sm_box])
    Wn.night = True
    target = (M[0], c_b[1] + 0.5, M[2])
    cam_p1 = IC._cam_look((M[0], c_b[1] + 1.0, M[2] - 6.0), target, w=240, h=135, fov=60.0)
    cam_p4 = IC._cam_look((M[0], c_b[1] + 1.0, M[2] - 40.0), target, w=480, h=270, fov=20.0)
    cam_p2 = IC._cam_look((c_b[0] - 6.0, seabed + 0.3, c_b[2]), c_b, w=240, h=135, fov=60.0)
    smb = (sm_lo, sm_hi)
    bp = (o, q, s_)
    runs = [("deep", "", [("P1", "P1", cam_p1, W), ("P2", "P2", cam_p2, W), ("P4", "P4", cam_p4, W)], ["P1", "P2", "P4"]),
            ("deep", "noon", [("P1", "P1", cam_p1, W)], ["P1"]), ("deep", "midnight", [("P1", "P1", cam_p1, Wn)], ["P1"])]
    frame = 1000
    for spot, tag, shots_, caps in runs:
        session = "%s-%s%s" % (stamp, spot, "-" + tag if tag else "")
        for label, pose_name, cam, world in shots_:
            frame += 7
            buried = [{"corner": i, "terrainFirst": True} for i in (0, 1, 4, 5)] if pose_name == "P2" else None
            write(session, label, frame, cam, world, render(cam, world, (session, label)), pose_name, seabed, bp, smb,
                  buried)
        sess.append({"ev": "labrun", "session": session, "spot": spot, "tag": tag, "captures": caps, "state": "Done"})
    # shallow spot: P3, P3+1s, P3*6 (one run), the scene moved up to -12 m
    dy = -12.0 - seabed
    cb2, cs2 = IC.add(c_b, (0, dy, 0)), IC.add(c_s, (0, dy, 0))
    M2 = IC.add(M, (0, dy, 0))
    rep_s = dict(rep, o=[o[0], o[1] + dy, o[2]], labSn=[rep["labSn"][0], rep["labSn"][1] + dy, rep["labSn"][2]])
    sess.append({"ev": "place", "spot": "shallow", "reply": rep_s, "seabedY": seabed + dy})
    blk2 = IC._Box("block", cb2, axes, (1.25 * s_, 1.25, 1.25 * s_), (170, 170, 175))
    sm2 = IC._Box("seamoth", cs2, ((1, 0, 0), (0, 1, 0), (0, 0, 1)), (1.1, 0.8, 2.0), (215, 215, 220))
    sun_low = IC.unit((0.8, 0.45, 0.2))
    cam_p3 = IC._cam_look((M2[0], cb2[1] + 10.0, M2[2]), (M2[0], cb2[1], M2[2]), w=240, h=135, fov=60.0,
                          up=(0.0, 0.0, 1.0))
    smb2 = ((sm_lo[0], sm_lo[1] + dy, sm_lo[2]), (sm_hi[0], sm_hi[1] + dy, sm_hi[2]))
    bp2 = ((o[0], o[1] + dy, o[2]), q, s_)
    session = "%s-shallow" % stamp
    labels = [("P3", 0.0), ("P3b", 1.4)] + [("P3_%03d" % k, 0.7 * k) for k in range(6)]
    first = None
    for label, t in labels:
        frame += 11
        Wk = IC._World(seabed + dy, sun_low, [blk2, sm2], t=t)
        png = write(session, label, frame, cam_p3, Wk, render(cam_p3, Wk, (session, label)), "P3", seabed + dy, bp2,
                    smb2)
        if label == "P3":
            first = png
        if label == "P3b" and mutant == "S3.M5":     # the plugin's S3.M5: the repeat is a copy of the previous PNG
            with open(first, "rb") as f_in, open(png, "wb") as f_out:
                f_out.write(f_in.read())
    sess.append({"ev": "labrun", "session": session, "spot": "shallow", "tag": "", "captures": ["P3", "P3+1s", "P3*6"],
                 "state": "Done", "cpu": [{"labOk": 0, "cpu": 10.0}, {"labOk": 1, "cpu": 10.2},
                                          {"labOk": 2, "cpu": 11.1}]})

def _write_jsonl(path, recs):
    with open(path, "w", encoding="utf-8") as f:
        for r in recs:
            f.write(json.dumps(r, default=str) + "\n")

def synth_session(folder: str, mutant: str = None, freq: int = 10_000_000, fmt: str = "contract",
                  captures: str = "harness"):
    """Writes se-standin.jsonl, sn-synth.jsonl, s3-session.jsonl, logs and captures for one synthetic session. fmt
 "contract": the Subnautica lines of this file's log contract; "plugin": the lines the Subnautica plugin writes
 (DE.Mirror model/material/placed, DE.S3 SHEAR/FRAMETIME/TEARDOWN/COUNT, buried-corner rays in a P2.s3.json).
 captures "harness": the launcher's per-target captures; "lab": Subnautica's lab runs (labrun events, one picture
 framing block and Seamoth, <label>_f<frame>.png with.json and.s3.json sidecars, Seamoth bounds only there)."""
    import wire_ref
    import fake_se_producer as FP
    os.makedirs(os.path.join(folder, "captures"), exist_ok=True)
    a = FP.parse(["--s3a"])
    se = FP._SelftestSe(a)
    scene = FP.S3aScene(se, a)
    lab = (-1029.0, -150.0, -1116.0)
    se.cam.teleport((lab[0], lab[1] + 0.6, lab[2] - 6.0), 0.0, 0.0, None, "P1")
    rep = scene.place({"sn": list(lab), "yawDeg": 30.0, "label": "deep"})
    P, o, q, s, k = scene.compute_pose()
    se_recs = [{"t": 1.0, "ev": "s1.site", "C": list(se.cam.C)}]
    for e in se.events:
        r = {k2: v for k2, v in e.items() if k2 not in ("ev", "msg")}
        r.update(ev=e["ev"], t=1.0)
        if e.get("msg"):
            r["msg"] = e["msg"]
        se_recs.append(r)
    se_recs.append({"t": 1.1, "ev": "s3.pose", "tick": 100, "entityId": rep["entityId"], "gridId": rep["gridId"],
                    "seMatrix": rep["seMatrix"], "pivot": list(P), "o": list(o), "q": list(q), "s": s, "k": 1.0,
                    "bounds": rep["bounds"], "site": rep["site"],
                    "wire": {"o": list(wire_ref.f32v(o)), "q": list(wire_ref.f32v(q)), "s": wire_ref.f32(s)}})
    sn = []

    def snl(text, level="info"):
        sn.append({"t": "2026-10-16T12:00:00.000Z", "frame": len(sn), "level": level, "event": text.split(" ")[0],
                   "text": text})
    gid_ = rep["gridId"]
    plugin = fmt == "plugin"
    mir_on, swap_on = mutant != "S3.M1", mutant != "S3.M2"
    frac = FP.s3_winding_measure(scene.cube["verts"], scene.cube["tris"], mir_on, swap_on)
    if plugin:
        snl("DE.Mirror built grid=%s cell=(0, 0, 0) parts=1 verts=24 tris=12 modelVerts=24 modelTris=12 chunks=1 "
            "winding=%g%% gridParts=1 gridVerts=26 gridTris=12 gridModelVerts=24 gridModelTris=12 uncoveredTris=0 "
            "overlapTris=0 (S3.C1: the model totals equal the producer's counts; the chunk total 26 shows a vertex split "
            "across materials)" % (gid_, 100 * frac))
        snl("DE.Mirror model key=%s verts=24 tris=12 submeshes=1 winding=%g%% degenerate=0 flipped=%s mirrorZ=%s triSwap=%s "
            "patternScale=1 skinned=false boundsUnity=(-1.25,-1.25,-1.25)-(1.25,1.25,1.25) tangentU=100%% binormalV=100%% "
            "tangentTris=12" % (rep["modelKey"], 100 * frac, "true" if frac < 0.01 else "false",
                                "on" if mir_on else "OFF(S3.M1)", "on" if swap_on else "OFF(S3.M2)"),
            level="warn" if frac < 0.99 else "info")
        for role, target in (("ColorMetal", "R8G8B8A8_UNorm"), ("NormalGloss", "R8G8B8A8_UNorm"), ("AddMaps", "R8G8B8A8_UNorm")):
            m8 = mutant == "S3.M8" and role == "NormalGloss"
            err = 0.2312 if m8 else 0.00187
            snl("DE.Mirror texture fidelity role=%s path='C:/x/large_edges.dds' target=%s float=R32G32B32A32_SFloat "
                "texels=4096 maxErr=%g meanErr=%g verdict=%s%s (S3.N1d)" % (
                    role, "R8G8B8A8_SRGB" if m8 else target, err, err / 3.0, "FAIL" if m8 else "PASS",
                    " (MUTANT S3.M8)" if m8 else ""), level="warn" if m8 else "info")
        for when in ("created", "final"):
            snl("DE.Mirror material %s grid=%s cell=(0,0,0)/m0/c0 shader=MarmosetUBER supported=True keywords=[MARMO_SPECMAP "
                "UWE_3COLOR _ZWRITE_ON] renderQueue=2000 planned=[MARMO_SPECMAP UWE_3COLOR] layer=0 shadows=On "
                "skyApplier=%s applySky=%s environmentSky=SkyGrandReef framesSinceCreate=%d maps{cm=ok} %s(S3.C4)" % (
                    when, gid_, mutant != "S3.M3", "none" if mutant == "S3.M3" else "SkyGrandReef", 1 if when ==
                    "created" else 40, "(MUTANT S3.M3) " if mutant == "S3.M3" else ""))
    else:
        snl("DE.Mirror built grid=%s parts=1 verts=24 tris=12" % gid_)
        snl("DE.Mirror winding model=%s agree=%d total=12 frac=%.4f flipped=%s" % (rep["modelKey"], round(frac * 12),
                                                                                 frac, frac < 0.01))
        snl("DE.Material grid=%s shader=MarmosetUBER supported=True keywords=MARMO_SPECMAP,UWE_3COLOR planned="
            "MARMO_SPECMAP layer=0 skyApplier=%s sky=%s frames=2" % (gid_, mutant != "S3.M3", "null" if mutant ==
                                                                    "S3.M3" else "SkyGrandReef"))
        snl("DE.Mirror texture fidelity role=NormalGloss target=R8G8B8A8_UNorm maxErr=0.0019 verdict=PASS (S3.N1d)")
    cs = []
    lo, hi = rep["bounds"][:3], rep["bounds"][3:]
    o32, q32, s32 = wire_ref.f32v(o), wire_ref.f32v(q), wire_ref.f32(s)
    for sg in IC.CORNER_SIGNS:
        l_ = tuple(lo[i] if sg[i] < 0 else hi[i] for i in range(3))
        if mir_on:
            cs.append(wire_ref.sn_apply(o32, q32, s32, l_))
        else:
            cs.append(wire_ref.add(o32, wire_ref.scale_h(wire_ref.qrot(q32, l_), s32)))
    if plugin:
        snl("DE.Mirror placed entity=%s tick=103 how=interp o=(%r, %r, %r) s=%r seLocalMin=%r,%r,%r seLocalMax=%r,%r,%r "
            "seLocalCorners=%s cornersSn=%s maxDevVsApplyM=0.000001 (S3.C2: drawn vertices; Python recomputes Apply from "
            "SE's matrix and P*)" % (
                gid_, o32[0], o32[1], o32[2], s32, lo[0], lo[1], lo[2], hi[0], hi[1], hi[2],
                ";".join("%r,%r,%r" % tuple(lo[i] if sg[i] < 0 else hi[i] for i in range(3)) for sg in IC.CORNER_SIGNS),
                ";".join("%r,%r,%r" % tuple(wire_ref.f32v(c)) for c in cs)))
        for sc in SHEAR_SCALES:
            err = rigid_error(sc) if mutant == "S3.M7" else 3.2e-6
            snl("DE.S3 SHEAR s=%.4f k=1 frames=360 maxDevLinearM=%.8f maxDevWorldM=%.8f verdict=%s%s (S3.G1-E2: ...)" % (
                sc, err, err + 2e-5, "FAIL" if err >= 1e-4 else "PASS", " (MUTANT S3.M7)" if mutant == "S3.M7" else ""))
        snl("DE.S3 FRAMETIME frames=600 meanMs=6.944 p50Ms=6.944 p99Ms=7.1 maxMs=9.0 cpuMsPerFrame=4.912 "
            "processCpuMsPerFrame=9.81 vSyncCount=1 targetFrameRate=-1 mirrorVisible=False views=1 (S3.C11)")
        snl("DE.S3 FRAMETIME frames=600 meanMs=6.944 p50Ms=6.944 p99Ms=7.2 maxMs=9.1 cpuMsPerFrame=%s "
            "processCpuMsPerFrame=9.93 vSyncCount=1 targetFrameRate=-1 mirrorVisible=True views=1 (S3.C11)" % (
                "5.512" if mutant == "C11cost" else "4.987"))
        snl("DE.S3 COUNT rootsAlive=1 rootMarkersInScenes=1 preserved=0 seamoth=True ourMeshesAlive=2 "
            "ourOtherObjectsAlive=3 meshesTotal=412 (S3.C12)")
        snl("DE.S3 TEARDOWN mirror objects destroyed, Seamoth removed (data kept: de.s3 rebuild brings it back) "
            "meshesAlive=%d" % (2 if mutant == "S3.M6" else 0))
        m6 = mutant == "S3.M6"
        snl("DE.S3 COUNT rootsAlive=%d rootMarkersInScenes=%d preserved=%d seamoth=False ourMeshesAlive=%d "
            "ourOtherObjectsAlive=0 meshesTotal=%d (S3.C12)" % (1 if m6 else 0, 1 if m6 else 0, 1 if m6 else 0,
                                                               2 if m6 else 0, 412 if m6 else 410))
    else:
        sn.append({"t": "x", "level": "info", "event": "s3.placement", "data": {
            "entityId": rep["entityId"], "seTick": 103, "corners": [list(wire_ref.f32v(c)) for c in cs],
            "localCorners": [[lo[i] if sg[i] < 0 else hi[i] for i in range(3)] for sg in IC.CORNER_SIGNS]}})
        for i in (0, 1, 4, 5):
            snl("DE.S3 occlusion corner=%d hitLayer=30 buried=True" % i)
        for sc in SHEAR_SCALES:
            err = rigid_error(sc) if mutant == "S3.M7" else 3.2e-6
            snl("DE.S3 shear s=%.4f steps=360 maxErrM=%.6g mode=%s axis=(1, 0, 0)" % (sc, err, "rigid" if mutant ==
                                                                                        "S3.M7" else "frame"))
        snl("DE.S3 frames label=without n=600 meanMs=6.061 cpuMsPerFrame=4.90")
        snl("DE.S3 frames label=with n=600 meanMs=6.102 cpuMsPerFrame=4.97")
        snl("DE.S3 teardown phase=before roots=1 meshes=412 baseline=410")
        snl("DE.S3 teardown phase=after roots=%d meshes=%d baseline=410" % ((1, 411) if mutant == "S3.M6" else (0, 410)))
    # N2: SE ticks at 60 Hz, SN frames at 165 Hz drawing the camera of a tick 5 ms (+16.7 ms with M4) behind
    sess = [{"ev": "start", "stamp": "selftest", "mode": "S3a", "mutants": [mutant] if mutant else [],
             "qpcFrequency": freq}]
    t0 = 1000.0
    lag = 0.005 + (1.0 / 60.0 if mutant == "S3.M4" else 0.0)
    W, H, fov = 2560, 1440, 70.0
    k_ = (H / 2.0) / math.tan(math.radians(fov) / 2.0)

    def marker_x(t):
        rel = t - (t0 + 2.0)
        ang = math.radians(30.0) * math.sin(2 * math.pi * 0.5 * rel) if 0.0 <= rel <= 6.0 else 0.0
        return W / 2.0 - k_ * math.tan(ang)
    for i in range(int(10.0 * 60)):
        t = t0 + i / 60.0
        se_recs.append({"t": t, "ev": "s3.gps", "tick": 1000 + i, "qpc": int(t * freq), "px": [marker_x(t), 700.0]})
    sess.append({"ev": "n2", "phase": "motion-begin", "qpc": int((t0 + 2.0) * freq)})
    sess.append({"ev": "n2", "phase": "motion-end", "qpc": int((t0 + 8.0) * freq)})
    for j in range(int(10.0 * 165)):
        t = t0 + 0.02 + j / 165.0
        tick = 1000 + int((t - 0.005 - t0) * 60)
        x = marker_x(t - lag)
        rest_noise = 0.4 if not (t0 + 2.0 <= t <= t0 + 8.0) else 0.0
        sn.append({"t": "x", "level": "info", "event": "DE.S3",
                   "text": "DE.S3 n2 frame=%d qpc=%d seTick=%d px=(%r, 700.3)" % (j, int(t * freq), tick,
                                                                                 marker_x(t0 + (tick - 1000) / 60.0) +
                                                                                 rest_noise if rest_noise else x)})
    # N3: pairs at two corners (level look)
    for i in (0, 3):
        sess.append({"ev": "pixel", "label": "n3.c%d" % i, "look": "level", "se": {"sePx": [1200.0 + i, 900.0 + i],
                     "screen": [W, H], "vFovDeg": fov, "pointSn": [0.0, -1.0, 6.0], "camSn": [0.0, -0.4, 0.0]},
                     "s": 1.0025})
        shift = 3.0 if mutant == "N3shift" else 0.0     # seeded: Subnautica draws the corner 3 px off
        snl("DE.S3 pixel label=n3.c%d seTick=200 px=(%r, %r)" % (i, 1200.0 + i + 0.12 + shift, 900.0 + i - 0.2))
    # captures (small renders through image_checks' tracer); scene from the place reply
    seabed = lab[1]
    seamoth_lo, seamoth_hi = (lab[0] + 6.0 - 1.1, seabed - 0.2, lab[2] - 2.0), (lab[0] + 6.0 + 1.1, seabed + 1.4,
                                                                                lab[2] + 2.0)
    sess.append({"ev": "place", "spot": "deep", "reply": rep, "seabedY": seabed})
    if captures != "lab":
        sess.append({"ev": "lab", "spot": "deep", "reply": {"seamothMin": list(seamoth_lo),
                                                           "seamothMax": list(seamoth_hi), "seabedY": seabed}})
    blk_obj = IC.block_object({"o": o, "q": q, "s": s}, 1.25)
    axes = [IC.sn_dir(q, s, a_) for a_ in ((1, 0, 0), (0, 1, 0), (0, 0, -1))]
    blk = IC._Box("block", blk_obj["centre"], axes, (1.25 * s, 1.25, 1.25 * s), (170, 170, 175))
    smc = tuple((a_ + b_) / 2.0 for a_, b_ in zip(seamoth_lo, seamoth_hi))
    smb = IC._Box("seamoth", smc, ((1, 0, 0), (0, 1, 0), (0, 0, 1)), (1.1, 0.8, 2.0), (215, 215, 220))
    sun = IC.unit((0.55, 0.75, 0.35))

    def shot(name, target, cam, world, spot="deep", time_=None):
        key = (spot, name, target, round(world.t, 3), world.night, cam.pos, cam.rot, cam.w, cam.h, cam.fov)
        img = _RENDER_CACHE.get(key)
        if img is None:
            img = _RENDER_CACHE[key] = world.render(cam)
        fn = "captures/%s-%s-%s-%02d.png" % (spot, name, target, len(sess))
        IC.write_png(os.path.join(folder, fn), img)
        sc = {"unityCamX": cam.pos[0], "unityCamY": cam.pos[1], "unityCamZ": cam.pos[2], "unityQx": cam.rot[0],
              "unityQy": cam.rot[1], "unityQz": cam.rot[2], "unityQw": cam.rot[3], "unityFov": cam.fov,
              "screenW": cam.w, "screenH": cam.h, "sunDir": list(world.sun)}
        with open(os.path.join(folder, fn) + ".json", "w", encoding="utf-8") as f:
            json.dump(sc, f)
        sess.append({"ev": "capture", "name": name, "target": target, "spot": spot, "time": time_, "png": fn})
        return fn
    W1 = IC._World(seabed, sun, [blk, smb])
    c_b, c_s = blk_obj["centre"], smc
    if captures == "lab":
        synth_lab_runs(folder, sess, mutant, rep, (o, q, s), seabed, c_b, c_s, axes, seamoth_lo, seamoth_hi, sun)
        _write_jsonl(os.path.join(folder, "se-standin.jsonl"), se_recs)
        _write_jsonl(os.path.join(folder, "sn-synth.jsonl"), sn)
        _write_jsonl(os.path.join(folder, "s3-session.jsonl"), sess)
        with open(os.path.join(folder, "BepInEx-LogOutput.log"), "w", encoding="utf-8") as f:
            f.write("[Info   :DeepEngineers] DE.Hello side=sn\n")
        return
    fn_p2 = shot("P2", "block", IC._cam_look((c_b[0] - 6.0, seabed + 0.3, c_b[2] + 0.5), c_b), W1)
    if plugin:
        with open(os.path.join(folder, fn_p2) + ".s3.json", "w", encoding="utf-8") as f:
            json.dump({"slice": "S3", "pose": "P2", "label": "P2", "block": {"buried": [
                {"corner": i, "terrainFirst": True} for i in (0, 1, 4, 5)]}}, f)
    for name, dist in (("P1", 6.0), ("P4", 40.0)):
        for target, c in (("block", c_b), ("seamoth", c_s)):
            kw = {"w": 240, "h": 135, "fov": 20.0} if dist > 10 else {}
            shot(name, target, IC._cam_look((c[0], seabed + 0.8, c[2] - dist), c, **kw), W1)
    Wn = IC._World(seabed, sun, [blk, smb])
    Wn.night = True
    for target, c in (("block", c_b), ("seamoth", c_s)):
        cam = IC._cam_look((c[0], seabed + 0.8, c[2] - 6.0), c)
        shot("P1-noon", target, cam, W1, time_=0.5)
        shot("P1-midnight", target, cam, Wn, time_=0.0)
    # shallow spot: the same scene moved up to -12 m (the place reply of the shallow spot)
    dy = -12.0 - seabed
    rep_s = dict(rep, o=[o[0], o[1] + dy, o[2]], labSn=[lab[0], lab[1] + dy, lab[2]])
    sess.append({"ev": "place", "spot": "shallow", "reply": rep_s})
    sess.append({"ev": "lab", "spot": "shallow", "reply": {"seamothMin": [seamoth_lo[0], seamoth_lo[1] + dy,
                                                                          seamoth_lo[2]],
                                                           "seamothMax": [seamoth_hi[0], seamoth_hi[1] + dy,
                                                                          seamoth_hi[2]], "seabedY": seabed + dy}})
    blk2 = IC._Box("block", IC.add(c_b, (0, dy, 0)), axes, (1.25 * s, 1.25, 1.25 * s), (170, 170, 175))
    sm2 = IC._Box("seamoth", IC.add(c_s, (0, dy, 0)), ((1, 0, 0), (0, 1, 0), (0, 0, 1)), (1.1, 0.8, 2.0),
                  (215, 215, 220))
    sun_low = IC.unit((0.8, 0.45, 0.2))
    for kf in range(6):
        Wk = IC._World(seabed + dy, sun_low, [blk2, sm2], t=0.7 * kf)
        for target, c in (("block", IC.add(c_b, (0, dy, 0))), ("seamoth", IC.add(c_s, (0, dy, 0)))):
            cam = IC._cam_look(IC.add(c, (0, 10.0, 0)), c, up=(0.0, 0.0, 1.0))
            shot("P3-burst", target, cam, Wk, spot="shallow")
            if kf == 0:
                shot("P3", target, cam, Wk, spot="shallow")
                shot("P3-a", target, cam, Wk, spot="shallow")
            if kf == 2 and target == "block":
                fn = shot("P3-b", target, cam, Wk, spot="shallow")
                if mutant == "S3.M5":      # the capture file reused: P3-b is P3-a's bytes
                    a_png = next(os.path.join(folder, r["png"]) for r in sess if r.get("ev") == "capture" and
                                 r.get("name") == "P3-a" and r.get("target") == "block")
                    with open(a_png, "rb") as f_in, open(os.path.join(folder, fn), "wb") as f_out:
                        f_out.write(f_in.read())
    sess.append({"ev": "cpu", "label": "c10", "seconds": 0.9})
    _write_jsonl(os.path.join(folder, "se-standin.jsonl"), se_recs)
    _write_jsonl(os.path.join(folder, "sn-synth.jsonl"), sn)
    _write_jsonl(os.path.join(folder, "s3-session.jsonl"), sess)
    with open(os.path.join(folder, "BepInEx-LogOutput.log"), "w", encoding="utf-8") as f:
        f.write("[Info   :DeepEngineers] DE.Hello side=sn\n[Warning:   Unity Log] some benign game warning\n")

def selftest() -> int:
    import shutil
    import tempfile
    base = tempfile.mkdtemp(prefix="de_s3_an_")
    problems = []
    print("S3 analyze_s3 self-test (synthetic sessions in %s)" % base)
    expect_red = dict(MUTANT_ROWS)
    for mutant in [None] + sorted(MUTANT_ROWS):
        if mutant == "S3.M8":
            continue        # S3.M8 is an image seeded failure, covered by image_checks --selftest (bump inverted)
        folder = os.path.join(base, mutant or "base")
        synth_session(folder, mutant)
        rows = evaluate(folder)
        write_results(folder, rows)
        bad = [r for r in rows if r.get("required", True) and r["status"] in ("FAIL", "MISSING")
               and not r["id"].endswith(".red")]
        if mutant is None:
            ok = not bad
            print("  %s base session: %d rows, %d required FAIL/MISSING %s" % ("ok  " if ok else "BAD ", len(rows),
                  len(bad), [(r["id"], r["status"], r["measured"]) for r in bad]))
            if not ok:
                problems.append("base")
            if ok:
                print(C.table(rows))
        else:
            want = set(expect_red[mutant])
            red = {r["id"] for r in rows if r["status"] == "FAIL"}
            others = {r["id"] for r in bad} - want - {mutant + ".red"}
            ok = want <= red and not others and any(r["id"] == mutant + ".red" and r["status"] == "PASS" for r in rows)
            print("  %s %s: red %s (wanted %s)%s" % ("ok  " if ok else "BAD ", mutant, sorted(red & want), sorted(want),
                  "; also bad: %s" % sorted(others) if others else ""))
            if not ok:
                problems.append(mutant)
    # the same sessions in the Subnautica plugin's own line formats (the adapters of parse_record / adapt)
    for mutant in (None, "S3.M1", "S3.M2", "S3.M3", "S3.M6", "S3.M7", "S3.M8"):
        folder = os.path.join(base, "plugin-" + (mutant or "base"))
        synth_session(folder, mutant, fmt="plugin")
        rows = evaluate(folder)
        bad = {r["id"] for r in rows if r.get("required", True) and r["status"] in ("FAIL", "MISSING")
               and not r["id"].endswith(".red")}
        if mutant is None:
            ok = not bad
            print("  %s plugin-format base: %d rows, required FAIL/MISSING %s" % ("ok  " if ok else "BAD ", len(rows),
                  sorted(bad) or "none"))
        else:
            want = set(MUTANT_ROWS[mutant])
            red = {r["id"] for r in rows if r["status"] == "FAIL"}
            ok = want <= red and not (bad - want)
            print("  %s plugin-format %s: red %s (wanted %s)%s" % ("ok  " if ok else "BAD ", mutant, sorted(red & want),
                  sorted(want), "; also bad: %s" % sorted(bad - want) if bad - want else ""))
        if not ok:
            problems.append("plugin-" + (mutant or "base"))
    # the same scene captured by Subnautica's lab runs (one picture per pose, Seamoth bounds only in the S3 sidecars)
    for mutant in (None, "S3.M5"):
        folder = os.path.join(base, "lab-" + (mutant or "base"))
        synth_session(folder, mutant, fmt="plugin", captures="lab")
        rows = evaluate(folder)
        bad = {r["id"] for r in rows if r.get("required", True) and r["status"] in ("FAIL", "MISSING")
               and not r["id"].endswith(".red")}
        img = {r["id"]: r["status"] for r in rows if r["id"] in ("S3.C5", "S3.C5r", "S3.C6", "S3.C7", "S3.C8", "S3.C9",
                                                                 "S3.C10")}
        if mutant is None:
            ok = not bad and all(v == "PASS" for v in img.values()) and len(img) == 7
            print("  %s lab-run captures base: %s%s" % ("ok  " if ok else "BAD ", img, "; bad %s" % sorted(bad) if bad
                  else ""))
        else:
            ok = img.get("S3.C10") == "FAIL" and not (bad - {"S3.C10"})
            print("  %s lab-run captures S3.M5: C10 %s%s" % ("ok  " if ok else "BAD ", img.get("S3.C10"),
                  "; also bad %s" % sorted(bad - {"S3.C10"}) if bad - {"S3.C10"} else ""))
        if not ok:
            problems.append("lab-" + (mutant or "base"))
    # S3.C11f under a frame cap: the wall frame times are equal (vsync), the main-thread
 # CPU grows by 0.53 ms: red
    folder = os.path.join(base, "c11cost")
    synth_session(folder, "C11cost", fmt="plugin")
    r11 = next((r for r in evaluate(folder) if r["id"] == "S3.C11f"), {})
    ok = r11.get("status") == "FAIL"
    print("  %s seeded C11 cost under vsync: S3.C11f %s (%s)" % ("ok  " if ok else "BAD ", r11.get("status"),
                                                                 r11.get("measured")))
    if not ok:
        problems.append("C11cost")
    # S3.N3's own seeded failure (not a ROADMAP mutant): a 3 px offset of Subnautica's corner pixel
    folder = os.path.join(base, "n3shift")
    synth_session(folder, "N3shift")
    r3 = next((r for r in evaluate(folder) if r["id"] == "S3.N3"), {})
    ok = r3.get("status") == "FAIL"
    print("  %s seeded N3 offset 3 px: S3.N3 %s (%s)" % ("ok  " if ok else "BAD ", r3.get("status"), r3.get("measured")))
    if not ok:
        problems.append("N3shift")
    # parser spot checks
    pr = parse_record({"event": "DE.Mirror", "text": "DE.Mirror built grid=0x10 parts=1 verts=24 tris=12"})
    ok = pr and pr[0] == "s3.mirror" and pr[1]["verts"] == 24 and gid(pr[1]["grid"]) == 16
    print("  %s text line DE.Mirror built -> s3.mirror" % ("ok  " if ok else "BAD "))
    if not ok:
        problems.append("parser")
    pr = parse_record({"ev": "s3.gps", "tick": 5, "qpc": 77, "px": [1.0, 2.0]})
    ok = pr and pr[0] == "s3.gps" and pr[1]["px"] == [1.0, 2.0]
    print("  %s structured s3.gps event" % ("ok  " if ok else "BAD "))
    if not ok:
        problems.append("parser2")
    shutil.rmtree(base, ignore_errors=True)
    print("ANALYZE S3 SELFTEST %s%s" % ("PASS" if not problems else "FAIL", "" if not problems else " %s" % problems))
    return 0 if not problems else 1

def main(argv) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="S3 oracle table (ROADMAP S3)")
    ap.add_argument("folder", nargs="?")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--s1-results", default=None, help="s1-results.json whose S1.N2 step p50 is the N2 reference")
    ap.add_argument("--baseline-logs", default=None, help="folder with a reference LogOutput.log / Player.log")
    ap.add_argument("--lenient", action="store_true", help="MISSING data does not fail the table")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.folder:
        ap.error("a session folder or --selftest")
    rows = evaluate(a.folder, a.s1_results, a.baseline_logs)
    ok = write_results(a.folder, rows, not a.lenient)
    print("S3 ORACLE TABLE (%s)  (* = not required)" % a.folder)
    print(C.table(rows))
    print("S3 ORACLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
