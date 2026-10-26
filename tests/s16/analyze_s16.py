"""S16 oracle table (ROADMAP §14 "### S16 Depth progression"; docs/S16-BRIEF.md; CONTRACT §5.4 "S16 conventions").

 python -I tests/s16/analyze_s16.py <tests/out/s16/<stamp>> [--lenient] [--json]
 python -I tests/s16/analyze_s16.py --selftest

Reads a folder written by `launcher.py s16` (s16-session.jsonl; se-*.jsonl, sn-*.jsonl) and judges, with the Python twin's
tables and rules (tools/s16/s16_ref.py; never the plugins' own numbers):

 S16.PIN session the SE plugin's conventions hash (s16.site) is the twin's pinned hash
 S16.STATIC station depth 9: the harness's static 3^3 grid (a station) is never tracked nor rated: its job reply tracked false, no
 crush, and no s16.grid/depth/damage/hud/voice/creak row names it (format crush.depth)
 S16.LADDER heavy,room,light,bare,tier2 every s16.grid summary's crush depth is the twin's (hull ladder, weakest class,
 best tier) and each scripted hull's settled summary has the counts its shape gives: light 4^3
 small 56 light counted of 64 (120 m), heavy 4^3 small 56 heavy (200 m), room 5^3 heavy shell around a dry
 room 98 heavy, the inner light block and the vent uncounted (800 m), bare cockpit + battery nothing counted
 and 2 wet (120 m), tier2 26 heavy, tier 2 (2,000 m since depth 11) [the shapes of depth 3; the twin's numbers rule]
 S16.BARE bare a craft with nothing counted still breaks past its limit
 S16.GROUP any every s16.group row's limit is the twin's (the mechanical group's best tier, a docked grid never below its
 carrier) and an exempt docked grid takes no damage
 S16.GRACE any no damage row before the load grace ends
 S16.BREAK light the light small hull: no block damaged at or above its limit (block centre depth <= 120 m), blocks
 damaged and at least one destroyed past it
 S16.T2 tier2 the top-tier hull (Mk2, 2,000 m): seen between 1,900 and 2,000 m without any damage, damaged once past 2,000 m
 S16.CURVE light,t2 every damage row past the limit: hp/s and the SE amount are the twin's curve (min(100, 10 + 1.0 (d - L)),
 x k(size)); at most 8 blocks per grid per period, each block once; one period per 60 ticks per grid
 S16.CREAK light,t2 every creak inside the band (depth >= L - 50), its cadence (enter, rise, the period of the level of
 the last creak), at least one creak per descent, the first creak before the first damage
 S16.VOICE light,t2 "hull integrity critical" played at the crossing (past the limit) and again every 20 s while the grid stays
 past it: never two within the 20 s cooldown, as many plays as the time past the limit gives (+-1)
 S16.HUD light,t2 the HUD rows: depth = the deepest of the grid's 8 box corners (Rs - |P - C|, the site row's C and Rs,
 0.05 m), the state = the twin's for that depth (the creak level is S16.CREAK's); damageOn = the gate
 (format damage.gate: `gate` destructible, not exempt; any game mode, depth 4); SE's text line (format
 hud.se, depth 5): every diver row `line` none, a grid row `grid`,
 or `suppressed` exactly while Subnautica shows its readouts (snHud)
 S16.SN S16b Subnautica played one creak per SE creak event (seq) and one voice per played SE voice (seq)
 S16.SNHUD S16b Subnautica's readout (format hud.sn, depth 7): seated one field "10 m / 120 m", checked live (TMP lineCount 1,
 the rendered width within the compass), no hull panel; the diver its depth; both rects on screen (format
 hud.place); MISSING for a build before depth 7 (S16.M11 forces a wrap: red); depth 9: the rendered text's
 centre within 5 % of the compass width of the half-moon's centre (centreDx) Formerly INFO: the placed
 compass / Seamoth panel rects in screen pixels (format hud.place: inside the screen, the compass top 6 %)
 S16.Mx.red a seeded failure turns exactly its rows red

Descent gate: BREAK (light), T2 (tier2), BARE (bare), CURVE, CREAK, VOICE, HUD and SN (light, tier2) are judged only when
every descent they read reached its target (DESCENT_TO, the launcher's: an s16.descend hold row or an s16.depth row within
REACH_TOL_M of it); otherwise the row is MISSING "<label> descent did not reach N m (deepest X m)", never PASS or FAIL on a
grid that never went deep.
"""
from __future__ import annotations

import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tools", "s16")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C   # noqa: E402 (row, table, verdict, jsonl reader)
import s16_ref as T     # noqa: E402 (the tables and rules: the twin)

row = C.row
MUTANT_ROWS = {"S16.M1": ("S16.LADDER", "S16.T2"), "S16.M2": ("S16.BREAK", "S16.T2", "S16.BARE"), "S16.M3": ("S16.CURVE",),
               "S16.M4": ("S16.CREAK",), "S16.M5": ("S16.VOICE",), "S16.M6": ("S16.HUD",), "S16.M7": ("S16.SN",),
               "S16.M8": ("S16.LADDER",), "S16.M9": ("S16.GROUP",), "S16.M10": ("S16.BARE",), "S16.M11": ("S16.SNHUD",)}
#  the settled summary each scripted hull's shape gives (the twin's ladder and tiers for the crush depths)
SHAPES = {
    "light": {"light": 56, "heavy": 0, "blocks": 64, "crush": T.hull_crush(T.SMALL, T.LIGHT)},
    "heavy": {"light": 0, "heavy": 56, "blocks": 64, "crush": T.hull_crush(T.SMALL, T.HEAVY)},
    "room": {"light": 0, "heavy": 98, "blocks": 100, "crush": T.hull_crush(T.LARGE, T.HEAVY)},
    "bare": {"light": 0, "heavy": 0, "wet": 2, "blocks": 2, "crush": T.hull_crush(T.SMALL, T.LIGHT)},
    "tier2": {"light": 0, "heavy": 26, "tier": 2, "blocks": 27, "crush": T.tier_crush(2)},
}
HUD_TOL_M = 0.05
# the launcher's descents (launcher_s16 LIGHT/BARE/TIER3 "to"; its self-test checks they match) and the reach tolerance
DESCENT_TO = {"light": T.hull_crush(T.SMALL, T.LIGHT) + 100.0, "bare": T.hull_crush(T.SMALL, T.LIGHT) + 50.0, "tier2": T.tier_crush(2) + 60.0}   # depth 10: the ladder + 100, past it, the top tier + 60 (from the twin, so a re-pin moves them)
VOICE_TOL_MS = 1500
REACH_TOL_M = 5.0
_LT = ("light", "tier2")
DESCENT_GATED = {"S16.BREAK": ("light",), "S16.T2": ("tier2",), "S16.BARE": ("bare",), "S16.CURVE": _LT, "S16.CREAK": _LT, "S16.VOICE": _LT,
                 "S16.HUD": _LT, "S16.SN": _LT}
CADENCE_TOL_TICKS = 2
REL = 1e-6

def parse_rows(recs, prefix="s16."):
    out = []
    for rec in recs:
        name = rec.get("ev") or rec.get("event")
        if not isinstance(name, str) or not name.startswith(prefix):
            continue
        data = dict(rec["data"]) if isinstance(rec.get("data"), dict) else {k: v for k, v in rec.items() if k not in ("ev", "event", "data")}
        out.append((name, data))
    return out

class Session:
    def __init__(self, folder):
        import glob
        self.dir = folder
        self.session = C.read_jsonl(os.path.join(folder, "s16-session.jsonl"))
        self.se = parse_rows(C.read_jsonl(sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))))
        self.sn = parse_rows(C.read_jsonl(sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))))
        self.start = next((e for e in self.session if e.get("ev") == "start"), {}) or {}
        self.mode = self.start.get("mode")
        self.parts = list(self.start.get("parts") or [])
        self.mutants = list(self.start.get("mutants") or [])

    def rows(self, side, name):
        return [d for n, d in (self.se if side == "se" else self.sn) if n == name]

    def label_rows(self, name, label):
        return [d for d in self.rows("se", name) if d.get("label") == label]

    def ran(self, *parts):
        return any(p in self.parts for p in parts)

