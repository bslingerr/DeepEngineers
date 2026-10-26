"""S7a "Subnautica's picture above water" oracle table: joins the SE and Subnautica JSONL rows, the launcher's session
log (s7a-session.jsonl: parts with their C1 ticks, C11 replies, 10 Hz section samples) of one collected folder
(tests/out/s7a/<stamp>/ from `launcher.py s7a`) into the rows of ROADMAP S7a (N1-N5) and the seeded failures
S7a.M1-M6; a tests/out/tp1/<stamp>/ folder (`launcher.py tp1 --picture`) gives the S7a.T-P1r rows. Stdlib only.

 python -I tests/s7a/analyze_s7a.py DIR [--lenient] [--json]
 python -I tests/s7a/analyze_s7a.py --tp1r DIR [--baseline DIR] [--picture site|waterline]
 python -I tests/s7a/analyze_s7a.py --selftest # the S7a conventions twin (text, hash, C1 loco and C6 pole codecs),
 # synthetic sessions: every row PASS, then each mutant red

S7a LOG CONTRACT (ROADMAP S7a "JSONL rows"; SE rows flat {"t", "ev",...}, Subnautica rows {"t", "event", "ev",
"data": {...}}; every row is matched to SE ticks: SE `tick`, Subnautica `presentedSeTick` / `c6Tick` / `seTick` /
`c2Tick`):
 SE s7a.view tick, view, approach, policy, picture, edge (+1 SE->SN, -1 back), reason, pictureReason, blocked,
 pictureBlocked, bandTick, y, vy, rho, zone, snReady, request, renderSeq
 s7a.flash tick, kind band|waterline, toSn, durationMs
 s7t.fade tick, phase start|end|superseded, dir toSn|toSe, ms, path F|A'|snap|cut, reason (S7t T1: under Site the
 crossfade covers the band swap and the band flash is off; N2.fade accepts its start rows)
 s7a.ghost tick, active, shown, allowed (m_buildAllowed; `buildAllowed` preferred when present), def, model,...
 s7a.loco tick, state, raw, flags, vF, vS, vU, yawRate, inWater
 s7a.sun tick, checks, H, c, elevDeg, sunDirSn, pole {az, el}, flags, jumpSeq, devPhase
 SN s7a.picture presentedSeTick, view, approach, perfMode, perfApplied, seeThrough, marginsOn, pictureSite, park,
 phase, hideAllowed, camY, frameMsP99
 s7a.ghost presentedSeTick, c2Tick, present, shown, allowed, material, alpha, poseErrMm, firstPresentedTick
 s7a.anim seTick, source, locoState, inWater, params {is_underwater, on_surface, move_speed,..., jump, diving},
 clips, armsShown, fpbody, walkSource, footFrames, footRetarget
 s7a.sun c6Tick, jumpSeq, H, cTarget, sTarget, sNow, cNow, timePassed, step, advancedS, errDeg, isDay,
 c6ElevDeg, dayNightSpeed, drive, lightElevDeg
 s6.* any row naming the ghost id (0x0144454700000001) fails S7a.N3.noproxy.

ROWS (PASS, FAIL, MISSING, INFO): S7a.N1.{space,track,prime,underwater,surfacing,waterline,far,capture*},
S7a.N2.{swap,fade,sun,hover}, S7a.N3.{pose,colour,latency,lifecycle,noproxy}, S7a.N4.{idle,walk,jump,arms},
S7a.N5.{err,isday,monotonic,syncoff,teleport}, S7a.T-P1r.<state>, S7a.Mx.red. Rows of parts the session did not run
are not listed. Exit code 0 when every required row passes (MISSING fails unless --lenient).
Notes on the oracle wording: Surfacing under `Site` lasts one SE tick (it ends as soon as its unchanged request is
acknowledged), so S7a.N1.surfacing asks for the state seen with the picture kept, not for 1 s of it.
"""
from __future__ import annotations

import glob
import json
import math
import os
import struct
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tests", "s5"), os.path.join(ROOT, "tools", "worldmap"),
           os.path.join(ROOT, "tests", "wire"), os.path.join(ROOT, "tests", "standins")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import s2_common as C   # noqa: E402 (jsonl reader, normalise, rows, table, verdict)

GHOST_ID = 0x0144454700000001
TICKS_PER_S = 60
LATCH_BUDGET_TICKS = 120          # 2.0 s (HYPOTHESIS budget)
WATERLINE_GUARD_TICKS = 120       # +-2 s
FLASH_TOL_TICKS = 1
SUN_EDGE_MAX_DEG = 0.25
SUN_EDGE_JOIN_TICKS = 150
FAST_LATCH_MIN_Y = 150.0
BAND_ENTER_Y, BAND_EXIT_Y = 1000.0, 1100.0   # the plugin's default band (S7t T3: back at 1 km; a live setting); Logs reads the session's band
FADE_BACK_LEAD_TICKS = 90   # S7t T1: the crossfade back starts at the leave dwell (1 s = 60 ticks before the off edge) + margin

def set_band(enter, exit):
    global BAND_ENTER_Y, BAND_EXIT_Y
    BAND_ENTER_Y, BAND_EXIT_Y = float(enter), float(exit)
GHOST_POSE_MM = 1.0
GHOST_POSE_DEG = 0.01
GHOST_COLOUR_SETTLE_TICKS = 3
GHOST_LAT_P95, GHOST_LAT_MAX = 3, 6
GHOST_SHOW_TICKS, GHOST_SHOW_FIRST_TICKS, GHOST_REMOVE_TICKS = 120, 300, 1
IDLE_SPEED_MAX, WALK_SPEED_MIN = 0.1, 0.5
CLIP_SHARE, RETARGET_SHARE, ARMS_SHARE = 0.90, 0.95, 0.95
SUN_ERR_MAX, SUN_ERR_ALWAYS, SUN_ERR_SHARE = 0.25, 2.25, 0.99
ISDAY_GUARD_DEG = 0.3
TP1_SIM_MIN, TP1_FRAME_TOL, TP1_SN_P99_MS, TP1_COMMIT_MAX = 0.98, 0.05, 16.7, 0.85
PERF_IDLE, PERF_FULL = 0, 2
VIEW_SPACE, VIEW_APPROACH, VIEW_UNDER, VIEW_SURF = 0, 1, 2, 3
C5_SEE_THROUGH, C5_MARGINS, C5_PICTURE_SITE = 1 << 3, 1 << 5, 1 << 9
MUTANT_RED = {"S7a.M1": ("S7a.N1.track", "S7a.N1.waterline"), "S7a.M2": ("S7a.N1.track",), "S7a.M3": ("S7a.N2.fade",),
              "S7a.M4": ("S7a.N3.colour",), "S7a.M5": ("S7a.N4.walk",), "S7a.M6": ("S7a.N5.monotonic",)}
FOOT = ("Idle", "Walk", "Run", "Sprint", "Jump", "Fall")

