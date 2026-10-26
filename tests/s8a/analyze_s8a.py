"""S8a "The terrain converter" oracle table, Stage A (S8a-1 drilling): joins the SE and Subnautica JSONL rows, the
launcher's session log (s8a-session.jsonl) and the offline results of one collected folder (tests/out/s8a/<stamp>/ from
`launcher.py s8a`) into the rows of ROADMAP "### S8a" (S8a.C1, C3, N1-N6) and the seeded failures S8a.M1-M5, M12-M14.
Stdlib only.

 python -I tests/s8a/analyze_s8a.py DIR [--lenient] [--json]
 python -I tests/s8a/analyze_s8a.py --selftest # synthetic sessions: every row PASS; each drilling mutant red on
 # its rows only (M1-M5, M12-M14; M13 through the c1 JSONs)

S8a LOG CONTRACT (CONTRACT §5.4 "S8a conventions" rows + the launcher's session events; SE rows flat {"t", "ev",...},
Subnautica rows {"t", "event", "ev", "data": {...}}):
 SE s8a.drill tick (the cut's tick), witnessTick, sn [x, y, z], se, radius, path sphere|hand, tile, beforeY, afterY,
 deeper, modified (IsRangeModified), overlayCells, drillGen, drillOn, m12 (de.s8a drill's witness)
 s8a.edit tick, path, tile, min, max, cells, count, suppressed, overlayCells
 s8a.write tick, layer, bx, by, bz, version, bytes, fnv (x16), ms, path, base, octrees, cells, box, coalesceMs
 s8a.notice tick, seq, kind, layer, flags, bx, by, bz, version, min, max, octrees, cells, pending, stream
 s8a.feed tick, stream, firstSeq, rows, pending, dropped, ackSeq, appliedSeq, resyncEpoch,... (1 Hz and on change)
 s8a.overlay tick, cells, hash, why save|restore|mirror|refuse|carried, bytes, target, m3
 s8a.store tick, refused, reason, root
 s8a.rays tick, label, columns, storageHits, castHits, hits [{x, z, storageY, castY, tile, castTile}]
 s8a.perf tick, hookUsP99, hookUsMax, errors
 SN s8a.notice snFrame, seq, kind, layer, flags, bx, by, bz, version, served, state, scheduledMs, appliedMs, cellsReloaded,
 needsReload, error, ackSeq, appliedSeq
 s8a.load snFrame, seq, bx, by, bz, version, path, bytes, fnv (x16), ok, served, ms, error
 s8a.status snFrame, ackSeq, appliedSeq, chartSeqApplied, served, failures, nearPendingSeen, areaBuiltNative, areaBuilt,... (1 Hz)
 s8a.reload snFrame, bx, by, bz, why, inRing, cellsReloaded, ms, failed, error (de.s8a reload, manifest changes)
 s4.probe batch (the rays label), i, x, z, yHint, state, hit, y,... (de.s8a rays -> de.s4 probe)
 session (s8a-session.jsonl, launcher_s8a.py):
 start {kind s8a, parts, mutants, world, band, snEnv, seEnv, storeRoot, storeKey, sites [{i, x, z, path, seam}], quick}
 part {name, phase begin|end, tick}
 reply {side, cmd, args, reply, label}
 drill {i, sn [x, y, z], radius, path, seam, s (horizontal scale Rs/(Rs+y)), tick, reply}
 applied {i, ok, waited, feed {pending, ackSeq, appliedSeq, nextSeq}}
 rays {label, side se|sn, site, kind footprint|n3|rim|seam|axis, when before|after|reload|presave|relaunch,
 columns [[x, z, yHint],...], results [{x, z, y (sn) | storageY, castY (se),...}], state}

 savefile {path, exists, bytes, ok, version, site, cells, hash (x16), error}
 relaunch {phase closing|started|inworld, ok}
 world_scan {folder, vx2, tileVx2, storageNames, tileVoxelMaps, error} (S8a.N4.portable: analyze_s4.scan_world)
 store_check {storeKey, folder, manifest {entries, key, hash, error}, files [{bx, by, bz, version, bytes, fnv, exists,
 bytesOnDisk, fnvOnDisk, ok}]}
 install_listing {when, path} install_compare {ok, counts, changed, added, removed, octreeHashBefore, octreeHashAfter}
 host_slot {when before|after, dir, files {rel: sha256}} (S1.N4's hash, written by the launcher)
 c1 {cs {path, rc, json}, py {path, rc, json}} c3 {wire {rc, tail}, ref {rc, tail}, layout {rc, tail}}

ROWS (PASS, FAIL, MISSING, INFO): S8a.C1.{roundtrip,rules}, S8a.C3.pin, S8a.N1.{edit,latency,visible}, S8a.N2.{position,
size,overlap}, S8a.N3.{rays,rim*}, S8a.N4.{save,file,portable}, S8a.N5.{store,bytes,reload}, S8a.N6.{install,writes,
hostsave}, S8a.Mx.red (* = information). Rows of parts the session did not run are not listed. The seam site (one of
the six, on the tile overlap at SN x = 0) is judged by N1.edit, N1.latency and N2.overlap only: N1.visible, N2.position,
N2.size and N3.rays take the other sites (a scripted drill cuts one tile; the overlap's other tile shows the hole through
the mirror, the 0.5 m sliver beyond the overlap keeps that tile's seabed by design). A site whose edit did not land
(N1.edit red) leaves its N1.latency, N1.visible, N2 and N3 rows MISSING (nothing to measure), never FAIL.
Exit code 0 when every required row passes (MISSING fails unless --lenient).
"""
from __future__ import annotations

import glob
import json
import math
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tools", "planet")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import s2_common as C   # noqa: E402 (jsonl reader, normalise, rows, table, verdict)

PREFIXES = ("s8a.", "s4.")
TICKS_PER_S = 60.0
PINNED_HASH = "cbf4ca049d06c430"
STORE_ROOT_TAIL = os.path.join("DeepEngineers", "terrain")
RS = 60000.00390625                     # the sea sphere radius (site v1); s = Rs / (Rs + y)

# ROADMAP "### S8a" oracle thresholds
EDIT_DEEPER_M = 1.5
LATENCY_P95_S, LATENCY_MAX_S = 1.0, 2.5
POSITION_M = 0.25
SIZE_M = 0.25
SURFACE_P95_M, SURFACE_MAX_M = 0.25, 0.5
RAYS_P95_M, RAYS_MAX_M = 0.1, 0.5
OVERLAP_DEEPER_M = 0.5
# S8a.N7.edit's surface bound: the planet's cut is
# PerformCutOutSphereFast's binary content on SE's 1 m lattice, converted as it stands; the drill layer's N2 holes are 2.5 m and keep
# SURFACE_P95_M; the window's 6 m hole is judged at SURFACE_MAX_M (centre and radius keep N2's 0.25 m)
WINDOW_EDIT_P95_M = 0.5
CARVE_MIN_DEPTH_M = 1.0                 # a footprint column belongs to the carve when this far below the reference seabed
SAVE_SE_M = 0.05
RELOAD_DEEPER_M = 1.0                   # every n3 column (within 0.7 r) is at least this much below the seabed after a reload

# ROADMAP "### S8a" seeded failures (Stage A: the drilling mutants; Stage B's listed for the launcher's groups)
MUTANT_RED = {
    "S8a.M1": ("S8a.N1.latency", "S8a.N1.visible", "S8a.N3.rays"),
    "S8a.M2": ("S8a.N2.position", "S8a.N3.rays"),
    "S8a.M3": ("S8a.N4.save", "S8a.N4.file"),
    "S8a.M4": ("S8a.N5.reload",),
    "S8a.M5": ("S8a.N6.writes",),
    "S8a.M6": ("S8a.N7.edit",),
    "S8a.M7": ("S8a.N8.snap",),
    "S8a.M8": ("S8a.N8.batchstep",),
    "S8a.M9": ("S8a.N9.gate",),
    "S8a.M10": ("S8a.N10.material", "S8a.N10.ore"),
    "S8a.M11": ("S8a.N10.spawn",),
    "S8a.M12": ("S8a.N1.edit",),
    "S8a.M13": ("S8a.C1.rules", "S8a.C1.roundtrip"),
    "S8a.M14": ("S8a.N2.overlap",),
    "S8a.M29": ("S8a.NL.look",),
    "S8a.M21": ("S8a.NO.cover",),
    "S8a.M22": ("S8a.NO.vein",),
    "S8a.M23": ("S8a.NO.agree",),
    "S8a.M27": ("S8a.N10.oredensity",),
    "S8a.M30": ("S8a.NO.voxel", "S8a.NO.look"),
    "S8a.M24": ("S8a.NO.shrink",),
    "S8a.M25": ("S8a.NO.immune",),
    "S8a.M26": ("S8a.ND.props",),
}
STAGE_A_MUTANTS = ("S8a.M1", "S8a.M2", "S8a.M3", "S8a.M4", "S8a.M5", "S8a.M12", "S8a.M13", "S8a.M14")
# rows that a mutant leaves MISSING besides its red ones (no hole: nothing to measure)
MUTANT_ALSO_MISSING = {
    "S8a.M12": ("S8a.N1.latency", "S8a.N1.visible", "S8a.N2.position", "S8a.N2.size", "S8a.N2.overlap", "S8a.N3.rays"),
    "S8a.M9": ("S8a.N9.resume",),        # the flag never rises under M9: there is no NearPending episode to time the resume of
}
# rows a mutant also turns red by its own mechanics (listed as a discrepancy with the roadmap's table, not hidden): S8a.M5
# writes the drill files under the SE world folder, where Subnautica (which reads the store root, not the world) never
# finds them: the hole never shows there (N1.visible)
MUTANT_ALSO_RED = {
    "S8a.M5": ("S8a.N1.visible",),
}

def pct(values, q):
    v = sorted(values)
    if not v:
        return None
    i = int(math.ceil(q * len(v))) - 1
    return v[max(0, min(len(v) - 1, i))]

def _num(d, *names):
    return C.num(d, *names)

def _dist2(ax, az, bx, bz):
    return (ax - bx) ** 2 + (az - bz) ** 2

def hole_depth(cx, cy, cz, rh, rv, x, z, seabed):
    """The analytic carve: the SN surface along the column (x, z) after a sphere of SE radius r cut at the seabed maps to
 an ellipsoid (vertical radius rv = r, horizontal rh = r * s): min(seabed, the ellipsoid's lower surface) inside the
 footprint, the seabed outside."""
    d2 = _dist2(x, z, cx, cz) / (rh * rh) if rh > 0 else 2.0
    if d2 >= 1.0:
        return seabed
    return min(seabed, cy - rv * math.sqrt(1.0 - d2))

def fit_carve(columns, guess, s=1.0):
    """columns: [(x, z, seabed, y)]; guess (cx, cy, cz, r); the carve of an SE sphere of radius r is the ellipsoid with
 vertical radius r and horizontal radius r * s (s = Rs / |c - C|, known from the drill point), so four parameters are
 fitted (a free vertical/horizontal pair is degenerate on the carve's inner cap). Nelder-Mead minimising the sum of
 squared residuals (restarted once from the result, then a fine pattern search); returns ((cx, cy, cz, r*s, r), residuals)."""

    def cost(q):
        if q[3] <= 0.05:
            return 1e30
        tot = 0.0
        for x, z, b, y in columns:
            e = y - hole_depth(q[0], q[1], q[2], q[3] * s, q[3], x, z, b)
            tot += e * e
        return tot

    def nelder_mead(p0, scale, iters):
        n = len(p0)
        simplex = [list(p0)]
        for k in range(n):
            q = list(p0)
            q[k] += scale
            simplex.append(q)
        vals = [cost(q) for q in simplex]
        for _ in range(iters):
            order = sorted(range(n + 1), key=lambda i: vals[i])
            simplex = [simplex[i] for i in order]
            vals = [vals[i] for i in order]
            if abs(vals[-1] - vals[0]) < 1e-14 and max(abs(simplex[-1][k] - simplex[0][k]) for k in range(n)) < 1e-5:
                break
            centroid = [sum(simplex[i][k] for i in range(n)) / n for k in range(n)]
            worst = simplex[-1]
            refl = [centroid[k] + (centroid[k] - worst[k]) for k in range(n)]
            fr = cost(refl)
            if fr < vals[0]:
                exp = [centroid[k] + 2.0 * (centroid[k] - worst[k]) for k in range(n)]
                fe = cost(exp)
                if fe < fr:
                    simplex[-1], vals[-1] = exp, fe
                else:
                    simplex[-1], vals[-1] = refl, fr
            elif fr < vals[-2]:
                simplex[-1], vals[-1] = refl, fr
            else:
                con = [centroid[k] + 0.5 * (worst[k] - centroid[k]) for k in range(n)]
                fc = cost(con)
                if fc < vals[-1]:
                    simplex[-1], vals[-1] = con, fc
                else:
                    best0 = simplex[0]
                    for i in range(1, n + 1):
                        simplex[i] = [best0[k] + 0.5 * (simplex[i][k] - best0[k]) for k in range(n)]
                        vals[i] = cost(simplex[i])
        i = min(range(n + 1), key=lambda j: vals[j])
        return simplex[i], vals[i]

    p, best = nelder_mead(list(guess[:4]), 0.5, 600)
    p, best = nelder_mead(p, 0.1, 400)
    step = 0.05
    while step >= 0.002:
        improved = False
        for k in range(4):
            for sgn in (1.0, -1.0):
                q = list(p)
                q[k] += sgn * step
                c = cost(q)
                if c < best - 1e-14:
                    best, p, improved = c, q, True
        if not improved:
            step *= 0.5
    full = [p[0], p[1], p[2], p[3] * s, p[3]]
    res = [abs(y - hole_depth(full[0], full[1], full[2], full[3], full[4], x, z, b)) for x, z, b, y in columns]
    return full, res

class Logs:
    """A collected S8a session folder."""

    def __init__(self, folder):
        self.folder = folder
        self.session = C.read_jsonl(os.path.join(folder, "s8a-session.jsonl"))
        self.se, self.sn, self.se_text = [], [], []
        for i, p in enumerate(sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))):
            raw = C.read_jsonl(p)
            for e in C.events(raw, PREFIXES):
                e["_file"] = i
                self.se.append(e)
            self.se_text += C.text_lines(raw)
        for i, p in enumerate(sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))):
            raw = C.read_jsonl(p)
            for e in C.events(raw, PREFIXES):
                e["_file"] = i
                self.sn.append(e)
        self.start = next((r for r in self.session if r.get("ev") == "start"), {})

    def se_ev(self, name):
        return [e for e in self.se if e.get("_ev") == name]

    def sn_ev(self, name):
        return [e for e in self.sn if e.get("_ev") == name]

    def events(self, ev, **match):
        out = []
        for r in self.session:
            if r.get("ev") != ev:
                continue
            if all(r.get(k) == v for k, v in match.items()):
                out.append(r)
        return out

    def parts_run(self):
        return [r.get("name") for r in self.session if r.get("ev") == "part" and r.get("phase") == "begin"]

    def ran(self, part):
        return part in self.parts_run()

    def window(self, name):
        """(name, tick_begin, tick_end) of a launcher part (or path) by its begin/end events; the last begin wins, an open part ends at +inf; None when never begun."""
        b = e = None
        for r in self.session:
            if r.get("ev") != "part" or r.get("name") != name:
                continue
            if r.get("phase") == "begin":
                b, e = _num(r, "tick"), None
            elif r.get("phase") == "end" and b is not None:
                e = _num(r, "tick")
        if b is None:
            return None
        return (name, b, e if e is not None else float("inf"))

    def sn_frames(self, name):
        """(first, last) Subnautica frame of a launcher part (its begin/end events' C7 snFrame), or None."""
        b = e = None
        for r in self.session:
            if r.get("ev") != "part" or r.get("name") != name:
                continue
            f = (r.get("c7") or {}).get("snFrame")
            if r.get("phase") == "begin":
                b, e = f, None
            elif r.get("phase") == "end" and b is not None:
                e = f
        if not isinstance(b, int):
            return None
        return (b, e if isinstance(e, int) else 1 << 62)

    def sites(self):
        """{i: drill event} of the holes drilled this session (the last drill of each site)."""
        out = {}
        for d in self.events("drill"):
            if isinstance(d.get("i"), int) and not d.get("skipped") and not d.get("window"):
                out[d["i"]] = d
        return out

    def window_drill(self):
        """The n7 drill into the generated seabed (a window batch), or None."""
        for d in reversed(self.events("drill")):
            if d.get("window") and not d.get("skipped"):
                return d
        return None

    def rays(self, side, kind, when, site=None):
        """The last rays event of that side/kind/when (and site): its results list, or None."""
        best = None
        for r in self.events("rays", side=side, kind=kind, when=when):
            if site is None or r.get("site") == site:
                best = r
        return best

    def se_raw_samples(self):
        """The SE plugin's 1 s sample rows (S8 memory columns) of every se-*.jsonl."""
        out = []
        for p in sorted(glob.glob(os.path.join(self.folder, "se-*.jsonl"))):
            out += [r for r in C.read_jsonl(p) if r.get("ev") == "sample"]
        return out

    def tick_of_status(self, d):
        """The SE tick of a Subnautica status row: its presentedSeTick/seTick when present, else the session's 10 Hz samples (snFrame)."""
        for k in ("presentedSeTick", "seTick", "c1Tick"):
            v = _num(d, k)
            if v is not None and v > 0:
                return v
        f = _num(d, "snFrame")
        if f is None:
            return None
        if not hasattr(self, "_fmap"):
            fm = []
            for r in self.session:
                if r.get("ev") == "sample" and isinstance(r.get("c7"), dict) and r.get("c1Tick") is not None:
                    ff = r["c7"].get("snFrame")
                    if isinstance(ff, (int, float)):
                        fm.append((float(ff), float(r["c7"].get("presentedSeTick") or r["c1Tick"])))
            self._fmap = sorted(fm)
        if not self._fmap:
            return None
        import bisect
        i = bisect.bisect_left(self._fmap, (f, -1.0))
        if i <= 0:
            return self._fmap[0][1]
        if i >= len(self._fmap):
            return self._fmap[-1][1]
        (f0, t0), (f1, t1) = self._fmap[i - 1], self._fmap[i]
        return t0 if f1 == f0 else t0 + (t1 - t0) * (f - f0) / (f1 - f0)

    def se_drill_row(self, drill):
        """The SE s8a.drill witness row of a launcher drill event (same SN x, z within 5 cm; the last one)."""
        sn = drill.get("sn") or []
        if len(sn) < 3:
            return None
        hit = None
        for e in self.se_ev("s8a.drill"):
            p = e.get("sn")
            if isinstance(p, list) and len(p) >= 3 and abs(p[0] - sn[0]) < 0.05 and abs(p[2] - sn[2]) < 0.05:
                hit = e
        return hit

def row(rows, cid, status, measured, threshold, what, part="", required=True, **detail):
    return C.row(rows, cid, status, measured, threshold, what, part, required, **detail)

# ----------------------------------------------------------------------------------------------------------------
# per-site helpers
# ----------------------------------------------------------------------------------------------------------------

def site_edit(L, drill):
    """(ok, detail) of a site's SE edit: the witness row says modified and deeper >= 1.5 m."""
    e = L.se_drill_row(drill)
    if e is None:
        return None, "no s8a.drill witness row"
    deeper = _num(e, "deeper")
    ok = bool(e.get("modified")) and deeper is not None and deeper >= EDIT_DEEPER_M
    return ok, "modified=%s deeper=%s" % (e.get("modified"), "n/a" if deeper is None else "%.2f" % deeper)

def sites_edit_ok(L):
    out = {}
    for i, d in L.sites().items():
        ok, _why = site_edit(L, d)
        out[i] = bool(ok)
    return out

def sn_results_map(res):
    """{(x, z): y} of a Subnautica rays result list (hit columns only)."""
    out = {}
    for r in res or []:
        if not isinstance(r, dict):
            continue
        x, z, y = _num(r, "x"), _num(r, "z"), _num(r, "y")
        if x is None or z is None or y is None or r.get("hit") is False or (isinstance(y, float) and math.isnan(y)):
            continue
        out[(round(x, 3), round(z, 3))] = y
    return out

def _finite(v):
    return isinstance(v, (int, float)) and not (isinstance(v, float) and math.isnan(v))

def se_results_map(res, key="castY"):
    """{(x, z): SN y} of SE ray results. key "surface" (the window, Stage C fix): castY where SE had voxel physics there, else
 storageY, the planet's LOD-0 mesh (the geometry SE builds its physics from; far window columns have no physics)."""
    out = {}
    for r in res or []:
        if not isinstance(r, dict):
            continue
        if key == "surface":
            y = _num(r, "castY")
            if not _finite(y):
                y = _num(r, "storageY")
            x, z = _num(r, "x"), _num(r, "z")
        else:
            x, z, y = _num(r, "x"), _num(r, "z"), _num(r, key)
        if x is None or z is None or y is None or (isinstance(y, float) and math.isnan(y)):
            continue
        out[(round(x, 3), round(z, 3))] = y
    return out

def before_map(L, site, kind):
    """The reference seabed per column: the SE storage surface before the drill (the S4 reference terrain)."""
    r = L.rays("se", kind, "before", site)
    return se_results_map(r.get("results") if r else None, "storageY")

# ----------------------------------------------------------------------------------------------------------------
# rows
# ----------------------------------------------------------------------------------------------------------------

def c1_rows(L, rows):
    if not L.ran("c1"):
        return
    ev = L.events("c1")
    if not ev:
        row(rows, "S8a.C1.roundtrip", "MISSING", "no c1 event", "files >= 100 identical", "converter round trip on the install (C# and Python)", "c1")
        row(rows, "S8a.C1.rules", "MISSING", "no c1 event", "0 bad nodes", "writer-rule census", "c1")
        return
    c1 = ev[-1]
    halves = []
    for side in ("cs", "py"):
        h = c1.get(side) or {}
        j = h.get("json")
        if isinstance(j, str) and os.path.exists(j):
            try:
                with open(j, encoding="utf-8") as f:
                    j = json.load(f)
            except (OSError, ValueError):
                j = None
        halves.append((side, h, j if isinstance(j, dict) else None))
    rt_ok, ru_ok, meas, rules_meas, any_json = True, True, [], [], False
    for side, h, j in halves:
        if j is None:
            if h:
                meas.append("%s: no json (rc %s)" % (side, h.get("rc")))
                rt_ok = ru_ok = False
            continue
        any_json = True
        files, diff = _num(j, "files") or 0, _num(j, "filesDifferent")
        rt = (j.get("rows") or {}).get("S8a.C1.roundtrip")
        good = files >= 100 and diff == 0 and rt == "PASS" and (h.get("rc") in (None, 0))
        rt_ok &= good
        meas.append("%s %d files, %s different%s" % (side, files, diff, "" if good else " (FAIL)"))
        r = j.get("rules") or {}
        rw = j.get("rulesWritten") or r
        bad = sum((_num(x, k) or 0) for x in (r, rw) for k in ("downsampleBad", "collapsible", "layoutBad", "countBad"))
        rrow = (j.get("rows") or {}).get("S8a.C1.rules")
        rgood = bad == 0 and rrow == "PASS" and (_num(r, "interior") or 0) > 0
        ru_ok &= rgood
        rules_meas.append("%s interior %s bad %d" % (side, r.get("interior"), bad))
        if side == "cs":
            m13 = j.get("m13") or {}
            if "red" in m13 and not m13.get("red"):
                ru_ok = False
                rules_meas.append("M13 not red")
            rd = j.get("reader") or {}
            if _num(rd, "diffs") not in (None, 0):
                rt_ok = False
                meas.append("S4 reader diffs %s" % rd.get("diffs"))
    if not any_json:
        row(rows, "S8a.C1.roundtrip", "MISSING", "; ".join(meas) or "no result json", "files >= 100 identical", "converter round trip on the install", "c1")
        row(rows, "S8a.C1.rules", "MISSING", "no result json", "0 bad nodes", "writer-rule census", "c1")
        return
    row(rows, "S8a.C1.roundtrip", "PASS" if rt_ok else "FAIL", "; ".join(meas), ">= 100 files, 0 different, S4 reader 0 diffs",
        "parse -> raster -> write -> bytes identical (C# S8aOctree and the Python twin)", "c1")
    row(rows, "S8a.C1.rules", "PASS" if ru_ok else "FAIL", "; ".join(rules_meas), "0 off CacheDownsampledType, 0 collapsible, layout pre-order",
        "writer-rule census on every parsed and written octree", "c1")

def c3_rows(L, rows):
    if not L.ran("c3"):
        return
    ev = L.events("c3")
    if not ev:
        row(rows, "S8a.C3.pin", "MISSING", "no c3 event", "hash %s" % PINNED_HASH, "Wire text = CONTRACT block = Python twin; layout 1.0 compare PASS", "c3")
        return
    c3 = ev[-1]
    parts = []
    ok = True
    for k, want in (("wire", None), ("ref", "PASS"), ("layout", "LAYOUT COMPARE PASS")):
        h = c3.get(k) or {}
        rc = h.get("rc")
        tail = str(h.get("tail") or "")
        good = rc == 0 and (want is None or want in tail)
        if k == "ref":
            good &= PINNED_HASH in tail.lower()
        ok &= good
        parts.append("%s rc %s%s" % (k, rc, "" if good else " FAIL"))
    row(rows, "S8a.C3.pin", "PASS" if ok else "FAIL", ", ".join(parts), "S8aConventionsTests green; s8a_ref selftest PASS with %s; layout compare PASS" % PINNED_HASH,
        "the S8a pin: Wire text = CONTRACT block = Python twin, layout 1.0 unchanged", "c3")

def n1_rows(L, rows):
    if not L.ran("n1"):
        return
    sites = L.sites()
    if not sites:
        for cid, what in (("S8a.N1.edit", "SE's storage changed at every drill site"), ("S8a.N1.latency", "edit tick -> C7 appliedSeq covers the notice"),
                          ("S8a.N1.visible", "a Subnautica ray down the hole's axis >= 1.5 m deeper")):
            row(rows, cid, "MISSING", "no drill events", "-", what, "n1")
        return
    edits = {}
    details = []
    for i in sorted(sites):
        ok, why = site_edit(L, sites[i])
        edits[i] = ok
        details.append("site %d %s" % (i, why))
    n_ok = sum(1 for v in edits.values() if v)
    status = "FAIL" if any(v is False for v in edits.values()) else ("MISSING" if any(v is None for v in edits.values()) else "PASS")
    row(rows, "S8a.N1.edit", status, "%d/%d sites modified and >= %.1f m deeper" % (n_ok, len(edits), EDIT_DEEPER_M), "every site",
        "SE's storage changed (IsRangeModified, SE ray into the hole >= 1.5 m deeper)", "n1", sites="; ".join(details))
    good_sites = [i for i in sorted(sites) if edits.get(i)]
    # latency: the SE drill tick -> the first s8a.feed row whose appliedSeq covers the drill's notices (+ one presented frame)
    if not good_sites:
        row(rows, "S8a.N1.latency", "MISSING", "no site with a landed edit", "P95 <= %.1f s, max <= %.1f s" % (LATENCY_P95_S, LATENCY_MAX_S), "drill edit -> applied in Subnautica", "n1")
        row(rows, "S8a.N1.visible", "MISSING", "no site with a landed edit", ">= %.1f m deeper" % EDIT_DEEPER_M, "a Subnautica ray down the hole's axis", "n1")
        return
    lat, lat_fail = [], []
    drill_rows = sorted([(e["tick"], e) for e in L.se_ev("s8a.drill") if _num(e, "tick") is not None], key=lambda t: t[0])
    for i in good_sites:
        d = sites[i]
        e = L.se_drill_row(d)
        t0 = _num(e, "tick")
        f = e.get("_file")
        later = [t for t, x in drill_rows if x.get("_file") == f and t > t0]
        t1 = min(later) if later else float("inf")
        notices = [n for n in L.se_ev("s8a.notice") if n.get("_file") == f and _num(n, "kind") == 1 and _num(n, "layer") == 1
                   and _num(n, "tick") is not None and t0 <= n["tick"] < t1]
        if not notices:
            lat_fail.append("site %d: no notice after the edit" % i)
            continue
        seq = max(_num(n, "seq") for n in notices)
        feed = [x for x in L.se_ev("s8a.feed") if x.get("_file") == f and _num(x, "tick") is not None and x["tick"] >= t0 and (_num(x, "appliedSeq") or 0) >= seq]
        if not feed:
            lat_fail.append("site %d: appliedSeq never covered seq %d" % (i, seq))
            continue
        ta = min(_num(x, "tick") for x in feed)
        lat.append((ta - t0) / TICKS_PER_S + 1.0 / TICKS_PER_S)
    if lat_fail and not lat:
        row(rows, "S8a.N1.latency", "FAIL", "; ".join(lat_fail), "P95 <= %.1f s, max <= %.1f s" % (LATENCY_P95_S, LATENCY_MAX_S),
            "SE edit tick -> the SE tick at which C7 appliedSeq covers its notice, plus one presented frame", "n1")
    else:
        p95, mx = pct(lat, 0.95), max(lat)
        ok = p95 <= LATENCY_P95_S and mx <= LATENCY_MAX_S and not lat_fail
        row(rows, "S8a.N1.latency", "PASS" if ok else "FAIL", "P95 %.2f s, max %.2f s over %d sites%s" % (p95, mx, len(lat), "; " + "; ".join(lat_fail) if lat_fail else ""),
            "P95 <= %.1f s, max <= %.1f s" % (LATENCY_P95_S, LATENCY_MAX_S), "SE edit tick -> C7 appliedSeq covers its notice (+ 1 frame)", "n1",
            latencies=[round(x, 3) for x in lat])
    # visible: the Subnautica axis ray after the drill vs the SE storage before it (non-seam sites)
    vis, miss, fail = [], [], []
    for i in good_sites:
        d = sites[i]
        if d.get("seam"):
            continue
        after = L.rays("sn", "axis", "after", i)
        before = before_map(L, i, "footprint") or before_map(L, i, "axis")
        sn = d.get("sn") or [0, 0, 0]
        key = (round(sn[0], 3), round(sn[2], 3))
        ya = sn_results_map(after.get("results") if after else None).get(key)
        yb = before.get(key, sn[1])
        if ya is None:
            miss.append("site %d: no Subnautica axis ray" % i)
            continue
        deeper = yb - ya
        vis.append(deeper)
        if deeper < EDIT_DEEPER_M:
            fail.append("site %d: %.2f m" % (i, deeper))
    if not vis:
        row(rows, "S8a.N1.visible", "MISSING", "; ".join(miss) or "no non-seam site", ">= %.1f m deeper" % EDIT_DEEPER_M, "a Subnautica ray down the hole's axis", "n1")
    else:
        row(rows, "S8a.N1.visible", "FAIL" if fail else ("MISSING" if miss else "PASS"), "min %.2f m deeper over %d sites%s" % (min(vis), len(vis), "; " + "; ".join(fail + miss) if fail or miss else ""),
            ">= %.1f m deeper than the reference seabed" % EDIT_DEEPER_M, "a Subnautica layer-30 ray down each hole's axis", "n1")

def n2_rows(L, rows):
    if not L.ran("n2"):
        return
    sites = L.sites()
    edits = sites_edit_ok(L)
    pos_err, size_err, surf, miss, fails_p, fails_s = [], [], [], [], [], []
    for i in sorted(sites):
        d = sites[i]
        if d.get("seam"):
            continue
        if not edits.get(i):
            miss.append("site %d: edit did not land" % i)
            continue
        after = L.rays("sn", "footprint", "after", i)
        before = before_map(L, i, "footprint")
        amap = sn_results_map(after.get("results") if after else None)
        allcols = [(x, z, before[(x, z)], y) for (x, z), y in amap.items() if (x, z) in before]
        # the carve is judged inside the hole only: columns at least CARVE_MIN_DEPTH_M below the reference;
 # the recomputed seabed around it (the stencil box) is not part of the fit
        cols = [c for c in allcols if c[2] - c[3] >= CARVE_MIN_DEPTH_M]
        if len(cols) < 9:
            miss.append("site %d: %d hole columns of %d" % (i, len(cols), len(allcols)))
            continue
        sn = d["sn"]
        r = float(d.get("radius") or 2.5)
        s = float(d.get("s") or (RS / (RS + sn[1])))
        p, res = fit_carve(cols, (sn[0], sn[1], sn[2], r), s)
        pe = math.sqrt((p[0] - sn[0]) ** 2 + (p[1] - sn[1]) ** 2 + (p[2] - sn[2]) ** 2)
        pos_err.append(pe)
        se_ = abs(p[4] - r)
        size_err.append(se_)
        surf += res
        if pe > POSITION_M:
            fails_p.append("site %d: %.2f m" % (i, pe))
        if se_ > SIZE_M:
            fails_s.append("site %d: r %.2f (rh %.2f)" % (i, p[4], p[3]))
    thr_p = "centre within %.2f m" % POSITION_M
    thr_s = "radii within %.2f m; surface P95 <= %.2f, max <= %.2f m" % (SIZE_M, SURFACE_P95_M, SURFACE_MAX_M)
    if not pos_err:
        row(rows, "S8a.N2.position", "MISSING", "; ".join(miss) or "no site", thr_p, "9 x 9 Subnautica rays vs the analytic carve: fitted centre", "n2")
        row(rows, "S8a.N2.size", "MISSING", "; ".join(miss) or "no site", thr_s, "fitted radii and surface residuals", "n2")
    else:
        row(rows, "S8a.N2.position", "FAIL" if fails_p else ("MISSING" if miss else "PASS"), "max centre error %.3f m over %d sites%s" % (max(pos_err), len(pos_err), "; " + "; ".join(fails_p + miss) if fails_p or miss else ""),
            thr_p, "9 x 9 Subnautica layer-30 rays over the footprint vs the analytic carve (ellipsoid r, r*s): fitted centre", "n2")
        p95, mx = pct(surf, 0.95), max(surf)
        bad = fails_s or p95 > SURFACE_P95_M or mx > SURFACE_MAX_M
        row(rows, "S8a.N2.size", "FAIL" if bad else ("MISSING" if miss else "PASS"), "max radius error %.3f m; surface P95 %.3f max %.3f m%s" % (max(size_err), p95, mx, "; " + "; ".join(fails_s + miss) if fails_s or miss else ""),
            thr_s, "fitted radii within a quarter of a leaf; the surface points against the fit", "n2")
    # overlap: the seam site, the n3 columns (within 0.7 r: inside the 4 m tile overlap): SE's ray and Subnautica's both deeper
    seam = [i for i in sorted(sites) if sites[i].get("seam")]
    thr_o = "every column: SE castY <= seabed - %.1f m; SN axis >= %.1f m deeper" % (OVERLAP_DEEPER_M, EDIT_DEEPER_M)
    if not seam:
        row(rows, "S8a.N2.overlap", "MISSING", "no seam site", thr_o, "the seam site: SE's ray into the hole hits the carved surface in every overlapping tile", "n2")
        return
    i = seam[-1]
    if not edits.get(i):
        row(rows, "S8a.N2.overlap", "MISSING", "seam site %d: edit did not land" % i, thr_o, "the seam site's overlapping tiles both carved", "n2")
        return
    se_r = L.rays("se", "seam", "after", i) or L.rays("se", "n3", "after", i)
    sn_r = L.rays("sn", "seam", "after", i) or L.rays("sn", "n3", "after", i)
    before = before_map(L, i, "seam") or before_map(L, i, "n3")
    semap = se_results_map(se_r.get("results") if se_r else None, "castY")
    snmap = sn_results_map(sn_r.get("results") if sn_r else None)
    cols = [k for k in before if k in semap and k in snmap]
    if len(cols) < 4:
        row(rows, "S8a.N2.overlap", "MISSING", "seam site %d: %d columns with SE, SN and reference" % (i, len(cols)), thr_o, "the seam site's overlapping tiles both carved", "n2")
        return
    bad_se = [k for k in cols if semap[k] > before[k] - OVERLAP_DEEPER_M]
    # Subnautica's half: the column nearest the axis shows the hole (the un-mirrored tile as the octree's reference tile would not)
    sn = sites[i].get("sn") or [0, 0, 0]
    axis = min(cols, key=lambda k: _dist2(k[0], k[1], sn[0], sn[2]))
    sn_deeper = before[axis] - snmap[axis]
    tiles = sorted(set(str(r.get("castTile")) for r in (se_r.get("results") or []) if isinstance(r, dict) and r.get("castTile")))
    ok = not bad_se and sn_deeper >= EDIT_DEEPER_M
    row(rows, "S8a.N2.overlap", "PASS" if ok else "FAIL", "%d columns; SE original surface at %d; SN axis %.2f m deeper; SE tiles hit %s" % (len(cols), len(bad_se), sn_deeper, ",".join(tiles) or "-"),
        "every column: SE castY <= seabed - %.1f m; SN axis >= %.1f m deeper" % (OVERLAP_DEEPER_M, EDIT_DEEPER_M),
        "the seam site: SE's ray into the hole hits the carved surface in every overlapping tile (no second tile's original surface); Subnautica shows the hole", "n2")

