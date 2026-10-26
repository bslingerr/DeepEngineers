"""S6 "Collision B + combat" oracle table: joins the SE and Subnautica logs and the launcher's session log of one
collected session folder (tests/out/s6/<stamp>/ from `launcher.py s6`) into the rows of ROADMAP S6 (B-T1, G1-E3,
N1-N10, B-T1e) and the seeded failures S6.M1-M12. Stdlib only; the S6 conventions, the lattice and the C8 field
meanings come from tests/standins/s6_stream.py (its own twin of CONTRACT §5.4, hash-checked), never from Wire's C#.

 python -I tests/s6/analyze_s6.py DIR [--lenient] [--json]
 python -I tests/s6/analyze_s6.py --selftest # synthetic S6a / S6se / S6b sessions: every row PASS, then each
 # of S6.M1-M12 turns exactly its rows red

S6 LOG CONTRACT (what the rows read). One JSON object per line; the event name in "ev" (SE plugin, stand-ins) or
"event" (SN plugin); the payload top-level or in "data"; "t" is an ISO UTC time (plugins) or QPC seconds (stand-ins).
Ids: decimal numbers, decimal strings, 0x-hex strings, or "<key>Str" companions (preferred when present); hit ids
"gen:seq". Floats may be written "NaN" / "inf". SE side (se-*.jsonl; the stand-in producer's se-standin.jsonl):
 s6.skin entity, root, subgrid, large, gridSize, version, why, sections, boxes, bytes, coverage, lane, detail[{id, c,
 n, hash}] [SKIN, N8]
 s6.damage id, gen, seq, kind (grid|character), target, root, section, flags, via, snType, seType, amount, k,
 kExpected, se, block[3]|null, searched, effBefore, effAfter, applied, hpBefore, hpAfter, local[3], tick
 (one per applied record; the text line "DE.Damage applied id=... applied=..." is read when no row exists)
 [N1, B-T1e, N5, N6, N8]
 s6.reject id, reason (a DamageReject name), detail, kind, target, section, amount, type, flags [N1, N10, N8]
 s6.stats drained, applied, rejected {reason: n}, admitted, duplicates, tooOld,... [N1, N10 counts]
 s6.c8 (stand-in producer, S6a) id, gen, seq, kind, target, root, section, type, amount, flags, local, w,
 dealerTechType, dealerId: every record Subnautica forwarded [B-T1 (2), N4, N5, N6]
Subnautica side (sn-*.jsonl; the stand-in host's sn-standin.jsonl):
 s6.penetration t, creature, id, section, depthM, dir, streak (depth > 0.05 m); {summary: true, pairs, maxDepthM,
 maxStreak} per second [B-T1 (1)]
 s6.hit gen, seq, kind, target, root, section, type, amount, flags, via, dealer, dealerId, w, local, tick, queued
 (true only for the overflow queue: ring full / corrupt / no C8; S6-3: the once/bt1 checks keep every
 non-capture, non-selfTest hit)
 (or {skip: reason,...}); health / healthAfter when reported [B-T1 (2), B-T1e]
 s6.perf steps, fixedDt, registered, sections, shapes, proxyMsP50/P95, stepMsP50/P95, proxiesOn [B-T1 (3)]
 s6.probe record, corner, expected, got, devM, boundM, rotated, tilted, pass (depth, tiltDeg when written) [G1-E3]
 s6.path id, techType, pos, inside (section or null) [N3]
 s6.gate mode, cheats, invisible, pass (text "DE.S6 freedom mode=... invisible=... PASS|REFUSED") [N2]
 s6.selftest sectionRecords, playerRecords, pass, detail [N9]
 s6.spawn techType, count, dist, cmd, ok; per object id, avoidTerrainOnly... [N3 information]
 s6.plan (stand-in host, S6se) mode n1|n10, targets [{entity, kind, cls, ids, amounts, sum, dupIds}] | list
 [{id, reason}]: the ground truth of N1 / N10
 s6.skin (stand-in host) entity, version, sections, boxes, bad, detail [SKIN]
Launcher session (s6-session.jsonl): start {mode, parts, mutants, quick, qpcFrequency, wm1}, part {name, phase
begin|end; n7 has the sub-parts n7.moving, n7.still, n7.away and, in S6b, n7.idle: information only) (utc + qpc on
every event), reply (every C11 command), c7 {part, sinceS, fixedDeltaTime, flags,
capabilities, damageDropped, damageOverflowDepth} at 10 Hz during the parts, s6bound {depth, tilt, boundM} (wire_ref
s6bound for G1-E3), n4_geometry {rockBetween, hullCentreY, seabedY}, combat_refused, end.

ROWS (required unless marked *): S6.B-T1.1,.2,.3 (bt1), S6.G1-E3 (g1e3), S6.N1 =.small +.large +.character +.dup
(n1), S6.N10 (n10), S6.SKIN* (skin), S6.B-T1e =.grid +.character +.once (bt1e), S6.N2 + S6.N9 (selftest), S6.N3,
N4, N5, N6, N7, N8 (their parts); S6.Mx.red for the session's mutants (its target rows are then expected red and not
required). Rows of parts the session did not run are not listed. Exit 0 when every required row passes (MISSING fails
unless --lenient).
"""
from __future__ import annotations

import calendar
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tests", "standins"),
           os.path.join(ROOT, "tests", "wire"), os.path.join(ROOT, "tools", "worldmap")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C          # noqa: E402 (row, table, verdict, key=value parser, jsonl reader)
import s6_stream as X          # noqa: E402 (conventions twin, lattice, record fields)

PEN_DEPTH_M = 0.1               # B-T1 (1): no penetration > 0.1 m...
PEN_MAX_STREAK = 3              #... for more than 3 consecutive physics steps
BT1_SECONDS = 180.0
BT1_BITES = (30.0, 80.0)        # boneshark, reaper
PERF_P95_MS = 1.0               # B-T1 (3)
PERF_SHAPES = 1000
PROXY_MAX_HEALTH = 100.0        # every proxy section's LiveMixinData.maxHealth (CONTRACT §4.2)
LEVEL_TOL_M = 0.001             # G1-E3 level
N1_TOL = 1e-3
N7_SHARE = 0.9
N7_SETTLE_S = {"n7.moving": 2.5, "n7.still": 3.0, "n7.away": 3.0}
N7_WANT = {"n7.moving": 0.02, "n7.still": 0.1, "n7.away": 0.1}
REAPER_BITE, BONESHARK_BITE, AMPEEL_SHOCK, CRASH_MAX = 80.0, 30.0, 15.0, 50.0
HULL100_ID = X.HULL_IDS["hull100"]
MUTANT_ROWS = {"S6.M1": ("S6.B-T1.1",), "S6.M2": ("S6.B-T1.2",), "S6.M3": ("S6.N1.dup",),
               "S6.M4": ("S6.N1.character",), "S6.M5": ("S6.G1-E3",), "S6.M6": ("S6.N2",),
               "S6.M7": ("S6.N1.small", "S6.N1.large"), "S6.M8": ("S6.B-T1e.character",), "S6.M9": ("S6.G1-E3",),
               "S6.M10": ("S6.N5",), "S6.M11": ("S6.N8",), "S6.M12": ("S6.N9",)}
AGGREGATES = {"S6.N1": ("S6.N1.small", "S6.N1.large", "S6.N1.character", "S6.N1.dup"),
              "S6.B-T1e": ("S6.B-T1e.grid", "S6.B-T1e.character", "S6.B-T1e.once")}
_EMBED = re.compile(r"\b(s6\.[A-Za-z0-9_]+)\s+(\{.*\})\s*$")
_ISO = re.compile(r"^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(\.\d+)?(Z|[+-]\d\d:?\d\d)?$")
row = C.row

# ======================================================================================================================
# reading
# ======================================================================================================================

def iso_epoch(s):
    """ISO 8601 UTC -> epoch seconds; None if not ISO."""
    m = _ISO.match(str(s).strip())
    if not m:
        return None
    y, mo, d, h, mi, se = (int(m.group(i)) for i in range(1, 7))
    frac = float(m.group(7)) if m.group(7) else 0.0
    t = calendar.timegm((y, mo, d, h, mi, se, 0, 0, 0)) + frac
    tz = m.group(8)
    if tz and tz != "Z":
        sign = 1 if tz[0] == "+" else -1
        hh, mm = int(tz[1:3]), int(tz[-2:])
        t -= sign * (hh * 3600 + mm * 60)
    return t

def stamp(rec):
    """("utc", epoch s) for ISO times, ("qpc", s) for the stand-ins' QPC seconds, else None."""
    t = rec.get("t", rec.get("utc"))
    if isinstance(t, (int, float)) and not isinstance(t, bool):
        return ("qpc", float(t))
    if isinstance(t, str):
        e = iso_epoch(t)
        if e is not None:
            return ("utc", e)
    return None

def parse_rows(recs):
    """[(name, data, stamp)] of the s6.* rows (and the text forms of DE.Damage applied / DE.S6 freedom)."""
    out = []
    for rec in recs:
        name = rec.get("ev") or rec.get("event")
        data = None
        if isinstance(name, str) and name.startswith("s6."):
            data = dict(rec["data"]) if isinstance(rec.get("data"), dict) else \
                {k: v for k, v in rec.items() if k not in ("ev", "event", "data")}
        text = rec.get("msg") if isinstance(rec.get("msg"), str) else rec.get("text")
        if isinstance(text, str):
            m = _EMBED.search(text)
            if m and data is None:
                try:
                    emb = json.loads(m.group(2))
                except ValueError:
                    emb = None
                if isinstance(emb, dict):
                    name, data = m.group(1), emb
            if data is None and text.startswith("DE.Damage applied "):
                name, data = "s6.damage", dict(C._kv(text[len("DE.Damage applied "):]), _text=True)
            elif data is None and text.startswith("DE.S6 freedom "):
                kv = C._kv(text[len("DE.S6 freedom "):])
                data = dict(kv, _text=True, **{"pass": text.rstrip().endswith("PASS")})
                name = "s6.gate"
        if data is None or not isinstance(name, str):
            continue
        out.append((name, data, stamp(rec)))
    return out

def ident(d, key):
    """An id field as int: "<key>Str" first, then the key (number, decimal or 0x string)."""
    for k in (key + "Str", key):
        v = d.get(k) if isinstance(d, dict) else None
        if v is None or isinstance(v, bool):
            continue
        if isinstance(v, int):
            return v & 0xFFFFFFFFFFFFFFFF
        if isinstance(v, float):
            return int(v) & 0xFFFFFFFFFFFFFFFF
        s = str(v).strip()
        try:
            return (int(s, 16) if s.lower().startswith("0x") else int(s)) & 0xFFFFFFFFFFFFFFFF
        except ValueError:
            continue
    return None

