"""`launcher.py s15` (ROADMAP §14 "### S15 Gameplay and immersion polish" and its addenda; docs/S15-BRIEF.md; CONTRACT §5.4
"S15 conventions", `ext s15 voice 5`). Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`,
`add_parsers(sub, LCH)`); the session plumbing is launcher_s6's (S6Runner: the C11 harness), as launcher_s16's.

 python -I src/DeepEngineers.Launcher/launcher.py s15 --mode S15se|S15b [--parts...] [--mutant-group NAME] [--world COPY]
 [--quick] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s15 --dry-run | --list-mutant-groups | --selftest | --notes
 python -I src/DeepEngineers.Launcher/launcher.py s15 --analyze tests/out/s15/<stamp>
 python -I src/DeepEngineers.Launcher/launcher.py s15 --latest [--captures DIR]

Modes:
 S15se SE alone + the stand-in Subnautica host (tests/standins/fake_sn_host.py): the SE rows (pin, aboard, the gate, the
 welcome and power decisions, the water material, SE's sky rows, the SE captures)
 S15b both games: the same parts plus Subnautica's lines (MIX, S2, LINE), its audio groups (MUSIC), its story off (STORY,
 ALERT), its sky (MOONS, SKYLIST, SKYDIR, SKYSIZE, SKYPX) and its captures (BAND, the moon pair, the sky-size pair)
Parts:
 selftest de.s15 status (the pin), the water material applied at bind (de.s15 water apply), voice/sky on
 voice the project's four lines. A large unpowered ship (de.s15 spawn ship: light armour, a large cockpit seat, a
 large ion thruster) held at SN y -20 over a column >= 60 m deep: board (Welcome, the Cyclops), a reactor placed empty
 (no line), 5 kg of uranium into it (EnginePowerUp), the Y key off (EnginePowerDown), 11 s later the Y key on
 (EnginePowerUp) with SE's picture and audio forced on 6 s once the line has started (the line plays to its end above
 the water: S15.LINE; the music at its above-water level: S15.MUSIC), thrust 3 s (never a line), out; S16's 5^3 heavy
 shell around a dry room 30 m away (welcome by room) and a reactor placed on its roof with its fuel (EnginePowerUp);
 the open deck with one wall (never aboard); a small craft with a battery (SmallWelcome, the Seamoth's line); the
 spectator above the sea and a forced line (Subnautica must not play it); S15b: the Aurora's first warning triggered
 the game's way (de.s15 story aurora1: skipped, silent); last, a second small craft boarded with SE's picture forced
 (SE's audio live: skipped), then the view back to auto under the sea (no line: no dive greeting)
 sky two test bodies in the site's sky (de.s15 planet Moon 400 km at 35 deg, Europa 900 km at 60 deg; not saved), the
 spectator at SN y 150 looking north and up 20 deg, held 12 s, then 20 deg to the east 8 s; S15b: both pictures
 aimed at the largest shown body (de.s15 look; sky-se, sky-sn); the bodies removed; S15b: midnight (de.s15 clock
 0.0), Subnautica's picture along both signs of uSky's moon axis above the horizon (moon-a, moon-b); the clock back
 band SE's noon (de.s15 clock 0.5: SE's own clock, S7's dev phase off); the spectator at band enter + 50 m heading north: SE's own screenshot (de.s15 shot);
 at band enter - 50 m: Subnautica's capture (de.capture); the clock back (de.s7 time off)
 air SE's noon; the spectator 30 and 5 km above the site looking down (de.s15 orbit): SE's screenshot each; the clock back
 orbit SE's noon; the spectator 600, 200, 100 km above the site looking at the planet centre (de.s15 orbit): SE's
 screenshot each, 10 s after the move; the clock back
 sonar the kept pick 3: a craft held 20 m deep, boarded, the sonar switch on 12 s (a block renamed "... Sonar"), off, out
 life S15b, the kept pick 11: a craft boarded at 20 m, six Peepers and Boomerangs spawned near it, the craft descending at
 5 m/s to 45 m (the fish scatter), out
 teardown the test grids and bodies removed, the character parked, de.s15 status
Output tests/out/s15/<stamp>/ (s15-session.jsonl, se-*/sn-*.jsonl, captures/*.png, the game logs) and the S15 ORACLE TABLE of
tests/s15/analyze_s15.py (s15-table.txt, s15-results.json). This launcher never writes into a game folder, a save or SE's
LastSession; the world copy is the orchestrator's (tools/lab/make_survival_copy.py, SE closed)."""
from __future__ import annotations

import datetime
import glob
import json
import os
import shutil
import sys
import time

import launcher_s6 as L6

