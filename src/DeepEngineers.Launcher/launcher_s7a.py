"""`launcher.py s7a` and `launcher.py tp1 --picture site|waterline` (ROADMAP S7a "Subnautica's picture above water";
CONTRACT §5.4 "C5/C7 semantics for S7a", "S7a conventions", "C1 locomotion extension"). Lives next to launcher.py,
which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`): the session machinery (preflight,
start_games, the in-world wait, the C11 harness, close_session, collect) is launcher.py's, the runner plumbing
ps1) is launcher_s5.S5Runner's.

 python -I src/DeepEngineers.Launcher/launcher.Mx]
 [--deck BLUEPRINT] [--dev-phases] [--quick] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s7a --list-mutant-groups | --analyze tests/out/s7a/<stamp>
 python -I src/DeepEngineers.Launcher/launcher.py tp1 --state... --picture site|waterline [--dry-run]

`s7a` runs one scripted S7a session with both games (DE Lab, Legacy.exe -continue; the S5 session rules of
S5-STATUS §9.1: the S5 mirror on, the character parked at SN (24, -8, 0) first). SE's environment: DEEPENGINEERS_S5=on,
DEEPENGINEERS_PICTURE=site, DEEPENGINEERS_GHOST=on, DEEPENGINEERS_TIMESYNC=on (and the SE mutants in
DEEPENGINEERS_MUTANTS); Subnautica's: DEEPENGINEERS_MUTANT for the SN mutants. Parts (each in windows named
<part>.<step> in s7a-session.jsonl; the section is sampled at 10 Hz):
 n1 picture per state: the spectator camera at +2,000 m (Space), +400, down to +250 m (Track, 10 s), +3 m (Prime,
 10 s), -5 m (Underwater, 10 s), +3 m (Surfacing -> Prime), +150 m (a capture pair, Subnautica and SE), +500 m
 (Space), then 8,000 m out (FAR); SE de.s7 status and Subnautica de.s7 status at every stop
 n2 the band swap: 5 descents +600 -> +200 m at 20 m/s and 2 at 100 m/s, the same ascents, and 20 s hovering between
 +300 and +350 m; Subnautica's de.s7 sun every second (the sun alike at every edge)
 n3 the placement ghost: a 3 x 3 armor wall placed by de.s3, the character 8 m in front of it, SE de.s7 {ghost:
 probe, block} with a large armor block and a small fat block, looks at the grid (allowed), into it (blocked), at
 de.s7 {action: ghost}
 on Subnautica after every aim (rows at once)
 n4 the astronaut on a deck above water: an 8 x 8 floor plate at SN (40, +1.5, 40) placed by de.s3 (or --deck: the
 swim script (stand 5 s, walk 10 s, jump once, step off
 into the sea
 person with Subnautica's de. de.s7 {action: anim} rows
 n5 the time sync: 5 min of samples every 2 s (SE and Subnautica de.s7 sun), SE de.s7 {time: {sync: off}} 10 s and on
 again, the camera 3 km east then 3 km west (de.teleport), optionally (--dev-phases) SE de.s7 {time: 0.26 | 0.5 |
 0.74} then {time: off}
 teardown SE de.s7 {ghost: stop}, {time: {sync: on}}, {time: off}, {picture: site}; the placed grids removed (de.s3
 remove); Subnautica de.s7 {walk: retarget}, de.s5 {fpbody: diver}; every mutant of the session off
Seeded failures (--mutant, one per session; its part runs with teardown): S7a.M1 -> n1, S7a.M2 -> n1, S7a.M3 -> n2,
S7a.M4 -> n3, S7a.M5 -> n4, S7a.M6 -> n5 (SE: M1, M3, M4; Subnautica: M2, M5, M6). Output tests/out/s7a/<stamp>/
(s7a-session.jsonl, se-*.jsonl, sn-*.jsonl, the game logs, captures, s7a-table.txt, s7a-results.json) and the S7a
ORACLE TABLE of tests/s7a/analyze_s7a.py.

`tp1 --picture site|waterline` (S7a.T-P1r): launcher_s5's T-P1 states with DEEPENGINEERS_PICTURE in SE's environment;
after launcher_s5's own analysis the S7a.T-P1r rows (analyze_s7a.py --tp1r) are written to s7a-tp1r-results.json in
the same folder. Games are never piped and only closed by PID; nobody but the orchestrator launches them."""
from __future__ import annotations

import datetime
import json
import os
import subprocess
import sys
import time