def hid(d):
    """The hit id "gen:seq" of a row."""
    v = d.get("id")
    if isinstance(v, str) and ":" in v:
        g, s = v.split(":", 1)
        try:
            return "%d:%d" % (int(g), int(s))
        except ValueError:
            return v
    g, s = d.get("gen", d.get("snGeneration")), d.get("seq", d.get("dedupSeq"))
    if g is not None and s is not None:
        try:
            return "%d:%d" % (int(g), int(s))
        except (TypeError, ValueError):
            return None
    return None

num = X.fnum

def vec(v, n=3):
    if isinstance(v, (list, tuple)) and len(v) >= n:
        out = [num(x) for x in v[:n]]
        return out if all(x is not None for x in out) else None
    if isinstance(v, str):
        parts = [p for p in re.split(r"[ ,()\[\]]+", v) if p]
        if len(parts) >= n:
            out = [num(p) for p in parts[:n]]
            return out if all(x is not None for x in out) else None
    return None

def flags_of(d):
    f = d.get("flags")
    if isinstance(f, (int, float)) and not isinstance(f, bool):
        return int(f)
    if isinstance(f, str):
        try:
            return int(f, 0)
        except ValueError:
            return 0
    return 0

def kind_of(d):
    k = d.get("kind", d.get("targetKind"))
    if k in (1, "1", "grid", "Grid", "GridSection"):
        return "grid"
    if k in (2, "2", "character", "Character"):
        return "character"
    return str(k)

def type_of(d):
    for k in ("snType", "type", "snDamageType"):
        v = d.get(k)
        if isinstance(v, (int, float)) and not isinstance(v, bool):
            return int(v)
        if isinstance(v, str):
            if v in ("DeepBite", "Normal"):
                return 0
            if v in ("DeepBlast", "Explosive"):
                return 7
            if v in ("DeepShock", "Electrical"):
                return 8
            try:
                return int(v)
            except ValueError:
                pass
    st = d.get("seType")
    return {"DeepBite": 0, "DeepBlast": 7, "DeepShock": 8}.get(st)

def pct(vals, q):
    v = sorted(x for x in vals if x is not None)
    if not v:
        return None
    k = max(0, min(len(v) - 1, int(math.ceil(q * len(v))) - 1))
    return v[k]

class Session:
    """One collected folder: the launcher's session log, the SE rows, the SN rows, part windows in both clocks."""

    def __init__(self, folder):
        import glob
        self.dir = folder
        self.session = C.read_jsonl(os.path.join(folder, "s6-session.jsonl"))
        self.se_files = sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        self.sn_files = sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))
        self.se = parse_rows(C.read_jsonl(self.se_files))
        self.sn = parse_rows(C.read_jsonl(self.sn_files))
        self.start = next((e for e in self.session if e.get("ev") == "start"), {}) or {}
        self.freq = float(self.start.get("qpcFrequency") or 10_000_000)
        self.mode = self.start.get("mode")
        self.parts = list(self.start.get("parts") or [])
        self.mutants = [m.split("@")[0] for m in (self.start.get("mutants") or [])]
        self.quick = bool(self.start.get("quick"))
        self.windows = {}
        open_ = {}
        for e in self.session:
            if e.get("ev") != "part":
                continue
            t = self.ev_time(e)
            if e.get("phase") == "begin":
                open_[e.get("name")] = t
            elif e.get("phase") == "end" and e.get("name") in open_:
                self.windows.setdefault(e["name"], []).append((open_.pop(e["name"]), t))
        for name, t in open_.items():           # an unfinished part runs to the end of the logs
            self.windows.setdefault(name, []).append((t, {"utc": 1e18, "qpc": 1e18}))

    def ev_time(self, e):
        return {"utc": iso_epoch(e.get("utc")) if e.get("utc") else None,
                "qpc": (float(e["qpc"]) / self.freq) if isinstance(e.get("qpc"), (int, float)) else None}

    def in_windows(self, st, prefix):
        """True when the row stamp falls in a window of a part named `prefix` or `prefix.*` (no windows: True)."""
        wins = [w for n, ws in self.windows.items() if n == prefix or n.startswith(prefix + ".") for w in ws]
        if not wins:
            return None
        if st is None:
            return False
        clock, t = st
        for b, e in wins:
            if b.get(clock) is not None and e.get(clock) is not None and b[clock] - 0.5 <= t <= e[clock] + 0.5:
                return True
        return False

    def rows(self, side, name, part=None, strict=False):
        src = self.se if side == "se" else self.sn
        out = []
        for n, d, st in src:
            if n != name:
                continue
            if part is not None:
                inside = self.in_windows(st, part)
                if inside is False or (inside is None and strict):
                    continue
            out.append(d)
        return out

    def events(self, ev, part=None):
        return [e for e in self.session if e.get("ev") == ev and (part is None or str(e.get("part", "")).startswith(
            part))]

    def ran(self, part):
        return part in self.parts

    def spawned(self, part, tech=None):
        """True when the launcher's de.spawn (label <part>.spawn[.<TechType>]) was answered ok: then a missing record
 means the hit never came (FAIL), not missing data."""
        for e in self.session:
            lab = str(e.get("label") or "")
            ok = isinstance(e.get("reply"), dict) and e["reply"].get("ok")
            if e.get("ev") == "reply" and lab.startswith(part + ".spawn") and (tech is None or lab.endswith(tech)) \
                    and ok:
                return True
        return False

# ======================================================================================================================
# the checks
# ======================================================================================================================

def check_bt1(S, rows):
    """B-T1 (1) penetration streaks, (2) the forwarded bites 30 and 80, (3) the proxy + PhysX time with 1,000 boxes."""
    pen = S.rows("sn", "s6.penetration", "bt1")
    summaries = [d for d in pen if d.get("summary")]
    steps = [d for d in pen if not d.get("summary")]
    perf_all = S.rows("sn", "s6.perf", "bt1")
    fdt = pct([num(d.get("fixedDt")) for d in perf_all], 0.5) or 0.02
    gap = 1.5 * fdt
    pairs = {}
    for d in steps:
        depth = num(d.get("depthM"))
        if depth is None or depth <= PEN_DEPTH_M:
            continue
        key = (str(d.get("id", d.get("creature"))), str(d.get("section")))
        pairs.setdefault(key, []).append(d)
    max_streak, worst = 0, None
    for key, ds in pairs.items():
        # Integrator S6: the plugin's "streak" counts consecutive physics steps exactly (S6Rules PenetrationStreaks,
 # one Note per pair per step); its "t" is realtimeSinceStartup since the bt1 start, so 50 Hz steps can be 33 ms
 # apart at 60 fps (> 1.5 x fixedDt) and rows past the per-second cap leave gaps. The time-gap rebuild is only the
 # fallback for rows without "streak".
        rs = [num(d.get("streak")) for d in ds]
        if all(s is not None for s in rs):
            best = max(int(s) for s in rs)
        else:
            ts = [num(d.get("t")) for d in ds]
            if all(t is not None for t in ts):
                ts = sorted(ts)
                run = best = 1
                for a, b in zip(ts, ts[1:]):
                    run = run + 1 if b - a <= gap else 1
                    best = max(best, run)
            else:
                best = 1
        if best > max_streak:
            max_streak, worst = best, key
    # the per-second summaries count every pair, rowed or not (the plugin caps the deep rows per second)
    for d in summaries:
        ms = num(d.get("maxStreak"))
        if ms is not None and int(ms) > max_streak:
            max_streak, worst = int(ms), ("summary", str(d.get("t")))
    secs = sum(max(0.0, (e.get("qpc") or 0) - (b.get("qpc") or 0)) for n, ws in S.windows.items()
               if n == "bt1" for b, e in ws if e.get("qpc", 0) < 1e17)
    if not summaries and not steps:
        row(rows, "S6.B-T1.1", "MISSING", "no s6.penetration rows", "streak <= %d steps > %.1f m" % (
            PEN_MAX_STREAK, PEN_DEPTH_M), "penetration monitor (ComputePenetration after every step)", "bt1")
    else:
        # review F16: the run length is the count of per-second summaries in phase bt1-run (the windows also summed the
 # baseline and bt1.k, so a short run passed)
        run_summaries = sum(1 for d in summaries if str(d.get("phase")) == "bt1-run")
        short = (not S.quick) and (0 < secs < BT1_SECONDS - 10 or 0 < run_summaries < BT1_SECONDS - 10)
        ok = max_streak <= PEN_MAX_STREAK and not short
        row(rows, "S6.B-T1.1", "PASS" if ok else "FAIL", "max streak %d (%d deep rows, %d summaries, %.0f s)" % (
            max_streak, sum(len(v) for v in pairs.values()), len(summaries), secs),
            "<= %d steps > %.1f m over %d s" % (PEN_MAX_STREAK, PEN_DEPTH_M, BT1_SECONDS),
            "creature colliders vs the proxy%s" % (" (worst %s)" % (worst,) if worst else ""), "bt1",
            maxDepthM=max([num(d.get("maxDepthM"), 0.0) for d in summaries] + [0.0]), stepGapS=gap)
    # (2)
    fwd = [d for d in S.rows("se", "s6.c8", "bt1") + S.rows("se", "s6.damage", "bt1") if type_of(d) == 0]
    src = "C8"
    if not fwd:
        fwd = [d for d in S.rows("sn", "s6.hit", "bt1") if not d.get("skip") and not d.get("capture") and not d.get("selfTest") and
               type_of(d) == 0]
        src = "s6.hit"
    amounts = sorted({round(num(d.get("amount"), -1), 3) for d in fwd})
    have = [b for b in BT1_BITES if any(abs(a - b) <= 1e-3 for a in amounts)]
    health = [d for d in S.rows("sn", "s6.hit", "bt1") if num(d.get("healthAfter")) is not None and
              num(d.get("healthBefore", d.get("health"))) is not None and
              num(d.get("healthAfter")) < num(d.get("healthBefore", d.get("health")))]
    # Integrator S6: the plugin writes one "health" (the section's LiveMixin after the call); every section starts at
 # maxHealth 100 (CONTRACT §4.2), so a lower value is a change
    health += [d for d in S.rows("sn", "s6.hit", "bt1") if num(d.get("healthAfter")) is None and
               num(d.get("health")) is not None and num(d.get("health")) < PROXY_MAX_HEALTH - 1e-6]
    st = "PASS" if len(have) == len(BT1_BITES) and not health else ("MISSING" if not fwd and not S.rows(
        "sn", "s6.hit") and not S.rows("se", "s6.c8") else "FAIL")
    row(rows, "S6.B-T1.2", st, "bites %s of %s (%d records via %s)%s" % (
        ",".join("%g" % b for b in have) or "none", ",".join("%g" % b for b in BT1_BITES), len(fwd), src,
        "; proxy health changed %d x" % len(health) if health else ""), ">= 1 x 30 and >= 1 x 80, health unchanged",
        "boneshark and reaper bites forwarded (originalDamage)", "bt1", amounts=amounts[:20])
    # (3)
    k = S.rows("sn", "s6.perf", "bt1.k") or [d for d in perf_all if (num(d.get("shapes"), 0) or 0) >= PERF_SHAPES]
    k = [d for d in k if (num(d.get("shapes"), 0) or 0) >= PERF_SHAPES]
    base = [d for d in perf_all if (num(d.get("sections"), 1) == 0) or d.get("proxiesOn") in (False, 0)]
    if not k:
        row(rows, "S6.B-T1.3", "MISSING", "no s6.perf row with >= %d shapes" % PERF_SHAPES, "p95 <= %.1f ms" %
            PERF_P95_MS, "proxy update + PhysX time per step with 1,000 boxes", "bt1")
    else:
        proxy = pct([num(d.get("proxyMsP95")) for d in k], 0.95) or 0.0
        step_k = pct([num(d.get("stepMsP95")) for d in k], 0.95)
        # Integrator S6: the plugin measures per step the proxy update (stepper FixedUpdate, order -1000) and PhysX (the
 # WaitForFixedUpdate resume minus the timer's FixedUpdate at order 32000, i.e. after every script): proxyPhysxMs is
 # B-T1 (3)'s quantity with the whole scene's PhysX (an upper bound of the proxies' share). stepMs also holds every
 # game script's FixedUpdate, so it is only the last resort. In S6a the wire proxy exists from the part's start,
 # so baseline rows (no sections) are few or none.
        cand = []
        direct = pct([num(d.get("proxyPhysxMsP95")) for d in k], 0.95)
        if direct is not None:
            cand.append((direct, "proxy+PhysX p95 %.3f (plugin per step; the whole scene's PhysX: upper bound)" % direct))
        physx_k = pct([num(d.get("physxMsP95")) for d in k], 0.95)
        physx_b = pct([num(d.get("physxMsP95")) for d in base], 0.5) if base else None
        if physx_k is not None and physx_b is not None:
            px = max(0.0, physx_k - physx_b)
            cand.append((proxy + px, "proxy p95 %.3f + PhysX delta %.3f (physx p95 %.3f - baseline %.3f)" % (
                proxy, px, physx_k, physx_b)))
        if not cand and base:
            step_b = pct([num(d.get("stepMsP95")) for d in base], 0.5) or 0.0
            physx = max(0.0, (step_k or 0.0) - step_b)
            cand.append((proxy + physx, "proxy p95 %.3f + PhysX delta %.3f (step p95 %.3f - baseline %.3f)" % (
                proxy, physx, step_k or 0.0, step_b)))
        if not cand:
            cand.append((proxy + (step_k or 0.0), "proxy p95 %.3f + step p95 %.3f (no baseline rows: upper bound)" % (
                proxy, step_k or 0.0)))
        total, how = min(cand)
        row(rows, "S6.B-T1.3", "PASS" if total <= PERF_P95_MS else "FAIL", "%.3f ms" % total, "<= %.1f ms" %
            PERF_P95_MS, how, "bt1", perfRows=len(k), baselineRows=len(base),
            alternatives=["%.3f: %s" % c for c in cand])

