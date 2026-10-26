"""S2b swimming oracle (ROADMAP S2b.N1-N4) from a collected `launcher.py s2b` session (SE alone, scripted input).

 python -I tests/s2b/analyze_swim.py tests/out/s2b/<stamp> [--json] [--model swim_replay|plant]
 python -I tests/s2b/analyze_swim.py --selftest # stand-in traces + seeded failures S2b.M1-M4

The SE plugin (S2b) replaces the engineer's movement in the water of DE_4546B by Subnautica's underwater motor and
logs it; the launcher's `s2b` part (src/DeepEngineers.Launcher/launcher.py, S2bRunner) drives the character with
`de.swim` scripts because nobody is at the keyboard. This analyser judges the logs against the independent model.

SOURCES (a session folder tests/out/s2b/<stamp>/)
 s2b-session.jsonl the launcher: start {parts, mutants, plan}, part {name, phase begin|end, qpc, tick (C1 tick =
 MySession.GameplayFrameCounter, the counter of the swim rows)}, reply {cmd "de.swim", args, reply,
 label} (every command, dumps included), swim_script {label, segments [{label, seconds, forward,
 strafe, vertical, sprint, jetpack, move}], reply}, o2 {label, o2 fraction, qpc, tick}, refill, n2,
 n3 {cycles, cycle}, n4 {depthY, early,...}, file_hash {path, when before|after, sha256},
 precheck {ok, problems, helmet, jetpackPowered, h2Level, o2Bottles,...},
 suit_prep {jetpackWas,...}, end
 se-*.jsonl the SE plugin, one writer per file. "swim" rows (one per SE tick while swimming or scripted) in
 any of three layouts, recognised per row:
 B the SE controller's row (SwimController.WriteRow, v1): tick, dt, src (script|keys|none), step (script step
 index, -1 none), in [forward, strafe, vertical, sprint], look [3], right [3], state swim|out, tr (+1 entry,
 -1 exit, 0), exit (reason), yIn (eye y the controller saw), chestY, grounded, stepped (a 0.1 s motor step ran),
 applied, vIn [3], vSet [3] (velocity imposed), vSe [3] (velocity after SE's physics step: the measured motion),
 pos [3] (eye after the step), jet, damp (after the suit rules), depthClass, o2 (suit fraction), o2L, o2Cap,
 o2Mult, helmet, mut
 A the Wire model's SwimTrace row (src/DeepEngineers.Wire/SwimModel.cs, read by tests/s2b/swim_replay.py): n,
 tick, md, exact IEEE bits for mx my mz (strafe, vertical, forward) fx fy fz rx ry rz vx vy vz (velocity at the
 start of the tick: the previous tick's measured motion) px py pz (eye) ox (O2 in Subnautica units), spr tool
 gnd jet mut, state s_*, outputs o_md o_ctl o_vx o_vy o_vz (imposed) o_st (motor step) o_ja (jetpack allowed)
 o_or o_do2 o_dc o_evt (1 entered, 2 exited) o_chg. THE PLUGIN WRITES THIS LAYOUT with
 its own keys appended (SwimRowExtras): t (UTC), src, step, vsrc, exit (reason), chestY, applied, gain, vApp,
 vSe (SE's velocity after its physics step: the measured motion), posAfter (eye after the step), jetWant, damp,
 dampWant, o2Use, o2Refill, o2L, o2Cap, o2Mult, helmet
 G generic keys (aliases): tick, seg, move [strafe, vertical, forward], sprint, look, vel, pos, yHead, yChest,
 inW|state, jet, jetOk, damp, o2 (fraction; > 1 read as percent), tr enter|exit
 text lines `DE.Swim enter tick=N y=... chestY=... jetpackWas=on|off dampenersWas=on|off...`,
 `DE.Swim exit tick=N y=... reason=R jetpack->on|off|kept dampeners->... o2mult->...`,
 `DE.Swim jetpack off: head under...`, `DE.Swim FEATURE DISABLED <feature>: <why>` (FAIL)
"Measured" velocity of a tick: B vSe; A vSe when logged, else the next contiguous row's input velocity (as
swim_replay.py n1); G vel.
Transitions: the rows' own flags (B tr, A o_evt) and the text lines, merged per (tick, kind); without any, inW flips.
Windows: a part's rows lie between its begin/end C1 ticks (else the rows' UTC is mapped to QPC). A script's segments
are found by row labels, else by the script step index of B rows (src script), else by runs of identical inputs
matched to the plan, else by time from the first row of the script.

THE MODEL (independent; never the SE code): tests/s2b/swim_replay.py, imported (it reads tests/s2b/sn_reference.py)
through its analyzer_port -> an object with step(v, tick) -> v' (one SE tick, open loop on the velocity; tick =
{move (strafe, vertical, forward), sprint, look, right, pos, dt, yHead, inWater, grounded, tickNo, row (the logged row
or None), phase, motorStep}), o2(depth_m) -> {rate, capacity, breathS, breathUnits}, o2_refill, surface_head_max,
optional begin and step_ticks. A swim_replay.py without analyzer_port is driven through its State/step; another
module through a step function matched by parameter names. Not importable or not usable: the N1 rows and the N4 rate
row are MISSING (never PASS). `--model plant` replays with this file's selftest plant (diagnostics only; those rows
are then not required).
 A rows: the module's openloop_series(rows) replays the window from its first row's logged state (as its own n1).
 B and G rows: one model tick per SE tick with the logged inputs and the model's own previous velocity. The 0.1 s
 motor-step phase comes from the rows' stepped flags (else from the ticks on which the imposed velocity changes);
 the replay starts on the last step tick at or before the window (a pre-roll over the rows before it), so a model
 that steps from its first tick runs in phase with SE.

ROWS (id, part; required unless marked INFO / *)
 S2b.N1 n1 per segment of the two N1 scripts at -50 m, measured vs the open-loop replay of the logged inputs:
 plateau velocity (mean of the last 1 s, vector error) of fwd, strafe, up, down, back, sprint; vertical
 speed of up/down; time to 90 % of the plateau (fwd, up; the others INFO); stop distance after a release
 (rel1, rel2, rel4, rel5; rel3 INFO) and time to 10 % of the release speed (rel1; others INFO); sprint
 factor (sprint / forward plateau; Subnautica swims without sprint). Within 5 % of the replay; floors as
 swim_replay.py n1: 0.05 m/s, 2 ticks, 0.05 m, 0.01. INFO: worst per-tick deviation, the segment method.
 S2b.N2 n2 swimming up 20 s from -20 m (segment n2.up): the eye reaches the surface (max >= -0.10 m) and never
 rises above the model's own climb on the same inputs + 0.05 m

 all jetpack off with the head under water (eye < -0.1 m): no two
 consecutive rows on, no row allowed (S2b.M2 red: the n2 script requests the jetpack at -18 m); all
 dampeners off on the in-water rows (when logged); n2 the request at -18 m reached SE's jetpack (a
 head-under row with SE's jetpack on, or the swim rule's switch-off line: without hydrogen SE refuses
 the jetpack and the check above could not fail); n2j jetpack back with the head out (SE's jetpack
 running on a row or a dump with the eye above the surface, or an exit with reason jetpack; the model's
 jetOk alone does not count).
 S2b.N3 n3 per cycle of the scripted crossings (swim up, jetpack out, fall back in): exactly one exit and one
 entry; over the part, transitions alternate and are >= 0.5 s apart; n2 the surface hold (n2.up: ~17 s
 at the clamp) has at most one transition, none within 0.5 s of another (without hysteresis the
 character bounces there: S2b.M3 red); INFO the eye at the transitions, the jetpack/dampener restore on
 exit against the entry's remembered states (DE.Swim enter/exit lines); all SE's own jetpack and
 dampeners after each exit (the row 3 ticks later, else a de.swim dump before the next entry) as the
 exit line restored them, and those as the entry remembered them (required when exit lines exist).
 S2b.N4 n4 O2 rate underwater at -30 m (n4.dive; per-tick rows when they carry O2, else the 5 s o2=dump replies):
 within 5 % of the model's rate (+ one breath / span when the model breathes in steps and only the sparse
 dumps exist; S2b.M4 red; upward jumps, e.g. SE's refill from a carried O2 bottle below 50 %, split the
 series and only the falling pieces count); refill at the surface (>= 98 % in n4.surface); SpaceEngineers.cfg and
 WaterClientSettings.xml unchanged; INFO no depth damage

 S2b.ALL no `DE.Swim FEATURE DISABLED` line (FAIL if any; MISSING if nothing of S2b was logged); no `DE.Swim
 error`, `DE.Swim depth MISMATCH` or `(S2b patch off)` line; the launcher's session preconditions
 (precheck: helmet closed, jetpack powered, no O2 bottle; MISSING without the event); INFO row
 layouts and counts, mutants, model; S2b.
Exit code 0 when every required row is PASS.
"""
from __future__ import annotations

