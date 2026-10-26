"""`launcher.py s10`. Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`);
the session plumbing is launcher_s6's (S6Runner: the C11 harness, the row tails, de.swim, the breathing).

 python -I src/DeepEngineers.Launcher/launcher.py s10 --part perf [--scenes spawn,paste,flight] [--world COPY]
 [--machine desktop|laptop] [--publish-site] [--quick] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s10 --notes
 python -I src/DeepEngineers.Launcher/launcher.py s10 --latest [--machine laptop] (judge the newest se-/sn-*.jsonl pair)
 python -I src/DeepEngineers.Launcher/launcher.py s10 --analyze tests/out/s10/<stamp> [--machine M] [--publish-site]
 python -I src/DeepEngineers.Launcher/launcher.py s10 --load-history (every world load in %LOCALAPPDATA%\\DeepEngineers\\logs)
 python -I src/DeepEngineers.Launcher/launcher.py s10 --selftest

Part `perf`: both real games, once, on a Creative THROWAWAY copy of the planet world (the S8a lab world: Creative so the
clipboard pastes; the throwaway marker and autosave off as every scripted part; the world check refuses anything else;
make it with tools/lab/make_survival_copy.py --source "DE Lab S8a" --name "DE Lab S10 Perf" --game-mode Creative
--autosave-off --point-last-session). No extra environment: the plugins' play defaults, as `launcher.py run`. Scenes:
 spawn the character parked at SN (24, -8, 0) (the usual spawn water), 15 s settle, then SPAWN_S of free running
 paste a 10,000-block ship (de.s10 paste: vanilla blocks, S10Blueprint) through SE's clipboard 150 m ahead (selection lag,
 placement wait; SE falls back to the paste's own spawn route when the clipboard refuses, and says so); then
 Subnautica's spawn (pendingParts back to 0), four looks (bow 24 m, beside 70 m, inside, far 124 m) LOOK_S each; de.s10 clear
 flight a 30 km straight flight (de.terrain perf: the spectator from SN (-15000, -12, 500) to (15000, -12, 500) at 100 m/s,
 300 s), every second recorded
Each scene's window is a part begin/end event in s10-session.jsonl; the analyzer (tests/s10/analyze_s10.py) slices the
plugins' per-second rows (s10.perf, s10.se, sample, s8a.window, s8a.status, s5.budget) with them and adds the load-time
breakdown of the session's world load. Output tests/out/s10/<stamp>/ (s10-table.txt, s10-results.json); --publish-site writes
the machine's block into site/data/requirements.json (the GitHub page's Requirements table). All rows INFO in part 1.
This launcher never writes into a game folder, a save or SE's LastSession; the world copy is the orchestrator's."""
from __future__ import annotations

import datetime
import glob
import json
import os
import sys
import time

import launcher_s6 as L6

PERF_WORLD = "DE Lab S10 Perf"
SCENES = ("spawn", "paste", "flight")
PARK = L6.PARK                         # (24, -8, 0): the character parked in open water, as s5/s6/s11
SPAWN_SETTLE_S, SPAWN_S = 15.0, 60.0
PASTE_BLOCKS, PASTE_DIST = 10000, 150.0
PASTE_TIMEOUT_S, SPAWN_TIMEOUT_S, LOOK_S = 400.0, 300.0, 10.0
FLIGHT_FROM, FLIGHT_TO, FLIGHT_SPEED = (-15000.0, -12.0, 500.0), (15000.0, -12.0, 500.0), 100.0
SITE_JSON = ("site", "data", "requirements.json")