def _rays_compare(L, sites, edits, when, kind="n3", include_seam=False):
    """[(site, |sn - se|)] over the columns both sides hit, plus the misses."""
    diffs, miss = [], []
    for i in sorted(sites):
        d = sites[i]
        if d.get("seam") and not include_seam:
            continue
        if not edits.get(i):
            miss.append("site %d: edit did not land" % i)
            continue
        se_r = L.rays("se", kind, when, i)
        sn_r = L.rays("sn", kind, when, i)
        semap = se_results_map(se_r.get("results") if se_r else None, "castY")
        snmap = sn_results_map(sn_r.get("results") if sn_r else None)
        cols = [k for k in semap if k in snmap]
        if not cols:
            miss.append("site %d: no %s rays %s on both sides" % (i, kind, when))
            continue
        for k in cols:
            diffs.append((i, abs(snmap[k] - semap[k])))
    return diffs, miss

def n3_rows(L, rows):
    if not L.ran("n3"):
        return
    sites = L.sites()
    edits = sites_edit_ok(L)
    diffs, miss = _rays_compare(L, sites, edits, "after", "n3")
    thr = "P95 <= %.2f m, max <= %.2f m" % (RAYS_P95_M, RAYS_MAX_M)
    if not diffs:
        row(rows, "S8a.N3.rays", "MISSING", "; ".join(miss) or "no site", thr, "collision across the hole: SE CastRay vs Subnautica layer-30 rays, 64 columns within 0.7 r", "n3")
    else:
        v = [x for _i, x in diffs]
        p95, mx = pct(v, 0.95), max(v)
        ok = p95 <= RAYS_P95_M and mx <= RAYS_MAX_M
        row(rows, "S8a.N3.rays", "FAIL" if not ok else ("MISSING" if miss else "PASS"), "P95 %.3f m, max %.3f m over %d columns%s" % (p95, mx, len(v), "; " + "; ".join(miss) if miss else ""),
            thr, "collision across the hole: SE CastRay vs Subnautica layer-30 rays, 64 columns within 0.7 r of each axis", "n3")
    rim, _m = _rays_compare(L, sites, edits, "after", "rim")
    if rim:
        v = [x for _i, x in rim]
        row(rows, "S8a.N3.rim", "INFO", "P95 %.3f m, max %.3f m over %d columns" % (pct(v, 0.95), max(v), len(v)), "information (S4 measured 0.189 m P95 on the unedited seabed)",
            "the unedited rim 1.5 r-3 r: SE vs Subnautica", "n3", required=False)

def n4_rows(L, rows):
    if not L.ran("n4"):
        return
    sites = L.sites()
    edits = sites_edit_ok(L)
    # save: SE rays after the relaunch within 0.05 m of the pre-save values; Subnautica rays as N3
    se_d, miss = [], []
    for i in sorted(sites):
        if not edits.get(i):
            miss.append("site %d: edit did not land" % i)
            continue
        a = L.rays("se", "n3", "presave", i)
        b = L.rays("se", "n3", "relaunch", i)
        am = se_results_map(a.get("results") if a else None, "castY")
        bm = se_results_map(b.get("results") if b else None, "castY")
        cols = [k for k in am if k in bm]
        if not cols:
            miss.append("site %d: no SE rays before the save and after the relaunch" % i)
            continue
        se_d += [abs(am[k] - bm[k]) for k in cols]
    snd, miss2 = _rays_compare(L, sites, edits, "relaunch", "n3", include_seam=True)
    thr = "SE within %.2f m of the pre-save values; SN vs SE P95 <= %.2f m, max <= %.2f m" % (SAVE_SE_M, RAYS_P95_M, RAYS_MAX_M)
    if not se_d or not snd:
        row(rows, "S8a.N4.save", "MISSING", "; ".join(miss + miss2) or "no rays", thr, "the SE save keeps the holes through quit and relaunch", "n4")
    else:
        v = [x for _i, x in snd]
        ok = max(se_d) <= SAVE_SE_M and pct(v, 0.95) <= RAYS_P95_M and max(v) <= RAYS_MAX_M
        row(rows, "S8a.N4.save", "PASS" if ok else "FAIL", "SE max delta %.3f m over %d columns; SN vs SE P95 %.3f max %.3f m" % (max(se_d), len(se_d), pct(v, 0.95), max(v)),
            thr, "after the scripted save, both games quit and relaunched on the copy: SE rays at the holes and Subnautica rays as N3", "n4")
    # file: the save holds DE_S8a_overlay.vx2 (decodes, cell count = the session's dirty cells, hash = the saved overlayHash)
    sf = (L.events("savefile") or [None])[-1]
    saved = [e for e in L.se_ev("s8a.overlay") if e.get("why") == "save"]
    ov = (L.events("overlay") or [None])[-1]
    thr_f = "decodes; cells = the session's dirty cells; hash = overlayHash"
    if sf is None:
        row(rows, "S8a.N4.file", "MISSING", "no savefile event", thr_f, "the save holds DE_S8a_overlay.vx2", "n4")
    elif not sf.get("exists") or not sf.get("ok"):
        row(rows, "S8a.N4.file", "FAIL", "%s: %s" % (sf.get("path"), "missing" if not sf.get("exists") else sf.get("error") or "does not decode"), thr_f, "the save holds DE_S8a_overlay.vx2", "n4")
    else:
        # review low: the pre-save `de.s8a overlay` reply (the cells and hash read back right before the save) is the reference; the
 # SE save row's hash is the cross-check
        pre = (ov.get("reply") or {}) if ov else {}
        want_hash = pre.get("hash") or (saved[-1].get("hash") if saved else None)
        want_cells = _num(pre, "cells") if pre else (_num(saved[-1], "cells") if saved else None)
        row_hash = saved[-1].get("hash") if saved else None
        got_hash, got_cells = str(sf.get("hash") or "").lower(), _num(sf, "cells")
        ok = (want_hash is None or got_hash == str(want_hash).lower()) and (want_cells is None or got_cells == want_cells) and got_cells is not None and got_cells > 0
        agree = row_hash is None or want_hash is None or str(row_hash).lower() == str(want_hash).lower()
        row(rows, "S8a.N4.file", "PASS" if ok else "FAIL", "cells %s (pre-save %s), hash %s (pre-save %s%s)" % (got_cells, want_cells, got_hash, want_hash, "" if agree else "; SE save row %s" % row_hash), thr_f,
            "DE_S8a_overlay.vx2 in the save decodes with the pre-save de.s8a overlay cells and hash", "n4")
    # portable: the save holds no tile storage (S4.G2-E2)
    ws = (L.events("world_scan") or [None])[-1]
    thr_p = "no DE.Terrain .vx2, no tile StorageName, no tile voxel map"
    if ws is None:
        row(rows, "S8a.N4.portable", "MISSING", "no world_scan event", thr_p, "the save stays portable (S4.G2-E2 checks)", "n4")
    elif ws.get("error"):
        row(rows, "S8a.N4.portable", "MISSING", str(ws.get("error")), thr_p, "the save stays portable", "n4")
    else:
        bad = (ws.get("tileVx2") or []) + (ws.get("storageNames") or []) + [str(x) for x in (ws.get("tileVoxelMaps") or [])]
        row(rows, "S8a.N4.portable", "PASS" if not bad else "FAIL", "tile artefacts: %s; vx2 %s" % (",".join(bad) or "none", ",".join(ws.get("vx2") or [])), thr_p,
            "offline, read-only: no tile storage, no .vx2 with provider id 45460 in the save", "n4")

def n5_rows(L, rows):
    if not L.ran("n5"):
        return
    sc = (L.events("store_check") or [None])[-1]
    thr = "every notice's batch in the manifest with its bytes and FNV on disk"
    if sc is None or sc.get("error"):
        row(rows, "S8a.N5.store", "MISSING", (sc or {}).get("error") or "no store_check event", thr, "Subnautica's store holds every edited batch", "n5")
    else:
        files = sc.get("files") or []
        bad = [f for f in files if not f.get("ok")]
        by_id = {}
        for f in files:
            by_id[(f.get("bx"), f.get("by"), f.get("bz"))] = f
        unlisted = []
        for n in L.se_ev("s8a.notice"):
            if _num(n, "kind") != 1 or _num(n, "layer") != 1:
                continue
            f = by_id.get((n.get("bx"), n.get("by"), n.get("bz")))
            if f is None or (_num(f, "version") or 0) < (_num(n, "version") or 0):
                unlisted.append("%s-%s-%s v%s" % (n.get("bx"), n.get("by"), n.get("bz"), n.get("version")))
        ok = files and not bad and not unlisted
        row(rows, "S8a.N5.store", "PASS" if ok else ("MISSING" if not files else "FAIL"), "%d manifest entries, %d bad, %d notices unlisted%s" % (
            len(files), len(bad), len(unlisted), "; " + ",".join(unlisted[:5]) if unlisted else ""), thr, "every notice's file exists in the store with the manifest's bytes and FNV", "n5")
    # bytes: the file Subnautica served (FNV of the bytes it read) = the file SE wrote
    writes = {}
    for w in L.se_ev("s8a.write"):
        writes[(w.get("bx"), w.get("by"), w.get("bz"), w.get("version"))] = str(w.get("fnv") or "").lower()
    joined, bad = 0, []
    for l in L.sn_ev("s8a.load"):
        if not l.get("ok"):
            continue
        k = (l.get("bx"), l.get("by"), l.get("bz"), l.get("version"))
        if k in writes:
            joined += 1
            if str(l.get("fnv") or "").lower() != writes[k]:
                bad.append("%s-%s-%s v%s" % k)
    thr_b = "FNV read by Subnautica = FNV written by SE, every version"
    if joined == 0:
        row(rows, "S8a.N5.bytes", "MISSING", "no (bx, by, bz, version) in both s8a.load and s8a.write", thr_b, "the file Subnautica served = the file SE wrote", "n5")
    else:
        row(rows, "S8a.N5.bytes", "PASS" if not bad else "FAIL", "%d versions joined, %d differ%s" % (joined, len(bad), "; " + ",".join(bad[:5]) if bad else ""), thr_b,
            "the file Subnautica served (FNV of the bytes it read) = the file SE wrote (s8a.write row)", "n5")
    # reload: N3's rays after de.s8a reload all (and after the relaunch when the session had one): the holes still there
    sites = L.sites()
    edits = sites_edit_ok(L)
    thr_r = "SN vs SE P95 <= %.2f m, max <= %.2f m; every column >= %.1f m below the seabed" % (RAYS_P95_M, RAYS_MAX_M, RELOAD_DEEPER_M)
    whens = ["reload"] + (["relaunch"] if L.rays("sn", "n3", "relaunch") else [])
    diffs, miss, shallow = [], [], []
    for when in whens:
        d, m = _rays_compare(L, sites, edits, when, "n3")
        diffs += d
        miss += m
        for i in sorted(sites):
            if sites[i].get("seam") or not edits.get(i):
                continue
            sn_r = L.rays("sn", "n3", when, i)
            before = before_map(L, i, "n3")
            snmap = sn_results_map(sn_r.get("results") if sn_r else None)
            for k, y in snmap.items():
                if k in before and y > before[k] - RELOAD_DEEPER_M:
                    shallow.append((when, i))
    if not diffs:
        row(rows, "S8a.N5.reload", "MISSING", "; ".join(miss) or "no rays after the reload", thr_r, "the holes after de.s8a reload {all: true} (and after the relaunch)", "n5")
    else:
        v = [x for _i, x in diffs]
        p95, mx = pct(v, 0.95), max(v)
        ok = p95 <= RAYS_P95_M and mx <= RAYS_MAX_M and not shallow
        row(rows, "S8a.N5.reload", "PASS" if ok else "FAIL", "P95 %.3f max %.3f m over %d columns (%s); %d columns at the old seabed" % (p95, mx, len(v), ",".join(whens), len(shallow)),
            thr_r, "N3's rays after every loaded batch and clipmap cell were reloaded (de.s8a reload all), and after the relaunch", "n5")

def n6_rows(L, rows):
    cmp_ = (L.events("install_compare") or [None])[-1]
    writes = L.se_ev("s8a.write")
    refused = [e for e in L.se_ev("s8a.store") if e.get("refused")]
    hs = {r.get("when"): r for r in L.events("host_slot")}
    online = L.events("install_listing") or hs or writes or L.ran("n6")
    if not online:
        return          # an offline session (c1, c3): nothing to list
    thr = "0 files changed, added or removed under either install; octree listing hash unchanged"
    if cmp_ is None:
        row(rows, "S8a.N6.install", "MISSING", "no install_compare event", thr, "nothing written into either install", "n6")
    else:
        c = cmp_.get("counts") or {}
        samples = [e.get("file") for e in (cmp_.get("changed") or []) + (cmp_.get("added") or []) + (cmp_.get("removed") or [])][:3]
        row(rows, "S8a.N6.install", "PASS" if cmp_.get("ok") else "FAIL", "changed %s added %s removed %s (BepInEx log/cache/config: %s info); octree hash %s -> %s%s" % (
            c.get("changed"), c.get("added"), c.get("removed"), c.get("info", 0), cmp_.get("octreeHashBefore"), cmp_.get("octreeHashAfter"), "; " + ",".join(str(s) for s in samples) if samples else ""),
            thr, "read-only listing of both installs before and after (SNAppData excluded; BepInEx's own log, cache and config reported, not judged)", "n6",
            info=[e.get("file") for e in (cmp_.get("info") or [])][:10])
    root = str(L.start.get("storeRoot") or "")
    thr_w = "every s8a.write path under %s" % (root or STORE_ROOT_TAIL)
    if not writes and not refused:
        landed = [i for i, ok in sites_edit_ok(L).items() if ok]
        if landed:          # edits landed and nothing was written: the converter did not run
            row(rows, "S8a.N6.writes", "MISSING", "no s8a.write row after %d landed edits" % len(landed), thr_w, "every store file lies under %%LOCALAPPDATA%%\\DeepEngineers\\terrain", "n6")
    else:
        def under(p):
            p = os.path.normcase(os.path.normpath(str(p)))
            if root:
                return p.startswith(os.path.normcase(os.path.normpath(root)) + os.sep)
            return (os.sep + STORE_ROOT_TAIL.lower() + os.sep) in p
        # an all-water window batch has no file by design: it is not a
 # file write; a Water row that names a path (or bytes) is itself a fault
        files = [w for w in writes if w.get("path")]
        water = [w for w in writes if not w.get("path")]
        bad = [w.get("path") for w in files if not under(w.get("path"))]
        bad_water = [w for w in water if w.get("kind") != "Water" or (_num(w, "bytes") or 0) != 0]
        bad_water += [w for w in files if w.get("kind") == "Water"]
        ok = not bad and not bad_water and not refused
        row(rows, "S8a.N6.writes", "PASS" if ok else "FAIL", "%d file writes, %d outside the store root; %d water batches without a file%s%s%s" % (
            len(files), len(bad), len(water), "; " + str(bad[0]) if bad else "",
            "; %d water/pathless rows malformed (kind %s, path %s, bytes %s)" % (len(bad_water), bad_water[0].get("kind"), bad_water[0].get("path"), bad_water[0].get("bytes")) if bad_water else "",
            "; store refused: " + str(refused[0].get("reason")) if refused else ""),
            thr_w, "every s8a.write row lies under the derived store root (never an install, SNAppData or the SE world)", "n6")
    b, a = hs.get("before"), hs.get("after")
    thr_h = "host slot hash unchanged (S1.N4)"
    if b is None or a is None or b.get("files") is None or a.get("files") is None:
        row(rows, "S8a.N6.hostsave", "MISSING", "host_slot before/after not both hashed", thr_h, "the Subnautica host slot stays unchanged", "n6")
    else:
        same = b["files"] == a["files"]
        diff = sorted(set(k for k in set(b["files"]) | set(a["files"]) if b["files"].get(k) != a["files"].get(k)))
        row(rows, "S8a.N6.hostsave", "PASS" if same else "FAIL", "%d files%s" % (len(a["files"]), "" if same else "; changed " + ",".join(diff[:3])), thr_h,
            "the host save slot hash unchanged (S1.N4)", "n6")

# ----------------------------------------------------------------------------------------------------------------
# Stage B rows: C2 (the sculpt), N7 (the window), N8 (the treadmill), N9 (the throttle), N10 (materials, spawns, biome)
# ----------------------------------------------------------------------------------------------------------------

WINDOW_RAYS_P95_M, WINDOW_RAYS_MAX_M = 0.3, 1.0
COVERAGE_MIN = {20: 0.99, 80: 0.90}
# budget decision: the surface
# budget is per surface octree (p95 <= 8 ms) with a batch p95 cap of 450 ms; water/rock judged at p95 (<= 5 ms), not max
WINDOW_SURFACE_P95_MS, WINDOW_EMPTY_MS = 450.0, 5.0
WINDOW_SURFACE_OCTREE_P95_MS = 10.0   # measured with ReadShell: p50 6.9 / p95 9.3 ms per surface octree
WINDOW_MEM_SE_MB, WINDOW_MEM_SN_MB = 300.0, 150.0
JUMP_REL, JUMP_ABS_M = 1.1e-3, 0.01
C1_JUMP_RESET_M = 20.0      # FrameReader.JumpMetres: a C1 camera jump over this drops Subnautica's pose history (no bracket across it)
BATCH_M = 160.0
GATE_RESUME_S = 1.5
GATE_TICKS = 1              # areaBuilt false for every presented tick >= the NearPending rise + this
JUMP_SNAP_ABS_M = 0.005     # the C1 camera jump across a chart change vs -T (f32 positions at 12 km: ~1 mm ulp)
MATERIAL_MIN_SHARE = 0.99
SPAWN_TOLERANCE = 0.30
SPAWN_MIN_HA = 100.0
SPAWN_SWEEP_M = 300.0
SPAWN_TABLE_PER_HA = {0: 21.0, 1: 20.0, 2: 11.0, 3: 5.0}      # the sum of spawn.table per band (creatures and flora)
SN_TYPE_OF = {"Sand_02": 1, "Stone_01": 111, "Stone_02": 150, "Stone_03": 190, "Stone_04": 141, "Stone_05": 84, "Grass": 219,
              # ext s8a terrain 6 (ores as voxels): this mod's own ore block types in the palette's null slots (format convert.orelook)
              "Iron_02": 95, "Nickel_01": 96, "Silicon_01": 107, "Cobalt_01": 108, "Magnesium_01": 117, "Silver_01": 118, "Gold_01": 119,
              "Platinum_01": 121, "Uraninite_01": 122}
# Stage C step 1 (S8a.C2.ingame): the planet's surface in game = the sculpt tool's --depth-at within 1 m (the voxel mesh: + half
# a voxel's slope error); the Water Mod depth at the surface point = its SN y within 1 m
INGAME_M = 1.0
INGAME_SLOPE_M = 0.5
WATER_M = 1.0
FIELD_POINTS = ((5280.0, 0.0), (4320.0, 0.0))
ORE_MIN_SHARE = 0.9                               # S8a.N10.ore: deposits whose SE vein lies at the row's depths and whose Subnautica type is the row's

def _t_of(d):
    """Seconds of a row's UTC stamp (both games write 't' as ISO 8601 Z)."""
    s = d.get("_t") or d.get("t")
    if not isinstance(s, str):
        return None
    try:
        import datetime
        return datetime.datetime.strptime(s.replace("Z", "")[:23], "%Y-%m-%dT%H:%M:%S.%f").timestamp()
    except ValueError:
        try:
            import datetime
            return datetime.datetime.strptime(s.replace("Z", "")[:19], "%Y-%m-%dT%H:%M:%S").timestamp()
        except ValueError:
            return None

def c2_rows(L, rows):
    if not L.ran("c2"):
        return
    ev = (L.events("c2") or [None])[-1]
    thr_s = "heights within 1 unit of the rules; materials in the table"
    thr_f = "crater pixels 0 within rhoInf 2,450 m; bicubic surface -3,000 within 2,300 m; nothing above -10"
    if ev is None:
        row(rows, "S8a.C2.sculpt", "MISSING", "no c2 event", thr_s, "the sculpt re-derived from the rules", "c2")
        row(rows, "S8a.C2.flat", "MISSING", "no c2 event", thr_f, "the crater stays flat under the tiles", "c2")
        return
    tail = str(ev.get("tail") or "")
    if "MISSING" in tail and "S8a.C2" not in tail:
        row(rows, "S8a.C2.sculpt", "MISSING", "the world mod holds no sculpt yet (owner action 1)", thr_s, "the sculpt re-derived from the rules", "c2")
        row(rows, "S8a.C2.flat", "MISSING", "the world mod holds no sculpt yet (owner action 1)", thr_f, "the crater stays flat under the tiles", "c2")
        return
    for cid, key, thr, what in (("S8a.C2.sculpt", "S8a.C2.sculpt", thr_s, "every face 2048 x 2048 16-bit, heights re-derived within 1 unit, materials in the .sbc table"),
                                ("S8a.C2.flat", "S8a.C2.flat", thr_f, "every pixel within rhoInf 2,450 m is 0, the bicubic surface at 2,000 points within 2,300 m is -3,000 m")):
        line = next((ln for ln in tail.splitlines() if key in ln), None)
        if line is None:
            row(rows, cid, "MISSING", "no %s line in the verify output (rc %s)" % (key, ev.get("rc")), thr, what, "c2")
        else:
            row(rows, cid, "PASS" if line.strip().startswith("OK") else "FAIL", line.strip()[:120], thr, what, "c2")

def c2g_rows(L, rows):
    """S8a.C2.ingame: per c2g column the planet's surface three ways against the tool (Stage C step 1); S8a.C2.field (INFO)."""
    if not L.ran("c2g"):
        return
    ev = (L.events("c2g") or [None])[-1]
    thr = "|closest - tool| <= %g m, |voxels - tool| <= %g m + %g x slope, |Water Mod depth - closest| <= %g m, same files" % (INGAME_M, INGAME_M, INGAME_SLOPE_M, WATER_M)
    what = "SE's planet in game (GetClosestSurfacePointGlobal, the LOD-0 voxel mesh, the Water Mod depth) = sculpt_planet.py --depth-at on the files SE loaded"
    probe = ev.get("probe") if ev else None
    exp = ev.get("expected") if ev else None
    if ev is None or not isinstance(probe, dict) or not probe.get("ok") or not isinstance(exp, list):
        why = "no c2g event" if ev is None else "probe failed: %s" % ((probe or {}).get("error") if isinstance(probe, dict) else "no reply") if not (isinstance(probe, dict) and probe.get("ok")) \
            else "no tool expectation (%s)" % ev.get("filesSculptHash")
        row(rows, "S8a.C2.ingame", "MISSING", why, thr, what, "c2g")
        return
    got = {(round(float(r["x"]), 3), round(float(r["z"]), 3)): r for r in probe.get("rows") or [] if "x" in r and "z" in r}
    worst_c = worst_s = worst_w = 0.0
    bad, n = [], 0
    for e in exp:
        k = (round(float(e["x"]), 3), round(float(e["z"]), 3))
        g = got.get(k)
        if g is None:
            bad.append("(%g, %g) no probe row" % k)
            continue
        n += 1
        y, slope = float(e["y"]), float(e.get("slope") or 0.0)
        cy, sy, wd = _num(g, "closestY"), _num(g, "storageY"), _num(g, "waterDepth")
        if cy is None or math.isnan(cy):
            bad.append("(%g, %g) no closest point" % k)
            continue
        dc = abs(cy - y)
        worst_c = max(worst_c, dc)
        if dc > INGAME_M:
            bad.append("(%g, %g) closest %.2f vs tool %.2f" % (k[0], k[1], cy, y))
        if sy is None or math.isnan(sy):
            bad.append("(%g, %g) no voxel hit" % k)
        else:
            ds = abs(sy - y)
            worst_s = max(worst_s, ds)
            if ds > INGAME_M + INGAME_SLOPE_M * slope:
                bad.append("(%g, %g) voxels %.2f vs tool %.2f (slope %.2f)" % (k[0], k[1], sy, y, slope))
        if wd is not None and not math.isnan(wd):
            dw = abs(wd - cy)
            worst_w = max(worst_w, dw)
            if dw > WATER_M:
                bad.append("(%g, %g) Water Mod depth %.2f vs closest %.2f" % (k[0], k[1], wd, cy))
    same = str(probe.get("sculptHash") or "").lower() == str(ev.get("filesSculptHash") or "").lower()
    if not same:
        bad.append("SE's planet data sculptHash %s != the measured files' %s (%s)" % (probe.get("sculptHash"), ev.get("filesSculptHash"), ev.get("filesWhere")))
    status = "PASS" if not bad and n == len(exp) and n > 0 else "FAIL"
    row(rows, "S8a.C2.ingame", status, "%d points: max |closest - tool| %.2f m, |voxels - tool| %.2f m, |water - closest| %.2f m; R %s, min %s, max %s, Rs %s%s" % (
        n, worst_c, worst_s, worst_w, probe.get("averageRadius"), probe.get("minimumRadius"), probe.get("maximumRadius"), probe.get("rs"),
        ("; " + "; ".join(bad[:4])) if bad else ""), thr, what, "c2g")
    field = []
    for fx, fz in FIELD_POINTS:
        g = got.get((round(fx, 3), round(fz, 3)))
        e = next((x for x in exp if abs(float(x["x"]) - fx) < 1e-3 and abs(float(x["z"]) - fz) < 1e-3), None)
        if g is not None:
            field.append("(%g, %g): voxels %s, closest %s, Water Mod %s, tool %s" % (fx, fz, _fmt(_num(g, "storageY")), _fmt(_num(g, "closestY")), _fmt(_num(g, "waterDepth")),
                                                                                    _fmt(float(e["y"]) if e else None)))
    row(rows, "S8a.C2.field", "INFO", "; ".join(field) or "the field points were not probed", "information",
        "the owner's two reported places (5.28 km: shelf at the water line, 4.32 km: rock island) as SE measures them", "c2g", required=False)

def _fmt(v):
    return "n/a" if v is None or (isinstance(v, float) and math.isnan(v)) else "%.2f" % v

def _path_names(L, prefix):
    return [p.get("name") for p in L.events("path") if str(p.get("name", "")).startswith(prefix)]

def n7_rows(L, rows):
    if not L.ran("n7"):
        return
    # rays: SE CastRay vs Subnautica layer-30 along the paths
    diffs = []
    miss = []
    for name in _path_names(L, "n7."):
        se_r, sn_r = L.rays("se", "path", name), L.rays("sn", "path", name)
        if not se_r or not sn_r:
            continue
        semap = se_results_map(se_r.get("results"), "surface")
        snmap = sn_results_map(sn_r.get("results"))
        cols = [k for k in semap if k in snmap]
        if not cols:
            miss.append(name)
            continue
        diffs += [abs(snmap[k] - semap[k]) for k in cols]
    thr = "P95 <= %.1f m, max <= %.1f m" % (WINDOW_RAYS_P95_M, WINDOW_RAYS_MAX_M)
    if not diffs:
        row(rows, "S8a.N7.rays", "MISSING", "no path with SE and SN rays" + ("; " + ",".join(miss) if miss else ""), thr, "Subnautica layer-30 vs SE's planet surface (CastRay where SE has physics, else its LOD-0 mesh) at 200 columns along the paths", "n7")
    else:
        p95, mx = pct(diffs, 0.95), max(diffs)
        row(rows, "S8a.N7.rays", "PASS" if p95 <= WINDOW_RAYS_P95_M and mx <= WINDOW_RAYS_MAX_M else "FAIL", "P95 %.3f m, max %.3f m over %d columns" % (p95, mx, len(diffs)), thr,
            "Subnautica layer-30 vs SE's planet surface (CastRay where SE has physics, else its LOD-0 mesh) at 200 columns along the paths (S4's budget, conversion inverted)", "n7")
    # coverage: per window id the s8a.near100 row SE logs when the id's box first comes within 100 m of the
 # camera: applied = its notice seq at or below the C7 appliedSeq SE read then (the batch was in Subnautica before the camera came near)
    near = L.se_ev("s8a.near100")
    parts = []
    ok = True
    any_cov = False
    for speed in (20, 80):
        samples = []
        for name in _path_names(L, "n7."):
            if not name.endswith(".%d" % speed) and (".%d." % speed) not in name:
                continue
            w = L.window(name)
            if not w:
                continue
            samples += [bool(d.get("applied")) for d in near if _num(d, "tick") is not None and w[1] <= d["tick"] <= w[2]]
        if not samples:
            continue
        any_cov = True
        share = sum(1 for x in samples if x) / float(len(samples))
        ok &= share >= COVERAGE_MIN[speed]
        parts.append("%d m/s %.1f %% of %d ids (>= %d %%)" % (speed, 100 * share, len(samples), int(100 * COVERAGE_MIN[speed])))
    thr_c = ">= 99 % at 20 m/s, >= 90 % at 80 m/s"
    if not any_cov:
        row(rows, "S8a.N7.coverage", "MISSING", "no s8a.near100 rows inside the path windows", thr_c, "ring batches applied before the camera came within 100 m of their box", "n7")
    else:
        row(rows, "S8a.N7.coverage", "PASS" if ok else "FAIL", "; ".join(parts), thr_c,
            "share of window ids whose notice was applied (C7 appliedSeq) when their box first came within 100 m of the camera", "n7")
    # time: the worker's per-batch time
    w7 = L.window("n7")
    writes = [d for d in L.se_ev("s8a.write") if d.get("layer") == "window" and w7 and _num(d, "tick") is not None and w7[1] <= d["tick"] <= w7[2]]
    surf = [_num(d, "ms") for d in writes if d.get("kind") == "Surface" and _num(d, "ms") is not None]
    empty = [_num(d, "ms") for d in writes if d.get("kind") in ("Water", "Rock") and _num(d, "ms") is not None]
    thr_t = "per surface octree p95 <= %.0f ms and surface batch p95 <= %.0f ms, water/rock p95 <= %.0f ms" % (WINDOW_SURFACE_OCTREE_P95_MS, WINDOW_SURFACE_P95_MS, WINDOW_EMPTY_MS)
    if not surf and not empty:
        row(rows, "S8a.N7.time", "MISSING", "no window s8a.write rows in n7", thr_t, "the window worker's time per batch", "n7")
    else:
        p95 = pct(surf, 0.95) if surf else 0.0
        me = max(empty) if empty else 0.0
        # the evidence for the budget (Stage C): a surface batch's time is about linear in its surface octrees (SE's LOD-0 reads)
        per = [_num(d, "ms") / _num(d, "octrees") for d in writes if d.get("kind") == "Surface" and (_num(d, "octrees") or 0) > 0 and _num(d, "ms") is not None]
        octs = [_num(d, "octrees") for d in writes if d.get("kind") == "Surface" and _num(d, "octrees") is not None]
        extra = "; per surface octree p50 %.1f / p95 %.1f ms, surface octrees per batch p50 %d / p95 %d; water/rock p95 %.1f ms" % (
            pct(per, 0.5), pct(per, 0.95), int(pct(octs, 0.5)), int(pct(octs, 0.95)), pct(empty, 0.95) if empty else 0.0) if per and octs else ""
        per_p95 = pct(per, 0.95) if per else 0.0
        empty_p95 = pct(empty, 0.95) if empty else 0.0
        ok_t = per_p95 <= WINDOW_SURFACE_OCTREE_P95_MS and p95 <= WINDOW_SURFACE_P95_MS and empty_p95 <= WINDOW_EMPTY_MS
        row(rows, "S8a.N7.time", "PASS" if ok_t else "FAIL", "surface p95 %.0f ms (%d), water/rock max %.1f ms (%d)%s" % (
            p95, len(surf), me, len(empty), extra), thr_t, "per surface octree p95, per surface batch p95 and per empty/solid batch p95 on the one window worker", "n7")
    # memory: SE from its plugin's 1 s sample rows (else the launcher's mem events), Subnautica from the
 # launcher's mem events (both game processes' working sets at 1 Hz, tagged with the part)
    se_mem = [(_num(d, "tick"), _num(d, "workingSetMB")) for d in L.se_raw_samples() if _num(d, "workingSetMB") is not None]
    mems = [d for d in L.events("mem") if str(d.get("part") or "").startswith("n7")]
    se_in = [m for t, m in se_mem if t is not None and w7 and w7[1] <= t <= w7[2]] if w7 else []
    if len(se_in) < 2:
        se_in = [_num(d, "seWorkingSetMB") for d in mems if _num(d, "seWorkingSetMB") is not None]
    sn_in = [_num(d, "snWorkingSetMB") for d in mems if _num(d, "snWorkingSetMB") is not None]
    thr_m = "SE <= %.0f MB, Subnautica <= %.0f MB" % (WINDOW_MEM_SE_MB, WINDOW_MEM_SN_MB)
    if len(se_in) < 2 and len(sn_in) < 2:
        row(rows, "S8a.N7.memory", "MISSING", "no memory samples inside n7 (SE sample rows, launcher mem events)", thr_m, "working-set growth over the paths", "n7")
    else:
        parts, miss, ok = [], [], True
        for label, vals, limit in (("SE", se_in, WINDOW_MEM_SE_MB), ("Subnautica", sn_in, WINDOW_MEM_SN_MB)):
            if len(vals) < 2:
                miss.append(label)
                continue
            growth = max(vals) - vals[0]
            ok &= growth <= limit
            parts.append("%s +%.0f MB over %d samples (<= %.0f)" % (label, growth, len(vals), limit))
        row(rows, "S8a.N7.memory", "FAIL" if not ok else "MISSING" if miss else "PASS", "; ".join(parts) + ("; no samples: " + ",".join(miss) if miss else ""), thr_m,
            "working-set growth of both processes over the paths (SE's sample rows; the launcher's per-process mem events)", "n7")
    # edit: the 6 m drill into the generated seabed regenerated the batch (version + 1) and Subnautica's rays see the hole
    d = L.window_drill()
    thr_e = "window batch regenerated (version + 1); the hole at N2's tolerances"
    if d is None:
        row(rows, "S8a.N7.edit", "MISSING", "no window drill", thr_e, "a 6 m drill into the generated seabed", "n7")
        return
    regen = [w for w in L.se_ev("s8a.write") if w.get("layer") == "window" and w.get("regen") and _num(w, "tick") is not None and _num(d, "tick") is not None and w["tick"] >= d["tick"]]
    before = before_map(L, 100, "wfootprint") or {k: v for k, v in se_results_map((L.rays("se", "wfootprint", "before", 100) or {}).get("results"), "surface").items()}
    after = L.rays("sn", "wfootprint", "after", 100)
    amap = sn_results_map(after.get("results") if after else None)
    allc = [(x, z, before[(x, z)], y) for (x, z), y in amap.items() if (x, z) in before]
    cols = [c for c in allc if c[2] - c[3] >= CARVE_MIN_DEPTH_M]
    if not regen:
        row(rows, "S8a.N7.edit", "FAIL" if d.get("reply", {}).get("ok") else "MISSING", "no regenerated window batch after the drill (%d hole columns seen)" % len(cols), thr_e, "the drilled spot regenerated (version + 1) and visible", "n7")
        return
    # the launcher waits until Subnautica applied the regenerated version (C7 appliedSeq caught up) before the rays (session
 # 20261023-150323: a teleport's burst of ring notices delayed it past the old 10 s wait, the rays saw the old collision)
    applied = [e for e in L.events("applied") if e.get("i") == 100 and e.get("phase", "after") == "after" and _num(e, "qpc") is not None and _num(d, "qpc") is not None and e["qpc"] >= d["qpc"]]
    if applied and not applied[-1].get("ok"):
        f = applied[-1].get("feed") or {}
        row(rows, "S8a.N7.edit", "MISSING", "Subnautica had not applied the regenerated version before the rays (waited %.0f s; appliedSeq %s of %s, %s pending); %d hole columns of %d" % (
            applied[-1].get("waited") or 0.0, f.get("appliedSeq"), (f.get("nextSeq") or 1) - 1, f.get("pending"), len(cols), len(allc)), thr_e, "the drilled spot regenerated (version + 1) and visible", "n7")
        return
    if len(cols) < 9:
        row(rows, "S8a.N7.edit", "FAIL", "batch regenerated (%d writes) and applied, but %d hole columns of %d (the hole is not in Subnautica's terrain)" % (len(regen), len(cols), len(allc)), thr_e, "the drilled spot regenerated (version + 1) and visible", "n7")
        return
    sn = d["sn"]
    r = float(d.get("radius") or 6.0)
    s = float(d.get("s") or 1.0)
    p, res = fit_carve(cols, (sn[0], sn[1], sn[2], r), s)
    pe = math.sqrt((p[0] - sn[0]) ** 2 + (p[1] - sn[1]) ** 2 + (p[2] - sn[2]) ** 2)
    ok = pe <= POSITION_M and abs(p[4] - r) <= SIZE_M and pct(res, 0.95) <= WINDOW_EDIT_P95_M
    row(rows, "S8a.N7.edit", "PASS" if ok else "FAIL", "%d regenerated writes; centre error %.2f m, r %.2f (want %.1f), surface P95 %.3f (<= %.2f)" % (
        len(regen), pe, p[4], r, pct(res, 0.95), WINDOW_EDIT_P95_M), thr_e,
        "the drilled spot regenerated (version + 1) and Subnautica's rays see the 6 m hole (N2 tolerances)", "n7")

