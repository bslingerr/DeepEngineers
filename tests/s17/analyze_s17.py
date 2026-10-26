"""S17 oracle table (docs/S17-BRIEF.md; CONTRACT section 5.4 "S17 conventions").

 python -I tests/s17/analyze_s17.py <folder with se-*.jsonl / sn-*.jsonl> [--lenient] [--json]
 python -I tests/s17/analyze_s17.py --se-log <se-*.jsonl> --sn-log <sn-*.jsonl> [--lenient] [--json]
 python -I tests/s17/analyze_s17.py --selftest

py run`, S17 on by default; `launcher.py s17 --notes`); this table judges
the s17.catch rows of both logs (never the plugins' own verdicts):

 S17.PIN the twin's canonical text hashes to the pinned value, and the C# source pins the same number
 S17.CATCH.ACK every SE request (phase request) ends exactly once: an ack (Subnautica's answer) or a timeout; every
 refusal names its reason; every Subnautica row names its result; requests with zero acks FAIL
 S17.CATCH.ONCE no fish twice: no instance given twice by SE, none caught twice by Subnautica, no reqSeq handled twice
 by Subnautica, no request of an instance while an earlier request of it is unresolved
 S17.CATCH.ITEM every Caught answer gives the item (phase given, inventory or dropped) within AckTimeoutMs of the
 request (request -> item <= 1,000 ms); a NoItem refusal (the world mod's definition missing) is a FAIL
 S17.CATCH.SN across the logs: every SE request has a Subnautica row of the same reqSeq, every item SE gave
 has Subnautica's Caught row of the same reqSeq and instance, and every fish Subnautica removed was given on
 SE (in time, or on a late Caught answer; else the fish is lost: FAIL)
 S17.INFO INFO: requests, results, refusals by reason, items given / dropped

"""
from __future__ import annotations

import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tools", "s17"))
import s17_ref as T   # noqa: E402

ACK_TIMEOUT_MS = T.CATCH["AckTimeoutMs"]
# synthetic seeded failures of the self-test (the rows each must turn red)
MUTANT_ROWS = {"S17.X1": ("S17.CATCH.ACK",), "S17.X2": ("S17.CATCH.ONCE",), "S17.X3": ("S17.CATCH.ITEM",), "S17.X4": ("S17.CATCH.SN",),
               "S17.X5": ("S17.CATCH.ACK", "S17.CATCH.SN")}
BAD = ("FAIL", "MISSING")

def row(rows, cid, status, measured, threshold, what, required=True):
    rows.append({"id": cid, "status": status, "measured": measured, "threshold": threshold, "what": what, "required": required})

def verdict(rows, strict=True) -> bool:
    bad = BAD if strict else ("FAIL",)
    return not any(r.get("required", True) and r["status"] in bad for r in rows)

def table(rows) -> str:
    out = ["%-15s %-8s %-44s %-24s %s" % ("check", "status", "measured", "threshold", "what"), "-" * 150]
    for r in rows:
        out.append("%-15s %-8s %-44s %-24s %s" % (r["id"], r["status"] + ("" if r.get("required", True) else "*"),
                                                 str(r["measured"])[:44], str(r["threshold"])[:24], r["what"]))
    return "\n".join(out)

def read_jsonl(paths):
    out = []
    for p in paths:
        try:
            with open(p, encoding="utf-8", errors="replace") as f:
                for line in f:
                    line = line.strip()
                    if not line.startswith("{"):
                        continue
                    try:
                        rec = json.loads(line)
                    except ValueError:
                        continue
                    ev = rec.get("ev") or rec.get("event")
                    if not isinstance(ev, str):
                        continue
                    d = dict(rec.get("data")) if isinstance(rec.get("data"), dict) else dict(rec)
                    d["_ev"] = ev
                    d["_src"] = p   # Unity instance ids restart per Subnautica process: duplicates are judged per file
                    out.append(d)
        except OSError:
            continue
    return out

