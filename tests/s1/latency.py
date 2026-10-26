"""S1 latency oracles from the two JSONL logs: ROADMAP S1.N1 (transport histogram) and S1.N2 (step response).

S1.N1: for every presented Subnautica frame, transport = qpcUsed (QPC in Camera.onPreCull of MainCamera.camera) -
qpcCapture of the newest SE snapshot that frame used (the orientation snapshot, tickRot; tickNew when tickRot is not
logged). Pass: p95 <= 25 ms over >= 10,000 frames; extrapolated frames < 1 % (with the SE tick rate reported, the
threshold assumes a steady 60 Hz SE); 0 torn snapshots accepted, i.e. no frame whose applied orientation differs from
the orientation SE published for that tick (or whose position differs from lerp(old, new, alpha) of the published
positions). Thresholds HYPOTHESIS (ship-render §6.6), as in ROADMAP.
S1.N2: 50 scripted 30 deg yaw steps; latency = qpcUsed of the first Subnautica frame whose camera yaw differs by more
than 15 deg from the yaw before the step, minus qpc(T) logged by SE when it applied the step. Pass: p95 <= 33 ms (two
SE ticks; HYPOTHESIS); p50 and p95 recorded; every step seen.
S1.M2 comparison (--baseline): the camera published from IPlugin.Update carries the previous tick's camera, stamped
with the QPC of the publication
It must shift S1.N2 by about one SE tick (16.7 ms) against the PrepareForDraw baseline: a step
p50 shift of half a tick (8.33 ms) or more is SHIFT (RED for the mutant run, the expected outcome of the seeded
failure). S1.N1's transport (qpcCapture -> qpcUsed) is NOT expected to shift by a tick, because qpcCapture is stamped
when the stale camera is published; its shift is reported as information only.

Event schema: tests/s1/check_wm.py docstring (s1.frame, s1.yawStep on the SE side; s1.cam, s1.camStats on the SN side).

 python -I tests/s1/latency.py --se SE.jsonl --sn SN.jsonl [--min-frames 10000] [--baseline BASE.json] [--json OUT]
 python -I tests/s1/latency.py --selftest
"""
from __future__ import annotations

import argparse
import json
import math
import os
import random
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import check_wm as W  # noqa: E402 (JSONL reader, schema, plan, report rows)

TICK_MS = 1000.0 / 60.0
HIST_BIN_MS = 2.0
HIST_MAX_MS = 60.0

def percentile(values, q: float):
    """Nearest-rank percentile (q in 0..100)."""
    v = sorted(values)
    if not v:
        return None
    k = max(0, min(len(v) - 1, int(math.ceil(q / 100.0 * len(v))) - 1))
    return v[k]

def histogram(values, bin_ms=HIST_BIN_MS, max_ms=HIST_MAX_MS) -> list:
    n = int(max_ms / bin_ms)
    bins = [0] * (n + 1)
    for x in values:
        i = int(x // bin_ms)
        bins[min(max(i, 0), n)] += 1
    return [{"fromMs": i * bin_ms, "toMs": (i + 1) * bin_ms if i < n else None, "count": c} for i, c in enumerate(bins)]

def hist_text(h, width=50) -> str:
    top = max((b["count"] for b in h), default=0) or 1
    lines = []
    for b in h:
        if b["count"] == 0:
            continue
        lab = ("%5.1f-%5.1f ms" % (b["fromMs"], b["toMs"])) if b["toMs"] is not None else (">= %5.1f ms   " % b["fromMs"])
        lines.append("  %s %7d %s" % (lab, b["count"], "#" * max(1, int(width * b["count"] / top))))
    return "\n".join(lines)

def yaw_deg(q) -> float:
    """Heading of the camera forward (q * +Z) in the SN horizontal plane, degrees clockwise from +Z (north)."""
    f = W.qrot(q, (0.0, 0.0, 1.0))
    return math.degrees(math.atan2(f[0], f[2]))

def cam_yaw(cam):
    """Heading of an SN frame's applied camera: from camRot, else from camFwd (yaw/pitch traces)."""
    f = W._sn_forward(cam)
    return None if f is None else math.degrees(math.atan2(f[0], f[2]))

def wrap180(a: float) -> float:
    return (a + 180.0) % 360.0 - 180.0

def qdist(a, b) -> float:
    d1 = math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))
    d2 = math.sqrt(sum((x + y) ** 2 for x, y in zip(a, b)))
    return min(d1, d2)