NOTES = """S10 owner notes: measuring your own sessions (both PCs), and MSI Afterburner / RTSS beside our rows.

Our rows are the source of truth (every second, both games, every build: Subnautica s10.perf, SE s10.se and sample, in
%LOCALAPPDATA%\\DeepEngineers\\logs). The overlay is for your eye while you play; nothing of ours depends on it.

A. Install (you authorised the downloads; both are free, from the vendor):
   1. MSI Afterburner from msi.com/Landing/afterburner (the installer offers RivaTuner Statistics Server: keep it ticked).
   2. Start Afterburner > Settings (gear) > Monitoring: tick "GPU usage", "Memory usage" (VRAM), "CPU usage", "RAM usage",
      "Framerate", "Frametime"; for each, tick "Show in On-Screen Display". On the laptop (Radeon 780M) the GPU rows read
      the shared memory as VRAM: note both.
   3. RTSS (tray icon) > Application profile properties: "Show On-Screen Display" on; "Framerate limit" EMPTY (0): a limit
      there would cap the games and spoil the numbers. Do not enable RTSS's "Scanline sync" either.
   4. Both games run at once: RTSS draws its overlay on the one in front (SE). Subnautica renders behind SE, so the
      overlay's framerate is SE's; Subnautica's frame rate is in our s10.perf rows only.
B. Read the overlay next to our rows:
   - SE fps on the overlay ~ our sample.fps / s10.se (same engine counter, one-second windows; small offsets are normal).
   - GPU usage near 100 % and Subnautica's fps low (from our table) = GPU-bound: the mirror's meshes or the water.
     GPU usage low while both fps drop = CPU-bound: compare our s10.perf stages (mirror, kit, place, window) with SE's
     update interval in s10.se.
   - RAM: our rows give each process's private MB; the overlay's RAM is the whole machine (16 GB is the pass mark).
   - A frametime spike on the overlay at a moment you remember: tell me the time (clock) and what you did; I find the row.
C. A session for the table (each PC, the same world): `launcher.py run`, then in this order, ~1 minute each:
   1. Spawn: stay where you spawn, under water, look around slowly.
   2. Paste: open the blueprint screen (F10) and paste a large ship of your own (the bigger the better; note its block
      count), wait until it shows in Subnautica, then look at it from outside and from inside.
   3. Flight: fly a fast ship (or jetpack) 2-3 minutes in a straight line far across the planet, under the surface.
   Then quit normally and tell me "s10 done" (+ the block count). I run: launcher.py s10 --latest --machine <desktop|laptop>
   and publish the table (--publish-site). The scripted version of these three scenes is `launcher.py s10 --part perf`.
D. Loading screens: no action needed; every world load is measured (launcher.py s10 --load-history lists all of them)."""

def analyzer(LCH):
    p = os.path.join(LCH.REPO, "tests", "s10")
    if p not in sys.path:
        sys.path.insert(0, p)
    import analyze_s10 as AN   # noqa: E402
    return AN

def newest_logs(log_dir):
    se = sorted(glob.glob(os.path.join(log_dir, "se-*.jsonl")), key=os.path.getmtime)
    sn = sorted(glob.glob(os.path.join(log_dir, "sn-*.jsonl")), key=os.path.getmtime)
    appdata = os.environ.get("APPDATA", "")
    game = sorted(glob.glob(os.path.join(appdata, "SpaceEngineers", "SpaceEngineers_*.log")), key=os.path.getmtime)
    return (se[-1] if se else None), (sn[-1] if sn else None), (game[-1] if game else None)

def judge(LCH, log, a, folder=None, se_logs=(), sn_logs=(), game_logs=()):
    AN = analyzer(LCH)
    rows = AN.evaluate(folder, se_logs, sn_logs, game_logs, a.machine)
    print(AN.table(rows))
    if folder:
        with open(os.path.join(folder, "s10-results.json"), "w", encoding="utf-8") as f:
            json.dump(rows, f, indent=1, default=str)
        with open(os.path.join(folder, "s10-table.txt"), "w", encoding="utf-8") as f:
            f.write(AN.table(rows) + "\n")
    if a.publish_site:
        path = os.path.join(LCH.REPO, *SITE_JSON)
        AN.write_site(path, AN.site_entry(rows, a.machine, a.spec))
        log.say("S10 site: %s block written into %s" % (a.machine, path), ev="s10_site")
    complete = AN.verdict(rows, strict=True)
    log.say("S10 TABLE %s (part 1: INFO baseline)" % ("COMPLETE" if complete else "WITH MISSING ROWS"), ev="s10_verdict", complete=complete)
    return 0