def _f(v, d=float("nan")):
    try:
        return float(v)
    except (TypeError, ValueError):
        return d

def _near(a, b, rel=REL):
    return abs(a - b) <= rel * max(1.0, abs(a), abs(b))

LABELS = (("light", "light"), ("tier2", "tier2"), ("bare", "bare"))
SHAPE_PARTS = ("heavy", "room", "light", "bare", "tier2")

def ran_labels(S):
    return [lab for part, lab in LABELS if S.ran(part)]

def descent_reach(S, lab):
    """(reached, deepest m): the label's grid reached DESCENT_TO (an s16.descend hold row, or an s16.depth row within REACH_TOL_M);
 deepest is SE's deepest row, else the launcher's s16_descent event, else NaN."""
    to = DESCENT_TO[lab]
    hold = [d for d in S.label_rows("s16.descend", lab) if d.get("phase") == "hold" and _f(d.get("depth"), 0) >= to - REACH_TOL_M]
    ds = [x for x in (_f(d.get("depth")) for d in S.label_rows("s16.depth", lab) + S.label_rows("s16.descend", lab)) if not math.isnan(x)]
    deepest = max(ds) if ds else float("nan")
    if math.isnan(deepest):
        seen = [_f(e.get("deepest")) for e in S.session if e.get("ev") == "s16_descent" and e.get("label") == lab]
        seen = [x for x in seen if not math.isnan(x)]
        deepest = max(seen) if seen else deepest
    return bool(hold) or (bool(ds) and max(ds) >= to - REACH_TOL_M), deepest

def gate_descents(S, rows):
    """The descent gate (the module docstring): a row whose descents fell short becomes MISSING 'descent did not reach N m'."""
    for r in rows:
        labs = [lab for lab in DESCENT_GATED.get(r["id"], ()) if S.ran(lab)]
        short = []
        for lab in labs:
            ok, deepest = descent_reach(S, lab)
            if not ok:
                short.append("%s descent did not reach %d m (deepest %s)" % (lab, DESCENT_TO[lab], "%.1f m" % deepest if not math.isnan(deepest) else "no depth row"))
        if short:
            r["detail"] = dict(r.get("detail") or {}, judged=r["status"], judgedMeasured=r["measured"])
            r["status"] = "MISSING"
            r["measured"] = "; ".join(short)[:160]

# ======================================================================================================================
# rows
# ======================================================================================================================

def check_pin(S, rows):
    site = S.rows("se", "s16.site")
    if not site:
        row(rows, "S16.PIN", "MISSING", "no s16.site row", "0x%016x" % T.PINNED_HASH, "the SE plugin's conventions pin", "session")
        return
    h = site[-1].get("hash")
    row(rows, "S16.PIN", "PASS" if h == "0x%016x" % T.PINNED_HASH else "FAIL", h, "0x%016x" % T.PINNED_HASH, "the SE plugin's conventions pin", "session")

def settled_grid(S, lab):
    """The label's last s16.grid row before its first damage (the room's gas recompute settles; damage changes the shape)."""
    grids = [g for g in S.rows("se", "s16.grid") if g.get("label") == lab]
    dm = [int(d.get("tick", 0)) for d in S.label_rows("s16.damage", lab)]
    if dm:
        grids = [g for g in grids if int(g.get("tick", 0)) < min(dm)] or grids[:1]
    return grids[-1] if grids else None

def check_ladder(S, rows):
    grids = S.rows("se", "s16.grid")
    bad = []
    for g in grids:
        s = {"size": int(g.get("size", 0)), "light": int(g.get("light", 0)), "heavy": int(g.get("heavy", 0)), "fitting": int(g.get("fitting", 0)),
             "tier": int(g.get("tier", 0))}
        hull, weak = T.hull_of(s)
        if int(g.get("crush", -1)) != T.crush_depth_of(s) or int(g.get("hull", -1)) != hull or g.get("weakest") != T.CLASSES[weak]:
            bad.append("%s: %s crush %s (twin %d)" % (g.get("label") or g.get("grid"), s, g.get("crush"), T.crush_depth_of(s)))
    seen = {}
    for lab in [p for p in SHAPE_PARTS if S.ran(p)]:
        g = settled_grid(S, lab)
        if g is None:
            bad.append("%s: no summary" % lab)
            continue
        want = SHAPES[lab]
        got = {k: int(g.get(k, -1)) for k in want}
        seen[lab] = got["crush"]
        if got != want:
            lost = want["blocks"] - got["blocks"]
            why = (" lost %d of %d blocks with no s16.damage row (a collision at the spawn: terrain or a grid in the column)" % (lost, want["blocks"])
                   if lost > 0 and not S.label_rows("s16.damage", lab) else "")
            bad.append("%s%s %s want %s" % (lab, why, got, want))
    if not grids or not seen:
        row(rows, "S16.LADDER", "MISSING", "%d grid rows" % len(grids), "the shapes' counts and depths", "the hull ladder, the counts and the tiers", "shapes")
        return
    row(rows, "S16.LADDER", "FAIL" if bad else "PASS", "; ".join(bad)[:120] if bad else "%d summaries, %s" % (len(grids), seen),
        "twin crush; the shapes' counts", "the hull ladder, the exposure counts and the tiers (s16.grid vs the twin and the shapes)", "shapes")

def check_bare(S, rows):
    dm = S.label_rows("s16.damage", "bare")
    depth = S.label_rows("s16.depth", "bare")
    limit = T.hull_crush(T.SMALL, T.LIGHT)
    if not depth:
        row(rows, "S16.BARE", "MISSING", "no bare rows", "damage past %d m" % limit, "a craft with nothing counted breaks past its limit", "bare")
        return
    early = [d for d in dm if _f(d.get("depth")) <= limit]
    applied = [d for d in dm if _f(d.get("applied"), 0) > 0 and _f(d.get("depth")) > limit]
    deepest = max(_f(d.get("depth"), 0) for d in depth)
    ok = applied and not early
    row(rows, "S16.BARE", "PASS" if ok else ("FAIL" if early or deepest > limit + 5 else "MISSING"), "applied %d, early %d, deepest %.1f m" % (len(applied), len(early), deepest),
        ">=1 applied past %d m (wet set)" % limit, "a craft with nothing counted (cockpit + battery) breaks past its limit: the wet-faced fallback set", "bare")

def check_group(S, rows):
    grp = S.rows("se", "s16.group")
    if not grp:
        row(rows, "S16.GROUP", "MISSING", "no grouped or docked grid", "twin group.crush", "the mechanical group's tier and docking (review 2)", "any", required=False)
        return
    bad = []
    for g in grp:
        want = T.effective_crush(int(g.get("own", 0)), int(g.get("groupTier", 0)), g.get("docked") is True, int(g.get("carrierCrush", 0)))
        if int(g.get("effective", -1)) != want:
            bad.append("grid %s: limit %s, twin %d (own %s, group tier %s, docked %s, carrier %s)" % (g.get("grid"), g.get("effective"), want, g.get("own"),
                                                                                                  g.get("groupTier"), g.get("docked"), g.get("carrierCrush")))
        if (g.get("exempt") is True) != (not T.damage_applies(g.get("docked") is True, g.get("inside") is True)):
            bad.append("grid %s: exempt %s" % (g.get("grid"), g.get("exempt")))
    exempt_grids = {str(g.get("grid")) for g in grp if g.get("exempt") is True}
    hurt = [d for d in S.rows("se", "s16.damage") if str(d.get("grid")) in exempt_grids]
    if hurt:
        bad.append("%d damage rows on exempt docked grids" % len(hurt))
    row(rows, "S16.GROUP", "FAIL" if bad else "PASS", "; ".join(bad)[:120] if bad else "%d group rows" % len(grp), "twin group.crush; exempt undamaged",
        "the mechanical group's tier and docking (review 2)", "any")

