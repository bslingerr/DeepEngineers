"""`launcher.py s17` (docs/S17-BRIEF.md; CONTRACT §5.4 "S17 conventions"): Subnautica's small fish feed the SE character.
Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`).

S17's oracle is the project's free play (`launcher.py run`: S17 is on by default in both plugins; DEEPENGINEERS_S17=off turns
it off on either side); this launcher prints what to do and what to look at, and judges the session's logs with
tests/s17/analyze_s17.py:

 python -I src/DeepEngineers.Launcher/launcher.py s17 --notes
 python -I src/DeepEngineers.Launcher/launcher.py s17 --latest [--lenient] [--json] (the newest se-/sn-*.jsonl pair)
 python -I src/DeepEngineers.Launcher/launcher.py s17 --analyze DIR | --se-log F --sn-log F [--lenient] [--json]
 python -I src/DeepEngineers.Launcher/launcher.py s17 --dry-run | --selftest

It never starts a game, never writes into a game folder, a save, the Mods folder or SE's LastSession; it only reads the logs
under %LOCALAPPDATA%\DeepEngineers\logs (or the files given)."""
from __future__ import annotations

import glob
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
ANALYZER_DIR = os.path.join(ROOT, "tests", "s17")
LOG_DIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeepEngineers", "logs")

NOTES = """S17 free play (owner-attended; launcher.py run, both games, the usual world; S17 is on by default). In this order:

 1. Catch. Under water, empty hands (put the tool away), swim to a small fish (a Peeper, a Bladderfish, a Boomerang...) and
    look straight at it within 2.5 m. Look at: the HUD line "F: catch the Peeper"; press F. The fish vanishes in Subnautica's
    picture (no death, no loot) and "Peeper (raw) added" shows; the item is in your inventory (dropped at your feet when full).
 2. Again. Try a fish that swims away while you press F, and one far away. Look at: nothing caught at more than 2.5 m;
    "The ... got away" when it moved; never two items for one fish.
 3. Eat. Open the inventory, use the raw fish (SE's own consume). Look at: SE's Food stat rises slowly (30 s).
 4. Cook. Put raw fish into a Food Processor, queue "Cook Peeper" (4 s each). Look at: the cooked fish comes out; eating it
    raises the Food stat about twice as much.

Then (orchestrator): python -I src/DeepEngineers.Launcher/launcher.py s17 --latest --lenient
Dev: /de s17 status (SE), /de sn s17 status (Subnautica). Rows: SE and Subnautica s17.catch.
"""

def analyzer():
    if ANALYZER_DIR not in sys.path:
        sys.path.insert(0, ANALYZER_DIR)
    import analyze_s17  # noqa: E402
    return analyze_s17

def newest_logs(log_dir=LOG_DIR):
    se = sorted(glob.glob(os.path.join(log_dir, "se-*.jsonl")), key=os.path.getmtime)
    sn = sorted(glob.glob(os.path.join(log_dir, "sn-*.jsonl")), key=os.path.getmtime)
    return (se[-1] if se else None), (sn[-1] if sn else None)

def s17(LCH, log, a) -> int:
    if a.selftest:
        return s17_selftest()
    if a.notes:
        print(NOTES)
        return 0
    AN = analyzer()
    se_logs, sn_logs = list(a.se_log or []), list(a.sn_log or [])
    if a.latest:
        se, sn = newest_logs()
        if se:
            se_logs.append(se)
        if sn:
            sn_logs.append(sn)
    if a.dry_run or not (a.analyze or se_logs or sn_logs):
        se, sn = newest_logs()
        print("S17 plan: free play with `launcher.py run` (S17 on by default), then the table of tests/s17/analyze_s17.py on the logs.")
        print("  newest logs: SE %s, Subnautica %s" % (se or "none", sn or "none"))
        print("  --notes prints what the owner does and looks at")
        return 0
    rows = AN.evaluate(a.analyze, se_logs, sn_logs)
    ok = AN.verdict(rows, strict=not a.lenient)
    if a.json:
        import json
        print(json.dumps(rows, indent=1, default=str))
    else:
        print("S17 logs: SE %s | Subnautica %s%s" % (", ".join(se_logs) or "-", ", ".join(sn_logs) or "-", (" | folder " + a.analyze) if a.analyze else ""))
        print(AN.table(rows))
    print("S17 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def run(LCH, log, a) -> int:
    return s17(LCH, log, a)

def s17_selftest() -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S17 launcher self-test (launcher.py s17 --selftest)")
    AN = analyzer()
    check(AN.selftest() == 0, "the analyzer self-test passes (twin pin, vectors, seeded rows)")
    check(sorted(AN.MUTANT_ROWS) == ["S17.X1", "S17.X2", "S17.X3", "S17.X4", "S17.X5"], "five seeded failures (one per S17.CATCH row, and the broken bridge)")
    check("1. Catch" in NOTES and "4. Cook" in NOTES and "F" in NOTES, "the notes cover catch, again, eat and cook")
    wm = os.path.join(ROOT, "src", "DeepEngineers.WorldMod", "Data")
    check(os.path.isfile(os.path.join(wm, "PhysicalItems_DE_Food.sbc")) and os.path.isfile(os.path.join(wm, "Blueprints_DE_Food.sbc")),
          "the world mod carries the two S17 data files")
    ok = all(results)
    print("S17 LAUNCHER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def add_parsers(sub, LCH):
    p = sub.add_parser("s17", help="S17 fish as food: the owner's free-play notes and the oracle table of its logs")
    p.add_argument("--notes", action="store_true", help="print the owner-attended free-play notes")
    p.add_argument("--latest", action="store_true", help="judge the newest se-/sn-*.jsonl pair under %%LOCALAPPDATA%%\DeepEngineers\logs")
    p.add_argument("--analyze", default=None, metavar="DIR", help="a folder with se-*.jsonl / sn-*.jsonl")
    p.add_argument("--se-log", action="append", default=[], metavar="FILE")
    p.add_argument("--sn-log", action="append", default=[], metavar="FILE")
    p.add_argument("--lenient", action="store_true", help="MISSING rows do not fail the table")
    p.add_argument("--json", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--selftest", action="store_true")
    return p