# ----------------------------------------------------------------------------------------------------------------------
# the scripted scenes
# ----------------------------------------------------------------------------------------------------------------------

class _S10Rows:
    """launcher_s6's row tails parse s6.* rows only; the paste scene waits on SE's s5.budget rows."""

    @staticmethod
    def parse_rows(recs):
        return [(r.get("ev"), r, None) for r in recs if isinstance(r, dict) and r.get("ev") in ("s5.budget", "s10.paste")]

class S10Runner(L6.S6Runner):
    """The perf scenes through launcher_s6's plumbing (the base runs in its S6b mode: both real games)."""

    def __init__(self, LCH, log, a, sess, sn, se, out, stamp, t_start):
        saved = a.mode
        a.mode = "S6b"
        try:
            super().__init__(LCH, log, a, sess, sn, se, out, stamp, t_start)
        finally:
            a.mode = saved
        self.se_tail.AN = _S10Rows()

    def s10(self, args, label, timeout=10.0):
        return self.call("se", "de.s10", args, timeout=timeout, label=label)

    def hold(self, seconds, label):
        self.sess.event("hold", label=label, seconds=seconds)
        self.wait(self.scale(seconds))

    def look(self, sn, yaw, label):
        self.swim({"teleportChar": list(sn), "yawDeg": float(yaw)}, label)
        self.hold(LOOK_S, label)

    def p_spawn(self):
        """the character parked at SN (24, -8, 0), 15 s settle, then 60 s recorded"""
        self.park()
        self.hold(SPAWN_SETTLE_S, "spawn.settle")
        self.part("spawn", "begin")
        self.hold(SPAWN_S, "spawn.run")
        self.part("spawn", "end")

    def p_paste(self):
        """the 10,000-block ship through SE's clipboard 150 m ahead; Subnautica's spawn; four looks (bow, beside, inside, far); cleared"""
        self.park()
        self.hold(5.0, "paste.settle")
        self.part("paste", "begin")
        rep = self.s10({"paste": {"blocks": self.a.paste_blocks, "dist": self.a.paste_dist, "path": self.a.paste_path}}, "paste.start", timeout=60.0)
        if not self.ok(rep):
            self.log.say("  S10 paste: de.s10 paste refused (%s)" % (json.dumps(rep)[:200] if rep else "no reply"), ev="s10_paste_refused")
        t0 = time.monotonic()
        state = None
        while self.alive() and time.monotonic() - t0 < PASTE_TIMEOUT_S:
            st = self.s10({}, "paste.status")
            state = ((st or {}).get("paste") or {}).get("state") if isinstance((st or {}).get("paste"), dict) else None
            if state in ("done", "failed"):
                break
            self.wait(1.0)
        self.sess.event("paste", state=state, seconds=round(time.monotonic() - t0, 1))
        self.log.say("  S10 paste: %s after %.0f s" % (state, time.monotonic() - t0), ev="s10_paste", state=state)
        # Subnautica's spawn: SE's s5.budget sn.pendingParts back to 0 (the analyzer measures it; here only the wait)
        mark = self.se_tail.mark()
        self.wait_rows(self.se_tail, mark, lambda rows: any(r.get("ev") == "s5.budget" and (r.get("sn") or {}).get("pendingParts") == 0
                                                              and (r.get("parts") or 0) > 0 for r in rows), self.scale(SPAWN_TIMEOUT_S, 30.0), "paste.spawn")
        x, y, z = PARK
        c = (x, y, z + self.a.paste_dist)                       # the ship's centre (yaw 0 = +z)
        self.look((x, y, z), 0.0, "paste.look.bow")
        self.look((x + 70.0, y, c[2]), 270.0, "paste.look.beside")
        self.look((x, y + 2.0, c[2]), 0.0, "paste.look.inside")
        self.look((x, y, z - 100.0), 0.0, "paste.look.far")
        self.part("paste", "end")
        self.s10({"clear": True}, "paste.clear")
        self.park()

    def p_flight(self):
        """the spectator flies 30 km in a straight line at 100 m/s (de.terrain perf), every second recorded"""
        f, t, v = self.a.flight_from, self.a.flight_to, self.a.flight_speed
        secs = (sum((b - a_) ** 2 for a_, b in zip(f, t)) ** 0.5) / v
        self.part("flight", "begin")
        rep = self.call("se", "de.terrain", {"action": "perf", "from": list(f), "to": list(t), "speed": v}, timeout=10.0, label="flight.start")
        if not self.ok(rep):
            self.log.say("  S10 flight: de.terrain perf refused (%s)" % (json.dumps(rep)[:200] if rep else "no reply"), ev="s10_flight_refused")
        self.hold(secs + 10.0, "flight.run")
        self.part("flight", "end")

