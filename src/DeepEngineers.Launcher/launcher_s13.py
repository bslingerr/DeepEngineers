"""`launcher.py s13` (ROADMAP §14 "### S13 Character and animation v2"; docs/S13-BRIEF.md; CONTRACT §5.4 "S13 conventions").
Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`).

S13's oracle is the project's free play (`launcher.py run`: S13 is on by default in both plugins, nothing to switch on); this
launcher prints what to do and what to look at, and judges the session's logs with tests/s13/analyze_s13.py:

 python -I src/DeepEngineers.Launcher/launcher.py s13 --notes
 python -I src/DeepEngineers.Launcher/launcher.py s13 --latest [--lenient] [--json] (the newest se-/sn-*.jsonl pair)
 python -I src/DeepEngineers.Launcher/launcher.py s13 --analyze DIR | --se-log F --sn-log F [--lenient] [--json]
 python -I src/DeepEngineers.Launcher/launcher.py s13 --list-mutant-groups | --mutant-group NAME | --dry-run | --selftest

It never starts a game, never writes into a game folder, a save, the Mods folder or SE's LastSession; it only reads the logs
under %LOCALAPPDATA%\\DeepEngineers\\logs (or the files given)."""
from __future__ import annotations

import glob
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
ANALYZER_DIR = os.path.join(ROOT, "tests", "s13")
LOG_DIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeepEngineers", "logs")

SE_MUTANTS = ("S13.M3", "S13.M5")
SN_MUTANTS = ("S13.M1", "S13.M2", "S13.M4", "S13.M6", "S13.M7", "S13.M8")
# a mutant group = one free-play look with the seeded failure on (dev builds), and the rows it must turn red
S13_MUTANT_GROUPS = {
    "hands": {"mutants": ("S13.M1",), "red": ("S13.A1",), "do": "first person, dive in and out of the water five times"},
    "grounded": {"mutants": ("S13.M2",), "red": ("S13.A2",), "do": "swim down to the seabed and rest on it 5 s, then on a hull's top"},
    "toolred": {"mutants": ("S13.M3",), "red": ("S13.A3",), "do": "equip the hand drill and the rifle (on foot is enough)"},
    "lamp": {"mutants": ("S13.M4",), "red": ("S13.A4N", "S13.A4F"), "do": "at night under water, suit lamp on, face a wall 3-6 m away"},
    "basis": {"mutants": ("S13.M5",), "red": ("S13.A5",), "do": "third person under water, hold Alt to face the engineer, swim with W then S 5 s each"},
    "held": {"mutants": ("S13.M6",), "red": ("S13.A6",), "do": "third person under water with the welder, then the rifle, then a block selected"},
    "ghost": {"mutants": ("S13.M7",), "red": ("S13.A7",), "do": "select a block (a thruster) and aim the preview under water"},
    "anim": {"mutants": ("S13.M8",), "red": ("S13.A5A",), "do": "third person: swim forward, release, turn 180 deg while coasting, repeat 5 times"},
}

NOTES = """S13 free play (owner-attended; launcher.py run, both games, the usual world; S13 is on by default). In this order:

 1. Hands (point 1). First person: jump into the sea from a deck or the shore, swim under, come up, dive again (5 times).
    Look at: no Subnautica diver hands, body or mask ever appear beside the SE astronaut, at the surface or under water.
    The dive-in arms are now SE's own (the swim animation); listen for Subnautica's dive splash and surfacing breath sound.
 2. Swim near a surface (point 2). Third person: swim down to the seabed and lie on it 5 s, then swim along a hull and
    land on its top. Look at: the engineer keeps swimming (no standing pose) while under water; standing only with the head
    out of the water in the shallows.
 3. Tools and skins (point 3). Equip the hand drill, grinder, welder and rifle on foot and under water (your Rainbow /
    Veteran skins). Look at: no red tool; the skin's own colours; no tool flashing at the feet when you equip it.
 4. Lamp at night (point 4). At night under water, suit lamp on, face a wall or the seabed at 1, 3 and 10 m, first and third
    person, then hold a tool in front of the lamp. Look at: no giant shadow of your helmet, arms or tool; the far field less
    dark than before, the near field less blinding. Tune live if needed: /de sn s13 nearPlane 1.5 | lightFit off.
 5. Third-person direction (point 5). Third person under water: swim forward, then backward (S), then turn the camera to face
    the engineer (hold Alt) and swim again. Look at: S plays the backward stroke and moves the body backwards along its own
    facing; W forward along it; the stroke always matches the motion. Fallback: /de s13 basis camera (S2b's rule).
 6. Swimming with items (point 6). Third person under water with the welder, the rifle and a block selected. Look at: the
    legs kick (Subnautica's swim cycle) while the arms hold the item as SE holds it; first person: the item stays where SE
    puts it.
 7. Placement ghost (point 7). Select a thruster, a cockpit and an armour block under water, aim at a grid and at open water,
    rotate the preview (PageUp/Delete/Home/End/Insert/PageDown). Look at: the real block shape with its panels in green (red
    when blocked), turning with SE's preview; walking into a large ghost no longer floods the view green.

Then (orchestrator): python -I src/DeepEngineers.Launcher/launcher.py s13 --latest --lenient
Dev toggles (dev builds): /de sn s13 status | ghost textured|flat | lightFit on|off | blockFit on|off | nearPlane <m>|default |
sounds on|off; /de s13 status | basis body|camera. Rows: SE s13.swim, s13.tool; Subnautica s13.char, s13.light, s13.ghost.
"""