def wm1_records(S):
    """The box records of the session's WM-1 file with their depth and tilt (s6_stream.wm1_boxes), or None."""
    path = S.start.get("wm1")
    if not path or not os.path.exists(path):
        return None
    return X.wm1_boxes(path)

_BOUND_CACHE = {}

def bound_key(depth, tilt):
    """The launcher's bucket (launcher_s6.bound_key): depth up to the next 100 m, the effective tilt min(t, 180 - t) up
 to the next 5 degrees (the K corner error is the same for a hull turned upside down and grows with both)."""
    eff = min(tilt, 180.0 - tilt)
    return (math.ceil(max(depth, 0.0) / 100.0) * 100.0, min(90.0, math.ceil(max(eff, 0.0) / 5.0) * 5.0))

def computed_bound(depth, tilt):
    """wire_ref.s6_box_bound for a bucket when the session holds no s6bound entry for it (cached; None without the
 Wire twin)."""
    key = bound_key(depth, tilt)
    if key not in _BOUND_CACHE:
        try:
            import wire_ref as W
            _BOUND_CACHE[key] = W.s6_box_bound(W.site_by_name("v1"), key[0], key[1], samples=200)["boundM"]
        except (ImportError, AttributeError, KeyError):
            _BOUND_CACHE[key] = None
    return _BOUND_CACHE[key]

def probe_bound(S, d, bounds, recs):
    """The G1-E3 limit of one probe row: 1 mm for a level box on a level hull; tilted or rotated: the row's boundM, else
 the session's s6bound entry that is at least as deep and as (effectively) tilted as the probed box, else
 wire_ref.s6_box_bound for its bucket (the tool's bound is monotone in depth and in min(tilt, 180 - tilt))."""
    tilted, rotated = bool(d.get("tilted")), bool(d.get("rotated"))
    if not tilted and not rotated:
        return LEVEL_TOL_M, "level"
    b = num(d.get("boundM"))
    if b is not None and b > 0:
        return max(b, LEVEL_TOL_M), "row"
    depth, tilt = num(d.get("depth", d.get("depthY"))), num(d.get("tiltDeg", d.get("tilt")))   # the plugin writes depthY
    if (depth is None or tilt is None) and recs is not None:
        i = int(num(d.get("record"), -1) or -1)
        if 0 <= i < len(recs):
            depth, tilt = recs[i]["depth"], recs[i]["tilt"]
    if depth is None or tilt is None:
        return None, "no depth/tilt"
    depth = abs(depth)                     # SN y (negative) or a depth below the surface: the same bucket
    eff = min(tilt, 180.0 - tilt)
    ok = [e for e in bounds if num(e.get("depth"), -1.0) >= depth - 1e-6 and num(e.get("tilt"), -1.0) >= eff - 1e-6]
    if ok:
        e = min(ok, key=lambda e: num(e.get("boundM"), 1e9))
        return max(num(e.get("boundM")), LEVEL_TOL_M), "s6bound"
    c = computed_bound(depth, tilt)
    if c is None:
        return None, "no s6bound entry covers depth %.0f tilt %.1f" % (depth, tilt)
    return max(c, LEVEL_TOL_M), "wire_ref"

def check_g1e3(S, rows):
    probes = S.rows("sn", "s6.probe")
    if not probes:
        row(rows, "S6.G1-E3", "MISSING", "no s6.probe rows", "level 1 mm; tilted <= s6bound", "WM-1 boxes as real "
            "colliders, ClosestPoint after SyncTransforms", "g1e3")
        return
    bounds = S.events("s6bound")
    recs = None
    if any((d.get("tilted") or d.get("rotated")) and num(d.get("boundM")) is None and
           num(d.get("depth", d.get("depthY"))) is None and not d.get("summary") for d in probes):
        recs = wm1_records(S)
    judged, bad, unjudged, worst = 0, [], 0, (0.0, None)
    for d in probes:
        if d.get("summary"):
            continue                          # the plugin's per-run summary (records, corners, fails, pass)
        if d.get("phase") == "repose30":
            # Integrator S6: after the rigid 30 deg re-pose the plugin judges the collider against the oracle's pre-scaled
 # box turned rigidly (devPrescaledM, 1 mm); its devM is null there (S6Scene.Measure). S6.M9 reads the stale pose.
            dev = num(d.get("devPrescaledM"))
            if dev is None:
                e, g = vec(d.get("expected")), vec(d.get("got"))
                dev = math.dist(e, g) if e and g else None
            lim, how = LEVEL_TOL_M, "repose"
        else:
            dev = num(d.get("devM"))
            if dev is None:
                e, g = vec(d.get("expected")), vec(d.get("got"))
                dev = math.dist(e, g) if e and g else None
            lim, how = probe_bound(S, d, bounds, recs)
        if dev is None or lim is None:
            unjudged += 1
            continue
        judged += 1
        if dev / lim > worst[0]:
            worst = (dev / lim, d)
        if dev > lim + 1e-9:
            bad.append({"record": d.get("record"), "corner": d.get("corner"), "devM": dev, "limitM": lim, "how": how,
                        "repose": d.get("repose", d.get("phase"))})
    level = sum(1 for d in probes if not d.get("tilted") and not d.get("rotated") and not d.get("summary"))
    st = "MISSING" if not judged else ("PASS" if not bad else "FAIL")
    row(rows, "S6.G1-E3", st, "%d/%d within (worst %.2f x limit)%s" % (judged - len(bad), judged, worst[0],
                                                                       "; %d unjudged" % unjudged if unjudged else ""),
        "level <= 1 mm; tilted/rotated <= s6bound", "ClosestPoint probes on WM-1 boxes (%d level rows)" % level, "g1e3",
        bad=bad[:8])

def _applied(d):
    a = num(d.get("applied"))
    if a is None and num(d.get("hpBefore")) is not None and num(d.get("hpAfter")) is not None:
        a = num(d.get("hpBefore")) - num(d.get("hpAfter"))
    if a is None and num(d.get("effBefore")) is not None and num(d.get("effAfter")) is not None:
        a = num(d.get("effBefore")) - num(d.get("effAfter"))
    return a

def damage_rows(S, part=None):
    rows_ = S.rows("se", "s6.damage", part)
    structured = [d for d in rows_ if not d.get("_text")]
    return structured if structured else rows_

