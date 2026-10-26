"""S2b swimming: independent replay of Subnautica's swim model and the S2b.N1 oracle (stdlib only).

The SE plugin runs the swim model (src/DeepEngineers.Wire/SwimModel.cs) once per SE tick while the character is on the
dive planet and may log one "swim" row per step (SwimTrace). This script re-implements the model from the numbers of
tests/s2b/sn_reference.py and the rules of CONTRACT §2.10 (its own code, not a translation of the C#), and checks:

 replay every row: the step from the row's logged state and inputs gives the logged outputs (velocity, oxygen within
 --tol, default 1e-6; mode, flags, events exactly), and the state after it equals the next row's logged
 state when the rows are contiguous (n, n + 1). Divergence = the plugin did not run the documented model (a
 seeded failure such as S2b.M1 turns this red unless --honour-mutant).
 n1 ROADMAP S2b.N1. The rows are cut into segments of constant move input while the model controls the
 velocity. Measured (SE's own motion: the plugin's vSe key = SE's velocity right after its physics step when
 the row carries it, else the velocity SE reports on the next tick; the logged eye positions)
 versus the open-loop model (this script's model driven by the same inputs, its own velocity and position):
 drive segments: steady speed (mean over the last 0.5 s), time to 90 % of it, vertical speed; release
 segments: stop distance and stop time (speed below 0.05 m/s); sprint factor (sprint / same input without).
 Each within 5 % (floors: 0.05 m/s, 2 ticks, 0.05 m). The canonical N1 script has a forward segment of at
 least 8 s, a release, strafe, backward, up, down and sprint segments; a missing one is MISSING (red).
 "curve" = the largest per-tick difference between the logged imposed velocity and the open-loop model
 (INFO; judged with --curve-tol: the C# cross-check uses 1e-6).
 info transitions (latch on/off ticks), the highest eye while swimming without the jetpack, the oxygen rate per
 depth class: inputs for S2b.N2-N4 (judged by their own analyzers).

Row format "swim" version 1 (SwimTrace): doubles as exact IEEE-754 bits (16 hex digits in quotes), booleans 0/1;
the readable keys (st inW y yHead spd o2 = suit fill 0..1, o2u = Subnautica units, move vel pos look jetOk, tr on a
transition, seg = script label) are for people and tests/s2b/analyze_swim.py; this replay reads the bits only.
Rows must be contiguous (every tick) for S2b.N1: a gap ends a segment.
 {"ev":"swim","v","n":step,"tick":T,["seg":label,]"md":mode,"st":name,"inW"|1,"y":eye,"yHead":eye,"spd":speed,
 "o2":fraction,"o2u":oxygen,"move":[..],"vel":[..],"pos":[..],"look":[..],"jetOk"|1,["tr":"enter"|"exit",]
 inputs mx my mz fx fy fz rx ry rz vx vy vz px py pz ox fin (bits; fin = the S5a fins modifier in m/s, 0 / 1.9 /
 3.2, absent in rows written before S5a = 0) spr tool gnd jet mut
 state s_lat s_jl s_jd s_ctl s_ph s_hx s_hy s_hz (bits) s_md s_eu
 outputs o_md o_ctl o_vx o_vy o_vz (bits) o_st o_max o_cap (bits) o_ja o_or o_do2 (bits) o_dc o_evt o_chg}

 python -I tests/s2b/swim_replay.py replay SE.jsonl [...] [--honour-mutant] [--tol 1e-6] [--json OUT]
 python -I tests/s2b/swim_replay.py n1 SE.jsonl [...] [--from-tick A --to-tick B] [--curve-tol 1e-6] [--json OUT]
 python -I tests/s2b/swim_replay.py info SE.jsonl [...]
 python -I tests/s2b/swim_replay.py gen OUT.jsonl --scenario NAME [--mutant M1|M2|M3|M4] [--defect gravity|slow|fins]
 [--fins off|basic|ultra] # S5a: the fins modifier of the stand-in (default off)
 python -I tests/s2b/swim_replay.py script n1 # the canonical S2b.N1 input script (for de.swim script=)
 python -I tests/s2b/swim_replay.py --selftest
The S2b analyser (tests/s2b/analyze_swim.py) imports this file and calls analyzer_port (its documented contract).
Exit code 0 = PASS, 1 = FAIL (a red check), 2 = usage or input error.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import struct
import sys
import tempfile

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import sn_reference as R  # noqa: E402

ROW_VERSION = 1
WALK, SWIM, SURFACE, FLY = 0, 1, 2, 3
MODE_NAMES = {WALK: "Walk", SWIM: "Swim", SURFACE: "Surface", FLY: "Fly"}
EV_ENTERED, EV_EXITED, EV_JET_GRANTED, EV_JET_REVOKED, EV_BREATH, EV_RESUMED = 1, 2, 4, 8, 16, 32
EV_MOTOR_SWITCH = 64          # the eye crossed 0 with the swim latch on: Subnautica's Dive<->Run motor switch
MUTANTS = {"M1": R.MUT_M1, "M2": R.MUT_M2, "M3": R.MUT_M3, "M4": R.MUT_M4}
FINS = dict(R.FINS_MODIFIERS)        # S5a: "off" 0, "basic" 1.9, "ultra" 3.2 m/s (the SE controller's default is ultra)

N1_TOL = 0.05                 # 5 % (ROADMAP S2b.N1)
N1_SPEED_FLOOR = 0.05         # m/s
N1_TIME_FLOOR = 2.0 / 60.0    # s: SE reports the velocity of a tick on the next one
N1_DIST_FLOOR = 0.05          # m
STOP_SPEED = 0.05             # m/s: "stopped"
STEADY_WINDOW_TICKS = 30      # last 0.5 s of a drive segment
MIN_SEGMENT_S = 2.0

# The canonical S2b.N1 script (SE alone, at -50 m, looking level): (seconds, strafe, vertical, forward, sprint)
N1_SCRIPT = [
    (10.0, 0, 0, 1, 0), (4.0, 0, 0, 0, 0),     # forward 10 s, release
    (6.0, 1, 0, 0, 0), (4.0, 0, 0, 0, 0),      # strafe right
    (6.0, 0, 0, -1, 0), (4.0, 0, 0, 0, 0),     # backward
    (6.0, 0, 1, 0, 0), (4.0, 0, 0, 0, 0),      # up
    (6.0, 0, -1, 0, 0), (4.0, 0, 0, 0, 0),     # down
    (6.0, 0, 0, 1, 1), (4.0, 0, 0, 0, 0),      # forward with sprint
]

# ------------------------------------------------------------------------------------------------ helpers
def bits_of(x: float) -> str:
    return "%016x" % struct.unpack("<Q", struct.pack("<d", x))[0]

def from_bits(h: str) -> float:
    return struct.unpack("<d", struct.pack("<Q", int(h, 16)))[0]

def vlen(v) -> float:
    return math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])

def fixed(x: float, d: int):
    if x is None or math.isnan(x) or math.isinf(x):
        return None
    q = math.floor(abs(x) * 10 ** d + 0.5)
    return float(("-" if x < 0 and q != 0 else "") + "%d.%0*d" % (q // 10 ** d, d, q % 10 ** d))

# ------------------------------------------------------------------------------------------------ the model
class State:
    __slots__ = ("steps", "lat", "jl", "jd", "ctl", "ph", "h", "md", "eu")

    def __init__(self):
        self.steps = 0
        self.lat = False
        self.jl = False
        self.jd = False
        self.ctl = False
        self.ph = 0
        self.h = (0.0, 0.0, 0.0)
        self.md = WALK
        self.eu = False           # the eye was under the surface on the last tick (motor mode Dive)

    def copy(self) -> "State":
        c = State()
        for k in State.__slots__:
            setattr(c, k, getattr(self, k))
        return c

    @staticmethod
    def from_row(r: dict) -> "State":
        s = State()
        s.steps = r["n"]
        s.lat = bool(r["s_lat"])
        s.jl = bool(r["s_jl"])
        s.jd = bool(r["s_jd"])
        s.ctl = bool(r["s_ctl"])
        s.ph = int(r["s_ph"])
        s.h = (from_bits(r["s_hx"]), from_bits(r["s_hy"]), from_bits(r["s_hz"]))
        s.md = int(r["s_md"])
        # rows without s_eu (written before the motor-switch rule): this row's own eye class, i.e. no switch here
        s.eu = bool(int(r["s_eu"])) if "s_eu" in r else from_bits(r["py"]) < R.UNDERWATER_EYE_Y
        return s

    def to_row(self) -> dict:
        return {"s_lat": int(self.lat), "s_jl": int(self.jl), "s_jd": int(self.jd), "s_ctl": int(self.ctl),
                "s_ph": self.ph, "s_hx": bits_of(self.h[0]), "s_hy": bits_of(self.h[1]), "s_hz": bits_of(self.h[2]),
                "s_md": self.md, "s_eu": int(self.eu)}

def unity_normalize(x: float, y: float, z: float):
    n = math.sqrt(x * x + y * y + z * z)
    if n > R.NORMALIZE_EPS:
        return x / n, y / n, z / n
    return 0.0, 0.0, 0.0

def max_speed(hx: float, hz: float, eye: float, tool: bool, m1: bool, fins: float = 0.0):
    """Subnautica's max speed for this input (UnderwaterMotor max-speed selection + AlterMaxSpeed without a tank or suit;
 the S5a fins modifier is added where adds it: after the direction maxima, before the tool and the surface
 factor, so the ratio a / a_before (and with it the acceleration) carries it)."""
    sc = R.MUTANT_M1_SCALE if m1 else 1.0
    if eye < R.UNDERWATER_EYE_Y:
        f, b, s = R.DIVE_FORWARD_MAX, R.DIVE_BACKWARD_MAX, R.DIVE_STRAFE_MAX
    else:
        f, b, s = R.RUN_FORWARD_MAX, R.RUN_BACKWARD_MAX, R.RUN_STRAFE_MAX
    a = 0.0
    if hz > 0.0:
        a = f * sc
    elif hz < 0.0:
        a = b * sc
    if hx != 0.0:
        a = max(a, s * sc)
    a = max(a, R.DIVE_VERTICAL_MAX * sc)
    base = a
    if fins > 0.0:
        a = a + fins
    if tool:
        a = a - R.HELD_TOOL_PENALTY
    if eye > R.UNDERWATER_EYE_Y:
        a = a * R.SURFACE_SPEED_MULTIPLIER
    return a, a / base

def motor_step(v0, move, tool: bool, contact: bool, fwd, right, eye: float, m1: bool, fins: float = 0.0):
    """One 0.1 s Subnautica swim step from v0; returns (velocity kept for the step, max speed, cap). contact: the diver
 touches something (Subnautica's grounded: groundAcceleration 45 instead of the water's 20); fins: the S5a modifier."""
    mx, my, mz = move
    num = min(1.0, math.sqrt(mx * mx + my * my + mz * mz))
    hl = math.sqrt(mx * mx + mz * mz)
    hx, hz = (mx / hl, mz / hl) if hl > R.NORMALIZE_EPS else (0.0, 0.0)
    a, ratio = max_speed(hx, hz, eye, tool, m1, fins)
    cap = max(vlen(v0), a)
    # camera rotation applied to the horizontal input: right (roll removed) and forward (with pitch)
    rl = math.sqrt(right[0] * right[0] + right[2] * right[2])
    rx, rz = (right[0] / rl, right[2] / rl) if rl > R.NORMALIZE_EPS else (0.0, 0.0)
    wx = hx * rx + hz * fwd[0]
    wy = hz * fwd[1]
    wz = hx * rz + hz * fwd[2]
    dx, dy, dz = unity_normalize(wx, wy + my, wz)
    if contact:
        accel = R.GROUND_ACCELERATION * (R.MUTANT_M1_SCALE if m1 else 1.0)
    else:
        accel = R.WATER_ACCELERATION * (R.MUTANT_M1_SCALE if m1 else 1.0) * R.PLAYER_SPEED_MODIFIER
    dv = num * accel * R.SN_FIXED_DT
    if ratio > 1.0:
        dv = dv * ratio
    if dv > 0.0:
        v1 = [v0[0] + dx * dv, v0[1] + dy * dv, v0[2] + dz * dv]
        n = vlen(v1)
        if n > cap:
            v1 = [v1[0] / n * cap, v1[1] / n * cap, v1[2] / n * cap]
        if eye >= R.SURFACE_BAND_BOTTOM_Y and not my < 0.0 and not wy < R.LOOK_DOWN_Y:
            t = (R.SURFACE_TOP_Y - eye) / (R.SURFACE_TOP_Y - R.SURFACE_BAND_BOTTOM_Y)
            t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
            v1[1] = v1[1] * math.pow(t, R.SURFACE_EXPONENT)
    else:
        v1 = list(v0)
    k = max(0.0, 1.0 - R.SN_FIXED_DT * R.SWIM_DRAG)
    return (v1[0] * k, v1[1] * k, v1[2] * k), a, cap

def depth_class(eye: float) -> int:
    d = max(0.0, -eye)
    if d > R.CRUSH_DEPTH_CLASS:
        return R.DC_CRUSH
    if d > R.UNSAFE_DEPTH:
        return R.DC_UNSAFE
    if d > R.SURFACE_DEPTH:
        return R.DC_SAFE
    return R.DC_SURFACE

def step(s: State, i: dict) -> dict:
    """Advance s by one SE tick with inputs i; returns the outputs (o_* keys, floats as floats)."""
    mut = i["mut"]
    single = bool(mut & R.MUT_M3)
    eye = i["p"][1]
    was = s.lat
    ev = 0
    if single:
        lat = eye < R.MUTANT_M3_EYE_Y
    elif not was:
        lat = eye < R.SWIM_ENTER_EYE_Y
    else:
        grounded = bool(i["gnd"]) and eye > R.SWIM_EXIT_GROUNDED_EYE_Y
        lat = eye < (R.SWIM_EXIT_GROUNDED_EYE_Y if grounded else R.SWIM_EXIT_EYE_Y)
    if lat and not was:
        ev |= EV_ENTERED
    if was and not lat:
        ev |= EV_EXITED
    under = eye < R.UNDERWATER_EYE_Y
    if lat and was and under != s.eu:
        ev |= EV_MOTOR_SWITCH          # Dive <-> Run while the swim motor stays on: it is enabled again
    if not lat:
        jl = False
    elif single or not was:
        jl = eye >= R.JETPACK_ON_EYE_Y
    else:
        jl = eye >= (R.JETPACK_OFF_EYE_Y if s.jl else R.JETPACK_ON_EYE_Y)
    allowed = (not lat) or jl or bool(mut & R.MUT_M2)
    if allowed and s.jd:
        ev |= EV_JET_GRANTED
    if not allowed and not s.jd:
        ev |= EV_JET_REVOKED
    if not lat:
        md = WALK
    elif i["jet"] and allowed:
        md = FLY
    elif eye < R.UNDERWATER_EYE_Y:
        md = SWIM
    else:
        md = SURFACE
    ctl = md in (SWIM, SURFACE)
    # (SwimYield.Of): a ladder, or a grid deck under the feet with the jetpack off and no up input, hands the tick to SE
    yld = 0
    if lat and i.get("lad"):
        yld = 1
    elif lat and md == SWIM and i.get("grd") and i["gnd"] and not i["jet"] and i["my"] <= 0:
        yld = 2
    if yld:
        ctl = False
    sn_step, mx_speed, cap = False, 0.0, 0.0
    if ctl:
        resume = not s.ctl
        if resume:
            ev |= EV_RESUMED
        ph = 0 if resume else s.ph
        enabled_again = bool(ev & (EV_ENTERED | EV_MOTOR_SWITCH))   # UnderwaterMotor.SetEnabled(true): vy x 0.5
        if ph == 0:
            v0 = i["v"]
            if enabled_again:
                v0 = (v0[0], v0[1] * R.ENTRY_VERTICAL_FACTOR, v0[2])
            s.h, mx_speed, cap = motor_step(v0, (i["mx"], i["my"], i["mz"]), bool(i["tool"]), bool(i["gnd"]), i["f"],
                                            i["r"], eye, bool(mut & R.MUT_M1), float(i.get("fin", 0.0)))
            sn_step = True
        elif enabled_again:
            s.h = (s.h[0], s.h[1] * R.ENTRY_VERTICAL_FACTOR, s.h[2])
        vel = s.h
        s.ph = (ph + 1) % R.SN_STEP_TICKS
    else:
        vel = i["v"]
        s.h = (0.0, 0.0, 0.0)
        s.ph = 0
    dc = depth_class(eye)
    rule = not (mut & R.MUT_M4) and (lat or eye < R.UNDERWATER_EYE_Y)
    do2 = 0.0
    if rule:
        o2 = i["o2"]
        after = o2
        if eye < R.UNDERWATER_EYE_Y and dc != R.DC_SURFACE:
            period = R.breath_ticks(dc)
            if i["tick"] % period == 0:
                amount = R.BREATH_PERIOD_S[dc] * R.BREATH_FACTOR[dc]
                after = max(0.0, after - min(amount, after))
                ev |= EV_BREATH
        if eye > R.OXYGEN_REFILL_EYE_Y:
            after += min(R.OXYGEN_REFILL_PER_S * R.SE_TICK_DT, R.OXYGEN_CAPACITY - after)
        do2 = after - o2
    out = {"o_md": md, "o_yld": yld, "o_ctl": int(ctl), "o_v": vel, "o_st": int(sn_step), "o_max": mx_speed, "o_cap": cap,
           "o_ja": int(allowed), "o_or": int(rule), "o_do2": do2, "o_dc": dc, "o_evt": ev, "o_chg": int(md != s.md)}
    s.steps += 1
    s.lat, s.jl, s.jd, s.ctl, s.md, s.eu = lat, jl, not allowed, ctl, md, under
    return out

# ------------------------------------------------------------------------------------------------ rows
def inputs_from_row(r: dict) -> dict:
    b = from_bits
    return {"tick": int(r["tick"]), "mx": b(r["mx"]), "my": b(r["my"]), "mz": b(r["mz"]),
            "f": (b(r["fx"]), b(r["fy"]), b(r["fz"])), "r": (b(r["rx"]), b(r["ry"]), b(r["rz"])),
            "v": (b(r["vx"]), b(r["vy"]), b(r["vz"])), "p": (b(r["px"]), b(r["py"]), b(r["pz"])),
            "o2": b(r["ox"]), "spr": int(r["spr"]), "tool": int(r["tool"]), "gnd": int(r["gnd"]), "jet": int(r["jet"]),
            "mut": int(r["mut"]), "fin": b(r["fin"]) if "fin" in r else 0.0,
            "lad": int(r.get("lad", 0)), "grd": int(r.get("grd", 0))}   #: the yield inputs (rows before them: 0)

def make_row(pre: State, i: dict, o: dict) -> dict:
    r = {"ev": "swim", "v": ROW_VERSION, "n": pre.steps, "tick": i["tick"], "md": o["o_md"],
         "st": MODE_NAMES[o["o_md"]], "inW": int(o["o_md"] != WALK), "y": fixed(i["p"][1], 4),
         "yHead": fixed(i["p"][1], 4), "spd": fixed(vlen(o["o_v"]), 4), "o2": fixed(i["o2"] / R.OXYGEN_CAPACITY, 5),
         "o2u": fixed(i["o2"], 3), "move": [fixed(i["mx"], 6), fixed(i["my"], 6), fixed(i["mz"], 6)],
         "vel": [fixed(x, 6) for x in o["o_v"]], "pos": [fixed(x, 4) for x in i["p"]],
         "look": [fixed(x, 6) for x in i["f"]], "jetOk": o["o_ja"]}
    if o["o_evt"] & EV_ENTERED:
        r["tr"] = "enter"
    elif o["o_evt"] & EV_EXITED:
        r["tr"] = "exit"
    for k, val in (("mx", i["mx"]), ("my", i["my"]), ("mz", i["mz"]), ("fx", i["f"][0]), ("fy", i["f"][1]),
                   ("fz", i["f"][2]), ("rx", i["r"][0]), ("ry", i["r"][1]), ("rz", i["r"][2]), ("vx", i["v"][0]),
                   ("vy", i["v"][1]), ("vz", i["v"][2]), ("px", i["p"][0]), ("py", i["p"][1]), ("pz", i["p"][2]),
                   ("ox", i["o2"]), ("fin", float(i.get("fin", 0.0)))):
        r[k] = bits_of(val)
    for k in ("spr", "tool", "gnd", "jet", "mut"):
        r[k] = int(i[k])
    for k in ("lad", "grd"):
        r[k] = int(i.get(k, 0))
    r.update(pre.to_row())
    r.update({"o_md": o["o_md"], "o_ctl": o["o_ctl"], "o_vx": bits_of(o["o_v"][0]), "o_vy": bits_of(o["o_v"][1]),
              "o_vz": bits_of(o["o_v"][2]), "o_st": o["o_st"], "o_max": bits_of(o["o_max"]),
              "o_cap": bits_of(o["o_cap"]), "o_ja": o["o_ja"], "o_or": o["o_or"], "o_do2": bits_of(o["o_do2"]),
              "o_dc": o["o_dc"], "o_evt": o["o_evt"], "o_chg": o["o_chg"]})
    return r

def read_rows(paths) -> list:
    rows = []
    for p in paths:
        with open(p, "r", encoding="utf-8") as fh:
            for ln, line in enumerate(fh, 1):
                line = line.strip()
                if not line or '"swim"' not in line:
                    continue
                try:
                    r = json.loads(line)
                except ValueError as e:
                    raise SystemExit("swim_replay: %s:%d: not JSON (%s)" % (p, ln, e))
                if r.get("ev") != "swim":
                    continue
                if r.get("v") != ROW_VERSION:
                    raise SystemExit("swim_replay: %s:%d: swim row version %r (this replay reads %d)" % (
                        p, ln, r.get("v"), ROW_VERSION))
                rows.append(r)
    return rows

def write_rows(path: str, rows: list):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        for r in rows:
            fh.write(json.dumps(r, separators=(",", ":")) + "\n")

EXACT_OUT = ("o_md", "o_ctl", "o_st", "o_ja", "o_or", "o_dc", "o_evt", "o_chg")
FLOAT_OUT = ("o_max", "o_cap", "o_do2")
EXACT_STATE = ("s_lat", "s_jl", "s_jd", "s_ctl", "s_ph", "s_md", "s_eu")
FLOAT_STATE = ("s_hx", "s_hy", "s_hz")

def outputs_differ(o: dict, r: dict, tol: float) -> list:
    bad = [k for k in EXACT_OUT if int(r[k]) != int(o[k])]
    for k, x in zip(("o_vx", "o_vy", "o_vz"), o["o_v"]):
        if abs(from_bits(r[k]) - x) > tol:
            bad.append(k)
    for k in FLOAT_OUT:
        if abs(from_bits(r[k]) - o[k]) > tol:
            bad.append(k)
    return bad

def replay(rows: list, honour_mutant: bool = False, tol: float = 1e-6) -> dict:
    res = {"rows": len(rows), "localMismatch": 0, "linkMismatch": 0, "contiguous": 0, "gaps": 0, "mutantRows": 0,
           "maxVelDiff": 0.0, "first": []}
    prev_post, prev_n = None, None
    for r in rows:
        if r.get("mut"):
            res["mutantRows"] += 1
        if prev_n is not None and r["n"] == prev_n + 1:
            res["contiguous"] += 1
            want = prev_post.to_row()
            diff = [k for k in EXACT_STATE if k in r and int(want[k]) != int(r[k])]
            diff += [k for k in FLOAT_STATE if abs(from_bits(want[k]) - from_bits(r[k])) > tol]
            if diff:
                res["linkMismatch"] += 1
                if len(res["first"]) < 8:
                    res["first"].append("n=%d tick=%d state after the previous step differs in %s" % (
                        r["n"], r["tick"], ",".join(diff)))
        elif prev_n is not None:
            res["gaps"] += 1
        i = inputs_from_row(r)
        if not honour_mutant:
            i["mut"] = 0
        post = State.from_row(r)
        o = step(post, i)
        for k, x in zip(("o_vx", "o_vy", "o_vz"), o["o_v"]):
            res["maxVelDiff"] = max(res["maxVelDiff"], abs(from_bits(r[k]) - x))
        bad = outputs_differ(o, r, tol)
        if bad:
            res["localMismatch"] += 1
            if len(res["first"]) < 8:
                res["first"].append("n=%d tick=%d y=%s outputs differ in %s (replay %s)" % (
                    r["n"], r["tick"], r.get("y"), ",".join(bad),
                    {k: (o[k] if k in o else o["o_v"]) for k in bad if not k.startswith("o_v")}))
        prev_post, prev_n = post, r["n"]
    res["pass"] = res["rows"] > 0 and res["localMismatch"] == 0 and res["linkMismatch"] == 0
    return res

# ------------------------------------------------------------------------------------------------ S2b.N1
def window(rows: list, from_tick=None, to_tick=None) -> list:
    return [r for r in rows if (from_tick is None or r["tick"] >= from_tick) and (to_tick is None or r["tick"] <= to_tick)]

def _vec_key(r: dict, key: str):
    v = r.get(key)
    if isinstance(v, (list, tuple)) and len(v) == 3 and all(isinstance(x, (int, float)) for x in v):
        return tuple(float(x) for x in v)
    return None

def measured_series(rows: list) -> list:
    """SE's own motion: the velocity of tick k is the plugin's vSe key (SE's velocity right after the physics step of
 tick k) when the row carries it, else the velocity SE reports at the start of tick k + 1 (with the velocity source
 "model" that input is the model's own output, so only vSe measures SE then)."""
    out = []
    for k, r in enumerate(rows):
        i = inputs_from_row(r)
        v = _vec_key(r, "vSe")
        if v is None:
            nxt = rows[k + 1] if k + 1 < len(rows) and rows[k + 1]["n"] == r["n"] + 1 else None
            v = inputs_from_row(nxt)["v"] if nxt is not None else None
        out.append({"tick": r["tick"], "v": v, "p": i["p"], "md": int(r["o_md"]), "ctl": int(r["o_ctl"]),
                    "key": (i["mx"], i["my"], i["mz"], i["spr"], i["tool"])})
    return out

def openloop_series(rows: list, honour_mutant: bool = False) -> list:
    """This script's model driven by the logged inputs, with its own velocity, position and oxygen."""
    if not rows:
        return []
    s = State.from_row(rows[0])
    first = inputs_from_row(rows[0])
    v, p, o2 = first["v"], first["p"], first["o2"]
    out = []
    for r in rows:
        i = inputs_from_row(r)
        if not honour_mutant:
            i["mut"] = 0
        i["v"], i["p"], i["o2"] = v, p, o2
        o = step(s, i)
        out.append({"tick": r["tick"], "v": o["o_v"], "p": p, "md": o["o_md"], "ctl": o["o_ctl"],
                    "key": (i["mx"], i["my"], i["mz"], i["spr"], i["tool"])})
        v = o["o_v"]
        p = (p[0] + v[0] * R.SE_TICK_DT, p[1] + v[1] * R.SE_TICK_DT, p[2] + v[2] * R.SE_TICK_DT)
        o2 = o2 + o["o_do2"]
    return out

def segments(rows: list) -> list:
    """Index ranges [a, b) of constant move input while the model controls the velocity (from the logged rows)."""
    segs, a = [], None
    keys = [(r["mx"], r["my"], r["mz"], r["spr"], r["tool"]) for r in rows]
    for k, r in enumerate(rows):
        ctl = int(r["o_ctl"]) == 1
        if a is not None and (not ctl or keys[k] != keys[a] or rows[k]["n"] != rows[k - 1]["n"] + 1):
            segs.append((a, k))
            a = None
        if a is None and ctl:
            a = k
    if a is not None:
        segs.append((a, len(rows)))
    return segs

def seg_metrics(series: list, a: int, b: int, drive: bool) -> dict:
    vs = [series[k]["v"] for k in range(a, b) if series[k]["v"] is not None]
    if len(vs) < STEADY_WINDOW_TICKS:
        return None
    if drive:
        tail = vs[-STEADY_WINDOW_TICKS:]
        steady = sum(vlen(v) for v in tail) / len(tail)
        vy = sum(v[1] for v in tail) / len(tail)
        t90 = next((k for k, v in enumerate(vs) if vlen(v) >= 0.9 * steady), len(vs)) * R.SE_TICK_DT
        return {"steady": steady, "vy": vy, "t90": t90}
    k_stop = next((k for k, v in enumerate(vs) if vlen(v) < STOP_SPEED), len(vs) - 1)
    p0, p1 = series[a]["p"], series[min(a + k_stop, b - 1)]["p"]
    dist = math.sqrt(sum((p1[j] - p0[j]) ** 2 for j in range(3)))
    return {"stopDist": dist, "stopTime": k_stop * R.SE_TICK_DT}

def close(meas: float, model: float, floor: float, tol: float = N1_TOL) -> bool:
    return abs(meas - model) <= max(tol * abs(model), floor)

def n1_report(rows: list, curve_tol=None, honour_mutant: bool = False) -> dict:
    meas = measured_series(rows)
    model = openloop_series(rows, honour_mutant)
    segs = segments(rows)
    checks, kinds_seen, prev_drive = [], set(), None
    drive_by_key = {}
    for a, b in segs:
        dur = (b - a) * R.SE_TICK_DT
        mx, my, mz, spr, tool = meas[a]["key"]
        drive = (mx, my, mz) != (0.0, 0.0, 0.0)
        if dur < MIN_SEGMENT_S:
            prev_drive = drive
            continue
        mm, om = seg_metrics(meas, a, b, drive), seg_metrics(model, a, b, drive)
        if mm is None or om is None:
            continue
        label = "tick %d-%d" % (rows[a]["tick"], rows[b - 1]["tick"])
        if drive:
            kind = "sprint" if spr else ("forward" if mz > 0 and mx == 0 and my == 0 else
                                         "backward" if mz < 0 and mx == 0 and my == 0 else
                                         "strafe" if mx != 0 and mz == 0 and my == 0 else
                                         "up" if my > 0 and mx == 0 and mz == 0 else
                                         "down" if my < 0 and mx == 0 and mz == 0 else "mixed")
            if kind == "forward" and dur >= 8.0:
                kinds_seen.add("forward10")
            kinds_seen.add(kind)
            checks.append(("%s steady speed (%s)" % (kind, label), mm["steady"], om["steady"],
                           close(mm["steady"], om["steady"], N1_SPEED_FLOOR)))
            checks.append(("%s time to 90 %% (%s)" % (kind, label), mm["t90"], om["t90"],
                           close(mm["t90"], om["t90"], N1_TIME_FLOOR)))
            checks.append(("%s vertical speed (%s)" % (kind, label), mm["vy"], om["vy"],
                           close(mm["vy"], om["vy"], N1_SPEED_FLOOR)))
            same = drive_by_key.get((mx, my, mz, 1 - spr, tool))
            if same is not None:
                mf = mm["steady"] / same[0] if spr else same[0] / mm["steady"]
                of = om["steady"] / same[1] if spr else same[1] / om["steady"]
                checks.append(("sprint factor (%s)" % label, mf, of, close(mf, of, 0.0)))
                kinds_seen.add("sprintFactor")
            drive_by_key[(mx, my, mz, spr, tool)] = (mm["steady"], om["steady"])
        elif prev_drive:
            kinds_seen.add("release")
            checks.append(("stop distance (%s)" % label, mm["stopDist"], om["stopDist"],
                           close(mm["stopDist"], om["stopDist"], N1_DIST_FLOOR)))
            checks.append(("stop time (%s)" % label, mm["stopTime"], om["stopTime"],
                           close(mm["stopTime"], om["stopTime"], N1_TIME_FLOOR)))
        prev_drive = drive
    required = ("forward10", "release", "strafe", "backward", "up", "down", "sprint", "sprintFactor")
    missing = [k for k in required if k not in kinds_seen]
    curve = 0.0
    for r, m in zip(rows, model):
        for k, x in zip(("o_vx", "o_vy", "o_vz"), m["v"]):
            curve = max(curve, abs(from_bits(r[k]) - x))
    curve_ok = curve_tol is None or curve <= curve_tol
    good = bool(checks) and all(c[3] for c in checks) and not missing and curve_ok
    return {"checks": checks, "missing": missing, "segments": len(segs), "curveMax": curve, "curveTol": curve_tol,
            "curvePass": curve_ok, "pass": good}

def info(rows: list) -> dict:
    tr = [(r["tick"], "enter" if int(r["o_evt"]) & EV_ENTERED else "exit") for r in rows
          if int(r["o_evt"]) & (EV_ENTERED | EV_EXITED)]
    swim_no_jet = [from_bits(r["py"]) for r in rows if int(r["o_md"]) in (SWIM, SURFACE)]
    rate = {}
    for dc in (R.DC_SAFE, R.DC_UNSAFE, R.DC_CRUSH):
        # full breaths below the refill height: oxygen per second = (breaths after the first) x amount / time between
 # the first and the last breath (an emptying suit gives partial breaths, which are left out)
        amount = R.BREATH_PERIOD_S[dc] * R.BREATH_FACTOR[dc]
        br = [r for r in rows if int(r["o_dc"]) == dc and int(r["o_or"]) and int(r["o_evt"]) & EV_BREATH and
              from_bits(r["py"]) <= R.OXYGEN_REFILL_EYE_Y and abs(-from_bits(r["o_do2"]) - amount) < 1e-9]
        if len(br) >= 3:
            span = (br[-1]["tick"] - br[0]["tick"]) * R.SE_TICK_DT
            rate[dc] = amount * (len(br) - 1) / span if span > 0 else None
    jet_in_water = sum(1 for r in rows if int(r["o_ja"]) and from_bits(r["py"]) < R.JETPACK_OFF_EYE_Y)
    return {"transitions": tr, "maxEyeSwimming": max(swim_no_jet) if swim_no_jet else None, "o2Rate": rate,
            "jetpackAllowedBelowMinus01": jet_in_water}

# ------------------------------------------------------------------------------------------------ synthetic sessions
class Sim:
    """A scripted SE stand-in: runs this script's model and plays SE's physics around it (perfect by default)."""

    def __init__(self, eye=-50.0, mutants=0, defect=None, o2=R.OXYGEN_CAPACITY, tick0=600, fins=0.0):
        self.s = State()
        self.fins = float(fins)      # S5a: the fins modifier the stand-in's controller sends (and logs)
        self.tick = tick0
        self.p = (100.0, eye, -200.0)
        self.v = (0.0, 0.0, 0.0)
        self.o2 = o2
        self.mut = mutants
        self.defect = defect
        self.move = (0.0, 0.0, 0.0)
        self.spr = self.tool = self.gnd = self.jet = 0
        self.f, self.r = (0.0, 0.0, 1.0), (1.0, 0.0, 0.0)
        self.floor = None            # eye height when standing on a block (Walk)
        self.rows = []

    def look(self, pitch_deg: float):
        c, s_ = math.cos(math.radians(pitch_deg)), math.sin(math.radians(pitch_deg))
        self.f = (0.0, s_, c)

    def run(self, ticks: int):
        for _ in range(ticks):
            i = {"tick": self.tick, "mx": float(self.move[0]), "my": float(self.move[1]), "mz": float(self.move[2]),
                 "f": self.f, "r": self.r, "v": self.v, "p": self.p, "o2": self.o2, "spr": self.spr,
                 "tool": self.tool, "gnd": self.gnd, "jet": self.jet, "mut": self.mut, "fin": self.fins}
            pre = self.s.copy()
            if self.defect == "fins":
                # S5a.M1 stand-in: the controller claims the fins (row key fin) but the model stepped without them
                o = step(self.s, dict(i, fin=0.0))
            else:
                o = step(self.s, i)
            self.rows.append(make_row(pre, i, o))
            if o["o_ctl"]:
                v = o["o_v"]
                if self.defect == "gravity":
                    v = (v[0], v[1] - 9.81 * R.SE_TICK_DT, v[2])
                elif self.defect == "slow":
                    v = (v[0] * 0.9, v[1] * 0.9, v[2] * 0.9)
            elif o["o_md"] == FLY:
                v = (0.0, 3.0 if self.move[1] > 0 else 0.0, 0.0)
            else:
                v = self.v
                if self.floor is not None and self.p[1] <= self.floor + 1e-9 and v[1] <= 0.0:
                    v = (0.0, 0.0, 0.0)
                else:
                    v = (v[0] * 0.98, v[1] - 9.81 * R.SE_TICK_DT, v[2] * 0.98)
            self.v = v
            ny = self.p[1] + v[1] * R.SE_TICK_DT
            if self.floor is not None and o["o_md"] == WALK and ny < self.floor:
                ny = self.floor
                self.v = (0.0, 0.0, 0.0)
            self.p = (self.p[0] + v[0] * R.SE_TICK_DT, ny, self.p[2] + v[2] * R.SE_TICK_DT)
            if o["o_or"]:
                self.o2 = self.o2 + o["o_do2"]
            self.tick += 1

def scenario(name: str, mutants: int = 0, defect=None, fins: float = 0.0) -> Sim:
    if name in ("n1", "n1fins"):
        w = Sim(-50.0, mutants, defect, fins=FINS["ultra"] if name == "n1fins" else fins)
        w.run(30)
        for sec, x, y, z, spr in N1_SCRIPT:
            w.move, w.spr = (x, y, z), spr
            w.run(int(round(sec * 60)))
        return w
    if name == "surface":
        # swim up from -20 m looking level, hold "up" at the surface 5 s, then jetpack on: up and out onto a block
        w = Sim(-20.0, mutants, defect)
        w.run(30)
        w.move = (0, 1, 0)
        w.run(7 * 60)
        w.run(5 * 60)
        w.jet = 1
        w.floor = 1.6
        w.run(3 * 60)
        w.move = (0, 0, 0)
        w.run(60)
        return w
    if name == "entry":
        # walk into the water down a slope, swim, then swim up, jetpack out onto a block, stand (grounded)
        w = Sim(1.2, mutants, defect)
        w.v = (0.0, -0.4, 2.0)
        w.move = (0, 0, 1)
        w.run(6 * 60)
        w.look(0.0)
        w.run(2 * 60)
        w.move = (0, 1, 0)
        w.run(5 * 60)
        w.jet = 1
        w.floor = 1.5
        w.run(2 * 60)
        w.jet, w.gnd, w.move = 0, 1, (0, 0, 0)
        w.run(2 * 60)
        return w
    if name == "oxygen":
        w = Sim(-50.0, mutants, defect, tick0=1)
        w.run(50 * 60)                  # 45 s of air at <= 100 m, then empty
        w.p = (w.p[0], -0.5, w.p[2])    # teleported to just under the surface: refill
        w.run(3 * 60)
        w.p = (w.p[0], -150.0, w.p[2])
        w.o2 = R.OXYGEN_CAPACITY
        w.run(20 * 60)
        w.p = (w.p[0], -250.0, w.p[2])
        w.o2 = R.OXYGEN_CAPACITY
        w.run(20 * 60)
        return w
    if name == "look":
        # forward while looking 45 degrees down and 30 degrees up, strafe with a rolled camera, tool held
        w = Sim(-60.0, mutants, defect)
        w.look(-45.0)
        w.move = (0, 0, 1)
        w.run(4 * 60)
        w.look(30.0)
        w.run(4 * 60)
        w.look(0.0)
        w.r = (math.cos(0.4), math.sin(0.4), 0.0)
        w.move = (1, 0, 0)
        w.run(3 * 60)
        w.r = (1.0, 0.0, 0.0)
        w.tool = 1
        w.move = (0, 0, 1)
        w.run(4 * 60)
        w.move = (0, 1, 1)
        w.run(2 * 60)
        return w
    if name == "random":
        import random
        rnd = random.Random(2046)
        w = Sim(-5.0, mutants, defect)
        for _ in range(240):
            w.move = (rnd.choice((-1, 0, 1)), rnd.choice((-1, 0, 0, 1)), rnd.choice((-1, 0, 1, 1)))
            w.look(rnd.uniform(-80, 80))
            w.spr, w.tool, w.jet, w.gnd = rnd.random() < 0.2, rnd.random() < 0.2, rnd.random() < 0.1, rnd.random() < 0.2
            w.run(rnd.randint(1, 40))
            if w.p[1] > 3.0:
                w.p = (w.p[0], -2.0, w.p[2])
                w.v = (0.0, 0.0, 0.0)
        return w
    raise SystemExit("swim_replay: unknown scenario %r" % name)

SCENARIOS = ("n1", "n1fins", "surface", "entry", "oxygen", "look", "random")

# ------------------------------------------------------------------------------------------------ the analyser's port
class _AnalyzerPort:
    """The model as tests/s2b/analyze_swim.py drives it (its documented port contract): one SE tick per step call,
 open loop on the velocity. When the call carries this file's "swim" row, the plugin's step phase and latches are
 taken from the row's logged state (so the 0.1 s motor steps fall on the same ticks) while the velocity stays the
 port's own; a tick that does not follow the previous one starts over."""

    name = "swim_replay.py (SwimModel v1, sn_reference numbers)"

    def __init__(self):
        self.s = State()
        self.last = None

    def step(self, v, tick):
        row = tick.get("row") if isinstance(tick.get("row"), dict) else {}
        own = "s_ph" in row and "py" in row
        tn = tick.get("tickNo")
        if tn is None:
            tn = (self.last + 1) if self.last is not None else 0
        tn = int(tn)
        fresh = self.last is None or tn != self.last + 1
        if own:
            h = self.s.h
            self.s = State.from_row(row)
            if not fresh:
                self.s.h = h
        elif fresh:
            self.s = State()
            self.s.lat = bool(tick.get("inWater"))
            self.s.eu = None          # set from the first eye below (no motor switch on a fresh start)
        b = from_bits
        mv = tick.get("move") or (0.0, 0.0, 0.0)
        look = tick.get("look")
        if own:
            fwd = (b(row["fx"]), b(row["fy"]), b(row["fz"]))
            right = (b(row["rx"]), b(row["ry"]), b(row["rz"]))
            eye = b(row["py"])
        else:
            fwd = tuple(look) if look else (0.0, 0.0, 1.0)
            n = math.sqrt(fwd[0] * fwd[0] + fwd[2] * fwd[2])
            right = (fwd[2] / n, 0.0, -fwd[0] / n) if n > R.NORMALIZE_EPS else (1.0, 0.0, 0.0)
            eye = tick.get("yHead")
            eye = float(eye) if eye is not None else -50.0
        if self.s.eu is None:
            self.s.eu = eye < R.UNDERWATER_EYE_Y
        i = {"tick": tn, "mx": float(mv[0]), "my": float(mv[1]), "mz": float(mv[2]), "f": fwd, "r": right,
             "v": tuple(float(x) for x in v), "p": (0.0, eye, 0.0), "o2": R.OXYGEN_CAPACITY,
             "spr": int(bool(tick.get("sprint"))), "tool": int(row.get("tool", 0)) if own else 0,
             "gnd": int(row.get("gnd", 0)) if own else 0, "jet": int(row.get("jet", 0)) if own else 0, "mut": 0,
             "fin": b(row["fin"]) if own and "fin" in row else float(tick.get("fins") or 0.0)}
        o = step(self.s, i)
        self.last = tn
        return o["o_v"]

    @staticmethod
    def o2(depth_m):
        eye = -float(depth_m)
        dc = depth_class(eye)
        if eye >= R.UNDERWATER_EYE_Y or dc == R.DC_SURFACE:
            return {"rate": 0.0, "capacity": R.OXYGEN_CAPACITY, "breathS": 0.0, "breathUnits": 0.0}
        return {"rate": R.oxygen_rate(dc), "capacity": R.OXYGEN_CAPACITY, "breathS": R.BREATH_PERIOD_S[dc],
                "breathUnits": R.BREATH_PERIOD_S[dc] * R.BREATH_FACTOR[dc]}

    @staticmethod
    def o2_refill():
        return R.OXYGEN_REFILL_PER_S

    @staticmethod
    def surface_head_max():
        return surface_head_max()

_HEAD_MAX = []

def surface_head_max() -> float:
    """Highest eye the model lets a diver swim to (up held, from any depth and step phase): the 0.52 m rule plus the
 overshoot of the 0.1 s steps. Computed once by brute force (start heights every 1 cm over one step's travel)."""
    if not _HEAD_MAX:
        best = -1.0
        for k in range(60):
            s = State()
            v, eye = (0.0, 0.0, 0.0), -3.0 - k * 0.01
            for t in range(1200):
                i = {"tick": t, "mx": 0.0, "my": 1.0, "mz": 0.0, "f": (0.0, 0.0, 1.0), "r": (1.0, 0.0, 0.0), "v": v,
                     "p": (0.0, eye, 0.0), "o2": R.OXYGEN_CAPACITY, "spr": 0, "tool": 0, "gnd": 0, "jet": 0, "mut": 0}
                o = step(s, i)
                if o["o_md"] == WALK:
                    break
                v = o["o_v"]
                eye += v[1] * R.SE_TICK_DT
                best = max(best, eye)
        _HEAD_MAX.append(best)
    return _HEAD_MAX[0]

def analyzer_port():
    """The object tests/s2b/analyze_swim.py drives (step, o2, o2_refill, surface_head_max, name)."""
    return _AnalyzerPort()

# ------------------------------------------------------------------------------------------------ CLI
def print_replay(rep: dict):
    print("  replay (documented model)  %s  rows=%d contiguous=%d gaps=%d localMismatch=%d linkMismatch=%d "
          "maxVelDiff=%.3g mutantRows=%d" % ("PASS" if rep["pass"] else "FAIL", rep["rows"], rep["contiguous"],
                                             rep["gaps"], rep["localMismatch"], rep["linkMismatch"],
                                             rep["maxVelDiff"], rep["mutantRows"]))
    for line in rep["first"]:
        print("    " + line)

def print_n1(rep: dict):
    for name, mv, ov, good in rep["checks"]:
        print("  %-52s %s  measured %.4f  model %.4f" % (name, "PASS" if good else "FAIL", mv, ov))
    if rep["missing"]:
        print("  required segments MISSING: %s" % ", ".join(rep["missing"]))
    print("  curve (logged velocity vs open-loop model) max %.3g%s" % (
        rep["curveMax"], "" if rep["curveTol"] is None else ("  %s (tol %g)" % ("PASS" if rep["curvePass"] else "FAIL",
                                                                                 rep["curveTol"]))))

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S2b swim model replay and S2b.N1 oracle (swim rows of se-*.jsonl)")
    ap.add_argument("--selftest", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    p = sub.add_parser("replay")
    p.add_argument("files", nargs="+")
    p.add_argument("--honour-mutant", action="store_true")
    p.add_argument("--tol", type=float, default=1e-6)
    p.add_argument("--from-tick", type=int)
    p.add_argument("--to-tick", type=int)
    p.add_argument("--json")
    p = sub.add_parser("n1")
    p.add_argument("files", nargs="+")
    p.add_argument("--from-tick", type=int)
    p.add_argument("--to-tick", type=int)
    p.add_argument("--curve-tol", type=float)
    p.add_argument("--tol", type=float, default=1e-6)
    p.add_argument("--json")
    p = sub.add_parser("info")
    p.add_argument("files", nargs="+")
    p = sub.add_parser("gen")
    p.add_argument("out")
    p.add_argument("--scenario", choices=SCENARIOS, required=True)
    p.add_argument("--mutant", choices=sorted(MUTANTS))
    p.add_argument("--defect", choices=("gravity", "slow", "fins"))
    p.add_argument("--fins", choices=sorted(FINS), default="off", help="S5a fins modifier of the stand-in (default off)")
    p = sub.add_parser("script")
    p.add_argument("name", choices=("n1",))
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.cmd in ("replay", "n1", "info"):
        rows = read_rows(a.files)
        if a.cmd != "info":
            rows = window(rows, a.from_tick, a.to_tick)
        if not rows:
            print("swim_replay: no swim rows")
            return 2
        if a.cmd == "info":
            print(json.dumps(info(rows), indent=1))
            return 0
        rep = replay(rows, getattr(a, "honour_mutant", False), a.tol)
        print("SWIM REPLAY %s" % ", ".join(a.files))
        print_replay(rep)
        result = {"replay": rep}
        good = rep["pass"]
        if a.cmd == "n1":
            n1 = n1_report(rows, a.curve_tol)
            print_n1(n1)
            result["n1"] = n1
            good = good and n1["pass"]
            print("S2b.N1 %s" % ("PASS" if good else "FAIL"))
        print("SWIM REPLAY %s" % ("PASS" if good else "FAIL"))
        if a.json:
            with open(a.json, "w", encoding="utf-8") as fh:
                json.dump(result, fh, indent=1, default=str)
        return 0 if good else 1
    if a.cmd == "gen":
        w = scenario(a.scenario, MUTANTS.get(a.mutant, 0), a.defect, FINS[a.fins])
        write_rows(a.out, w.rows)
        print("wrote %d rows to %s" % (len(w.rows), a.out))
        return 0
    if a.cmd == "script":
        print(json.dumps([{"seconds": s, "strafe": x, "vertical": y, "forward": z, "sprint": spr}
                          for s, x, y, z, spr in N1_SCRIPT]))
        return 0
    ap.print_help()
    return 2

# ------------------------------------------------------------------------------------------------ selftest
def selftest() -> int:
    ok = True
    tmp = tempfile.mkdtemp(prefix="swim-selftest-")

    def line(name, good, detail=""):
        nonlocal ok
        ok = ok and good
        print("  %-62s %s  %s" % (name, "ok" if good else "WRONG", detail))

    print("SWIM REPLAY SELFTEST")
    d = R.derived()
    for name in SCENARIOS:
        w = scenario(name)
        path = os.path.join(tmp, name + ".jsonl")
        write_rows(path, w.rows)
        rows = read_rows([path])
        rep = replay(rows)
        line("green %s: replay" % name, rep["pass"], "rows=%d maxVelDiff=%.3g" % (len(rows), rep["maxVelDiff"]))
        if name == "n1":
            n1 = n1_report(rows, curve_tol=1e-9)
            line("green n1: S2b.N1 metrics within 5 %, curve exact", n1["pass"],
                 "%d checks, missing %s, curve %.3g" % (len(n1["checks"]), n1["missing"], n1["curveMax"]))
            fwd = next(c for c in n1["checks"] if c[0].startswith("forward steady"))
            line("  forward steady speed = 5.70 m/s (sn_reference)", abs(fwd[1] - d["forwardSteady"]) < 1e-9,
                 "%.6f" % fwd[1])
            up = next(c for c in n1["checks"] if c[0].startswith("up steady"))
            line("  vertical steady speed = 4.725 m/s", abs(up[1] - d["verticalSteady"]) < 1e-9, "%.6f" % up[1])
            stop = next(c for c in n1["checks"] if c[0].startswith("stop distance"))
            line("  stop distance 1.693 m to 0.05 m/s (1.71 m in the limit)", abs(stop[1] - d["stopDistanceTo005M"]) <
                 1e-9, "%.4f" % stop[1])
            t90 = next(c for c in n1["checks"] if c[0].startswith("forward time to 90"))
            line("  time to 90 % of the top speed = 0.6 s", abs(t90[1] - d["accel90S"]) < 1e-9, "%.4f s" % t90[1])
            sf = next(c for c in n1["checks"] if c[0].startswith("sprint factor"))
            line("  sprint factor 1.0 (no swim sprint)", abs(sf[1] - 1.0) < 1e-12, "%.6f" % sf[1])
            sparse = [r for k, r in enumerate(rows) if k % 4 == 0]
            rp = replay(sparse)
            line("  sparse rows (every 4th) reseed and replay", rp["pass"] and rp["gaps"] > 0, "gaps=%d" % rp["gaps"])
        if name == "n1fins":
            n1 = n1_report(rows, curve_tol=1e-9)
            line("green n1fins (Ultraglide +3.2): S2b.N1 metrics within 5 %, curve exact", n1["pass"],
                 "%d checks, missing %s, curve %.3g" % (len(n1["checks"]), n1["missing"], n1["curveMax"]))
            fwd = next(c for c in n1["checks"] if c[0].startswith("forward steady"))
            line("  forward steady speed = 8.1 m/s ((7.6 + 3.2) x 0.75)", abs(fwd[1] - d["finsUltraForwardSteady"]) < 1e-9,
                 "%.6f" % fwd[1])
            up = next(c for c in n1["checks"] if c[0].startswith("up steady"))
            line("  vertical steady speed = 7.125 m/s ((6.3 + 3.2) x 0.75)", abs(up[1] - d["finsUltraVerticalSteady"]) < 1e-9,
                 "%.6f" % up[1])
            t90 = next(c for c in n1["checks"] if c[0].startswith("forward time to 90"))
            line("  time to 90 % unchanged by the fins (the acceleration scales with the ratio)",
                 abs(t90[1] - d["accel90S"]) < 1e-9, "%.4f s" % t90[1])
            line("  every row carries fin = 3.2", all(from_bits(r["fin"]) == FINS["ultra"] for r in rows))
            plain = [dict(r) for r in rows]
            for r in plain:
                del r["fin"]
            line("  rows without the fin key replay as no fins (pre-S5a rows): red here", not replay(plain)["pass"])
        if name == "surface":
            inf = info(rows)
            # the 0.1 s steps overshoot +0.52 m a little (the rule scales the step's vertical speed), never the +0.8 m exit
            line("  surface: swimming up stops with the head out, below the +0.8 m exit",
                 inf["maxEyeSwimming"] is not None and R.SURFACE_TOP_Y < inf["maxEyeSwimming"] < R.SWIM_EXIT_EYE_Y,
                 "max eye %.4f" % inf["maxEyeSwimming"])
            held = [r for r in rows if int(r["o_md"]) == SURFACE and int(r["o_st"]) and from_bits(r["py"]) >=
                    R.SURFACE_TOP_Y]
            line("  surface: no vertical speed left above +0.52 m (up held)", bool(held) and
                 all(from_bits(r["o_vy"]) == 0.0 for r in held), "%d steps" % len(held))
            line("  surface: latched at the first row, one exit (jetpack out)", [t[1] for t in inf["transitions"]] ==
                 ["enter", "exit"] and inf["transitions"][0][0] == rows[0]["tick"], str(inf["transitions"]))
            line("  surface: jetpack never allowed below -0.1 m", inf["jetpackAllowedBelowMinus01"] == 0)
            fly = [r for r in rows if int(r["o_md"]) == FLY]
            line("  surface: Fly once the head is out and the jetpack is on", bool(fly) and
                 all(from_bits(r["py"]) >= R.JETPACK_OFF_EYE_Y for r in fly), "%d Fly rows" % len(fly))
        if name == "entry":
            inf = info(rows)
            line("  entry: one transition each way", [t[1] for t in inf["transitions"]] == ["enter", "exit"],
                 str(inf["transitions"]))
            ent = next(r for r in rows if int(r["o_evt"]) & EV_ENTERED)
            line("  entry: the latch turns on below -0.1 m, vertical velocity halved",
                 from_bits(ent["py"]) < R.SWIM_ENTER_EYE_Y and int(ent["o_st"]) == 1, "eye %.4f" % from_bits(ent["py"]))
        if name == "oxygen":
            inf = info(rows)
            rate = inf["o2Rate"]
            line("  oxygen: 1 / 1.5 / 2 per second by depth class", all(
                abs(rate.get(dc, -1) - R.oxygen_rate(dc)) < 0.05 for dc in (1, 2, 3)),
                 " ".join("%d:%.3f" % (k, v) for k, v in sorted(rate.items())))
            empty = next((r for r in rows if float(r["o2"]) <= 0.0), None)
            line("  oxygen: empty after 45 s at -50 m", empty is not None and abs((empty["tick"] - 1) / 60.0 - 45.0)
                 <= 3.0, "tick %s" % (empty and empty["tick"]))
            refill = [r for r in rows if from_bits(r["py"]) == -0.5]
            full = next((k for k, r in enumerate(refill) if from_bits(r["ox"]) >= R.OXYGEN_CAPACITY), None)
            breaths = sum(1 for r in refill[:full] if int(r["o_evt"]) & EV_BREATH) if full is not None else 0
            want = int(R.OXYGEN_CAPACITY / (R.OXYGEN_REFILL_PER_S * R.SE_TICK_DT)) + breaths * 6
            line("  oxygen: refilled in 1.5 s just under the surface (+0.1 s per breath)", full == want,
                 "%s ticks, %d breath(s) on the way" % (full, breaths))

    # seeded failures
    n1m1 = scenario("n1", R.MUT_M1)
    rows = n1m1.rows
    rep = replay(rows)
    line("S2b.M1 (numbers x1.5): replay red", not rep["pass"], "localMismatch=%d" % rep["localMismatch"])
    n1 = n1_report(rows)
    line("S2b.M1: S2b.N1 metrics red against the documented model", not n1["pass"],
         "%d failing checks" % sum(1 for c in n1["checks"] if not c[3]))
    line("S2b.M1: --honour-mutant replays it", replay(rows, True)["pass"])
    m2 = scenario("surface", R.MUT_M2)
    rep = replay(m2.rows)
    line("S2b.M2 (jetpack left on): replay red, jetpack allowed under water", not rep["pass"] and
         info(m2.rows)["jetpackAllowedBelowMinus01"] > 0, "allowed rows %d" % info(m2.rows)["jetpackAllowedBelowMinus01"])
    m3 = scenario("surface", R.MUT_M3)
    rep = replay(m3.rows)
    tr = info(m3.rows)["transitions"]
    line("S2b.M3 (no hysteresis): replay red, bouncing at the surface", not rep["pass"] and len(tr) > 4,
         "%d transitions" % len(tr))
    m4 = scenario("oxygen", R.MUT_M4)
    rep = replay(m4.rows)
    line("S2b.M4 (oxygen rule off): replay red, no Subnautica rate", not rep["pass"] and not info(m4.rows)["o2Rate"],
         "o2Rate %s" % info(m4.rows)["o2Rate"])
    # S5a.M1 stand-in: the rows claim Ultraglide (fin = 3.2) but the model stepped without: the replay is red, and the
 # fins steady speed is the no-fins 5.70 instead of 8.1
    wf = scenario("n1fins", 0, "fins")
    rep = replay(wf.rows)
    n1 = n1_report(wf.rows)
    fwd = next(c for c in n1["checks"] if c[0].startswith("forward steady"))
    line("fins not applied (S5a.M1 stand-in): replay red, S2b.N1 red, measured 5.70 against the model's 8.1",
         not rep["pass"] and not n1["pass"] and abs(fwd[1] - d["forwardSteady"]) < 1e-9 and abs(fwd[2] - d["finsUltraForwardSteady"]) < 1e-9,
         "localMismatch=%d measured %.4f model %.4f" % (rep["localMismatch"], fwd[1], fwd[2]))
    line("  honouring nothing helps: the claimed fins are the model's input", not replay(wf.rows, True)["pass"])
    port = analyzer_port()
    v = (0.0, 0.0, 0.0)
    for t in range(240):
        v = port.step(v, {"move": (0.0, 0.0, 1.0), "sprint": False, "look": (0.0, 0.0, 1.0), "dt": R.SE_TICK_DT,
                          "yHead": -50.0, "inWater": True, "tickNo": 2000 + t, "row": None, "fins": FINS["ultra"]})
    line("analyzer_port(): without rows, fins=3.2 settles at 8.1 m/s", abs(v[2] - d["finsUltraForwardSteady"]) < 1e-9,
         "%.6f" % v[2])
    # SE does not honour the imposed velocity: the replay stays green (the plugin computed the model right) but N1 is red
    for defect in ("gravity", "slow"):
        w = scenario("n1", 0, defect)
        rep = replay(w.rows)
        n1 = n1_report(w.rows)
        line("SE defect '%s': replay green, S2b.N1 red" % defect, rep["pass"] and not n1["pass"],
             "%d failing checks" % sum(1 for c in n1["checks"] if not c[3]))
    # tampering
    rows = [dict(r) for r in scenario("n1").rows]
    k = next(n for n, r in enumerate(rows) if int(r["o_st"]) and n > 100)
    rows[k]["o_vz"] = bits_of(from_bits(rows[k]["o_vz"]) + 1e-3)
    line("tampered velocity: replay red", not replay(rows)["pass"], "row %d" % k)
    rows = [dict(r) for r in scenario("n1").rows]
    rows[k + 1]["s_ph"] = (int(rows[k + 1]["s_ph"]) + 1) % R.SN_STEP_TICKS
    line("tampered phase: linkage red", replay(rows)["linkMismatch"] > 0, "row %d" % (k + 1))
    rows = [dict(r) for r in scenario("entry").rows]
    k = next(n for n, r in enumerate(rows) if int(r["o_evt"]) & EV_ENTERED)
    rows[k]["o_ja"] = 1
    line("tampered jetpack flag at the entry: replay red", not replay(rows)["pass"], "row %d" % k)
    rows = scenario("n1").rows
    part = [r for r in rows if r["tick"] < rows[0]["tick"] + 15 * 60]
    n1 = n1_report(part)
    line("partial N1 session (15 s): MISSING segments, red", not n1["pass"] and "strafe" in n1["missing"],
         "missing %s" % n1["missing"])
    # the analyser's port: open loop on the velocity, phase and latches from the rows -> the logged velocities
    rows = scenario("n1").rows
    port = analyzer_port()
    v = inputs_from_row(rows[0])["v"]
    worst = 0.0
    for r in rows:
        i = inputs_from_row(r)
        v = port.step(v, {"move": (i["mx"], i["my"], i["mz"]), "sprint": bool(i["spr"]), "look": i["f"],
                          "dt": R.SE_TICK_DT, "yHead": i["p"][1], "inWater": bool(r["inW"]), "tickNo": r["tick"],
                          "row": r})
        worst = max(worst, max(abs(a - from_bits(r[k])) for a, k in zip(v, ("o_vx", "o_vy", "o_vz"))))
    line("analyzer_port(): open loop with the logged rows = the logged velocities", worst <= 1e-9, "worst %.3g" % worst)
    port = analyzer_port()
    v = (0.0, 0.0, 0.0)
    for t in range(240):
        v = port.step(v, {"move": (0.0, 0.0, 1.0), "sprint": False, "look": (0.0, 0.0, 1.0), "dt": R.SE_TICK_DT,
                          "yHead": -50.0, "inWater": True, "tickNo": 1000 + t, "row": None})
    line("analyzer_port(): without rows, forward settles at 5.70 m/s", abs(v[2] - d["forwardSteady"]) < 1e-9,
         "%.6f" % v[2])
    o2 = port.o2(150.0)
    line("analyzer_port(): o2 at 150 m = 1.5/s, breath 2.25 s of 3.375", (o2["rate"], o2["breathS"], o2["breathUnits"],
                                                                          o2["capacity"]) == (1.5, 2.25, 3.375, 45.0))
    hm = port.surface_head_max()
    line("analyzer_port(): swim head limit between +0.52 and +0.8 m", R.SURFACE_TOP_Y < hm < R.SWIM_EXIT_EYE_Y,
         "%.4f m" % hm)
    rows = scenario("entry").rows
    line("readable keys: tr on the transitions, o2 a fraction", [r.get("tr") for r in rows if r.get("tr")] ==
         ["enter", "exit"] and all(0.0 <= r["o2"] <= 1.0 for r in rows), "")
    #  the Dive<->Run motor switch re-enables the swim motor (vy x 0.5) at every crossing
    s = State()
    s.lat, s.ctl, s.md, s.eu, s.ph, s.h = True, True, SWIM, True, 3, (0.0, 2.0, 1.0)
    i = {"tick": 1, "mx": 0.0, "my": 1.0, "mz": 0.0, "f": (0.0, 0.0, 1.0), "r": (1.0, 0.0, 0.0), "v": (0.0, 2.0, 1.0),
         "p": (0.0, 0.01, 0.0), "o2": R.OXYGEN_CAPACITY, "spr": 0, "tool": 0, "gnd": 0, "jet": 0, "mut": 0}
    o = step(s, i)
    line("motor switch between two steps: event 64, held vy halved, vx/vz kept", o["o_evt"] & EV_MOTOR_SWITCH and
         o["o_v"] == (0.0, 1.0, 1.0) and not o["o_st"], "%s evt %d" % (o["o_v"], o["o_evt"]))
    o = step(s, dict(i, tick=2, p=(0.0, 0.02, 0.0)))
    line("  no second switch on the same side", not o["o_evt"] & EV_MOTOR_SWITCH and o["o_v"] == (0.0, 1.0, 1.0))
    s = State()
    s.lat, s.ctl, s.md, s.eu, s.ph = True, True, SURFACE, False, 0
    o = step(s, dict(i, v=(0.0, -2.0, 0.0), my=0.0, p=(0.0, -0.3, 0.0)))
    line("motor switch on a step tick: v0.y halved before the step", o["o_evt"] & EV_MOTOR_SWITCH and o["o_st"] and
         abs(o["o_v"][1] - (-1.0 * 0.75)) < 1e-12, "%s" % (o["o_v"],))
    up = [r for r in scenario("surface").rows if int(r["o_evt"]) & EV_MOTOR_SWITCH]
    line("surface scenario: one motor switch on the way up (the eye crosses 0 once)", len(up) == 1 and
         from_bits(up[0]["py"]) >= 0.0, "%d switch row(s)" % len(up))
    # finding 3: the contact acceleration
    for gnd, want in ((0, 2.0 * 0.75), (1, 4.5 * 0.75)):
        s = State()
        s.lat, s.ctl, s.md, s.eu = True, True, SWIM, True
        o = step(s, dict(i, my=0.0, mz=1.0, v=(0.0, 0.0, 0.0), p=(0.0, -50.0, 0.0), gnd=gnd))
        line("contact %d: the first step adds %g m/s (x0.75)" % (gnd, want / 0.75), abs(o["o_v"][2] - want) < 1e-12,
             "%.6f" % o["o_v"][2])
    line("swim head limit with the motor switch (surface_head_max)", R.SURFACE_TOP_Y < surface_head_max() <
         R.SWIM_EXIT_EYE_Y, "%.4f m" % surface_head_max())
    print("SWIM REPLAY SELFTEST %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