# ----------------------------------------------------------------------------------------------------------------
# the S7a conventions twin (CONTRACT §5.4 "S7a conventions"; Wire S7aConventions, S7aLoco, S7aSky)
# ----------------------------------------------------------------------------------------------------------------
CANONICAL_TEXT = """ext s7a picture 1
const C1Loco Version 1
const C1Loco Size 16
const C1Loco Offset 160
const C1LocoFlags Grounded 1
const C1LocoFlags Crouching 2
const C1LocoFlags Jetpack 4
const C1LocoFlags InWater 8
const C1LocoFlags Seated 16
const C1LocoFlags Dead 32
const C1LocoFlags Ladder 64
const C1LocoState None 0
const C1LocoState Idle 1
const C1LocoState Walk 2
const C1LocoState Run 3
const C1LocoState Sprint 4
const C1LocoState CrouchIdle 5
const C1LocoState CrouchWalk 6
const C1LocoState Jump 7
const C1LocoState Fall 8
const C1LocoState Fly 9
const C1LocoState Ladder 10
const C1LocoState Seated 11
const C1LocoState Dead 12
const C1LocoState Swim 13
const C2FlagsExt GhostShown 4
const C2FlagsExt GhostAllowed 8
const C5FlagsExt PictureSite 512
const C6FlagsExt SkyPoleValid 4
const C6Sky Size 4
const C6Sky Offset 60
const Ghost PartKey 1
const Ghost MaxParts 32
const Ghost EntityId 91274063699247105
const GridFlagsExt Ghost 8
const Limits SunHoldMaxCdeg 200
const Limits PictureDwellMs 250
const Limits FallAnimMs 450
const Limits GroundAnimScalePermille 1000
const Limits MirrorEnterAboveWaterM 1000
const Limits MirrorLeaveAboveWaterM 1050
const ViewPicture Site 0
const ViewPicture Waterline 1
field loco version 0xa0 1 u8
field loco state 0xa1 1 u8
field loco flags 0xa2 1 u8
field loco pad 0xa3 1 u8
field loco vForwardCm 0xa4 2 i16
field loco vStrafeCm 0xa6 2 i16
field loco vUpCm 0xa8 2 i16
field loco yawRateCdeg 0xaa 2 i16
field loco seMovement 0xac 2 u16
field loco pad2 0xae 2 u16
field sky poleAzCdeg 0x3c 2 i16
field sky poleElCdeg 0x3e 2 i16
format ghost C2 kind 1 pose = gizmo SpaceDefault.m_worldMatrixAdd; PartAdd key 1 class 3 NoShadows local = translation(ModelOffset) model = MyCubeBlockDefinition.Model|lod0
format pole q = M_sn x W_sn, M = normalize(U-axis*(axis.U)), W = normalize(axis x U); az = atan2(q.x,q.z), el = asin(q.y), centi-degrees rounded half away from zero
format sky c = frac(H/2pi+0.5); n = normalize(up-q*(up.q)); r = -(q x n); SunDirection = atan2(-r.x,-r.z); NorthPoleOffset = atan2(-n.x*cos(SunDirection)+n.z*sin(SunDirection),n.y)
format time DayNightCycle paused; timePassedAsDouble += frac(sTarget-sNow)*1200 unless behind by <= SunHoldMaxCdeg of cycle angle (hold)
"""
PINNED_HASH, CANONICAL_LINES, CANONICAL_BYTES = 0x63d59c04766f4e03, 59, 2219
LOCO_STATES = ("None", "Idle", "Walk", "Run", "Sprint", "CrouchIdle", "CrouchWalk", "Jump", "Fall", "Fly", "Ladder", "Seated",
               "Dead", "Swim")
LOCO_FMT = "<BBBBhhhhHH"          # 0xA0-0xAF

def fnv1a64(text):
    h = 0xcbf29ce484222325
    for b in text.encode("ascii"):
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def loco_state_from_se(movement, present, swim_latch):
    """The CONTRACT mapping (Wire S7aLoco.StateFromSe): first match wins."""
    if not present:
        return 0
    if swim_latch:
        return 13
    mode, d, speed = movement & 15, movement & 1008, movement & 3072
    if mode == 6:
        return 12
    if mode == 1:
        return 11
    if mode == 7 or movement & 16384:
        return 10
    if mode == 3:
        return 9
    if mode == 4:
        return 8
    if mode == 5:
        return 7
    if mode == 2:
        return 5 if d == 0 else 6
    if mode == 0:
        if d == 0:
            return 1
        return 4 if speed & 2048 else (3 if speed & 1024 else 2)
    return 0

def loco_pack(state, flags, vf, vs, vu, yaw, raw):
    return struct.pack(LOCO_FMT, 1, state, flags, 0, vf, vs, vu, yaw, raw, 0)

def loco_unpack(b):
    v = struct.unpack(LOCO_FMT, b)
    return None if v[0] != 1 else {"state": v[1], "flags": v[2], "vF": v[4], "vS": v[5], "vU": v[6], "yawRate": v[7], "raw": v[8]}

def conventions_selftest():
    ok = True
    txt = CANONICAL_TEXT
    ok &= fnv1a64(txt) == PINNED_HASH and txt.count("\n") == CANONICAL_LINES and len(txt.encode("ascii")) == CANONICAL_BYTES
    print("S7a conventions twin: hash 0x%016x (pinned 0x%016x), %d lines, %d bytes" % (
        fnv1a64(txt), PINNED_HASH, txt.count("\n"), len(txt.encode("ascii"))))
    b = loco_pack(3, 0x49, -1234, 32767, -32767, 4500, 1040)
    ok &= len(b) == 16 and b[0] == 1 and b[3] == 0 and b[14:16] == b"\x00\x00"
    ok &= loco_unpack(b) == {"state": 3, "flags": 0x49, "vF": -1234, "vS": 32767, "vU": -32767, "yawRate": 4500, "raw": 1040}
    ok &= loco_unpack(b"\x00" * 16) is None
    cases = {0: 1, 4096: 1, 16: 2, 1040: 3, 2064: 4, 2: 5, 18: 6, 5: 7, 4: 8, 3: 9, 7: 10, 263: 10, 16384: 10, 1: 11, 6: 12, 8: 0}
    for mv, st in cases.items():
        ok &= loco_state_from_se(mv, True, False) == st
    ok &= loco_state_from_se(1040, False, False) == 0 and loco_state_from_se(6, True, True) == 13
    pole = struct.pack("<hh", -9000, 745)
    ok &= struct.unpack("<hh", pole) == (-9000, 745)
    print("S7a conventions twin %s" % ("PASS" if ok else "FAIL"))
    return ok

# ----------------------------------------------------------------------------------------------------------------
# reading a folder
# ----------------------------------------------------------------------------------------------------------------
PREFIXES = ("s7a.", "s6.")

def _num(d, *names):
    for n in names:
        v = d.get(n) if isinstance(d, dict) else None
        if isinstance(v, bool):
            return 1.0 if v else 0.0
        if isinstance(v, (int, float)):
            return float(v)
        if isinstance(v, str):
            try:
                return float(int(v, 16)) if v.lower().startswith("0x") else float(v)
            except ValueError:
                continue
    return None

_VIEW_NAMES = {VIEW_SPACE: "Space", VIEW_APPROACH: "Approach", VIEW_UNDER: "Underwater", VIEW_SURF: "Surfacing"}
_APPROACH_NAMES = {0: "none", 1: "Track", 2: "Prime"}
_VIEW_BY_NAME = {"space": VIEW_SPACE, "approach": VIEW_APPROACH, "underwater": VIEW_UNDER, "surfacing": VIEW_SURF}

def _view(d):
    """The view state of a row: a number, or the name the plugins write."""
    v = d.get("view") if isinstance(d, dict) else None
    if isinstance(v, str):
        k = v.split("/")[0].strip().lower()
        if k in _VIEW_BY_NAME:
            return float(_VIEW_BY_NAME[k])
    return _num(d, "view")

def _flag(d, name):
    v = d.get(name)
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return v != 0
    if isinstance(v, str):
        return v.lower() in ("true", "1", "on", "yes")
    return None

class Logs:
    """A collected S7a session folder."""

    def __init__(self, folder):
        self.folder = folder
        self.session = C.read_jsonl(os.path.join(folder, "s7a-session.jsonl"))
        self.se = [e for e in C.events(C.read_jsonl(sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))), PREFIXES)]
        self.sn = [e for e in C.events(C.read_jsonl(sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))), PREFIXES)]
        self.start = next((r for r in self.session if r.get("ev") == "start"), {})
        band = self.start.get("band")
        if isinstance(band, (list, tuple)) and len(band) == 2:
            set_band(band[0], band[1])

    def se_ev(self, name):
        return [e for e in self.se if e.get("_ev") == name]

    def sn_ev(self, name):
        return [e for e in self.sn if e.get("_ev") == name]

    def parts_run(self):
        return [r.get("name") for r in self.session if r.get("ev") == "part" and r.get("phase") == "begin"]

    def windows(self, prefix):
        """[(name, t0, t1)] of the parts whose name starts with `prefix` (SE ticks of the launcher's C1 samples)."""
        out = []
        for r in self.session:
            if r.get("ev") == "part" and r.get("phase") == "begin" and str(r.get("name", "")).startswith(prefix):
                name = r["name"]
                end = next((e for e in self.session if e.get("ev") == "part" and e.get("name") == name and e.get("phase") == "end"
                            and (e.get("tick") or 0) >= (r.get("tick") or 0)), None)
                t0 = r.get("tick")
                t1 = end.get("tick") if end else None
                if t0 is not None:
                    out.append((name, t0, t1 if t1 is not None else 1 << 62))
        return out

    def window(self, name):
        w = [x for x in self.windows(name) if x[0] == name]
        return w[0] if w else None

    def samples(self, t0=None, t1=None):
        out = []
        for r in self.session:
            if r.get("ev") != "sample":
                continue
            t = r.get("c1Tick")
            if t0 is not None and (t is None or t < t0 or t > t1):
                continue
            out.append(r)
        return out

    @staticmethod
    def in_ticks(rows, t0, t1, *keys):
        out = []
        for d in rows:
            t = next((_num(d, k) for k in keys if _num(d, k) is not None), None)
            if t is not None and t0 <= t <= t1:
                out.append(d)
        return out

def row(rows, cid, status, measured, threshold, what, part="", required=True, **detail):
    return C.row(rows, cid, status, measured, threshold, what, part, required, **detail)