def check_n1(S, rows):
    plans = [d for d in S.rows("sn", "s6.plan") if d.get("mode") == "n1" and d.get("targets")]
    if not plans:
        for cid in ("S6.N1.small", "S6.N1.large", "S6.N1.character", "S6.N1.dup"):
            row(rows, cid, "MISSING", "no s6.plan (n1) from the stand-in host", "-", "S6.N1", "n1")
        return
    dmg = damage_rows(S)
    by_id = {}
    for d in dmg:
        by_id.setdefault(hid(d), []).append(d)
    roles = {}
    for p in plans:
        for t in p["targets"]:
            role = "character" if t.get("kind") == "character" else ("large" if int(t.get("cls") or 0) == 1 else "small")
            r = roles.setdefault(role, {"ids": [], "amounts": [], "dupIds": [], "entities": set()})
            r["ids"] += t.get("ids") or []
            r["amounts"] += [float(a) for a in t.get("amounts") or []]
            r["dupIds"] += t.get("dupIds") or []
            r["entities"].add(t.get("entity"))
    K = {"small": X.DAMAGE_SCALE["KSmall"], "large": X.DAMAGE_SCALE["KLarge"], "character": X.DAMAGE_SCALE["KChar"]}
    for role in ("small", "large", "character"):
        cid = "S6.N1.%s" % role
        r = roles.get(role)
        if not r:
            row(rows, cid, "MISSING", "no %s target in the plans" % role, "-", "S6.N1 %s" % role, "n1")
            continue
        expected = sum(r["amounts"]) * K[role]
        got, applied_ids, missing = 0.0, 0, []
        for i in r["ids"]:
            ds = by_id.get(i) or []
            if not ds:
                missing.append(i)
                continue
            a = _applied(ds[0])                       # the first application of each unique id
            got += a or 0.0
            applied_ids += 1
        rej = {}
        for d in S.rows("se", "s6.reject"):
            if hid(d) in set(r["ids"]):
                rej[str(d.get("reason"))] = rej.get(str(d.get("reason")), 0) + 1
        ok = abs(got - expected) <= N1_TOL and not missing
        row(rows, cid, "PASS" if ok else "FAIL", "delta %.4f (%d/%d ids applied)" % (got, applied_ids, len(r["ids"])),
            "%.4f = sum %.4f x k %g (+-%g)" % (expected, sum(r["amounts"]), K[role], N1_TOL),
            "%s %s: SE drop over unique ids%s" % (role, ",".join(sorted(str(e) for e in r["entities"])),
                                                  "; rejected %s" % rej if rej else ""), "n1")
    dup_ids = [i for r in roles.values() for i in r["dupIds"]]
    all_ids = [i for r in roles.values() for i in r["ids"]]
    twice = {i: len(by_id[i]) for i in set(all_ids) if len(by_id.get(i) or []) > 1}
    dup_rej = sum(1 for d in S.rows("se", "s6.reject") if str(d.get("reason")) == "Duplicate" and hid(d) in set(dup_ids))
    stats = S.rows("se", "s6.stats")
    stat_dup = max([num((d.get("rejected") or {}).get("Duplicate"), 0.0) or 0.0 for d in stats if
                    isinstance(d.get("rejected"), dict)] + [0.0])
    row(rows, "S6.N1.dup", "PASS" if not twice else "FAIL", "%d ids applied twice; %d Duplicate rejects (stats %d)" % (
        len(twice), dup_rej, stat_dup), "0 of %d duplicates applied" % len(dup_ids),
        "dedup by hit id (window 8,192)", "n1", twice=sorted(twice)[:10])

def check_n10(S, rows):
    plans = [d for d in S.rows("sn", "s6.plan") if d.get("mode") == "n10" and d.get("list")]
    if not plans:
        row(rows, "S6.N10", "MISSING", "no s6.plan (n10) from the stand-in host", "none applied, all counted",
            "record validation", "n10")
        return
    lst = [x for p in plans for x in p["list"]]
    ids = {x["id"] for x in lst}
    applied = [d for d in damage_rows(S) if hid(d) in ids]
    want = {}
    for x in lst:
        want[(x["id"], x["reason"])] = want.get((x["id"], x["reason"]), 0) + 1
    got = {}
    for d in S.rows("se", "s6.reject"):
        i = hid(d)
        if i in ids:
            got[(i, str(d.get("reason")))] = got.get((i, str(d.get("reason"))), 0) + 1
    matched = sum(min(n, got.get(k, 0)) for k, n in want.items())
    wrong = sorted({k for k in got if k not in want})
    # counted: the per-reason totals of s6.stats grew by at least the plan's numbers (rows are rate-limited)
    stats = [d for d in S.rows("se", "s6.stats") if isinstance(d.get("rejected"), dict)]
    reasons = {}
    for x in lst:
        reasons[x["reason"]] = reasons.get(x["reason"], 0) + 1
    covered = None
    if len(stats) >= 1:
        last = stats[-1]["rejected"]
        covered = all((num(last.get(r), 0.0) or 0.0) >= n for r, n in reasons.items())
    counted = matched == len(lst) or bool(covered)
    ok = not applied and counted and not wrong
    row(rows, "S6.N10", "PASS" if ok else ("MISSING" if not applied and matched == 0 and covered is None else "FAIL"),
        "%d applied, %d/%d rejects matched%s%s" % (len(applied), matched, len(lst),
                                                    "; wrong reasons %d" % len(wrong) if wrong else "",
                                                    "; stats cover the mix" if covered else ""),
        "0 applied, %d counted with their reasons" % len(lst), "malformed records (gen, epoch, amount, entity, ...)",
        "n10", wrong=[list(w) for w in wrong[:10]], mix=reasons)

def _skin_removed(d):
    # S6-2: the teardown's remove row (why=remove / op=remove, version 0, boxes 0) is not a skin to compare with
    return d.get("why") == "remove" or d.get("op") == "remove" or int(num(d.get("removed"), 0)) < 0 or         int(num(d.get("version"), 0)) <= 0

def check_skin(S, rows):
    se, census = {}, {}
    for d in S.rows("se", "s6.skin"):
        e = ident(d, "entity")
        if e is None or _skin_removed(d):
            continue
        se[e] = d
        if d.get("op") == "census" and d.get("detail"):
            census[e] = d
    sn = {}
    bad = 0
    for d in S.rows("sn", "s6.skin"):
        if d.get("bad"):
            bad += 1
        e = ident(d, "entity")
        if e is not None and not d.get("stale") and not d.get("bad") and not _skin_removed(d):
            sn[e] = d
    both = [e for e in se if e in sn]
    mism = []
    for e in both:
        a, b = se[e], sn[e]
        # the send rows carry no detail; the census row of the same version does
        if not a.get("detail") and e in census and num(census[e].get("version")) == num(a.get("version")):
            a = dict(a, detail=census[e].get("detail"))
        ha = sorted(str(x.get("hash")) for x in a.get("detail") or [])
        hb = sorted(str(x.get("hash")) for x in b.get("detail") or [])
        if int(num(a.get("version"), -1)) != int(num(b.get("version"), -2)) or \
                int(num(a.get("boxes"), -1)) != int(num(b.get("boxes"), -2)) or (ha and hb and ha != hb):
            mism.append("0x%016x" % e)
    st = "MISSING" if not both else ("PASS" if not mism and not bad else "FAIL")
    row(rows, "S6.SKIN", st, "%d entities matched, %d bad, %d mismatched" % (len(both) - len(mism), bad, len(mism)),
        "the host decodes SE's SkinSet as SE built it", "C4 type 6 through the Python twin (version, boxes, section "
        "hashes)", "skin", required=False, mismatched=mism[:5])

def _block_ok(d, g):
    # review F2: SE reports worldCellDelta = max |cell(F^-1(snWorldHit) via the live grid matrix) - cell(localHit)|; the
 # old comparison derived both sides from the same local point and could not fail
    wcd = num(d.get("worldCellDelta"))
    if wcd is not None and wcd >= 0:
        return wcd <= 1
    loc, blk = vec(d.get("local")), vec(d.get("block"))
    if loc is None or blk is None or not g:
        return None
    cell = X.hit_cell(loc, g)
    return max(abs(cell[i] - blk[i]) for i in range(3)) <= 1

def skin_info(S):
    out = {}
    for d in S.rows("se", "s6.skin"):
        e = ident(d, "entity")
        if e is not None:
            out[e] = d
    return out

def check_bt1e(S, rows):
    dmg = damage_rows(S, "bt1e")
    skins = skin_info(S)
    grid = [d for d in dmg if kind_of(d) == "grid" and abs((num(d.get("amount")) or 0) - REAPER_BITE) <= 1e-3]
    good, notes = 0, []
    for d in grid:
        info = skins.get(ident(d, "target")) or {}
        g = num(info.get("gridSize")) or (2.5 if (num(d.get("k")) or 25.0) > 1.5 else 0.5)
        a = _applied(d)
        eff = num(d.get("effBefore"))
        want = REAPER_BITE * X.DAMAGE_SCALE["KLarge"]
        if eff is not None:
            want = min(want, eff)
        blk = _block_ok(d, g)
        if a is not None and abs(a - want) <= max(N1_TOL, 1e-6 * want) and blk is not False:
            good += 1
        else:
            notes.append({"id": hid(d), "applied": a, "want": want, "blockWithin1Cell": blk})
    row(rows, "S6.B-T1e.grid", "PASS" if good and not notes else (
        "FAIL" if notes or S.spawned("bt1e", "ReaperLeviathan") else "MISSING"),
        "%d of %d reaper hits = 80 x 25 under the hit point" % (good, len(grid)), "integrity -2000 (k_large 25), "
        "block +-1 cell", "a reaper bite on a real large grid", "bt1e", bad=notes[:5])
    ch = [d for d in dmg if kind_of(d) == "character" and abs((num(d.get("amount")) or 0) - BONESHARK_BITE) <= 1e-3]
    okc = [d for d in ch if _applied(d) is not None and abs(_applied(d) - min(BONESHARK_BITE * X.DAMAGE_SCALE[
        "KChar"], num(d.get("hpBefore"), 1e9) or 1e9)) <= N1_TOL]
    row(rows, "S6.B-T1e.character", "PASS" if okc and len(okc) == len(ch) else (
        "FAIL" if ch or S.spawned("bt1e", "BoneShark") else "MISSING"),
        "%d of %d boneshark hits = 30 HP" % (len(okc), len(ch)), "health -30 (k_char 1.0)",
        "a boneshark bite on the swimming SE character (character section)", "bt1e")
    hits = [d for d in S.rows("sn", "s6.hit", "bt1e") if not d.get("skip") and not d.get("capture") and not d.get("selfTest")]
    sn_ids = {hid(d) for d in hits if hid(d)}
    se_applied = {}
    for d in damage_rows(S):
        se_applied[hid(d)] = se_applied.get(hid(d), 0) + 1
    se_rej = {hid(d) for d in S.rows("se", "s6.reject")}
    twice = [i for i, n in se_applied.items() if n > 1 and i in sn_ids]
    lost = [i for i in sn_ids if i not in se_applied and i not in se_rej]
    st = "MISSING" if not sn_ids else ("PASS" if not twice and not lost else "FAIL")
    row(rows, "S6.B-T1e.once", st, "%d Subnautica hit ids: %d applied once, %d twice, %d lost" % (
        len(sn_ids), sum(1 for i in sn_ids if se_applied.get(i) == 1), len(twice), len(lost)),
        "one DE.Damage applied per hit id", "exactly once end to end", "bt1e", lost=sorted(lost)[:8])