S15_MODES = ("S15se", "S15b")
ALL_PARTS = ("selftest", "voice", "sky", "band", "air", "orbit", "sonar", "life", "teardown")
S15_PARTS = {"S15se": tuple(p for p in ALL_PARTS if p != "life"), "S15b": ALL_PARTS}
SE_MUTANTS = ("S15.M1", "S15.M2", "S15.M4", "S15.M16", "S15.M17", "S15.M18")
SN_MUTANTS = ("S15.M3", "S15.M5", "S15.M6", "S15.M7", "S15.M11", "S15.M12", "S15.M13", "S15.M14", "S15.M15", "S15.M19", "S15.M20")
ALL_MUTANTS = SE_MUTANTS + SN_MUTANTS
S15_MUTANT_GROUPS = {
    "gate": {"mutants": ("S15.M1",), "mode": "S15se", "parts": ("selftest", "voice", "teardown"), "red": ("S15.VOICE",)},
    "deck": {"mutants": ("S15.M2",), "mode": "S15se", "parts": ("selftest", "voice", "teardown"), "red": ("S15.ABOARD",)},
    "unmapped": {"mutants": ("S15.M3",), "mode": "S15b", "parts": ("selftest", "sky", "teardown"), "red": ("S15.SKYDIR",)},
    "water": {"mutants": ("S15.M4",), "mode": "S15b", "parts": ("selftest", "band", "air", "orbit", "teardown"), "red": ("S15.AIR", "S15.ORBIT")},
    "moon": {"mutants": ("S15.M5",), "mode": "S15b", "parts": ("selftest", "sky", "teardown"), "red": ("S15.MOONS",)},
    "size": {"mutants": ("S15.M6",), "mode": "S15b", "parts": ("selftest", "sky", "teardown"), "red": ("S15.SKYSIZE",)},
    "s2": {"mutants": ("S15.M7",), "mode": "S15b", "parts": ("selftest", "voice", "teardown"), "red": ("S15.S2",)},
    "night": {"mutants": ("S15.M11",), "mode": "S15b", "parts": ("selftest", "sky", "teardown"), "red": ("S15.NIGHT",)},
    "splash": {"mutants": ("S15.M12",), "mode": "S15b", "parts": ("selftest", "voice", "teardown"), "red": ("S15.SPLASH",)},
    "sonar": {"mutants": ("S15.M13",), "mode": "S15b", "parts": ("selftest", "sonar", "teardown"), "red": ("S15.SONAR",)},
    "hum": {"mutants": ("S15.M14",), "mode": "S15b", "parts": ("selftest", "voice", "teardown"), "red": ("S15.SEABASE",)},
    "fish": {"mutants": ("S15.M15",), "mode": "S15b", "parts": ("selftest", "life", "teardown"), "red": ("S15.FISH",)},
    "thrust": {"mutants": ("S15.M16",), "mode": "S15se", "parts": ("selftest", "voice", "teardown"), "red": ("S15.POWER",)},
    "held": {"mutants": ("S15.M17",), "mode": "S15se", "parts": ("selftest", "voice", "teardown"), "red": ("S15.WELCOME", "S15.VOICE")},
    "small": {"mutants": ("S15.M18",), "mode": "S15se", "parts": ("selftest", "voice", "teardown"), "red": ("S15.WELCOME",)},
    "music": {"mutants": ("S15.M19",), "mode": "S15b", "parts": ("selftest", "voice", "teardown"), "red": ("S15.MUSIC", "S15.LINE")},
    "story": {"mutants": ("S15.M20",), "mode": "S15b", "parts": ("selftest", "voice", "teardown"), "red": ("S15.STORY",)},
}
SURVIVAL_WORLD = L6.SURVIVAL_WORLD
SUB_Y = -20.0
ROOM_Y = -40.0
ROOM_OFFSET = 30.0
DECK_OFFSET = -30.0
SKY_BODIES = ({"planet": "Moon", "km": 400.0, "size": 19000.0, "elevDeg": 35.0}, {"planet": "Europa", "km": 900.0, "size": 9500.0, "elevDeg": 60.0})
BAND_ENTER_M = 1000.0      # ViewThresholds enter (S7t T3); DEEPENGINEERS_BAND moves it: the launcher reads it from the env
ORBIT_KM = (600, 200, 100)
AIR_KM = (30, 5)
SUB_OFFSET = 12.0
SEAM_CLOCK = 0.25           # the site's sunrise: SE's sun on the horizon, the Water Mod's terminator through the site (S15.SEAM.PAIR)
SEAM_KM = 5                 # the pair's height (the look sheet's 5 km seam)
SEAM_SHIFT_M = 4000.0       # the pair's camera move east (the analyzer's SEAM_SHIFT_M)
SEA_SPECULAR = "0"          # S15Conventions.Water.SpecularIntensity (format water.material: the Water Mod's specular off on DE_4546B)
APPROACH_M = 15.0           # the water beside a craft the boarding starts from (outside the ship's 12.5 m floor and the room's 12.5 m box)
SN_AREA_M = 2300.0          # Subnautica's area around the site (run 20261024-054404: (2145, 2121) built, (3024, 0) never)
COLUMN_STARTS = ((0, 0), (1500, 1500), (-1500, 1500), (1500, -1500), (-1500, -1500), (0, 1500), (0, -1500), (1500, 0), (-1500, 0))
SHIP_REACTOR_CELL = (0, 1, 0)   # de.s15 spawn ship leaves it free beside the seat
ROOM_REACTOR_CELL = (2, 5, 2)   # on the room's roof (S16's 5^3 shell: the roof at y = 4)
FUEL_KG = 5.0
POWER_COOLDOWN_S = 10.0         # S15Conventions voice 2/3 cooldownMs (format voice.gate)
CROSSING_S = 6.0                # SE's picture and audio forced on while a line plays (S15.LINE, S15.MUSIC)
LIFE_FISH, LIFE_FISH_DIST = 6, 5
LIFE_SCHOOL = ("Peeper", "Boomerang")   # spawned as the descent starts (run 20261024-054404: Peepers spawned 3 s before had left)
SEA_MATERIAL = "DE_4546B_Sea"   # the planet's Water Mod material (S15Conventions.WaterFar.SeaMaterial, format water.material)
PLAIN_MATERIAL = "Water"        # the Water Mod's own, the band's information pair (WaterFar.PlainMaterial, format band.colour)
MOON_MIN_SIN = 0.0871557      # sin 5 deg: a moon direction lower than this is under the horizon (the analyzer's MOON_MIN_SIN)
SKY_MIN_SIN = 0.2588190       # sin 15 deg: the sky-size body well above the horizon
NOTES = """S15 free play (owner-attended; launcher.py run, both games, the usual world; S15 is on by default in both plugins; deploy
the world mod first for the water). The Cyclops voice is now only your four lines (2026-10-24). In this order, and tell me what
you hear and see:

 1. Welcome aboard. Under water, sit in the cockpit of a LARGE ship (or walk into a sealed room of a large ship or a base):
    after about a second the Cyclops says "Welcome aboard, captain". A SMALL ship greets you with the Seamoth's own boarding line
    instead. Out and back in within a few seconds: nothing; after two minutes: again. A door, a ladder or a deck never greets you.
    Board at the surface (SE's picture) and dive: NOTHING, not on the board (SE's sound is the one you hear there) and not later
    (no more "welcome" when diving).
 2. Engine powering up. Aboard (seated, in a sealed room, or in your base), the moment the grid gets power: place a reactor that
    has uranium, or put uranium into an empty reactor, or switch the power back on with Y: "Engine powering up". Never on thrust.
 3. Engine shutting down. Seated, press Y (the power switch) off: "Engine shutting down". Y on again: "Engine powering up".
    Each once per switch (a quick double press within 10 s says it once). "Hull integrity critical" stays as it was (S16).
    Gone for good: fire, silent running, abandon ship and its countdown, hull breach / restored, engine power down on thrust,
    the oxygen warnings of ours (Subnautica's own low-oxygen alert is back, untouched) and the first-encounter lines.
 4. Subnautica's sound across the surface. Under water with Subnautica's music playing, come up above the water: the music goes
    on (quieter: 0.6) and comes back to full when you dive again, over about 2 s; the water's ambience and the effects still
    stop above water as before, SE's own sound unchanged. A voice line that starts under water finishes above it. Tune the
    music above water live: /de sn s15 music 0.4 (and /de sn s15 voiceAbove 0.8); tell me the value you like.
 5. Subnautica's story is off: no PDA or radio lines about the Aurora, no Aurora countdown or explosion (no camera shake, no
    flash). Tell me if any Subnautica story voice still plays (its words help me find it).
 6. The planet from orbit to the seabed. Fly from space to the sea: from 600, 200, 100, 30 and 5 km the sea should be
    opaque (no seabed through it: the Water Mod's Transparent is off for DE_4546B, set once per world load; your
    /wtransparent still toggles it for the session) and its blue the hue of the 4546B ending, and the colours at the
    hand-over (1 km) should not jump. /de s15 water off goes back to the Water Mod's JWater2, /de s15 water on to ours.
    Where the sea's day side meets its night side (from orbit, or at sunrise and sunset) there should be no hard straight
    line across the sea any more (the Water Mod's specular is off for DE_4546B; /de water specularIntensity=1 shows the old
    line for comparison, =0 back); a faint faceting of the Water Mod's tiles near that line is the mod's own.
 7. SE's planets in Subnautica's sky. Above the water (Subnautica's picture), look for SE's other planets and moons of your
    world where SE shows them: same place, same size, lit on the sun's side; Subnautica's own two moons are gone.
    /de sn s15 sky off brings Subnautica's moons back (compare), on again.
 8. The sonar: name any functional block of your ship with "Sonar" in its name and switch it on: seated under water it pings
    every 5 s like the Cyclops (its sound and the screen pulse); switch it off to stop.
 9. At night under water your ship's lights dim to 40 % the Subnautica way; coming up through the surface, water runs down the
    screen for a second; in a sealed room under water Subnautica's base hum and a warm room light; small fish scatter from a
    fast ship.
 Dev: /de s15 status | play welcome|engine|down|small | power on|off | voice on|off | water on|off|apply | sky on|off;
      /de sn s15 status | voice on|off | sky on|off | music 0..1 | voiceAbove 0..1."""
QUICK = L6.QUICK

