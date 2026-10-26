"""`launcher.py s11` (ROADMAP §14 "### S11 Creatures and hazards"; docs/S11-BRIEF.md; CONTRACT §5.4 "S11 conventions").
Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`); the session
plumbing is launcher_s6's (S6Runner: the C11 harness, the row tails, the S3 grid placement, the spawns, the breathing).

 python -I src/DeepEngineers.Launcher/launcher.py s11 --mode S11se|S11b [--parts...] [--mutant-group NAME] [--world COPY]
 [--quick] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s11 --dry-run | --list-mutant-groups | --selftest | --notes
 python -I src/DeepEngineers.Launcher/launcher.py s11 --analyze tests/out/s11/<stamp>

Modes:
 S11se SE alone + the stand-in Subnautica host (tests/standins/fake_sn_host.py --s6 --damage-rate 0). Parts:
 selftest de.s11 status (S11 on, the patches)
 stack de.s11 {stack: 5} then {stack: 1}: five warhead-sized explosions in one tick become ONE row of 75,000
 (S11.A3, SE half: s11.blast groups)
 grabse the size probes (below), then on the small wall an applied start and 3 s of hold records moving
 the attach point up 1 m/s (s11.hold error), then silence (the 1,000 ms timeout lets go) (S11.C1-C3)
 hazardse one applied hazard record of each type of the table, then a burst of 3 x 25 Heat in one second (the
 50 HP/s cap) (S11.D1, S11.D3)
 airse one bubble's oxygen (10 s) and one first-aid heal (20 SN_HP) (S11.E1, S11.E2)
 teardown
 S11b both games. Parts (pass 4 order: the parts that need real damage and healing first, then the guard):
 selftest S6's selftest (Freedom gate, damage self-check) and de.s11 status on both sides
 hazard de.s11 scene hazard: 1 SN_HP of each hazard type on the ghost Player and Acid on the character section
 through the game's own TakeDamage (S11.D1)
 air de.s11 scene air: bubble, air pocket and base oxygen, a first-aid AddHealth(50) (S11.E1, S11.E2)
 grab the GUARD on (SE de.s11 {guard: true}, SE's admin invulnerability: run 3's reapers bit the character to
 death and SE's respawn screen paused the session); the size probes (below); on the small wall the forced
 grab of grabse, emitted by Subnautica (de.s11 scene grab: its own record seqs) (S11.C2, C3); a reaper
 spawned 30 m out, up to 60 s for a natural grab (INFO)
 emp de.s11 scene emp on the room the probes left (its battery): one EMP record, a producer off (S11.D2)
 stack a reaper spawned 60 m out; SE de.s11 {stack: 5, ahead: 60}: one row of the summed yield, Subnautica's
 chain and creature damage (S11.A3)
 danger two reapers spawned 40 m out; de.s11 scene danger: the hand rifle until it stops (the floor at half),
 two hand-held rockets on the other one (the same floor), a large turret volley and a warhead blast
 (S11.A1, S11.A2, S11.A4)
 path a 3 x 3 LargeBlockArmorBlock wall at SN (0, -10, 40); the character 16 m in front of it; 26 harmless fish
 spawned between; de.s11 scene path 60 s with the wall: every 2 s up to 12 creatures are put facing the
 wall within their own scan and their AvoidObstacles.Evaluate runs once (s11.probe), then they are lured
 behind the wall; no creature centre inside a section (S11.B1)
 teardown the guard off, grabs released, S6's teardown
 Size probes (grab, grabse; S11.C1 judges every verdict against the twin's size_fits on the box SE measured, and needs both
 sides of each creature's class; pass 5 classes: reaper 700 m3 / 18 m, sea dragon 2,900 m3 / 31 m): a 3 x 3 SmallBlockArmorBlock
 wall at SN (0, -10, 30) (both fit), at SN (0, -12, 30) a 5 x 5 LargeBlockArmorBlock wall (12.5 x 12.5 x 2.5 m, 391 m3:
 both fit), a 1 x 8 one (20 m: over the reaper's 18 m edge, inside the dragon's), a 1 x 14 one (35 m: over the dragon's
 31 m) and the 5 x 5 x 5 room (12.5 m cube, 1,953 m3: over the reaper's volume, inside the dragon's); each takes a dry-run
 start of a reaper (2540) and of a sea dragon (2553)

Owner-attended free play: see OWNER_NOTES below.
Output tests/out/s11/<stamp>/ (s11-session.jsonl, se-*/sn-*.jsonl or the stand-ins' *-standin.jsonl, the game logs) and
the S11 ORACLE TABLE of tests/s11/analyze_s11.py (s11-table.txt, s11-results.json). This launcher never writes into a game
folder, a save or SE's LastSession; the world copy is the orchestrator's (tools/lab/make_survival_copy.py, SE closed)."""
from __future__ import annotations

import datetime
import json
import os
import subprocess
import sys
import time

import launcher_s6 as L6

S11_MODES = ("S11se", "S11b")
S11_PARTS = {"S11se": ("selftest", "stack", "grabse", "hazardse", "airse", "teardown"),
             "S11b": ("selftest", "hazard", "air", "grab", "emp", "stack", "danger", "path", "volley", "holdhit", "surface", "teardown")}