def check_gate_selftest(S, rows):
    gates = [d for d in S.rows("sn", "s6.gate") if not d.get("emulated")]
    refused = S.events("combat_refused")
    if not gates:
        emu = S.rows("sn", "s6.gate")
        row(rows, "S6.N2", "MISSING", "only the stand-in's emulated gate" if emu else "no s6.gate row",
            "mode=Freedom invisible=false", "Freedom assertion", "selftest", required=not emu)
    else:
        g = gates[-1]
        mode = str(g.get("mode"))
        inv = g.get("invisible")
        inv = inv if isinstance(inv, bool) else str(inv).lower() == "true"
        ok = bool(g.get("pass")) and mode.lower() in ("freedom", "nosurvival", "2") and not inv
        row(rows, "S6.N2", "PASS" if ok else "FAIL", "mode=%s invisible=%s %s%s" % (
            mode, str(inv).lower(), "PASS" if g.get("pass") else "REFUSED",
            "; combat parts refused (%d)" % len(refused) if refused else ""), "mode=Freedom invisible=false",
            "Freedom assertion (combat tests refuse otherwise)", "selftest")
    sts = [d for d in S.rows("sn", "s6.selftest") if not d.get("emulated")]
    c7 = S.events("c7")
    bit = 1 << 10
    seen_bit = any(int(num(e.get("flags"), 0) or 0) & bit for e in c7) if c7 else None
    if not sts:
        emu = S.rows("sn", "s6.selftest")
        row(rows, "S6.N9", "MISSING", "only the stand-in's emulated self-check" if emu else "no s6.selftest row",
            "1 record each, C7 bit 10", "damage self-check at start", "selftest", required=not emu)
    else:
        d = sts[-1]
        sec_n, ply_n = int(num(d.get("sectionRecords"), -1)), int(num(d.get("playerRecords"), -1))
        ok = bool(d.get("pass")) and sec_n == 1 and ply_n == 1 and seen_bit is not False
        row(rows, "S6.N9", "PASS" if ok else "FAIL", "section %d, player %d, pass %s, C7 bit 10 %s" % (
            sec_n, ply_n, bool(d.get("pass")), {None: "not sampled", True: "seen", False: "never set"}[seen_bit]),
            "exactly 1 + 1 records, C7 damageSelfTestPass", "TakeDamage(1) on a test section and the Player",
            "selftest", detail=str(d.get("detail", ""))[:120])

def check_n3(S, rows):
    paths = S.rows("sn", "s6.path", "n3")
    spawn = [d for d in S.rows("sn", "s6.spawn") if "avoidTerrainOnly" in d]
    ato = sorted({str(d.get("techType")) + "=" + str(d.get("avoidTerrainOnly")) for d in spawn})
    inside = [d for d in paths if d.get("inside") not in (None, "", False, "null")]
    row(rows, "S6.N3", "MISSING" if not paths else ("PASS" if not inside else "FAIL"),
        "%d samples, %d inside a section" % (len(paths), len(inside)), "0 samples inside",
        "peeper paths vs sections (avoidTerrainOnly %s)" % (",".join(ato[:4]) or "not logged"), "n3")

def _c8_or_damage(S, part):
    """S6a: the stand-in producer's s6.c8 rows (what Subnautica forwarded); S6b: SE's s6.damage rows."""
    c8 = S.rows("se", "s6.c8", part)
    return (c8, "C8") if c8 or S.rows("se", "s6.c8") else (damage_rows(S, part), "SE")

def check_n4(S, rows):
    recs, src = _c8_or_damage(S, "n4")
    bites = [d for d in recs if ident(d, "target") == HULL100_ID and type_of(d) == 0]
    if src == "SE":
        bites = [d for d in recs if type_of(d) == 0 and kind_of(d) == "grid"]
    geo = S.events("n4_geometry")
    rock = bool(geo and geo[-1].get("rockBetween") in (True, "owner"))
    st = "PASS" if bites and rock else ("MISSING" if not geo and not bites else "FAIL")
    row(rows, "S6.N4", st, "%d bites on the 100 m hull; rock between %s" % (len(bites), rock if geo else "unknown"),
        ">= 1 bite, hull centre under the terrain", "line of sight through sections on a long hull", "n4",
        geometry=geo[-1] if geo else None)

def check_n5(S, rows):
    recs, src = _c8_or_damage(S, "n5")
    blasts = [d for d in recs if type_of(d) == 7]
    bad, seen = [], {}
    for d in blasts:
        amt = num(d.get("amount"))
        f = flags_of(d)
        lim = CRASH_MAX
        if src == "SE":
            se_amt = num(d.get("se"))
            k = num(d.get("k"), 1.0) or 1.0
            if se_amt is not None and se_amt > CRASH_MAX * k + 1e-3:
                bad.append({"id": hid(d), "se": se_amt, "why": "se > 50 x k"})
        if amt is None or amt > lim + 1e-3 or not f & X.C8_FLAGS["Radius"]:
            bad.append({"id": hid(d), "amount": amt, "flags": f})
        key = (str(d.get("dealerId", "")), str(d.get("section")), round((num(d.get("snTime"), 0.0) or 0.0) * 2) / 2)
        seen[key] = seen.get(key, 0) + 1
    dup = [k for k, n in seen.items() if n > 1 and k[0] not in ("", "0", "None")]
    st = "PASS" if blasts and not bad and not dup else ("FAIL" if blasts or S.spawned("n5") else "MISSING")
    row(rows, "S6.N5", st, "%d DeepBlast records via %s, %d bad, %d doubled" % (len(blasts), src, len(bad), len(dup)),
        ">= 1, amount <= 50 (x k), radius flag, one per section", "crashfish blast forwarded", "n5", bad=bad[:5])

def check_n6(S, rows):
    recs, src = _c8_or_damage(S, "n6")
    shocks = [d for d in recs if type_of(d) == 8]
    bad = []
    for d in shocks:
        amt, f = num(d.get("amount")), flags_of(d)
        if amt is None or abs(amt - AMPEEL_SHOCK) > 1e-3 or not f & X.C8_FLAGS["FallbackHitPoint"]:
            bad.append({"id": hid(d), "amount": amt, "flags": f})
        if src == "SE":
            k = num(d.get("k"), 1.0) or 1.0
            se_amt = num(d.get("se"))
            if se_amt is not None and abs(se_amt - AMPEEL_SHOCK * k) > 1e-3:
                bad.append({"id": hid(d), "se": se_amt, "why": "se != 15 x k"})
    st = "PASS" if shocks and not bad else ("FAIL" if shocks or S.spawned("n6") else "MISSING")
    row(rows, "S6.N6", st, "%d DeepShock records via %s, %d bad" % (len(shocks), src, len(bad)),
        "15 (x k), fallback-hit-point flag", "ampeel shock forwarded (no dealer: OnTouch context)", "n6", bad=bad[:5])

def check_n7(S, rows):
    out, missing = {}, []
    for sub, want in N7_WANT.items():
        samples = [e for e in S.events("c7", sub) if num(e.get("sinceS"), 0.0) >= N7_SETTLE_S[sub] and
                   num(e.get("fixedDeltaTime")) is not None]
        if not samples:
            missing.append(sub)
            continue
        share = sum(1 for e in samples if abs(num(e.get("fixedDeltaTime")) - want) < 1e-4) / float(len(samples))
        out[sub] = (share, len(samples), want)
    if not out:
        row(rows, "S6.N7", "MISSING", "no C7 samples in n7.moving/still/away", "0.02 moving, 0.1 otherwise",
            "physics rate (C7 fixedDeltaTime)", "n7")
        return
    ok = all(v[0] >= N7_SHARE for v in out.values()) and "n7.moving" in out and ("n7.still" in out or
                                                                                  "n7.away" in out)
    row(rows, "S6.N7", "PASS" if ok else "FAIL", "; ".join("%s %.0f%% of %d at %g" % (k.split(".")[1], 100 * v[0], v[1],
                                                                                     v[2]) for k, v in out.items()),
        ">= %d%% at the wanted rate" % int(100 * N7_SHARE), "fixedDeltaTime 0.02 while a moving proxy is visible, 0.1 "
        "otherwise%s" % ("; no samples in %s" % ",".join(missing) if missing else ""), "n7")

def check_n8(S, rows):
    dmg = [d for d in damage_rows(S, "n8") if flags_of(d) & X.C8_FLAGS["FrameSubgrid"]]
    skins = skin_info(S)
    bad, good = [], 0
    for d in dmg:
        tgt = ident(d, "target")
        info = skins.get(tgt) or {}
        sub = info.get("subgrid")
        if sub is False or (ident(d, "root") is not None and ident(d, "root") == tgt):
            bad.append({"id": hid(d), "why": "target is not a subgrid"})
            continue
        g = num(info.get("gridSize")) or (0.5 if info.get("large") is False else 2.5)
        large = bool(info.get("large", g > 1.0))
        blk = _block_ok(d, g)
        loc = vec(d.get("local"))
        sid = ident(d, "section")
        inside = None
        if loc is not None and sid is not None:
            lo, hi = X.section_cube(sid, g, large)
            inside = all(lo[i] - g <= loc[i] <= hi[i] + g for i in range(3))
        if blk is False or inside is False:
            bad.append({"id": hid(d), "blockWithin1Cell": blk, "insideSection": inside})
        else:
            good += 1
    rej = [d for d in S.rows("se", "s6.reject") if flags_of(d) & X.C8_FLAGS["FrameSubgrid"] and
           str(d.get("reason")) in ("NoBlock", "Frame", "Section")]
    st = "MISSING" if not dmg and not rej else ("PASS" if good and not bad and not rej else "FAIL")
    row(rows, "S6.N8", st, "%d subgrid hits under the hit point, %d bad, %d rejected (%s)" % (
        good, len(bad), len(rej), ",".join(sorted({str(d.get("reason")) for d in rej})) or "-"),
        "block +-1 cell in the subgrid frame, subgrid id + flag", "rotor at 10 rpm, small-grid head", "n8", bad=bad[:5])