def pin_row(rows):
    t = T.canonical_text()
    h = T.fnv1a64(t.encode("ascii"))
    cs = T.cs_pinned_hash()
    ok = h == T.PINNED_HASH and cs == T.PINNED_HASH
    row(rows, "S17.PIN", "PASS" if ok else "FAIL", "0x%016x / C# %s" % (h, "0x%016x" % cs if cs is not None else "?"), "0x%016x" % T.PINNED_HASH,
        "the twin's text and the C# source pin the same hash")

def evaluate_events(se, sn, have_sn=None):
    rows = []
    pin_row(rows)
    se = [e for e in se if e["_ev"] == "s17.catch"]
    sn = [e for e in sn if e["_ev"] == "s17.catch"]
    if have_sn is None:
        have_sn = bool(sn)
    reqs = [e for e in se if e.get("phase") == "request"]
    ends = {}
    for e in se:
        if e.get("phase") in ("ack", "timeout"):
            ends.setdefault(e.get("seq"), []).append(e)
    # ACK
    if not reqs:
        row(rows, "S17.CATCH.ACK", "MISSING", "0 requests", "each ends once", "no catch request in the session")
    else:
        bad = [r.get("seq") for r in reqs if len(ends.get(r.get("seq"), [])) != 1]
        noreason = [e.get("seq") for e in se if e.get("phase") in ("refused", "timeout", "late") and not e.get("reason")]
        noresult = [e.get("seq") for e in sn if e.get("result") not in T.RESULTS]
        nacks = sum(1 for e in se if e.get("phase") == "ack")
        ok = not bad and not noreason and not noresult and nacks > 0
        row(rows, "S17.CATCH.ACK", "PASS" if ok else "FAIL", "%d requests, %d acks, %d unended, %d no reason, %d no result" % (len(reqs), nacks, len(bad), len(noreason), len(noresult)),
            "acks > 0; 0 / 0 / 0", "every request acked or timed out once; refusals with a reason")
    # ONCE
    if not reqs and not sn:
        row(rows, "S17.CATCH.ONCE", "MISSING", "0 requests", "no fish twice", "no catch request in the session")
    else:
        given = [(e.get("_src"), e.get("instance")) for e in se if e.get("phase") == "given"]
        caught = [(e.get("_src"), e.get("instance")) for e in sn if e.get("result") == "Caught"]
        seqs = [(e.get("_src"), e.get("seq")) for e in sn]
        open_inst, overlap = {}, 0
        for e in se:
            ph = e.get("phase")
            if ph == "request":
                if e.get("instance") in open_inst.values():
                    overlap += 1
                open_inst[e.get("seq")] = e.get("instance")
            elif ph in ("ack", "timeout"):
                open_inst.pop(e.get("seq"), None)
        dup = (len(given) - len(set(given))) + (len(caught) - len(set(caught))) + (len(seqs) - len(set(seqs))) + overlap
        row(rows, "S17.CATCH.ONCE", "PASS" if dup == 0 else "FAIL", "%d duplicates (given %d, caught %d, seqs %d, overlapping %d)" %
            (dup, len(given) - len(set(given)), len(caught) - len(set(caught)), len(seqs) - len(set(seqs)), overlap), "0", "no fish requested, caught or given twice")
    # ITEM
    acks = [e for e in se if e.get("phase") == "ack" and e.get("result") == "Caught"]
    if not acks:
        row(rows, "S17.CATCH.ITEM", "MISSING", "0 Caught answers", "item <= %d ms" % ACK_TIMEOUT_MS, "no fish caught in the session")
    else:
        gv = {e.get("seq"): e for e in se if e.get("phase") == "given"}
        late = []
        for a in acks:
            g = gv.get(a.get("seq"))
            if g is None or g.get("where") not in ("inventory", "dropped") or not isinstance(g.get("ms"), (int, float)) or g["ms"] > ACK_TIMEOUT_MS:
                late.append(a.get("seq"))
        ms = [gv[a.get("seq")]["ms"] for a in acks if a.get("seq") in gv and isinstance(gv[a.get("seq")].get("ms"), (int, float))]
        row(rows, "S17.CATCH.ITEM", "PASS" if not late else "FAIL", "%d caught, %d without the item in time; max %s ms" % (len(acks), len(late), max(ms) if ms else "-"),
            "0; <= %d ms" % ACK_TIMEOUT_MS, "each Caught answer puts the raw item in the inventory (or at the feet)")
    # SN cross-check
    if not have_sn:
        row(rows, "S17.CATCH.SN", "MISSING", "no Subnautica rows", "both logs", "Subnautica's log absent", required=False)
    elif not reqs and not sn:
        row(rows, "S17.CATCH.SN", "MISSING", "0 requests", "both logs", "no catch request in the session")
    else:
        sn_caught = {(e.get("seq"), e.get("instance")) for e in sn if e.get("result") == "Caught"}
        sn_seqs = {e.get("seq") for e in sn}
        se_given = {(e.get("seq"), e.get("instance")) for e in se if e.get("phase") == "given"}
        unseen = [r.get("seq") for r in reqs if r.get("seq") not in sn_seqs]
        unbacked = se_given - sn_caught
        lost = sn_caught - se_given
        ok = not unbacked and not lost and not unseen
        row(rows, "S17.CATCH.SN", "PASS" if ok else "FAIL", "%d removed, %d unseen requests, %d items without a removal, %d fish lost" %
            (len(sn_caught), len(unseen), len(unbacked), len(lost)), "0 / 0 / 0", "each request reached Subnautica, each item matches a removed fish, each removed fish was given")
    # INFO
    reasons = {}
    for e in se:
        if e.get("reason"):
            reasons[e["reason"]] = reasons.get(e["reason"], 0) + 1
    results = {}
    for e in sn:
        results[e.get("result")] = results.get(e.get("result"), 0) + 1
    row(rows, "S17.INFO", "INFO", "req %d given %d dropped %d" % (len(reqs), sum(1 for e in se if e.get("where") == "inventory"), sum(1 for e in se if e.get("where") == "dropped")),
        "-", "SE refusals %s; Subnautica results %s" % (reasons or "none", results or "none"), required=False)
    return rows

