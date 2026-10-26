"""S2 view state machine: independent oracle for the "vsm" rows of se-*.jsonl (stdlib only).

The SE plugin runs the view state machine (src/DeepEngineers.Wire/ViewStateMachine.cs) once per SE tick and may log one
"vsm" row per step (src/DeepEngineers.Wire/ViewTrace.cs). This script re-implements the machine from the documents
(ARCHITECTURE §4, ROADMAP S2 state table, CONTRACT §5.4 C5/C7) with the same named constants, replays the rows and
checks:

 replay every row: the step from the row's logged state and inputs gives the logged outputs (local replay), and
 the state after it equals the next row's logged state when the rows are contiguous (step n, n + 1).
 Divergence means the plugin did not run the documented machine (a seeded failure such as S2.M4 turns this
 red: the oracle replays with the correct constants unless --honour-mutant).
 order crossing order on the logged outputs (design F): the scene is skipped only after a margins-on request was
 acknowledged by the render thread (margins on for >= 1 presented frame); margins drop only once a
 scene-drawn request was acknowledged after the last skip; a skip never without margins.
 zone the logged zone agrees with rho (CORE <= 1,950, TILE VOID <= 2,048, OUTER VOID <= 7,800 in / 7,900 out,
 FAR beyond); beyond 7,900 m (zone FAR or off-site) the view is never Underwater and the scene never skipped
 ("SE view beyond 7,900 m", S2.N2). zone_exit says whether a window
 really exercised "Underwater -> Surfacing at once past 7,900 m": Underwater before the first FAR row, and
 Surfacing on that very row.
 n2 ROADMAP S2.N2 on the logged view states: a switch is a change into or out of Underwater. Trace (a): 0
 switches. Trace (b): switches never closer than 0.25 s and at most 4 in any 1 s window (half-open).

Row format "vsm" version 4 (one JSON object per line; booleans 0/1; doubles compared by thresholds also as exact
IEEE-754 bits, 16 hex digits). Version 2 adds the input mrd (the render thread's live margins
state, ViewInputs.MarginsReady); a version 1 row has no mrd and is replayed with mrd = 1. Version 3 has the version 2 keys and marks the machine whose Prime quiet timeout is 10 s below +5 m (3 s from +5 m
up); version 1 and 2 rows are replayed with the 3 s timeout at every altitude (the machine that wrote them). Version 4 adds the picture policy: input pp (0 Site, 1 Waterline), state s_sp s_bt s_ps s_pt (picture latch,
bandTick, last latch change), outputs o_sp o_pe o_pb o_prs o_pp (picture, edge -1/0/+1, picture blocked, picture
reason, effective policy); rows of version 3 or lower are replayed as the Waterline policy (S2's machine), their v4 keys
absent:
 {"ev":"vsm","v","n":step,"tick":T,"us":nowUs,
 inputs bu site zone rho rhob y yb vy vyb peer ph snf pst abt zok pau fg gui cur ld kau evt ack mrd dm mut pp
 state s_v s_a s_sw s_ls s_pc s_bw s_xt s_rp s_rt s_rs s_rq s_ma s_pf s_pz s_mf(bits) s_ms s_mu s_sp s_bt s_ps s_pt
 (before the step)
 outputs o_vs o_am o_pm o_fps o_rq o_rs o_zr o_fz o_xt o_rdy o_blk o_ov o_c5f o_bus o_xf o_sg o_sm o_wm o_aw o_ma
 o_mix(bits) o_rsn o_sp o_pe o_pb o_prs o_pp}

S7a picture policy (ARCHITECTURE §4.2 "Picture policy (S7a)", CONTRACT §5.4 "C5/C7 semantics for S7a"). Site (the
default) keeps S2's states and transitions; Subnautica's picture follows a latch that turns on in Approach once
Subnautica is ready (below +300 m, snReady, z-order, margins presented, 0.25 s dwell, a frame presented since the band
entry) and off in Space or at once on a site, peer or re-streaming loss; margins in every non-Space state, Subnautica
Full in every non-Space state; the only picture swap is at the band. The extra checks of the S7a scenarios (band,
band-fast, band-hover, site-dive, site-peer, site-teleport, site-aprime) are in the selftest.

 python -I tests/s2/state_traces.py replay SE.jsonl [...] [--honour-mutant] [--json OUT]
 python -I tests/s2/state_traces.py n2 SE.jsonl --case a|b [--from-tick A --to-tick B]
 python -I tests/s2/state_traces.py gen OUT.jsonl --scenario NAME [--mutant M4|M10|S7A_M1] [--picture site|waterline]
 python -I tests/s2/state_traces.py --selftest
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

# ------------------------------------------------------------------------------------------------ constants (named
# as in ViewThresholds / SiteZones / AudioMix; values from ARCHITECTURE §4.2, §5.3, §5.13, ROADMAP S2, CONTRACT §5.4)
APPROACH_ENTER_Y = 400.0          # Space -> Approach when y < the band enter (live setting since
APPROACH_EXIT_Y = 450.0           # Approach -> Space when y > the band exit (default +450 m), held 1 s (F15)
APPROACH_LEAVE_DWELL_US = 1000000

def set_band(enter: float, exit: float) -> None:
    """The plugin's band (env DEEPENGINEERS_BAND / de.s7 band); the selftest pins +300 / +350 m, replays take --band."""
    global APPROACH_ENTER_Y, APPROACH_EXIT_Y
    assert 0.0 <= enter < exit, (enter, exit)
    APPROACH_ENTER_Y, APPROACH_EXIT_Y = float(enter), float(exit)
APPROACH_PREDICT_S = 8.0          #... also Approach when y <= 300 + 8 * -vy (vy < 0); kept while y <= 350 + 8 * -vy
PRIME_Y = 1.0                     # Track -> Prime when y <= +1 m
PRIME_DESCENT_Y = 5.0             #... or y <= +5 m with vy <= -0.2 m/s
PRIME_DESCENT_VY = -0.2
PRIME_PREDICT_S = 4.0             #... or a crossing predicted within 4 s (y <= 4 * -vy, vy < 0); 2 s until (fast dives)
PRIME_QUIET_US = 3000000          # Prime -> Track after 3 s without these and y > +1 m, while y >= +5 m
PRIME_QUIET_NEAR_Y = 5.0          # re-dive fix (vsm/3): below +5 m (strict) the quiet timeout is...
PRIME_QUIET_NEAR_US = 10000000    #...
VSM_VERSION = 5                   # the rows this oracle writes; it reads 1, 2, 3 (as Waterline), 4 (no exit dwell) and 5
UNDERWATER_ENTER_Y = -0.20        # Prime -> Underwater needs y <= -0.20 m
UNDERWATER_EXIT_Y = -0.05         # Underwater -> Surfacing when y > -0.05 m (after the dwell)
MIN_DWELL_US = 250000             # 0.25 s between Approach <-> Underwater switches
CORE_HALF_M = 1950.0
TILE_HALF_M = 2048.0
ZONE_IN_M = 7800.0
ZONE_OUT_M = 7900.0
UNDERWATER_MAX_RHO_M = ZONE_IN_M
FRESHNESS_TICKS = 4               # C7 presentedSeTick >= tick - 4
AHEAD_TICKS = 1                   # stamps more than one tick ahead are stale
RESETTLE_SETTLE_CAP_TICKS = 180      #: the settled wait after a jump, capped once the area is built (ViewThresholds.ResettleSettleCapTicks)
READY_PHASE_MIN, READY_PHASE_MAX = 6, 6   # snPhase Active only
IDLE_FPS, TRACK_FPS, FULL_FPS, UNFOCUSED_UNDERWATER_FPS = 10, 10, 0, 30
MUTANT_M4_WATERLINE_Y = -0.125
CROSSFADE_MS = 300.0
CROSSFADE_US = 300000
SN_BUS_UNDERWATER, SE_GAME_UNDERWATER, SE_MUSIC_UNDERWATER, WATERMOD_UNDERWATER = 1.0, 0.7, 0.0, 0.0
TICK_US_NUM, TICK_US_DEN = 1000000, 60
# S7a picture policy (ViewPicture, ViewThresholds.PictureDwellUs; ARCHITECTURE §4.2 "Picture policy (S7a)")
PICTURE_SITE, PICTURE_WATERLINE = 0, 1
PICTURE_DWELL_US = 250000         # 0.25 s between two picture-latch changes (the on edge only)
PICTURE_NAMES = {PICTURE_SITE: "site", PICTURE_WATERLINE: "waterline"}

# enumerations (CONTRACT §5.4)
SPACE, APPROACH, UNDERWATER, SURFACING = 0, 1, 2, 3
AM_NONE, AM_TRACK, AM_PRIME = 0, 1, 2
PM_IDLE, PM_TRACK, PM_FULL = 0, 1, 2
Z_CORE, Z_TILE, Z_OUTER, Z_FAR, Z_OFFSITE = 0, 1, 2, 3, 4
PEER_UNKNOWN, PEER_ALIVE, PEER_STALLED, PEER_EXITED = 0, 1, 2, 3
DM_F, DM_APRIME, DM_C = 0, 1, 2
SNF_SETTLED, SNF_AREA, SNF_RECT, SNF_UI = 1 << 1, 1 << 4, 1 << 5, 1 << 6
C5_FG, C5_PAUSED, C5_SEE, C5_MARGINS, C5_ZOK, C5_MIX, C5_GUI = 1, 2, 8, 32, 64, 128, 256
C5_PICTURE_SITE = 512             # S7a conventions C5FlagsExt.PictureSite (bit 9): effective policy Site
OV_PAUSED, OV_UNFOCUSED, OV_STALLED, OV_EXITED = 1, 2, 4, 8
EV_TELEPORT, EV_NEWGEN, EV_LINKREC, EV_WORLDLOAD = 1, 2, 4, 8
MUT_M4, MUT_M11, MUT_M10 = 1, 2, 4
M_S7A_M1 = 1 << 3                 # S7a.M1: the machine runs Waterline while Site is selected (ViewMutants.S7aM1Waterline)
RQ_MARGINS, RQ_SKIP, RQ_ALPHA, RQ_LOWERED = 1, 2, 4, 8
B_NOTPRIME, B_Y, B_RHO, B_PEER, B_PHASE, B_AREA, B_SETTLE, B_FRESH, B_RECT, B_UI, B_ZORDER, B_DWELL, B_CROSSING, \
    B_RENDER, B_MODE, B_SITE = [1 << k for k in range(16)]
B_BAND, B_NOT_APPROACH, B_HIGH = 1 << 16, 1 << 17, 1 << 18   # S7a picture-latch guards (ViewBlock.Band/NotApproach/High)
B_SNREADY = B_PEER | B_PHASE | B_AREA | B_SETTLE | B_FRESH | B_RECT | B_UI
R_NONE, R_WORLDLOAD, R_ENTER_APPROACH, R_LEAVE_SITE, R_LEAVE_PEER, R_LEAVE_ALT, R_ENTER_PRIME, R_PRIME_QUIET, \
    R_ENTER_UW, R_SURF_WATERLINE, R_SURF_SITE, R_SURF_PEER, R_SURF_RESETTLE, R_SURFACED_PRIME, R_SURFACED_SPACE = range(15)
R_PICTURE_ON, R_PICTURE_OFF_SPACE, R_PICTURE_OFF_SITE, R_PICTURE_OFF_PEER, R_PICTURE_OFF_RESETTLE = range(15, 20)
VIEW_NAMES = {SPACE: "Space", APPROACH: "Approach", UNDERWATER: "Underwater", SURFACING: "Surfacing"}
U32 = 0xFFFFFFFF

STATE_KEYS_V3 = ("s_v", "s_a", "s_sw", "s_ls", "s_pc", "s_bw", "s_xt", "s_rp", "s_rt", "s_rs", "s_rq", "s_ma", "s_pf",
                 "s_pz", "s_mf", "s_ms", "s_mu")
