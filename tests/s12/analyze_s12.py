"""S12 oracle table (ROADMAP §14 "### S12 Interiors v2"; docs/S12-BRIEF.md; CONTRACT §5.4 "S12 conventions").

 python -I tests/s12/analyze_s12.py <tests/out/s12/<stamp>> [--lenient] [--json]
 python -I tests/s12/analyze_s12.py --selftest

Reads a folder written by `launcher.py s12` (s12-session.jsonl; se-*.jsonl, sn-*.jsonl; captures/) and judges, with the Python
twin's tables and rules (tools/s12/s12_ref.py; never the plugins' own numbers), from SE's rows (s7.interior: the C1 answer;
s12.air, s12.flood, s12.vent), Subnautica's rows (s12.plane, s12.fx, s12.look) and the captures (measured pixels):

 S12.PIN selftest both plugins' s12.site hash is the twin's pin
 S12.AIR.OUT air every outside point (2 m south, 0.3 m from the north window wall, the vent's wall, the east wall,
 the roof, 0.3 m outside the east window's glass): no s7.interior row in a room; S12b: the S7 patch
 on/off captures there differ by <= 0.02 (the median patch of the lower half: the view stays under water)
 S12.AIR.IN air the inside points: every row in the room; S12b: patch on/off differ by >= 0.05 (a dry interior)
 S12.AIR.FLIP air each scripted crossing of the east wall at a plain block (1 cm steps with +-4 cm jitter, out then in):
 exactly one change of the C1 answer (the hysteresis of format air.hold; S12.M1's raw cell flickers)
 S12.AIR.HOLD air after a recompute started with the camera inside and the camera moved out: no row in the room
 (INFO when no recompute overlapped the move: nothing measured)
 S12.AIR.CHAR air the character's swim gate: characterInRoom false 0.4 m outside the east wall, true inside
 S12.AIR.THIRD air the camera inside while the character swims outside, as third person (de.s12 third): no row in a room
 (format air.
 S12.AIR.WINDOW.IN air the diver pressed against the east window from inside (its eye in the window's cell, 0.4 m from the

 S12.AIR.WINDOW.OUT air the diver pressed against the window's glass from outside: characterInRoom false on every row
 S12.AIR.OPEN flood the house opened (a wall block removed) with the diver inside: no row in a room for the camera or the
 S12b: the
 rising capture is not the dry picture (the median patch differs by >= 0.05)
 S12.AIR.WATER flood while the room drains at about half height: the camera under the plane has no air, above it has
 S12.FLOOD.FILL flood the breach: the row's fill time is the twin's (Torricelli: cells, grid size, holes, depth) and the
 level reaches full within 0.85 T - 0.3 s.. 1.15 T + 0.6 s of the start, never falling while rising
 S12.FLOOD.ONCE flood one start per breach on SE's rows (startSeq + 1); S12b: one Subnautica splash per SE start
 S12.FLOOD.DRAIN flood after the repair: every draining row's level >= the water of SE's raw room oxygen (D = its
 environment oxygen) and <= the level at the repair, falling no faster than a full room in DrainMs
 (+15 %), and the track ends dry
 S12.FLOOD.LINE flood S12b: the waterline found in each draining capture (a colour step down the far wall's column) within
 LineTolPermille of the frame height of SE's level at the capture (the plane measured)
 S12.FLOOD.
 doorways and attached blocks; S12.M10's bounding box reaches the wall layer)
 S12.FLOOD.MOVE wade the diver walking in the sealed house while its vent lets the water in and out: under the room's plane
 (eye 0.2 m below) the surface is the room's and S2b swims, above it (0.2 m over) S2b lets SE walk; the
 swim latch changes at most once per crossing; while the script walks, never 1 s at a speed under 0.05
 S12.FLOOD.
 S12.FLOOD.DEBOUNCE airlock the vent rig split by an inner sliding door, an outer door in the chamber; the doors cycled: every

 airlock that filled and flushed)
 S12.FLOOD.
 ceiling and one on its floor)
 S12.FLOOD.PLANE flood S12b: the half-full capture's lower part differs from the dry one after by >= 0.04; Subnautica's
 s12.plane rows drawn (their own transforms: information)
 S12.VENT vent the rig's Air Vent: SE's own vent effect seen (seEffect) at a start; S12b: one Subnautica burst per SE
 start, a stream, and the capture at the vent's mouth differs from the idle one by >= CaptureMinPermille
 S12.CAPS flood S12b: every s12.fx row emitted <= asked, a burst <= BurstBubbles, a splash <= SplashBubbles, a vent's
 stream <= BubblesPerS + 1 per second (and S7p's per-frame budget counted by S7's own rows)
 S12.LOOK.PRESET look S12b: inside at noon and at night, the preset on is darker than off by >= DarkerPermille (luma) and
 cooler (b - r larger); Subnautica's s12.look rows applied while inside under water; the switch sent as

 S12.LOOK.WINDOW look S12b: the water seen through the window from inside: on vs off <= WindowTolPermille at its pixel
 S12.LOOK.EXT look S12b: outside the house the preset changes nothing: on vs off <= ExteriorTolPermille
 S12.LOOK.REF lookref INFO: the interior's mean colour against a capture inside a Subnautica base (the reference)
 S12.Mx.red a seeded failure turns its rows red: every one measured is FAIL, at least one measured (MUTANT_ROWS;
 the launcher's mutant groups run SE's with SE alone, Subnautica's with both games)

The air rows of a window are the rows inside it led by the answer carried into it. Picture
comparisons take the median over the probe grid of each patch's difference (Subnautica's breath bubbles, fish and the animated
surface move a few patches; a dry interior, the water plane or the preset moves most).
"""
from __future__ import annotations

import glob
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tests", "s7"), os.path.join(ROOT, "tools", "s12")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C           # noqa: E402 (row, table, verdict, jsonl reader)
import s12_ref as T             # noqa: E402 (the twin)
import s7_interior_check as IMG  # noqa: E402 (PNG read/write, probes)

row = C.row
MUTANT_ROWS = {"S12.M1": ("S12.AIR.FLIP", "S12.AIR.HOLD", "S12.AIR.THIRD"), "S12.M2": ("S12.FLOOD.FILL",), "S12.M3": ("S12.FLOOD.DRAIN",),
               "S12.M4": ("S12.FLOOD.ONCE",), "S12.M5": ("S12.VENT",), "S12.M6": ("S12.LOOK.PRESET",), "S12.M7": ("S12.FLOOD.PLANE", "S12.FLOOD.LINE"),
               "S12.M8": ("S12.AIR.OPEN",), "S12.M9": ("S12.AIR.WINDOW.IN",), "S12.M10": ("S12.FLOOD.CLIP",), "S12.M11": ("S12.FLOOD.MOVE",),
               "S12.M12": ("S12.FLOOD.DEBOUNCE", "S12.FLOOD.ONEPLANE")}
SN_MUTANTS = ("S12.M6", "S12.M7")
OUT_TOL = 0.02
IN_MIN = 0.05
PLANE_MIN = 0.04
PLANE_Y_TOL = 0.15
DRAIN_TOL = 2e-3
RATE_SLACK = 1.15
WINDOW_LEAD_S = 0.25
GRID = (24, 14)            # capture statistics: the frame sampled on a 24 x 14 grid of 7 x 7 patches

_PIXEL = re.compile(r"DE\.SnPixel label=(\S+) .*?px=\(([-0-9.eE+]+), ([-0-9.eE+]+)\)")
_PIXEL_DEPTH = re.compile(r" depth=([-0-9.eE+]+)")
_PIXEL_SCREEN = re.compile(r"screen=\((\d+), (\d+)\)")

def _utc(s):
    return C.Session._utc_seconds(s)

def parse_se(recs):
    out = []
    for r in recs:
        ev = r.get("ev")
        if isinstance(ev, str) and (ev.startswith("s12.") or ev == "s7.interior"):
            out.append(dict(r, _t=_utc(r.get("t"))))
    return out

def parse_sn(recs):
    out = []
    for r in recs:
        ev = r.get("ev") or r.get("event")
        if not isinstance(ev, str) or not ev.startswith("s12."):
            continue
        d = dict(r["data"]) if isinstance(r.get("data"), dict) else {k: v for k, v in r.items() if k not in ("data",)}
        d["ev"] = ev
        d["_t"] = _utc(r.get("t"))
        out.append(d)
    return out

class Session:
    def __init__(self, folder):
        self.dir = folder
        self.session = C.read_jsonl(os.path.join(folder, "s12-session.jsonl"))
        self.se = parse_se(C.read_jsonl(sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))))
        sn_recs = C.read_jsonl(sorted(glob.glob(os.path.join(folder, "sn-*.jsonl"))))
        self.sn = parse_sn(sn_recs)
        self.sn_text = C.text_lines(sn_recs)
        self.start = next((e for e in self.session if e.get("ev") == "start"), {}) or {}
        self.mode = self.start.get("mode")
        self.parts = list(self.start.get("parts") or [])
        self.mutants = list(self.start.get("mutants") or [])
        self.windows = {}
        opened = {}
        for e in self.session:
            if e.get("ev") != "part":
                continue
            t = _utc(e.get("utc"))
            if e.get("phase") == "begin":
                opened[e.get("name")] = t
            elif e.get("phase") == "end" and e.get("name") in opened:
                self.windows[e["name"]] = (opened.pop(e["name"]), t)

    def ran(self, *parts):
        return any(p in self.parts for p in parts)

    def se_rows(self, ev):
        return [r for r in self.se if r.get("ev") == ev]

    def sn_rows(self, ev):
        return [r for r in self.sn if r.get("ev") == ev]

    def in_window(self, rows, name, lead=0.0):
        w = self.windows.get(name)
        if not w or w[0] is None or w[1] is None:
            return None
        return [r for r in rows if r.get("_t") is not None and w[0] + lead <= r["_t"] <= w[1]]

    def state_rows(self, rows, name, lead=0.0):
        """The rows of window `name` after its start + lead, led by the last row at or before that time (marked _carried): the
 answer it carried in. S7's and S12's rows are written at every change (InteriorProbe.Row, S12Air), periodic ones only
 while the camera is in a grid's box, so a camera outside the house writes none and its answer is the carried one."""
        w = self.windows.get(name)
        if not w or w[0] is None or w[1] is None:
            return None
        t0 = w[0] + lead
        prior = [r for r in rows if r.get("_t") is not None and r["_t"] <= t0]
        inside = [r for r in rows if r.get("_t") is not None and t0 < r["_t"] <= w[1]]
        if prior:
            last = max(prior, key=lambda r: r["_t"])
            inside = [dict(last, _carried=True)] + inside
        return inside

    def names(self, prefix):
        return sorted(n for n in self.windows if n.startswith(prefix))

    def events(self, ev):
        return [e for e in self.session if e.get("ev") == ev]

    def pixels(self):
        """{label: (x, y, depth, (width, height))} of the DE.SnPixel lines."""
        out = {}
        for line in self.sn_text:
            m = _PIXEL.search(line)
            if not m:
                continue
            d = _PIXEL_DEPTH.search(line)
            sc = _PIXEL_SCREEN.search(line)
            out[m.group(1)] = (float(m.group(2)), float(m.group(3)), float(d.group(1)) if d else None, (int(sc.group(1)), int(sc.group(2))) if sc else None)
        return out

    def capture_time(self, name):
        for e in self.events("capture"):
            if e.get("name") == name:
                return _utc(e.get("utc"))
        return None

    def capture(self, name):
        for e in self.events("capture"):
            if e.get("name") == name and e.get("path"):
                rel = e["path"]
                for base in (os.path.join(self.dir, "captures"), self.dir):
                    p = os.path.join(base, os.path.basename(rel))
                    if os.path.exists(p):
                        return p
                    p = os.path.join(base, rel)
                    if os.path.exists(p):
                        return p
        return None

# ------------------------------------------------------------------------------------------------------------- images

