"""S2.N7 re-dive oracle from a collected `launcher.py s2` session.

 python -I tests/s2/analyze_n7b.py tests/out/s2/<stamp> [--json]
 python -I tests/s2/analyze_n7b.py --selftest # the view machine's re-dive timeline (state_traces.py) + seeded failures

crossing, but after lingering just above the water for more than 3 s the next dive shows SE for about 0.5-1 s (the
machine had dropped Prime -> Track: "Prime quiet"; measured 533 ms from the crossing to the entry), and sometimes the

-0.20 < y <= -0.05 m, where Prime never enters Underwater; the SE plugin logs it as `DE.View band hold #N` lines).
The fix keeps Prime for 10 s without a Prime condition while y < +5 m (3 s from +5 m up; ARCHITECTURE §4.2).

The part n7b (src/DeepEngineers.Launcher/launcher.py S2Runner.p_n7b) drives SE's spectator at the grandReef spot with
`de.motion` lines at 1.5 m/s (no teleport inside the windows) and logs three windows (SE ticks of the "part" events):
 n7b.quick N re-dive cycles (10; 3 with --quick): +2 m -> -3 m -> +2 m, 2 s at +2 m between cycles
 n7b.near a pause at +2 m (6 s: Prime is then quiet for about 7.7 s, above the old 3 s and below the new 10 s),
 then one dive
 n7b.far up to +8 m, 12 s there (above +5 m the 3 s rule drops Prime to Track), then one dive from +8 m
(The bare window name "n7b" belongs to the n7 part's case (b), the 5 m/s dive of analyze_n2.py; this part never logs
it.) Source: the SE plugin's `DE.View -> <state> (<reason>) tick=... crossing->entry N ms` and `DE.View band hold`
lines (tests/s2/s2_common.py parsers). The vsm replay of the part's rows (trace on) is analyze_n2.py's S2.vsm row.

Rows (id S2.N7, part n7b.*):
 quick every quick re-dive enters Underwater <= 300 ms after the crossing, one entry per cycle (a cycle without an
 entry is the reported refusal) required
 quick Prime -> Track drops between the quick cycles INFO (0 under the 10 s rule)
 near the dive after the near pause: no Prime -> Track in the window, entry <= 300 ms required
 far Prime -> Track during the far window (3 s rule above +5 m) required
 far the dive after the Track pause: crossing -> entry, <= 1,000 ms RECORD (FAIL*, not required,
 above 1,000 ms)
 band 0 `DE.View band hold` lines during the part required
Exit code 0 when every required row is PASS.
"""
from __future__ import annotations

import argparse
import json
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import s2_common as C  # noqa: E402

try:
    import state_traces as ST  # noqa: E402 (the selftest drives the documented machine through the re-dive timeline)
except Exception:  # pragma: no cover
    ST = None

QUICK_ENTRY_MS = 300.0          # every quick re-dive (2 s at +2 m between cycles)
NEAR_ENTRY_MS = 300.0           # the dive after the near pause (Prime kept: y < +5 m)
FAR_ENTRY_MS = 1000.0           # the dive after the Track pause at +8 m (RECORD)
WINDOWS = ("n7b.quick", "n7b.near", "n7b.far")
DEFAULT_CYCLES = 10

def part_ticks(sess, part):
    """(tickBegin, tickEnd) of a launcher part window from its "part" events, or (None, None)."""
    b = next((r for r in sess.session if r.get("ev") == "part" and r.get("name") == part and r.get("phase") == "begin"),
             None)
    e = next((r for r in sess.session if r.get("ev") == "part" and r.get("name") == part and r.get("phase") == "end"),
             None)
    return (b or {}).get("tick"), (e or {}).get("tick")

def _in(e, w):
    t = C.num(e, "tick")
    return w[0] is not None and t is not None and w[0] <= t <= (w[1] if w[1] is not None else 1 << 62)

def _lat(e):
    """crossing -> entry (ms): the plugin's `crossing->entry N ms`; else the ticks from crossingTick (60 Hz; the
 stand-in SE's structured s2.state events, as analyze_n2.py's fallback)."""
    ms = C.num(e, "crossingToEntryMs")
    if ms is None and C.num(e, "crossingTick") is not None and C.num(e, "tick") is not None:
        ms = (e["tick"] - e["crossingTick"]) * 1000.0 / 60.0
    return ms