STATE_KEYS_V4 = STATE_KEYS_V3 + ("s_sp", "s_bt", "s_ps", "s_pt")
STATE_KEYS = STATE_KEYS_V4 + ("s_la", "s_lt")
OUTPUT_KEYS_V3 = ("o_vs", "o_am", "o_pm", "o_fps", "o_rq", "o_rs", "o_zr", "o_fz", "o_xt", "o_rdy", "o_blk", "o_ov",
                  "o_c5f", "o_bus", "o_xf", "o_sg", "o_sm", "o_wm", "o_aw", "o_ma", "o_mix", "o_rsn")
OUTPUT_KEYS = OUTPUT_KEYS_V3 + ("o_sp", "o_pe", "o_pb", "o_prs", "o_pp")
FLOAT_OUTPUTS = ("o_bus", "o_xf", "o_sg", "o_sm", "o_wm")

# ------------------------------------------------------------------------------------------------ helpers
def bits_of(x: float) -> str:
    return "%016x" % struct.unpack("<Q", struct.pack("<d", x))[0]

def from_bits(h: str) -> float:
    return struct.unpack("<d", struct.pack("<Q", int(h, 16)))[0]

def f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]

def is_dive(zone: int) -> bool:
    return zone in (Z_CORE, Z_TILE, Z_OUTER)

def classify_zone(on_site: bool, rho: float, in_zone: bool):
    """The C5 zone with the 7,800/7,900 m hysteresis; returns (zone, in_zone)."""
    if not on_site or math.isnan(rho):
        return Z_OFFSITE, False
    in_zone = rho <= ZONE_OUT_M if in_zone else rho <= ZONE_IN_M
    if not in_zone:
        return Z_FAR, False
    if rho <= CORE_HALF_M:
        return Z_CORE, True
    if rho <= TILE_HALF_M:
        return Z_TILE, True
    return Z_OUTER, True

def prime_condition(y: float, vy: float) -> bool:
    if y <= PRIME_Y:
        return True
    if y <= PRIME_DESCENT_Y and vy <= PRIME_DESCENT_VY:
        return True
    return vy < 0.0 and y <= PRIME_PREDICT_S * -vy

def prime_quiet_us(y: float, version: int = VSM_VERSION) -> int:
    """Prime -> Track quiet timeout at altitude y: 10 s below +5 m from vsm/3 on, else 3 s."""
    if version >= 3 and y < PRIME_QUIET_NEAR_Y:
        return PRIME_QUIET_NEAR_US
    return PRIME_QUIET_US

def prime_like(view: int, approach: int) -> bool:
    """Prime, Underwater, Surfacing: the margins states of design F under the Waterline policy (S2)."""
    return (view == APPROACH and approach == AM_PRIME) or view in (UNDERWATER, SURFACING)

def effective_picture(picture: int, mutants: int) -> int:
    """The policy the machine runs: Waterline when selected or under S7a.M1, Site for any other value."""
    if picture == PICTURE_WATERLINE or (mutants & M_S7A_M1):
        return PICTURE_WATERLINE
    return PICTURE_SITE

def margins_wanted(view: int, approach: int, picture: int) -> bool:
    """Design F margins wanted (the z-order pulse fires on its rise): S2 Prime-like under Waterline, non-Space under Site."""
    if picture == PICTURE_WATERLINE:
        return prime_like(view, approach)
    return view != SPACE

def perf_for(view: int, approach: int, picture: int) -> int:
    """C5 perfMode: Idle in Space; Waterline Track in Approach/Track; Full elsewhere (Site: every non-Space state)."""
    if view == SPACE:
        return PM_IDLE
    if picture == PICTURE_WATERLINE and view == APPROACH and approach == AM_TRACK:
        return PM_TRACK
    return PM_FULL

def mix_level(frm: float, to_uw: bool, start_us: int, now_us: int) -> float:
    target = 1.0 if to_uw else 0.0
    dt = now_us - start_us
    if dt <= 0:
        return frm
    if dt >= CROSSFADE_US:
        return target
    return frm + (target - frm) * (dt / CROSSFADE_US)

def lerp(above: float, under: float, level: float) -> float:
    return above + (under - above) * level

# ------------------------------------------------------------------------------------------------ the machine
class State:
    __slots__ = ("left_armed", "left_since_us", "steps", "view", "approach", "has_switched", "last_switch_us", "prime_cond_us", "below",
                 "crossing_tick", "resettle", "resettle_tick", "render_seq", "request", "margins_applied",
                 "prev_fg", "prev_zok", "mix_from", "mix_start_us", "mix_to_uw",
                 "picture", "band_tick", "pic_switched", "pic_switch_us")

    def __init__(self):
        self.steps = 0
        self.view = SPACE
        self.approach = AM_NONE
        self.has_switched = False
        self.last_switch_us = 0
        self.prime_cond_us = 0
        self.below = False
        self.crossing_tick = 0
        self.resettle = False
        self.resettle_tick = 0
        self.render_seq = 0
        self.request = 0
        self.margins_applied = False
        self.prev_fg = False
        self.prev_zok = False
        self.mix_from = 0.0
        self.mix_start_us = 0
        self.mix_to_uw = False
        self.picture = False          # S7a: the picture latch (Site); view == Underwater under Waterline
        self.band_tick = 0            # S7a: SE tick of the last Space -> Approach
        self.pic_switched = False     # S7a: the latch changed at pic_switch_us (picture dwell)
        self.pic_switch_us = 0
        self.left_armed = False       # F15: the altitude exit condition holds since left_since_us (Space after 1 s)
        self.left_since_us = 0

    def copy(self) -> "State":
        c = State()
        for k in State.__slots__:
            setattr(c, k, getattr(self, k))
        return c

    def to_row(self) -> dict:
        return {"s_v": self.view, "s_a": self.approach, "s_sw": int(self.has_switched), "s_ls": self.last_switch_us,
                "s_pc": self.prime_cond_us, "s_bw": int(self.below), "s_xt": self.crossing_tick,
                "s_rp": int(self.resettle), "s_rt": self.resettle_tick, "s_rs": self.render_seq, "s_rq": self.request,
                "s_ma": int(self.margins_applied), "s_pf": int(self.prev_fg), "s_pz": int(self.prev_zok),
                "s_mf": bits_of(self.mix_from), "s_ms": self.mix_start_us, "s_mu": int(self.mix_to_uw),
                "s_sp": int(self.picture), "s_bt": self.band_tick, "s_ps": int(self.pic_switched),
                "s_pt": self.pic_switch_us, "s_la": int(self.left_armed), "s_lt": self.left_since_us}

    @staticmethod
    def from_row(r: dict) -> "State":
        s = State()
        s.steps = int(r["n"])
        s.view = int(r["s_v"])
        s.approach = int(r["s_a"])
        s.has_switched = bool(r["s_sw"])
        s.last_switch_us = int(r["s_ls"])
        s.prime_cond_us = int(r["s_pc"])
        s.below = bool(r["s_bw"])
        s.crossing_tick = int(r["s_xt"])
        s.resettle = bool(r["s_rp"])
        s.resettle_tick = int(r["s_rt"])
        s.render_seq = int(r["s_rs"])
        s.request = int(r["s_rq"])
        s.margins_applied = bool(r["s_ma"])
        s.prev_fg = bool(r["s_pf"])
        s.prev_zok = bool(r["s_pz"])
        s.mix_from = from_bits(r["s_mf"])
        s.mix_start_us = int(r["s_ms"])
        s.mix_to_uw = bool(r["s_mu"])
        # vsm/1-3 rows: the Waterline machine, whose picture is the Underwater view; no band tick, no latch history
        s.picture = bool(r["s_sp"]) if "s_sp" in r else int(r["s_v"]) == UNDERWATER
        s.band_tick = int(r.get("s_bt", 0))
        s.pic_switched = bool(r.get("s_ps", 0))
        s.pic_switch_us = int(r.get("s_pt", 0))
        s.left_armed = bool(r.get("s_la", 0))     # F15: the altitude-exit dwell; older rows: not armed
        s.left_since_us = int(r.get("s_lt", 0))
        return s

def inputs_from_row(r: dict) -> dict:
    d = {k: r[k] for k in ("tick", "us", "bu", "site", "zone", "peer", "ph", "snf", "pst", "abt", "zok", "pau", "fg",
                           "gui", "cur", "ld", "evt", "ack", "dm", "mut")}
    d["kau"] = r.get("kau", 0)
    d["mrd"] = r.get("mrd", 1)     # version 1 rows: the acknowledgement implied the margins
    d["v"] = int(r.get("v", VSM_VERSION))   # version 1/2 rows: the 3 s Prime quiet timeout at every altitude
    d["dw"] = 1 if "s_la" in r else 0       # the altitude-exit dwell (F15) only when the writer had it
 # version 1-3 rows: the Waterline policy (S2's machine wrote them)
    d["pp"] = int(r.get("pp", PICTURE_SITE)) if d["v"] >= 4 else PICTURE_WATERLINE
    d["rho"] = from_bits(r["rhob"]) if "rhob" in r else float(r["rho"] if r["rho"] is not None else "nan")
    d["y"] = from_bits(r["yb"]) if "yb" in r else float(r["y"] if r["y"] is not None else "nan")
    d["vy"] = from_bits(r["vyb"]) if "vyb" in r else float(r["vy"] if r["vy"] is not None else "nan")
    return d