def _share(n, d):
    return (n / float(d)) if d else None

def _pct(x):
    return "-" if x is None else "%.1f %%" % (x * 100)

def _p(values, q):
    v = sorted(values)
    if not v:
        return None
    return v[min(len(v) - 1, int(math.ceil(q * len(v))) - 1)]

# ----------------------------------------------------------------------------------------------------------------
# N1 picture per state
# ----------------------------------------------------------------------------------------------------------------

def _picture_bad(d):
    why = []
    if not _flag(d, "seeThrough"):
        why.append("seeThrough 0")
    if not _flag(d, "marginsOn"):
        why.append("margins off")
    if _num(d, "perfApplied") != PERF_FULL:
        why.append("perf %s" % _num(d, "perfApplied"))
    if _flag(d, "park"):
        why.append("parked")
    if _flag(d, "hideAllowed"):
        why.append("HideAllowed")
    if not _flag(d, "pictureSite"):
        why.append("not Site")
    return why

def _state_seconds(samples, view, mode=None):
    ticks = [s.get("c1Tick") for s in samples if (s.get("c5") or {}).get("viewState") == view and
             (mode is None or (s.get("c5") or {}).get("approachMode") == mode) and s.get("c1Tick") is not None]
    return (max(ticks) - min(ticks)) / float(TICKS_PER_S) if len(ticks) >= 2 else 0.0

def n1_rows(L, rows):
    if "n1" not in L.parts_run():
        return
    pic = L.sn_ev("s7a.picture")
    view = sorted(L.se_ev("s7a.view"), key=lambda d: _num(d, "tick") or 0)
    what = "S7a.N1 picture per state (ROADMAP S7a): Space SE's picture, Idle, parked; every non-Space row after the latch " \
           "Subnautica's (seeThrough, margins, Full, not parked, HideAllowed false, C5 bit 9); latch <= 2.0 s after the " \
           "later of the Approach entry and +300 m, never above +300 m; no picture edge within +-2 s of a waterline crossing"

    def win_rows(names):
        out = []
        for n in names:
            w = L.window(n)
            if w:
                out += L.in_ticks(pic, w[1], w[2], "presentedSeTick")
        return out

    def win_samples(names):
        out = []
        for n in names:
            w = L.window(n)
            if w:
                out += L.samples(w[1], w[2])
        return out

    # Space and FAR
    for rid, names, part in (("S7a.N1.space", ("n1.space", "n1.space2"), "n1.space"), ("S7a.N1.far", ("n1.far",), "n1.far")):
        rs = win_rows(names)
        sm = win_samples(names)
        settled = rs[1:] if len(rs) > 1 else rs
        bad = [d for d in settled if _flag(d, "seeThrough") or _num(d, "perfApplied") != PERF_IDLE or not _flag(d, "park")]
        c5bad = [s for s in sm if (s.get("c5") or {}).get("viewState") not in (None, VIEW_SPACE)]
        if not rs and not sm:
            row(rows, rid, "MISSING", "no s7a.picture rows / samples", "Space rows", what, part)
            continue
        status = "PASS" if not bad and not c5bad and rs else ("MISSING" if not rs else "FAIL")
        row(rows, rid, status, "%d rows, %d bad, C5 not Space %d" % (len(rs), len(bad), len(c5bad)),
            "seeThrough 0, Idle, parked", what, part, bad=[_picture_bad(d) for d in bad[:3]])
    # Track, Prime, Underwater
    for rid, name, v, mode in (("S7a.N1.track", "n1.track", VIEW_APPROACH, 1), ("S7a.N1.prime", "n1.prime", VIEW_APPROACH, 2),
                               ("S7a.N1.underwater", "n1.underwater", VIEW_UNDER, None)):
        rs = win_rows((name,))
        sm = win_samples((name,))
        secs = _state_seconds(sm, v, mode)
        bad = [d for d in rs if not (_flag(d, "seeThrough") and _flag(d, "marginsOn") and _num(d, "perfApplied") == PERF_FULL
                                     and not _flag(d, "park") and not _flag(d, "hideAllowed") and _flag(d, "pictureSite"))]
        c5bad = [s for s in sm if (s.get("c5") or {}).get("viewState") == v and
                 ((s.get("c5") or {}).get("flags", 0) & (C5_SEE_THROUGH | C5_PICTURE_SITE)) != (C5_SEE_THROUGH | C5_PICTURE_SITE)]
        crit = []
        if not rs:
            crit.append("no rows")
        if bad:
            crit.append("%d rows not Subnautica's picture (%s)" % (len(bad), ",".join(_picture_bad(bad[0]))))
        if c5bad:
            crit.append("%d C5 samples without seeThrough+bit 9" % len(c5bad))
        if sm and secs < 1.0:
            crit.append("state seen %.2f s" % secs)
        if rid == "S7a.N1.track":
            lat = _latch_timing(view, L)
            if lat is not None:
                crit += lat
        status = "MISSING" if not rs else ("PASS" if not crit else "FAIL")
        row(rows, rid, status, "; ".join(crit)[:34] if crit else "%d rows, %.1f s" % (len(rs), secs), "Subnautica's picture", what,
            name, detail=crit)
    # Surfacing: one tick under the latch; the picture kept
    w1, w2 = L.window("n1.surface"), L.window("n1.surfacing")
    if w1 or w2:
        t0 = (w1 or w2)[1]
        t1 = (w2 or w1)[2]
        vs = L.in_ticks(view, t0, t1, "tick")
        seen = any(_view(d) == VIEW_SURF for d in vs) or any((s.get("c5") or {}).get("viewState") == VIEW_SURF for s in L.samples(t0, t1))
        edges = [d for d in vs if (_num(d, "edge") or 0) != 0]
        rs = L.in_ticks(pic, t0, t1, "presentedSeTick")
        lost = [d for d in rs if not _flag(d, "seeThrough")]
        status = "MISSING" if not vs and not rs else ("PASS" if seen and not edges and not lost else "FAIL")
        row(rows, "S7a.N1.surfacing", status, "seen %s, edges %d, SE-picture rows %d" % (seen, len(edges), len(lost)),
            "seen, picture kept", what, "n1.surfacing")
    # waterline crossings
    w = L.window("n1")
    if w:
        vs = L.in_ticks(view, w[1], w[2], "tick")
        crossings, prev = [], None
        for d in vs:
            v = _view(d)
            if prev is not None and v != prev and (v == VIEW_UNDER or prev == VIEW_UNDER):
                crossings.append(_num(d, "tick"))
            prev = v
        edges = [_num(d, "tick") for d in vs if (_num(d, "edge") or 0) != 0]
        near = [(c, e) for c in crossings for e in edges if abs(e - c) <= WATERLINE_GUARD_TICKS]
        status = "MISSING" if not crossings else ("PASS" if not near else "FAIL")
        row(rows, "S7a.N1.waterline", status, "%d crossings, %d edges near" % (len(crossings), len(near)), "0 edges within +-2 s",
            what, "n1", near=near[:5])
    caps = [r for r in L.session if r.get("ev") == "capture"]
    row(rows, "S7a.N1.capture", "INFO", "%d captures" % len(caps), "owner look", "the +150 m pair shows Subnautica's sky and sea",
        "n1.capture", required=False, files=[c.get("path") for c in caps])

def _latch_timing(view, L):
    """The latch on edge of the settled descent (n1.descent + n1.track): within 2.0 s of max(Approach entry, +300 m)."""
    wd, wt = L.window("n1.descent"), L.window("n1.track")
    if not wd:
        return None
    t0, t1 = wd[1], (wt or wd)[2]
    vs = L.in_ticks(view, t0, t1, "tick")
    entry = next((_num(d, "tick") for d in vs if _view(d) == VIEW_APPROACH), None)
    cross = next((_num(d, "tick") for d in vs if (_num(d, "y") is not None and _num(d, "y") < BAND_ENTER_Y)), None)
    on = next((d for d in vs if (_num(d, "edge") or 0) > 0 or (_flag(d, "picture") and _view(d) != VIEW_SPACE)), None)
    out = []
    if on is None:
        return ["no latch-on row"]
    ref = max(x for x in (entry, cross) if x is not None) if (entry is not None or cross is not None) else None
    if ref is not None and _num(on, "tick") - ref > LATCH_BUDGET_TICKS:
        out.append("latch %.2f s late" % ((_num(on, "tick") - ref) / TICKS_PER_S))
    if _num(on, "y") is not None and _num(on, "y") > BAND_ENTER_Y:
        out.append("latch above +%.0f m (%.0f)" % (BAND_ENTER_Y, _num(on, "y")))
    return out