def _ms(v):
    return "-" if v is None else "%.0f ms" % v

def evaluate(sess) -> list:
    rows = []
    if "n7b" not in set(sess.parts):
        return rows
    info = next((r for r in sess.session if r.get("ev") == "n7b"), {})
    cycles = int(info.get("cycles") or DEFAULT_CYCLES)
    near_s, far_s = info.get("nearPauseS"), info.get("farPauseS")
    states = sorted((e for e in C.by(sess.se, "s2.state") if C.num(e, "tick") is not None), key=lambda e: e["tick"])
    holds = C.by(sess.se, "s2.bandhold")
    win = {k: part_ticks(sess, k) for k in WINDOWS}

    # ---------------------------------------------------------------- quick re-dives
    w = win["n7b.quick"]
    what = "re-dive: %d quick cycles +2 m -> -3 m -> +2 m at 1.5 m/s (2 s at +2 m): Underwater after the crossing" % cycles
    if w[0] is None:
        C.row(rows, "S2.N7", "MISSING", "no part window", "<= 300 ms, %d/%d" % (cycles, cycles), what, "n7b.quick")
    else:
        ent = [e for e in states if e.get("to") == "Underwater" and _in(e, w)]
        lat = [_lat(e) for e in ent]
        known = [x for x in lat if x is not None]
        slow = [x for x in known if x > QUICK_ENTRY_MS]
        if not ent:
            status = "MISSING"
        elif len(ent) < cycles or slow or len(known) < len(ent):
            status = "FAIL"
        else:
            status = "PASS"
        C.row(rows, "S2.N7", status, "%d/%d entries, max %s%s" % (
            len(ent), cycles, _ms(max(known) if known else None), ", %d > 300 ms" % len(slow) if slow else ""),
            "<= 300 ms, %d/%d" % (cycles, cycles), what, "n7b.quick", latenciesMs=lat)
        drops = [e for e in states if e.get("to") == "Track" and _in(e, w)]
        C.row(rows, "S2.N7", "INFO", "%d Prime -> Track drops" % len(drops), "0 (10 s rule)",
              "re-dive: Prime -> Track between the quick cycles (the old 3 s rule dropped there)", "n7b.quick",
              required=False)

    # ---------------------------------------------------------------- the dive after the near pause
    w = win["n7b.near"]
    what = "re-dive after %s at +2 m: Prime kept (10 s below +5 m), no Track, Underwater after the crossing" % (
        "%g s" % near_s if near_s is not None else "the near pause")
    if w[0] is None:
        C.row(rows, "S2.N7", "MISSING", "no part window", "no Track, <= 300 ms", what, "n7b.near")
    else:
        drops = [e for e in states if e.get("to") == "Track" and _in(e, w)]
        ent = [e for e in states if e.get("to") == "Underwater" and _in(e, w)]
        lat = _lat(ent[0]) if ent else None
        if not ent and not drops:
            status = "MISSING"
        elif drops or not ent or lat is None or lat > NEAR_ENTRY_MS:
            status = "FAIL"
        else:
            status = "PASS"
        C.row(rows, "S2.N7", status, "%d Track drops; entry %s" % (len(drops), _ms(lat) if ent else "none"),
              "no Track, <= 300 ms", what, "n7b.near")

    # ---------------------------------------------------------------- the far pause (Track) and its dive
    w = win["n7b.far"]
    what = "re-dive: Prime -> Track during %s at +8 m (3 s rule above +5 m)" % (
        "%g s" % far_s if far_s is not None else "the far pause")
    if w[0] is None:
        C.row(rows, "S2.N7", "MISSING", "no part window", "Track seen", what, "n7b.far")
        C.row(rows, "S2.N7", "MISSING", "no part window", "<= 1,000 ms (recorded)",
              "re-dive after a Track pause at +8 m: crossing -> Underwater", "n7b.far", required=False)
    else:
        inside = [e for e in states if _in(e, w)]
        drops = [e for e in inside if e.get("to") == "Track"]
        C.row(rows, "S2.N7", "PASS" if drops else ("MISSING" if not inside else "FAIL"),
              "%d Prime -> Track%s" % (len(drops), " at y %.2f m" % drops[0]["y"] if drops and isinstance(
                  drops[0].get("y"), (int, float)) else ""), "Track seen", what,
              "n7b.far")
        after = [e for e in inside if e.get("to") == "Underwater" and (not drops or e["tick"] > drops[-1]["tick"])]
        lat = _lat(after[0]) if after else None
        C.row(rows, "S2.N7", "MISSING" if lat is None else ("RECORD" if lat <= FAR_ENTRY_MS else "FAIL"),
              "entry %s" % _ms(lat) if after else "no Underwater entry", "<= 1,000 ms (recorded)",
              "re-dive after a Track pause at +8 m: crossing -> Underwater (Track -> Prime -> Underwater)", "n7b.far",
              required=False)

    # ---------------------------------------------------------------- band holds during the part
    spans = [win[k] for k in WINDOWS if win[k][0] is not None]
    what = "re-dive: no DE.View band hold line (Prime with -0.20 < y <= -0.05 m for > 1 s; refusal HYPOTHESIS)"
    if not spans:
        C.row(rows, "S2.N7", "MISSING", "no part window", "0 lines", what, "n7b")
    else:
        span = (min(s[0] for s in spans), max((s[1] if s[1] is not None else 1 << 62) for s in spans))
        q0 = min(q for q in (sess.window(k)[0] for k in WINDOWS) if q is not None) if any(
            sess.window(k)[0] is not None for k in WINDOWS) else None
        q1 = max((sess.window(k)[1] for k in WINDOWS if sess.window(k)[0] is not None), default=None)
        mine = [h for h in holds if _in(h, span) or (C.num(h, "tick") is None and q0 is not None and h.get("qpc") is not None
                                                     and q0 <= h["qpc"] <= q1)]
        seen = any(_in(e, span) for e in states)
        C.row(rows, "S2.N7", "PASS" if not mine and seen else ("MISSING" if not mine else "FAIL"),
              "%d lines (%d holds)" % (len(mine), len({h.get("index") for h in mine})) if (mine or seen) else
              "no DE.View line in the part (SE log missing?)", "0 lines", what, "n7b",
              detail=[{k: h.get(k) for k in ("index", "phase", "tick", "y", "heldS", "blocked", "how")} for h in mine][:6])
    return rows