def step(s: State, i: dict) -> dict:
    """One SE tick: mutates s, returns the outputs (trace keys o_*). Written from the documents, not from the C#."""
    tick = int(i["tick"])
    now = int(i["us"])
    mut4 = (int(i["mut"]) & MUT_M4) != 0
    version = int(i.get("v", VSM_VERSION))
    pp = int(i.get("pp", PICTURE_SITE)) if version >= 4 else PICTURE_WATERLINE
    policy = effective_picture(pp, int(i["mut"]))
    site = policy == PICTURE_SITE
    prev_picture = s.picture
    pic_reason = R_NONE
    ev = int(i["evt"])
    if s.steps == 0:
        ev |= EV_WORLDLOAD
    reason = R_NONE
    if ev & EV_WORLDLOAD:
        s.view, s.approach = SPACE, AM_NONE
        s.has_switched, s.last_switch_us, s.prime_cond_us = False, 0, 0
        s.below, s.crossing_tick, s.prev_zok = False, 0, False
        reason = R_WORLDLOAD
    if ev & (EV_WORLDLOAD | EV_TELEPORT | EV_NEWGEN | EV_LINKREC):
        s.resettle, s.resettle_tick = True, tick

    render_acked = int(i["ack"]) == s.render_seq
    if render_acked:
        s.margins_applied = (s.request & RQ_MARGINS) != 0

    y = float(i["y"])
    vy = float(i["vy"])
    if math.isnan(vy) or math.isinf(vy):
        vy = 0.0
    y_finite = not (math.isnan(y) or math.isinf(y))
    thr_in = MUTANT_M4_WATERLINE_Y if mut4 else UNDERWATER_ENTER_Y
    thr_out = MUTANT_M4_WATERLINE_Y if mut4 else UNDERWATER_EXIT_Y
    below_enter = y <= thr_in
    above_exit = y > thr_out
    if not s.below and below_enter:
        s.below, s.crossing_tick = True, tick
    elif s.below and above_exit:
        s.below = False
    if s.below and (ev & EV_TELEPORT):
        s.crossing_tick = tick

    site_ok = bool(i["bu"]) and bool(i["site"]) and is_dive(int(i["zone"])) and y_finite
    peer_ok = int(i["peer"]) == PEER_ALIVE
    snf = int(i["snf"])
    area = (snf & SNF_AREA) != 0
    settled = (snf & SNF_SETTLED) != 0
    ahead = tick + AHEAD_TICKS
    abt, pst = int(i["abt"]), int(i["pst"])
    #: the settled wait is capped at RESETTLE_SETTLE_CAP_TICKS from the jump once the area is built (ViewThresholds)
    if s.resettle and peer_ok and area and s.resettle_tick <= abt <= ahead and (settled or tick >= s.resettle_tick + RESETTLE_SETTLE_CAP_TICKS):
        s.resettle = False

    blocked = 0
    if not peer_ok:
        blocked |= B_PEER
    if not (READY_PHASE_MIN <= int(i["ph"]) <= READY_PHASE_MAX):
        blocked |= B_PHASE
    if not area:
        blocked |= B_AREA
    if s.resettle or ((int(i["mut"]) & MUT_M10) and not settled):
        blocked |= B_SETTLE
    if pst + FRESHNESS_TICKS < tick or pst > ahead:
        blocked |= B_FRESH
    if not (snf & SNF_RECT):
        blocked |= B_RECT
    if not (snf & SNF_UI):
        blocked |= B_UI
    sn_ready = (blocked & B_SNREADY) == 0

    dwell_ok = mut4 or not s.has_switched or now - s.last_switch_us >= MIN_DWELL_US
    pcond = prime_condition(y, vy)
    mode = int(i["dm"])
    mode_f, mode_a = mode == DM_F, mode == DM_APRIME
    # review fix (finding 11): the acknowledgement is sticky; the live margins state is required too
    margins_ready = bool(s.request & RQ_MARGINS) and not (s.request & RQ_SKIP) and render_acked and bool(i.get("mrd", 1))
    # S7a (Site) step 2: the picture latch drops at once on a site, peer or re-streaming loss
    if site and s.picture:
        if not site_ok:
            off = R_PICTURE_OFF_SITE
        elif not peer_ok:
            off = R_PICTURE_OFF_PEER
        elif s.resettle and not sn_ready:
            off = R_PICTURE_OFF_RESETTLE
        else:
            off = R_NONE
        if off != R_NONE:
            s.picture, s.pic_switched, s.pic_switch_us, pic_reason = False, True, now, off
    if not site_ok:
        blocked |= B_SITE
    if not below_enter:
        blocked |= B_Y
    if not (float(i["rho"]) <= UNDERWATER_MAX_RHO_M):
        blocked |= B_RHO
    if not i["zok"]:
        blocked |= B_ZORDER
    if not dwell_ok:
        blocked |= B_DWELL
    if pst < s.crossing_tick:
        blocked |= B_CROSSING
    if not mode_f and not mode_a:
        blocked |= B_MODE
    elif site:
        if not s.picture:           # S7a: Underwater only under the picture latch
            blocked |= B_RENDER
    elif mode_f and not margins_ready:
        blocked |= B_RENDER
    prev_view, prev_approach = s.view, s.approach
    if not (prev_view == APPROACH and prev_approach == AM_PRIME):
        blocked |= B_NOTPRIME

    if s.view == SPACE and site_ok and peer_ok and (y < APPROACH_ENTER_Y or (vy < 0.0 and y <= APPROACH_ENTER_Y + APPROACH_PREDICT_S * -vy)):
        s.view, s.approach, reason = APPROACH, AM_TRACK, R_ENTER_APPROACH
        s.band_tick = tick
    if s.view == APPROACH:
        left_alt = y > APPROACH_EXIT_Y and not (vy < 0.0 and y <= APPROACH_EXIT_Y + APPROACH_PREDICT_S * -vy)
        if not left_alt:
            s.left_armed = False
        elif not s.left_armed:
            s.left_armed, s.left_since_us = True, now
        # F15: held 1 s when the writer knew the dwell (rows carrying s_la/s_lt, input dw); older plugins left at once
        left_held = left_alt and (not int(i.get("dw", 1)) or now - s.left_since_us >= APPROACH_LEAVE_DWELL_US)
        if not site_ok or not peer_ok or left_held:
            reason = R_LEAVE_SITE if not site_ok else (R_LEAVE_PEER if not peer_ok else R_LEAVE_ALT)
            s.view, s.approach = SPACE, AM_NONE
            s.left_armed = False
        elif s.approach == AM_TRACK:
            if pcond:
                s.approach, s.prime_cond_us, reason = AM_PRIME, now, R_ENTER_PRIME
        else:
            if pcond:
                s.prime_cond_us = now
            elif y > PRIME_Y and now - s.prime_cond_us >= prime_quiet_us(y, int(i.get("v", VSM_VERSION))):
                s.approach, reason = AM_TRACK, R_PRIME_QUIET
            if s.approach == AM_PRIME and blocked == 0:
                s.view, s.has_switched, s.last_switch_us, reason = UNDERWATER, True, now, R_ENTER_UW
    elif prev_view == UNDERWATER:
        why = R_NONE
        if not site_ok:
            why = R_SURF_SITE
        elif not peer_ok:
            why = R_SURF_PEER
        elif s.resettle and not sn_ready:
            why = R_SURF_RESETTLE
        elif above_exit and dwell_ok:
            why = R_SURF_WATERLINE
        if why != R_NONE:
            s.view, s.has_switched, s.last_switch_us, reason = SURFACING, True, now, why
    elif prev_view == SURFACING:
        # S7a (Site): under the latch the unchanged request completes Surfacing (one tick); else S2's scene-drawn rule
        if render_acked and ((site and s.picture) or not (s.request & RQ_SKIP)):
            if site_ok and peer_ok and not (y > APPROACH_EXIT_Y):
                s.view, s.approach, s.prime_cond_us, reason = APPROACH, AM_PRIME, now, R_SURFACED_PRIME
            else:
                s.view, s.approach, reason = SPACE, AM_NONE, R_SURFACED_SPACE

    # S7a step 4: the picture latch (Site), or the picture = Underwater (Waterline)
    if site:
        if s.view == SPACE and s.picture:
            s.picture, s.pic_switched, s.pic_switch_us, pic_reason = False, True, now, R_PICTURE_OFF_SPACE
        pb = 0
        if s.view == SPACE or prev_view == SPACE:
            pb |= B_NOT_APPROACH
        if not (y < APPROACH_ENTER_Y):
            pb |= B_HIGH
        if not site_ok:
            pb |= B_SITE
        if not (float(i["rho"]) <= UNDERWATER_MAX_RHO_M):
            pb |= B_RHO
        pb |= blocked & B_SNREADY
        if not i["zok"]:
            pb |= B_ZORDER
        if s.pic_switched and now - s.pic_switch_us < PICTURE_DWELL_US:
            pb |= B_DWELL
        if pst < s.band_tick:
            pb |= B_BAND
        if not mode_f and not mode_a:
            pb |= B_MODE
        elif mode_f and not margins_ready:
            pb |= B_RENDER
        if not s.picture and pb == 0:
            s.picture, s.pic_switched, s.pic_switch_us, pic_reason = True, True, now, R_PICTURE_ON
        pic_blocked = 0 if s.picture else pb
    else:
        under = s.view == UNDERWATER
        if under != s.picture:
            s.picture, s.pic_switched, s.pic_switch_us = under, True, now
        pic_blocked = 0 if s.picture else blocked

    margins = skip = alpha = lowered = False
    if site:
        if mode_f:
            # margins in every non-Space state; after a skip they stay until a scene-drawn request is presented
            hold = bool(s.request & RQ_MARGINS) and (bool(s.request & RQ_SKIP) or not render_acked)
            margins = s.view != SPACE or hold
            skip = s.picture
            alpha = (not skip) and (margins or s.margins_applied)
        elif mode_a:
            menu = bool(i["cur"]) or bool(i["gui"]) or bool(i["pau"])
            if s.picture and not menu:
                lowered = skip = True
            else:
                # the latch dropped: lowered and drawing until a drawn frame was presented; a menu raises at once
                drawn_presented = render_acked and not (s.request & RQ_SKIP)
                lowered = bool(s.request & RQ_LOWERED) and not menu and not drawn_presented
    elif mode_f:
        margins = prime_like(s.view, s.approach)
        skip = s.view == UNDERWATER
        alpha = (not skip) and (margins or s.margins_applied)
    elif mode_a:
        menu = bool(i["cur"]) or bool(i["gui"]) or bool(i["pau"])
        if s.view == UNDERWATER:
            lowered = not menu
        elif s.view == SURFACING:
            lowered = (s.request & RQ_LOWERED) != 0
        skip = s.view == UNDERWATER and lowered
    request = (RQ_MARGINS if margins else 0) | (RQ_SKIP if skip else 0) | (RQ_ALPHA if alpha else 0) | \
              (RQ_LOWERED if lowered else 0)
    if request != s.request:
        s.request = request
        s.render_seq = (s.render_seq + 1) & U32

    fg, zok = bool(i["fg"]), bool(i["zok"])
    wanted = margins_wanted(s.view, s.approach, policy)
    prev_wanted = margins_wanted(prev_view, prev_approach, policy)
    zpulse = wanted and ((not prev_wanted) or (fg and not s.prev_fg) or (not zok and s.prev_zok))
    s.prev_fg, s.prev_zok = fg, zok

    perf = perf_for(s.view, s.approach, PICTURE_WATERLINE if (not mode_f and not mode_a) else policy)   # F16: mode C
    if site and s.view == SPACE and (margins or lowered):   # F1: Full until SE's scene-drawn frame is presented
        perf = PM_FULL
    fps = IDLE_FPS if perf == PM_IDLE else (TRACK_FPS if perf == PM_TRACK else FULL_FPS)
    if s.picture and not fg:          # S2: Underwater (the Waterline picture); S7a Site: the latch
        fps = UNFOCUSED_UNDERWATER_FPS

    mix_uw = s.view == UNDERWATER
    if mix_uw != s.mix_to_uw:
        s.mix_from = mix_level(s.mix_from, s.mix_to_uw, s.mix_start_us, now)
        s.mix_start_us, s.mix_to_uw = now, mix_uw
    m = mix_level(s.mix_from, s.mix_to_uw, s.mix_start_us, now)

    paused = bool(i["pau"])
    peer = int(i["peer"])
    ov = (OV_PAUSED if paused else 0) | (0 if fg else OV_UNFOCUSED) | (OV_STALLED if peer == PEER_STALLED else 0) | \
         (OV_EXITED if peer == PEER_EXITED else 0)
    mix_active = m > 0.0
    c5f = (C5_FG if fg else 0) | (C5_PAUSED if paused else 0) | (C5_SEE if skip else 0) | \
          (C5_MARGINS if margins else 0) | (C5_ZOK if zok else 0) | (C5_MIX if mix_active else 0) | \
          (C5_GUI if i["gui"] else 0) | (C5_PICTURE_SITE if site else 0)
    gate = (fg or bool(i.get("kau", 0))) and not paused and not bool(i["gui"]) and not bool(i["ld"])
    out = {
        "o_vs": s.view, "o_am": s.approach, "o_pm": perf, "o_fps": fps, "o_rq": request, "o_rs": s.render_seq,
        "o_zr": int(zpulse), "o_fz": int(perf == PM_IDLE or paused), "o_xt": s.crossing_tick, "o_rdy": int(sn_ready),
        "o_blk": blocked, "o_ov": ov, "o_c5f": c5f,
        "o_bus": f32(SN_BUS_UNDERWATER) if (mix_uw and fg) else 0.0, "o_xf": CROSSFADE_MS,
        "o_sg": f32(lerp(1.0, SE_GAME_UNDERWATER, m)), "o_sm": f32(lerp(1.0, SE_MUSIC_UNDERWATER, m)),
        "o_wm": f32(lerp(1.0, WATERMOD_UNDERWATER, m)),
        "o_aw": int(gate or (int(i["mut"]) & MUT_M11) != 0), "o_ma": int(mix_active), "o_mix": bits_of(m),
        "o_rsn": reason,
        "o_sp": int(s.picture), "o_pe": 0 if s.picture == prev_picture else (1 if s.picture else -1),
        "o_pb": pic_blocked, "o_prs": pic_reason if site else R_NONE, "o_pp": policy,
    }
    s.steps += 1
    return out