S7A_PARTS = ("n1", "n2", "n3", "n4", "n5", "teardown")
SE_MUTANTS = ("S7a.M1", "S7a.M3", "S7a.M4")
SN_MUTANTS = ("S7a.M2", "S7a.M5", "S7a.M6")
ALL_MUTANTS = SE_MUTANTS + SN_MUTANTS
MUTANT_GROUPS = {   # ROADMAP S7a "S7a seeded failures": mutant -> the part whose rows go red
    "S7a.M1": {"parts": ("n1", "teardown"), "red": ("S7a.N1.track", "S7a.N1.waterline")},
    "S7a.M2": {"parts": ("n1", "teardown"), "red": ("S7a.N1.track",)},
    "S7a.M3": {"parts": ("n2", "teardown"), "red": ("S7a.N2.fade",)},
    "S7a.M4": {"parts": ("n3", "teardown"), "red": ("S7a.N3.colour",)},
    "S7a.M5": {"parts": ("n4", "teardown"), "red": ("S7a.N4.walk",)},
    "S7a.M6": {"parts": ("n5", "teardown"), "red": ("S7a.N5.monotonic",)},
}
PICTURES = ("site", "waterline")

def band_from_env():
    """The plugin's picture band (enter, exit) in metres: env DEEPENGINEERS_BAND='enter,exit' (the same variable SE reads),
 default +1,000 / +1,100 m again since S7t T3."""
    v = os.environ.get("DEEPENGINEERS_BAND", "")
    try:
        parts = [float(x) for x in v.replace(";", ",").split(",") if x.strip()]
        if len(parts) == 2 and 0.0 <= parts[0] < parts[1]:
            return parts[0], parts[1]
    except ValueError:
        pass
    return 1000.0, 1100.0   # S7t T3: the band back at 1 km (crossfade, horizon/haze, look match, impostors)

BAND = band_from_env()             # (enter, exit): the picture swaps at the band under the Site policy
SPACE_Y = BAND[1] + 900.0          # well above the band (2,000 m for the default band)
SPOT = (0.0, 0.0)                  # the spectator path's column (SN x, z): Safe Shallows anchor
FAR = (8000.0, 150.0, 0.0)         # beyond 7,900 m (zone FAR)
N1_PATH = (   # (window, kind, args): teleport (x, y, z, yaw) | motion from y to y at speed | hold seconds
    ("space", "teleport", (0.0, SPACE_Y, 0.0, 0.0), 5.0),
    ("approach", "teleport", (0.0, BAND[0] + 150.0, 0.0, 0.0), 5.0),       # above the exit: Space until the descent
    ("descent", "line", (BAND[0] + 150.0, BAND[0] - 50.0, 20.0), 0.0),    # through the band: the swap
    ("track", "hold", None, 10.0),
    ("toprime", "line", (BAND[0] - 50.0, 3.0, 50.0), 0.0),
    ("prime", "hold", None, 10.0),
    ("dive", "line", (3.0, -5.0, 2.0), 0.0),
    ("underwater", "hold", None, 10.0),
    ("surface", "line", (-5.0, 3.0, 2.0), 0.0),
    ("surfacing", "hold", None, 5.0),
    ("climb150", "line", (3.0, 150.0, 20.0), 0.0),
    ("capture", "hold", None, 3.0),
    ("climb", "line", (150.0, BAND[1] + 150.0, 40.0), 0.0),              # through the band exit (held 1 s) into Space
    ("space2", "hold", None, 5.0),
    ("far", "teleport", FAR + (0.0,), 5.0),
)
N2_DESCENTS = (20.0,) * 5 + (100.0,) * 2
N2_TOP, N2_BOTTOM = BAND[1] + 250.0, BAND[0] - 100.0                 # legs through the band (1,350 / 900 m for the default band)
N2_HOVER = {"centreY": (BAND[0] + BAND[1]) / 2.0, "amplitudeM": 20.0, "hz": 0.05, "seconds": 20.0}   # inside the hysteresis
GRID_SPOT = (0.0, -6.0, 40.0)      # n3: the 3 x 3 armor wall (SN), 6 m under the surface in the Safe Shallows
CHAR_N3 = (0.0, -6.0, 32.0)        # the character 8 m south of it, facing north
GHOST_BLOCKS = ("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", "MyObjectBuilder_Thrust/SmallBlockSmallThrust")
DECK = (40.0, 1.5, 40.0)           # n4: block (0,0,0) centre of the 8 x 8 floor plate (top at about +2.75 m)
DECK_PLATE = {"rows": 8, "cols": 8, "orientation": "floor"}
N4_SCRIPT = (   # de.swim script steps (label, seconds, forward, strafe, vertical)
    ("stand", 5.0, 0.0, 0.0, 0.0),
    ("walk", 2.0, 0.6, 0.0, 0.0),        # S7a-2: 6 m/s run; 2 s stays on the 20 m plate (5 s ran it into the sea before the jump)
    ("walkback", 2.0, -0.6, 0.0, 0.0),
    ("jump", 0.4, 0.0, 0.0, 1.0),
    ("land", 2.6, 0.0, 0.0, 0.0),
    ("stepoff", 9.0, 1.0, 0.0, 0.0),
)
N5_SECONDS, N5_EVERY = 300.0, 2.0
N5_SPOT = (0.0, 100.0, 0.0)        # Approach (Subnautica renders Full: its sky updates)
N5_TELEPORT_M = 3000.0
DEV_PHASES = (0.26, 0.5, 0.74)
QUICK = 0.25