# ----------------------------------------------------------------------------------------------------------------
# N2 the band swap
# ----------------------------------------------------------------------------------------------------------------

def _crossfaded(edge, fades):
    """S7t T1: an s7t.fade start row covers a picture edge: toSn within FLASH_TOL_TICKS of an on edge; toSe from the
 leave dwell (up to FADE_BACK_LEAD_TICKS before) to FLASH_TOL_TICKS after an off edge (a snap is at the edge itself)."""
    t = _num(edge, "tick")
    if t is None:
        return False
    on = (_num(edge, "edge") or 0) > 0
    for f in fades:
        ft = _num(f, "tick")
        if ft is None or (str(f.get("dir", "")) == "toSn") != on:
            continue
        lead = FLASH_TOL_TICKS if on else FADE_BACK_LEAD_TICKS
        if t - lead <= ft <= t + FLASH_TOL_TICKS:
            return True
    return False

def n2_rows(L, rows):
    if "n2" not in L.parts_run():
        return
    view = sorted(L.se_ev("s7a.view"), key=lambda d: _num(d, "tick") or 0)
    flash = L.se_ev("s7a.flash")
    fades = [d for d in L.se_ev("s7t.fade") if str(d.get("phase", "start")) == "start"]   # S7t T1: the crossfade covers the swap
    suns = [d for d in L.sn_ev("s7a.sun") if _num(d, "c6Tick") is not None]
    what = "S7a.N2 the band swap: one SE->SN edge per descent, one back per ascent, none hovering; 100 m/s latched above " \
           "+150 m; an s7a.flash band row within 1 tick of every edge (S7t: or an s7t.fade start row: toSn within 1 tick of an on edge, " \
           "toSe up to the leave dwell before an off edge); the sun alike (<= 0.25 deg) at every edge"
    bad_swap, fast_bad, all_edges = [], [], []
    wins = [w for w in L.windows("n2.") if w[0].split(".")[1][:1] in ("d", "a") and w[0] != "n2.hover"]
    for name, t0, t1 in wins:
        es = [d for d in L.in_ticks(view, t0, t1, "tick") if (_num(d, "edge") or 0) != 0]
        all_edges += es
        want = 1 if name.split(".")[1].startswith("d") else -1
        if len(es) != 1 or _num(es[0], "edge") != want:
            bad_swap.append("%s: %s" % (name, [int(_num(d, "edge")) for d in es]))
        if name.startswith("n2.d100") and es:
            y = _num(es[0], "y")
            if y is None or y < FAST_LATCH_MIN_Y:
                fast_bad.append("%s y %s" % (name, y))
    if not wins:
        row(rows, "S7a.N2.swap", "MISSING", "no n2 windows", "", what, "n2")
        return
    crit = bad_swap + fast_bad
    row(rows, "S7a.N2.swap", "PASS" if not crit else "FAIL", "; ".join(crit)[:34] or "%d windows" % len(wins),
        "1 edge each, 100 m/s above +150", what, "n2", detail=crit)
    nofade = [int(_num(e, "tick")) for e in all_edges if not any(
        abs((_num(f, "tick") or -1e9) - _num(e, "tick")) <= FLASH_TOL_TICKS and str(f.get("kind", "band")) == "band" for f in flash)
        and not _crossfaded(e, fades)]
    row(rows, "S7a.N2.fade", "MISSING" if not all_edges else ("PASS" if not nofade else "FAIL"),
        "%d of %d edges unfaded" % (len(nofade), len(all_edges)), "a band flash within 1 tick", what, "n2", ticks=nofade[:5])
    errs, missing = [], 0
    for e in all_edges:
        t = _num(e, "tick")
        near = sorted(suns, key=lambda d: abs(_num(d, "c6Tick") - t))
        if not near or abs(_num(near[0], "c6Tick") - t) > SUN_EDGE_JOIN_TICKS or _num(near[0], "errDeg") is None:
            missing += 1
            continue
        errs.append(_num(near[0], "errDeg"))
    status = "MISSING" if not errs else ("PASS" if max(errs) <= SUN_EDGE_MAX_DEG and not missing else "FAIL")
    row(rows, "S7a.N2.sun", status, "max %.3f deg, %d unjoined" % (max(errs) if errs else float("nan"), missing),
        "<= 0.25 deg at every edge", what, "n2")
    wh = L.window("n2.hover")
    if wh:
        es = [d for d in L.in_ticks(view, wh[1], wh[2], "tick") if (_num(d, "edge") or 0) != 0]
        any_rows = L.in_ticks(view, wh[1], wh[2], "tick")
        row(rows, "S7a.N2.hover", "MISSING" if not any_rows else ("PASS" if not es else "FAIL"), "%d edges" % len(es),
            "0 edges", what, "n2.hover")

# ----------------------------------------------------------------------------------------------------------------
# N3 the placement ghost
# ----------------------------------------------------------------------------------------------------------------

def _gid(v):
    if isinstance(v, bool) or v is None:
        return None
    if isinstance(v, (int, float)):
        return int(v) & 0xFFFFFFFFFFFFFFFF
    s = str(v).strip()
    try:
        return (int(s, 16) if s.lower().startswith("0x") else int(s)) & 0xFFFFFFFFFFFFFFFF
    except ValueError:
        return None

def _names_ghost(d):
    for k, v in d.items():
        if k.startswith("_"):
            continue
        if isinstance(v, dict) and _names_ghost(v):
            return True
        if isinstance(v, list) and any(_gid(x) == GHOST_ID for x in v if not isinstance(x, (dict, list))):
            return True
        if not isinstance(v, (dict, list)) and _gid(v) == GHOST_ID:
            return True
    return False