# ------------------------------------------------------------------------------------------------ rows
def make_row(pre: State, i: dict, o: dict) -> dict:
    """A row of the version in i["v"]: version 4 carries the picture keys, versions 1-3 the S2 keys only."""
    version = int(i.get("v", VSM_VERSION))
    r = {"ev": "vsm", "v": version, "n": pre.steps, "tick": i["tick"], "us": i["us"],
         **({"be": round(APPROACH_ENTER_Y, 1), "bx": round(APPROACH_EXIT_Y, 1)} if version >= 5 else {}),
         "bu": int(i["bu"]), "site": int(i["site"]), "zone": i["zone"],
         "rho": round(i["rho"], 2) if math.isfinite(i["rho"]) else None, "rhob": bits_of(i["rho"]),
         "y": round(i["y"], 4) if math.isfinite(i["y"]) else None, "yb": bits_of(i["y"]),
         "vy": round(i["vy"], 4) if math.isfinite(i["vy"]) else None, "vyb": bits_of(i["vy"]),
         "peer": i["peer"], "ph": i["ph"], "snf": i["snf"], "pst": i["pst"], "abt": i["abt"], "zok": int(i["zok"]),
         "pau": int(i["pau"]), "fg": int(i["fg"]), "gui": int(i["gui"]), "cur": int(i["cur"]), "ld": int(i["ld"]),
         "kau": int(i.get("kau", 0)),
         "evt": i["evt"], "ack": i["ack"], "mrd": int(i.get("mrd", 1)), "dm": i["dm"], "mut": i["mut"]}
    if version >= 4:
        r["pp"] = int(i.get("pp", PICTURE_SITE))
    st = pre.to_row()
    for k in (STATE_KEYS if version >= 5 else (STATE_KEYS_V4 if version == 4 else STATE_KEYS_V3)):
        r[k] = st[k]
    for k in (OUTPUT_KEYS if version >= 4 else OUTPUT_KEYS_V3):
        r[k] = round(o[k], 6) if k in FLOAT_OUTPUTS else o[k]
    return r

def band_from_rows(rows: list) -> None:
    """vsm/5 rows carry the plugin's band (be, bx); the oracle follows it."""
    for r in rows:
        if "be" in r and "bx" in r:
            try:
                e, x = float(r["be"]), float(r["bx"])
            except (TypeError, ValueError):
                return
            if (e, x) != (APPROACH_ENTER_Y, APPROACH_EXIT_Y) and 0.0 <= e < x:
                set_band(e, x)
            return

def read_rows(paths) -> list:
    rows = []
    for p in paths:
        with open(p, "r", encoding="utf-8") as fh:
            for ln, line in enumerate(fh, 1):
                line = line.strip()
                if not line or '"vsm"' not in line:
                    continue
                try:
                    r = json.loads(line)
                except ValueError as e:
                    raise SystemExit("state_traces: %s:%d: not JSON (%s)" % (p, ln, e))
                if r.get("ev") != "vsm":
                    continue
                if r.get("v") not in (1, 2, 3, 4, 5):
                    raise SystemExit("state_traces: %s:%d: vsm row version %r (this oracle reads 1 to 5)" % (
                        p, ln, r.get("v")))
                rows.append(r)
    band_from_rows(rows)
    return rows

def row_version(r: dict) -> int:
    return int(r.get("v", VSM_VERSION))

def outputs_equal(a: dict, b: dict) -> list:
    """Keys whose values differ (floats within 1e-5, bit strings exactly); a vsm/1-3 row b has the S2 keys only."""
    bad = []
    for k in (OUTPUT_KEYS if row_version(b) >= 4 else OUTPUT_KEYS_V3):
        if k not in b:
            bad.append(k)
            continue
        if k in FLOAT_OUTPUTS:
            if b[k] is None or abs(float(a[k]) - float(b[k])) > 1e-5:
                bad.append(k)
        elif a[k] != b[k]:
            bad.append(k)
    return bad

def replay(rows: list, honour_mutant: bool = False) -> dict:
    """Local replay of every row and state linkage of contiguous rows."""
    res = {"rows": len(rows), "localMismatch": 0, "linkMismatch": 0, "contiguous": 0, "gaps": 0, "mutantRows": 0,
           "first": []}
    prev_post, prev_n, prev_v = None, None, VSM_VERSION
    for r in rows:
        if r.get("mut"):
            res["mutantRows"] += 1
        if "be" in r and "bx" in r and (float(r["be"]), float(r["bx"])) != (APPROACH_ENTER_Y, APPROACH_EXIT_Y):
            set_band(float(r["be"]), float(r["bx"]))   # a live band change inside the log
        pre = State.from_row(r)
        if prev_n is not None and r["n"] == prev_n + 1:
            res["contiguous"] += 1
            want = prev_post.to_row()
            keys = (STATE_KEYS if "s_la" in r else STATE_KEYS_V4) if row_version(r) >= 4 and prev_v >= 4 else STATE_KEYS_V3
            got = {k: r[k] for k in keys}
            diff = [k for k in keys if want[k] != got[k]]
            if diff:
                res["linkMismatch"] += 1
                if len(res["first"]) < 8:
                    res["first"].append("n=%d tick=%d state after the previous step differs in %s (oracle %s, log %s)" % (
                        r["n"], r["tick"], ",".join(diff), {k: want[k] for k in diff}, {k: got[k] for k in diff}))
                # continue from the logged state (the oracle's own chain would cascade)
        elif prev_n is not None:
            res["gaps"] += 1
        inp = inputs_from_row(r)
        if not honour_mutant:
            inp["mut"] = 0
        post = pre.copy()
        out = step(post, inp)
        bad = outputs_equal(out, r)
        if bad:
            res["localMismatch"] += 1
            if len(res["first"]) < 8:
                res["first"].append("n=%d tick=%d y=%s outputs differ in %s (oracle %s, log %s)" % (
                    r["n"], r["tick"], r.get("y"), ",".join(bad), {k: out[k] for k in bad}, {k: r.get(k) for k in bad}))
        prev_post, prev_n, prev_v = post, r["n"], row_version(r)
    res["pass"] = res["localMismatch"] == 0 and res["linkMismatch"] == 0
    return res

def check_order(rows: list) -> dict:
    """Crossing order on the logged outputs (design F rows only)."""
    res = {"enterChecked": 0, "leaveChecked": 0, "violations": 0, "first": []}

    def bad(msg):
        res["violations"] += 1
        if len(res["first"]) < 8:
            res["first"].append(msg)

    last_skip_seq = None      # newest request seq with SKIP
    cleared_seq = None        # first request seq without SKIP after it
    prev = None
    for r in rows:
        if r["dm"] != DM_F:
            prev = None
            continue
        rq, rs = r["o_rq"], r["o_rs"]
        if (rq & RQ_SKIP) and not (rq & RQ_MARGINS):
            bad("n=%d tick=%d scene skipped without margins (rq=%d)" % (r["n"], r["tick"], rq))
        contiguous = prev is not None and r["n"] == prev["n"] + 1
        if not contiguous:
            last_skip_seq, cleared_seq = None, None   # history unknown across a gap: only the local rules apply
        if contiguous:
            prev_rq = prev["o_rq"]
            if (rq & RQ_SKIP) and not (prev_rq & RQ_SKIP):
                res["enterChecked"] += 1
                ok = (r["s_rq"] & RQ_MARGINS) and not (r["s_rq"] & RQ_SKIP) and r["ack"] == r["s_rs"] and r.get("mrd", 1)
                if not ok:
                    bad("n=%d tick=%d skip started before the margins-on request was presented (s_rq=%d ack=%d s_rs=%d)"
                        % (r["n"], r["tick"], r["s_rq"], r["ack"], r["s_rs"]))
            if (prev_rq & RQ_MARGINS) and not (rq & RQ_MARGINS):
                res["leaveChecked"] += 1
                ok = not (r["s_rq"] & RQ_SKIP) and not (rq & RQ_SKIP)
                if ok and last_skip_seq is not None:
                    ok = cleared_seq is not None and r["ack"] >= cleared_seq
                if not ok:
                    bad("n=%d tick=%d margins dropped before a scene-drawn frame was presented (ack=%d cleared=%s)"
                        % (r["n"], r["tick"], r["ack"], cleared_seq))
        if rq & RQ_SKIP:
            last_skip_seq, cleared_seq = rs, None
        elif last_skip_seq is not None and cleared_seq is None:
            cleared_seq = rs
        if contiguous and (prev["o_rq"] & RQ_MARGINS) and not (rq & RQ_MARGINS):
            last_skip_seq, cleared_seq = None, None
        prev = r
    res["pass"] = res["violations"] == 0
    return res

def check_zone(rows: list) -> dict:
    res = {"checked": 0, "violations": 0, "beyond7900": 0, "first": []}

    def bad(msg):
        res["violations"] += 1
        if len(res["first"]) < 8:
            res["first"].append(msg)

    for r in rows:
        res["checked"] += 1
        rho = from_bits(r["rhob"]) if "rhob" in r else float(r["rho"])
        zone = r["zone"]
        if r["site"] and not math.isnan(rho):
            if rho > ZONE_OUT_M and zone != Z_FAR:
                bad("n=%d rho=%.2f > 7,900 but zone %d" % (r["n"], rho, zone))
            if rho <= ZONE_IN_M:
                want = Z_CORE if rho <= CORE_HALF_M else (Z_TILE if rho <= TILE_HALF_M else Z_OUTER)
                if zone != want:
                    bad("n=%d rho=%.2f zone %d, expected %d" % (r["n"], rho, zone, want))
            if ZONE_IN_M < rho <= ZONE_OUT_M and zone not in (Z_OUTER, Z_FAR):
                bad("n=%d rho=%.2f in the hysteresis band but zone %d" % (r["n"], rho, zone))
        elif not r["site"] and zone != Z_OFFSITE:
            bad("n=%d off site but zone %d" % (r["n"], zone))
        if not is_dive(zone):
            res["beyond7900"] += 1
            if r["o_vs"] == UNDERWATER or (r["o_rq"] & RQ_SKIP):
                bad("n=%d tick=%d zone %d (FAR/off-site) but view %s rq=%d" % (r["n"], r["tick"], zone,
                                                                              VIEW_NAMES.get(r["o_vs"]), r["o_rq"]))
    res["pass"] = res["violations"] == 0
    return res

def zone_exit(rows: list) -> dict:
    """S2.N2 zone exit, discriminating: the first row whose zone is FAR (or off site)
 after an Underwater row; the exit was exercised when such a row exists, and happened at once when that row's step
 left Underwater (Surfacing, reason "surface: site/zone/bridge") on the same tick. ticksToSpace: steps from that row to
 the first Space output (SE's own view)."""
    res = {"underwaterBefore": False, "exercised": False, "atOnce": None, "exitTick": None, "ticksToSpace": None,
           "farRows": 0}
    seen_uw = False
    exit_i = None
    for k, r in enumerate(rows):
        far = not is_dive(int(r["zone"]))
        if far:
            res["farRows"] += 1
        if exit_i is None:
            if far and seen_uw:
                exit_i = k
            elif not far and (r["o_vs"] == UNDERWATER or r["s_v"] == UNDERWATER):
                seen_uw = True
    res["underwaterBefore"] = seen_uw
    if exit_i is not None:
        r = rows[exit_i]
        res["exercised"] = r["s_v"] == UNDERWATER
        res["exitTick"] = r["tick"]
        res["atOnce"] = r["s_v"] == UNDERWATER and r["o_vs"] == SURFACING and r["o_rsn"] == R_SURF_SITE
        for j in range(exit_i, len(rows)):
            if rows[j]["o_vs"] == SPACE:
                res["ticksToSpace"] = rows[j]["tick"] - r["tick"]
                break
    res["pass"] = bool(res["exercised"] and res["atOnce"])
    return res

def switches(rows: list, from_tick=None, to_tick=None) -> list:
    """Times (us) at which the logged view went into or out of Underwater, inside the tick window."""
    out = []
    prev = None
    for r in rows:
        if from_tick is not None and r["tick"] < from_tick:
            prev = r
            continue
        if to_tick is not None and r["tick"] > to_tick:
            break
        if prev is not None:
            was, now = prev["o_vs"] == UNDERWATER, r["o_vs"] == UNDERWATER
            if was != now:
                out.append(r["us"])
        prev = r
    return out

def n2_metrics(sw: list) -> dict:
    gaps = [b - a for a, b in zip(sw, sw[1:])]
    max_per_s = 0
    j = 0
    for k in range(len(sw)):
        while sw[k] - sw[j] >= 1000000:
            j += 1
        max_per_s = max(max_per_s, k - j + 1)
    return {"switches": len(sw), "minSpacingS": (min(gaps) / 1e6) if gaps else None, "maxPerSecond": max_per_s}