SN_MUTANTS = ("S11.M1", "S11.M2", "S11.M3", "S11.M4", "S11.M5", "S11.M6", "S11.M7", "S11.M8", "S11.M9", "S11.M10", "S11.M11", "S11.M14", "S11.M15", "S11.M16")
SE_MUTANTS = ("S11.M4", "S11.M5", "S11.M11", "S11.M12", "S11.M13", "S11.M14")   # set on SE (M4, M5, M11, M14 on both sides; M12, M13 SE only)
ALL_MUTANTS = SN_MUTANTS + ("S11.M12", "S11.M13")
S11_MUTANT_GROUPS = {
    "floor": {"mutants": ("S11.M1",), "mode": "S11b", "parts": ("selftest", "danger", "teardown"), "red": ("S11.A1", "S11.A4")},
    "stack": {"mutants": ("S11.M2",), "mode": "S11b", "parts": ("selftest", "stack", "teardown"), "red": ("S11.A3",)},
    "avoid": {"mutants": ("S11.M3",), "mode": "S11b", "parts": ("selftest", "path", "teardown"), "red": ("S11.B1",)},
    "size": {"mutants": ("S11.M4",), "mode": "S11se", "parts": ("selftest", "grabse", "teardown"), "red": ("S11.C1",)},
    "hold": {"mutants": ("S11.M12",), "mode": "S11se", "parts": ("selftest", "grabse", "teardown"), "red": ("S11.C5",)},
    #: the jaw centre, the surface, the missile hit test, the rage rule
    "jaw": {"mutants": ("S11.M13",), "mode": "S11b", "parts": ("selftest", "grab", "teardown"), "red": ("S11.C6",)},   # in-game 122326: the queue runs S11b
    "surface": {"mutants": ("S11.M14",), "mode": "S11b", "parts": ("selftest", "surface", "teardown"), "red": ("S11.C7",)},
    "hits": {"mutants": ("S11.M15",), "mode": "S11b", "parts": ("selftest", "volley", "teardown"), "red": ("S11.R1", "S11.R2", "S11.R3")},
    "rage": {"mutants": ("S11.M16",), "mode": "S11b", "parts": ("selftest", "holdhit", "teardown"), "red": ("S11.R4", "S11.R5")},
    "debris": {"mutants": ("S11.M11",), "mode": "S11se", "parts": ("selftest", "grabse", "teardown"), "red": ("S11.C4",)},
    "release": {"mutants": ("S11.M5",), "mode": "S11se", "parts": ("selftest", "grabse", "teardown"), "red": ("S11.C3",)},
    "hazard": {"mutants": ("S11.M6",), "mode": "S11b", "parts": ("selftest", "hazard", "teardown"), "red": ("S11.D1",)},
    "double": {"mutants": ("S11.M7",), "mode": "S11b", "parts": ("selftest", "hazard", "teardown"), "red": ("S11.D1.once",)},
    "oxygen": {"mutants": ("S11.M8",), "mode": "S11b", "parts": ("selftest", "air", "teardown"), "red": ("S11.E1",)},
    "emp": {"mutants": ("S11.M9",), "mode": "S11b", "parts": ("selftest", "emp", "teardown"), "red": ("S11.D2",)},
    "heal": {"mutants": ("S11.M10",), "mode": "S11b", "parts": ("selftest", "air", "teardown"), "red": ("S11.E2",)},
}
SURVIVAL_WORLD = L6.SURVIVAL_WORLD
GRAB_SMALL = (0.0, -10.0, 30.0)
PROBE_AT = (0.0, -12.0, 30.0)     # pass 4: the larger probes 2 m deeper (a 5 x 5 wall and the room rise 12.5 m: S7 ROOM_AT depth)
WALL_PATH = (0.0, -10.0, 40.0)
PATH_VIEW = (0.0, -10.0, 24.0)    # run 1: the character 16 m in front of the wall, looking at it (yaw 0 = +z)
REAPER, GHOST, DRAGON = 2540, 2562, 2553
# pass 4: probes
# on both sides of each class; the analyzer judges each verdict with the twin on the box SE measured, not a fixed verdict
# pass 6: the small 3 x 3 wall (9 blocks, 1.1 m3: under the 8 m3
# debris box) and a single large block (1 block: under 8 blocks) are debris (S11.C4: SE refuses them, S11Reject.Debris);
# the size class (S11.C1) is judged on the others, the forced grab holds the 5 x 5 wall (25 blocks, 391 m3)
SIZE_PROBES = (("small", "SmallBlockArmorBlock", {"rows": 3, "cols": 3, "orientation": "wall"}, GRAB_SMALL),
               ("single", "LargeBlockArmorBlock", {"rows": 1, "cols": 1, "orientation": "wall"}, PROBE_AT),
               # in-game 20261024-144924: at PROBE_AT the 12.5 m wall's top stood 0.8 m under the surface and the surface rule let the
 # forced grab go at once; the wall the forced grab holds sits deep (FORCED_AT, the burst wall's water)
               ("wall5", "LargeBlockArmorBlock", {"rows": 5, "cols": 5, "orientation": "wall"}, (12.0, -40.0, 80.0)),
               ("row8", "LargeBlockArmorBlock", {"rows": 1, "cols": 8, "orientation": "wall"}, PROBE_AT),
               # pass 5: the x2.5 sea dragon holds up to 2,900 m3 / 31 m: a 1 x 14 large wall (35 m; de.s3 plates up to 16) is over it
               ("row14", "LargeBlockArmorBlock", {"rows": 1, "cols": 14, "orientation": "wall"}, PROBE_AT),
               ("room", "LargeBlockArmorBlock", {"orientation": "room"}, PROBE_AT))
FORCED_KEY = 77
#: the forced attach point 4 m above the wall's placement point (the wall's centre of bounds moves onto it over the
# 1 s blend; with S11.M13's bite point the centre stays >= 4 m off the jaw: S11.C6 red); the rise ends 5 m under the surface
FORCED_RISE = 4.0
VOLLEY_PIN = (0.0, -45.0, 90.0)       # the pinned x3 reaper (SN); the missiles come from 80 m toward the camera
VOLLEY_PLATE = (-20.0, -12.0, 30.0)   # the small plate whose id is the small-grid rockets' origin (class SmallGrid)
HOLDHIT_AT = (12.0, -40.0, 80.0)      # the 5 x 5 wall the reaper holds twice (in-game 144924: at (20, -28, 40) it was destroyed 0.1 s after placing)
BURST_WALL = (12.0, -40.0, 80.0)      # a 3 x 3 wall by the pinned reaper: the burst's held-ship shots
SURFACE_PLATE = (-30.0, 6.0, 50.0)    # a static plate 6 m above the water (the reaper hunts it from below)
RISE_AT = (12.0, -40.0, 80.0)         # the 5 x 5 wall held and pushed up (in-game 144924: a 3 x 3 broke under the hold in 4.5 s)
FAST_S, FAST_SPEED = 1.0, 40.0    # S11b 20261024-000956: the x3 reaper's mouth moved at 40 m/s (hold rows' targetSpeed)
RAMP_S = 0.25                     # S11Scene.RampS: the mouth reaches 40 m/s in 0.25 s (160 m/s2), as a lunge
OWNER_NOTES = """S11 owner-attended free play (launcher.py run; S6 and S11 are on in run now). Look at, and tell me:
 A  danger   Shoot a reaper with the hand rifle: it gets hurt, flees or charges, but never dies (it stops taking
             damage at half health). A large-grid gatling turret or a warhead kills it. /de sn s11 weapon class=handheld
             tier=leviathan floor=0.3 retunes live (dev); /de sn s11 weapon reset=1.
    warheads Place 5 warheads close together, ARM them all (an unarmed warhead never chain-detonates in SE: only armed
             ones do, with up to 0.5 s between them), detonate one under water: Subnautica shows a growing chain that
             ends in one big explosion (radius x cbrt(5)), and a leviathan within ~30 m dies. SE log: "DE.S11 blast tick".
 B  hitbox   Swim and fly next to creatures: they turn away from hulls and from you; none ends up inside a ship.
 C  size     Reapers are 2.5 x (owner 2026-10-24: 3 x clipped the ground), ghost leviathans 2.5 x (juveniles 1.8 x), sea dragons 2.5 x their Subnautica size. A
             reaper bite takes about a quarter of a small-grid functional block (880 of a drill's 4,381), its grab hold
             165 a second; a ghost's a third, a sea dragon's a half. /de sn s11 scale creature=ReaperLeviathan factor=3
             and /de s11 bite creature=ReaperLeviathan mult=11 retune live (dev).
    debris   Break a ship into pieces near a reaper: it goes for you and complete ships, never for a remnant of a few
             blocks (under 8 blocks or 8 m3).
    red      Hit while swimming under Subnautica's picture: Subnautica's own damage effects (heat glow, poison, blood
             splatter) instead of SE's red screen; above water SE's red fades within a second after the last hit.
    grab     A small ship (up to 400 m3 and 15 m long, e.g. a small ship with a cockpit) near a reaper: it grabs the
             ship and, within a second, holds it IN its jaw (the ship's centre where Subnautica holds a Seamoth), blocks
             take damage every second, it lets go after 8-13 s (or when you hit it with something electric). A bigger
             ship is bitten and shoved hard, never held. The grab plays the reaper's own grab sound.
    surface  Fly or float your ship at the surface or above it: no creature follows it out of the water, and a reaper
             holding you lets go before its mouth reaches the surface (2 m).
    rage     Shoot the reaper that holds you (rockets, guns): one or two rockets make it angrier (its bites hurt 1.5 x, it
             holds 4 s longer); 30 % of its health in one hold (three rockets) makes it let go and flee for 10 s.
             /de sn s11 rage creature=ReaperLeviathan release=0.3 anger=1.5 extend=4 flee=10 retunes live (dev).
    rockets  Fire small-grid rockets, large-grid rockets, a gatling and a railgun at a reaper anywhere on its body: every
             hit explodes on the body and hurts it (SN log s11.missile "hit", SE log s11.missile "detonate").
 D  hazards  Swim near lava (Lava Zone, Inactive Lava Zone): heat damage; acid brine (Lost River), geysers, gasopod gas,
             crabsquid EMP on a ship (its power goes out for a moment). No radiation (the SE suit protects).
    aggro    Swim outside a ship near stalkers, sharks or a reaper: the SN log's s11.aggro rows say whether they target
             you (targetCharacter / targetGhost).
 E  air      Brain coral bubbles, air pockets and a powered Subnautica base refill the SE suit's oxygen."""