def mutant_rows(S, rows):
    for m in S.mutants:
        want = MUTANT_ROWS.get(m, ())
        aggs = [a for a, parts in AGGREGATES.items() if any(w in parts for w in want)]
        for r in rows:
            if r["id"] in want or r["id"] in aggs:
                r["required"] = False
                r["what"] = r["what"] + " [expected red under %s]" % m
        measured = [r for r in rows if r["id"] in want and r["status"] in ("PASS", "FAIL")]
        red = [r["id"] for r in measured if r["status"] == "FAIL"]
        st = "MISSING" if not measured else ("PASS" if len(red) == len(want) else "FAIL")
        row(rows, "%s.red" % m, st, "red: %s" % (",".join(red) or "none"), "red: %s" % ",".join(want),
            "seeded failure %s turns its rows red" % m, "mutants")

def aggregate(rows):
    for agg, parts in AGGREGATES.items():
        sub = [r for r in rows if r["id"] in parts]
        if not sub:
            continue
        sts = [r["status"] for r in sub]
        st = "FAIL" if "FAIL" in sts else ("MISSING" if "MISSING" in sts else "PASS")
        idx = rows.index(sub[0])
        rows.insert(idx, {"id": agg, "status": st, "measured": " ".join("%s:%s" % (r["id"].split(".")[-1], r["status"])
                                                                         for r in sub),
                          "threshold": "all of %s" % ",".join(p.split(".")[-1] for p in parts),
                          "what": "aggregate", "part": sub[0]["part"],
                          "required": all(r.get("required", True) for r in sub)})

PART_CHECKS = (("bt1", check_bt1), ("g1e3", check_g1e3), ("skin", check_skin), ("n1", check_n1),
               ("n10", check_n10), ("bt1e", check_bt1e), ("selftest", check_gate_selftest), ("n3", check_n3),
               ("n4", check_n4), ("n5", check_n5), ("n6", check_n6), ("n7", check_n7), ("n8", check_n8))

def evaluate(folder):
    S = Session(folder)
    rows = []
    for part, fn in PART_CHECKS:
        if not S.ran(part):
            continue
        try:
            fn(S, rows)
        except Exception as e:  # noqa: BLE001 - an analyser bug is a FAIL row, never a crash
            import traceback
            row(rows, "S6.%s" % part, "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:100] + " | " +
                traceback.format_exc().strip().splitlines()[-1][:100], part)
    aggregate(rows)
    mutant_rows(S, rows)
    end = next((e for e in reversed(S.session) if e.get("ev") == "end"), None)
    if end is not None:
        row(rows, "S6.harness", "INFO", "unsupported %s" % (",".join(end.get("unsupported") or []) or "none"), "-",
            "session plumbing: %s" % json.dumps(end.get("stats"))[:120], "session", required=False)
    return rows, S