def n2_verdict(case: str, m: dict) -> bool:
    if case == "a":
        return m["switches"] == 0
    return (m["minSpacingS"] is None or m["minSpacingS"] >= MIN_DWELL_US / 1e6) and m["maxPerSecond"] <= 4

# ------------------------------------------------------------------------------------------------ synthetic world
class World:
    """Scripted inputs: Subnautica ready and presenting one tick late, the render thread acknowledging one step late."""

    def __init__(self, mutants=0, display=DM_F, sn_lag=1, ack_lag=1, version=VSM_VERSION, picture=PICTURE_SITE):
        self.mutants, self.display, self.sn_lag, self.ack_lag = mutants, display, sn_lag, ack_lag
        # 4: the current machine (picture policy from `picture`); 3: S2's machine (Waterline); 2: the 3 s Prime quiet
 # rule before
        self.version = version
        self.picture = picture
        self.state = State()
        self.rows = []
        self.tick = 1000
        self.seq_hist = []
        self.in_zone = False
        self.peer = PEER_ALIVE
        self.fg, self.paused, self.gui, self.zok = True, False, False, True
        self.events = 0
        self.settle_at = None      # tick from which Subnautica reports settled + built after an event
        self.settled_only_when_still = False   # IsWorldSettled rarely holds while the camera moves
        self.last_y = None
        self.mrd = True             # the render thread's live margins state (vsm/2 "mrd")

    def run(self, ticks: int, y_of, x_of=lambda t: 100.0, z_of=lambda t: 100.0):
        for _ in range(ticks):
            t = self.tick
            y = y_of(t)
            vy = 0.0 if self.last_y is None else (y - self.last_y) * 60.0
            self.last_y = y
            rho = max(abs(x_of(t)), abs(z_of(t)))
            zone, self.in_zone = classify_zone(True, rho, self.in_zone)
            pst = t - self.sn_lag
            snf = SNF_RECT | SNF_UI
            abt = pst
            if self.settle_at is None or t >= self.settle_at:
                snf |= SNF_SETTLED | SNF_AREA
                if self.settled_only_when_still and abs(vy) >= 0.5:
                    snf &= ~SNF_SETTLED
            else:
                abt = max(0, self.settle_at - 400)
            ack = self.seq_hist[-self.ack_lag] if len(self.seq_hist) >= self.ack_lag else 0
            i = {"tick": t, "us": t * TICK_US_NUM // TICK_US_DEN, "bu": True, "site": True, "zone": zone,
                 "rho": rho, "y": y, "vy": vy, "peer": self.peer, "ph": 6, "snf": snf, "pst": pst, "abt": abt,
                 "zok": self.zok, "pau": self.paused, "fg": self.fg, "gui": self.gui, "cur": False, "ld": False,
                 "evt": self.events, "ack": ack, "mrd": self.mrd, "dm": self.display, "mut": self.mutants,
                 "v": self.version, "pp": self.picture, "dw": 1 if self.version >= 5 else 0}
            self.events = 0
            pre = self.state.copy()
            o = step(self.state, i)
            self.rows.append(make_row(pre, i, o))
            self.seq_hist.append(o["o_rs"])
            self.tick += 1

    def teleport(self, settle_ticks=90):
        self.events |= EV_TELEPORT
        self.settle_at = self.tick + settle_ticks

def bob(centre, amp, hz):
    return lambda t: centre + amp * math.sin(2.0 * math.pi * hz * (t / 60.0))

def ramp(w: World, ticks: int, y0: float, y1: float):
    """Linear y from y0 to y1 over `ticks` steps (last step at y1), like a de.motion line."""
    t0 = w.tick
    w.run(ticks, lambda t: y0 + (y1 - y0) * (t - t0 + 1) / float(ticks))

# re-dive timeline, at 60 ticks/s: de.motion lines at 1.5 m/s between +2 m and -3 m
# (200 ticks), 1 s of path slack after each line, 2 s between quick cycles, the near pause at +2 m, the far pause at +8 m
REDIVE_CYCLES = 10
REDIVE_NEAR_PAUSE_TICKS = 360     # 6 s (launcher N7B_NEAR_PAUSE_S)
REDIVE_FAR_PAUSE_TICKS = 720      # 12 s (launcher N7B_FAR_PAUSE_S)

def redive(w: World) -> dict:
    """Runs the n7b timeline on `w`; returns the tick windows {"quick": (a, b), "near": (a, b), "far": (a, b)}."""
    win = {}
    w.run(120, lambda t: -3.0)
    a = w.tick
    for _k in range(REDIVE_CYCLES):
        ramp(w, 200, -3.0, 2.0)
        w.run(60 + 120, lambda t: 2.0)
        ramp(w, 200, 2.0, -3.0)
        w.run(60, lambda t: -3.0)
    win["quick"] = (a, w.tick - 1)
    ramp(w, 200, -3.0, 2.0)
    w.run(60, lambda t: 2.0)
    a = w.tick
    w.run(REDIVE_NEAR_PAUSE_TICKS, lambda t: 2.0)
    ramp(w, 200, 2.0, -3.0)
    w.run(60, lambda t: -3.0)
    win["near"] = (a, w.tick - 1)
    a = w.tick
    ramp(w, 440, -3.0, 8.0)
    w.run(60 + REDIVE_FAR_PAUSE_TICKS, lambda t: 8.0)
    ramp(w, 440, 8.0, -3.0)
    w.run(60, lambda t: -3.0)
    win["far"] = (a, w.tick - 1)
    ramp(w, 200, -3.0, 2.0)
    w.run(60, lambda t: 2.0)
    return win

def band_cycles(w: World, cycles: int, ticks: int, top: float = 600.0, bottom: float = 200.0) -> list:
    """Descents top -> bottom and ascents back (S7a.N2), `ticks` per leg; returns [(descent a, b), (ascent a, b)] windows."""
    win = []
    w.run(60, lambda t: top)
    for _k in range(cycles):
        a = w.tick
        ramp(w, ticks, top, bottom)
        w.run(120, lambda t: bottom)
        win.append(("descent", a, w.tick - 1))
        a = w.tick
        ramp(w, ticks, bottom, top)
        w.run(120, lambda t: top)
        win.append(("ascent", a, w.tick - 1))
    return win

def hover(centre: float, amp: float, hz: float, t0: int):
    return lambda t: centre + amp * math.sin(2.0 * math.pi * hz * ((t - t0) / 60.0))

def scenario(name: str, mutants=0, version=VSM_VERSION, picture=PICTURE_SITE) -> World:
    if name.startswith("site-aprime"):
        w = World(mutants=mutants, version=version, picture=picture, display=DM_APRIME)
    else:
        w = World(mutants=mutants, version=version, picture=picture)
    if name == "n2a-above":          # descend from +2 m into the band, then bob +-0.05 m around -0.125 m for 30 s
        w.run(120, lambda t: 2.0)
        w.run(200, lambda t: 2.0 - (2.125 * (t - 1120) / 200.0))
        w.run(1800, bob(-0.125, 0.05, 0.5))
    elif name == "n2a-below":        # from -1 m underwater, rise into the band, then bob
        w.run(120, lambda t: -1.0)
        w.run(100, lambda t: -1.0 + 0.875 * (t - 1120) / 100.0)
        w.run(1800, bob(-0.125, 0.05, 0.5))
    elif name == "n2b":              # +-0.1 m at 5 Hz around -0.125 m for 30 s
        w.run(120, lambda t: -1.0)
        w.run(1800, bob(-0.125, 0.1, 5.0))
    elif name == "dive":             # from +400 m down to -50 m and back up
        w.run(60, lambda t: 400.0)
        w.run(900, lambda t: 400.0 - 450.0 * (t - 1060) / 900.0)
        w.run(120, lambda t: -50.0)
        w.run(900, lambda t: -50.0 + 450.0 * (t - 2080) / 900.0)
    elif name == "dive5":            # S2.N7 (b): a scripted dive at 5 m/s vertical, Subnautica unsettled while moving
        w.settled_only_when_still = True
        w.run(120, lambda t: 20.0)
        w.run(480, lambda t: 20.0 - 5.0 * (t - 1120) / 60.0)
        w.run(120, lambda t: -20.0)
        w.run(480, lambda t: -20.0 + 5.0 * (t - 1720) / 60.0)
        w.run(60, lambda t: 20.0)
    elif name == "zone-exit":        # underwater, move out past 7,900 m and back in
        w.run(120, lambda t: -20.0, x_of=lambda t: 7700.0)
        w.run(600, lambda t: -20.0, x_of=lambda t: 7700.0 + 300.0 * (t - 1120) / 600.0)
        w.run(600, lambda t: -20.0, x_of=lambda t: 8000.0 - 300.0 * (t - 1720) / 600.0)
        w.run(120, lambda t: -20.0, x_of=lambda t: 7700.0)
    elif name == "zone-exit-far":    # S2.N2 before the review fix: starts at 7,850 m (Rho guard: never Underwater)
        w.run(120, lambda t: -2.0, x_of=lambda t: 7850.0)
        w.run(660, lambda t: -2.0, x_of=lambda t: 7850.0 + 110.0 * (t - 1120) / 660.0)
    elif name == "margins-lost":     # Prime below the waterline with the margins dropped (finding 11): no Underwater
        w.run(120, lambda t: 0.5)
        w.mrd = False
        w.run(120, lambda t: -1.0)
        w.mrd = True
        w.run(120, lambda t: -1.0)
    elif name == "peer":             # underwater, Subnautica stalls, recovers, exits
        w.run(120, lambda t: -20.0)
        w.peer = PEER_STALLED
        w.run(60, lambda t: -20.0)
        w.peer = PEER_ALIVE
        w.events |= EV_LINKREC
        w.settle_at = w.tick + 60
        w.run(180, lambda t: -20.0)
        w.peer = PEER_EXITED
        w.run(60, lambda t: -20.0)
    elif name == "teleport":         # underwater, teleport to another underwater spot
        w.run(120, lambda t: -20.0)
        w.teleport(90)
        w.run(240, lambda t: -40.0)
    elif name == "redive":           # launcher part n7b
        w.windows = redive(w)
    elif name == "focus":            # underwater: Alt+Tab, ESC, inventory
        w.run(120, lambda t: -20.0)
        w.fg = False
        w.run(60, lambda t: -20.0)
        w.fg = True
        w.run(30, lambda t: -20.0)
        w.paused = True
        w.run(60, lambda t: -20.0)
        w.paused = False
        w.gui = True
        w.run(30, lambda t: -20.0)
        w.gui = False
        w.run(30, lambda t: -20.0)
    # ---- S7a (picture policy Site): the swap at the band and nowhere else
    elif name == "band":             # S7a.N2: 5 descents +600 -> +200 m at 20 m/s and the ascents back
        w.windows = band_cycles(w, 5, 1200)
    elif name == "band-fast":        # S7a.N2: 2 descents at 100 m/s (Approach by predicted arrival) and the ascents
        w.windows = band_cycles(w, 2, 240)
    elif name == "band-hover":       # S7a.N2: 20 s hovering between +300 and +350 m, latch on (from below), then off (from above)
        w.windows = {}
        w.run(60, lambda t: 400.0)
        ramp(w, 600, 400.0, 250.0)                     # 15 m/s down: Approach by prediction, the latch below +300 m
        ramp(w, 120, 250.0, 327.5)
        a = w.tick
        w.run(1200, hover(327.5, 20.0, 0.05, a))       # 307.5.. 347.5 m
        w.windows["hover-on"] = (a, w.tick - 1)
        ramp(w, 300, 327.5, 600.0)
        w.run(60, lambda t: 600.0)
        ramp(w, 1080, 600.0, 327.5)                    # 15 m/s down to +327.5 m: Approach, never below +300 m
        a = w.tick
        w.run(1200, hover(327.5, 20.0, 0.05, a))
        w.windows["hover-off"] = (a, w.tick - 1)
        ramp(w, 300, 327.5, 600.0)
        w.run(60, lambda t: 600.0)
    elif name == "site-dive":        # S7a.N1 waterline: two 5 m/s dives through the water and back, no picture edge there
        w.run(120, lambda t: 20.0)
        for _k in range(2):
            ramp(w, 480, 20.0, -20.0)
            w.run(120, lambda t: -20.0)
            ramp(w, 480, -20.0, 20.0)
            w.run(120, lambda t: 20.0)
    elif name == "site-peer":        # latch on in Track, peer stalls (Space at once), recovers, dives, exits underwater
        w.run(120, lambda t: 50.0)
        w.peer = PEER_STALLED
        w.run(60, lambda t: 50.0)
        w.peer = PEER_ALIVE
        w.events |= EV_LINKREC
        w.settle_at = w.tick + 60
        w.windows = {"recovery": w.tick, "settle": w.settle_at}
        w.run(180, lambda t: 50.0)
        ramp(w, 420, 50.0, -20.0)
        w.run(60, lambda t: -20.0)
        w.peer = PEER_EXITED
        w.run(60, lambda t: -20.0)
    elif name == "site-teleport":    # latch on at +50 m, a teleport (re-streaming), then the same under water
        w.run(120, lambda t: 50.0)
        w.teleport(90)
        w.windows = {"above": (w.tick, w.settle_at)}
        w.run(240, lambda t: 50.0)
        ramp(w, 420, 50.0, -20.0)
        w.run(60, lambda t: -20.0)
        w.teleport(90)
        w.windows["under"] = (w.tick, w.settle_at)
        w.run(240, lambda t: -40.0)
    elif name == "site-aprime":      # design A': lowered with the latch from Approach, a menu raises, the band drop draws first
        w.run(120, lambda t: 50.0)
        w.gui = True
        w.run(30, lambda t: 50.0)
        w.gui = False
        ramp(w, 420, 50.0, -20.0)
        w.run(60, lambda t: -20.0)
        ramp(w, 1260, -20.0, 400.0)
        w.run(60, lambda t: 400.0)
    else:
        raise SystemExit("unknown scenario %r" % name)
    return w

