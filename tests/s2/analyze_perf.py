"""S2 performance oracle: S2.N3 (idle cost in Space), S2.N4 (see-through GPU), S2.N8 (covered render, real), S2.N11
(Track cost), and the S2.N7 (a) SE frame hitch during Prime (ROADMAP S2 oracle).

 python -I tests/s2/analyze_perf.py tests/out/s2/<stamp> [--baseline SE_ALONE_DIR_OR_RESULTS_JSON] [--json]
 python -I tests/s2/analyze_perf.py --selftest # synthetic session, then the seeded failures red

Inputs of a collected session folder (`launcher.py s2`):
- `measure-<part>.csv` from tools/measure/measure.ps1 (1 s samples: cpu_pct of all logical cores, gpu3d_pct of the
 pid's 3D engines; names Legacy and Subnautica);
- `s2-session.jsonl` "sample" events: the launcher's read-only section reads (C0 snHeartbeatMs, C1 tick, C5 view state,
 approach mode and flags, C7 snFrame, presentedSeTick, perfModeApplied, vSyncCountApplied, targetFrameRateApplied)
 at 10 Hz, each tagged with the part (n3, n4.above, n4.under, n8.on, n8.off, n11.hover, n11.sail, n7a...);
- se-*.jsonl `s2.perf` (frameMsP50, frameMsMax, gpuFrameMs = SE STATISTICS FrameTimeGPU, simRatio) and the "part"
 begin/end events (QPC) to cut them.
Thresholds:
- S2.N3: Subnautica frame rate (snFrame deltas over the hold) <= 11 fps; GPU < 5 %; CPU <= 7 % (the covered-idle level
 C7 (vSyncCountApplied, targetFrameRateApplied) = (0, 10) in every sample; SE
 FrameTimeGPU within 5 % of SE alone (needs --baseline: an SE-alone session of the same part, `launcher.py s2
 --se-alone`; MISSING without it).
- S2.N4: SE per-process GPU % underwater <= 0.70 x the same spot above water (at least 30 % lower).
- S2.N8 (margins on): snFrame >= 25 fps in every full 1 s window, no heartbeat gap > 1 s (snHeartbeatMs unchanged or
 jumping by more than 1,000 ms between samples), presentedSeTick age (C1 tick - presentedSeTick) <= 4 ticks on
 >= 99 % of the samples; margins off: the same numbers RECORDed (O9).
- S2.N11: SE frame time p50 within 5 % of SE alone at the same spots (--baseline; MISSING without it); Prime duty
 cycle RECORD (share of samples in Prime).
- S2.N7 (a): SE frameMsMax < 50 ms in every s2.perf record during Prime.
Exit code 0 when every required row is PASS.
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import s2_common as C  # noqa: E402

N3_FPS_MAX, N3_GPU_MAX, N3_CPU_MAX = 11.0, 5.0, 7.0
N4_RATIO_MAX = 0.70
N8_FPS_MIN, N8_GAP_MS, N8_AGE_TICKS, N8_AGE_SHARE = 25.0, 1000.0, 4, 0.99
BASELINE_TOL = 0.05
HITCH_MS = 50.0

def read_measure(path) -> dict:
    """{process name: [rows]} of a measure.ps1 CSV."""
    out = {}
    if not path or not os.path.exists(path):
        return out
    with open(path, encoding="utf-8-sig", newline="") as f:
        for r in csv.DictReader(f):
            out.setdefault(r.get("name", "?"), []).append(r)
    return out

def _floats(rows, key):
    v = []
    for r in rows or ():
        try:
            if r.get(key) not in (None, ""):
                v.append(float(r[key]))
        except ValueError:
            pass
    return v

def mean(v):
    return sum(v) / len(v) if v else None

def sample_fps(samples):
    """(overall fps, min fps over full 1 s windows) from C7 snFrame and QPC of the launcher samples."""
    pts = [(s["qpc"], (s.get("c7") or {}).get("snFrame")) for s in samples if (s.get("c7") or {}).get("snFrame") is not None]
    if len(pts) < 2:
        return None, None
    f = samples[0].get("qpcFrequency") or C.qpc_frequency()
    total = (pts[-1][1] - pts[0][1]) / ((pts[-1][0] - pts[0][0]) / f)
    wins = []
    i = 0
    for j in range(len(pts)):
        while (pts[j][0] - pts[i][0]) / f > 1.0:
            i += 1
        if i > 0 and (pts[j][0] - pts[i - 1][0]) / f >= 1.0:
            dt = (pts[j][0] - pts[i - 1][0]) / f
            wins.append((pts[j][1] - pts[i - 1][1]) / dt)
    return total, (min(wins) if wins else None)

def heartbeat_gaps(samples):
    """Largest snHeartbeatMs gap seen: a value jumping by > 1 s, or unchanged across > 1 s of samples (ms)."""
    f = samples[0].get("qpcFrequency") if samples else None
    f = f or C.qpc_frequency()
    worst = 0.0
    last_hb, last_change_q = None, None
    for s in samples:
        hb = (s.get("c0") or {}).get("snHeartbeatMs")
        if hb is None:
            continue
        if last_hb is not None and hb != last_hb:
            worst = max(worst, float(hb - last_hb))
            last_change_q = s["qpc"]
        elif last_hb is None:
            last_change_q = s["qpc"]
        elif last_change_q is not None:
            worst = max(worst, (s["qpc"] - last_change_q) / f * 1000.0)
        last_hb = hb
    return worst

def age_share(samples, limit=N8_AGE_TICKS):
    ages = [s["c1Tick"] - (s.get("c7") or {}).get("presentedSeTick", 0) for s in samples
            if s.get("c1Tick") is not None and (s.get("c7") or {}).get("presentedSeTick") is not None]
    if not ages:
        return None, None
    return sum(1 for a in ages if a <= limit) / len(ages), max(ages)

def se_perf_in(sess, part):
    """SE performance records of a part: structured s2.perf when the plugin writes them, else the plugin's 1 Hz
 "sample" records (frameTimeAvgMs -> frameMsP50, frameTimeMaxMs -> frameMsMax) plus SE's own 60 s STATISTICS lines
 of the game log (FrameTimeGPU -> gpuFrameMs). Each record gets the C5 view state of the launcher sample at its QPC."""
    recs = [r for r in sess.in_window(C.by(sess.se, "s2.perf"), part)
            if any(k in r for k in ("frameMsP50", "gpuFrameMs", "frameMsMax"))]
    if not recs:
        for r in sess.in_window(sess.se_samples, part):
            recs.append({"qpc": r["qpc"], "frameMsP50": r.get("frameTimeAvgMs"), "frameMsMax": r.get("frameTimeMaxMs"),
                         "source": "sample"})
        for r in sess.in_window(sess.se_stats, part):
            if isinstance(r.get("FrameTimeGPU"), float):
                recs.append({"qpc": r["qpc"], "gpuFrameMs": r["FrameTimeGPU"], "source": "STATISTICS"})
    for r in recs:
        if r.get("viewState") is None:
            st = sess.state_at(r["qpc"])
            c5 = (st or {}).get("c5") or {}
            r["viewState"], r["approachMode"] = c5.get("viewState"), c5.get("approachMode")
    return recs

def load_baseline(path):
    """{part: {"seGpuFrameMs": x, "seFrameMsP50": y}} from an SE-alone session folder or its perf-results.json."""
    if not path:
        return None
    if os.path.isdir(path):
        p = os.path.join(path, "perf-results.json")
        if os.path.exists(p):
            path = p
        else:
            return baseline_from_session(C.Session(path))
    with open(path, encoding="utf-8") as f:
        d = json.load(f)
    return d.get("baseline") or d.get("seAlone") or {}

def baseline_from_session(sess) -> dict:
    out = {}
    for part in ("n3", "n11.hover", "n11.sail", "n4.above", "n4.under"):
        recs = se_perf_in(sess, part)
        g = [C.num(r, "gpuFrameMs") for r in recs if C.num(r, "gpuFrameMs") is not None]
        p = [C.num(r, "frameMsP50") for r in recs if C.num(r, "frameMsP50") is not None]
        if g or p:
            out[part] = {"seGpuFrameMs": mean(g), "seFrameMsP50": mean(p)}
    return out

def evaluate(sess, baseline=None) -> list:
    rows = []
    parts = set(sess.parts)
    # ---------------------------------------------------------------- S2.N3
    if "n3" in parts:
        m = read_measure(os.path.join(sess.dir, "measure-n3.csv"))
        sn = m.get("Subnautica", [])
        smp = sess.samples("n3")
        fps, _wmin = sample_fps(smp)
        C.row(rows, "S2.N3", "MISSING" if fps is None else ("PASS" if fps <= N3_FPS_MAX else "FAIL"),
              "%.2f fps" % fps if fps is not None else "no C7 samples", "<= %g fps" % N3_FPS_MAX,
              "Subnautica frame rate in Space (C7 snFrame over the 60 s hold)", "n3")
        g, c = mean(_floats(sn, "gpu3d_pct")), mean(_floats(sn, "cpu_pct"))
        C.row(rows, "S2.N3", "MISSING" if g is None else ("PASS" if g < N3_GPU_MAX else "FAIL"),
              "%.2f %% GPU" % g if g is not None else "no measure.ps1 samples", "< %g %%" % N3_GPU_MAX,
              "Subnautica GPU (3D engines, measure.ps1)", "n3")
        C.row(rows, "S2.N3", "MISSING" if c is None else ("PASS" if c <= N3_CPU_MAX else "FAIL"),
              "%.2f %% CPU" % c if c is not None else "no measure.ps1 samples", "<= %g %% (covered idle)" % N3_CPU_MAX,
              "Subnautica CPU of all cores (MODLOG covered-idle level 0-7 %)", "n3")
        pairs = [((s.get("c7") or {}).get("vSyncCountApplied"), (s.get("c7") or {}).get("targetFrameRateApplied"))
                 for s in smp if s.get("c7")]
        bad = [p for p in pairs if p != (0, 10)]
        C.row(rows, "S2.N3", "MISSING" if not pairs else ("PASS" if not bad else "FAIL"),
              "%d/%d samples (0, 10)%s" % (len(pairs) - len(bad), len(pairs), (" e.g. %s" % (bad[0],)) if bad else ""),
              "(0, 10) every sample", "C7 applied (vSyncCount, targetFrameRate) in Space", "n3")
        se_g = [C.num(r, "gpuFrameMs") for r in se_perf_in(sess, "n3") if C.num(r, "gpuFrameMs") is not None]
        b = (baseline or {}).get("n3", {}).get("seGpuFrameMs")
        if not se_g:
            C.row(rows, "S2.N3", "MISSING", "no SE s2.perf gpuFrameMs", "within 5 % of SE alone",
                  "SE STATISTICS FrameTimeGPU vs SE alone", "n3")
        elif b is None:
            C.row(rows, "S2.N3", "MISSING", "SE %.3f ms; no SE-alone baseline" % mean(se_g), "within 5 % of SE alone",
                  "SE FrameTimeGPU vs SE alone (run launcher.py s2 --se-alone --parts n3,n11, pass --baseline)", "n3")
        else:
            r = mean(se_g) / b - 1.0
            # one-sided: only a slower SE is a regression
            C.row(rows, "S2.N3", "PASS" if r <= BASELINE_TOL else "FAIL", "%+.1f %% (%.3f vs %.3f ms)" % (
                100 * r, mean(se_g), b), "not more than 5 % slower", "SE FrameTimeGPU vs SE alone", "n3")
    # ---------------------------------------------------------------- S2.N4
    if "n4" in parts:
        a = read_measure(os.path.join(sess.dir, "measure-n4.above.csv")).get("Legacy", [])
        u = read_measure(os.path.join(sess.dir, "measure-n4.under.csv")).get("Legacy", [])
        ga, gu = mean(_floats(a, "gpu3d_pct")), mean(_floats(u, "gpu3d_pct"))
        if ga is None or gu is None or ga <= 0:
            C.row(rows, "S2.N4", "MISSING", "above %s, under %s" % (ga, gu), "under <= 0.70 x above",
                  "SE per-process GPU % underwater vs the same spot above water", "n4")
        else:
            C.row(rows, "S2.N4", "PASS" if gu <= N4_RATIO_MAX * ga else "FAIL",
                  "%.1f %% vs %.1f %% (x%.2f)" % (gu, ga, gu / ga), "under <= 0.70 x above",
                  "SE GPU underwater (scene skipped) vs above water", "n4")
    # ---------------------------------------------------------------- S2.N8
    if "n8" in parts:
        for sub, required in (("n8.on", True), ("n8.off", False)):
            smp = sess.samples(sub)
            fps, wmin = sample_fps(smp)
            gap = heartbeat_gaps(smp)
            share, worst = age_share(smp)
            label = "margins on" if sub == "n8.on" else "margins off (recorded, O9)"
            if fps is None:
                C.row(rows, "S2.N8", "MISSING" if required else "RECORD", "no samples", ">= 25 fps", label, sub, required)
                continue
            if required:
                ok_fps = wmin is not None and wmin >= N8_FPS_MIN
                C.row(rows, "S2.N8", "PASS" if ok_fps else "FAIL", "%.1f fps, min 1 s window %s" % (
                    fps, "%.1f" % wmin if wmin is not None else "-"), ">= 25 fps every 1 s",
                    "Subnautica snFrame rate in Prime, %s" % label, sub)
                C.row(rows, "S2.N8", "PASS" if gap <= N8_GAP_MS else "FAIL", "max heartbeat gap %.0f ms" % gap,
                      "<= 1000 ms", "Subnautica heartbeat in Prime, %s" % label, sub)
                C.row(rows, "S2.N8", "MISSING" if share is None else ("PASS" if share >= N8_AGE_SHARE else "FAIL"),
                      "%.2f %% <= 4 ticks (worst %s)" % (100 * share, worst) if share is not None else "no ages",
                      ">= 99 %", "presentedSeTick age (C1 tick - presentedSeTick)", sub)
            else:
                C.row(rows, "S2.N8", "RECORD", "%.1f fps (min 1 s %s), gap %.0f ms, age<=4 %s" % (
                    fps, "%.1f" % wmin if wmin is not None else "-", gap,
                    "%.1f %%" % (100 * share) if share is not None else "-"), "recorded", label, sub, False)
    # ---------------------------------------------------------------- S2.N11
    if "n11" in parts:
        for sub in ("n11.hover", "n11.sail"):
            recs = se_perf_in(sess, sub)
            p = [C.num(r, "frameMsP50") for r in recs if C.num(r, "frameMsP50") is not None]
            b = (baseline or {}).get(sub, {}).get("seFrameMsP50")
            if not p:
                C.row(rows, "S2.N11", "MISSING", "no SE s2.perf", "within 5 % of SE alone", "SE frame time, %s" % sub, sub)
            elif b is None:
                C.row(rows, "S2.N11", "MISSING", "SE %.3f ms; no SE-alone baseline" % mean(p), "within 5 % of SE alone",
                      "SE frame time p50, %s (pass --baseline)" % sub, sub)
            else:
                r = mean(p) / b - 1.0
                C.row(rows, "S2.N11", "PASS" if r <= BASELINE_TOL else "FAIL", "%+.1f %% (%.3f vs %.3f ms)" % (
                    100 * r, mean(p), b), "within 5 % of SE alone", "SE frame time p50, %s" % sub, sub)
            smp = sess.samples(sub)
            modes = [C.mode_name((s.get("c5") or {}).get("approachMode")) for s in smp if s.get("c5")]
            duty = sum(1 for m in modes if m == "Prime") / len(modes) if modes else None
            C.row(rows, "S2.N11", "RECORD", "Prime %.1f %% of %d samples" % (100 * duty, len(modes)) if duty is not None
                  else "no samples", "logged", "Prime duty cycle, %s" % sub, sub, False)
    # ---------------------------------------------------------------- S2.N7 (a) hitch
    if "n7" in parts:
        recs = [r for r in se_perf_in(sess, "n7a") + se_perf_in(sess, "n7b")
                if C.state_label(r.get("viewState"), r.get("approachMode")) == "Prime"]
        mx = [C.num(r, "frameMsMax") for r in recs if C.num(r, "frameMsMax") is not None]
        C.row(rows, "S2.N7", "MISSING" if not mx else ("PASS" if max(mx) < HITCH_MS else "FAIL"),
              "max %.1f ms over %d Prime seconds" % (max(mx), len(mx)) if mx else "no SE s2.perf in Prime",
              "< 50 ms", "SE frame hitch during Prime (warm-up)", "n7")
    return rows

# ----------------------------------------------------------------------------------------------------------- selftest
def _synth(tmp, mutant=None):
    """A synthetic session folder for the selftest."""
    import random
    random.seed(4)
    os.makedirs(tmp, exist_ok=True)
    f = 10_000_000
    sess, se, stats = [], [], []
    q = 1_000_000_000
    t0 = 1_791_000_000.0                       # POSIX seconds of the first launcher event (utc <-> qpc mapping)
    plugin = mutant in ("plugin", "plugin-hitch")   # the real-plugin formats: SE "sample" records + STATISTICS lines
    import datetime

    def utc(qq):
        return datetime.datetime.fromtimestamp(t0 + (qq - 1_000_000_000) / f, datetime.timezone.utc).strftime(
            "%Y-%m-%dT%H:%M:%S.%f") + "Z"
    sess.append({"ev": "start", "parts": ["n3", "n4", "n8", "n11", "n7"], "qpcFrequency": f, "qpc": q, "utc": utc(q)})

    def part(name, seconds, c7, hz=10, approach=0, view=0, hb_stall_at=None, fps=60.0, age=1):
        nonlocal q
        sess.append({"ev": "part", "name": name, "phase": "begin", "qpc": q, "utc": utc(q)})
        frame, tick, hb = 1000, 5000, 900000
        for i in range(int(seconds * hz)):
            q += f // hz
            frame += fps / hz
            tick += 60 / hz
            if hb_stall_at is None or not (hb_stall_at <= i < hb_stall_at + 25):
                hb += 1000 // hz
            sess.append({"ev": "sample", "part": name, "qpc": q, "qpcFrequency": f, "c0": {"snHeartbeatMs": hb},
                         "c1Tick": int(tick), "c5": {"viewState": view, "approachMode": approach},
                         "c7": dict(c7, snFrame=int(frame), presentedSeTick=int(tick) - age)})
            if i % hz == 0 and not plugin:
                se.append({"ev": "s2.perf", "qpc": q, "gpuFrameMs": 4.0, "frameMsP50": 8.33, "frameMsMax": 12.0,
                           "simRatio": 1.0, "viewState": view, "approachMode": approach})
            elif i % hz == 0:
                se.append({"t": utc(q), "ev": "sample", "gameplayTick": int(tick), "frameTimeAvgMs": 8.33,
                           "frameTimeMaxMs": 75.0 if (mutant == "plugin-hitch" and name == "n7a" and i == 50) else 12.0,
                           "paused": False})
                if i % (hz * 20) == 0:
                    stats.append((q, 4.0))
        sess.append({"ev": "part", "name": name, "phase": "end", "qpc": q, "utc": utc(q)})
    idle = (0, 10) if mutant != "M14" else (1, 10)
    part("n3", 60, {"vSyncCountApplied": idle[0], "targetFrameRateApplied": idle[1]},
         fps=10.0 if mutant not in ("M6", "M14") else 60.0)
    part("n4.above", 60, {})
    part("n4.under", 60, {})
    part("n8.on", 60, {}, approach=2, view=1, fps=144.0 if mutant != "M7" else 3.0,
         hb_stall_at=300 if mutant == "M7" else None)
    part("n8.off", 60, {}, approach=2, view=1, fps=12.0)
    part("n11.hover", 60, {}, approach=1, view=1)
    part("n11.sail", 60, {}, approach=1, view=1)
    part("n7a", 20, {}, approach=2, view=1)
    if mutant == "hitch":
        se[-3]["frameMsMax"] = 75.0
    with open(os.path.join(tmp, "s2-session.jsonl"), "w") as fh:
        for r in sess:
            fh.write(json.dumps(r) + "\n")
    with open(os.path.join(tmp, "se-20261012-000000.jsonl"), "w") as fh:
        for r in se:
            fh.write(json.dumps(r) + "\n")
    if plugin:   # SE's game log: local-time stamps, a LEGEND line, then the values (FrameTimeGPU = 4.0 ms)
        legend = "time,FrameTime,FrameTimeCPU,FrameTimeGPU"
        with open(os.path.join(tmp, "SpaceEngineers_20261012_000000000.log"), "w") as fh:
            for qq, g in stats:
                lt = datetime.datetime.fromtimestamp(t0 + (qq - 1_000_000_000) / f).strftime("%Y-%m-%d %H:%M:%S.%f")[:23]
                fh.write("%s - Thread:   1 ->  STATISTICS LEGEND,%s\n" % (lt, legend))
                fh.write("%s - Thread:   1 ->  STATISTICS,0,8.33333,1.7,%.4f\n" % (lt, g))
    hdr ="time,t_s,name,pid,cpu_pct,ws_mb,gpu3d_pct,responding,iconic,foreground,fg_name\n"

    def csvf(name, rows):
        with open(os.path.join(tmp, name), "w") as fh:
            fh.write(hdr)
            for i, (n, cpu, gpu) in enumerate(rows):
                fh.write("2026-10-12T00:00:%02d.000,%d,%s,1,%s,100,%s,True,False,False,Legacy\n" % (i % 60, i, n, cpu, gpu))
    sn_gpu = 2.0 if mutant != "M6" else 40.0
    csvf("measure-n3.csv", [("Subnautica", 3.0, sn_gpu), ("Legacy", 15.0, 60.0)] * 60)
    csvf("measure-n4.above.csv", [("Legacy", 15.0, 80.0)] * 60)
    csvf("measure-n4.under.csv", [("Legacy", 12.0, 30.0 if mutant != "N4" else 70.0)] * 60)
    return {"n3": {"seGpuFrameMs": 4.0, "seFrameMsP50": 8.33}, "n11.hover": {"seFrameMsP50": 8.33},
            "n11.sail": {"seFrameMsP50": 8.33}}

def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-66s %s %s" % (label, "PASS" if cond else "FAIL", detail))
    root = tempfile.mkdtemp(prefix="de_perf_")
    try:
        base = _synth(os.path.join(root, "good"))
        rows = evaluate(C.Session(os.path.join(root, "good")), base)
        bad = [r for r in rows if r.get("required", True) and r["status"] in C.BAD]
        check("good session: every required row PASS", not bad and len(rows) >= 14, "%d rows%s" % (
            len(rows), "".join("; %s %s %s" % (r["id"], r["status"], r["measured"]) for r in bad)))
        rows = evaluate(C.Session(os.path.join(root, "good")), None)
        check("no baseline: the SE-alone rows are MISSING (required)", any(
            r["status"] == "MISSING" and "SE alone" in r["threshold"] for r in rows))

        def red(mutant, cid, text):
            d = os.path.join(root, mutant)
            b = _synth(d, mutant)
            rr = evaluate(C.Session(d), b)
            hit = [r for r in rr if r["id"] == cid and r["status"] == "FAIL" and text in r["what"]]
            check("seeded %s -> %s red (%s)" % (mutant, cid, text), bool(hit), hit[0]["measured"] if hit else "")
        red("M6", "S2.N3", "frame rate")            # S2.M6 idle recipe off
        red("M14", "S2.N3", "applied")              # S2.M14 vSyncCount left at 1 in Idle
        red("M14", "S2.N3", "frame rate")
        red("M7", "S2.N8", "snFrame rate")          # S2.M7 margins off during Prime: covered stall
        red("M7", "S2.N8", "heartbeat")
        red("N4", "S2.N4", "SE GPU")
        red("hitch", "S2.N7", "hitch")
        d = os.path.join(root, "plugin")
        b = _synth(d, "plugin")
        rr = evaluate(C.Session(d), b)
        bad = [r for r in rr if r.get("required", True) and r["status"] in C.BAD]
        check("plugin formats (SE sample records + STATISTICS FrameTimeGPU, UTC->QPC): every required row PASS",
              not bad, "".join("; %s %s %s" % (r["id"], r["status"], r["measured"]) for r in bad))
        red("plugin-hitch", "S2.N7", "hitch")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("ANALYZE_PERF SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S2 performance oracle (N3, N4, N8, N11, N7a)")
    ap.add_argument("dir", nargs="?")
    ap.add_argument("--baseline", default=None)
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.dir:
        ap.error("a session folder is required")
    sess = C.Session(a.dir)
    rows = evaluate(sess, load_baseline(a.baseline))
    ok = C.write_results(a.dir, "perf", rows, {"baseline": baseline_from_session(sess)})
    print(json.dumps(rows) if a.json else C.table(rows))
    print("ANALYZE_PERF %s (%d rows)" % ("PASS" if ok else "FAIL", len(rows)))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