# ----------------------------------------------------------------------------------------------------------- selftest
VIEW = {0: "Space", 2: "Underwater", 3: "Surfacing"}

def _state_name(vs, am):
    if vs == 1:
        return "Prime" if am == 2 else "Track"
    return VIEW.get(vs, "?")

def machine_lines(w) -> list:
    """DE.View lines as the SE plugin writes them (ViewStateDriver.LogStep) from the rows of a state_traces World."""
    first_us = {}
    for r in w.rows:
        first_us.setdefault(r["tick"], r["us"])
    out, prev = [], None
    for r in w.rows:
        cur = (r["o_vs"], r["o_am"])
        if cur != prev:
            to = _state_name(*cur)
            extra = ""
            if to == "Underwater":
                extra = " crossing->entry %s ms (presentedSeTick %d >= crossingTick %d)" % (
                    round((r["us"] - first_us.get(r["o_xt"], r["us"])) / 1000.0, 3), r["pst"], r["o_xt"])
            out.append("DE.View -> %s (%s) tick=%d y=%s vy=%s rho=%s zone=CORE snReady=%s blocked=none perf=Full/0 "
                       "request=0x%x renderSeq=%d ack=%d%s" % (
                           to, ST.REASON_NAMES.get(r["o_rsn"], "none"), r["tick"], r["y"], r["vy"], r["rho"],
                           bool(r["o_rdy"]), r["o_rq"], r["o_rs"], r["ack"], extra))
        prev = cur
    return out