def n8_rows(L, rows):
    if not L.ran("n8"):
        return
    steps = L.events("chart_step")
    charts = L.se_ev("s8a.chart")
    # jump: the plate's offset from the camera on Subnautica's side (de.s8a pose: the mirror's drawn frame and
 # Subnautica's camera) before and after each forced step, the motion paused across it
    jumps, miss = [], []
    keys = ("camX", "camY", "camZ", "entityX", "entityY", "entityZ")
    for s in steps:
        sb, sa = s.get("snBefore") or {}, s.get("snAfter") or {}
        have = all(_num(x, k) is not None for x in (sb, sa) for k in keys) and sb.get("entityFound") and sa.get("entityFound")
        if not have:
            miss.append("%s (no Subnautica-side pose%s)" % (s.get("kind"), ": plate not mirrored" if (sb or sa) and not (sb.get("entityFound") and sa.get("entityFound")) else ""))
            continue
        rb = [sb["entityX"] - sb["camX"], sb["entityY"] - sb["camY"], sb["entityZ"] - sb["camZ"]]
        ra = [sa["entityX"] - sa["camX"], sa["entityY"] - sa["camY"], sa["entityZ"] - sa["camZ"]]
        dist = math.sqrt(sum(v * v for v in rb))
        delta = math.sqrt(sum((ra[i] - rb[i]) ** 2 for i in range(3)))
        jumps.append((s.get("kind"), delta, JUMP_REL * dist + JUMP_ABS_M, dist))
    thr_j = "|delta(p - cam)| <= 1.1e-3 * |p - cam| + 0.01 m (Subnautica's side, the motion paused)"
    if not jumps:
        row(rows, "S8a.N8.jump", "MISSING", "no forced step with Subnautica-side poses of the plate and the camera" + ("; " + ",".join(miss) if miss else ""), thr_j,
            "every mirrored entity's camera-relative position across a step", "n8")
    else:
        bad = [j for j in jumps if j[1] > j[2]]
        row(rows, "S8a.N8.jump", "FAIL" if bad else "PASS", "; ".join("%s: %.4f m (bound %.4f at %.0f m)" % (k, dl, bd, ds) for k, dl, bd, ds in jumps) + ("; " + ",".join(miss) if miss else ""), thr_j,
            "the test plate's drawn position relative to Subnautica's camera across each forced step (the outgoing chart's distortion bound)", "n8")
    # batchstep: every step row (returns excluded: their T is 0 while the camera jumps back by the accumulated
 # offset) moves by whole batches, and the C1 camera frames across the change (Subnautica's s8a.chartsnap row: the bracket's
 # older and newer poses, the motion per tick before it) jump by -T
    snaps = L.sn_ev("s8a.chartsnap")
    thr_b = ("T = n * 160 m; |camNewer - camOlder - v*dt + T| <= 1.1e-3 * |T| + 5 mm (the C1 frames across the change; a step over %.0f m resets "
             "Subnautica's C1 history: SE's camera in both charts, after - before = -T)" % C1_JUMP_RESET_M)
    judged = [c for c in charts if not c.get("toSite")]
    if not judged:
        row(rows, "S8a.N8.batchstep", "MISSING", "no s8a.chart step rows (%d returns not judged)" % len(charts), thr_b, "every chart step moves by whole batches", "n8")
    else:
        bad = []
        nosnap = 0
        se_judged = 0
        for c in judged:
            T = c.get("T") or [0, 0, 0]
            whole = all(abs(T[i] / BATCH_M - round(T[i] / BATCH_M)) < 1e-9 for i in (0, 2))
            seq = _num(c, "chartSeq")
            snap = next((d for d in snaps if _num(d, "chartSeq") == seq), None)
            if snap is None:
                # night queue (session 20261023-194702): a step of |T| > C1_JUMP_RESET_M resets Subnautica's C1 history
 # (FrameReader.JumpMetres: no bracket across it, so no snap and no chartsnap row); the step is then judged by SE's
 # own camera in the two charts at the step's tick (s8a.chart camSnBefore / camSnAfter: after - before = -T)
                nosnap += 1
                cb, ca = c.get("camSnBefore"), c.get("camSnAfter")
                if not whole:
                    bad.append("seq %s T %s not whole batches" % (seq, T))
                elif isinstance(cb, list) and isinstance(ca, list) and len(cb) == 3 and len(ca) == 3:
                    jump = max(abs(ca[i] - cb[i] + T[i]) for i in (0, 2))
                    if jump > JUMP_REL * max(abs(T[0]), abs(T[2])) + JUMP_SNAP_ABS_M:
                        bad.append("seq %s T %s SE camera jump off by %.4f" % (seq, T, jump))
                    else:
                        se_judged += 1
                continue
            dt = (_num(snap, "newer") or 0) - (_num(snap, "older") or 0)
            dp = (_num(snap, "older") or 0) - (_num(snap, "prevTick") or 0)
            jump = 0.0
            for i, ax in ((0, "X"), (2, "Z")):
                v = ((snap["camOlder" + ax] - snap["camPrev" + ax]) / dp) if dp > 0 else 0.0
                jump = max(jump, abs(snap["camNewer" + ax] - snap["camOlder" + ax] - v * dt + T[i]))
            bound = JUMP_REL * max(abs(T[0]), abs(T[2])) + JUMP_SNAP_ABS_M
            if not whole or jump > bound:
                bad.append("seq %s T %s jump %.4f (bound %.4f)" % (seq, T, jump, bound))
        if nosnap == len(judged) and se_judged == 0:
            row(rows, "S8a.N8.batchstep", "MISSING" if not bad else "FAIL", "%d steps, no s8a.chartsnap row for any%s" % (len(judged), "; " + "; ".join(bad[:3]) if bad else ""), thr_b,
                "every ChartStep T is a whole number of batches and the C1 camera jump equals -T", "n8")
        else:
            row(rows, "S8a.N8.batchstep", "FAIL" if bad else "PASS", "%d steps (%d returns not judged), %d bad%s%s" % (
                len(judged), len(charts) - len(judged), len(bad), "; " + "; ".join(bad[:3]) if bad else "",
                "; %d without a chartsnap row (C1 history reset, > %.0f m), %d of them judged by SE's camera in both charts" % (nosnap, C1_JUMP_RESET_M, se_judged) if nosnap else ""), thr_b,
                "every ChartStep T is a whole number of batches and Subnautica's C1 camera frames jump by -T across the change (the motion per tick allowed)", "n8")
    # snap: Subnautica snapped its pose brackets across every chart change
    st = L.sn_ev("s8a.status")
    w8 = L.window("n8")
    if w8:      # the counters are cumulative since the plugin started: judge the n8 part's own increments when its rows exist
        st = [d for d in st if L.tick_of_status(d) is not None and w8[1] <= L.tick_of_status(d) <= w8[2]] or st
    thr_s = "snapFrames >= the chart changes Subnautica brackets (net camera jump <= %.0f m; a larger jump resets its C1 history), changes >= 1" % C1_JUMP_RESET_M
    if not st:
        row(rows, "S8a.N8.snap", "MISSING", "no s8a.status rows", thr_s, "no interpolated pose across a chartSeq change", "n8")
    else:
        first, last = st[0], st[-1]
        changes = (_num(last, "chartChanges") or 0) - (_num(first, "chartChanges") or 0)
        snaps = (_num(last, "snapFrames") or 0) - (_num(first, "snapFrames") or 0)
        if changes <= 0:
            changes, snaps = _num(last, "chartChanges") or 0, _num(last, "snapFrames") or 0
        # night queue (session 20261023-194702: 9 changes, 1 snapped frame): Subnautica's C1 history resets on a camera
 # jump over C1_JUMP_RESET_M (FrameReader.JumpMetres), so a real step is never bracketed and needs no snap; only the
 # changes whose net camera jump stays within it (a step and its return in one tick, a return to the same chart) do. The
 # SE rows of one tick are one change for Subnautica (the header shows the last chartSeq of the tick)
        groups = {}
        for c in charts:
            if _num(c, "tick") is not None and (not w8 or w8[1] <= c["tick"] <= w8[2]):
                groups.setdefault(int(c["tick"]), []).append(c)
        need, unknown = 0, 0
        for tick, cs in groups.items():
            cs.sort(key=lambda d: _num(d, "chartSeq") or 0)
            cb, ca = cs[0].get("camSnBefore"), cs[-1].get("camSnAfter")
            if isinstance(cb, list) and isinstance(ca, list) and len(cb) == 3 and len(ca) == 3:
                if math.sqrt(sum((ca[i] - cb[i]) ** 2 for i in range(3))) <= C1_JUMP_RESET_M:
                    need += 1
            else:
                unknown += 1
        if changes == 0:
            row(rows, "S8a.N8.snap", "MISSING", "Subnautica saw no chart change", thr_s, "no interpolated pose across a chartSeq change", "n8")
        elif groups:
            row(rows, "S8a.N8.snap", "PASS" if snaps >= need and unknown == 0 else ("MISSING" if unknown else "FAIL"),
                "%d chart changes (%d SE ticks): %d within %.0f m of camera jump need a snap, %d snapped frames; %d jumped further (C1 history reset, no bracket across)%s" % (
                    changes, len(groups), need, C1_JUMP_RESET_M, snaps, len(groups) - need - unknown, "; %d without SE camera rows" % unknown if unknown else ""), thr_s,
                "Subnautica's pose brackets snapped to the newer tick across every chartSeq change it brackets (S8a.M7 interpolates)", "n8")
        else:
            row(rows, "S8a.N8.snap", "PASS" if snaps >= changes else "FAIL", "%d chart changes, %d snapped frames" % (changes, snaps), thr_s,
                "Subnautica's pose brackets snapped to the newer tick across every chartSeq change (S8a.M7 interpolates)", "n8")
    # picture (terrain 4 "chart.
 # 1 km vertically"): across every forced step under 1 km altitude SE's picture latch and its edge counter (de.s7 status) unchanged
    thr_p = "no picture edge across a step under 1 km altitude (de.s7 latch and pictureEdges before and 2 s after)"
    judged_p, bad_p = [], []
    for s in steps:
        pb, pa = s.get("pictureBefore") or {}, s.get("pictureAfter") or {}
        if _num(pb, "pictureEdges") is None or _num(pa, "pictureEdges") is None or (_num(s, "altitude") or 0.0) >= 1000.0:
            continue
        judged_p.append(s)
        edges = _num(pa, "pictureEdges") - _num(pb, "pictureEdges")
        if edges != 0 or pb.get("latch") != pa.get("latch"):
            bad_p.append("%s at %s m: latch %s -> %s, %d edge(s), policy %s -> %s" % (s.get("kind"), s.get("altitude"), pb.get("latch"), pa.get("latch"), edges, pb.get("picture"), pa.get("picture")))
    if not judged_p:
        row(rows, "S8a.N8.picture", "MISSING", "%d forced steps, none with SE's picture state before and after" % len(steps), thr_p, "the picture across a step", "n8")
    else:
        row(rows, "S8a.N8.picture", "FAIL" if bad_p else "PASS", "%d steps judged (%s)%s" % (len(judged_p), ", ".join("%s %s m" % (s.get("kind"), s.get("altitude")) for s in judged_p),
            "; " + "; ".join(bad_p) if bad_p else ""), thr_p, "SE keeps Subnautica's picture across a treadmill step below the altitude band", "n8")

def n9_rows(L, rows):
    if not L.ran("n9"):
        return
    w = L.window("n9.path.80") or L.window("n9")
    se_w = [d for d in L.se_ev("s8a.window") if w and _num(d, "tick") is not None and w[1] <= d["tick"] <= w[2]]
    st = [d for d in L.sn_ev("s8a.status") if w and L.tick_of_status(d) is not None and w[1] <= L.tick_of_status(d) <= w[2]]
    gate = [d for d in L.se_ev("s8a.gate") if w and _num(d, "tick") is not None and w[1] <= d["tick"] <= w[2] + 600]   # + 10 s: the tail after the path
    counted = [d for d in se_w if (_num(d, "nearPendingCount") or 0) > 0 and (_num(d, "seabedBelowCamM") if _num(d, "seabedBelowCamM") is not None else 1e9) <= 150.0]
    seen = [d for d in st if (_num(d, "nearPendingSeenCount") or 0) > 0]
    thr_g = "Subnautica's C7 areaBuilt false at every presented tick >= the NearPending rise + %d (SE's s8a.gate rows, one per tick while pending)" % GATE_TICKS
    thr_r = "<= %.1f s" % GATE_RESUME_S
    pend = [d for d in gate if d.get("nearPending")]
    if not se_w and not gate:
        row(rows, "S8a.N9.gate", "MISSING", "no window/gate rows inside n9", thr_g, "the gate while the generator lags", "n9")
        row(rows, "S8a.N9.resume", "MISSING", "no rows", thr_r, "the picture back after the last near-box notice", "n9")
        return
    if not pend:
        if counted or seen:
            # near-box batches were pending near SE's surface (SE counted them, or Subnautica saw their notices unapplied) yet the flag never rose: S8a.M9
            row(rows, "S8a.N9.gate", "FAIL", "NearPending never raised although %d SE samples counted pending near-box ids and %d Subnautica samples saw unapplied near notices" % (len(counted), len(seen)),
                thr_g, "Subnautica reports not built while SE's surface is near and a near-box window batch is not applied", "n9")
        else:
            row(rows, "S8a.N9.gate", "MISSING", "SE never raised NearPending on the throttled path (the generator kept up)", thr_g, "the gate while the generator lags", "n9")
        row(rows, "S8a.N9.resume", "MISSING", "no NearPending episode", thr_r, "the picture back after the last near-box notice", "n9")
        return
    # the episodes: a rise (first pending tick) to the first tick not pending
    episodes = []
    rise = None
    for d in sorted(gate, key=lambda x: x["tick"]):
        if d.get("nearPending"):
            if rise is None:
                rise = d["tick"]
        elif rise is not None:
            episodes.append((rise, d["tick"]))
            rise = None
    if rise is not None:
        episodes.append((rise, None))
    judged = bad = 0
    for d in pend:
        rise_t = max(r for r, e in episodes if r <= d["tick"])
        pt = _num(d, "snPresentedSeTick")
        if pt is None or pt < rise_t + GATE_TICKS:
            continue
        judged += 1
        if d.get("snAreaBuilt"):
            bad += 1
    if judged == 0:
        row(rows, "S8a.N9.gate", "MISSING", "%d pending ticks in %d episodes, none with a presented tick after its rise" % (len(pend), len(episodes)), thr_g, "the gate while the generator lags", "n9")
    else:
        row(rows, "S8a.N9.gate", "PASS" if bad == 0 else "FAIL", "%d episodes, %d pending ticks, %d judged, %d with areaBuilt true" % (len(episodes), len(pend), judged, bad), thr_g,
            "Subnautica reports not built for every presented tick after SE raised NearPending (C7 areaBuilt as SE read it per tick)", "n9")
    # resume: after each episode's end the first gate row (the 2 s tail) with areaBuilt true at a presented tick past the end
    backs = []
    missing = 0
    for r, e in episodes:
        if e is None:
            continue
        hit = next((d for d in sorted(gate, key=lambda x: x["tick"]) if d["tick"] >= e and not d.get("nearPending") and d.get("snAreaBuilt") and (_num(d, "snPresentedSeTick") or 0) > e), None)
        if hit is None:
            missing += 1
        else:
            backs.append((_num(hit, "snPresentedSeTick") - e) / 60.0)
    if not backs and missing:
        row(rows, "S8a.N9.resume", "FAIL", "areaBuilt not back within the gate tail after %d episode(s)" % missing, thr_r, "the picture back after the last near-box notice is applied", "n9")
    elif not backs:
        row(rows, "S8a.N9.resume", "MISSING", "no closed NearPending episode", thr_r, "the picture back after the last near-box notice is applied", "n9")
    else:
        worst = max(backs)
        row(rows, "S8a.N9.resume", "PASS" if worst <= GATE_RESUME_S and missing == 0 else "FAIL", "areaBuilt back %.2f s after the episode end (worst of %d%s)" % (worst, len(backs), "; %d never" % missing if missing else ""),
            thr_r, "Subnautica's picture back after the last near-box notice is applied", "n9")

def n10_rows(L, rows):
    if not L.ran("n10"):
        return
    mp = (L.events("material_probe") or [None])[-1]
    thr_m = ">= 99 % of columns: SN top solid type = table(SE material)"
    if mp is None or not mp.get("columns"):
        row(rows, "S8a.N10.material", "MISSING", "no material probes", thr_m, "Subnautica's top solid voxel type = the table entry of SE's surface material", "n10")
    else:
        n = ok = 0
        nofile = nomat = nosurf = inhole = 0
        bad = []
        wd = L.window_drill() if L.ran("n7") else None
        for c in mp["columns"]:
            if c.get("file") is False:
                nofile += 1         # a window id the ring never generated (or all water): no Subnautica voxel to compare
                continue
            if wd is not None and wd.get("sn") and math.hypot(c["x"] - wd["sn"][0], c["z"] - wd["sn"][2]) <= float(wd.get("radius") or 6.0) + 1.0:
                inhole += 1         # n7's drilled hole: judged by S8a.N7.edit (a binary cut leaves no surface voxel there)
                continue
            if c.get("solid") is False or (c.get("probeDepth") is not None and (c.get("snDensity") or 0) == 0):
                nosurf += 1         # no converted surface voxel within the probe depths (a far-solid voxel's type is the octree's stand-in)
                continue
            want = SN_TYPE_OF.get(str(c.get("seMaterial")), 84)
            got = c.get("snType")
            if got is None or c.get("seMaterial") is None:
                nomat += 1
                continue
            n += 1
            if got == want:
                ok += 1
            elif len(bad) < 3:
                bad.append("(%.0f, %.0f) SE %s -> SN %s (want %s)" % (c.get("x"), c.get("z"), c.get("seMaterial"), got, want))
        share = ok / float(n) if n else 0.0
        sk = mp.get("skipped") or {}
        info = "; not judged: %d crater-box, %d without SE surface, %d outside the generated layers, %d without a served file, %d without a material, %d in n7's hole, %d without a surface voxel" % (
            sk.get("crater", 0), sk.get("noSurface", 0), sk.get("notGenerated", 0), nofile, nomat, inhole, nosurf)
        row(rows, "S8a.N10.material", "MISSING" if n == 0 else "PASS" if share >= MATERIAL_MIN_SHARE else "FAIL", "%d/%d columns agree (%.1f %%)%s%s" % (
            ok, n, 100 * share, info, ("; " + "; ".join(bad)) if bad else ""), thr_m,
            "at the window columns Subnautica's top solid voxel type = the table entry of SE's surface material there", "n10")
    # spawns: the deterministic draw per scanned batch column (Subnautica's s8a.scan rows: 1 m^2 columns per
 # depth band, points drawn per band), independent of the live caps and the spawn radius
    scans = [d for d in L.sn_ev("s8a.scan") if not d.get("failed")]
    ha = {b: 0.0 for b in range(4)}
    pts = {b: 0 for b in range(4)}
    for d in scans:
        for b in range(4):
            ha[b] += (_num(d, "c%d" % b) or 0) / 10000.0
            pts[b] += _num(d, "p%d" % b) or 0
    thr_s = "per band within +-30 %% of the table over >= %.0f ha scanned" % SPAWN_MIN_HA
    judged = []
    bad = []
    for b in range(4):
        if ha[b] < SPAWN_MIN_HA:
            continue
        dens = pts[b] / ha[b]
        want = SPAWN_TABLE_PER_HA[b]
        judged.append("band %d: %.1f/ha over %.0f ha (table %.0f)" % (b, dens, ha[b], want))
        if abs(dens - want) > SPAWN_TOLERANCE * want:
            bad.append(b)
    if not scans:
        row(rows, "S8a.N10.spawn", "MISSING", "no s8a.scan rows (spawns off, or no window batch under the camera)", thr_s, "the spawn draw per depth band per hectare", "n10")
    elif not judged:
        row(rows, "S8a.N10.spawn", "MISSING", "no band scanned >= %.0f ha (%s)" % (SPAWN_MIN_HA, ", ".join("%d: %.0f ha" % (b, ha[b]) for b in range(4))), thr_s, "the spawn draw per depth band per hectare", "n10")
    else:
        row(rows, "S8a.N10.spawn", "FAIL" if bad else "PASS", "; ".join(judged) + " (%d batch columns)" % len(scans), thr_s,
            "points drawn per depth band per hectare of scanned seabed within 30 % of the table (S8a.M11 x 10)", "n10")
    # S8a.N10.ore (Stage C): the planted deposits. SE half: the storage holds the row's ore at the vein's depths and not above it
 # (the launcher scales the vertical depths by the slope: SE measures the perpendicular depth). Subnautica half: in a drilled
 # cavity the converted wall voxels (density != 0) carry the ore row's type. An undrilled interior voxel is information only:
 # Subnautica's own octree collapse ("octree.write") keeps far-solid types as whole blocks of their first child's type.
    op = (L.events("ore_probe") or [None])[-1]
    thr_o = (">= %d %% of the probed deposits: SE vein inside, none above (veins starting under 1 m: not judged above); every drilled cavity's wall voxels "
             "(the first solid voxel outward, near or far): Subnautica type = table(SE's material there), or table(ore) in 3 of 4 directions without it" % int(100 * ORE_MIN_SHARE))
    what_o = "SE's storage holds the deposits at the table's depths; the drilled vein converts to the ore row's Subnautica type"
    if op is None or not op.get("rows"):
        row(rows, "S8a.N10.ore", "MISSING", "no ore probes (no deposit near the n10 path: the designed planet not deployed?)", thr_o, what_o, "n10")
    else:
        se_good = se_judged = 0
        walls_good = walls = 0
        notes, interior = [], []
        any_vein = False
        for r in op["rows"]:
            mats, depths = r.get("seMaterials"), r.get("depths")
            roles = r.get("roles") or (["above"] + ["inside"] * (len(depths or []) - 1))
            if not isinstance(mats, list) or not isinstance(depths, list) or len(mats) != len(depths):
                continue
            se_judged += 1
            inside = [m for m, ro in zip(mats, roles) if ro == "inside"]
            above = [m for m, ro in zip(mats, roles) if ro == "above"]
            # night queue (session 20261023-195536): a row whose vein starts at the seabed (Start 0) has no rock above
 # it; the launcher's "above" sample (0.5 m) lies on the vein's top edge, so it is not judged
            if (r.get("startM") or 0.0) < 1.0:
                above = []
            vein = bool(inside) and all(m == r["type"] for m in inside) and not any(m == r["type"] for m in above)
            any_vein |= any(m == r["type"] for m in mats)
            if vein:
                se_good += 1
            elif len(notes) < 3:
                notes.append("%s at (%.0f, %.0f) slope %s: SE %s at %s m" % (r["ore"], r["x"], r["z"], r.get("slope"), ",".join(str(m) for m in mats), depths))
            si = r.get("snInterior") or r.get("sn") or {}
            if si.get("type") is not None:
                interior.append(si.get("type") == SN_TYPE_OF.get(r["type"], 84))
            wall = r.get("wall")
            if isinstance(wall, list):
                # per direction the first solid voxel outward from the cavity's centre (the launcher marches 3.0.. 6.0 m): the
 # converted wall's surface voxel, near (density != 0) and of the ore row's type; a far one there = no cavity
                walls += 1
                found = [w for w in wall if w.get("type") is not None and (w.get("type") or 0) > 0]
                # night queue (session 20261023-195536): SE's cut is binary, so where Subnautica's voxel centres meet SE's the
 # first solid voxel behind the cavity is far-solid (density 0) with no near voxel before it: it is the wall's solid
 # side all the same (Subnautica meshes the sign change there with its type). The type wanted is SE's own material at
 # that point when the launcher read it (seType), else the ore row's; one direction may leave a thin or sloped vein
 # (3 of 4 then), every direction must when SE's own material is known
                ore_t = SN_TYPE_OF.get(r["type"], 84)
                good_w = [w for w in found if w.get("type") == (SN_TYPE_OF.get(w.get("seType"), -1) if w.get("seType") else ore_t)]
                truth = bool(found) and all(w.get("seType") for w in found)
                if len(found) >= 3 and (len(good_w) == len(found) if truth else len(good_w) >= 3):
                    walls_good += 1
                elif len(notes) < 4:
                    notes.append("%s cavity at (%.0f, %.0f): wall %s (want %s)" % (
                        r["ore"], r["x"], r["z"], ",".join("%s/%s%s" % (w.get("type"), w.get("density"), "/SE " + str(w.get("seType")) if w.get("seType") else "") for w in wall),
                        "SE's material at each" if truth else "type %s in 3 of 4" % ore_t))
        info = "; undrilled interior voxel = the ore type at %d/%d (information)" % (sum(interior), len(interior)) if interior else ""
        ores = ",".join(sorted({r["ore"] for r in op["rows"]}))
        if se_judged == 0:
            row(rows, "S8a.N10.ore", "MISSING", "no ore probe answered", thr_o, what_o, "n10")
        elif not any_vein:
            row(rows, "S8a.N10.ore", "MISSING", "%d deposits probed, no ore in SE's storage at any of them: the deployed planet is not the design (owner action)" % se_judged,
                thr_o, what_o, "n10")
        elif walls == 0:
            row(rows, "S8a.N10.ore", "MISSING", "SE veins %d/%d; no deposit drilled (the Subnautica half needs a cavity)%s" % (se_good, se_judged, info), thr_o, what_o, "n10")
        else:
            share = se_good / float(se_judged)
            ok = share >= ORE_MIN_SHARE and walls_good == walls
            row(rows, "S8a.N10.ore", "PASS" if ok else "FAIL", "SE veins %d/%d (%.0f %%) of %s; drilled cavities with the ore type on their walls %d/%d%s%s" % (
                se_good, se_judged, 100 * share, ores, walls_good, walls, info, ("; " + "; ".join(notes)) if notes else ""), thr_o, what_o, "n10")
    bio = L.sn_ev("s8a.biome")
    if bio:
        agree = sum(1 for d in bio if d.get("biome") == d.get("cameraBiomeName"))
        row(rows, "S8a.N10.biome", "INFO", "%d/%d rows: GetBiome at the camera = names[cameraBiome]" % (agree, len(bio)), "information", "the biome rule at the camera", "n10", required=False)

CRATER_MIN_CUBES = 20         # S8a.N10.crater: cube pockets read in solid rock
CRATER_MIN_SHARE = 0.95       #... of which carry their row's ore in SE's voxels
CRATER_MIN_BANDS = 2          #... over at least this many depth bands

def n10c_rows(L, rows):
    """S8a.N10.crater (Stage E): SE ore voxels inside the Subnautica map at the table's depths (the cube rule's pockets read back
 from the tiles, de.s8a ores) and a pocket under a Subnautica large resource deposit (the deposits Subnautica reported)."""
    if not L.ran("n10c"):
        return
    ev = (L.events("crater_ores") or [None])[-1]
    rep = ev.get("reply") if ev else None
    thr = ">= %d cube pockets, >= %.0f %% with their ore, >= %d bands; >= 1 deposit pocket with its ore" % (CRATER_MIN_CUBES, 100 * CRATER_MIN_SHARE, CRATER_MIN_BANDS)
    what = "the Subnautica map's ores in SE (depth-rarity table by SN cube; pockets under Subnautica's large deposits)"
    if not isinstance(rep, dict) or not rep.get("ok"):
        row(rows, "S8a.N10.crater", "MISSING", "no de.s8a ores reply (%s)" % ((rep or {}).get("error") if isinstance(rep, dict) else "none"), thr, what, "n10c")
        return
    cubes, match = int(rep.get("cubes") or 0), int(rep.get("cubesMatch") or 0)
    bands = rep.get("bands") or {}
    dsolid, dmatch, reported = int(rep.get("depositsSolid") or 0), int(rep.get("depositsMatch") or 0), int(rep.get("reported") or 0)
    bad = [r for r in rep.get("rows") or [] if r.get("kind") == "cube" and r.get("got") != r.get("expected")][:4]
    cube_ok = cubes >= CRATER_MIN_CUBES and match >= CRATER_MIN_SHARE * cubes and len(bands) >= CRATER_MIN_BANDS
    detail = "cube pockets %d/%d with their ore over bands %s%s; deposits %d/%d solid with their ore (%d reported by Subnautica)" % (
        match, cubes, bands, ("; e.g. " + ", ".join("%s got %s at %.0f m" % (b.get("expected"), b.get("got"), b.get("depth") or 0) for b in bad)) if bad else "",
        dmatch, dsolid, reported)
    if cubes == 0:
        row(rows, "S8a.N10.crater", "MISSING", "no cube pocket in solid rock read: " + detail, thr, what, "n10c")
    elif not cube_ok:
        row(rows, "S8a.N10.crater", "FAIL", detail, thr, what, "n10c")
    elif dsolid == 0:
        row(rows, "S8a.N10.crater", "MISSING", detail + " (no deposit under a large resource deposit to judge: Subnautica started none in the visited cells)", thr, what, "n10c")
    else:
        row(rows, "S8a.N10.crater", "PASS" if dmatch >= 1 and dmatch >= 0.9 * dsolid else "FAIL", detail, thr, what, "n10c")

def n11_rows(L, rows):
    """S8a.N11.beacon (Stage E): after the re-arm the GPS is added once and the radio text shown once (s8a.beacon rows; the two
 status replies of the part agree: no second add)."""
    if not L.ran("n11"):
        return
    thr = "after the re-arm: exactly 1 'added' row, the text shown once, no add between the two status replies"
    what = "the Aurora distress beacon: the GPS and the radio text on the first arrival in DE_4546B's gravity"
    se = L.se_ev("s8a.beacon")
    rearm = [i for i, r in enumerate(se) if r.get("action") == "retrigger"]
    ev = (L.events("beacon") or [None])[-1]
    if not rearm:
        row(rows, "S8a.N11.beacon", "MISSING", "no re-arm row (s8a.beacon retrigger)", thr, what, "n11")
        return
    added = [r for r in se[rearm[-1] + 1:] if r.get("action") == "added"]
    s1 = (ev or {}).get("first") or {}
    s2 = (ev or {}).get("second") or {}
    same = s1.get("added") is not None and s1.get("added") == s2.get("added") and s1.get("shown") == s2.get("shown")
    texts = [r.get("text") for r in added]
    ok = len(added) == 1 and same and bool(texts[0])
    row(rows, "S8a.N11.beacon", "PASS" if ok else ("MISSING" if not added else "FAIL"),
        "%d 'added' row(s) after the re-arm%s; status added %s -> %s, shown %s -> %s" % (
            len(added), " (%s%s, SE %s, SN %s)" % (added[0].get("where") or "gravity", ", far %s km" % added[0].get("km") if added[0].get("far") else "", added[0].get("se"), added[0].get("sn"))
            if added else " (no local player or no DE_4546B in the world?)",
            s1.get("added"), s2.get("added"), s1.get("shown"), s2.get("shown")), thr, what, "n11")

