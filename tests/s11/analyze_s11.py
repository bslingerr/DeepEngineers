"""S11 oracle table (ROADMAP §14 "### S11 Creatures and hazards"; docs/S11-BRIEF.md; CONTRACT §5.4 "S11 conventions").

 python -I tests/s11/analyze_s11.py <tests/out/s11/<stamp>> [--lenient] [--json]
 python -I tests/s11/analyze_s11.py --selftest

Reads a folder written by `launcher.py s11` (s11-session.jsonl; se-*.jsonl / sn-*.jsonl or the stand-ins'
*-standin.jsonl) and judges, with the Python twin's tables (tools/s11/s11_ref.py; never the plugins' own numbers):

 S11.A1 danger every handheld hit on a leviathan left it at or above the pinned floor (half its maxHealth) and the
 rifle was seen to stop (a floored row)
 S11.A2 danger a leviathan killed by a large-grid weapon or an explosion
 S11.A4 danger every hand-held rocket left a leviathan at or above the
 floor, and one was floored
 S11.A3 stack SE grouped the tick's synthetic explosions into ONE row: count n, yield n x Y, radius r x cbrt(n), the
 class asked for (a hand-held one stays Handheld);
 (S11b) Subnautica saw that summed yield (an s11.chain row within one yield unit)
 S11.B1 path the scripted probe (pass 4): creatures put facing the placed wall within their own scan; each one's
 AvoidObstacles.Evaluate saw it and the S11 hook counted proxy hits; then, lured behind the wall, no
 sampled creature centre inside a section
 S11.C1 grab* the size class at its boundary (pass 4): every dry-run start's verdict (would hold / TooLarge) equals
 the twin's size_fits on the box SE measured (its reply), the probes give each creature (reaper, sea
 dragon) a fit and a refusal, and every applied start's box fits the creature's row
 S11.C4 grab* (pass 6) debris is never a target: every dry-run start's Debris verdict equals the twin's is_debris on
 SE's block count and box (one debris probe and one complete grid at least), and no applied start
 holds a grid under 8 blocks or 8 m3
 S11.C5 grab* (S11b 20261024-000956) the kinematic hold: every hold's largest anchor-to-mouth error over the whole
 hold (SE's release row, every tick) <= 2 m, the forced one through a 40 m/s mouth segment
 S11.C6 grab* the held grid's centre of bounds (SE's WorldAABB centre) to the jaw centre
 (the attach point now) after the blend: <= 1.5 m for the whole hold (the release row's maxJawM) when the
 mouth stayed under 45 m/s; a faster (thrashing) hold is judged on its rows' median (max reported)
 S11.C7 surface no creature above the surface while it holds or follows a grid; the rise part let go
 at the surface (a release "surface"); both states sampled
 S11.C8 grab* every released grid 0.5 s on (SE's s11.grab "after"): speed <= 15 m/s (10 m/s at the release),
 and after a surface release its top still under the surface
 S11.R1 volley small-grid rockets fired by SE at a pinned x3 reaper: >= 90 % hit (Subnautica's sweep found it,
 SE exploded the missile there, the blast reached the creature: s11.danger "missile=<id>")
 S11.R2 volley the same for large-grid rockets (rail slugs: S11.RAIL INFO)
 S11.R3 volley a gatling burst through S7p's own Shot, from 40 m outside, from inside the body and from a
 grid the creature holds, pointed at it: >= 90 % hit of each
 S11.R4 holdhit a hold whose blast damage (s11.danger "missile=", the twin's share of the
 row's maxHealth) reached the release share let go exactly once ("rage release") and the creature fled: its
 distance from the grid grew >= 20 m within 10 s (s11.flee)
 S11.R5 holdhit a hold hit below the share got angrier exactly once (s11.rage rage: x1.5 hold damage, +4 s) and never
 let go by damage
 S11.C2 grab* the hold: SE's s11.hold rows after 1 s of holding have a median error to the attach point <= 2 m and
 a median orientation error <= 30 deg
 S11.C3 grab* the release: every start has a release (record, timeout or gone), held <= planned + 1.5 s
 S11.D1 hazard* every hazard type of the table reached SE's health (S11b: per Subnautica event; S11se: per inject)
 S11.D1.once each Subnautica hazard event is one record id, each id applied once on SE (exactly once)
 S11.D2 emp the EMP record reached SE's consumer and switched a producer off (the room's battery; pass 4: its own
 part, after a grid exists)
 S11.D3 hazardse the per-second cap: within any 1,000 ms window, the Heat granted <= 50 HP
 S11.E1 air* oxygen records reached the suit: litres = seconds / 45 x capacity (capped)
 S11.E2 air* the first-aid heal reached SE's health: applied = min(amount, max - before)
 S11.Mx.red a seeded failure turns exactly its rows red
 S11.CHAR INFO (S11b) SE's character after each part (se_character events); a dead one pauses SE at its respawn
 screen, so every later row that did not pass is MISSING, never FAIL (pass 4, run 3)
 S11.C.NATURAL INFO: a reaper's own grab (Subnautica's s11.grab rows), beside the forced one C2/C3 judge
 S11.FX INFO (pass 5) Subnautica's damage FX for the diver (s11.dmgfx: played, splatters moved under S16's canvas)
 and SE's blood overlay (s11.hud: who painted, its alpha after the last drop)
 S11.BITE INFO (pass 5) bites and hold seconds on SE grids: SN amount x k x the creature's bite multiplier (DE.Damage
 lines are text: the s11.scale rows give each creature's live MeleeAttack.biteDamage)
"""
from __future__ import annotations

import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tools", "s11")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C   # noqa: E402 (row, table, verdict, jsonl reader)
import s11_ref as T     # noqa: E402 (the tables and rules: the twin)

row = C.row
FLEE_M = 20.0
MUTANT_ROWS = {"S11.M13": ("S11.C6",), "S11.M14": ("S11.C7",), "S11.M15": ("S11.R1", "S11.R2", "S11.R3"), "S11.M16": ("S11.R4", "S11.R5"),
               "S11.M12": ("S11.C5",), "S11.M11": ("S11.C4",), "S11.M1": ("S11.A1", "S11.A4"), "S11.M2": ("S11.A3",), "S11.M3": ("S11.B1",), "S11.M4": ("S11.C1",),
               "S11.M5": ("S11.C3",), "S11.M6": ("S11.D1",), "S11.M7": ("S11.D1.once",), "S11.M8": ("S11.E1",),
               "S11.M9": ("S11.D2",), "S11.M10": ("S11.E2",)}
# B1 tolerance: a creature rigidbody driven into a kinematic box penetrates for a physics step before
# PhysX pushes it out; a leak is a creature that stays inside or goes deep. Sampled at 10 Hz (PathStep 0.1 s).
INSIDE_MAX_RUN = 2          # consecutive inside samples of one creature (200 ms)
INSIDE_MAX_DEPTH_M = 0.25   # past the nearest face (a tenth of a large block)
INSIDE_MAX_FRACTION = 0.0005
HOLD_ERROR_M = 2.0
HOLD_ERROR_DEG = 30.0
RELEASE_SLACK_S = 1.5
_ISO = re.compile(r"^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(\.\d+)?(Z|[+-]\d\d:?\d\d)?$")

def iso_epoch(s):
    import calendar
    m = _ISO.match(s or "")
    if not m:
        return None
    y, mo, d, h, mi, se = (int(m.group(i)) for i in range(1, 7))
    t = calendar.timegm((y, mo, d, h, mi, se, 0, 0, 0)) + (float(m.group(7)) if m.group(7) else 0.0)
    tz = m.group(8)
    if tz and tz != "Z":
        sign = 1 if tz[0] == "+" else -1
        hh, mm = int(tz[1:3]), int(tz[-2:])
        t -= sign * (hh * 3600 + mm * 60)
    return t

def parse_rows(recs):
    out = []
    for rec in recs:
        name = rec.get("ev") or rec.get("event")
        if not isinstance(name, str) or not name.startswith("s11."):
            continue
        data = dict(rec["data"]) if isinstance(rec.get("data"), dict) else {k: v for k, v in rec.items() if k not in ("ev", "event", "data")}
        t = rec.get("t", rec.get("utc"))
        st = iso_epoch(t) if isinstance(t, str) else (float(t) if isinstance(t, (int, float)) else None)
        data["_t"] = st
        out.append((name, data))
    return out

class Session:
    def __init__(self, folder):
        import glob
        self.dir = folder
        self.session = C.read_jsonl(os.path.join(folder, "s11-session.jsonl"))
        self.se = parse_rows(C.read_jsonl(sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))))
        self.sn = parse_rows(C.read_jsonl(sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))))
        self.start = next((e for e in self.session if e.get("ev") == "start"), {}) or {}
        self.mode = self.start.get("mode")
        self.parts = list(self.start.get("parts") or [])
        self.mutants = list(self.start.get("mutants") or [])

    def rows(self, side, name):
        return [d for n, d in (self.se if side == "se" else self.sn) if n == name]

    def replies(self, prefix):
        return [e for e in self.session if e.get("ev") == "reply" and str(e.get("label") or "").startswith(prefix)]

    def ran(self, *parts):
        return any(p in self.parts for p in parts)

def _no_character(S, part):
    """Run 1: the scene's own row says the SE character was not there (dead, absent, seated) at its start or end."""
    ends = [d for d in S.rows("sn", "s11.scene") if d.get("part") == part and "characterPresent" in d]
    return bool(ends) and not all(d.get("characterPresent") for d in ends)

def _f(v, d=float("nan")):
    try:
        return float(v)
    except (TypeError, ValueError):
        return d

# ======================================================================================================================
# A
# ======================================================================================================================

def check_danger(S, rows):
    lev = [d for d in S.rows("sn", "s11.danger") if d.get("tier") == "Leviathan"]
    hand = [d for d in lev if d.get("cls") == "Handheld"]
    if not hand:
        row(rows, "S11.A1", "MISSING", "no handheld hit on a leviathan", "floor x maxHealth", "a handheld weapon never kills a leviathan", "danger")
    else:
        floor = T.weapon_row(1, 3)[3] / 1000.0
        # in-game 122326: a floored row (applied 0) on a leviathan an Explosion-class blast had already taken under the
 # handheld floor is no breach: only a hit that applied damage can break it
        low = [d for d in hand if _f(d.get("applied"), 0.0) > 1e-6 and _f(d.get("healthAfter")) < floor * _f(d.get("maxHealth")) - 1e-3]
        killed = [d for d in hand if d.get("killed")]
        floored = [d for d in hand if d.get("floored")]
        ok = not low and not killed and floored
        row(rows, "S11.A1", "PASS" if ok else "FAIL", "%d hits, %d below the floor, %d floored, %d kills" % (len(hand), len(low), len(floored), len(killed)),
            ">= %.2f x maxHealth, floored seen" % floor, "a handheld weapon never kills a leviathan", "danger")
    rockets = [d for d in hand if str(d.get("why")) in ("blast", "scene rocket")]
    if not rockets:
        row(rows, "S11.A4", "MISSING", "no hand-held rocket on a leviathan", "floor x maxHealth", "a hand-held rocket never kills a leviathan", "danger")
    else:
        floor = T.weapon_row(1, 3)[3] / 1000.0
        low = [d for d in rockets if (_f(d.get("applied"), 0.0) > 1e-6 and _f(d.get("healthAfter")) < floor * _f(d.get("maxHealth")) - 1e-3) or d.get("killed")]
        floored = [d for d in rockets if d.get("floored")]
        row(rows, "S11.A4", "PASS" if not low and floored else "FAIL", "%d rockets, %d below the floor, %d floored" % (len(rockets), len(low), len(floored)),
            ">= %.2f x maxHealth, floored seen" % floor, "a hand-held rocket never kills a leviathan (the class rides the row)", "danger")
    big = [d for d in lev if d.get("cls") in ("LargeGrid", "Explosion") and d.get("killed")]
    any_big = [d for d in lev if d.get("cls") in ("LargeGrid", "Explosion")]
    if not big and any(d.get("killed") for d in hand):
        row(rows, "S11.A2", "INFO", "the rifle killed it first", ">= 1", "a large turret or a warhead kills a leviathan (no target left)", "danger", required=False)
        return
    st = "PASS" if big else ("FAIL" if any_big else "MISSING")
    row(rows, "S11.A2", st, "%d kills by %s" % (len(big), ",".join(sorted({d.get("cls") for d in big})) or "-"), ">= 1",
        "a large turret or a warhead kills a leviathan", "danger")