def analyzer():
    if ANALYZER_DIR not in sys.path:
        sys.path.insert(0, ANALYZER_DIR)
    import analyze_s13  # noqa: E402
    return analyze_s13

def newest_logs(log_dir=LOG_DIR):
    se = sorted(glob.glob(os.path.join(log_dir, "se-*.jsonl")), key=os.path.getmtime)
    sn = sorted(glob.glob(os.path.join(log_dir, "sn-*.jsonl")), key=os.path.getmtime)
    return (se[-1] if se else None), (sn[-1] if sn else None)

def env_for(mutants):
    se = [m for m in mutants if m in SE_MUTANTS]
    sn = [m for m in mutants if m in SN_MUTANTS]
    env = {}
    if se:
        env["DEEPENGINEERS_MUTANTS"] = ",".join(se)
    if sn:
        env["DEEPENGINEERS_MUTANT"] = ",".join(sn)
    return env

def s13(LCH, log, a) -> int:
    if a.selftest:
        return s13_selftest()
    if a.notes:
        print(NOTES)
        return 0
    if a.list_mutant_groups:
        for name, g in sorted(S13_MUTANT_GROUPS.items()):
            print("%-9s %-8s red %-18s env %s  -- %s" % (name, ",".join(g["mutants"]), ",".join(g["red"]), env_for(g["mutants"]), g["do"]))
        return 0
    if a.mutant_group:
        g = S13_MUTANT_GROUPS[a.mutant_group]
        print("S13 mutant group %s (dev builds): set %s for `launcher.py run`, then: %s; expected red: %s (launcher.py s13 --latest)"
              % (a.mutant_group, env_for(g["mutants"]), g["do"], ", ".join(g["red"])))
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
        print("S13 plan: free play with `launcher.py run` (S13 on by default), then the table of tests/s13/analyze_s13.py on the logs.")
        print("  newest logs: SE %s, Subnautica %s" % (se or "none", sn or "none"))
        print("  --notes prints what the owner does and looks at; --list-mutant-groups the seeded failures for a free-play check")
        return 0
    rows = AN.evaluate(a.analyze, se_logs, sn_logs)
    ok = AN.verdict(rows, strict=not a.lenient)
    if a.json:
        import json
        print(json.dumps(rows, indent=1, default=str))
    else:
        print("S13 logs: SE %s | Subnautica %s%s" % (", ".join(se_logs) or "-", ", ".join(sn_logs) or "-", (" | folder " + a.analyze) if a.analyze else ""))
        print(AN.table(rows))
    print("S13 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def run(LCH, log, a) -> int:
    return s13(LCH, log, a)

def s13_selftest() -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S13 launcher self-test (launcher.py s13 --selftest)")
    AN = analyzer()
    check(AN.selftest() == 0, "the analyzer self-test passes")
    for name, g in S13_MUTANT_GROUPS.items():
        check(tuple(g["red"]) == tuple(AN.MUTANT_ROWS[g["mutants"][0]]), "group %s: its rows are the analyzer's for %s" % (name, g["mutants"][0]))
        env = env_for(g["mutants"])
        side_ok = all((m in SE_MUTANTS and m in env.get("DEEPENGINEERS_MUTANTS", "")) or (m in SN_MUTANTS and m in env.get("DEEPENGINEERS_MUTANT", ""))
                      for m in g["mutants"])
        check(side_ok, "group %s: the mutant goes to its own side %s" % (name, env))
    check(sorted(SE_MUTANTS + SN_MUTANTS) == sorted(AN.MUTANT_ROWS), "every analyzer mutant has a side")
    check("1. Hands" in NOTES and "7. Placement ghost" in NOTES, "the notes cover the seven points")
    ok = all(results)
    print("S13 LAUNCHER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def add_parsers(sub, LCH):
    p = sub.add_parser("s13", help="S13 character and animation v2: the owner's free-play notes and the oracle table of its logs")
    p.add_argument("--notes", action="store_true", help="print the owner-attended free-play notes")
    p.add_argument("--latest", action="store_true", help="judge the newest se-/sn-*.jsonl pair under %%LOCALAPPDATA%%\\DeepEngineers\\logs")
    p.add_argument("--analyze", default=None, metavar="DIR", help="a folder with se-*.jsonl / sn-*.jsonl")
    p.add_argument("--se-log", action="append", default=[], metavar="FILE")
    p.add_argument("--sn-log", action="append", default=[], metavar="FILE")
    p.add_argument("--lenient", action="store_true", help="MISSING rows do not fail the table")
    p.add_argument("--json", action="store_true")
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--mutant-group", default=None, choices=sorted(S13_MUTANT_GROUPS))
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--selftest", action="store_true")
    return p