def check_grace(S, rows):
    gr = S.rows("se", "s16.grace")
    ends = [int(g.get("endTick", 0)) for g in gr if g.get("phase") == "start"]
    if not ends:
        row(rows, "S16.GRACE", "MISSING", "no s16.grace row", "no damage in the grace", "the 60 s load grace (review 10)", "any")
        return
    end = max(ends)
    early = [d for d in S.rows("se", "s16.damage") if int(d.get("tick", 0)) < end]
    row(rows, "S16.GRACE", "FAIL" if early else "PASS", "%d damage rows before tick %d" % (len(early), end), "0", "no crush damage during the 60 s load grace (review 10)", "any")

def check_snhud(S, rows):
    """format hud.sn, depth 6: seated, the compass's player readout "depth m / crush m" and no panel; the diver,
 the depth only; the readout's rect on screen (format hud.place). A build before depth 6 (no `text`) is MISSING."""
    if S.mode != "S16b":
        return
    sn = S.rows("sn", "s16.snhud")
    modes = {}
    for d in sn:
        modes[d.get("mode")] = modes.get(d.get("mode"), 0) + 1
    seated = [d for d in sn if d.get("mode") == "grid" and d.get("text") is not None]
    divers = [d for d in sn if d.get("mode") == "diver" and d.get("text") is not None]
    if not seated or not divers:
        row(rows, "S16.SNHUD", "MISSING", "SN %s; %d seated and %d diver readout rows with text (a build before depth 6?)" % (modes or "none", len(seated), len(divers)),
            "seated depth / crush, no panel; diver depth", "Subnautica's readout: seated depth and crush depth like the Cyclops, the diver's depth", "S16b")
        return
    live = [d for d in seated if d.get("lines") is not None and int(d.get("lines", -1)) >= 0]
    if not live:
        row(rows, "S16.SNHUD", "MISSING", "%d seated rows without the live TMP check (a build before depth 7)" % len(seated),
            "one line, no wider than the compass", "Subnautica's seated readout, checked on the rendered text", "S16b")
        return
    bad = []
    # the twin's rule on the live TMP numbers (format hud.sn, depth 8: one line within 90 % of the compass), and the plugin's own verdict
    wrapped = [d for d in live if not T.seated_text_fits(int(d.get("lines", 0)), _f(d.get("textW"), 1e9), _f(d.get("compassW"), 0)) or d.get("oneLine") is not True]
    if wrapped:
        d = wrapped[0]
        bad.append("seated %r: %s line(s), text %.0f of %.0f units, target %.0f (wrapped or too wide)" % (d.get("text"), d.get("lines"), _f(d.get("textW"), -1),
                                                                                                        _f(d.get("compassW"), -1), T.seated_target(_f(d.get("compassW"), 0))))
    for d in seated:
        crush = int(round(_f(d.get("crush"), 0)))
        text = str(d.get("text"))
        if " / %d" % crush not in text or d.get("seamoth") is True or d.get("panel") is True:
            bad.append("seated %r (crush %d, panel %s)" % (text, crush, d.get("seamoth") or d.get("panel")))
            break
    off = [d for d in live if d.get("centreDx") is not None and not T.seated_centred(_f(d.get("centreDx"), 1e9), _f(d.get("compassW"), 0))]
    if off:
        d = off[0]
        bad.append("seated %r off the compass centre by %.1f of %.0f units" % (d.get("text"), _f(d.get("centreDx")), _f(d.get("compassW"))))
    for d in divers:
        if " / " in str(d.get("text")) or d.get("seamoth") is True or d.get("panel") is True:
            bad.append("diver %r" % d.get("text"))
            break
    rects = []
    for name, group in (("seated", seated), ("diver", divers)):
        placed = [d for d in group if d.get("compassX0") is not None]
        if not placed:
            bad.append("%s: no placed rect" % name)
            continue
        d = placed[-1]
        sw, sh = _f(d.get("screenW"), 0), _f(d.get("screenH"), 0)
        x0, y0, x1, y1 = (_f(d.get(k)) for k in ("compassX0", "compassY0", "compassX1", "compassY1"))
        inside = 0 <= x0 and 0 <= y0 and x1 <= sw and y1 <= sh
        if not inside:
            bad.append("%s rect (%.0f,%.0f)-(%.0f,%.0f) outside %.0fx%.0f" % (name, x0, y0, x1, y1, sw, sh))
        rects.append("%s (%.0f,%.0f)-(%.0f,%.0f) of %.0fx%.0f" % (name, x0, y0, x1, y1, sw, sh))
    se = [d for d in S.rows("se", "s16.hud") if d.get("snHud") is True]
    meas = "seated %d (e.g. %r, 1 line, %.0f of %.0f units), diver %d; %s; SE lines replaced %d" % (
        len(seated), live[-1].get("text"), _f(live[-1].get("textW"), -1), _f(live[-1].get("compassW"), -1), len(divers), "; ".join(rects), len(se))
    meas = meas.replace(" units)", " units, target %.0f)" % T.seated_target(_f(live[-1].get("compassW"), 0)), 1)
    row(rows, "S16.SNHUD", "FAIL" if bad else "PASS", "; ".join(bad)[:120] if bad else meas[:160], "seated 'd m / L m', no panel; diver 'd m'; on screen",
        "Subnautica's readout: seated depth and crush depth like the Cyclops (no hull panel), the diver's depth; top-centre, never clipped", "S16b")

def check_static(S, rows):
    """depth 9: the harness's static 3^3 grid is never
 tracked nor rated: its job says tracked false with no crush, and no s16.grid/depth/damage/hud row names it."""
    ev = [e for e in S.session if e.get("ev") == "s16_static"]
    if not ev:
        row(rows, "S16.STATIC", "MISSING", "no s16_static event (the station part)", "never tracked, no crush depth",
            "a static grid (a station) has no crush depth", "station")
        return
    e = ev[-1]
    gid = str(e.get("grid"))
    named = [n for n, d in S.se if n in ("s16.grid", "s16.depth", "s16.damage", "s16.hud", "s16.voice", "s16.creak")
             and str(d.get("gridStr") or d.get("grid")) == gid]
    bad = []
    if e.get("tracked") is not False:
        bad.append("tracked %s" % e.get("tracked"))
    if e.get("crush") is not None:
        bad.append("crush %s" % e.get("crush"))
    if e.get("static") is not True:
        bad.append("not static (%s)" % e.get("static"))
    if named:
        bad.append("%d row(s) name it (%s)" % (len(named), named[0]))
    row(rows, "S16.STATIC", "FAIL" if bad else "PASS", "; ".join(bad)[:120] if bad else "station %s: static, not tracked, no crush, no rows" % gid,
        "never tracked, no crush depth", "a static grid (a station) has no crush depth", "station")

def _damage_of(S, lab):
    return S.label_rows("s16.damage", lab)

def check_break(S, rows):
    dm = _damage_of(S, "light")
    depth = S.label_rows("s16.depth", "light")
    limit = T.hull_crush(T.SMALL, T.LIGHT)
    if not dm and not depth:
        row(rows, "S16.BREAK", "MISSING", "no light rows", "damage only past %d m" % limit, "the light small hull breaks past its limit", "light")
        return
    early = [d for d in dm if _f(d.get("depth")) <= limit]
    applied = [d for d in dm if _f(d.get("applied"), 0) > 0]
    destroyed = [d for d in dm if d.get("destroyed") is True]
    deepest = max([_f(d.get("depth"), 0) for d in depth] + [0.0])
    ok = not early and applied and destroyed and deepest > limit
    meas = "early %d, applied %d, destroyed %d, deepest %.1f m" % (len(early), len(applied), len(destroyed), deepest)
    row(rows, "S16.BREAK", "PASS" if ok else ("FAIL" if early or deepest > limit + 5 else "MISSING"), meas,
        "0 early, >=1 applied and destroyed past %d m" % limit, "the light small hull: no damage before %d m, blocks break past it" % limit, "light")