def evaluate(folder=None, se_logs=None, sn_logs=None):
    se_logs = list(se_logs or [])
    sn_logs = list(sn_logs or [])
    if folder:
        se_logs += sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        sn_logs += sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))
    return evaluate_events(read_jsonl(se_logs), read_jsonl(sn_logs), have_sn=bool(sn_logs))

# ------------------------------------------------------------------------------------------------------------- self-test

def _clean():
    se, sn = [], []
    for seq, inst, res in ((11, 101, "Caught"), (12, 102, "Moved"), (13, 103, "Caught")):
        se.append({"_ev": "s17.catch", "phase": "request", "seq": seq, "instance": inst, "techType": 2504, "ms": 0})
        se.append({"_ev": "s17.catch", "phase": "ack", "seq": seq, "instance": inst, "result": res, "ms": 40})
        if res == "Caught":
            se.append({"_ev": "s17.catch", "phase": "given", "seq": seq, "instance": inst, "result": res, "where": "inventory" if seq == 11 else "dropped", "ms": 41})
        else:
            se.append({"_ev": "s17.catch", "phase": "refused", "seq": seq, "instance": inst, "result": res, "reason": "NotCaught", "ms": 40})
        sn.append({"_ev": "s17.catch", "phase": "request", "seq": seq, "instance": inst, "techType": 2504, "result": res, "distM": 0.3})
    se.append({"_ev": "s17.catch", "phase": "refused", "seq": 0, "instance": 101, "reason": "Again", "ms": 0})
    se.append({"_ev": "s17.catch", "phase": "request", "seq": 14, "instance": 104, "techType": 2515, "ms": 0})
    se.append({"_ev": "s17.catch", "phase": "timeout", "seq": 14, "instance": 104, "reason": "Timeout", "ms": 1001})
    se.append({"_ev": "s17.catch", "phase": "late", "seq": 14, "instance": 104, "result": "Gone", "reason": "Late", "ms": 0})
    sn.append({"_ev": "s17.catch", "phase": "request", "seq": 14, "instance": 104, "techType": 2515, "result": "Gone", "distM": -1})
    # a late Caught answer still gives the item
    se.append({"_ev": "s17.catch", "phase": "request", "seq": 16, "instance": 106, "techType": 2504, "ms": 0})
    se.append({"_ev": "s17.catch", "phase": "timeout", "seq": 16, "instance": 106, "reason": "Timeout", "ms": 1003})
    se.append({"_ev": "s17.catch", "phase": "late", "seq": 16, "instance": 106, "result": "Caught", "reason": "Late", "ms": 0})
    se.append({"_ev": "s17.catch", "phase": "given", "seq": 16, "instance": 106, "result": "Caught", "where": "inventory", "ms": 0})
    sn.append({"_ev": "s17.catch", "phase": "request", "seq": 16, "instance": 106, "techType": 2504, "result": "Caught", "distM": 0.4})
    return se, sn