SCENARIOS = ("n2a-above", "n2a-below", "n2b", "dive", "dive5", "zone-exit", "zone-exit-far", "margins-lost", "peer",
             "teleport", "focus", "redive", "band", "band-fast", "band-hover", "site-dive", "site-peer", "site-teleport",
             "site-aprime")
# the scenarios that pin S2 behaviour run under the Waterline policy (S2 policy, the fallback); the rest under Site
S2_PINNED = ("margins-lost",)
N7B_ENTRY_MS = 500.0              # S2.N7 (b): Underwater entered <= 0.5 s after the crossing
N2_WINDOWS = {"n2a-above": ("a", 1320, None), "n2a-below": ("a", 1220, None), "n2b": ("b", 1120, None)}

def write_rows(path: str, rows: list):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        for r in rows:
            fh.write(json.dumps(r, separators=(",", ":")) + "\n")

# ------------------------------------------------------------------------------------------------ commands
REASON_NAMES = {R_WORLDLOAD: "world load", R_ENTER_APPROACH: "enter Approach", R_LEAVE_SITE: "left site/zone/bridge",
                R_LEAVE_PEER: "peer not alive", R_LEAVE_ALT: "y > +350 m", R_ENTER_PRIME: "enter Prime",
                R_PRIME_QUIET: "Prime quiet: 10 s below +5 m, 3 s above", R_ENTER_UW: "enter Underwater",
                R_SURF_WATERLINE: "surface: waterline",
                R_SURF_SITE: "surface: site/zone/bridge", R_SURF_PEER: "surface: peer",
                R_SURF_RESETTLE: "surface: not ready after teleport", R_SURFACED_PRIME: "surfaced to Prime",
                R_SURFACED_SPACE: "surfaced to Space",
                R_PICTURE_ON: "picture on", R_PICTURE_OFF_SPACE: "picture off: Space",
                R_PICTURE_OFF_SITE: "picture off: site/zone/bridge", R_PICTURE_OFF_PEER: "picture off: peer",
                R_PICTURE_OFF_RESETTLE: "picture off: re-streaming"}

def picture_edges(rows: list, from_tick=None, to_tick=None) -> list:
    """S7a: the rows whose step changed the picture (o_pe +1 SE -> Subnautica, -1 back), inside the tick window."""
    out = []
    for r in rows:
        if from_tick is not None and r["tick"] < from_tick:
            continue
        if to_tick is not None and r["tick"] > to_tick:
            break
        if r.get("o_pe", 0):
            out.append(r)
    return out

def site_invariants(rows: list) -> list:
    """S7a Site policy on the logged outputs (v4 rows with o_pp = Site): perf Idle in Space and Full elsewhere, C5 bit 9
 set, design F margins in every non-Space state and skip exactly with the picture, A' lowered+skip with the picture
 (no menu) and never margins, the picture off in Space and never on above +300 m when it turns on."""
    bad = []
    for r in rows:
        if row_version(r) < 4 or r.get("o_pp") != PICTURE_SITE:
            continue
        vs, rq = r["o_vs"], r["o_rq"]
        want_pm = PM_IDLE if (vs == SPACE and not (rq & (RQ_MARGINS | RQ_LOWERED))) else PM_FULL   # F1: Full during the exit hold
        if r["o_pm"] != want_pm:
            bad.append("n=%d perf %d in %s" % (r["n"], r["o_pm"], VIEW_NAMES.get(vs)))
        if not (r["o_c5f"] & C5_PICTURE_SITE):
            bad.append("n=%d C5 bit 9 clear" % r["n"])
        if vs == SPACE and r["o_sp"]:
            bad.append("n=%d picture on in Space" % r["n"])
        if r.get("o_pe") == 1 and not (from_bits(r["yb"]) < APPROACH_ENTER_Y):
            bad.append("n=%d picture on at y %s >= +%g m" % (r["n"], r.get("y"), APPROACH_ENTER_Y))
        if r["dm"] == DM_F:
            if vs != SPACE and not (rq & RQ_MARGINS):
                bad.append("n=%d no margins in %s" % (r["n"], VIEW_NAMES.get(vs)))
            if bool(rq & RQ_SKIP) != bool(r["o_sp"]):
                bad.append("n=%d skip %d but picture %d" % (r["n"], bool(rq & RQ_SKIP), r["o_sp"]))
        elif r["dm"] == DM_APRIME:
            if rq & (RQ_MARGINS | RQ_ALPHA):
                bad.append("n=%d A' requested margins" % r["n"])
            menu = r["cur"] or r["gui"] or r["pau"]
            if r["o_sp"] and not menu and rq != (RQ_LOWERED | RQ_SKIP):
                bad.append("n=%d A' picture without lowered+skip (rq=%d)" % (r["n"], rq))
    return bad

def transitions(rows: list) -> dict:
    """Logged transitions by reason (INFO)."""
    out = {}
    for r in rows:
        if r["o_rsn"]:
            name = REASON_NAMES.get(r["o_rsn"], str(r["o_rsn"]))
            out[name] = out.get(name, 0) + 1
    return out

def entry_latency(rows: list) -> list:
    """Per Underwater entry: ms from the crossing tick (C5 crossingTick) to the entry (S2.N7 (b), T-D1)."""
    first_us = {}
    for r in rows:
        first_us.setdefault(r["tick"], r["us"])
    out = []
    for r in rows:
        if r["o_rsn"] == R_ENTER_UW and r["o_xt"] in first_us:
            out.append((r["us"] - first_us[r["o_xt"]]) / 1000.0)
    return out