ND_MAX_HIDDEN_FRAMES = 1      # S8a.ND.hidden: no hidden-cell interval longer than one frame
ND_MIN_LIVE = 10              # S8a.ND.live: live edits applied per pass (a cut every 250 ms for 20 s; the converter coalesces)
ND_MAX_LIVE_MS = 1000         #... p95 of a live edit (notice seen -> the level-0 cells swapped): within the same second
ND_CAM_MOVE_M = 5.0           # a cell row whose camera is farther than this from the pass's spot is not judged (streaming, not the swap)
ND_CAM_MOVED_SHARE = 0.10     #... more than this share of the rows: the hidden / wall rows are MISSING (the camera was not held)

def nd_rows(L, rows):
    """Stage D (drilling like SE's own): per drill pass the per-frame cell rows (s8a.cellvis), the live edits (s8a.live), the
 fallbacks to batch reloads and the round trip of the drilled batches."""
    if not L.ran("nd"):
        return
    passes = L.events("drill_pass")
    thr_h = "no frame run with an uncovered level-0 cell over the edited box longer than %d frame" % ND_MAX_HIDDEN_FRAMES
    thr_w = "the cell under the edited box's centre covered in every frame"
    thr_l = ">= %d live edits per pass, p95 (notice seen -> the last level-0 cell swapped) <= %d ms, no fallback to a batch reload" % (ND_MIN_LIVE, ND_MAX_LIVE_MS)
    thr_r = "every drilled batch: the octrees Subnautica holds = the served file loaded the loader's way (byte-identical)"
    if not passes:
        for rid, thr in (("S8a.ND.hidden", thr_h), ("S8a.ND.wall", thr_w), ("S8a.ND.live", thr_l), ("S8a.ND.roundtrip", thr_r)):
            row(rows, rid, "MISSING", "no drill pass", thr, "Stage D", "nd")
        return
    vis = L.sn_ev("s8a.cellvis")
    live = L.sn_ev("s8a.live")
    span = L.sn_frames("nd")
    if span:
        # a test run: only nd's own frames (nf's drill pass turns the cell rows on too)
        vis = [d for d in vis if not isinstance(d.get("snFrame"), int) or span[0] <= d["snFrame"] <= span[1]]
        live = [d for d in live if not isinstance(d.get("snFrame"), int) or span[0] <= d["snFrame"] <= span[1]]
    # nd rerun (session 20261023-213453): the camera left its spot (SE gave it back to the swimming character), so the runs
 # measured the streaming of a moving camera; rows whose camera is farther than ND_CAM_MOVE_M from every pass's spot are not
 # judged
    spots = [p.get("camSn") for p in passes if isinstance(p.get("camSn"), list) and len(p["camSn"]) == 3]
    moved, max_off = 0, 0.0
    kept = []
    for d in vis:
        cx, cy, cz = _num(d, "camX"), _num(d, "camY"), _num(d, "camZ")
        if spots and cx is not None and cy is not None and cz is not None:
            off = min(math.sqrt((cx - s_[0]) ** 2 + (cy - s_[1]) ** 2 + (cz - s_[2]) ** 2) for s_ in spots)
            max_off = max(max_off, off)
            if off > ND_CAM_MOVE_M:
                moved += 1
                continue
        kept.append(d)
    runs, walls, nvis = [], [], 0
    for d in kept:
        nvis += 1
        runs.append(int(d.get("run") or 0))
        if d.get("centreCovered") is False:
            walls.append(d)
    moved_note = "; %d frames not judged: the camera %.0f m from the pass's spot" % (moved, max_off) if moved else ""
    if vis and moved > ND_CAM_MOVED_SHARE * len(vis):
        why = "the camera left the pass's spot in %d of %d frames (up to %.0f m): the rows would measure its streaming, not the swap" % (moved, len(vis), max_off)
        row(rows, "S8a.ND.hidden", "MISSING", why, thr_h, "Stage D (1): swap, never hide", "nd")
        row(rows, "S8a.ND.wall", "MISSING", why, thr_w, "Stage D: the hole's wall in every frame", "nd")
    elif nvis == 0:
        row(rows, "S8a.ND.hidden", "MISSING", "no s8a.cellvis rows (the switch did not reach Subnautica)", thr_h, "Stage D (1): swap, never hide", "nd")
        row(rows, "S8a.ND.wall", "MISSING", "no s8a.cellvis rows", thr_w, "Stage D: the hole's wall in every frame", "nd")
    else:
        worst = max(runs) if runs else 0
        row(rows, "S8a.ND.hidden", "PASS" if worst <= ND_MAX_HIDDEN_FRAMES else "FAIL", "%d frames, longest run with an uncovered cell %d frame(s)%s" % (nvis, worst, moved_note), thr_h,
            "Stage D (1): swap, never hide", "nd")
        row(rows, "S8a.ND.wall", "PASS" if not walls else "FAIL", "%d/%d frames with the centre cell covered%s%s" % (
            nvis - len(walls), nvis, "; first uncovered state %s" % walls[0].get("centreState") if walls else "", moved_note), thr_w, "Stage D: the hole's wall in every frame", "nd")
    edits = [d for d in live if d.get("levelZero") and (d.get("seq") or -1) >= 0]
    fallbacks = sum(int((p.get("snStatus") or {}).get("liveFallbacks") or 0) for p in passes[-1:])
    # night queue: "ms" is the whole job (levels 1-3 included); the rule's measure is the level-0 swap
    ms = sorted(float(d.get("level0Ms") or d.get("ms") or 0) for d in edits)
    p95 = ms[int(0.95 * (len(ms) - 1))] if ms else None
    per_pass = len(edits) / float(len(passes))
    ok_l = per_pass >= ND_MIN_LIVE and p95 is not None and p95 <= ND_MAX_LIVE_MS and fallbacks == 0
    # a test run (nd 20261024-003439): where a level-0 cell's time goes and the
 # level-0 cells per edit by layer (the planet's window notices covered 180 cells, the crater's drill boxes 27-36)
    timing = ""
    if edits and any(d.get("l0MeshMs") is not None for d in edits):
        def mean(k):
            v = [float(d.get(k) or 0.0) for d in edits if d.get(k) is not None]
            return sum(v) / len(v) if v else 0.0
        timing = "; per level-0 cell: queue %.1f, mesh %.1f, wait %.1f, finish %.1f ms; first swap mean %.0f ms; finishing budget %s ms" % (
            mean("l0QueueMs"), mean("l0MeshMs"), mean("l0WaitMs"), mean("l0FinalMs"), mean("firstL0Ms"), edits[-1].get("finalizeBudgetMs"))
    cells = {}
    for d in edits:
        cells.setdefault((d.get("bx"), d.get("by"), d.get("bz")), []).append(int(d.get("rebuilds0") or 0))
    by_batch = ", ".join("%s-%s-%s: %d-%d" % (k[0], k[1], k[2], min(v), max(v)) for k, v in sorted(cells.items(), key=lambda kv: str(kv[0])))
    row(rows, "S8a.ND.live", "MISSING" if not edits else ("PASS" if ok_l else "FAIL"),
        "%d live edits over %d pass(es) (%.0f per pass), p95 %s ms, %d swapped cells, %d fallbacks; level-0 cells per edit by batch %s%s" % (
            len(edits), len(passes), per_pass, "%.0f" % p95 if p95 is not None else "n/a", sum(int(d.get("swapped") or 0) for d in live), fallbacks,
            by_batch or "n/a", timing),
        thr_l, "Stage D (3): the live in-memory edit", "nd")
    flora = L.sn_ev("s8a.flora")
    if flora:

        fam = {}
        for d in flora:
            for part in str(d.get("families") or "").split(","):
                if ":" not in part:
                    continue
                k, v = part.split(":", 1)
                n = [int(x) for x in v.split("/")] if v.count("/") == 2 else [0, 0, 0]
                t = fam.setdefault(k, [0, 0, 0])
                for i in range(3):
                    t[i] += n[i]
        late = [d for d in flora if d.get("why") == "late"]
        famtxt = "; ".join("%s %d tested, %d hidden, %d kept" % (k, v[0], v[1], v[2]) for k, v in sorted(fam.items(), key=lambda kv: -kv[1][0]))
        row(rows, "S8a.ND.flora", "INFO", "%d carve tests (%d of late-loaded objects), %d entities tested, %d hung over a hole and hidden (transient)%s" % (
            len(flora) - len(late), len(late), sum(int(d.get("candidates") or 0) for d in flora), sum(int(d.get("hidden") or 0) for d in flora),
            (" | by family: " + famtxt) if famtxt else " (no family counts: a build before flora v2)"),
            "information", "flora and props without ground after a carve hidden, by family (owner play 2026-10-23 night)", "nd", required=False)
    ver = [v for p in passes for v in (p.get("verify") or []) if isinstance(v, dict)]
    judged = [v for v in ver if v.get("served") and (v.get("octreesCompared") or 0) > 0]
    unloaded = [v for v in ver if v.get("served") and (v.get("streamers") or 0) == 0]
    bad = [v for v in judged if (v.get("octreesDiffer") or 0) != 0]
    if not judged:
        row(rows, "S8a.ND.roundtrip", "MISSING", "no drilled batch verified (%d replies)" % len(ver), thr_r, "Stage D (3): the in-memory patch = the file", "nd")
    else:
        row(rows, "S8a.ND.roundtrip", "PASS" if not bad else "FAIL", "%d batches, %d octrees compared, %d differ%s" % (
            len(judged), sum(int(v.get("octreesCompared") or 0) for v in judged), sum(int(v.get("octreesDiffer") or 0) for v in judged),
            "; %d drilled batch(es) not held by any streamer at the end (%s: the camera had left)" % (len(unloaded), ",".join("%s-%s-%s" % (v.get("bx"), v.get("by"), v.get("bz")) for v in unloaded)) if unloaded else ""), thr_r,
            "Stage D (3): the in-memory patch = the file", "nd")

# ---------------------------------------------------------------------------------------------------------- Stage F2 (nf)
NF_FIELD_M = 0.5            # S8a.NF.served: SE's surface = the ring field's own top surface (SE serves the field, not the heightmap)
NF_MIN_FEATURED = 5         #... over at least this many columns where the field departs from the heightmap by > 1 m
NF_SEAM_STEP_M = 0.5        # S8a.NF.seam: |Subnautica's step across a window batch seam - SE's step there|
NF_COST_FACTOR = 4.0        # S8a.NF.cost: per LOD, the served p95 <= this x SE's own generator p95 at that LOD (same run) + NF_COST_ABS_MS
NF_COST_ABS_MS = 0.5
NF_GAME_MAX_MS = 8.0        #... and every served call on the game thread <= max(this, 2 x SE's own max on the game thread at that LOD)
NF_COST_MIN_SAMPLES = 20
NF_FALLBACK_M = 0.3         # S8a.NF.fallback: SE's surface at the failing column = the heightmap seabed
NF_MAX_LOD = 3
NS_ENTITY_R_A = 60.0        # the launcher's NS_ENTITY_R (S8a.NS.entities' radius)
NL_COVER_MIN, NL_COVER_SHARE = 0.03, 0.5   # S8a.NL.look against the reference: the ground cover at least half the reference's (when it has some)
NS_CLIFF_SLOPE = 1.0        # S8a.NS.terrain: SN vs SE judged on columns no steeper than this (rise per run over the 8 m line step; a cliff is reported)
NS_SLOPE_ALLOW = 0.5        #... the max per column: N7's max + this x the slope (the 1 m lattice's half-voxel horizontal offset), in the core

def _reply(L, label):
    """The last reply of a launcher call by its label (dict or None)."""
    out = None
    for r in L.events("reply", label=label):
        out = r.get("reply") if isinstance(r.get("reply"), dict) else out
    return out

def _field_map(L, when, kind):
    """{(x, z): (top, seabed)} of a ring_field event (de.s8a ring {columns})."""
    out = {}
    for r in L.events("ring_field", when=when, kind=kind):
        for c in r.get("results") or []:
            if isinstance(c, list) and len(c) >= 4 and _finite(c[0]) and _finite(c[1]):
                out[(round(float(c[0]), 3), round(float(c[1]), 3))] = (c[2] if _finite(c[2]) else None, c[3] if _finite(c[3]) else None)
    return out

def nf_rows(L, rows):
    """Stage F2: the ring field served by the planet's generator (de.s8a ring status, the nf paths, seams, blend, served bytes,
 the fallback, the drill pass)."""
    if not L.ran("nf"):
        return
    st = _reply(L, "nf.status.end") or {}
    st0 = _reply(L, "nf.status.start") or {}
    skip = L.events("nf_skip")
    thr_r = "ring on, patch installed, field ready, placements > 0"
    if skip or not st:
        why = skip[-1].get("reason") if skip else "no de.s8a ring status"
        for rid in ("S8a.NF.ready", "S8a.NF.agree", "S8a.NF.served", "S8a.NF.seam", "S8a.F.repeat", "S8a.F.forbidden", "S8a.F.blend", "S8a.NF.cost",
                    "S8a.NF.scope", "S8a.NF.determinism", "S8a.NF.fallback", "S8a.NF.memory", "S8a.NF.time", "S8a.NF.drill"):
            row(rows, rid, "MISSING", why, "-", "Stage F2", "nf")
        return
    ready = st.get("state") == "ready" and st.get("setting") and st.get("patch") and (st.get("placements") or 0) > 0
    row(rows, "S8a.NF.ready", "PASS" if ready else "FAIL", "%s, %s placements of %s brushes, init %.0f ms, base %s%s" % (
        st.get("state"), st.get("placements"), st.get("brushesUsed"), _num(st, "initMs") or 0.0, st.get("source"),
        "; refused: %s" % st.get("refusal") if st.get("refusal") else ""), thr_r, "the ring field initialised from SE's maps and the store's library", "nf")
    # agree / served: the nf paths
    diffs, served, featured, miss = [], [], 0, []
    paths = [e.get("name") for e in L.events("nf_path")]
    for name in paths:
        se_r, sn_r = L.rays("se", "path", name), L.rays("sn", "path", name)
        semap = se_results_map((se_r or {}).get("results"), "surface")
        snmap = sn_results_map((sn_r or {}).get("results"))
        fmap = _field_map(L, name, "path")
        cols = [k for k in semap if k in snmap]
        if not cols:
            miss.append(name)
        diffs += [abs(snmap[k] - semap[k]) for k in cols]
        for k, y in semap.items():
            top, h = fmap.get(k, (None, None))
            if top is None:
                continue
            served.append(abs(y - top))
            if h is not None and abs(top - h) > 1.0:
                featured += 1
    thr_a = "P95 <= %.1f m, max <= %.1f m (N7's budget)" % (WINDOW_RAYS_P95_M, WINDOW_RAYS_MAX_M)
    if not diffs:
        row(rows, "S8a.NF.agree", "MISSING", "no ring path with SE and Subnautica rays" + ("; " + ",".join(miss) if miss else ""), thr_a, "Subnautica vs SE over the ring field", "nf")
    else:
        p95, mx = pct(diffs, 0.95), max(diffs)
        row(rows, "S8a.NF.agree", "PASS" if p95 <= WINDOW_RAYS_P95_M and mx <= WINDOW_RAYS_MAX_M else "FAIL", "P95 %.3f m, max %.3f m over %d columns of %d paths" % (
            p95, mx, len(diffs), len(paths)), thr_a, "Subnautica layer-30 vs SE's surface (CastRay, else storage) along the ring paths (stamps, caves, arches)", "nf")
    thr_s = "|SE surface - the field's top| P95 <= %.1f m over >= %d columns where the field departs from the heightmap by > 1 m" % (NF_FIELD_M, NF_MIN_FEATURED)
    if not served:
        row(rows, "S8a.NF.served", "MISSING", "no field surface beside SE's rays", thr_s, "SE serves the ring field (not the heightmap)", "nf")
    else:
        p95 = pct(served, 0.95)
        row(rows, "S8a.NF.served", "PASS" if p95 <= NF_FIELD_M and featured >= NF_MIN_FEATURED else "FAIL", "P95 %.3f m over %d columns, %d of them off the heightmap by > 1 m" % (
            p95, len(served), featured), thr_s, "SE's own surface is the ring field's (de.s8a ring {columns}: the C# field's top surface)", "nf")
    # seams
    seam_dev, seam_sn, nseam = [], [], 0
    for e in L.events("nf_seam"):
        name, sm = e.get("name"), e.get("spot") or {}
        sx = _num(sm, "seamX")
        semap = se_results_map((L.rays("se", "seam", name) or {}).get("results"), "surface")
        snmap = sn_results_map((L.rays("sn", "seam", name) or {}).get("results"))
        if sx is None or not semap or not snmap:
            continue
        nseam += 1
        seam_sn += [abs(snmap[k] - semap[k]) for k in semap if k in snmap]
        zs = sorted({k[1] for k in semap})
        for z in zs:
            row_k = sorted(k for k in semap if k[1] == z and k in snmap)
            for a, b in zip(row_k, row_k[1:]):
                if a[0] < sx < b[0]:
                    seam_dev.append(abs((snmap[b] - snmap[a]) - (semap[b] - semap[a])))
    thr_m = "across every seam column pair |SN step - SE step| <= %.1f m; SN - SE P95 <= %.1f m" % (NF_SEAM_STEP_M, WINDOW_RAYS_P95_M)
    if not seam_dev:
        row(rows, "S8a.NF.seam", "MISSING", "no seam with rays on both sides", thr_m, "window batch seams on steep ring spots", "nf")
    else:
        ok = max(seam_dev) <= NF_SEAM_STEP_M and pct(seam_sn, 0.95) <= WINDOW_RAYS_P95_M
        row(rows, "S8a.NF.seam", "PASS" if ok else "FAIL", "%d seams, %d pairs across: step deviation max %.3f m; SN - SE P95 %.3f m over %d columns" % (
            nseam, len(seam_dev), max(seam_dev), pct(seam_sn, 0.95), len(seam_sn)), thr_m, "Subnautica follows SE across window batch seams on steep ring spots (cliffs)", "nf")
    # repeat / forbidden / blend from the served field
    near = _num(st, "nearestSameBrushM")
    row(rows, "S8a.F.repeat", "PASS" if near is not None and near >= (_num(st, "repeatM") or 300.0) else "FAIL",
        "%s placements of %s brushes; nearest same brush %.0f m" % (st.get("placements"), st.get("brushesUsed"), near if near is not None else -1),
        ">= RepeatM (%.0f m)" % (_num(st, "repeatM") or 300.0), "no brush twice within RepeatM in the served placements", "nf")
    fb, fp = _num(st, "forbiddenBrushes"), _num(st, "forbiddenPlacements")
    row(rows, "S8a.F.forbidden", "PASS" if fb == 0 and fp == 0 else "FAIL", "library brushes near a landmark root or below the deep limit %s, placed %s" % (fb, fp),
        "0 and 0", "no forbidden landmark in a served brush", "nf")
    bl = ((L.events("nf_blend") or [{}])[-1].get("blend")) or {}
    thr_b = "border step P95 <= max(2 x inside, 2 m)"
    if not bl or bl.get("running") or not bl.get("borderPairs"):
        row(rows, "S8a.F.blend", "MISSING", "no finished blend probe%s" % ("; " + str(bl.get("error")) if bl.get("error") else ""), thr_b, "stamps blend into the base (served planet)", "nf")
    else:
        row(rows, "S8a.F.blend", "PASS" if bl.get("ok") else "FAIL", "%d border pairs: step P95 %.2f m (inside %.2f m, bound %.2f m; %d inner pairs) over %d placements, %.0f ms" % (
            bl.get("borderPairs") or 0, _num(bl, "borderP95") or 0.0, _num(bl, "innerP95") or 0.0, _num(bl, "bound") or 0.0, bl.get("innerPairs") or 0,
            len(bl.get("placements") or []), _num(bl, "ms") or 0.0), thr_b, "the surface steps at the stamps' borders read back from SE's planet storage", "nf")
    # cost and scope
    cost = st.get("cost") or []
    thr_c = ("per LOD 0-%d: served p95 <= %.0f x SE's own generator p95 at that LOD (same run, any zone) + %.1f ms; served max on the game thread <= max(%.0f ms, "
             "2 x SE's own max there)" % (NF_MAX_LOD, NF_COST_FACTOR, NF_COST_ABS_MS, NF_GAME_MAX_MS))
    parts_c, bad_c, judged = [], [], 0
    for lod in range(NF_MAX_LOD + 1):
        srv = [c for c in cost if c.get("cat") == "served" and c.get("lod") == lod]
        van = [c for c in cost if c.get("cat") in ("vanillaZone", "vanillaOutside") and c.get("lod") == lod]
        ns, nv = sum(c.get("count") or 0 for c in srv), sum(c.get("count") or 0 for c in van)
        if ns == 0:
            continue
        sp95 = max(_num(c, "p95") or 0.0 for c in srv)
        if nv < NF_COST_MIN_SAMPLES:
            parts_c.append("LOD %d: served p95 %.2f ms (%d), SE's own %d samples (too few)" % (lod, sp95, ns, nv))
            continue
        judged += 1
        vp95 = max(_num(c, "p95") or 0.0 for c in van if (c.get("count") or 0) >= 1)
        budget = NF_COST_FACTOR * vp95 + NF_COST_ABS_MS
        sg = [c for c in srv if c.get("thread") == "game"]
        vg = [c for c in van if c.get("thread") == "game"]
        smax_g = max([_num(c, "max") or 0.0 for c in sg] or [0.0])
        gbudget = max(NF_GAME_MAX_MS, 2.0 * max([_num(c, "max") or 0.0 for c in vg] or [0.0]))
        nsv = max([_num(c, "nsPerVoxel") or 0.0 for c in srv] or [0.0])
        nvv = max([_num(c, "nsPerVoxel") or 0.0 for c in van] or [0.0])
        parts_c.append("LOD %d: served p95 %.2f ms (%d, %.0f ns/voxel) vs SE's own %.2f ms (%d, %.0f ns/voxel), budget %.2f; game thread served max %.2f ms (%d calls, budget %.1f)" % (
            lod, sp95, ns, nsv, vp95, nv, nvv, budget, smax_g, sum(c.get("count") or 0 for c in sg), gbudget))
        if sp95 > budget or smax_g > gbudget:
            bad_c.append(lod)
    if judged == 0:
        row(rows, "S8a.NF.cost", "MISSING", "; ".join(parts_c) or "no served reads timed", thr_c, "the ReadRange prefix's time against SE's own generator", "nf")
    else:
        row(rows, "S8a.NF.cost", "FAIL" if bad_c else "PASS", "; ".join(parts_c), thr_c,
            "the ReadRange prefix's time per call and LOD (all threads; the game thread's max) against SE's own generator in the same run", "nf")
    above = [c for c in cost if c.get("cat") == "served" and (c.get("lod") or 0) > NF_MAX_LOD]
    so, sa, reads, passes = _num(st, "servedOutside") or 0, _num(st, "servedAboveLod") or 0, _num(st, "reads") or 0, _num(st, "passes") or 0
    row(rows, "S8a.NF.scope", "PASS" if reads > 0 and passes > 0 and so == 0 and sa == 0 and not above else ("MISSING" if reads == 0 else "FAIL"),
        "%d served reads, %d let through to SE's generator; served outside the ring %d, above LOD %d %d (cost table rows above: %d)" % (
            reads, passes, so, NF_MAX_LOD, sa, len(above)), "0 served outside the ring zone, 0 above LOD %d" % NF_MAX_LOD,
        "the field path is never taken outside the ring (SiteMap's own arctan checks each served box) or at LOD > 3", "nf")
    # determinism
    sv = (L.events("nf_serve") or [{}])[-1]
    vec, rep = sv.get("vectors") or {}, sv.get("reply") or {}
    head = vec.get("head") or {}
    thr_d = "per serve box: the C# served bytes and SE's generator read = the twin's FNV (inputs equal to the vectors')"
    if not rep or not rep.get("results"):
        row(rows, "S8a.NF.determinism", "MISSING", "no de.s8a ring {serve} reply%s" % ("; vectors: " + str(vec.get("error")) if vec.get("error") else ""), thr_d,
            "the served bytes are a function of the pin (saved drill deltas sit on them)", "nf")
    else:
        inputs = []
        for key, val in (("heights", rep.get("heightsFnv")), ("biomes", rep.get("biomesFnv")), ("rules", rep.get("rules"))):
            if (head.get(key) or [None])[0] != val:
                inputs.append("%s %s vs vectors %s" % (key, val, (head.get(key) or [None])[0]))
        lib = head.get("library") or [None, None]
        if lib[0] != rep.get("libraryFnv") or lib[1] != rep.get("indexFnv"):
            inputs.append("library %s/%s vs vectors %s/%s" % (rep.get("libraryFnv"), rep.get("indexFnv"), lib[0], lib[1]))
        res = rep.get("results") or []
        same = [r for r in res if r.get("fieldFnv") and r.get("fieldFnv") == r.get("providerFnv")]
        want = {tuple(v["box"]): v["fnv"] for v in vec.get("serve") or []}
        match = [r for r in res if want.get(tuple(r.get("box") or ())) == r.get("fieldFnv") == r.get("providerFnv")]
        detail = "%d boxes: C# = SE's generator read %d, = the twin %d" % (len(res), len(same), len(match))
        if inputs:
            row(rows, "S8a.NF.determinism", "MISSING" if len(same) == len(res) else "FAIL", detail + "; inputs differ from the vectors' (%s): rerun s8a_ring3d.py vectors" % "; ".join(inputs),
                thr_d, "the served bytes are a function of the pin (saved drill deltas sit on them)", "nf")
        else:
            row(rows, "S8a.NF.determinism", "PASS" if res and len(match) == len(res) else "FAIL", detail, thr_d,
                "the served bytes are a function of the pin: the game's equal the twin's, so every run and machine serves these bytes", "nf")
    # fallback
    fe = (L.events("nf_fallback") or [{}])[-1]
    thr_f = ("S8a.M15 failed >= 1 read; at every column: an SE surface (no hole) within %.1f m of the heightmap seabed, Subnautica within %.1f m of SE (P95); "
             "neighbour steps as the heightmap's (no seam)" % (NF_FALLBACK_M, WINDOW_RAYS_P95_M))
    if not fe.get("spot"):
        row(rows, "S8a.NF.fallback", "MISSING", fe.get("reason") or "no fallback exercise", thr_f, "SE's heightmap serves a column the field fails on", "nf")
    else:
        inj = (_num(fe.get("after") or {}, "failInjected") or 0) - (_num(fe.get("before") or {}, "failInjected") or 0)
        semap = se_results_map((L.rays("se", "fallback", "nf.fallback") or {}).get("results"), "surface")
        snmap = sn_results_map((L.rays("sn", "fallback", "nf.fallback") or {}).get("results"))
        fmap = _field_map(L, "nf.fallback", "fallback")
        cols = sorted(fmap)
        holes = [k for k in cols if k not in semap]
        off = [abs(semap[k] - fmap[k][1]) for k in cols if k in semap and fmap[k][1] is not None]
        agree = [abs(snmap[k] - semap[k]) for k in cols if k in semap and k in snmap]
        steps = []
        for axis in (0, 1):
            line = sorted((k for k in cols if k in semap and fmap[k][1] is not None and k[1 - axis] == fe["spot"]["z" if axis == 0 else "x"]), key=lambda k: k[axis])
            for a, b in zip(line, line[1:]):
                steps.append(abs((semap[b] - semap[a]) - (fmap[b][1] - fmap[a][1])))
        ok = inj >= 1 and not holes and off and max(off) <= NF_FALLBACK_M and agree and pct(agree, 0.95) <= WINDOW_RAYS_P95_M and (not steps or max(steps) <= NF_FALLBACK_M)
        row(rows, "S8a.NF.fallback", "PASS" if ok else ("MISSING" if inj < 1 else "FAIL"),
            "%d reads failed on the column (%.0f, %.0f); %d columns, %d without an SE surface; |SE - heightmap| max %.3f m; SN - SE P95 %.3f m; step deviation max %.3f m" % (
                inj, fe["spot"].get("x", 0.0), fe["spot"].get("z", 0.0), len(cols), len(holes), max(off) if off else -1.0, pct(agree, 0.95) if agree else -1.0,
                max(steps) if steps else 0.0), thr_f, "S8a.M15 makes the field fail on a plain column: SE's heightmap serves it, no hole, no seam", "nf")
    # memory
    w = L.window("nf")
    mems = [d for d in L.events("mem") if str(d.get("part") or "").startswith("nf")]
    se_mem = [(_num(d, "tick"), _num(d, "workingSetMB")) for d in L.se_raw_samples() if _num(d, "workingSetMB") is not None]
    se_in = [m for t, m in se_mem if t is not None and w and w[1] <= t <= w[2]]
    if len(se_in) < 2:
        se_in = [_num(d, "seWorkingSetMB") for d in mems if _num(d, "seWorkingSetMB") is not None]
    sn_in = [_num(d, "snWorkingSetMB") for d in mems if _num(d, "snWorkingSetMB") is not None]
    init = (_num(st, "privateAfterMB") or 0.0) - (_num(st, "privateBeforeMB") or 0.0)
    thr_mem = "SE: the field's init + growth over nf <= %.0f MB; Subnautica growth <= %.0f MB (the S10 budgets)" % (WINDOW_MEM_SE_MB, WINDOW_MEM_SN_MB)
    if len(se_in) < 2 and len(sn_in) < 2:
        row(rows, "S8a.NF.memory", "MISSING", "init +%.0f MB; no memory samples inside nf" % init, thr_mem, "the ring field's memory", "nf")
    else:
        parts_m, okm = ["SE field init +%.0f MB" % init], True
        if len(se_in) >= 2:
            g = max(se_in) - se_in[0]
            okm &= init + g <= WINDOW_MEM_SE_MB
            parts_m.append("SE growth +%.0f MB (%d samples)" % (g, len(se_in)))
        if len(sn_in) >= 2:
            g = max(sn_in) - sn_in[0]
            okm &= g <= WINDOW_MEM_SN_MB
            parts_m.append("Subnautica +%.0f MB (%d samples)" % (g, len(sn_in)))
        row(rows, "S8a.NF.memory", "PASS" if okm else "FAIL", "; ".join(parts_m), thr_mem, "the ring field's memory (SE) and the window over the ring (both)", "nf")
    # time: the window's batches over the ring
    writes = [d for d in L.se_ev("s8a.write") if d.get("layer") == "window" and w and _num(d, "tick") is not None and w[1] <= d["tick"] <= w[2]]
    surf = [_num(d, "ms") for d in writes if d.get("kind") == "Surface" and _num(d, "ms") is not None]
    per = [_num(d, "ms") / _num(d, "octrees") for d in writes if d.get("kind") == "Surface" and (_num(d, "octrees") or 0) > 0 and _num(d, "ms") is not None]
    thr_t = "per surface octree p95 <= %.0f ms and surface batch p95 <= %.0f ms (N7's)" % (WINDOW_SURFACE_OCTREE_P95_MS, WINDOW_SURFACE_P95_MS)
    if not surf:
        row(rows, "S8a.NF.time", "MISSING", "no window surface batch inside nf", thr_t, "the window's batch time over the ring field", "nf")
    else:
        ok_t = pct(surf, 0.95) <= WINDOW_SURFACE_P95_MS and (not per or pct(per, 0.95) <= WINDOW_SURFACE_OCTREE_P95_MS)
        row(rows, "S8a.NF.time", "PASS" if ok_t else "FAIL", "surface batch p95 %.0f ms (%d), per surface octree p95 %.1f ms" % (
            pct(surf, 0.95), len(surf), pct(per, 0.95) if per else 0.0), thr_t, "the window worker's time per batch over the ring (the field read through SE's storage)", "nf")
    # drill
    dr = (L.events("nf_drill") or [None])[-1]
    thr_dr = ">= %d live edits, level-0 p95 <= %d ms, no fallback, no uncovered run > %d frame, every drilled batch round trip identical" % (
        ND_MIN_LIVE, ND_MAX_LIVE_MS, ND_MAX_HIDDEN_FRAMES)
    if not dr:
        row(rows, "S8a.NF.drill", "MISSING", "no drill pass on the ring", thr_dr, "Stage D's live edit over the ring field", "nf")
    else:
        b, a = dr.get("snBefore") or {}, dr.get("snAfter") or {}
        live = (_num(a, "liveApplied") or 0) - (_num(b, "liveApplied") or 0)
        fbk = (_num(a, "liveFallbacks") or 0) - (_num(b, "liveFallbacks") or 0)
        vis = dr.get("vis") or {}
        run = _num(vis, "runMax")
        batches = {tuple(x) for x in dr.get("batches") or []}
        ms = sorted(float(d.get("level0Ms") or d.get("ms") or 0) for d in L.sn_ev("s8a.live")
                    if d.get("levelZero") and (d.get("bx"), d.get("by"), d.get("bz")) in batches)
        p95 = ms[int(0.95 * (len(ms) - 1))] if ms else None
        ver = [v for v in dr.get("verify") or [] if isinstance(v, dict) and v.get("served") and (v.get("octreesCompared") or 0) > 0]
        differ = sum(int(v.get("octreesDiffer") or 0) for v in ver)
        ok_d = live >= ND_MIN_LIVE and fbk == 0 and run is not None and run <= ND_MAX_HIDDEN_FRAMES and p95 is not None and p95 <= ND_MAX_LIVE_MS and ver and differ == 0
        row(rows, "S8a.NF.drill", "PASS" if ok_d else "FAIL", "%d live edits, %d fallbacks, level-0 p95 %s ms, longest uncovered run %s frames, %d batches verified, %d octrees differ" % (
            live, fbk, "%.0f" % p95 if p95 is not None else "n/a", run, len(ver), differ), thr_dr, "a drill pass on a stamp: Stage D over the ring field", "nf")

# ---------------------------------------------------------------------------------------------------------------- no (ores you can see)
NO_AGREE_M = 5.0                # the detector's centroid and the deposit, horizontally (undrilled outcrops)
NO_COVER_M = 95.0               # records judged for cover: within this of the camera (the spawn radius is 100 m)