def check_t3(S, rows):
    dm = _damage_of(S, "tier2")
    depth = S.label_rows("s16.depth", "tier2")
    limit = T.tier_crush(2)
    if not depth:
        row(rows, "S16.T2", "MISSING", "no tier2 depth rows", "none above %d m, damage past" % limit, "the top-tier hull holds to 2,000 m", "tier2")
        return
    early = [d for d in dm if _f(d.get("depth")) <= limit]
    approach = [d for d in depth if limit - 100 <= _f(d.get("depth"), 0) <= limit]
    past = [d for d in dm if _f(d.get("depth")) > limit and _f(d.get("applied"), 0) > 0]
    ok = not early and approach and past
    meas = "early %d, rows %d-%d m %d, damaged past %d" % (len(early), limit - 100, limit, len(approach), len(past))
    row(rows, "S16.T2", "PASS" if ok else ("FAIL" if early or not past and max(_f(d.get("depth"), 0) for d in depth) > limit + 5 else "MISSING"), meas,
        "0 early, seen 1900-2000, damage past", "the top-tier hull: no damage until 2,000 m, damage past it", "tier2")

def check_curve(S, rows):
    dm = [d for d in S.rows("se", "s16.damage") if d.get("label") in ("light", "tier2")]
    if not dm:
        row(rows, "S16.CURVE", "MISSING", "no damage rows", "twin curve", "the damage curve, per period, per block", "light,tier2")
        return
    bad = []
    per = {}
    for d in dm:
        depth, crush, size = _f(d.get("depth")), _f(d.get("crush")), int(d.get("size", 0))
        key = (d.get("grid"), int(d.get("tick", 0)))
        per.setdefault(key, []).append(d.get("block"))
        if not depth > crush:
            continue    # judged by S16.BREAK / S16.T2
        if not _near(_f(d.get("hps")), T.damage_per_second(depth, crush)) or not _near(_f(d.get("amount")), T.per_period_se(depth, crush, size)):
            bad.append("%s %.1f m: hps %s amount %s (twin %.4f / %.4f)" % (d.get("label"), depth, d.get("hps"), d.get("amount"),
                                                                           T.damage_per_second(depth, crush), T.per_period_se(depth, crush, size)))
    for (g, tick), blocks in per.items():
        if len(blocks) > T.D["MaxBlocksPerPeriod"]:
            bad.append("grid %s tick %d: %d blocks" % (g, tick, len(blocks)))
        if len(set(blocks)) != len(blocks):
            bad.append("grid %s tick %d: a block twice" % (g, tick))
    ticks = {}
    for g, tick in per:
        ticks.setdefault(g, []).append(tick)
    for g, ts in ticks.items():
        ts.sort()
        for a, b in zip(ts, ts[1:]):
            if (b - a) % T.D["PeriodTicks"] != 0:
                bad.append("grid %s: periods %d -> %d" % (g, a, b))
                break
    row(rows, "S16.CURVE", "FAIL" if bad else "PASS", "; ".join(bad)[:120] if bad else "%d rows in %d periods" % (len(dm), len(per)),
        "min(%g,%g+%g(d-L)) x k; <=%d/period" % (T.D["CapMilliHpS"] / 1000.0, T.D["BaseMilliHpS"] / 1000.0, T.D["PerMetreMilliHpS"] / 1000.0, T.D["MaxBlocksPerPeriod"]),
        "the damage curve past the limit, capped; once per period per grid", "light,tier2")

def check_creak(S, rows):
    labs = [x for x in ran_labels(S) if x in ("light", "tier2")]
    cr = [d for d in S.rows("se", "s16.creak") if d.get("label") in labs]
    if not cr:
        row(rows, "S16.CREAK", "MISSING", "no creak rows", "band 50 m, cadence", "hull creaks from 50 m above the limit", "light,tier2")
        return
    bad = []
    for d in cr:
        depth, crush, lv = _f(d.get("depth")), _f(d.get("crush")), int(d.get("level", 0))
        if depth < crush - T.K["BandM"]:
            bad.append("%s creak at %.1f m (limit %d)" % (d.get("label"), depth, crush))
            continue
        if lv != T.creak_level(depth, crush):
            bad.append("%s level %d at %.1f m (twin %d)" % (d.get("label"), lv, depth, T.creak_level(depth, crush)))
        why, prev, pl = d.get("reason"), int(d.get("prevTick", 0)), int(d.get("prevLevel", 0))
        if why == "period" and abs(int(d.get("tick", 0)) - prev - T.creak_period(pl)) > CADENCE_TOL_TICKS:
            bad.append("%s period %d after %d (level %d wants %d)" % (d.get("label"), int(d.get("tick", 0)), prev, pl, T.creak_period(pl)))
        if why == "rise" and not lv > pl:
            bad.append("%s rise %d -> %d" % (d.get("label"), pl, lv))
        if why == "enter" and pl != 0:
            bad.append("%s enter with level %d" % (d.get("label"), pl))
    for lab in labs:
        c = [int(d.get("tick", 0)) for d in cr if d.get("label") == lab]
        dm = [int(d.get("tick", 0)) for d in _damage_of(S, lab)]
        if not c:
            bad.append("%s: no creak" % lab)
        elif dm and min(dm) < min(c):
            bad.append("%s: damage at tick %d before the first creak %d" % (lab, min(dm), min(c)))
    row(rows, "S16.CREAK", "FAIL" if bad else "PASS", "; ".join(bad)[:120] if bad else "%d creaks" % len(cr), "d >= L-50, cadence 540/360/180",
        "hull creaks from 50 m above the limit, before the damage", "light,tier2")