def transport(lg: W.Logs, p: dict, rep: W.Report) -> dict:
    c = p["n1"]
    f = lg.freq
    ms, bracket_ms, outcomes, torn_accepted, checked, mismatch = [], [], {}, 0, 0, []
    for cam in lg.cams:
        out = cam.get("outcome") or "bracketed"
        outcomes[out] = outcomes.get(out, 0) + 1
        if out == "torn":
            continue
        used = W.num(cam, "qpcUsed")
        tr = W.num(cam, "tickRot", "tickNew")
        cap = W.num(cam, "qpcCaptureRot") if W.num(cam, "tickRot") is not None else None
        if cap is None:
            cap = W.num(cam, "qpcCaptureNew")
        fr = lg.frame_by_tick.get(int(tr)) if tr is not None else None
        if cap is None and fr is not None:
            cap = W.num(fr, "qpcCapture")
        if used is not None and cap is not None:
            ms.append((used - cap) * 1000.0 / f)
        capn = W.num(cam, "qpcCaptureNew")
        if used is not None and capn is not None:
            bracket_ms.append((used - capn) * 1000.0 / f)
        # torn-accepted oracle: the applied orientation must be the one SE published for that tick, and the position
 # lerp(old, new, alpha) of the published positions (snap/held: the newest)
        if fr is not None and W._sn_forward(cam) and W.vec(fr, "camRot", n=4):
            checked += 1
            if W.vec(cam, "camRot", n=4):
                bad = qdist(W.vec(cam, "camRot", n=4), W.vec(fr, "camRot", n=4)) > 1e-5
            else:     # a yaw/pitch trace (0.001 deg resolution): compare the forward vectors
                bad = W.angle_deg(W._sn_forward(cam), W.qrot(W.vec(fr, "camRot", n=4), (0.0, 0.0, 1.0))) > 0.01
            pn = lg.frame_by_tick.get(int(W.num(cam, "tickNew"))) if W.num(cam, "tickNew") is not None else None
            po = lg.frame_by_tick.get(int(W.num(cam, "tickOld"))) if W.num(cam, "tickOld") is not None else None
            a = W.num(cam, "alpha")
            v = W.vec(cam, "camPos")
            if v and pn is not None and W.vec(pn, "camPos"):
                if out in ("snap", "held") or po is None or a is None or not W.vec(po, "camPos"):
                    e = W.vec(pn, "camPos")
                else:
                    e = W.lerp(W.vec(po, "camPos"), W.vec(pn, "camPos"), a)
                if W.dist(v, e) > 1e-3 + 1e-6 * W.norm(e):
                    bad = True
            if bad:
                torn_accepted += 1
                if len(mismatch) < 5:
                    mismatch.append({"frame": cam.get("frame"), "tickRot": tr, "outcome": out})
    frames = sum(outcomes.values())
    stats_tot = {}
    for s in lg.cam_stats:
        for k in ("frames", "bracketed", "extrapolated", "held", "snap", "torn"):
            stats_tot[k] = stats_tot.get(k, 0) + int(W.num(s, k) or 0)
    if not frames and stats_tot.get("frames"):
        frames = stats_tot["frames"]
        outcomes = {k: v for k, v in stats_tot.items() if k != "frames"}
    p50, p95, p99 = percentile(ms, 50), percentile(ms, 95), percentile(ms, 99)
    rep.check("S1.N1", "transport frames (qpcCapture -> qpcUsed)", len(ms), c["minFrames"], "n1", "%d", "", cmp="ge")
    rep.check("S1.N1", "transport p95 (p50 %s, p99 %s, max %s)" % (_ms(p50), _ms(p99), _ms(max(ms) if ms else None)),
              p95, c["p95MaxMs"], "n1", "%.2f", " ms")
    extrap = outcomes.get("extrapolated", 0)
    frac = extrap / frames if frames else None
    rep.check("S1.N1", "extrapolated frames (%d of %d)" % (extrap, frames), None if frac is None else 100.0 * frac,
              100.0 * c["extrapMaxFrac"], "n1", "%.3f", " %")
    rep.row("S1.N1", "torn snapshots accepted (%d frames checked against the SE log)" % checked,
            (W.PASS if torn_accepted == 0 else W.FAIL) if checked else W.MISSING, str(torn_accepted) if checked else
            "not paired", "0", "n1", mismatches=mismatch)
    rate = se_tick_rate(lg)
    if rate:
        steady = abs(rate["meanMs"] - TICK_MS) <= 0.5 and rate["p95Ms"] <= TICK_MS + 4.0
        rep.row("S1.N1", "SE snapshot interval mean %.3f ms, median %.3f, p95 %.3f ms (a steady 60 Hz SE: the "
                "extrapolation threshold assumes it)" % (rate["meanMs"], rate["medianMs"], rate["p95Ms"]),
                W.INFO if steady else W.FAIL, "mean %.3f ms" % rate["meanMs"], "16.67 +- 0.5, p95 <= 20.67", "n1",
                required=False)
    bp50 = percentile(bracket_ms, 50)
    if bracket_ms:
        rep.row("S1.N1", "position-bracket age p50 %s p95 %s (info: D = 1.25 ticks)" % (
            _ms(bp50), _ms(percentile(bracket_ms, 95))), W.INFO, _ms(bp50), "", "n1", required=False)
    return {"frames": frames, "samples": len(ms), "p50": p50, "p95": p95, "p99": p99, "max": max(ms) if ms else None,
            "outcomes": outcomes, "camStats": stats_tot, "tornAccepted": torn_accepted, "checked": checked,
            "histogram": histogram(ms), "seTickRate": rate, "bracketP50": bp50}