def _mutate(name, se, sn):
    se = [dict(r) for r in se]
    sn = [dict(r) for r in sn]
    if name == "S17.X1":      # an ack lost: the request never ends
        se = [r for r in se if not (r.get("seq") == 14 and r.get("phase") == "timeout")]
    elif name == "S17.X2":    # a fish caught twice on Subnautica (the same instance under a second seq)
        sn.append({"_ev": "s17.catch", "phase": "request", "seq": 15, "instance": 101, "techType": 2504, "result": "Gone", "distM": -1})
        sn[-1]["result"] = "Caught"
        se.append({"_ev": "s17.catch", "phase": "request", "seq": 15, "instance": 101, "techType": 2504, "ms": 0})
        se.append({"_ev": "s17.catch", "phase": "ack", "seq": 15, "instance": 101, "result": "Caught", "ms": 30})
        se.append({"_ev": "s17.catch", "phase": "given", "seq": 15, "instance": 101, "result": "Caught", "where": "inventory", "ms": 31})
    elif name == "S17.X3":    # the item 1.5 s after the request
        for r in se:
            if r.get("phase") == "given" and r.get("seq") == 13:
                r["ms"] = 1500
    elif name == "S17.X4":    # Subnautica removed a fish SE never gave (lost)
        sn.append({"_ev": "s17.catch", "phase": "request", "seq": 99, "instance": 199, "techType": 2515, "result": "Caught", "distM": 0.2})
    elif name == "S17.X5":    # a broken bridge: every request times out, Subnautica never sees one (its log present)
        se = [r for r in se if r.get("phase") == "request"]
        se += [{"_ev": "s17.catch", "phase": "timeout", "seq": r["seq"], "instance": r["instance"], "reason": "Timeout", "ms": 1000} for r in list(se)]
        sn = []
    return se, sn