def write_session(d, windows, lines, cycles, parts=("n7b",)):
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, "s2-session.jsonl"), "w") as f:
        f.write(json.dumps({"ev": "start", "parts": list(parts), "qpcFrequency": 10_000_000}) + "\n")
        for name, (t0, t1) in windows.items():
            f.write(json.dumps({"ev": "part", "name": name, "phase": "begin", "tick": t0, "qpc": t0 * 166667}) + "\n")
            f.write(json.dumps({"ev": "part", "name": name, "phase": "end", "tick": t1, "qpc": t1 * 166667}) + "\n")
        f.write(json.dumps({"ev": "n7b", "cycles": cycles, "nearPauseS": 6.0, "farPauseS": 12.0}) + "\n")
    with open(os.path.join(d, "se-20261016-000000.jsonl"), "w") as f:
        for ln in lines:
            f.write(json.dumps({"t": "2026-10-16T00:00:00.000Z", "ev": "log", "msg": ln}) + "\n")

def selftest() -> int:
    import re
    import shutil
    import tempfile
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-74s %s %s" % (label, "PASS" if cond else "FAIL", detail))

    def find(rows, part, text):
        return [r for r in rows if r["part"] == part and text in r["what"]]

    def run(name, windows, lines, cycles):
        d = os.path.join(root, name)
        write_session(d, {"n7b." + k: v for k, v in windows.items()}, lines, cycles)
        return evaluate(C.Session(d))

    def statuses(rows):
        return {(r["part"], r["what"][:40]): r["status"] for r in rows}

    root = tempfile.mkdtemp(prefix="de_n7b_")
    try:
        if ST is None:
            check("state_traces.py importable", False)
            return 1
        cyc = ST.REDIVE_CYCLES
        # green: the documented machine (vsm/3, 10 s below +5 m) through the n7b timeline
        w = ST.scenario("redive")
        good = machine_lines(w)
        r = run("green", w.windows, good, cyc)
        req_bad = [x for x in r if x.get("required", True) and x["status"] != "PASS"]
        check("machine (10 s rule): every required row PASS", not req_bad, "; ".join(
            "%s %s %s" % (x["part"], x["status"], x["measured"]) for x in req_bad) or statuses(r))
        q = find(r, "n7b.quick", "quick cycles")[0]
        check("  quick: %d/%d entries <= 300 ms" % (cyc, cyc), q["status"] == "PASS", q["measured"])
        check("  quick: 0 Track drops (INFO)", find(r, "n7b.quick", "between the quick")[0]["measured"].startswith("0 "))
        f = find(r, "n7b.far", "crossing -> Underwater")[0]
        check("  far: Track seen, entry recorded (RECORD, not required)", f["status"] == "RECORD" and not f["required"]
              and find(r, "n7b.far", "during")[0]["status"] == "PASS", f["measured"])
        check("  verdict PASS", C.verdict(r))

        # seeded: the machine before the fix (vsm/2: 3 s everywhere) -> Track near the surface
        w2 = ST.scenario("redive", version=2)
        r = run("old-rule", w2.windows, machine_lines(w2), cyc)
        n = find(r, "n7b.near", "Prime kept")[0]
        check("seeded old 3 s rule: near-pause dive red (Track in between)", n["status"] == "FAIL", n["measured"])
        check("  quick Track drops reported (INFO)", not find(r, "n7b.quick", "between the quick")[0]["measured"]
              .startswith("0 "), find(r, "n7b.quick", "between the quick")[0]["measured"])
        check("  verdict FAIL", not C.verdict(r))

        k = [i for i, ln in enumerate(good) if "crossing->entry" in ln]
        quick_entries = [i for i in k if w.windows["quick"][0] <= int(re.search(r"tick=(\d+)", good[i]).group(1)) <=
                         w.windows["quick"][1]]
        slow = list(good)
        slow[quick_entries[3]] = re.sub(r"crossing->entry [0-9.]+ ms", "crossing->entry 533 ms", slow[quick_entries[3]])
        q = find(run("slow", w.windows, slow, cyc), "n7b.quick", "quick cycles")[0]
        check("seeded slow re-dive (533 ms): quick red", q["status"] == "FAIL", q["measured"])

        # the refusal: one quick cycle without an entry, the camera held in the band
        ref = list(good)
        t = int(re.search(r"tick=(\d+)", ref[quick_entries[5]]).group(1))
        ref[quick_entries[5]] = ("DE.View band hold #1 start: Prime with y in (-0.2, -0.05] m for 1.017 s (Underwater "
                                 "needs y <= -0.2 m, Surfacing happens at y > -0.05 m) tick=%d y=-0.15 vy=0 snReady=True "
                                 "blocked=y heldS=1.017" % (t + 61))
        r = run("refusal", w.windows, ref, cyc)
        q = find(r, "n7b.quick", "quick cycles")[0]
        b = find(r, "n7b", "band hold")[0]
        check("seeded refusal (a cycle without an entry): quick red", q["status"] == "FAIL", q["measured"])
        check("seeded band hold line during the part: band row red", b["status"] == "FAIL", b["measured"])

        # the near dive slow although Prime was kept
        near_e = [i for i in k if w.windows["near"][0] <= int(re.search(r"tick=(\d+)", good[i]).group(1)) <=
                  w.windows["near"][1]]
        sn = list(good)
        sn[near_e[0]] = re.sub(r"crossing->entry [0-9.]+ ms", "crossing->entry 450 ms", sn[near_e[0]])
        n = find(run("near-slow", w.windows, sn, cyc), "n7b.near", "Prime kept")[0]
        check("seeded near-pause dive 450 ms: near red", n["status"] == "FAIL", n["measured"])

        # far: no Track (the 3 s rule above +5 m broken), and a slow far entry (RECORD row turns FAIL*, not required)
        nf = [ln for ln in good if not (ln.startswith("DE.View -> Track") and w.windows["far"][0] <= int(
            re.search(r"tick=(\d+)", ln).group(1)) <= w.windows["far"][1])]
        r = run("far-no-track", w.windows, nf, cyc)
        check("seeded far pause without Track: far row red", find(r, "n7b.far", "during")[0]["status"] == "FAIL")
        far_e = [i for i in k if w.windows["far"][0] <= int(re.search(r"tick=(\d+)", good[i]).group(1)) <=
                 w.windows["far"][1]]
        fs = list(good)
        fs[far_e[-1]] = re.sub(r"crossing->entry [0-9.]+ ms", "crossing->entry 1400 ms", fs[far_e[-1]])
        r = run("far-slow", w.windows, fs, cyc)
        f = find(r, "n7b.far", "crossing -> Underwater")[0]
        check("seeded far entry 1,400 ms: RECORD row FAIL* (not required), verdict unchanged",
              f["status"] == "FAIL" and not f["required"] and C.verdict(r), f["measured"])

        # the stand-in SE's structured s2.state events (no crossing->entry text): latency from tick - crossingTick
        d = os.path.join(root, "structured")
        write_session(d, {"n7b." + k: v for k, v in w.windows.items()}, [], cyc)
        with open(os.path.join(d, "se-20261016-000000.jsonl"), "w") as fh:
            for ln in good:
                tk = int(re.search(r"tick=(\d+)", ln).group(1))
                to = re.match(r"DE\.View -> (\w+)", ln).group(1)
                ms = re.search(r"crossing->entry ([0-9.]+) ms", ln)
                ev = {"ev": "s2.state", "tick": tk, "to": to, "y": float(re.search(r" y=(\S+)", ln).group(1))}
                if ms:
                    ev["crossingTick"] = tk - int(round(float(ms.group(1)) * 60.0 / 1000.0))
                fh.write(json.dumps(ev) + "\n")
        q = find(evaluate(C.Session(d)), "n7b.quick", "quick cycles")[0]
        check("stand-in structured events: latency from crossingTick", q["status"] == "PASS", q["measured"])

        # no SE log at all: MISSING, never PASS
        r = run("no-log", w.windows, [], cyc)
        req = [x for x in r if x.get("required", True)]
        check("no DE.View lines: every required row MISSING (not PASS)", req and all(x["status"] == "MISSING" for x in req),
              statuses(r))
        # the part not in the session: no rows
        d = os.path.join(root, "other-part")
        write_session(d, {}, good, cyc, parts=("n7",))
        check("part n7b not run: no rows", evaluate(C.Session(d)) == [])
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("ANALYZE_N7B SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S2.N7 re-dive oracle (launcher part n7b)")
    ap.add_argument("dir", nargs="?")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.dir:
        ap.error("a session folder is required")
    rows = evaluate(C.Session(a.dir))
    ok = C.write_results(a.dir, "n7b", rows)
    print(json.dumps(rows) if a.json else C.table(rows))
    print("ANALYZE_N7B %s (%d rows)" % ("PASS" if ok else "FAIL", len(rows)))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