def _ms(v):
    return "-" if v is None else "%.2f ms" % v

def se_tick_rate(lg: W.Logs):
    d = []
    prev = None
    for fr in lg.frames:
        t, q = W.num(fr, "tick"), W.num(fr, "qpcCapture")
        if t is None or q is None:
            prev = None
            continue
        if prev is not None and t == prev[0] + 1:
            d.append((q - prev[1]) * 1000.0 / lg.freq)
        prev = (t, q)
    if not d:
        return None
    return {"meanMs": sum(d) / len(d), "medianMs": percentile(d, 50), "p95Ms": percentile(d, 95), "n": len(d)}

def step_response(lg: W.Logs, p: dict, rep: W.Report) -> dict:
    c = p["yaw"]
    f = lg.freq
    cams = [(W.num(x, "qpcUsed"), cam_yaw(x)) for x in lg.cams]
    cams = [(u, y) for u, y in cams if u is not None and y is not None]
    lat, missed, details = [], 0, []
    j = 0
    for st in lg.yaw:
        qt = W.num(st, "qpc")
        if qt is None:
            missed += 1
            continue
        while j < len(cams) and cams[j][0] < qt:
            j += 1
        if j == 0:
            missed += 1
            continue
        before = cams[j - 1][1]
        hit = None
        k = j
        while k < len(cams) and (cams[k][0] - qt) / f < 1.0:
            if abs(wrap180(cams[k][1] - before)) > c["detectDeg"]:
                hit = k
                break
            k += 1
        if hit is None:
            missed += 1
            details.append({"index": st.get("index"), "tick": st.get("tick"), "missed": True})
            continue
        ms = (cams[hit][0] - qt) * 1000.0 / f
        lat.append(ms)
        if len(details) < 60:
            details.append({"index": st.get("index"), "tick": st.get("tick"), "ms": round(ms, 3)})
    p50, p95 = percentile(lat, 50), percentile(lat, 95)
    rep.check("S1.N2", "yaw steps seen (of %d logged by SE)" % len(lg.yaw), len(lat), c["count"], "yaw", "%d", "",
              cmp="ge")
    rep.row("S1.N2", "missed steps (no >%.0f deg change within 1 s)" % c["detectDeg"],
            W.PASS if missed == 0 and lg.yaw else (W.MISSING if not lg.yaw else W.FAIL), str(missed), "0", "yaw")
    rep.check("S1.N2", "step response p95 (p50 %s, max %s)" % (_ms(p50), _ms(max(lat) if lat else None)), p95,
              c["p95MaxMs"], "yaw", "%.2f", " ms")
    return {"steps": len(lg.yaw), "seen": len(lat), "missed": missed, "p50": p50, "p95": p95,
            "max": max(lat) if lat else None, "details": details}

def compare(cur: dict, base: dict, rep: W.Report) -> dict:
    """S1.M2: p50 shifts against a baseline result (latency JSON or s1-results): the step response (S1.N2) is the red
 signal (SHIFT when >= half a tick); the transport shift (S1.N1) is information only (see the module docstring)."""
    b = base.get("latency", base)
    out = {}
    for key, lab, signal in (("step", "S1.N2 step p50", True), ("transport", "S1.N1 transport p50", False)):
        x, y = (cur.get(key) or {}).get("p50"), (b.get(key) or {}).get("p50")
        if x is None or y is None:
            rep.row("S1.M2", "%s shift vs baseline" % lab, W.MISSING, "-", "< %.2f ms" % (TICK_MS / 2), "m2",
                    required=False)
            continue
        shift = x - y
        out[key] = shift
        if signal:
            rep.row("S1.M2", "%s shift vs baseline (%.2f -> %.2f ms)" % (lab, y, x),
                    "SHIFT" if abs(shift) >= TICK_MS / 2 else "SAME", "%+.2f ms" % shift, "< %.2f ms" % (TICK_MS / 2),
                    "m2", required=False)
        else:
            rep.row("S1.M2", "%s shift vs baseline (%.2f -> %.2f ms; info: qpcCapture is stamped at publication, so no "
                             "one-tick shift is expected)" % (lab, y, x), W.INFO, "%+.2f ms" % shift, "info", "m2",
                    required=False)
    return out