def n3_rows(L, rows):
    if "n3" not in L.parts_run():
        return
    se = sorted(L.se_ev("s7a.ghost"), key=lambda d: _num(d, "tick") or 0)
    sn = L.sn_ev("s7a.ghost")
    what = "S7a.N3 placement ghost: pose <= 1 mm / 0.01 deg (alpha 1), green <=> m_buildAllowed (outside 3 ticks of a " \
           "change), latency <= 3 ticks p95 / 6 max, shown <= 2 s after probe (5 s first), removed <= 1 tick after stop, no " \
           "s6.* row or proxy names the ghost"
    drawn = [d for d in sn if _flag(d, "present") and _flag(d, "shown")]
    alpha1 = [d for d in drawn if _num(d, "alpha") is not None and _num(d, "alpha") >= 0.999]   # review F5: the plugin's own threshold
    errs = [_num(d, "poseErrMm") for d in alpha1 if _num(d, "poseErrMm") is not None]
    # S7a-2: the angle only on frames drawn at alpha exactly 1 — at 0.999 the interpolation remainder of the
 # 02 deg by itself (the position error stays, 1e-5 mm)
    angs = [_num(d, "poseErrDeg") for d in alpha1 if _num(d, "poseErrDeg") is not None and _num(d, "alpha") == 1.0]
    status = "MISSING" if not errs else ("PASS" if max(errs) <= GHOST_POSE_MM and (not angs or max(angs) <= GHOST_POSE_DEG) else "FAIL")
    row(rows, "S7a.N3.pose", status, "max %s mm, %s deg (%d rows)" % (_f(max(errs) if errs else None), _f(max(angs) if angs else None),
                                                                        len(errs)), "<= 1 mm, 0.01 deg", what, "n3")
    # colour: Subnautica's drawn colour (its C2 flag) vs SE's m_buildAllowed at that tick
    changes = []
    prev = None
    for d in se:
        a = _flag(d, "buildAllowed") if d.get("buildAllowed") is not None else _flag(d, "allowed")
        if prev is not None and a != prev:
            changes.append(_num(d, "tick"))
        prev = a
    judged, wrong = 0, []
    for d in drawn:
        t = _num(d, "c2Tick") or _num(d, "presentedSeTick")
        if t is None:
            continue
        ref = [x for x in se if (_num(x, "tick") or 0) <= t]
        if not ref:
            continue
        if any(0 <= t - c <= GHOST_COLOUR_SETTLE_TICKS for c in changes):
            continue
        x = ref[-1]
        want = _flag(x, "buildAllowed") if x.get("buildAllowed") is not None else _flag(x, "allowed")
        judged += 1
        if bool(_flag(d, "allowed")) != bool(want):
            wrong.append(int(t))
    row(rows, "S7a.N3.colour", "MISSING" if not judged else ("PASS" if not wrong else "FAIL"),
        "%d of %d rows wrong" % (len(wrong), judged), "100 % green <=> allowed", what, "n3", ticks=wrong[:5])
    # review F4: the plugin's own measure (the first C7 presentedSeTick at or past each pose change, the
 # contract's definition) as the Subnautica rows report it: latencyP95Ticks (a window) and latencyMaxTicks; the old
 # firstPresentedTick - c2Tick compared the last resolved change with the newest snapshot and could not fail
    p95s = [_num(d, "latencyP95Ticks") for d in drawn if _num(d, "latencyP95Ticks") is not None and _num(d, "latencyP95Ticks") >= 0]
    maxs = [_num(d, "latencyMaxTicks") for d in drawn if _num(d, "latencyMaxTicks") is not None]
    p95 = max(p95s) if p95s else None
    lmax = max(maxs) if maxs else None
    status = "MISSING" if p95 is None else ("PASS" if p95 <= GHOST_LAT_P95 and (lmax is None or lmax <= GHOST_LAT_MAX) else "FAIL")
    row(rows, "S7a.N3.latency", status, "p95 %s, max %s ticks (%d rows)" % (_f(p95), _f(lmax), len(p95s)),
        "<= 3 p95, <= 6 max", what, "n3")
    # lifecycle: probe -> shown, stop -> removed
    crit, judged = [], 0
    probes = [w for w in L.windows("n3.probe.")]
    for k, (name, t0, _t1) in enumerate(probes):
        first = next((d for d in sorted(drawn, key=lambda d: _num(d, "presentedSeTick") or 0) if (_num(d, "presentedSeTick") or 0) >= t0), None)
        judged += 1
        lim = GHOST_SHOW_FIRST_TICKS if k == 0 else GHOST_SHOW_TICKS
        if first is None or _num(first, "presentedSeTick") - t0 > lim:
            crit.append("%s shown %s" % (name, "never" if first is None else "%.1f s" % ((_num(first, "presentedSeTick") - t0) / 60.0)))
    for name, t0, t1 in L.windows("n3.stop."):
        off = next((d for d in se if (_num(d, "tick") or 0) >= t0 and _flag(d, "active") is False), None)
        if off is None:
            continue
        offt = _num(off, "tick")

        def _gone_tick(d):   # review F6: the presented tick, or the newest C2 tick the row saw (a row written before the next camera sample)
            pt, nt = _num(d, "presentedSeTick") or 0, _num(d, "newestTick") or 0
            return pt if pt >= offt else max(pt, nt)

        gone = next((d for d in sorted(sn, key=lambda d: _num(d, "presentedSeTick") or 0)
                     if _flag(d, "present") is False and _gone_tick(d) >= offt), None)
        judged += 1
        dt = None if gone is None else int(_gone_tick(gone) - offt)
        if gone is None or dt > GHOST_REMOVE_TICKS:
            crit.append("%s removed %s" % (name, "never" if gone is None else "%d ticks" % dt))
    row(rows, "S7a.N3.lifecycle", "MISSING" if not judged else ("PASS" if not crit else "FAIL"), "; ".join(crit)[:34] or "%d checks" % judged,
        "shown <= 2 s, removed <= 1 tick", what, "n3", detail=crit)
    s6all = [d for d in (L.se + L.sn) if str(d.get("_ev", "")).startswith("s6.")]
    s6 = [d for d in s6all if _names_ghost(d)]
    # integrator: with S6 off in the session (no s6.* row at all) the row holds by construction only (the ghost is
 # never an entry of the S5 grid set, so no SkinSet and no proxy); the value says so
    row(rows, "S7a.N3.noproxy", "PASS" if not s6 else "FAIL",
        "%d s6 rows name the ghost (%s)" % (len(s6), "%d s6 rows" % len(s6all) if s6all else "S6 off: by construction"), "0", what, "n3")

def _f(v):
    return "-" if v is None else ("%.3g" % v)

# ----------------------------------------------------------------------------------------------------------------
# N4 the astronaut on a deck
# ----------------------------------------------------------------------------------------------------------------

def _params(d):
    p = d.get("params")
    return p if isinstance(p, dict) else {}

def n4_rows(L, rows):
    if "n4" not in L.parts_run():
        return
    anim = L.sn_ev("s7a.anim")
    what = "S7a.N4 astronaut on a deck: standing move_speed < 0.1 and player_view_idle on >= 90 %; walking move_speed >= 0.5 " \
           "and run/sprint clips on >= 90 %, Retarget on >= 95 % of foot-state frames; jump true after 0.45 s of air; first " \
           "person: the diver arms on >= 95 % of foot-state frames"

    SETTLE = 60   # S7a-2: the first second of a step (acceleration, clip blend) is not judged

    def rows_of(suffixes):
        """(row, weight in ticks) after the settle: the anim rows come on every change, so a share by rows over-counted the
 transitions (S7a-2: 28 % by rows while the astronaut ran at 6 m/s); the weight is the time until the next row."""
        out = []
        for name, t0, t1 in L.windows("n4."):
            if name.split(".")[-1] in suffixes:
                rs = sorted(L.in_ticks(anim, t0, t1, "seTick"), key=lambda d: _num(d, "seTick") or 0)
                for i, d in enumerate(rs):
                    a = _num(d, "seTick") or 0
                    b = (_num(rs[i + 1], "seTick") if i + 1 < len(rs) else t1) or t1
                    lo, hi = max(a, t0 + SETTLE), min(b, t1)
                    if hi > lo:
                        out.append((d, hi - lo))
        return out

    def wshare(pairs, pred):
        tot = sum(w for _d, w in pairs)
        return _share(sum(w for d, w in pairs if pred(d)), tot) if tot else None

    st = rows_of(("stand",))
    sh = wshare(st, lambda d: (_num(_params(d), "move_speed") or 0) < IDLE_SPEED_MAX and "player_view_idle" in str(d.get("clips", "")))
    row(rows, "S7a.N4.idle", "MISSING" if not st else ("PASS" if sh >= CLIP_SHARE else "FAIL"), "%s of %d rows (by time)" % (_pct(sh), len(st)),
        ">= 90 %", what, "n4.stand")
    wkp = rows_of(("walk", "walkback"))
    wk = [d for d, _w in wkp]
    sh = wshare(wkp, lambda d: (_num(_params(d), "move_speed") or 0) >= WALK_SPEED_MIN and
                any(c in str(d.get("clips", "")) for c in ("player_view_run", "player_view_sprint", "runF", "sprint")))
    footp = [(d, w) for d, w in wkp if str(d.get("locoState")) in FOOT]
    ff = [(_num(d, "footFrames"), _num(d, "footRetarget")) for d in wk if _num(d, "footFrames") is not None]
    if len(ff) >= 2 and ff[-1][0] > ff[0][0]:
        ret = (ff[-1][1] - ff[0][1]) / (ff[-1][0] - ff[0][0])
    else:
        ret = wshare(footp, lambda d: d.get("source") == "Retarget")
    crit = []
    if sh is not None and sh < CLIP_SHARE:
        crit.append("walk clip/speed %s" % _pct(sh))
    if ret is not None and ret < RETARGET_SHARE:
        crit.append("Retarget %s" % _pct(ret))
    row(rows, "S7a.N4.walk", "MISSING" if not wk else ("PASS" if not crit else "FAIL"), "; ".join(crit) or "%s, Retarget %s" % (_pct(sh), _pct(ret)),
        ">= 90 %, Retarget >= 95 %", what, "n4.walk", detail=crit)
    jp = [d for d, _w in rows_of(("jump", "land"))] + [d for name, t0, t1 in L.windows("n4.") if name.split(".")[-1] in ("jump", "land")
                                                       for d in L.in_ticks(anim, t0, t1, "seTick")]
    hit = [d for d in jp if _flag(_params(d), "jump")]
    row(rows, "S7a.N4.jump", "MISSING" if not jp else ("PASS" if hit else "FAIL"), "%d rows with jump" % len(hit), "jump true",
        what, "n4.jump")
    fp = []
    for name, t0, t1 in L.windows("n4.first."):
        fp += [d for d in L.in_ticks(anim, t0, t1, "seTick") if str(d.get("locoState")) in FOOT]
    sh = _share(sum(1 for d in fp if _flag(d, "armsShown")), len(fp))
    row(rows, "S7a.N4.arms", "MISSING" if not fp else ("PASS" if sh >= ARMS_SHARE else "FAIL"), "%s of %d rows" % (_pct(sh), len(fp)),
        ">= 95 %", what, "n4.first")

# ----------------------------------------------------------------------------------------------------------------
# N5 the time sync
# ----------------------------------------------------------------------------------------------------------------