def parse_vec(s, dflt):
    if not s:
        return dflt
    v = tuple(float(x) for x in s.split(","))
    if len(v) != 3:
        raise ValueError("a point is x,y,z")
    return v

def plan_lines(a, scenes, world):
    out = ["  world: %s (Creative, the throwaway marker, autosave off; tools/lab/make_survival_copy.py --source \"DE Lab S8a\" --name %r"
           " --game-mode Creative --autosave-off --point-last-session)" % (world, PERF_WORLD),
           "  both real games, no extra environment (the plugins' play defaults, as launcher.py run); machine %s" % a.machine]
    for s in scenes:
        out.append("  scene %-7s %s" % (s, (getattr(S10Runner, "p_" + s).__doc__ or "").strip()))
    out.append("  paste: %d blocks, %.0f m ahead, path %s; flight %s -> %s at %.0f m/s" % (a.paste_blocks, a.paste_dist, a.paste_path,
                                                                                         a.flight_from, a.flight_to, a.flight_speed))
    return out

def s10(LCH, log, a) -> int:
    S = LCH.S
    if a.notes:
        print(NOTES)
        return 0
    if a.selftest:
        return s10_selftest(LCH, log)
    AN = analyzer(LCH)
    if a.load_history:
        print(AN.history_table(AN.load_history(LCH.LOG_DIR)))
        return 0
    if a.preset:   # round 2 D: the machine's preset file (launcher.py run reads it; DEEPENGINEERS_S10_PRESET overrides)
        if a.preset == "show":
            v = LCH.s10_resolve_preset(log)
            print("S10 preset: %s (%s)" % (v, LCH.S10_PRESET_FILE))
            return 0
        v = a.preset
        if v == "auto":
            v, ram, shared, names = LCH.s10_auto_preset()
            print("S10 auto-pick: %s (RAM %.1f GB, GPU %s%s)" % (v, ram, ", ".join(names) or "unknown", " (shared)" if shared else ""))
        elif v == "ask":
            #): Preset.cmd / Play.cmd -Preset asks the first Play's
 # question again, the current preset (the file, else the auto-pick) offered as the one to keep
            rec, ram, shared, names = LCH.s10_auto_preset()
            current = LCH.s10_resolve_preset(None)
            if current in LCH.S10_PRESETS:
                rec = current
            v = LCH.s10_ask_preset(rec, ram, shared, names, LCH.s10_console_ask())
            print("preset %s kept in %s" % (v.upper(), LCH.S10_PRESET_FILE))
        os.makedirs(os.path.dirname(LCH.S10_PRESET_FILE), exist_ok=True)
        with open(LCH.S10_PRESET_FILE, "w", encoding="utf-8") as f:
            f.write(v + "\n")
        log.say("S10 preset %s written to %s" % (v, LCH.S10_PRESET_FILE), ev="s10_preset_set", preset=v)
        return 0
    if a.analyze:
        return judge(LCH, log, a, folder=a.analyze)
    if a.latest:
        se, sn, game = newest_logs(LCH.LOG_DIR)
        log.say("S10 latest: SE %s | Subnautica %s | SE game log %s" % (se, sn, game))
        return judge(LCH, log, a, se_logs=[se] if se else [], sn_logs=[sn] if sn else [], game_logs=[game] if game else [])
    try:
        a.flight_from = parse_vec(a.flight_from_s, FLIGHT_FROM)
        a.flight_to = parse_vec(a.flight_to_s, FLIGHT_TO)
    except ValueError as e:
        log.say("s10: %s" % e)
        return 2
    scenes = [x.strip() for x in (a.scenes or ",".join(SCENES)).split(",") if x.strip()]
    unknown = [x for x in scenes if x not in SCENES]
    world = a.world or PERF_WORLD
    if a.part != "perf" or unknown:
        log.say("s10: --part perf [--scenes %s] (unknown: %s); or --notes | --latest | --analyze | --load-history | --selftest" % (",".join(SCENES), unknown))
        return 2
    info, wprobs = L6.world_check(LCH, world, "Creative", a.force_unmarked)
    if a.dry_run:
        print("S10 perf plan: scenes %s%s" % (",".join(scenes), " (quick)" if a.quick else ""))
        for line in plan_lines(a, scenes, world):
            print(line)
        for p in wprobs:
            print("  A REAL RUN NEEDS: world %r: %s" % (world, p))
        log.say("dry run: nothing started")
        return 0
    if wprobs:
        for p in wprobs:
            log.say("REFUSE s10 perf: world %r: %s (throwaway-copy rule)" % (world, p))
        return 2
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s10", stamp)
    os.makedirs(out, exist_ok=True)
    a.cont = True
    sn_cmd, se_cmd = LCH.session_commands(a)
    sess = LCH.S2SessionLog(os.path.join(out, "s10-session.jsonl"))
    sess.event("start", kind="s10", stamp=stamp, scenes=scenes, machine=a.machine, name=a.name, argv=sys.argv[1:], quick=a.quick,
               snCmd=sn_cmd, seCmd=se_cmd, qpcFrequency=S.QPC_FREQUENCY, world=world)
    log.say("S10 perf session %s: scenes %s; out %s" % (stamp, ",".join(scenes), out), ev="s10_plan")
    S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            runner = S10Runner(LCH, log, a, sess, sn, se, out, stamp, t_start)
            ok, why = runner.attach()
            if not ok:
                log.say("S10: harness not usable: %s" % why, ev="s10_harness_refused")
                rc = 1
            else:
                for s in scenes:
                    if not runner.alive():
                        log.say("S10: a game exited before scene %s" % s, ev="s10_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + s)()
                    except Exception as e:  # noqa: BLE001 - a scene error is logged, the session goes on
                        log.say("S10 scene %s failed: %r" % (s, e), ev="s10_scene_error", scene=s)
                        sess.event("scene_error", name=s, error=repr(e))
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if not a.keep_running:
            LCH.close_session(log, a, sn, se, False, False)
        S.fine_timer(False)
        sess.event("end", rc=rc, stats=runner.stats if runner else None)
        sess.close()
        LCH.collect(log, out, t_start, sn, False, False, stamp)
    res = judge(LCH, log, a, folder=out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------------
# self-test (no game, no section, no process)
# ----------------------------------------------------------------------------------------------------------------------

def s10_selftest(LCH, log) -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S10 launcher self-test (launcher.py s10 --selftest)")
    AN = analyzer(LCH)
    check(AN.selftest() == 0, "the analyzer self-test passes")
    check(all(getattr(S10Runner, "p_" + s, None) for s in SCENES), "every scene has a runner part")
    check(tuple(AN.SCENES) == SCENES, "the analyzer slices the same scenes")
    d = abs(FLIGHT_TO[0] - FLIGHT_FROM[0])
    check(abs(d - 30000.0) < 1e-6 and abs(d / FLIGHT_SPEED - 300.0) < 1e-6, "the flight is 30 km at 100 m/s (300 s)")
    counts, slices = AN.blueprint_counts(PASTE_BLOCKS)
    check(sum(counts) == PASTE_BLOCKS and slices * 2.5 < PASTE_DIST * 2, "the paste ship: %d blocks, %.0f m long" % (sum(counts), slices * 2.5))
    check(parse_vec("1,2,3", None) == (1.0, 2.0, 3.0) and parse_vec("", FLIGHT_FROM) == FLIGHT_FROM, "points parse")
    check("Afterburner" in NOTES and "RTSS" in NOTES and "Framerate limit" in NOTES and "source of truth" in NOTES,
          "the owner notes cover the overlay, its limit and the rows as the truth")
    site = os.path.join(LCH.REPO, *SITE_JSON)
    try:
        with open(site, encoding="utf-8") as f:
            doc = json.load(f)
        check("machines" in doc and "passMarks" in doc, "site/data/requirements.json is readable")
    except (OSError, ValueError) as e:
        check(False, "site/data/requirements.json: %s" % e)
    check(os.path.exists(os.path.join(LCH.REPO, "site", "index.html")), "the GitHub page scaffold exists (site/index.html)")
    ok = all(results)
    print("S10 LAUNCHER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def add_parsers(sub, LCH):
    p = sub.add_parser("s10", help="S10 performance: the perf scenes, the load-time breakdown, the owner notes, the site table")
    p.add_argument("--part", default=None, choices=("perf",))
    p.add_argument("--scenes", default="", help="comma list of %s (default all)" % ",".join(SCENES))
    p.add_argument("--world", default=None, help="the Creative throwaway copy (default %r)" % PERF_WORLD)
    p.add_argument("--force-unmarked", action="store_true", help="accept a copy without the throwaway marker (owner-approved)")
    p.add_argument("--machine", default="desktop", choices=("desktop", "laptop"))
    p.add_argument("--spec", default="", help="the machine's CPU / GPU / RAM line for the site table")
    p.add_argument("--publish-site", action="store_true", help="write the machine's block into site/data/requirements.json")
    p.add_argument("--paste-blocks", type=int, default=PASTE_BLOCKS)
    p.add_argument("--paste-dist", type=float, default=PASTE_DIST)
    p.add_argument("--paste-path", default="clipboard", choices=("clipboard", "direct"))
    p.add_argument("--flight-from", dest="flight_from_s", default="", help="x,y,z (SN; default %s)" % (FLIGHT_FROM,))
    p.add_argument("--flight-to", dest="flight_to_s", default="", help="x,y,z (SN; default %s)" % (FLIGHT_TO,))
    p.add_argument("--flight-speed", type=float, default=FLIGHT_SPEED)
    p.add_argument("--notes", action="store_true", help="print the owner notes (Afterburner / RTSS, the session for the table)")
    p.add_argument("--latest", action="store_true", help="judge the newest se-/sn-*.jsonl pair (an owner's own session)")
    p.add_argument("--analyze", default=None, metavar="DIR")
    p.add_argument("--load-history", action="store_true")
    p.add_argument("--preset", default=None, choices=("low", "mid", "high", "auto", "show", "ask"), help="the machine's performance preset (run reads it); ask = the first Play's question again (Preset.cmd)")
    p.add_argument("--quick", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--selftest", action="store_true")
    p.add_argument("--out", default=None)
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    p.add_argument("--mode", default=None, help=argparse_suppress())
    # the session plumbing's options (as launcher_s6's)
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--screen", default="2560x1440")
    p.add_argument("--fov", type=float, default=70.0)
    p.add_argument("--owner", action="store_true", help=argparse_suppress())
    p.add_argument("--rotor-at", default=None, help=argparse_suppress())
    return p

def argparse_suppress():
    import argparse
    return argparse.SUPPRESS

def run(LCH, log, a) -> int:
    return s10(LCH, log, a)