def evaluate(se_records, sn_records, p: dict = None, min_frames=None, baseline=None, strict=True) -> dict:
    p = p or W.PLAN
    if min_frames is not None:
        p = dict(p)
        p["n1"] = dict(p["n1"], minFrames=min_frames)
    lg = W.Logs(W.s1_events(se_records), W.s1_events(sn_records))
    rep = W.Report(strict)
    res = {"tool": "latency", "qpcFrequency": lg.freq}
    res["transport"] = transport(lg, p, rep)
    res["step"] = step_response(lg, p, rep)
    if baseline:
        res["m2"] = compare(res, baseline, rep)
    res["rows"] = rep.rows
    res["ok"] = rep.ok()
    return res

# ----------------------------------------------------------------------------------------------------------------
# Self-test with synthetic logs
# ----------------------------------------------------------------------------------------------------------------

def _synthetic(shift_ticks=0, frames=12000, fps=144.0, seed=7):
    """SE at 60 Hz, Subnautica at `fps`. shift_ticks > 0 is the S1.M2 mutant as the plugin implements it: each published
 C1 carries the camera of `shift_ticks` ticks earlier, stamped with the QPC of its own publication."""
    rnd = random.Random(seed)
    F = 10_000_000
    tick_q = F / 60.0
    se, sn = [{"ev": "s1.site", "C": [0, 0, 0], "Rs": 60000.0, "qpcFrequency": F}], []

    def q_of(yaw):
        h = math.radians(yaw) / 2
        return [0.0, math.sin(h), 0.0, math.cos(h)]

    yaw = 0.0
    steps = {}
    for i in range(50):
        steps[200 + 30 * i] = (i, 30.0 if i % 2 == 0 else -30.0)
    published = {}
    tmax = int(frames / fps * 60) + 10
    for t in range(1, tmax):
        qcap = int(t * tick_q + rnd.uniform(0, 0.3) * tick_q * 0.1)
        if t in steps:
            i, d = steps[t]
            se.append({"ev": "s1.yawStep", "index": i, "tick": t, "qpc": qcap - 20000, "yawFromDeg": yaw,
                       "yawToDeg": yaw + d})
            yaw += d
        published[t] = (qcap, q_of(yaw))
        src = (qcap, published.get(t - shift_ticks, published[t])[1])
        se.append({"ev": "s1.frame", "tick": t, "qpcCapture": src[0], "camRot": src[1], "camPos": [0, 0, 0]})
    for n in range(frames):
        used = int((n / fps) * F + 2 * tick_q + 30000)
        newest = max(t for t in range(max(1, int(used / tick_q) - 2), int(used / tick_q) + 1)
                     if published.get(t) and published[t][0] <= used - 5000)
        src = (published[newest][0], published.get(newest - shift_ticks, published[newest])[1])
        sn.append({"event": "s1.cam", "frame": n, "qpcUsed": used, "tickNew": newest, "tickOld": newest - 1,
                   "tickRot": newest, "alpha": 1.0, "outcome": "bracketed", "qpcCaptureRot": src[0],
                   "qpcCaptureNew": src[0], "camRot": src[1], "camPos": [0, 0, 0]})
    return se, sn