QUICK = L6.QUICK

class _TailRows:
    """Pass 4: launcher_s6's row tails parse s6.* rows only, so run 3's waits on s11.* rows (the danger scene's end, a grab's
 release) always ran to their timeouts; this parser gives the tails both."""

    def __init__(self, an6, an11):
        self.an6, self.an11 = an6, an11

    def parse_rows(self, recs):
        out = list(self.an6.parse_rows(recs))
        out.extend((n, d, d.get("_t")) for n, d in self.an11.parse_rows(recs))
        return out

class S11Runner(L6.S6Runner):
    """The S11 parts through launcher_s6's plumbing (the base runs in its S6 mode: S11b as S6b, S11se as S6se)."""

    def __init__(self, LCH, log, a, sess, sn, se, out, stamp, t_start, s11mode):
        self.s11mode = s11mode
        saved = a.mode
        a.mode = "S6b" if s11mode == "S11b" else "S6se"
        try:
            super().__init__(LCH, log, a, sess, sn, se, out, stamp, t_start)
        finally:
            a.mode = saved
        self.seq = 4_000_000           # explicit seqs of applied S11se injects (the stand-in host writes none: rate 0)
        for tail in (getattr(self, "se_tail", None), getattr(self, "sn_tail", None)):
            if tail is not None and hasattr(tail, "AN") and hasattr(tail.AN, "parse_rows"):
                tail.AN = _TailRows(tail.AN, analyzer(LCH))

    def s11(self, side, args, label, timeout=10.0):
        return self.call(side, "de.s11", args, timeout=timeout, label=label)

    def mutants_on(self, mutants):
        for m in mutants:
            if self.s11mode == "S11b" and m in SN_MUTANTS:
                self.call("sn", "de.mutant", {"name": m, "on": True}, label="mutant.sn.%s" % m)
            if m in SE_MUTANTS:
                self.call("se", "de.mutant", {"name": m, "on": True}, label="mutant.se.%s" % m)

    def inject(self, rec, apply, label):
        args = {"inject": dict(rec), "apply": bool(apply)}
        if apply:
            self.seq += 1
            args["inject"]["seq"] = self.seq
        return self.s11("se", args, label)

    # ---- parts --------------------------------------------------------------------------------------------------
    def p_selftest(self):
        """S6's selftest in S11b (the Freedom gate, the damage self-check), then de.s11 status on each side"""
        if self.s11mode == "S11b":
            super().p_selftest()
        self.part("s11.selftest", "begin")
        self.s11("se", {"status": True}, "selftest.se.s11")
        if self.s11mode == "S11b":
            self.s11("sn", {"action": "status"}, "selftest.sn.s11")
        self.part("s11.selftest", "end")

    def p_danger(self):
        """S11.A1/A2/A4: two reapers 40 m out; de.s11 scene danger (rifle to the floor, hand rockets, a large turret volley, a warhead)"""
        if not self.combat_allowed("bt1"):
            return
        self.part("danger", "begin")
        self.guard(True, "danger")
        self.spawn("danger", "ReaperLeviathan", 2, 40)   # two: the rifle on one, the hand-held rockets on the other (A4)
        self.wait(self.scale(10.0, 4.0))
        self.s11("sn", {"action": "scene", "part": "danger", "seconds": self.scale(60.0, 15.0)}, "danger.scene")
        mark = self.sn_tail.mark()
        self.wait_rows(self.sn_tail, mark, lambda rows: any(d.get("part") == "danger" and d.get("phase") == "end"
                                                            for d in rows), self.scale(70.0, 20.0), "danger.end")
        self.act("sn", "s6teardown", label="danger.teardown")
        self.part("danger", "end")

    def p_stack(self):
        """S11.A3: SE de.s11 {stack: 5}, {stack: 1}, a hand-held-class one: one row each of the summed yield and its class"""
        self.part("stack", "begin")
        self.guard(True, "stack")
        if self.s11mode == "S11b" and self.combat_allowed("bt1"):
            self.spawn("stack", "ReaperLeviathan", 1, 60)
            self.wait(self.scale(8.0, 3.0))
        self.s11("se", {"stack": 5, "ahead": 60, "radius": 22.4415, "yield": 15000, "spread": 1.5}, "stack.five")
        self.wait(3.0)
        self.s11("se", {"stack": 1, "ahead": 60, "radius": 22.4415, "yield": 15000}, "stack.one")
        self.wait(3.0)
        #  a hand-held rocket's class rides its row (Handheld: the leviathan keeps half its health)
        self.s11("se", {"stack": 1, "ahead": 60, "radius": 30.0, "yield": 20000, "cls": "handheld"}, "stack.hand")
        self.wait(3.0)
        if self.s11mode == "S11b":
            self.act("sn", "s6teardown", label="stack.teardown")
        self.part("stack", "end")

    def p_path(self):
        """S11.B1: a large armour wall, 26 harmless fish, de.s11 scene path 60 s with the wall: creatures probed facing it (their own Evaluate), lured behind it, none inside"""
        if not self.combat_allowed("n3"):
            return
        self.part("path", "begin")
        self.guard(True, "path")
        eid = self.place_grid("path", "LargeBlockArmorBlock", WALL_PATH)
        if eid is not None:
            # run 1: the bonesharks killed the character; harmless fish, spawned between the character
 # and the wall (de.spawn puts them dist m along the camera's forward), so they meet the hull
            self.swim({"teleportChar": list(PATH_VIEW), "yawDeg": 0.0}, "path.view")
            self.wait(2.0)
            self.spawn("path", "Peeper", 12, 12)
            self.spawn("path", "Boomerang", 8, 12)
            self.spawn("path", "Bladderfish", 6, 12)
            secs = self.scale(60.0, 15.0)
            # pass 4: the wall's SN position for the scripted probe (creatures put facing it, their own Evaluate run once)
            self.s11("sn", {"action": "scene", "part": "path", "seconds": secs, "radius": 40, "wall": list(WALL_PATH)}, "path.scene")
            self.wait(secs + 2.0)
        self.act("sn", "s6teardown", label="path.teardown")
        self.park()
        self.part("path", "end")

    def guard(self, on, label):
        """pass 4: SE's admin invulnerability on the harness character (S11b creature parts; off at teardown)"""
        if self.s11mode != "S11b":
            return None
        rep = self.s11("se", {"guard": bool(on)}, "%s.guard.%s" % (label, "on" if on else "off"))
        if not self.ok(rep):
            self.log.say("  S11 guard %s not acknowledged (%s)" % ("on" if on else "off", json.dumps(rep)[:200] if rep else "no reply"), ev="s11_guard_failed")
        return rep

    def character_check(self, part):
        """pass 4: after each S11b part, SE's character (present, alive, health, guard) as an se_character event; a dead one
 means SE sits at its respawn screen (a paused single-player session): the analyzer marks the later rows MISSING"""
        if self.s11mode != "S11b" or not self.alive():
            return
        rep = self.s11("se", {"status": True}, "%s.character" % part)
        c = (((rep or {}).get("consumer") or {}).get("character") or {}) if self.ok(rep) else {}
        guard = ((rep or {}).get("consumer") or {}).get("guard")
        self.sess.event("se_character", part=part, present=c.get("present"), alive=c.get("alive"), health=c.get("health"), guard=guard)
        if c.get("present") is False or c.get("alive") is False:
            self.log.say("  S11 after part %s: the SE character is %s (SE pauses at its respawn screen: later parts are not judged)" %
                         (part, "dead" if c.get("present") else "absent"), ev="s11_character_lost", part=part)

    def place_dynamic(self, part, block, sn, plate=None):
        """run 1: de.s3 place with {dynamic: true} (S3 places static grids; SE never holds a static one), waiting for its skin"""
        mark = self.sn_tail.mark() if self.mode == "S6se" else self.se_tail.mark()
        args = {"place": {"sn": list(sn), "yawDeg": 0.0, "block": block, "plate": dict(plate or L6.PLATE), "label": "s11.%s" % part, "dynamic": True}}
        rep = self.call("se", "de.s3", args, timeout=20.0, label="%s.place.%s" % (part, block))
        eid = L6._int_id((rep or {}).get("entityId")) if self.ok(rep) else None
        if eid is None:
            self.log.say("  S11 %s: de.s3 place %s failed (%s)" % (part, block, json.dumps(rep)[:200] if rep else "no reply"), ev="s11_place_failed")
            return None
        self.grid, self.grid_sn = (eid, block if (plate or {}).get("orientation") != "room" else "room"), tuple(sn)
        tail = self.sn_tail if self.mode == "S6se" else self.se_tail
        self.wait_rows(tail, mark, lambda rows: any(L6._int_id(d.get("entity")) == eid and d.get("sections") for d in rows),
                       self.scale(40.0, 10.0), "%s.skin %d" % (part, eid))
        return eid

    def _size_probe(self, part, label, block, plate, sn):
        """one placement, then a dry-run grab start of a reaper and of a sea dragon (size_probe events with SE's measured box)"""
        eid = self.place_dynamic(part, block, sn, plate)
        if eid is None:
            self.sess.event("size_probe", part=part, label=label, block=block, plate=plate, grid=None, creature=None, reply=None)
            return None
        for name, tech in (("ReaperLeviathan", REAPER), ("SeaDragon", DRAGON)):
            # run 1: the id as a string (a JSON number above 2^53 lost its last digits: "grid not mirrored")
            rep = self.inject({"unit": 21, "type": 1, "kind": 1, "target": str(eid), "amount": 9.5, "tech": tech, "key": 7}, False,
                              "%s.size.%s.%s" % (part, label, name))
            self.sess.event("size_probe", part=part, label=label, block=block, plate=plate, grid=str(eid), creature=name, tech=tech, reply=rep)
        return eid

    def _forced_grab(self, part, eid, at):
        """an applied reaper start on the grid, 3 s of hold records moving the attach point up 1 m/s, then silence (the
 1,000 ms timeout lets go): S11.C2's hold error and S11.C3's release, the same in S11se and S11b (pass 4)"""
        x, y, z = at
        self.inject({"unit": 21, "type": 1, "kind": 1, "target": str(eid), "amount": 3.0 + FAST_S, "tech": REAPER, "key": FORCED_KEY,
                     "w": [x, y + FORCED_RISE, z], "dir": [0, 0, 1], "up": [0, 1, 0]}, True, "%s.forced.start" % part)
        t0 = time.monotonic()
        n = 0
        while time.monotonic() - t0 < 3.0 + FAST_S and self.alive():
            t = time.monotonic() - t0
            dy = FORCED_RISE + min(t, 3.0)
            tau = max(0.0, t - 3.0)                  # the fast segment, ramped (in-game 122326: a step gave 2.18 m over one record gap)
            dx = (0.5 * (FAST_SPEED / RAMP_S) * tau * tau) if tau < RAMP_S else (0.5 * FAST_SPEED * RAMP_S + FAST_SPEED * (tau - RAMP_S))
            self.inject({"unit": 21, "type": 2, "kind": 1, "target": str(eid), "amount": 1.0, "tech": REAPER, "key": FORCED_KEY,
                         "w": [x + dx, y + dy, z], "dir": [0, 0, 1], "up": [0, 1, 0], "snTime": time.monotonic()}, True, "%s.forced.hold.%d" % (part, n))
            n += 1
            time.sleep(0.02 if t < 3.0 else 0.0)    # 20261024-030926: the fast segment as fast as C11 answers (~50 Hz)
        self.sess.event("forced_grab", part=part, grid=str(eid), holds=n, key=FORCED_KEY)
        self.wait(2.5)            # silence: SE's 1,000 ms timeout releases it

    def _grab_parts(self, part, natural):
        small = None
        for i, (label, block, plate, at) in enumerate(SIZE_PROBES):
            eid = self._size_probe(part, label, block, plate, at)
            if label == "wall5" and eid is not None:
                small = eid
                if self.s11mode == "S11b":
                    # pass 4: Subnautica emits the forced grab (its own seqs; an SE inject with a test seq in the live generation
 # would push SE's dedup window past Subnautica's next real records)
                    self.s11("sn", {"action": "scene", "part": "grab", "seconds": 3.0, "key": FORCED_KEY, "wall": list(at),
                                    "fastS": FAST_S, "fastSpeed": FAST_SPEED}, "%s.forced" % part)
                    self.sess.event("forced_grab", part=part, grid=str(eid), via="sn", key=FORCED_KEY)
                    self.wait(3.0 + FAST_S + 2.5 + 1.0)
                else:
                    self._forced_grab(part, eid, at)
                if natural:
                    # a reaper's own grab of the 5 x 5 wall: INFO (it may well choose the character or nothing)
                    mark = self.sn_tail.mark()
                    self.spawn(part, "ReaperLeviathan", 1, 30)
                    self.wait_rows(self.sn_tail, mark, lambda rows: any(d.get("phase") in ("start", "refused") for d in rows if "phase" in d and "holdS" in d),
                                   self.scale(60.0, 15.0), "%s.natural" % part)
                    self.wait(self.scale(3.0, 1.0))
                    self.act("sn", "s6teardown", label="%s.teardown" % part)
        return small

    def p_grab(self):
        """S11.C1-C3: the guard on; the size probes; a forced grab on the small wall (C2, C3); a reaper 30 m out for a natural grab (INFO)"""
        if not self.combat_allowed("bt1e"):
            return
        self.part("grab", "begin")
        self.guard(True, "grab")
        self._grab_parts("grab", natural=True)
        self.part("grab", "end")

    def p_grabse(self):
        """S11.C1-C3 (SE half): the size probes, a forced grab on the small wall with 3 s of moving holds then silence (timeout)"""
        self.part("grabse", "begin")
        self._grab_parts("grabse", natural=False)
        self.part("grabse", "end")

    # ---- ----------------------------------------------------------------------------------------------
    def _pin(self, label):
        """de.s11 scene volley: the nearest leviathan pinned at VOLLEY_PIN, full health; its collider centre (SN) from the reply"""
        rep = self.s11("sn", {"action": "scene", "part": "volley", "seconds": 30, "pin": list(VOLLEY_PIN)}, "volley.pin.%s" % label)
        r = rep or {}
        c = r.get("centre") or (r.get("result") or {}).get("centre")
        return list(c) if isinstance(c, list) and len(c) == 3 else None

    def p_volley(self):
        """S11.R1-R3: a reaper pinned 3 x (full health each time); SE fires 10 small-grid rockets (a small plate as origin), 4 large-grid rockets and 2 rail slugs at its centre from 80 m; then a gatling burst (24 shots, half from inside its jaw)"""
        if not self.combat_allowed("bt1"):
            return
        self.part("volley", "begin")
        self.guard(True, "volley")
        small = self.place_dynamic("volley", "SmallBlockArmorBlock", VOLLEY_PLATE, {"rows": 3, "cols": 3, "orientation": "wall"})
        self.spawn("volley", "ReaperLeviathan", 1, 50)
        self.wait(self.scale(8.0, 3.0))
        for kind, n, origin in (("small", 10, small), ("large", 4, None), ("rail", 2, None)):
            centre = self._pin(kind)
            if centre is None:
                self.log.say("  S11 volley: no pinned leviathan (%s)" % kind, ev="s11_volley_nopin")
                continue
            args = {"sn": centre, "count": n, "kind": kind, "from": 80, "every": 0.5}
            if origin is not None:
                args["origin"] = str(origin)
            self.s11("se", {"volley": args}, "volley.%s" % kind)
            self.wait(n * 0.5 + 4.0)
        #  a wall by the pinned reaper; the burst makes it hold it and fires a third of the shots from it
        self.place_dynamic("volley", "LargeBlockArmorBlock", BURST_WALL, {"rows": 3, "cols": 3, "orientation": "wall"})
        self._pin("burst")
        self.s11("sn", {"action": "scene", "part": "burst", "count": 21, "from": 40}, "volley.burst")   # 21 x 33 SN_HP < the 750 release share
        self.wait(6.0)
        self.act("sn", "s6teardown", label="volley.teardown")
        self.part("volley", "end")

    def p_holdhit(self):
        """S11.R4/R5: a 5 x 5 wall and a reaper 30 m out; de.s11 scene holdhit: three holds hit by SE salvos of one, two (rage) and three rockets (release and flee)"""
        if not self.combat_allowed("bt1e"):
            return
        self.part("holdhit", "begin")
        self.guard(True, "holdhit")
        eid = self.place_dynamic("holdhit", "LargeBlockArmorBlock", HOLDHIT_AT, {"rows": 5, "cols": 5, "orientation": "wall"})
        if eid is not None:
            self.spawn("holdhit", "ReaperLeviathan", 1, 30)
            self.wait(self.scale(6.0, 2.0))
            #  SE's own rockets at the holder (de.s11 volley; S7p's blast path decides rage or release): one rocket in the
 # first hold (500 < 15 % of 5,000: angrier), two at once in the second (1,000: it lets go and flees, measured 10 s)
 # project: real salvos; with the 30 % share one and two rockets anger, three let go
            for n, rockets in ((1, 1), (2, 2), (3, 3)):
                rep = None
                for attempt in range(4):
                    rep = self.s11("sn", {"action": "scene", "part": "holdhit", "step": "hold", "seconds": 80, "holdS": 6.0, "wall": list(HOLDHIT_AT)}, "holdhit.hold%d" % n)
                    if (rep or {}).get("ok"):
                        break
                    if "no grid section" in str((rep or {}).get("error") or "") or "debris" in str((rep or {}).get("why") or ""):
                        # in-game 144924: the wall placed right after the previous part's teardown was gone 0.1 s later: place it again
                        self.place_dynamic("holdhit", "LargeBlockArmorBlock", HOLDHIT_AT, {"rows": 5, "cols": 5, "orientation": "wall"})
                    self.wait(2.0)
                c = (rep or {}).get("centre")
                if not (rep or {}).get("ok") or not isinstance(c, list):
                    self.log.say("  S11 holdhit: hold %d not started (%s)" % (n, json.dumps(rep)[:200] if rep else "no reply"), ev="s11_holdhit_nohold")
                    break
                self.wait(1.5)          # the grid blends onto the jaw
                mark = self.sn_tail.mark()
                self.s11("se", {"volley": {"sn": c, "count": rockets, "kind": "small", "from": 80, "every": 0.05}}, "holdhit.rockets%d" % n)
                self.wait_rows(self.sn_tail, mark, lambda rows, n=n: any(str(d.get("step")) == "holdEnd%d" % n for d in rows), 25.0, "holdhit.end%d" % n)
                self.wait(11.0 if rockets >= 3 else 1.0)   # the flee, 10 s of s11.flee rows
        self.act("sn", "s6teardown", label="holdhit.teardown")
        self.part("holdhit", "end")

    def p_surface(self):
        """S11.C7: follow (a static plate 6 m above the water, the reaper's target for 20 s) then rise (a 3 x 3 wall held and pushed up until the reaper lets go at the surface)"""
        if not self.combat_allowed("bt1e"):
            return
        self.part("surface", "begin")
        self.guard(True, "surface")
        self.place_grid("surface", "LargeBlockArmorBlock", SURFACE_PLATE)
        self.spawn("surface", "ReaperLeviathan", 1, 40)
        self.wait(self.scale(6.0, 2.0))
        self.s11("sn", {"action": "scene", "part": "surface", "mode": "follow", "seconds": self.scale(20.0, 8.0), "plate": list(SURFACE_PLATE)}, "surface.follow")
        self.wait(self.scale(22.0, 9.0))
        if self.place_dynamic("surface", "LargeBlockArmorBlock", RISE_AT, {"rows": 5, "cols": 5, "orientation": "wall"}) is not None:
            mark = self.sn_tail.mark()
            self.s11("sn", {"action": "scene", "part": "surface", "mode": "rise", "seconds": 12, "wall": list(RISE_AT)}, "surface.rise")
            self.wait_rows(self.sn_tail, mark, lambda rows: any(d.get("part") == "surface" and d.get("phase") == "end" for d in rows), 15.0, "surface.end")
        self.act("sn", "s6teardown", label="surface.teardown")
        self.part("surface", "end")

    def p_emp(self):
        """S11.D2: de.s11 scene emp on the room (the grab part leaves it; else it is placed): one record, its battery off"""
        self.part("emp", "begin")
        self.guard(True, "emp")
        if not (self.grid and self.grid[1] == "room"):
            label, block, plate, at = SIZE_PROBES[-1]
            self.place_dynamic("emp", block, at, plate)
        self.s11("sn", {"action": "scene", "part": "emp"}, "emp.scene")
        self.wait(3.0)              # the 2 s power loss ends (s11.emp row, then the restore)
        self.part("emp", "end")

    def p_hazard(self):
        """S11.D1: de.s11 scene hazard (each hazard type through the game's TakeDamage; the EMP is the emp part since pass 4)"""
        self.part("hazard", "begin")
        self.s11("sn", {"action": "scene", "part": "hazard"}, "hazard.scene")
        self.wait(4.0)
        self.part("hazard", "end")

    def p_hazardse(self):
        """S11.D1/D3 (SE half): one applied hazard record per type, then 3 x 25 Heat in one second (the 50 HP/s cap)"""
        self.part("hazardse", "begin")
        for t in (1, 5, 6, 10, 14, 16):   # Radiation 12 left the table
            self.inject({"unit": 17, "type": t, "kind": 2, "target": "character", "amount": 2.0}, True, "hazardse.%d" % t)
        self.wait(1.5)
        for i in range(3):
            self.inject({"unit": 17, "type": 1, "kind": 2, "target": "character", "amount": 25.0}, True, "hazardse.burst.%d" % i)
        self.wait(2.0)
        self.part("hazardse", "end")

    def p_air(self):
        """S11.E1/E2: de.s11 scene air (bubble, air pocket, base oxygen; a first-aid AddHealth(50))"""
        self.part("air", "begin")
        self.s11("sn", {"action": "scene", "part": "air"}, "air.scene")
        self.wait(4.0)
        self.part("air", "end")

    def p_airse(self):
        """S11.E1/E2 (SE half): an applied bubble oxygen record (10 s) and a heal record (20 SN_HP)"""
        self.part("airse", "begin")
        self.inject({"unit": 19, "type": 1, "kind": 2, "target": "character", "amount": 10.0}, True, "airse.bubble")
        self.inject({"unit": 18, "type": 0, "kind": 2, "target": "character", "amount": 20.0}, True, "airse.heal")
        self.wait(2.0)
        self.part("airse", "end")

    def p_teardown(self):
        """the guard off, grabs released, de.s11 teardown and status, then S6's teardown (the wall removed, creatures swept)"""
        self.part("s11.teardown", "begin")
        self.guard(False, "teardown")
        self.s11("se", {"release": True}, "teardown.se.release")
        self.s11("se", {"status": True}, "teardown.se.s11")
        if self.s11mode == "S11b":
            self.s11("sn", {"action": "teardown"}, "teardown.sn.s11")
            self.s11("sn", {"action": "status"}, "teardown.sn.status")
        self.part("s11.teardown", "end")
        super().p_teardown()

    def keep_breathing(self):
        if self.s11mode != "S11b":
            return
        now = time.monotonic()
        if now - self._last_refill < L6.REFILL_S:
            return
        self._last_refill = now
        self.call("se", "de.swim", {"refill": True}, label="o2.refill")