def analyzer(LCH):
    p = os.path.join(LCH.REPO, "tests", "s7a")
    if p not in sys.path:
        sys.path.insert(0, p)
    import analyze_s7a   # noqa: E402
    return analyze_s7a

def s5_module(LCH):
    if LCH.HERE not in sys.path:
        sys.path.insert(0, LCH.HERE)
    import launcher_s5   # noqa: E402
    return launcher_s5

def _runner_class(LCH):
    L5 = s5_module(LCH)

    class S7aRunner(L5.S5Runner):
        """The scripted S7a parts. The action tables name every command in the plugins' own syntax (SE: S7aCommands
 de.s7, S2's de.teleport/de.motion, S2b's de.swim, S3's de.s3 place/remove; Subnautica: S7aHost de.s7, S5Host
 de.s5 {fpbody}), so a renamed command changes in one place."""

        SE_ACTIONS = {
            "status": ("de.s7", lambda: {"action": "status"}),
            "sun": ("de.s7", lambda: {"action": "sun"}),
            "picture": ("de.s7", lambda p: {"picture": p}),
            "ghost": ("de.s7", lambda v: {"ghost": v}),
            "probe": ("de.s7", lambda block: {"ghost": "probe", "block": block}),
            "sync": ("de.s7", lambda on: {"time": {"sync": "on" if on else "off"}}),
            "phase": ("de.s7", lambda c: {"time": c}),
            "phaseOff": ("de.s7", lambda: {"time": "off"}),
            "place": ("de.s3", lambda sn, block, plate, yaw, label: {"place": {"sn": list(sn), "yawDeg": float(yaw), "block": block,
                                                                             "plate": dict(plate), "label": label}}),
            "remove": ("de.s3", lambda: {"remove": True}),
            "look": ("de.look", lambda d, label: {"dir": d, "label": label}),
            "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
        }
        STANDIN_ONLY = ()
        SN_ACTIONS = {
            "status": ("de.s7", lambda: {"action": "status"}),
            "sun": ("de.s7", lambda: {"action": "sun"}),
            "ghost": ("de.s7", lambda: {"action": "ghost"}),
            "anim": ("de.s7", lambda: {"action": "anim"}),
            "walk": ("de.s7", lambda w: {"walk": w}),
            "fpbody": ("de.s5", lambda which: {"fpbody": which}),
            "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
        }

        def __init__(self, *args, **kw):
            super().__init__(*args, **kw)
            self.placed = 0
            self.cap_n = 0

        def scale(self, secs, quick=None):
            return secs if not self.q else (quick if quick is not None else max(1.0, secs * QUICK))

        def window(self, name, fn):
            self.part(name, "begin")
            try:
                fn()
            finally:
                self.part(name, "end")

        def stops(self, label):
            self.act("se", "status", label="%s.se.status" % label)
            self.act("sn", "status", label="%s.sn.status" % label)

        def line_y(self, y0, y1, speed, label, x=SPOT[0], z=SPOT[1]):
            secs = abs(y1 - y0) / speed
            self.motion({"kind": "line", "fromSn": [x, y0, z], "toSn": [x, y1, z], "speedMps": speed}, label)
            self.wait(self.scale(secs + 0.5, min(secs + 0.5, 3.0)))

        def capture_pair(self, label):
            for side in ("sn", "se"):
                self.cap_n += 1
                rel = "s7a/%s/%s-%s-%02d.png" % (self.stamp, label, side, self.cap_n)
                rep = self.call(side, "de.capture", {"path": rel, "checkId": "S7a.N1.capture", "label": label, "kind": 1},
                                timeout=10.0, label="%s.capture.%s" % (label, side))
                self.sess.event("capture", name=label, side=side, path=rel, reply=rep, qpc=self.S.qpc())

        # ---- parts ------------------------------------------------------------------------------------------------
        def p_n1(self):
            """S7a.N1: the spectator path Space (+2,000) -> +400 -> +250 (Track 10 s) -> +3 (Prime 10 s) -> -5 (Underwater
 10 s) -> +3 (Surfacing -> Prime) -> +150 (a capture pair) -> +500 (Space) -> 8,000 m out (FAR); de.s7 status
 on both sides at every stop"""
            self.part("n1", "begin")
            y = None
            for name, kind, args, hold in N1_PATH:
                lab = "n1.%s" % name
                self.part(lab, "begin")
                if kind == "teleport":
                    x, y, z, yaw = args
                    self.teleport(x, y, z, yaw, label=lab)
                    self.wait(self.scale(hold, 1.5))
                elif kind == "line":
                    y0, y1, speed = args
                    self.line_y(y0, y1, speed, lab)
                    y = y1
                else:
                    self.wait(self.scale(hold, 1.5))
                if name == "capture":
                    self.capture_pair("n1.capture150")
                if kind != "line":
                    self.stops(lab)
                self.part(lab, "end")
            self.teleport(SPOT[0], 500.0, SPOT[1], 0.0, label="n1.back")
            self.part("n1", "end")

        def p_n2(self):
            """S7a.N2: 5 descents +600 -> +200 m at 20 m/s and 2 at 100 m/s (predicted arrival), the same ascents, 20 s
 hovering inside the band's hysteresis (+1,000 / +1,100 m default); Subnautica de.s7 sun every second (the sun alike at every edge)"""
            self.part("n2", "begin")
            for k, speed in enumerate(N2_DESCENTS):
                tag = "d%d.%d" % (int(speed), k + 1)
                self.teleport(SPOT[0], N2_TOP, SPOT[1], 0.0, label="n2.%s.top" % tag)
                self.wait(self.scale(4.0, 1.5))
                self.window("n2.%s" % tag, lambda: self.sampled_line(N2_TOP, N2_BOTTOM, speed, "n2.%s" % tag))
                self.wait(self.scale(3.0, 1.0))
                atag = "a%d.%d" % (int(speed), k + 1)
                self.window("n2.%s" % atag, lambda: self.sampled_line(N2_BOTTOM, N2_TOP, speed, "n2.%s" % atag))
                self.wait(self.scale(3.0, 1.0))
            self.teleport(SPOT[0], N2_HOVER["centreY"], SPOT[1], 0.0, label="n2.hover.start")
            self.wait(self.scale(4.0, 1.5))
            self.part("n2.hover", "begin")
            secs = self.scale(N2_HOVER["seconds"], 5.0)
            self.motion({"kind": "bob", "x": SPOT[0], "z": SPOT[1], "centreY": N2_HOVER["centreY"],
                         "amplitudeM": N2_HOVER["amplitudeM"], "hz": N2_HOVER["hz"], "seconds": secs}, "n2.hover")
            self.sun_samples(secs + 0.5, 1.0, "n2.hover")
            self.part("n2.hover", "end")
            self.part("n2", "end")

        def sampled_line(self, y0, y1, speed, label):
            secs = abs(y1 - y0) / speed
            self.motion({"kind": "line", "fromSn": [SPOT[0], y0, SPOT[1]], "toSn": [SPOT[0], y1, SPOT[1]], "speedMps": speed}, label)
            self.sun_samples(self.scale(secs + 1.0, min(secs + 1.0, 4.0)), 1.0, label)

        def sun_samples(self, seconds, every, label, se_too=False):
            end = time.monotonic() + seconds
            while time.monotonic() < end and self.alive():
                self.act("sn", "sun", label="%s.sn.sun" % label)
                if se_too:
                    self.act("se", "sun", label="%s.se.sun" % label)
                self.wait(max(0.0, min(every, end - time.monotonic())))

        def p_n3(self):
            """S7a.N3: a 3 x 3 armor wall (de.s3 place), the character 8 m in front; de.s7 {ghost: probe} with a large armor
 block and a small fat block, aimed at the grid (allowed), into it (blocked), at the seabed (voxel), into open
 Subnautica de.s7 {action: ghost} after every aim"""
            self.part("n3", "begin")
            rep = self.act("se", "place", GRID_SPOT, "LargeBlockArmorBlock", {"rows": 3, "cols": 3, "orientation": "wall"}, 0.0,
                           "s7a.n3", label="n3.place", timeout=20.0)
            if self.ok(rep):
                self.placed += 1
            self.call("se", "de.swim", {"teleportChar": list(CHAR_N3), "yawDeg": 0.0}, label="n3.char")
            self.wait(self.scale(3.0, 1.0))
            aims = (("allowed", "N"), ("blocked", "pitch-5"), ("voxel", "down"), ("free", "pitch+60"))
            for block in GHOST_BLOCKS:
                short = block.split("/")[-1]
                self.part("n3.probe.%s" % short, "begin")
                rep = self.act("se", "probe", block, label="n3.probe.%s" % short)
                if not self.ok(rep):
                    self.owner("n3.build.%s" % short, "enter build mode with %s (G menu or toolbar), aim at the wall" % block, 20.0)
                self.wait(self.scale(5.0, 2.0))
                self.part("n3.probe.%s" % short, "end")
                for aim, look in aims:
                    lab = "n3.%s.%s" % (aim, short)
                    self.part(lab, "begin")
                    self.act("se", "look", look, lab, label="%s.look" % lab)
                    if aim == "blocked":
                        self.owner(lab, "aim the %s preview INTO the wall so SE shows it red (blocked)" % short, 10.0)
                    self.wait(self.scale(3.0, 1.0))
                    self.act("sn", "ghost", label="%s.sn.ghost" % lab)
                    self.act("se", "status", label="%s.se.status" % lab)
                    self.part(lab, "end")
                self.part("n3.rotate.%s" % short, "begin")
                self.owner("n3.rotate.%s" % short, "rotate the %s preview (Page Up/Down, Home/End, Insert/Delete) a few times" % short, 15.0)
                self.act("sn", "ghost", label="n3.rotate.%s.sn.ghost" % short)
                self.part("n3.rotate.%s" % short, "end")
                self.part("n3.stop.%s" % short, "begin")
                self.act("se", "ghost", "stop", label="n3.stop.%s" % short)
                self.wait(self.scale(2.0, 1.0))
                self.act("sn", "ghost", label="n3.stop.%s.sn.ghost" % short)
                self.part("n3.stop.%s" % short, "end")
            self.act("se", "look", "N", "n3.end", label="n3.look.end")
            self.part("n3", "end")

        def p_n4(self):
            """S7a.N4: an 8 x 8 floor plate at SN (40, +1.5, 40) (de.s3 place
 character on it, de.
 if the script does not act out of the water); third then first person with de.s5 {fpbody: diver}"""
            self.part("n4", "begin")
            deck = getattr(self.a, "deck", None)
            if deck:
                self.owner("n4.deck", "paste the blueprint '%s' as a deck at the surface around SN (%g, %g, %g)" % (
                    deck, DECK[0], DECK[1], DECK[2]), 60.0)
            else:
                rep = self.act("se", "place", DECK, "LargeBlockArmorBlock", DECK_PLATE, 0.0, "s7a.n4", label="n4.place", timeout=20.0)
                if self.ok(rep):
                    self.placed += 1
            self.act("sn", "fpbody", "diver", label="n4.fpbody")
            for view in ("third", "first"):
                if view == "first":
                    self.owner("n4.camera.first", "switch SE to FIRST person (V) and keep it", 10.0)
                else:
                    self.owner("n4.camera.third", "switch SE to THIRD person (V) and keep it", 10.0)
                self.call("se", "de.swim", {"teleportChar": [DECK[0], DECK[1] + 1.6, DECK[2] - 6.0], "yawDeg": 0.0},
                          label="n4.%s.char" % view)
                self.wait(self.scale(3.0, 1.0))
                steps = [{"label": "n4.%s.%s" % (view, lab), "seconds": self.scale(secs, min(secs, 1.0)), "forward": fw,
                          "strafe": st, "vertical": vt} for lab, secs, fw, st, vt in N4_SCRIPT]
                rep = self.call("se", "de.swim", {"script": steps, "label": "n4.%s" % view}, label="n4.%s.script" % view)
                for step in steps:
                    self.part(step["label"], "begin")
                    self.act("sn", "anim", label="%s.sn.anim" % step["label"])
                    if not self.ok(rep):
                        self.owner(step["label"], "on the deck: %s for %g s" % (step["label"].split(".")[-1], step["seconds"]),
                                   step["seconds"])
                    else:
                        self.wait(step["seconds"])
                    self.act("sn", "anim", label="%s.sn.anim.end" % step["label"])
                    self.part(step["label"], "end")
                self.wait(self.scale(3.0, 1.0))
            # review S7a F13: one ghost aim ABOVE water, on the deck (every n3 aim is under water); the analyzer's
 # N3 rows take it through the n3.probe./n3.stop. windows
            short = "deck"
            self.part("n3.probe.%s" % short, "begin")
            rep = self.act("se", "probe", GHOST_BLOCKS[0], label="n3.probe.%s" % short)
            if not self.ok(rep):
                self.owner("n3.build.%s" % short, "on the deck: enter build mode with a large armor block, aim at the deck top", 20.0)
            self.wait(self.scale(5.0, 2.0))
            self.part("n3.probe.%s" % short, "end")
            lab = "n3.deck.%s" % short
            self.part(lab, "begin")
            self.act("se", "look", "pitch-30", lab, label="%s.look" % lab)
            self.wait(self.scale(3.0, 1.0))
            self.act("sn", "ghost", label="%s.sn.ghost" % lab)
            self.act("se", "status", label="%s.se.status" % lab)
            self.part(lab, "end")
            self.part("n3.stop.%s" % short, "begin")
            self.act("se", "ghost", "stop", label="n3.stop.%s" % short)
            self.wait(self.scale(2.0, 1.0))
            self.act("sn", "ghost", label="n3.stop.%s.sn.ghost" % short)
            self.part("n3.stop.%s" % short, "end")
            self.part("n4", "end")

        def p_n5(self):
            """S7a.N5: 5 min of sun samples every 2 s (SE and Subnautica de.s7 sun), SE de.s7 {time: {sync: off}} 10 s
 then on, the camera 3 km east then 3 km west, optionally (--dev-phases) de.s7 {time: 0.26 | 0.5 | 0.74}"""
            self.part("n5", "begin")
            x, y, z = N5_SPOT
            self.teleport(x, y, z, 0.0, label="n5.spot")
            self.wait(self.scale(5.0, 2.0))
            self.window("n5.samples", lambda: self.sun_samples(self.scale(N5_SECONDS, 20.0), N5_EVERY, "n5.samples", se_too=True))
            self.part("n5.syncoff", "begin")
            self.act("se", "sync", False, label="n5.sync.off")
            self.sun_samples(self.scale(10.0, 4.0), 0.5, "n5.syncoff", se_too=True)
            self.part("n5.syncoff", "end")
            self.part("n5.syncon", "begin")
            self.act("se", "sync", True, label="n5.sync.on")
            self.sun_samples(self.scale(10.0, 4.0), 0.5, "n5.syncon", se_too=True)
            self.part("n5.syncon", "end")
            for name, dx in (("east", N5_TELEPORT_M), ("west", -N5_TELEPORT_M)):
                self.part("n5.%s" % name, "begin")
                self.teleport(x + dx, y, z, 0.0, label="n5.%s" % name)
                self.sun_samples(self.scale(10.0, 4.0), 1.0, "n5.%s" % name, se_too=True)
                self.part("n5.%s" % name, "end")
            if getattr(self.a, "dev_phases", False):
                self.teleport(x, y, z, 0.0, label="n5.dev.spot")
                for c in DEV_PHASES:
                    lab = "n5.dev.%g" % c
                    self.part(lab, "begin")
                    self.act("se", "phase", c, label=lab)
                    self.sun_samples(self.scale(8.0, 3.0), 1.0, lab, se_too=True)
                    self.capture_pair(lab)
                    self.part(lab, "end")
                self.act("se", "phaseOff", label="n5.dev.off")
            self.part("n5", "end")

        def p_teardown(self):
            """SE de.s7 {ghost: stop}, {time: {sync: on}}, {time: off}, {picture: site}; the placed grids removed (de.s3
 remove); Subnautica de.s7 {walk: retarget}, de.s5 {fpbody: diver}; every mutant of the session off"""
            self.part("teardown", "begin")
            self.act("se", "ghost", "stop", label="teardown.ghost")
            self.act("se", "sync", True, label="teardown.sync")
            self.act("se", "phaseOff", label="teardown.phase")
            self.act("se", "picture", "site", label="teardown.picture")
            for _ in range(self.placed):
                self.act("se", "remove", label="teardown.remove")
            self.act("sn", "walk", "retarget", label="teardown.walk")
            self.act("sn", "fpbody", "diver", label="teardown.fpbody")
            for m in getattr(self, "mutants", []) or []:
                self.act("sn" if m in SN_MUTANTS else "se", "mutant", m, False, label="teardown.mutant.%s" % m)
            self.stops("teardown")
            self.part("teardown", "end")

    return S7aRunner