# ---------------------------------------------------------------------------------------------------------------- planet v2 route B: the sample (ns)
NS_FIELD_M = 0.5            # S8a.NS.terrain: SE's surface = the sample field's own top (SE serves the chunk, not the heightmap), P95
NS_OWN_M = 0.5              #... where the chunk's weight is 1 the field's top = the chunk's own top (the real map's seabed, re-based), P95
NS_TWIN_M = 0.02            #... the C# chunk's own top = the Python twin's (the store's chunk read offline), max
NS_MIN_FEATURED = 5         #... over at least this many core columns where the chunk departs from the heightmap by > 1 m
NS_SEAM_OUTER_M = 0.5       # S8a.NS.seam: beyond the square SE's surface = the heightmap's seabed
NS_SEAM_EDGE_W = 0.25      #... judged where the chunk's weight is under this (a seam wall stands at the square's edge, not inside the band)
NS_SEAM_STEP_MIN_M = 4.0    #... a 4 m step of SE's surface <= max(this, 1.25 x the larger input step + 0.1 x |own - heightmap|: the band's ramp)
NS_ENTITY_SHARE = 0.9       # S8a.NS.entities: Subnautica's live objects per baked family (>= NS_ENTITY_MIN records) / its records within the radius
NS_ENTITY_MIN = 5
NS_RECORDS_TOL = (0.15, 3)  #... SE's records per family within the radius vs the chunk's own (relative, absolute): different frames, same set
NS_ROWS_ALL = ("S8a.NS.ready", "S8a.NS.cut", "S8a.NS.roundtrip", "S8a.NS.placement", "S8a.NS.terrain", "S8a.NS.seam", "S8a.NS.cost", "S8a.NS.scope",
               "S8a.NS.determinism", "S8a.NS.entities")

def _ns_field(L, when, kind):
    """[(x, z, top, own, h, w, nx, nz, twinOwn)] of an ns_field event in column order."""
    out = []
    for r in L.events("ns_field", when=when, kind=kind):
        res, tw = r.get("results") or [], r.get("twinOwn") or []
        out = []
        for i, c in enumerate(res):
            if not isinstance(c, list) or len(c) < 8:
                continue
            out.append(tuple(c[:8]) + (tw[i] if i < len(tw) else None,))
    return out

def _fams(text):
    out = {}
    for part in str(text or "").split(";"):
        if ":" in part:
            k, v = part.split(":", 1)
            try:
                out[k] = int(float(v))
            except ValueError:
                pass
    return out

def ns_rows(L, rows):
    """Planet v2 route B's first slice: the sample chunk served by the planet's generator (part ns)."""
    if not L.ran("ns"):
        return
    st = _reply(L, "ns.status.end") or _reply(L, "ns.status.start") or {}
    skip = L.events("ns_skip")
    if skip or not st:
        why = skip[-1].get("reason") if skip else "no de.s8a sample status"
        for rid in NS_ROWS_ALL:
            row(rows, rid, "MISSING", why, "-", "the sample (route B)", "ns")
        return
    ready = st.get("state") == "ready" and st.get("setting") and st.get("patch")
    row(rows, "S8a.NS.ready", "PASS" if ready else "FAIL", "%s, chunk %s (%s), %s entities (%s left out), re-base %s m, identity %s, init %.0f ms%s" % (
        st.get("state"), st.get("chunk"), st.get("chunkFnv"), st.get("entities"), st.get("excluded"), st.get("rebaseM"), st.get("identity"), _num(st, "initMs") or 0.0,
        "; refused: %s" % st.get("refusal") if st.get("refusal") else ""), "sample on, patch installed, chunk and placement loaded",
        "the sample made from the store's chunk and SE's maps", "ns")
    # offline rows (the store's chunk against the install, run by the launcher on the user's machine)
    off = (L.events("ns_offline") or [{}])[-1]
    got = {ln.split(" ")[0]: (ok, ln) for ok, ln in (off.get("rows") or []) if isinstance(ln, str)}
    for rid, what in (("S8a.NS.cut", "no landmark or unique prefab among the chunk's entities"), ("S8a.NS.roundtrip", "the chunk's octree bytes = the install's, through our writer"),
                      ("S8a.NS.placement", "re-base within +-50 m, the frame orthonormal, the seabed lit")):
        if rid in got:
            ok, ln = got[rid]
            row(rows, rid, "PASS" if ok else "FAIL", ln[len(rid) + 1:], "s8a_chunks.verify", what, "ns")
        else:
            row(rows, rid, "MISSING", off.get("error") or "no offline rows in the session", "s8a_chunks.verify", what, "ns")
    # terrain: the lines
    diffs, served, own, twin, featured, nlines = [], [], [], [], 0, 0
    cliffs, core_max, band_max = [], [], []
    for e in L.events("ns_line"):
        name = e.get("name")
        semap = se_results_map((L.rays("se", "line", name) or {}).get("results"), "surface")
        snmap = sn_results_map((L.rays("sn", "line", name) or {}).get("results"))
        fl = _ns_field(L, name, "line")
        if not semap:
            continue
        nlines += 1
        keys = [(round(float(c[0]), 3), round(float(c[1]), 3)) for c in fl]
        wts = {k: c[5] for k, c in zip(keys, fl)}
        for i, k in enumerate(keys):
            if k not in semap or k not in snmap:
                continue
            nb = [abs(semap[k] - semap[keys[j]]) / 8.0 for j in (i - 1, i + 1) if 0 <= j < len(keys) and keys[j] in semap]
            slope = max(nb) if nb else 0.0
            dd = abs(snmap[k] - semap[k])
            if slope > NS_CLIFF_SLOPE:
                cliffs.append(round(dd, 2))
                continue
            diffs.append(dd)
            wk = wts.get(k)
            if _finite(wk) and wk >= 0.99:
                core_max.append(dd - NS_SLOPE_ALLOW * slope)
            else:
                band_max.append(round(dd, 2))
        for x, z, top, ow, h, w, _nx, _nz, tw in fl:
            k = (round(float(x), 3), round(float(z), 3))
            if _finite(top) and k in semap:
                served.append(abs(semap[k] - top))
            if _finite(top) and _finite(ow) and _finite(w) and w >= 0.999:
                own.append(abs(top - ow))
                if _finite(h) and abs(ow - h) > 1.0:
                    featured += 1
            if _finite(ow) and _finite(tw):
                twin.append(abs(ow - tw))
    thr_t = ("Subnautica vs SE P95 <= %.1f m over the columns no steeper than %.1f (cliffs reported), max <= %.1f m + %.1f x slope in the core (the band reported); SE vs the "
             "field's top P95 <= %.1f m; field vs the chunk's own top (weight 1) P95 <= %.1f m over >= %d columns off the heightmap; C# own top = the twin's <= %.2f m" % (
                 WINDOW_RAYS_P95_M, NS_CLIFF_SLOPE, WINDOW_RAYS_MAX_M, NS_SLOPE_ALLOW, NS_FIELD_M, NS_OWN_M, NS_MIN_FEATURED, NS_TWIN_M))
    if not diffs or not served or not own:
        row(rows, "S8a.NS.terrain", "MISSING", "%d lines; columns with SE+Subnautica %d, SE+field %d, core %d" % (nlines, len(diffs), len(served), len(own)), thr_t,
            "the chunk's terrain served: SE, Subnautica and the chunk's own heights", "ns")
    else:
        cmax = max(core_max) if core_max else 0.0
        ok = (pct(diffs, 0.95) <= WINDOW_RAYS_P95_M and cmax <= WINDOW_RAYS_MAX_M and pct(served, 0.95) <= NS_FIELD_M and pct(own, 0.95) <= NS_OWN_M
              and featured >= NS_MIN_FEATURED and (not twin or max(twin) <= NS_TWIN_M))
        row(rows, "S8a.NS.terrain", "PASS" if ok else "FAIL", "SN-SE P95 %.3f m (%d), core max over the slope allowance %.3f m, band max %s, cliffs %d %s; SE-field P95 %.3f m (%d); "
            "field-own P95 %.3f m (%d core, %d off the heightmap); twin %s" % (
                pct(diffs, 0.95), len(diffs), cmax + WINDOW_RAYS_MAX_M - WINDOW_RAYS_MAX_M, max(band_max) if band_max else "n/a", len(cliffs), sorted(cliffs)[-3:] if cliffs else "",
                pct(served, 0.95), len(served), pct(own, 0.95), len(own), featured, "max %.4f m (%d)" % (max(twin), len(twin)) if twin else "not read (no store chunk)"), thr_t,
            "the chunk's terrain served: SE, Subnautica and the chunk's own heights", "ns")
    # seams
    outer, bad_steps, inner_steps, steps, seam_sn, nseam = [], [], [], 0, [], 0
    for e in L.events("ns_seam"):
        name = e.get("name")
        semap = se_results_map((L.rays("se", "seam", name) or {}).get("results"), "surface")
        snmap = sn_results_map((L.rays("sn", "seam", name) or {}).get("results"))
        fl = _ns_field(L, name, "seam")
        if not semap or not fl:
            continue
        nseam += 1
        seam_sn += [abs(snmap[k] - semap[k]) for k in semap if k in snmap]
        prev = None
        pw = float("nan")
        for x, z, top, ow, h, w, _nx, _nz, _tw in fl:
            k = (round(float(x), 3), round(float(z), 3))
            y = semap.get(k)
            if y is None:
                prev = None
                continue
            if _finite(w) and w <= 0.0 and _finite(h):
                outer.append(abs(y - h))
            if prev is not None:
                py, pow_, ph = prev
                dy = abs(y - py)
                din = max(abs(ow - pow_) if _finite(ow) and _finite(pow_) else 0.0, abs(h - ph) if _finite(h) and _finite(ph) else 0.0)
                ramp = 0.1 * abs(ow - h) if _finite(ow) and _finite(h) else 0.0
                steps += 1
                over = dy > max(NS_SEAM_STEP_MIN_M, 1.25 * din + ramp)
                # ns 20261024-124136: two steps (6.3, 4.8 m at weights 0.72 and 0.5) were the chunk's own caves and overhangs inside the
 # band (the 3D field blended, not a height blend): a seam wall would stand where the chunk's weight is small
                if over and _finite(w) and _finite(pw) and min(w, pw) < NS_SEAM_EDGE_W:
                    bad_steps.append(round(dy, 2))
                elif over:
                    inner_steps.append(round(dy, 2))
            prev = (y, ow, h)
            pw = w
    thr_m = ("beyond the square |SE - heightmap| <= %.1f m; where the chunk's weight is under %.2f every 4 m step of SE's surface <= max(%.0f m, 1.25 x the "
             "inputs' step + 0.1 x |own - heightmap|); Subnautica vs SE P95 <= %.1f m" % (NS_SEAM_OUTER_M, NS_SEAM_EDGE_W, NS_SEAM_STEP_MIN_M, WINDOW_RAYS_P95_M))
    if not nseam or not outer:
        row(rows, "S8a.NS.seam", "MISSING", "%d seams with rays, %d columns beyond the square" % (nseam, len(outer)), thr_m, "the chunk blends into the planet's seabed", "ns")
    else:
        ok = max(outer) <= NS_SEAM_OUTER_M and not bad_steps and (not seam_sn or pct(seam_sn, 0.95) <= WINDOW_RAYS_P95_M)
        row(rows, "S8a.NS.seam", "PASS" if ok else "FAIL", "%d seams: beyond the square max %.3f m (%d); %d steps, %d over the bound at the edge%s; inside the band "
            "(the chunk's relief) %s; SN-SE P95 %s" % (
            nseam, max(outer), len(outer), steps, len(bad_steps), (" " + str(bad_steps[:6])) if bad_steps else "", str(inner_steps[:6]) if inner_steps else "none over",
            "%.3f m (%d)" % (pct(seam_sn, 0.95), len(seam_sn)) if seam_sn else "n/a"), thr_m, "the chunk blends into the planet's seabed", "ns")
    # cost and scope
    cost, van = st.get("cost") or [], [c for c in (st.get("vanilla") or []) if c.get("cat") in ("vanillaZone", "vanillaOutside")]
    thr_c = ("per LOD 0-%d: the sample's served p95 <= %.0f x SE's own generator p95 at that LOD (same run) + %.1f ms; served max on the game thread <= max(%.0f ms, "
             "2 x SE's own max there)" % (NF_MAX_LOD, NF_COST_FACTOR, NF_COST_ABS_MS, NF_GAME_MAX_MS))
    parts_c, bad_c, judged = [], [], 0
    for lod in range(NF_MAX_LOD + 1):
        srv = [c for c in cost if c.get("lod") == lod]
        vv = [c for c in van if c.get("lod") == lod]
        nsv, nv = sum(c.get("count") or 0 for c in srv), sum(c.get("count") or 0 for c in vv)
        if nsv == 0:
            continue
        sp95 = max(_num(c, "p95") or 0.0 for c in srv)
        if nv < NF_COST_MIN_SAMPLES:
            parts_c.append("LOD %d: served p95 %.2f ms (%d), SE's own %d samples (too few)" % (lod, sp95, nsv, nv))
            continue
        judged += 1
        vp95 = max(_num(c, "p95") or 0.0 for c in vv)
        budget = NF_COST_FACTOR * vp95 + NF_COST_ABS_MS
        smax_g = max([_num(c, "max") or 0.0 for c in srv if c.get("thread") == "game"] or [0.0])
        gbudget = max(NF_GAME_MAX_MS, 2.0 * max([_num(c, "max") or 0.0 for c in vv if c.get("thread") == "game"] or [0.0]))
        parts_c.append("LOD %d: served p95 %.2f ms (%d, %.0f ns/voxel) vs SE's own %.2f ms (%d), budget %.2f; game thread max %.2f (budget %.1f)" % (
            lod, sp95, nsv, max(_num(c, "nsPerVoxel") or 0.0 for c in srv), vp95, nv, budget, smax_g, gbudget))
        if sp95 > budget or smax_g > gbudget:
            bad_c.append(lod)
    if judged == 0:
        row(rows, "S8a.NS.cost", "MISSING", "; ".join(parts_c) or "no served reads timed", thr_c, "the sample's field cost against SE's own generator", "ns")
    else:
        row(rows, "S8a.NS.cost", "FAIL" if bad_c else "PASS", "; ".join(parts_c), thr_c, "the sample's field cost per call and LOD against SE's own generator (same run)", "ns")
    reads, so = _num(st, "reads") or 0, _num(st, "servedOutside") or 0
    above = [c for c in cost if (c.get("lod") or 0) > NF_MAX_LOD]
    row(rows, "S8a.NS.scope", "PASS" if reads > 0 and so == 0 and not above else ("MISSING" if reads == 0 else "FAIL"),
        "%d served reads, %d outside the square, %d cost rows above LOD %d, %s errors" % (reads, so, len(above), NF_MAX_LOD, st.get("errors")),
        "0 served outside the square, 0 above LOD %d" % NF_MAX_LOD, "the sample path is taken only in its zone", "ns")
    # determinism
    sv = (L.events("ns_serve") or [{}])[-1]
    vec, rep_ = sv.get("vectors") or {}, sv.get("reply") or {}
    head = vec.get("head") or {}
    thr_d = "per serve box: the C# served bytes and SE's generator read = the twin's FNV (chunk and placement equal to the vectors')"
    if not rep_ or not rep_.get("results"):
        row(rows, "S8a.NS.determinism", "MISSING", "no de.s8a sample {serve} reply%s" % ("; vectors: " + str(vec.get("error")) if vec.get("error") else ""), thr_d,
            "the served bytes are a function of the pin", "ns")
    else:
        inputs = []
        if (head.get("chunk") or [None])[0] != rep_.get("chunkFnv"):
            inputs.append("chunk %s vs vectors %s" % (rep_.get("chunkFnv"), (head.get("chunk") or [None])[0]))
        if (head.get("sample") or [None])[0] != rep_.get("placementFnv"):
            inputs.append("placement %s vs vectors %s" % (rep_.get("placementFnv"), (head.get("sample") or [None])[0]))
        res = rep_.get("results") or []
        same = [r for r in res if r.get("fieldFnv") and r.get("fieldFnv") == r.get("providerFnv")]
        want = {tuple(v["box"]): v["fnv"] for v in vec.get("serve") or []}
        match = [r for r in res if want.get(tuple(r.get("box") or ())) == r.get("fieldFnv") == r.get("providerFnv")]
        detail = "%d boxes: C# = SE's generator read %d, = the twin %d" % (len(res), len(same), len(match))
        if inputs:
            row(rows, "S8a.NS.determinism", "MISSING" if len(same) == len(res) else "FAIL", detail + "; inputs differ (%s): rerun s8a_chunks.py vectors" % "; ".join(inputs),
                thr_d, "the served bytes are a function of the pin", "ns")
        else:
            row(rows, "S8a.NS.determinism", "PASS" if res and len(match) == len(res) else "FAIL", detail, thr_d,
                "the served bytes are a function of the pin: the game's equal the twin's", "ns")
    # entities
    parts_e, bad_e, nspots = [], [], 0
    for e in L.events("ns_entities"):
        cen, exp = e.get("census") or {}, e.get("expected")
        if not cen:
            continue
        nspots += 1
        recs, live = _fams(cen.get("records")), _fams(cen.get("live"))
        why = []
        for fam, n in recs.items():
            if fam == "slot":
                continue
            if n >= NS_ENTITY_MIN and live.get(fam, 0) < NS_ENTITY_SHARE * n:
                why.append("%s live %d of %d" % (fam, live.get(fam, 0), n))
        if isinstance(exp, dict):
            for fam in set(exp) | set(recs):
                a, b = recs.get(fam, 0), exp.get(fam, 0)
                if abs(a - b) > NS_RECORDS_TOL[0] * max(a, b) + NS_RECORDS_TOL[1]:
                    why.append("%s records %d vs the chunk's %d" % (fam, a, b))
        if (_num(cen, "slotPending") or 0) > 0:
            why.append("%d slots not rolled" % (_num(cen, "slotPending") or 0))
        if recs.get("slot", 0) >= 20 and (_num(cen, "slotFilled") or 0) == 0:
            why.append("no slot filled of %d" % recs.get("slot", 0))
        total = sum(n for f, n in recs.items() if f != "slot")
        parts_e.append("%s: records %s, live %s, slots %s filled / %s empty, life %s%s" % (
            e.get("name"), cen.get("records"), cen.get("live"), cen.get("slotFilled"), cen.get("slotEmpty"), (e.get("life") or {}).get("life"),
            "" if isinstance(exp, dict) else " (the chunk's own counts not read)"))
        if why or total == 0:
            bad_e.append("%s: %s" % (e.get("name"), "; ".join(why) or "no baked entity within the radius"))
    thr_e = ("per spot within %.0f m: SE's records per family = the chunk's own (+-%.0f %% + %d); Subnautica's live objects >= %.0f %% of the records per baked family "
             "(>= %d); every slot rolled, some filled" % (NS_ENTITY_R_A, NS_RECORDS_TOL[0] * 100, NS_RECORDS_TOL[1], NS_ENTITY_SHARE * 100, NS_ENTITY_MIN))
    if nspots == 0:
        row(rows, "S8a.NS.entities", "MISSING", "no census at a spot", thr_e, "the chunk's entities spawned through its transformed cell data", "ns")
    else:
        row(rows, "S8a.NS.entities", "FAIL" if bad_e else "PASS", "; ".join(bad_e + parts_e)[:900], thr_e, "the chunk's entities spawned through its transformed cell data", "ns")

# ---------------------------------------------------------------------------------------------------------------- ores as voxels (planet v2 section 13)
ORE_LOOK_CHANGED = 12.0     # S8a.NO.look: a pixel changed between our ore look and the template rock's when its RGB distance exceeds this
ORE_LOOK_SHARE = 0.02       #... the ore look covers at least this share of the frame (the drilled vein's walls and outcrop)
ORE_LOOK_MEAN = 20.0        #... and differs from the rock there by this mean RGB distance (fitted on the first capture
ORE_VOXEL_MIN = 50          # S8a.NO.voxel: at least this many ore surface voxels written in the session

def _ore_diff(L, a, b):
    """(changed share, mean RGB distance over the changed pixels) of two captures of the same view (None when unreadable)."""
    try:
        import numpy as np  # noqa: E402
        from PIL import Image  # noqa: E402
        ia = np.asarray(Image.open(os.path.join(L.folder, a)).convert("RGB").resize((320, 180)), np.float64)
        ib = np.asarray(Image.open(os.path.join(L.folder, b)).convert("RGB").resize((320, 180)), np.float64)
    except Exception:
        return None
    d = np.sqrt(((ia - ib) ** 2).sum(axis=-1))
    ch = d > ORE_LOOK_CHANGED
    return float(ch.mean()), float(d[ch].mean()) if ch.any() else 0.0

def ore_voxel_rows(L, rows):
    """Ores as voxels: S8a.NO.palette (Subnautica's ore slots filled with our textures), S8a.NO.voxel (every ore surface voxel the
 converters wrote carries its ore's type), S8a.NO.look (at a drilled vein our ore look differs from the rock's: the same view with the
 ore materials shown as the template rock), the paint test's candidates reported."""
    if not L.ran("no"):
        return
    looks = L.events("ore_look")
    lk = looks[-1] if looks else {}
    pal = lk.get("palette") or {}
    thr_p = "the WorldStreamer.Start prefix patched and applied, all nine ore slots ours (or the game's), >= 1 texture replaced per slot we fill"
    if not pal:
        row(rows, "S8a.NO.palette", "MISSING", "no ore look at a drilled vein (no drill in the session)", thr_p, "this mod's ore block types in Subnautica's palette", "no")
    else:
        filled, taken, tex = _num(pal, "filled") or 0, _num(pal, "taken") or 0, _num(pal, "texturesReplaced") or 0
        ok = bool(pal.get("patched")) and bool(pal.get("applied")) and filled + taken == 9 and filled >= 1 and tex >= filled
        row(rows, "S8a.NO.palette", "PASS" if ok else "FAIL", "patched %s, applied %s, template %s, filled %d, the game's %d, textures replaced %d; slots %s; template textures %s" % (
            pal.get("patched"), pal.get("applied"), pal.get("template"), filled, taken, tex, pal.get("slots"), str(pal.get("templateTextures"))[:200]), thr_p,
            "this mod's ore block types in Subnautica's palette", "no")
    ov = ((L.events("ore_voxels") or [{}])[-1].get("reply")) or {}
    thr_v = "every ore surface voxel written carries its ore's type (typed = surface), >= %d of them in the session" % ORE_VOXEL_MIN
    if not ov:
        row(rows, "S8a.NO.voxel", "MISSING", "no de.s8a orevoxels reply", thr_v, "the converters write SE's ores as their Subnautica ore types", "no")
    else:
        sv, tv = _num(ov, "surface") or 0, _num(ov, "typed") or 0
        row(rows, "S8a.NO.voxel", "PASS" if sv >= ORE_VOXEL_MIN and tv == sv else ("MISSING" if sv < ORE_VOXEL_MIN and tv == sv else "FAIL"),
            "%d of %d ore surface voxels carry their ore's type%s" % (tv, sv, " (S8a.M30 on)" if ov.get("m30") else ""), thr_v,
            "the converters write SE's ores as their Subnautica ore types", "no")
    caps = {c.get("as"): c for c in (lk.get("captures") or []) if c.get("png")}
    thr_l = ("at the drilled vein, our ore look against the same view as the template rock: >= %.0f %% of the frame changed (RGB distance > %.0f) by a mean "
             ">= %.0f" % (ORE_LOOK_SHARE * 100, ORE_LOOK_CHANGED, ORE_LOOK_MEAN))
    rock = str(_num(lk, "rock") or 16)
    if "ours" not in caps or "16" not in caps:
        row(rows, "S8a.NO.look", "MISSING", "no capture pair (ours, rock) at a drilled vein", thr_l, "the ore look on the remaining wall", "no")
        return
    d = _ore_diff(L, caps["ours"]["png"], caps["16"]["png"])
    if d is None:
        row(rows, "S8a.NO.look", "MISSING", "a capture could not be read", thr_l, "the ore look on the remaining wall", "no")
        return
    cands = []
    for k, c in sorted(caps.items()):
        if k in ("ours", "16"):
            continue
        dc = _ore_diff(L, c["png"], caps["16"]["png"])
        if dc is not None:
            cands.append("%s %s: %.1f %% by %.0f" % (k, c.get("result") or "", dc[0] * 100, dc[1]))
    row(rows, "S8a.NO.look", "PASS" if d[0] >= ORE_LOOK_SHARE and d[1] >= ORE_LOOK_MEAN else "FAIL",
        "ours vs the rock: %.1f %% of the frame changed by a mean %.0f; the paint test's candidates vs the rock: %s" % (d[0] * 100, d[1], "; ".join(cands) or "none"),
        thr_l, "the ore look on the remaining wall (the paint test's candidates reported)", "no")
    _ = rock

def _near_list(text):
    """Subnautica's "key:techType:x:y:z:scale;..." -> {key: (techType, x, y, z, scale)}."""
    out = {}
    for part in str(text or "").split(";"):
        f = part.split(":")
        if len(f) < 6:
            continue
        try:
            out[f[0]] = (f[1], float(f[2]), float(f[3]), float(f[4]), float(f[5]))
        except ValueError:
            continue
    return out

def no_rows(L, rows):
    """S8a.NO (planet v2 R8): per site SE's records within 100 m, SE's own detector (MyDepositQuery) and Subnautica's deposits read
 right after; the drill on the biggest undrilled outcrop; Subnautica's drill on the nearest deposit."""
    if not L.ran("no"):
        return
    ore_density_rows(L, rows)
    sites = L.events("no_site")
    th = {"cover": "every SE outcrop record within %g m of the camera has a Subnautica deposit with its key" % NO_COVER_M,
          "vein": "every Subnautica deposit near the camera has an SE record whose cell SE's own detector reports for that ore",
          "agree": ("undrilled outcrops: this mod's centroid = SE's detector's within 1 m, and the deposit within %g m of it horizontally or, an edge "
                    "exposure (the cell's exposed ore away from its buried centroid), on its own ore: SE's material 1 m inside the anchor = the ore" % NO_AGREE_M),
          "shrink": "the drilled outcrop's volume falls and its deposit shrinks (scale) or goes",
          "immune": "%s of Subnautica's drill hits leave the deposit's health unchanged" % "60"}
    what = "ores you can see (planet v2 R8)"
    if not sites:
        for k in ("cover", "vein", "agree", "shrink", "immune"):
            row(rows, "S8a.NO." + k, "MISSING", "no ore site judged" + ("; " + str((L.events("no_skip") or [{}])[0].get("reason")) if L.events("no_skip") else ""), th[k], what, "no")
        return
    miss_cover, miss_vein, bad_agree = [], [], []
    n_rec = n_dep = n_agree = n_edge = 0
    for st in sites:
        recs = {r.get("key"): r for r in st.get("records") or []}
        dep = _near_list((st.get("deposits") or {}).get("near"))
        cam = st.get("deposits") or {}
        cx, cy, cz = _num(cam, "camX"), _num(cam, "camY"), _num(cam, "camZ")
        det = st.get("detect") or []
        cells = {}
        for d in det:
            c = d.get("cell") or []
            cells.setdefault((str(d.get("body")), ",".join(str(int(v)) for v in c), d.get("ore")), d)
        for key, r in recs.items():
            if cx is not None and math.sqrt((r["x"] - cx) ** 2 + (r["y"] - cy) ** 2 + (r["z"] - cz) ** 2) > NO_COVER_M:
                continue
            n_rec += 1
            if key not in dep:
                miss_cover.append("%s %s (%.0f, %.0f)" % (key[-6:], r.get("ore"), r["x"], r["z"]))
        for key, (tech, x, y, z, scale) in dep.items():
            n_dep += 1
            r = recs.get(key)
            body, cell = (str(r.get("cell") or "").split(":") + [""])[:2] if r else ("", "")
            d = cells.get((body, cell, r.get("ore"))) if r else None
            if r is None or d is None:
                miss_vein.append("%s %s (%.0f, %.0f)%s" % (key[-6:], tech, x, z, "" if r else " without a record"))
                continue
            if not r.get("drilled"):
                n_agree += 1
                hz = math.hypot(d["x"] - x, d["z"] - z)
                ours = math.hypot((r.get("detX") or 0.0) - d["x"], (r.get("detZ") or 0.0) - d["z"]) if r.get("detX") is not None else 0.0
                on_ore = (st.get("anchorOre") or {}).get(key) == r.get("ore")
                if ours > 1.0:
                    bad_agree.append("%s %s centroid %.1f m from SE's" % (key[-6:], r.get("ore"), ours))
                elif hz > NO_AGREE_M and on_ore:
                    n_edge += 1
                elif hz > NO_AGREE_M:
                    bad_agree.append("%s %s %.1f m, exposed %s m2, anchor on %s" % (key[-6:], r.get("ore"), hz, r.get("exposedM2"), (st.get("anchorOre") or {}).get(key, "?")))
    row(rows, "S8a.NO.cover", "MISSING" if n_rec == 0 else ("PASS" if not miss_cover else "FAIL"),
        "%d/%d records with a deposit over %d sites%s" % (n_rec - len(miss_cover), n_rec, len(sites), ("; missing " + ", ".join(miss_cover[:5])) if miss_cover else ""), th["cover"], what, "no")
    row(rows, "S8a.NO.vein", "MISSING" if n_dep == 0 else ("PASS" if not miss_vein else "FAIL"),
        "%d/%d deposits in a cell SE's detector reports%s" % (n_dep - len(miss_vein), n_dep, ("; not: " + ", ".join(miss_vein[:5])) if miss_vein else ""), th["vein"], what, "no")
    row(rows, "S8a.NO.agree", "MISSING" if n_agree == 0 else ("PASS" if not bad_agree else "FAIL"),
        "%d/%d undrilled deposits within %g m of the detector, %d edge exposures on their ore%s" % (
            n_agree - len(bad_agree) - n_edge, n_agree, NO_AGREE_M, n_edge, ("; off: " + ", ".join(bad_agree[:5])) if bad_agree else ""),
        th["agree"], what, "no")
    dr = next((st.get("drill") for st in sites if st.get("drill")), None)
    if not dr:
        row(rows, "S8a.NO.shrink", "MISSING", "no drill on an outcrop", th["shrink"], what, "no")
    else:
        key = dr.get("key")
        before = dr.get("before") or {}
        after = next((r for r in dr.get("after") or [] if r.get("key") == key), None)
        dep_before = None
        for st in sites:
            if st.get("drill"):
                dep_before = _near_list((st.get("deposits") or {}).get("near")).get(key)
        dep_after = _near_list(dr.get("depositsAfter")).get(key)
        vol_ok = after is None or (after.get("volumeM3") or 0) < (before.get("volumeM3") or 0)
        dep_ok = dep_after is None or (dep_before is not None and dep_after[4] < dep_before[4])
        local = "" if before.get("localM3") is None else "; ore near the anchor %s -> %s m3" % (before.get("localM3"), after.get("localM3") if after else "gone")
        row(rows, "S8a.NO.shrink", "PASS" if vol_ok and dep_ok else "FAIL", "volume %s -> %s m3, deposit scale %s -> %s%s" % (
            before.get("volumeM3"), after.get("volumeM3") if after else "gone", dep_before[4] if dep_before else "-", dep_after[4] if dep_after else "gone", local), th["shrink"], what, "no")
    im = next((st.get("immune") for st in sites if st.get("immune") is not None), None)
    if not im or im.get("ok") is False:
        row(rows, "S8a.NO.immune", "MISSING", "no immune test (%s)" % ((im or {}).get("error") or "no deposit"), th["immune"], what, "no")
    else:
        # SN builds before a test run sent these as strings in the machine's culture ("1600", "89,08162")
        def _n(k):
            v = im.get(k)
            if isinstance(v, str):
                try:
                    return float(v.replace(",", "."))
                except ValueError:
                    return None
            return _num(im, k)
        hb, ha, calls, hits = _n("healthBefore"), _n("healthAfter"), _n("calls"), _n("hits")
        good = hb is not None and ha is not None and abs(hb - ha) < 1e-3 and calls == hits and hits
        row(rows, "S8a.NO.immune", "PASS" if good else "FAIL", "%s hits (%s calls): health %s -> %s" % (hits, calls, hb, ha), th["immune"], what, "no")

# planet v2 R8 (S8a.N10.oredensity): SE's detector cells of one ore within this merge into one deposit; the planet's table per class
# (docs/S8A-PLANET-V2.md section 2: deposits per km2 of the class's depth band) and the judged band; the crater's cap from the rules
ORE_DENSITY_MERGE_M = 64.0
ORE_DENSITY_CLASSES = (("common", ("Iron_02", "Nickel_01", "Silicon_01"), 0.0, 200.0, 14.4),
                       ("medium", ("Cobalt_01", "Magnesium_01"), 100.0, 300.0, 4.4),
                       ("uncommon", ("Silver_01", "Gold_01"), 300.0, 600.0, 1.5))
ORE_DENSITY_FACTOR = 2.0          # a class's measured density within x0.5.. x2 of the table
ORE_DENSITY_MIN_POINTS = 3        # a class is judged over at least this many points in its band
ORE_DENSITY_CAP_SLACK = 1.5       # the crater: deposits per km2 within the cap x this (a detector circle meets up to 4 cap cells)

