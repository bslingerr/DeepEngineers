"""`launcher.py s14` (ROADMAP §14 "### S14 Audio v2"; docs/S14-BRIEF.md; CONTRACT §5.4 "S14 conventions").
Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`).

S14's oracle is the project's free play (`launcher.py run`: S14 is on by default in both plugins, nothing to switch on; the
world mod's weapon override needs the world mod deployed); this launcher prints what to do and what to listen to, and
judges the session's logs with tests/s14/analyze_s14.py:

 python -I src/DeepEngineers.Launcher/launcher.py s14 --notes
 python -I src/DeepEngineers.Launcher/launcher.py s14 --latest [--lenient] [--json] (the newest se-/sn-*.jsonl pair)
 python -I src/DeepEngineers.Launcher/launcher.py s14 --analyze DIR | --se-log F --sn-log F [--lenient] [--json]
 python -I src/DeepEngineers.Launcher/launcher.py s14 --list-mutant-groups | --mutant-group NAME | --dry-run | --selftest

It never starts a game, never writes into a game folder, a save, the Mods folder or SE's LastSession; it only reads the logs
under %LOCALAPPDATA%\\DeepEngineers\\logs (or the files given)."""
from __future__ import annotations

import glob
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
ANALYZER_DIR = os.path.join(ROOT, "tests", "s14")
LOG_DIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeepEngineers", "logs")

SE_MUTANTS = ("S14.M1", "S14.M2", "S14.M3", "S14.M7", "S14.M8", "S14.M9", "S14.M10")
SN_MUTANTS = ("S14.M4", "S14.M5", "S14.M6")
# a mutant group = one free-play listen with the seeded failure on (dev builds), and the rows it must turn red
S14_MUTANT_GROUPS = {
    "jetpack": {"mutants": ("S14.M1",), "red": ("S14.N1",), "do": "jetpack on above the sea, fly down into the water, swim 10 s"},
    "muffle": {"mutants": ("S14.M2",), "red": ("S14.N2", "S14.N2C"), "do": "under water: weld, grind and drill a block, fire the rifle (an Arcade world)"},
    "weapons": {"mutants": ("S14.M3",), "red": ("S14.N3", "S14.N3E"), "do": "a ship with a gatling turret under water, 15 s (the plugin reverts the override in memory)"},
    "listener": {"mutants": ("S14.M4",), "red": ("S14.N4L",), "do": "third person under water next to a ship firing its thrusters, 10 s"},
    "ramp": {"mutants": ("S14.M5",), "red": ("S14.N4E", "S14.N4V"), "do": "/de sn s14 engine subnautica, pilot a ship with ion thrusters under water, tap them 5 times"},
    "jets": {"mutants": ("S14.M6",), "red": ("S14.N5", "S14.N5O"), "do": "fly a ship with hydrogen thrusters just above the sea, 10 s (the old yellow puffs come back)"},
    "pan": {"mutants": ("S14.M8",), "red": ("S14.N4P",), "do": "third person: pilot a ship with thrusters above and under water, turn it left and right, 20 s"},
    "chain": {"mutants": ("S14.M9",), "red": ("S14.N2C",), "do": "under water: pilot a ship, tap the thrusters, weld and grind (either sound mode)"},
    "eye": {"mutants": ("S14.M10",), "red": ("S14.N2E",), "do": "no ship: float at the surface, bob the eye just under and over the waterline 5 times, dive 5 m, surface (first person)"},
    "sethrust": {"mutants": ("S14.M7",), "red": ("S14.N4E", "S14.N4V", "S14.N4S"), "do": "pilot a ship under water, tap the thrusters 5 times, then /de sn s14 engine subnautica and again"},
}

