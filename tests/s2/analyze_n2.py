"""S2 state-trace oracle: S2.N2 (hysteresis, crossing order, SE view beyond 7,900 m) and S2.N7 (warm-up gating, entry
within 0.5 s of the crossing) from a collected `launcher.py s2` session (ROADMAP S2 oracle).

 python -I tests/s2/analyze_n2.py tests/out/s2/<stamp> [--json]
 python -I tests/s2/analyze_n2.py --selftest # wire-s2's scenario generator + seeded failures (S2.M4, S2.M10...)

Sources, best first:
1. The SE plugin's "vsm" rows (one per view-machine step, src/DeepEngineers.Wire/ViewTrace.cs) analysed with wire-s2's
 independent oracle tests/s2/state_traces.py (reused, not re-implemented): replay of the documented machine,
 crossing order (margins on and presented before the scene is skipped; scene drawn and presented before the margins
 drop), zone ("SE view beyond 7,900 m"), the S2.N2 switch metrics and the crossing -> Underwater latency.
2. Fallback without "vsm" rows: the plugin's `DE.View A -> B tick=... crossingTick=... snReady=` lines and
 `DE.Display margins ON|OFF` lines (tests/s2/s2_common.py parsers), plus the launcher's 50 Hz C5 samples
 (viewState, zone, flags) as an independent, coarser trace. Rows from this path say "(fallback)".
Part windows (SE ticks from the launcher's "part" events: C1 tick at begin and end): n2a (bob +-0.05 m around
-0.125 m, 30 s), n2b (+-0.1 m at 5 Hz, 30 s), n2z (out past 7,900 m and back), n7a (an Approach phase, then the dive),
n7b (a dive at 5 m/s).
Thresholds: (a) 0 switches; (b) switches >= 0.25 s apart and <= 4 in any 1 s; crossing order 0 violations; beyond
7,900 m never Underwater; S2.N7 (b) Underwater entered <= 0.5 s after the crossing tick; S2.N7 (a) every Underwater
entry with snReady true (never a half-streamed Subnautica).
Review fixes: the zone exit must be exercised from Underwater (finding 7: the run starts at 7,750 m, inside
the Rho guard; a run that never was Underwater before 7,900 m is MISSING, not PASS) and leave Underwater on the first
FAR tick; the composed video of the exit (video-n2z, the margins-off transition from Underwater; finding 5) must have
no black frame.
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
    import state_traces as ST  # noqa: E402 (wire-s2's oracle; optional)
except Exception:  # pragma: no cover - the fallback path stays usable without it
    ST = None

N7B_ENTRY_S = 0.5
DWELL_S = 0.25
MAX_PER_S = 4

def _video_summary(sess, name):
    p = os.path.join(sess.dir, name, "video-summary.json")
    if not os.path.exists(p):
        return None
    with open(p, encoding="utf-8") as f:
        return json.load(f)

def part_ticks(sess, part):
    """(tickBegin, tickEnd) of a launcher part from its "part" events, or (None, None)."""
    b = next((r for r in sess.session if r.get("ev") == "part" and r.get("name") == part and r.get("phase") == "begin"),
             None)
    e = next((r for r in sess.session if r.get("ev") == "part" and r.get("name") == part and r.get("phase") == "end"),
             None)
    return (b or {}).get("tick"), (e or {}).get("tick")

def _switch_metrics_s(times_s):
    gaps = [b - a for a, b in zip(times_s, times_s[1:])]
    mx = 0
    j = 0
    for k in range(len(times_s)):
        while times_s[k] - times_s[j] >= 1.0:
            j += 1
        mx = max(mx, k - j + 1)
    return {"switches": len(times_s), "minSpacingS": min(gaps) if gaps else None, "maxPerSecond": mx}

def _n2_ok(case, m):
    if case == "a":
        return m["switches"] == 0
    return (m["minSpacingS"] is None or m["minSpacingS"] >= DWELL_S) and m["maxPerSecond"] <= MAX_PER_S

def _m_text(m):
    return "%d switches, min spacing %s s, max %d/s" % (m["switches"], "-" if m["minSpacingS"] is None else
                                                         "%.3f" % m["minSpacingS"], m["maxPerSecond"])

def evaluate(sess) -> list:
    rows = []
    parts = set(sess.parts)
    vsm = sess.vsm
    use_vsm = bool(vsm) and ST is not None
    states = sorted((e for e in C.by(sess.se, "s2.state") if C.num(e, "tick") is not None), key=lambda e: e["tick"])
    if use_vsm:
        rep = ST.replay(vsm)
        C.row(rows, "S2.vsm", "PASS" if rep["pass"] else "FAIL", "%d rows, %d+%d mismatches" % (
            rep["rows"], rep["localMismatch"], rep["linkMismatch"]), "0 mismatches",
            "replay of the documented view machine (state_traces.py)", "all", detail=rep["first"][:4])
        od = ST.check_order(vsm)
        C.row(rows, "S2.N2", "PASS" if od["pass"] else "FAIL", "%d entries, %d exits, %d violations" % (
            od["enterChecked"], od["leaveChecked"], od["violations"]), "0 violations",
            "crossing order: margins presented before the skip; scene presented before margins off", "order",
            detail=od["first"][:4])
    else:
        C.row(rows, "S2.vsm", "MISSING" if ST is not None else "OTHER",
              "no vsm rows in se-*.jsonl" if ST is not None else "state_traces.py not importable", "vsm rows",
              "replay of the documented view machine", "all", required=False)
        # fallback order check: before every Underwater entry the latest margins event is ON
        disp = sorted((e for e in C.by(sess.se, "s2.display") if e.get("action") in ("marginsOn", "marginsOff")),
                      key=lambda e: e["_i"])
        entries = [e for e in states if e.get("to") == "Underwater"]
        bad = 0
        for e in entries:
            prev = [d for d in disp if d["_i"] < e["_i"]]
            if not prev or prev[-1]["action"] != "marginsOn":
                bad += 1
        C.row(rows, "S2.N2", "MISSING" if not entries else ("PASS" if bad == 0 else "FAIL"),
              "%d entries, %d without margins on before (fallback)" % (len(entries), bad) if entries else
              "no Underwater entry logged", "0 violations",
              "crossing order (fallback: DE.Display margins before DE.View -> Underwater)", "order")
    # ---------------------------------------------------------------- N2 (a) and (b)
    for case, part in (("a", "n2a"), ("b", "n2b")):
        if "n2" not in parts:
            break
        t0, t1 = part_ticks(sess, part)
        if t0 is None:
            C.row(rows, "S2.N2", "MISSING", "no part window", "-", "trace (%s)" % case, part)
            continue
        if use_vsm:
            m = ST.n2_metrics(ST.switches(vsm, t0, t1))
            src = "vsm"
        else:
            # fallback: DE.View transitions into or out of Underwater inside the window (tick / 60 s)
            times = [e["tick"] / 60.0 for e in states if t0 <= e["tick"] <= (t1 or 1 << 62) and
                     (e.get("to") == "Underwater") != (e.get("from") == "Underwater")]
            m = _switch_metrics_s(times)
            src = "DE.View (fallback)"
        thr = "0 switches" if case == "a" else ">= 0.25 s apart, <= 4/s"
        what = ("trace (a): bob +-0.05 m around -0.125 m (inside the band) for 30 s" if case == "a" else
                "trace (b): bob +-0.1 m around -0.125 m at 5 Hz for 30 s")
        C.row(rows, "S2.N2", "PASS" if _n2_ok(case, m) else "FAIL", _m_text(m) + " [%s]" % src, thr, what, part)
        # independent coarse cross-check: the launcher's C5 samples
        smp = sess.samples(part)
        vs = [(s["qpc"], C.view_name((s.get("c5") or {}).get("viewState"))) for s in smp if s.get("c5")]
        times = [q / float(sess.qpcf) for (q, v), (_q0, v0) in zip(vs[1:], vs) if (v == "Underwater") != (v0 == "Underwater")]
        if vs:
            mc = _switch_metrics_s(times)
            C.row(rows, "S2.N2", "INFO", _m_text(mc), "cross-check", "trace (%s) from the launcher's C5 samples "
                  "(%d samples, coarse)" % (case, len(vs)), part, required=False)
    # ---------------------------------------------------------------- zone exit
    if "n2" in parts:
        t0, t1 = part_ticks(sess, "n2z")
        if use_vsm:
            win = [r for r in vsm if t0 is None or t0 <= r["tick"] <= (t1 or 1 << 62)]
            zn = ST.check_zone(win)
            C.row(rows, "S2.N2", "PASS" if zn["pass"] else "FAIL", "%d rows, %d FAR/off-site, %d violations" % (
                zn["checked"], zn["beyond7900"], zn["violations"]), "never Underwater beyond 7,900 m",
                "zone exit: SE's own view beyond 7,900 m", "n2z", detail=zn["first"][:4])
            seen_far = zn["beyond7900"] > 0
        else:
            smp = sess.samples("n2z")
            far = [s for s in smp if (s.get("c5") or {}).get("zone") in (3, 4)]
            bad = [s for s in far if C.view_name((s.get("c5") or {}).get("viewState")) == "Underwater"]
            seen_far = bool(far)
            C.row(rows, "S2.N2", "MISSING" if not smp else ("PASS" if not bad else "FAIL"),
                  "%d samples FAR, %d of them Underwater (fallback)" % (len(far), len(bad)) if smp else "no samples",
                  "never Underwater beyond 7,900 m", "zone exit: SE's own view beyond 7,900 m", "n2z")
        if not seen_far:
            C.row(rows, "S2.N2", "FAIL", "zone FAR never reached in n2z", "FAR reached",
                  "the zone-exit run went beyond 7,900 m", "n2z")
        # review fix (finding 7): the exit must start from Underwater, else the row above cannot fail
        what = "zone exit exercised: Underwater before 7,900 m, Surfacing on the first tick beyond"
        if use_vsm:
            ze = ST.zone_exit(win)
            C.row(rows, "S2.N2", "PASS" if ze["pass"] else ("FAIL" if ze["exercised"] else "MISSING"),
                  "Underwater before: %s; first FAR tick %s: %s; Space %s ticks later" % (
                      ze["underwaterBefore"], ze["exitTick"], "Surfacing at once" if ze["atOnce"] else (
                          "not left at once" if ze["exercised"] else "not Underwater there"), ze["ticksToSpace"]),
                  "exercised and at once", what, "n2z", detail=ze)
        else:
            smp = sorted(sess.samples("n2z"), key=lambda x: x.get("qpc") or 0)
            first_far = next((k for k, x in enumerate(smp) if (x.get("c5") or {}).get("zone") in (3, 4)), None)
            uw_before = first_far is not None and any(
                C.view_name((x.get("c5") or {}).get("viewState")) == "Underwater" for x in smp[:first_far])
            C.row(rows, "S2.N2", "PASS" if uw_before else "MISSING",
                  "Underwater sample before the first FAR sample: %s (fallback, 10 Hz)" % uw_before,
                  "exercised", what, "n2z")
        pre = next((r for r in sess.session if r.get("ev") == "n2z" and "underwaterBeforeExit" in r), None)
        if pre is not None:
            C.row(rows, "S2.N2", "INFO", "Underwater %s at 7,750 m (waited %s s)" % (
                "reached" if pre.get("underwaterBeforeExit") else "NOT reached", pre.get("waitS")), "launcher",
                "zone-exit precondition seen by the launcher (C5 view before the path out)", "n2z", required=False)
        # review fix (finding 5): the margins-off transition from Underwater, recorded as a composed video
        start = next((r for r in sess.session if r.get("ev") == "start"), {})
        st_in = start.get("standin")
        standin = bool(st_in.get("sn") or st_in.get("se")) if isinstance(st_in, dict) else bool(st_in)
        summ = _video_summary(sess, "video-n2z")
        if summ is None:
            skipped = next((r for r in sess.session if r.get("ev") == "video" and r.get("name") == "video-n2z" and
                            r.get("skipped")), None)
            C.row(rows, "S2.N2", "OTHER" if (standin or skipped) else "MISSING",
                  "no video-n2z (%s)" % ((skipped or {}).get("skipped") or ("stand-in run" if standin else "not recorded")),
                  "0 black frames", "zone exit from Underwater: no black frame in the composed video", "n2z",
                  required=not (standin or skipped))
        else:
            blacks, n = summ.get("blackCount", 0), summ.get("frames", 0)
            C.row(rows, "S2.N2", "MISSING" if not n else ("PASS" if blacks == 0 else "FAIL"),
                  "%d black of %d frames" % (blacks, n), "0 black frames",
                  "zone exit from Underwater: no black frame in the composed video (margins off after the scene)", "n2z")
    # ---------------------------------------------------------------- N7
    if "n7" in parts:
        for case, part in (("b", "n7b"), ("a", "n7a")):
            t0, t1 = part_ticks(sess, part)
            if use_vsm:
                win = [r for r in vsm if t0 is None or t0 <= r["tick"] <= (t1 or 1 << 62)]
                entries = [r for r in win if r["o_rsn"] == ST.R_ENTER_UW]
                lat = ST.entry_latency(win) if case == "b" else None
                ready = [bool(r["o_rdy"]) for r in entries]
            else:
                win = [e for e in states if t0 is None or t0 <= e["tick"] <= (t1 or 1 << 62)]
                entries = [e for e in win if e.get("to") == "Underwater"]
                lat = []
                for e in entries:
                    ms = C.num(e, "crossingToEntryMs")
                    if ms is None and C.num(e, "crossingTick") is not None:
                        ms = (e["tick"] - e["crossingTick"]) * 1000.0 / 60.0
                    if ms is not None:
                        lat.append(ms)
                ready = [e.get("snReady") is True for e in entries]
            if case == "b":
                C.row(rows, "S2.N7", "MISSING" if not lat else ("PASS" if max(lat) <= N7B_ENTRY_S * 1000 else "FAIL"),
                      "%d entries, max %.0f ms" % (len(lat), max(lat)) if lat else "no Underwater entry in the dive",
                      "<= 500 ms", "(b) dive at 5 m/s: Underwater entered after the crossing", part)
            else:
                C.row(rows, "S2.N7", "MISSING" if not entries else ("PASS" if all(ready) else "FAIL"),
                      "%d entries, %d with snReady" % (len(entries), sum(ready)) if entries else "no entry",
                      "every entry snReady", "(a) after an Approach phase: never a half-streamed Subnautica", part)
    return rows

# ----------------------------------------------------------------------------------------------------------- selftest
def _write_session(d, parts, windows, vsm_rows=(), se_lines=(), samples=()):
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, "s2-session.jsonl"), "w") as f:
        f.write(json.dumps({"ev": "start", "parts": parts, "qpcFrequency": 10_000_000}) + "\n")
        for name, (t0, t1) in windows.items():
            f.write(json.dumps({"ev": "part", "name": name, "phase": "begin", "tick": t0, "qpc": t0 * 166667}) + "\n")
            f.write(json.dumps({"ev": "part", "name": name, "phase": "end", "tick": t1, "qpc": t1 * 166667}) + "\n")
        for s in samples:
            f.write(json.dumps(s) + "\n")
    with open(os.path.join(d, "se-20261012-000000.jsonl"), "w") as f:
        for r in vsm_rows:
            f.write(json.dumps(r, separators=(",", ":")) + "\n")
        for ln in se_lines:
            f.write(json.dumps({"t": "2026-10-12T00:00:00.000Z", "ev": "log", "msg": ln}) + "\n")

def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-70s %s %s" % (label, "PASS" if cond else "FAIL", detail))

    def find(rows, cid, text):
        return [r for r in rows if r["id"] == cid and text in r["what"]]
    root = tempfile.mkdtemp(prefix="de_n2_")
    try:
        if ST is None:
            check("state_traces.py importable (wire-s2's oracle)", False)
        else:
            # vsm path, one session per scenario (each scenario starts at tick 1000)
            def run(name, mutants, parts, windows):
                d = os.path.join(root, "%s-%d" % (name, mutants))
                w = ST.scenario(name, mutants)
                _write_session(d, parts, windows, w.rows)
                return evaluate(C.Session(d))
            r = run("n2a-above", 0, ["n2"], {"n2a": (1320, 3120), "n2z": (1000, 1000)})
            check("vsm: trace (a) green (0 switches)", find(r, "S2.N2", "trace (a)")[0]["status"] == "PASS")
            check("vsm: replay of the documented machine green", find(r, "S2.vsm", "replay")[0]["status"] == "PASS")
            r = run("n2a-above", ST.MUT_M4, ["n2"], {"n2a": (1320, 3120), "n2z": (1000, 1000)})
            a = find(r, "S2.N2", "trace (a)")[0]
            check("seeded S2.M4 (no hysteresis, no dwell): trace (a) red", a["status"] == "FAIL", a["measured"])
            r = run("n2b", 0, ["n2"], {"n2b": (1120, 2920), "n2z": (1000, 1000)})
            b = find(r, "S2.N2", "trace (b)")[0]
            check("vsm: trace (b) green (>= 0.25 s apart, <= 4/s)", b["status"] == "PASS", b["measured"])
            r = run("n2b", ST.MUT_M4, ["n2"], {"n2b": (1120, 2920), "n2z": (1000, 1000)})
            b = find(r, "S2.N2", "trace (b)")[0]
            check("seeded S2.M4: trace (b) spacing red", b["status"] == "FAIL", b["measured"])
            r = run("zone-exit", 0, ["n2"], {"n2z": (1000, 2440)})
            z = find(r, "S2.N2", "zone exit: SE")[0]
            check("vsm: zone exit green, FAR reached", z["status"] == "PASS" and not find(r, "S2.N2", "went beyond"),
                  z["measured"])
            ze = find(r, "S2.N2", "zone exit exercised")[0]
            check("vsm: zone exit exercised from Underwater, Surfacing at once", ze["status"] == "PASS", ze["measured"])
            r = run("zone-exit-far", 0, ["n2"], {"n2z": (1000, 1780)})
            ze = find(r, "S2.N2", "zone exit exercised")[0]
            check("seeded finding 7: a run from 7,850 m (never Underwater) -> MISSING, not PASS", ze["status"] == "MISSING",
                  ze["measured"])
            # a zone exit that stays Underwater one tick past 7,900 m: tamper the first FAR row
            zr = [dict(x) for x in ST.scenario("zone-exit").rows]
            k = next(n for n, x in enumerate(zr) if x["zone"] == ST.Z_FAR)
            zr[k]["o_vs"], zr[k]["o_rsn"] = ST.UNDERWATER, 0
            dz = os.path.join(root, "zone-late")
            _write_session(dz, ["n2"], {"n2z": (1000, 2440)}, zr)
            ze = find(evaluate(C.Session(dz)), "S2.N2", "zone exit exercised")[0]
            check("seeded: Underwater kept on the first FAR tick -> red", ze["status"] == "FAIL", ze["measured"])
            # the composed video of the exit (finding 5)
            os.makedirs(os.path.join(dz, "video-n2z"), exist_ok=True)
            with open(os.path.join(dz, "video-n2z", "video-summary.json"), "w") as f:
                json.dump({"frames": 3000, "blackCount": 2}, f)
            v = find(evaluate(C.Session(dz)), "S2.N2", "no black frame")[0]
            check("seeded: black frames at the zone-exit margins-off -> red", v["status"] == "FAIL", v["measured"])
            r = run("dive5", 0, ["n7"], {"n7b": (1000, 2260), "n7a": (1000, 2260)})
            n7b = find(r, "S2.N7", "(b)")[0]
            check("vsm: N7 (b) entry within 0.5 s of the crossing", n7b["status"] == "PASS", n7b["measured"])
            check("vsm: N7 (a) every entry with snReady", find(r, "S2.N7", "(a)")[0]["status"] == "PASS")
            r = run("dive5", ST.MUT_M10, ["n7"], {"n7b": (1000, 2260), "n7a": (1000, 2260)})
            n7b = find(r, "S2.N7", "(b)")[0]
            check("seeded S2.M10 (snReady gated on IsWorldSettled): N7 (b) delayed or never -> red",
                  n7b["status"] in ("FAIL", "MISSING"), n7b["measured"])
        # fallback path: DE.View lines (no vsm rows)
        good = ["DE.Display margins ON (-1) hr=0x00000000 S_OK label=prime presents=10",
                "DE.View Prime -> Underwater tick=1300 y=-0.21 vy=-0.5 rho=10 snReady=True zOrderOk=True "
                "marginsReady=True crossingTick=1290 presentedSeTick=1299 (enter Underwater) crossing->entry 166.7 ms",
                "DE.View Underwater -> Surfacing tick=1400 y=-0.04 vy=0.5 rho=10 snReady=True zOrderOk=True "
                "marginsReady=True crossingTick=1290 presentedSeTick=1399 (surface: waterline)",
                "DE.View Surfacing -> Prime tick=1401 y=-0.04 vy=0.5 rho=10 snReady=True zOrderOk=True marginsReady=True "
                "crossingTick=1290 presentedSeTick=1400 (surfaced to Prime)"]
        d = os.path.join(root, "fallback")
        _write_session(d, ["n2", "n7"], {"n2a": (2000, 3800), "n2b": (1200, 1500), "n2z": (4000, 4100),
                                         "n7b": (1200, 1500), "n7a": (1200, 1500)}, se_lines=good,
                       samples=[{"ev": "sample", "part": "n2z", "qpc": 4000 * 166667 + i, "c5": {"zone": 2, "viewState": 2}}
                                for i in range(3)] +
                               [{"ev": "sample", "part": "n2z", "qpc": 4000 * 166667 + 10 + i, "c5": {"zone": 3, "viewState": 0}}
                                for i in range(5)])
        os.makedirs(os.path.join(d, "video-n2z"), exist_ok=True)
        with open(os.path.join(d, "video-n2z", "video-summary.json"), "w") as f:
            json.dump({"frames": 3000, "blackCount": 0}, f)
        r = evaluate(C.Session(d))
        bad = [x for x in r if x.get("required", True) and x["status"] in C.BAD]
        check("fallback (DE.View lines): every required row PASS", not bad, "; ".join(
            "%s %s %s" % (x["id"], x["status"], x["measured"]) for x in bad))
        slow = [ln.replace("crossing->entry 166.7 ms", "crossing->entry 800.0 ms") for ln in good]
        d2 = os.path.join(root, "fallback-slow")
        _write_session(d2, ["n7"], {"n7b": (1200, 1500), "n7a": (1200, 1500)}, se_lines=slow)
        r = evaluate(C.Session(d2))
        check("fallback seeded: entry 800 ms after the crossing -> N7 (b) red",
              find(r, "S2.N7", "(b)")[0]["status"] == "FAIL")
        d3 = os.path.join(root, "fallback-order")
        _write_session(d3, ["n7"], {"n7b": (1200, 1500), "n7a": (1200, 1500)}, se_lines=good[1:])
        r = evaluate(C.Session(d3))
        check("fallback seeded: Underwater without margins on before -> order red",
              find(r, "S2.N2", "crossing order")[0]["status"] == "FAIL")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("ANALYZE_N2 SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S2 state-trace oracle (N2, N7)")
    ap.add_argument("dir", nargs="?")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.dir:
        ap.error("a session folder is required")
    rows = evaluate(C.Session(a.dir))
    ok = C.write_results(a.dir, "n2", rows)
    print(json.dumps(rows) if a.json else C.table(rows))
    print("ANALYZE_N2 %s (%d rows)" % ("PASS" if ok else "FAIL", len(rows)))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