def n5_rows(L, rows):
    if "n5" not in L.parts_run():
        return
    suns = sorted(L.sn_ev("s7a.sun"), key=lambda d: (_num(d, "snFrame") or 0, _num(d, "c6Tick") or 0))
    what = "S7a.N5 time sync: angle(C6 sunDirSn, uSkyManager.SunDir) <= 0.25 deg on >= 99 % outside holds and <= 2.25 deg " \
           "always; IsDay() = elevation > 0 beyond +-0.3 deg; timePassedAsDouble never decreases; sync off -> speed 1 " \
           "within 1 s, on -> <= 0.25 deg within 1 s; west teleport hold or one forward jump, east an advance"
    wn = L.window("n5")
    off = L.window("n5.syncoff")
    inn = [d for d in suns if wn and wn[1] <= (_num(d, "c6Tick") or -1) <= wn[2]]
    # S7a-1: a jump sample (and the second after it, while the clock settles) is judged by the west/east rows,
 # not by the error share: the C6 target moves by up to a day in one step there
    jumps = [_num(d, "c6Tick") for d in inn if d.get("step") == "jump" and _num(d, "c6Tick") is not None]
    judged = [d for d in inn if _flag(d, "drive") is not False and d.get("step") not in ("hold", "jump") and _num(d, "errDeg") is not None
              and not (off and off[1] <= _num(d, "c6Tick") <= off[2])
              and not any(0 <= _num(d, "c6Tick") - jt <= 60 for jt in jumps)]
    good = [d for d in judged if _num(d, "errDeg") <= SUN_ERR_MAX]
    worst = max((_num(d, "errDeg") for d in judged), default=None)
    sh = _share(len(good), len(judged))
    status = "MISSING" if not judged else ("PASS" if sh >= SUN_ERR_SHARE and worst <= SUN_ERR_ALWAYS else "FAIL")
    row(rows, "S7a.N5.err", status, "%s <= 0.25, max %s deg" % (_pct(sh), _f(worst)), ">= 99 %, <= 2.25 always", what, "n5")
    dd = [d for d in inn if _flag(d, "drive") is not False and _num(d, "c6ElevDeg") is not None and abs(_num(d, "c6ElevDeg")) > ISDAY_GUARD_DEG
          and _flag(d, "isDay") is not None and not (off and off[1] <= _num(d, "c6Tick") <= off[2])]
    bad = [d for d in dd if _flag(d, "isDay") != (_num(d, "c6ElevDeg") > 0)]
    row(rows, "S7a.N5.isday", "MISSING" if not dd else ("PASS" if not bad else "FAIL"), "%d of %d wrong" % (len(bad), len(dd)),
        "IsDay = elevation > 0", what, "n5")
    tp = [(_num(d, "snFrame") or 0, _num(d, "timePassed")) for d in suns if _num(d, "timePassed") is not None]
    tp.sort()
    back = [(a, b) for (f0, a), (f1, b) in zip(tp, tp[1:]) if b < a - 1e-9]
    row(rows, "S7a.N5.monotonic", "MISSING" if len(tp) < 2 else ("PASS" if not back else "FAIL"), "%d decreases of %d" % (len(back), len(tp)),
        "never decreases", what, "n5", back=back[:3])
    crit = []
    if off:
        rs = [d for d in suns if off[1] + TICKS_PER_S < (_num(d, "c6Tick") or -1) <= off[2]]
        if not rs:
            crit.append("no rows 1 s after sync off")
        elif any(_num(d, "dayNightSpeed") not in (None, 1.0) or _flag(d, "drive") for d in rs):
            crit.append("speed not 1 after sync off")
    on = L.window("n5.syncon")
    if on:
        rs = [d for d in suns if on[1] + TICKS_PER_S < (_num(d, "c6Tick") or -1) <= on[2]]
        if not rs:
            crit.append("no rows 1 s after sync on")
        else:
            if any((_num(d, "errDeg") or 99) > SUN_ERR_MAX for d in rs):
                crit.append("err > 0.25 after sync on")
            if any(_num(d, "dayNightSpeed") not in (0.0, None) for d in rs):
                crit.append("speed not 0 while synced")
    row(rows, "S7a.N5.syncoff", "MISSING" if not (off and on) else ("PASS" if not crit else "FAIL"), "; ".join(crit) or "off/on",
        "speed 1 off, 0.25 deg on", what, "n5.syncoff", detail=crit)
    crit, seen = [], 0
    for name, want in (("n5.west", ("hold", "jump")), ("n5.east", ("advance", "jump"))):
        w = L.window(name)
        if not w:
            continue
        seen += 1
        rs = [d for d in suns if w[1] <= (_num(d, "c6Tick") or -1) <= w[2]]
        steps = [d.get("step") for d in rs]
        if name == "n5.west":
            if not any(s in want for s in steps):
                crit.append("west: no hold or jump (%s)" % ",".join(sorted(set(str(s) for s in steps))))
        else:
            fwd = [d for d in rs if d.get("step") in want and 0 < (_num(d, "advancedS") or 0) < 600]
            if not fwd:
                crit.append("east: no forward advance")
    row(rows, "S7a.N5.teleport", "MISSING" if not seen else ("PASS" if not crit else "FAIL"), "; ".join(crit) or "west hold/jump, east advance",
        "as the oracle", what, "n5.east", detail=crit)

# ----------------------------------------------------------------------------------------------------------------
# T-P1r (a tp1 folder from `launcher.py tp1 --picture`)
# ----------------------------------------------------------------------------------------------------------------

def tp1r_rows_for(folder, baseline=None, picture=None):
    import analyze_s5 as S5
    L = S5.Logs(folder)
    rows = []
    run = [p[4:] for p in L.parts_run() if p and p.startswith("tp1.")]
    if picture is None:
        try:
            with open(os.path.join(folder, "s7a-tp1.json"), encoding="utf-8") as f:
                picture = json.load(f).get("picture")
        except (OSError, ValueError):
            picture = None
    base = S5.load_tp1_baseline(baseline) if baseline else {}
    what = "S7a.T-P1r (picture %s): SE simulation ratio >= 0.98 everywhere; SE frame time in Space within 5 %% of SE alone; " \
           "Subnautica p99 <= 16.7 ms in band-hover, track-hover, track-sail, prime, underwater (all Full); commit < 85 %% of RAM, no " \
           "page-file growth; Track SE frame time vs SE alone INFO" % (picture or "?")
    for st in run:
        d = S5.tp1_state_data(L, st)
        if st.startswith("base"):
            row(rows, "S7a.T-P1r.%s" % st, "INFO", "SE %s ms, SN p99 %s ms" % (_f(d["seFrameMs"]), _f(d["snP99"])), "baseline", what,
                "tp1.%s" % st, required=False, data=d)
            if st.startswith("base-se"):
                base[st] = {"seFrameMs": d["seFrameMs"]}
            continue
        crit = []
        if d["simMean"] is None:
            crit.append(("sim", "MISSING", "no samples"))
        else:
            crit.append(("sim", "PASS" if d["simMean"] >= TP1_SIM_MIN else "FAIL", "%.3f" % d["simMean"]))
        if d["commitMax"] is None:
            crit.append(("commit", "MISSING", "no mem"))
        else:
            crit.append(("commit", "PASS" if d["commitMax"] < TP1_COMMIT_MAX else "FAIL", "%.1f %%" % (d["commitMax"] * 100)))
            crit.append(("pagefile", "PASS" if not d["pageFileGrowth"] else "FAIL", "growth %s" % _f(d["pageFileGrowth"])))
        sb = next(((base.get(k) or {}).get("seFrameMs") for k in ("base-se." + st, "base-se") if (base.get(k) or {}).get("seFrameMs")), None)
        if st == "space":
            if d["seFrameMs"] is None or sb is None:
                crit.append(("seFrame", "MISSING", "SE %s / alone %s ms" % (_f(d["seFrameMs"]), _f(sb))))
            else:
                r = d["seFrameMs"] / sb - 1.0
                crit.append(("seFrame", "PASS" if r <= TP1_FRAME_TOL else "FAIL", "%+.1f %%" % (r * 100)))
        if st in ("band-hover", "track-hover", "track-sail", "prime", "underwater"):   # review F12: the band hover (Full at 1 km)
            if d["snP99"] is None:
                crit.append(("snP99", "MISSING", "no p99"))
            else:
                crit.append(("snP99", "PASS" if d["snP99"] <= TP1_SN_P99_MS else "FAIL", "%.2f ms" % d["snP99"]))
        if st in ("track-hover", "track-sail"):
            info = "SE %s ms vs alone %s ms" % (_f(d["seFrameMs"]), _f(sb))
            row(rows, "S7a.T-P1r.%s.seFrame" % st, "INFO", info, "INFO (S5's 5 % rule retired)", what, "tp1.%s" % st, required=False)
        sts = [c[1] for c in crit]
        status = "FAIL" if "FAIL" in sts else ("MISSING" if "MISSING" in sts else "PASS")
        row(rows, "S7a.T-P1r.%s" % st, status, "; ".join("%s %s" % (n, m) for n, s, m in crit if s != "PASS")[:34] or "all criteria",
            "every criterion", what, "tp1.%s" % st, criteria=[{"name": n, "status": s, "measured": m} for n, s, m in crit])
    if not run:
        row(rows, "S7a.T-P1r", "MISSING", "no tp1 parts", "", what, "tp1")
    return rows