_PNG = {}

def _png(path):
    """IMG.read_png, decoded once per file version (path, size, mtime)."""
    st = os.stat(path)
    key = (os.path.abspath(path), st.st_size, st.st_mtime_ns)
    v = _PNG.get(key)
    if v is None:
        if len(_PNG) > 24:
            _PNG.clear()
        v = _PNG[key] = IMG.read_png(path)
    return v

def frame_stats(path, region=None):
    """(mean rgb, mean luma, b - r) over a grid of probes of the frame (region = (x0, y0, x1, y1) fractions)."""
    w, h, ch, rows = _png(path)
    x0, y0, x1, y1 = region or (0.0, 0.0, 1.0, 1.0)
    acc, n = [0.0, 0.0, 0.0], 0
    gx, gy = GRID
    for j in range(gy):
        for i in range(gx):
            px = int((x0 + (x1 - x0) * (i + 0.5) / gx) * w)
            py = int((y0 + (y1 - y0) * (j + 0.5) / gy) * h)
            for yy in range(max(0, py - 3), min(h, py + 4)):
                line = rows[yy]
                for xx in range(max(0, px - 3), min(w, px + 4)):
                    for k in range(3):
                        acc[k] += line[xx * ch + (k if ch >= 3 else 0)]
                    n += 1
    if n == 0:
        return None
    rgb = [a / (255.0 * n) for a in acc]
    luma = 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]
    return rgb, luma, rgb[2] - rgb[0]

def frame_mad(a, b, region=None):
    """Mean absolute RGB difference of two captures on the probe grid (0..1)."""
    wa, ha, cha, ra = _png(a)
    wb, hb, chb, rb = _png(b)
    x0, y0, x1, y1 = region or (0.0, 0.0, 1.0, 1.0)
    gx, gy = GRID
    tot, n = 0.0, 0
    for j in range(gy):
        for i in range(gx):
            fx, fy = x0 + (x1 - x0) * (i + 0.5) / gx, y0 + (y1 - y0) * (j + 0.5) / gy
            pa = (int(fx * wa), int(fy * ha))
            pb = (int(fx * wb), int(fy * hb))
            for k in range(3):
                va = ra[pa[1]][pa[0] * cha + (k if cha >= 3 else 0)]
                vb = rb[pb[1]][pb[0] * chb + (k if chb >= 3 else 0)]
                tot += abs(va - vb) / 255.0
            n += 3
    return tot / n if n else None

def patch(path, x, y, r=7):
    w, h, ch, rows = _png(path)
    acc, n = [0.0, 0.0, 0.0], 0
    for yy in range(max(0, int(y) - r), min(h, int(y) + r + 1)):
        line = rows[yy]
        for xx in range(max(0, int(x) - r), min(w, int(x) + r + 1)):
            for k in range(3):
                acc[k] += line[xx * ch + (k if ch >= 3 else 0)]
            n += 1
    return [a / (255.0 * n) for a in acc] if n else None