import argparse
import glob
import importlib
import inspect
import json
import math
import os
import re
import struct
import sys
from collections import Counter

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.normpath(os.path.join(_HERE, "..", ".."))
for _p in (_HERE, os.path.join(_ROOT, "tests", "s2")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C  # noqa: E402 (rows, table, verdict, JSONL reading, UTC parsing)

TICK_HZ = 60.0
TICK_S = 1.0 / TICK_HZ
REL_TOL = 0.05                  # S2b.N1 / N4: within 5 % of the model
FLOOR_SPEED = 0.05              # m/s absolute floors (as swim_replay.py n1)
FLOOR_DIST = 0.05               # m
FLOOR_TIME = 2.0 * TICK_S       # s two ticks: SE reports the velocity of a tick on the next one
FLOOR_FACTOR = 0.01
PLATEAU_S = 1.0                 # plateau = mean velocity over the last second of a held input
T90 = 0.9                       # acceleration time: segment start -> 90 % of the plateau speed
T10 = 0.1                       # stop time: release -> 10 % of the release speed
SURFACE_REACH_Y = -0.10         # S2b.N2: the eye got to the surface...
SURFACE_TOL_M = 0.05            #... and never above the model's swim limit + 5 cm
SURFACE_HEAD_MAX_DEFAULT = 0.52  # waterLevel + 0.8 - 0.28 (when the model gives none)
JET_OFF_Y = -0.1                # the jetpack is withdrawn below this eye height (sn_reference JETPACK_OFF_EYE_Y)
HEAD_OUT_Y = 0.0                # the jetpack is back from this eye height (JETPACK_ON_EYE_Y)
BOUNCE_S = 0.5                  # S2b.N3: no two transitions closer than this
O2_FULL = 0.98                  # S2b.N4: refilled
O2_EMPTY = 0.02                 # samples at or below this are the empty-tank floor, not the rate
O2_ROWS_MIN = 10                # per-tick O2 from the rows when at least this many samples exist in the dive
O2_CAPACITY_DEFAULT = 45.0      # Subnautica units (seconds) of the diver's own tank, to read A rows' ox
STEP_TICKS_DEFAULT = 6          # Subnautica's 0.1 s motor step in SE ticks
PHASE_SHARE = 0.8               # a motor-step phase is named when this share of the evidence agrees
N1_PARTS = ("n1a", "n1b")
N1_DRIVE = ("n1.fwd", "n1.strafe", "n1.up", "n1.down", "n1.back", "n1.sprint")
N1_RELEASE = ("n1.rel1", "n1.rel2", "n1.rel3", "n1.rel5", "n1.rel4")
N1_REQUIRED_T90 = ("n1.fwd", "n1.up")
N1_REQUIRED_STOP = ("n1.rel1", "n1.rel2", "n1.rel4", "n1.rel5")
N1_REQUIRED_T10 = ("n1.rel1",)
SPRINT_SEG, FORWARD_SEG = "n1.sprint", "n1.fwd"
_TEXT_TR = re.compile(r"^DE\.Swim (enter|exit)\b(.*)$")
_DISABLED = "DE.Swim FEATURE DISABLED"
_ERROR_LINES = ("DE.Swim error", "DE.Swim depth MISMATCH", "(S2b patch off)")   # must never appear (S2B-STATUS §10.3)
RESTORE_LAG_TICKS = 3           # the controller's jetpack/dampener switches are applied by the plugin Update after the tick
O2_JUMP = 0.002                 # an O2 rise above this between two samples splits the rate series (bottle refill)
_SWIM_EVENTS = ("swim", "s2b.swim", "swim.row")
_TR_EVENTS = {"swim.enter": "enter", "swim.exit": "exit", "swim.transition": None, "swim.tr": None}
_ENTER_WORDS = ("enter", "entry", "swim", "swimming", "water", "underwater", "in")
_EXIT_WORDS = ("exit", "walk", "fly", "air", "land", "surface", "out", "jetpack", "stand")
_HEX = re.compile(r"^[0-9a-f]{16}$")

try:
    import sn_reference as _R    # the S2b number table (thresholds; another tool's file, numbers only)
    JET_OFF_Y = _R.JETPACK_OFF_EYE_Y
    HEAD_OUT_Y = _R.JETPACK_ON_EYE_Y
    O2_CAPACITY_DEFAULT = _R.OXYGEN_CAPACITY
    STEP_TICKS_DEFAULT = int(_R.SN_STEP_TICKS)
except Exception:  # pragma: no cover - the defaults above are the same numbers
    _R = None

# =============================================================================================== small helpers
def _first(d, names):
    if not isinstance(d, dict):
        return None
    for k in names:
        if k in d and d[k] is not None:
            return d[k]
    return None

def _f(v):
    if isinstance(v, bool) or v is None:
        return None
    if isinstance(v, (int, float)):
        return float(v) if math.isfinite(float(v)) else None
    if isinstance(v, str) and not _HEX.match(v):
        try:
            x = float(v)
            return x if math.isfinite(x) else None
        except ValueError:
            return None
    return None

def _bits(v):
    """A Wire SwimTrace double (16 lower-case hex digits of the IEEE-754 bits) -> float, else None."""
    if isinstance(v, str) and _HEX.match(v):
        return struct.unpack("<d", struct.pack("<Q", int(v, 16)))[0]
    return None

def _bits_of(x):
    return "%016x" % struct.unpack("<Q", struct.pack("<d", float(x)))[0]

def _flag(v):
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return bool(v)
    if isinstance(v, str):
        low = v.strip().lower()
        if low in ("true", "1", "yes", "on"):
            return True
        if low in ("false", "0", "no", "off"):
            return False
    return None

def _vec3(d, vec_names=(), comp_names=None):
    v = _first(d, vec_names) if vec_names else None
    if isinstance(v, (list, tuple)) and len(v) >= 3:
        c = [_f(x) for x in v[:3]]
        if all(x is not None for x in c):
            return tuple(c)
    if isinstance(v, dict):
        got = _vec3(v, (), (("x",), ("y",), ("z",)))
        if got:
            return got
    if comp_names:
        c = [_f(_first(d, names)) for names in comp_names]
        if all(x is not None for x in c):
            return tuple(c)
    return None

def _bvec(d, a, b, c):
    v = (_bits(d.get(a)), _bits(d.get(b)), _bits(d.get(c)))
    return v if all(x is not None for x in v) else None

def _norm(v):
    return math.sqrt(sum(x * x for x in v))

def _sub(a, b):
    return tuple(x - y for x, y in zip(a, b))

def _add(a, b):
    return tuple(x + y for x, y in zip(a, b))

def _mul(a, k):
    return tuple(x * k for x in a)

def _dot(a, b):
    return sum(x * y for x, y in zip(a, b))

def _unit(v):
    n = _norm(v)
    return tuple(x / n for x in v) if n > 1e-12 else (0.0, 0.0, 0.0)

def _fmt(v, unit="", nd=3):
    if v is None:
        return "-"
    return ("%." + str(nd) + "f%s") % (v, unit)

def _right_of(look):
    f = look or (0.0, 0.0, 1.0)
    h = math.sqrt(f[0] * f[0] + f[2] * f[2])
    return (f[2] / h, 0.0, -f[0] / h) if h > 1e-9 else (1.0, 0.0, 0.0)

# =============================================================================================== the model port
class ModelPort:
    """The analyser's view of the independent model (module docstring); subclasses wrap a module."""
    name = "?"
    step_ticks = STEP_TICKS_DEFAULT
    module = None

    def begin(self):
        pass

    def step(self, v, tick):
        raise NotImplementedError

    def o2(self, depth_m):
        return None

    def o2_refill(self):
        return None

    def surface_head_max(self):
        return None

class SelftestPlant(ModelPort):
    """The analyser's own reading of Subnautica's underwater motor and oxygen rules, for the selftest's stand-in traces
 (and `--model plant` diagnostics). Written from the numbers (never the code) of the decompiled game; when
 tests/s2b/sn_reference.py (the S2b number table) is importable its values are used, so the stand-in traces look
 like the real model's (the scene's 7.6/7.6/7.6/6.3 m/s instead of the C# initializers 6.64/6.64/6.64/6.6).
 Subnautica runs the swim motor in FixedUpdate at Time.fixedDeltaTime = 0.1 s for a free diver
 i.e. one motor step every 6 SE ticks; the
 velocity holds in between. A step happens when the tick carries "motorStep" true, else on ticks with tickNo % 6 == 0;
 without either every call is one step of "dt". One step = UnderwaterMotor.UpdateMove, then Unity's rigidbody drag
 v *= max(0, 1 - drag * dt).
 forward / backward / strafe max 7.6 (scene) | 6.64 initializer (SetMotorMode
 -> UnderwaterMotor)
 vertical max 6.3 (scene) | 6.6 initializer;
 takes max(axis maxima, vertical) for any input
 water acceleration 20 m/s^2 x input magnitude;
 swim drag 2.5 1/s via
 underwater gravity 0
 speed cap max(|v|, max speed)
 above the water level max speed x 1.3
 surface rule (eye >= -0.5 m, not pressing down, not swimming down 0.3): vy x clamp01((0.52 - y) / 1.02)^0.3

 sprint none (IsSprinting false)
 O2 capacity 45 units (s) the player's Oxygen component

 breath 3 s x 1 (depth <= 100 m), 2.25 s x 1.5 (100-200 m), 1.5 s x 2 (> 200 m)

 surface refill 30 units/s, eye above -1 m
 pressure on the diver none: Player.CrushDamageUpdate is never invoked
 """
    name = "analyser selftest plant"
    FWD, BACK, STRAFE, VERT, ACCEL, DRAG = 6.64, 6.64, 6.64, 6.6, 20.0, 2.5
    SURF_TOP, SURF_LOW, SURF_EXP, SURF_MULT, LOOK_DOWN = 0.52, -0.5, 0.3, 1.3, -0.3
    O2_CAP, O2_REFILL = 45.0, 30.0
    FIXED_DT, STEP_TICKS = 0.1, 6
    SOURCE = "decomp initializers"

    def __init__(self, scale=1.0):
        self.scale = scale
        if _R is not None:
            self.FWD, self.BACK, self.STRAFE, self.VERT = (_R.DIVE_FORWARD_MAX, _R.DIVE_BACKWARD_MAX,
                                                           _R.DIVE_STRAFE_MAX, _R.DIVE_VERTICAL_MAX)
            self.ACCEL, self.DRAG = _R.WATER_ACCELERATION, _R.SWIM_DRAG
            self.SURF_TOP, self.SURF_LOW, self.SURF_EXP = _R.SURFACE_TOP_Y, _R.SURFACE_BAND_BOTTOM_Y, _R.SURFACE_EXPONENT
            self.SURF_MULT, self.LOOK_DOWN = _R.SURFACE_SPEED_MULTIPLIER, _R.LOOK_DOWN_Y
            self.O2_CAP, self.O2_REFILL = _R.OXYGEN_CAPACITY, _R.OXYGEN_REFILL_PER_S
            self.FIXED_DT, self.STEP_TICKS = _R.SN_FIXED_DT, int(_R.SN_STEP_TICKS)
            self.SOURCE = "tests/s2b/sn_reference.py"
        self.step_ticks = self.STEP_TICKS
        self.name = "%s (%s)%s" % (self.name, self.SOURCE, " x%g" % scale if scale != 1.0 else "")

    def step(self, v, tick):
        if not tick.get("inWater", True):
            return tuple(v)
        n, hint = tick.get("tickNo"), tick.get("motorStep")
        if hint is not None or n is not None:
            if not (hint if hint is not None else int(n) % self.STEP_TICKS == 0):
                return tuple(v)                # between two motor steps the velocity holds
            dt = self.FIXED_DT
        else:
            dt = tick.get("dt") or TICK_S
        mx, my, mz = tick.get("move") or (0.0, 0.0, 0.0)
        mag = min(1.0, math.sqrt(mx * mx + my * my + mz * mz))
        a = 0.0
        if mz > 0:
            a = self.FWD
        elif mz < 0:
            a = self.BACK
        if mx != 0:
            a = max(a, self.STRAFE)
        a = max(a, self.VERT) * self.scale
        y_eye = tick.get("yHead")
        y_eye = -100.0 if y_eye is None else y_eye
        ratio = 1.0
        if y_eye > 0.0:
            ratio = self.SURF_MULT
            a *= ratio
        cap = max(_norm(v), a)
        f = _unit(tick.get("look") or (0.0, 0.0, 1.0))
        right = _right_of(f)
        h = _unit((mx, 0.0, mz))
        world_h = _add(_mul(right, h[0]), _mul(f, h[2]))
        d = _unit(_add(world_h, (0.0, my, 0.0)))
        acc = mag * self.ACCEL * self.scale * dt * (ratio if ratio > 1.0 else 1.0)
        out = tuple(v)
        if acc > 0:
            v2 = _add(v, _mul(d, acc))
            nn = _norm(v2)
            if nn > cap:
                v2 = _mul(v2, cap / nn)
            if y_eye >= self.SURF_LOW and my >= 0 and world_h[1] >= self.LOOK_DOWN:
                k = min(1.0, max(0.0, (self.SURF_TOP - y_eye) / (self.SURF_TOP - self.SURF_LOW))) ** self.SURF_EXP
                v2 = (v2[0], v2[1] * k, v2[2])
            out = v2
        return _mul(out, max(0.0, 1.0 - self.DRAG * dt))

    def o2(self, depth_m):
        if depth_m > 200.0:
            period, mult = 1.5, 2.0
        elif depth_m > 100.0:
            period, mult = 2.25, 1.5
        else:
            period, mult = 3.0, 1.0
        units = period * mult
        return {"rate": units / period, "capacity": self.O2_CAP, "breathS": period, "breathUnits": units}

    def o2_refill(self):
        return self.O2_REFILL

    def surface_head_max(self):
        return self.SURF_TOP

class SwimReplayPort(ModelPort):
    """tests/s2b/swim_replay.py's own model (State + step, numbers of sn_reference.py), open loop: each tick gets the
 logged inputs and the model's own previous velocity; A windows are replayed by its openloop_series."""

    def __init__(self, mod):
        self.module = self.M = mod
        self.R = mod.R
        self.name = "swim_replay.py (State/step; %s)" % os.path.basename(getattr(self.R, "__file__", "sn_reference"))
        self.step_ticks = int(self.R.SN_STEP_TICKS)
        self.s = None

    def begin(self):
        self.s = None

    def step(self, v, tick):
        M, R = self.M, self.R
        n = int(tick.get("tickNo") or 0)
        eye = tick.get("yHead")
        eye = -100.0 if eye is None else eye
        if self.s is None:
            s = M.State()
            s.lat, s.ctl, s.md, s.h = True, True, M.SWIM, tuple(v)
            if hasattr(s, "eu"):
                s.eu = eye < R.UNDERWATER_EYE_Y    # a fresh start is no Dive<->Run motor switch
            k = tick.get("phase")
            s.ph = (n - k) % self.step_ticks if k is not None else 0
            self.s = s
        mx, my, mz = tick.get("move") or (0.0, 0.0, 0.0)
        f = tuple(tick.get("look") or (0.0, 0.0, 1.0))
        r = tuple(tick.get("right") or _right_of(f))
        pos = tick.get("pos")
        p = (pos[0], eye, pos[2]) if pos else (0.0, eye, 0.0)
        i = {"tick": n, "mx": float(mx), "my": float(my), "mz": float(mz), "f": f, "r": r, "v": tuple(v), "p": p,
             "o2": R.OXYGEN_CAPACITY, "spr": int(bool(tick.get("sprint"))), "tool": 0,
             "gnd": int(bool(tick.get("grounded"))), "jet": 0, "mut": 0, "fin": float(tick.get("fins") or 0.0)}
        return tuple(M.step(self.s, i)["o_v"])

    def o2(self, depth_m):
        R = self.R
        dc = R.DC_CRUSH if depth_m > R.CRUSH_DEPTH_CLASS else (R.DC_UNSAFE if depth_m > R.UNSAFE_DEPTH else R.DC_SAFE)
        period = R.BREATH_PERIOD_S[dc]
        return {"rate": R.oxygen_rate(dc), "capacity": R.OXYGEN_CAPACITY, "breathS": period,
                "breathUnits": period * R.BREATH_FACTOR[dc]}

    def o2_refill(self):
        return self.R.OXYGEN_REFILL_PER_S

    def surface_head_max(self):
        return self.R.SURFACE_TOP_Y

_ALIAS = {
    "v": ("v", "vel", "velocity", "v0", "vprev", "prevvel", "prevvelocity", "currentvelocity", "rbvelocity"),
    "move": ("move", "movedir", "moveinput", "input", "inp", "movementinputdirection", "inputdir", "wish",
             "movedirection", "mv"),
    "sprint": ("sprint", "sprinting", "run", "running", "spr"),
    "look": ("look", "lookdir", "forward", "fwd", "camfwd", "camforward", "camera", "forwardref", "cam", "lookforward"),
    "dt": ("dt", "deltatime", "delta", "dts", "timestep"),
    "yHead": ("y", "yhead", "heady", "eyey", "posy", "positiony", "py", "head", "eye", "ypos", "depthy"),
    "inWater": ("inwater", "underwater", "uw", "swimming", "water", "inw"),
    "tick": ("tick", "row", "r", "rec", "record", "inputs", "frame"),
    "tickNo": ("tickno", "ticknumber", "setick", "tickindex"),
}

class _SignatureCaller:
    """Calls a model function whose parameters are named like ours (the alias table above)."""

    def __init__(self, fn):
        self.fn = fn
        sig = inspect.signature(fn)
        self.plan = []
        for p in sig.parameters.values():
            if p.kind in (p.VAR_POSITIONAL, p.VAR_KEYWORD):
                continue
            key = p.name.lower().replace("_", "")
            role = next((r for r, names in _ALIAS.items() if key in names), None)
            if role is None and p.default is p.empty:
                raise TypeError("parameter %r of %s has no known meaning" % (p.name, getattr(fn, "__name__", fn)))
            if role is not None:
                self.plan.append((p.name, role, p.kind))
        if not any(r == "v" for _n, r, _k in self.plan) and not any(r == "tick" for _n, r, _k in self.plan):
            raise TypeError("%s takes no velocity" % getattr(fn, "__name__", fn))

    def __call__(self, v, tick):
        vals = {"v": tuple(v), "move": tuple(tick.get("move") or (0.0, 0.0, 0.0)), "sprint": bool(tick.get("sprint")),
                "look": tuple(tick.get("look") or (0.0, 0.0, 1.0)), "dt": tick.get("dt") or TICK_S,
                "yHead": tick.get("yHead") if tick.get("yHead") is not None else -100.0,
                "inWater": bool(tick.get("inWater", True)), "tick": dict(tick, v=tuple(v)),
                "tickNo": tick.get("tickNo")}
        args, kw = [], {}
        for name, role, kind in self.plan:
            if kind == inspect.Parameter.POSITIONAL_ONLY:
                args.append(vals[role])
            else:
                kw[name] = vals[role]
        return _as_velocity(self.fn(*args, **kw))

def _as_velocity(r):
    if isinstance(r, (list, tuple)):
        if len(r) >= 3 and all(_f(x) is not None for x in r[:3]):
            return tuple(float(x) for x in r[:3])
        if r and isinstance(r[0], (list, tuple, dict)):
            return _as_velocity(r[0])
    if isinstance(r, dict):
        got = _vec3(r, ("vel", "v", "velocity", "o_v"), (("vx",), ("vy",), ("vz",)))
        if got:
            return got
    for names in (("vx", "vy", "vz"), ("x", "y", "z")):
        if all(hasattr(r, n) for n in names):
            return tuple(float(getattr(r, n)) for n in names)
    for n in ("vel", "v", "velocity"):
        if hasattr(r, n):
            return _as_velocity(getattr(r, n))
    raise TypeError("model step returned %r (no velocity)" % (type(r).__name__,))

class ModulePort(ModelPort):
    """A model module without analyzer_port and without swim_replay's State/step: its step function (by parameter
 names) and its O2 constants."""

    def __init__(self, mod):
        self.module = mod
        self.name = "%s (step by parameter names)" % os.path.basename(getattr(mod, "__file__", "?"))
        self.caller, why = None, []
        cands = []
        for n in ("step", "step_velocity", "swim_step", "update_move", "model_step", "advance"):
            fn = getattr(mod, n, None)
            if callable(fn) and not inspect.isclass(fn):
                cands.append(fn)
        for cn in ("SwimModel", "Model", "SwimMotor", "UnderwaterMotor", "Motor", "Swim"):
            cls = getattr(mod, cn, None)
            if inspect.isclass(cls):
                try:
                    inst = cls()
                except Exception as e:      # needs arguments
                    why.append("%s(): %s" % (cn, e))
                    continue
                for n in ("step", "update_move", "UpdateMove", "update", "advance"):
                    fn = getattr(inst, n, None)
                    if callable(fn):
                        cands.append(fn)
        for fn in cands:
            try:
                self.caller = _SignatureCaller(fn)
                break
            except (TypeError, ValueError) as e:
                why.append(str(e))
        if self.caller is None:
            raise TypeError("no step function the analyser can call (%s)" % ("; ".join(why) or "none found"))
        st = self._const("SN_STEP_TICKS", "STEP_TICKS")
        self.step_ticks = int(st) if st else STEP_TICKS_DEFAULT

    def step(self, v, tick):
        return self.caller(v, tick)

    def _const(self, *names):
        for n in names:
            v = _f(getattr(self.module, n, None))
            if v is not None:
                return v
        return None

    def o2(self, depth_m):
        for n in ("o2", "o2_rate", "oxygen_rate", "o2_per_second", "o2_model"):
            fn = getattr(self.module, n, None)
            if callable(fn):
                try:
                    r = fn(depth_m)
                except TypeError:
                    continue
                if isinstance(r, dict):
                    return r
                if _f(r) is not None:
                    return {"rate": float(r), "capacity": self._const("O2_CAPACITY", "OXYGEN_CAPACITY", "O2_CAPACITY_S",
                                                                      "OXYGEN_CAPACITY_S", "CAPACITY"),
                            "breathS": 0.0, "breathUnits": 0.0}
        rate = self._const("O2_RATE", "O2_RATE_SAFE", "OXYGEN_RATE", "O2_PER_SECOND")
        cap = self._const("O2_CAPACITY", "OXYGEN_CAPACITY", "O2_CAPACITY_S", "OXYGEN_CAPACITY_S", "CAPACITY")
        if rate is None or cap is None:
            return None
        return {"rate": rate, "capacity": cap, "breathS": self._const("BREATH_PERIOD_S", "BREATH_S") or 0.0,
                "breathUnits": self._const("BREATH_UNITS") or 0.0}

    def o2_refill(self):
        return self._const("OXYGEN_UNITS_PER_SECOND_SURFACE", "O2_REFILL", "O2_SURFACE_REFILL", "SURFACE_REFILL")

    def surface_head_max(self):
        return self._const("SURFACE_HEAD_MAX", "SURFACE_TOP", "SURFACE_MAX_Y", "HEAD_MAX_Y")

class _WrappedPort(ModelPort):
    """analyzer_port's object, with the optional methods defaulted."""

    def __init__(self, obj):
        self.obj = obj
        self.name = str(getattr(obj, "name", None) or "analyzer_port()")
        self.step_ticks = int(getattr(obj, "step_ticks", None) or STEP_TICKS_DEFAULT)

    def begin(self):
        fn = getattr(self.obj, "begin", None)
        if callable(fn):
            fn()

    def step(self, v, tick):
        return _as_velocity(self.obj.step(tuple(v), tick))

    def o2(self, depth_m):
        fn = getattr(self.obj, "o2", None)
        return fn(depth_m) if callable(fn) else None

    def o2_refill(self):
        fn = getattr(self.obj, "o2_refill", None)
        return fn() if callable(fn) else None

    def surface_head_max(self):
        fn = getattr(self.obj, "surface_head_max", None)
        return fn() if callable(fn) else None

def adapt(mod) -> ModelPort:
    fn = getattr(mod, "analyzer_port", None)
    if callable(fn):
        obj = fn()
        if not callable(getattr(obj, "step", None)):
            raise TypeError("analyzer_port() returned an object without step()")
        port = _WrappedPort(obj)
        port.module = mod
        return port
    if inspect.isclass(getattr(mod, "State", None)) and callable(getattr(mod, "step", None)) and hasattr(mod, "R"):
        return SwimReplayPort(mod)
    return ModulePort(mod)

_MODEL = {"port": None, "status": "not loaded", "mode": "swim_replay"}

def set_model(mode="swim_replay", port=None):
    """Selects the model: "swim_replay" (default, imported), "plant" (this file's), or an explicit port (tests)."""
    _MODEL.update(port=port, mode=mode, status="explicit: %s" % port.name if port is not None else "not loaded")

def load_model():
    if _MODEL["port"] is not None or _MODEL["status"] != "not loaded":
        return _MODEL["port"]
    if _MODEL["mode"] == "plant":
        _MODEL.update(port=SelftestPlant(), status="--model plant: the analyser's own plant (not independent of this "
                                                    "file; diagnostics only)")
        return _MODEL["port"]
    try:
        mod = importlib.import_module("swim_replay")
    except Exception as e:
        _MODEL["status"] = "swim_replay.py not importable (%s: %s)" % (type(e).__name__, str(e)[:120])
        return None
    try:
        _MODEL["port"] = adapt(mod)
        _MODEL["status"] = "ok: %s" % _MODEL["port"].name
    except Exception as e:
        _MODEL["status"] = "swim_replay.py not usable: %s (add analyzer_port(), analyze_swim.py docstring)" % (
            str(e)[:160],)
    return _MODEL["port"]

def model_status() -> str:
    load_model()
    return _MODEL["status"]

# =============================================================================================== parsing
def _layout(data):
    if "o_vx" in data or "s_lat" in data:
        return "A"
    if "vSet" in data or "yIn" in data or "vSe" in data:
        return "B"
    return "G"

def _row_a(data, tick):
    move = _bvec(data, "mx", "my", "mz")
    look = _bvec(data, "fx", "fy", "fz")
    vin = _bvec(data, "vx", "vy", "vz")
    vout = _bvec(data, "o_vx", "o_vy", "o_vz")
    pos = _bvec(data, "px", "py", "pz")
    md = int(data.get("o_md") or 0)
    ox, do2 = _bits(data.get("ox")), _bits(data.get("o_do2"))
    o2 = ((ox + (do2 or 0.0)) / O2_CAPACITY_DEFAULT) if ox is not None else None
    if "o2u" in data and _f(data.get("o2")) is not None:
        o2 = _f(data.get("o2"))             # the readable suit fill (0..1) next to the units (swim_replay.py rows)
    evt = int(data.get("o_evt") or 0)
    tr = "enter" if evt & 1 else ("exit" if evt & 2 else None)
    seg = data.get("seg")
    # the SE plugin appends its own keys to the Wire row (SwimRowExtras, CONTRACT §2.10 "SE controller"): vSe = SE's
 # velocity right after its physics step (the measured motion), posAfter = the eye after it, damp, src, step, exit,
 # helmet. A Wire row without them (swim_replay.py gen) keeps the defaults.
    step = data.get("step")
    after = _vec3(data, ("posAfter",))
    return {"tick": tick, "n": data.get("n"), "fmt": "A", "dt": _f(data.get("dt")) or TICK_S,
            "seg": seg if isinstance(seg, str) else None,
            "step": int(step) if isinstance(step, (int, float)) and step >= 0 else None,
            "src": data.get("src") if isinstance(data.get("src"), str) else None,
            "move": move, "sprint": bool(data.get("spr")), "look": look, "right": _bvec(data, "rx", "ry", "rz"),
            "vel": vout, "velIn": vin, "velMeas": _vec3(data, ("vSe",)), "pos": pos, "y": pos[1] if pos else None,
            "yHead": pos[1] if pos else None, "yCtl": pos[1] if pos else None, "yChest": _f(data.get("chestY")),
            "yHeadAfter": after[1] if after else None,
            "state": {0: "walk", 1: "swim", 2: "surface", 3: "fly"}.get(md, str(md)), "inWater": md != 0,
            "ctl": bool(data.get("o_ctl")), "jet": _flag(data.get("jet")), "jetOk": _flag(data.get("o_ja")),
            "damp": _flag(data.get("damp")) if "damp" in data else None, "o2": o2,
            "stepped": _flag(data.get("o_st")), "tr": tr, "exitReason": data.get("exit") or None,
            "grounded": _flag(data.get("gnd")), "roll": None,
            "helmet": _flag(data.get("helmet")) if "helmet" in data else None}

def _row_b(data, tick):
    inl = data.get("in")
    move, sprint = None, False
    if isinstance(inl, (list, tuple)) and len(inl) >= 3:
        f, s, v = (_f(x) for x in inl[:3])
        if None not in (f, s, v):
            move = (s, v, f)                  # [forward, strafe, vertical] -> (strafe, vertical, forward)
        sprint = bool(_f(inl[3])) if len(inl) > 3 and _f(inl[3]) is not None else False
    pos = _vec3(data, ("pos",))
    st = data.get("state")
    trv = _f(data.get("tr"))
    o2 = _f(data.get("o2"))
    step = data.get("step")
    return {"tick": tick, "n": None, "fmt": "B", "dt": _f(data.get("dt")) or TICK_S, "seg": None,
            "step": int(step) if isinstance(step, (int, float)) and step >= 0 else None, "src": data.get("src"),
            "move": move, "sprint": sprint, "look": _vec3(data, ("look",)), "right": _vec3(data, ("right",)),
            "vel": _vec3(data, ("vSet",)), "velIn": _vec3(data, ("vIn",)), "velMeas": _vec3(data, ("vSe",)),
            "pos": pos, "y": _f(data.get("yIn")), "yHead": _f(data.get("yIn")), "yCtl": _f(data.get("yIn")),
            "yHeadAfter": pos[1] if pos else None, "yChest": _f(data.get("chestY")), "state": st,
            "inWater": (st == "swim") if isinstance(st, str) else None, "ctl": (st == "swim") if isinstance(st, str)
            else None, "jet": _flag(data.get("jet")), "jetOk": None, "damp": _flag(data.get("damp")),
            "o2": o2 / 100.0 if (o2 is not None and o2 > 1.0 + 1e-9) else o2, "stepped": _flag(data.get("stepped")),
            "tr": "enter" if trv and trv > 0 else ("exit" if trv and trv < 0 else None),
            "exitReason": data.get("exit") or None, "grounded": _flag(data.get("grounded")), "roll": None,
            "helmet": _flag(data.get("helmet"))}

def _row_g(data, tick):
    move = _vec3(data, ("move", "mv"), (("mx", "ms", "strafe", "inStrafe"), ("my", "mu", "vertical", "inUp"),
                                        ("mz", "mf", "forward", "inFwd")))
    if move is None:
        d = _first(data, ("input", "inputs", "in"))
        if isinstance(d, dict):
            move = _vec3(d, (), (("x", "strafe", "s"), ("y", "vertical", "up", "u"), ("z", "forward", "fwd", "f")))
    pos = _vec3(data, ("pos", "p", "position"), (("x",), ("y",), ("z",)))
    y = _f(data.get("y")) if not isinstance(data.get("y"), (list, dict)) else None
    if y is None and pos:
        y = pos[1]
    st = _first(data, ("st", "state", "mode"))
    inw = _flag(_first(data, ("inW", "inWater", "swimming", "underwater", "uw", "water")))
    if inw is None and isinstance(st, str):
        inw = st.lower() in ("swim", "swimming", "water", "underwater", "inwater")
    o2 = _f(_first(data, ("o2", "o2Frac", "oxygen", "suitO2", "o2Level")))
    if o2 is not None and o2 > 1.0 + 1e-9:
        o2 /= 100.0
    seg = _first(data, ("seg", "segment", "lbl", "label"))
    tr = _first(data, ("tr", "transition"))
    yh = _f(_first(data, ("yHead", "headY", "head", "eyeY", "camY", "yh")))
    return {"tick": tick, "n": None, "fmt": "G", "dt": _f(_first(data, ("dt", "dtS"))) or TICK_S,
            "seg": seg if isinstance(seg, str) else None, "step": None, "src": None, "move": move,
            "sprint": bool(_flag(_first(data, ("sprint", "spr", "run")))),
            "look": _vec3(data, ("look", "lookDir", "camFwd"), (("lx",), ("ly",), ("lz",))), "right": None,
            "vel": _vec3(data, ("vel", "v", "velocity"), (("vx",), ("vy",), ("vz",))), "velIn": None, "velMeas": None,
            "pos": pos, "y": y, "yHead": yh if yh is not None else y, "yCtl": None,
            "yChest": _f(_first(data, ("yChest", "chestY", "chest", "yc"))), "state": st, "inWater": inw, "ctl": inw,
            "jet": _flag(_first(data, ("jet", "jetpack", "jetOn", "jetpackOn"))),
            "jetOk": _flag(_first(data, ("jetOk", "jetpackAllowed", "jetAllowed", "jetpackOk"))),
            "damp": _flag(_first(data, ("damp", "dampeners", "dampOn", "dampenersOn"))), "o2": o2,
            "stepped": _flag(data.get("stepped")),
            "tr": (_tr_kind(tr) if isinstance(tr, str) else None), "exitReason": None, "grounded": None,
            "roll": _f(_first(data, ("roll", "rollDeg")))}

def swim_row(rec):
    """One raw se-*.jsonl record -> a normalised swim row dict (layout A, B or G), or None."""
    name = rec.get("ev") or rec.get("event")
    data = None
    if name in _SWIM_EVENTS:
        data = rec["data"] if isinstance(rec.get("data"), dict) else rec
    else:
        text = rec.get("msg") if isinstance(rec.get("msg"), str) else rec.get("text")
        if isinstance(text, str):
            m = re.match(r"^(?:DE\.Swim row |s2b\.swim |swim )(\{.*\})\s*$", text)
            if m:
                try:
                    data = json.loads(m.group(1))
                except ValueError:
                    data = None
    if not isinstance(data, dict):
        return None
    tick = _f(_first(data, ("tick", "seTick", "gameplayTick")))
    if tick is None:
        return None
    lay = _layout(data)
    r = (_row_a if lay == "A" else _row_b if lay == "B" else _row_g)(data, int(tick))
    r.update(t=rec.get("t") or rec.get("utc"), qpc=rec.get("qpc") if isinstance(rec.get("qpc"), int) else None,
             raw=data)
    return r

def head_of(r):
    """The eye height of a row: yHead, else y (reference point unknown)."""
    return r["yHead"] if r.get("yHead") is not None else r.get("y")

def measured(r):
    """The measured velocity of a row's tick (B vSe, A the next row's input velocity), else the imposed one."""
    return r.get("velMeas") or r.get("vel")

def _tr_kind(word):
    w = str(word or "").lower()
    if w in _ENTER_WORDS:
        return "enter"
    if w in _EXIT_WORDS:
        return "exit"
    return None

def text_transitions(raw):
    """`DE.Swim enter|exit tick=N...` lines and swim.enter/exit events: [{tick, kind, yHead,...}] in log order."""
    out = []
    for rec in raw:
        name = rec.get("ev") or rec.get("event")
        if name in _TR_EVENTS:
            d = rec["data"] if isinstance(rec.get("data"), dict) else rec
            kind = _TR_EVENTS[name] or _tr_kind(_first(d, ("kind", "to", "tr", "transition")))
            t = _f(_first(d, ("tick", "seTick")))
            if kind:
                out.append({"tick": int(t) if t is not None else None, "kind": kind,
                            "yHead": _f(_first(d, ("yHead", "y"))), "src": name, "t": rec.get("t")})
            continue
        text = rec.get("msg") if isinstance(rec.get("msg"), str) else rec.get("text")
        if not isinstance(text, str):
            continue
        m = _TEXT_TR.match(text)
        if not m:
            continue
        body = m.group(2)
        kv = C._kv(body)
        t = _f(kv.get("tick"))
        rest = {"jetpackWas": kv.get("jetpackWas"), "dampenersWas": kv.get("dampenersWas"),
                "reason": kv.get("reason")}
        rm = re.search(r"jetpack->(\w+) dampeners->(\w+)", body)
        if rm:
            rest.update(jetpackTo=rm.group(1), dampenersTo=rm.group(2))
        out.append(dict({"tick": int(t) if t is not None else None, "kind": m.group(1),
                         "yHead": _f(kv.get("y")), "src": "text", "t": rec.get("t"), "text": text}, **rest))
    return out

def derived_transitions(rows):
    """Transitions from inW flips between contiguous rows (no explicit transition logged)."""
    out = []
    prev = None
    for r in rows:
        if r.get("inWater") is None:
            prev = None
            continue
        if prev is not None and r["tick"] - prev["tick"] <= 2 and r["inWater"] != prev["inWater"]:
            out.append({"tick": r["tick"], "kind": "enter" if r["inWater"] else "exit", "yHead": head_of(r),
                        "src": "derived"})
        prev = r
    return out

# =============================================================================================== the session
class Session:
    """A collected s2b folder: the launcher's s2b-session.jsonl and the SE plugin's se-*.jsonl."""

    def __init__(self, folder):
        self.dir = folder
        self.session = C.read_jsonl(os.path.join(folder, "s2b-session.jsonl"))
        self.se_files = sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        self.raw = C.read_jsonl(self.se_files)
        start = next((r for r in self.session if r.get("ev") == "start"), {})
        self.start = start
        self.parts = start.get("parts") or []
        self.qpcf = int(start.get("qpcFrequency") or C.qpc_frequency(self.session, default=10_000_000))
        d = []
        for r in self.session:
            u = C.Session._utc_seconds(r.get("utc"))
            if u is not None and isinstance(r.get("qpc"), int):
                d.append(r["qpc"] - u * self.qpcf)
        self.utc_offset = sorted(d)[len(d) // 2] if d else None
        rows = {}
        for rec in self.raw:
            r = swim_row(rec)
            if r is None:
                continue
            if r["qpc"] is None and self.utc_offset is not None:
                s = C.Session._utc_seconds(r.get("t"))
                r["qpc"] = int(s * self.qpcf + self.utc_offset) if s is not None else None
            rows[r["tick"]] = r              # one row per tick (the last one wins)
        self.rows = [rows[k] for k in sorted(rows)]
        self.by_tick = rows
        # A rows: the measured motion of tick k is SE's post-step velocity when the plugin logged it (vSe), else the
 # input velocity of the next contiguous row (n + 1)
        by_n = {r["n"]: r for r in self.rows if r["fmt"] == "A" and isinstance(r.get("n"), int)}
        for r in self.rows:
            if r["fmt"] == "A" and r.get("velMeas") is None and isinstance(r.get("n"), int) and (r["n"] + 1) in by_n:
                r["velMeas"] = by_n[r["n"] + 1].get("velIn")
        self.layouts = Counter(r["fmt"] for r in self.rows)
        self.lines = C.text_lines(self.raw)
        self.text_tr = text_transitions(self.raw)
        merged = {}
        for t in [dict(tick=r["tick"], kind=r["tr"], yHead=head_of(r), src="row") for r in self.rows if r.get("tr")] + \
                [t for t in self.text_tr if t["tick"] is not None]:
            key = (t["tick"], t["kind"])
            merged[key] = dict(merged.get(key, {}), **{k: v for k, v in t.items() if v is not None})
        if merged:
            self.transitions = sorted(merged.values(), key=lambda t: t["tick"])
            self.transitions_src = "logged"
        else:
            self.transitions = derived_transitions(self.rows)
            self.transitions_src = "derived from inW"

    def ev(self, name):
        return [r for r in self.session if r.get("ev") == name]

    def part(self, name):
        b = next((r for r in self.session if r.get("ev") == "part" and r.get("name") == name and
                  r.get("phase") == "begin"), None)
        e = next((r for r in self.session if r.get("ev") == "part" and r.get("name") == name and
                  r.get("phase") == "end"), None)
        return b, e

    def has_part(self, name):
        return self.part(name)[0] is not None

    def tick_window(self, name):
        b, e = self.part(name)
        if b is None:
            return None
        t0, t1 = _f((b or {}).get("tick")), _f((e or {}).get("tick"))
        if t0 is not None and t0 > 0:
            return int(t0), int(t1) if t1 is not None else 1 << 62
        return None

    def qpc_window(self, name):
        b, e = self.part(name)
        if b is None or not isinstance(b.get("qpc"), int):
            return None
        return b["qpc"], e["qpc"] if e and isinstance(e.get("qpc"), int) else 1 << 62

    def rows_in(self, name):
        """Swim rows inside a launcher part (tick window, else UTC->QPC window)."""
        w = self.tick_window(name)
        if w is not None:
            got = [r for r in self.rows if w[0] <= r["tick"] <= w[1]]
            if got:
                return got
        q = self.qpc_window(name)
        if q is not None:
            return [r for r in self.rows if r.get("qpc") is not None and q[0] <= r["qpc"] <= q[1]]
        return []

    def span_of(self, name):
        """(first tick, last tick) of a part: its rows, else its C1 ticks."""
        rows = self.rows_in(name)
        if rows:
            return rows[0]["tick"], rows[-1]["tick"]
        return self.tick_window(name)

    def transitions_in(self, span):
        if span is None:
            return []
        return [t for t in self.transitions if span[0] <= t["tick"] <= span[1]]

    def replies(self, label_prefix=None, cmd="de.swim"):
        out = []
        for r in self.session:
            if r.get("ev") != "reply" or r.get("cmd") != cmd:
                continue
            lab = str(r.get("label") or "")
            if label_prefix is None or lab.startswith(label_prefix):
                out.append(r)
        return out

    def dumps(self, label_prefix):
        """de.swim dump / o2=dump replies of a part, normalised: {label, qpc, inWater, jetpack, jetpackAllowed,
 dampeners, yHead, o2, health} (keys the SE dump does not carry stay None)."""
        out = []
        for r in self.replies(label_prefix):
            args, rep = r.get("args") or {}, r.get("reply")
            if not isinstance(rep, dict) or not (args.get("dump") or "o2" in args):
                continue
            src = rep
            st = rep.get("state")
            o2 = _f(_first(src, ("o2", "o2Frac", "oxygen", "suitO2", "o2Level")))
            if o2 is None and isinstance(src.get("o2"), dict):
                o2 = _f(_first(src["o2"], ("level", "fraction", "o2")))
            if o2 is not None and o2 > 1.0 + 1e-9:
                o2 /= 100.0
            pos = _vec3(src, ("eyeSn", "pos", "position"))
            yh = _f(_first(src, ("eyeY", "yHead", "headY", "head", "camY")))
            if yh is None and pos:
                yh = pos[1]
            pol = src.get("policy") if isinstance(src.get("policy"), dict) else {}
            tk = _f(src.get("tick"))
            out.append({"label": r.get("label"), "qpc": r.get("qpc"), "ok": bool(rep.get("ok")),
                        "tick": int(tk) if tk is not None else None,
                        "inWater": (st == "swim") if isinstance(st, str) else _flag(_first(src, ("inWater", "inW"))),
                        "jetpack": _flag(_first(src, ("jetpack", "jet", "jetOn", "jetpackOn"))),
                        "jetpackAllowed": _flag(_first(src, ("jetpackAllowed", "jetOk", "jetAllowed"))),
                        "dampeners": _flag(_first(src, ("dampeners", "damp", "dampenersOn"))),
                        "yHead": yh, "o2": o2, "health": _f(_first(src, ("health", "hp"))),
                        "policy": pol})
        return out

    def o2_samples(self, name):
        """(seconds from the part start, O2 fraction) samples of a part and their source: per-tick rows when they carry
 O2, else the launcher's `o2` events (o2=dump replies every 5 s)."""
        rows = [r for r in self.rows_in(name) if r.get("o2") is not None]
        if len(rows) >= O2_ROWS_MIN:
            t0 = rows[0]["tick"]
            return [((r["tick"] - t0) / TICK_HZ, r["o2"]) for r in rows], "rows"
        q = self.qpc_window(name)
        ev = [r for r in self.ev("o2") if r.get("o2") is not None and isinstance(r.get("qpc"), int) and q and
              q[0] <= r["qpc"] <= q[1]]
        if not ev:
            ev = [r for r in self.ev("o2") if r.get("o2") is not None and str(r.get("label", "")).startswith(name)]
        ev.sort(key=lambda r: r.get("qpc") or 0)
        if not ev:
            return [], "none"
        q0 = ev[0].get("qpc") or 0
        return [(((r.get("qpc") or q0) - q0) / float(self.qpcf), float(r["o2"])) for r in ev], "dumps"

# =============================================================================================== segments and replay
def _ticks_of(seg):
    return max(1, int(math.floor(float(seg.get("seconds") or 0.0) * TICK_HZ + 0.5)))

def _move_of(seg):
    m = seg.get("move")
    if isinstance(m, (list, tuple)) and len(m) >= 3:
        return tuple(float(x) for x in m[:3])
    return (float(seg.get("strafe") or 0.0), float(seg.get("vertical") or 0.0), float(seg.get("forward") or 0.0))

def segments_of(sess, part):
    """The plan segments of a launcher script and the rows of each: ([(seg, rows)], how)."""
    plan = next((e for e in sess.ev("swim_script") if e.get("label") == part), None)
    if not plan or not plan.get("segments"):
        return [], "no plan"
    segs = plan["segments"]
    rows = sess.rows_in(part)
    labelled = {s["label"]: [r for r in rows if r.get("seg") == s["label"]] for s in segs}
    if rows and all(labelled[s["label"]] for s in segs):
        return [(s, labelled[s["label"]]) for s in segs], "row labels"
    scripted = [r for r in rows if r.get("step") is not None and r.get("src") == "script"]
    if scripted:
        # B rows carry the running script's step index: the first run of step 0 starts this script
        i0 = next((k for k, r in enumerate(scripted) if r["step"] == 0), None)
        if i0 is not None:
            mine = []
            for r in scripted[i0:]:
                if mine and r["step"] < mine[-1]["step"]:
                    break                       # the next script began
                mine.append(r)
            by = {k: [r for r in mine if r["step"] == k] for k in range(len(segs))}
            if all(by[k] for k in range(len(segs))):
                return [(s, by[k]) for k, s in enumerate(segs)], "script step index"
    if not rows:
        return [(s, []) for s in segs], "no rows"

    def key(m, sp):
        return (tuple(round(x, 3) for x in (m or (0.0, 0.0, 0.0))), bool(sp))
    want = [key(_move_of(s), s.get("sprint")) for s in segs]
    if any(r.get("move") is not None for r in rows):
        # runs of identical inputs (runs shorter than 3 ticks dropped); the run that matches the first segment starts
 # the plan; each run is cut to its planned length (the release after a script continues the last run)
        runs = []
        for r in rows:
            k = key(r.get("move"), r.get("sprint"))
            if runs and runs[-1][0] == k:
                runs[-1][1].append(r)
            else:
                runs.append([k, [r]])
        runs = [x for x in runs if len(x[1]) >= 3]
        i0 = next((i for i, x in enumerate(runs) if x[0] == want[0]), None)
        if i0 is not None and [x[0] for x in runs[i0:i0 + len(want)]] == want:
            out = []
            for k, s in enumerate(segs):
                rs = runs[i0 + k][1]
                out.append((s, [r for r in rs if r["tick"] < rs[0]["tick"] + _ticks_of(s)]))
            return out, "input runs"
    st = _f(plan.get("startTick"))
    t0 = int(st) if st is not None and rows[0]["tick"] - 120 <= st <= rows[-1]["tick"] else None
    if t0 is None:
        first = next((r for r in rows if r.get("move") is not None and key(r["move"], r.get("sprint")) == want[0]),
                     None)
        t0 = first["tick"] if first else rows[0]["tick"]
    out, cur = [], t0
    for s in segs:
        n = _ticks_of(s)
        out.append((s, [r for r in rows if cur <= r["tick"] < cur + n]))
        cur += n
    return out, "time from tick %d" % t0

def motor_phase(rows, step_ticks):
    """The tick residue (mod step_ticks) of the motor steps: from the rows' stepped flags, else from the ticks on which
 the imposed velocity changes; None when no residue dominates."""
    res = Counter(r["tick"] % step_ticks for r in rows if r.get("stepped"))
    if sum(res.values()) < 3:
        res = Counter()
        for a, b in zip(rows, rows[1:]):
            if b["tick"] - a["tick"] == 1 and a.get("vel") and b.get("vel") and _norm(_sub(a["vel"], b["vel"])) > 1e-9:
                res[b["tick"] % step_ticks] += 1
    if sum(res.values()) < 3:
        return None
    k, n = res.most_common(1)[0]
    return k if n >= PHASE_SHARE * sum(res.values()) else None

def replay(port, sess, seg_rows, own_position=False, pass_rows=True):
    """Open-loop replay of a script's rows from the logged velocity before them: ({tick: v}, {tick: eye}, how).
 A rows go through the model module's openloop_series when it has one (its own n1 semantics). Otherwise one model
 tick per SE tick with the logged inputs; when the motor-step phase is known the replay starts on the last step
 tick at or before the window (a pre-roll over the rows before it), so a model that steps from its first tick runs
 in phase with SE. own_position: the eye the model sees is its own (integrated from the first row), else the
 logged one; pass_rows: each tick carries the logged row (a model may read its own layout's state from it)."""
    rows = sorted((r for _s, rs in seg_rows for r in rs), key=lambda r: r["tick"])
    if not rows:
        return {}, {}, "no rows"
    M = getattr(port, "module", None)
    if pass_rows and not own_position and rows[0]["fmt"] == "A" and M is not None and \
            callable(getattr(M, "openloop_series", None)):
        n0, n1 = rows[0].get("n"), rows[-1].get("n")
        by_n = {r["n"]: r for r in sess.rows if r["fmt"] == "A"}
        if isinstance(n0, int) and isinstance(n1, int) and all(k in by_n for k in range(n0, n1 + 1)):
            series = M.openloop_series([by_n[k]["raw"] for k in range(n0, n1 + 1)])
            return ({s["tick"]: tuple(s["v"]) for s in series}, {s["tick"]: s["p"][1] for s in series},
                    "openloop_series (A rows)")
    step_ticks = int(getattr(port, "step_ticks", None) or STEP_TICKS_DEFAULT)
    first = rows[0]["tick"]
    window = [r for r in sess.rows if first - 6 * step_ticks <= r["tick"] <= rows[-1]["tick"]]
    phase = motor_phase(window, step_ticks)
    start = first - ((first - phase) % step_ticks) if phase is not None else first
    pre = [sess.by_tick.get(t) or dict(rows[0], tick=t, move=(0.0, 0.0, 0.0), sprint=False, raw=None)
           for t in range(start, first)]
    prev = next((sess.by_tick[t] for t in range(start - 1, start - 4, -1) if t in sess.by_tick and
                 sess.by_tick[t].get("vel")), None)
    v = prev["vel"] if prev else (0.0, 0.0, 0.0)
    y_prev = head_of(prev) if prev else None
    y_own = None
    port.begin()
    out, eyes = {}, {}
    last_tick = start - 1
    for r in pre + rows:
        n = max(1, r["tick"] - last_tick)
        for k in range(n):
            t = r["tick"] - n + 1 + k
            y = r.get("yCtl") if r.get("yCtl") is not None else (y_prev if y_prev is not None else head_of(r))
            if own_position:
                y_own = y if y_own is None else y_own
                y = y_own
            tick = {"move": r.get("move") or (0.0, 0.0, 0.0), "sprint": r.get("sprint"), "look": r.get("look"),
                    "right": r.get("right"), "pos": r.get("pos"), "dt": r.get("dt") or TICK_S, "yHead": y,
                    "inWater": True if r.get("ctl") is None else r["ctl"], "grounded": r.get("grounded"),
                    "tickNo": t, "row": r.get("raw") if (pass_rows and k == n - 1) else None, "phase": phase,
                    "motorStep": None if phase is None else t % step_ticks == phase}
            v = tuple(port.step(v, tick))
            if own_position:
                y_own = y + v[1] * (r.get("dt") or TICK_S)
        out[r["tick"]] = v
        if own_position:
            eyes[r["tick"]] = y_own
        y_prev = head_of(r)
        last_tick = r["tick"]
    return out, eyes, "per-tick steps (phase %s%s)" % (
        "tick %% %d = %d" % (step_ticks, phase) if phase is not None else "unknown",
        ", pre-roll %d ticks" % len(pre) if pre else "")

def metrics(seg, series, prev_end):
    """series: [(t from the segment start, v)] per tick. The segment's numbers."""
    if not series:
        return {}
    moving = any(abs(x) > 1e-9 for x in _move_of(seg))
    T = series[-1][0]
    out = {"moving": moving}
    if moving:
        last = [v for t, v in series if t > T - min(PLATEAU_S, T / 2.0) + 1e-9]
        p = tuple(sum(c) / len(last) for c in zip(*last))
        out.update(plateau=_norm(p), plateauVec=p, vy=p[1])
        u = _unit(p)
        out["t90"] = next((t for t, v in series if _dot(v, u) >= T90 * out["plateau"]), None)
    else:
        v0 = prev_end if prev_end is not None else _norm(series[0][1])
        dist, t_prev = 0.0, 0.0
        for t, v in series:                  # rows may be sparse: each speed holds over its own interval
            dist += _norm(v) * (t - t_prev)
            t_prev = t
        out.update(releaseSpeed=v0, stopDist=dist,
                   t10=next((t for t, v in series if _norm(v) <= T10 * v0), None) if v0 > FLOOR_SPEED else None)
    return out

def _cmp(rows, cid, what, logged, model, floor, unit, part, required=True, nd=3, vec=None):
    if logged is None or model is None:
        C.row(rows, cid, "MISSING", "logged %s, replay %s" % (_fmt(logged, unit, nd), _fmt(model, unit, nd)),
              "within 5 %", what, part, required=required)
        return
    err = abs(vec) if vec is not None else abs(logged - model)
    tol = max(REL_TOL * abs(model), floor)
    status = "PASS" if err <= tol else "FAIL"
    if not required:
        status = "INFO" if status == "PASS" else "FAIL"
    C.row(rows, cid, status, "%s vs %s" % (_fmt(logged, unit, nd), _fmt(model, unit, nd)),
          "|d| %s <= %s" % (_fmt(err, unit, nd), _fmt(tol, unit, nd)), what, part, required=required,
          logged=logged, replay=model, error=err, tolerance=tol)

# =============================================================================================== N1
def evaluate_n1(sess, rows):
    if "n1" not in sess.parts:
        return
    parts = [p for p in N1_PARTS if sess.has_part(p)]
    if not parts:
        C.row(rows, "S2b.N1", "MISSING", "no n1a/n1b part", "two scripts", "S2b.N1 scripted swim at -50 m", "n1")
        return
    port = load_model()
    seg_all, hows = [], []
    for p in parts:
        sr, how = segments_of(sess, p)
        seg_all.append((p, sr))
        hows.append("%s: %s" % (p, how))
    nrows = sum(len(rs) for _p, sr in seg_all for _s, rs in sr)
    if nrows == 0:
        C.row(rows, "S2b.N1", "MISSING", "no swim rows in the script windows", "per-tick swim rows",
              "S2b.N1 swim rows of the two scripts (%s)" % "; ".join(hows), "n1")
        return
    if port is None:
        C.row(rows, "S2b.N1", "MISSING", "no model", "swim_replay.py replay", "S2b.N1 replay of the logged inputs: %s"
              % _MODEL["status"], "n1")
        return
    diag = _MODEL["mode"] == "plant"
    logged, model, rhow = {}, {}, []
    worst = (0.0, None)
    for p, sr in seg_all:
        if not all(rs for _s, rs in sr):
            missing = [s["label"] for s, rs in sr if not rs]
            C.row(rows, "S2b.N1", "MISSING", "segments without rows: %s" % ",".join(missing)[:40], "every segment",
                  "S2b.N1 script %s" % p, "n1")
        try:
            rep, _eyes, how = replay(port, sess, sr)
        except Exception as e:
            C.row(rows, "S2b.N1", "MISSING", "model step failed: %s" % type(e).__name__, "swim_replay.py replay",
                  "S2b.N1 replay of script %s: %s" % (p, str(e)[:100]), "n1")
            return
        rhow.append("%s %s" % (p, how))
        prev_l = prev_m = None
        for s, rs in sr:
            if not rs:
                prev_l = prev_m = None
                continue
            t0 = rs[0]["tick"] - 1
            ser_l = [((r["tick"] - t0) / TICK_HZ, measured(r)) for r in rs if measured(r)]
            ser_m = [((r["tick"] - t0) / TICK_HZ, rep[r["tick"]]) for r in rs if r["tick"] in rep]
            logged[s["label"]] = metrics(s, ser_l, prev_l)
            model[s["label"]] = metrics(s, ser_m, prev_m)
            prev_l = _norm(ser_l[-1][1]) if ser_l else None
            prev_m = _norm(ser_m[-1][1]) if ser_m else None
            for (t, vl), (_t2, vm) in zip(ser_l, ser_m):
                d = _norm(_sub(vl, vm))
                if d > worst[0]:
                    worst = (d, "%s +%.2f s" % (s["label"], t))
    C.row(rows, "S2b.N1", "INFO", ("%d rows; %s" % (nrows, "; ".join(hows)))[:60], "segments found",
          "S2b.N1 rows per segment (%s); replay: %s" % ("; ".join(hows), "; ".join(rhow)), "n1", required=False)
    req = not diag
    suffix = " [--model plant]" if diag else ""
    for lab in N1_DRIVE:
        lg, md = logged.get(lab, {}), model.get(lab, {})
        if not lg and not md:
            continue
        verr = _norm(_sub(lg["plateauVec"], md["plateauVec"])) if lg.get("plateauVec") and md.get("plateauVec") else None
        _cmp(rows, "S2b.N1", "%s plateau velocity (mean of the last 1 s; vector error vs the replay)%s" % (lab, suffix),
             lg.get("plateau"), md.get("plateau"), FLOOR_SPEED, " m/s", "n1", required=req, vec=verr)
        if lab in ("n1.up", "n1.down"):
            _cmp(rows, "S2b.N1", "%s vertical speed%s" % (lab, suffix), lg.get("vy"), md.get("vy"), FLOOR_SPEED,
                 " m/s", "n1", required=req)
        _cmp(rows, "S2b.N1", "%s acceleration time to 90 %% of the plateau%s" % (lab, suffix), lg.get("t90"),
             md.get("t90"), FLOOR_TIME, " s", "n1", required=req and lab in N1_REQUIRED_T90)
    for lab in N1_RELEASE:
        lg, md = logged.get(lab, {}), model.get(lab, {})
        if not lg and not md:
            continue
        _cmp(rows, "S2b.N1", "%s stop distance after the release%s" % (lab, suffix), lg.get("stopDist"),
             md.get("stopDist"), FLOOR_DIST, " m", "n1", required=req and lab in N1_REQUIRED_STOP)
        _cmp(rows, "S2b.N1", "%s stop time to 10 %% of the release speed%s" % (lab, suffix), lg.get("t10"),
             md.get("t10"), FLOOR_TIME, " s", "n1", required=req and lab in N1_REQUIRED_T10)
    f_l = (logged.get(SPRINT_SEG, {}).get("plateau"), logged.get(FORWARD_SEG, {}).get("plateau"))
    f_m = (model.get(SPRINT_SEG, {}).get("plateau"), model.get(FORWARD_SEG, {}).get("plateau"))
    _cmp(rows, "S2b.N1", "sprint factor (sprint plateau / forward plateau; Subnautica: no sprint underwater)%s" % suffix,
         f_l[0] / f_l[1] if all(f_l) else None, f_m[0] / f_m[1] if all(f_m) else None, FLOOR_FACTOR, "", "n1",
         required=req)
    C.row(rows, "S2b.N1", "INFO", "%.3f m/s at %s" % (worst[0], worst[1]) if worst[1] else "-", "information",
          "S2b.N1 worst per-tick |v measured - v replay| (model: %s)" % (port.name,), "n1", required=False)

# =============================================================================================== N2
def evaluate_n2(sess, rows):
    if not any(p in sess.parts for p in ("n1", "n2", "n3", "n4")):
        return
    # the jetpack with the head under water (every part; the n2 script requests it at -18 m)
    under = [r for r in sess.rows if head_of(r) is not None and head_of(r) < JET_OFF_Y]
    known = [r for r in under if r.get("jet") is not None or r.get("jetOk") is not None]
    allowed = [r for r in known if r.get("jetOk")]
    on_twice = [b for a, b in zip(sess.rows, sess.rows[1:]) if b["tick"] - a["tick"] == 1 and b.get("jet") and
                a.get("jet") and head_of(a) is not None and head_of(b) is not None and head_of(a) < JET_OFF_Y and
                head_of(b) < JET_OFF_Y]
    # only dumps taken while the controller swims: a dump right after a teleport into the water (before the entry
 # tick) still shows the stock jetpack state
    dumps_bad = [d for d in sess.dumps("") if d["yHead"] is not None and d["yHead"] < JET_OFF_Y and d["inWater"] and
                 (d["jetpack"] or d["jetpackAllowed"])]
    what = "jetpack off with the head under water (eye < %g m): never on two ticks running, never allowed (S2b.M2)" % (
        JET_OFF_Y)
    req_seen = any("script jetpack request on" in x for x in sess.lines)
    if not known:
        C.row(rows, "S2b.N2", "MISSING", "no head-under row with the jetpack state", "0", what, "all")
    else:
        bad = len(allowed) + len(on_twice) + len(dumps_bad)
        C.row(rows, "S2b.N2", "FAIL" if bad else "PASS", "on2 %d, allowed %d, dumps %d" % (
            len(on_twice), len(allowed), len(dumps_bad)), "0 of %d rows%s" % (len(known), "" if req_seen else
                                                                                 " (no request)"),
              what, "all", first=[r["tick"] for r in (on_twice + allowed)[:5]])
    damp_rows = [r for r in sess.rows if r.get("inWater") and r.get("damp") is not None]
    if damp_rows:
        damp_twice = [b for a, b in zip(sess.rows, sess.rows[1:]) if b["tick"] - a["tick"] == 1 and a.get("inWater")
                      and b.get("inWater") and a.get("damp") and b.get("damp")]
        C.row(rows, "S2b.N2", "FAIL" if damp_twice else "PASS", "%d of %d in-water rows on twice running" % (
            len(damp_twice), len(damp_rows)), "0", "dampeners off in the water (swim rows)", "all")
    if "n2" in sess.parts and sess.has_part("n2"):
        # the check above can only fail if SE switched the jetpack on at all
        on_under = [r for r in sess.rows_in("n2") if r.get("jet") and head_of(r) is not None and head_of(r) < JET_OFF_Y]
        off_line = [x for x in sess.lines if "DE.Swim jetpack off: head under" in x]
        what = ("the n2 jetpack request under water reached SE's jetpack (a head-under row with SE's jetpack on, or the "
                "swim rule's switch-off line; never without hydrogen)")
        logged = any(x.startswith("DE.Swim") for x in sess.lines)     # the plugin logs; generic rows alone may not
        C.row(rows, "S2b.N2", "PASS" if (on_under or off_line) else ("FAIL" if logged else "MISSING"),
              "%d head-under rows on, %d switch-off lines" % (len(on_under), len(off_line)), ">= 1", what, "n2",
              required=logged)
    if "n2" not in sess.parts:
        return
    if not sess.has_part("n2"):
        C.row(rows, "S2b.N2", "MISSING", "n2 part not run", "eye at the surface", "S2b.N2 swim up from -20 m", "n2")
        return
    port = load_model()
    sr, how = segments_of(sess, "n2")
    up = next((rs for s, rs in sr if s["label"] == "n2.up"), [])
    up = [r for r in up if head_of(r) is not None]
    limit, src = None, None
    if port is not None and up:
        try:
            _v, eyes, _how = replay(port, sess, [(None, up)], own_position=True, pass_rows=False)
            if eyes:
                limit, src = max(eyes.values()), "model's own climb on these inputs"
        except Exception:
            limit = None
    if limit is None and port is not None and port.surface_head_max() is not None:
        limit, src = port.surface_head_max(), "model surface_head_max()"
    if limit is None:
        limit, src = SURFACE_HEAD_MAX_DEFAULT, "default (UnderwaterMotor.cs:264)"
    what = "swimming up 20 s from -20 m (n2.up, %s): the eye reaches the surface, never above the swim limit (%s %.2f m)" % (
        how, src, limit)
    if not up:
        C.row(rows, "S2b.N2", "MISSING", "no n2.up rows with an eye height", "[%.2f, %.2f] m" % (
            SURFACE_REACH_Y, limit + SURFACE_TOL_M), what, "n2")
    else:
        mx = max(max(head_of(r), r.get("yHeadAfter") if r.get("yHeadAfter") is not None else -1e9) for r in up)
        status = "PASS" if SURFACE_REACH_Y <= mx <= limit + SURFACE_TOL_M else "FAIL"
        C.row(rows, "S2b.N2", status, "max eye %.3f m" % mx, "[%.2f, %.2f] m" % (SURFACE_REACH_Y,
                                                                                    limit + SURFACE_TOL_M),
              what, "n2", maxEye=mx, limit=limit)
    # the jetpack back with the head out (n2j: a request at the surface clamp). SE's own jetpack counts: the rows' jet
 # (SE's JetpackRunning at the start of the tick), a dump's jetpack, an exit with reason jetpack (the jetpack
 # running at the latch exit); the model's jetOk alone does not
    what = ("jetpack back with the head out (n2j: SE's jetpack running on a row or a dump with the eye >= %g m, or an "
            "exit reason=jetpack)" % HEAD_OUT_Y)
    if not sess.has_part("n2j"):
        C.row(rows, "S2b.N2", "MISSING", "n2j part not run", "granted", what, "n2j")
        return
    rs = sess.rows_in("n2j")
    out = [r for r in rs if head_of(r) is not None and head_of(r) >= HEAD_OUT_Y]
    granted = [r for r in out if r.get("jet")]
    model_only = [r for r in out if r.get("jetOk") and not r.get("jet")]
    dumps_on = [d for d in sess.dumps("n2j") if d.get("jetpack") and d.get("yHead") is not None and
                d["yHead"] >= HEAD_OUT_Y]
    span = sess.span_of("n2j")
    jet_exits = [t for t in sess.transitions_in(span) if t["kind"] == "exit" and t.get("reason") == "jetpack"]
    refused = any("DE.Swim jetpack off" in x for x in sess.lines) and not granted and not jet_exits
    if granted or jet_exits or dumps_on:
        C.row(rows, "S2b.N2", "PASS", "%d rows and %d dumps with SE's jetpack on, %d jetpack exits" % (
            len(granted), len(dumps_on), len(jet_exits)), "granted", what, "n2j")
    elif not out:
        C.row(rows, "S2b.N2", "MISSING", "the eye never out in n2j (%d rows)" % len(rs), "granted", what, "n2j")
    else:
        C.row(rows, "S2b.N2", "FAIL", "%d head-out rows, SE's jetpack never on (%d allowed by the model only)%s" % (
            len(out), len(model_only), "; refused" if refused else ""), "granted", what, "n2j")

# =============================================================================================== N3
def _bounce(trs):
    """(alternation violations, min spacing in s, pairs closer than BOUNCE_S)."""
    alt = sum(1 for a, b in zip(trs, trs[1:]) if a["kind"] == b["kind"])
    gaps = [(b["tick"] - a["tick"]) / TICK_HZ for a, b in zip(trs, trs[1:])]
    close = sum(1 for g in gaps if g < BOUNCE_S)
    return alt, (min(gaps) if gaps else None), close

def evaluate_n3(sess, rows):
    if "n2" in sess.parts and sess.has_part("n2"):
        sr, _how = segments_of(sess, "n2")
        up = next((rs for s, rs in sr if s["label"] == "n2.up"), [])
        span = (up[0]["tick"], up[-1]["tick"]) if up else None
        trs = sess.transitions_in(span)
        alt, mn, close = _bounce(trs)
        what = "no bounce at the surface hold (n2.up: ~17 s at the clamp): <= 1 transition, none within 0.5 s (S2b.M3)"
        if span is None:
            C.row(rows, "S2b.N3", "MISSING", "no n2.up rows", "<= 1", what, "n2")
        else:
            C.row(rows, "S2b.N3", "PASS" if len(trs) <= 1 and close == 0 else "FAIL", "%d transitions%s" % (
                len(trs), ", min gap %.2f s" % mn if mn is not None else ""), "<= 1, gaps >= 0.5 s", what, "n2",
                  source=sess.transitions_src)
    if "n3" not in sess.parts:
        return
    plan = next((e for e in sess.ev("n3")), None)
    if not sess.has_part("n3") or plan is None:
        C.row(rows, "S2b.N3", "MISSING", "n3 part not run", "1 exit + 1 entry per cycle", "S2b.N3 crossings", "n3")
        return
    cycles = int(plan.get("cycles") or 0)
    sr, how = segments_of(sess, "n3")
    what = "%d scripted crossings (swim up, jetpack out, fall back in; %s): one exit and one entry per cycle (%s)" % (
        cycles, how, sess.transitions_src)
    span = sess.span_of("n3")
    trs = sess.transitions_in(span)
    if not trs and not sess.rows_in("n3"):
        C.row(rows, "S2b.N3", "MISSING", "no rows or transitions in n3", "%d x (1 + 1)" % cycles, what, "n3")
        return
    per = []
    for k in range(cycles):
        rs = [r for s, rr in sr if s["label"].startswith("n3.c%d." % k) for r in rr]
        if not rs:
            per.append(None)
            continue
        nxt = [r for s, rr in sr if s["label"].startswith("n3.c%d." % (k + 1)) for r in rr]
        cs = (rs[0]["tick"], (nxt[0]["tick"] - 1) if nxt else span[1])
        t = sess.transitions_in(cs)
        per.append((sum(1 for x in t if x["kind"] == "exit"), sum(1 for x in t if x["kind"] == "enter")))
    good = [p for p in per if p == (1, 1)]
    exits = sum(1 for t in trs if t["kind"] == "exit")
    enters = sum(1 for t in trs if t["kind"] == "enter")
    C.row(rows, "S2b.N3", "PASS" if len(good) == cycles and cycles else "FAIL",
          "%d/%d cycles 1+1 (%d exits, %d entries)" % (len(good), cycles, exits, enters), "%d/%d" % (cycles, cycles),
          what, "n3", perCycle=per)
    alt, mn, close = _bounce(trs)
    C.row(rows, "S2b.N3", "PASS" if alt == 0 and close == 0 and trs else "FAIL",
          "%d not alternating, %d < 0.5 s%s" % (alt, close, ", min %.2f s" % mn if mn is not None else ""),
          "0, 0", "no bounce in the crossings: transitions alternate, >= 0.5 s apart (hysteresis; S2b.M3)", "n3")
    ent = [t["yHead"] for t in trs if t["kind"] == "enter" and t.get("yHead") is not None]
    ext = [t["yHead"] for t in trs if t["kind"] == "exit" and t.get("yHead") is not None]
    C.row(rows, "S2b.N3", "INFO", "entry eye %s, exit eye %s" % (
        _fmt(sum(ent) / len(ent), " m", 2) if ent else "-", _fmt(sum(ext) / len(ext), " m", 2) if ext else "-"),
        "model: -0.1 / jetpack", "eye heights at the transitions (mean)", "n3", required=False)
    # restore on exit (the SE's DE.Swim enter/exit lines): dampeners (and the jetpack, unless the exit was the jetpack)
 # back to what the entry remembered
    res = []
    for ex in [t for t in sess.text_tr if t["kind"] == "exit" and t.get("tick") is not None and span and
               span[0] <= t["tick"] <= span[1]]:
        en = next((t for t in reversed(sess.text_tr) if t["kind"] == "enter" and t.get("tick") is not None and
                   t["tick"] < ex["tick"]), None)
        if en is None or ex.get("dampenersTo") is None:
            continue
        ok = ex.get("dampenersTo") == en.get("dampenersWas") and (
            ex.get("reason") == "jetpack" or ex.get("jetpackTo") in (en.get("jetpackWas"), "kept"))
        res.append((ok, "%s: damp %s->%s jet %s->%s" % (ex.get("reason"), en.get("dampenersWas"), ex.get("dampenersTo"),
                                                         en.get("jetpackWas"), ex.get("jetpackTo"))))
    oks = [x for x in res if x[0]]
    C.row(rows, "S2b.N3", "INFO" if res and len(oks) == len(res) else ("MISSING" if not res else "FAIL"),
          "%d/%d exits as the entry found them" % (len(oks), len(res)), "all",
          "jetpack/dampeners restored on exit (DE.Swim enter/exit lines; INFO)", "n3", required=False,
          detail=[x[1] for x in res][:6])

def _expect_restore(ex):
    """(jetpack, dampeners) SE must show after an exit line ("jetpack->on|off|kept dampeners->on|off|kept..."); None =
 not checked. A jetpack exit keeps the jetpack on (it was running at the exit)."""
    jt, dt = ex.get("jetpackTo"), ex.get("dampenersTo")
    jet = True if (jt == "kept" and ex.get("reason") == "jetpack") else {"on": True, "off": False}.get(jt)
    return jet, {"on": True, "off": False}.get(dt)

def evaluate_restore(sess, rows):
    """SE's own jetpack and dampeners after every exit: the first row RESTORE_LAG_TICKS or
 more after the exit tick (the rows' jet / damp = SE's states at the start of that tick), else a de.swim dump with a
 tick in that window, before the next entry; against the exit line's restore, and the line's dampeners (and jetpack,
 unless a jetpack exit) against the entry's remembered states."""
    exits = [t for t in sess.text_tr if t["kind"] == "exit" and t.get("tick") is not None and t.get("dampenersTo")]
    enters = [t for t in sess.text_tr if t["kind"] == "enter" and t.get("tick") is not None]
    what = "jetpack/dampeners restored on exit: SE's state after each exit (rows, else dumps) as the exit line and the entry say"
    if not exits:
        C.row(rows, "S2b.N3", "MISSING", "no DE.Swim exit line with a restore", "every exit", what, "all",
              required=False)
        return
    bad, checked, unseen, detail = [], 0, 0, []
    for ex in exits:
        nxt = next((t["tick"] for t in enters if t["tick"] > ex["tick"]), None)
        lo, hi = ex["tick"] + RESTORE_LAG_TICKS, (nxt - 1) if nxt is not None else ex["tick"] + 600
        en = next((t for t in reversed(enters) if t["tick"] < ex["tick"]), None)
        want_jet, want_damp = _expect_restore(ex)
        src, jet, damp = None, None, None
        row = next((r for r in sess.rows if lo <= r["tick"] <= hi and r.get("damp") is not None), None)
        if row is not None:
            src, jet, damp = "row %d" % row["tick"], row.get("jet"), row.get("damp")
        else:
            d = next((d for d in sess.dumps("") if d.get("tick") is not None and lo <= d["tick"] <= hi and
                      d.get("dampeners") is not None), None)
            if d is not None:
                src, jet, damp = "dump %s" % d["label"], d.get("jetpack"), d.get("dampeners")
        policy_ok = en is None or (ex.get("dampenersTo") == en.get("dampenersWas") and (
            ex.get("reason") == "jetpack" or ex.get("jetpackTo") in (en.get("jetpackWas"), "kept", None)))
        if src is None:
            unseen += 1
            detail.append("tick %d %s: no row or dump before the next entry" % (ex["tick"], ex.get("reason")))
            if not policy_ok:
                bad.append(ex["tick"])
            continue
        checked += 1
        ok = policy_ok and (want_damp is None or bool(damp) == want_damp) and (want_jet is None or bool(jet) == want_jet)
        if not ok:
            bad.append(ex["tick"])
        detail.append("tick %d %s: SE jet %s damp %s (%s) vs line jetpack->%s dampeners->%s, entry %s/%s" % (
            ex["tick"], ex.get("reason"), _fmt_flag(jet), _fmt_flag(damp), src, ex.get("jetpackTo"),
            ex.get("dampenersTo"), en.get("jetpackWas") if en else "-", en.get("dampenersWas") if en else "-"))
    status = "FAIL" if bad else ("PASS" if checked else "MISSING")
    C.row(rows, "S2b.N3", status, "%d/%d exits checked, %d wrong, %d unseen" % (checked, len(exits), len(bad), unseen),
          "every seen exit as restored", what, "all", detail=detail[:8], first=bad[:5])

def _fmt_flag(x):
    return "-" if x is None else ("on" if x else "off")

# =============================================================================================== N4
def _slope_fit(samples):
    n = len(samples)
    if n < 2:
        return None
    mt = sum(t for t, _ in samples) / n
    mo = sum(o for _, o in samples) / n
    den = sum((t - mt) ** 2 for t, _ in samples)
    return sum((t - mt) * (o - mo) for t, o in samples) / den if den > 0 else None

def o2_rate(samples):
    """(rate fraction/s consumed (positive), span s, method). Step-aligned when the series is a staircase. An upward
 jump
 splits the series: the rate is the total drop over the total time of the falling
 pieces, each measured as below."""
    s = [(t, o) for t, o in samples if o > O2_EMPTY]
    if len(s) < 2:
        return None, 0.0, "too few samples"
    jumps = [i for i in range(1, len(s)) if s[i][1] > s[i - 1][1] + O2_JUMP]
    if jumps:
        drop, span, a = 0.0, 0.0, 0
        for j in jumps + [len(s)]:
            piece = s[a:j]
            a = j
            if len(piece) < 2:
                continue
            r, sp, _how = o2_rate(piece)
            if r is not None and sp > 0:
                drop += r * sp
                span += sp
        if span <= 0:
            return None, 0.0, "too few samples between %d upward jump(s)" % len(jumps)
        return drop / span, span, "falling pieces between %d upward jump(s) (SE bottle refill?) excluded" % len(jumps)
    steps = [i for i in range(1, len(s)) if s[i][1] < s[i - 1][1] - 1e-6]
    flat = sum(1 for i in range(1, len(s)) if abs(s[i][1] - s[i - 1][1]) <= 1e-6)
    if len(steps) >= 3 and flat >= len(s) // 2:
        i0, i1 = steps[0], steps[-1]
        span = s[i1][0] - s[i0][0]
        if span > 0:
            return (s[i0][1] - s[i1][1]) / span, span, "step-aligned (%d steps)" % len(steps)
    span = s[-1][0] - s[0][0]
    fit = _slope_fit(s)
    return (-fit if fit is not None else None), span, "linear fit"

def evaluate_n4(sess, rows):
    if "n4" not in sess.parts:
        return
    if not sess.has_part("n4.dive"):
        C.row(rows, "S2b.N4", "MISSING", "n4 part not run", "model rate", "S2b.N4 O2 underwater", "n4")
        return
    info = next((e for e in sess.ev("n4")), {})
    rs = [r for r in sess.rows_in("n4.dive") if head_of(r) is not None]
    depth = -sum(head_of(r) for r in rs) / len(rs) if rs else -float(info.get("depthY") or -30.0)
    port = load_model()
    model = port.o2(depth) if port is not None else None
    samples, src = sess.o2_samples("n4.dive")
    rate, span, how = o2_rate(samples)
    what = "O2 underwater (n4.dive, eye %.1f m; %s, %s): rate vs the model" % (-depth, src, how)
    if model is None or not model.get("rate") or not model.get("capacity"):
        C.row(rows, "S2b.N4", "MISSING", "no model O2 rule (%s)" % (_MODEL["status"] if port is None else "o2()")[:40],
              "within 5 %", what, "n4")
    elif rate is None:
        C.row(rows, "S2b.N4", "MISSING", "%d samples" % len(samples), "within 5 %", what, "n4")
    else:
        m_rate = model["rate"] / model["capacity"]
        step = (model.get("breathUnits") or 0.0) / model["capacity"]
        extra = step / span if (step and span > 0 and src == "dumps") else 0.0
        tol = REL_TOL * m_rate + extra
        status = "PASS" if abs(rate - m_rate) <= tol else "FAIL"
        C.row(rows, "S2b.N4", status, "%.5f vs %.5f /s" % (rate, m_rate), "|d| <= %.5f /s%s" % (
            tol, " (+1 breath/%.0f s)" % span if extra else ""), what, "n4", logged=rate, model=m_rate, span=span,
              samples=len(samples))
    surf, ssrc = sess.o2_samples("n4.surface")
    what = "refill at the surface (n4.surface, %s): back to >= %.0f %%" % (ssrc, O2_FULL * 100)
    if not surf:
        C.row(rows, "S2b.N4", "MISSING", "no surface O2 sample", ">= %.2f" % O2_FULL, what, "n4")
    else:
        mx, last = max(o for _t, o in surf), surf[-1][1]
        lo = min([o for _t, o in samples] or [mx])
        status = "PASS" if mx >= O2_FULL and last >= O2_FULL and (mx > lo + 1e-6 or lo >= O2_FULL) else "FAIL"
        C.row(rows, "S2b.N4", status, "dive min %.3f -> surface max %.3f, last %.3f" % (lo, mx, last),
              ">= %.2f" % O2_FULL, what, "n4")
    for path_key, name in (("SpaceEngineers.cfg", "SpaceEngineers.cfg"), ("WaterClientSettings.xml",
                                                                          "WaterClientSettings.xml")):
        h = [e for e in sess.ev("file_hash") if str(e.get("path", "")).replace("\\", "/").endswith(path_key)]
        b = next((e for e in h if e.get("when") == "before"), None)
        a = next((e for e in h if e.get("when") == "after"), None)
        what = "%s unchanged by the session (SHA-256 before/after, read-only)" % name
        if b is None or a is None:
            standin = (sess.start.get("standin") or {}).get("se")
            C.row(rows, "S2b.N4", "OTHER" if standin else "MISSING", "stand-in run" if standin else "no hash pair",
                  "identical", what, "n4", required=not standin)
        else:
            C.row(rows, "S2b.N4", "PASS" if b.get("sha256") == a.get("sha256") else "FAIL",
                  "identical" if b.get("sha256") == a.get("sha256") else "changed", "identical", what, "n4")
    hel = [r["helmet"] for r in rs if r.get("helmet") is not None]
    if hel:
        C.row(rows, "S2b.N4", "INFO", "helmet closed on %d/%d dive rows" % (sum(1 for h in hel if h), len(hel)),
              "all closed", "the suit breathes from its tank only with the helmet closed (an open helmet underwater "
              "leaves the O2 flat whatever the rule)", "n4", required=False)
    hp = [d["health"] for d in sess.dumps("n4") if d.get("health") is not None]
    C.row(rows, "S2b.N4", "INFO", ("health %s -> %s" % (_fmt(hp[0], "", 1), _fmt(hp[-1], "", 1))) if hp else
          "no health in the dumps", "no depth damage", "pressure: Subnautica's diver takes no depth damage "
          "(Player.CrushDamageUpdate is never invoked); CrushDamage stays 0 for the character", "n4", required=False)

# =============================================================================================== all
def evaluate_common(sess, rows):
    dis = [x for x in sess.lines if _DISABLED in x]
    swim_lines = [x for x in sess.lines if x.startswith("DE.Swim")]
    what = "no `%s` line (a missing Harmony/API member leaves the stock SE movement)" % _DISABLED
    if dis:
        C.row(rows, "S2b.ALL", "FAIL", "%d line(s): %s" % (len(dis), dis[0][len(_DISABLED):].strip()[:40]), "0 lines",
              what, "all", lines=dis[:5])
    elif not sess.rows and not swim_lines:
        C.row(rows, "S2b.ALL", "MISSING", "no swim row and no DE.Swim line", "S2b logged", what, "all")
    else:
        C.row(rows, "S2b.ALL", "PASS", "0 lines (%d rows, %d DE.Swim lines)" % (len(sess.rows), len(swim_lines)),
              "0 lines", what, "all")
    #  a game call failing every tick logs `DE.Swim error` (not FEATURE DISABLED)
    errs = [x for x in sess.lines if any(k in x for k in _ERROR_LINES)]
    what = "no `DE.Swim error`, `DE.Swim depth MISMATCH` or `(S2b patch off)` line"
    if errs:
        C.row(rows, "S2b.ALL", "FAIL", "%d line(s): %s" % (len(errs), errs[0][:60]), "0 lines", what, "all",
              lines=errs[:5])
    elif not sess.rows and not swim_lines:
        C.row(rows, "S2b.ALL", "MISSING", "no swim row and no DE.Swim line", "S2b logged", what, "all")
    else:
        C.row(rows, "S2b.ALL", "PASS", "0 lines", "0 lines", what, "all")
    #  the launcher's session preconditions (helmet, hydrogen, bottles)
    pc = next((e for e in sess.ev("precheck")), None)
    what = "session preconditions (launcher precheck): helmet closed, jetpack powered, no O2 bottle carried"
    if pc is None:
        standin = (sess.start.get("standin") or {}).get("se")
        C.row(rows, "S2b.ALL", "OTHER" if standin else "MISSING", "stand-in run" if standin else "no precheck event",
              "all met", what, "all", required=not standin)
    else:
        C.row(rows, "S2b.ALL", "PASS" if pc.get("ok") else "FAIL", "; ".join(pc.get("problems") or []) or "all met",
              "all met", what, "all", precheck={k: pc.get(k) for k in ("helmet", "jetpackPowered", "h2Level",
                                                                     "o2Bottles", "survival")})
    C.row(rows, "S2b.ALL", "INFO", "layouts %s" % (dict(sess.layouts) or "none"), "B (SE) or A (Wire)",
          "swim row layouts (B SwimController.WriteRow, A Wire SwimTrace, G generic)", "all", required=False)
    muts = sess.start.get("mutants") or []
    seen = sorted({m for x in sess.lines for m in re.findall(r"S2b\.M\d+", x)})
    C.row(rows, "S2b.ALL", "INFO", "mutants %s; in log %s" % (",".join(muts) or "none", ",".join(seen) or "none"),
          "none for a clean run", "seeded failures requested / seen in the SE log", "all", required=False)
    C.row(rows, "S2b.ALL", "INFO", "model %s" % model_status()[:50], "swim_replay.py",
          "the independent model of the N1/N4 comparisons", "all", required=False)
    C.row(rows, "S2b.N5", "OTHER", "", "owner", "both games, the S2 picture: swim 2 min around the reef; the owner's "
          "verdict \"ca feel SN\" (speed, inertia, verticality)", "n5", required=False)

def evaluate(folder) -> list:
    sess = Session(folder)
    rows = []
    if not sess.session:
        C.row(rows, "S2b", "MISSING", "no s2b-session.jsonl", "a launcher s2b session", "session log", "all")
        return rows
    for fn in (evaluate_n1, evaluate_n2, evaluate_n3, evaluate_restore, evaluate_n4, evaluate_common):
        try:
            fn(sess, rows)
        except Exception as e:     # an analyser bug must not hide the other rows
            C.row(rows, "S2b.%s" % fn.__name__.split("_")[-1].upper(), "FAIL", "analyser error %s" % type(e).__name__,
                  "-", str(e)[:120], "all")
    return rows

# =============================================================================================== stand-in traces
_N1_SCRIPTS = (
    ("n1a", (("n1.fwd", 10.0, (0, 0, 1), False), ("n1.rel1", 5.0, (0, 0, 0), False),
             ("n1.strafe", 5.0, (1, 0, 0), False), ("n1.rel2", 5.0, (0, 0, 0), False))),
    ("n1b", (("n1.up", 3.0, (0, 1, 0), False), ("n1.down", 3.0, (0, -1, 0), False),
             ("n1.rel3", 3.0, (0, 0, 0), False), ("n1.back", 5.0, (0, 0, -1), False),
             ("n1.rel5", 4.0, (0, 0, 0), False), ("n1.sprint", 5.0, (0, 0, 1), True),
             ("n1.rel4", 5.0, (0, 0, 0), False))),
)
_N2_SCRIPT = (("n2.jetreq", 1.0, (0, 0, 0), False, True), ("n2.up", 20.0, (0, 1, 0), False, None),
              ("n2.rel", 1.0, (0, 0, 0), False, None))
_N2J_SCRIPT = (("n2j.up", 2.0, (0, 1, 0), False, None), ("n2j.jet", 1.0, (0, 0, 0), False, True),
               ("n2j.off", 3.0, (0, 0, 0), False, False))
_N3_CYCLE = (("up", 2.5, (0, 1, 0), False, None), ("jet", 1.5, (0, 1, 0), False, True),
             ("fall", 3.0, (0, 0, 0), False, False))

class SynthSE:
    """Stand-in traces for the selftest: a deterministic 60 Hz stand-in of the SE swim controller as the SE plugin
 describes it (SwimController, SwimSuitPolicy, SwimO2Rule, the model's latch), driven through the launcher's s2b
 timeline (same parts, scripts, dumps and windows as S2bRunner). The eye is the reference (feet + 1.7 m); the swim
 latch enters below -0.1 m and leaves at +0.8 m (S2b.M3: one line at +0.35 m); with the head out (eye >= 0) the
 jetpack is allowed and switching it on ends the swim (exit reason jetpack); under water it is switched off (S2b.M2:
 never); dampeners off in the water, both restored on exit as remembered at the entry (a teleport while swimming
 re-enters and re-remembers, as the plugin does); the motor through the analyser's plant (S2b.M1: x1.5) every 6
 ticks from the entry; out of the water gravity, the jetpack lifts at 3 m/s with vertical input and holds without;
 O2 per Subnautica (continuous in B rows, as SE's suit drains, breaths on tick % period in A rows), refill 30/s with
 the eye above -1 m (S2b.M4: SE's stock drain 0.0005/s and no refill). Layout "B" (SwimController.WriteRow, the
 default), "A" (Wire SwimTrace bits) or "G" (generic aliases); text lines as the plugin's. Layout "AP" is the
 integrated plugin: the Wire row with the SE keys appended (t, src,
 step, seg, exit, vSe, posAfter, damp, helmet, o2 fraction + o2u,...), continuous O2, and the Fly semantics: the
 jetpack switched on with the head out keeps the swim latch (mode Fly, the model inert, the jetpack lifts) until the
 eye reaches +0.8 m, where the exit has the reason jetpack."""
    EYE, CHEST, G = 1.7, 0.4, 9.81
    QPCF = 10_000_000

    def __init__(self, mut=(), layout="B", text_lines=True, disabled=False, parts=("n1", "n2", "n3", "n4"),
                 quick=False, labels=False, settle=3.0, no_h2=False, bad_restore=False, bottle=False,
                 swim_error=False, precheck_fail=False):
        # review knobs: no_h2 (SE refuses the jetpack), bad_restore (dampeners left off at an exit while the
 # line claims the restore), bottle (SE refills the suit from a bottle 5 s after it falls below 50 %),
 # swim_error (a `DE.Swim error` line), precheck_fail (the launcher's preconditions not met)
        self.no_h2, self.bad_restore, self.bottle = no_h2, bad_restore, bottle
        self.swim_error, self.precheck_fail = swim_error, precheck_fail
        self.bottle_at, self.bottle_used, self.bottle_armed = None, False, False
        self.mut = set(mut)
        self.settle = settle             # seconds after a teleport (3.05: the scripts start off the motor-step phase)
        self.plant = SelftestPlant(1.5 if "M1" in self.mut else 1.0)
        self.layout, self.text, self.disabled, self.labels = layout, text_lines, disabled, labels
        self.parts, self.quick = list(parts), quick
        self.enter_y = getattr(_R, "SWIM_ENTER_EYE_Y", -0.1)
        self.exit_y = getattr(_R, "SWIM_EXIT_EYE_Y", 0.8)
        self.m3_y = getattr(_R, "MUTANT_M3_EYE_Y", 0.35)
        self.tick = 1000
        self.feet = [0.0, -3000.0 - self.EYE, 0.0]
        self.v = (0.0, 0.0, 0.0)
        self.lat, self.jl, self.ph = False, False, 0
        self.eu = False                  # the eye was under the surface on the last tick (the model's s_eu)
        self.jet, self.damp = False, True
        self.jet_was, self.damp_was = False, True
        self.o2u = 45.0
        self.look, self.right = (0.0, 0.0, 1.0), (1.0, 0.0, 0.0)
        self.session, self.se = [], []
        self.n = 0
        self.fly, self.md_prev, self.ctl_prev = False, 0, False
        self.script, self.si, self.sk = None, 0, 0

    # --------------------------------------------------------------------------------------------- clock and logs
    def qpc(self):
        return 5_000_000_000 + self.tick * self.QPCF // 60

    def utc(self):
        import datetime
        t = datetime.datetime(2026, 10, 16, 12, 0, 0) + datetime.timedelta(seconds=self.tick / 60.0)
        return t.isoformat(timespec="milliseconds") + "Z"

    def ev(self, _ev, **kw):
        rec = {"utc": self.utc(), "qpc": self.qpc(), "ev": _ev}
        rec.update(kw)
        self.session.append(rec)

    def log(self, msg):
        if self.text:
            self.se.append({"t": self.utc(), "ev": "log", "msg": msg})

    def eye(self):
        return self.feet[1] + self.EYE

    # --------------------------------------------------------------------------------------------- one tick
    def step(self):
        self.tick += 1
        inp, sprint, req = (0.0, 0.0, 0.0), False, None
        step_idx, src = -1, "none"
        if self.script is not None:
            seg = self.script[self.si]
            if self.sk == 0:
                req = seg[4] if len(seg) > 4 else None
                if req is not None:
                    self.log("DE.Swim script jetpack request %s at y=%.3f" % ("on" if req else "off", self.eye()))
                    self.jet = bool(req) and not self.no_h2      # no hydrogen: SE refuses (HudUnable)
            inp, sprint, step_idx, src = tuple(float(x) for x in seg[2]), bool(seg[3]), self.si, "script"
        y = self.eye()
        jet_in, damp_in, v_in, pre = self.jet, self.damp, self.v, (self.lat, self.jl, self.ph, self.v, self.eu)
        # the latch (the model) and the policy's jetpack exit
        was = self.lat
        if "M3" in self.mut:
            lat = y < self.m3_y
        elif not was:
            lat = y < self.enter_y
        else:
            lat = y < self.exit_y
        allowed_head = y >= 0.0 if (not was or "M3" in self.mut) else (y >= (-0.1 if self.jl else 0.0))
        allowed = (not lat) or allowed_head or ("M2" in self.mut)
        reason = None
        fly_sem = self.layout == "AP"
        if fly_sem:
            if was and not lat:
                reason = "jetpack" if self.jet else "surface"
        elif was and lat and self.jet and allowed:
            lat, reason = False, "jetpack"
        elif was and not lat:
            reason = "surface"
        tr = 0
        if lat and not was:
            tr = 1
            self.jet_was, self.damp_was = self.jet, self.damp
            self.v = (self.v[0], self.v[1] * 0.5, self.v[2])
            self.ph = 0
            self.log("DE.Swim enter tick=%d y=%.3f chestY=%.3f vy=%.3f jetpackWas=%s dampenersWas=%s src=%s" % (
                self.tick, y, y - self.CHEST, self.v[1], "on" if self.jet_was else "off",
                "on" if self.damp_was else "off", src))
        elif was and not lat:
            tr = -1
            jet_to = "kept" if (reason == "jetpack" or "M2" in self.mut) else ("on" if self.jet_was else "off")
            if jet_to != "kept":
                self.jet = self.jet_was
            if not self.bad_restore:
                self.damp = self.damp_was
            self.log("DE.Swim exit tick=%d y=%.3f vy=%.3f reason=%s jetpack->%s dampeners->%s o2mult->restored" % (
                self.tick, y, self.v[1], reason, jet_to, "on" if self.damp_was else "off"))
        self.lat = lat
        self.jl = lat and allowed_head
        stepped = False
        self.fly = fly_sem and lat and self.jet and allowed
        if self.fly:
            self.damp = False                  # the latch is on: dampeners kept off; SE's jetpack moves the engineer
            self.ph = 0                        # the model takes the velocity again with a fresh step (Resumed)
            self.v = (0.0, 3.0 if inp[1] > 0 else 0.0, 0.0)
        elif lat:
            if self.jet and not allowed and "M2" not in self.mut:
                self.jet = False
                self.log("DE.Swim jetpack off: head under the surface (y=%.3f); it comes back with the head out" % y)
            self.damp = False
            if was and (y < 0.0) != self.eu:
                # Subnautica's Dive<->Run motor switch with the latch on: the swim motor is enabled again, vy x 0.5
                self.v = (self.v[0], self.v[1] * 0.5, self.v[2])
            if self.ph == 0:
                self.v = self.plant.step(self.v, {"move": inp, "sprint": sprint, "look": self.look, "dt": TICK_S,
                                                  "yHead": y, "inWater": True, "motorStep": True})
                stepped = True
            self.ph = (self.ph + 1) % self.plant.STEP_TICKS
        elif self.jet:
            self.v = (0.0, 3.0 if inp[1] > 0 else 0.0, 0.0)
        else:
            self.v = (self.v[0], self.v[1] - self.G * TICK_S, self.v[2])
        self.eu = y < 0.0
        self.feet = [p + v * TICK_S for p, v in zip(self.feet, self.v)]
        # oxygen
        o2_before = self.o2u
        if self.bottle and self.bottle_armed and not self.bottle_used and "M4" not in self.mut and \
                self.o2u < 0.5 * self.plant.O2_CAP:
            if self.bottle_at is None:
                self.bottle_at = self.tick + 300          # SE: 5 s after the suit fell below GAS_REFILL_RATION 0.5
            elif self.tick >= self.bottle_at:
                self.o2u, self.bottle_used = self.plant.O2_CAP, True
        if "M4" in self.mut:
            self.o2u = max(0.0, self.o2u - 0.0005 * self.plant.O2_CAP * TICK_S)
        else:
            if y < 0.0:
                rule = self.plant.o2(-y)
                if self.layout == "A":
                    if self.tick % int(round(rule["breathS"] * TICK_HZ)) == 0:
                        self.o2u = max(0.0, self.o2u - rule["breathUnits"])
                else:
                    self.o2u = max(0.0, self.o2u - rule["rate"] * TICK_S)
            if y > -1.0:
                self.o2u = min(self.plant.O2_CAP, self.o2u + self.plant.O2_REFILL * TICK_S)
        self.emit(inp, sprint, step_idx, src, y, tr, reason, stepped, jet_in, damp_in, v_in, pre, allowed, o2_before)
        if self.script is not None:
            self.sk += 1
            if self.sk >= _ticks_of({"seconds": self.script[self.si][1]}):
                self.si, self.sk = self.si + 1, 0
                if self.si >= len(self.script):
                    self.script = None

    def emit(self, inp, sprint, step_idx, src, y, tr, reason, stepped, jet_in, damp_in, v_in, pre, allowed, o2_before):
        eye_after = self.eye()
        if self.layout == "B":
            r = {"t": self.utc(), "ev": "swim", "v": 1, "siteVersion": 1, "tick": self.tick, "dt": TICK_S, "src": src,
                 "step": step_idx, "in": [inp[2], inp[0], inp[1], int(sprint)], "look": list(self.look),
                 "right": list(self.right), "state": "swim" if self.lat else "out", "tr": tr,
                 "exit": reason or "", "yIn": y, "chestY": y - self.CHEST, "grounded": 0, "stepped": int(stepped),
                 "applied": int(self.lat), "vIn": list(v_in), "vSet": list(self.v), "vSe": list(self.v),
                 "pos": [self.feet[0], eye_after, self.feet[2]], "jet": int(self.jet), "damp": int(self.damp),
                 "depthClass": 1, "o2Use": 1.0, "o2Refill": 0.0, "o2": self.o2u / self.plant.O2_CAP,
                 "o2L": self.o2u, "o2Cap": 45.0, "o2Mult": 1.0, "helmet": 1, "mut": 0}
        elif self.layout in ("A", "AP"):
            lat0, jl0, ph0, h0, eu0 = pre
            md = (1 if y < 0 else 2) if self.lat else 0
            md0 = (1 if y < 0 else 2) if lat0 else 0
            if self.layout == "AP":
                md = 3 if self.fly else md
                md0 = self.md_prev
                self.md_prev = md
            r = {"ev": "swim", "v": 1, "n": self.n, "tick": self.tick, "md": md, "y": round(y, 4),
                 "spd": round(_norm(self.v), 4), "o2": round(o2_before, 3)}
            for k, val in (("mx", inp[0]), ("my", inp[1]), ("mz", inp[2]), ("fx", self.look[0]), ("fy", self.look[1]),
                           ("fz", self.look[2]), ("rx", self.right[0]), ("ry", self.right[1]), ("rz", self.right[2]),
                           ("vx", v_in[0]), ("vy", v_in[1]), ("vz", v_in[2]), ("px", self.feet[0]), ("py", y),
                           ("pz", self.feet[2]), ("ox", o2_before)):
                r[k] = _bits_of(val)
            r.update({"spr": int(sprint), "tool": 0, "gnd": 0, "jet": int(jet_in), "mut": 0, "s_lat": int(lat0),
                      "s_jl": int(jl0), "s_jd": int(not allowed), "s_ctl": int(lat0), "s_ph": ph0,
                      "s_hx": _bits_of(h0[0]), "s_hy": _bits_of(h0[1]), "s_hz": _bits_of(h0[2]), "s_md": md0,
                      "s_eu": int(eu0),
                      "o_md": md, "o_ctl": int(self.lat), "o_vx": _bits_of(self.v[0]), "o_vy": _bits_of(self.v[1]),
                      "o_vz": _bits_of(self.v[2]), "o_st": int(stepped), "o_max": _bits_of(0.0),
                      "o_cap": _bits_of(0.0), "o_ja": int(allowed), "o_or": 1, "o_do2": _bits_of(self.o2u - o2_before),
                      "o_dc": 1, "o_evt": (1 if tr > 0 else 2 if tr < 0 else 0), "o_chg": int(md != md0)})
            if self.layout == "AP":
                ctl = self.lat and not self.fly
                r["o_ctl"] = int(ctl)
                r["s_ctl"] = int(self.ctl_prev)
                self.ctl_prev = ctl
                r = dict({"t": self.utc()}, **r)
                r.update({"o2": round(o2_before / self.plant.O2_CAP, 5), "o2u": round(o2_before, 3),
                          "siteVersion": 1, "dt": TICK_S, "src": src, "step": step_idx, "vsrc": "poststep",
                          "exit": reason or "", "chestY": y - self.CHEST, "applied": int(ctl), "gain": 1.0,
                          "vApp": list(self.v) if ctl else [0.0, 0.0, 0.0], "vSe": list(self.v),
                          "posAfter": [self.feet[0], eye_after, self.feet[2]], "jetWant": int(self.jet),
                          "damp": int(damp_in), "dampWant": int(self.damp), "o2Use": 1.0 if y < 0 else 0.0,
                          "o2Refill": 30.0 if y > -1.0 else 0.0, "o2L": round(self.o2u / self.plant.O2_CAP * 30.0, 4),
                          "o2Cap": 30.0, "o2Mult": 1.0, "helmet": 1})
                if self.labels and self.script is not None:
                    r["seg"] = self.script[self.si][0]
            self.n += 1
        else:
            r = {"ev": "swim", "tick": self.tick, "input": {"strafe": inp[0], "vertical": inp[1], "forward": inp[2]},
                 "run": int(sprint), "vx": self.v[0], "vy": self.v[1], "vz": self.v[2], "lx": self.look[0],
                 "ly": self.look[1], "lz": self.look[2], "x": self.feet[0], "y": eye_after, "z": self.feet[2],
                 "headY": y, "chestY": y - self.CHEST, "state": "swim" if self.lat else "walk",
                 "jetpack": int(self.jet), "jetpackAllowed": int(allowed), "dampeners": int(self.damp),
                 "oxygen": round(100.0 * self.o2u / self.plant.O2_CAP, 4)}
            if self.labels and self.script is not None:
                r["seg"] = self.script[self.si][0]
        self.se.append(r)

    def run(self, seconds):
        for _k in range(int(round(seconds * TICK_HZ))):
            self.step()

    # --------------------------------------------------------------------------------------------- launcher acts
    def state(self):
        return {"ok": True, "state": "swim" if self.lat else "out", "eyeY": self.eye(), "tick": self.tick,
                "eyeSn": [self.feet[0], self.eye(), self.feet[2]], "vSet": list(self.v), "vSe": list(self.v),
                "jetpack": self.jet, "dampeners": self.damp, "script": None,
                "suit": {"helmet": True, "jetpackPowered": not self.no_h2, "h2Level": 0.0 if self.no_h2 else 1.0,
                         "o2Bottles": 1 if self.bottle else 0, "survival": True},
                "policy": {"active": self.lat, "jetpackWas": self.jet_was, "dampenersWas": self.damp_was}}

    def reply(self, args, label, rep):
        self.ev("reply", side="se", cmd="de.swim", args=args, reply=rep, label=label, ms=1.0)

    def teleport(self, feet_y, label):
        self.feet, self.v = [0.0, feet_y, 0.0], (0.0, 0.0, 0.0)
        if self.lat:
            # the plugin keeps the latch (SwimModelAdapter.RestartStep): re-evaluated at the new point next tick, where
 # an exit restores as usual; the held velocity is dropped and the next row's step index skips one
            self.log("DE.Swim teleport while swimming: the state is re-evaluated at the new point next tick")
            self.ph, self.n = 0, self.n + 1
        self.reply({"teleportChar": [0.0, feet_y, 0.0], "yawDeg": 0.0}, label, {"ok": True})

    def dump(self, label):
        self.reply({"dump": True}, label, self.state())

    def o2dump(self, label):
        o2 = self.o2u / self.plant.O2_CAP
        self.reply({"o2": "dump"}, label, {"ok": True, "o2": {"rule": "on", "tankSeconds": 45.0, "level": o2}})
        self.ev("o2", label=label, o2=o2, tick=self.tick)
        return o2

    def part(self, name, phase):
        self.ev("part", name=name, phase=phase, tick=self.tick)
        if name == "n4.dive":
            self.bottle_armed = phase == "begin"      # the bottle knob acts in the O2 dive only

    def run_script(self, name, segs, dumps=(), o2=False):
        body = []
        for seg in segs:
            d = {"label": seg[0], "seconds": seg[1], "forward": float(seg[2][2]), "strafe": float(seg[2][0]),
                 "vertical": float(seg[2][1]), "sprint": bool(seg[3]), "move": [float(x) for x in seg[2]]}
            if len(seg) > 4 and seg[4] is not None:
                d["jetpack"] = bool(seg[4])
            body.append(d)
        self.ev("swim_script", label=name, segments=body, seconds=sum(s[1] for s in segs), tick=self.tick,
                reply={"ok": True, "steps": len(segs)}, supported=True)
        self.script, self.si, self.sk = list(segs), 0, 0
        t0 = self.tick
        marks = sorted(dumps)
        while self.script is not None:
            self.step()
            while marks and (self.tick - t0) / TICK_HZ >= marks[0]:
                off = marks.pop(0)
                (self.o2dump if o2 else self.dump)("%s.%s.%g" % (name, "o2" if o2 else "dump", off))
        self.run(0.5)

    def refill(self, label):
        self.teleport(-3.0, label + ".arrive")
        self.run(1.0)
        before = self.o2dump(label + ".before")
        self.run_script(label + ".up0", ((label + ".up", 3.0 / 5.0 + 3.0, (0, 1, 0), False),
                                         (label + ".rel", 0.5, (0, 0, 0), False)))
        after = self.o2dump(label + ".after0")
        self.ev("refill", label=label, before=before, after=after)
        return after

    # --------------------------------------------------------------------------------------------- the session
    def session_run(self):
        self.ev("start", parts=self.parts, quick=self.quick, seAlone=True, standin={"se": False},
                mutants=["S2b." + m for m in sorted(self.mut)], qpcFrequency=self.QPCF)
        for p in ("SpaceEngineers.cfg", "Storage/WaterClientSettings.xml"):
            self.ev("file_hash", path="C:/x/" + p, when="before", sha256="ab" * 32)
        self.log("DE.Swim config: on (selftest stand-in)")
        if self.disabled:
            self.log("DE.Swim FEATURE DISABLED velocity: MissingMethodException (stock SE movement applies)")
        if self.swim_error:
            self.log("DE.Swim error #1 before-step MissingMethodException: Method not found (selftest)")
        self.run(1.0)
        # the launcher: the dump check, the preconditions, per-tick rows, then the session refill
 # and the suit preparation (a known jetpack state: out of the water, jetpack off, fall back in)
        self.dump("session.check")
        problems = ["helmet open"] if self.precheck_fail else []
        if self.no_h2:
            problems.append("jetpack not powered (no hydrogen)")
        self.ev("precheck", ok=not problems, problems=problems, helmet=not self.precheck_fail,
                jetpackPowered=not self.no_h2, h2Level=0.0 if self.no_h2 else 1.0, o2Bottles=0, survival=True)
        self.reply({"rows": "on"}, "session.rows", {"ok": True})
        self.refill("session.refill")
        self.teleport(2.0, "prep.arrive")
        self.run(0.5)
        self.dump("prep.exit")
        self.run_script("prep", (("prep.jetoff", 0.5, (0, 0, 0), False, False), ("prep.fall", 3.0, (0, 0, 0), False)))
        self.dump("prep.after")
        self.ev("suit_prep", jetpackWas=self.jet_was, dampenersWas=self.damp_was, inWater=self.lat)
        if "n1" in self.parts:
            for name, segs in _N1_SCRIPTS:
                self.refill(name + ".refill")
                self.teleport(-50.0, name + ".arrive")
                self.run(self.settle)
                self.dump(name + ".before")
                self.part(name, "begin")
                self.run_script(name, segs)
                self.part(name, "end")
                self.dump(name + ".after")
        if "n2" in self.parts:
            self.teleport(-20.0, "n2.arrive")
            self.run(3.0)
            self.dump("n2.before")
            self.part("n2", "begin")
            self.run_script("n2", _N2_SCRIPT, dumps=(0.5, 1.5, 5.0, 10.0, 15.0, 20.0))
            self.part("n2", "end")
            self.part("n2j", "begin")
            self.run_script("n2j", _N2J_SCRIPT, dumps=(2.5,))
            self.part("n2j", "end")
            self.ev("n2", startY=-20.0, upS=20.0, supported=True, jetSupported=True)
        if "n3" in self.parts:
            cyc = 2 if self.quick else 4
            self.teleport(-3.0, "n3.arrive")
            self.run(3.0)
            self.dump("n3.before")
            self.part("n3", "begin")
            segs = [("n3.c%d.%s" % (k, s[0]),) + tuple(s[1:]) for k in range(cyc) for s in _N3_CYCLE]
            self.run_script("n3", segs, dumps=[round(k * 7.0 + d, 3) for k in range(cyc) for d in (2.25, 3.75, 6.75)])
            self.part("n3", "end")
            self.dump("n3.after")
            self.ev("n3", cycles=cyc, cycle=[list(x[:2]) for x in _N3_CYCLE], startY=-3.0,
                    method="script jetpack cycles", supported=True)
        if "n4" in self.parts:
            start = self.refill("n4.refill")
            self.teleport(-30.0, "n4.arrive")
            self.run(1.0)
            self.part("n4.dive", "begin")
            dive, k, last, early, prev = (30.0 if self.quick else 60.0), 0, None, False, None
            while True:
                off = k * 5.0
                if off > dive + 1e-6:
                    break
                if k:
                    self.run(5.0)
                prev, last = last, self.o2dump("n4.dive.o2.%g" % off)
                k += 1
                nxt = last - (prev - last) if prev is not None else last
                if min(last, nxt) <= 0.08:
                    early = True
                    break
            self.part("n4.dive", "end")
            self.part("n4.surface", "begin")
            self.run_script("n4.surface", (("n4.up", 30.0 / 5.0 + 20.0, (0, 1, 0), False),
                                           ("n4.rel", 1.0, (0, 0, 0), False)), dumps=(5.0, 10.0, 15.0, 20.0, 25.0),
                            o2=True)
            self.o2dump("n4.surface.o2.end")
            self.part("n4.surface", "end")
            self.ev("n4", depthY=-30.0, diveS=dive, early=early, lastDiveO2=last, startO2=start)
        for p in ("SpaceEngineers.cfg", "Storage/WaterClientSettings.xml"):
            self.ev("file_hash", path="C:/x/" + p, when="after", sha256="ab" * 32)
        self.ev("end", rc=None)
        return self

    def write(self, folder):
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, "s2b-session.jsonl"), "w", encoding="utf-8") as f:
            for r in self.session:
                f.write(json.dumps(r) + "\n")
        with open(os.path.join(folder, "se-20261016-120000.jsonl"), "w", encoding="utf-8") as f:
            for r in self.se:
                f.write(json.dumps(r) + "\n")
        return folder