# ----------------------------------------------------------------------------------------------------------------
# evaluation
# ----------------------------------------------------------------------------------------------------------------

def evaluate(folder):
    if os.path.exists(os.path.join(folder, "tp1-session.jsonl")) and not os.path.exists(os.path.join(folder, "s7a-session.jsonl")):
        return tp1r_rows_for(folder)
    L = Logs(folder)
    rows = []
    n1_rows(L, rows)
    n2_rows(L, rows)
    n3_rows(L, rows)
    n4_rows(L, rows)
    n5_rows(L, rows)
    mutants = L.start.get("mutants") or []
    for m in mutants:
        want = MUTANT_RED.get(m, ())
        got = [r for r in rows if r["id"] in want]
        for r in got:
            r["required"] = False      # judged by the.red row
        red = [r["id"] for r in got if r["status"] == "FAIL"]
        status = "MISSING" if not got else ("PASS" if len(red) == len(got) else "FAIL")
        row(rows, "%s.red" % m, status, "red: %s" % (",".join(red) or "none"), "red: %s" % ",".join(want),
            "seeded failure %s turns its rows red" % m, "mutants")
    return rows

def table(rows):
    return C.table(rows)

def write_results(folder, rows, strict=True, extra=None, name="s7a") -> bool:
    ok = C.verdict(rows, strict)
    res = {"tool": "analyze_s7a", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "%s-results.json" % name), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "%s-table.txt" % name), "w", encoding="utf-8") as f:
        f.write(C.table(rows) + "\n")
    return ok

# ----------------------------------------------------------------------------------------------------------------
# self-test: synthetic sessions in the log contract
# ----------------------------------------------------------------------------------------------------------------

class _Synth:
    """A synthetic S7a session: the launcher's parts and samples, SE rows and Subnautica rows, in SE ticks."""

    def __init__(self, mutant=None):
        self.m = mutant
        self.tick = 1000
        self.sess, self.se, self.sn = [], [], []
        self.frame = 5000
        self.tp = 1200.0 * 10 + 300.0
        set_band(300.0, 350.0)   # the synthetic session was written for the original band; the start row carries it for Logs
        self.sess.append({"ev": "start", "kind": "s7a", "parts": ["n1", "n2", "n3", "n4", "n5"], "mutants": [mutant] if mutant else [],
                          "band": [300.0, 350.0]})
        self.view, self.approach, self.picture, self.y = VIEW_SPACE, 0, False, 2000.0

    def part(self, name, phase):
        self.sess.append({"ev": "part", "name": name, "phase": phase, "tick": self.tick})

    def se_row(self, ev, **kw):
        r = {"t": "2026-10-17T12:00:00Z", "ev": ev, "tick": self.tick}
        r.update(kw)
        self.se.append(r)

    def sn_row(self, ev, **kw):
        self.sn.append({"t": "2026-10-17T12:00:00Z", "event": ev, "ev": ev, "data": dict(kw, snFrame=self.frame)})

    def step(self, ticks=1):
        self.tick += ticks
        self.frame += ticks * 2

    # the machine (Site policy) as the rows see it
    def set_view(self, view, approach, y):
        prev_view, prev_pic = self.view, self.picture
        self.view, self.approach, self.y = view, approach, y
        waterline = self.m == "S7a.M1"
        if waterline:
            pic = view in (VIEW_UNDER, VIEW_SURF)
        else:
            pic = view != VIEW_SPACE and y < BAND_ENTER_Y
        edge = 0
        if pic != prev_pic:
            edge = 1 if pic else -1
        self.picture = pic
        if edge or view != prev_view:
            # as SE writes it: names (ViewStateMachine.ViewName / ApproachName), integrator
            self.se_row("s7a.view", view=_VIEW_NAMES[view], approach=_APPROACH_NAMES[approach], policy="waterline" if waterline else "site",
                        picture=pic, edge=edge, y=y)
        if edge:
            if self.m != "S7a.M3":
                self.se_row("s7a.flash", kind="band" if not waterline else "waterline", toSn=edge > 0, durationMs=260)
            self.sun(err=0.01)
        return edge

    def pic_row(self):
        site = self.view != VIEW_SPACE
        see = self.picture
        park = (self.view == VIEW_SPACE) or (self.m == "S7a.M2" and self.view == VIEW_APPROACH and self.approach == 1)
        flags = (C5_SEE_THROUGH if see else 0) | (C5_MARGINS if site else 0) | (0 if self.m == "S7a.M1" else C5_PICTURE_SITE)
        self.sn_row("s7a.picture", presentedSeTick=self.tick, view=self.view, approach=self.approach, perfMode=PERF_FULL if site else PERF_IDLE,
                    perfApplied=PERF_FULL if site else PERF_IDLE, seeThrough=see, marginsOn=site, pictureSite=self.m != "S7a.M1",
                    park=park, phase=6 if site else 4, hideAllowed=False, camY=self.y, frameMsP99=11.0)
        self.sess.append({"ev": "sample", "c1Tick": self.tick, "c5": {"viewState": self.view, "approachMode": self.approach, "flags": flags}})

    def sun(self, err=0.01, step="advance", speed=0.0, drive=True, elev=30.0, adv=0.016):
        if self.m == "S7a.M6" and step in ("hold", "jump"):
            self.tp -= 9.0
        else:
            self.tp += adv
        self.sn_row("s7a.sun", c6Tick=self.tick, jumpSeq=1, errDeg=err, step=step, advancedS=adv, timePassed=self.tp, isDay=elev > 0,
                    c6ElevDeg=elev, dayNightSpeed=speed, drive=drive)

    def hold(self, seconds, every=30):
        for _ in range(int(seconds * TICKS_PER_S / every)):
            self.step(every)
            self.pic_row()

    def move(self, y0, y1, speed, view_of):
        n = int(abs(y1 - y0) / speed * TICKS_PER_S)
        for i in range(n + 1):
            y = y0 + (y1 - y0) * i / float(max(1, n))
            v, a = view_of(y)
            self.set_view(v, a, y)
            if i % 30 == 0:
                self.pic_row()
            self.step(1)

def _band_view(y):
    if y > BAND_EXIT_Y:
        return VIEW_SPACE, 0
    if y < -0.2:
        return VIEW_UNDER, 0
    return VIEW_APPROACH, (2 if y < 10.0 else 1)