def write_results(folder, rows, strict=True, extra=None) -> bool:
    ok = C.verdict(rows, strict)
    res = {"tool": "analyze_s6", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "s6-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "s6-table.txt"), "w", encoding="utf-8") as f:
        f.write(C.table(rows) + "\n")
    return ok

# ======================================================================================================================
# self-test: synthetic sessions written in the log contract
# ======================================================================================================================

E0 = 1792195200.0          #:00 UTC
FREQ = 10_000_000
Q0 = 10 ** 12

def _iso(t):
    import datetime
    return datetime.datetime.utcfromtimestamp(E0 + t).strftime("%Y-%m-%dT%H:%M:%S.%f") + "Z"

class _Synth:
    """One synthetic session folder: launcher events, SE rows and SN rows at session times tau (seconds)."""

    def __init__(self, folder, mode, parts, mutants=()):
        self.folder, self.mode, self.parts, self.mutants = folder, mode, list(parts), list(mutants)
        os.makedirs(folder, exist_ok=True)
        self.sess, self.se, self.sn = [], [], []
        self.se_standin = mode == "S6a"
        self.sn_standin = mode == "S6se"
        self.tau = 0.0
        self.event("start", mode=mode, parts=self.parts, mutants=self.mutants, quick=False, qpcFrequency=FREQ,
                   kind="s6")

    def event(self, ev, **kw):
        self.sess.append(dict({"utc": _iso(self.tau), "qpc": int(Q0 + self.tau * FREQ), "ev": ev}, **kw))

    def se_row(self, ev, **kw):
        if self.se_standin:
            self.se.append(dict({"t": round(Q0 / FREQ + self.tau, 6), "src": "se", "ev": ev}, **kw))
        else:
            out = {"t": _iso(self.tau), "ev": ev}
            for k, v in kw.items():
                out[k] = v
                if k in ("target", "root", "entity") and isinstance(v, int):
                    out[k + "Str"] = str(v)
            self.se.append(out)

    def sn_row(self, ev, **kw):
        if self.sn_standin:
            self.sn.append(dict({"t": round(Q0 / FREQ + self.tau, 6), "src": "sn", "ev": ev}, **kw))
        else:
            self.sn.append({"t": _iso(self.tau), "level": "info", "event": ev, "ev": ev, "data": kw})

    def part(self, name, fn, seconds):
        self.event("part", name=name, phase="begin")
        t0 = self.tau
        fn(t0)
        self.tau = t0 + seconds
        self.event("part", name=name, phase="end")
        self.tau += 1.0

    def write(self):
        self.event("end", rc=None, stats={"sent": 10}, unsupported=[])
        for name, recs in (("s6-session.jsonl", self.sess), ("se-standin.jsonl" if self.se_standin else
                                                             "se-20261017-000000.jsonl", self.se),
                           ("sn-standin.jsonl" if self.sn_standin else "sn-20261017-000000-1234.jsonl", self.sn)):
            with open(os.path.join(self.folder, name), "w", encoding="utf-8") as f:
                for r in recs:
                    f.write(json.dumps(r, default=str) + "\n")

def synth_s6a(folder, mutant=None):
    m = {mutant} if mutant else set()
    s = _Synth(folder, "S6a", ("selftest", "g1e3", "bt1", "n3", "n4", "n5", "n6", "n7", "teardown"), [mutant] if mutant
               else [])
    bt1 = "0x%016x" % X.HULL_IDS["bt1"]

    def selftest(t0):
        s.tau = t0 + 1
        s.sn_row("s6.gate", mode="Creative" if "S6.M6" in m else "Freedom", cheats=0,
                 invisible="S6.M6" in m, **{"pass": "S6.M6" not in m})
        if "S6.M6" in m:
            s.event("combat_refused", reason="gate")
        s.sn_row("s6.selftest", sectionRecords=1, playerRecords=0 if "S6.M12" in m else 1,
                 **{"pass": "S6.M12" not in m, "detail": "synthetic"})
        for i in range(10):
            s.tau = t0 + 1 + 0.1 * i
            s.event("c7", part="selftest", sinceS=0.1 * i, fixedDeltaTime=0.1,
                    flags=(1 << 3) | (0 if "S6.M12" in m else 1 << 10), capabilities=2)
    s.part("selftest", selftest, 3)

    def g1e3(t0):
        s.event("s6bound", depth=1000.0, tilt=45.0, boundM=0.08, worstM=0.078, s=1.017, samples=400)
        for i in range(40):
            s.tau = t0 + 0.05 * i
            tilted = i % 2 == 1
            dev = 0.0004 if not tilted else 0.05
            if "S6.M5" in m and tilted:
                dev = 0.6
            if "S6.M9" in m and i >= 30:
                dev = 0.9
            extra = {"boundM": 0.08} if tilted and i % 3 else ({"depth": -950.0, "tiltDeg": 130.0} if tilted else {})
            s.sn_row("s6.probe", record=i, corner=i % 8, expected=[0, 0, 0], got=[dev, 0, 0], devM=dev,
                     rotated=tilted and i % 4 == 1, tilted=tilted, repose=i >= 30, **dict(extra, **{"pass": True}))
        # the plugin's own row form (S6Scene.Measure): re-pose rows carry devM null and devPrescaledM (1 mm), tilted rows
 # their boundM and depthY; one summary row per run (integrator S6)
        for i in range(8):
            s.tau = t0 + 2.0 + 0.05 * i
            dp = 0.9 if "S6.M9" in m else 0.0003
            s.sn_row("s6.probe", record=i, corner=i, phase="repose30", expected=[1, 2, 3], got=[1 + dp, 2, 3], devM=None,
                     boundM=0.012, devPrescaledM=dp, predictedM=0.01, rotated=False, tilted=True, tiltDeg=31.0,
                     depthY=-150.0, s=1.002, **{"pass": "S6.M9" not in m})
        s.sn_row("s6.probe", summary=True, records=40, corners=48, fails=8 if "S6.M9" in m else 0,
                 **{"pass": "S6.M9" not in m})
    s.part("g1e3", g1e3, 4)

    def bt1(t0):
        for sec in range(180):
            s.tau = t0 + sec
            s.sn_row("s6.penetration", summary=True, pairs=22, maxDepthM=0.07, maxStreak=1)
            if sec % 20 == 0:
                for j in range(2):
                    s.sn_row("s6.penetration", t=t0 + sec + 0.02 * j, creature="Peeper", id=7, section="0x200801ff",
                             depthM=0.06, dir=[0, 1, 0], streak=j + 1)
            if "S6.M1" in m and sec % 10 == 0:
                for j in range(12):
                    s.sn_row("s6.penetration", t=t0 + sec + 0.02 * j, creature="BoneShark", id=9,
                             section="0x200801ff", depthM=1.4, dir=[0, 1, 0], streak=j + 1)
            s.sn_row("s6.perf", steps=50, fixedDt=0.02, registered=True, sections=0 if sec < 30 else 2,
                     shapes=0 if sec < 30 else 50, refreshes=3, created=0, proxyMsP50=0.02, proxyMsP95=0.05,
                     stepMsP50=0.4, stepMsP95=0.6, proxiesOn=sec >= 30)
        if "S6.M2" not in m:
            for i, amt in enumerate((30.0, 80.0, 30.0)):
                s.tau = t0 + 60 + 10 * i
                s.se_row("s6.c8", id="3:%d" % (i + 1), gen=3, seq=i + 1, kind="grid", target=bt1, root=bt1,
                         section="0x200801ff", type=0, seType="DeepBite", amount=amt, kExpected=25.0, flags=1,
                         local=[1.25, 2.75, -5.25], w=[0, -8, 20], dealerTechType=0, dealerId=99 + i)
                s.sn_row("s6.hit", gen=3, seq=i + 1, kind=1, target=bt1, type=0, amount=amt, flags=1, queued=True,
                         healthBefore=100, healthAfter=100)
        s.event("part", name="bt1.k", phase="begin")
        for sec in range(60):
            s.tau = t0 + 181 + sec
            s.sn_row("s6.perf", steps=50, fixedDt=0.02, registered=True, sections=8, shapes=1000, refreshes=250,
                     created=0, proxyMsP50=0.15, proxyMsP95=0.35, stepMsP50=0.7, stepMsP95=0.95, proxiesOn=True)
        s.event("part", name="bt1.k", phase="end")
    s.part("bt1", bt1, 245)

    def n3(t0):
        for i in range(60):
            s.tau = t0 + 0.5 * i
            s.sn_row("s6.path", id=10 + i % 20, techType="Peeper", pos=[0, -8, 10], inside=None)
        s.sn_row("s6.spawn", techType="Peeper", id=10, avoidTerrainOnly=False)
    s.part("n3", n3, 31)

    def n4(t0):
        s.event("n4_geometry", rockBetween=True, hullCentreY=-20.2, seabedY=-17.2)
        s.tau = t0 + 20
        s.se_row("s6.c8", id="3:50", gen=3, seq=50, kind="grid", target="0x%016x" % HULL100_ID,
                 root="0x%016x" % HULL100_ID, section="0x20080200", type=0, amount=30.0, flags=1, local=[0, 0, 8])
    s.part("n4", n4, 30)

    def n5(t0):
        s.event("reply", side="sn", cmd="de.spawn", label="n5.spawn.Crash", reply={"ok": True})
        if "S6.M10" not in m:
            s.tau = t0 + 10
            s.se_row("s6.c8", id="3:60", gen=3, seq=60, kind="grid", target=bt1, root=bt1, section="0x200801ff",
                     type=7, seType="DeepBlast", amount=31.5, flags=6, local=[0, 0, 0], dealerId=0, snTime=500.0)
            s.se_row("s6.c8", id="3:61", gen=3, seq=61, kind="grid", target=bt1, root=bt1, section="0x20080200",
                     type=7, seType="DeepBlast", amount=12.0, flags=6, local=[0, 0, 0], dealerId=0, snTime=500.0)
    s.part("n5", n5, 20)

    def n6(t0):
        s.event("reply", side="sn", cmd="de.spawn", label="n6.spawn.Shocker", reply={"ok": True})
        s.tau = t0 + 5
        s.se_row("s6.c8", id="3:70", gen=3, seq=70, kind="grid", target=bt1, root=bt1, section="0x200801ff", type=8,
                 seType="DeepShock", amount=15.0, flags=4, local=[0, 0, 0], dealerTechType=52)
    s.part("n6", n6, 20)

    def n7(t0):
        for sub, fdt in (("n7.moving", 0.02), ("n7.still", 0.1), ("n7.away", 0.1)):
            s.event("part", name=sub, phase="begin")
            for i in range(100):
                s.event("c7", part=sub, sinceS=0.1 * i, fixedDeltaTime=fdt if i >= 15 else 0.05, flags=1 << 10)
            s.tau += 11
            s.event("part", name=sub, phase="end")
    s.part("n7", n7, 34)
    s.part("teardown", lambda t0: None, 2)
    s.write()

def synth_s6se(folder, mutant=None):
    m = {mutant} if mutant else set()
    s = _Synth(folder, "S6se", ("skin", "n1", "n10", "teardown"), [mutant] if mutant else [])
    large_id, small_id, char_id = 144115188075855873, 144115188075855901, 144115188075855999
    large = X.Target(large_id, "grid", 1, 0, X.Hull("bt1").sections(X.HULL_IDS["bt1"]))
    small = X.Target(small_id, "grid", 2, 0, X.Hull("rotor").sections(X.HULL_IDS["rotorHead"]))
    char = X.Target(char_id, "character")
    seqc = [0]

    def nxt():
        seqc[0] += 1
        return seqc[0]

    def skin(t0):
        for t in (large, small):
            s.tau = t0 + 1
            det = [{"id": "0x%08x" % x["id"], "c": list(x["centre"]), "n": len(x["boxes"]), "hash": "0x%016x" % x["hash"]}
                   for x in t.sections]
            nb = sum(len(x["boxes"]) for x in t.sections)
            s.se_row("s6.skin", entity=t.entity, root=t.entity, subgrid=False, large=t.cls == 1,
                     gridSize=2.5 if t.cls == 1 else 0.5, version=1, why="first", sections=len(t.sections), boxes=nb,
                     coverage="ok", lane="NearEdit", detail=det)
            s.sn_row("s6.skin", entity="0x%016x" % t.entity, version=1, sections=len(t.sections), boxes=nb, bad=False,
                     stale=False, detail=det)
    s.part("skin", skin, 4)

    applied_stats = {"Duplicate": 0}

    def apply_plan(recs, t0):
        dedup = X.Dedup()
        for i, (rec, meta) in enumerate(recs):
            s.tau = t0 + 2 + 0.01 * i
            v = X.unpack_record(rec)
            verdict = dedup.admit(v["snGeneration"], v["dedupSeq"])
            rid = meta["id"]
            if verdict == "Duplicate" and "S6.M3" not in m:
                applied_stats["Duplicate"] += 1
                s.se_row("s6.reject", id=rid, reason="Duplicate", kind=v["targetKind"], target=v["targetEntityId"])
                continue
            kind = "character" if v["targetKind"] == 2 else "grid"
            cls = 1 if v["targetEntityId"] == large_id else 2
            k = X.factor(v["targetKind"], cls)
            if "S6.M7" in m and kind == "grid":
                k = 1.0 if cls == 1 else 25.0
            if kind == "character" and "S6.M4" in m:
                s.se_row("s6.reject", id=rid, reason="CannotDie", kind=2, target=v["targetEntityId"])
                continue
            amt = v["amount"]
            row_ = dict(id=rid, gen=v["snGeneration"], seq=v["dedupSeq"], kind=kind, target=v["targetEntityId"],
                        root=v["rootEntityId"], section="0x%08x" % v["sectionId"], flags=v["flags"], via="mouth",
                        snType=0, seType="DeepBite", amount=amt, k=k, kExpected=v["seScaleExpected"], se=amt * k,
                        applied=amt * k, local=list(v["localHit"]), tick=100 + i)
            if kind == "character":
                row_.update(hpBefore=100.0, hpAfter=100.0 - amt * k, block=None)
            else:
                g = 2.5 if cls == 1 else 0.5
                row_.update(block=list(X.hit_cell(v["localHit"], g)), effBefore=2500.0, effAfter=2500.0 - amt * k)
            s.se_row("s6.damage", **row_)

    def n1(t0):
        recs_a, plan_a = X.plan_n1([large, char], 3, 2, nxt, seed=1, unique=30, dups={large_id: 4, char_id: 3})
        s.tau = t0 + 1
        s.sn_row("s6.plan", **plan_a)
        apply_plan(recs_a, t0)
        recs_b, plan_b = X.plan_n1([small], 3, 2, nxt, seed=2, unique=30, dups=3)
        s.tau = t0 + 6
        s.sn_row("s6.plan", **plan_b)
        apply_plan(recs_b, t0 + 6)
        s.se_row("s6.stats", drained=100, applied=90, rejected={"Duplicate": applied_stats["Duplicate"]})
    s.part("n1", n1, 12)

    def n10(t0):
        recs, plan = X.plan_n10([large, small], char, 3, 2, nxt, seed=1)
        s.tau = t0 + 1
        s.sn_row("s6.plan", **plan)
        tot = {"Duplicate": applied_stats["Duplicate"]}
        for i, (rec, meta) in enumerate(recs):
            s.tau = t0 + 2 + 0.01 * i
            s.se_row("s6.reject", id=meta["id"], reason=meta["reason"], detail=meta["note"])
            tot[meta["reason"]] = tot.get(meta["reason"], 0) + 1
        s.se_row("s6.stats", drained=200, applied=90, rejected=tot)
    s.part("n10", n10, 5)
    s.part("teardown", lambda t0: None, 2)
    s.write()

def synth_s6b(folder, mutant=None):
    m = {mutant} if mutant else set()
    s = _Synth(folder, "S6b", ("selftest", "bt1e", "n8", "n5", "n6", "n7", "teardown"), [mutant] if mutant else [])
    grid, head, root2, char = 144115188075860001, 144115188075860010, 144115188075860009, 144115188075860099

    def selftest(t0):
        s.tau = t0 + 1
        s.sn_row("s6.gate", mode="Freedom", cheats=0, invisible=False, **{"pass": True})
        s.sn_row("s6.selftest", sectionRecords=1, playerRecords=1, **{"pass": True})
        s.event("c7", part="selftest", sinceS=1.0, fixedDeltaTime=0.1, flags=1 << 10)
    s.part("selftest", selftest, 3)

    def bt1e(t0):
        s.tau = t0 + 1
        s.se_row("s6.skin", entity=grid, root=grid, subgrid=False, large=True, gridSize=2.5, version=1, sections=1,
                 boxes=1, detail=[])
        s.event("reply", side="sn", cmd="de.spawn", label="bt1e.spawn.ReaperLeviathan", reply={"ok": True})
        s.event("reply", side="sn", cmd="de.spawn", label="bt1e.spawn.BoneShark", reply={"ok": True})
        hits = [(1, "grid", 80.0), (2, "character", 30.0), (3, "grid", 80.0)]
        for seq, kind, amt in hits:
            if kind == "character" and "S6.M8" in m:
                continue
            s.tau = t0 + 10 * seq
            tgt = grid if kind == "grid" else char
            s.sn_row("s6.hit", gen=4, seq=seq, kind=1 if kind == "grid" else 2, target=str(tgt), type=0, amount=amt,
                     flags=1, queued=True, via="mouth")
            d = dict(id="4:%d" % seq, gen=4, seq=seq, kind=kind, target=tgt, root=tgt, section="0x20080200" if
                     kind == "grid" else "0x00000000", flags=1, via="mouth", snType=0, seType="DeepBite", amount=amt,
                     tick=1000 + seq)
            if kind == "grid":
                eff = 2500.0 if seq == 1 else 500.0
                ap = min(eff, amt * 25.0)
                d.update(k=25.0, se=amt * 25.0, applied=ap, effBefore=eff, effAfter=eff - ap, local=[1.2, 0.3, -0.4],
                         block=[0, 0, 0])
            else:
                d.update(k=1.0, se=30.0, applied=30.0, hpBefore=100.0, hpAfter=70.0, local=[0, 0.9, 0], block=None)
            s.se_row("s6.damage", **d)
    s.part("bt1e", bt1e, 40)

    def n8(t0):
        s.tau = t0 + 1
        s.se_row("s6.skin", entity=head, root=root2, subgrid=True, large=False, gridSize=0.5, version=1, sections=2,
                 boxes=3, detail=[])
        sid = X.section_id(0, 0, 0)
        local = [1.4, 0.3, 0.1]
        if "S6.M11" in m:
            local = [6.4, 4.3, -3.2]           # left in the root grid's frame
            s.se_row("s6.reject", id="4:20", reason="NoBlock", flags=9, kind="grid", target=head, section="0x%08x" % sid)
        else:
            s.se_row("s6.damage", id="4:20", gen=4, seq=20, kind="grid", target=head, root=root2,
                     section="0x%08x" % sid, flags=9, via="mouth", snType=0, seType="DeepBite", amount=30.0, k=1.0,
                     se=30.0, applied=30.0, effBefore=100.0, effAfter=70.0, local=local,
                     block=list(X.hit_cell(local, 0.5)))
    s.part("n8", n8, 10)

    def n5(t0):
        s.event("reply", side="sn", cmd="de.spawn", label="n5.spawn.Crash", reply={"ok": True})
        if "S6.M10" not in m:
            s.tau = t0 + 5
            s.se_row("s6.damage", id="4:30", gen=4, seq=30, kind="grid", target=grid, root=grid, section="0x20080200",
                     flags=6, via="radius", snType=7, seType="DeepBlast", amount=40.0, k=25.0, se=1000.0, applied=1000.0,
                     local=[0, 0, 0], block=[0, 0, 0], snTime=12.0)
    s.part("n5", n5, 10)

    def n6(t0):
        s.tau = t0 + 5
        s.se_row("s6.damage", id="4:40", gen=4, seq=40, kind="grid", target=grid, root=grid, section="0x20080200",
                 flags=4, via="fallback", snType=8, seType="DeepShock", amount=15.0, k=25.0, se=375.0, applied=375.0,
                 local=[0, 0, 0], block=[0, 0, 0])
    s.part("n6", n6, 10)

    def n7(t0):
        for sub, fdt in (("n7.moving", 0.02), ("n7.idle", 0.02), ("n7.away", 0.1)):
            s.event("part", name=sub, phase="begin")
            for i in range(80):
                s.event("c7", part=sub, sinceS=0.1 * i, fixedDeltaTime=fdt, flags=1 << 10)
            s.tau += 9
            s.event("part", name=sub, phase="end")
    s.part("n7", n7, 20)
    s.part("teardown", lambda t0: None, 2)
    s.write()

SYNTH = {"S6a": synth_s6a, "S6se": synth_s6se, "S6b": synth_s6b}
MUTANT_MODE = {"S6.M1": "S6a", "S6.M2": "S6a", "S6.M3": "S6se", "S6.M4": "S6se", "S6.M5": "S6a", "S6.M6": "S6a",
               "S6.M7": "S6se", "S6.M8": "S6b", "S6.M9": "S6a", "S6.M10": "S6a", "S6.M11": "S6b", "S6.M12": "S6a"}

def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S6 analyzer self-test (tests/s6/analyze_s6.py --selftest)")
    root = tempfile.mkdtemp(prefix="de-s6-analyze-")
    try:
        check(X.conventions_hash() == X.PINNED_HASH, "S6 conventions twin hash 0x%016x" % X.conventions_hash())
        check(abs(iso_epoch("2026-10-17T00:00:01.1234567Z") - (E0 + 1.1234567)) < 1e-6 and
              iso_epoch("2026-10-17T02:00:00+02:00") == E0, "ISO times: 7-digit fractions, offsets")
        for mode, fn in SYNTH.items():
            folder = os.path.join(root, mode)
            fn(folder)
            rows, _S = evaluate(folder)
            bad = [(r["id"], r["status"], r["measured"]) for r in rows if r.get("required", True) and r["status"] !=
                   "PASS" and r["status"] != "INFO"]
            ids = [r["id"] for r in rows]
            check(not bad, "%s synthetic session: %d rows, all required PASS%s" % (
                mode, len(rows), (" (bad %s)" % bad) if bad else ""))
            want = {"S6a": ("S6.N2", "S6.N9", "S6.G1-E3", "S6.B-T1.1", "S6.B-T1.2", "S6.B-T1.3", "S6.N3", "S6.N4",
                            "S6.N5", "S6.N6", "S6.N7"),
                    "S6se": ("S6.SKIN", "S6.N1", "S6.N1.small", "S6.N1.large", "S6.N1.character", "S6.N1.dup",
                             "S6.N10"),
                    "S6b": ("S6.N2", "S6.N9", "S6.B-T1e", "S6.B-T1e.grid", "S6.B-T1e.character", "S6.B-T1e.once",
                            "S6.N8", "S6.N5", "S6.N6", "S6.N7")}[mode]
            check(all(i in ids for i in want), "%s: rows %s present" % (mode, ",".join(want)))
            write_results(folder, rows)
            check(os.path.exists(os.path.join(folder, "s6-table.txt")), "%s: s6-table.txt and s6-results.json" % mode)
        for mid in sorted(MUTANT_ROWS, key=lambda k: int(k.split("M")[1])):
            mode = MUTANT_MODE[mid]
            folder = os.path.join(root, "%s-%s" % (mode, mid))
            SYNTH[mode](folder, mid)
            rows, _S = evaluate(folder)
            st = {r["id"]: r["status"] for r in rows}
            red = [i for i in MUTANT_ROWS[mid] if st.get(i) == "FAIL"]
            others = [r["id"] for r in rows if r["status"] == "FAIL" and r["id"] not in MUTANT_ROWS[mid] and
                      not r["id"].endswith(".red") and r["id"] not in AGGREGATES]
            ok = len(red) == len(MUTANT_ROWS[mid]) and st.get(mid + ".red") == "PASS" and not others and \
                C.verdict(rows, True)
            check(ok, "%s: %s red, %s.red PASS, nothing else red, session verdict PASS%s" % (
                mid, ",".join(MUTANT_ROWS[mid]), mid, (" (others %s)" % others) if others else ""))
        # a mutant that did nothing: its.red row FAILs
        folder = os.path.join(root, "S6a-M1-silent")
        synth_s6a(folder)
        s = Session(folder)
        rows, _S = evaluate(folder)
        start = os.path.join(folder, "s6-session.jsonl")
        with open(start, encoding="utf-8") as f:
            lines = f.readlines()
        first = json.loads(lines[0])
        first["mutants"] = ["S6.M1"]
        lines[0] = json.dumps(first) + "\n"
        with open(start, "w", encoding="utf-8") as f:
            f.writelines(lines)
        rows, _S = evaluate(folder)
        st = {r["id"]: r["status"] for r in rows}
        check(st.get("S6.M1.red") == "FAIL" and not C.verdict(rows, True), "a silent mutant: S6.M1.red FAIL")
        # B-T1 (3) on the plugin's own perf fields (integrator S6): proxyPhysxMsP95 within / beyond 1 ms
        for direct, want_st in ((0.6, "PASS"), (2.5, "FAIL")):
            folder = os.path.join(root, "S6a-perf-%g" % direct)
            synth_s6a(folder)
            snf = [n for n in os.listdir(folder) if n.startswith("sn-")][0]
            with open(os.path.join(folder, snf), encoding="utf-8") as f:
                recs = [json.loads(x) for x in f if x.strip()]
            for r in recs:
                dd = r.get("data") or {}
                if r.get("ev") == "s6.perf" and dd.get("shapes", 0) >= PERF_SHAPES:
                    dd.update(proxyPhysxMsP95=direct, physxMsP95=direct - 0.3, stepMsP95=direct + 2.0)
            with open(os.path.join(folder, snf), "w", encoding="utf-8") as f:
                f.writelines(json.dumps(r) + "\n" for r in recs)
            rows, _S = evaluate(folder)
            st = {r["id"]: (r["status"], r["how"] if "how" in r else "") for r in rows}
            got = st.get("S6.B-T1.3", ("MISSING", ""))[0]
            check(got == want_st, "B-T1 (3) from the plugin's proxyPhysxMsP95 %.1f ms: %s (want %s)" % (direct, got, want_st))
        # the plugins' text forms
        folder = os.path.join(root, "text")
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, "se-x.jsonl"), "w", encoding="utf-8") as f:
            f.write(json.dumps({"t": "2026-10-17T00:00:01.0000000Z", "ev": "log", "msg": "DE.Damage applied id=3:7 "
                                "unit=SN_HP k=25 se=750 applied=750 target=99 kind=grid section=1 block=0,0,0 "
                                "type=DeepBite amount=30"}) + "\n")
        with open(os.path.join(folder, "sn-x.jsonl"), "w", encoding="utf-8") as f:
            f.write(json.dumps({"t": "2026-10-17T00:00:01.000Z", "event": "log", "msg": "DE.S6 freedom mode=Freedom "
                                "invisible=false PASS"}) + "\n")
        s = Session(folder)
        dm = s.rows("se", "s6.damage")
        gt = s.rows("sn", "s6.gate")
        check(dm and hid(dm[0]) == "3:7" and _applied(dm[0]) == 750 and gt and gt[0]["pass"] and gt[0]["mode"] ==
              "Freedom", "text forms: DE.Damage applied and DE.S6 freedom")
    except Exception as e:  # noqa: BLE001 - a self-test reports, never raises
        import traceback
        traceback.print_exc()
        check(False, "self-test raised %s: %s" % (type(e).__name__, e))
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results) and bool(results)
    print("S6 ANALYZE SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="S6 oracle table of a collected session folder")
    ap.add_argument("folder", nargs="?")
    ap.add_argument("--lenient", action="store_true", help="MISSING rows do not fail the verdict")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.folder:
        ap.error("folder or --selftest")
    rows, S = evaluate(a.folder)
    ok = write_results(a.folder, rows, not a.lenient, {"mode": S.mode, "parts": S.parts, "mutants": S.mutants})
    if a.json:
        print(json.dumps(rows, indent=1, default=str))
    else:
        print("S6 ORACLE TABLE (%s)  (* = not required; INFO = information)" % a.folder)
        print(C.table(rows))
    print("S6 ORACLE %s: %d rows" % ("PASS" if ok else "FAIL", len(rows)))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