# ----------------------------------------------------------------------------------------------------------------
# sessions
# ----------------------------------------------------------------------------------------------------------------

def env_plan(mutants, picture="site"):
    """(Subnautica env, SE env) of a session."""
    sn = [m for m in mutants if m in SN_MUTANTS]
    se = [m for m in mutants if m in SE_MUTANTS]
    sn_env = {"DEEPENGINEERS_MUTANT": ",".join(sn)} if sn else {}
    se_env = {"DEEPENGINEERS_S5": "on", "DEEPENGINEERS_PICTURE": picture, "DEEPENGINEERS_GHOST": "on", "DEEPENGINEERS_TIMESYNC": "on"}
    if se:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(se)
    return sn_env, se_env

def plan_lines(LCH, a, parts, mutants, sn_env, se_env) -> list:
    R = _runner_class(LCH)
    out = ["  both games (DE Lab, Legacy.exe -continue); character parked at SN (24, -8, 0) first (S5-STATUS 9.1)",
           "  Subnautica env %s; SE env %s" % (json.dumps(sn_env) or "-", json.dumps(se_env)),
           "  mutants: %s%s" % (",".join(mutants) or "none", (" -> red rows " + ",".join(
               r for m in mutants for r in MUTANT_GROUPS[m]["red"])) if mutants else ""),
           "  owner steps: %s" % ("waited for (--owner)" if a.owner else "skipped (their rows may stay MISSING)")]
    for p in parts:
        doc = " ".join((getattr(R, "p_" + p).__doc__ or "").split())
        out.append("  part %-9s %s" % (p, doc))
        if p == "n1":
            for name, kind, args, hold in N1_PATH:
                out.append("      n1.%-12s %-8s %s hold %gs" % (name, kind, json.dumps(args), hold))
        if p == "n2":
            out.append("      descents %s m/s from +%g to +%g m, ascents back; hover %s" % (
                ",".join("%g" % s for s in N2_DESCENTS), N2_TOP, N2_BOTTOM, json.dumps(N2_HOVER)))
        if p == "n3":
            out.append("      wall at SN %s, character at %s, blocks %s" % (GRID_SPOT, CHAR_N3, ", ".join(GHOST_BLOCKS)))
        if p == "n4":
            out.append("      deck %s at SN %s; script %s" % ("blueprint %r (owner)" % a.deck if a.deck else "8x8 floor plate",
                                                             DECK, ", ".join("%s %gs" % (s[0], s[1]) for s in N4_SCRIPT)))
        if p == "n5":
            out.append("      %g s of samples every %g s at SN %s; sync off/on 10 s each; teleports +-%g m%s" % (
                N5_SECONDS, N5_EVERY, N5_SPOT, N5_TELEPORT_M, "; dev phases %s" % (DEV_PHASES,) if a.dev_phases else ""))
    out.append("  SE actions: " + ", ".join("%s=%s" % (k, v[0]) for k, v in R.SE_ACTIONS.items()))
    out.append("  Subnautica actions: " + ", ".join("%s=%s" % (k, v[0]) for k, v in R.SN_ACTIONS.items()))
    return out