class S15Runner(L6.S6Runner):
    """The S15 parts through launcher_s6's plumbing (the base runs in its S6 mode: S15b as S6b, S15se as S6se)."""

    def __init__(self, LCH, log, a, sess, sn, se, out, stamp, t_start, s15mode):
        self.s15mode = s15mode
        saved = a.mode
        a.mode = "S6b" if s15mode == "S15b" else "S6se"
        try:
            super().__init__(LCH, log, a, sess, sn, se, out, stamp, t_start)
        finally:
            a.mode = saved
        self.spawned = []
        self.LCH = LCH
        self.capdir = os.path.join(out, "captures")
        os.makedirs(self.capdir, exist_ok=True)

    def s15(self, args, label, timeout=10.0):
        return self.call("se", "de.s15", args, timeout=timeout, label=label)

    def s16(self, args, label, timeout=10.0):
        return self.call("se", "de.s16", args, timeout=timeout, label=label)

    def mutants_on(self, mutants):
        for m in mutants:
            if m in SN_MUTANTS and self.s15mode == "S15b":
                self.call("sn", "de.mutant", {"name": m, "on": True}, label="mutant.sn.%s" % m)
            if m in SE_MUTANTS:
                self.call("se", "de.mutant", {"name": m, "on": True}, label="mutant.se.%s" % m)

    def keep_grid(self, label, rep):
        g = grid_of(rep)
        if g:
            self.spawned.append(g)
            self.sess.event("s15_grid", label=label, grid=g)
        return g

    def hold(self, gid, depth, label):
        self.s16({"descend": gid, "rate": 0.5, "to": abs(depth), "label": label}, "%s.hold" % label)

    # ---- parts --------------------------------------------------------------------------------------------------
    def p_selftest(self):
        """de.s15 status (the pin), the switches on, the water material applied (de.s15 water apply)"""
        self.part("s15.selftest", "begin")
        self.s15({"status": True}, "selftest.status")
        self.s15({"voice": "on"}, "selftest.voice")
        self.s15({"sky": "on"}, "selftest.sky")
        self.s15({"water": "apply"}, "selftest.water")
        self.part("s15.selftest", "end")

    def p_voice(self):
        """the four lines: the ship (welcome, reactor empty, fuel, Y off, Y on across the surface, thrust), the room, the deck, the sub, the surface board"""
        self.part("s15.voice", "begin")
        col = self.find_column(60.0, "voice.deep")
        if not self.ok(col):
            self.log.say("  S15 voice: no column 60 m deep (%s)" % (json.dumps(col)[:160] if col else "no reply"), ev="s15_no_column")
            self.part("s15.voice", "end")
            return
        x, z = col["x"], col["z"]
        ship = self.keep_grid("ship", self.s15({"spawn": "ship", "x": x, "y": SUB_Y, "z": z, "label": "s15ship"}, "voice.ship"))
        if ship:
            self.hold(ship, SUB_Y, "ship")
            self.approach(x, SUB_Y, z - APPROACH_M, "voice.ship.near")      # Subnautica's picture and audio live before the boarding
            self.s15({"board": ship}, "voice.ship.board")
            self.wait(4.0)                                                   # Welcome (the Cyclops: a large grid), played
            self.wait_underwater("voice.ship.board")
            self.s15({"reactor": ship, "cell": list(SHIP_REACTOR_CELL), "fuel": 0}, "voice.ship.reactor")
            self.wait(3.0)                                                   # a reactor without fuel: no power, no line
            self.voice_step("EnginePowerUp", lambda: self.s15({"fuel": ship, "kg": FUEL_KG}, "voice.ship.fuel"), 5.0, 3.0, "voice.ship.fuel",
                            again=lambda: (self.s15({"power": "off"}, "voice.ship.fuel.off"), self.wait(3.0), self.s15({"power": "on"}, "voice.ship.fuel.on")))
            t_up = time.monotonic()
            self.voice_step("EnginePowerDown", lambda: self.s15({"power": "off"}, "voice.ship.y.off"), 4.0, 3.0, "voice.ship.y.off",
                            again=lambda: (self.s15({"power": "on"}, "voice.ship.y.off.on"), self.wait(3.0), self.s15({"power": "off"}, "voice.ship.y.off2")))
            self.wait(max(0.0, POWER_COOLDOWN_S + 1.0 - (time.monotonic() - t_up)))   # the power lines' cooldown (10 s per event)
 # Y on: EnginePowerUp; once Subnautica started the line, SE's picture and audio forced on (the Prime view: Subnautica keeps
 # running) for CROSSING_S: the line plays to its end above the water (S15.LINE), the music at its above-water level (S15.MUSIC)
            self.wait_live("voice.ship.y.on")
            before = self.sn_played("voice.ship.y.on.before")
            self.s15({"power": "on"}, "voice.ship.y.on")
            started = self.wait_sn_played(before, "voice.ship.y.on.line")
            self.call("se", "de.view", {"force": "prime"}, label="voice.ship.cross.prime")
            self.sess.event("s15_crossing", started=started)
            self.wait(CROSSING_S)
            self.call("se", "de.view", {"force": "auto"}, label="voice.ship.cross.auto")
            self.wait_underwater("voice.ship.cross.auto")
            self.wait(3.0)
            self.s15({"thrust": ship, "on": True}, "voice.ship.thrust")      # never a line on thrust
            self.wait(3.0)
            self.s15({"thrust": ship, "on": False}, "voice.ship.thrust.off")
            self.wait(2.0)
            self.s15({"unboard": True}, "voice.ship.unboard")
            self.wait(2.0)
        rc = self.s16({"deep": abs(ROOM_Y) + 30.0, "near": [x + ROOM_OFFSET, z]}, "voice.room.deep", timeout=20.0)
        rx, rz = (rc["x"], rc["z"]) if self.ok(rc) else (x + ROOM_OFFSET, z)
        room = self.keep_grid("room", self.s16({"spawn": "room", "x": rx, "y": ROOM_Y, "z": rz, "label": "s15room"}, "voice.room"))
        if room:
            self.hold(room, ROOM_Y, "room")
            self.approach(rx, ROOM_Y, rz - APPROACH_M, "voice.room.near")    # the wait there lets the gas system find the dry room too
            self.wait(self.scale(14.0, 6.0))
            self.wait_live("voice.room.near.again")
            # the floor of cell (2, 1, 1|3): 2.5 m off the centre along z, feet on the top of cell row 0 (centre - 3.75 m)
            self.swim({"teleportChar": [rx, ROOM_Y - 3.70, rz + 2.5], "yawDeg": 0.0}, "voice.room.in")
            self.wait(5.0)                                                   # Welcome by room (a large grid), played
            self.wait_underwater("voice.room.in")
            self.voice_step("EnginePowerUp", lambda: self.s15({"reactor": room, "cell": list(ROOM_REACTOR_CELL), "fuel": FUEL_KG}, "voice.room.reactor"),
                            5.0, 3.0, "voice.room.reactor")                  # a reactor placed with its fuel: the power from none to some
            self.park()
        dc = self.s16({"deep": abs(SUB_Y) + 20.0, "near": [x + DECK_OFFSET, z]}, "voice.deck.deep", timeout=20.0)
        dx, dz = (dc["x"], dc["z"]) if self.ok(dc) else (x + DECK_OFFSET, z)
        deck = self.keep_grid("deck", self.s15({"spawn": "deck", "x": dx, "y": SUB_Y, "z": dz, "label": "s15deck"}, "voice.deck"))
        if deck:
            self.hold(deck, SUB_Y, "deck")
            self.wait(2.0)
            # on the floor (the box is 3 cells high: floor top at centre - 1.25 m), 2.5 m off the wall's side
            self.swim({"teleportChar": [dx + 2.5, SUB_Y - 1.20, dz], "yawDeg": 0.0}, "voice.deck.on")
            self.wait(5.0)                                                   # never aboard
            self.park()
        sub = self.keep_grid("sub", self.s15({"spawn": "sub", "x": x + SUB_OFFSET, "y": SUB_Y, "z": z, "label": "s15sub"}, "voice.sub"))
        if sub:
            self.hold(sub, SUB_Y, "sub")
            self.approach(x + SUB_OFFSET, SUB_Y, z - APPROACH_M, "voice.sub.near")
            self.s15({"board": sub}, "voice.sub.board")
            self.wait(4.0)                                                   # SmallWelcome (the Seamoth's line: a small grid; powered by its battery: no engine line)
            self.wait_underwater("voice.sub.board")
            self.s15({"unboard": True}, "voice.sub.unboard")
            self.wait(2.0)
            self.park()
        # SE's audio is live above the sea: a forced line must stay silent on Subnautica (the S2 rule); the surfacing splashes the lens
        self.teleport(x, 50.0, z, 0.0, label="voice.above")
        self.wait(3.0)
        self.s15({"play": "welcome"}, "voice.play.above")
        self.wait(3.0)
        if self.s15mode == "S15b":
            # Subnautica's story off: the Aurora's first warning the game's way (StoryGoal.Execute): skipped, no PDA line
            rep = self.call("sn", "de.s15", {"story": "aurora1"}, label="voice.story")
            self.sess.event("s15_story", key=(rep or {}).get("storyKey"), reply=rep)
            self.wait(3.0)
        self.park()
        # boarded with SE's audio live, then the dive: no line at all
        sub2 = self.keep_grid("sub2", self.s15({"spawn": "sub", "x": x - SUB_OFFSET, "y": SUB_Y, "z": z, "label": "s15sub2"}, "voice.surface.spawn"))
        if sub2:
            self.hold(sub2, SUB_Y, "sub2")
            self.wait(2.0)
            self.call("se", "de.view", {"force": "space"}, label="voice.surface.view")
            self.wait(3.0)
            self.s15({"board": sub2}, "voice.surface.board")
            self.wait(4.0)                                                   # aboard after 1 s: skipped (SE's audio live)
            self.call("se", "de.view", {"force": "auto"}, label="voice.surface.dive")
            self.wait(8.0)                                                   # Subnautica's picture and audio: still nothing
            self.sess.event("s15_surface", grid=sub2)
            self.s15({"unboard": True}, "voice.surface.unboard")
            self.wait(2.0)
            self.park()
        self.part("s15.voice", "end")

    def approach(self, x, y, z, label):
        """The character into the water APPROACH_M beside a craft, then Subnautica's picture and audio live: the boarding that
 follows is a short move (under ViewStateDriver.ResettleJumpM, 150 m: no teleport event, no re-streaming), as when the
 project swims in. Run 20261024-133305 boarded from the park spot 2.3 km away: every boarding re-streamed Subnautica's
 area with SE's audio live, and the rule skipped every welcome (format voice.gate: met with SE's audio live = skipped)."""
        self.swim({"teleportChar": [x, y, z], "yawDeg": 0.0}, label)
        self.swim({"refill": True}, "%s.refill" % label)
        self.wait_underwater(label)
        self.wait(2.0)
        return self.wait_live(label)

    def sn_played(self, label):
        """Subnautica's played line count (de.s15 status), -1 without Subnautica (S15se)."""
        if self.s15mode != "S15b":
            return -1
        rep = self.call("sn", "de.s15", {"status": True}, label=label)
        return int((rep or {}).get("played", -1)) if self.ok(rep) else -1

    def wait_sn_played(self, before, label, timeout=4.0):
        """Until Subnautica played one more line (the crossing starts while it plays); False after the timeout."""
        if self.s15mode != "S15b" or before < 0:
            self.wait(1.0)
            return False
        t_end = time.monotonic() + timeout
        while time.monotonic() < t_end and self.alive():
            if self.sn_played("%s.poll" % label) > before:
                return True
            self.wait(0.2)
        return False

    def p_sky(self):
        """two test bodies in the site's sky, the spectator at SN y 150 looking north and up, then east; the bodies removed"""
        self.part("s15.sky", "begin")
        for b in SKY_BODIES:
            rep = self.s15(dict(b), "sky.planet.%s" % b["planet"], timeout=60.0)
            if not self.ok(rep):
                self.log.say("  S15 sky: %s not spawned (%s)" % (b["planet"], json.dumps(rep)[:160] if rep else "no reply"), ev="s15_planet_failed")
        self.wait(self.scale(12.0, 6.0))                             # SE lists them within 300 ticks
        self.teleport(0.0, 150.0, 0.0, 0.0, label="sky.view")
        self.call("se", "de.look", {"dir": "pitch+20"}, label="sky.pitch")
        self.wait(self.scale(12.0, 6.0))
        self.call("se", "de.look", {"dir": "yaw+20"}, label="sky.yaw")
        self.wait(self.scale(8.0, 4.0))
        if self.s15mode == "S15b":
            self._sky_size()
        self.s15({"planet": "remove"}, "sky.remove")
        if self.s15mode == "S15b":
            self._moons()
        self.park()
        self.part("s15.sky", "end")

    def _sky_size(self):
        """both pictures aimed at the largest disc Subnautica shows (format sky.size: pixels, information)"""
        # well above the horizon (run 20261024-003024: Mars at 2 deg put the sea in the frame)
        shown = [b for b in (self._sn_sky("sky.size.list").get("shown") or []) if b.get("dir") and float(b["dir"][1]) >= SKY_MIN_SIN]
        if not shown:
            self.log.say("  S15 sky: Subnautica shows no disc to measure", ev="s15_no_disc")
            return
        b = max(shown, key=lambda q: float(q.get("angDeg") or 0.0))
        lk = self._look(b["dir"], "sky.size.look")
        if lk is None:
            return
        extra = {"body": b.get("name"), "angDeg": b.get("angDeg"), "dir": b["dir"], "vFovDeg": lk.get("vFovDeg")}
        self._se_shot("sky-se", 4.0, **extra)
        self._sn_capture("sky-sn", 1.0, prime=True, **extra)

    def _moons(self):
        """midnight; Subnautica's picture along both signs of uSky's moon axis (format moons.hide); the clock back"""
        self.clock(0.0, "moons.midnight")
        self.wait(self.scale(6.0, 3.0))
        axis = self._sn_sky("moons.axis").get("moonAxis")
        self.sess.event("s15_moon", axis=axis)
        if axis and len(axis) == 3:
            for kind, sign in (("moon-a", -1.0), ("moon-b", 1.0)):
                d = [sign * float(v) for v in axis]
                if d[1] < MOON_MIN_SIN:
                    self.sess.event("s15_moon_skip", kind=kind, dir=d, why="under the horizon")
                    continue
                if self._look(d, "moons.%s" % kind) is not None:
                    self._sn_capture(kind, 4.0, prime=True, dir=d)
        self.clock(0.5, "moons.noon")

    def clock(self, c, label):
        """SE's own clock to the site's cycle time c (de.s15 clock: SE's ElapsedGameTime), S7's dev phase off first."""
        self.call("se", "de.s7", {"time": "off"}, label="%s.devoff" % label)
        rep = self.s15({"clock": c}, label)
        sn = self.call("sn", "de.s15", {"time": c}, label="%s.sn" % label) if self.s15mode == "S15b" else None
        self.sess.event("s15_clock", c=c, reply=rep, sn=sn)
        self.wait(3.0)
        return rep

    def find_column(self, need, label):
        """A clear column at least need m deep inside Subnautica's area (|x|, |z| <= SN_AREA_M): S16's search from several
 points (it rejects the columns over S8a's terrain tiles, so from the site's centre it found (3024, 0), where Subnautica
 never built its area: run 20261024-054404); the first inside wins, else the last reply (logged)."""
        last = None
        for nx, nz in COLUMN_STARTS:
            rep = self.s16({"deep": need, "near": [nx, nz]}, "%s.%d.%d" % (label, nx, nz), timeout=20.0)
            if not self.ok(rep):
                continue
            last = rep
            if abs(float(rep["x"])) <= SN_AREA_M and abs(float(rep["z"])) <= SN_AREA_M:
                self.sess.event("s15_column", label=label, need=need, x=rep["x"], z=rep["z"], inside=True)
                return rep
        if last is not None:
            self.sess.event("s15_column", label=label, need=need, x=last["x"], z=last["z"], inside=False)
            self.log.say("  S15 %s: no %.0f m column inside Subnautica's area; using (%s, %s)" % (label, need, last["x"], last["z"]), ev="s15_column_outside")
        return last

    def wait_underwater(self, label, timeout=30.0):
        """Until SE's view state is Underwater (Subnautica's picture and audio): far from the site Subnautica's area needs
 time to build (run 20261024-041144: the voice part's craft stayed in SE's audio, every line held and expired)."""
        t_end = time.monotonic() + self.scale(timeout, 10.0)
        last = None
        while time.monotonic() < t_end and self.alive():
            rep = self.call("se", "de.view", {}, label="%s.view" % label)
            st = (rep or {}).get("state") or {}
            last = st
            if st.get("view") == "Underwater":
                self.sess.event("s15_underwater", label=label, ok=True)
                return True
            self.wait(1.0)
        self.sess.event("s15_underwater", label=label, ok=False, view=(last or {}).get("view"), blocked=(last or {}).get("blocked"))
        self.log.say("  S15 %s: never Underwater (view %s, blocked %s)" % (label, (last or {}).get("view"), (last or {}).get("blocked")), ev="s15_not_underwater")
        return False

    def s15_status(self, label):
        rep = self.s15({"status": True}, label)
        return ((rep or {}).get("status") or {}) if self.ok(rep) else {}

    def played(self, line, label):
        """How many times SE's gate played this line so far (de.s15 status playedBy)."""
        return int((self.s15_status(label).get("playedBy") or {}).get(line, 0) or 0)

    def wait_live(self, label, timeout=20.0):
        """Until S15's gate sees Subnautica's audio live (de.s15 status live): a line met while SE's audio is live is held and
 dropped when its condition ends first (run 20261024-072836: SE lost the foreground for 6 s, DE.Audio gate=closed, and the
 bite's 3 s condition ended 170 ms before the audio came back)."""
        if self.s15mode != "S15b":
            return True   # the stand-in host plays nothing: S15se judges the decisions
        t_end = time.monotonic() + self.scale(timeout, 10.0)
        t0 = time.monotonic()
        while time.monotonic() < t_end and self.alive():
            if self.s15_status("%s.live" % label).get("live"):
                self.sess.event("s15_live", label=label, ok=True, waitedS=round(time.monotonic() - t0, 1))
                return True
            self.wait(0.5)
        self.sess.event("s15_live", label=label, ok=False, waitedS=round(time.monotonic() - t0, 1))
        self.log.say("  S15 %s: Subnautica's audio never live (SE in front? the bridge?)" % label, ev="s15_not_live")
        return False

    def voice_step(self, line, act, settle, rearm_s, label, again=None):
        """act, then settle; when the line did not play (a pulse met while SE's audio was live), wait its rearm, Subnautica's
 audio, and act once more (again, else act; the s15_retry event names it)."""
        before = self.played(line, "%s.before" % label)
        self.wait_live(label)
        act()
        self.wait(settle)
        if self.s15mode != "S15b" or self.played(line, "%s.after" % label) > before:
            return True
        self.sess.event("s15_retry", label=label, line=line, rearmS=rearm_s)
        self.log.say("  S15 %s: %s not played, once more after %.0f s" % (label, line, rearm_s), ev="s15_retry")
        self.wait(rearm_s)
        self.wait_live("%s.retry" % label)
        (again or act)()
        self.wait(settle)
        ok = self.played(line, "%s.retried" % label) > before
        self.sess.event("s15_retried", label=label, line=line, played=ok)
        return ok

    def _noon(self, on):
        if on:
            self.clock(0.5, "noon")

    def _se_shot(self, name, settle, **extra):
        path = os.path.join(self.capdir, name + ".png")
        self.wait(settle)
        rep = self.s15({"shot": path}, "shot.%s" % name)
        got = wait_file(path, 15.0)
        self.sess.event("s15_capture", kind=name, path=path, written=got, via="se", reply=rep, **extra)
        return got

    def _sn_capture(self, name, settle, prime=False, **extra):
        if prime:
            # above the sea SE's Track recipe keeps Subnautica's cameras off (run 20261024-003024: the capture never
 # triggered): the Prime state asks for the Full recipe (cameras on), auto again after the capture
            self.call("se", "de.view", {"force": "prime"}, label="capture.%s.prime" % name)
            settle = max(settle, 4.0)
        self.wait(settle)
        rel = "s15/%s/%s.png" % (self.stamp, name)
        rep = self.call("sn", "de.capture", {"path": rel, "checkId": "S15.%s" % name, "label": name, "kind": 1}, timeout=10.0, label="capture.%s" % name)
        src = os.path.join(self.LCH.CAPTURES, *rel.split("/"))
        got = wait_file(src, 10.0)
        dst = os.path.join(self.capdir, name + ".png")
        if got:
            shutil.copyfile(src, dst)
        self.sess.event("s15_capture", kind=name, path=dst if got else None, written=got, via="sn", reply=rep, source=src, **extra)
        if prime:
            self.call("se", "de.view", {"force": "auto"}, label="capture.%s.auto" % name)
        return got

    def _sn_sky(self, label):
        """Subnautica's de.s15 reply: its shown discs (SN directions) and uSky's moon axis."""
        rep = self.call("sn", "de.s15", {"status": True}, label=label)
        return rep if self.ok(rep) else {}

    def _look(self, d, label):
        rep = self.s15({"look": [float(v) for v in d]}, label)
        return rep if self.ok(rep) else None

    def p_band(self):
        """SE's noon; SE's screenshot at band enter + 50 m, Subnautica's capture at band enter - 50 m, same heading"""
        self.part("s15.band", "begin")
        self._noon(True)
        enter = band_enter()
        self.teleport(0.0, enter + 50.0, 0.0, 0.0, label="band.se")
        self._se_shot("band-se", 5.0)
        # the planet's material is DE_4546B_Sea (Reflectivity 0.5, run 20261024-063156); the information pair: the Water Mod's own,
 # then the material read before back (S15.M4's look keeps the mod's own)
        was = water_token(self.call("se", "de.water", {}, label="band.material.read"), "material") or SEA_MATERIAL
        self.call("se", "de.water", {"material": PLAIN_MATERIAL}, label="band.material.plain")
        self._se_shot("band-se-plain", 4.0, material=PLAIN_MATERIAL)
        self.call("se", "de.water", {"material": was}, label="band.material.back")
        if self.s15mode == "S15b":
            self.teleport(0.0, enter - 50.0, 0.0, 0.0, label="band.sn")
            self._sn_capture("band-sn", 8.0, prime=True)
        self._noon(False)
        self.park()
        self.part("s15.band", "end")

    def p_air(self):
        """SE's noon; the spectator 30 and 5 km above the site looking down; SE's screenshot each (S15.AIR, S15.SEAM). Then the seam
 pair (S15.SEAM.PAIR, information): SE's clock at the site's sunrise (the Water Mod's terminator through the site) with the
 mod's own specular (de.water specularIntensity 1), 5 km above the site and SEAM_SHIFT_M east of it (a step that moves with
 the ground is the mod's quad terminator; one fixed on the screen would be the sky's reflection), then the planet's own
 (S15Conventions.Water.SpecularIntensity: no step); SE's noon back"""
        self.part("s15.air", "begin")
        self._noon(True)
        for km in AIR_KM:
            rep = self.s15({"orbit": km}, "air.%d" % km) or {}
            self._se_shot("air-%d" % km, self.scale(8.0, 4.0), km=km, rs=rep.get("rs"), vFovDeg=rep.get("vFovDeg"))
        self.clock(SEAM_CLOCK, "seam.clock")
        was = water_token(self.call("se", "de.water", {}, label="seam.read"), "specularIntensity")
        self.call("se", "de.water", {"specularIntensity": "1"}, label="seam.spec.mod")
        for name, x in (("seam-5a", 0.0), ("seam-5b", SEAM_SHIFT_M)):
            rep = self.s15({"orbit": SEAM_KM, "x": x, "z": 0.0}, "seam.%s" % name) or {}
            self._se_shot(name, self.scale(8.0, 4.0), km=SEAM_KM, x=x, specular=1.0, clock=SEAM_CLOCK, vFovDeg=rep.get("vFovDeg"))
        self.call("se", "de.water", {"specularIntensity": SEA_SPECULAR}, label="seam.spec.ours")
        rep = self.s15({"orbit": SEAM_KM, "x": 0.0, "z": 0.0}, "seam.seam-5s") or {}
        self._se_shot("seam-5s", self.scale(8.0, 4.0), km=SEAM_KM, x=0.0, specular=float(SEA_SPECULAR), clock=SEAM_CLOCK, vFovDeg=rep.get("vFovDeg"))
        if was is not None and was != SEA_SPECULAR:
            self.call("se", "de.water", {"specularIntensity": was}, label="seam.spec.back")
        self._noon(True)
        self.park()
        self.part("s15.air", "end")

    def p_orbit(self):
        """SE's noon; the spectator 600, 200, 100 km above the site looking at the planet centre; SE's screenshot each"""
        self.part("s15.orbit", "begin")
        self._noon(True)
        for km in ORBIT_KM:
            rep = self.s15({"orbit": km}, "orbit.%d" % km) or {}
            self._se_shot("orbit-%d" % km, self.scale(10.0, 4.0), km=km, rs=rep.get("rs"), vFovDeg=rep.get("vFovDeg"))
        self._noon(False)
        self.park()
        self.part("s15.orbit", "end")

    def p_teardown(self):
        """the test grids and bodies removed, the character parked, de.s15 status"""
        self.part("s15.teardown", "begin")
        for gid in self.spawned:
            self.s16({"stop": gid}, "teardown.stop")
            self.s16({"remove": gid}, "teardown.remove")
        self.s15({"planet": "remove"}, "teardown.planets")
        self.s15({"status": True}, "teardown.status")
        self.part("s15.teardown", "end")

    def p_sonar(self):
        """the kept pick 3: a craft 20 m deep, boarded, the sonar switch on 12 s (three pings), off, out"""
        self.part("s15.sonar", "begin")
        col = self.find_column(40.0, "sonar.deep")
        if not self.ok(col):
            self.log.say("  S15 sonar: no column 40 m deep (%s)" % (json.dumps(col)[:160] if col else "no reply"), ev="s15_no_column")
            self.part("s15.sonar", "end")
            return
        x, z = col["x"], col["z"]
        sub = self.keep_grid("sub3", self.s15({"spawn": "sub", "x": x, "y": SUB_Y, "z": z, "label": "s15sub3"}, "sonar.spawn"))
        if sub:
            self.hold(sub, SUB_Y, "sub3")
            self.wait(2.0)
            self.s15({"board": sub}, "sonar.board")
            self.wait_underwater("sonar.board")
            self.wait(2.0)
            self.wait_live("sonar.live")
            self.s15({"sonar": sub, "on": True}, "sonar.on")
            self.wait(12.0)                                          # three pings
            self.s15({"sonar": sub, "on": False}, "sonar.off")
            self.s15({"unboard": True}, "sonar.unboard")
            self.wait(2.0)
            self.park()
        self.part("s15.sonar", "end")

    def p_life(self):
        """S15b: fish scattering from a craft descending at 5 m/s through a spawned school"""
        self.part("s15.life", "begin")
        if self.s15mode != "S15b":
            self.part("s15.life", "end")
            return
        col = self.find_column(70.0, "life.deep")
        if not self.ok(col):
            self.log.say("  S15 life: no column 70 m deep", ev="s15_no_column")
            self.part("s15.life", "end")
            return
        x, z = col["x"], col["z"]
        sub = self.keep_grid("sub4", self.s15({"spawn": "sub", "x": x, "y": SUB_Y, "z": z, "label": "s15sub4"}, "life.spawn"))
        if sub:
            self.hold(sub, SUB_Y, "sub4")
            self.wait(2.0)
            self.s15({"board": sub}, "life.board")
            self.wait_underwater("life.board")
            self.wait(3.0)
            self.s16({"descend": sub, "rate": 5.0, "to": 45.0, "label": "sub4"}, "life.dive")
            for tech in LIFE_SCHOOL:
                self.call("sn", "de.spawn", {"techType": tech, "count": LIFE_FISH, "dist": LIFE_FISH_DIST}, label="life.fish.%s" % tech)
            self.wait(7.0)                                           # 5 m/s past the school: it flees
            self.hold(sub, -45.0, "sub4")
            self.s15({"unboard": True}, "life.unboard")
            self.park()
        self.part("s15.life", "end")

    def keep_breathing(self):
        if self.s15mode != "S15b":
            return
        now = time.monotonic()
        if now - self._last_refill < L6.REFILL_S:
            return
        self._last_refill = now
        self.call("se", "de.swim", {"refill": True}, label="o2.refill")