def _deposits_of(detect, merge_m=ORE_DENSITY_MERGE_M):
    """SE's detector list -> deposits (ore, x, z): cells of one ore within merge_m (horizontally) chain into one."""
    pts = [(str(d.get("ore")), float(d["x"]), float(d["z"])) for d in detect or [] if isinstance(d, dict) and d.get("x") is not None]
    parent = list(range(len(pts)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    for i in range(len(pts)):
        for j in range(i + 1, len(pts)):
            if pts[i][0] == pts[j][0] and math.hypot(pts[i][1] - pts[j][1], pts[i][2] - pts[j][2]) <= merge_m:
                parent[find(i)] = find(j)
    groups = {}
    for i, p in enumerate(pts):
        groups.setdefault(find(i), []).append(p)
    return [(g[0][0], sum(p[1] for p in g) / len(g), sum(p[2] for p in g) / len(g)) for g in groups.values()]

def ore_density_rows(L, rows):
    """S8a.N10.oredensity (planet v2 R8): the crater's ore deposits per km2 within the rules' cap (cube rule off), the planet's per
 class within x0.5-x2 of the vein table in the class's depth band; design 3 (no rules file, no vein rule) is information."""
    if not L.ran("no"):
        return
    ev = [e for e in L.events("no_density") if isinstance(e.get("detect"), list)]
    thr = ("the crater: deposits per km2 <= the rules' cap x %.1f with the cube rule off; the planet: each class's deposits per km2 in its "
           "depth band within x%.1f of the table (common 14.4, medium 4.4, uncommon 1.5; >= %d points)" % (ORE_DENSITY_CAP_SLACK, ORE_DENSITY_FACTOR, ORE_DENSITY_MIN_POINTS))
    what = "the ore density (planet v2 R8)"
    if not ev:
        row(rows, "S8a.N10.oredensity", "MISSING", "no detector point read", thr, what, "no")
        return
    area = math.pi * (float(ev[0].get("radius") or 300.0) / 1000.0) ** 2
    rule = next((e.get("rule") for e in ev if e.get("rule")), None) or {}
    cube, cap = rule.get("craterCubeRule"), float(rule.get("craterDepositsPerKm2") or 0.0)
    rules_text = str(rule.get("planetRules") or "")
    rules_on = "defaults" not in rules_text and rules_text != ""
    crater = [e for e in ev if e.get("zone") == "crater"]
    planet = [e for e in ev if e.get("zone") == "planet"]
    notes, fails = [], 0
    if crater:
        n = sum(len(_deposits_of(e["detect"])) for e in crater)
        dens = n / (area * len(crater))
        if rules_on and cap > 0:
            good = dens <= cap * ORE_DENSITY_CAP_SLACK and cube is False
            fails += 0 if good else 1
            notes.append("crater %.1f deposits/km2 over %d points (cap %g, cube rule %s)%s" % (dens, len(crater), cap, "on" if cube else "off", "" if good else " FAIL"))
        else:
            notes.append("crater %.1f deposits/km2 over %d points (cube rule %s, no cap: the design-3 rules, information)" % (dens, len(crater), "on" if cube is not False else "off"))
    judged = 0
    for name, ores, d0, d1, table in ORE_DENSITY_CLASSES:
        pts = [e for e in planet if e.get("seabedY") is not None and d0 <= -float(e["seabedY"]) <= d1]
        if len(pts) < ORE_DENSITY_MIN_POINTS:
            notes.append("%s: %d points in %g-%g m" % (name, len(pts), d0, d1))
            continue
        n = sum(1 for e in pts for (o, _x, _z) in _deposits_of(e["detect"]) if o in ores)
        dens = n / (area * len(pts))
        ok = table / ORE_DENSITY_FACTOR <= dens <= table * ORE_DENSITY_FACTOR
        if rules_on:
            judged += 1
            fails += 0 if ok else 1
        notes.append("%s %.1f/km2 (table %g) over %d points%s" % (name, dens, table, len(pts), "" if ok or not rules_on else " FAIL"))
    if not rules_on:
        row(rows, "S8a.N10.oredensity", "INFO", "design-3 rules (no %s): %s" % ("de-planet-rules.txt", "; ".join(notes)), thr, what, "no")
        return
    row(rows, "S8a.N10.oredensity", "PASS" if fails == 0 and (crater or judged) else ("FAIL" if fails else "MISSING"), "; ".join(notes), thr, what, "no")

def nd_props_rows(L, rows):
    """S8a.ND.props (planet v2 R9): the test props over nd's crater pass, all hidden after the carve, each family as the rule sees it."""
    if not L.ran("nd"):
        return
    ev = L.events("nd_props")
    thr = "every test prop (Pink Cap, scrap metal, limestone outcrop, coral plate, big coral tubes) placed over the pass is hidden after the carve"
    before = next((e for e in ev if e.get("phase") == "before"), None)
    after = next((e for e in ev if e.get("phase") == "after"), None)
    if not before or not after:
        row(rows, "S8a.ND.props", "MISSING", "no test props", thr, "props over holes (flora v2)", "nd")
        return

    def parse(e):
        out = {}
        for part in str((e.get("reply") or {}).get("list") or "").split(";"):
            f = part.split(":")
            if len(f) >= 4:
                # SN builds after: field 5 = the rule's view of the prop (candidate, in the registry, collisions on,
 # kinematic, cell level, ground now, ground in the original)
                out[f[0]] = (f[1], f[2] == "1", f[3], f[5] if len(f) > 5 else "")
        return out
    b, a = parse(before), parse(after)
    live = [k for k, v in b.items() if v[0] != "-"]
    shown = [k for k in live if not (a.get(k) or ("", False, "", ""))[1]]
    if not live:
        row(rows, "S8a.ND.props", "MISSING", "no test prop came up (%s)" % (before.get("reply") or {}).get("list"), thr, "props over holes (flora v2)", "nd")
        return
    row(rows, "S8a.ND.props", "PASS" if not shown else "FAIL", "%d/%d hidden after the carve%s" % (len(live) - len(shown), len(live),
        ("; still shown: " + ", ".join("%s (%s%s)" % (k, b[k][0], ("; after: " + a[k][3]) if a.get(k) and a[k][3] else "") for k in shown)) if shown else ""),
        thr, "props over holes (flora v2)", "nd")

# ---------------------------------------------------------------------------------------------------------------- nl (the look)
NL_SIZE = (320, 180)            # every capture is compared at this size
NL_BLOCK = 8                    # flat-seabed blocks (px at NL_SIZE)
NL_FLAT_GRAD = 3.0              # a block is flat when its mean |grey gradient| is under this (grey levels per px)
NL_SIL_MIN_AREA = 0.002         # a silhouette: a dark component of the upper third covering at least this share of it
NL_TOL_FACTOR = 3.0             # tolerance = the spawn's own spread x this
NL_FLOORS = {"flat": 0.10, "silhouettes": 2.0, "histogram": 0.15, "entities": 5.0, "luma": 15.0, "chroma": 0.04}
# a test run (nl 20261024-051730: the far frames at L 4-23 against the spawn's 83-105, no plant, no creature, yet 5/8 "read like the
# spawn": the terraces' edges counted as texture and silhouettes): the brightness, the colour and the life round the camera must each
# match (one-sided where less is the failure), and 2 of the 3 texture metrics; silhouettes only above the horizon line
NL_HORIZON_FRAC = 0.25          # pitch -15 deg with Subnautica's 60 deg vertical field of view: the horizon about 27 % from the top
NL_LIFE_SHARE = 0.5             # the far camera's plants and creatures within 40 m: at least this share of the spawn's

NL_LIT_DEPTH_M = 450.0          # the launcher's: far points only on seabeds no deeper (Subnautica's noon light)
NL_EXCLUDE_BIOMES = ("void",)
NL_BLACK_LUMA = 3.0             # mean luma (0-255) under this: no picture

def _luma(path):
    try:
        from PIL import Image  # noqa: E402
        im = Image.open(path).convert("L").resize((160, 90))
        px = list(im.getdata())
        return sum(px) / float(len(px)) if px else None
    except Exception:
        return None

def look_metrics(path):
    """(flat, silhouettes, histogram) of one capture: the share of 8 px blocks of the lower two thirds whose mean grey gradient is
 under NL_FLAT_GRAD (flat seabed or empty water), the dark connected components of the upper third (Otsu split) covering at
 least NL_SIL_MIN_AREA of it, the 8 x 8 x 8 RGB histogram (normalised). None without Pillow or the file."""
    try:
        from PIL import Image  # noqa: E402
        import numpy as np  # noqa: E402
        im = np.asarray(Image.open(path).convert("RGB").resize(NL_SIZE), dtype=np.float64)
    except Exception:
        return None
    g = im.mean(axis=2)
    H, W = g.shape
    grad = np.abs(np.diff(g, axis=1))[:-1, :] + np.abs(np.diff(g, axis=0))[:, :-1]
    low = grad[H // 3:, :]
    nb_y, nb_x = low.shape[0] // NL_BLOCK, low.shape[1] // NL_BLOCK
    blocks = low[:nb_y * NL_BLOCK, :nb_x * NL_BLOCK].reshape(nb_y, NL_BLOCK, nb_x, NL_BLOCK).mean(axis=(1, 3))
    flat = float((blocks < NL_FLAT_GRAD).mean()) if blocks.size else 1.0
    up = g[:int(H * NL_HORIZON_FRAC), :]          # a test run: objects above the horizon, not the terrain's edges below it
 # Otsu threshold
    hist, edges = np.histogram(up, bins=64, range=(0.0, 256.0))
    tot = hist.sum()
    best, thr = -1.0, float(up.mean())
    cum, cum_m = 0.0, 0.0
    mids = 0.5 * (edges[:-1] + edges[1:])
    mean_all = float((hist * mids).sum()) / max(1.0, float(tot))
    for k in range(64):
        cum += hist[k]
        cum_m += hist[k] * mids[k]
        if cum == 0 or cum == tot:
            continue
        w0 = cum / tot
        m0 = cum_m / cum
        m1 = (mean_all * tot - cum_m) / (tot - cum)
        var = w0 * (1.0 - w0) * (m0 - m1) ** 2
        if var > best:
            best, thr = var, edges[k + 1]
    dark = up < thr
    sil = 0
    if dark.any() and best > 25.0:          # a uniform sky has no split worth counting
        seen = np.zeros(dark.shape, bool)
        min_area = NL_SIL_MIN_AREA * dark.size
        hh, ww = dark.shape
        for y0 in range(hh):
            for x0 in range(ww):
                if not dark[y0, x0] or seen[y0, x0]:
                    continue
                stack, area = [(y0, x0)], 0
                seen[y0, x0] = True
                while stack:
                    y, x = stack.pop()
                    area += 1
                    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        yy, xx = y + dy, x + dx
                        if 0 <= yy < hh and 0 <= xx < ww and dark[yy, xx] and not seen[yy, xx]:
                            seen[yy, xx] = True
                            stack.append((yy, xx))
                if area >= min_area:
                    sil += 1
    q = np.clip((im / 32.0).astype(int), 0, 7)
    hist3 = np.bincount((q[..., 0] * 64 + q[..., 1] * 8 + q[..., 2]).ravel(), minlength=512).astype(np.float64)
    hist3 /= max(1.0, hist3.sum())
    # a test run: the mean luma (0-255) and the mean chromaticity (r, b over r + g + b) of the frame
    luma = float((0.299 * im[..., 0] + 0.587 * im[..., 1] + 0.114 * im[..., 2]).mean())
    ssum = im.sum(axis=2) + 1e-6
    chroma = (float((im[..., 0] / ssum).mean()), float((im[..., 2] / ssum).mean()))

    # the frame (the seabed) in a red hue (r > 1.3 g, r > 1.15 b, r > 40)
    low = im[int(im.shape[0] * 0.4):]
    cover = float(((low[..., 0] > 1.3 * low[..., 1]) & (low[..., 0] > 1.15 * low[..., 2]) & (low[..., 0] > 40)).mean())
    return {"flat": flat, "silhouettes": float(sil), "histogram": hist3, "luma": luma, "chroma": chroma, "cover": cover}

def _chi2(a, b):
    import numpy as np  # noqa: E402
    return float(0.5 * np.sum((a - b) ** 2 / (a + b + 1e-12)))

def nl_rows(L, rows):
    """S8a.NL.
 things a viewer notices. One-sided where emptiness is the failure: the flat share may not exceed the spawn's by more than the
 tolerance, silhouettes and entities may not fall below it; the colour histogram's distance to the spawn's mean stays within it.
 Tolerance = the spawn's own spread (max - min over its captures) x NL_TOL_FACTOR, at least the metric's floor; a capture passes
 on 3 of the 4."""
    if not L.ran("nl"):
        return
    caps = L.events("look_capture")
    thr = ("every far capture like the spawn's captures at its height: mean luma and chroma within tolerance and plants plus creatures within 40 m at "
           "least half the spawn's, and 2 of 3 texture metrics (flat share, silhouettes above the horizon, colour histogram)")
    if not caps:
        row(rows, "S8a.NL.look", "MISSING", "no look_capture events", thr, "the look: far biomes against the spawn", "nl")
        return
    import numpy as np  # noqa: E402
    for c in caps:
        path = c.get("png")
        if path and not os.path.isabs(path):
            path = os.path.join(L.folder, path)
        c["_m"] = look_metrics(path) if path else None
    if any(c["_m"] is None for c in caps if c.get("written")):
        row(rows, "S8a.NL.look", "MISSING", "a capture could not be read (Pillow or the file)", thr, "the look: far biomes against the spawn", "nl")
        return
    # a test run (nl 20261024-033738: 16 black far frames): a far point on a seabed Subnautica's noon does not light (design 3's void at
 # -3,000 m, plains below NL_LIT_DEPTH_M) is left out, and a black frame (no picture presented) makes the row MISSING, not FAIL
    dark = [c for c in caps if c.get("kind") == "far" and (c.get("biome") in NL_EXCLUDE_BIOMES or (c.get("seabedY") is not None and float(c["seabedY"]) < -NL_LIT_DEPTH_M))]
    caps = [c for c in caps if c not in dark]
    # planet v2 route B: with the sample's captures in the session the row judges them; the design-3 point beside them is the sheet's

    if any(str(c.get("biome") or "").startswith("sample:") for c in caps):
        caps = [c for c in caps if c.get("kind") != "far" or str(c.get("biome") or "").startswith("sample:")]
    black = []
    for c in caps:
        lum = c.get("luma")
        if lum is None and c["_m"] is not None and c.get("png"):
            lum = _luma(os.path.join(L.folder, c["png"]) if not os.path.isabs(c["png"]) else c["png"])
        if c.get("black") or (lum is not None and lum < NL_BLACK_LUMA):
            black.append(c)
    dark_note = ("; %d far captures on unlit seabeds left out (%s)" % (len(dark), ", ".join(sorted({"%s %.0f m" % (c.get("biome"), c.get("seabedY") or 0) for c in dark})))) if dark else ""
    if black:
        row(rows, "S8a.NL.look", "MISSING", "no picture (black frame) at %d of %d captures: %s%s" % (
            len(black), len(caps), ", ".join("%s %s h%g (tries %s, zone %s)" % (c.get("kind"), c.get("biome"), c.get("height") or 0, c.get("tries"), ((c.get("chart") or {}).get("zone")))
                                             for c in black[:6]), dark_note), thr, "the look: far biomes against the spawn", "nl")
        return
    results, failed = [], []

    def life_of(c):
        # a test run: plants and creatures within 40 m (de.s8a life); older sessions: the spawns and deposits counted ("entities")
        lf = c.get("life")
        return float(lf.get("life")) if isinstance(lf, dict) and lf.get("life") is not None else float(c.get("entities") or 0)
    # ns 20261024-113052 (coordinator): a Grassy Plateaus sample never histogram-matches the kelp-forest spawn; with reference captures
 # (kind "ref": the real map's own Grassy Plateaus, the chunk's native spots, same heights, noon) the far captures are judged against them
    ref_kind = "ref" if sum(1 for c in caps if c.get("kind") == "ref" and c["_m"] is not None) >= 2 else "spawn"
    for h in sorted({c.get("height") for c in caps}):
        sp = [c for c in caps if c.get("height") == h and c.get("kind") == ref_kind and c["_m"] is not None]
        far = [c for c in caps if c.get("height") == h and c.get("kind") == "far" and c["_m"] is not None]
        if len(sp) < 2 or not far:
            continue
        ref = {k: float(np.mean([c["_m"][k] for c in sp])) for k in ("flat", "silhouettes", "luma")}
        ref["life"] = float(np.mean([life_of(c) for c in sp]))
        ref["cover"] = float(np.mean([c["_m"].get("cover", 0.0) for c in sp]))
        hm = np.mean([c["_m"]["histogram"] for c in sp], axis=0)
        cm = (float(np.mean([c["_m"]["chroma"][0] for c in sp])), float(np.mean([c["_m"]["chroma"][1] for c in sp])))
        tol = {k: max(NL_FLOORS[k], NL_TOL_FACTOR * (max(c["_m"][k] for c in sp) - min(c["_m"][k] for c in sp))) for k in ("flat", "silhouettes", "luma")}
        tol["histogram"] = max(NL_FLOORS["histogram"], NL_TOL_FACTOR * max(_chi2(c["_m"]["histogram"], hm) for c in sp))
        cd = lambda c: math.hypot(c["_m"]["chroma"][0] - cm[0], c["_m"]["chroma"][1] - cm[1])  # noqa: E731
        tol["chroma"] = max(NL_FLOORS["chroma"], NL_TOL_FACTOR * max(cd(c) for c in sp))
        for c in far:
            m = c["_m"]
            must = {"luma": abs(m["luma"] - ref["luma"]) <= tol["luma"], "chroma": cd(c) <= tol["chroma"], "life": life_of(c) >= NL_LIFE_SHARE * ref["life"]}
            if ref_kind == "ref" and ref["cover"] >= NL_COVER_MIN:
                # the biome's ground cover (the red grass carpet) at least half the real plateau's own
                must["cover"] = m.get("cover", 0.0) >= NL_COVER_SHARE * ref["cover"]
            tex = {"flat": m["flat"] <= ref["flat"] + tol["flat"], "silhouettes": m["silhouettes"] >= ref["silhouettes"] - tol["silhouettes"],
                   "histogram": _chi2(m["histogram"], hm) <= tol["histogram"]}
            good = all(must.values()) and sum(tex.values()) >= 2
            results.append(good)
            if not good:
                failed.append("%s %.0f km h%g: luma %.0f (spawn %.0f +-%.0f), chroma %.3f (<= %.3f), life %.0f (spawn %.0f), cover %.3f (spawn %.3f), flat %.2f (spawn %.2f +%.2f), silhouettes %g (spawn %.1f), histogram %.2f (<= %.2f)" % (
                    c.get("biome"), (c.get("rho") or 0) / 1000.0, h, m["luma"], ref["luma"], tol["luma"], cd(c), tol["chroma"], life_of(c), ref["life"], m.get("cover", 0.0), ref["cover"],
                    m["flat"], ref["flat"], tol["flat"], m["silhouettes"], ref["silhouettes"], _chi2(m["histogram"], hm), tol["histogram"]))
    if not results:
        row(rows, "S8a.NL.look", "MISSING", "no height with >= 2 spawn captures and a far capture", thr, "the look: far biomes against the spawn", "nl")
        return
    row(rows, "S8a.NL.look", "PASS" if not failed else "FAIL", "%d/%d far captures read like the %s%s%s" % (
        sum(results), len(results), "real map's Grassy Plateaus (reference captures)" if ref_kind == "ref" else "spawn",
        ("; " + "; ".join(failed[:4]).replace("(spawn ", "(ref " if ref_kind == "ref" else "(spawn ")) if failed else "", dark_note), thr,
        "the look: far biomes against the spawn (or the reference)", "nl")

def evaluate(folder):
    L = Logs(folder)
    rows = []
    for fn in (c1_rows, c2_rows, c2g_rows, c3_rows, n1_rows, n2_rows, n3_rows, n4_rows, n5_rows, n6_rows, n7_rows, n8_rows, n9_rows, n10_rows, n10c_rows, n11_rows, nd_rows, nf_rows,
               nl_rows, no_rows, nd_props_rows, ns_rows, ore_voxel_rows):
        try:
            fn(L, rows)
        except Exception as e:     # one check's bug must not hide the others
            row(rows, "S8a.%s" % fn.__name__.replace("_rows", "").upper(), "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:120], fn.__name__)
    for m in L.start.get("mutants") or []:
        want = MUTANT_RED.get(m, ())
        also = MUTANT_ALSO_RED.get(m, ())
        missing_ok = MUTANT_ALSO_MISSING.get(m, ())
        got = [r for r in rows if r["id"] in want]
        for r in got:
            r["required"] = False      # judged by the.red row
        for r in rows:
            if r["id"] in also or (r["id"] in missing_ok and r["status"] == "MISSING"):
                r["required"] = False  #  the rows the mutant leaves without data are not the table's verdict
        red = [r["id"] for r in got if r["status"] == "FAIL"]
        status = "MISSING" if not got else ("PASS" if len(red) >= 1 and all(r["status"] == "FAIL" for r in got if r["status"] != "MISSING") else "FAIL")
        row(rows, "%s.red" % m, status, "red: %s" % (",".join(red) or "none"), "red: %s" % ",".join(want),
            "seeded failure %s turns its rows red" % m, "mutants")
    return rows

def table(rows):
    return C.table(rows)

def write_results(folder, rows, strict=True, extra=None, name="s8a") -> bool:
    ok = C.verdict(rows, strict)
    res = {"tool": "analyze_s8a", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "%s-results.json" % name), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "%s-table.txt" % name), "w", encoding="utf-8") as f:
        f.write(C.table(rows) + "\n")
    return ok

# ----------------------------------------------------------------------------------------------------------------
# self-test: synthetic sessions in the log contract
# ----------------------------------------------------------------------------------------------------------------

SYNTH_SITES = [{"i": 0, "x": 40.0, "z": 40.0, "path": "sphere", "seam": False}, {"i": 1, "x": 85.0, "z": 35.0, "path": "sphere", "seam": False},
               {"i": 2, "x": 35.0, "z": 85.0, "path": "sphere", "seam": False}, {"i": 3, "x": 90.0, "z": 90.0, "path": "hand", "seam": False},
               {"i": 4, "x": 62.0, "z": 120.0, "path": "hand", "seam": False}, {"i": 5, "x": 0.0, "z": 70.0, "path": "sphere", "seam": True}]
SEABED = -17.2
RADIUS = 2.5

def grid_columns(cx, cz, half, n):
    cols = []
    for ix in range(n):
        for iz in range(n):
            cols.append((round(cx - half + 2.0 * half * ix / (n - 1), 3), round(cz - half + 2.0 * half * iz / (n - 1), 3)))
    return cols

def ring_columns(cx, cz, r, n):
    cols = []
    for k in range(n):
        rr = r * (1.5 + 0.5 * (k % 4))
        a = 2 * math.pi * k / n
        cols.append((round(cx + rr * math.cos(a), 3), round(cz + rr * math.sin(a), 3)))
    return cols

class Synth:
    """A synthetic S8a-1 session: a flat seabed, six holes, the rows and events of both games and the launcher."""

    def __init__(self, folder, mutant=None, parts=("n1", "n2", "n3", "n5", "n6", "teardown")):
        self.folder, self.mutant, self.parts = folder, mutant, list(parts)
        os.makedirs(folder, exist_ok=True)
        self.sess, self.se, self.sn = [], [], []
        self.tick = 1000
        self.frame = 500
        self.seq = 0
        self.applied = 0
        self.c7 = {}
        self.root = r"C:\Users\user\AppData\Local\DeepEngineers\terrain"
        self.world = r"C:\Users\user\AppData\Roaming\SpaceEngineers\Saves\1\DE Lab S8a"
        self.store_key = "00c0ffee00c0ffee"
        self.sites = [dict(s) for s in SYNTH_SITES]
        self.holes = {}         # i -> (cx, cy, cz, rh, rv) as SE sees it
        self.sn_holes = {}      # i -> as Subnautica shows it (None: no hole there)
        self.writes = []
        self.loads = []
        self.cells = 0
        self.saved_hash = None
        self.chart_seq, self.snap_frames, self.chart_changes = 0, 0, 0

    # ---- rows ----
    def se_row(self, ev, **kw):
        self.se.append(dict({"t": "2026-10-23T10:00:00.000Z", "ev": ev}, **kw))

    def sn_row(self, ev, **kw):
        self.sn.append({"t": "2026-10-23T10:00:00.000Z", "event": ev, "ev": ev, "data": dict(kw)})

    def ev(self, ev, **kw):
        self.sess.append(dict({"ev": ev, "qpc": self.tick * 1000}, **kw))

    def part(self, name, phase):
        self.ev("part", name=name, phase=phase, tick=self.tick)

    # ---- the terrain model ----
    def se_surface(self, x, z):
        y = SEABED
        for h in self.holes.values():
            y = min(y, hole_depth(h[0], h[1], h[2], h[3], h[4], x, z, SEABED))
        return y

    def se_cast(self, x, z, seam_original=False):
        y = SEABED
        for i, h in self.holes.items():
            if seam_original and self.sites[i]["seam"] and x >= -2.0:
                continue        # S8a.M14: the overlap's other tile keeps its original surface (the un-mirrored side)
            y = min(y, hole_depth(h[0], h[1], h[2], h[3], h[4], x, z, SEABED))
        return y

    def sn_surface(self, x, z):
        # S8a.M2: the converter samples at the voxel corner, so every recomputed voxel of the +-5 m stencil
 # box around a hole shows the field half a metre away: inside that box the whole surface, the seabed included, is
 # shifted by (0.5, 0.5, 0.5); the columns outside keep the base
        if self.mutant == "S8a.M2":
            for i, h in self.sn_holes.items():
                if h is None:
                    continue
                if abs(x - h[0]) <= 5.0 and abs(z - h[2]) <= 5.0:
                    y = SEABED + 0.5
                    for g in self.sn_holes.values():
                        if g is not None:
                            y = min(y, hole_depth(g[0] + 0.5, g[1] + 0.5, g[2] + 0.5, g[3], g[4], x, z, SEABED + 0.5))
                    return y + 0.02 * math.sin(x * 7.1 + z * 3.3)
        y = SEABED
        for h in self.sn_holes.values():
            if h is None:
                continue
            y = min(y, hole_depth(h[0], h[1], h[2], h[3], h[4], x, z, SEABED))
        return y + 0.02 * math.sin(x * 7.1 + z * 3.3)      # mesh noise

 # ---- rays ----
    def se_rays(self, i, kind, when, cols, before=False):
        res = []
        for x, z in cols:
            sy = SEABED if before else self.se_surface(x, z)
            cy = SEABED if before else self.se_cast(x, z, seam_original=self.mutant == "S8a.M14")
            res.append({"x": x, "z": z, "storageY": sy, "castY": cy, "tile": "1,1,1", "castTile": "1,1,1" if x < 0 else "2,1,1"})
        self.se_row("s8a.rays", tick=self.tick, label="%s.%d.%s.%s" % (when, i, kind, "se"), columns=len(cols), storageHits=len(cols), castHits=len(cols), hits=res)
        self.ev("rays", label="%s.%d.%s.se" % (when, i, kind), side="se", site=i, kind=kind, when=when, columns=[[x, z, SEABED] for x, z in cols], results=res, state="done")

    def sn_rays(self, i, kind, when, cols):
        res = []
        for k, (x, z) in enumerate(cols):
            y = self.sn_surface(x, z)
            res.append({"i": k, "x": x, "z": z, "yHint": SEABED, "state": "ok", "hit": True, "y": y})
            self.sn_row("s4.probe", batch="%s.%d.%s.sn" % (when, i, kind), i=k, x=x, z=z, yHint=SEABED, state="ok", hit=True, y=y)
        self.ev("rays", label="%s.%d.%s.sn" % (when, i, kind), side="sn", site=i, kind=kind, when=when, columns=[[x, z, SEABED] for x, z in cols], results=res, state="done")

    def columns(self, i, kind):
        s = self.sites[i]
        if kind == "footprint":
            return grid_columns(s["x"], s["z"], 1.2 * RADIUS, 9)
        if kind in ("n3", "seam"):
            return grid_columns(s["x"], s["z"], 0.7 * RADIUS / math.sqrt(2.0), 8)
        if kind == "rim":
            return ring_columns(s["x"], s["z"], RADIUS, 32)
        return [(s["x"], s["z"])]

    # ---- the drill ----
    def drill(self, i):
        s = self.sites[i]
        m = self.mutant
        x, z = s["x"], s["z"]
        for kind in ("footprint", "n3", "rim") + (("seam",) if s["seam"] else ()):
            self.se_rays(i, kind, "before", self.columns(i, kind), before=True)
        self.tick += 20
        sc = RS / (RS + SEABED)
        landed = m != "S8a.M12"
        self.ev("drill", i=i, sn=[x, SEABED, z], radius=RADIUS, path=s["path"], seam=s["seam"], s=sc, tick=self.tick,
                reply={"ok": True, "pending": True, "tile": "1,1,1", "beforeY": SEABED})
        t_drill = self.tick
        if landed:
            self.holes[i] = (x, SEABED, z, RADIUS * sc, RADIUS)
            self.cells += 8
            self.se_row("s8a.edit", tick=self.tick, path="explosion" if s["path"] == "sphere" else "cutout", tile="1,1,1", min=[10, 10, 10], max=[16, 16, 16], cells=8, count=i + 1, suppressed=0, overlayCells=self.cells)
            # the converter (coalesce 250 ms = 15 ticks), the write, the notice
            self.tick += 16
            bx, by, bz = int((x + 2048) // 160), int((SEABED + 3040) // 160), int((z + 2048) // 160)
            version = 1 + sum(1 for w in self.writes if w[0] == (bx, by, bz))
            fnv = "%016x" % (0x1234567890abcdef ^ (bx * 7919 + bz * 31 + version))
            path = os.path.join(self.world, "DE_S8a_store", self.store_key, "drill", "compiled-batch-%d-%d-%d.v%d.optoctrees" % (bx, by, bz, version)) if m == "S8a.M5" \
                else os.path.join(self.root, self.store_key, "drill", "compiled-batch-%d-%d-%d.v%d.optoctrees" % (bx, by, bz, version))
            self.writes.append(((bx, by, bz), version, fnv, path))
            self.se_row("s8a.write", tick=self.tick, layer="drill", bx=bx, by=by, bz=bz, version=version, bytes=123456, fnv=fnv, ms=4.2, path=path, base="install", octrees=2, cells=400)
            if m != "S8a.M1":
                self.seq += 1
                self.se_row("s8a.notice", tick=self.tick, seq=self.seq, kind=1, layer=1, flags=0, bx=bx, by=by, bz=bz, version=version, min=[0, 0, 0], max=[31, 31, 31], octrees=2, cells=400, pending=1, stream=7)
                self.tick += 1
                self.se_row("s8a.feed", tick=self.tick, stream=7, firstSeq=self.seq, rows=1, pending=1, dropped=0, ackSeq=self.seq - 1, appliedSeq=self.applied, resyncEpoch=0)
                # Subnautica validates and reloads
                self.frame += 5
                ok_load = m != "S8a.M5"
                self.sn_row("s8a.load", snFrame=self.frame, seq=self.seq, bx=bx, by=by, bz=bz, version=version, path=path, bytes=123456, fnv=fnv, ok=ok_load, served=ok_load, ms=1.5,
                            error=None if ok_load else "missing " + path)
                self.loads.append(((bx, by, bz), version, fnv, ok_load))
                self.tick += 22
                self.frame += 20
                self.applied = self.seq
                self.sn_row("s8a.notice", snFrame=self.frame, seq=self.seq, kind=1, layer=1, flags=0, bx=bx, by=by, bz=bz, version=version, served=ok_load, state="applied" if ok_load else "refused",
                            scheduledMs=40, appliedMs=380, cellsReloaded=27, needsReload=True, error=None, ackSeq=self.seq, appliedSeq=self.seq)
                self.se_row("s8a.feed", tick=self.tick, stream=7, firstSeq=self.seq + 1, rows=0, pending=0, dropped=0, ackSeq=self.seq, appliedSeq=self.applied, resyncEpoch=0)
                if ok_load:
                    if m == "S8a.M14" and s["seam"]:
                        self.sn_holes[i] = None      # the reference tile for the octree was the un-mirrored one
                    else:
                        self.sn_holes[i] = self.holes[i]
                else:
                    self.sn_holes[i] = None
            else:
                self.sn_holes[i] = None
            self.tick += 70
            self.se_row("s8a.drill", tick=t_drill, witnessTick=self.tick, sn=[x, SEABED, z], se=[x, 57000.0, z], radius=RADIUS, path=s["path"], tile="1,1,1", beforeY=SEABED,
                        afterY=SEABED - RADIUS, deeper=RADIUS, modified=True, overlayCells=self.cells, drillGen=len(self.writes), drillOn=True, m12=False)
        else:
            self.tick += 90
            self.se_row("s8a.drill", tick=t_drill, witnessTick=self.tick, sn=[x, SEABED, z], se=[x, 57000.0, z], radius=RADIUS, path=s["path"], tile="1,1,1", beforeY=SEABED,
                        afterY=SEABED, deeper=0.0, modified=False, overlayCells=0, drillGen=0, drillOn=True, m12=True)
            self.sn_holes[i] = None
        self.ev("applied", i=i, ok=True, waited=0.6, feed={"pending": 0, "ackSeq": self.seq, "appliedSeq": self.applied, "nextSeq": self.seq + 1})
        self.sn_rays(i, "axis", "after", [(x, z)])
        self.tick += 600 - 130

    def ensure_holes(self, idx=None):
        for i in (idx if idx is not None else range(len(self.sites))):
            if i not in self.holes and i not in self.sn_holes:
                self.drill(i)

    # ---- parts ----
    def p_n1(self):
        self.part("n1", "begin")
        for i in range(len(self.sites)):
            self.drill(i)
        self.part("n1", "end")

    def p_n2(self):
        self.part("n2", "begin")
        self.ensure_holes()
        for i in range(len(self.sites)):
            cols = self.columns(i, "footprint")
            self.sn_rays(i, "footprint", "after", cols)
            if self.sites[i]["seam"]:
                sc = self.columns(i, "seam")
                self.se_rays(i, "seam", "after", sc)
                self.sn_rays(i, "seam", "after", sc)
        self.part("n2", "end")

    def p_n3(self):
        self.part("n3", "begin")
        self.ensure_holes()
        for i in range(len(self.sites)):
            for kind in ("n3", "rim"):
                cols = self.columns(i, kind)
                self.se_rays(i, kind, "after", cols)
                self.sn_rays(i, kind, "after", cols)
        self.part("n3", "end")

    def p_n4(self):
        self.part("n4", "begin")
        self.ensure_holes(range(3))
        for i in range(3):
            cols = self.columns(i, "n3")
            self.se_rays(i, "n3", "presave", cols)
            self.sn_rays(i, "n3", "presave", cols)
        h = "%016x" % (0xabcdef ^ self.cells)
        self.ev("overlay", reply={"ok": True, "cells": self.cells, "hash": h, "overlayGen": 3, "savedHash": "0" * 16})
        self.ev("save", reply={"ok": True, "world": self.world, "cells": self.cells}, how="scripted")
        self.tick += 120
        self.se_row("s8a.overlay", tick=self.tick, cells=self.cells, hash=h, why="save", bytes=4000, target=self.world, m3=self.mutant == "S8a.M3")
        self.saved_hash = h
        if self.mutant == "S8a.M3":
            self.ev("savefile", path=os.path.join(self.world, "DE_S8a_overlay.vx2"), exists=False, bytes=0, ok=False, error="missing")
        else:
            self.ev("savefile", path=os.path.join(self.world, "DE_S8a_overlay.vx2"), exists=True, bytes=4000, ok=True, version=1, site=1, cells=self.cells, hash=h, error=None)
        self.ev("relaunch", phase="closing", ok=True)
        self.ev("relaunch", phase="started", ok=True)
        self.ev("relaunch", phase="inworld", ok=True)
        if self.mutant == "S8a.M3":
            self.holes.clear()
            self.sn_holes = {i: None for i in self.sn_holes}
        for i in range(3):
            cols = self.columns(i, "n3")
            self.se_rays(i, "n3", "relaunch", cols)
            self.sn_rays(i, "n3", "relaunch", cols)
        self.ev("world_scan", folder=self.world, vx2=["DE_4546B-4546d120000.vx2", "DE_S8a_overlay.vx2"], tileVx2=[], storageNames=[], tileVoxelMaps=[], error=None)
        self.part("n4", "end")

    def p_n5(self):
        self.part("n5", "begin")
        self.ensure_holes()
        self.ev("reply", side="sn", cmd="de.s8a", args={"action": "reload", "all": True}, reply={"ok": True, "scheduled": len(self.writes)}, label="n5.reload")
        for (bx, by, bz), v, fnv, path in self.writes:
            self.frame += 3
            self.sn_row("s8a.reload", snFrame=self.frame, bx=bx, by=by, bz=bz, why="de.s8a reload all", inRing=True, cellsReloaded=27, ms=300, failed=False, error=None)
        if self.mutant == "S8a.M4":
            self.sn_holes = {i: None for i in self.sn_holes}
        for i in range(len(self.sites)):
            if self.sites[i]["seam"]:
                continue
            cols = self.columns(i, "n3")
            self.se_rays(i, "n3", "reload", cols)
            self.sn_rays(i, "n3", "reload", cols)
        files = []
        newest = {}
        for id_, v, fnv, path in self.writes:
            if id_ not in newest or newest[id_][0] < v:
                newest[id_] = (v, fnv, path)
        for (bx, by, bz), (v, fnv, path) in newest.items():
            files.append({"bx": bx, "by": by, "bz": bz, "version": v, "bytes": 123456, "fnv": fnv, "exists": True, "bytesOnDisk": 123456, "fnvOnDisk": fnv, "ok": True})
        self.ev("store_check", storeKey=self.store_key, folder=os.path.join(self.root, self.store_key, "drill"), manifest={"entries": len(files), "key": self.store_key, "hash": "0" * 16, "error": None}, files=files)
        self.part("n5", "end")

    def p_n6(self):
        self.part("n6", "begin")      # the listing itself is taken by the session around the games (run), as the launcher does
        self.part("n6", "end")

    def install_after(self):
        self.ev("install_listing", when="after", path=os.path.join(self.folder, "s8a-install-after.txt"))
        self.ev("install_compare", ok=True, counts={"changed": 0, "added": 0, "removed": 0, "filesBefore": 5500, "filesAfter": 5500}, changed=[], added=[], removed=[],
                octreeHashBefore="2cc277ce91763285", octreeHashAfter="2cc277ce91763285")

    def p_teardown(self):
        self.part("teardown", "begin")
        self.part("teardown", "end")

    def p_c1(self):
        self.part("c1", "begin")
        bad = self.mutant == "S8a.M13"
        cs = {"tool": "S8aRoundTripTests (C#)", "files": 100, "filesIdentical": 100, "filesDifferent": 0, "octrees": 12250,
              "rules": {"octrees": 12250, "interior": 788008, "downsampleOk": 788008, "downsampleBad": 0, "collapsible": 0, "layoutBad": 0, "countBad": 0},
              "rulesWritten": {"octrees": 12250, "interior": 788008, "downsampleOk": 788008, "downsampleBad": 0, "collapsible": 0, "layoutBad": 0, "countBad": 0},
              "reader": {"samples": 15000, "diffs": 0}, "m13": {"checked": 20, "different": 20, "downsampleBad": 15546, "red": True},
              "rows": {"S8a.C1.roundtrip": "PASS", "S8a.C1.rules": "PASS", "S8a.C1.reader": "PASS", "S8a.M13.red": "PASS"}}
        py = {"files": 100, "filesIdentical": 0 if bad else 100, "filesDifferent": 100 if bad else 0, "octrees": 12300, "mutant": "S8a.M13" if bad else "",
              "rules": {"octrees": 12300, "interior": 1128336, "downsampleOk": 0 if bad else 1128336, "downsampleBad": 1128336 if bad else 0, "collapsible": 0, "layoutBad": 0, "countBad": 0},
              "rows": {"S8a.C1.roundtrip": "FAIL" if bad else "PASS", "S8a.C1.rules": "FAIL" if bad else "PASS"}}
        d = os.path.join(self.folder, "c1")
        os.makedirs(d, exist_ok=True)
        pcs, ppy = os.path.join(d, "s8a-c1-cs.json"), os.path.join(d, "s8a-c1-py.json")
        with open(pcs, "w") as f:
            json.dump(cs, f)
        with open(ppy, "w") as f:
            json.dump(py, f)
        self.ev("c1", cs={"path": pcs, "rc": 0, "json": pcs}, py={"path": ppy, "rc": 0, "json": ppy})
        self.part("c1", "end")

    def p_c3(self):
        self.part("c3", "begin")
        self.ev("c3", wire={"rc": 0, "tail": "Passed! 12 tests"}, ref={"rc": 0, "tail": "hash 0xcbf4ca049d06c430\ns8a_ref selftest: PASS"}, layout={"rc": 0, "tail": "LAYOUT COMPARE PASS"})
        self.part("c3", "end")

    # ---- Stage B (S8a-2) ----
    WINDOW_SEABED = -80.0

    def p_c2(self):
        self.part("c2", "begin")
        self.ev("c2", rc=0, tail="OK   S8a.C2.sculpt heights within 1 unit of the rules (worst 1); material pixels differing 0\nOK   S8a.C2.flat pixels within rhoInf 2450 m non-zero: 0; bicubic at 2,000 columns within 2,300 m: max 0.004 m\nSCULPT VERIFY PASS")
        self.part("c2", "end")

    def p_c2g(self):
        self.part("c2g", "begin")
        pts = [[0.0, 0.0], [2700.0, 0.0], [4320.0, 0.0], [5280.0, 0.0], [0.0, 5280.0], [-7000.0, -3000.0]]
        exp = [{"x": x, "z": z, "y": -3000.0 if abs(x) + abs(z) < 3000 else -60.0 - 0.01 * abs(x + z), "slope": 0.1} for x, z in pts]
        off = getattr(self, "c2g_offset", 0.0)        # the seeded field bug: SE's surface off the tool's by this much
        rows = [{"x": e["x"], "z": e["z"], "closestY": e["y"] + 0.003 + off, "storageY": e["y"] - 0.4 + off, "waterDepth": e["y"] + 0.003 + off, "material": "Sand_02",
                 "biomeMap": 3} for e in exp]
        probe = {"ok": True, "rows": rows, "sculptHash": "00000000c0ffee00", "averageRadius": 60000.0, "minimumRadius": 57000.0, "maximumRadius": 67200.0,
                 "rs": 60000.00390625, "planetDataDir": "<mod>/Data/PlanetDataFiles/DE_4546B"}
        self.ev("c2g", points=pts, expected=exp, files="<mod>", filesWhere="the planet data folder SE loaded", filesSculptHash="00000000c0ffee00", probe=probe)
        self.part("c2g", "end")

    def se_sample(self, ws_mb):
        self.se.append({"t": self.stamp_t(), "ev": "sample", "tick": self.tick, "workingSetMB": ws_mb, "privateMB": ws_mb + 100})

    def stamp_t(self):
        import datetime
        return (datetime.datetime(2026, 10, 23, 10, 0, 0) + datetime.timedelta(seconds=self.tick / 60.0)).strftime("%Y-%m-%dT%H:%M:%S.%fZ")[:-4] + "Z"

    def window_row(self, cam, vel, near_pending=False, near_count=0, near_empty=False, seabed_below=60.0):
        self.se.append({"t": self.stamp_t(), "ev": "s8a.window", "tick": self.tick, "chartSeq": self.chart_seq, "ring": 125, "generated": 125, "pending": 0, "nearPending": near_pending,
                        "nearPendingCount": near_count, "nearEmpty": near_empty, "msP95": 120.0, "workerBusy": False, "storeMB": 40.0, "cameraBiome": 1, "seabedBelowCamM": seabed_below,
                        "camSn": list(cam), "velSn": list(vel), "windowGen": 125})

    def status_row(self, area_built=True, near_seen=0, header_pending=False):
        self.frame += 60
        self.sn_row("s8a.status", snFrame=self.frame, presentedSeTick=self.tick, ackSeq=self.seq, appliedSeq=self.applied, chartSeqApplied=self.chart_seq, served=10, failures=0,
                    nearPendingSeen=near_seen, areaBuiltNative=area_built, areaBuilt=area_built, windowOn=True, nearPendingSeenCount=near_seen, headerNearPending=header_pending,
                    headerNearEmpty=False, cameraBiome=1, snapFrames=self.snap_frames, chartChanges=self.chart_changes, spawnsLive=12, windowLoads=125)
        self.sn[-1]["t"] = self.stamp_t()

    def window_path(self, name, p0, p1, speed, lag=False, columns=True, kinds=("Surface", "Water", "Rock")):
        m = self.mutant
        dist = math.hypot(p1[0] - p0[0], p1[1] - p0[1])
        secs = int(dist / speed)
        y = self.WINDOW_SEABED + 60.0
        self.ev("path", name=name, fromSn=[p0[0], y, p0[1]], toSn=[p1[0], y, p1[1]], speed=speed, seabedAtStart=self.WINDOW_SEABED, seconds=dist / speed)
        self.part(name, "begin")
        for s in range(secs):
            f = s / float(max(1, secs))
            cam = (p0[0] + (p1[0] - p0[0]) * f, y, p0[1] + (p1[1] - p0[1]) * f)
            vel = ((p1[0] - p0[0]) / dist * speed, 0.0, (p1[1] - p0[1]) / dist * speed)
            pending = lag and 10 <= s < 20
            count = 3 if pending else 0
            self.window_row(cam, vel, near_pending=pending and m != "S8a.M9", near_count=count, seabed_below=60.0)
            self.status_row(area_built=not (pending and m != "S8a.M9"), near_seen=(2 if pending else 0), header_pending=pending and m != "S8a.M9")
            kind = kinds[s % len(kinds)]
            # an all-water batch has no file (path null, 0 bytes) as the real worker logs it; the seeded fault names a path for it
            water_path = kind == "Water" and not getattr(self, "water_with_path", False)
            self.se_row("s8a.write", tick=self.tick, layer="window", bx=20 + s % 7, by=18, bz=5, version=1,
                        bytes=0 if water_path else 200000 if kind == "Surface" else 600, fnv="%016x" % (0xabc + s), ms=120.0 if kind == "Surface" else 1.5,
                        path=None if water_path else os.path.join(self.root, self.store_key, "window", "0" * 16, "compiled-batch-%d-18-5.v1.optoctrees" % (20 + s % 7)),
                        kind=kind, octrees=30, cells=4000, regen=False, reuse=False,
                        chartSeq=self.chart_seq, chartKey="0" * 16)
            self.se_sample(1800 + s * 0.2)
            self.ev("mem", part=name, seWorkingSetMB=1800 + s * 0.2, snWorkingSetMB=900 + s * 0.1, commitTotal=1)
            for k in range(2):      # two window ids per second come within 100 m, announced and applied before
                self.se_row("s8a.near100", tick=self.tick + k, bx=20 + (s * 2 + k) % 40, by=18, bz=5, distM=95.0, state="generated", kind="Surface", version=1,
                            noticeSeq=100 + s * 2 + k, appliedSeq=200 + s * 2, applied=True, chartSeq=self.chart_seq)
            if lag and m != "S8a.M9":
                self.gate_rows(pending, s)
            self.tick += 60
        self.part(name, "end")
        if columns:
            cols = [(round(p0[0] + (p1[0] - p0[0]) * k / 39.0, 3), round(p0[1] + (p1[1] - p0[1]) * k / 39.0, 3)) for k in range(40)]
            res = [{"x": x, "z": z, "storageY": float("nan"), "castY": self.WINDOW_SEABED, "tile": None, "castTile": "planet", "material": None} for x, z in cols]
            self.ev("rays", label="%s.100.path.se" % name, side="se", site=100, kind="path", when=name, columns=[[x, z, self.WINDOW_SEABED] for x, z in cols], results=res, state="done")
            res2 = [{"i": k, "x": x, "z": z, "yHint": self.WINDOW_SEABED, "state": "ok", "hit": True, "y": self.WINDOW_SEABED + 0.05 * math.sin(k)} for k, (x, z) in enumerate(cols)]
            self.ev("rays", label="%s.100.path.sn" % name, side="sn", site=100, kind="path", when=name, columns=[[x, z, self.WINDOW_SEABED] for x, z in cols], results=res2, state="done")

    def gate_rows(self, pending, s):
        """SE's per-tick gate rows of the synthetic episode (seconds 10..19 pending): Subnautica's C7 lags 2 ticks; areaBuilt false from the first presented tick after the rise; the tail after the episode shows areaBuilt back after 30 ticks."""
        rise = getattr(self, "_rise", None)
        if pending:
            if rise is None:
                rise = self._rise = self.tick
            for k in range(60):
                t = self.tick + k
                pt = t - 2
                self.se_row("s8a.gate", tick=t, nearPending=True, nearPendingCount=3, nearEmpty=False, snAreaBuilt=pt < rise + 1, snPresentedSeTick=pt, seabedBelowCamM=60.0)
        elif rise is not None and s == 20:
            end = self.tick
            for k in range(120):
                t = self.tick + k
                self.se_row("s8a.gate", tick=t, nearPending=False, nearPendingCount=0, nearEmpty=False, snAreaBuilt=(t - 2) >= end + 30, snPresentedSeTick=t - 2, seabedBelowCamM=60.0)
            self._rise = None

    def p_n7(self):
        self.part("n7", "begin")
        for speed in (20.0, 80.0):
            self.window_path("n7.path.%d" % int(speed), (1800.0, 0.0), (4600.0, 0.0), speed)
            self.window_path("n7.ring.%d.0" % int(speed), (4600.0, 0.0), (4600.0, 4600.0), speed, columns=False)
        # the 6 m drill into the generated seabed: the batch regenerates; S8a.M6 regenerates from the unedited provider (no hole)
        x, z, r = 3200.0, 0.0, 6.0
        sc = RS / (RS + self.WINDOW_SEABED)
        site = {"x": x, "z": z}
        cols = grid_columns(x, z, 1.2 * r * sc, 9)
        self.ev("rays", label="before.100.wfootprint.se", side="se", site=100, kind="wfootprint", when="before", columns=[[cx, cz, self.WINDOW_SEABED] for cx, cz in cols],
                results=[{"x": cx, "z": cz, "storageY": float("nan"), "castY": self.WINDOW_SEABED, "tile": None, "castTile": "planet"} for cx, cz in cols], state="done")
        self.tick += 30
        self.ev("drill", i=100, sn=[x, self.WINDOW_SEABED, z], radius=r, path="sphere", seam=False, s=sc, tick=self.tick, reply={"ok": True, "pending": True, "tile": "planet"}, window=True)
        t_drill = self.tick
        self.tick += 120
        self.se_row("s8a.write", tick=self.tick, layer="window", bx=int((x + 2048) // 160), by=int((self.WINDOW_SEABED + 3040) // 160), bz=int((z + 2048) // 160), version=2, bytes=210000, fnv="%016x" % 0xdead,
                    ms=140.0, path=os.path.join(self.root, self.store_key, "window", "0" * 16, "compiled-batch-32-18-12.v2.optoctrees"), kind="Surface", octrees=30, cells=800,
                    regen=True, reuse=False, chartSeq=0, chartKey="0" * 16)
        self.se_row("s8a.drill", tick=t_drill, witnessTick=self.tick, sn=[x, self.WINDOW_SEABED, z], se=[x, 57000.0, z], radius=r, path="sphere", guardPath="explosion", tile="planet",
                    beforeY=self.WINDOW_SEABED, afterY=self.WINDOW_SEABED - r, deeper=r, modified=True, overlayCells=0, drillGen=0, drillOn=True, m12=False)
        self.ev("applied", i=100, ok=True, waited=2.5, feed={"pending": 0})
        hole = None if self.mutant == "S8a.M6" else (x, self.WINDOW_SEABED, z, r * sc, r)
        res = []
        for k, (cx, cz) in enumerate(cols):
            yy = self.WINDOW_SEABED if hole is None else hole_depth(hole[0], hole[1], hole[2], hole[3], hole[4], cx, cz, self.WINDOW_SEABED)
            res.append({"i": k, "x": cx, "z": cz, "yHint": self.WINDOW_SEABED, "state": "ok", "hit": True, "y": yy + 0.02 * math.sin(k)})
        self.ev("rays", label="after.100.wfootprint.sn", side="sn", site=100, kind="wfootprint", when="after", columns=[[cx, cz, self.WINDOW_SEABED] for cx, cz in cols], results=res, state="done")
        self.part("n7", "end")

    def p_n8(self):
        m = self.mutant
        self.part("n8", "begin")
        self.ev("plate", reply={"ok": True, "entityId": "123456789"}, entity="123456789")
        y = -10.0
        self.ev("path", name="n8.out", fromSn=[0.0, y, 0.0], toSn=[12000.0, y, 0.0], speed=80.0, seconds=150.0)
        cam_x = 0.0
        for kind, at_x in (("open", 2600.0), ("terrain", 6000.0)):
            while cam_x < at_x:
                cam_x += 80.0
                self.tick += 60
                self.window_row((cam_x, y, 0.0), (80.0, 0.0, 0.0))
                self.status_row()
            # the forced step: T = StepBatches * 160 along +x (S8a.M8: the exact overshoot)
            n = 10 if self.chart_seq == 0 else 24
            tx_m = cam_x if m == "S8a.M8" else n * 160.0
            before_cam, before_ent = [cam_x, y, 0.0], [cam_x + 30.0, y, 0.0]
            self.ev("reply", side="se", cmd="de.s8a", args={"action": "chart", "entity": "123456789"}, reply={"ok": True, "camSn": before_cam, "entitySn": before_ent}, label="n8.step.%s.before" % kind)
            self.chart_seq += 1
            self.chart_changes += 1
            if m != "S8a.M7":
                self.snap_frames += 1
            after_cam = [cam_x - tx_m, y, 0.0]
            after_ent = [cam_x + 30.0 - tx_m + 30.0 * 1.0e-3 * 0.5, y, 0.0]      # the outgoing chart's distortion: well inside 1.1e-3 * 30 m
            self.se_row("s8a.chart", tick=self.tick, chartSeq=self.chart_seq, chartKey="%016x" % (0x51 + self.chart_seq), T=[tx_m, 0.0, 0.0], tBatches=[n, 0], anchor="U0=... O=...",
                        reason="forced x+", toSite=False, camSnBefore=before_cam, camSnAfter=after_cam, seq=self.seq + 1, m8=m == "S8a.M8")
            sn_before = {"ok": True, "haveCam": True, "camX": cam_x, "camY": y, "camZ": 0.0, "entityFound": True, "entityX": cam_x + 30.0, "entityY": y, "entityZ": 0.0, "entityTick": self.tick - 1}
            sn_after = {"ok": True, "haveCam": True, "camX": cam_x - tx_m, "camY": y, "camZ": 0.0, "entityFound": True, "entityX": cam_x + 30.0 - tx_m + 30.0 * 1.0e-3 * 0.5, "entityY": y, "entityZ": 0.0, "entityTick": self.tick + 2}
            self.ev("chart_step", kind=kind, entity="123456789", before={"ok": True, "camSn": before_cam, "entitySn": before_ent}, step={"ok": True, "chartSeq": self.chart_seq},
                    after={"ok": True, "camSn": after_cam, "entitySn": after_ent}, snBefore=sn_before, snAfter=sn_after, snApplied=self.chart_seq, tick=self.tick,
                    altitude=y, pictureBefore={"ok": True, "picture": "site", "latch": "sn", "pictureEdges": 4}, pictureAfter={"ok": True, "picture": "site", "latch": "sn", "pictureEdges": 4})
            # Subnautica's C1 frames across the change: the motion paused (prev = older), the newer pose -T
            self.frame += 1
            self.sn_row("s8a.chartsnap", snFrame=self.frame, preCull=False, older=self.tick - 1, newer=self.tick, prevTick=self.tick - 2, alpha=0.5,
                        camOlderX=cam_x, camOlderY=y, camOlderZ=0.0, camNewerX=cam_x - tx_m, camNewerY=y, camNewerZ=0.0, camPrevX=cam_x, camPrevY=y, camPrevZ=0.0,
                        chartSeq=self.chart_seq, chartSeqApplied=self.chart_seq)
            cam_x -= tx_m
            self.status_row()
        self.ev("path", name="n8.back", fromSn=[12000.0, y, 0.0], toSn=[0.0, y, 0.0], speed=80.0, seconds=150.0)
        self.tick += 600
        self.status_row()
        # the return to chart 0 (T = 0 logged, the camera jumps back by the accumulated offset): not judged by N8.batchstep
        self.chart_seq += 1
        self.se_row("s8a.chart", tick=self.tick, chartSeq=self.chart_seq, chartIndex=0, chartKey="%016x" % 0x51, T=[0.0, 0.0, 0.0], tBatches=[0, 0], anchor="U0=... O=...",
                    reason="auto: site-return", toSite=True, camSnBefore=[cam_x, y, 0.0], camSnAfter=[cam_x + 34 * 160.0, y, 0.0], seq=self.seq + 1, m8=m == "S8a.M8")
        self.chart_changes += 1
        self.status_row()
        # the same-chart return (de.s8a step site on chart 0: T = 0, the camera does not jump): Subnautica brackets it and snaps
 # (S8a.M7 interpolates: no snap)
        self.tick += 120
        cam_x += 34 * 160.0
        self.chart_seq += 1
        self.chart_changes += 1
        if m != "S8a.M7":
            self.snap_frames += 1
        self.se_row("s8a.chart", tick=self.tick, chartSeq=self.chart_seq, chartIndex=0, chartKey="%016x" % 0x51, T=[0.0, 0.0, 0.0], tBatches=[0, 0], anchor="U0=... O=...",
                    reason="forced return", toSite=True, camSnBefore=[cam_x, y, 0.0], camSnAfter=[cam_x, y, 0.0], seq=self.seq + 1, m8=False)
        self.tick += 60
        self.status_row()
        self.part("n8", "end")

    def p_n9(self):
        self.part("n9", "begin")
        self.ev("throttle", ms=2000)
        self.window_path("n9.path.80", (1800.0, 0.0), (4600.0, 0.0), 80.0, lag=True, columns=False)
        self.part("n9", "end")

    def p_n10(self):
        m = self.mutant
        self.part("n10", "begin")
        self.window_path("n10.path.20", (1800.0, 0.0), (4600.0, 0.0), 20.0, columns=False)
        self.window_path("n10.ring.20", (4600.0, 0.0), (4600.0, 4600.0), 20.0, columns=False)
        # spawns over the band-1 seabed (60 < 80 m <= 200): 7,400 m * 300 m = 222 ha at 20 per ha (S8a.M11: x 10)
        ha = 7400.0 * 300.0 / 10000.0
        scale = 10.0 if m == "S8a.M11" else 1.0
        for tech, per in (("Hoopfish", 3), ("Spadefish", 2), ("Reginald", 2), ("RedGrass", 8), ("PurpleFan", 3), ("SmallKoosh", 2)):
            self.frame += 1
            self.sn_row("s8a.spawn", snFrame=self.frame, techType=tech, band=1, count=int(round(per * ha * scale)), despawned=3, live=40, m11=m == "S8a.M11")
            self.sn_row("s8a.biome", snFrame=self.frame, cameraBiome=1, cameraBiomeName="grassyPlateaus", biome="grassyPlateaus", windowOn=True)
        cols = []
        mats = ("Sand_02", "Stone_01", "Stone_03", "Stone_05")
        for k in range(40):
            mat = mats[k % 4]
            want = SN_TYPE_OF[mat]
            cols.append({"x": 1800.0 + 70.0 * k, "z": 0.0, "seY": self.WINDOW_SEABED, "seMaterial": mat, "snType": 1 if m == "S8a.M10" else want, "snDensity": 0, "solid": True, "source": "store v1"})
        self.ev("material_probe", columns=cols, n=40)
        # the ore probes (Stage C): SE's vein at the slope-scaled depths, one cavity per probe set drilled (S8a.M10: every type 1)
        orows = []
        for k_, (ore, sub, s0, d, slope) in enumerate((("Iron", "Iron_02", 2.0, 10.0, 0.1), ("Cobalt", "Cobalt_01", 3.0, 6.0, 0.4), ("Nickel", "Nickel_01", 2.0, 8.0, 0.0),
                                                       ("Magnesium", "Magnesium_01", 3.0, 6.0, 0.2))):
            k = math.sqrt(1.0 + slope * slope)
            depths = [max(0.5, s0 - 1.0) * k, (s0 + 1.5) * k, (s0 + 0.5 + 0.5 * d) * k, (s0 + d - 0.5) * k]
            mats = ["Sand_02", sub, sub, sub]
            want = 1 if m == "S8a.M10" else SN_TYPE_OF[sub]
            wall = [{"dx": ux, "dz": uz, "radius": 4.0, "type": want, "density": 120 + 3 * j, "solid": True, "source": "store v2"}
                    for j, (ux, uz) in enumerate(((1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0)))] if k_ == 0 else None
            orows.append({"ore": ore, "type": sub, "x": 4400.0 + 50 * k_, "z": 100.0, "seabedY": -150.0, "slope": slope, "pixels": 2, "startM": s0, "endM": s0 + d, "k": k,
                          "depths": depths, "roles": ["above", "inside", "inside", "inside"], "seMaterials": mats, "seY": -150.0, "sculptHash": "00000000c0ffee00",
                          "snInterior": {"type": 84, "density": 0, "solid": True, "source": "store v1"}, "wall": wall})
        self.ev("ore_probe", rows=orows, n=len(orows), drills=1)
        # the spawn draw per scanned batch column: 150 columns of band-1 seabed (25,600 m^2 each) at the table's 20 per ha (S8a.M11: x 10)
        for k in range(150):
            self.frame += 1
            p1 = int(round(20 * 2.56 * scale))
            self.sn_row("s8a.scan", snFrame=self.frame, bx=20 + k % 30, bz=5 + k // 30, chartKey="0" * 16, columns=25600, c0=0, c1=25600, c2=0, c3=0, p0=0, p1=p1, p2=0, p3=0,
                        points=p1, failed=False, m11=m == "S8a.M11")
        self.part("n10", "end")

    def p_n10c(self):
        self.part("n10c", "begin")
        rows = []
        for k in range(24):
            sub, band, d = (("Iron_02", "0-200", 40.0), ("Cobalt_01", "100-300", 150.0), ("Silver_01", "300-600", 420.0))[k % 3]
            rows.append({"kind": "cube", "sn": [10.0 * k, -d, 5.0], "depth": d, "band": band, "expected": sub, "got": sub, "content": 255})
        rows.append({"kind": "deposit", "sn": [300.0, -63.0, -40.0], "depth": 63.0, "expected": "Iron_02", "got": "Iron_02", "content": 255})
        self.ev("crater_ores", reply={"ok": True, "rows": rows, "cubes": 24, "cubesMatch": 24, "bands": {"0-200": 8, "100-300": 8, "300-600": 8}, "deposits": 1,
                                       "depositsSolid": 1, "depositsMatch": 1, "reported": 1})
        self.part("n10c", "end")

    def p_nd(self):
        self.part("nd", "begin")
        for name in ("crater", "planet"):
            for k in range(40):
                self.frame += 1
                self.sn_row("s8a.cellvis", snFrame=self.frame, cells=4, covered=4, centreCovered=True, centreState=6, states="6:4", run=0, live=1, jobs=0)
            for k in range(30):
                self.frame += 1
                self.sn_row("s8a.live", snFrame=self.frame, seq=100 + k, bx=13, by=18, bz=13, levelZero=True, higher=k % 4 == 0, changed=2, patched=2, rebuilds=2, swapped=2,
                            discarded=0, noMesh=0, diffMs=3, patchMs=1, ms=180 + k)
            self.sn_row("s8a.flora", snFrame=self.frame, seq=130, why="notice", candidates=6, hidden=2, kept=4,
                        families="mushroom:3/2/1,rock:2/0/2,outcrop:1/0/1")
            if name == "crater":
                fams = (("PinkMushroom", "pickflora"), ("ScrapMetal", "pickup"), ("LimestoneChunk", "outcrop"), ("CoralShellPlate", "coral"), ("BigCoralTubes", "coral"))
                self.ev("nd_props", phase="before", reply={"ok": True, "list": ";".join("%s:%s:0:-17.20:" % (t, f) for t, f in fams)})
                v1 = self.mutant == "S8a.M26"
                self.ev("nd_props", phase="after", reply={"ok": True, "list": ";".join("%s:%s:%d:-17.20:" % (t, f, 0 if v1 and f.startswith("pick") else 1) for t, f in fams)})
            self.ev("drill_pass", name=name, seconds=20.0, vis={"ok": True, "rows": 40, "runMax": 0}, snStatus={"liveFallbacks": 0, "liveApplied": 30},
                    verify=[{"ok": True, "served": True, "octreesCompared": 125, "octreesDiffer": 0}], batches=[[13, 18, 13]])
        self.part("nd", "end")

    def ore_look(self, no_ore):
        """Ores as voxels (synthetic): the capture pair at a drilled vein (a rust-red wall against grey rock; S8a.M30: no ore, the same picture)
 and two paint-test candidates; the palette's status."""
        caps = []
        try:
            import numpy as np  # noqa: E402
            from PIL import Image  # noqa: E402
            os.makedirs(os.path.join(self.folder, "look"), exist_ok=True)
            for look in ("ours", "16", "206", "155"):
                img = np.zeros((180, 320, 3), np.uint8)
                img[...] = (60, 66, 72)
                if look != "16" and not no_ore:
                    img[60:130, 110:210] = (170, 70, 40) if look == "ours" else (90, 60, 60) if look == "206" else (70, 80, 110)
                pth = os.path.join("look", "ore-%s.png" % look)
                Image.fromarray(img).save(os.path.join(self.folder, pth))
                caps.append({"as": look, "png": pth, "luma": 60.0, "result": look})
        except Exception:
            caps = []
        out = {"camera": [0.0, -60.0, -7.0], "pitch": -35, "captures": caps,
               "palette": {"ok": True, "patched": True, "applied": True, "template": "GreyRock", "filled": 9, "taken": 0, "texturesReplaced": 18,
                           "slots": "95:Iron_02:filled 2", "templateTextures": "_MainTex=rock"}}
        self.ev("ore_look", **out)
        return out

    def p_no(self):
        """Ores you can see (synthetic): two sites with records, SE's detector cells and Subnautica's deposits; the drill shrinks the
 biggest; 60 drill hits leave the nearest whole. Mutants M21-M25 edit what each side reports."""
        self.part("no", "begin")
        m = self.mutant
        for k in range(2):
            cam = {"camX": 100.0 * k, "camY": -60.0, "camZ": 0.0}
            recs, det, near = [], [], []
            for j in range(3):
                key = "%016x" % (0xabc000 + 10 * k + j)
                x, z = 100.0 * k + 20.0 * j, 10.0
                ax = x + (8.0 if m == "S8a.M23" else 0.0)
                recs.append({"key": key, "ore": "Iron_02", "x": ax, "y": -72.0, "z": z, "exposedM2": 40.0, "volumeM3": 320.0, "firstVolumeM3": 320.0, "scale": 1.6,
                             "drilled": False, "detX": x + 0.5, "detY": -76.0, "detZ": z, "cell": "77:%d,%d,1" % (k, j)})
                det.append({"ore": "Iron_02", "x": x + 0.5, "y": -76.0, "z": z, "body": 77, "cell": [k, j, 1]})
                if not (m == "S8a.M21" and j == 2):
                    near.append("%s:DrillableTitanium:%.1f:-72.4:%.1f:1.60" % (key, ax, z))
            if m == "S8a.M22":
                near.append("deaddeaddeaddead:DrillableTitanium:%.1f:-72.4:10.0:1.60" % (100.0 * k + 40.0 + 40.0))
            ev = {"i": k, "site": {"x": 100.0 * k, "z": 0.0}, "records": recs, "detect": det, "deposits": dict(cam, near=";".join(near), liveNear=len(near))}
            if k == 0:
                big = recs[0]
                after = dict(big, volumeM3=200.0, drilled=True)
                scale_after = "1.60" if m == "S8a.M24" else "1.36"
                ev["drill"] = {"key": big["key"], "before": big, "after": [after] + recs[1:],
                               "depositsAfter": ";".join([n if not n.startswith(big["key"]) else "%s:DrillableTitanium:%.1f:-72.4:%.1f:%s" % (big["key"], big["x"], big["z"], scale_after) for n in near])}
                ev["immune"] = {"ok": True, "hits": 60, "calls": 60, "healthBefore": 600.0, "healthAfter": 300.0 if m == "S8a.M25" else 600.0}
                ev["oreLook"] = self.ore_look(m == "S8a.M30")
            self.ev("no_site", **ev)
        self.ev("ore_voxels", reply={"ok": True, "surface": 1200, "typed": 0 if m == "S8a.M30" else 1200, "m30": m == "S8a.M30"})
        # S8a.N10.oredensity: the design-5 rules (cube rule off, cap 4); the crater points see one deposit each, the planet's the
 # table's density (common 14.4/km2 over 0.283 km2: 4 deposits); S8a.M27 turns the cube rule back on (a deposit every 20 m)
        rules = "cubeRule off, deposits per km2 4 (de-planet-rules.txt 0123456789abcdef)"
        cube = m == "S8a.M27"
        for k in range(4):
            det = [{"ore": "Iron_02", "x": 10.0 + 300.0 * k, "y": -60.0, "z": 0.0, "body": 77, "cell": [k, 0, 0]}]
            if cube:
                det += [{"ore": "Iron_02", "x": -280.0 + 80.0 * j + 300.0 * k, "y": -60.0, "z": 150.0 * (j % 3) - 150.0, "body": 77, "cell": [k, j, 1]} for j in range(8)]
            self.ev("no_density", i=k, zone="crater", x=300.0 * k, z=0.0, seabedY=-60.0, radius=300.0, detect=det,
                    rule={"craterCubeRule": cube, "craterDepositsPerKm2": 4.0, "craterPockets": 12, "craterCapped": 30, "planetRules": rules})
        for k in range(4):
            det = [{"ore": o, "x": 20000.0 + 200.0 * j - 300.0, "y": -100.0, "z": 150.0 * (j % 2), "body": 9, "cell": [j, 0, 0]} for j, o in enumerate(("Iron_02", "Nickel_01", "Silicon_01", "Iron_02"))]
            self.ev("no_density", i=4 + k, zone="planet", x=20000.0, z=0.0, seabedY=-80.0, radius=300.0, detect=det,
                    rule={"craterCubeRule": cube, "craterDepositsPerKm2": 4.0, "planetRules": rules})
        self.part("no", "end")

    def p_nl(self):
        """The look (synthetic): three spawn captures and two far ones per height; textured seabed with dark silhouettes above, or
 (mutant S8a.M29) a flat empty picture with no entities."""
        self.part("nl", "begin")
        try:
            import numpy as np  # noqa: E402
            from PIL import Image  # noqa: E402
        except Exception:
            self.part("nl", "end")
            return
        rng = np.random.RandomState(7)
        os.makedirs(os.path.join(self.folder, "look"), exist_ok=True)

        def picture(flat_empty, k):
            img = np.zeros((180, 320, 3))
            img[...] = (20, 60, 90)
            if not flat_empty:
                img[60:, :, :] += rng.uniform(-40, 40, size=(120, 320, 1))
                for q in range(4):
                    x0 = 30 + 70 * q + 5 * k
                    img[10:58, x0:x0 + 20, :] = (8, 12, 18)
            return np.clip(img, 0, 255).astype(np.uint8)
        n = 0
        for h in (8.0, 25.0):
            for k in range(3):
                n += 1
                pth = os.path.join("look", "spawn-%d.png" % n)
                Image.fromarray(picture(False, k)).save(os.path.join(self.folder, pth))
                self.ev("look_capture", kind="spawn", biome="spawn", height=h, heading=120.0 * k, rho=0, png=pth, written=True, entities=20 + k)
            for k in range(2):
                n += 1
                empty = self.mutant == "S8a.M29"
                pth = os.path.join("look", "far-%d.png" % n)
                Image.fromarray(picture(empty, k)).save(os.path.join(self.folder, pth))
                self.ev("look_capture", kind="far", biome="grassyPlateaus", height=h, heading=0.0, rho=20000 + 1000 * k, png=pth, written=True,
                        entities=0 if empty else 18 + k)
        self.part("nl", "end")

    def p_ns(self):
        """The sample (synthetic): ready, the offline rows, a line with a plateau the heightmap lacks (SE, Subnautica, the field, the chunk's
 own top and the twin agree), a seam (the blend ramp, the heightmap beyond the square), the served bytes = the vectors', two spots
 whose census matches the chunk's records."""
        self.part("ns", "begin")
        cost = []
        for lod, (sp, vp) in enumerate(((2.0, 1.2), (1.8, 1.0), (1.5, 0.9), (1.2, 0.8))):
            cost.append({"cat": "sample", "lod": lod, "thread": "worker", "count": 300, "p50": sp / 2, "p95": sp, "max": sp * 2, "nsPerVoxel": 250.0})
        van = [{"cat": "vanillaOutside", "lod": lod, "thread": "worker", "count": 900, "p50": vp / 2, "p95": vp, "max": vp * 3, "nsPerVoxel": 90.0}
               for lod, vp in enumerate((1.2, 1.0, 0.9, 0.8))]
        status = {"ok": True, "setting": True, "patch": True, "state": "ready", "chunk": "gp1", "chunkFnv": "1193edd6486ccc49", "entities": 39247, "excluded": 2685,
                  "rebaseM": -25.0, "identity": "0" * 16, "initMs": 900.0, "halfM": 320.0, "blendM": 80.0, "reads": 3000, "servedOutside": 0, "errors": 0,
                  "cost": cost, "vanilla": van}
        self.ev("reply", side="se", cmd="de.s8a", args={"action": "sample"}, reply=dict(status), label="ns.status.start")
        self.ev("ns_offline", rows=[[True, "S8a.NS.cut 39247 entities kept, 0 kept with a landmark prefab"], [True, "S8a.NS.roundtrip 500 octrees of 5200 checked: 0 differ"],
                                    [True, "S8a.NS.placement re-base -25 m"]])
        h = -121.0
        cols = [(round(4000.0 + 8.0 * j, 3), round(-19500.0, 3)) for j in range(60)]
        own = lambda x: h + (14.0 if 4150.0 < x < 4300.0 else 2.0 * math.sin(x / 20.0))   # noqa: E731 - a plateau the heightmap lacks
        name = "ns.line.0.0"
        se = [{"x": x, "z": z, "storageY": own(x), "castY": own(x) + 0.05, "tile": None, "castTile": "planet"} for x, z in cols]
        sn = [{"i": j, "x": x, "z": z, "yHint": own(x), "state": "ok", "hit": True, "y": own(x) + 0.05 + 0.03 * math.sin(j)} for j, (x, z) in enumerate(cols)]
        self.ev("rays", label="%s.600.line.se" % name, side="se", site=600, kind="line", when=name, columns=[[x, z, h] for x, z in cols], results=se, state="done")
        self.ev("rays", label="%s.600.line.sn" % name, side="sn", site=600, kind="line", when=name, columns=[[x, z, h] for x, z in cols], results=sn, state="done")
        self.ev("ns_field", label="%s.600.line.field" % name, site=600, kind="line", when=name, rebaseM=-25.0, chunkFnv="1193edd6486ccc49",
                results=[[x, z, own(x), own(x), h, 1.0, x - 4800.0, -192.0] for x, z in cols], twinOwn=[own(x) for x, z in cols])
        self.ev("ns_line", name=name, line=0, axis="u", offset=32.0, segment=0, columns=len(cols), ref=h)
        # a seam: the chunk's own seabed 30 m above the heightmap, the blend ramp over 80 m, the heightmap beyond the square (d > 320)
        ds = [200.0 + 4.0 * i for i in range(41)]
        wgt = lambda d: (lambda t: t * t * (3 - 2 * t))(min(max((320.0 - d) / 80.0, 0.0), 1.0))   # noqa: E731
        hs = lambda d: h + 0.02 * d   # noqa: E731
        os_ = lambda d: h + 30.0   # noqa: E731
        srv = lambda d: wgt(d) * os_(d) + (1 - wgt(d)) * hs(d)   # noqa: E731
        cols = [(round(4158.0 + d, 3), round(-19400.0, 3)) for d in ds]
        name = "ns.seam.0"
        se = [{"x": x, "z": z, "storageY": srv(d), "castY": srv(d), "tile": None, "castTile": "planet"} for d, (x, z) in zip(ds, cols)]
        sn = [{"i": j, "x": x, "z": z, "yHint": srv(d), "state": "ok", "hit": True, "y": srv(d) + 0.04} for j, (d, (x, z)) in enumerate(zip(ds, cols))]
        self.ev("rays", label="%s.610.seam.se" % name, side="se", site=610, kind="seam", when=name, columns=[[x, z, h] for x, z in cols], results=se, state="done")
        self.ev("rays", label="%s.610.seam.sn" % name, side="sn", site=610, kind="seam", when=name, columns=[[x, z, h] for x, z in cols], results=sn, state="done")
        self.ev("ns_field", label="%s.610.seam.field" % name, site=610, kind="seam", when=name, rebaseM=-25.0,
                results=[[x, z, srv(d), os_(d) if d <= 320.0 else None, hs(d), wgt(d), 0.0, 0.0] for d, (x, z) in zip(ds, cols)], twinOwn=None)
        self.ev("ns_seam", name=name, side=[1.0, 0.0], along=48.0, distances=ds, columns=len(cols), ref=h)
        # the served bytes: the repository's install vectors as the game would return them
        vec = {"path": "synthetic", "serve": [], "head": {}}
        vp = os.path.join(ROOT, "tests", "s8a", "vectors", "sample", "sample-install-vectors.tsv")
        if os.path.exists(vp):
            for ln in open(vp, encoding="utf-8").read().splitlines():
                f = ln.split("\t")
                if f[0] == "serve":
                    vec["serve"].append({"name": f[1], "box": [float(f[2]), float(f[3]), float(f[4]), int(f[5]), int(f[6]), int(f[7]), int(f[8])], "fnv": f[9]})
                elif f[0] in ("chunk", "sample"):
                    vec["head"][f[0]] = f[1:]
        hd = vec["head"]
        self.ev("ns_serve", vectors=vec, reply={"ok": True, "results": [{"box": v["box"], "fieldFnv": v["fnv"], "providerFnv": v["fnv"]} for v in vec["serve"]],
                                                "chunkFnv": (hd.get("chunk") or [None])[0], "placementFnv": (hd.get("sample") or [None])[0]})
        for k in range(2):
            cen = {"ok": True, "radius": 60.0, "records": "flora:%d;light:2;prop:6;slot:300" % (40 + k), "live": "flora:%d;light:2;prop:6;slot:110" % (40 + k),
                   "slotRecords": 300, "slotFilled": 110, "slotEmpty": 190, "slotPending": 0, "camNativeX": -640.0, "camNativeY": -88.0, "camNativeZ": -192.0 + 180 * k}
            self.ev("ns_entities", name="ns.spot.%d" % k, spot={"u": 0.0, "v": 180.0 * k}, radius=60.0, census=cen,
                    expected={"flora": 41 + k, "light": 2, "prop": 6, "slot": 298}, life={"life": 150}, camera=None, se={"entityWrites": 4, "entitiesInFile": 3000})
        self.ev("reply", side="se", cmd="de.s8a", args={"action": "sample"}, reply=dict(status), label="ns.status.end")
        self.part("ns", "end")

    def p_nf(self):
        """Stage F2 (synthetic): the ring status, two paths over a stamp (SE, Subnautica and the field agree; the field departs from
 the heightmap), a seam, the blend, the served bytes = the vectors' (read from the repository), the fallback, a drill pass."""
        self.part("nf", "begin")
        cost = []
        for lod, (sp, vp) in enumerate(((3.0, 1.2), (2.5, 1.0), (2.0, 0.9), (1.5, 0.8))):
            cost.append({"cat": "served", "lod": lod, "thread": "worker", "count": 400, "p50": sp / 2, "p95": sp, "max": sp * 2, "nsPerVoxel": 300.0})
            cost.append({"cat": "served", "lod": lod, "thread": "game", "count": 5, "p50": sp / 2, "p95": sp, "max": sp * 1.5, "nsPerVoxel": 300.0})
            cost.append({"cat": "vanillaOutside", "lod": lod, "thread": "worker", "count": 900, "p50": vp / 2, "p95": vp, "max": vp * 3, "nsPerVoxel": 90.0})
        cost.append({"cat": "vanillaOutside", "lod": 6, "thread": "worker", "count": 300, "p50": 0.5, "p95": 1.0, "max": 3.0, "nsPerVoxel": 40.0})
        status = {"ok": True, "setting": True, "patch": True, "state": "ready", "placements": 15894, "brushesUsed": 165, "initMs": 2100.0, "source": "SE's face-1 heightmap",
                  "nearestSameBrushM": 300.0, "repeatM": 300.0, "forbiddenBrushes": 0, "forbiddenPlacements": 0, "reads": 4000, "passes": 90000, "servedOutside": 0,
                  "servedAboveLod": 0, "privateBeforeMB": 5000.0, "privateAfterMB": 5060.0, "failInjected": 0, "cost": cost}
        self.ev("reply", side="se", cmd="de.s8a", args={"action": "ring"}, reply=dict(status), label="nf.status.start")
        h = -150.0
        for k in range(2):
            name = "nf.path.%d" % k
            self.part(name, "begin")
            for s in range(15):
                self.se_row("s8a.write", tick=self.tick, layer="window", bx=30 + s % 5, by=18, bz=8, version=1, bytes=300000, fnv="%016x" % (0xf00 + s), ms=200.0,
                            path=os.path.join(self.root, self.store_key, "window", "0" * 16, "compiled-batch-%d-18-8.v1.optoctrees" % (30 + s % 5)), kind="Surface",
                            octrees=40, cells=5000, regen=False, reuse=False, chartSeq=0, chartKey="0" * 16)
                self.ev("mem", part=name, seWorkingSetMB=1900 + s * 0.5, snWorkingSetMB=950 + s * 0.2, commitTotal=1)
                self.se_sample(1900 + s * 0.5)
                self.tick += 60
            self.part(name, "end")
            cols = [(round(4500.0 + 300.0 * k - 150.0 + 300.0 * j / 39.0, 3), round(600.0 * k, 3)) for j in range(40)]
            top = lambda x: h + (12.0 if abs(x - (4500.0 + 300.0 * k)) < 15.0 else 0.0)   # noqa: E731 - a pillar at the spot
            se = [{"x": x, "z": z, "storageY": top(x), "castY": top(x) + 0.05, "tile": None, "castTile": "planet"} for x, z in cols]
            sn = [{"i": j, "x": x, "z": z, "yHint": top(x), "state": "ok", "hit": True, "y": top(x) + 0.05 + 0.03 * math.sin(j)} for j, (x, z) in enumerate(cols)]
            self.ev("rays", label="%s.%d.path.se" % (name, 400 + k), side="se", site=400 + k, kind="path", when=name, columns=[[x, z, h] for x, z in cols], results=se, state="done")
            self.ev("rays", label="%s.%d.path.sn" % (name, 400 + k), side="sn", site=400 + k, kind="path", when=name, columns=[[x, z, h] for x, z in cols], results=sn, state="done")
            self.ev("ring_field", label="%s.%d.path.field" % (name, 400 + k), site=400 + k, kind="path", when=name, results=[[x, z, top(x), h] for x, z in cols])
            self.ev("nf_path", name=name, spot={"x": 4500.0 + 300.0 * k, "z": 600.0 * k, "h": h}, y=h + 60.0)
        # a seam on a cliff: both games step 6 m between x 5151.5 and 5152.5
        sx = 5152.0
        cols = [(round(sx + dx, 3), round(900.0 + dz, 3)) for dz in (-4.0, 0.0, 4.0) for dx in (-3.0, -2.0, -1.0, -0.5, 0.5, 1.0, 2.0, 3.0)]
        cl = lambda x: h + (6.0 if x > sx else 0.0) + 0.3 * (x - sx)   # noqa: E731
        se = [{"x": x, "z": z, "storageY": cl(x), "castY": cl(x), "tile": None, "castTile": "planet"} for x, z in cols]
        sn = [{"i": j, "x": x, "z": z, "yHint": cl(x), "state": "ok", "hit": True, "y": cl(x) + 0.04} for j, (x, z) in enumerate(cols)]
        self.ev("rays", label="nf.seam.0.420.seam.se", side="se", site=420, kind="seam", when="nf.seam.0", columns=[[x, z, h] for x, z in cols], results=se, state="done")
        self.ev("rays", label="nf.seam.0.420.seam.sn", side="sn", site=420, kind="seam", when="nf.seam.0", columns=[[x, z, h] for x, z in cols], results=sn, state="done")
        self.ev("nf_seam", name="nf.seam.0", spot={"seamX": sx, "seamZ": 900.0, "h": h})
        self.ev("nf_blend", blend={"running": False, "ok": True, "borderPairs": 320, "innerPairs": 410, "borderP95": 1.1, "innerP95": 5.5, "bound": 11.0,
                                   "placements": [{"i": 1}] * 12, "ms": 2300.0})
        # the served bytes: the repository's install vectors as the game would return them
        vec = {"path": "synthetic", "serve": [], "head": {}}
        vp = os.path.join(ROOT, "tests", "s8a", "vectors", "ring3d", "ring3d-install-vectors.tsv")
        if os.path.exists(vp):
            for ln in open(vp, encoding="utf-8").read().splitlines():
                f = ln.split("\t")
                if f[0] == "serve":
                    vec["serve"].append({"name": f[1], "box": [float(f[2]), float(f[3]), float(f[4]), int(f[5]), int(f[6]), int(f[7]), int(f[8])], "fnv": f[9]})
                elif f[0] in ("rules", "heights", "biomes", "library", "placements"):
                    vec["head"][f[0]] = f[1:]
        hd = vec["head"]
        self.ev("nf_serve", vectors=vec, reply={"ok": True, "results": [{"box": v["box"], "fieldFnv": v["fnv"], "providerFnv": v["fnv"]} for v in vec["serve"]],
                                                "heightsFnv": (hd.get("heights") or [None])[0], "biomesFnv": (hd.get("biomes") or [None])[0], "rules": (hd.get("rules") or [None])[0],
                                                "libraryFnv": (hd.get("library") or [None, None])[0], "indexFnv": (hd.get("library") or [None, None])[1]})
        # the fallback on a plain column: SE serves the heightmap (= the field there), Subnautica agrees
        px, pz, ph = -5200.0, -2100.0, -210.0
        d = (-8.0, -4.0, -2.0, -1.0, 0.0, 1.0, 2.0, 4.0, 8.0)
        cols = [(round(px + v, 3), round(pz, 3)) for v in d] + [(round(px, 3), round(pz + v, 3)) for v in d if v != 0.0]
        hm = lambda x, z: ph + 0.05 * (x - px) - 0.03 * (z - pz)   # noqa: E731
        se = [{"x": x, "z": z, "storageY": hm(x, z), "castY": hm(x, z) + 0.02, "tile": None, "castTile": "planet"} for x, z in cols]
        sn = [{"i": j, "x": x, "z": z, "yHint": hm(x, z), "state": "ok", "hit": True, "y": hm(x, z) + 0.04} for j, (x, z) in enumerate(cols)]
        self.ev("rays", label="nf.fallback.440.fallback.se", side="se", site=440, kind="fallback", when="nf.fallback", columns=[[x, z, ph] for x, z in cols], results=se, state="done")
        self.ev("rays", label="nf.fallback.440.fallback.sn", side="sn", site=440, kind="fallback", when="nf.fallback", columns=[[x, z, ph] for x, z in cols], results=sn, state="done")
        self.ev("ring_field", label="nf.fallback.440.fallback.field", site=440, kind="fallback", when="nf.fallback", results=[[x, z, hm(x, z), hm(x, z)] for x, z in cols])
        self.ev("nf_fallback", spot={"x": px, "z": pz, "h": ph, "biome": "dunes"}, before={"failInjected": 0}, after={"failInjected": 7})
        # the drill pass on the first stamp
        for k in range(14):
            self.frame += 1
            self.sn_row("s8a.live", snFrame=self.frame, seq=300 + k, bx=41, by=18, bz=16, levelZero=True, higher=k % 4 == 0, changed=2, patched=2, rebuilds=6, swapped=6,
                        discarded=0, noMesh=0, diffMs=3, patchMs=1, ms=900 + k, level0Ms=240 + k, rebuilds0=2)
        self.ev("nf_drill", seconds=20.0, vis={"ok": True, "rows": 1200, "runMax": 0}, snBefore={"liveApplied": 30, "liveFallbacks": 0},
                snAfter={"liveApplied": 44, "liveFallbacks": 0}, verify=[{"ok": True, "served": True, "octreesCompared": 125, "octreesDiffer": 0}], batches=[[41, 18, 16]])
        self.ev("reply", side="se", cmd="de.s8a", args={"action": "ring"}, reply=dict(status), label="nf.status.end")
        self.part("nf", "end")

    def p_n11(self):
        self.part("n11", "begin")
        self.se_row("s8a.beacon", tick=self.tick, action="retrigger", removed=1)
        self.tick += 120
        self.se_row("s8a.beacon", tick=self.tick, action="added", identity=1, se=[0.0, 0.0, 0.0], sn=[764.4, 0.0, -138.6], text="[placeholder] distress call")
        self.ev("beacon", first={"ok": True, "added": 1, "shown": 1}, second={"ok": True, "added": 1, "shown": 1})
        self.part("n11", "end")

    def run(self):
        self.ev("start", kind="s8a", stamp="synthetic", parts=self.parts, mutants=[self.mutant] if self.mutant else [], world="DE Lab S8a", band=[1000.0, 1100.0],
                snEnv={}, seEnv={"DEEPENGINEERS_S8A": "on"}, storeRoot=self.root, storeKey=self.store_key, sites=self.sites, quick=False)
        online = any(p not in ("c1", "c3") for p in self.parts)
        if online:
            self.ev("install_listing", when="before", path=os.path.join(self.folder, "s8a-install-before.txt"))
            self.ev("host_slot", when="before", dir="slot0000", files={"gameinfo.json": "aa", "scene-objects.bin": "bb"})
        for p in self.parts:
            getattr(self, "p_" + p)()
        if online:
            self.ev("host_slot", when="after", dir="slot0000", files={"gameinfo.json": "aa", "scene-objects.bin": "bb"})
            self.install_after()
        self.ev("end", rc=None)
        with open(os.path.join(self.folder, "s8a-session.jsonl"), "w", encoding="utf-8") as f:
            for r in self.sess:
                f.write(json.dumps(r) + "\n")
        with open(os.path.join(self.folder, "se-0.jsonl"), "w", encoding="utf-8") as f:
            for r in self.se:
                f.write(json.dumps(r) + "\n")
        with open(os.path.join(self.folder, "sn-0.jsonl"), "w", encoding="utf-8") as f:
            for r in self.sn:
                f.write(json.dumps(r) + "\n")
        return self.folder

# the launcher's mutant groups (ROADMAP "### S8a": the parts that show each mutant)
MUTANT_PARTS = {
    "S8a.M1": ("n1", "n3"), "S8a.M2": ("n2", "n3"), "S8a.M3": ("n4",), "S8a.M4": ("n5",), "S8a.M5": ("n1", "n6"),
    "S8a.M6": ("n7",), "S8a.M7": ("n8",), "S8a.M8": ("n8",), "S8a.M9": ("n9",), "S8a.M10": ("n10",), "S8a.M11": ("n10",),
    "S8a.M12": ("n1",), "S8a.M13": ("c1",), "S8a.M14": ("n2",), "S8a.M29": ("nl",),
    "S8a.M21": ("no",), "S8a.M22": ("no",), "S8a.M23": ("no",), "S8a.M27": ("no",), "S8a.M24": ("no",), "S8a.M25": ("no",), "S8a.M26": ("nd",), "S8a.M30": ("no",),
}
STAGE_B_MUTANTS = ("S8a.M6", "S8a.M7", "S8a.M8", "S8a.M9", "S8a.M10", "S8a.M11")
PLANET_V2_MUTANTS = ("S8a.M29", "S8a.M21", "S8a.M22", "S8a.M23", "S8a.M24", "S8a.M25", "S8a.M26", "S8a.M27", "S8a.M30")   # docs/S8A-PLANET-V2.md section 5
ALL_PARTS = ("c1", "c2", "c2g", "c3", "n1", "n2", "n3", "n4", "n5", "n6", "n7", "n8", "n9", "n10", "n10c", "n11", "nd", "nf", "nl", "no", "ns", "teardown")

def synth_session(folder, mutant=None, parts=None):
    if parts is None:
        parts = MUTANT_PARTS.get(mutant, ("n1", "n2", "n3", "n5", "n6", "teardown")) if mutant else ALL_PARTS
    return Synth(folder, mutant, parts).run()

def selftest() -> int:
    ok = True
    tmp = tempfile.mkdtemp(prefix="s8a-selftest-")
    rows = evaluate(synth_session(os.path.join(tmp, "good")))
    bad = [r for r in rows if r["status"] not in ("PASS", "INFO")]
    ids = [r["id"] for r in rows]
    want_ids = ["S8a.C1.roundtrip", "S8a.C1.rules", "S8a.C2.sculpt", "S8a.C2.flat", "S8a.C2.ingame", "S8a.C2.field", "S8a.C3.pin", "S8a.N1.edit", "S8a.N1.latency", "S8a.N1.visible", "S8a.N2.position", "S8a.N2.size",
                "S8a.N2.overlap", "S8a.N3.rays", "S8a.N3.rim", "S8a.N4.save", "S8a.N4.file", "S8a.N4.portable", "S8a.N5.store", "S8a.N5.bytes", "S8a.N5.reload",
                "S8a.N6.install", "S8a.N6.writes", "S8a.N6.hostsave", "S8a.N7.rays", "S8a.N7.coverage", "S8a.N7.time", "S8a.N7.memory", "S8a.N7.edit", "S8a.N8.jump",
                "S8a.N8.snap", "S8a.N8.batchstep", "S8a.N8.picture", "S8a.N9.gate", "S8a.N9.resume", "S8a.N10.material", "S8a.N10.spawn", "S8a.N10.ore", "S8a.N10.biome",
                "S8a.N10.crater", "S8a.N11.beacon", "S8a.ND.hidden", "S8a.ND.wall", "S8a.ND.live", "S8a.ND.roundtrip",
                "S8a.NF.ready", "S8a.NF.agree", "S8a.NF.served", "S8a.NF.seam", "S8a.F.repeat", "S8a.F.forbidden", "S8a.F.blend", "S8a.NF.cost", "S8a.NF.scope",
                "S8a.NF.determinism", "S8a.NF.fallback", "S8a.NF.memory", "S8a.NF.time", "S8a.NF.drill", "S8a.NL.look",
                "S8a.NO.cover", "S8a.NO.vein", "S8a.NO.agree", "S8a.NO.shrink", "S8a.NO.immune", "S8a.ND.props", "S8a.NO.palette", "S8a.NO.voxel", "S8a.NO.look"] + list(NS_ROWS_ALL)
    missing_ids = [i for i in want_ids if i not in ids]
    print("S8a synthetic session: %d rows, %d not PASS%s%s" % (len(rows), len(bad), ": " + ", ".join("%s %s (%s)" % (
        r["id"], r["status"], r["measured"]) for r in bad) if bad else "", "; rows missing: " + ",".join(missing_ids) if missing_ids else ""))
    ok &= not bad and not missing_ids
    # planet v2 route B seeded faults (ns): each edit of the synthetic ns session turns its row red
    def ns_fault(name, edit, rid):
        folder = synth_session(os.path.join(tmp, "ns_" + name), parts=("ns", "teardown"))
        path = os.path.join(folder, "s8a-session.jsonl")
        evs = [json.loads(ln) for ln in open(path, encoding="utf-8") if ln.strip()]
        for e in evs:
            edit(e)
        with open(path, "w", encoding="utf-8") as fh:
            for e in evs:
                fh.write(json.dumps(e) + "\n")
        r = next((x for x in evaluate(folder) if x["id"] == rid), None)
        good = r is not None and r["status"] == "FAIL"
        print("  ns fault %-12s -> %s %s: %s" % (name, rid, r["status"] if r else "missing", "PASS" if good else "FAIL"))
        return good

    def ns_heightmap(e):           # SE serves the heightmap, not the chunk: the field's top departs from SE's surface
        if e.get("ev") == "rays" and e.get("kind") == "line" and e.get("side") in ("se", "sn"):
            for r in e["results"]:
                r["storageY"] = r["castY"] = r["y"] = -121.0
                r.pop("y", None) if e.get("side") == "se" else None
                if e.get("side") == "sn":
                    r["y"] = -121.0

    def ns_wall(e):                # a seam wall: SE's surface jumps 25 m at the square's edge
        if e.get("ev") == "rays" and e.get("kind") == "seam" and e.get("side") in ("se", "sn"):
            for r in e["results"]:
                if r["x"] - 4158.0 < 300.0:
                    for k in ("storageY", "castY", "y"):
                        if k in r:
                            r[k] += 25.0

    def ns_bytes(e):
        if e.get("ev") == "ns_serve" and e.get("reply") and e["reply"].get("results"):
            e["reply"]["results"][0]["providerFnv"] = "deadbeefdeadbeef"

    def ns_scope(e):
        if e.get("ev") == "reply" and e.get("label") == "ns.status.end":
            e["reply"]["servedOutside"] = 2

    def ns_missing(e):             # Subnautica spawned half of the flora
        if e.get("ev") == "ns_entities" and e.get("census"):
            e["census"]["live"] = "flora:20;light:2;prop:6;slot:110"

    def ns_landmark(e):
        if e.get("ev") == "ns_offline":
            e["rows"][0] = [False, "S8a.NS.cut 1 kept with a landmark prefab"]

    for name, edit, rid in (("heightmap", ns_heightmap, "S8a.NS.terrain"), ("wall", ns_wall, "S8a.NS.seam"), ("bytes", ns_bytes, "S8a.NS.determinism"),
                            ("scope", ns_scope, "S8a.NS.scope"), ("missing", ns_missing, "S8a.NS.entities"), ("landmark", ns_landmark, "S8a.NS.cut")):
        ok &= ns_fault(name, edit, rid)
    # Stage F2 seeded faults (nf): each edit of the synthetic nf session turns its row red
    def nf_fault(name, edit, rid):
        folder = synth_session(os.path.join(tmp, "nf_" + name), parts=("nf", "teardown"))
        path = os.path.join(folder, "s8a-session.jsonl")
        evs = [json.loads(ln) for ln in open(path, encoding="utf-8") if ln.strip()]
        for e in evs:
            edit(e)
        with open(path, "w", encoding="utf-8") as fh:
            for e in evs:
                fh.write(json.dumps(e) + "\n")
        r = next((x for x in evaluate(folder) if x["id"] == rid), None)
        good = r is not None and r["status"] == "FAIL"
        print("  nf fault %-12s -> %s %s: %s" % (name, rid, r["status"] if r else "missing", "PASS" if good else "FAIL"))
        return good

    def served_outside(e):
        if e.get("ev") == "reply" and e.get("label") == "nf.status.end":
            e["reply"]["servedOutside"] = 1

    def bytes_differ(e):
        if e.get("ev") == "nf_serve" and e.get("reply"):
            e["reply"]["results"][0]["providerFnv"] = "deadbeefdeadbeef"

    def hole(e):
        if e.get("ev") == "rays" and e.get("kind") == "fallback" and e.get("side") == "se":
            e["results"] = e["results"][1:]

    def slow(e):
        if e.get("ev") == "reply" and e.get("label") == "nf.status.end":
            for c in e["reply"]["cost"]:
                if c["cat"] == "served":
                    c["p95"] *= 10.0

    def heightmap(e):
        if e.get("ev") == "ring_field" and e.get("kind") == "path":
            e["results"] = [[x, z, top - 3.0, h] for x, z, top, h in e["results"]]

    # nd rerun: cell rows whose camera left the pass's spot are not judged; most of them: ND.hidden /.wall MISSING
    def nd_camera(far):
        folder = synth_session(os.path.join(tmp, "nd_cam_%s" % far), parts=("nd", "teardown"))
        sp = os.path.join(folder, "s8a-session.jsonl")
        evs = [json.loads(ln) for ln in open(sp, encoding="utf-8") if ln.strip()]
        for e in evs:
            if e.get("ev") == "drill_pass":
                e["camSn"] = [40.0, -11.0, 46.0]
        with open(sp, "w", encoding="utf-8") as fh:
            for e in evs:
                fh.write(json.dumps(e) + "\n")
        np_ = os.path.join(folder, "sn-0.jsonl")
        rs = [json.loads(ln) for ln in open(np_, encoding="utf-8") if ln.strip()]
        for r_ in rs:
            if r_.get("ev") == "s8a.cellvis":
                r_["data"].update({"camX": 40.0 + (46.0 if far else 0.5), "camY": -11.0, "camZ": 46.0})
        with open(np_, "w", encoding="utf-8") as fh:
            for r_ in rs:
                fh.write(json.dumps(r_) + "\n")
        return {x["id"]: x["status"] for x in evaluate(folder)}
    near_st, far_st = nd_camera(False), nd_camera(True)
    good = near_st.get("S8a.ND.hidden") == "PASS" and far_st.get("S8a.ND.hidden") == "MISSING" and far_st.get("S8a.ND.wall") == "MISSING"
    print("  nd camera held: hidden %s; camera 46 m away: hidden %s, wall %s: %s" % (near_st.get("S8a.ND.hidden"), far_st.get("S8a.ND.hidden"), far_st.get("S8a.ND.wall"),
                                                                                     "PASS" if good else "FAIL"))
    ok &= good
    for name, edit, rid in (("scope", served_outside, "S8a.NF.scope"), ("bytes", bytes_differ, "S8a.NF.determinism"), ("hole", hole, "S8a.NF.fallback"),
                            ("cost", slow, "S8a.NF.cost"), ("heightmap", heightmap, "S8a.NF.served")):
        ok &= nf_fault(name, edit, rid)
    for m in STAGE_A_MUTANTS + STAGE_B_MUTANTS + PLANET_V2_MUTANTS:
        want = MUTANT_RED[m]
        mrows = evaluate(synth_session(os.path.join(tmp, m.replace(".", "_")), m))
        red = [r["id"] for r in mrows if r["id"] in want and r["status"] == "FAIL"]
        mr = next((r for r in mrows if r["id"] == "%s.red" % m), None)
        others = [r["id"] for r in mrows if r["status"] in ("FAIL", "MISSING") and r["id"] not in want and r.get("required", True)
                  and r["id"] not in MUTANT_ALSO_MISSING.get(m, ())]
        also = [r["id"] for r in mrows if r["id"] in MUTANT_ALSO_RED.get(m, ()) and r["status"] == "FAIL"]
        good = sorted(red) == sorted(want) and mr is not None and mr["status"] == "PASS" and not others
        print("  %-7s red %s (want %s), .red %s%s%s" % (m, ",".join(red) or "none", ",".join(want), mr["status"] if mr else "-",
                                                      ", other required: " + ",".join(others) if others else "",
                                                      ", also red by its mechanics (documented): " + ",".join(also) if also else ""))
        ok &= good
    # a session without a seam site, without n4: the N2.overlap row is MISSING (required): the table does not pass
    s = Synth(os.path.join(tmp, "noseam"), None, ("n2",))
    s.sites = [dict(x) for x in SYNTH_SITES if not x["seam"]]
    rows2 = evaluate(s.run())
    ov = next((r for r in rows2 if r["id"] == "S8a.N2.overlap"), None)
    good = ov is not None and ov["status"] == "MISSING" and not C.verdict(rows2, True)
    print("  no seam site: S8a.N2.overlap %s, table %s: %s" % (ov and ov["status"], "fails" if not C.verdict(rows2, True) else "passes", "PASS" if good else "FAIL"))
    ok &= good
    # water batches log no file (path null): N6.writes counts file writes only; a water row that names a file is a fault
    s = Synth(os.path.join(tmp, "water_path"), None, ("n7", "n6"))
    s.water_with_path = True
    wr = next((r for r in evaluate(s.run()) if r["id"] == "S8a.N6.writes"), None)
    s2 = Synth(os.path.join(tmp, "water_nopath"), None, ("n7", "n6"))
    wr2 = next((r for r in evaluate(s2.run()) if r["id"] == "S8a.N6.writes"), None)
    good = wr is not None and wr["status"] == "FAIL" and wr2 is not None and wr2["status"] == "PASS"
    print("  water batches without a file: N6.writes %s; a water row naming a file: %s: %s" % (wr2 and wr2["status"], wr and wr["status"], "PASS" if good else "FAIL"))
    ok &= good
    # Stage C step 1: a planet 80 m higher than the tool says turns S8a.C2.ingame red; one probe without the expectation leaves it MISSING
    s = Synth(os.path.join(tmp, "c2g_offset"), None, ("c2g",))
    s.c2g_offset = 80.0
    ing = next((r for r in evaluate(s.run()) if r["id"] == "S8a.C2.ingame"), None)
    good = ing is not None and ing["status"] == "FAIL"
    print("  c2g with SE's surface 80 m above the tool: S8a.C2.ingame %s: %s" % (ing and ing["status"], "PASS" if good else "FAIL"))
    ok &= good
    # the carve fit recovers a shifted, resized hole from noisy columns (the inner cap only, as the rows use it)
    allc = [(x, z, SEABED, hole_depth(40.3, SEABED - 0.2, 39.8, 2.4 * 1.125, 2.4, x, z, SEABED) + 0.01 * math.sin(x * 9 + z)) for x, z in grid_columns(40.0, 40.0, 3.0, 9)]
    cols = [c for c in allc if c[2] - c[3] >= CARVE_MIN_DEPTH_M]
    p, res = fit_carve(cols, (40.0, SEABED, 40.0, 2.5), 1.125)
    good = abs(p[0] - 40.3) < 0.05 and abs(p[2] - 39.8) < 0.05 and abs(p[1] - (SEABED - 0.2)) < 0.05 and abs(p[4] - 2.4) < 0.05 and abs(p[3] - 2.7) < 0.06 and max(res) < 0.05
    print("  carve fit (%d cap columns): centre (%.2f, %.2f, %.2f) r %.2f (rh %.2f), max residual %.3f: %s" % (len(cols), p[0], p[1], p[2], p[4], p[3], max(res), "PASS" if good else "FAIL"))
    ok &= good
    print("analyze_s8a selftest %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv) -> int:
    if "--selftest" in argv:
        return selftest()
    lenient = "--lenient" in argv
    args = [a for a in argv if not a.startswith("--")]
    if not args:
        print(__doc__)
        return 2
    folder = args[0]
    rows = evaluate(folder)
    ok = write_results(folder, rows, not lenient)
    if "--json" in argv:
        print(json.dumps(rows, indent=1, default=str))
    else:
        print(table(rows))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