def s7a_analyze(LCH, log, a, out) -> int:
    AN = analyzer(LCH)
    try:
        rows = AN.evaluate(out)
    except Exception as e:     # an analyser bug must not hide the session
        import traceback
        rows = [{"id": "S7a", "status": "FAIL", "measured": "analyser error %s" % type(e).__name__, "threshold": "-",
                 "what": str(e)[:120] + " | " + traceback.format_exc().strip().splitlines()[-1][:120], "part": "all",
                 "required": True}]
    strict = not getattr(a, "lenient", False)
    ok = AN.write_results(out, rows, strict, {"tool": "launcher %s" % a.cmd})
    print("\nS7a ORACLE TABLE (%s)  (* = not required; INFO = information)" % out)
    try:
        print(AN.table(rows))
    except UnicodeEncodeError:
        print(AN.table(rows).encode("ascii", "replace").decode("ascii"))
    log.say("S7a ORACLE %s: %d rows -> %s" % ("PASS" if ok else "FAIL", len(rows), os.path.join(out, "s7a-results.json")),
            ev="s7a_result", ok=ok)
    return 0 if ok else 1

def s7a(LCH, log, a) -> int:
    S = LCH.S
    if a.list_mutant_groups:
        for m, g in MUTANT_GROUPS.items():
            print("  %-7s %-10s parts %-14s red %s" % (m, "SN" if m in SN_MUTANTS else "SE", ",".join(g["parts"]), ",".join(g["red"])))
        return 0
    if a.analyze:
        return s7a_analyze(LCH, log, a, a.analyze)
    mutants = [m.strip() for m in (a.mutant or "").split(",") if m.strip()]
    bad = [m for m in mutants if m not in ALL_MUTANTS]
    if bad:
        log.say("unknown mutants %s (known: %s)" % (bad, ",".join(ALL_MUTANTS)))
        return 2
    if len(mutants) > 1:
        log.say("one mutant per session (its rows are judged alone): %s" % ",".join(mutants))
        return 2
    parts = list(MUTANT_GROUPS[mutants[0]]["parts"]) if mutants else list(S7A_PARTS)
    if a.parts:
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
    unknown = [x for x in parts if x not in S7A_PARTS]
    if unknown:
        log.say("unknown parts %s (known: %s)" % (unknown, ",".join(S7A_PARTS)))
        return 2
    sn_env, se_env = env_plan(mutants)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s7a", stamp + ("-" + mutants[0] if mutants else ""))
    a.cont = not a.no_continue
    sn_cmd, se_cmd = LCH.session_commands(a)
    log.say("S7a session %s: parts %s%s%s; Subnautica: %s; SE: %s; out %s" % (
        stamp, ",".join(parts), " (quick)" if a.quick else "", " mutant %s" % mutants[0] if mutants else "",
        subprocess.list2cmdline(sn_cmd), subprocess.list2cmdline(se_cmd), out), ev="s7a_plan")
    if a.dry_run:
        for line in plan_lines(LCH, a, parts, mutants, sn_env, se_env):
            print(line)
        log.say("dry run: nothing started")
        return 0
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "s7a-session.jsonl"))
    standin_sn, standin_se = bool(a.sn_cmd), bool(a.se_cmd)
    se_env["DEEPENGINEERS_BAND"] = "%g,%g" % BAND   # SE and the analyzer use the same band (env or the default)
    sess.event("start", kind="s7a", stamp=stamp, parts=parts, mutants=mutants, name=a.name, argv=sys.argv[1:], quick=a.quick, band=list(BAND),
               owner=a.owner, deck=a.deck, devPhases=a.dev_phases, standin={"sn": standin_sn, "se": standin_se},
               snCmd=sn_cmd, seCmd=se_cmd, qpcFrequency=S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env,
               groundSpot=list(SPOT), ghostId=0x0144454700000001)
    S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            R = _runner_class(LCH)
            runner = R(LCH, log, a, sess, sn, se, out, stamp, standin_sn, standin_se, t_start=t_start)
            runner.mutants = mutants
            ok, why = runner.attach()
            if not ok:
                log.say("S7a: harness not usable: %s" % why, ev="s7a_harness_refused")
                rc = 1
            else:
                runner.park()
                for m in mutants:
                    runner.act("sn" if m in SN_MUTANTS else "se", "mutant", m, True, label="mutant.%s" % m)
                runner.stops("start")
                for p in parts:
                    if not runner.alive():
                        log.say("S7a: a game exited before part %s" % p, ev="s7a_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:
                        log.say("S7a part %s failed: %r" % (p, e), ev="s7a_part_error", part=p)
                        sess.event("part_error", name=p, error=repr(e))
                log.say("S7a script finished: %s" % json.dumps(runner.stats), ev="s7a_script_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            LCH.close_session(log, a, sn, se, standin_se, standin_sn)
        S.fine_timer(False)
        sess.event("end", rc=rc, stats=runner.stats if runner else None,
                   unsupported=sorted("%s.%s" % k for k in runner.unsupported) if runner else None)
        sess.close()
        LCH.collect(log, out, t_start, sn, standin_sn, standin_se, stamp)
    if rc is not None:
        log.say("S7a session aborted (rc %d); analysing what was collected" % rc)
    res = s7a_analyze(LCH, log, a, out)
    return rc if rc is not None else res

def tp1(LCH, log, a) -> int:
    """`tp1 --picture site|waterline` (S7a.T-P1r): DEEPENGINEERS_PICTURE in SE's environment (inherited by start_games),
 launcher_s5's T-P1 run and analysis, then the S7a.T-P1r rows in s7a-tp1r-results.json of the same folder."""
    L5 = s5_module(LCH)
    picture = getattr(a, "picture", None)
    if not picture or getattr(a, "analyze", None) and not os.path.exists(os.path.join(a.analyze, "tp1-session.jsonl")):
        return L5.run(LCH, log, a)
    if a.analyze:
        rc = L5.run(LCH, log, a)
        return _tp1r(LCH, log, a, a.analyze, picture, rc)
    states = [s.strip() for s in (a.state or "").split(",") if s.strip()]
    if not a.out:
        stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
        a.out = os.path.join(LCH.REPO, "tests", "out", "tp1", "%s-%s-%s" % (stamp, "-".join(states) or "none", picture))
    os.environ["DEEPENGINEERS_PICTURE"] = picture        # start_games copies os.environ into SE's environment
    log.say("T-P1r: picture policy %s (DEEPENGINEERS_PICTURE=%s in SE's environment); out %s" % (picture, picture, a.out),
            ev="tp1r_plan", picture=picture)
    if a.dry_run:
        print("  picture policy %s: SE env DEEPENGINEERS_PICTURE=%s; S7a.T-P1r rows -> %s" % (
            picture, picture, os.path.join(a.out, "s7a-tp1r-results.json")))
        return L5.run(LCH, log, a)
    rc = L5.run(LCH, log, a)
    return _tp1r(LCH, log, a, a.out, picture, rc)

def _tp1r(LCH, log, a, folder, picture, rc):
    if not os.path.isdir(folder):
        return rc
    try:
        with open(os.path.join(folder, "s7a-tp1.json"), "w", encoding="utf-8") as f:
            json.dump({"picture": picture, "states": getattr(a, "state", "")}, f)
    except OSError:
        pass
    AN = analyzer(LCH)
    rows = AN.tp1r_rows_for(folder, getattr(a, "baseline", None), picture)
    ok = AN.write_results(folder, rows, not getattr(a, "lenient", False), {"tool": "launcher tp1 --picture %s" % picture},
                          name="s7a-tp1r")
    print("\nS7a.T-P1r TABLE (%s, picture %s)" % (folder, picture))
    print(AN.table(rows))
    log.say("S7a.T-P1r %s -> %s" % ("PASS" if ok else "FAIL", os.path.join(folder, "s7a-tp1r-results.json")), ev="tp1r_result", ok=ok)
    return rc

# ----------------------------------------------------------------------------------------------------------------
# argparse and dispatch (called by launcher.py)
# ----------------------------------------------------------------------------------------------------------------

def add_parsers(sub, LCH):
    p = sub.add_parser("s7a", help="one scripted S7a 'Subnautica's picture above water' session (both games): the S7a "
                                   "oracle table")
    p.add_argument("--parts", default="", help="comma list of %s (default: all; with --mutant: its part)" % ",".join(S7A_PARTS))
    p.add_argument("--mutant", default="", help="one seeded failure: %s" % ",".join(ALL_MUTANTS))
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--owner", action="store_true", help="wait for the owner's steps (build mode, aims, rotations, the camera "
                                                        "switch, a deck blueprint)")
    p.add_argument("--deck", default=None, metavar="BLUEPRINT", help="n4: the owner pastes this blueprint as the deck "
                                                                    "(default: an 8 x 8 floor plate by de.s3)")
    p.add_argument("--dev-phases", action="store_true", help="n5: SE de.s7 {time: 0.26 | 0.5 | 0.74} (HYPOTHESIS path)")
    p.add_argument("--quick", action="store_true", help="short holds (smoke runs)")
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="print the plan (parts, steps, environment); start nothing")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in plumbing only)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (stand-in plumbing only)")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--out", default=None)
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S7a oracles on a collected folder")
    tp = sub.choices.get("tp1")
    if tp is not None:
        tp.add_argument("--picture", choices=PICTURES, default=None,
                        help="S7a.T-P1r: SE's picture policy (DEEPENGINEERS_PICTURE); the S7a.T-P1r rows are added")

def run(LCH, log, a) -> int:
    if a.cmd == "s7a":
        return s7a(LCH, log, a)
    if a.cmd == "tp1":
        return tp1(LCH, log, a)
    return 2