# ----------------------------------------------------------------------------------------------------------------------
# helpers
# ----------------------------------------------------------------------------------------------------------------------

def wait_file(path, timeout):
    t0 = time.monotonic()
    while time.monotonic() - t0 < timeout:
        if os.path.isfile(path) and os.path.getsize(path) > 0:
            time.sleep(0.3)
            return True
        time.sleep(0.25)
    return False

def water_token(rep, key):
    """A token of de.water's state line (key=value, space separated), or None."""
    st = rep.get("state") if isinstance(rep, dict) else None
    for tok in str(st or "").split():
        if tok.startswith(key + "="):
            return tok[len(key) + 1:] or None
    return None

def band_enter():
    v = os.environ.get("DEEPENGINEERS_BAND", "")
    try:
        return float(v.split(",")[0]) if v else BAND_ENTER_M
    except ValueError:
        return BAND_ENTER_M

def grid_of(rep):
    """The spawned grid's id from a de.s15/de.s16 spawn reply: the string "grid" (never "id", the C11 reply's own key)."""
    if not L6.S6Runner.ok(rep):
        return None
    g = rep.get("grid")
    return str(g) if g not in (None, "", 0, "0") else None

def analyzer(LCH=None):
    p = os.path.join(LCH.REPO if LCH else os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")), "tests", "s15")
    if p not in sys.path:
        sys.path.insert(0, p)
    import analyze_s15 as AN   # noqa: E402
    return AN

def env_for(mode, mutants):
    """(Subnautica env, SE env): S15 on (its default; set explicitly), S6 as the s16 sessions; the mutants on their side."""
    sn_env = {"DEEPENGINEERS_S6": "on", "DEEPENGINEERS_S15": "on"}
    se_env = {"DEEPENGINEERS_S5": "on", "DEEPENGINEERS_S6": "on", "DEEPENGINEERS_S16": "on", "DEEPENGINEERS_S15": "on"}
    sn_m = [m for m in mutants if m in SN_MUTANTS]
    se_m = [m for m in mutants if m in SE_MUTANTS]
    if sn_m and mode == "S15b":
        sn_env["DEEPENGINEERS_MUTANT"] = ",".join(sn_m)
    if se_m:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(se_m)
    return sn_env, se_env

def resolve(a, mode):
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    parts = None
    probs = []
    if a.mutant_group:
        g = S15_MUTANT_GROUPS[a.mutant_group]
        mutants, parts = list(g["mutants"]), list(g["parts"])
    if a.parts and a.parts.strip().lower() == "all":
        parts = list(S15_PARTS[mode])
    elif a.parts:
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
    if parts is None:
        parts = list(S15_PARTS[mode])
    unknown = [p for p in parts if p not in S15_PARTS[mode]]
    if unknown:
        probs.append("parts %s are not %s parts (known: %s)" % (unknown, mode, ",".join(S15_PARTS[mode])))
    bad = [m for m in mutants if m not in ALL_MUTANTS]
    if bad:
        probs.append("unknown mutants %s (known: %s)" % (bad, ",".join(ALL_MUTANTS)))
    sn_only = [m for m in mutants if mode == "S15se" and m in SN_MUTANTS]
    if sn_only:
        probs.append("mutants %s are Subnautica's: they need S15b (S15se runs the stand-in host)" % sn_only)
    return parts, mutants, a.world or SURVIVAL_WORLD, probs

def host_cmd(LCH, a):
    return [sys.executable, "-I", os.path.join(LCH.REPO, "tests", "standins", "fake_sn_host.py"), "--name", a.name,
            "--s1", "--s3", "--s6", "--damage-rate", "0", "--damage-seed", str(a.seed), "--quit-on-se-exit", "--quiet"]

def plan_lines(mode, parts, mutants, world):
    out = ["  world: %s (Survival, the throwaway marker, autosave off; tools/lab/make_survival_copy.py; the world mod with"
           " Data/TransparentMaterials.sbc deployed)" % world,
           "  Subnautica: %s" % ("the stand-in host (fake_sn_host.py)" if mode == "S15se" else "the real game, Freedom host slot"),
           "  SE: the real game on the copy",
           "  env: %s" % json.dumps(env_for(mode, mutants))]
    for p in parts:
        out.append("  part %-9s %s" % (p, (getattr(S15Runner, "p_" + p).__doc__ or "").strip().split("\n")[0][:110]))
    return out

def newest_logs(log_dir=None):
    log_dir = log_dir or os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeepEngineers", "logs")
    se = sorted(glob.glob(os.path.join(log_dir, "se-*.jsonl")), key=os.path.getmtime)
    sn = sorted(glob.glob(os.path.join(log_dir, "sn-*.jsonl")), key=os.path.getmtime)
    return (se[-1] if se else None), (sn[-1] if sn else None)

def s15_analyze(LCH, log, a, out) -> int:
    AN = analyzer(LCH)
    rows, _S = AN.evaluate(out)
    ok = AN.write_results(out, rows, strict=not a.lenient)
    print(AN.C.table(rows))
    log.say("S15 ORACLE TABLE: %s (%s)" % ("PASS" if ok else "FAIL", os.path.join(out, "s15-table.txt")), ev="s15_verdict", ok=ok)
    return 0 if ok else 1

def s15(LCH, log, a) -> int:
    S = LCH.S
    if a.notes:
        print(NOTES)
        return 0
    if a.list_mutant_groups:
        for name, g in S15_MUTANT_GROUPS.items():
            print("  %-9s %-6s mutants %-8s parts %-26s red %s" % (name, g["mode"], ",".join(g["mutants"]), ",".join(g["parts"]), ",".join(g["red"])))
        return 0
    if a.analyze:
        return s15_analyze(LCH, log, a, a.analyze)
    if a.selftest:
        return s15_selftest(LCH, log)
    if a.latest:
        se, sn = newest_logs()
        AN = analyzer(LCH)
        rows, _S = AN.evaluate(None, [se] if se else [], [sn] if sn else [], a.captures)
        ok = AN.C.verdict(rows, not a.lenient)
        print("S15 logs: SE %s | Subnautica %s" % (se or "-", sn or "-"))
        print(AN.C.table(rows))
        print("S15 ORACLE TABLE %s" % ("PASS" if ok else "FAIL"))
        return 0 if ok else 1
    modes = [a.mode] if a.mode else ([S15_MUTANT_GROUPS[a.mutant_group]["mode"]] if a.mutant_group else None)
    if modes is None:
        if not a.dry_run:
            log.say("s15: choose --mode S15se|S15b (or a --mutant-group); --dry-run without a mode prints both")
            return 2
        modes = list(S15_MODES)
    if a.dry_run:
        rc = 0
        for mode in modes:
            parts, mutants, world, probs = resolve(a, mode)
            print("S15 %s plan: parts %s%s%s" % (mode, ",".join(parts), " (quick)" if a.quick else "", " mutants %s" % ",".join(mutants) if mutants else ""))
            for line in plan_lines(mode, parts, mutants, world):
                print(line)
            info, wprobs = L6.world_check(LCH, world, "Survival", getattr(a, "force_unmarked", False))
            for p in wprobs:
                print("  A REAL RUN NEEDS: world %r: %s" % (world, p))
            for p in probs:
                print("  PROBLEM: %s" % p)
                rc = 2
        print("  mutant groups: %s" % ", ".join("%s=%s(%s)" % (n, g["mode"], ",".join(g["mutants"])) for n, g in S15_MUTANT_GROUPS.items()))
        log.say("dry run: nothing started")
        return rc
    mode = modes[0]
    parts, mutants, world, probs = resolve(a, mode)
    if probs:
        for p in probs:
            log.say("REFUSE s15: %s" % p)
        return 2
    info, wprobs = L6.world_check(LCH, world, "Survival", getattr(a, "force_unmarked", False))
    if wprobs:
        for p in wprobs:
            log.say("REFUSE s15 %s: world %r: %s (throwaway-copy rule)" % (mode, world, p))
        return 2
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s15", stamp)
    sn_env, se_env = env_for(mode, mutants)
    a.cont = True
    if mode == "S15se" and not a.sn_cmd:
        a.sn_cmd = json.dumps(host_cmd(LCH, a))
    sn_cmd, se_cmd = LCH.session_commands(a)
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "s15-session.jsonl"))
    sess.event("start", kind="s15", stamp=stamp, mode=mode, parts=parts, mutants=mutants, name=a.name, argv=sys.argv[1:],
               quick=a.quick, standin={"sn": bool(a.sn_cmd), "se": False}, snCmd=sn_cmd, seCmd=se_cmd,
               qpcFrequency=S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env, world=world)
    log.say("S15 session %s (%s): parts %s mutants %s; out %s" % (stamp, mode, ",".join(parts), ",".join(mutants) or "none", out), ev="s15_plan")
    S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            runner = S15Runner(LCH, log, a, sess, sn, se, out, stamp, t_start, mode)
            ok, why = runner.attach()
            if not ok:
                log.say("S15: harness not usable: %s" % why, ev="s15_harness_refused")
                rc = 1
            else:
                if mode == "S15b":
                    runner.park()
                runner.mutants_on(mutants)
                for p in parts:
                    if not runner.alive():
                        log.say("S15: a game exited before part %s" % p, ev="s15_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:  # noqa: BLE001 - a part error is logged, the session goes on
                        log.say("S15 part %s failed: %r" % (p, e), ev="s15_part_error", part=p)
                        sess.event("part_error", name=p, error=repr(e))
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if not a.keep_running:
            LCH.close_session(log, a, sn, se, False, bool(a.sn_cmd))
        S.fine_timer(False)
        sess.event("end", rc=rc, stats=runner.stats if runner else None,
                   unsupported=sorted("%s.%s" % k for k in runner.unsupported) if runner else None)
        sess.close()
        LCH.collect(log, out, t_start, sn, bool(a.sn_cmd), False, stamp)
    res = s15_analyze(LCH, log, a, out)
    return rc if rc is not None else res

def run(LCH, log, a) -> int:
    return s15(LCH, log, a)

# ----------------------------------------------------------------------------------------------------------------------
# self-test (no game, no section, no process)
# ----------------------------------------------------------------------------------------------------------------------

def s15_selftest(LCH, log) -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S15 launcher self-test (launcher.py s15 --selftest)")

    class A:
        mutants = ""
        parts = ""
        world = None
        mutant_group = None

    for mode in S15_MODES:
        a = A()
        parts, mutants, world, probs = resolve(a, mode)
        check(parts == list(S15_PARTS[mode]) and not probs and world == SURVIVAL_WORLD, "%s default parts %s on %s" % (mode, ",".join(parts), world))
        check(all(hasattr(S15Runner, "p_" + p) for p in S15_PARTS[mode]), "%s: every part has a runner method" % mode)
    AN = analyzer(LCH)
    need = {"S15.VOICE": "voice", "S15.ABOARD": "voice", "S15.S2": "voice", "S15.WELCOME": "voice", "S15.POWER": "voice", "S15.MUSIC": "voice",
            "S15.LINE": "voice", "S15.STORY": "voice", "S15.SKYDIR": "sky", "S15.SKYSIZE": "sky", "S15.MOONS": "sky", "S15.AIR": "air",
            "S15.ORBIT": "orbit", "S15.NIGHT": "sky", "S15.SPLASH": "voice", "S15.SONAR": "sonar", "S15.SEABASE": "voice", "S15.FISH": "life"}
    for name, g in S15_MUTANT_GROUPS.items():
        a = A()
        a.mutant_group = name
        parts, mutants, world, probs = resolve(a, g["mode"])
        sn_env, se_env = env_for(g["mode"], mutants)
        se_side = [m for m in mutants if m in SE_MUTANTS]
        sn_side = [m for m in mutants if m in SN_MUTANTS]
        ok = not probs and parts == list(g["parts"]) and (not se_side or se_env.get("DEEPENGINEERS_MUTANTS") == ",".join(se_side))
        ok = ok and (not sn_side or (g["mode"] == "S15b" and sn_env.get("DEEPENGINEERS_MUTANT") == ",".join(sn_side))) and se_env["DEEPENGINEERS_S15"] == "on"
        ok = ok and tuple(g["red"]) == tuple(AN.MUTANT_ROWS[g["mutants"][0]]) and all(need[r] in parts for r in g["red"])
        check(ok, "group %s: %s %s, red %s (the analyzer's MUTANT_ROWS), env %s / %s" % (name, g["mode"], ",".join(mutants), ",".join(g["red"]),
                                                                                           sn_env.get("DEEPENGINEERS_MUTANT"), se_env.get("DEEPENGINEERS_MUTANTS")))
    grouped = {m for g in S15_MUTANT_GROUPS.values() for m in g["mutants"]}
    check(set(AN.MUTANT_ROWS) == set(ALL_MUTANTS) == grouped, "every analyzer mutant has a group and a side")
    check(set(AN.SE_MUTANTS) == set(SE_MUTANTS) and set(AN.SN_MUTANTS) == set(SN_MUTANTS), "the sides agree with the analyzer")
    a = A()
    a.mutants = "S15.M5"
    _p, _m, _w, probs = resolve(a, "S15se")
    check(bool(probs), "a Subnautica mutant refused in S15se (the stand-in host has no S15)")
    check(ORBIT_KM == AN.ORBIT_KM and AIR_KM == AN.AIR_KM, "the orbit and air captures are the analyzer's altitudes (600, 200, 100; 30, 5 km)")
    check(abs(MOON_MIN_SIN - AN.MOON_MIN_SIN) < 1e-6, "the moon captures' horizon is the analyzer's")
    check(all(m in S15_MUTANT_GROUPS["water"]["parts"] for m in ("band", "air", "orbit")) and S15_MUTANT_GROUPS["water"]["mode"] == "S15b",
          "the water group (S15.M4) captures the band, the air and the orbit (review MEDIUM 6)")
    check(abs(ROOM_Y) - 3.70 > 0 and SUB_Y < 0 and ROOM_Y < 0, "the test grids under the sea")
    check(grid_of({"id": 20, "ok": True, "grid": "129626897907186171"}) == "129626897907186171" and grid_of({"id": 1, "ok": True}) is None,
          "the spawn reply's grid id is the string 'grid'")
    # run 20261024-003024: S6Runner's own attribute self.grid shadowed a method of this name ("'NoneType' object is not callable")
    import inspect
    import re
    attrs = set(re.findall(r"self\.([A-Za-z_][A-Za-z0-9_]*) = ", inspect.getsource(L6.S6Runner)))
    mine = {n for n, v in vars(S15Runner).items() if callable(v)}
    check(not (attrs & mine), "no S15Runner method is shadowed by an attribute S6Runner sets (%s)" % (sorted(attrs & mine) or "none"))
    check(AN.selftest() == 0, "tests/s15/analyze_s15.py --selftest")
    check(all(k in NOTES for k in ("Welcome aboard", "Seamoth", "Engine powering up", "Engine shutting down", "Y (the power switch)", "Never on thrust",
                                   "/de sn s15 music", "story is off", "Aurora", "/de sn s15 sky off", "Sonar", "dim to 40 %", "base hum", "fish scatter")),
          "the owner notes name the four lines, the audio across the surface, the story, the sky switch and the kept picks")
    check(all("Silent running" not in NOTES.split("Gone for good")[0] for _ in (0,)) and "Gone for good" in NOTES,
          "the notes list the removed lines as gone")
    check(POWER_COOLDOWN_S * 1000 == AN.T.VOICE_BY_ID[AN.T.POWER_UP][3] == AN.T.VOICE_BY_ID[AN.T.POWER_DOWN][3] and
          CROSSING_S * 1000 >= AN.T.MX["CrossfadeMs"] + 1000, "the voice part waits out the power cooldown; the crossing outlasts the music's fade")
    check(SEA_MATERIAL == AN.T.WATER_FAR["seaMaterial"] and PLAIN_MATERIAL == AN.T.WATER_FAR["plain"] and
          water_token({"state": "texture=DE_4546B_Water transparent=off material=DE_4546B_Sea colorIntensity=8"}, "material") == "DE_4546B_Sea" and
          water_token({"ok": False}, "material") is None and water_token(None, "material") is None,
          "the sea materials and the engine-down wait are the pin's; de.water's state token read")
    ok = all(results)
    print("S15 LAUNCHER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def add_parsers(sub, LCH):
    p = sub.add_parser("s15", help="S15 gameplay and immersion polish: modes S15se (SE + stand-in host) and S15b (both games)")
    p.add_argument("--mode", choices=S15_MODES, default=None)
    p.add_argument("--parts", default="", help="comma list (default: every part of the mode), or 'all'")
    p.add_argument("--mutants", default="", help="S15.M1..S15.M20 (dev builds; see --list-mutant-groups)")
    p.add_argument("--mutant-group", default=None, choices=sorted(S15_MUTANT_GROUPS))
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--world", default=None, help="the Survival throwaway copy (default %r)" % SURVIVAL_WORLD)
    p.add_argument("--force-unmarked", action="store_true", help="accept a copy without the throwaway marker (owner-approved)")
    p.add_argument("--quick", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--selftest", action="store_true")
    p.add_argument("--notes", action="store_true", help="print the owner-attended free-play notes")
    p.add_argument("--analyze", default=None, metavar="DIR")
    p.add_argument("--latest", action="store_true", help="judge the newest se-/sn-*.jsonl pair under %%LOCALAPPDATA%%\\DeepEngineers\\logs")
    p.add_argument("--captures", default=None, metavar="DIR", help="with --latest: a folder of band-se/band-sn/air-*/orbit-*/moon-*/sky-*.png")
    p.add_argument("--lenient", action="store_true", help="MISSING rows do not fail the table")
    p.add_argument("--out", default=None)
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--seed", type=int, default=15)
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    # the session plumbing's options (as launcher_s6's)
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (S15se default: the stand-in host)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--screen", default="2560x1440")
    p.add_argument("--fov", type=float, default=70.0)
    p.add_argument("--owner", action="store_true", help="accepted for symmetry with s6 (S15 has no scripted owner step: --notes)")
    return p