def check_voice(S, rows):
    labs = [x for x in ran_labels(S) if x in ("light", "tier2")]
    vo = [d for d in S.rows("se", "s16.voice") if d.get("label") in labs]
    if not vo:
        row(rows, "S16.VOICE", "MISSING", "no voice rows", "one per crossing", "the Cyclops 'hull integrity critical' voice once", "light,tier2")
        return
    bad = []
    counts = []
    for lab in labs:
        played = sorted((d for d in vo if d.get("label") == lab and d.get("played") is True), key=lambda d: int(d.get("ms", 0)))
        if not played:
            bad.append("%s: none played" % lab)
            continue
        for d in played:
            if not _f(d.get("depth")) > _f(d.get("crush")):
                bad.append("%s: played at %.1f m above the limit" % (lab, _f(d.get("depth"))))
        ms = [int(d.get("ms", 0)) for d in played]
        for a, b in zip(ms, ms[1:]):
            if b - a < T.V["CooldownMs"]:
                bad.append("%s: twice within %d ms" % (lab, b - a))
                break
            if b - a > T.V["RepeatMs"] + VOICE_TOL_MS:
                bad.append("%s: %d ms without the warning while past the limit" % (lab, b - a))
                break
        # the time past the limit from the first play (the HUD rows of the watched grid): one play per RepeatMs of it
        crush = _f(played[0].get("crush"))
        past = [int(h.get("tick", 0)) for h in S.rows("se", "s16.hud") if h.get("label") == lab and h.get("mode") == "grid" and _f(h.get("depth")) > crush]
        if past:
            span = (max(past) - int(played[0].get("tick", 0))) * 1000 // 60
            lo = 1 + max(0, (span - VOICE_TOL_MS) // T.V["RepeatMs"])
            hi = 1 + (span + VOICE_TOL_MS) // T.V["RepeatMs"]
            if not lo <= len(played) <= hi:
                bad.append("%s: %d played in %.1f s past the limit (want %d-%d)" % (lab, len(played), span / 1000.0, lo, hi))
        counts.append("%s %d" % (lab, len(played)))
    row(rows, "S16.VOICE", "FAIL" if bad else "PASS", "; ".join(bad)[:120] if bad else "%d event(s), played %s" % (len(vo), ", ".join(counts)),
        "crossing + every %d s past, %d s cooldown" % (T.V["RepeatMs"] // 1000, T.V["CooldownMs"] // 1000),
        "the 'hull integrity critical' voice at the crossing and every 20 s while past the limit", "light,tier2")

def check_hud(S, rows):
    site = S.rows("se", "s16.site")
    labs = [x for x in ran_labels(S) if x in ("light", "tier2")]
    hud = [d for d in S.rows("se", "s16.hud") if d.get("mode") == "grid" and d.get("label") in labs]
    if not site or len(hud) < 5:
        row(rows, "S16.HUD", "MISSING", "%d HUD rows, site %s" % (len(hud), bool(site)), "corners, state", "the HUD depth, crush depth and state", "light,tier2")
        return
    c = [float(v) for v in site[-1]["c"]]
    rs = float(site[-1]["rs"])
    bad = []
    for d in hud:
        cs = d.get("corners") or []
        if len(cs) != 24:
            bad.append("no corners")
            continue
        deepest = max(rs - math.sqrt((cs[i] - c[0]) ** 2 + (cs[i + 1] - c[1]) ** 2 + (cs[i + 2] - c[2]) ** 2) for i in range(0, 24, 3))
        depth, crush = _f(d.get("depth")), _f(d.get("crush"))
        if abs(depth - deepest) > HUD_TOL_M:
            bad.append("%s HUD %.2f m, lowest corner %.2f m" % (d.get("label"), depth, deepest))
            continue
        st = T.HUD_STATES[T.hud_state(depth, crush, d.get("damageOn") is True)]
        if d.get("state") != st:
            bad.append("%s %.1f m: state %s (twin %s)" % (d.get("label"), depth, d.get("state"), st))
        gate = d.get("gate")
        if gate is not None and (d.get("damageOn") is True) != (gate == T.DAMAGE_GATES[T.GATE_DESTRUCTIBLE] and not (int(d.get("flags", 0)) & T.FLAG_EXEMPT)):
            bad.append("%s: damageOn %s with gate %s" % (d.get("label"), d.get("damageOn"), gate))
        line = d.get("line")
        if line is not None and line != T.SE_LINES[T.LINE_SUPPRESSED if d.get("snHud") is True else T.LINE_GRID]:
            bad.append("%s: SE line %s with snHud %s" % (d.get("label"), line, d.get("snHud")))
    divers = [d for d in S.rows("se", "s16.hud") if d.get("mode") == "diver"]
    shown = [d for d in divers if d.get("line") is not None and d.get("line") != T.SE_LINES[T.LINE_NONE]]
    if shown:
        bad.append("%d diver row(s) with an SE line (%s)" % (len(shown), shown[0].get("line")))
    row(rows, "S16.HUD", "FAIL" if bad else "PASS", "; ".join(bad)[:120] if bad else "%d rows, %d diver rows without an SE line" % (len(hud), len(divers)),
        "lowest corner +-0.05 m, twin state; diver line none", "the HUD depth (lowest point), crush depth and state; no SE diver line", "light,tier2")

def check_sn(S, rows):
    if S.mode != "S16b":
        return
    se_c = sorted({int(d.get("seq", 0)) for d in S.rows("se", "s16.creak")})
    se_v = sorted({int(d.get("seq", 0)) for d in S.rows("se", "s16.voice") if d.get("played") is True})
    sn_c = [int(d.get("seq", 0)) for d in S.rows("sn", "s16.creak")]
    sn_v = [int(d.get("seq", 0)) for d in S.rows("sn", "s16.voice")]
    if not se_c or not sn_c:
        row(rows, "S16.SN", "MISSING", "SE creaks %d, Subnautica creaks %d" % (len(se_c), len(sn_c)), "one sound per SE event",
            "Subnautica plays the creaks and the voice once per SE event", "S16b")
        return
    bad = []
    if len(set(sn_c)) != len(sn_c):
        bad.append("creak seq played twice (%d rows, %d seqs)" % (len(sn_c), len(set(sn_c))))
    first = min(sn_c)
    missing = [s for s in se_c if s >= first and s not in set(sn_c)]
    extra = [s for s in set(sn_c) if s not in set(se_c)]
    if len(missing) > 1 or extra:
        bad.append("creak seqs missing %s extra %s" % (missing[:5], extra[:5]))
    if sorted(sn_v) != [s for s in se_v if not sn_v or s >= min(sn_v)] or len(set(sn_v)) != len(sn_v):
        bad.append("voice seqs SN %s SE %s" % (sn_v, se_v))
    row(rows, "S16.SN", "FAIL" if bad else "PASS", "; ".join(bad)[:120] if bad else "%d creaks, %d voices" % (len(sn_c), len(sn_v)),
        "one per SE seq", "Subnautica plays the creaks and the voice once per SE event", "S16b")

def mutant_rows(S, rows):
    for m in S.mutants:
        want = MUTANT_ROWS.get(m, ())
        for r in rows:
            if r["id"] in want:
                r["required"] = False
                r["what"] = r["what"] + " [expected red under %s]" % m
        measured = [r for r in rows if r["id"] in want and r["status"] in ("PASS", "FAIL")]
        red = [r["id"] for r in measured if r["status"] == "FAIL"]
        st = "MISSING" if not measured else ("PASS" if len(red) == len(want) else "FAIL")
        row(rows, "%s.red" % m, st, "red: %s" % (",".join(red) or "none"), "red: %s" % ",".join(want), "seeded failure %s turns its rows red" % m, "mutants")

CHECKS = ((("selftest", "light", "tier2"), check_pin), (SHAPE_PARTS, check_ladder), (("station",), check_static), (("light",), check_break), (("tier2",), check_t3),
          (("bare",), check_bare), (("selftest",) + SHAPE_PARTS, check_group), (("light", "tier2", "bare"), check_grace),
          (("light", "tier2"), check_snhud),
          (("light", "tier2"), check_curve), (("light", "tier2"), check_creak), (("light", "tier2"), check_voice), (("light", "tier2"), check_hud),
          (("light", "tier2"), check_sn))

def evaluate(folder):
    S = Session(folder)
    rows = []
    for parts, fn in CHECKS:
        if not S.ran(*parts):
            continue
        try:
            fn(S, rows)
        except Exception as e:  # noqa: BLE001 - an analyser bug is a FAIL row, never a crash
            row(rows, "S16.%s" % fn.__name__[6:].upper(), "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:120], parts[0])
    gate_descents(S, rows)
    mutant_rows(S, rows)
    return rows, S

def write_results(folder, rows, strict=True, extra=None) -> bool:
    ok = C.verdict(rows, strict)
    res = {"tool": "analyze_s16", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "s16-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "s16-table.txt"), "w", encoding="utf-8") as f:
        f.write(C.table(rows) + "\n")
    return ok

# ======================================================================================================================
# self-test: synthetic sessions in the plugins' row forms, simulated with the twin's rules (the SE side's algorithm)
# ======================================================================================================================

E0 = 1792800000.0
RS = 60000.00390625
CEN = (-1000000.0, 250000.0, 1500000.0)

def _iso(t):
    import datetime
    return datetime.datetime.utcfromtimestamp(E0 + t).strftime("%Y-%m-%dT%H:%M:%S.%f") + "Z"

class _Synth:
    def __init__(self, folder, mode, parts, mutants=()):
        self.folder, self.mode = folder, mode
        os.makedirs(folder, exist_ok=True)
        self.sess, self.se, self.sn = [], [], []
        self.tick = 0
        self.mutants = set(mutants)
        self.creak_seq = self.voice_seq = self.damage_seq = 0
        self.event("start", mode=mode, parts=list(parts), mutants=list(mutants), kind="s16")

    def t(self):
        return self.tick / 60.0

    def event(self, ev, **kw):
        self.sess.append(dict({"utc": _iso(self.tick / 60.0), "ev": ev}, **kw))

    def se_row(self, ev, **kw):
        self.se.append(dict({"t": _iso(self.t()), "ev": ev}, **kw))

    def sn_row(self, ev, **kw):
        self.sn.append({"t": _iso(self.t()), "level": "info", "event": ev, "ev": ev, "data": kw})

    def write(self):
        self.event("end", rc=None)
        for name, recs in (("s16-session.jsonl", self.sess), ("se-20261023-000000.jsonl", self.se), ("sn-20261023-000000-1234.jsonl", self.sn)):
            with open(os.path.join(self.folder, name), "w", encoding="utf-8") as f:
                for r in recs:
                    f.write(json.dumps(r) + "\n")

def _point(depth, lateral=(0.0, 0.0)):
    """An SE world point at depth below the sea on the +Z radial through the test centre, shifted laterally (x, y)."""
    r = RS - depth
    return (CEN[0] + lateral[0], CEN[1] + lateral[1], CEN[2] + r)

def _depth_of(p):
    return RS - math.sqrt(sum((p[i] - CEN[i]) ** 2 for i in range(3)))

def _grid_row(s, gid, label, size, counts, tier, blocks, wet):
    m = s.mutants
    summary = {"size": size, "light": 0 if "S16.M8" in m else counts[0], "heavy": 0 if "S16.M8" in m else counts[1], "fitting": 0,
               "tier": 0 if "S16.M1" in m else tier}
    rated = T.crush_depth_of(summary)
    hull, weak = T.hull_of(summary)
    s.se_row("s16.grid", grid=gid, label=label, name="DE.S16 " + label, size=size, light=summary["light"], heavy=summary["heavy"], fitting=0, wet=wet,
             tier=summary["tier"], blocks=blocks, hull=hull, weakest=T.CLASSES[weak], crush=rated, wetSet=summary["light"] + summary["heavy"] == 0,
             scanMs=1.0, tick=s.tick)
    return summary, rated, hull, weak

def _shape(s, label, size, counts, tier, blocks, wet):
    """A scripted hull spawned, summarised and removed (heavy, room): no descent."""
    s.tick += 30
    _grid_row(s, 900 + len(s.se), label, size, counts, tier, blocks, wet)
    s.tick += 300

def _descent(s, label, size, n, crush, start_bottom, to, rate, cls_counts, tier, wet=None, cells=None, hold_s=3.0):
    """One scripted descent as S16Host runs it: the cells (default n^3 cubes), bottom from start_bottom to `to` at `rate` m/s, then a hold_s hold."""
    m = s.mutants
    gsz = 2.5 if size == T.LARGE else 0.5
    k = T.k_of(size)
    gid = 1000 + len(s.se)
    cells = cells or [(x, y, z) for x in range(n) for y in range(n) for z in range(n)]
    summary, rated, hull, weak = _grid_row(s, gid, label, size, cls_counts, tier, len(cells), wet if wet is not None else cls_counts[0] + cls_counts[1])
    wet_set = summary["light"] + summary["heavy"] == 0
    if wet_set and "S16.M10" in m:
        cells = []      # S16.M10: no fallback set, nothing to damage
    blocks = {c: 100.0 * (1 if size == T.SMALL else 25) for c in cells}
    clock, arm, rule = T.CreakClock(), T.VoiceArm(), T.VoiceRule()
    band = 2 * T.K["BandM"] if "S16.M4" in m else T.K["BandM"]
    thr = rated - T.K["BandM"] if "S16.M2" in m else rated
    t0 = s.tick
    bottom = start_bottom
    hold_until = None
    half = n * gsz / 2.0
    while True:
        s.tick += 1
        if hold_until is None:
            bottom = min(to, start_bottom + rate * (s.tick - t0) / 60.0)
            if bottom >= to:
                hold_until = s.tick + int(hold_s * 60)
                s.se_row("s16.descend", grid=gid, label=label, tick=s.tick, phase="hold", depth=bottom, to=to, **{"from": start_bottom}, seconds=(s.tick - t0) / 60.0)
        elif s.tick >= hold_until:
            break
        corners = [_point(bottom - (dz * n * gsz), (dx * half, dy * half)) for dx in (-1, 1) for dy in (-1, 1) for dz in (0, 1)]
        deepest = max(_depth_of(p) for p in corners)
        centre = bottom - half
        hud_depth = centre if "S16.M6" in m else deepest
        level = T.creak_level(hud_depth, rated, band)
        prev_tick, prev_level = clock.last_tick, (clock.last_level if clock.active else 0)
        why = clock.step(s.tick, level)
        if why:
            s.creak_seq += 1
            s.se_row("s16.creak", grid=gid, label=label, tick=s.tick, level=level, reason=T.REASONS[why], prevTick=prev_tick, prevLevel=prev_level,
                     depth=hud_depth, crush=rated, seq=s.creak_seq)
            if s.mode == "S16b":
                if "S16.M7" not in m:
                    s.sn_row("s16.creak", seq=s.creak_seq, level=level, played=True, path="event:/sub/base/crush_1", coalesced=0, mutant=False)
        if "S16.M7" in m and s.mode == "S16b" and level > 0 and s.tick % 30 == 0:
            s.sn_row("s16.creak", seq=s.creak_seq, level=level, played=True, path="event:/sub/base/crush_1", coalesced=0, mutant=True)
        ms = s.tick * 1000 // 60
        crossing = arm.armed
        ev = arm.step(hud_depth, rated, ms)
        if "S16.M5" in m and hud_depth > rated and not ev and s.tick % 60 == 0:
            ev = True
        if ev:
            play = "S16.M5" in m or rule.try_fire(gid, ms)
            if play:
                s.voice_seq += 1
                if s.mode == "S16b":
                    s.sn_row("s16.voice", seq=s.voice_seq, level=level, played=True, path="event:/sub/cyclops/AI_hull_crit", coalesced=0, mutant=False)
            s.se_row("s16.voice", grid=gid, label=label, tick=s.tick, ms=ms, depth=hud_depth, crush=rated, played=play, reason="cross" if crossing else "repeat", seq=s.voice_seq)
        if (s.tick - t0) % 30 == 0:
            st = T.hud_state(hud_depth, rated, True)
            if st != T.OFF:
                cs = [v for p in corners for v in p]
                s.se_row("s16.hud", mode="grid", tick=s.tick, depth=hud_depth, crush=rated, state=T.HUD_STATES[st], level=level, breathX10=0, damageOn=True,
                         gate=T.DAMAGE_GATES[T.GATE_DESTRUCTIBLE], flags=T.FLAG_GRID | T.FLAG_DAMAGE, line=T.SE_LINES[T.LINE_GRID], snHud=False,
                         grid=gid, label=label, tier=summary["tier"], hullCode=T.hull_code(size, weak), corners=cs, api=True)
        if (s.tick - t0) % 60 == 0:
            s.se_row("s16.depth", grid=gid, label=label, tick=s.tick, depth=deepest, centreDepth=centre, crush=rated, tier=summary["tier"], blocks=len(blocks), api=True)
            if deepest > thr:
                keys = list(blocks)
                depths = [bottom - (y + 0.5) * gsz for (_x, y, _z) in keys]
                picked = T.pick(depths, thr)
                for i in picked:
                    d = depths[i]
                    if "S16.M2" in m and d <= rated:
                        hps = T.D["BaseMilliHpS"] / 1000.0          # S16.M2: the base rate from the creak band on
                    elif "S16.M3" in m:
                        hps = (T.D["BaseMilliHpS"] + T.D["PerMetreMilliHpS"] * (d - rated)) / 1000.0
                    else:
                        hps = T.damage_per_second(d, rated)
                    amount = hps * k
                    before = blocks[keys[i]]
                    after = max(0.0, before - amount)
                    destroyed = after <= 0
                    if destroyed:
                        del blocks[keys[i]]
                    else:
                        blocks[keys[i]] = after
                    s.se_row("s16.damage", grid=gid, label=label, tick=s.tick, block="%d,%d,%d" % keys[i], subtype="X", size=size, depth=d, crush=rated,
                             hps=hps, k=k, amount=amount, applied=before - after, destroyed=destroyed, wetSet=wet_set, gridDepth=deepest)
                if picked:
                    s.damage_seq += 1
                    if s.mode == "S16b":
                        s.sn_row("s16.damage", seq=s.damage_seq, level=level, played=True, path="event:/sub/cyclops/crush", coalesced=0, mutant=False)

def _group_rows(s):
    """Two grids of an project scene (synthetic): a rotor subgrid of a tier-2 sub, and a small craft docked inside its hangar."""
    m = s.mutants
    for gid, own, gt, docked, carrier, inside in ((3001, 200, 2, False, 0, False), (3002, 200, 0, True, 1700, True), (3003, 500, 0, True, 1700, False)):
        eff = own if "S16.M9" in m else T.effective_crush(own, gt, docked, carrier)
        exempt = False if "S16.M9" in m else not T.damage_applies(docked, inside)
        s.se_row("s16.group", grid=gid, label=None, tick=s.tick, own=own, tier=0, groupTier=gt, docked=docked, carrier=4000 if docked else 0, carrierCrush=carrier,
                 inside=inside, exempt=exempt, effective=eff, physicalNodes=2)
        if "S16.M9" in m and docked and inside:   # the ignored dock lets the crush damage reach the craft in the hangar
            s.se_row("s16.damage", grid=gid, label=None, tick=s.tick, block="0,0,0", subtype="X", size=1, depth=250.0, crush=own, hps=T.damage_per_second(250.0, own),
                     k=1.0, amount=T.per_period_se(250.0, own, 1), applied=1.0, destroyed=False, wetSet=False, gridDepth=250.5)

def synth(folder, mode, mutant=None, short=None):
    """A synthetic session; `short`: that label's descent stops 60 m above its target (the descent gate)."""
    parts = ("selftest", "heavy", "room", "station", "light", "bare", "tier2", "teardown")
    to = {lab: v - (60.0 if lab == short else 0.0) for lab, v in DESCENT_TO.items()}
    s = _Synth(folder, mode, parts, (mutant,) if mutant else ())
    s.se_row("s16.site", c=list(CEN), rs=RS, planet=1, hash="0x%016x" % T.PINNED_HASH, mutants=mutant or "")
    s.se_row("s16.grace", phase="start", tick=s.tick, endTick=s.tick + T.D["GraceTicks"], gridsPast=0)
    for p in parts:
        s.event("part", name=p, phase="begin")
        if p == "selftest":
            s.tick += 300
            s.se_row("s16.grace", phase="notice", tick=s.tick, endTick=T.D["GraceTicks"], gridsPast=0)
            s.tick = T.D["GraceTicks"]
            s.se_row("s16.grace", phase="end", tick=s.tick, endTick=T.D["GraceTicks"], gridsPast=0)
            _group_rows(s)
            for i in range(4):   # the parked diver (format hud.se: no SE line)
                s.se_row("s16.hud", mode="diver", tick=s.tick + 30 * i, depth=6.2, crush=0, state="safe", level=0, breathX10=10, damageOn=True,
                         gate=T.DAMAGE_GATES[T.GATE_DESTRUCTIBLE], flags=T.FLAG_DIVER, line=T.SE_LINES[T.LINE_NONE], snHud=mode == "S16b", hullPct=0, powerPct=255)
        elif p == "heavy":
            _shape(s, "heavy", T.SMALL, (0, 56), 0, 64, 56)
        elif p == "room":
            _shape(s, "room", T.LARGE, (0, 98), 0, 100, 98)
        elif p == "station":
            s.event("s16_static", part="station", grid="5551", tracked=False, crush=None, scanned=False, static=True, exists=True)
        elif p == "light":
            _descent(s, "light", T.SMALL, 4, T.hull_crush(T.SMALL, T.LIGHT), 51.0, to["light"], 10.0, (56, 0), 0)   # depth 9: 50 -> 280 m
        elif p == "bare":
            _descent(s, "bare", T.SMALL, 1, T.hull_crush(T.SMALL, T.LIGHT), 151.0, to["bare"], 5.0, (0, 0), 0, wet=2, cells=[(0, 0, 0), (1, 0, 0)])
        elif p == "tier2":
            _descent(s, "tier2", T.LARGE, 3, T.tier_crush(2), 1904.0, to["tier2"], 10.0, (0, 26), 2, hold_s=18.0)
        s.tick += 60
        s.event("part", name=p, phase="end")
    if mode == "S16b":
        s.sn_row("s16.snhud", mode="diver", depth=6.2, crush=0.0, state=1, text="6 m", tone="normal", compass=True, seamoth=False, panel=False, flags=5,
                 screenW=2560, screenH=1440, scale=4.0 / 3.0, fits=2, compassX0=1146.7, compassY0=86.4, compassX1=1413.3, compassY1=226.4)
        wrap = "S16.M11" in s.mutants
        s.sn_row("s16.snhud", mode="grid", depth=250.0, crush=200.0, state=4, text="250 m / 200 m", tone="danger", compass=True, seamoth=False, panel=False, flags=9,
                 lines=2 if wrap else 1, textW=120.0 if wrap else 138.0, compassW=155.6, oneLine=not wrap, mutantWrap=wrap, centreDx=0.6, centred=True,
                 screenW=2560, screenH=1440, scale=4.0 / 3.0, fits=3, compassX0=1106.7, compassY0=86.4, compassX1=1453.3, compassY1=226.4)
    s.write()

MUTANT_MODE = {"S16.M11": "S16b", "S16.M1": "S16se", "S16.M2": "S16se", "S16.M3": "S16se", "S16.M4": "S16se", "S16.M5": "S16se", "S16.M6": "S16se", "S16.M7": "S16b",
               "S16.M8": "S16se", "S16.M9": "S16se", "S16.M10": "S16se"}

def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S16 analyzer self-test (tests/s16/analyze_s16.py --selftest)")
    root = tempfile.mkdtemp(prefix="de-s16-analyze-")
    try:
        check(T.text_hash()[0] == T.PINNED_HASH, "the twin's pin 0x%016x" % T.PINNED_HASH)
        base = ("S16.PIN", "S16.LADDER", "S16.STATIC", "S16.BREAK", "S16.T2", "S16.BARE", "S16.GROUP", "S16.GRACE", "S16.CURVE", "S16.CREAK", "S16.VOICE", "S16.HUD")
        want = {"S16se": base, "S16b": base + ("S16.SN",)}
        for mode in ("S16se", "S16b"):
            folder = os.path.join(root, mode)
            synth(folder, mode)
            rows, _S = evaluate(folder)
            st = {r["id"]: r["status"] for r in rows}
            bad = [(r["id"], r["status"], r["measured"]) for r in rows if r.get("required", True) and r["status"] not in ("PASS", "INFO")]
            check(not bad, "%s synthetic session: %d rows, all required PASS%s" % (mode, len(rows), (" (bad %s)" % bad) if bad else ""))
            check(all(st.get(i) == "PASS" for i in want[mode]), "%s: rows %s PASS" % (mode, ",".join(want[mode])))
            write_results(folder, rows)
            check(os.path.exists(os.path.join(folder, "s16-table.txt")), "%s: s16-table.txt and s16-results.json" % mode)
        for mid in sorted(MUTANT_ROWS, key=lambda k: int(k.split("M")[1])):
            mode = MUTANT_MODE[mid]
            folder = os.path.join(root, "%s-%s" % (mode, mid))
            synth(folder, mode, mid)
            rows, _S = evaluate(folder)
            st = {r["id"]: r["status"] for r in rows}
            red = [i for i in MUTANT_ROWS[mid] if st.get(i) == "FAIL"]
            others = [r["id"] for r in rows if r["status"] == "FAIL" and r["id"] not in MUTANT_ROWS[mid] and not r["id"].endswith(".red")]
            ok = len(red) == len(MUTANT_ROWS[mid]) and st.get(mid + ".red") == "PASS" and not others and C.verdict(rows, True)
            check(ok, "%s: %s red, %s.red PASS, nothing else red%s" % (mid, ",".join(MUTANT_ROWS[mid]), mid,
                                                                     (" (others %s; statuses %s)" % (others, {i: st.get(i) for i in MUTANT_ROWS[mid]})) if not ok else ""))
        # a mutant that did nothing: its.red row fails
        folder = os.path.join(root, "silent")
        synth(folder, "S16se")
        path = os.path.join(folder, "s16-session.jsonl")
        lines = open(path, encoding="utf-8").read().splitlines()
        first = json.loads(lines[0])
        first["mutants"] = ["S16.M3"]
        lines[0] = json.dumps(first)
        open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
        rows, _S = evaluate(folder)
        st = {r["id"]: r["status"] for r in rows}
        check(st.get("S16.M3.red") == "FAIL", "a silent mutant fails its .red row")
        # the descent gate: a descent that fell short leaves its rows MISSING (never PASS/FAIL), the others judged
        for lab, gated in (("light", ("S16.BREAK", "S16.CURVE", "S16.CREAK", "S16.VOICE", "S16.HUD", "S16.SN")), ("tier2", ("S16.T2", "S16.CURVE", "S16.CREAK", "S16.VOICE", "S16.HUD", "S16.SN")),
                           ("bare", ("S16.BARE",))):
            folder = os.path.join(root, "short-" + lab)
            synth(folder, "S16b", short=lab)
            rows, _S = evaluate(folder)
            st = {r["id"]: r for r in rows}
            miss = [i for i in gated if st.get(i, {}).get("status") == "MISSING" and "%s descent did not reach %d m" % (lab, DESCENT_TO[lab]) in st[i]["measured"]]
            judged = [i for i in ("S16.BREAK", "S16.T2", "S16.BARE") if i not in DESCENT_GATED or not set(DESCENT_GATED[i]) & {lab}]
            ok = len(miss) == len(gated) and all(st.get(i, {}).get("status") == "PASS" for i in judged) and st.get("S16.LADDER", {}).get("status") == "PASS"
            check(ok, "%s descent 60 m short: %s MISSING 'descent did not reach %d m', %s still PASS" % (lab, ",".join(gated), DESCENT_TO[lab], ",".join(judged)))
        # the damage gate on the HUD rows (format damage.gate): damageOn without a destructible gate is red
        folder = os.path.join(root, "gate")
        synth(folder, "S16se")
        import glob as _g
        se_files = _g.glob(os.path.join(folder, "se-*.jsonl"))
        lines = open(se_files[0], encoding="utf-8").read().splitlines()
        lines = [ln.replace('"gate": "destructible"', '"gate": "indestructible"') if '"s16.hud"' in ln else ln for ln in lines]
        open(se_files[0], "w", encoding="utf-8").write("\n".join(lines) + "\n")
        rows, _S = evaluate(folder)
        st = {r["id"]: r for r in rows}
        check(st.get("S16.HUD", {}).get("status") == "FAIL" and "gate indestructible" in st["S16.HUD"]["measured"], "damageOn with an indestructible gate: S16.HUD red")
        # night queue: a shape broken at its spawn (no damage rows) is red and says so
        folder = os.path.join(root, "broken")
        synth(folder, "S16se")
        se_files = _g.glob(os.path.join(folder, "se-*.jsonl"))
        with open(se_files[0], "a", encoding="utf-8") as fh:
            fh.write(json.dumps({"t": "2026-10-23T20:34:12.485Z", "ev": "s16.grid", "grid": 902, "label": "room", "size": 2, "light": 0, "heavy": 2, "fitting": 0,
                                 "wet": 2, "tier": 0, "blocks": 2, "crush": 1200, "hull": 1200, "weakest": "HeavyArmor"}) + chr(10))
        rows, _S = evaluate(folder)
        st = {r["id"]: r for r in rows}
        check(st.get("S16.LADDER", {}).get("status") == "FAIL" and "lost 98 of 100 blocks with no s16.damage row" in st["S16.LADDER"]["measured"],
              "a shape broken at its spawn: S16.LADDER red, the collision named")
        # depth 9: a station that got a crush depth (a row names it), or a seated readout off the compass centre, is red
        folder = os.path.join(root, "station")
        synth(folder, "S16b")
        se_files = _g.glob(os.path.join(folder, "se-*.jsonl"))
        with open(se_files[0], "a", encoding="utf-8") as fh:
            fh.write(json.dumps({"t": "2026-10-24T16:40:00.000Z", "ev": "s16.grid", "grid": 5551, "gridStr": "5551", "label": None, "size": 2, "light": 0,
                                 "heavy": 26, "fitting": 0, "wet": 26, "tier": 0, "blocks": 27, "crush": 1200, "hull": 1200, "weakest": "HeavyArmor"}) + chr(10))
        sn_files = _g.glob(os.path.join(folder, "sn-*.jsonl"))
        lines = open(sn_files[0], encoding="utf-8").read().splitlines()
        lines = [ln.replace('"centreDx": 0.6', '"centreDx": -20.0') if '"s16.snhud"' in ln and '"mode": "grid"' in ln else ln for ln in lines]
        open(sn_files[0], "w", encoding="utf-8").write(chr(10).join(lines) + chr(10))
        rows, _S = evaluate(folder)
        st = {r["id"]: r for r in rows}
        check(st.get("S16.STATIC", {}).get("status") == "FAIL" and "name it" in st["S16.STATIC"]["measured"], "a static grid with a rating row: S16.STATIC red")
        check(st.get("S16.SNHUD", {}).get("status") == "FAIL" and "off the compass centre" in st["S16.SNHUD"]["measured"], "a seated readout 20 units off centre: S16.SNHUD red")
        # format hud.sn, depth 6: a seated readout without its crush depth, or with the hull panel, is red
        folder = os.path.join(root, "seatedpanel")
        synth(folder, "S16b")
        sn_files = _g.glob(os.path.join(folder, "sn-*.jsonl"))
        lines = open(sn_files[0], encoding="utf-8").read().splitlines()
        lines = [ln.replace('"seamoth": false', '"seamoth": true') if '"s16.snhud"' in ln and '"mode": "grid"' in ln else ln for ln in lines]
        open(sn_files[0], "w", encoding="utf-8").write("\n".join(lines) + "\n")
        rows, _S = evaluate(folder)
        st = {r["id"]: r for r in rows}
        check(st.get("S16.SNHUD", {}).get("status") == "FAIL" and "seated" in st["S16.SNHUD"]["measured"], "a seated readout with the hull panel: S16.SNHUD red")
        # format hud.se: a diver row with an SE line is red
        folder = os.path.join(root, "diverline")
        synth(folder, "S16se")
        se_files = _g.glob(os.path.join(folder, "se-*.jsonl"))
        lines = open(se_files[0], encoding="utf-8").read().splitlines()
        lines = [ln.replace('"line": "none"', '"line": "grid"') if '"s16.hud"' in ln and '"mode": "diver"' in ln else ln for ln in lines]
        open(se_files[0], "w", encoding="utf-8").write("\n".join(lines) + "\n")
        rows, _S = evaluate(folder)
        st = {r["id"]: r for r in rows}
        check(st.get("S16.HUD", {}).get("status") == "FAIL" and "diver row(s) with an SE line" in st["S16.HUD"]["measured"], "a diver row with an SE line: S16.HUD red")
        # no data: MISSING, never PASS
        folder = os.path.join(root, "empty")
        os.makedirs(folder)
        with open(os.path.join(folder, "s16-session.jsonl"), "w", encoding="utf-8") as f:
            f.write(json.dumps({"ev": "start", "mode": "S16b", "parts": ["selftest", "heavy", "room", "light", "bare", "tier2"], "mutants": []}) + "\n")
        rows, _S = evaluate(folder)
        check(rows and all(r["status"] in ("MISSING", "INFO") for r in rows), "a session without rows: every row MISSING")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("S16 ANALYZE SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
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
    print("S16 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