def _stack_args(S):
    out = []
    for e in S.replies("stack."):
        args = e.get("args") or {}
        if "stack" in args and isinstance(e.get("reply"), dict) and e["reply"].get("ok"):
            cl = T.CLASSES[1] if str(args.get("cls", "")).lower().startswith("hand") else ("SmallGrid" if str(args.get("cls", "")).lower().startswith("small") else "Explosion")
            out.append((int(args["stack"]), _f(args.get("yield", 15000)), _f(args.get("radius", 22.4415)), cl))
    return out

def check_stack(S, rows):
    blasts = S.rows("se", "s11.blast")
    wants = _stack_args(S)
    if not blasts or not wants:
        row(rows, "S11.A3", "MISSING", "%d s11.blast rows, %d stack commands" % (len(blasts), len(wants)), "n -> 1 row", "stacked warheads act as n yields", "stack")
        return
    problems = []
    seen = 0
    used = set()
    for n, y, r, cl in wants:
        cand = [b for b in blasts if id(b) not in used and int(b.get("synthetic") or 0) == n and int(b.get("n") or 0) == n and
                all(str(x.get("cls", "Explosion")) == cl for x in (b.get("groups") or []))]
        if cand:
            used.add(id(cand[0]))
        if not cand:
            problems.append("no tick of %d" % n)
            continue
        g = cand[0].get("groups") or []
        tot_c = sum(int(x.get("count") or 0) for x in g)
        tot_y = sum(_f(x.get("yield")) for x in g)
        if tot_c != n or abs(tot_y - n * y) > 1e-6 * n * y:
            problems.append("tick of %d: count %d yield %g (nothing may be dropped)" % (n, tot_c, tot_y))
            continue
        if len(g) != 1:
            problems.append("tick of %d: %d rows, want 1" % (n, len(g)))
            continue
        if str(g[0].get("cls", "Explosion")) != cl:
            problems.append("tick of %d: class %s, want %s" % (n, g[0].get("cls"), cl))
            continue
        want_r = r * n ** (1.0 / 3.0)
        if abs(_f(g[0].get("radius")) - want_r) > 1e-6 * max(1.0, want_r):
            problems.append("tick of %d: radius %g, want %g" % (n, _f(g[0].get("radius")), want_r))
            continue
        seen += 1
        if S.mode == "S11b":
            unit = T.B["YieldUnitSe"]
            chain = [c for c in S.rows("sn", "s11.chain") if abs(_f(c.get("yield")) - n * y) <= unit / 2.0 + 1e-6]
            if not chain:
                problems.append("tick of %d: Subnautica never saw the summed yield %g" % (n, n * y))
    st = "PASS" if not problems and seen == len(wants) else "FAIL"
    row(rows, "S11.A3", st, "%d/%d ticks" % (seen, len(wants)) + ("; " + "; ".join(problems) if problems else ""), "1 row, n x yield, r x cbrt(n)",
        "stacked warheads act as n yields (SE groups, Subnautica's yield)", "stack")

# ======================================================================================================================
# B
# ======================================================================================================================

def check_path(S, rows):
    ends = [d for d in S.rows("sn", "s11.scene") if d.get("part") == "path" and d.get("phase") == "end"]
    what = "creatures see the hull with their own avoidance and never end inside it (scripted probe, then lured behind it)"
    want = "every probe saw it, hook hits > 0, inside only as contact transients (run <= %d samples, depth <= %.2f m, <= %.2f %% of samples)" % (
        INSIDE_MAX_RUN, INSIDE_MAX_DEPTH_M, 100 * INSIDE_MAX_FRACTION)
    if not ends:
        row(rows, "S11.B1", "MISSING", "no path scene end", want, what, "path")
        return
    e = ends[-1]
    samples, inside = int(e.get("samples") or 0), int(e.get("inside") or 0)
    probes = [d for d in S.rows("sn", "s11.probe") if not d.get("why")]
    faceless = [d for d in S.rows("sn", "s11.probe") if d.get("why")]
    saw = [d for d in probes if d.get("saw")]
    hook = sum(int(d.get("hookHits") or 0) for d in probes)
    terrain = sum(1 for d in probes if d.get("terrainOnly"))
    measured = "%d probe(s) (%d terrain-only), %d saw the wall, hook hits %d, %d samples, %d inside, %s within 3 m, %s steer(s)" % (
        len(probes), terrain, len(saw), hook, samples, inside, e.get("near"), e.get("steers"))
    if not probes:
        why = "no creature with AvoidObstacles near the wall" if not faceless else "%d probe(s) found no face (%s)" % (len(faceless), faceless[0].get("why"))
        row(rows, "S11.B1", "MISSING", "%s; %s" % (why, measured), want, what, "path")
        return
    # each creature's longest run of consecutive inside samples (every inside sample is logged; 10 Hz: <= 0.15 s apart)
    ins = sorted((d for d in S.rows("sn", "s11.path") if d.get("inside")), key=lambda d: (str(d.get("instance")), d.get("_t") or 0))
    max_run, run, prev = 0, 0, None
    for d in ins:
        logged = int(d.get("run") or 0)
        if logged:
            run = logged
        elif prev is not None and prev.get("instance") == d.get("instance") and (d.get("_t") or 0) - (prev.get("_t") or 0) <= 0.15:
            run += 1
        else:
            run = 1
        max_run = max(max_run, run)
        prev = d
    max_run = max(max_run, int(e.get("insideMaxRun") or 0))
    depths = [_f(d.get("depth")) for d in ins if not math.isnan(_f(d.get("depth")))]
    max_depth = max(depths + [_f(e.get("insideMaxDepth"), 0.0)]) if (depths or e.get("insideMaxDepth") is not None) else float("nan")
    frac = inside / float(samples) if samples else 0.0
    if inside:
        measured += "; inside: %d creature(s), longest run %d sample(s), deepest %s, %.4f %% of samples" % (
            len({d.get("instance") for d in ins}), max_run, "%.2f m" % max_depth if not math.isnan(max_depth) else "not logged", 100 * frac)
    leak = inside > 0 and (max_run > INSIDE_MAX_RUN or (not math.isnan(max_depth) and max_depth > INSIDE_MAX_DEPTH_M) or frac > INSIDE_MAX_FRACTION)
    ok = len(saw) == len(probes) and hook > 0 and not leak
    row(rows, "S11.B1", "PASS" if ok else "FAIL", measured, want, what, "path")

# ======================================================================================================================
# C
# ======================================================================================================================

def _probe_verdict(res):
    """SE's verdict of a dry-run start: True (would hold, or past the size test to the static one), False (TooLarge), None"""
    o, r = res.get("outcome"), res.get("reason")
    if o == "would apply" or (o == "rejected" and r == "Static"):
        return True
    if o == "rejected" and r == "TooLarge":
        return False
    return None

def check_size(S, rows, part, bad_starts):
    """pass 4: each probe's verdict against the twin on SE's measured box; each creature's class seen from both sides"""
    probes = [e for e in S.session if e.get("ev") == "size_probe" and e.get("creature")]
    sides, problems, unjudged, seen = {}, [], [], []
    for e in probes:
        res = ((e.get("reply") or {}).get("result") or {})
        if res.get("debris") or res.get("reason") == "Debris":
            continue   # pass 6: a remnant is S11.C4's, not the size class's
        size = res.get("size")
        g = T.grab_row(res.get("creature") or e.get("creature") or "")
        if not (isinstance(size, list) and len(size) == 3 and g):
            unjudged.append("%s/%s %s" % (e.get("label"), e.get("creature"), res.get("reason") or res.get("outcome") or "no reply"))
            continue
        sx, sy, sz = (_f(v) for v in size)
        twin = T.size_fits(g, sx, sy, sz)
        se = _probe_verdict(res)
        name = g["creature"] if isinstance(g, dict) else e.get("creature")
        seen.append("%s %s %.1fx%.1fx%.1f %s" % (e.get("label"), "reaper" if "Reaper" in str(name) else "dragon", sx, sy, sz, "fits" if twin else "over"))
        if se is None or se != twin or bool(res.get("fits")) != twin:
            problems.append("%s/%s: SE %s (fits %s), twin %s on %.1fx%.1fx%.1f" % (e.get("label"), e.get("creature"), res.get("reason") or res.get("outcome"),
                                                                                  res.get("fits"), twin, sx, sy, sz))
        sides.setdefault(e.get("creature"), set()).add(twin)
    if not seen and not bad_starts:
        row(rows, "S11.C1", "MISSING", "no judged probe (%s)" % ("; ".join(unjudged) or "none ran"), "each verdict = twin, both sides of each class",
            "only grids of the creature's size class are held", part)
        return
    straddle = [c for c in ("ReaperLeviathan", "SeaDragon") if sides.get(c) != {True, False}]
    measured = "%d probe(s) judged, %d disagree, %d start(s) over the class; %s" % (len(seen), len(problems), len(bad_starts), "; ".join(seen))
    if problems:
        measured = "; ".join(problems[:4]) + " | " + measured
    if problems or bad_starts:
        st = "FAIL"
    elif straddle:
        st = "MISSING"
        measured = "the probes did not straddle the class of %s | %s" % (",".join(straddle), measured)
    else:
        st = "PASS"
    row(rows, "S11.C1", st, measured, "each verdict = twin(SE's box); a fit and a refusal per creature",
        "only grids of the creature's size class are held (the class boundary)", part)

def check_debris(S, rows, part):
    """pass 6: a remnant is never held (format debris.grid)"""
    probes = [e for e in S.session if e.get("ev") == "size_probe" and e.get("creature")]
    judged, problems, sides = [], [], set()
    for e in probes:
        res = ((e.get("reply") or {}).get("result") or {})
        if "blocks" not in res or "volumeM3" not in res:
            continue
        twin = T.is_debris(_f(res.get("blocks")), _f(res.get("volumeM3")))
        se = res.get("reason") == "Debris"
        judged.append(e)
        sides.add(twin)
        if se != twin:
            problems.append("%s/%s: %d blocks %.1f m3: SE %s, twin %s" % (e.get("label"), e.get("creature"), int(_f(res.get("blocks"), 0)), _f(res.get("volumeM3")),
                                                                         "Debris" if se else (res.get("reason") or res.get("outcome")), "debris" if twin else "complete"))
    starts = [d for d in S.rows("se", "s11.grab") if d.get("phase") == "start" and "blocks" in d]
    held = [d for d in starts if T.is_debris(_f(d.get("blocks")), _f(d.get("sx"), 0) * _f(d.get("sy"), 0) * _f(d.get("sz"), 0))]
    if not judged and not starts:
        row(rows, "S11.C4", "MISSING", "no probe with a block count", "debris refused, complete grids not", "the character and complete grids only (no remnants)", part)
        return
    measured = "%d probe(s) judged (%d debris), %d disagree, %d start(s) on debris" % (len(judged), sum(1 for e in judged if T.is_debris(
        _f(((e.get("reply") or {}).get("result") or {}).get("blocks")), _f(((e.get("reply") or {}).get("result") or {}).get("volumeM3")))), len(problems), len(held))
    if problems:
        measured = "; ".join(problems[:3]) + " | " + measured
    if problems or held:
        st = "FAIL"
    elif sides != {True, False}:
        st = "MISSING"
        measured = "the probes were all %s | %s" % ("debris" if True in sides else "complete grids", measured)
    else:
        st = "PASS"
    row(rows, "S11.C4", st, measured, "Debris = twin(blocks, box); no start on debris", "the character and complete grids only (no remnants)", part)

