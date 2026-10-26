"""S1.N6 recorder: FMOD events started in Subnautica per hold (ROADMAP S1.N6; the PDA-voice count also feeds S1.N9).

Reads the SN plugin's `s1.fmod` events (frame, qpc, path, source, isUnderwater; schema in tests/s1/check_wm.py) and
counts them per hold. Holds come from, in order: --window FROM TO (QPC seconds or ISO UTC), the launcher session log
(`"ev": "hold"` with label, qpcFrom/qpcTo, utcFrom/utcTo), `s1.hold` begin/end events in either log, else the whole
SN log is one hold. For every hold: duration, events per path and per category, the rate per 60 s, and the
`isUnderwater` values seen with the events. The S1 plan has two 60 s holds at the WM-2 point: hold60 with the
Subnautica plugin's IsUnderwater left false (de.underwater off) and hold60u with its global variant (de.underwater
global). S1. the
one pass/fail line here is S1.N9's "no PDA voice event in 60 s", judged on hold60 only.

Categories (case-insensitive regular expressions on "path source"; --category NAME=REGEX adds or replaces one):
 waterAmbience water[ _]?ambience | ambience.*water | surface
 breathing breath
 pdaVoice pda | voice | /vo/ | story | notification
 depthWarning depth | rebreather

 python -I tests/s1/fmod_events.py --sn SN.jsonl [--session S.jsonl] [--se SE.jsonl] [--hold hold60,hold60u]
 [--json OUT]
 python -I tests/s1/fmod_events.py --selftest
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import check_wm as W  # noqa: E402

CATEGORIES = {
    "waterAmbience": r"water[ _]?ambience|ambience.*water|surface",
    "breathing": r"breath",
    "pdaVoice": r"pda|voice|/vo/|story|notification",
    "depthWarning": r"depth|rebreather",
}

def _iso(v):
    if not isinstance(v, str):
        return None
    try:
        s = v.rstrip("Z")
        dt = datetime.datetime.fromisoformat(s[:26])
        return dt.replace(tzinfo=datetime.timezone.utc).timestamp()
    except ValueError:
        return None

def event_time(e: dict, freq: int):
    """(clock, seconds): 'qpc' when a QPC stamp is available (preferred), else 'utc'."""
    q = W.num(e, "qpc", "qpcUsed")
    if q is not None:
        return "qpc", q / freq
    t = e.get("_t")
    if isinstance(t, (int, float)):
        return "qpc", float(t)            # stand-in emitters stamp "t" in QPC seconds
    u = _iso(t)
    if u is not None:
        return "utc", u
    return None, None

def holds_from(session, se_events, sn_events, freq, labels=None, window=None) -> list:
    """[{label, qpc: (from, to) or None, utc: (from, to) or None, seconds}]"""
    out = []
    if window:
        a, b = window
        try:
            fa, fb = float(a), float(b)
            out.append({"label": "window", "qpc": (fa, fb), "utc": None, "seconds": fb - fa})
        except ValueError:
            ua, ub = _iso(a), _iso(b)
            out.append({"label": "window", "qpc": None, "utc": (ua, ub), "seconds": (ub - ua) if ua and ub else None})
        return out
    for r in session:
        if r.get("ev") == "hold":
            q = (W.num(r, "qpcFrom"), W.num(r, "qpcTo"))
            u = (_iso(r.get("utcFrom")), _iso(r.get("utcTo")))
            out.append({"label": r.get("label"), "qpc": (q[0] / freq, q[1] / freq) if None not in q else None,
                        "utc": u if None not in u else None, "seconds": W.num(r, "seconds")})
    if not out:
        begins = {}
        for e in list(se_events) + list(sn_events):
            if e["_ev"] != "s1.hold":
                continue
            clock, t = event_time(e, freq)
            if t is None:
                continue
            if e.get("phase") == "begin":
                begins[e.get("label")] = (clock, t)
            elif e.get("phase") == "end" and e.get("label") in begins:
                c0, t0 = begins.pop(e.get("label"))
                out.append({"label": e.get("label"), "qpc": (t0, t) if c0 == "qpc" else None,
                            "utc": (t0, t) if c0 == "utc" else None, "seconds": t - t0})
    if labels:
        out = [h for h in out if h["label"] in labels]
    return out

def count(sn_events, holds, freq, categories=None) -> list:
    cats = {k: re.compile(v, re.I) for k, v in (categories or CATEGORIES).items()}
    fm = [e for e in sn_events if e["_ev"] == "s1.fmod"]
    res = []
    for h in holds:
        evs = []
        for e in fm:
            clock, t = event_time(e, freq)
            win = h["qpc"] if clock == "qpc" else h["utc"]
            if h["qpc"] is None and h["utc"] is None:
                evs.append(e)
            elif win is not None and win[0] <= t <= win[1]:
                evs.append(e)
        per_path, per_cat, under = {}, {k: 0 for k in cats}, {}
        for e in evs:
            key = str(e.get("path") or e.get("event") or "?")
            per_path[key] = per_path.get(key, 0) + 1
            hay = "%s %s" % (key, e.get("source") or "")
            for k, rx in cats.items():
                if rx.search(hay):
                    per_cat[k] += 1
            u = str(e.get("isUnderwater"))
            under[u] = under.get(u, 0) + 1
        secs = h.get("seconds")
        if secs is None and h["qpc"]:
            secs = h["qpc"][1] - h["qpc"][0]
        rate = {k: round(v * 60.0 / secs, 2) for k, v in per_cat.items()} if secs else None
        res.append({"label": h["label"], "seconds": secs, "events": len(evs), "perPath": per_path,
                    "perCategory": per_cat, "per60s": rate, "isUnderwater": under})
    return res

def audio_summaries(sn_records) -> list:
    """The SN plugin's 60 s audio summary lines (`DE.Audio 60s events=N distinct=K top=<path>x<n>,... underwaterVariant=
 off|global`, top 12 paths only) as windows [end - 60 s, end] in UTC seconds."""
    out = []
    for rec in sn_records:
        text = rec.get("text") if isinstance(rec.get("text"), str) else rec.get("msg")
        if not isinstance(text, str) or not text.startswith("DE.Audio 60s"):
            continue
        end = _iso(rec.get("t"))
        kv = W.parse_kv(text)
        top = {}
        for item in str(kv.get("top", "")).split(","):
            path, sep, n = item.rpartition("x")
            if sep and n.isdigit() and path:
                top[path] = top.get(path, 0) + int(n)
        out.append({"end": end, "events": kv.get("events", 0), "distinct": kv.get("distinct", 0), "top": top,
                    "variant": kv.get("underwaterVariant")})
    return out

def summaries_for(holds, summaries, categories=None) -> list:
    """Summaries whose 60 s window overlaps a hold by at least half (UTC); per-category counts of their top paths."""
    cats = {k: re.compile(v, re.I) for k, v in (categories or CATEGORIES).items()}
    res = []
    for h in holds:
        if not h.get("utc") or None in h["utc"]:
            continue
        a, b = h["utc"]
        for sm in summaries:
            if sm["end"] is None:
                continue
            lo, hi = sm["end"] - 60.0, sm["end"]
            if min(b, hi) - max(a, lo) >= 30.0:
                per_cat = {k: sum(n for p, n in sm["top"].items() if rx.search(p)) for k, rx in cats.items()}
                res.append({"label": h["label"], "events": sm["events"], "distinct": sm["distinct"],
                            "perCategory": per_cat, "variant": sm["variant"], "top": sm["top"]})
    return res

def evaluate(sn_records, se_records=(), session_records=(), labels=("hold60", "hold60u"), window=None, categories=None,
             strict=True, pda_labels=("hold60", "window", "whole log")) -> dict:
    """S1.N6 rows for every hold; the S1.N9 "no PDA voice" row only for the vanilla hold (hold60: IsUnderwater left
 false). The hold60u variant (de.underwater global) is expected to fire depth warnings: recorded, not judged."""
    sn = W.s1_events(sn_records)
    se = W.s1_events(se_records)
    freq = W.qpc_frequency(se, sn)
    holds = holds_from(session_records, se, sn, freq, labels, window)
    if not holds and not labels:
        holds = [{"label": "whole log", "qpc": None, "utc": None, "seconds": None}]
    rep = W.Report(strict)
    res = count(sn, holds, freq, categories)
    if not res:
        rep.row("S1.N6", "FMOD events per hold (%s)" % ",".join(labels or ()), W.MISSING, "no hold found",
                "a 60 s hold", "fmod")
    fm_total = sum(1 for e in sn if e["_ev"] == "s1.fmod")
    if not fm_total:
        # the SN plugin's own 60 s summaries (top 12 paths), when it logs no per-event s1.fmod records
        sums = summaries_for(holds, audio_summaries(sn_records), categories)
        for s in sums:
            rep.row("S1.N6", "DE.Audio 60s summary overlapping hold %s (variant %s, top-12 paths): %s" % (
                s["label"], s["variant"], ", ".join("%s %d" % kv for kv in s["perCategory"].items())), W.RECORD,
                "%s events" % s["events"], "recorded; decision -> MODLOG", "fmod", required=False, top=s["top"])
            if s["label"] in pda_labels:
                pda = s["perCategory"].get("pdaVoice", 0)
                rep.row("S1.N9", "PDA voice events in hold %s (DE.Audio summary, top 12 paths)" % s["label"],
                        W.PASS if pda == 0 else W.FAIL, str(pda), "0", "fmod")
        if sums:
            return {"tool": "fmod_events", "ok": rep.ok(), "rows": rep.rows, "holds": res, "fmodEvents": 0,
                    "audioSummaries": sums}
    for h in res:
        rep.row("S1.N6", "FMOD events started in hold %s (%.0f s): %s" % (
            h["label"], h["seconds"] or 0, ", ".join("%s %d" % kv for kv in h["perCategory"].items())),
            W.RECORD if fm_total else W.MISSING, "%d events" % h["events"], "recorded; decision -> MODLOG", "fmod",
            required=False, perPath=h["perPath"], isUnderwater=h["isUnderwater"])
        if h["label"] not in pda_labels:
            continue
        pda = h["perCategory"].get("pdaVoice", 0)
        rep.row("S1.N9", "PDA voice events in hold %s" % h["label"], (W.PASS if pda == 0 else W.FAIL) if fm_total else
                W.MISSING, str(pda), "0", "fmod")
    return {"tool": "fmod_events", "ok": rep.ok(), "rows": rep.rows, "holds": res, "fmodEvents": fm_total}

def selftest() -> int:
    F = 10_000_000
    sn = [{"event": "s1.fmod", "qpc": int(t * F), "path": "event:/env/water_ambience_loop", "source": "WaterAmbience",
           "isUnderwater": False} for t in range(100, 160, 10)]
    sn += [{"event": "s1.fmod", "qpc": int(105.5 * F), "path": "event:/player/breathing", "source": "BreathingSound",
            "isUnderwater": False}]
    sn += [{"event": "s1.fmod", "qpc": int(300 * F), "path": "event:/env/water_ambience_loop"}]   # outside the hold
    session = [{"ev": "hold", "label": "hold60", "qpcFrom": 100 * F, "qpcTo": 160 * F, "seconds": 60.0}]
    r = evaluate(sn, (), session)
    h = r["holds"][0] if r["holds"] else {}
    ok = r["ok"] and h.get("events") == 7 and h["perCategory"]["waterAmbience"] == 6 and \
        h["perCategory"]["breathing"] == 1 and h["perCategory"]["pdaVoice"] == 0
    print("fmod_events selftest green: %s (%s)" % ("PASS" if ok else "FAIL", json.dumps(h.get("perCategory"))))
    sn.append({"event": "s1.fmod", "qpc": int(130 * F), "path": "event:/player/story/pda_voice_line"})
    r2 = evaluate(sn, (), session)
    red = not r2["ok"] and r2["holds"][0]["perCategory"]["pdaVoice"] == 1
    print("fmod_events selftest mutant PDA voice in the hold: %s" % ("RED (expected)" if red else "GREEN (unexpected)"))
    ok &= red
    # the SN plugin's 60 s summary lines instead of per-event records
    sess2 = [{"ev": "hold", "label": "hold60", "utcFrom": "2026-10-12T01:00:00.000Z", "utcTo": "2026-10-12T01:01:00.000Z",
              "seconds": 60.0}]
    sn2 = [{"t": "2026-10-12T01:00:58.000Z", "event": "DE.Audio",
            "text": "DE.Audio 60s events=9 distinct=2 top=event:/env/water_ambience_loopx7,event:/player/breathingx2 "
                    "suppressed{pda=0} saveBlocked=0 underwaterVariant=off"}]
    r3 = evaluate(sn2, (), sess2)
    s3 = (r3.get("audioSummaries") or [{}])[0]
    good = r3["ok"] and s3.get("perCategory", {}).get("waterAmbience") == 7 and s3["perCategory"]["breathing"] == 2
    print("fmod_events selftest DE.Audio 60s summary: %s (%s)" % ("PASS" if good else "FAIL",
                                                                  json.dumps(s3.get("perCategory"))))
    ok &= good
    print("FMOD_EVENTS SELFTEST", "PASS" if ok else "FAIL")
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S1.N6 FMOD events per hold")
    ap.add_argument("--sn", action="append", default=[])
    ap.add_argument("--se", action="append", default=[])
    ap.add_argument("--session", default=None)
    ap.add_argument("--hold", default="hold60,hold60u", help="comma list of hold labels ('' = every hold)")
    ap.add_argument("--window", nargs=2, metavar=("FROM", "TO"), default=None)
    ap.add_argument("--category", action="append", default=[], help="NAME=REGEX")
    ap.add_argument("--lenient", action="store_true")
    ap.add_argument("--json", default=None)
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(sys.argv[1:] if argv is None else argv)
    if a.selftest:
        return selftest()
    if not a.sn:
        ap.error("--sn is required (or --selftest)")
    cats = dict(CATEGORIES)
    for c in a.category:
        k, _, v = c.partition("=")
        cats[k] = v
    labels = tuple(x for x in a.hold.split(",") if x) or None
    r = evaluate(W.read_jsonl(a.sn), W.read_jsonl(a.se), W.read_jsonl(a.session) if a.session else (), labels,
                 a.window, cats, strict=not a.lenient)
    for h in r["holds"]:
        print("hold %s: %s s, %d events; per category %s; per 60 s %s; isUnderwater %s" % (
            h["label"], "%.1f" % h["seconds"] if h["seconds"] else "?", h["events"], json.dumps(h["perCategory"]),
            json.dumps(h["per60s"]), json.dumps(h["isUnderwater"])))
        for k, v in sorted(h["perPath"].items(), key=lambda kv: -kv[1]):
            print("   %5d  %s" % (v, k))
    print(W.table(r["rows"]))
    print("FMOD_EVENTS %s" % ("PASS" if r["ok"] else "FAIL"))
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(r, f, indent=1)
    return 0 if r["ok"] else 1

if __name__ == "__main__":
    sys.exit(main())