def selftest() -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S17 analyzer self-test (tests/s17/analyze_s17.py --selftest)")
    check(T.selftest() == 0, "the twin's self-test passes")
    se, sn = _clean()
    st = {r["id"]: r["status"] for r in evaluate_events(se, sn)}
    check(all(s in ("PASS", "INFO") for s in st.values()), "clean session: every row PASS (%s)" % ", ".join("%s=%s" % kv for kv in st.items() if kv[1] not in ("PASS", "INFO")))
    for name, red in sorted(MUTANT_ROWS.items()):
        se2, sn2 = _mutate(name, se, sn)
        st2 = {r["id"]: r["status"] for r in evaluate_events(se2, sn2, have_sn=True)}
        reds = sorted(k for k, v in st2.items() if v == "FAIL")
        check(reds == sorted(red), "%s red exactly on %s (got %s)" % (name, ",".join(red), ",".join(reds) or "none"))
    se7, sn7 = _clean()
    for r in sn7:
        r["_src"] = "sn-a.jsonl"
    sn7.append({"_ev": "s17.catch", "phase": "request", "seq": 21, "instance": 101, "techType": 2504, "result": "Caught", "distM": 0.1, "_src": "sn-b.jsonl"})
    se7 += [{"_ev": "s17.catch", "phase": "request", "seq": 21, "instance": 101, "techType": 2504, "ms": 0},
            {"_ev": "s17.catch", "phase": "ack", "seq": 21, "instance": 101, "result": "Caught", "ms": 20},
            {"_ev": "s17.catch", "phase": "given", "seq": 21, "instance": 101, "result": "Caught", "where": "inventory", "ms": 21, "_src": "se-b.jsonl"}]
    check({r["id"]: r["status"] for r in evaluate_events(se7, sn7)}.get("S17.CATCH.ONCE") == "PASS", "the same instance id in two Subnautica sessions is no double catch")
    st3 = {r["id"]: r["status"] for r in evaluate_events([], [])}
    check(st3.get("S17.CATCH.ACK") == "MISSING" and st3.get("S17.CATCH.ITEM") == "MISSING", "an empty session is MISSING")
    se4, sn4 = _clean()
    for r in se4:
        if r.get("phase") == "given" and r.get("seq") == 11:
            r["phase"], r["reason"] = "refused", "NoItem"
    check({r["id"]: r["status"] for r in evaluate_events(se4, sn4)}.get("S17.CATCH.ITEM") == "FAIL", "a NoItem refusal turns ITEM red")
    se5, _sn5 = _clean()
    st5 = {r["id"]: r for r in evaluate_events(se5, [], have_sn=False)}
    check(st5["S17.CATCH.SN"]["status"] == "MISSING" and not st5["S17.CATCH.SN"]["required"] and verdict(list(st5.values()), strict=True),
          "without Subnautica's log the cross-check is MISSING and not required")
    import tempfile
    d = tempfile.mkdtemp(prefix="s17-selftest-")
    try:
        se6, sn6 = _clean()
        with open(os.path.join(d, "se-x.jsonl"), "w", encoding="utf-8") as f:
            for r in se6:
                rr = {k: v for k, v in r.items() if k != "_ev"}
                rr["ev"] = "s17.catch"
                f.write(json.dumps(rr) + "\n")
        with open(os.path.join(d, "sn-x.jsonl"), "w", encoding="utf-8") as f:
            for r in sn6:
                f.write(json.dumps({"t": "x", "event": "s17.catch", "ev": "s17.catch", "data": {k: v for k, v in r.items() if k != "_ev"}}) + "\n")
        st6 = {r["id"]: r["status"] for r in evaluate(d)}
        check(all(s in ("PASS", "INFO") for s in st6.values()), "folder reader (flat se rows, nested sn rows)")
    finally:
        for p in glob.glob(os.path.join(d, "*")):
            os.remove(p)
        os.rmdir(d)
    ok = all(results)
    print("S17 ANALYZE SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv):
    if argv and argv[0] == "--selftest":
        return selftest()
    folder, se_logs, sn_logs = None, [], []
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--se-log" and i + 1 < len(argv):
            se_logs.append(argv[i + 1]); i += 2; continue
        if a == "--sn-log" and i + 1 < len(argv):
            sn_logs.append(argv[i + 1]); i += 2; continue
        if not a.startswith("--") and folder is None:
            folder = a
        i += 1
    if folder is None and not se_logs and not sn_logs:
        print(__doc__)
        return 2
    rows = evaluate(folder, se_logs, sn_logs)
    ok = verdict(rows, strict="--lenient" not in argv)
    if "--json" in argv:
        print(json.dumps(rows, indent=1, default=str))
    else:
        print(table(rows))
    print("S17 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