def selftest() -> int:
    ok = True
    se, sn = _synthetic(0)
    base = evaluate(se, sn, min_frames=10000)
    print("latency selftest green: %s  transport p95 %.2f ms, step p50 %.2f p95 %.2f ms, seen %d/%d" % (
        "PASS" if base["ok"] else "FAIL", base["transport"]["p95"], base["step"]["p50"], base["step"]["p95"],
        base["step"]["seen"], base["step"]["steps"]))
    ok &= base["ok"]
    se, sn = _synthetic(1)
    mut = evaluate(se, sn, min_frames=10000, baseline=base)
    shifts = mut.get("m2", {})
    red = abs(shifts.get("step", 0.0)) >= TICK_MS / 2 and abs(shifts.get("transport", 99.0)) < TICK_MS / 2 and \
        any(r["status"] == "SHIFT" for r in mut["rows"] if r["id"] == "S1.M2")
    print("latency selftest mutant prev-tick (previous camera, stamped at publication): %s (step %+.2f ms: SHIFT; "
          "transport %+.2f ms: info)" % ("RED (expected)" if red else "GREEN (unexpected)", shifts.get("step", 0.0),
                                          shifts.get("transport", 0.0)))
    ok &= red
    # the same green data in the plugins' own log formats (SE `DE.Look dir=yaw+30... cmdQpc=`, SN `DE.CamTrace`)
    se, sn = _synthetic(0)
    pse = [{"ev": "log", "msg": "DE.Look dir=yaw+30 step=1/1 at sn=(0, 0, 0) forwardSn=(0, 0, 1) cmdTick=%d cmdQpc=%d" % (
        e["tick"], e["qpc"])} for e in se if e["ev"] == "s1.yawStep"]
    pse += [e for e in se if e["ev"] == "s1.frame"]
    psn = []
    for c in sn:
        f = W.qrot(c["camRot"], (0.0, 0.0, 1.0))
        psn.append({"event": "DE.CamTrace", "text": "DE.CamTrace f=%d qpcUsed=%d qpcFreq=10000000 newestTick=%d "
                    "newestQpc=%d presented=%d older=%d outcome=Bracketed alpha=1.0000 yaw=%.3f pitch=%.3f x=0 y=0 "
                    "z=0 fov=70" % (c["frame"], c["qpcUsed"], c["tickRot"], c["qpcCaptureRot"], c["tickNew"],
                                    c["tickOld"], math.degrees(math.atan2(f[0], f[2])),
                                    math.degrees(math.atan2(f[1], math.hypot(f[0], f[2]))))})
    pf = evaluate(pse, psn, min_frames=10000)
    same = pf["ok"] and abs(pf["step"]["p50"] - base["step"]["p50"]) < 0.01 and \
        abs(pf["transport"]["p95"] - base["transport"]["p95"]) < 0.01
    print("latency selftest plugin-format logs (DE.Look, DE.CamTrace): %s (step p50 %.2f ms, transport p95 %.2f ms)" % (
        "PASS" if same else "FAIL", pf["step"]["p50"] or -1, pf["transport"]["p95"] or -1))
    ok &= same
    se, sn = _synthetic(0)
    for c in sn[::50]:
        c["camRot"] = [0.0, 0.5, 0.0, 0.866]          # a torn copy accepted every 50 frames
    torn = evaluate(se, sn, min_frames=10000)
    t_red = torn["transport"]["tornAccepted"] > 0 and not torn["ok"]
    print("latency selftest mutant torn-accepted: %s (%d frames)" % ("RED (expected)" if t_red else
                                                                     "GREEN (unexpected)",
                                                                     torn["transport"]["tornAccepted"]))
    ok &= t_red
    print("LATENCY SELFTEST", "PASS" if ok else "FAIL")
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S1.N1 transport histogram and S1.N2 step response")
    ap.add_argument("--se", action="append", default=[])
    ap.add_argument("--sn", action="append", default=[])
    ap.add_argument("--min-frames", type=int, default=None, help="default: the plan's 10,000 (quick plan: 2,000)")
    ap.add_argument("--quick", action="store_true")
    ap.add_argument("--baseline", default=None, help="a latency JSON (or s1-results.json) of the baseline run")
    ap.add_argument("--lenient", action="store_true")
    ap.add_argument("--json", default=None)
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(sys.argv[1:] if argv is None else argv)
    if a.selftest:
        return selftest()
    if not a.se or not a.sn:
        ap.error("--se and --sn are required (or --selftest)")
    base = None
    if a.baseline:
        with open(a.baseline, encoding="utf-8") as f:
            base = json.load(f)
    r = evaluate(W.read_jsonl(a.se), W.read_jsonl(a.sn), W.plan(a.quick), a.min_frames, base, strict=not a.lenient)
    print("transport histogram (qpcCapture of the newest snapshot used -> qpcUsed):")
    print(hist_text(r["transport"]["histogram"]))
    print("outcomes: %s" % json.dumps(r["transport"]["outcomes"]))
    print(W.table(r["rows"]))
    print("LATENCY %s" % ("PASS" if r["ok"] else "FAIL"))
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(r, f, indent=1)
    return 0 if r["ok"] else 1

if __name__ == "__main__":
    sys.exit(main())