NOTES = """S14 free play (owner-attended; launcher.py run, both games, the usual world; S14 is on by default). In this order:

 1. Jetpack (point 1). Above the sea switch the jetpack on and fly; dive into the water (the jetpack switches off) and swim
    10 s, first and third person. Listen: no jetpack hiss or loop under water; it comes back only when you fly again.
 2. Every SE sound under water (point 2). Under water: weld, grind and drill a block, place and remove a few blocks, fire the
    rifle, then do the same on deck above water. Listen: under water everything SE makes is muffled (low-passed, a little
    reverb if SE's Hardware reverb option is on), above water dry as before; your own breathing and the HUD voice stay
    clear. Round 4 (the 21:3x deploy, "too muffled"): the muffle is now SE's LowPassHelmet (1800 Hz) instead of LowPassCockpit
    (750 Hz), and exactly one low-pass in both sound modes. The sound mode is the WORLD's setting: Load Game > the world > Edit
    Settings > Advanced > "Sound mode" (Arcade or Realistic); "DE Lab S4" is Arcade (Sandbox_config.sbc RealisticSound false;
    the 22:09 log printed "RealisticSound = False" at all three loads), your Star System saves are Realistic. Play it once in
    each mode if you like: it must sound the same (in a Realistic world SE's "heard through the suit" recordings are swapped for
    the normal ones under water; the s14.muffle row says mode and chain). Compare live: /de s7audio effect LowPassCockpit (the old,
    darker one), LowPassNoHelmetNoOxy (much stronger), back with /de s7audio effect LowPassHelmet; /de s14 reverb off|on;
    /de s14 cue off (the Realistic swap off).
 2b. The muffle follows your eyes (ext s14 audio 5, owner 2026-10-24: "the diver at the surface without a ship hears the
    muffle"). Without a ship, float at the surface in first person and look around; let the eyes dip just under the water and
    come back up a few times; then dive 5 m and surface. Listen: muffled exactly while the eyes are under the water line, dry the
    moment they are above it (before, SE's muffle followed the view's switch: on only 20 cm down, off only 5 cm under, and 0.3 s
    late after surfacing). Row S14.N2E (s14.eye).
 3. Ship weapons (point 3; the world mod must be deployed). A ship with gatling/missile turrets or fixed guns under water:
    the guns stay on (no "Functional Underwater: False" switch-off by the Water Mod) and fire; look for the bubble tracers and
    the muzzle flash, listen for the muffled shot. Also a turret at the surface half in the water. A ship saved under water
    before S14 had its guns switched off by the Water Mod: the plugin switches them back on once when it first sees the ship
    (log "DE.S14 weapons: ... switched back on"); a gun still off: switch it on in the terminal.
 4. Thrusters (point 4). Pilot a ship under water in third person and dodge the seabed with short taps; then hold full thrust
    10 s, with hydrogen, ion and atmospheric thrusters if you have them. Listen: each type keeps SE's own sound (muffled),
    from both ears as the ship turns (left when the ship is on your left); short taps are quiet and fade in/out in about a
    quarter second (no hard start/stop, no late start), full thrust is the loudest. Round 4, the left ear: do this in THIRD
    person, above and under water, orbit the camera around the ship; say where you hear it. Found: SE plays a LARGE ship's
    whole sound from the grid's centre (a small ship's from its cockpit), in 3D unless you sit enclosed, while the
    third-person camera looks at the cockpit; in DE Lab S4 (the 10-18 save) your ship's centre is 7.5 m to the LEFT of its cockpit, so SE put
    the whole ship on your left (Arcade or Realistic alike). Now the ship you pilot plays its ship sounds in both ears (SE's
    own 2D cockpit sound, both views); other ships stay 3D. Also SE's 3D call for a 2-channel sound breaks X3DAudio's rules;
    the plugin rebuilds that pan from a valid call. /de s14 pan off|on compares live; row s14.pan logs every SE voice's own
    left/right matrix against where it is. Then compare by ear:
    /de sn s14 engine subnautica (hydrogen = the Cyclops engine, ion = the Seamoth engine, atmospheric stays SE's) and back
    with /de sn s14 engine se. Toggles: /de sn s14 envelope off, /de sn s14 listener off, /de s14 thrusters off.
    On a ship SE plays one thruster sound per type for the whole grid (its ship sound system); S14 ramps and scales that one.
    Note (subnautica mode, dev only): SE fades its hydrogen/ion sound as soon as Subnautica says the loop is loaded, but
    Subnautica can still refuse a loop for a grid (its 16-loop cap, or no flame of that grid flagged under water): such a
    thruster is then silent; switch back with /de sn s14 engine se.
 5. Thrusters above the water (point 5). Fly a ship with hydrogen and ion thrusters just above the sea and at 20 m, idle and
    full thrust, then let the flames reach the water. Look at: each flame now has Subnautica's own Prawn jet, bigger with
    thrust and (round 4) as long as SE's own flame, and the spray/steam where the plume meets the sea; the old yellow puffs
    of scene-above are gone while the jets are on (they come back with /de sn s14 jets off). Look at the jet's DIRECTION: it
    must blow out of the nozzle along the flame, not sideways or back into the ship (tell us if not; the log line "DE.S14 jets
    above water" names the template's axis, renderers and shaders). Tune: /de sn s14 jetScale 0.5..2 | jets off.

Then (orchestrator): python -I src/DeepEngineers.Launcher/launcher.py s14 --latest --lenient
Dev toggles (dev builds): /de s14 status | jetpack|arcade|gui|reverb|thrusters|pan|cue on|off | effect <SE effect> | weapons check;
/de sn s14 status | listener|envelope|jets on|off | jetScale <0.1..4>. Rows: SE s14.jetpack, s14.muffle, s14.thrusters,
s14.weapons, s14.pan; Subnautica s14.listener, s14.engine, s14.jets.
"""