def report(rows: list, honour_mutant: bool, n2_case=None, from_tick=None, to_tick=None, max_entry_ms=None) -> dict:
    rep = {"replay": replay(rows, honour_mutant), "order": check_order(rows), "zone": check_zone(rows)}
    sw = switches(rows, from_tick, to_tick)
    m = n2_metrics(sw)
    rep["n2"] = dict(m, case=n2_case, window=[from_tick, to_tick])
    if n2_case:
        rep["n2"]["pass"] = n2_verdict(n2_case, m)
    lat = sorted(entry_latency(rows))
    rep["entry"] = {"entries": len(lat), "medianMs": lat[len(lat) // 2] if lat else None,
                    "maxMs": lat[-1] if lat else None, "limitMs": max_entry_ms}
    if max_entry_ms is not None:
        rep["entry"]["pass"] = bool(lat) and lat[-1] <= max_entry_ms
    rep["transitions"] = transitions(rows)
    bad = site_invariants(rows)
    rep["site"] = {"checked": sum(1 for r in rows if row_version(r) >= 4 and r.get("o_pp") == PICTURE_SITE),
                   "pictureEdges": len(picture_edges(rows)), "violations": len(bad), "first": bad[:8],
                   "pass": not bad}
    rep["pass"] = rep["replay"]["pass"] and rep["order"]["pass"] and rep["zone"]["pass"] and \
        rep["n2"].get("pass", True) and rep["entry"].get("pass", True) and rep["site"]["pass"]
    return rep

def print_report(rep: dict, title: str):
    rp, od, zn, n2 = rep["replay"], rep["order"], rep["zone"], rep["n2"]
    print("STATE TRACES %s" % title)
    print("  %-36s %s  rows=%d contiguous=%d gaps=%d localMismatch=%d linkMismatch=%d mutantRows=%d" % (
        "S2 vsm replay (documented machine)", "PASS" if rp["pass"] else "FAIL", rp["rows"], rp["contiguous"],
        rp["gaps"], rp["localMismatch"], rp["linkMismatch"], rp["mutantRows"]))
    print("  %-36s %s  enter=%d leave=%d violations=%d" % (
        "S2 crossing order", "PASS" if od["pass"] else "FAIL", od["enterChecked"], od["leaveChecked"], od["violations"]))
    print("  %-36s %s  rows=%d FAR/off-site=%d violations=%d" % (
        "S2.N2 zone / SE view beyond 7,900 m", "PASS" if zn["pass"] else "FAIL", zn["checked"], zn["beyond7900"],
        zn["violations"]))
    verdict = ("PASS" if n2.get("pass") else "FAIL") if n2.get("case") else "INFO"
    print("  %-36s %s  switches=%d minSpacing=%s s maxPerSecond=%d%s" % (
        "S2.N2 hysteresis" + (" (%s)" % n2["case"] if n2.get("case") else ""), verdict, n2["switches"],
        "-" if n2["minSpacingS"] is None else "%.3f" % n2["minSpacingS"], n2["maxPerSecond"],
        "" if n2["window"] == [None, None] else "  window ticks %s..%s" % tuple(n2["window"])))
    en = rep["entry"]
    print("  %-36s %s  entries=%d median=%s ms max=%s ms%s" % (
        "S2.N7 (b) crossing -> Underwater", ("PASS" if en.get("pass") else "FAIL") if "pass" in en else "INFO",
        en["entries"], "-" if en["medianMs"] is None else "%.1f" % en["medianMs"],
        "-" if en["maxMs"] is None else "%.1f" % en["maxMs"],
        "" if en["limitMs"] is None else " (limit %.0f ms)" % en["limitMs"]))
    st = rep["site"]
    print("  %-36s %s  rows=%d pictureEdges=%d violations=%d" % (
        "S7a site picture policy", "PASS" if st["pass"] else "FAIL", st["checked"], st["pictureEdges"], st["violations"]))
    print("  %-36s INFO  %s" % ("transitions", ", ".join("%s %d" % kv for kv in sorted(rep["transitions"].items()))
                                or "none"))
    for part in (rp, od, zn, st):
        for line in part["first"]:
            print("    " + line)
    print("STATE TRACES %s" % ("PASS" if rep["pass"] else "FAIL"))

def cmd_replay(a) -> int:
    rows = read_rows(a.files)
    if not rows:
        print("state_traces: no vsm rows in %s" % ", ".join(a.files))
        return 2
    rep = report(rows, a.honour_mutant, a.case, a.from_tick, a.to_tick, a.max_entry_ms)
    print_report(rep, "replay of %d rows from %s" % (len(rows), ", ".join(os.path.basename(f) for f in a.files)))
    if a.json:
        with open(a.json, "w", encoding="utf-8") as fh:
            json.dump(rep, fh, indent=1)
    return 0 if rep["pass"] else 1

def cmd_gen(a) -> int:
    picture = PICTURE_WATERLINE if a.picture == "waterline" else PICTURE_SITE
    w = scenario(a.scenario, {"M4": MUT_M4, "M10": MUT_M10, "S7A_M1": M_S7A_M1}.get(a.mutant, 0), picture=picture)
    write_rows(a.out, w.rows)
    print("state_traces: %d rows of %s, picture %s%s -> %s" % (
        len(w.rows), a.scenario, PICTURE_NAMES[picture], " (mutant %s)" % a.mutant if a.mutant else "", a.out))
    return 0

def selftest() -> int:
    ok = True
    tmp = tempfile.mkdtemp(prefix="vsm-selftest-")

    def line(name, good, detail):
        nonlocal ok
        ok = ok and good
        print("  %-60s %s  %s" % (name, "ok" if good else "WRONG", detail))

    print("STATE TRACES SELFTEST")
    set_band(300.0, 350.0)   # the scenarios were written for the original band; the plugin default is +1,000 / +1,100 m
    s7a_names = ("band", "band-fast", "band-hover", "site-dive", "site-peer", "site-teleport", "site-aprime")
    for name in SCENARIOS:
        # the S2 scenarios run under both policies: Waterline is S2's pin (S2 policy, the fallback), Site restates it
 # (same states and transitions, Subnautica's picture from the band); the S7a scenarios run under Site
        pictures = (PICTURE_WATERLINE,) if name in S2_PINNED else \
            (PICTURE_SITE,) if name in s7a_names else (PICTURE_WATERLINE, PICTURE_SITE)
        for pic in pictures:
            w = scenario(name, picture=pic)
            tag = "%s [%s]" % (name, PICTURE_NAMES[pic])
            path = os.path.join(tmp, "%s-%s.jsonl" % (name, PICTURE_NAMES[pic]))
            write_rows(path, w.rows)
            rows = read_rows([path])
            case, f, t = N2_WINDOWS.get(name, (None, None, None))
            rep = report(rows, False, case, f, t, N7B_ENTRY_MS if name == "dive5" else None)
            n2 = rep["n2"]
            line("green %s" % tag, rep["pass"], "rows=%d switches=%d minSpacing=%s maxPerSecond=%d edges=%d" % (
                len(rows), n2["switches"], n2["minSpacingS"], n2["maxPerSecond"], rep["site"]["pictureEdges"]))
            if not rep["pass"]:
                for part in ("replay", "order", "zone", "site"):
                    for msg in rep[part]["first"][:3]:
                        print("      " + part + ": " + msg)
            if name == "dive":
                views = [r["o_vs"] for r in rows]
                line("  dive reaches Underwater and returns to Space", UNDERWATER in views and views[-1] == SPACE,
                     "views seen %s" % sorted(set(VIEW_NAMES[v] for v in views)))
                track = [r for r in rows if r["o_vs"] == APPROACH and r["o_am"] == AM_TRACK]
                ed = picture_edges(rows)
                if pic == PICTURE_WATERLINE:
                    line("  S2 policy: Track is Subnautica's Track recipe, margins off", bool(track) and all(
                        r["o_pm"] == PM_TRACK and not (r["o_rq"] & RQ_MARGINS) for r in track), "%d Track rows" % len(track))
                    line("  S2 policy: picture edges = the Underwater entry and exit",
                         len(ed) == 2 and ed[0]["o_vs"] == UNDERWATER and ed[1]["s_v"] == UNDERWATER,
                         "edges at %s" % [(r["tick"], VIEW_NAMES[r["o_vs"]]) for r in ed])
                else:
                    line("  S7a site: Track is Full with margins on (Subnautica warm from the band)", bool(track) and all(
                        r["o_pm"] == PM_FULL and (r["o_rq"] & RQ_MARGINS) for r in track), "%d Track rows" % len(track))
                    line("  S7a site: one edge in, one out, both at the band, none at the waterline",
                         len(ed) == 2 and ed[0]["o_pe"] == 1 and from_bits(ed[0]["yb"]) < APPROACH_ENTER_Y and
                         ed[1]["o_pe"] == -1 and ed[1]["o_vs"] == SPACE,
                         "edges at y %s" % [r["y"] for r in ed])
            if name == "zone-exit":
                far = [r for r in rows if r["zone"] == Z_FAR]
                line("  SE view beyond 7,900 m", bool(far) and all(r["o_vs"] in (SPACE, SURFACING) and not r.get("o_sp")
                                                                 for r in far), "%d FAR rows" % len(far))
                ze = zone_exit(rows)
                line("  zone exit exercised from Underwater, Surfacing at once", ze["pass"] and ze["ticksToSpace"] is not None,
                     "exitTick=%s ticksToSpace=%s" % (ze["exitTick"], ze["ticksToSpace"]))
            if name == "zone-exit-far":
                ze = zone_exit(rows)
                line("  start at 7,850 m: zone exit NOT exercised (finding 7)", not ze["exercised"] and not ze["pass"] and
                     ze["farRows"] > 0, "underwaterBefore=%s farRows=%d" % (ze["underwaterBefore"], ze["farRows"]))
            if name == "margins-lost":
                held = [r for r in rows if r["mrd"] == 0 and float(r["y"]) <= UNDERWATER_ENTER_Y]
                line("  margins dropped in Prime: Underwater held (finding 11)", bool(held) and all(
                    r["o_vs"] != UNDERWATER and (r["o_blk"] & B_RENDER) for r in held) and rows[-1]["o_vs"] == UNDERWATER,
                    "%d held rows, last view %s" % (len(held), VIEW_NAMES.get(rows[-1]["o_vs"])))
            if name == "peer":
                first = next((r for r in rows if r["peer"] == PEER_STALLED), None)
                line("  peer stalled: Surfacing and scene drawn in the same tick",
                     first is not None and first["o_vs"] == SURFACING and not (first["o_rq"] & RQ_SKIP),
                     "view %s rq=%s" % (VIEW_NAMES.get(first["o_vs"]) if first else None, first["o_rq"] if first else None))
            if name in ("band", "band-fast"):
                good, det = True, []
                for kind, a, b in w.windows:
                    ed = picture_edges(rows, a, b)
                    want = 1 if kind == "descent" else -1
                    one = len(ed) == 1 and ed[0]["o_pe"] == want
                    good = good and one
                    if not one:
                        det.append("%s %d..%d: %s" % (kind, a, b, [(r["tick"], r["o_pe"], r["y"]) for r in ed]))
                    if one and kind == "descent":
                        y_on = from_bits(ed[0]["yb"])
                        good = good and y_on < APPROACH_ENTER_Y and ed[0]["o_prs"] == R_PICTURE_ON
                        if name == "band-fast":
                            good = good and y_on > 150.0
                    if one and kind == "ascent":
                        good = good and ed[0]["o_vs"] == SPACE and ed[0]["o_prs"] == R_PICTURE_OFF_SPACE
                line("  %s: one SE->SN edge per descent below +300 m, one back per ascent at Space" % name, good,
                     "; ".join(det) or "%d legs, %d edges" % (len(w.windows), len(picture_edges(rows))))
                if name == "band-fast":
                    early = [r for r in rows if r["o_rsn"] == R_ENTER_APPROACH]
                    line("  band-fast: Approach by predicted arrival far above the band, picture still at the band",
                         len(early) == 2 and all(from_bits(r["yb"]) > 500.0 for r in early),
                         "Approach entries at y %s" % [r["y"] for r in early])
            if name == "band-hover":
                on_a, on_b = w.windows["hover-on"]
                off_a, off_b = w.windows["hover-off"]
                won = [r for r in rows if on_a <= r["tick"] <= on_b]
                woff = [r for r in rows if off_a <= r["tick"] <= off_b]
                line("  band-hover: no edge while hovering at +307..+347 m (latch kept on / kept off)",
                     not picture_edges(rows, on_a, on_b) and not picture_edges(rows, off_a, off_b) and
                     all(r["o_sp"] == 1 and r["o_vs"] == APPROACH for r in won) and
                     all(r["o_sp"] == 0 and r["o_vs"] == APPROACH and (r["o_pb"] & B_HIGH) for r in woff),
                     "on-window %d rows, off-window %d rows" % (len(won), len(woff)))
            if name == "site-dive":
                cross = sorted(set(r["o_xt"] for r in rows if r["o_xt"]))
                near = [r for r in rows if any(abs(r["tick"] - c) <= 120 for c in cross) and r.get("o_pe")]
                uw = [r for r in rows if r["o_rsn"] == R_ENTER_UW]
                mix_up = any(from_bits(r["o_mix"]) == 1.0 for r in rows)
                mix_down = from_bits(rows[-1]["o_mix"]) == 0.0
                line("  site-dive: zero picture edges within 2 s of the waterline crossings, audio still crossfades",
                     len(cross) == 2 and not near and len(uw) == 2 and mix_up and mix_down and len(picture_edges(rows)) == 1,
                     "crossings %d, edges near %d, entries %d, mix up %s down %s" % (len(cross), len(near), len(uw),
                                                                                  mix_up, mix_down))
            if name == "site-peer":
                st = next(r for r in rows if r["peer"] == PEER_STALLED)
                line("  site-peer: stall in Track -> Space, picture off at once, scene drawn, margins kept",
                     st["o_vs"] == SPACE and st["s_sp"] == 1 and st["o_sp"] == 0 and st["o_prs"] == R_PICTURE_OFF_PEER and
                     not (st["o_rq"] & RQ_SKIP) and bool(st["o_rq"] & RQ_MARGINS) and st["o_pe"] == -1,
                     "view %s rq=%d prs=%d" % (VIEW_NAMES[st["o_vs"]], st["o_rq"], st["o_prs"]))
                back = next((r for r in rows if r["tick"] >= w.windows["recovery"] and r.get("o_pe") == 1), None)
                line("  site-peer: picture back only after Subnautica settled after the recovery",
                     back is not None and back["tick"] >= w.windows["settle"], "on at tick %s, settle %d" % (
                         back["tick"] if back else None, w.windows["settle"]))
                ex = next(r for r in rows if r["peer"] == PEER_EXITED)
                line("  site-peer: exit underwater -> Surfacing, picture off, scene drawn in the same tick",
                     ex["s_v"] == UNDERWATER and ex["o_vs"] == SURFACING and ex["o_sp"] == 0 and not (ex["o_rq"] & RQ_SKIP),
                     "view %s rq=%d" % (VIEW_NAMES[ex["o_vs"]], ex["o_rq"]))
            if name == "site-teleport":
                good, det = True, []
                for key in ("above", "under"):
                    t_ev, t_settle = w.windows[key]
                    tr = next(r for r in rows if r["tick"] == t_ev)
                    on = next((r for r in rows if r["tick"] > t_ev and r.get("o_pe") == 1), None)
                    g = tr["o_sp"] == 0 and tr["o_prs"] == R_PICTURE_OFF_RESETTLE and on is not None and on["tick"] >= t_settle
                    if key == "under":
                        g = g and tr["o_vs"] == SURFACING and any(
                            r["tick"] >= t_settle and r["o_vs"] == UNDERWATER for r in rows)
                    good = good and g
                    det.append("%s: off prs=%d, on at %s (settle %d)" % (key, tr["o_prs"], on["tick"] if on else None, t_settle))
                line("  site-teleport: re-streaming drops the picture at once, back after the settle", good, "; ".join(det))
            if name == "site-aprime":
                on = next(r for r in rows if r.get("o_pe") == 1)
                gui = [r for r in rows if r["gui"]]
                sp = next(r for r in rows if r["o_rsn"] in (R_LEAVE_ALT, R_SURFACED_SPACE))
                k = rows.index(sp)
                raised = next((r for r in rows[k:] if r["o_rq"] == 0), None)
                line("  site-aprime: lowered+skip from the latch in Track, a menu raises, the band drop draws first",
                     on["o_vs"] == APPROACH and on["o_rq"] == RQ_LOWERED | RQ_SKIP and bool(gui) and
                     all(r["o_rq"] == 0 for r in gui) and sp["o_rq"] == RQ_LOWERED and raised is not None and
                     raised["tick"] > sp["tick"],
                     "on rq=%d, Space rq=%d, raised %s ticks later" % (on["o_rq"], sp["o_rq"],
                                                                      raised["tick"] - sp["tick"] if raised else None))

    # S7a, finding 11 restated: margins never presented -> the picture latch never turns on -> no Underwater either
    w = World(picture=PICTURE_SITE)
    w.mrd = False
    w.run(120, lambda t: 50.0)
    ramp(w, 420, 50.0, -20.0)
    w.run(120, lambda t: -20.0)
    rows = w.rows
    line("S7a site: margins never ready -> no picture, no Underwater (Render guard)",
         all(not r["o_sp"] and r["o_vs"] != UNDERWATER for r in rows) and all(
             r["o_pb"] & B_RENDER for r in rows if r["o_vs"] != SPACE and r["s_v"] != SPACE),
         "%d rows" % len(rows))

    # seeded failure S2.M4: rows produced by the mutant machine
    for name in ("n2a-above", "n2b"):
        w = scenario(name, MUT_M4)
        path = os.path.join(tmp, name + "-m4.jsonl")
        write_rows(path, w.rows)
        rows = read_rows([path])
        case, f, t = N2_WINDOWS[name]
        rep = report(rows, False, case, f, t)
        n2 = rep["n2"]
        line("mutant S2.M4 %s: replay red" % name, not rep["replay"]["pass"],
             "localMismatch=%d" % rep["replay"]["localMismatch"])
        line("mutant S2.M4 %s: S2.N2 (%s) red" % (name, case), not n2["pass"],
             "switches=%d minSpacing=%s maxPerSecond=%d" % (n2["switches"], n2["minSpacingS"], n2["maxPerSecond"]))
        rep_h = report(rows, True, None)
        line("mutant S2.M4 %s: --honour-mutant replays it" % name, rep_h["replay"]["pass"],
             "localMismatch=%d" % rep_h["replay"]["localMismatch"])

    # seeded failure S2.M10: snReady gated on IsWorldSettled at all times
    w = scenario("dive5", MUT_M10)
    rep_m10 = report(w.rows, False, None, None, None, N7B_ENTRY_MS)
    en = rep_m10["entry"]
    line("mutant S2.M10 dive5: S2.N7 (b) red", not en["pass"],
         "entries=%d max=%s ms" % (en["entries"], en["maxMs"]))
    green5 = report(scenario("dive5").rows, False, None, None, None, N7B_ENTRY_MS)["entry"]
    line("  dive5 green entry latency", green5["pass"], "max=%s ms" % green5["maxMs"])

    # seeded failure S7a.M1: the machine runs Waterline while Site is selected
    w = scenario("band", M_S7A_M1)
    rows = w.rows
    rp1 = replay(rows)
    track = [r for r in rows if r["o_vs"] == APPROACH and r["o_am"] == AM_TRACK]
    line("mutant S7a.M1 band: Waterline behaviour (Track recipe, no picture, bit 9 clear)",
         bool(track) and all(r["o_pm"] == PM_TRACK and not r["o_sp"] and r["o_pp"] == PICTURE_WATERLINE and
                             not (r["o_c5f"] & C5_PICTURE_SITE) for r in track) and not picture_edges(rows),
         "%d Track rows, %d picture edges" % (len(track), len(picture_edges(rows))))
    line("mutant S7a.M1 band: replay red", not rp1["pass"], "localMismatch=%d" % rp1["localMismatch"])
    line("mutant S7a.M1 band: --honour-mutant replays it", replay(rows, True)["pass"], "")

    # tampered rows
    w = scenario("dive")
    rows = [dict(r) for r in w.rows]
    k = next(n for n, r in enumerate(rows) if r["o_vs"] == UNDERWATER)
    rows[k + 5]["o_vs"] = APPROACH
    line("tampered view state: replay red", not replay(rows)["pass"], "row %d" % (k + 5))
    rows = [dict(r) for r in w.rows]
    k = next(n for n, r in enumerate(rows) if (r["o_rq"] & RQ_SKIP))
    rows[k]["ack"] = rows[k]["s_rs"] - 1
    line("skip before the margins ack: order red", not check_order(rows)["pass"], "row %d" % k)
    rows = [dict(r) for r in w.rows]
    k = next(n for n, r in enumerate(rows) if r["o_vs"] == SURFACING)
    rows[k]["o_rq"] = rows[k]["o_rq"] & ~RQ_MARGINS
    line("margins dropped while surfacing: order red", not check_order(rows)["pass"], "row %d" % k)
    rows = [dict(r) for r in w.rows]
    k = next(n for n, r in enumerate(rows) if r["o_vs"] == UNDERWATER)
    rows[k + 3]["s_xt"] = rows[k + 3]["s_xt"] + 7
    line("tampered state: linkage red", replay(rows)["linkMismatch"] > 0, "row %d" % (k + 3))
    rows = [dict(r) for r in w.rows]
    k = next(n for n, r in enumerate(rows) if r.get("o_pe") == -1)
    rows[k]["o_rq"] = rows[k]["o_rq"] & ~RQ_MARGINS & ~RQ_ALPHA
    line("S7a: margins dropped on the band exit before the drawn frame: order red", not check_order(rows)["pass"],
         "row %d" % k)
    rows = [dict(r) for r in w.rows]
    k = next(n for n, r in enumerate(rows) if r["o_vs"] == APPROACH and r["o_am"] == AM_TRACK and r["o_sp"])
    rows[k]["o_pm"] = PM_TRACK
    line("S7a: Track recipe under the site policy: site check red", not report(rows, False)["site"]["pass"], "row %d" % k)
    zr = [dict(r) for r in scenario("zone-exit").rows]
    k = next(n for n, r in enumerate(zr) if r["zone"] == Z_FAR)
    zr[k]["o_vs"] = UNDERWATER
    line("Underwater beyond 7,900 m: zone red", not check_zone(zr)["pass"], "row %d" % k)

    # vsm/1-3 rows replay as Waterline (S2's machine wrote them)
    v1 = []
    for r in scenario("dive", version=2).rows:
        r1 = dict(r)
        r1["v"] = 1
        r1.pop("mrd", None)
        v1.append(r1)
    line("vsm/1 rows (no mrd) still replay", replay(v1)["pass"], "%d rows" % len(v1))
    v3 = scenario("dive", version=3).rows
    line("vsm/3 rows (no picture keys) replay as Waterline", replay(v3)["pass"] and "pp" not in v3[0] and
         "o_sp" not in v3[0], "%d rows" % len(v3))
    as_v3 = [dict(r, v=3) for r in scenario("dive").rows]
    line("site rows relabelled vsm/3 (replayed as Waterline): replay red", not replay(as_v3)["pass"], "")
    mr = [dict(r) for r in scenario("margins-lost", picture=PICTURE_WATERLINE).rows]
    k = next(n for n, r in enumerate(mr) if r["mrd"] == 0 and float(r["y"]) <= UNDERWATER_ENTER_Y)
    mr[k]["o_vs"] = UNDERWATER
    line("Underwater entered without the margins: replay red", not replay(mr)["pass"], "row %d" % k)

    # re-dive fix (vsm/3 rule, kept by vsm/4): Prime kept through the quick cycles and the near pause
    w = scenario("redive")
    rows = w.rows

    def tracks(rr, win):
        return [r for r in rr if win[0] <= r["tick"] <= win[1] and r["o_rsn"] == R_PRIME_QUIET]
    q, n, f = (tracks(rows, w.windows[k]) for k in ("quick", "near", "far"))
    line("redive: no Track in the quick cycles and the near pause", not q and not n,
         "quick %d, near %d Prime -> Track drops" % (len(q), len(n)))
    line("redive: Track above +5 m in the far pause", len(f) == 1 and float(f[0]["y"]) >= PRIME_QUIET_NEAR_Y,
         "far drops at y %s" % [r["y"] for r in f])
    ent = [r for r in rows if r["o_rsn"] == R_ENTER_UW and w.windows["quick"][0] <= r["tick"] <= w.windows["quick"][1]]
    line("redive: %d quick re-dives entered" % REDIVE_CYCLES, len(ent) == REDIVE_CYCLES, "%d entries" % len(ent))
    as_v2 = [dict(r, v=2) for r in rows]
    rp2 = replay(as_v2)
    line("redive rows read as vsm/2 (3 s everywhere, Waterline): replay red", not rp2["pass"],
         "localMismatch=%d linkMismatch=%d" % (rp2["localMismatch"], rp2["linkMismatch"]))
    w2 = scenario("redive", version=2)
    q2, n2_ = (tracks(w2.rows, w2.windows[k]) for k in ("quick", "near"))
    line("redive by the vsm/2 machine: Track drops near the surface (seeded old rule)", len(q2) >= REDIVE_CYCLES - 1 and
         len(n2_) == 1, "quick %d, near %d" % (len(q2), len(n2_)))
    line("  and its vsm/2 rows replay green", replay(w2.rows)["pass"], "%d rows" % len(w2.rows))

    # gaps: every third row only -> reseeding keeps the replay green
    for pic in (PICTURE_SITE, PICTURE_WATERLINE):
        w = scenario("dive", picture=pic)
        sparse = [r for n, r in enumerate(w.rows) if n % 3 == 0]
        rp = replay(sparse)
        line("sparse rows (gaps) still replay [%s]" % PICTURE_NAMES[pic], rp["pass"] and rp["gaps"] > 0,
             "gaps=%d" % rp["gaps"])

    print("STATE TRACES SELFTEST %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S2 view state machine oracle (vsm rows of se-*.jsonl)")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--band", default=None, help="the plugin's band 'enter,exit' in metres (default 400,450; the selftest pins 300,350)")
    sub = ap.add_subparsers(dest="cmd")
    p = sub.add_parser("replay", help="replay vsm rows and run every check")
    p.add_argument("files", nargs="+")
    p.add_argument("--honour-mutant", action="store_true", help="replay with the mutant flags logged in the rows")
    p.add_argument("--case", choices=("a", "b"), help="judge S2.N2 trace (a) or (b) on the window")
    p.add_argument("--from-tick", type=int)
    p.add_argument("--to-tick", type=int)
    p.add_argument("--max-entry-ms", type=float,
                   help="judge crossing -> Underwater (S2.N7 (b): 500 for the 5 m/s dive) instead of INFO")
    p.add_argument("--json")
    p = sub.add_parser("n2", help="S2.N2 only")
    p.add_argument("files", nargs="+")
    p.add_argument("--case", choices=("a", "b"), required=True)
    p.add_argument("--from-tick", type=int)
    p.add_argument("--to-tick", type=int)
    p = sub.add_parser("gen", help="write a synthetic trace")
    p.add_argument("out")
    p.add_argument("--scenario", choices=SCENARIOS, required=True)
    p.add_argument("--mutant", choices=("M4", "M10", "S7A_M1"))
    p.add_argument("--picture", choices=("site", "waterline"), default="site",
                   help="the picture policy input (S7a; default site)")
    a = ap.parse_args(argv)
    if a.band:
        set_band(*(float(v) for v in a.band.replace(";", ",").split(",")))
    if a.selftest:
        return selftest()
    if a.cmd == "replay":
        return cmd_replay(a)
    if a.cmd == "n2":
        rows = read_rows(a.files)
        if not rows:
            print("state_traces: no vsm rows")
            return 2
        m = n2_metrics(switches(rows, a.from_tick, a.to_tick))
        good = n2_verdict(a.case, m)
        print("S2.N2 (%s) %s switches=%d minSpacing=%s s maxPerSecond=%d" % (
            a.case, "PASS" if good else "FAIL", m["switches"], m["minSpacingS"], m["maxPerSecond"]))
        return 0 if good else 1
    if a.cmd == "gen":
        return cmd_gen(a)
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main())