def waterline(path, x, y0, y1, half_w=4, win=3):
    """(row of the largest colour step down column x between rows y0 and y1, its size): the mean RGB of `win` rows above and below."""
    w, h, ch, rows = _png(path)
    win = max(win, h // 100)
    a, b = int(max(0, min(y0, y1) - win)), int(min(h - 1, max(y0, y1) + win))
    xs = range(max(0, int(x) - half_w), min(w, int(x) + half_w + 1))
    col = []
    for yy in range(a, b + 1):
        line = rows[yy]
        col.append([sum(line[xx * ch + (k if ch >= 3 else 0)] for xx in xs) / (255.0 * len(xs)) for k in range(3)])
    best, best_y = 0.0, None
    for i in range(win, len(col) - win):
        up = [sum(col[i - j][k] for j in range(1, win + 1)) / win for k in range(3)]
        dn = [sum(col[i + j][k] for j in range(0, win)) / win for k in range(3)]
        step = sum(abs(up[k] - dn[k]) for k in range(3)) / 3.0
        if step > best:
            best, best_y = step, a + i
    return best_y, best

def _probe_means(path, region):
    w, h, ch, rows = _png(path)
    x0, y0, x1, y1 = region or (0.0, 0.0, 1.0, 1.0)
    gx, gy = GRID
    out = []
    for j in range(gy):
        for i in range(gx):
            px = int((x0 + (x1 - x0) * (i + 0.5) / gx) * w)
            py = int((y0 + (y1 - y0) * (j + 0.5) / gy) * h)
            acc, n = [0.0, 0.0, 0.0], 0
            for yy in range(max(0, py - 3), min(h, py + 4)):
                line = rows[yy]
                for xx in range(max(0, px - 3), min(w, px + 4)):
                    for k in range(3):
                        acc[k] += line[xx * ch + (k if ch >= 3 else 0)]
                    n += 1
            out.append([a / (255.0 * n) for a in acc] if n else [0.0, 0.0, 0.0])
    return out

def frame_med(a, b, region=None):
    """The median over the probe grid of each 7 x 7 patch's mean RGB difference (0..1): a whole-picture change (a dry interior,
 the water plane, the preset) moves most patches; Subnautica's breath bubbles, fish and the animated surface move a few."""
    pa, pb = _probe_means(a, region), _probe_means(b, region)
    d = sorted(sum(abs(u[k] - v[k]) for k in range(3)) / 3.0 for u, v in zip(pa, pb))
    if not d:
        return None
    n = len(d)
    return d[n // 2] if n % 2 else 0.5 * (d[n // 2 - 1] + d[n // 2])

# ------------------------------------------------------------------------------------------------------------- checks

def interior_rows(S):
    return S.se_rows("s7.interior")

def check_pin(S, rows):
    want = "0x%016x" % T.PINNED_HASH
    se = [r.get("hash") for r in S.se_rows("s12.site")]
    sn = [r.get("hash") for r in S.sn_rows("s12.site")]
    if not se:
        row(rows, "S12.PIN", "MISSING", "no SE s12.site row", want, "the conventions pin of both plugins", "selftest")
        return
    bad = [h for h in se + sn if h != want]
    need_sn = S.mode == "S12b"
    st = "FAIL" if bad else ("MISSING" if need_sn and not sn else "PASS")
    row(rows, "S12.PIN", st, "SE %s, SN %s" % (se[-1], sn[-1] if sn else "-"), want, "the conventions pin of both plugins", "selftest")

OUT_REGION = (0.0, 0.5, 1.0, 1.0)   # outside: the lower half (the house and the seafloor; the roof point is 0.45 m under the surface)

def _patch_mad(S, name, region=None):
    a, b = S.capture(name + ".patch-on"), S.capture(name + ".patch-off")
    if not a or not b:
        return None
    return frame_med(a, b, region)

def check_air_out(S, rows):
    names = S.names("air.out.")
    if not names:
        row(rows, "S12.AIR.OUT", "MISSING", "no air.out window", "no row in a room", "outside the house: no air", "air")
        return
    bad, n, mads, worst = [], 0, [], 0.0
    for nm in names:
        rs = S.state_rows(interior_rows(S), nm, WINDOW_LEAD_S) or []
        n += len(rs)
        if any(r.get("inRoom") for r in rs):
            bad.append(nm)
        m = _patch_mad(S, nm, OUT_REGION) if S.mode == "S12b" else None
        if m is not None:
            mads.append(m)
            worst = max(worst, m)
            if m > OUT_TOL:
                bad.append(nm + " picture %.3f" % m)
    if n == 0:
        row(rows, "S12.AIR.OUT", "MISSING", "no s7.interior row in or before %d windows" % len(names), "no row in a room", "outside the house: no air", "air")
        return
    pic = (", patch on/off max %.3f" % worst) if mads else ""
    st = "FAIL" if bad else ("MISSING" if S.mode == "S12b" and not mads else "PASS")
    house = (S.events("house") or [{}])[0]
    if "air.out.glass" in names and house.get("window") is False:
        pic += " (no window: the swap was not done, the glass point faces a plain wall)"
    row(rows, "S12.AIR.OUT", st, "%d rows in %d points%s%s" % (n, len(names), pic, (" bad " + ",".join(bad)) if bad else ""),
        "none in a room; picture <= %.2f" % OUT_TOL, "outside a sealed house under water: no air, the view under water", "air")

def check_air_in(S, rows):
    names = S.names("air.in.")
    if not names:
        row(rows, "S12.AIR.IN", "MISSING", "no air.in window", "every row in the room", "inside the house: air", "air")
        return
    bad, n, mads = [], 0, []
    for nm in names:
        rs = S.state_rows(interior_rows(S), nm, WINDOW_LEAD_S) or []
        n += len(rs)
        if not rs or not all(r.get("inRoom") for r in rs):
            bad.append(nm)
        m = _patch_mad(S, nm) if S.mode == "S12b" else None
        if m is not None:
            mads.append(m)
            if m < IN_MIN:
                bad.append(nm + " picture %.3f" % m)
    st = "FAIL" if bad else ("MISSING" if S.mode == "S12b" and not mads else "PASS")
    row(rows, "S12.AIR.IN", st, "%d rows in %d points%s%s" % (n, len(names), (", patch on/off min %.3f" % min(mads)) if mads else "",
                                                             (" bad " + ",".join(bad)) if bad else ""),
        "all in the room; picture >= %.2f" % IN_MIN, "inside the house: air, a dry interior", "air")

def _flips(rs, key="inRoom"):
    rs = sorted(rs, key=lambda r: r["_t"])
    f, prev = 0, None
    for r in rs:
        v = bool(r.get(key))
        if prev is not None and v != prev:
            f += 1
        prev = v
    return f, (rs[0].get(key) if rs else None), (rs[-1].get(key) if rs else None)

def check_air_flip(S, rows):
    names = S.names("air.cross.")
    if not names:
        row(rows, "S12.AIR.FLIP", "MISSING", "no crossing", "1 flip per crossing", "crossing the wall flips the air once", "air")
        return
    res, bad = [], []
    for nm in names:
        rs = S.state_rows(interior_rows(S), nm) or []
        f, first, last = _flips(rs)
        res.append("%s %d" % (nm.split(".")[-1], f))
        if f != 1 or first == last:
            bad.append(nm)
    row(rows, "S12.AIR.FLIP", "FAIL" if bad else "PASS", "flips " + ", ".join(res), "exactly 1 per crossing",
        "crossing the wall with +-4 cm jitter flips the air once (format air.hold)", "air")

def check_air_hold(S, rows):
    names = S.names("air.hold.")
    if not names:
        row(rows, "S12.AIR.HOLD", "MISSING", "no hold window", "no row in a room", "no air held after leaving during a recompute", "air", required=False)
        return
    overlap, leaks = 0, 0
    for nm in names:
        rs = S.state_rows(interior_rows(S), nm, WINDOW_LEAD_S) or []
        overlap += sum(1 for r in rs if r.get("processing"))
        leaks += sum(1 for r in rs if r.get("inRoom"))
    if overlap == 0 and leaks == 0:
        row(rows, "S12.AIR.HOLD", "INFO", "no recompute overlapped the move (%d windows)" % len(names), "no row in a room",
            "no air held after leaving during a recompute (nothing measured)", "air", required=False)
        return
    row(rows, "S12.AIR.HOLD", "FAIL" if leaks else "PASS", "%d row(s) in a room, %d processing row(s)" % (leaks, overlap), "0 in a room",
        "no air held after leaving during a recompute (S7's held answer)", "air")

def check_air_char(S, rows):
    out = S.state_rows(interior_rows(S), "air.char.out", WINDOW_LEAD_S)
    inn = S.state_rows(interior_rows(S), "air.char.in", WINDOW_LEAD_S)
    if out is None or inn is None or not out or not inn:
        row(rows, "S12.AIR.CHAR", "MISSING", "no character windows", "out false, in true", "the character's swim gate", "air")
        return
    bad_out = sum(1 for r in out if r.get("characterInRoom"))
    bad_in = sum(1 for r in inn if not r.get("characterInRoom"))
    row(rows, "S12.AIR.CHAR", "FAIL" if bad_out or bad_in else "PASS", "outside %d/%d in a room, inside %d/%d out" % (bad_out, len(out), bad_in, len(inn)),
        "0 / 0", "the character 0.4 m outside the wall has no air, inside it has", "air")

def _floods(S):
    return sorted(S.se_rows("s12.flood"), key=lambda r: r["_t"])

def _breach_track(S):
    fl = _floods(S)
    for r in fl:
        if r.get("source") == "breach" and r.get("state") == "rising":
            return r.get("track"), [x for x in fl if x.get("track") == r.get("track")]
    return None, []

def check_air_water(S, rows):
    below = S.state_rows(interior_rows(S), "flood.water.below", WINDOW_LEAD_S)
    above = S.state_rows(interior_rows(S), "flood.water.above", WINDOW_LEAD_S)
    if not below or not above:
        row(rows, "S12.AIR.WATER", "MISSING", "no water windows", "below out, above in", "no air under a flood plane", "flood")
        return
    bad = sum(1 for r in below if r.get("inRoom")) + sum(1 for r in above if not r.get("inRoom"))
    _tid, tr = _breach_track(S)
    w = S.windows.get("flood.water.below")
    lv = [r.get("level") for r in tr if w and w[0] - 2 <= r["_t"] <= w[1] + 2]
    row(rows, "S12.AIR.WATER", "FAIL" if bad else "PASS", "%d wrong of %d rows (level %s)" % (bad, len(below) + len(above), ",".join("%.2f" % x for x in lv[:3]) or "-"),
        "under the plane out, above in", "no air under a flood plane (format air.water)", "flood")

def check_fill(S, rows):
    tid, tr = _breach_track(S)
    if not tr:
        row(rows, "S12.FLOOD.FILL", "MISSING", "no breach flood rows", "twin fill time", "the breach fills at the Torricelli rate", "flood")
        return
    start = next(r for r in tr if r.get("state") == "rising")
    full = next((r for r in tr if r.get("state") == "full" and r["_t"] >= start["_t"]), None)
    want = T.fill_ms(int(start.get("cells", 0)), float(start.get("gridSize", 2.5)), int(start.get("holes", 1)), float(start.get("depth", 0.0)))
    got = float(start.get("fillMs", -1))
    issues = []
    if abs(got - want) > 1.0:
        issues.append("fillMs %.1f vs the twin's %.1f" % (got, want))
    if full is None:
        issues.append("never full")
        el = None
    else:
        el = full["_t"] - start["_t"]
        lo, hi = 0.85 * want / 1000.0 - 0.3, 1.15 * want / 1000.0 + 0.6
        if not lo <= el <= hi:
            issues.append("full after %.2f s (%.2f..%.2f)" % (el, lo, hi))
    rising = [r for r in tr if r.get("state") == "rising" and (full is None or r["_t"] <= full["_t"])]
    if any(rising[i + 1].get("level", 0) < rising[i].get("level", 0) - 1e-6 for i in range(len(rising) - 1)):
        issues.append("the level fell while rising")
    row(rows, "S12.FLOOD.FILL", "FAIL" if issues else "PASS", "T %.2f s, full after %s s" % (want / 1000.0, "%.2f" % el if el is not None else "-") + ("; " + "; ".join(issues) if issues else ""),
        "the twin's T, full in 0.85T-0.3..1.15T+0.6 s", "the breach: the plane rises to the full height at the Torricelli rate (format flood.fill)", "flood")

def check_once(S, rows):
    breaches = len([e for e in S.events("breach") if (e.get("reply") or {}).get("ok")])
    tid, tr = _breach_track(S)
    if not breaches or not tr:
        row(rows, "S12.FLOOD.ONCE", "MISSING", "%d breach(es), %d rows" % (breaches, len(tr)), "1 start per breach", "one effect per flood event", "flood")
        return
    seqs = sorted({int(r.get("startSeq", 0)) for r in tr if int(r.get("startSeq", 0)) > 0})
    se_starts = max(seqs) if seqs else 0
    issues = []
    if se_starts != breaches:
        issues.append("SE startSeq %d for %d breach(es)" % (se_starts, breaches))
    sn_n = None
    if S.mode == "S12b":
        sp = [r for r in S.sn_rows("s12.fx") if r.get("kind") == "splash" and int(r.get("id", -1)) == int(tid)]
        sn_n = len(sp)
        if sn_n != len(seqs):
            issues.append("Subnautica %d splash(es) for %d SE start(s)" % (sn_n, len(seqs)))
    row(rows, "S12.FLOOD.ONCE", "FAIL" if issues else "PASS", "SE starts %d, breaches %d%s" % (se_starts, breaches, ", SN splashes %d" % sn_n if sn_n is not None else "") +
        ("; " + "; ".join(issues) if issues else ""), "1 per breach, 1 splash per start", "exactly one effect per flood event", "flood")

def check_air_third(S, rows):
    rs = S.state_rows(interior_rows(S), "air.third", WINDOW_LEAD_S)
    if not rs:
        row(rows, "S12.AIR.THIRD", "MISSING", "no air.third rows", "no row in a room", "third person: the character outside gives the camera no air", "air")
        return
    bad = sum(1 for r in rs if r.get("inRoom"))
    row(rows, "S12.AIR.THIRD", "FAIL" if bad else "PASS", "%d of %d rows in a room" % (bad, len(rs)), "0",
        "the camera inside the house while the character swims outside, third person: no air (format air.camera)", "air")

def _char_rows(S, window, lead=0.0):
    return S.in_window(S.se_rows("s12.char"), window, lead) or []

def check_air_window(S, rows):
    for side, rid, want in (("in", "S12.AIR.WINDOW.IN", True), ("out", "S12.AIR.WINDOW.OUT", False)):
        rs = S.state_rows(interior_rows(S), "air.window." + side, WINDOW_LEAD_S + 0.5)
        what = ("the diver pressed against the window from inside stays in the room, no swim" if want else
                "the diver pressed against the window's glass from outside is out")
        if not rs:
            row(rows, rid, "MISSING", "no air.window.%s rows" % side, "characterInRoom %s" % want, what, "air")
            continue
        house = (S.events("house") or [{}])[0]
        bad = sum(1 for r in rs if bool(r.get("characterInRoom")) != want)
        cr = _char_rows(S, "air.window." + side, WINDOW_LEAD_S + 0.5)
        swim = sum(1 for r in cr if r.get("chrSwim")) if want else 0
        issues = []
        if bad:
            issues.append("%d of %d rows characterInRoom %s" % (bad, len(rs), not want))
        if swim:
            issues.append("swimming on %d of %d character rows" % (swim, len(cr)))
        note = "" if house.get("window", True) else " (no window: the swap was not done)"
        row(rows, rid, "FAIL" if issues else "PASS", ("%d rows, %d character rows" % (len(rs), len(cr))) + note + ("; " + "; ".join(issues) if issues else ""),
            "characterInRoom %s%s" % (want, ", no swim" if want else ""), what + " (format air.volume: the air reaches the glass)", "air")

def check_air_open(S, rows):
    w = "flood.open"
    cam = S.state_rows(interior_rows(S), w, WINDOW_LEAD_S + 1.0)
    if not cam:
        row(rows, "S12.AIR.OPEN", "MISSING", "no flood.open rows", "no air in an open room", "an open house is water", "flood")
        return
    issues = []
    cin = sum(1 for r in cam if r.get("inRoom"))
    chin = sum(1 for r in cam if r.get("characterInRoom"))
    if cin:
        issues.append("camera in a room on %d of %d rows" % (cin, len(cam)))
    if chin:
        issues.append("diver in a room on %d of %d rows" % (chin, len(cam)))
    env = [r.get("chrEnvO2") for r in _char_rows(S, w, WINDOW_LEAD_S + 1.0) + (S.in_window(S.se_rows("s12.air"), w, WINDOW_LEAD_S + 1.0) or [])]
    env = [float(e) for e in env if e is not None]
    if env and max(env) > 0.01:
        issues.append("SE's environment oxygen at the diver up to %.2f" % max(env))
    pic = None
    if S.mode == "S12b":
        dry, wet = S.capture("flood.dry"), S.capture("flood.rise.1")
        if dry and wet:
            pic = frame_med(wet, dry)
            if pic < IN_MIN:
                issues.append("the rising capture is the dry picture (%.3f)" % pic)
        else:
            issues.append("no flood.dry / flood.rise.1 capture")
    st = "FAIL" if issues else ("MISSING" if not env else "PASS")
    row(rows, "S12.AIR.OPEN", st, "%d rows, env O2 max %s%s" % (len(cam), "%.2f" % max(env) if env else "-", (", picture %.3f" % pic) if pic is not None else "") +
        ("; " + "; ".join(issues) if issues else ""), "no air, env O2 0%s" % (", picture >= %.2f" % IN_MIN if S.mode == "S12b" else ""),
        "the house opened with the diver inside: water at once for the camera, the diver and SE's oxygen (format air.flood)", "flood")

def check_clip(S, rows):
    fl = [r for r in S.se_rows("s12.flood") if "outside" in r]
    if not fl:
        row(rows, "S12.FLOOD.CLIP", "MISSING", "no flood row with its boxes' outside count", "outside 0", "the plane only in the room's water cells", "flood")
        return
    worst = max(int(r.get("outside", 0)) for r in fl)
    dropped = max(int(r.get("boxesDropped", 0)) for r in fl)
    planes = [r for r in S.sn_rows("s12.plane") if int(r.get("visible", 0)) > 0]
    row(rows, "S12.FLOOD.CLIP", "FAIL" if worst else "PASS", "%d rows, outside max %d, boxes left out %d%s" % (len(fl), worst, dropped,
        (", %d drawn plane rows" % len(planes)) if S.mode == "S12b" else ""), "outside 0",
        "the water plane is drawn in the room's water cells only, never their bounding box (format flood.plane)", "flood")

def check_debounce(S, rows):
    ev = (S.events("airlock") or [{}])[0]
    rs = S.in_window([r for r in S.se_rows("s12.flood") if str(r.get("grid")) == str(ev.get("grid"))], "airlock") or []
    if not ev.get("built") or not rs:
        row(rows, "S12.FLOOD.DEBOUNCE", "MISSING", "no airlock%s" % (" rows" if ev.get("built") else " (not built)"), "1 fill, 1 drain per room",
            "the airlock's doors cycled: one fill and one drain", "airlock")
        return
    by = {}
    for r in sorted(rs, key=lambda r: r["_t"]):
        by.setdefault(r.get("track"), []).append(r)
    res, bad = [], []
    for tid, tr in sorted(by.items(), key=lambda kv: str(kv[0])):
        seq = max(int(r.get("startSeq", 0)) for r in tr)
        drains = sum(1 for i, r in enumerate(tr) if r.get("state") == "draining" and (i == 0 or tr[i - 1].get("state") != "draining"))
        res.append("track %s: %d start(s), %d drain(s)" % (tid, seq, drains))
        if seq > 1 or drains > 1:
            bad.append(str(tid))
    row(rows, "S12.FLOOD.DEBOUNCE", "FAIL" if bad else "PASS", "; ".join(res)[:220], "startSeq <= 1, <= 1 drain per track",
        "the airlock's doors cycled: every room fills once and drains once (format flood.breach's debounce)", "airlock")

def check_oneplane(S, rows):
    fl = [r for r in S.se_rows("s12.flood") if "overlap" in r]
    if not fl:
        row(rows, "S12.FLOOD.ONEPLANE", "MISSING", "no flood row with its overlap", "overlap 0", "one plane per cell", "airlock")
        return
    worst = max(int(r.get("overlap", 0)) for r in fl)
    row(rows, "S12.FLOOD.ONEPLANE", "FAIL" if worst else "PASS", "%d rows, overlap max %d cells" % (len(fl), worst), "0",
        "no cell under two planes: of two active tracks the newer geometry draws (format flood.breach)", "airlock")

def _swim_changes(rs):
    n, prev = 0, None
    for r in rs:
        v = bool(r.get("chrSwim"))
        if prev is not None and v != prev:
            n += 1
        prev = v
    return n

def check_move(S, rows):
    fill, flush = _char_rows(S, "wade.fill"), _char_rows(S, "wade.flush")
    rs = sorted(fill + flush, key=lambda r: r["_t"])
    if not rs:
        row(rows, "S12.FLOOD.MOVE", "MISSING", "no s12.char rows in the wade", "room surface, swim below, walk above", "the room's plane is the diver's surface", "wade")
        return
    issues = []
    under = [r for r in rs if r.get("eyeAbove") is not None and float(r["eyeAbove"]) <= -0.2]
    over = [r for r in rs if r.get("eyeAbove") is not None and float(r["eyeAbove"]) >= 0.2]
    if not under:
        issues.append("the plane never rose 0.2 m over the eye")
    bad_surf = sum(1 for r in under if r.get("surface") != "room")
    bad_swim = sum(1 for r in under if not r.get("chrSwim"))
    bad_walk = sum(1 for r in over if r.get("chrSwim"))
    if bad_surf:
        issues.append("%d of %d rows under the plane without the room's surface" % (bad_surf, len(under)))
    if bad_swim:
        issues.append("%d rows under the plane not swimming" % bad_swim)
    if bad_walk:
        issues.append("%d rows over the plane swimming" % bad_walk)
    ch = [_swim_changes(fill), _swim_changes(flush)]
    if max(ch) > 1:
        issues.append("the swim latch changed %d / %d times (once per crossing)" % tuple(ch))
    stuck, run_start = 0.0, None
    for r in rs:
        if not r.get("script"):
            run_start = None
            continue
        spd = r.get("speed")
        if spd is not None and float(spd) < 0.05:
            run_start = r["_t"] if run_start is None else run_start
            stuck = max(stuck, r["_t"] - run_start)
        else:
            run_start = None
    if stuck > 1.0:
        issues.append("stuck %.1f s" % stuck)
    row(rows, "S12.FLOOD.MOVE", "FAIL" if issues else "PASS", "%d rows (%d under, %d over), latch changes %d/%d, longest stop %.1f s" % (len(rs), len(under), len(over), ch[0], ch[1], stuck) +
        ("; " + "; ".join(issues) if issues else ""), "room surface, swim under, walk over, <= 1 change per crossing, no stop > 1 s",
        "the room's plane is the diver's water surface as the sea's (format flood.surface)", "wade")

def check_drywalk(S, rows):
    w = S.windows.get("wade.dry")
    rs = _char_rows(S, "wade.dry")
    if not w or not rs:
        row(rows, "S12.FLOOD.DRYWALK", "MISSING", "no rows after the room dried", "walking within 2 s", "the dry room releases the swim", "wade")
        return
    ok = [r for r in rs if not r.get("chrSwim") and r.get("speed") is not None and float(r["speed"]) > 0.3 and r["_t"] <= w[0] + 2.0]
    swim_late = [r for r in rs if r.get("chrSwim") and r["_t"] > w[0] + 2.0]
    row(rows, "S12.FLOOD.DRYWALK", "PASS" if ok and not swim_late else "FAIL", "%d rows, walking within 2 s: %s, swimming after 2 s: %d" % (len(rs), "yes" if ok else "no", len(swim_late)),
        "walking within 2 s, no swim after", "the room dry again: the diver walks at once (the swim latch released)", "wade")

def _level_at(S, t):
    """SE's flood level of the breach track nearest in time to t (the rows are written at least every RowTicks while it changes)."""
    _tid, tr = _breach_track(S)
    best = None
    for r in tr:
        if r.get("_t") is None:
            continue
        if best is None or abs(r["_t"] - t) < abs(best["_t"] - t):
            best = r
    return (float(best.get("level", 0.0)), best.get("state")) if best else (None, None)

def _line_names(S):
    return [e.get("name") for e in S.events("capture") if isinstance(e.get("name"), str) and
            (e["name"].startswith("flood.drain.") or e["name"] == "flood.half")]

def check_line(S, rows):
    if S.mode != "S12b":
        return
    names = _line_names(S)
    px = S.pixels()
    if not names:
        row(rows, "S12.FLOOD.LINE", "MISSING", "no draining waterline capture", "within %.0f %% of the frame" % (T.L["LineTolPermille"] / 10.0), "the waterline in the pictures", "flood")
        return
    res, bad, measured = [], [], 0
    for nm in names:
        path, t = S.capture(nm), S.capture_time(nm)
        f, c = px.get(nm + ".floor"), px.get(nm + ".ceil")
        if not path or t is None or f is None or c is None:
            continue
        level, _st = _level_at(S, t)
        if level is None:
            continue
        iw, hgt = _png(path)[:2]
        sx, sy = (iw / float(f[3][0]), hgt / float(f[3][1])) if f[3] else (1.0, 1.0)
        want = (f[1] + level * (c[1] - f[1])) * sy
        band = 2.0 * T.L["LineTolPermille"] / 1000.0 * hgt
        got, step = waterline(path, f[0] * sx, want - band, want + band)
        measured += 1
        if got is None or step < T.L["LineMinStepPermille"] / 1000.0:
            bad.append("%s no line (step %.3f)" % (nm, step))
            continue
        err = abs(got - want) / hgt
        res.append("%s %.3f" % (nm.split(".", 1)[1], err))
        if err > T.L["LineTolPermille"] / 1000.0:
            bad.append("%s off by %.1f %%" % (nm, err * 100))
    if measured == 0:
        row(rows, "S12.FLOOD.LINE", "MISSING", "no waterline capture with its pixels", "-", "the waterline in the pictures", "flood")
        return
    row(rows, "S12.FLOOD.LINE", "FAIL" if bad else "PASS", "%d captures: %s" % (measured, ", ".join(res + bad))[:200],
        "a step >= %.2f within %.0f %% of the frame of SE's level" % (T.L["LineMinStepPermille"] / 1000.0, T.L["LineTolPermille"] / 10.0),
        "the waterline seen in each draining picture is SE's level (format flood.plane, measured)", "flood")

def check_drain(S, rows):
    tid, tr = _breach_track(S)
    last = max((i for i, r in enumerate(tr) if r.get("state") in ("rising", "full")), default=None)
    post = tr[last + 1:] if last is not None else []
    if not post:
        row(rows, "S12.FLOOD.DRAIN", "MISSING", "no row after the repair", "water(O2, env) .. cap, DrainMs", "the repair drains the room", "flood")
        return
    cap = float(tr[last].get("level", 1.0))
    seq = [tr[last]] + post                 # the level at the repair, then every row after it (draining, and the dry end)
    dr = [r for r in post if r.get("state") == "draining"]
    drain_ms = float(T.F["DrainMs"])
    issues, worst_low, worst_rate = [], 0.0, 0.0
    for r in dr:
        w = T.water(float(r.get("oxygen", 0.0)), float(r.get("envOxygen", 0.0)))
        lv = float(r.get("level", 0.0))
        worst_low = max(worst_low, w - lv)
        if lv > cap + DRAIN_TOL:
            issues.append("above the level at the repair (%.3f > %.3f)" % (lv, cap))
            break
    if worst_low > DRAIN_TOL:
        issues.append("below the water of SE's oxygen by %.4f" % worst_low)
    for i in range(1, len(seq)):
        dt = (seq[i]["_t"] - seq[i - 1]["_t"]) * 1000.0
        drop = float(seq[i - 1].get("level", 0.0)) - float(seq[i].get("level", 0.0))
        allowed = max(dt, 0.0) / drain_ms * RATE_SLACK + 0.01
        worst_rate = max(worst_rate, drop - allowed)
    if worst_rate > 0:
        issues.append("fell faster than DrainMs by %.3f" % worst_rate)
    ended = any(r.get("ended") for r in post)
    if not ended:
        issues.append("never dry")
    env = [float(r.get("envOxygen", 0.0)) for r in dr]
    row(rows, "S12.FLOOD.DRAIN", "FAIL" if issues else "PASS", "%d draining rows from %.2f, env %s, %s" % (len(dr), cap, "%.2f" % env[0] if env else "-",
        "ended" if ended else "not ended") + ("; " + "; ".join(issues) if issues else ""), "water(O2) <= level <= cap, rate <= 1/DrainMs, ends",
        "the repair drains the plane with SE's own room oxygen, at DrainMs at the fastest (format flood.drain)", "flood")

def check_plane(S, rows):
    if S.mode != "S12b":
        return
    pr = [r for r in S.sn_rows("s12.plane") if r.get("state") in ("rising", "draining", "full")]
    drawn = [r for r in pr if int(r.get("visible", 0)) > 0]
    half, dry = S.capture("flood.half"), S.capture("flood.dry2")
    m = None
    issues = []
    if half and dry:
        m = frame_med(half, dry, (0.0, 0.55, 1.0, 1.0))
        if m < PLANE_MIN:
            issues.append("the half-full capture's lower part differs by %.3f only" % m)
    if m is None and not pr:
        row(rows, "S12.FLOOD.PLANE", "MISSING", "no half/dry capture, no s12.plane row", ">= %.2f" % PLANE_MIN, "Subnautica's water plane in the room", "flood")
        return
    if m is None:
        issues.append("no flood.half / flood.dry2 capture (the drain never passed half height)")
    st = "FAIL" if (m is not None and m < PLANE_MIN) else ("PASS" if m is not None else "MISSING")
    row(rows, "S12.FLOOD.PLANE", st, "picture %s; %d of %d plane rows drawn (information)" % ("%.3f" % m if m is not None else "-", len(drawn), len(pr)) +
        ("; " + "; ".join(issues) if issues else ""), "picture >= %.2f" % PLANE_MIN, "Subnautica's water plane in the half-full room (format flood.plane)", "flood")

def check_vent(S, rows):
    rig = S.events("rig")
    vr = S.se_rows("s12.vent")
    if not rig:
        row(rows, "S12.VENT", "MISSING", "no vent rig", "SE's vent effect, SN bursts", "venting plays bubbles at the vent", "vent")
        return
    vid = str(rig[0].get("vent"))
    mine = [r for r in vr if str(r.get("vent")) == vid]
    starts = [r for r in mine if r.get("start")]
    se_effect = [r for r in mine if r.get("seEffect")]
    issues = []
    if not starts:
        issues.append("no SE vent start row")
    if not se_effect:
        issues.append("SE's own vent effect never played (the tank did not reach the vent?)")
    sn_b = m = None
    if S.mode == "S12b":
        fx = S.sn_rows("s12.fx")
        sn_b = len([r for r in fx if r.get("kind") == "burst" and str(r.get("grid")) == str(rig[0].get("grid"))])
        if sn_b != len(starts):
            issues.append("Subnautica %d burst(s) for %d SE start(s)" % (sn_b, len(starts)))
        idle, on = S.capture("vent.idle"), S.capture("vent.on")
        p = S.pixels().get("vent.mouth.on") or S.pixels().get("vent.mouth")
        if idle and on and p:
            a, b = patch(idle, p[0], p[1], 9), patch(on, p[0], p[1], 9)
            m = sum(abs(a[k] - b[k]) for k in range(3)) / 3.0 if a and b else None
            if m is None or m < T.V["CaptureMinPermille"] / 1000.0:
                issues.append("no bubbles at the vent's mouth in the capture (%.3f)" % (m or 0.0))
        else:
            issues.append("no vent capture pair")
    oxy = [r for r in S.se_rows("s12.flood") if r.get("source") == "oxygen"]
    row(rows, "S12.VENT", "FAIL" if issues else "PASS", "SE starts %d, effect rows %d%s, oxygen floods %d" % (len(starts), len(se_effect),
        (", SN bursts %s, mouth %.3f" % (sn_b, m if m is not None else -1.0)) if sn_b is not None else "", len({r.get("track") for r in oxy})) +
        ("; " + "; ".join(issues) if issues else ""), "SE's effect, 1 burst per start, bubbles in the picture",
        "SE's own Air Vent venting: bubbles at its mouth, one burst per start (format vent.event)", "vent")

def check_caps(S, rows):
    if S.mode != "S12b":
        return
    fx = S.sn_rows("s12.fx")
    if not fx:
        row(rows, "S12.CAPS", "MISSING", "no s12.fx row", "the caps", "the effects stay inside S7p's caps", "flood")
        return
    bad = []
    for r in fx:
        e, a = int(r.get("emitted", 0)), int(r.get("asked", 0))
        if e > a:
            bad.append("%s emitted %d > asked %d" % (r.get("kind"), e, a))
        if r.get("kind") == "burst" and a > T.V["BurstBubbles"]:
            bad.append("burst asked %d" % a)
        if r.get("kind") == "splash" and a > T.V["SplashBubbles"]:
            bad.append("splash asked %d" % a)
    by = {}
    for r in fx:
        if r.get("kind") == "stream":
            by.setdefault(r.get("id"), []).append(r)
    for vid, rs in by.items():
        rs.sort(key=lambda r: r["_t"])
        for i in range(1, len(rs)):
            dt = rs[i]["_t"] - rs[i - 1]["_t"]
            n = int(rs[i].get("totalAsked", 0)) - int(rs[i - 1].get("totalAsked", 0))
            if dt > 0.5 and n > (T.V["BubblesPerS"] + 1) * dt + T.V["BurstBubbles"] + 2:
                bad.append("vent %s asked %d in %.1f s" % (vid, n, dt))
    row(rows, "S12.CAPS", "FAIL" if bad else "PASS", "%d fx rows%s" % (len(fx), ("; " + "; ".join(bad[:3])) if bad else ""), "emitted <= asked <= the caps",
        "the S12 effects stay inside their caps (S7p's per-frame budget behind them)", "flood")

def check_look(S, rows):
    if S.mode != "S12b":
        return
    issues, parts = [], []
    if not any(S.capture("look.%s.%s" % (w, o)) for w in ("noon", "night") for o in ("on", "off")):
        row(rows, "S12.LOOK.PRESET", "MISSING", "no look captures", "darker and cooler", "the interior look under water", "look")
        row(rows, "S12.LOOK.EXT", "MISSING", "no exterior captures", "<= %.3f" % (T.L["ExteriorTolPermille"] / 1000.0), "the exterior is unchanged", "look")
        return
    for when in ("noon", "night"):
        on, off = S.capture("look.%s.on" % when), S.capture("look.%s.off" % when)
        if not on or not off:
            issues.append("no %s captures" % when)
            continue
        so, sf = frame_stats(on), frame_stats(off)
        darker = sf[1] - so[1]
        cooler = so[2] - sf[2]
        parts.append("%s luma %.3f->%.3f b-r %.3f->%.3f" % (when, sf[1], so[1], sf[2], so[2]))
        if darker < T.L["DarkerPermille"] / 1000.0:
            issues.append("%s not darker (%.3f)" % (when, darker))
        if cooler <= 0:
            issues.append("%s not cooler (%.3f)" % (when, cooler))
    for e in S.events("reply"):
        a = e.get("args") or {}
        words = a.get("argv") if isinstance(a, dict) else None
        if e.get("side") != "sn" or e.get("cmd") != "de.s12" or not isinstance(words, list) or len(words) < 2 or words[0] != "look":
            continue
        got = (e.get("reply") or {}).get("look")
        if got is not None and bool(got) != (words[1] == "on"):
            issues.append("the switch did not reach Subnautica's look (asked %s, reply look=%s)" % (words[1], got))
            break
    lk = S.sn_rows("s12.look")
    inside_on = [r for r in lk if r.get("want")]
    if inside_on and not all(r.get("applied") for r in inside_on):
        issues.append("s12.look wanted but not applied")
    row(rows, "S12.LOOK.PRESET", "FAIL" if issues else "PASS", "; ".join(parts + issues)[:200] or "-", "darker by %.3f and cooler" % (T.L["DarkerPermille"] / 1000.0),
        "the interior look under water: darker and cooler with the preset (captures at noon and at night)", "look")
    on, off = S.capture("look.window.on"), S.capture("look.window.off")
    pb = S.pixels().get("look.window.b")
    tol = T.L["WindowTolPermille"] / 1000.0
    if not on or not off or not pb:
        row(rows, "S12.LOOK.WINDOW", "MISSING", "no window captures", "<= %.3f" % tol, "the water through the window is unchanged", "look")
    else:
        a, b = patch(on, pb[0], pb[1], 9), patch(off, pb[0], pb[1], 9)
        mw = sum(abs(a[k] - b[k]) for k in range(3)) / 3.0 if a and b else None
        row(rows, "S12.LOOK.WINDOW", "MISSING" if mw is None else ("PASS" if mw <= tol else "FAIL"), "on/off %s at the window" % ("%.4f" % mw if mw is not None else "-"),
            "<= %.3f" % tol, "the water seen through the window from inside: the preset changes nothing there", "look")
    on, off = S.capture("look.ext.on"), S.capture("look.ext.off")
    if not on or not off:
        row(rows, "S12.LOOK.EXT", "MISSING", "no exterior captures", "<= %.3f" % (T.L["ExteriorTolPermille"] / 1000.0), "the exterior is unchanged", "look")
    else:
        m = frame_med(on, off)
        row(rows, "S12.LOOK.EXT", "PASS" if m <= T.L["ExteriorTolPermille"] / 1000.0 else "FAIL", "on/off %.4f" % m, "<= %.3f" % (T.L["ExteriorTolPermille"] / 1000.0),
            "outside the house the preset changes nothing", "look")

def check_lookref(S, rows):
    ref, on = S.capture("lookref.base"), S.capture("look.noon.on")
    if not ref or not on:
        row(rows, "S12.LOOK.REF", "INFO", "no reference capture (part lookref with --ref-at inside a Subnautica base)", "<= %.3f" % (T.L["RefTolPermille"] / 1000.0),
            "the interior against Subnautica's own seabase", "lookref", required=False)
        return
    a, b = frame_stats(ref), frame_stats(on)
    m = sum(abs(a[0][k] - b[0][k]) for k in range(3)) / 3.0
    row(rows, "S12.LOOK.REF", "INFO", "base rgb %s, ours %s, diff %.3f" % (",".join("%.2f" % v for v in a[0]), ",".join("%.2f" % v for v in b[0]), m),
        "<= %.3f" % (T.L["RefTolPermille"] / 1000.0), "the interior against Subnautica's own seabase (information)", "lookref", required=False)

def mutant_rows(S, rows):
    """A seeded failure turns its measured rows red: every one of them measured (PASS or FAIL) is FAIL, at least one measured."""
    for m in S.mutants:
        want = MUTANT_ROWS.get(m, ())
        for r in rows:
            if r["id"] in want:
                r["required"] = False
                r["what"] = r["what"] + " [expected red under %s]" % m
        measured = [r for r in rows if r["id"] in want and r["status"] in ("PASS", "FAIL")]
        red = [r["id"] for r in measured if r["status"] == "FAIL"]
        st = "MISSING" if not measured else ("PASS" if len(red) == len(measured) else "FAIL")
        row(rows, "%s.red" % m, st, "red: %s of %d measured" % (",".join(red) or "none", len(measured)), "red: %s" % ",".join(want),
            "seeded failure %s turns its rows red" % m, "mutants")

CHECKS = ((("selftest", "air", "flood", "look"), check_pin), (("air",), check_air_out), (("air",), check_air_in), (("air",), check_air_flip),
          (("air",), check_air_hold), (("air",), check_air_char), (("air",), check_air_third), (("air",), check_air_window), (("flood",), check_air_open),
          (("flood",), check_air_water), (("flood",), check_fill), (("flood",), check_once), (("flood",), check_drain), (("flood",), check_line),
          (("flood",), check_clip), (("flood",), check_plane), (("wade",), check_move), (("wade",), check_drywalk), (("vent",), check_vent),
          (("airlock",), check_debounce), (("flood", "airlock"), check_oneplane),
          (("flood", "vent"), check_caps), (("look",), check_look), (("look", "lookref"), check_lookref))

def evaluate(folder):
    S = Session(folder)
    rows = []
    for parts, fn in CHECKS:
        if not S.ran(*parts):
            continue
        try:
            fn(S, rows)
        except Exception as e:  # noqa: BLE001 - an analyser bug is a FAIL row, never a crash
            row(rows, "S12.%s" % fn.__name__[6:].upper(), "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:120], parts[0])
    mutant_rows(S, rows)
    return rows, S

def write_results(folder, rows, strict=True, extra=None) -> bool:
    ok = C.verdict(rows, strict)
    res = {"tool": "analyze_s12", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "s12-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "s12-table.txt"), "w", encoding="utf-8") as f:
        f.write(C.table(rows) + "\n")
    return ok

# ======================================================================================================================
# self-test: a synthetic session in the plugins' row forms, simulated with the twin's rules
# ======================================================================================================================

E0 = 1792800000.0

def _iso(t):
    import datetime
    return datetime.datetime.utcfromtimestamp(E0 + t).strftime("%Y-%m-%dT%H:%M:%S.%f") + "0Z"

class _Synth:
    def __init__(self, folder, mode, parts, mutants=()):
        self.folder, self.mode = folder, mode
        os.makedirs(os.path.join(folder, "captures"), exist_ok=True)
        self.sess, self.se, self.sn = [], [], []
        self.t = 0.0
        self.mut = set(mutants)
        self.cap_n = 0
        self.event("start", mode=mode, parts=list(parts), mutants=list(mutants), kind="s12")

    def event(self, ev, **kw):
        self.sess.append(dict({"utc": _iso(self.t)[:23] + "Z", "ev": ev}, **kw))

    def se_row(self, ev, **kw):
        self.se.append(dict({"t": _iso(self.t), "ev": ev}, **kw))

    def sn_row(self, ev, **kw):
        self.sn.append({"t": _iso(self.t)[:23] + "Z", "level": "info", "event": ev, "ev": ev, "data": kw})

    def part(self, name, phase):
        self.event("part", name=name, phase=phase)

    def pixel(self, label, x, y, w=None, h=None):
        """Subnautica's de.pixel answer as S1Host logs it (S12b only, as the launcher)."""
        if self.mode != "S12b":
            return
        line = ("DE.SnPixel label=%s seTick=1 newestTick=1 frame=1 px=(%r, %r) depth=5.0 screen=(%d, %d) fov=60 pointSn=(0, 0, 0) camPos=(0, 0, 0) "
                "camRot=(0, 0, 0, 1) (Camera.WorldToScreenPoint; y from the top)" % (label, float(x), float(y), w or SYN_W, h or SYN_H))
        self.sn.append({"t": _iso(self.t)[:23] + "Z", "level": "info", "event": "DE.SnPixel", "text": line})

    def wait(self, s):
        self.t += s

    def capture(self, name, rgb):
        self.cap_n += 1
        rel = "s1/synth/s12-%s-%02d.png" % (name, self.cap_n)
        p = os.path.join(self.folder, "captures", os.path.basename(rel))
        IMG.write_png(p, SYN_W, SYN_H, lambda x, y: rgb(x, y))
        self.event("capture", name=name, path=rel, reply={"ok": True})

    def write(self):
        self.event("end", rc=None)
        for name, recs in (("s12-session.jsonl", self.sess), ("se-20261024-000000.jsonl", self.se), ("sn-20261024-000000-1234.jsonl", self.sn)):
            with open(os.path.join(self.folder, name), "w", encoding="utf-8") as f:
                for r in recs:
                    f.write(json.dumps(r) + "\n")

def _flat(c):
    return lambda x, y: tuple(int(max(0, min(255, round(v * 255)))) for v in c)

def _split(line_y, below, above):
    """A waterline picture: `above` over row line_y, `below` under it."""
    b, a = _flat(below), _flat(above)
    return lambda x, y: b(x, y) if y >= line_y else a(x, y)

def _settle(s, in_room, chr_in=False, processing=False):
    """The camera moved (de.teleport): S7 writes its change row at once, then the launcher settles before the window opens."""
    s.wait(0.1)
    s.se_row("s7.interior", why="change", inRoom=in_room, characterInRoom=chr_in, processing=processing, candidates=1 if in_room else 0)
    s.wait(1.0)

def _interior(s, in_room, chr_in=False, processing=False, n=6, dt=0.5):
    for _ in range(n):
        s.wait(dt)
        s.se_row("s7.interior", why="periodic", inRoom=in_room, characterInRoom=chr_in, processing=processing, candidates=1)

SYN_W, SYN_H = 48, 112
SYN_FLOOR_Y, SYN_CEIL_Y, SYN_COL_X = 96.0, 16.0, 30.0     # the far wall's floor and ceiling pixels in the synthetic frame
ENV_O2 = 0.9                                            # DE_4546B is breathable: a rebuilt room starts at the planet's oxygen

def synth(folder, mode, mutant=None):
    parts = ["selftest", "air", "flood", "wade", "vent", "airlock", "look", "teardown"]
    s = _Synth(folder, mode, parts, [mutant] if mutant else [])
    m = s.mut
    b = mode == "S12b"
    pin = "0x%016x" % T.PINNED_HASH
    s.se_row("s12.site", hash=pin, header=T.HEADER)
    if b:
        s.sn_row("s12.site", hash=pin, header=T.HEADER)
    water_out, water_in, dry = (0.10, 0.20, 0.30), (0.40, 0.35, 0.30), (100 / 255, 90 / 255, 80 / 255)
    m1 = "S12.M1" in m                 # the camera box: S7's raw cell and held per-grid answer decide
    m8, m9, m10, m11 = "S12.M8" in m, "S12.M9" in m, "S12.M10" in m, "S12.M11" in m

    def char_row(**kw):
        d = dict(speed=0.0, swimMode="Walk", script=False, surface="none", eyeAbove=None, eyeSnY=-9.0, floodActive=False, chrIn=True, chrEnvO2=0.9, chrSwim=False)
        d.update(kw)
        s.se_row("s12.char", **d)
    # ---- air
    s.part("air", "begin")
    for nm in ("south", "window", "vent", "east", "roof", "glass"):
        w = "air.out." + nm
        leak = False                   # outside every room: no rule gives air there
        _settle(s, leak)
        s.part(w, "begin")
        if leak:
            _interior(s, True)
        else:
            s.wait(3.0)                # outside every grid's box S7 writes no periodic row: the carried answer counts
        if b:
            s.capture(w + ".patch-on", _flat(water_in if leak else water_out))
            s.capture(w + ".patch-off", _flat(water_out))
        s.part(w, "end")
    for nm in ("centre", "corner"):
        w = "air.in." + nm
        _settle(s, True)
        s.part(w, "begin")
        _interior(s, True)
        if b:
            s.capture(w + ".patch-on", _flat(water_in))
            s.capture(w + ".patch-off", _flat(water_out))
        s.part(w, "end")
    g = 2.5
    rf = T._room_fn([((1, 1, 1, 3, 3, 3), 1), ((4, 1, 2, 4, 1, 2), 1)])   # the interior and the east window's cell
    face = 3.5 * g                     # the room's east face (cells 1..3), crossed at a plain wall block (cell z 1)
    for k, (a0, a1) in enumerate(((face - 0.5, face + 0.5), (face + 0.5, face - 0.5))):
        w = "air.cross.%d" % (k + 1)
        was = a0 < a1
        _settle(s, was)
        s.part(w, "begin")
        steps = 100
        for i in range(steps + 1):
            x = a0 + (a1 - a0) * i / steps + 0.04 * math.sin(i * 1.7)
            if m1:
                now = rf(T.cell_of(x, g), 2, 1) > 0
            else:
                now = T.hold(was, T.depth((x, 2 * g, 1 * g), g, rf)[0], T.hysteresis(g))
            if now != was or i % 10 == 0:
                s.wait(0.02)
                s.se_row("s7.interior", why="change" if now != was else "periodic", inRoom=now, characterInRoom=False, processing=False, candidates=1)
            was = now
        s.part(w, "end")
    _settle(s, True)
    s.wait(0.05)                       # the door toggled (a recompute), the camera moved out at once
    s.se_row("s7.interior", why="change", inRoom=m1, characterInRoom=False, processing=True, candidates=1 if m1 else 0)
    s.part("air.hold.1", "begin")
    s.wait(0.3)
    if m1:
        _interior(s, False)            # the held answer ends with the recompute
    else:
        s.wait(3.0)
    s.part("air.hold.1", "end")
    _settle(s, False, False)
    s.part("air.char.out", "begin")
    s.wait(3.0)
    s.part("air.char.out", "end")
    _settle(s, True, True)
    s.part("air.char.in", "begin")
    _interior(s, True, True)
    s.part("air.char.in", "end")
    _settle(s, m1, False)
    s.part("air.third", "begin")
    _interior(s, m1, False)            # third person, the character outside: S7's camera cell gives air
    s.part("air.third", "end")
    # M9: round 2's mid-plane puts its eye out: it swims)
    _settle(s, True, not m9)
    s.part("air.window.in", "begin")
    for _ in range(8):
        s.wait(0.5)
        s.se_row("s7.interior", why="periodic", inRoom=True, characterInRoom=not m9, processing=False, candidates=1)
        char_row(chrIn=not m9, chrSwim=m9, chrEnvO2=0.0 if m9 else 0.9, swimMode="Swim" if m9 else "Walk")
    s.part("air.window.in", "end")
    _settle(s, True, False)
    s.part("air.window.out", "begin")
    for _ in range(8):
        s.wait(0.5)
        s.se_row("s7.interior", why="periodic", inRoom=True, characterInRoom=False, processing=False, candidates=1)
        char_row(chrIn=False, chrSwim=True, chrEnvO2=0.0, swimMode="Swim")
    s.part("air.window.out", "end")
    s.part("air", "end")
    # ---- flood
    s.part("flood", "begin")
    gid, tid, cells, depth = "7000", 1, 28, 7.0
    fill = T.fill_ms(cells, g, 1, depth)

    def line_capture(name, level, wet=True):
        s.pixel(name + ".floor", SYN_COL_X, SYN_FLOOR_Y)
        s.pixel(name + ".ceil", SYN_COL_X, SYN_CEIL_Y)
        if b:
            y = SYN_FLOOR_Y + level * (SYN_CEIL_Y - SYN_FLOOR_Y)
            s.capture(name, _split(y, water_out, dry) if wet and "S12.M7" not in m and level > 0 else _flat(dry))

    _settle(s, True, True)             # the ceiling camera in the dry room, the diver standing inside
    line_capture("flood.dry", 0.0, False)
    s.event("breach", reply={"ok": True}, grid=gid)
    t0 = s.t
    track = T.FloodTrack(True)
    track.fill = 1.0 if "S12.M2" in m else fill
    if "S12.M3" in m:
        track.drain = 0.0
    seqmul = 2 if "S12.M4" in m else 1

    def flood_row(o2, env, airtight):
        rowd = dict(grid=gid, track=tid, state=T.FLOOD_STATES[track.state], source=T.FLOOD_SOURCES[track.source], level=round(track.level, 5),
                    rate=round(track.rate, 5), startSeq=track.seq * seqmul, fillMs=round(fill, 1), drainMs=round(track.drain, 1), cells=cells,
                    gridSize=g, holes=1, depth=depth, oxygen=round(o2, 5), envOxygen=round(env, 5), known=True, processing=False,
                    room=1 if airtight else 0, ended=track.ended, boxes=1, up=3, frozen=28, repicks=0, waterCells=27,
                    outside=9 if m10 else 0, boxesDropped=0)
        s.se_row("s12.flood", **rowd)
        if b:
            vis = 0 if "S12.M7" in m else (1 if 0 < track.level < 1 else 0)
            s.sn_row("s12.plane", grid=gid, track=tid, state=rowd["state"], level=track.level, shown=track.level, rate=track.rate, startSeq=rowd["startSeq"],
                     planes=1, visible=vis, worldY=-10.0 + 7.5 * track.level if vis else -99999.0, material="base")

    # the room at the planet's oxygen, then the breach (SE: IsAirtight false, OxygenAmount 0)
    track.step(1.0, False, True, True, ENV_O2, ENV_O2, True, False)
    track.step(16.0, False, True, False, 0.0, ENV_O2, True, False)
    flood_row(0.0, ENV_O2, False)
    if b:
        s.sn_row("s12.fx", kind="splash", grid=gid, id=tid, startSeq=track.seq * seqmul, emitted=80, asked=80, sound=True, totalAsked=0, totalEmitted=0)
    # format air.flood: the open room is water at once (S12.M8: round 2's air pocket above the rising plane)
    s.wait(0.1)
    s.se_row("s7.interior", why="change", inRoom=m8, characterInRoom=m8, processing=False, candidates=1)
    s.part("flood.open", "begin")
    todo = [0.5, 0.75]
    t_ms = 16.0
    while track.state != T.ST_FULL:
        t_ms += 100.0
        s.t = t0 + 0.1 + t_ms / 1000.0
        track.step(t_ms, False, True, False, 0.0, ENV_O2, True, False)
        flood_row(0.0, ENV_O2, False)
        if int(t_ms) % 500 == 16:
            cam_air = m8 and track.level < 0.93
            chr_air = m8 and track.level < 0.23
            s.se_row("s7.interior", why="periodic", inRoom=cam_air, characterInRoom=chr_air, processing=False, candidates=1)
            char_row(chrIn=chr_air, chrSwim=not chr_air, chrEnvO2=0.0, swimMode="Walk" if chr_air else "Swim", speed=0.4, floodActive=True)
        if todo and track.level >= todo[0]:
            name = "flood.rise.%d" % (3 - len(todo))
            s.pixel(name + ".floor", SYN_COL_X, SYN_FLOOR_Y)
            s.pixel(name + ".ceil", SYN_COL_X, SYN_CEIL_Y)
            if b:
                s.capture(name, _flat(water_out))          # the open room seen from inside: under water
            todo.pop(0)
    s.part("flood.open", "end")
    s.wait(1.0)
    t_ms += 1000.0
    s.event("repair", reply={"ok": True}, grid=gid)
    # the rebuilt room starts at the planet's oxygen (MyGridGasSystem.CreateAirtightRoom): it drains at DrainMs
    o2 = ENV_O2
    track.step(t_ms, False, True, True, o2, ENV_O2, True, False)
    flood_row(o2, ENV_O2, True)
    half_done, drain_done = False, False
    while not track.ended:
        s.wait(0.2)
        t_ms += 200.0
        track.step(t_ms, False, True, True, o2, ENV_O2, True, False)
        flood_row(o2, ENV_O2, True)
        if not drain_done and track.state == T.ST_DRAINING and track.level <= 0.75:
            drain_done = True
            line_capture("flood.drain.1", track.level)
        if not half_done and track.level <= 0.55 and track.state == T.ST_DRAINING:
            half_done = True
            line_capture("flood.half", track.level)
            _settle(s, False)
            s.part("flood.water.below", "begin")
            _interior(s, False, n=3, dt=0.2)
            s.part("flood.water.below", "end")
            _settle(s, True)
            s.part("flood.water.above", "begin")
            _interior(s, True, n=3, dt=0.2)
            s.part("flood.water.above", "end")
    s.wait(1.0)
    line_capture("flood.dry2", 0.0, False)
    s.part("flood", "end")
    # ---- wade: the house's vent lets the water in and out around the walking diver (format flood.surface)
    s.part("wade", "begin")
    eye_h, H = 1.75, 7.5
    swim = False
    stop_left = 0.0

    def wade_row(level, plane_on):
        nonlocal swim, stop_left
        above = eye_h - level * H if plane_on else None
        if m11:
            # round 2: no room surface (the sea's 20 m down: swim at once below the eye, the gate's room above it: flapping)
            want = above is not None and above < 0.0
            if above is not None and abs(above) < 0.15:
                want = not swim
                stop_left = max(stop_left, 1.5)
            swim = want
        elif above is not None:
            if not swim and above < -0.1:
                swim = True
            elif swim and above > 0.1:
                swim = False
        else:
            swim = False
        speed = 0.0 if stop_left > 0 else (0.8 if swim else 1.2)
        stop_left = max(0.0, stop_left - 0.1)
        char_row(speed=speed, script=True, surface="room" if plane_on and not m11 else "none", eyeAbove=round(above, 3) if above is not None else None,
                 floodActive=plane_on, chrIn=not swim, chrSwim=swim, swimMode="Swim" if swim else "Walk", chrEnvO2=0.0 if swim else 0.9)

    s.part("wade.fill", "begin")
    for i in range(151):
        s.wait(0.1)
        wade_row(min(1.0, i / 150.0), i > 0)
    s.part("wade.fill", "end")
    s.part("wade.flush", "begin")
    for i in range(201):
        s.wait(0.1)
        lv = max(0.0, 1.0 - i / 200.0)
        wade_row(lv, lv > 0.01)
    s.part("wade.flush", "end")
    s.part("wade.dry", "begin")
    for i in range(30):
        s.wait(0.1)
        wade_row(0.0, False)
    s.part("wade.dry", "end")
    s.part("wade", "end")
    # ---- vent: the rig's Air Vent depressurizes its room into the tank, then pressurizes it back
    s.part("vent", "begin")
    rig_grid, vent_id = "9000", "9001"
    s.event("rig", grid=rig_grid, vent=vent_id, tank="9002", tankFill=0.5)
    s.pixel("vent.mouth", 24.0, 14.0)
    if b:
        s.capture("vent.idle", _flat(water_out))
    m5 = "S12.M5" in m
    asked = 0
    for k, (win, st) in enumerate((("vent.depress", "depressurizing"), ("vent.press", "pressurizing"))):
        s.part(win, "begin")
        s.wait(0.5)
        if not m5:
            s.se_row("s12.vent", grid=rig_grid, vent=vent_id, on=True, start=True, state=st, startSeq=k + 1, dev=False, room=1, dummyRoom=1,
                     seEffect=True, depressurizing=st == "depressurizing", canVent=True, distance=6.0)
            if b:
                asked += 60
                s.sn_row("s12.fx", kind="burst", grid=rig_grid, id=9001, startSeq=k + 1, emitted=60, asked=60, sound=False, totalAsked=asked, totalEmitted=asked)
        if k == 0:
            s.pixel("vent.mouth.on", 24.0, 14.0)
            if b:
                s.capture("vent.on", (lambda x, y: (230, 235, 240) if abs(x - 24) < 8 and abs(y - 14) < 8 else _flat(water_out)(x, y)) if not m5 else _flat(water_out))
        for _ in range(8):
            s.wait(1.0)
            if not m5:
                s.se_row("s12.vent", grid=rig_grid, vent=vent_id, on=True, start=False, state=st, startSeq=k + 1, dev=False, room=1, dummyRoom=1,
                         seEffect=True, depressurizing=st == "depressurizing", canVent=True, distance=6.0)
                if b:
                    asked += 40
                    s.sn_row("s12.fx", kind="stream", grid=rig_grid, id=9001, startSeq=k + 1, emitted=40, asked=40, sound=False, totalAsked=asked, totalEmitted=asked)
        if not m5:
            s.se_row("s12.vent", grid=rig_grid, vent=vent_id, on=False, start=False, state="none", startSeq=k + 1, dev=False, room=1, dummyRoom=1,
                     seEffect=False, depressurizing=st == "depressurizing", canVent=True, distance=6.0)
        s.part(win, "end")
    s.part("vent", "end")
    # ---- airlock: the rig split by an inner door, the outer door cycled (S12.M12: round 2's door flip, two planes over the chamber)
    m12 = "S12.M12" in m
    s.part("airlock", "begin")
    s.event("airlock", grid=rig_grid, inner="9101", outer="9102", built=True)

    def lock_row(tid_, state, level, seq, overlap=0, ended=False):
        s.se_row("s12.flood", grid=rig_grid, track=tid_, state=state, source="breach", level=level, rate=0.0, startSeq=seq, fillMs=3000.0, drainMs=20000.0,
                 cells=18 if tid_ == 5 else 9, gridSize=g, holes=1, depth=7.0, oxygen=0.0 if state != "draining" else ENV_O2, envOxygen=ENV_O2, known=True,
                 processing=False, room=0 if state != "draining" else 1, ended=ended, boxes=1, up=3, frozen=18, repicks=0, waterCells=18 if tid_ == 5 else 9,
                 outside=0, boxesDropped=0, overlap=overlap)
    s.wait(12.0)
    s.part("airlock.cycle", "begin")
    lock_row(5, "rising", 0.0, 1)
    for k in range(2):
        s.wait(1.0)
        if m12:
            lock_row(5, "draining", 0.3 + 0.1 * k, 1 + k)
            s.wait(1.0)
            lock_row(5, "rising", 0.3 + 0.1 * k, 2 + k)
        else:
            lock_row(5, "rising", 0.3 + 0.1 * k, 1)
            s.wait(1.0)
    s.wait(2.0)
    lock_row(5, "draining", 0.6, 3 if m12 else 1)
    s.wait(4.0)
    s.part("airlock.cycle", "end")
    s.wait(4.0)
    s.part("airlock.again", "begin")
    lock_row(6, "rising", 0.0, 1)
    for k in range(6):
        s.wait(1.0)
        lock_row(6, "rising" if k < 3 else "full", min(1.0, 0.35 * (k + 1)), 1)
        lock_row(5, "draining", max(0.0, 0.45 - 0.05 * k), 3 if m12 else 1, overlap=9 if m12 else 0)
    s.wait(2.0)
    lock_row(6, "draining", 1.0, 1)
    for k in range(20):
        s.wait(1.0)
        lock_row(6, "draining", max(0.0, 1.0 - 0.05 * (k + 1)), 1, ended=k == 19)
    lock_row(5, "none", 0.0, 3 if m12 else 1, ended=True)
    s.part("airlock.again", "end")
    s.part("airlock", "end")
    # ---- look
    s.part("look", "begin")
    if b:
        for when, base in (("noon", (0.50, 0.45, 0.40)), ("night", (0.20, 0.18, 0.16))):
            on = (base[0] * 0.70, base[1] * 0.75, base[2] * 0.85) if "S12.M6" not in m else base
            for st in ("on", "off"):
                s.event("reply", side="sn", cmd="de.s12", args={"argv": ["look", st]}, reply={"ok": True, "look": st == "on"})
            s.capture("look.%s.on" % when, _flat(on))
            s.capture("look.%s.off" % when, _flat(base))
            s.sn_row("s12.look", want="S12.M6" not in m, applied="S12.M6" not in m, inRoom=True, camY=-7.0)
        s.pixel("look.window.b", 24.0, 10.0)
        through = lambda x, y: _flat(water_out)(x, y) if abs(x - 24) < 10 and abs(y - 10) < 10 else _flat((0.35, 0.31, 0.28))(x, y)  # noqa: E731
        inside_on = (lambda x, y: through(x, y) if abs(x - 24) < 10 and abs(y - 10) < 10 else _flat((0.25, 0.23, 0.24))(x, y))  # noqa: E731
        s.capture("look.window.on", inside_on if "S12.M6" not in m else through)
        s.capture("look.window.off", through)
        s.capture("look.ext.on", _flat(water_out))
        s.capture("look.ext.off", _flat(water_out))
    s.part("look", "end")
    s.write()

# the launcher's mutant groups run SE's seeded failures with SE alone, Subnautica's with both games
MUTANT_MODE = {k: ("S12b" if k in SN_MUTANTS else "S12se") for k in MUTANT_ROWS}

def _edit_line(folder, name, shift):
    """Move the waterline of capture `name` by `shift` of the frame height (a mis-drawn plane)."""
    sess = [json.loads(x) for x in open(os.path.join(folder, "s12-session.jsonl"), encoding="utf-8")]
    cap = [e for e in sess if e.get("ev") == "capture" and e.get("name") == name][0]
    p = os.path.join(folder, "captures", os.path.basename(cap["path"]))
    w, h, ch, rows = _png(p)
    y_line = next(y for y in range(h) if rows[y][0] != rows[0][0])
    y2 = int(round(y_line + shift * h))
    IMG.write_png(p, w, h, _split(y2, (0.10, 0.20, 0.30), (100 / 255, 90 / 255, 80 / 255)))

def _append_se(folder, window, rec):
    se_files = glob.glob(os.path.join(folder, "se-*.jsonl"))
    sess = [json.loads(x) for x in open(os.path.join(folder, "s12-session.jsonl"), encoding="utf-8")]
    w = [e for e in sess if e.get("ev") == "part" and e.get("name") == window]
    t_mid = (_utc(w[0]["utc"]) + _utc(w[1]["utc"])) / 2.0
    with open(se_files[0], "a", encoding="utf-8") as fh:
        fh.write(json.dumps(dict({"t": _iso(t_mid - E0)}, **rec)) + "\n")

def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    def statuses(folder):
        rows, _S = evaluate(folder)
        return rows, {r["id"]: r["status"] for r in rows}

    print("S12 analyzer self-test (tests/s12/analyze_s12.py --selftest)")
    root = tempfile.mkdtemp(prefix="de-s12-analyze-")
    try:
        check(T.text_hash()[0] == T.PINNED_HASH, "the twin's pin 0x%016x" % T.PINNED_HASH)
        base_se = ("S12.PIN", "S12.AIR.OUT", "S12.AIR.IN", "S12.AIR.FLIP", "S12.AIR.HOLD", "S12.AIR.CHAR", "S12.AIR.THIRD", "S12.AIR.WINDOW.IN",
                   "S12.AIR.WINDOW.OUT", "S12.AIR.OPEN", "S12.AIR.WATER", "S12.FLOOD.FILL", "S12.FLOOD.ONCE", "S12.FLOOD.DRAIN", "S12.FLOOD.CLIP",
                   "S12.FLOOD.MOVE", "S12.FLOOD.DRYWALK", "S12.VENT", "S12.FLOOD.DEBOUNCE", "S12.FLOOD.ONEPLANE")
        want = {"S12se": base_se, "S12b": base_se + ("S12.FLOOD.LINE", "S12.FLOOD.PLANE", "S12.CAPS", "S12.LOOK.PRESET", "S12.LOOK.WINDOW", "S12.LOOK.EXT")}
        for mode in ("S12se", "S12b"):
            folder = os.path.join(root, mode)
            synth(folder, mode)
            rows, st = statuses(folder)
            bad = [(r["id"], r["status"], r["measured"]) for r in rows if r.get("required", True) and r["status"] not in ("PASS", "INFO")]
            check(not bad, "%s synthetic session (the planet's oxygen 0.9): %d rows, all required PASS%s" % (mode, len(rows), (" (bad %s)" % bad) if bad else ""))
            miss = [i for i in want[mode] if st.get(i) != "PASS"]
            check(not miss, "%s: rows %s PASS%s" % (mode, ",".join(want[mode]), (" (not %s)" % miss) if miss else ""))
            write_results(folder, rows)
            check(os.path.exists(os.path.join(folder, "s12-table.txt")), "%s: s12-table.txt and s12-results.json" % mode)
        for mid in sorted(MUTANT_ROWS, key=lambda k: int(k.split("M")[1])):
            mode = MUTANT_MODE[mid]
            folder = os.path.join(root, "%s-%s" % (mode, mid))
            synth(folder, mode, mid)
            rows, st = statuses(folder)
            red = [i for i in MUTANT_ROWS[mid] if st.get(i) == "FAIL"]
            others = [r["id"] for r in rows if r["status"] == "FAIL" and r["id"] not in MUTANT_ROWS[mid] and not r["id"].endswith(".red")]
            ok = len(red) == len(MUTANT_ROWS[mid]) and st.get(mid + ".red") == "PASS" and not others and C.verdict(rows, True) is not None
            check(ok, "%s (%s): %s red, %s.red PASS, nothing else red%s" % (mid, mode, ",".join(MUTANT_ROWS[mid]), mid,
                                                                          (" (others %s; statuses %s)" % (others, {i: st.get(i) for i in MUTANT_ROWS[mid]})) if not ok else ""))
        # a seeded failure whose rows are partly unmeasured passes on the measured ones only, never on none
        folder = os.path.join(root, "partial")
        synth(folder, "S12se", "S12.M7")
        rows, st = statuses(folder)
        check(st.get("S12.M7.red") == "MISSING", "S12.M7 with SE alone (no picture measured): S12.M7.red MISSING, never PASS")
        # a silent mutant fails its.red row
        folder = os.path.join(root, "silent")
        synth(folder, "S12b")
        path = os.path.join(folder, "s12-session.jsonl")
        lines = open(path, encoding="utf-8").read().splitlines()
        first = json.loads(lines[0])
        first["mutants"] = ["S12.M2"]
        lines[0] = json.dumps(first)
        open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
        rows, st = statuses(folder)
        check(st.get("S12.M2.red") == "FAIL", "a silent mutant fails its .red row")
        # a test run: outside, only the carried answer exists; a carried leak (air when the window opens) is red
        folder = os.path.join(root, "carried")
        synth(folder, "S12se")
        sess = [json.loads(x) for x in open(os.path.join(folder, "s12-session.jsonl"), encoding="utf-8")]
        w = [e for e in sess if e.get("ev") == "part" and e.get("name") == "air.out.south"]
        se_file = glob.glob(os.path.join(folder, "se-*.jsonl"))[0]
        with open(se_file, "a", encoding="utf-8") as fh:
            fh.write(json.dumps({"t": _iso(_utc(w[0]["utc"]) - 0.5 - E0), "ev": "s7.interior", "why": "change", "inRoom": True, "characterInRoom": False,
                                 "processing": False, "candidates": 0}) + "\n")
        rows, st = statuses(folder)
        check(st.get("S12.AIR.OUT") == "FAIL", "the answer carried into an outside window with air (no row inside it): S12.AIR.OUT red")

        folder = os.path.join(root, "leak")
        synth(folder, "S12se")
        _append_se(folder, "air.out.window", {"ev": "s7.interior", "why": "change", "inRoom": True, "characterInRoom": False, "processing": False})
        rows, st = statuses(folder)
        check(st.get("S12.AIR.OUT") == "FAIL", "air outside next to the window (the owner's leak): S12.AIR.OUT red")
        # third person: air while the character swims outside is red
        folder = os.path.join(root, "third")
        synth(folder, "S12se")
        _append_se(folder, "air.third", {"ev": "s7.interior", "why": "change", "inRoom": True, "characterInRoom": False, "processing": False})
        rows, st = statuses(folder)
        check(st.get("S12.AIR.THIRD") == "FAIL", "the camera's air in third person with the character outside: S12.AIR.THIRD red")
        # a picture leak outside (patch on/off differ) is red in S12b
        folder = os.path.join(root, "picleak")
        synth(folder, "S12b")
        cap = [e for e in (json.loads(x) for x in open(os.path.join(folder, "s12-session.jsonl"), encoding="utf-8")) if e.get("ev") == "capture" and e.get("name") == "air.out.east.patch-on"]
        IMG.write_png(os.path.join(folder, "captures", os.path.basename(cap[0]["path"])), SYN_W, SYN_H, _flat((0.4, 0.35, 0.3)))
        rows, st = statuses(folder)
        check(st.get("S12.AIR.OUT") == "FAIL", "the dry picture outside (patch on/off differ): S12.AIR.OUT red")
        # the waterline drawn 12 % of the frame off SE's level is red; 3 % is inside the tolerance
        for shift, want_st in ((0.12, "FAIL"), (0.03, "PASS")):
            folder = os.path.join(root, "line%d" % int(shift * 100))
            synth(folder, "S12b")
            _edit_line(folder, "flood.drain.1", shift)
            rows, st = statuses(folder)
            check(st.get("S12.FLOOD.LINE") == want_st, "a waterline %d %% of the frame off SE's level: S12.FLOOD.LINE %s (got %s)" % (int(shift * 100), want_st, st.get("S12.FLOOD.LINE")))

        folder = os.path.join(root, "drylatch")
        synth(folder, "S12se")
        se_file = glob.glob(os.path.join(folder, "se-*.jsonl"))[0]
        sess = [json.loads(x) for x in open(os.path.join(folder, "s12-session.jsonl"), encoding="utf-8")]
        w = [_utc(e["utc"]) for e in sess if e.get("ev") == "part" and e.get("name") == "wade.dry"]
        recs = [json.loads(x) for x in open(se_file, encoding="utf-8")]
        for r in recs:
            if r.get("ev") == "s12.char" and w[0] <= _utc(r["t"]) <= w[1]:
                r["chrSwim"], r["speed"] = True, 0.0
        with open(se_file, "w", encoding="utf-8") as fh:
            for r in recs:
                fh.write(json.dumps(r) + "\n")
        rows, st = statuses(folder)
        check(st.get("S12.FLOOD.DRYWALK") == "FAIL", "the diver still swimming, stopped, after the room dried: S12.FLOOD.DRYWALK red")

        folder = os.path.join(root, "lookargv")
        synth(folder, "S12b")
        path = os.path.join(folder, "s12-session.jsonl")
        sess = [json.loads(x) for x in open(path, encoding="utf-8")]
        for e in sess:
            if e.get("ev") == "reply" and e.get("cmd") == "de.s12" and (e.get("args") or {}).get("argv") == ["look", "off"]:
                e["reply"]["look"] = True
        with open(path, "w", encoding="utf-8") as fh:
            for e in sess:
                fh.write(json.dumps(e) + "\n")
        rows, st = statuses(folder)
        check(st.get("S12.LOOK.PRESET") == "FAIL", "look off answered look on (the chat's words lost): S12.LOOK.PRESET red")

        folder = os.path.join(root, "openo2")
        synth(folder, "S12se")
        se_file = glob.glob(os.path.join(folder, "se-*.jsonl"))[0]
        recs = [json.loads(x) for x in open(se_file, encoding="utf-8")]
        for r in recs:
            if r.get("ev") == "s12.char" and r.get("floodActive") and not r.get("script"):
                r["chrEnvO2"] = 0.9
        with open(se_file, "w", encoding="utf-8") as fh:
            for r in recs:
                fh.write(json.dumps(r) + "\n")
        rows, st = statuses(folder)
        check(st.get("S12.AIR.OPEN") == "FAIL", "the planet's oxygen at the diver in the open house: S12.AIR.OPEN red")
        # a drain judged against the raw oxygen: a level below the water of SE's oxygen is red
        folder = os.path.join(root, "lowdrain")
        synth(folder, "S12se")
        se_file = glob.glob(os.path.join(folder, "se-*.jsonl"))[0]
        recs = [json.loads(x) for x in open(se_file, encoding="utf-8")]
        dr = [r for r in recs if r.get("ev") == "s12.flood" and r.get("state") == "draining"]
        dr[len(dr) // 2]["oxygen"] = 0.0          # SE says the room is empty: the water is full, the level must not be lower
        with open(se_file, "w", encoding="utf-8") as fh:
            for r in recs:
                fh.write(json.dumps(r) + "\n")
        rows, st = statuses(folder)
        check(st.get("S12.FLOOD.DRAIN") == "FAIL", "a draining level under the water of SE's raw oxygen: S12.FLOOD.DRAIN red")
        # without the planet's oxygen (env 1.0 judged as 1 - O2) the same session's drain is red: the environment matters
        folder = os.path.join(root, "noenv")
        synth(folder, "S12se")
        se_file = glob.glob(os.path.join(folder, "se-*.jsonl"))[0]
        recs = [json.loads(x) for x in open(se_file, encoding="utf-8")]
        for r in recs:
            if r.get("ev") == "s12.flood":
                r["envOxygen"] = 1.0
        with open(se_file, "w", encoding="utf-8") as fh:
            for r in recs:
                fh.write(json.dumps(r) + "\n")
        rows, st = statuses(folder)
        check(st.get("S12.FLOOD.DRAIN") == "FAIL", "the same drain judged with a dry level of 1.0 (not the room's environment oxygen 0.9): red")
        # the vent: no bubbles at its mouth in the picture is red
        folder = os.path.join(root, "nobubbles")
        synth(folder, "S12b")
        sess = [json.loads(x) for x in open(os.path.join(folder, "s12-session.jsonl"), encoding="utf-8")]
        cap = [e for e in sess if e.get("ev") == "capture" and e.get("name") == "vent.on"][0]
        IMG.write_png(os.path.join(folder, "captures", os.path.basename(cap["path"])), SYN_W, SYN_H, _flat((0.10, 0.20, 0.30)))
        rows, st = statuses(folder)
        check(st.get("S12.VENT") == "FAIL", "the vent's mouth unchanged in the capture: S12.VENT red")
        # the window: the preset changing the water seen through it is red
        folder = os.path.join(root, "window")
        synth(folder, "S12b")
        sess = [json.loads(x) for x in open(os.path.join(folder, "s12-session.jsonl"), encoding="utf-8")]
        cap = [e for e in sess if e.get("ev") == "capture" and e.get("name") == "look.window.on"][0]
        IMG.write_png(os.path.join(folder, "captures", os.path.basename(cap["path"])), SYN_W, SYN_H, _flat((0.05, 0.10, 0.15)))
        rows, st = statuses(folder)
        check(st.get("S12.LOOK.WINDOW") == "FAIL", "the water through the window darkened by the preset: S12.LOOK.WINDOW red")
        # the pixel parser reads Subnautica's DE.SnPixel line as S1Host writes it
        line = ("DE.SnPixel label=flood.rise.1.floor seTick=10 newestTick=11 frame=5 px=(812.5, 943.25) depth=6.2 screen=(1920, 1080) fov=60 "
                "pointSn=(1, 2, 3) camPos=(0, 0, 0) camRot=(0, 0, 0, 1) (Camera.WorldToScreenPoint; y from the top)")
        mm, sc = _PIXEL.search(line), _PIXEL_SCREEN.search(line)
        check(mm and mm.group(1) == "flood.rise.1.floor" and float(mm.group(3)) == 943.25 and sc and sc.group(2) == "1080", "DE.SnPixel: label, px and screen parsed")
        # no data: MISSING, never PASS
        folder = os.path.join(root, "empty")
        os.makedirs(folder)
        with open(os.path.join(folder, "s12-session.jsonl"), "w", encoding="utf-8") as f:
            f.write(json.dumps({"ev": "start", "mode": "S12b", "parts": ["selftest", "air", "flood", "vent", "look"], "mutants": []}) + "\n")
        rows, st = statuses(folder)
        check(rows and all(r["status"] in ("MISSING", "INFO") for r in rows), "a session without rows: every row MISSING or INFO")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("S12 ANALYZE SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv):
    if argv and argv[0] == "--selftest":
        return selftest()
    if not argv:
        print(__doc__)
        return 2
    folder = argv[0]
    rows, _S = evaluate(folder)
    ok = write_results(folder, rows, strict="--lenient" not in argv)
    if "--json" in argv:
        print(json.dumps(rows, indent=1, default=str))
    else:
        print(C.table(rows))
    print("S12 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