def analyzer():
    if ANALYZER_DIR not in sys.path:
        sys.path.insert(0, ANALYZER_DIR)
    import analyze_s14  # noqa: E402
    return analyze_s14

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

def s14(LCH, log, a) -> int:
    if a.selftest:
        return s14_selftest()
    if a.notes:
        print(NOTES)
        return 0
    if a.list_mutant_groups:
        for name, g in sorted(S14_MUTANT_GROUPS.items()):
            print("%-9s %-8s red %-18s env %s  -- %s" % (name, ",".join(g["mutants"]), ",".join(g["red"]), env_for(g["mutants"]), g["do"]))
        return 0
    if a.mutant_group:
        g = S14_MUTANT_GROUPS[a.mutant_group]
        print("S14 mutant group %s (dev builds): set %s for `launcher.py run`, then: %s; expected red: %s (launcher.py s14 --latest)"
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
        print("S14 plan: free play with `launcher.py run` (S14 on by default), then the table of tests/s14/analyze_s14.py on the logs.")
        print("  newest logs: SE %s, Subnautica %s" % (se or "none", sn or "none"))
        print("  --notes prints what the owner does and listens to; --list-mutant-groups the seeded failures for a free-play check")
        return 0
    rows = AN.evaluate(a.analyze, se_logs, sn_logs)
    ok = AN.verdict(rows, strict=not a.lenient)
    if a.json:
        import json
        print(json.dumps(rows, indent=1, default=str))
    else:
        print("S14 logs: SE %s | Subnautica %s%s" % (", ".join(se_logs) or "-", ", ".join(sn_logs) or "-", (" | folder " + a.analyze) if a.analyze else ""))
        print(AN.table(rows))
    print("S14 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def run(LCH, log, a) -> int:
    return s14(LCH, log, a)

def s14_selftest() -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S14 launcher self-test (launcher.py s14 --selftest)")
    AN = analyzer()
    check(AN.selftest() == 0, "the analyzer self-test passes")
    for name, g in S14_MUTANT_GROUPS.items():
        check(tuple(g["red"]) == tuple(AN.MUTANT_ROWS[g["mutants"][0]]), "group %s: its rows are the analyzer's for %s" % (name, g["mutants"][0]))
        env = env_for(g["mutants"])
        side_ok = all((m in SE_MUTANTS and m in env.get("DEEPENGINEERS_MUTANTS", "")) or (m in SN_MUTANTS and m in env.get("DEEPENGINEERS_MUTANT", ""))
                      for m in g["mutants"])
        check(side_ok, "group %s: the mutant goes to its own side %s" % (name, env))
    check(sorted(SE_MUTANTS + SN_MUTANTS) == sorted(AN.MUTANT_ROWS), "every analyzer mutant has a side")
    check(all(("%d. " % k) in NOTES for k in range(1, 6)), "the notes cover the five points")
    check("Sound mode" in NOTES and "THIRD" in NOTES and "yellow puffs" in NOTES, "round 4: the notes name the world's sound mode, third person and the old puffs")
    ok = all(results)
    print("S14 LAUNCHER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def add_parsers(sub, LCH):
    p = sub.add_parser("s14", help="S14 audio v2: the owner's free-play notes and the oracle table of its logs")
    p.add_argument("--notes", action="store_true", help="print the owner-attended free-play notes")
    p.add_argument("--latest", action="store_true", help="judge the newest se-/sn-*.jsonl pair under %%LOCALAPPDATA%%\\DeepEngineers\\logs")
    p.add_argument("--analyze", default=None, metavar="DIR", help="a folder with se-*.jsonl / sn-*.jsonl")
    p.add_argument("--se-log", action="append", default=[], metavar="FILE")
    p.add_argument("--sn-log", action="append", default=[], metavar="FILE")
    p.add_argument("--lenient", action="store_true", help="MISSING rows do not fail the table")
    p.add_argument("--json", action="store_true")
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--mutant-group", default=None, choices=sorted(S14_MUTANT_GROUPS))
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--selftest", action="store_true")
    return p