# ----------------------------------------------------------------------------------------------------------------------
# plan, environment, world
# ----------------------------------------------------------------------------------------------------------------------

def analyzer(LCH):
    p = os.path.join(LCH.REPO, "tests", "s11")
    if p not in sys.path:
        sys.path.insert(0, p)
    import analyze_s11 as AN   # noqa: E402
    return AN

def env_for(mode, mutants):
    """(Subnautica env, SE env): S5, S6 and S11 on; the mutants on the side(s) that own them."""
    sn_env = {"DEEPENGINEERS_S6": "on", "DEEPENGINEERS_S11": "on"}
    se_env = {"DEEPENGINEERS_S5": "on", "DEEPENGINEERS_S6": "on", "DEEPENGINEERS_S11": "on"}
    sn_m = [m for m in mutants if m in SN_MUTANTS]
    se_m = [m for m in mutants if m in SE_MUTANTS]
    if sn_m and mode == "S11b":
        sn_env["DEEPENGINEERS_MUTANT"] = ",".join(sn_m)
    if se_m:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(se_m)
    return sn_env, se_env

def resolve(a, mode):
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    parts = None
    probs = []
    if a.mutant_group:
        g = S11_MUTANT_GROUPS[a.mutant_group]
        mutants, parts = list(g["mutants"]), list(g["parts"])
    if a.parts and a.parts.strip().lower() == "all":
        parts = list(S11_PARTS[mode])
    elif a.parts:
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
    if parts is None:
        parts = list(S11_PARTS[mode])
    unknown = [p for p in parts if p not in S11_PARTS[mode]]
    if unknown:
        probs.append("parts %s are not %s parts (known: %s)" % (unknown, mode, ",".join(S11_PARTS[mode])))
    bad = [m for m in mutants if m not in ALL_MUTANTS]
    if bad:
        probs.append("unknown mutants %s (known: %s)" % (bad, ",".join(ALL_MUTANTS)))
    se_only = [m for m in mutants if mode == "S11se" and m not in SE_MUTANTS]
    if se_only:
        probs.append("mutants %s are Subnautica's: they need S11b (S11se runs the stand-in host)" % se_only)
    return parts, mutants, a.world or SURVIVAL_WORLD, probs