# =============================================================================================== selftest
def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-80s %s %s" % (label, "PASS" if cond else "FAIL", str(detail)[:160]))

    def rows_of(name, port=None, **kw):
        set_model(port=port or SelftestPlant())
        d = SynthSE(**kw).session_run().write(os.path.join(root, name))
        return evaluate(d)

    def find(rows, cid, text, part=None):
        return [r for r in rows if r["id"] == cid and text in r["what"] and (part is None or r["part"] == part)]

    def bad(rows):
        return [r for r in rows if r.get("required", True) and r["status"] != "PASS"]

    def why(rows):
        return "; ".join("%s %s %s | %s" % (x["id"], x["status"], x["measured"], x["what"][:40]) for x in rows)

    root = tempfile.mkdtemp(prefix="de_s2b_")
    try:
        # ---------------------------------------------------------------- the plant reads the numbers as intended
        p = SelftestPlant()
        damp = 1.0 - p.DRAG * p.FIXED_DT

        def hold(move, sprint=False, look=(0, 0, 1), n=600):
            v = (0.0, 0.0, 0.0)
            seen = set()
            for k in range(1, n + 1):
                v = p.step(v, {"move": move, "sprint": sprint, "look": look, "dt": TICK_S, "yHead": -50.0,
                               "inWater": True, "tickNo": k})
                seen.add(round(_norm(v), 9))
            return v, seen
        v, seen = hold((0, 0, 1))
        check("plant (%s): forward plateau = max x (1 - drag x 0.1 s) = %.3f m/s" % (p.SOURCE, p.FWD * damp),
              abs(_norm(v) - p.FWD * damp) < 1e-6, "%.4f" % _norm(v))
        v6, _s = hold((0, 0, 1), n=5)
        check("plant: one motor step every %d SE ticks (velocity held between them)" % p.STEP_TICKS,
              _norm(v6) == 0.0 and len(seen) < 600 / p.STEP_TICKS + 2, "%d distinct speeds in 600 ticks" % len(seen))
        v2, _s = hold((0, 0, 1), sprint=True)
        check("plant: no sprint underwater (UnderwaterMotor.IsSprinting false)", abs(_norm(v2) - _norm(v)) < 1e-9)
        vy, _s = hold((0, 1, 0))
        check("plant: vertical plateau = vertical max x damping", abs(vy[1] - p.VERT * damp) < 1e-6, "%.4f" % vy[1])
        vd = p.step((0.0, 0.0, 0.0), {"move": (0, 0, 1), "look": (0, -math.sin(0.5), math.cos(0.5)), "dt": TICK_S,
                                      "yHead": -50.0, "tickNo": 6})
        check("plant: forward follows the look pitch (free 3-D)", vd[1] < 0 and abs(math.atan2(-vd[1], vd[2]) - 0.5) < 1e-6)
        check("plant: O2 1 unit/s at 30 m, 1.5 at 150 m, 2 at 250 m; capacity 45; refill 30/s",
              p.o2(30)["rate"] == 1.0 and p.o2(150)["rate"] == 1.5 and p.o2(250)["rate"] == 2.0 and
              p.o2(30)["capacity"] == 45.0 and p.o2_refill() == 30.0)

        # ---------------------------------------------------------------- green: the stand-in follows the model
        r = rows_of("green")
        b = bad(r)
        check("green stand-in (B rows, the SE controller's layout): every required row PASS", not b, why(b))
        check("  N1: 6 plateaus, 2 vertical speeds, 2 t90, 4 stop distances, 1 t10, sprint factor (16 required)",
              len([x for x in r if x["id"] == "S2b.N1" and x.get("required", True)]) == 16,
              len([x for x in r if x["id"] == "S2b.N1" and x.get("required", True)]))
        seg_info = find(r, "S2b.N1", "rows per segment")[0]
        check("  N1 segments by the script step index of B rows", "script step index" in seg_info["what"],
              seg_info["what"][:120])
        n3 = find(r, "S2b.N3", "one exit and one entry per cycle")[0]
        check("  N3: 4/4 cycles with one exit and one entry", n3["status"] == "PASS" and n3["measured"].startswith(
            "4/4"), n3["measured"])
        j = find(r, "S2b.N2", "jetpack back with the head out")[0]
        check("  N2: the jetpack request at the surface is granted (jetpack exit)", j["status"] == "PASS", j["measured"])
        rst = find(r, "S2b.N3", "restored on exit")[0]
        check("  N3 INFO restore from the DE.Swim lines", rst["status"] == "INFO", rst["measured"])
        o2 = find(r, "S2b.N4", "O2 underwater")[0]
        check("  N4: O2 rate on the rows = 1/45 per s", o2["status"] == "PASS" and "rows" in o2["what"], o2["measured"])
        check("  verdict PASS", C.verdict(r))

        # ---------------------------------------------------------------- seeded failures (ROADMAP S2b.M1-M4)
        r = rows_of("m1", mut=("M1",))
        n1 = [x for x in r if x["id"] == "S2b.N1" and x.get("required", True)]
        red = [x for x in n1 if x["status"] == "FAIL"]
        check("S2b.M1 numbers x1.5: N1 red (plateaus 1.5x the replay)", red and find(
            r, "S2b.N1", "n1.fwd plateau")[0]["status"] == "FAIL", "%d/%d N1 rows red" % (len(red), len(n1)))
        r = rows_of("m2", mut=("M2",))
        j = find(r, "S2b.N2", "jetpack off with the head under water")[0]
        check("S2b.M2 jetpack left on: N2 red (the request at -18 m stays on)", j["status"] == "FAIL",
              "%s (%s)" % (j["measured"], j["threshold"]))
        r = rows_of("m3", mut=("M3",))
        bn = find(r, "S2b.N3", "surface hold")[0]
        cy = find(r, "S2b.N3", "one exit and one entry per cycle")[0]
        check("S2b.M3 no hysteresis: N3 red (bounce at the surface hold and in the crossings)",
              bn["status"] == "FAIL" and cy["status"] == "FAIL", "%s | %s" % (bn["measured"], cy["measured"]))
        r = rows_of("m4", mut=("M4",))
        o = find(r, "S2b.N4", "O2 underwater")[0]
        f = find(r, "S2b.N4", "refill at the surface")[0]
        check("S2b.M4 O2 rule off: N4 red (stock drain, no refill)", o["status"] == "FAIL" and f["status"] == "FAIL",
              "%s | %s" % (o["measured"], f["measured"]))

        # ---------------------------------------------------------------- layouts and fallbacks
        r = rows_of("layout-A", layout="A")
        b = bad(r)
        check("Wire SwimTrace rows (A: exact bits, O2 in units, o_evt transitions): every required row PASS",
              not b, why(b))
        r = rows_of("layout-AP", layout="AP", labels=True)
        b = bad(r)
        check("plugin rows (AP: the Wire row + the SE keys, Fly semantics, labels): every required row PASS", not b,
              why(b))
        n3 = find(r, "S2b.N3", "one exit and one entry per cycle")[0]
        check("  AP N3: 4/4 crossings, the exit at +0.8 m under the jetpack (Fly)", n3["status"] == "PASS" and
              n3["measured"].startswith("4/4"), n3["measured"])
        dp = find(r, "S2b.N2", "dampeners off in the water")[0]
        check("  AP N2: dampeners read from the plugin's damp key", dp["status"] == "PASS" and "no rows" not in str(
            dp["measured"]), dp["measured"])
        r = rows_of("layout-AP-m1", layout="AP", labels=True, mut=("M1",))
        check("  AP S2b.M1: N1 red", find(r, "S2b.N1", "n1.fwd plateau")[0]["status"] == "FAIL")
        r = rows_of("layout-AP-m2", layout="AP", labels=True, mut=("M2",))
        j = find(r, "S2b.N2", "jetpack off with the head under water")[0]
        check("  AP S2b.M2: N2 red", j["status"] == "FAIL", j["measured"])
        r = rows_of("layout-AP-m3", layout="AP", labels=True, mut=("M3",))
        bn = find(r, "S2b.N3", "surface hold")[0]
        check("  AP S2b.M3: N3 surface hold red", bn["status"] == "FAIL", bn["measured"])
        r = rows_of("layout-AP-m4", layout="AP", labels=True, mut=("M4",))
        o = find(r, "S2b.N4", "O2 underwater")[0]
        check("  AP S2b.M4: N4 rate red", o["status"] == "FAIL", o["measured"])
        r = rows_of("layout-G", layout="G", text_lines=False, labels=True)
        b = bad(r)
        check("generic rows (G: aliases, labels, percent O2, transitions from inW): every required row PASS", not b,
              why(b))
        for name, pp in (("plant", SelftestPlant()), ("swim_replay.py", None)):
            if pp is None:
                set_model(mode="swim_replay")
                pp = load_model()
                if pp is None:
                    continue
            r = rows_of("offphase-" + name.replace(".", ""), port=pp, settle=3.05)
            b = [x for x in r if x["id"] == "S2b.N1" and x.get("required", True) and x["status"] != "PASS"]
            info = find(r, "S2b.N1", "rows per segment")[0]
            check("scripts starting 3 ticks off the motor step: the phase pre-roll keeps %s in phase" % name,
                  not b and "pre-roll" in info["what"], why(b) or info["what"][-70:])
        r = rows_of("disabled", disabled=True)
        d = find(r, "S2b.ALL", "FEATURE DISABLED")[0]
        check("a `DE.Swim FEATURE DISABLED` line: FAIL", d["status"] == "FAIL" and not C.verdict(r), d["measured"])

        # ---------------------------------------------------------------- review fixes
        r = rows_of("review-green", layout="AP", labels=True)
        b = bad(r)
        check("review: green AP session with the suit prep, the precheck and the restore row: every required row PASS",
              not b, why(b))
        x = find(r, "S2b.N3", "SE's state after each exit")[0]
        q = find(r, "S2b.N2", "reached SE's jetpack")[0]
        check("  restore row from SE's own states (rows 3 ticks after each exit) PASS; the n2 request reached SE",
              x["status"] == "PASS" and x.get("required", True) and q["status"] == "PASS",
              "%s | %s" % (x["measured"], q["measured"]))
        r = rows_of("review-noh2", layout="AP", labels=True, no_h2=True)
        j = find(r, "S2b.N2", "jetpack back with the head out")[0]
        q = find(r, "S2b.N2", "reached SE's jetpack")[0]
        p_ = find(r, "S2b.ALL", "session preconditions")[0]
        check("finding 4/6: no hydrogen: n2j red, the n2 request row red, the precheck red", j["status"] == "FAIL" and
              q["status"] == "FAIL" and p_["status"] == "FAIL", "%s | %s | %s" % (j["measured"], q["measured"],
                                                                                   p_["measured"]))
        r = rows_of("review-restore", layout="AP", labels=True, bad_restore=True)
        x = find(r, "S2b.N3", "SE's state after each exit")[0]
        check("finding 6: dampeners left off at an exit: the required restore row red", x["status"] == "FAIL",
              x["measured"])
        r = rows_of("review-bottle", layout="AP", labels=True, bottle=True)
        o = find(r, "S2b.N4", "O2 underwater")[0]
        check("finding 5: an SE bottle refill in n4.dive is excluded from the rate: PASS", o["status"] == "PASS" and
              "upward jump" in o["what"], "%s | %s" % (o["measured"], o["what"][-60:]))
        r = rows_of("review-error", swim_error=True)
        e = find(r, "S2b.ALL", "DE.Swim error")[0]
        check("finding 11: a `DE.Swim error` line: FAIL", e["status"] == "FAIL" and not C.verdict(r), e["measured"])
        r = rows_of("review-precheck", precheck_fail=True)
        p_ = find(r, "S2b.ALL", "session preconditions")[0]
        check("finding 4: preconditions not met: FAIL", p_["status"] == "FAIL" and not C.verdict(r), p_["measured"])
        sy = SynthSE().session_run()
        sy.se = []
        dd = sy.write(os.path.join(root, "nolog"))
        set_model(port=SelftestPlant())
        r = evaluate(dd)
        okm = [x for x in r if x.get("required", True) and x["status"] == "PASS" and (
            x["id"] in ("S2b.N1", "S2b.N3", "S2b.ALL") or "never above" in x["what"]) and
               "session preconditions" not in x["what"]]      # the launcher's own event, not SE's log
        check("no SE log: N1, N3, the surface row and ALL never PASS", not okm, why(okm))
        sy = SynthSE().session_run()
        for x in sy.se:
            x.pop("o2", None)
        dd = sy.write(os.path.join(root, "dumps"))
        set_model(port=SelftestPlant())
        o = find(evaluate(dd), "S2b.N4", "O2 underwater")[0]
        check("N4 from the 5 s o2=dump replies only: PASS", o["status"] == "PASS" and "dumps" in o["what"],
              "%s %s" % (o["measured"], o["threshold"]))
        set_model(mode="swim_replay")
        _MODEL.update(status="swim_replay.py not importable (selftest)", port=None)
        r = evaluate(os.path.join(root, "green"))
        n1 = [x for x in r if x["id"] == "S2b.N1" and x.get("required", True)]
        check("no model: N1 MISSING (never PASS)", n1 and all(x["status"] == "MISSING" for x in n1),
              [x["measured"] for x in n1][:3])

        # ---------------------------------------------------------------- the port adapter on plausible model shapes
        mdir = os.path.join(root, "models")
        os.makedirs(mdir)
        with open(os.path.join(mdir, "fake_port_a.py"), "w") as fh:
            fh.write("class P:\n    name = 'a'\n    def step(self, v, tick):\n        return [v[0] + 1.0, v[1], v[2]]\n"
                     "    def o2(self, d):\n        return {'rate': 1.0, 'capacity': 45.0}\n"
                     "def analyzer_port():\n    return P()\n")
        with open(os.path.join(mdir, "fake_port_b.py"), "w") as fh:
            fh.write("O2_CAPACITY = 45.0\nO2_RATE = 1.0\nSURFACE_HEAD_MAX = 0.52\n"
                     "def step(velocity, move_dir, look, dt, sprint=False, y=-10.0, in_water=True):\n"
                     "    return {'vel': (velocity[0], velocity[1], velocity[2] + move_dir[2] * dt)}\n")
        with open(os.path.join(mdir, "fake_port_c.py"), "w") as fh:
            fh.write("class SwimModel:\n    def step(self, state, tick):\n        return state\n")
        sys.path.insert(0, mdir)
        try:
            pa = adapt(importlib.import_module("fake_port_a"))
            check("adapter: analyzer_port()", pa.step((0, 0, 0), {}) == (1.0, 0.0, 0.0) and pa.o2(30)["rate"] == 1.0)
            pb = adapt(importlib.import_module("fake_port_b"))
            got = pb.step((0.0, 0.0, 1.0), {"move": (0, 0, 1), "dt": 0.5})
            check("adapter: step(velocity, move_dir, look, dt, ...) by names + O2 constants",
                  got == (0.0, 0.0, 1.5) and pb.o2(30)["capacity"] == 45.0 and pb.surface_head_max() == 0.52, got)
            try:
                adapt(importlib.import_module("fake_port_c"))
                check("adapter: an unmappable step(state, tick) is refused", False)
            except TypeError as e:
                check("adapter: an unmappable step(state, tick) is refused (N1 then MISSING)", True, e)
        finally:
            sys.path.remove(mdir)

        # ---------------------------------------------------------------- tests/s2b/swim_replay.py itself
        set_model(mode="swim_replay")
        port = load_model()
        if port is None:
            print("SELFTEST %-80s SKIP %s" % ("swim_replay.py through the port", _MODEL["status"][:120]))
        else:
            check("swim_replay.py imported (%s: %s)" % (type(port).__name__, port.name),
                  isinstance(port, (SwimReplayPort, _WrappedPort)) and port.module is not None)
            for lay in ("B", "A", "AP"):
                set_model(port=port)
                d = SynthSE(layout=lay).session_run().write(os.path.join(root, "real-model-" + lay))
                r = evaluate(d)
                n1 = [x for x in r if x["id"] == "S2b.N1" and x.get("required", True)]
                b = [x for x in n1 if x["status"] != "PASS"]
                info = find(r, "S2b.N1", "rows per segment")[0]
                check("swim_replay.py replays the green %s stand-in: every N1 row PASS (%s)" % (
                    lay, "openloop_series" if lay in ("A", "AP") else "State/step in phase"), n1 and not b,
                      why(b) or info["what"][-60:])
                o = find(r, "S2b.N4", "O2 underwater")[0]
                check("  swim_replay.py O2 rule: N4 rate PASS on the %s stand-in" % lay, o["status"] == "PASS",
                      o["measured"])
            set_model(port=port)
            r = evaluate(SynthSE(mut=("M1",)).session_run().write(os.path.join(root, "real-model-m1")))
            red = [x for x in r if x["id"] == "S2b.N1" and x.get("required", True) and x["status"] == "FAIL"]
            check("swim_replay.py: S2b.M1 red", red, "%d rows red" % len(red))
            # swim_replay.py's own perfect-SE session (its Sim, A rows) through this analyser, and with its defects
            M = importlib.import_module("swim_replay")
            for defect, want in ((None, True), ("slow", False), ("gravity", False)):
                w = M.scenario("n1", 0, defect)
                body = []
                for k, (sec, x, y, z, spr) in enumerate(M.N1_SCRIPT):
                    body.append({"label": "seg%d" % k, "seconds": sec, "forward": z, "strafe": x, "vertical": y,
                                 "sprint": bool(spr), "move": [x, y, z]})
                d = os.path.join(root, "sim-%s" % defect)
                os.makedirs(d)
                t0 = w.rows[30]["tick"]
                with open(os.path.join(d, "s2b-session.jsonl"), "w") as fh:
                    for rec in ({"ev": "start", "parts": ["n1"], "qpcFrequency": 10_000_000},
                                {"ev": "part", "name": "n1a", "phase": "begin", "tick": t0},
                                {"ev": "swim_script", "label": "n1a", "segments": body},
                                {"ev": "part", "name": "n1a", "phase": "end", "tick": w.rows[-1]["tick"]}):
                        fh.write(json.dumps(rec) + "\n")
                with open(os.path.join(d, "se-x.jsonl"), "w") as fh:
                    for rec in w.rows:
                        fh.write(json.dumps(rec) + "\n")
                set_model(port=port)
                sess = Session(d)
                sr, how = segments_of(sess, "n1a")
                rep, _eyes, rhow = replay(port, sess, sr)
                diffs = []
                prev_l = prev_m = None
                for s, rs in sr:
                    if not rs:
                        continue
                    t0s = rs[0]["tick"] - 1
                    ml = metrics(s, [((x["tick"] - t0s) / TICK_HZ, measured(x)) for x in rs if measured(x)], prev_l)
                    mm = metrics(s, [((x["tick"] - t0s) / TICK_HZ, rep[x["tick"]]) for x in rs if x["tick"] in rep],
                                 prev_m)
                    key = "plateau" if ml.get("moving") else "stopDist"
                    if ml.get(key) is not None and mm.get(key) is not None:
                        diffs.append(abs(ml[key] - mm[key]) <= max(REL_TOL * abs(mm[key]), FLOOR_SPEED))
                    prev_l = _norm(measured(rs[-1])) if measured(rs[-1]) else None
                    prev_m = _norm(rep[rs[-1]["tick"]]) if rs[-1]["tick"] in rep else None
                n1ok = bool(diffs) and all(diffs)
                theirs = M.n1_report(M.read_rows([os.path.join(d, "se-x.jsonl")]))["pass"]
                check("swim_replay.py Sim n1%s: this analyser %s, swim_replay.py n1 %s (agree)" % (
                    " defect " + defect if defect else "", "PASS" if n1ok else "red", "PASS" if theirs else "red"),
                      n1ok == want and theirs == want, "%s; %s" % (how, rhow))
    finally:
        set_model(mode="swim_replay")
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("ANALYZE_SWIM SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S2b swimming oracle (launcher.py s2b session folders)")
    ap.add_argument("dir", nargs="?")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--model", choices=("swim_replay", "plant"), default="swim_replay",
                    help="plant: replay with this file's selftest plant (diagnostics; rows not required)")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.dir:
        ap.error("a session folder is required")
    set_model(mode=a.model)
    rows = evaluate(a.dir)
    ok = C.write_results(a.dir, "s2b-swim", rows, {"model": model_status()})
    print(json.dumps(rows, default=str) if a.json else C.table(rows))
    print("ANALYZE_SWIM %s (%d rows; model: %s)" % ("PASS" if ok else "FAIL", len(rows), model_status()))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
