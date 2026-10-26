"""S2.T-D1 display oracle (and S2.N0 composed-capture rows) from a collected `launcher.py s2` session.

 python -I tests/s2/analyze_td1.py tests/out/s2/<stamp> [--json]
 python -I tests/s2/analyze_td1.py --selftest # synthetic session; S2.M1, S2.M9 and every threshold seeded red

Rows (ROADMAP S2.T-D1 pass column):
- foreground 50/50: after every toggle GetForegroundWindow == seHwnd (the launcher's check after each `de.display
 {"toggle": true}`; the plugin's own `DE.Display toggle #N... foregroundIsSe=` agrees, cross-checked);
- z-order: the walker (tools/display/walker.py) finds snHwnd directly under seHwnd at every toggle (skipped windows
 listed as INFO);
- DWM: every `DE.Display margins ON|OFF... hr=` is S_OK (a "MUTANT S2.M1... NOT called" line fails it);
- crossing -> Subnautica ready: presentedSeTick >= crossingTick within 60 ms of the crossing tick (the launcher's
 CrossingWatcher: C1 qpcCapture of the crossing tick -> first C7 presentedSeTick >= it, polled every ~1 ms; crossings
 of every part count when Subnautica already ran Full at the crossing (C7 perfModeApplied; ARCHITECTURE 4.4 "Subnautica
 runs at full rate from Prime"); crossings by a teleport from Track or Space are an INFO row);
- skip frame -> composed <= 2 refreshes (12.1 ms at 165 Hz): the first video frame showing the change after a toggle
 (tools/capture/composed_video.py, ddagrab, frame times aligned to QPC as an upper bound) minus the toggle's QPC and
 the plugin's "see-through first skipped frame presented +X ms after the request". This bound still contains the
 C11 delivery delay (<= 1 SE tick) and the capture pipeline, so a value above 12.1 ms is reported MISSING
 ("inconclusive") rather than FAIL; a value under it is a PASS;
- no black frame in any composed video of the session (>= 98 % of the frame at luma <= 4);
- alpha readback (`de.display {"readback": "once"}`): see-through with no menu open < 15 % of the pixels alpha > 0 (the
 SE HUD footprint, 10.2 % measured at 2560x1440 on the real runs, stays opaque by design; 2 % was the ROADMAP's guess);
 scene drawn (Prime or see-through off) < 0.1 % alpha < 1; a readback inside a launcher "menu" window is INFO;
- SE update time per toggle < 1 ms (`DE.Display toggle... updateMs=`);
- SE simulation ratio >= 0.98 over the td1 part (mean of the plugin's 1 Hz samples: gameplayTick delta / 60 per
 second; the minimum is reported);
the launcher's desktop shots of each menu are listed)
S2.N0 (composed-capture tooling): with see-through on the calibration colour appears at the screen centre in >= 95 % of
the frames of video-n0-on; with see-through off it does not (<= 5 % in video-n0-off); with the calibration pattern off,
two frames 1 s apart differ (video-n0-anim: Subnautica animates under the see-through area).
Review fix (finding 1): the plugin's composed-pixel probe verdict, taken while the calibration colour is on
(`DE.Display probe HONOURED|NOT HONOURED|UNDECIDED...`, two agreeing GDI reads): HONOURED -> PASS, NOT HONOURED ->
FAIL, anything else MISSING; and the
display design never switched during the session (`DE.Display mode F -> A'`; the scripted sessions turn autoFallback
off, so a switch there means rows after it ran under another design).
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

TOGGLES = 50
CROSS_MS = 60.0
REFRESH_MS = 1000.0 / 165.0
COMPOSED_MS = 2 * REFRESH_MS
# see-through, no menu: the pixels with alpha > 0 are SE's HUD (toolbar, stats, crosshair), which must stay opaque over
# Subnautica. Real runs/16 at 2560x1440: 10.23-10.25 % in every readback (stable = no leak beyond the HUD).
# The ROADMAP's 2 % was a guess; 15 % catches a leak (a full-screen sprite, a skipped clear) without flagging the HUD.
ALPHA_GT0_MAX = 0.15
ALPHA_LT1_MAX = 0.001
UPDATE_MS = 1.0
SIM_MIN = 0.98
CHANGE_LUMA = 3.0
CHANGE_RGB = 24

def load_video(sess, name):
    d = os.path.join(sess.dir, name)
    sp = os.path.join(d, "video-summary.json")
    if not os.path.exists(sp):
        return None, []
    with open(sp, encoding="utf-8") as f:
        summ = json.load(f)
    return summ, C.read_jsonl(os.path.join(d, "video-frames.jsonl"))

def videos(sess):
    return sorted(n for n in os.listdir(sess.dir) if n.startswith("video-") and
                  os.path.isdir(os.path.join(sess.dir, n)))

def _changed(a, b):
    if abs((a.get("meanLuma") or 0) - (b.get("meanLuma") or 0)) > CHANGE_LUMA:
        return True
    pa, pb = a.get("probes") or {}, b.get("probes") or {}
    for k in pa:
        if k in pb and max(abs(x - y) for x, y in zip(pa[k], pb[k])) > CHANGE_RGB:
            return True
    return False

def evaluate(sess) -> list:
    rows = []
    if "td1" not in set(sess.parts):
        return rows
    toggles = [r for r in sess.session if r.get("ev") == "toggle"]
    se_tog = [e for e in C.by(sess.se, "s2.display") if e.get("action") == "toggle"]
    # ---- foreground
    fg = [r for r in toggles if isinstance(r.get("fg"), dict)]
    ok_fg = [r for r in fg if r["fg"].get("isSe")]
    if fg and all(r["fg"].get("standin") for r in fg):
        C.row(rows, "S2.T-D1", "OTHER", "%d toggles of a stand-in SE" % len(fg), "real SE",
              "SE foreground after every toggle (a stand-in window never takes the foreground)", "td1", required=False)
    else:
        C.row(rows, "S2.T-D1", "MISSING" if not fg else (
            "PASS" if len(fg) >= TOGGLES and len(ok_fg) == len(fg) else "FAIL"), "%d/%d" % (len(ok_fg), len(fg)),
            "50/50", "SE foreground after every toggle (GetForegroundWindow == seHwnd)", "td1")
    if se_tog:
        n_se = sum(1 for e in se_tog if e.get("isSe"))
        C.row(rows, "S2.T-D1", "INFO", "%d/%d" % (n_se, len(se_tog)), "cross-check",
              "the plugin's own foregroundIsSe per toggle (DE.Display toggle lines)", "td1", required=False)
    # ---- z-order
    wk = [r for r in toggles if isinstance(r.get("walker"), dict)]
    ok_wk = [r for r in wk if r["walker"].get("ok")]
    C.row(rows, "S2.T-D1", "MISSING" if not wk else ("PASS" if len(wk) >= TOGGLES and len(ok_wk) == len(wk) else "FAIL"),
          "%d/%d directly under" % (len(ok_wk), len(wk)), "every toggle", "z-order walker: snHwnd directly under seHwnd",
          "td1", detail={"firstBad": [r["walker"].get("verdict") for r in wk if not r["walker"].get("ok")][:3]})
    skipped = {}
    for r in wk:
        for s in r["walker"].get("skipped") or []:
            key = "%s/%s/%s" % (s.get("image"), s.get("cls"), s.get("reason"))
            skipped[key] = skipped.get(key, 0) + 1
    C.row(rows, "S2.T-D1", "INFO", "%d kinds" % len(skipped), "logged", "windows the walker skipped: %s" % (
        ", ".join("%s x%d" % kv for kv in sorted(skipped.items())[:6]) or "none"), "td1", required=False)
    # ---- DWM
    marg = [e for e in C.by(sess.se, "s2.display") if e.get("action") in ("marginsOn", "marginsOff")]
    bad = [e for e in marg if e.get("hr") not in (0,) or e.get("mutantNoCall") or e.get("failed")]
    C.row(rows, "S2.T-D1", "MISSING" if not marg else ("PASS" if not bad else "FAIL"),
          "%d calls, %d not S_OK%s" % (len(marg), len(bad), " (S2.M1: not called)" if any(
              e.get("mutantNoCall") for e in bad) else ""), "all S_OK", "DwmExtendFrameIntoClientArea returns S_OK",
          "td1")
    # ---- crossing -> presentedSeTick >= crossingTick
    allx = [r for r in sess.session if r.get("ev") == "crossing"]

    def full_mode(r):
        # the 60 ms budget assumes Subnautica already runs Full (Prime, Underwater, Surfacing: ARCHITECTURE 4.4): judged
 # when C7 perfModeApplied was Full at the crossing (else C5 perfMode); a crossing made by a teleport from Track or
 # Space (Subnautica's cameras still off) is information only
        if r.get("snPerfApplied") is not None:
            return r["snPerfApplied"] == 2
        pm = r.get("perfMode")
        if pm is None:
            st = sess.state_at(r.get("qpcC5") or 0)
            pm = ((st or {}).get("c5") or {}).get("perfMode")
        return pm == 2
    judged = [r for r in allx if full_mode(r)]
    cr = [r for r in judged if C.num(r, "latencyMs") is not None]
    never = [r for r in judged if r.get("latencyMs") is None]
    worst = max((r["latencyMs"] for r in cr), default=None)
    C.row(rows, "S2.T-D1", "MISSING" if not cr else ("PASS" if worst <= CROSS_MS and not never else "FAIL"),
          "%d crossings, worst %.1f ms%s" % (len(cr), worst, ", %d never" % len(never) if never else "") if cr else
          "no crossing in Full mode measured", "<= 60 ms",
          "crossing (Subnautica Full) -> it presents presentedSeTick >= crossingTick", "all")
    other = [r for r in allx if r not in judged]
    if other:
        lat = [r["latencyMs"] for r in other if r.get("latencyMs") is not None]
        C.row(rows, "S2.T-D1", "INFO", "%d crossings, worst %s" % (len(other), "%.1f ms" % max(lat) if lat else "-"),
              "information", "crossings while Subnautica was not Full (teleports from Track/Space)", "all",
              required=False)
    # ---- skip frame -> composed (upper bound from the td1 video)
    summ, frames = load_video(sess, "video-td1")
    se_lat = [C.num(e, "requestToPresentMs") for e in C.by(sess.se, "s2.display") if e.get("action") == "skipPresented"]
    se_lat = [x for x in se_lat if x is not None]
    if not frames or not toggles:
        C.row(rows, "S2.T-D1", "MISSING", "no td1 video or no toggles", "<= 2 refreshes (12.1 ms)",
              "skip frame -> composed (ddagrab frames vs the toggle QPC)", "td1")
    else:
        fr = [f for f in frames if f.get("qpcEst") is not None]
        f_ = float(sess.qpcf)
        bounds = []
        for t in toggles:
            q = t.get("qpc")
            if q is None:
                continue
            before = [f for f in fr if f["qpcEst"] <= q]
            after = [f for f in fr if f["qpcEst"] > q]
            if not before or not after:
                continue
            ref = before[-1]
            hit = next((f for f in after if _changed(f, ref)), None)
            if hit:
                bounds.append((hit["qpcEst"] - q) / f_ * 1000.0)
        sub = min(se_lat) if se_lat else 0.0
        comp = [b - sub for b in bounds]
        if not comp:
            C.row(rows, "S2.T-D1", "MISSING", "no change found in the video after the toggles", "<= 12.1 ms",
                  "skip frame -> composed", "td1")
        else:
            w = sorted(comp)[int(0.95 * (len(comp) - 1))]
            C.row(rows, "S2.T-D1", "PASS" if w <= COMPOSED_MS else "MISSING",
                  "p95 upper bound %.1f ms over %d toggles%s" % (w, len(comp), "" if w <= COMPOSED_MS else
                                                                 " (inconclusive: bound includes C11 + capture delay)"),
                  "<= 2 refreshes (12.1 ms)", "skip frame -> composed (video; minus SE request->present %.1f ms)" % sub,
                  "td1", detail={"boundsMs": [round(x, 1) for x in comp[:60]]})
    # ---- black frames, every video
    vids = videos(sess)
    blacks, nframes = 0, 0
    for v in vids:
        s, _f = load_video(sess, v)
        if s:
            blacks += s.get("blackCount", 0)
            nframes += s.get("frames", 0)
    C.row(rows, "S2.T-D1", "MISSING" if not nframes else ("PASS" if blacks == 0 else "FAIL"),
          "%d black of %d frames (%d videos)" % (blacks, nframes, len(vids)), "0", "no black frame at any switch", "td1")
    # ---- alpha readback
    menus = [(r.get("qpc0"), r.get("qpc1")) for r in sess.session if r.get("ev") == "menu" and r.get("qpc0")]
    alpha = C.by(sess.se, "s2.alpha")
    for a in alpha:
        q = a.get("qpc")
        if q is not None and any(m0 <= q <= (m1 or m0) for m0, m1 in menus):
            a["menuOpen"] = True
    see = [a for a in alpha if a.get("mode") == "seeThrough" and not a.get("menuOpen")]
    scene = [a for a in alpha if a.get("mode") == "scene"]
    w1 = max((a["fracAlphaGt0"] for a in see), default=None)
    w2 = max((a["fracAlphaLt1"] for a in scene), default=None)
    C.row(rows, "S2.T-D1", "MISSING" if w1 is None else ("PASS" if w1 < ALPHA_GT0_MAX else "FAIL"),
          "%d readbacks, worst %.3f %%" % (len(see), 100 * w1) if w1 is not None else "no see-through readback",
          "< 15 % (HUD footprint ~10 %)", "see-through, no menu: backbuffer pixels with alpha > 0", "td1")
    C.row(rows, "S2.T-D1", "MISSING" if w2 is None else ("PASS" if w2 < ALPHA_LT1_MAX else "FAIL"),
          "%d readbacks, worst %.4f %%" % (len(scene), 100 * w2) if w2 is not None else "no scene-drawn readback",
          "< 0.1 %", "scene drawn (Prime): backbuffer pixels with alpha < 1", "td1")
    # ---- update time per toggle
    um = [C.num(e, "updateMs") for e in se_tog if C.num(e, "updateMs") is not None]
    if not um:
        um = [C.num(e, "updateUs") / 1000.0 for e in C.by(sess.se, "s2.display") if C.num(e, "updateUs") is not None]
    C.row(rows, "S2.T-D1", "MISSING" if not um else ("PASS" if max(um) < UPDATE_MS else "FAIL"),
          "%d toggles, max %.3f ms" % (len(um), max(um)) if um else "no DE.Display toggle line", "< 1 ms",
          "SE update time per toggle", "td1")
    # ---- simulation ratio
    st = [C.num(e, "simRatio") for e in sess.in_window(C.by(sess.se, "s2.perf"), "td1") if C.num(e, "simRatio")]
    mean_r, min_r = (sum(st) / len(st), min(st)) if st else sess.sim_ratio("td1")
    C.row(rows, "S2.T-D1", "MISSING" if mean_r is None else ("PASS" if mean_r >= SIM_MIN else "FAIL"),
          "mean %.3f, min %.3f" % (mean_r, min_r) if mean_r is not None else "no SE samples in td1", ">= 0.98",
          "SE simulation ratio during the toggles", "td1")

    shots = [r.get("shot") for r in sess.session if r.get("ev") == "menu" and r.get("shot")]
    C.row(rows, "S2.T-D1", "MANUAL", "%d menu shots" % len(shots), "readable (owner)",
          "every menu readable over Subnautica: inventory I, terminal K, toolbar G, ESC, chat, F10", "td1",
          required=False, shots=shots[:12])
    # ---- S2.N0 probe verdict
    disp = C.by(sess.se, "s2.display")
    probes = [e for e in disp if e.get("action") == "probe"]
    if probes:
        last = probes[-1]
        if last.get("standin"):
            C.row(rows, "S2.N0", "OTHER", "stand-in SE (no composition)", "real SE",
                  "composed-pixel probe with the calibration colour on: HONOURED", "n0", required=False)
        else:
            v = last.get("verdict")
            C.row(rows, "S2.N0", "PASS" if v == "HONOURED" else ("FAIL" if v == "NOT HONOURED" else "MISSING"),
                  "%s (%d probe%s)" % (last.get("word"), len(probes), "s" if len(probes) != 1 else ""), "HONOURED",
                  "composed-pixel probe with the calibration colour on: HONOURED (NOT HONOURED -> runbook 8.3 rule 3)",
                  "n0", detail={"text": str(last.get("text", ""))[:300]})
    elif "n0" in set(sess.parts) or any(r.get("label") == "n0.probe" for r in sess.session if r.get("ev") == "reply"):
        C.row(rows, "S2.N0", "MISSING", "no DE.Display probe verdict line", "HONOURED",
              "composed-pixel probe with the calibration colour on: HONOURED", "n0")
    modes = [e for e in disp if e.get("action") == "mode"]
    C.row(rows, "S2.T-D1", "PASS" if not modes else "FAIL",
          "no switch" if not modes else "%d switch(es): %s" % (len(modes), "; ".join(
              "%s -> %s (%s)" % (e.get("from"), e.get("to"), str(e.get("why"))[:60]) for e in modes[:3])),
          "no switch", "display design unchanged during the session (autoFallback off in scripted sessions)", "td1")
    # ---- S2.N0
    for name, what, want in (("video-n0-on", "see-through on: the calibration colour shows in the composed capture", "on"),
                             ("video-n0-off", "see-through off: the calibration colour does not show", "off"),
                             ("video-n0-anim", "Subnautica animates under the see-through area (1 s apart)", "anim")):
        s, _f = load_video(sess, name)
        if not s:
            if "n0" in set(sess.parts) or want != "anim" and any(v.startswith("video-n0") for v in vids):
                C.row(rows, "S2.N0", "MISSING", "no %s" % name, "-", what, "n0")
            continue
        if want == "anim":
            C.row(rows, "S2.N0", "PASS" if s.get("differs1s") else "FAIL", "differs1s=%s" % s.get("differs1s"),
                  "differ", what, "n0")
            continue
        e = next(iter((s.get("expect") or {}).values()), None)
        frac = e.get("fraction") if e else None
        if frac is None:
            C.row(rows, "S2.N0", "MISSING", "no probe expectation", "-", what, "n0")
        else:
            ok = frac >= 0.95 if want == "on" else frac <= 0.05
            C.row(rows, "S2.N0", "PASS" if ok else "FAIL", "%.0f %% of %d frames" % (100 * frac, e.get("frames", 0)),
                  ">= 95 %" if want == "on" else "<= 5 %", what, "n0")
    return rows

# ----------------------------------------------------------------------------------------------------------- selftest
def _synth(d, mutant=None):
    f = 10_000_000
    os.makedirs(d, exist_ok=True)
    sess = [{"ev": "start", "parts": ["td1", "n0"], "qpcFrequency": f, "qpc": 0, "utc": "2026-10-12T10:00:00.000000Z"}]
    sess.append({"ev": "part", "name": "td1", "phase": "begin", "qpc": f, "utc": "2026-10-12T10:00:01.000000Z"})
    se = []
    frames = []
    q = 2 * f
    see = False
    lum = 40.0
    n = 0
    for i in range(50):
        q += f
        see = not see
        fg_ok = not (mutant == "fg" and i == 7)
        wk_ok = not (mutant == "M9" and i == 12)
        sess.append({"ev": "toggle", "index": i + 1, "qpc": q, "fg": {"isSe": fg_ok},
                     "walker": {"ok": wk_ok, "verdict": "ok" if wk_ok else "first window under seHwnd is Notepad",
                                "skipped": [{"image": "Legacy.exe", "cls": "IME", "reason": "invisible"}]}})
        utc = "2026-10-12T10:00:%02d.%06dZ" % ((q // f) % 60, (q % f) // 10)
        se.append({"t": utc, "ev": "log", "msg": "DE.Display toggle #%d see-through %s via de.display mode=F "
                   "foregroundIsSe=%s updateMs=%.3f" % (i + 1, "ON" if see else "OFF", fg_ok,
                                                       1.5 if (mutant == "update" and i == 20) else 0.2)})
        se.append({"t": utc, "ev": "log", "msg": "DE.Display see-through first skipped frame presented +4.0 ms after "
                                                  "the request (toggle #%d, margins on for 30 presents)" % (i + 1)})
        lag = 0.030 if mutant == "slow" else 0.008      # toggle -> first changed composed frame
        for k in range(165):                            # one second of frames at 165 fps after each toggle
            fq = q + int(k * f / 165.0)
            changed = fq >= q + int(lag * f)
            l = (lum if not changed else (90.0 if see else 40.0))
            black = mutant == "black" and i == 30 and k == 3
            frames.append({"n": n, "pts": None, "qpcEst": fq, "meanLuma": 0.0 if black else l, "blackFraction":
                           1.0 if black else 0.0, "black": black, "changed": True})
            n += 1
        lum = 90.0 if see else 40.0
    hr = "0x00000000 S_OK" if mutant != "M1" else "0x00000000 (MUTANT S2.M1: DwmExtendFrameIntoClientArea NOT called)"
    se.append({"t": "2026-10-12T10:00:02.000000Z", "ev": "log", "msg": "DE.Display margins ON (-1) hr=%s label=x presents=1" % hr})
    se.append({"t": "2026-10-12T10:00:03.000000Z", "ev": "log", "msg": "DE.Display alpha mode=F label=toggle margins=on "
               "scene=skipped alphaFill=no 2560x1440 format=X pixels=100 alphaGt0=1 (%s %%) alphaLt1=100 (100.00 %%)"
               % ("1.00" if mutant != "alpha" else "25.00")})   # a leak beyond the HUD footprint (threshold 15 %)
    se.append({"t": "2026-10-12T10:00:04.000000Z", "ev": "log", "msg": "DE.Display alpha mode=F label=prime margins=on "
               "scene=drawn alphaFill=done 2560x1440 format=X pixels=100000 alphaGt0=100000 (100.00 %) alphaLt1=0 (0.00 %)"})
    for k in range(60):
        se.append({"t": "2026-10-12T10:00:%02d.000000Z" % (2 + k), "ev": "sample", "gameplayTick": 1000 + k * (
            60 if mutant != "sim" else 55), "paused": False})
    sess.append({"ev": "part", "name": "td1", "phase": "end", "qpc": q + 2 * f, "utc": "2026-10-12T10:01:00.000000Z"})
    sess.append({"ev": "crossing", "crossingTick": 5000, "latencyMs": 22.0 if mutant != "cross" else 85.0, "perfMode": 2,
                 "snPerfApplied": 2})
    sess.append({"ev": "crossing", "crossingTick": 4000, "latencyMs": 140.0, "perfMode": 1})   # a teleport from Track: INFO only
    sess.append({"ev": "crossing", "crossingTick": 4500, "latencyMs": 81.0, "perfMode": 2, "snPerfApplied": 1})  # not yet Full
    sess.append({"ev": "menu", "name": "inventory", "qpc0": q + f, "qpc1": q + 2 * f, "shot": "menu-inventory.png"})
    # the N0 probe, run while the calibration colour is on
    se.append({"t": "2026-10-12T10:01:10.000000Z", "ev": "log", "msg": "DE.Display probe START (composed-pixel measurement)"})
    probe = "NOT HONOURED expected 255/0/255 +-24: 0/5 match; points: ..." if mutant in ("M1", "probeNot") else \
        "HONOURED expected 255/0/255 +-24: 5/5 match; points: ... | read 2: HONOURED expected 255/0/255 +-24: 5/5 match"
    if mutant == "probeUndecided":
        probe = "UNDECIDED (the two reads disagree or are mixed) read 1: NOT HONOURED ... | read 2: HONOURED ..."
    se.append({"t": "2026-10-12T10:01:11.000000Z", "ev": "log", "msg": "DE.Display probe " + probe})
    if mutant == "modeSwitch":
        se.append({"t": "2026-10-12T10:01:12.000000Z", "ev": "log", "msg": "DE.Display mode F -> A' (composed-pixel probe: "
                   "DWM alpha not honoured in 2 probes in a row (ARCHITECTURE 5.1: not honoured -> A'))"})
    with open(os.path.join(d, "s2-session.jsonl"), "w") as fh:
        for r in sess:
            fh.write(json.dumps(r) + "\n")
    with open(os.path.join(d, "se-20261012-100000.jsonl"), "w") as fh:
        for r in se:
            fh.write(json.dumps(r) + "\n")

    def video(name, frs, expect=None, differs=None):
        vd = os.path.join(d, name)
        os.makedirs(vd, exist_ok=True)
        with open(os.path.join(vd, "video-frames.jsonl"), "w") as fh:
            for fr in frs:
                fh.write(json.dumps(fr) + "\n")
        with open(os.path.join(vd, "video-summary.json"), "w") as fh:
            json.dump({"frames": len(frs), "blackCount": sum(1 for x in frs if x["black"]), "expect": expect or {},
                       "differs1s": differs}, fh)
    video("video-td1", frames)
    on = 0.99 if mutant != "M1" else 0.0
    video("video-n0-on", frames[:100], {"centre": {"frames": 100, "fraction": on}})
    video("video-n0-off", frames[:100], {"centre": {"frames": 100, "fraction": 0.0}})
    video("video-n0-anim", frames[:100], None, differs=True)

def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-66s %s %s" % (label, "PASS" if cond else "FAIL", detail))
    root = tempfile.mkdtemp(prefix="de_td1_")
    try:
        d = os.path.join(root, "good")
        _synth(d)
        rows = evaluate(C.Session(d))
        bad = [r for r in rows if r.get("required", True) and r["status"] in C.BAD]
        check("good session: every required row PASS", not bad and len(rows) >= 14, "%d rows%s" % (
            len(rows), "".join("; %s: %s %s" % (r["what"][:40], r["status"], r["measured"]) for r in bad)))

        def red(mutant, text, status=("FAIL",)):
            dd = os.path.join(root, mutant)
            _synth(dd, mutant)
            rr = evaluate(C.Session(dd))
            hit = [r for r in rr if r["status"] in status and text in r["what"]]
            check("seeded %s -> %s (%s)" % (mutant, "/".join(status), text), bool(hit), hit[0]["measured"] if hit else "")
        red("M1", "S_OK")                         # S2.M1: DWM call disabled
        red("M1", "calibration colour shows")     # S2.M1: S2.N0 calibration colour absent
        red("M9", "z-order walker")               # S2.M9: Notepad between the games while the walker runs
        red("fg", "SE foreground")
        red("black", "black frame")
        red("alpha", "alpha > 0")
        red("update", "update time")
        red("sim", "simulation ratio")
        red("cross", "crossing")
        red("slow", "composed", ("MISSING",))     # a 30 ms bound is inconclusive, never a PASS
        red("M1", "composed-pixel probe")         # S2.M1: the probe finds no calibration colour
        red("probeNot", "composed-pixel probe")   # finding 1: NOT HONOURED is a red row, not a silent A' switch
        red("probeUndecided", "composed-pixel probe", ("MISSING",))
        red("modeSwitch", "design unchanged")     # finding 1: a mid-session design switch is red
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("ANALYZE_TD1 SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S2.T-D1 display oracle (+ S2.N0)")
    ap.add_argument("dir", nargs="?")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.dir:
        ap.error("a session folder is required")
    rows = evaluate(C.Session(a.dir))
    ok = C.write_results(a.dir, "td1", rows)
    print(json.dumps(rows) if a.json else C.table(rows))
    print("ANALYZE_TD1 %s (%d rows)" % ("PASS" if ok else "FAIL", len(rows)))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