def host_cmd(LCH, a):
    return [sys.executable, "-I", os.path.join(LCH.REPO, "tests", "standins", "fake_sn_host.py"), "--name", a.name,
            "--s1", "--s3", "--s6", "--damage-rate", "0", "--damage-seed", str(a.seed), "--quit-on-se-exit", "--quiet"]

def plan_lines(mode, parts, mutants, world):
    out = ["  world: %s (Survival, destructible blocks, the throwaway marker, autosave off; tools/lab/make_survival_copy.py)" % world,
           "  Subnautica: %s" % ("the stand-in host (fake_sn_host.py --s6 --damage-rate 0)" if mode == "S11se" else "the real game, Freedom host slot"),
           "  SE: the real game on the copy",
           "  env: %s" % json.dumps(env_for(mode, mutants))]
    for p in parts:
        out.append("  part %-9s %s" % (p, (getattr(S11Runner, "p_" + p).__doc__ or "").strip().split("\n")[0][:110]))
    return out

def s11_analyze(LCH, log, a, out) -> int:
    AN = analyzer(LCH)
    rows, _S = AN.evaluate(out)
    ok = AN.write_results(out, rows, strict=not a.lenient)
    print(AN.C.table(rows))
    log.say("S11 ORACLE TABLE: %s (%s)" % ("PASS" if ok else "FAIL", os.path.join(out, "s11-table.txt")), ev="s11_verdict", ok=ok)
    return 0 if ok else 1