HOLD_MAX_ERROR_M = 2.0
FAST_MOUTH_MS = 30.0
JAW_MAX_M = 1.5
JAW_P95_M = 3.0
JAW_THRASH_MS = 45.0
HIT_RATE = 0.9

def check_jaw(S, rows, part):

    rel = [d for d in S.rows("se", "s11.grab") if d.get("phase") == "release" and "maxJawM" in d]
    keys = {str(d.get("key")) for d in rel}
    for k in sorted({str(h.get("key")) for h in S.rows("se", "s11.hold") if "jawM" in h} - keys):
        # a hold never released (S11.M5): its hold rows after the blend stand for the release row
        hs = [h for h in S.rows("se", "s11.hold") if str(h.get("key")) == k and "jawM" in h]
        after = [_f(h.get("jawM")) for h in hs if _f(h.get("blend"), 0.0) >= 1.0 and not math.isnan(_f(h.get("jawM")))]
        rel.append({"key": k, "heldS": max(_f(h.get("heldS"), 0.0) for h in hs), "blendMs": 0.0 if after else 1e9, "maxJawM": max(after) if after else float("nan"),
                    "maxTargetSpeed": max(_f(h.get("targetSpeed"), 0.0) for h in hs), "anchor": "unreleased"})
    what = "the held grid sits in the jaw: its centre of bounds on the jaw centre after the blend (Subnautica's Seamoth rule)"
    if not rel:
        row(rows, "S11.C6", "MISSING", "no release row with maxJawM", "<= %.1f m" % JAW_MAX_M, what, part)
        return
    per, bad, judged = [], [], 0
    # in-game 144924 (the coordinator): a creature's hold is judged in Subnautica's view, the drawn hull's centre to the jaw
 # (s11.jaw drawnGapM after the blend): median <= 1.5 m and p95 <= 3 m; SE's own number is reported beside it
    jaw = [d for d in S.rows("sn", "s11.jaw") if not d.get("released") and not math.isnan(_f(d.get("drawnGapM")))
           and _f(d.get("ageS"), 0.0) >= _f(d.get("blendS"), 1.0) + 0.3]
    for key in sorted({str(d.get("key")) for d in jaw}):
        g = sorted(_f(d.get("drawnGapM")) for d in jaw if str(d.get("key")) == key)
        med, p95 = g[len(g) // 2], g[min(len(g) - 1, int(len(g) * 0.95))]
        judged += 1
        per.append("creature %s (Subnautica's view) median %.2f m, p95 %.2f m, max %.2f m over %d rows" % (key, med, p95, g[-1], len(g)))
        if not (med <= JAW_MAX_M and p95 <= JAW_P95_M):
            bad.append("creature %s" % key)
    sn_keys = {str(d.get("key")) for d in jaw}
    for d in rel:
        k = "forced" if str(d.get("key")) == "77" else "creature %s" % d.get("key")
        if k != "forced" and str(d.get("key")) in sn_keys:
            per.append("%s SE max %.2f m at up to %.0f m/s" % (k, _f(d.get("maxJawM")), _f(d.get("maxTargetSpeed"), 0.0)))
            continue
        held, blend = _f(d.get("heldS"), 0.0), _f(d.get("blendMs"), 0.0) / 1000.0
        if held < blend + 0.3:
            per.append("%s held %.1f s (inside its blend)" % (k, held))
            continue
        judged += 1
        mx, sp = _f(d.get("maxJawM")), _f(d.get("maxTargetSpeed"), 0.0)
        if sp <= JAW_THRASH_MS:
            ok = mx <= JAW_MAX_M
            per.append("%s max %.2f m (%s anchor, mouth <= %.0f m/s)" % (k, mx, d.get("anchor"), sp))
        else:
            js = sorted(_f(h.get("jawM")) for h in S.rows("se", "s11.hold") if str(h.get("key")) == str(d.get("key")) and _f(h.get("blend"), 0.0) >= 1.0
                        and not math.isnan(_f(h.get("jawM"))))
            med = js[len(js) // 2] if js else mx
            ok = med <= JAW_MAX_M
            per.append("%s thrash %.0f m/s: median %.2f m, max %.2f m (%s anchor)" % (k, sp, med, mx, d.get("anchor")))
        if not ok:
            bad.append(k)
    st = "FAIL" if bad else ("PASS" if judged else "MISSING")
    row(rows, "S11.C6", st, "; ".join(per), "forced <= %.1f m after the blend; a creature's hold in Subnautica's view median <= %.1f, p95 <= %.1f m" % (JAW_MAX_M, JAW_MAX_M, JAW_P95_M), what, part)

def check_release(S, rows, part):
    """ a release never flings the grid (the mouth's 150 m/s), a surface release keeps it under"""
    after = [d for d in S.rows("se", "s11.grab") if d.get("phase") == "after"]
    what = "a released grid keeps at most 10 m/s and, at the surface, does not leave the water"
    if not after:
        released = any(d.get("phase") == "release" for d in S.rows("se", "s11.grab"))
        row(rows, "S11.C8", "MISSING" if released else "INFO", "no after row (0.5 s after a release)" + ("" if released else "; no release to judge"),
            "<= 15 m/s; a surface release under water", what, part, required=released)
        return
    bad = []
    for d in after:
        sp, td = _f(d.get("speed")), _f(d.get("topDepthM"))
        if not (sp <= 15.0):
            bad.append("key %s %.1f m/s (%.1f at the release)" % (d.get("key"), sp, _f(d.get("speedAtRelease"))))
        if d.get("surface") and not (td >= -0.5):
            bad.append("key %s top %.1f m above the water after a surface release" % (d.get("key"), -td))
    sps = sorted(_f(d.get("speed")) for d in after)
    row(rows, "S11.C8", "FAIL" if bad else "PASS", "%d release(s), speed 0.5 s on max %.1f m/s (%d at the surface)%s" % (
        len(after), sps[-1], sum(1 for d in after if d.get("surface")), ("; " + "; ".join(bad)) if bad else ""), "<= 15 m/s; a surface release under water", what, part)

def check_surface(S, rows):
    part = "surface"
    sec = [d for d in S.rows("sn", "s11.surface") if d.get("op") == "second"]
    above = [d for d in S.rows("sn", "s11.surface") if d.get("op") == "above"]
    hold_n = sum(int(_f(d.get("holding"), 0)) for d in sec)
    fol_n = sum(int(_f(d.get("following"), 0)) for d in sec)
    rel = [d for d in S.rows("sn", "s11.grab") if d.get("phase") == "release" and str(d.get("why") or "").startswith("surface")]
    rel += [d for d in S.rows("se", "s11.grab") if d.get("phase") == "release" and str(d.get("why") or "").startswith("surface")]
    maxh = max([_f(d.get("maxYHolding")) for d in sec if not math.isnan(_f(d.get("maxYHolding"))) and _f(d.get("maxYHolding")) > -1e9] or [float("nan")])
    maxf = max([_f(d.get("maxYFollowing")) for d in sec if not math.isnan(_f(d.get("maxYFollowing"))) and _f(d.get("maxYFollowing")) > -1e9] or [float("nan")])
    measured = "%d holding / %d following samples, highest %.1f / %.1f m; %d above; %d surface release(s)" % (hold_n, fol_n, maxh, maxf, len(above), len(rel))
    what = "creatures never leave the water with a grid (release at the surface, follow targets kept under)"
    if above:
        st = "FAIL"
        measured += " (e.g. %s at y %.1f, %s)" % (above[0].get("creature"), _f(above[0].get("y")), "holding" if above[0].get("holding") else "following")
    elif hold_n < 5 or fol_n < 5 or not rel:
        st = "MISSING"
    else:
        st = "PASS"
    row(rows, "S11.C7", st, measured, "0 above; both states sampled; a surface release", what, part)

def _rate(S, kind):
    vol = [d for d in S.rows("se", "s11.volley") if str(d.get("kind")) == kind]
    hits_sn = {str(d.get("missile")) for d in S.rows("sn", "s11.missile") if d.get("phase") == "hit"}
    det = {str(d.get("missileStr") or d.get("missile")) for d in S.rows("se", "s11.missile") if d.get("phase") == "detonate"}
    blast = set()
    for d in S.rows("sn", "s11.danger"):
        w = str(d.get("why") or "")
        if "missile=" in w:
            blast.add(w.split("missile=")[1].split()[0])
    n, good = 0, 0
    for d in vol:
        mid = str(d.get("missileStr") or d.get("missile"))
        n += 1
        if mid in hits_sn and mid in det and mid in blast:
            good += 1
    return n, good, len(hits_sn), len(det)

def check_volley(S, rows):
    part = "volley"
    for rid, kind, what in (("S11.R1", "small", "small-grid rockets hit a x3 reaper across its body"), ("S11.R2", "large", "large-grid rockets hit a x3 reaper")):
        n, good, sn, det = _rate(S, kind)
        if n == 0:
            row(rows, rid, "MISSING", "no %s missile fired (s11.volley)" % kind, ">= %d %%" % int(HIT_RATE * 100), what, part)
            continue
        st = "PASS" if good >= HIT_RATE * n else "FAIL"
        row(rows, rid, st, "%d/%d hit (Subnautica hits %d, SE detonations %d)" % (good, n, sn, det), ">= %d %%" % int(HIT_RATE * 100), what, part)
    n, good, sn, det = _rate(S, "rail")
    if n:
        row(rows, "S11.RAIL", "INFO", "%d/%d rail slugs hit" % (good, n), "-", "railgun slugs (health pool: a synthetic yield of 10 %% of it)", part, required=False)
    b = S.rows("sn", "s11.burst")
    cats = {k: [d for d in b if d.get("from") == k] for k in ("outside", "inside", "holder")}
    what = "gatling rounds hit the x3 reaper, from outside, from inside its body and from a grid it holds (aimed at it)"
    if not cats["inside"] or not cats["outside"]:
        row(rows, "S11.R3", "MISSING", "%d inside / %d outside shots" % (len(cats["inside"]), len(cats["outside"])), ">= %d %% of each" % int(HIT_RATE * 100), what, part)
    else:
        hits = {k: sum(1 for d in v if d.get("hit")) for k, v in cats.items()}
        st = "PASS" if all(hits[k] >= HIT_RATE * len(v) for k, v in cats.items() if v) else "FAIL"
        row(rows, "S11.R3", st, ", ".join("%s %d/%d" % (k, hits[k], len(v)) for k, v in cats.items() if v) + ("" if cats["holder"] else " (no holder shot)"),
            ">= %d %% of each" % int(HIT_RATE * 100), what, part)

def _holds(S):
    """s11.rage rows grouped per hold: the same key and a start (t - holdAgeS) within 0.5 s"""
    holds = []
    for d in sorted(S.rows("sn", "s11.rage"), key=lambda x: _f(x.get("_t"), 0.0)):
        t0 = _f(d.get("_t"), 0.0) - _f(d.get("holdAgeS"), 0.0)
        h = next((h for h in holds if h["key"] == str(d.get("key")) and abs(h["t0"] - t0) < 0.5), None)
        if h is None:
            h = {"key": str(d.get("key")), "t0": t0, "rows": []}
            holds.append(h)
        h["rows"].append(d)
    return holds

def _hold_windows(S):
    """ the forced holds of the holdhit scene (SN s11.grab start.. release), with the blast SN_HP the holder took
 in each (s11.danger rows "missile=" on that instance; the share judged with the twin's rage row and the row's maxHealth)"""
    # in-game 144924: the holdhit scene's own holds only (its s11.holdhit "holdN".. "holdEndN" rows), never the burst's or the rise's
    hh = S.rows("sn", "s11.holdhit")
    st = [d for d in hh if str(d.get("step", "")).startswith("hold") and not str(d.get("step", "")).startswith("holdEnd") and d.get("ok")]
    rel = [d for d in S.rows("sn", "s11.grab") if d.get("phase") == "release"]
    out = []
    for s in st:
        t0 = _f(s.get("_t"), 0.0)
        end = next((d for d in hh if str(d.get("step")) == "holdEnd" + str(s.get("step"))[4:]), None)
        r = next((d for d in sorted(rel, key=lambda x: _f(x.get("_t"), 0.0)) if _f(d.get("_t"), 0.0) >= t0), None)
        t1 = _f(end.get("_t"), 0.0) if end else (_f(r.get("_t"), 0.0) if r else t0 + 60.0)
        dn = [d for d in S.rows("sn", "s11.danger") if t0 <= _f(d.get("_t"), 0.0) <= t1 + 0.5 and "missile=" in str(d.get("why") or "")]
        out.append({"t0": t0, "t1": t1, "release": r, "danger": dn, "applied": sum(_f(d.get("applied"), 0.0) for d in dn),
                    "maxHealth": max([_f(d.get("maxHealth"), 0.0) for d in dn] or [0.0])})
    return out

def check_rage(S, rows):
    part = "holdhit"
    wins = [w for w in _hold_windows(S) if w["danger"]]
    if wins:
        row_r = T.rage_row("ReaperLeviathan")
        rage_rows = S.rows("sn", "s11.rage")
        flee = S.rows("sn", "s11.flee")
        over = [w for w in wins if w["applied"] >= row_r[2] / 1000.0 * w["maxHealth"] - 1e-6]
        under = [w for w in wins if w not in over]
        what4 = "shot above the release share while holding (SE's rockets): it lets go once and flees"
        if not over:
            row(rows, "S11.R4", "MISSING", "no hold took the release share (%d hold(s) hit)" % len(wins), "one release per hold, a flee", what4, part)
        else:
            bad = []
            for w in over:
                why = str((w["release"] or {}).get("why") or "")
                rr = [d for d in rage_rows if w["t0"] <= _f(d.get("_t"), 0.0) <= w["t1"] + 0.5 and d.get("outcome") == "release"]
                fl = [d for d in flee if w["t1"] <= _f(d.get("_t"), 0.0) <= w["t1"] + 11.0]
                grew = (max(_f(d.get("distM"), 0.0) for d in fl) - _f(fl[0].get("distM"), 0.0)) if fl else 0.0
                if not why.startswith("rage release") or len(rr) != 1:
                    bad.append("%.0f SN_HP, release '%s', %d rage release row(s)" % (w["applied"], why[:40], len(rr)))
                elif grew < FLEE_M:
                    bad.append("fled %.1f m in 10 s (< %.0f)" % (grew, FLEE_M))
            row(rows, "S11.R4", "FAIL" if bad else "PASS", "%d hold(s) over the share; %s" % (len(over), "; ".join(bad) or "each let go once and fled"),
                "one release per hold, fled >= %.0f m" % FLEE_M, what4, part)
        what5 = "shot below the release share while holding (SE's rocket): it gets angrier once (x1.5 hold damage, a longer hold)"
        if not under:
            row(rows, "S11.R5", "MISSING", "no hold hit below the share", "one rage per hold", what5, part)
        else:
            bad = []
            for w in under:
                rr = [d for d in rage_rows if w["t0"] <= _f(d.get("_t"), 0.0) <= w["t1"] + 0.5]
                rg = [d for d in rr if d.get("outcome") == "rage"]
                if len(rg) != 1 or any(d.get("outcome") == "release" for d in rr) or abs(_f(rg[0].get("angerMult")) - 1.5) > 1e-6 or not (_f(rg[0].get("extendS"), 0) > 0):
                    bad.append("%.0f SN_HP, outcomes %s" % (w["applied"], ",".join(str(d.get("outcome")) for d in rr) or "none"))
            row(rows, "S11.R5", "FAIL" if bad else "PASS", "%d hold(s) under the share; %s" % (len(under), "; ".join(bad) or "each angered once (x1.5, longer hold)"),
                "exactly one rage per hold, no release", what5, part)
        return
    holds = _holds(S)
    # the share is judged on the hits' own applied SN_HP (the book's "taken" is the plugin's number; under S11.M16 it stays 0)
    over = [h for h in holds if sum(_f(r.get("applied"), 0.0) for r in h["rows"]) >= min(_f(r.get("threshold"), float("inf")) for r in h["rows"]) - 1e-6]
    under = [h for h in holds if h not in over]
    rel_sn = [d for d in S.rows("sn", "s11.grab") if d.get("phase") == "release" and str(d.get("why") or "").startswith("rage release")]
    what4 = "shot above the release share while holding: it lets go (once) and flees"
    if not over:
        row(rows, "S11.R4", "MISSING", "no hold reached the release share (%d hold(s) hit)" % len(holds), "one release per hold", what4, part)
    else:
        bad = []
        for h in over:
            outs = [r.get("outcome") for r in h["rows"]]
            if outs.count("release") != 1 or (outs.index("release") < len(outs) - 1 if "release" in outs else False):
                bad.append("key %s outcomes %s" % (h["key"], ",".join(outs)))
        if len(rel_sn) < len(over):
            bad.append("%d grab release(s) 'rage release' for %d hold(s)" % (len(rel_sn), len(over)))
        row(rows, "S11.R4", "FAIL" if bad else "PASS", "%d hold(s) over the share; %s" % (len(over), "; ".join(bad) or "each let go once"),
            "exactly one release per hold, a grab release", what4, part)
    what5 = "shot below the release share while holding: it gets angrier (once): x1.5 hold damage, a longer hold"
    if not under:
        row(rows, "S11.R5", "MISSING", "no hold hit below the share", "one rage per hold", what5, part)
    else:
        bad = []
        for h in under:
            outs = [r.get("outcome") for r in h["rows"]]
            rg = [r for r in h["rows"] if r.get("outcome") == "rage"]
            if outs.count("rage") != 1 or "release" in outs or abs(_f(rg[0].get("angerMult") if rg else 0) - 1.5) > 1e-6 or not (_f(rg[0].get("extendS") if rg else 0) > 0):
                bad.append("key %s outcomes %s" % (h["key"], ",".join(outs)))
        row(rows, "S11.R5", "FAIL" if bad else "PASS", "%d hold(s) under the share; %s" % (len(under), "; ".join(bad) or "each angered once (x1.5, longer hold)"),
            "exactly one rage per hold, no release", what5, part)

def check_hold_max(S, rows, part):
    """S11b 20261024-000956: the whole hold within 2 m of the mouth at any mouth speed (the release row's maxErrorM)"""
    rel = [d for d in S.rows("se", "s11.grab") if d.get("phase") == "release" and "maxErrorM" in d]
    keys = {str(d.get("key")) for d in rel}
    last = {}
    for d in S.rows("se", "s11.hold"):   # a hold never released (S11.M5): its last hold row carries the running maximum
        if "maxErrorM" in d and str(d.get("key")) not in keys:
            last[str(d.get("key"))] = dict(d, maxTargetSpeed=max(_f(d.get("targetSpeed"), 0.0), _f(last.get(str(d.get("key")), {}).get("maxTargetSpeed"), 0.0)))
    rel += list(last.values())
    what = "the held grid stays at the mouth for the whole hold, at any mouth speed (kinematic hold)"
    if not rel:
        row(rows, "S11.C5", "MISSING", "no release row with maxErrorM", "max <= %.1f m" % HOLD_MAX_ERROR_M, what, part)
        return
    per, bad = [], []
    for d in rel:
        k = "forced" if str(d.get("key")) == "77" else "creature"
        mx, sp = _f(d.get("maxErrorM")), _f(d.get("maxTargetSpeed"), 0.0)
        per.append("%s max %.2f m at up to %.0f m/s (%s, %s snap(s))" % (k, mx, sp, d.get("mode"), d.get("snaps")))
        if not (mx <= HOLD_MAX_ERROR_M):
            bad.append(k)
    fast = max(_f(d.get("maxTargetSpeed"), 0.0) for d in rel)
    if bad:
        st = "FAIL"
    elif fast < FAST_MOUTH_MS:
        st = "MISSING"
    else:
        st = "PASS"
    row(rows, "S11.C5", st, "; ".join(per) + ("" if fast >= FAST_MOUTH_MS else " | no mouth faster than %.0f m/s" % FAST_MOUTH_MS),
        "max <= %.1f m per hold, a mouth >= %.0f m/s" % (HOLD_MAX_ERROR_M, FAST_MOUTH_MS), what, part)

def check_grab(S, rows):
    part = "grab" if S.ran("grab") else "grabse"
    reaper = T.grab_row("ReaperLeviathan")
    starts = [d for d in S.rows("se", "s11.grab") if d.get("phase") == "start"]
    bad_starts = []
    for d in starts:
        g = T.grab_row(d.get("creature") or "") or reaper
        if not T.size_fits(g, _f(d.get("sx"), 0), _f(d.get("sy"), 0), _f(d.get("sz"), 0)):
            bad_starts.append(d)
    check_size(S, rows, part, bad_starts)
    check_debris(S, rows, part)
    if S.mode == "S11b":
        nat = [d for d in S.rows("sn", "s11.grab") if d.get("phase") in ("start", "refused")]
        row(rows, "S11.C.NATURAL", "INFO", "%d natural grab row(s) in Subnautica (%s)" % (len(nat), ", ".join(sorted({"%s %s" % (d.get("creature"), d.get("phase")) for d in nat})) or "none"),
            "-", "a reaper's own grab (the forced one is judged by C2/C3)", part, required=False)
    holds = [d for d in S.rows("se", "s11.hold") if _f(d.get("heldS"), 0) >= 1.0]
    if not holds:
        row(rows, "S11.C2", "MISSING", "no hold row after 1 s", "median error <= %.1f m" % HOLD_ERROR_M, "the held grid follows the mouth (forces)", part)
    else:
        errs = sorted(_f(d.get("errorM")) for d in holds)
        med = errs[len(errs) // 2]
        degs = sorted(_f(d.get("errorDeg")) for d in holds if not math.isnan(_f(d.get("errorDeg"))))
        mdeg = degs[len(degs) // 2] if degs else float("nan")
        ok = med <= HOLD_ERROR_M and degs and mdeg <= HOLD_ERROR_DEG
        # S11b 20261023-220012: each hold (key) apart, the forced one (77) and a creature's own, so a failing one is named
        per = []
        for k in sorted({str(d.get("key")) for d in holds}):
            ek = sorted(_f(d.get("errorM")) for d in holds if str(d.get("key")) == k)
            dk = sorted(_f(d.get("errorDeg")) for d in holds if str(d.get("key")) == k and not math.isnan(_f(d.get("errorDeg"))))
            per.append("%s %.2f m %s deg (%d)" % ("forced" if k == "77" else "creature", ek[len(ek) // 2], "%.0f" % dk[len(dk) // 2] if dk else "-", len(ek)))
            ok = ok and ek[len(ek) // 2] <= HOLD_ERROR_M and (not dk or dk[len(dk) // 2] <= HOLD_ERROR_DEG)
        row(rows, "S11.C2", "PASS" if ok else "FAIL", "median %.2f m, %s deg over %d rows; %s" % (med, "%.1f" % mdeg if degs else "no", len(errs), ", ".join(per)),
            "<= %.1f m, <= %.0f deg" % (HOLD_ERROR_M, HOLD_ERROR_DEG), "the held grid follows the mouth (forces and torque)", part)
    check_hold_max(S, rows, part)
    check_jaw(S, rows, part)
    check_release(S, rows, part)
    if not starts:
        row(rows, "S11.C3", "MISSING", "no applied grab", "release <= planned + %.1f s" % RELEASE_SLACK_S, "released on the creature's release event", part)
        return
    rel = [d for d in S.rows("se", "s11.grab") if d.get("phase") == "release"]
    problems = []
    for s in starts:
        r = [d for d in rel if str(d.get("key")) == str(s.get("key"))]
        if not r:
            problems.append("key %s never released" % s.get("key"))
            continue
        held, planned = _f(r[0].get("heldS")), _f(r[0].get("plannedS"))
        # (format rage.hold): an angered hold is planned ExtendMs longer (Subnautica's s11.rage rage row)
        planned += max([_f(d.get("extendS"), 0.0) for d in S.rows("sn", "s11.rage") if d.get("outcome") == "rage" and str(d.get("key")) == str(s.get("key"))] or [0.0])
        if held > planned + RELEASE_SLACK_S:
            problems.append("key %s held %.1f s > planned %.1f + %.1f" % (s.get("key"), held, planned, RELEASE_SLACK_S))
    row(rows, "S11.C3", "PASS" if not problems else "FAIL", "%d start(s), %d release(s)" % (len(starts), len(rel)) + ("; " + "; ".join(problems) if problems else ""),
        "every start released in time", "released on the creature's release event (or the 1 s timeout)", part)

# ======================================================================================================================
# D
# ======================================================================================================================

HAZARD_TYPES = tuple(h[1] for h in T.HAZARD)

def check_hazard(S, rows):
    se = S.rows("se", "s11.hazard")
    applied_ids = {}
    for d in se:
        applied_ids.setdefault(str(d.get("id")), []).append(d)
    if S.mode == "S11b":
        sn = S.rows("sn", "s11.hazard")
        if not sn and _no_character(S, "hazard"):
            row(rows, "S11.D1", "MISSING", "the SE character was absent or dead during the scene", "every type", "every Subnautica damage source reaches SE", "hazard")
            row(rows, "S11.D1.once", "MISSING", "-", "1 id per event, applied once", "exactly once", "hazard")
            return
        if not sn:
            row(rows, "S11.D1", "MISSING", "no s11.hazard rows", "every type", "every Subnautica damage source reaches SE", "hazard")
            row(rows, "S11.D1.once", "MISSING", "-", "1 id per event, applied once", "exactly once", "hazard")
            return
        got = {}
        for d in sn:
            if d.get("id") and str(d.get("id")) in applied_ids:
                got.setdefault(int(d.get("type")), 0)
                got[int(d.get("type"))] += 1
        missing = [t for t in HAZARD_TYPES if t not in got]
        row(rows, "S11.D1", "PASS" if not missing else "FAIL", "%d/%d types applied" % (len(HAZARD_TYPES) - len(missing), len(HAZARD_TYPES)) +
            (" (missing %s)" % ",".join(T.SN_TYPES[t] for t in missing) if missing else ""), "every type of the table", "every Subnautica damage source reaches SE", "hazard")
        events = {}
        for d in sn:
            if d.get("id"):
                events.setdefault(int(d.get("event") or 0), []).append(str(d.get("id")))
        multi = [e for e, ids in events.items() if len(ids) != 1]
        twice = [i for i, ds in applied_ids.items() if len(ds) != 1]
        if not events:
            row(rows, "S11.D1.once", "INFO", "no record id to judge", "1 id per event, applied once", "exactly once per event", "hazard", required=False)
            return
        ok = not multi and not twice
        row(rows, "S11.D1.once", "PASS" if ok else "FAIL",
            "%d events, %d with != 1 id, %d ids applied != 1" % (len(events), len(multi), len(twice)), "1 id per event, applied once", "exactly once per event", "hazard")
    else:
        inj = [e for e in S.replies("hazardse.") if "burst" not in str(e.get("label"))]
        types = {int((e.get("args") or {}).get("inject", {}).get("type", -1)) for e in inj if ((e.get("reply") or {}).get("result") or {}).get("outcome") == "applied"}
        missing = [t for t in HAZARD_TYPES if t not in types]
        st = "MISSING" if not inj else ("PASS" if not missing else "FAIL")
        row(rows, "S11.D1", st, "%d/%d types applied (injects)" % (len(HAZARD_TYPES) - len(missing), len(HAZARD_TYPES)), "every type of the table",
            "every hazard type reaches SE's health (SE half)", "hazardse")
        twice = [i for i, ds in applied_ids.items() if len(ds) != 1]
        row(rows, "S11.D1.once", "PASS" if se and not twice else ("MISSING" if not se else "FAIL"), "%d ids, %d applied != 1" % (len(applied_ids), len(twice)),
            "each id once", "exactly once per record", "hazardse")
        heat = sorted((d["_t"], _f(d.get("granted"), 0)) for d in se if int(d.get("type") or -1) == 1 and d.get("_t") is not None)
        cap = next(h[4] for h in T.HAZARD if h[1] == 1)
        worst = 0.0
        for i, (t0, _g) in enumerate(heat):
            s = sum(g for t, g in heat[i:] if t - t0 < 1.0)
            worst = max(worst, s)
        st = "MISSING" if len(heat) < 3 else ("PASS" if worst <= cap + 1e-6 else "FAIL")
        row(rows, "S11.D3", st, "worst 1 s window %.1f HP" % worst, "<= %d HP" % cap, "the per-second cap of the hazard table", "hazardse")

def check_emp(S, rows):
    part = "emp" if S.ran("emp") else "hazard"
    sn = [d for d in S.rows("sn", "s11.emp") if d.get("why") != "noGrid"]
    if not sn:
        why = "no grid section in Subnautica (s11.emp noGrid)" if S.rows("sn", "s11.emp") else "no s11.emp row"
        row(rows, "S11.D2", "MISSING", why, "the id reaches SE, a producer off", "crabsquid EMP as SE power loss", part)
        return
    ids = [str(d.get("id")) for d in sn if d.get("id")]
    se_rows = [d for d in S.rows("se", "s11.emp") if d.get("id") and not d.get("dry")]
    se_ids = {str(d.get("id")) for d in se_rows}
    se_rej = {str(d.get("id")): d for d in S.rows("se", "s11.reject") if d.get("unit") == "emp"}
    reached = [i for i in ids if i in se_ids or i in se_rej]
    off = sum(int(d.get("off") or 0) for d in se_rows if str(d.get("id")) in reached)
    producers = sum(int(d.get("producers") or 0) for d in se_rows if str(d.get("id")) in reached)
    st = "PASS" if ids and len(reached) == len(ids) and off > 0 else "FAIL"
    rej = ", ".join(sorted({"%s %s" % (d.get("reason"), d.get("detail")) for d in se_rej.values()}))
    row(rows, "S11.D2", st, "%d/%d ids reached SE, %d of %d producer(s) off%s" % (len(reached), len(ids), off, producers, ("; rejects: " + rej) if rej else ""),
        "every id, a producer off", "crabsquid EMP as SE power loss", part)

# ======================================================================================================================
# E
# ======================================================================================================================

def check_air(S, rows):
    se_ox = S.rows("se", "s11.oxygen")
    if S.mode == "S11b":
        sn = [d for d in S.rows("sn", "s11.oxygen") if d.get("id")]
        sent = {str(d.get("id")): _f(d.get("seconds")) for d in sn}
        part = "air"
        if _no_character(S, "air") or (S.rows("sn", "s11.oxygen") and all(d.get("why") == "notEmitted" for d in S.rows("sn", "s11.oxygen"))):
            row(rows, "S11.E1", "MISSING", "the SE character was absent or dead during the scene", "litres = s / 45 x capacity", "air sources refill the SE suit", part)
            row(rows, "S11.E2", "MISSING", "the SE character was absent or dead during the scene", "applied = min(amount, max - before)", "first aid heals SE's health", part)
            return
        if not S.rows("sn", "s11.oxygen"):
            row(rows, "S11.E1", "MISSING", "no s11.oxygen rows", "litres = s / 45 x capacity", "air sources refill the SE suit", part)
        else:
            okc, bad = 0, []
            for d in se_ox:
                i = str(d.get("id"))
                if i not in sent:
                    continue
                want = T.litres(min(sent[i], 120), _f(d.get("capacity")))
                after_want = min(_f(d.get("capacity")), _f(d.get("before")) + want)
                if abs(_f(d.get("litres")) - want) <= 1e-3 and abs(_f(d.get("after")) - after_want) <= 1e-2:
                    okc += 1
                else:
                    bad.append(i)
            st = "PASS" if sent and okc == len(sent) else "FAIL"
            row(rows, "S11.E1", st, "%d/%d records applied right%s" % (okc, len(sent), (" (bad %s)" % ",".join(bad)) if bad else ""),
                "litres = s / 45 x capacity", "air sources refill the SE suit", part)
        heals_sn = [d for d in S.rows("sn", "s11.heal")]
        ids = {str(d.get("id")): _f(d.get("amount")) for d in heals_sn if d.get("id")}
    else:
        part = "airse"
        inj = [e for e in S.replies("airse.bubble")]
        if not inj:
            row(rows, "S11.E1", "MISSING", "no inject", "litres = s / 45 x capacity", "air sources refill the SE suit", part)
        else:
            okc = 0
            for d in se_ox:
                want = T.litres(_f(d.get("seconds")), _f(d.get("capacity")))
                if abs(_f(d.get("litres")) - want) <= 1e-3:
                    okc += 1
            row(rows, "S11.E1", "PASS" if se_ox and okc == len(se_ox) else "FAIL", "%d/%d rows right" % (okc, len(se_ox)), "litres = s / 45 x capacity",
                "air sources refill the SE suit (SE half)", part)
        ids = {}
        for e in S.replies("airse.heal"):
            res = (e.get("reply") or {}).get("result") or {}
            if res.get("id"):
                ids[str(res["id"])] = _f(((e.get("args") or {}).get("inject") or {}).get("amount"))
    heals = S.rows("se", "s11.heal")
    if not ids and not S.rows("sn", "s11.heal") and part == "air":
        row(rows, "S11.E2", "MISSING", "no s11.heal rows", "applied = min(amount, max - before)", "first aid heals SE's health", part)
        return
    good = 0
    for d in heals:
        i = str(d.get("id"))
        if i not in ids:
            continue
        want = T.se_heal(ids[i], _f(d.get("hpBefore")), _f(d.get("hpMax")))
        if abs(_f(d.get("se")) - want) <= 1e-3 and _f(d.get("applied")) <= want + 1e-3:
            good += 1
    st = "PASS" if ids and good == len(ids) else "FAIL"
    row(rows, "S11.E2", st, "%d/%d heals right" % (good, len(ids)), "applied = min(amount, max - before)", "first aid heals SE's health", part)

# ======================================================================================================================
# evaluation
# ======================================================================================================================

PART_CHECKS = ((("danger",), check_danger), (("stack",), check_stack), (("path",), check_path), (("grab", "grabse"), check_grab),
               (("hazard", "hazardse"), check_hazard), (("emp",), check_emp), (("air", "airse"), check_air),
               (("volley",), check_volley), (("holdhit",), check_rage), (("surface",), check_surface))

def character_gate(S, rows):
    """pass 4 (S11b run 3): the reapers killed the SE character in the danger part; SE's respawn screen paused the session and
 every later part judged a frozen world. The first se_character event that is not alive marks its part: rows of that part
 and of the parts after it that did not pass become MISSING (not judged), never FAIL."""
    ev = [e for e in S.session if e.get("ev") == "se_character"]
    if S.mode != "S11b" or not ev:
        return
    lost = next((e for e in ev if e.get("present") is False or e.get("alive") is False), None)
    guard = sorted({str(e.get("part")) for e in ev if e.get("guard")})
    if lost is None:
        row(rows, "S11.CHAR", "INFO", "alive after %d part(s); guard on in %s" % (len(ev), ",".join(guard) or "none"), "-",
            "SE's character through the session (the guard keeps scripted bites from killing it)", "session", required=False)
        return
    order = list(S.parts)
    lp = str(lost.get("part"))
    after = set(order[order.index(lp):]) if lp in order else set()
    if lp == "grab":
        after |= {"grabse"}
    hit = []
    for r in rows:
        if r.get("part") in after and r["status"] == "FAIL":
            r["status"] = "MISSING"
            r["measured"] = ("the SE character died in part %s (SE paused at its respawn screen) | " % lp) + str(r["measured"])
            hit.append(r["id"])
        elif r.get("part") in after and r["status"] == "MISSING":
            r["measured"] = ("character dead since part %s | " % lp) + str(r["measured"])
    row(rows, "S11.CHAR", "INFO", "the SE character %s after part %s (health %s); %d row(s) not judged: %s" % (
        "dead" if lost.get("present") else "absent", lp, lost.get("health"), len(hit), ",".join(hit) or "none"), "-",
        "SE's character through the session (dead = SE paused at its respawn screen)", "session", required=False)

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

def evaluate(folder):
    S = Session(folder)
    rows = []
    for parts, fn in PART_CHECKS:
        if not S.ran(*parts):
            continue
        try:
            fn(S, rows)
        except Exception as e:  # noqa: BLE001 - an analyser bug is a FAIL row, never a crash
            row(rows, "S11.%s" % parts[0], "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:120], parts[0])
    character_gate(S, rows)
    ag = S.rows("sn", "s11.aggro")
    if ag:
        row(rows, "S11.AGGRO", "INFO", "%d rows: max near %d, aggressive %d, on the character %d, on the ghost %d, on grids %d" % (
            len(ag), max(int(d.get("near") or 0) for d in ag), max(int(d.get("aggressive") or 0) for d in ag),
            sum(int(d.get("targetCharacter") or 0) for d in ag), sum(int(d.get("targetGhost") or 0) for d in ag),
            sum(int(d.get("targetGrid") or 0) for d in ag)), "-", "what the creatures near the camera target (diagnostic)", "session", required=False)
    db = S.rows("sn", "s11.debris")
    if db:
        row(rows, "S11.DEBRIS", "INFO", "%d remnant(s) judged in Subnautica (%d debris): no EcoTarget, no bite, no grab, no push" % (
            len({d.get("root") for d in db}), len({d.get("root") for d in db if d.get("debris")})), "-", "grid remnants are no creature target (pass 6)", "session", required=False)
    fx = S.rows("sn", "s11.dmgfx")
    hud = S.rows("se", "s11.hud")
    if fx or hud:
        row(rows, "S11.FX", "INFO", "Subnautica FX %d (%s; %d splatter(s) moved); SE overlay rows %d (%s)" % (
            len(fx), ", ".join(sorted({str(d.get("type")) for d in fx})) or "none", sum(int(d.get("splatters") or 0) for d in fx),
            len(hud), ", ".join(sorted({str(d.get("mode")) for d in hud})) or "none"), "-",
            "the diver's damage under Subnautica's picture: its own effects; SE's red fades 800 ms after the last drop", "session", required=False)
    sc = S.rows("sn", "s11.scale")
    if sc:
        row(rows, "S11.SCALE", "INFO", "%d creature(s) scaled: %s" % (len(sc), ", ".join(sorted({"%s x%g bite %s" % (d.get("creature"), _f(d.get("factor")), d.get("biteSn", "?")) for d in sc}))),
            "-", "the leviathans' size (owner 2026-10-23)", "session", required=False)
    row(rows, "S11.PIN", "INFO", "twin 0x%016x" % T.PINNED_HASH, "-", "the conventions pin (the plugins log it at start)", "session", required=False)
    mutant_rows(S, rows)
    return rows, S

def write_results(folder, rows, strict=True, extra=None) -> bool:
    ok = C.verdict(rows, strict)
    res = {"tool": "analyze_s11", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "s11-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "s11-table.txt"), "w", encoding="utf-8") as f:
        f.write(C.table(rows) + "\n")
    return ok

# ======================================================================================================================
# self-test: synthetic sessions in the plugins' row forms
# ======================================================================================================================

E0 = 1792800000.0

def _iso(t):
    import datetime
    return datetime.datetime.utcfromtimestamp(E0 + t).strftime("%Y-%m-%dT%H:%M:%S.%f") + "Z"

class _Synth:
    def __init__(self, folder, mode, parts, mutants=()):
        self.folder, self.mode = folder, mode
        os.makedirs(folder, exist_ok=True)
        self.sess, self.se, self.sn = [], [], []
        self.tau = 0.0
        self.seq = 0
        self.event("start", mode=mode, parts=list(parts), mutants=list(mutants), kind="s11")

    def event(self, ev, **kw):
        self.sess.append(dict({"utc": _iso(self.tau), "ev": ev}, **kw))

    def se_row(self, ev, **kw):
        self.se.append(dict({"t": _iso(self.tau), "ev": ev}, **kw))

    def sn_row(self, ev, **kw):
        self.sn.append({"t": _iso(self.tau), "level": "info", "event": ev, "ev": ev, "data": kw})

    def nid(self):
        self.seq += 1
        return "7:%d" % self.seq

    def write(self):
        self.event("end", rc=None)
        for name, recs in (("s11-session.jsonl", self.sess), ("se-20261023-000000.jsonl", self.se), ("sn-20261023-000000-1234.jsonl", self.sn)):
            with open(os.path.join(self.folder, name), "w", encoding="utf-8") as f:
                for r in recs:
                    f.write(json.dumps(r) + "\n")

def _danger(s, m):
    floor = 0.0 if m == "S11.M1" else 0.5
    h = 5000.0
    while True:
        d = T.applied(30.0, h, 5000.0, 1000, int(floor * 1000))
        s.sn_row("s11.danger", creature="ReaperLeviathan", cls="Handheld", tier="Leviathan", base=30.0, applied=d, healthBefore=h,
                 healthAfter=h - d, maxHealth=5000.0, floorPermille=int(floor * 1000), floored=d < 30.0, killed=h - d <= 0, why="scene rifle")
        s.tau += 0.25
        h -= d
        if d <= 0 or h <= 0:
            break
    if h > 0:
        s.sn_row("s11.danger", creature="ReaperLeviathan", cls="Explosion", tier="Leviathan", base=11000.0, applied=h, healthBefore=h,
                 healthAfter=0.0, maxHealth=5000.0, floorPermille=0, floored=True, killed=True, why="scene warhead")
    h2 = 5000.0
    for k in range(2):   # two hand-held rockets on the second reaper
        base = T.damage_hp(20000, 30, 1, 1000)
        d = T.applied(base, h2, 5000.0, 1000, int(floor * 1000))
        s.sn_row("s11.danger", creature="ReaperLeviathan", cls="Handheld", tier="Leviathan", base=base, applied=d, healthBefore=h2,
                 healthAfter=h2 - d, maxHealth=5000.0, floorPermille=int(floor * 1000), floored=d < base, killed=h2 - d <= 0, why="scene rocket")
        h2 -= d
        s.tau += 0.25
        if h2 <= 0:
            break

def _stack(s, m):
    for n, y, r, cl in ((5, 15000, 22.4415, "Explosion"), (1, 15000, 22.4415, "Explosion"), (1, 20000, 30.0, "Handheld")):
        args = {"stack": n, "yield": y, "radius": r}
        if cl == "Handheld":
            args["cls"] = "handheld"
        s.event("reply", label="stack.%d.%s" % (n, cl), args=args, reply={"ok": True})
        g = T.group([T.blast(0, -20, 60, r, y)] * n, 16)
        s.se_row("s11.blast", tick=100 + n, n=n, warheads=n if cl == "Explosion" else 0, synthetic=n,
                 groups=[{"yield": x["yld"], "radius": x["r"], "count": x["n"], "sumR3": x["s3"], "cls": cl} for x in g])
        if s.mode == "S11b" and m != "S11.M2":
            s.sn_row("s11.chain", op="play", extra=T.yield_code(n * y), radius=r * n ** (1.0 / 3.0))
            s.sn[-1]["data"]["yield"] = float(T.yield_code(n * y) * 10)
        s.tau += 1.0

def _path(s, m):
    s.sn_row("s11.scene", part="path", phase="start", seconds=60.0)
    for i in range(6):
        hits = 0 if m == "S11.M3" else 2
        saw = hits > 0 or i % 2 == 1        # a creature whose own mask sees layer 0 detects the wall without the hook
        s.sn_row("s11.probe", creature="Peeper", instance=100 + i, terrainOnly=i % 2 == 0, scanDistance=2.0, scanRadius=0.0, dFace=1.0,
                 result=0.9 if saw else 0.0, hookHits=hits, saw=saw, why="")
    s.sn_row("s11.scene", part="path", phase="end", samples=400, inside=0, creatures=16, near=55, avoidScans=900, probes=6,
             avoidHits=0 if m == "S11.M3" else 37, avoidTerrainHits=0, characterPresent=True, steers=4)

PROBE_BOXES = (("small", (1.5, 1.5, 0.5), 9), ("single", (2.5, 2.5, 2.5), 1), ("wall5", (12.5, 12.5, 2.5), 25), ("row8", (20.0, 2.5, 2.5), 8),
               ("row14", (35.0, 2.5, 2.5), 14), ("room", (12.5, 12.5, 12.5), 98))

def _grab(s, m):
    for label, box, nb in PROBE_BOXES:
        for cre in ("ReaperLeviathan", "SeaDragon"):
            fits = T.size_fits(T.grab_row(cre), *box)
            vol = box[0] * box[1] * box[2]
            debris = T.is_debris(nb, vol)
            se = fits or m == "S11.M4"        # S11.M4: SE ignores the size class (its fits flag stays the measured one)
            if debris and m != "S11.M11":     # S11.M11: SE ignores the debris rule
                res = {"outcome": "rejected", "reason": "Debris"}
            else:
                res = {"outcome": "would apply", "detail": "would hold"} if se else {"outcome": "rejected", "reason": "TooLarge"}
            res.update({"creature": cre, "size": list(box), "fits": fits, "sizeSource": "skin", "blocks": nb, "volumeM3": vol, "debris": debris})
            s.event("size_probe", label=label, creature=cre, reply={"ok": True, "result": res})
    s.se_row("s11.grab", phase="start", id=s.nid(), key=77, grid=123, creature="ReaperLeviathan", sx=12.5, sy=12.5, sz=2.5, blocks=25)
    if m == "S11.M4":
        s.se_row("s11.grab", phase="start", id=s.nid(), key=78, grid=124, creature="ReaperLeviathan", sx=20.0, sy=2.5, sz=2.5, blocks=8)
    for i in range(6):
        s.tau += 0.5
        s.se_row("s11.hold", key=77, grid=123, errorM=1.2 - 0.15 * i, errorDeg=40.0 - 6.0 * i, heldS=0.5 * (i + 1),
                 maxErrorM=34.0 if m == "S11.M12" else 1.4, targetSpeed=40.0 if i == 5 else 1.0, jawM=6.3 if m == "S11.M13" else 0.4, blend=min(1.0, 0.5 * (i + 1)))
    if m != "S11.M5":
        s.se_row("s11.grab", phase="release", key=77, grid=123, heldS=5.1, plannedS=4.0, why="timeout", mode="force" if m == "S11.M12" else "kinematic",
                 maxErrorM=34.0 if m == "S11.M12" else 1.4, maxTargetSpeed=40.0, snaps=0 if m == "S11.M12" else 9,
                 anchor="bite" if m == "S11.M13" else "centre", blendMs=0.0 if m == "S11.M13" else 1000.0, maxJawM=6.3 if m == "S11.M13" else 1.1)
        if m == "S11.M4":
            s.se_row("s11.grab", phase="release", key=78, grid=124, heldS=3.0, plannedS=3.0, why="timeout")
        s.tau += 0.5
        s.se_row("s11.grab", phase="after", key=77, grid=123, surface=False, speedAtRelease=40.0, speed=8.5, topDepthM=6.0, why="timeout")

def _hazard_b(s, m):
    for ev, t in enumerate(HAZARD_TYPES + (6,), 1):
        if m == "S11.M6":
            s.sn_row("s11.hazard", event=ev, type=t, amount=1.0, via="player", id="", why="mutant")
            continue
        ids = [s.nid()] + ([s.nid()] if m == "S11.M7" else [])
        for i in ids:
            s.sn_row("s11.hazard", event=ev, type=t, amount=1.0, via="player", id=i, why="")
            s.se_row("s11.hazard", id=i, type=t, seType="Deep", amount=1.0, se=1.0, granted=1.0, applied=1.0, hpBefore=100.0)

def _emp(s, m):
    if m == "S11.M9":
        s.sn_row("s11.emp", root="0x1", entity="0x1", seconds=2.0, id="", why="mutant")
    else:
        i = s.nid()
        s.sn_row("s11.emp", root="0x1", entity="0x1", seconds=2.0, id=i, why="scene")
        s.se_row("s11.emp", id=i, grid=1, seconds=2.0, producers=1, off=1, extended=0, dry=False)

def _hazard_se(s, m):
    for t in HAZARD_TYPES:
        i = s.nid()
        s.event("reply", label="hazardse.%d" % t, args={"inject": {"unit": 17, "type": t, "amount": 2.0}}, reply={"ok": True, "result": {"outcome": "applied", "id": i}})
        s.se_row("s11.hazard", id=i, type=t, amount=2.0, se=2.0, granted=2.0, applied=2.0, hpBefore=100.0)
    s.tau += 2.0
    used = 0.0
    for k in range(3):
        i = s.nid()
        g = min(25.0, 50.0 - used)
        used += g
        s.se_row("s11.hazard", id=i, type=1, amount=25.0, se=25.0, granted=g, applied=g, hpBefore=90.0)
        s.tau += 0.1

def _air(s, m):
    cap = 100.0
    if s.mode == "S11b":
        before = 50.0
        for src, sec in (("bubble", 10.0), ("area", 2.5), ("base", 7.5)):
            if m == "S11.M8":
                s.sn_row("s11.oxygen", source=src, seconds=sec, id="", why="mutant")
                continue
            i = s.nid()
            s.sn_row("s11.oxygen", source=src, seconds=sec, id=i, why="")
            lit = T.litres(sec, cap)
            s.se_row("s11.oxygen", id=i, source=src, seconds=sec, litres=lit, before=before, after=min(cap, before + lit), capacity=cap)
            before = min(cap, before + lit)
        if m == "S11.M10":
            s.sn_row("s11.heal", amount=50.0, id="", why="mutant")
        else:
            i = s.nid()
            s.sn_row("s11.heal", amount=50.0, id=i, why="")
            s.se_row("s11.heal", id=i, amount=50.0, se=T.se_heal(50, 80, 100), applied=20.0, hpBefore=80.0, hpMax=100.0)
    else:
        i = s.nid()
        s.event("reply", label="airse.bubble", args={"inject": {"unit": 19, "amount": 10.0}}, reply={"ok": True, "result": {"outcome": "applied", "id": i}})
        s.se_row("s11.oxygen", id=i, source="bubble", seconds=10.0, litres=T.litres(10, cap), before=10.0, after=10.0 + T.litres(10, cap), capacity=cap)
        i = s.nid()
        s.event("reply", label="airse.heal", args={"inject": {"unit": 18, "amount": 20.0}}, reply={"ok": True, "result": {"outcome": "applied", "id": i}})
        s.se_row("s11.heal", id=i, amount=20.0, se=T.se_heal(20, 70, 100), applied=20.0, hpBefore=70.0, hpMax=100.0)

def _volley(s, m):
    s.sn_row("s11.pin", phase="start", part="volley", creature="ReaperLeviathan", instance=501, centre=[0, -40, 60], health=5000.0, scale=3.0)
    k = 0
    for kind, n in (("small", 10), ("large", 4), ("rail", 2)):
        for i in range(n):
            k += 1
            mid = str(9000000000000000000 + k)
            s.se_row("s11.volley", i=i, count=n, kind=kind, missile=int(mid), missileStr=mid, origin=0, speed=100.0)
            s.tau += 0.4
            if m == "S11.M15":
                continue     # no sweep: the missile flies through and explodes at its range
            idn = s.nid()
            s.sn_row("s11.missile", phase="hit", missile=mid, creature="ReaperLeviathan", instance=501, at=[0, -40, 52], how="segment", frames=6, id=idn, sent=True)
            s.se_row("s11.missile", phase="detonate", id=idn, missile=int(mid), missileStr=mid, creature=2540, instance=501, lagM=8.0, slug=kind == "rail", pool=0.0, yield_=0.0, why="SmallGrid")
            s.sn_row("s11.danger", creature="ReaperLeviathan", instance=501, cls="SmallGrid", tier="Leviathan", base=500.0, applied=500.0, healthBefore=5000.0, healthAfter=4500.0,
                     maxHealth=5000.0, floorPermille=250, floored=False, killed=False, why="blast missile=" + mid)
    for i in range(24):
        inside = i % 2 == 1
        cat = ("outside", "inside", "holder")[i % 3]
        s.sn_row("s11.burst", i=i, **{"from": cat}, hit=not (cat == "inside" and m == "S11.M15"), instance=501, origin=[0, -40, 20])
        s.tau += 0.02

def _holdhit(s, m):
    """ two forced holds, SE's rockets (blast rows "missile="), the rage book, the flee"""
    k = 0
    for n, rockets in ((1, 1), (2, 2), (3, 3)):
        s.sn_row("s11.holdhit", step="hold%d" % n, ok=True, why="", applied=0.0, instance=501, health=5000.0)
        s.sn_row("s11.grab", phase="start", creature="ReaperLeviathan", root="0x1", sx=12.5, sy=12.5, sz=2.5, fits=True, holdS=6.0, id=s.nid(), why="forced (scene)")
        for q in range(6):
            s.tau += 0.25
            s.sn_row("s11.jaw", key=501, creature="ReaperLeviathan", ageS=0.25 * (q + 1), centreGapM=4.0, drawnGapM=0.6 + 0.1 * q, blendS=1.0, mouthY=-30.0, creatureY=-34.0, gapM=0.5)
        s.tau += 0.5
        taken = 0.0
        released = False
        for r in range(rockets):
            k += 1
            taken += 500.0
            s.sn_row("s11.danger", creature="ReaperLeviathan", instance=501, cls="Explosion", tier="Leviathan", base=500.0, applied=500.0, healthBefore=5000.0 - taken + 500.0,
                     healthAfter=5000.0 - taken, maxHealth=5000.0, floorPermille=0, floored=False, killed=False, why="blast missile=%d" % (7000 + k))
            if m == "S11.M16":
                s.sn_row("s11.rage", key=501, creature="ReaperLeviathan", outcome="ignored", applied=500.0, taken=0.0, threshold=1500.0, maxHealth=5000.0, hits=0, angerMult=1.0, extendS=0.0, raged=False, holdAgeS=2.0)
            else:
                out = "release" if taken >= 1500.0 else ("rage" if r == 0 else "taken")
                s.sn_row("s11.rage", key=501, creature="ReaperLeviathan", outcome=out, applied=500.0, taken=taken, threshold=1500.0, maxHealth=5000.0, hits=r + 1,
                         angerMult=1.5, extendS=4.0, raged=True, holdAgeS=2.0)
                released = released or out == "release"
            s.tau += 0.05
        if released:
            s.sn_row("s11.grab", phase="release", creature="ReaperLeviathan", root="0x1", sx=0, sy=0, sz=0, fits=True, holdS=2.1, id=s.nid(), why="rage release: 1500 SN_HP taken (threshold 1500)")
            s.sn_row("s11.holdhit", step="holdEnd%d" % n, ok=True, why="fleeing", applied=0.0, instance=501, health=4000.0)
            for i in range(20):
                s.tau += 0.5
                s.sn_row("s11.flee", instance=501, distM=10.0 + 2.0 * i, leftS=10.0 - 0.5 * i, fleeing=True, holding=False)
        else:
            s.tau += 8.0
            s.sn_row("s11.grab", phase="release", creature="ReaperLeviathan", root="0x1", sx=0, sy=0, sz=0, fits=True, holdS=10.0, id=s.nid(), why="hold ended")
            s.sn_row("s11.holdhit", step="holdEnd%d" % n, ok=True, why="", applied=0.0, instance=501, health=4500.0)
        s.tau += 1.0

def _surface(s, m):
    for k in range(12):
        s.tau += 1.0
        s.sn_row("s11.surface", op="second", samples=10, holding=0, following=10, above=0, maxYFollowing=-6.0 if m != "S11.M14" else 3.0, maxYHolding=-1e300, maxMouthY=-1e300)
        if m == "S11.M14" and k == 5:
            s.sn_row("s11.surface", op="above", creature="ReaperLeviathan", instance=501, y=3.0, holding=False, mouthY=float("nan"))
    s.sn_row("s11.surface", op="rise", held=True, why="", y=-25.0)
    for k in range(4):
        s.tau += 1.0
        s.sn_row("s11.surface", op="second", samples=10, holding=10, following=0, above=0, maxYHolding=-20.0 + 5 * k, maxYFollowing=-1e300, maxMouthY=-18.0 + 5 * k)
    if m == "S11.M14":
        s.sn_row("s11.surface", op="above", creature="ReaperLeviathan", instance=501, y=4.0, holding=True, mouthY=6.0)
    else:
        s.sn_row("s11.grab", phase="release", creature="ReaperLeviathan", root="0x2", sx=0, sy=0, sz=0, fits=True, holdS=4.0, id=s.nid(), why="surface (top 1.8 m deep)")
        s.sn_row("s11.surface", op="riseEnd", y=-6.0, surfaceReleases=1)
        s.tau += 0.5
        s.se_row("s11.grab", phase="after", key=501, grid=9, surface=True, speedAtRelease=22.0, speed=3.0, topDepthM=1.5, why="release record")

def synth(folder, mode, mutant=None, dead_after=None):
    parts = {"S11b": ("selftest", "hazard", "air", "grab", "emp", "stack", "danger", "path", "volley", "holdhit", "surface", "teardown"),
             "S11se": ("selftest", "stack", "grabse", "hazardse", "airse", "teardown")}[mode]
    s = _Synth(folder, mode, parts, (mutant,) if mutant else ())
    for p in parts:
        s.event("part", name=p, phase="begin")
        if p == "danger":
            _danger(s, mutant)
        elif p == "stack":
            _stack(s, mutant)
        elif p == "path":
            _path(s, mutant)
        elif p in ("grab", "grabse"):
            _grab(s, mutant)
        elif p == "hazard":
            _hazard_b(s, mutant)
        elif p == "emp":
            _emp(s, mutant)
        elif p == "hazardse":
            _hazard_se(s, mutant)
        elif p in ("air", "airse"):
            _air(s, mutant)
        elif p == "volley":
            _volley(s, mutant)
        elif p == "holdhit":
            _holdhit(s, mutant)
        elif p == "surface":
            _surface(s, mutant)
        s.tau += 1.0
        s.event("part", name=p, phase="end")
        if mode == "S11b":
            dead = dead_after is not None and parts.index(p) >= parts.index(dead_after)
            s.event("se_character", part=p, present=True, alive=not dead, health=0.0 if dead else 100.0, guard=p in ("grab", "emp", "stack", "danger", "path", "volley", "holdhit", "surface"))
    s.write()

MUTANT_MODE = {"S11.M13": "S11se", "S11.M14": "S11b", "S11.M15": "S11b", "S11.M16": "S11b", "S11.M12": "S11se", "S11.M11": "S11se", "S11.M1": "S11b", "S11.M2": "S11b", "S11.M3": "S11b", "S11.M4": "S11se", "S11.M5": "S11se", "S11.M6": "S11b",
               "S11.M7": "S11b", "S11.M8": "S11b", "S11.M9": "S11b", "S11.M10": "S11b"}

def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S11 analyzer self-test (tests/s11/analyze_s11.py --selftest)")
    root = tempfile.mkdtemp(prefix="de-s11-analyze-")
    try:
        check(T.text_hash()[0] == T.PINNED_HASH, "the twin's pin 0x%016x" % T.PINNED_HASH)
        want = {"S11b": ("S11.A1", "S11.A2", "S11.A4", "S11.A3", "S11.B1", "S11.C1", "S11.C4", "S11.C5", "S11.C6", "S11.C2", "S11.C3", "S11.D1", "S11.D1.once", "S11.D2", "S11.E1", "S11.E2",
                         "S11.C7", "S11.R1", "S11.R2", "S11.R3", "S11.R4", "S11.R5"),
                "S11se": ("S11.A3", "S11.C1", "S11.C4", "S11.C5", "S11.C6", "S11.C2", "S11.C3", "S11.D1", "S11.D1.once", "S11.D3", "S11.E1", "S11.E2")}
        for mode in ("S11b", "S11se"):
            folder = os.path.join(root, mode)
            synth(folder, mode)
            rows, _S = evaluate(folder)
            st = {r["id"]: r["status"] for r in rows}
            bad = [(r["id"], r["status"], r["measured"]) for r in rows if r.get("required", True) and r["status"] not in ("PASS", "INFO")]
            check(not bad, "%s synthetic session: %d rows, all required PASS%s" % (mode, len(rows), (" (bad %s)" % bad) if bad else ""))
            check(all(st.get(i) == "PASS" for i in want[mode]), "%s: rows %s PASS" % (mode, ",".join(want[mode])))
            write_results(folder, rows)
            check(os.path.exists(os.path.join(folder, "s11-table.txt")), "%s: s11-table.txt and s11-results.json" % mode)
        for mid in sorted(MUTANT_ROWS, key=lambda k: int(k.split("M")[1])):
            mode = MUTANT_MODE[mid]
            folder = os.path.join(root, "%s-%s" % (mode, mid))
            synth(folder, mode, mid)
            rows, _S = evaluate(folder)
            st = {r["id"]: r["status"] for r in rows}
            red = [i for i in MUTANT_ROWS[mid] if st.get(i) == "FAIL"]
            others = [r["id"] for r in rows if r["status"] == "FAIL" and r["id"] not in MUTANT_ROWS[mid] and not r["id"].endswith(".red")]
            ok = len(red) == len(MUTANT_ROWS[mid]) and st.get(mid + ".red") == "PASS" and not others and C.verdict(rows, True)
            check(ok, "%s: %s red, %s.red PASS, nothing else red%s" % (mid, ",".join(MUTANT_ROWS[mid]), mid, (" (others %s)" % others) if others else ""))
        # a mutant that did nothing: its.red row fails
        folder = os.path.join(root, "silent")
        synth(folder, "S11b")
        path = os.path.join(folder, "s11-session.jsonl")
        lines = open(path, encoding="utf-8").read().splitlines()
        first = json.loads(lines[0])
        first["mutants"] = ["S11.M1"]
        lines[0] = json.dumps(first)
        open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
        rows, _S = evaluate(folder)
        st = {r["id"]: r["status"] for r in rows}
        check(st.get("S11.M1.red") == "FAIL", "a silent mutant fails its .red row")
        # night a test run-204313: two one-sample contacts pass; a creature inside for 3 samples fails
        for name, extra, want in (("transient", [(0.0, 101, 0.035), (9.0, 102, 0.054)], "PASS"), ("sitting", [(0.0, 103, 0.04), (0.1, 103, 0.05), (0.2, 103, 0.06)], "FAIL"),
                                  ("deep", [(0.0, 104, 0.6)], "FAIL")):
            folder = os.path.join(root, "b1-" + name)
            synth(folder, "S11b")
            path = os.path.join(folder, "sn-20261023-000000-1234.jsonl")
            lines = open(path, encoding="utf-8").read().splitlines()
            for t, inst, dep in extra:
                lines.append(json.dumps({"t": _iso(1000.0 + t), "level": "info", "event": "s11.path", "ev": "s11.path",
                                         "data": {"creature": "Boomerang", "instance": inst, "p": [0, -6, 38.8], "inside": True, "section": "0x1/0x2", "depth": dep}}))
            out = []
            for ln in lines:
                if '"s11.scene"' in ln and '"phase": "end"' in ln and '"path"' in ln:
                    j = json.loads(ln)
                    j["data"]["inside"] = len(extra)
                    j["data"]["samples"] = 31715
                    ln = json.dumps(j)
                out.append(ln)
            open(path, "w", encoding="utf-8").write("\n".join(out) + "\n")
            rows, _S = evaluate(folder)
            st = {r["id"]: r["status"] for r in rows}
            check(st.get("S11.B1") == want, "B1 tolerance %s: %s (want %s)" % (name, st.get("S11.B1"), want))
        # pass 4: the SE character dies in the danger part: danger and the later rows that fail are MISSING, never FAIL
        folder = os.path.join(root, "dead")
        synth(folder, "S11b", "S11.M3", dead_after="danger")
        rows, _S = evaluate(folder)
        st = {r["id"]: r["status"] for r in rows}
        check(st.get("S11.B1") == "MISSING" and st.get("S11.CHAR") == "INFO" and st.get("S11.E1") == "PASS" and st.get("S11.C1") == "PASS",
              "a dead character after danger: B1 (path) MISSING, not FAIL; earlier rows still judged")
        # pass 4: probes that never cross a class judge nothing (MISSING), a probe that disagrees with the twin FAILs
        folder = os.path.join(root, "nostraddle")
        synth(folder, "S11se")
        path = os.path.join(folder, "s11-session.jsonl")
        keep = [ln for ln in open(path, encoding="utf-8").read().splitlines() if '"size_probe"' not in ln or '"small"' in ln]
        open(path, "w", encoding="utf-8").write("\n".join(keep) + "\n")
        rows, _S = evaluate(folder)
        st = {r["id"]: r["status"] for r in rows}
        check(st.get("S11.C1") == "MISSING", "probes on one side of the class only: S11.C1 MISSING (the boundary not judged)")
        #: a natural thrashing hold (mouth 120 m/s) is judged on its median; a rage-extended hold passes C3 with its extension
        folder = os.path.join(root, "thrash")
        synth(folder, "S11b")
        path = os.path.join(folder, "se-20261023-000000.jsonl")
        lines = open(path, encoding="utf-8").read().splitlines()
        for i in range(8):
            lines.append(json.dumps({"t": _iso(2000.0 + i), "ev": "s11.hold", "key": 501, "grid": 9, "errorM": 0.8, "heldS": 1.0 + i, "jawM": 0.9 if i != 4 else 7.5, "blend": 1.0, "targetSpeed": 120.0}))
        snp = os.path.join(folder, "sn-20261023-000000-1234.jsonl")
        snl = open(snp, encoding="utf-8").read().splitlines()
        for i in range(20):   # Subnautica's view of that hold: 4.
            snl.append(json.dumps({"t": _iso(2000.0 + 0.25 * i), "level": "info", "event": "s11.jaw", "ev": "s11.jaw",
                                   "data": {"key": 777, "ageS": 1.5 + 0.25 * i, "drawnGapM": 4.8 if i % 3 else 18.0, "blendS": 1.0}}))
        open(snp, "w", encoding="utf-8").write("\n".join(snl) + "\n")
        lines.append(json.dumps({"t": _iso(2009.0), "ev": "s11.grab", "phase": "start", "id": "7:999", "key": 501, "grid": 9, "creature": "ReaperLeviathan", "sx": 5.7, "sy": 4.1, "sz": 9.7}))
        lines.append(json.dumps({"t": _iso(2010.0), "ev": "s11.grab", "phase": "release", "key": 501, "grid": 9, "heldS": 9.0, "plannedS": 4.0, "anchor": "centre",
                                 "blendMs": 1000.0, "maxJawM": 7.5, "maxTargetSpeed": 120.0, "maxErrorM": 1.8, "mode": "kinematic", "snaps": 30, "why": "release record"}))
        open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
        rows, _S = evaluate(folder)
        st = {r["id"]: r["status"] for r in rows}
        check(st.get("S11.C6") == "FAIL" and st.get("S11.C3") == "PASS", "a thrashing hold seen 4.8 m off the jaw in Subnautica: C6 FAIL; the angered hold's +4 s keeps C3 (PASS): C6 %s, C3 %s" % (st.get("S11.C6"), st.get("S11.C3")))
        # no data: MISSING, never PASS
        folder = os.path.join(root, "empty")
        os.makedirs(folder)
        with open(os.path.join(folder, "s11-session.jsonl"), "w", encoding="utf-8") as f:
            f.write(json.dumps({"ev": "start", "mode": "S11b", "parts": ["danger", "stack", "path", "grab", "emp", "hazard", "air", "volley", "holdhit", "surface"], "mutants": []}) + "\n")
        rows, _S = evaluate(folder)
        check(all(r["status"] in ("MISSING", "INFO") for r in rows), "a session without rows: every row MISSING")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("S11 ANALYZE SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
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
    print("S11 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