def synth_session(folder, mutant=None):
    s = _Synth(mutant)
    # N1
    s.part("n1", "begin")
    s.part("n1.space", "begin"); s.set_view(VIEW_SPACE, 0, 2000.0); s.hold(5); s.part("n1.space", "end")
    s.part("n1.approach", "begin"); s.set_view(VIEW_SPACE, 0, 400.0); s.hold(3); s.part("n1.approach", "end")
    s.part("n1.descent", "begin"); s.move(400.0, 250.0, 20.0, _band_view); s.part("n1.descent", "end")
    s.part("n1.track", "begin"); s.hold(10); s.part("n1.track", "end")
    s.part("n1.toprime", "begin"); s.move(250.0, 3.0, 50.0, _band_view); s.part("n1.toprime", "end")
    s.part("n1.prime", "begin"); s.hold(10); s.part("n1.prime", "end")
    s.part("n1.dive", "begin"); s.move(3.0, -5.0, 2.0, _band_view); s.part("n1.dive", "end")
    s.part("n1.underwater", "begin"); s.hold(10); s.part("n1.underwater", "end")
    s.part("n1.surface", "begin")
    s.move(-5.0, -0.1, 2.0, _band_view)
    s.set_view(VIEW_SURF, 0, -0.04); s.step(1); s.pic_row()
    s.move(-0.04, 3.0, 2.0, _band_view)
    s.part("n1.surface", "end")
    s.part("n1.surfacing", "begin"); s.hold(5); s.part("n1.surfacing", "end")
    s.part("n1.climb150", "begin"); s.move(3.0, 150.0, 20.0, _band_view); s.part("n1.climb150", "end")
    s.part("n1.capture", "begin"); s.hold(3); s.sess.append({"ev": "capture", "path": "s7a/x/n1.capture150-sn-01.png"}); s.part("n1.capture", "end")
    s.part("n1.climb500", "begin"); s.move(150.0, 500.0, 20.0, _band_view); s.part("n1.climb500", "end")
    s.part("n1.space2", "begin"); s.hold(5); s.part("n1.space2", "end")
    s.part("n1.far", "begin"); s.set_view(VIEW_SPACE, 0, 150.0); s.hold(5); s.part("n1.far", "end")
    s.part("n1", "end")
    # N2
    s.part("n2", "begin")
    for k, sp in enumerate((20.0,) * 5 + (100.0,) * 2):
        s.set_view(VIEW_SPACE, 0, 600.0); s.hold(1)
        s.part("n2.d%d.%d" % (int(sp), k + 1), "begin"); s.move(600.0, 200.0, sp, _band_view); s.part("n2.d%d.%d" % (int(sp), k + 1), "end")
        s.part("n2.a%d.%d" % (int(sp), k + 1), "begin"); s.move(200.0, 600.0, sp, _band_view); s.part("n2.a%d.%d" % (int(sp), k + 1), "end")
    s.set_view(VIEW_APPROACH, 1, 325.0)
    s.part("n2.hover", "begin")
    for i in range(20 * TICKS_PER_S // 10):
        s.set_view(VIEW_APPROACH, 1, 325.0 + 20.0 * math.sin(i / 60.0))
        s.step(10)
    s.part("n2.hover", "end")
    s.part("n2", "end")
    # N3
    s.part("n3", "begin")
    allowed_seq = (True, False, True, True)
    first = True
    for blk in ("LargeBlockArmorBlock", "SmallBlockSmallThrust"):
        s.part("n3.probe.%s" % blk, "begin")
        s.step(30)
        for aim, allowed in zip(("allowed", "blocked", "voxel", "free"), allowed_seq):
            for i in range(30):
                s.se_row("s7a.ghost", active=True, shown=True, allowed=allowed, buildAllowed=allowed)
                c2 = s.tick
                s.step(1)
                drawn = True if s.m == "S7a.M4" else allowed
                s.sn_row("s7a.ghost", presentedSeTick=s.tick, c2Tick=c2, present=True, shown=True, allowed=drawn, alpha=1.0,
                         poseErrMm=0.2, poseErrDeg=0.002, firstPresentedTick=c2 + 2, latencyP95Ticks=2, latencyMaxTicks=3, material="glass")
            first = False
        s.part("n3.probe.%s" % blk, "end")
        s.part("n3.stop.%s" % blk, "begin")
        s.step(2)
        s.se_row("s7a.ghost", active=False, shown=False, allowed=False)
        s.step(1)
        s.sn_row("s7a.ghost", presentedSeTick=s.tick, present=False, shown=False, allowed=False)
        s.part("n3.stop.%s" % blk, "end")
    s.se_row("s6.skin", entity=12345, sections=3)
    s.part("n3", "end")
    # N4
    s.part("n4", "begin")
    for view in ("third", "first"):
        for lab, secs, spd, state, clip in (("stand", 5, 0.0, "Idle", "player_view_idle"), ("walk", 5, 1.2, "Walk", "player_view_runF_3p5"),
                                            ("walkback", 5, 1.2, "Walk", "player_view_runB"), ("jump", 0.5, 0.2, "Jump", "player_view_jump_START"),
                                            ("land", 2.5, 0.0, "Fall", "player_view_jump_loop_HIGH"), ("stepoff", 5, 2.0, "Walk", "player_view_runF_3p5")):
            name = "n4.%s.%s" % (view, lab)
            s.part(name, "begin")
            for i in range(max(1, int(secs * 2))):
                s.step(30)
                ms = 0.0 if s.m == "S7a.M5" else spd
                jump = lab == "land"
                s.sn_row("s7a.anim", seTick=s.tick, source="Retarget", locoState=state, inWater=False,
                         params={"is_underwater": False, "on_surface": False, "move_speed": ms, "jump": jump, "diving": False},
                         clips=clip if ms > 0 or lab in ("stand", "jump", "land") else "player_view_idle", armsShown=view == "first",
                         fpbody="diver", walkSource="retarget", footFrames=1000 + s.tick, footRetarget=1000 + s.tick)
            s.part(name, "end")
    s.part("n4", "end")
    # N5
    s.part("n5", "begin")
    s.part("n5.samples", "begin")
    for i in range(150):
        s.step(120)
        s.sun(err=0.02 + 0.001 * (i % 7), elev=40.0 - i * 0.6)
    s.part("n5.samples", "end")
    s.part("n5.syncoff", "begin")
    for i in range(20):
        s.step(30)
        s.sun(err=0.0 if i < 2 else 3.0, speed=0.0 if i < 2 else 1.0, drive=i < 2, step="none")
    s.part("n5.syncoff", "end")
    s.part("n5.syncon", "begin")
    for i in range(20):
        s.step(30)
        s.sun(err=0.05, step="jump" if i == 0 else "advance")
    s.part("n5.syncon", "end")
    s.part("n5.east", "begin")
    for i in range(10):
        s.step(60)
        s.sun(err=0.05, step="jump" if i == 0 else "advance", adv=9.5 if i == 0 else 0.016)
    s.part("n5.east", "end")
    s.part("n5.west", "begin")
    for i in range(10):
        s.step(60)
        s.sun(err=0.05, step="jump" if i == 0 else "advance", adv=1190.0 if i == 0 else 0.016)
    s.part("n5.west", "end")
    s.part("n5", "end")
    os.makedirs(folder, exist_ok=True)
    for name, recs in (("s7a-session.jsonl", s.sess), ("se-synth.jsonl", s.se), ("sn-synth.jsonl", s.sn)):
        with open(os.path.join(folder, name), "w", encoding="utf-8") as f:
            for r in recs:
                f.write(json.dumps(r) + "\n")
    return folder

def selftest() -> int:
    ok = conventions_selftest()
    tmp = tempfile.mkdtemp(prefix="s7a-selftest-")
    rows = evaluate(synth_session(os.path.join(tmp, "good")))
    bad = [r for r in rows if r["status"] not in ("PASS", "INFO")]
    print("S7a synthetic session: %d rows, %d not PASS%s" % (len(rows), len(bad), ": " + ", ".join("%s %s (%s)" % (
        r["id"], r["status"], r["measured"]) for r in bad) if bad else ""))
    ok &= not bad and len(rows) >= 24
    for m, want in MUTANT_RED.items():
        mrows = evaluate(synth_session(os.path.join(tmp, m), m))
        red = [r["id"] for r in mrows if r["id"] in want and r["status"] == "FAIL"]
        mr = next((r for r in mrows if r["id"] == "%s.red" % m), None)
        others = [r["id"] for r in mrows if r["status"] == "FAIL" and r["id"] not in want and r.get("required", True)]
        good = sorted(red) == sorted(want) and mr is not None and mr["status"] == "PASS"
        print("  %s: red %s (want %s), %s.red %s%s" % (m, ",".join(red) or "none", ",".join(want), m, mr["status"] if mr else "-",
                                                       ", other required FAIL: " + ",".join(others) if others else ""))
        ok &= good
    # T-P1r on the S5 analyzer's synthetic tp1 folder
    try:
        import analyze_s5 as S5
        tp = os.path.join(tmp, "tp1")
        S5.synth_session(tp, None, "tp1")
        trows = tp1r_rows_for(tp, picture="site")
        ids = sorted(r["id"] for r in trows)
        need = ["S7a.T-P1r.%s" % st for st in ("space", "track-hover", "track-sail", "prime", "underwater")]
        tbad = [r for r in trows if r["id"] in need and r["status"] == "FAIL"]
        print("  T-P1r on a synthetic tp1 folder: %d rows (%s), %d FAIL" % (len(trows), ",".join(i.split(".")[-1] for i in ids), len(tbad)))
        ok &= all(i in ids for i in need) and not tbad
    except Exception as e:      # the S5 synthetic generator needs its own inputs; report, do not hide
        print("  T-P1r synthetic check not run: %s: %s" % (type(e).__name__, e))
        ok = False
    print("analyze_s7a selftest %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv) -> int:
    if "--selftest" in argv:
        return selftest()
    lenient = "--lenient" in argv
    if "--tp1r" in argv:
        i = argv.index("--tp1r")
        folder = argv[i + 1]
        base = argv[argv.index("--baseline") + 1] if "--baseline" in argv else None
        pic = argv[argv.index("--picture") + 1] if "--picture" in argv else None
        rows = tp1r_rows_for(folder, base, pic)
        ok = write_results(folder, rows, not lenient, name="s7a-tp1r")
        print(table(rows))
        return 0 if ok else 1
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