def s11(LCH, log, a) -> int:
    S = LCH.S
    if a.notes:
        print(OWNER_NOTES)
        return 0
    if a.list_mutant_groups:
        for name, g in S11_MUTANT_GROUPS.items():
            print("  %-8s %-6s mutants %-8s parts %-26s red %s" % (name, g["mode"], ",".join(g["mutants"]), ",".join(g["parts"]),
                                                                ",".join(g["red"])))
        return 0
    if a.analyze:
        return s11_analyze(LCH, log, a, a.analyze)
    if a.selftest:
        return s11_selftest(LCH, log)
    if a.mutant_group and a.mutant_group not in S11_MUTANT_GROUPS:
        log.say("unknown mutant group %s (known: %s)" % (a.mutant_group, ",".join(S11_MUTANT_GROUPS)))
        return 2
    modes = [a.mode] if a.mode else ([S11_MUTANT_GROUPS[a.mutant_group]["mode"]] if a.mutant_group else None)
    if modes is None:
        if not a.dry_run:
            log.say("s11: choose --mode S11se|S11b (or a --mutant-group); --dry-run without a mode prints both")
            return 2
        modes = list(S11_MODES)
    if a.dry_run:
        rc = 0
        for mode in modes:
            parts, mutants, world, probs = resolve(a, mode)
            print("S11 %s plan: parts %s%s%s" % (mode, ",".join(parts), " (quick)" if a.quick else "",
                                                 " mutants %s" % ",".join(mutants) if mutants else ""))
            for line in plan_lines(mode, parts, mutants, world):
                print(line)
            info, wprobs = L6.world_check(LCH, world, "Survival", getattr(a, "force_unmarked", False))
            for p in wprobs:
                print("  A REAL RUN NEEDS: world %r: %s" % (world, p))
            for p in probs:
                print("  PROBLEM: %s" % p)
                rc = 2
        print("  mutant groups: %s" % ", ".join("%s=%s(%s)" % (n, g["mode"], ",".join(g["mutants"])) for n, g in S11_MUTANT_GROUPS.items()))
        log.say("dry run: nothing started")
        return rc
    mode = modes[0]
    parts, mutants, world, probs = resolve(a, mode)
    if probs:
        for p in probs:
            log.say("REFUSE s11: %s" % p)
        return 2
    info, wprobs = L6.world_check(LCH, world, "Survival", getattr(a, "force_unmarked", False))
    if wprobs:
        for p in wprobs:
            log.say("REFUSE s11 %s: world %r: %s (throwaway-copy rule)" % (mode, world, p))
        return 2
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s11", stamp)
    sn_env, se_env = env_for(mode, mutants)
    a.cont = True
    if mode == "S11se" and not a.sn_cmd:
        a.sn_cmd = json.dumps(host_cmd(LCH, a))
    sn_cmd, se_cmd = LCH.session_commands(a)
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "s11-session.jsonl"))
    sess.event("start", kind="s11", stamp=stamp, mode=mode, parts=parts, mutants=mutants, name=a.name, argv=sys.argv[1:],
               quick=a.quick, standin={"sn": bool(a.sn_cmd), "se": False}, snCmd=sn_cmd, seCmd=se_cmd,
               qpcFrequency=S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env, world=world)
    log.say("S11 session %s (%s): parts %s mutants %s; out %s" % (stamp, mode, ",".join(parts), ",".join(mutants) or "none", out),
            ev="s11_plan")
    S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            runner = S11Runner(LCH, log, a, sess, sn, se, out, stamp, t_start, mode)
            ok, why = runner.attach()
            if not ok:
                log.say("S11: harness not usable: %s" % why, ev="s11_harness_refused")
                rc = 1
            else:
                if mode == "S11b":
                    runner.park()
                runner.mutants_on(mutants)
                for p in parts:
                    if not runner.alive():
                        log.say("S11: a game exited before part %s" % p, ev="s11_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                        runner.character_check(p)   # pass 4: a dead SE character pauses SE (its respawn screen)
                    except Exception as e:  # noqa: BLE001 - a part error is logged, the session goes on
                        log.say("S11 part %s failed: %r" % (p, e), ev="s11_part_error", part=p)
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
    res = s11_analyze(LCH, log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------------
# self-test (no game, no section, no process)
# ----------------------------------------------------------------------------------------------------------------------

def s11_selftest(LCH, log) -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S11 launcher self-test (launcher.py s11 --selftest)")

    class A:
        mutants = ""
        parts = ""
        world = None
        mutant_group = None

    for mode in S11_MODES:
        a = A()
        parts, mutants, world, probs = resolve(a, mode)
        check(parts == list(S11_PARTS[mode]) and not probs and world == SURVIVAL_WORLD, "%s default parts %s on %s" % (mode, ",".join(parts), world))
        check(all(hasattr(S11Runner, "p_" + p) for p in S11_PARTS[mode]), "%s: every part has a runner method" % mode)
    for name, g in S11_MUTANT_GROUPS.items():
        a = A()
        a.mutant_group = name
        parts, mutants, world, probs = resolve(a, g["mode"])
        sn_env, se_env = env_for(g["mode"], mutants)
        se_side = [m for m in mutants if m in SE_MUTANTS]
        ok = not probs and parts == list(g["parts"]) and (not se_side or se_env.get("DEEPENGINEERS_MUTANTS") == ",".join(se_side))
        sn_side = [m for m in mutants if m in SN_MUTANTS]      # an SE-only mutant (S11.M13) leaves Subnautica's list empty
        ok = ok and (g["mode"] != "S11b" or sn_env.get("DEEPENGINEERS_MUTANT") == (",".join(sn_side) or None))
        ok = ok and sn_env["DEEPENGINEERS_S6"] == "on" and se_env["DEEPENGINEERS_S11"] == "on"
        check(ok, "group %s: %s %s, env %s / %s" % (name, g["mode"], ",".join(mutants), sn_env.get("DEEPENGINEERS_MUTANT"), se_env.get("DEEPENGINEERS_MUTANTS")))
    a = A()
    a.mutants = "S11.M1"
    _p, _m, _w, probs = resolve(a, "S11se")
    check(bool(probs), "a Subnautica mutant refused in S11se (the stand-in host has no S11)")
    a = A()
    a.parts = "danger"
    _p, _m, _w, probs = resolve(a, "S11se")
    check(bool(probs), "danger is not an S11se part")
    #  restart-renderer restarts Subnautica with the S6/S11 switches the `run` session had (its record), else off
    import tempfile
    import launcher_s8 as L8

    class FakeL:
        LOCAL = tempfile.mkdtemp(prefix="de-s11-playenv-")
    check(L8.play_env(FakeL) == {"DEEPENGINEERS_S6": "off", "DEEPENGINEERS_S11": "off"}, "restart-renderer without a record: S6/S11 off")
    L8.record_play_env(FakeL, {"DEEPENGINEERS_S6": "on", "DEEPENGINEERS_S11": "on"})
    check(L8.play_env(FakeL) == {"DEEPENGINEERS_S6": "on", "DEEPENGINEERS_S11": "on"}, "restart-renderer after a run with them on: on")
    L8.record_play_env(FakeL, {"DEEPENGINEERS_S6": "off", "DEEPENGINEERS_S11": None})
    check(L8.play_env(FakeL) == {"DEEPENGINEERS_S6": "off", "DEEPENGINEERS_S11": "off"}, "a run with them off: off")
    # pass 4: the probes straddle each creature's class by the twin (nominal boxes: blocks x 2.5 m / 0.5 m)
    AN = analyzer(LCH)
    T = AN.T
    nominal = {"small": (1.5, 1.5, 0.5), "single": (2.5, 2.5, 2.5), "wall5": (12.5, 12.5, 2.5), "row8": (20.0, 2.5, 2.5), "row14": (35.0, 2.5, 2.5), "room": (12.5, 12.5, 12.5)}
    blocks = {"small": 9, "single": 1, "wall5": 25, "row8": 8, "row14": 14, "room": 98}
    debris = {lb for lb in nominal if T.is_debris(blocks[lb], nominal[lb][0] * nominal[lb][1] * nominal[lb][2])}
    check(debris == {"small", "single"}, "debris probes: the small wall (by volume) and the single block (by count); the others are complete grids")
    sides = {c: {T.size_fits(T.grab_row(c), *nominal[lb]) for lb, _b, _p, _a in SIZE_PROBES if lb not in debris} for c in ("ReaperLeviathan", "SeaDragon")}
    check(all(v == {True, False} for v in sides.values()) and [p[0] for p in SIZE_PROBES] == ["small", "single", "wall5", "row8", "row14", "room"],
          "size probes straddle both classes (reaper and sea dragon: a fit and a refusal each)")
    check(S11_PARTS["S11b"].index("hazard") < S11_PARTS["S11b"].index("grab") and S11_PARTS["S11b"].index("air") < S11_PARTS["S11b"].index("grab")
          and S11_PARTS["S11b"].index("grab") < S11_PARTS["S11b"].index("emp"), "S11b: hazard and air before the guard; emp after grab (the room)")
    check(AN.selftest() == 0, "tests/s11/analyze_s11.py --selftest")
    check("ARM them all" in OWNER_NOTES and "400 m3" in OWNER_NOTES and "radiation" not in OWNER_NOTES.split("No radiation")[0].lower(),
          "owner notes name the armed-warhead rule, the size class and no radiation")
    ok = all(results)
    print("S11 LAUNCHER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def add_parsers(sub, LCH):
    p = sub.add_parser("s11", help="S11 creatures and hazards: modes S11se (SE + stand-in host) and S11b (both games)")
    p.add_argument("--mode", choices=S11_MODES, default=None)
    p.add_argument("--parts", default="", help="comma list (default: every part of the mode), or 'all'")
    p.add_argument("--mutants", default="", help="S11.M1..S11.M10 (dev builds)")
    p.add_argument("--mutant-group", default=None, choices=sorted(S11_MUTANT_GROUPS))
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--world", default=None, help="the Survival throwaway copy (default %r)" % SURVIVAL_WORLD)
    p.add_argument("--force-unmarked", action="store_true", help="accept a copy without the throwaway marker (owner-approved)")
    p.add_argument("--quick", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--selftest", action="store_true")
    p.add_argument("--notes", action="store_true", help="print the owner-attended free-play notes")
    p.add_argument("--analyze", default=None, metavar="DIR")
    p.add_argument("--lenient", action="store_true", help="MISSING rows do not fail the table")
    p.add_argument("--out", default=None)
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--seed", type=int, default=11)
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    # the session plumbing's options (as launcher_s6's)
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (S11se default: the stand-in host)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--screen", default="2560x1440")
    p.add_argument("--fov", type=float, default=70.0)
    p.add_argument("--owner", action="store_true", help="accepted for symmetry with s6 (S11 has no scripted owner step: --notes)")
    p.add_argument("--rotor-at", default=None, help=argparse_suppress())

def argparse_suppress():
    import argparse
    return argparse.SUPPRESS

def run(LCH, log, a) -> int:
    return s11(LCH, log, a)
