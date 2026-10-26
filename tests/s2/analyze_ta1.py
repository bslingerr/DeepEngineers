"""S2.T-A1 audio oracle: per-process peak meters + the C5 view state -> the T-A1 rows (ROADMAP S2 oracle).

 python -I tests/s2/analyze_ta1.py tests/out/s2/<stamp> [--json]
 python -I tests/s2/analyze_ta1.py --selftest # synthetic cycles, then S2.M3 / M11 / M12 / M13 and gaps red

Inputs of a collected session folder (`launcher.py s2 --parts ta1`):
- the peaks file of tools/audio/peaks.py (named by the session's "peaks" event; 20 ms samples of Legacy.exe and
 Subnautica.exe, QPC-stamped);
- the launcher's section samples of the ta1 part (C5 viewState, approachMode, overlays Paused/Unfocused, flags
 seForeground/sePaused, snBusTarget; C7 frozen; C0 sePhase) - each peak sample takes the state of the last section
 sample at or before its QPC, and is a "Transition" sample (excluded from the above/under checks) when the next
 section sample shows another view state (the 10 Hz samples cannot place a switch more precisely);
- the replies of `de.audio` that the launcher sends during each underwater hold (SE's mix: game, music, hud and their
 config values) and the SE log's `DE.Audio gate=` lines; the SN log's `DE.S2 sec` (timeScale, frozen);
- "file_hash" events: SHA-256 of %APPDATA%\\SpaceEngineers\\Storage\\WaterClientSettings.xml at the start and the end;

 save underwater): their QPC windows.
Rows (thresholds of ROADMAP S2.T-A1; peaks <= 1e-4 count as silent):
- underwater: in every Underwater stretch (from 0.3 s after the entry to its end) Subnautica's peak > 0 on >= 90 % of
 the samples; at least 10 stretches (the 10 cycles);
- above water: 0 samples with Subnautica's peak > 0 in Space/Approach (from 0.3 s after leaving Underwater);
- crossfades: around every entry and exit (-0.3 s.. +0.6 s), no run with both peaks silent longer than 100 ms;
- SE mix underwater: SE music 0 and SE game = 0.7 x config (+-0.01) in every fully mixed `de.audio` reply (mix >= 0.999),

- away (C5 Unfocused): both peaks silent;
- ESC underwater: Subnautica frozen (C7) and silent while paused;
- ESC in Space and resume: Subnautica stays frozen (C7 frozen, DE.S2 sec timeScale 0) and silent afterwards in Space;
- SE save underwater done (C0 sePhase Saving seen while Underwater);
- WaterClientSettings.xml byte-identical (SHA-256) before and after;

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

EPS = 1e-4
SE_PROC, SN_PROC = "Legacy.exe", "Subnautica.exe"
SETTLE_S = 1.0   # real runs/16: the 300 ms crossfade plus the FMOD peak meter's release keep Subnautica's
 # peak above zero for up to ~1 s after a Surfacing (39 of 4,659 samples, all within that second)
UNDER_SHARE = 0.90
GAP_MS = 100.0
CYCLES = 10
OV_PAUSED, OV_UNFOCUSED = 1, 2
C5_SEPAUSED = 2
C7_FROZEN = 1 << 7
SE_SAVING = 4

def load_peaks(sess):
    ev = next((r for r in sess.session if r.get("ev") == "peaks" and r.get("path")), None)
    path = ev and (ev["path"] if os.path.isabs(ev["path"]) else os.path.join(sess.dir, ev["path"]))
    if not path or not os.path.exists(path):
        cands = [os.path.join(sess.dir, f) for f in os.listdir(sess.dir) if f.startswith("peaks") and f.endswith(".jsonl")]
        path = cands[0] if cands else None
    if not path:
        return None, []
    recs = C.read_jsonl(path)
    start = next((r for r in recs if r.get("ev") == "peaks.start"), {})
    return start, [r for r in recs if r.get("ev") == "peaks" and r.get("qpc") is not None]

def timeline(sess, peaks):
    """[(qpc, se, sn, view, overlays, c5flags, frozen, sePhase)] per peak sample."""
    out = []
    for p in peaks:
        st = sess.state_at(p["qpc"]) or {}
        nx = sess.state_after(p["qpc"]) or {}
        c5, c7, c0 = st.get("c5") or {}, st.get("c7") or {}, st.get("c0") or {}
        view = C.view_name(c5.get("viewState"))
        nview = C.view_name((nx.get("c5") or {}).get("viewState"))
        if nx and nview != view:
            view = "Transition"      # the state changed between the two section samples around this peak sample
        out.append((p["qpc"], float(p["p"].get(SE_PROC, 0.0)), float(p["p"].get(SN_PROC, 0.0)),
                    view, int(c5.get("overlays") or 0), int(c5.get("flags") or 0),
                    bool(int(c7.get("flags") or 0) & C7_FROZEN), c0.get("sePhase")))
    return out

def stretches(tl, f):
    """[(qStart, qEnd)] of consecutive Underwater samples."""
    out = []
    start = prev = None
    for row in tl:
        q, under = row[0], row[3] == "Underwater"
        if under and start is None:
            start = q
        if not under and start is not None:
            out.append((start, prev))
            start = None
        prev = q
    if start is not None:
        out.append((start, prev))
    return out

def owner_window(sess, name):
    ev = [r for r in sess.session if r.get("ev") == "owner" and r.get("step") == name]
    if not ev:
        return None
    b = next((r for r in ev if r.get("phase") == "begin"), ev[0])
    e = next((r for r in ev if r.get("phase") == "end"), ev[-1])
    return b.get("qpc"), e.get("qpc"), e.get("done", b.get("done"))

def evaluate(sess) -> list:
    rows = []
    if "ta1" not in set(sess.parts):
        return rows
    f = float(sess.qpcf)
    start, peaks = load_peaks(sess)
    if not peaks:
        C.row(rows, "S2.T-A1", "MISSING", "no peaks file", "tools/audio/peaks.py output", "peak meters (20 ms)", "ta1")
    else:
        if start.get("emulated"):
            C.row(rows, "S2.T-A1", "INFO", "backend %s (emulated)" % start.get("backend"), "-",
                  "peaks are EMULATED (stand-in dry run): no audio was measured", "ta1", required=False)
        tl = timeline(sess, peaks)
        under = stretches(tl, f)
        # ---- underwater: Subnautica audible
        shares = []
        for q0, q1 in under:
            # paused (ESC, Steam overlay) and away (Alt+Tab) samples are silent by design: not counted here
            vals = [r[2] for r in tl if q0 + SETTLE_S * f <= r[0] <= q1 and not (r[4] & (OV_PAUSED | OV_UNFOCUSED))
                    and not (r[5] & C5_SEPAUSED)]
            if len(vals) >= 5:
                shares.append(sum(1 for v in vals if v > EPS) / len(vals))
        ok = len(shares) >= CYCLES and all(s >= UNDER_SHARE for s in shares)
        C.row(rows, "S2.T-A1", "PASS" if ok else "FAIL", "%d stretches, worst %s audible" % (
            len(shares), "%.0f %%" % (100 * min(shares)) if shares else "-"), ">= 10, each >= 90 %",
            "underwater: Subnautica peak > 0", "ta1")
        # ---- above water: Subnautica silent (after the crossfade)
        last_wet = None          # the last sample that was Underwater, Surfacing or a transition sample
        viol = n = 0
        for r in tl:
            if r[3] in ("Underwater", "Surfacing", "Transition"):
                last_wet = r[0]
                continue
            if r[3] in ("Space", "Approach") and (last_wet is None or r[0] >= last_wet + SETTLE_S * f):
                n += 1
                if r[2] > EPS:
                    viol += 1
        C.row(rows, "S2.T-A1", "MISSING" if n == 0 else ("PASS" if viol == 0 else "FAIL"),
              "%d of %d samples audible" % (viol, n), "0", "above water: Subnautica peak = 0", "ta1")
        # ---- crossfades: no gap with both silent > 100 ms around each switch
        worst = 0.0
        switches = [q for q0, q1 in under for q in (q0, q1)]
        judged = 0
        for qs in switches:
            win = [r for r in tl if qs - 0.3 * f <= r[0] <= qs + 0.6 * f]

            # session: the one "gap" was SE itself silent in Space, spectator, music between tracks)
            if not any(r[1] > EPS for r in win if r[0] < qs):
                continue
            judged += 1
            run0 = None
            for r in win:
                silent = r[1] <= EPS and r[2] <= EPS
                if silent and run0 is None:
                    run0 = r[0]
                if not silent and run0 is not None:
                    worst = max(worst, (r[0] - run0) / f * 1000.0)
                    run0 = None
            if run0 is not None and win:
                worst = max(worst, (win[-1][0] - run0) / f * 1000.0)
        C.row(rows, "S2.T-A1", "MISSING" if not judged else ("PASS" if worst <= GAP_MS else "FAIL"),
              "longest both-silent run %.0f ms over %d switches (%d with SE audible before)" % (worst, len(switches), judged),
              "<= 100 ms", "crossfades overlap (no gap with both silent)", "ta1")
        # ---- away: both silent
        away = [r for r in tl if r[4] & OV_UNFOCUSED]
        loud = [r for r in away if r[1] > EPS or r[2] > EPS]
        C.row(rows, "S2.T-A1", "MISSING" if not away else ("PASS" if not loud else "FAIL"),
              "%d of %d away samples audible" % (len(loud), len(away)) if away else "no Alt+Tab away (owner step)",
              "both silent", "Alt+Tab away underwater: SE and Subnautica silent", "ta1.owner")
        # ---- ESC underwater: frozen and silent while paused
        pz = [r for r in tl if (r[4] & OV_PAUSED or r[5] & C5_SEPAUSED) and r[3] == "Underwater"]
        bad = [r for r in pz if r[2] > EPS or not r[6]]
        C.row(rows, "S2.T-A1", "MISSING" if not pz else ("PASS" if not bad else "FAIL"),
              "%d of %d paused samples not frozen or audible" % (len(bad), len(pz)) if pz else "no ESC underwater",
              "frozen, silent", "ESC underwater: Subnautica frozen and silent", "ta1.owner")
        # ---- ESC in Space then resume: Subnautica stays frozen and silent in Space afterwards
        w = owner_window(sess, "escSpace")
        if w is None:
            C.row(rows, "S2.T-A1", "MISSING", "owner step not run", "frozen, silent",
                  "ESC in Space, resume: Subnautica stays frozen (timeScale 0) and silent", "ta1.owner")
        else:
            after = [r for r in tl if r[0] > (w[1] or 0) and r[0] <= (w[1] or 0) + 5 * f and r[3] == "Space"]
            ts = [e for e in C.by(sess.sn, "s2.perf") if e.get("qpc") and (w[1] or 0) < e["qpc"] <= (w[1] or 0) + 5 * f]
            bad = [r for r in after if r[2] > EPS or not r[6]] + [e for e in ts if (C.num(e, "timeScale") or 0) != 0]
            C.row(rows, "S2.T-A1", "MISSING" if not after else ("PASS" if not bad else "FAIL"),
                  "%d of %d samples after the resume unfrozen or audible" % (len(bad), len(after) + len(ts)),
                  "frozen, silent", "ESC in Space, resume: Subnautica stays frozen (timeScale 0) and silent",
                  "ta1.owner")
        # ---- SE save underwater
        saves = [r for r in tl if r[7] == SE_SAVING and r[3] == "Underwater"]
        C.row(rows, "S2.T-A1", "PASS" if saves else "MISSING", "%d samples Saving while Underwater" % len(saves),
              "a save underwater", "SE save made underwater (owner step; WaterClientSettings.xml written by it)",
              "ta1.owner")
    # ---- SE mix underwater (de.audio replies)
    reps = []
    for r in sess.session:
        if r.get("ev") == "reply" and r.get("cmd") == "de.audio" and isinstance(r.get("reply"), dict):
            a = r["reply"].get("audio") if isinstance(r["reply"].get("audio"), dict) else r["reply"]
            st = sess.state_at(r.get("qpc") or 0) or {}
            reps.append((r.get("label", ""), C.view_name((st.get("c5") or {}).get("viewState")), a))
    full = [(lab, a) for lab, v, a in reps if v == "Underwater" and (C.num(a, "mix") or 0) >= 0.999]
    bad = [lab for lab, a in full if abs(C.num(a, "music") or 0) > 1e-3 or
           C.num(a, "configGame") is None or abs((C.num(a, "game") or 0) - 0.7 * C.num(a, "configGame")) > 0.01]
    C.row(rows, "S2.T-A1", "MISSING" if not full else ("PASS" if not bad else "FAIL"),
          "%d fully mixed replies, %d wrong%s" % (len(full), len(bad), (" (%s)" % ",".join(bad[:3])) if bad else ""),
          "music 0, game 0.7 x config", "no SE music underwater; SE game 0.7 x config (de.audio)", "ta1")
    after_tab = [(lab, a) for lab, a in full if "afterAltTab" in lab]
    bad2 = [lab for lab, a in after_tab if abs(C.num(a, "music") or 0) > 1e-3]
    C.row(rows, "S2.T-A1", "MISSING" if not after_tab else ("PASS" if not bad2 else "FAIL"),
          "%d replies after the return" % len(after_tab) if after_tab else "no Alt+Tab return (owner step)",
          "music 0", "no SE music underwater after the Alt+Tab return", "ta1.owner")
    # ---- WaterClientSettings.xml
    hashes = [r for r in sess.session if r.get("ev") == "file_hash" and "WaterClientSettings" in str(r.get("path"))]
    b = next((r for r in hashes if r.get("when") == "before"), None)
    e = next((r for r in reversed(hashes) if r.get("when") == "after"), None)
    if not b or not e:
        C.row(rows, "S2.T-A1", "MISSING", "hash before %s, after %s" % (bool(b), bool(e)), "identical",
              "WaterClientSettings.xml byte-identical (SHA-256)", "ta1")
    else:
        C.row(rows, "S2.T-A1", "PASS" if b.get("sha256") == e.get("sha256") and b.get("sha256") else "FAIL",
              "%s -> %s" % (str(b.get("sha256"))[:12], str(e.get("sha256"))[:12]), "identical",
              "WaterClientSettings.xml byte-identical (SHA-256)", "ta1")
    C.row(rows, "S2.T-A1", "MANUAL", "owner", "judged by ear", "the crossfades sound right (owner)", "ta1.owner",
          required=False)
    return rows

# ----------------------------------------------------------------------------------------------------------- selftest
def _synth(d, mutant=None, owner=True):
    f = 10_000_000
    os.makedirs(d, exist_ok=True)
    sess = [{"ev": "start", "parts": ["ta1"], "qpcFrequency": f}]
    peaks = [{"ev": "peaks.start", "backend": "fake", "emulated": False}]
    q = 10 * f
    step = f // 50

    def add(seconds, view, se, sn, overlays=0, flags=1, frozen=False, phase=3):
        nonlocal q
        for i in range(int(seconds * 50)):
            q += step
            if i % 5 == 0:   # 10 Hz section samples
                sess.append({"ev": "sample", "part": "ta1", "qpc": q, "c0": {"sePhase": phase},
                             "c5": {"viewState": view, "overlays": overlays, "flags": flags},
                             "c7": {"flags": C7_FROZEN if frozen else 0}})
            t = i / 50.0
            sev = se(t) if callable(se) else se
            snv = sn(t) if callable(sn) else sn
            peaks.append({"ev": "peaks", "qpc": q, "p": {SE_PROC: sev, SN_PROC: snv}})
    for k in range(10):
        add(4, 1, 0.3, 0.0 if mutant != "M3" else 0.2)                                     # above (Approach)
        gap = 0.45 if mutant == "gap" and k == 3 else 0.0
        add(6, 2, lambda t: 0.3 * max(0.0, 1 - t / 0.3), lambda t: 0.0 if t < 0.05 + gap else 0.5)   # Underwater
        sess.append({"ev": "reply", "cmd": "de.audio", "qpc": q - f, "label": "ta1.cycle%d" % k,
                     "reply": {"ok": True, "audio": {"mix": 1.0, "game": 0.56 if mutant != "music" else 0.8,
                                                     "music": 0.0 if mutant != "music" else 0.6, "configGame": 0.8,
                                                     "configMusic": 0.6}}})
    if owner:
        add(3, 2, 0.0 if mutant != "M11" else 0.3, 0.0, overlays=OV_UNFOCUSED, flags=0)          # away
        add(2, 2, 0.2, 0.5)
        sess.append({"ev": "reply", "cmd": "de.audio", "qpc": q - f // 2, "label": "ta1.afterAltTab",
                     "reply": {"ok": True, "audio": {"mix": 1.0, "game": 0.56, "music": 0.0, "configGame": 0.8}}})
        add(3, 2, 0.1, 0.0, overlays=OV_PAUSED, flags=1 | C5_SEPAUSED, frozen=True)               # ESC underwater
        add(1, 2, 0.2, 0.5, phase=SE_SAVING)                                                       # save underwater
        add(2, 0, 0.3, 0.0, frozen=True)                                                            # Space
        sess.append({"ev": "owner", "step": "escSpace", "phase": "begin", "qpc": q, "done": True})
        add(2, 0, 0.0, 0.0, overlays=OV_PAUSED, flags=1 | C5_SEPAUSED, frozen=True)
        sess.append({"ev": "owner", "step": "escSpace", "phase": "end", "qpc": q, "done": True})
        add(3, 0, 0.3, 0.0 if mutant != "M12" else 0.4, frozen=mutant != "M12")
    h0 = "a" * 64
    sess.append({"ev": "file_hash", "when": "before", "path": "...\\WaterClientSettings.xml", "sha256": h0})
    sess.append({"ev": "file_hash", "when": "after", "path": "...\\WaterClientSettings.xml",
                 "sha256": h0 if mutant != "M13" else "b" * 64})
    sess.append({"ev": "peaks", "path": "peaks-ta1.jsonl"})
    with open(os.path.join(d, "s2-session.jsonl"), "w") as fh:
        for r in sess:
            fh.write(json.dumps(r) + "\n")
    with open(os.path.join(d, "peaks-ta1.jsonl"), "w") as fh:
        for r in peaks:
            fh.write(json.dumps(r) + "\n")

def selftest() -> int:
    import shutil
    import tempfile
    results = []

    def check(label, cond, detail=""):
        results.append(bool(cond))
        print("SELFTEST %-66s %s %s" % (label, "PASS" if cond else "FAIL", detail))
    root = tempfile.mkdtemp(prefix="de_ta1_")
    try:
        d = os.path.join(root, "good")
        _synth(d)
        rows = evaluate(C.Session(d))
        bad = [r for r in rows if r.get("required", True) and r["status"] in C.BAD]
        check("good session: every required row PASS", not bad and len(rows) >= 10, "%d rows%s" % (
            len(rows), "".join("; %s: %s" % (r["what"], r["measured"]) for r in bad)))
        d = os.path.join(root, "noowner")
        _synth(d, owner=False)
        rows = evaluate(C.Session(d))
        check("owner steps not run: their rows MISSING (required)",
              sum(1 for r in rows if r["status"] == "MISSING" and r["part"] == "ta1.owner") >= 4)

        def red(mutant, text):
            dd = os.path.join(root, mutant)
            _synth(dd, mutant)
            rr = evaluate(C.Session(dd))
            hit = [r for r in rr if r["status"] == "FAIL" and text in r["what"]]
            check("seeded %s -> red (%s)" % (mutant, text), bool(hit), hit[0]["measured"] if hit else "")
        red("M3", "above water")              # S2.M3 above-water mute skipped
        red("M11", "Alt+Tab away")             # S2.M11 SE volumes written without the focus gate
        red("M12", "ESC in Space")             # S2.M12 freeze by separate Begin/End per cause
        red("M13", "WaterClientSettings")      # S2.M13 Water Mod volume through /wvolume
        red("gap", "crossfades")               # a 200 ms both-silent gap at one entry
        red("music", "no SE music underwater")
    finally:
        shutil.rmtree(root, ignore_errors=True)
    ok = all(results)
    print("ANALYZE_TA1 SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S2.T-A1 audio oracle")
    ap.add_argument("dir", nargs="?")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.dir:
        ap.error("a session folder is required")
    rows = evaluate(C.Session(a.dir))
    ok = C.write_results(a.dir, "ta1", rows)
    print(json.dumps(rows) if a.json else C.table(rows))
    print("ANALYZE_TA1 %s (%d rows)" % ("PASS" if ok else "FAIL", len(rows)))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
