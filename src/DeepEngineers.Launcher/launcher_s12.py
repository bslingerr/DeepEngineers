"""`launcher.py s12` (ROADMAP §14 "### S12 Interiors v2"; docs/S12-BRIEF.md; CONTRACT §5.4 "S12 conventions").
Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`); the session
plumbing is launcher_s6's (S6Runner: the C11 harness, the row tails), as launcher_s16's.

 python -I src/DeepEngineers.Launcher/launcher.py s12 --mode S12se|S12b [--parts...] [--mutant-group NAME] [--world COPY]
 [--house-at X,Y,Z] [--ref-at X,Y,Z[,YAW]] [--quick] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s12 --dry-run | --list-mutant-groups | --selftest | --notes
 python -I src/DeepEngineers.Launcher/launcher.py s12 --analyze tests/out/s12/<stamp>

Modes:
 S12se SE alone + the stand-in Subnautica host (tests/standins/fake_sn_host.py): the SE rows only. Parts:
 selftest de.s12 status on both sides (the pin rows)
 house the test house: de.s3's room fixture (a light-armour 5 x 5 x 5 shell, a round window in the north wall, an
 Air Vent against the west wall, a battery, an interior door, an interior light, three spotlights) at
 --house-at (default SN (60, -12, 60): block (0, 0, 0)), static; the east wall's lower middle block becomes a
 flat window (de.s12 swap: Window1x1Flat, its glass on the outer face, so SE counts its cell as room, air
 up to the glass); the gas system builds its room
 air the camera at six outside points (2 m south, 0.3 m from the north window wall, the vent's wall, the east
 wall, the roof, 0.3 m outside the east window's glass: S12.AIR.OUT) and two inside points (S12.AIR.IN), each
 with the S7 patch on and off captured after its window (S12b); two crossings of the east wall's inner face in
 1 cm steps with +-4 cm jitter (S12.AIR.FLIP); the interior door toggled with the camera inside, the camera
 moved out at once (S12.AIR.HOLD); the character 0.4 m outside the east wall, then inside (S12.AIR.CHAR); the
 camera inside while the character swims outside, as third person (S12.AIR.THIRD); the character standing
 pressed against the east window from inside (S12.AIR.WINDOW.IN: in the room, no swim), then floating pressed
 against it from outside (S12.AIR.WINDOW.OUT)
 flood the diver standing inside, the camera under the ceiling looking east; one south-wall block removed (de.s12
 breach): the room is open, so it is water at once for the camera and the diver, SE's environment oxygen
 none (S12.AIR.OPEN), while the plane fills at the twin's Torricelli time (S12.FLOOD.FILL, ONCE) inside the
 room's water cells only (S12.FLOOD.CLIP); the block put back (de.s12 repair): the room rebuilt at the
 planet's oxygen drains at DrainMs (S12.FLOOD.DRAIN) with waterline captures (S12.FLOOD.LINE); at half
 height the camera goes under the plane and back (S12.AIR.WATER) and the half-full capture is compared with
 the dry one after (S12.FLOOD.PLANE)
 wade the diver walking back and forth in the sealed house (a de.swim script), the camera inside: the house's
 Air Vent driven to depressurize (de.s12 vent: the room's oxygen falls, its water rises over the diver),
 then to pressurize (it drains): the room's plane is the diver's surface (S2b's swim below it, SE's walk
 above it, never stuck: S12.FLOOD.MOVE); the room dry again, the diver walks within 2 s (S12.FLOOD.DRYWALK)
 vent the vent rig 25 m east of the house (de.s12 rig: an Air Vent in a wall with a half-full Oxygen Tank behind it,
 a battery): SE's own vent set to depressurize, then to pressurize (de.s12 ventmode): SE's vent effect,
 one Subnautica burst per start and the bubbles at the vent's mouth in a capture (S12.VENT)
 airlock on the vent rig: an inner wall with a sliding door splits its room in two (the west room with the vent, a
 chamber), a sliding door in the chamber's outer (east) wall; the inner door opened, the outer opened (the
 whole room breached), the inner door cycled twice (SE flips the west room's airtightness), closed, the outer
 closed, opened again and closed: every room fills once and drains once (S12.FLOOD.DEBOUNCE) and no cell is
 under two planes (S12.FLOOD.ONEPLANE): the project's airlock that filled and flushed
 teardown the house and the rig removed, the switches back on
 S12b both games (the same parts with Subnautica's rows and captures), plus:
 look inside at noon and at night (Subnautica's clock: de.s15 time), the preset on and off captured
 (S12.LOOK.PRESET); the water through the window from inside on and off (S12.LOOK.WINDOW); outside the house
 on and off (S12.LOOK.EXT)
 lookref a capture inside a Subnautica base of the real map at --ref-at (S12.LOOK.REF, information)

Owner-attended free play (`--notes` prints it). Output tests/out/s12/<stamp>/ (s12-session.jsonl, se-*/sn-*.jsonl, the game
logs, captures/) and the S12 ORACLE TABLE of tests/s12/analyze_s12.py (s12-table.txt, s12-results.json). This launcher never
writes into a game folder, a save or SE's LastSession; the world copy is the orchestrator's (tools/lab/make_survival_copy.py,
SE closed)."""
from __future__ import annotations

import datetime
import json
import math
import os
import sys
import time

import launcher_s6 as L6

S12_MODES = ("S12se", "S12b")
SE_PARTS = ("selftest", "house", "air", "flood", "wade", "vent", "airlock", "teardown")
ALL_PARTS = ("selftest", "house", "air", "flood", "wade", "vent", "airlock", "look", "lookref", "teardown")
S12_PARTS = {"S12se": SE_PARTS, "S12b": ("selftest", "house", "air", "flood", "wade", "vent", "airlock", "look", "teardown")}
OPTIONAL_PARTS = ("lookref",)
SE_MUTANTS = ("S12.M1", "S12.M2", "S12.M3", "S12.M4", "S12.M5", "S12.M8", "S12.M9", "S12.M10", "S12.M11", "S12.M12")
SN_MUTANTS = ("S12.M6", "S12.M7")
ALL_MUTANTS = SE_MUTANTS + SN_MUTANTS
S12_MUTANT_GROUPS = {
    "camerabox": {"mutants": ("S12.M1",), "mode": "S12se", "parts": ("selftest", "house", "air", "teardown"),
                  "red": ("S12.AIR.FLIP", "S12.AIR.HOLD", "S12.AIR.THIRD")},
    "fill": {"mutants": ("S12.M2",), "mode": "S12se", "parts": ("selftest", "house", "flood", "teardown"), "red": ("S12.FLOOD.FILL",)},
    "drain": {"mutants": ("S12.M3",), "mode": "S12se", "parts": ("selftest", "house", "flood", "teardown"), "red": ("S12.FLOOD.DRAIN",)},
    "twice": {"mutants": ("S12.M4",), "mode": "S12se", "parts": ("selftest", "house", "flood", "teardown"), "red": ("S12.FLOOD.ONCE",)},
    "novent": {"mutants": ("S12.M5",), "mode": "S12se", "parts": ("selftest", "house", "vent", "teardown"), "red": ("S12.VENT",)},
    "lookoff": {"mutants": ("S12.M6",), "mode": "S12b", "parts": ("selftest", "house", "look", "teardown"), "red": ("S12.LOOK.PRESET",)},
    "noplane": {"mutants": ("S12.M7",), "mode": "S12b", "parts": ("selftest", "house", "flood", "teardown"), "red": ("S12.FLOOD.PLANE", "S12.FLOOD.LINE")},
    "airpocket": {"mutants": ("S12.M8",), "mode": "S12se", "parts": ("selftest", "house", "flood", "teardown"), "red": ("S12.AIR.OPEN",)},
    "midplane": {"mutants": ("S12.M9",), "mode": "S12se", "parts": ("selftest", "house", "air", "teardown"), "red": ("S12.AIR.WINDOW.IN",)},
    "boxplane": {"mutants": ("S12.M10",), "mode": "S12se", "parts": ("selftest", "house", "flood", "teardown"), "red": ("S12.FLOOD.CLIP",)},
    "nosurface": {"mutants": ("S12.M11",), "mode": "S12se", "parts": ("selftest", "house", "wade", "teardown"), "red": ("S12.FLOOD.MOVE",)},
    "doorflip": {"mutants": ("S12.M12",), "mode": "S12se", "parts": ("selftest", "vent", "airlock", "teardown"), "red": ("S12.FLOOD.DEBOUNCE", "S12.FLOOD.ONEPLANE")},
}
SURVIVAL_WORLD = L6.SURVIVAL_WORLD
HOUSE_AT = (60.0, -12.0, 60.0)          # S7's ROOM_AT (launcher_s7): block (0, 0, 0) of de.s3's room fixture
BLOCK = 2.5
HOUSE_PLATE = {"orientation": "room"}
BREACH_CELL = (2, 2, 0)                 # the south wall's middle block (grid-local cell = (i, j, -k) of the SN offsets)
CROSS_SPAN_M, CROSS_STEP_M, CROSS_JITTER_M = 0.5, 0.01, 0.04
HALF_LEVEL = 0.55
RISE_LINES = (0.5, 0.75)                 # waterline captures while rising at these levels, and while draining at the next
DRAIN_LINES = (0.75,)
WINDOW_CELL = (4, 1, -2)                 # grid-local cell of the east wall's lower middle block (SN offsets 4, 1, 2)
WINDOW_DEF = ("MyObjectBuilder_CubeBlock/Window1x1Flat", "Forward", "Up")   # its full Right mount face (the glass) east: CubeBlocks_Windows.sbc
ROOM_CELLS = 28                          # the 3 x 3 x 3 interior and the window's cell
WADE_RATE, WADE_DOWN_S, WADE_UP_S, WADE_DRAIN_S = 0.06, 17.0, 17.0, 26.0   # de.s12 vent dev drive: of the room per s
WADE_STEP_S = 1.0                        # the diver's script: forward then back, this long each
# the airlock on the vent rig (grid-local cells; its interior is x 1..3): an inner wall at x = 2 with a sliding door, the outer
# door in the east wall; both slide doors face east (their Front/Back are the passage: CubeBlocks_Doors.sbc)
AIRLOCK_WALL = [(2, j, -k) for j in (1, 2, 3) for k in (1, 2, 3) if (j, k) != (1, 2)]
AIRLOCK_INNER, AIRLOCK_OUTER = (2, 1, -2), (4, 1, -2)
AIRLOCK_DOOR = ("MyObjectBuilder_AirtightSlideDoor/LargeBlockSlideDoor", "Right", "Up")
AIRLOCK_WALL_DEF = ("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", "Forward", "Up")
RIG_OFFSET = (25.0, 0.0, 0.0)
RIG_WAIT_S, DEPRESS_S, PRESS_S = 8.0, 10.0, 12.0
OUT_YAW = {"south": 0.0, "window": 180.0, "vent": 90.0, "east": 270.0, "roof": 0.0, "glass": 270.0}   # SN headings (0 north, 90 east) toward the house
STEADY_S = 0.25                          # a test run: one de.teleport does not hold SE's camera once Subnautica's swimmer moves it
QUICK = L6.QUICK
OWNER_NOTES = """S12 owner-attended free play (launcher.py run after both plugins are deployed). Look at, and tell me:
 A  air      Swim around your sealed house under water, close to its walls and its windows, in first and third person: the
             picture stays under water until you are really inside; inside it is dry. Stand pressed against a window from
             inside: you keep walking (no swim). In third person the picture is dry only when your character is inside too.
             Walking in through the door, the picture flips once (no flicker in the doorway). Note: SE's glass is drawn
             very clear under water, so a window can look like an open hole; SE counts it as a wall (air and O2 inside).
 B  look     Inside the house under water: darker and cooler than outside, no sun rays and no caustic ripples on the walls,
             lit by your own lamps with a soft glow from the floor. The water seen through the window does not change.
             Compare noon and night. Dev: /de sn s12 look off | on to compare.
 C  flood    Grind one wall block of the house (or let a creature break it): an open house is water at once (the picture
             under water, SE's oxygen none, you swim), while a water surface fills the room in a few seconds. Weld the block
             back: the room gets the planet's air and the water drains over about 20 seconds; walk around while it drains:
             under the water you swim, above it you walk, and you are never stuck. Depressurize the Air Vent: the water comes
             in as the room's oxygen goes; pressurize it again: the water goes down and you walk on the dry floor at once.
             From outside, the water surface is never drawn outside the room (not in doorways, not in attached blocks).
 D  vent     The effect is on SE's own Air Vent (no new block): any vent, any house; bubbles come out of the vent while it
             works (it needs power and a tank or a generator on its conveyor).
 Dev: /de s12 status | air on|off | third on|off | charrows on|off | probe | breach <grid> x y z | repair <grid> | vent <id> pressurize|depressurize|off [s]."""

def parse_xyz(text, n_min=3, n_max=3, what="--house-at"):
    if not text:
        return None
    v = [float(t) for t in str(text).replace(";", ",").split(",") if t.strip()]
    if not n_min <= len(v) <= n_max:
        raise ValueError("%s needs %s numbers (SN metres%s)" % (what, "x,y,z" if n_max == 3 else "x,y,z[,yaw]", "" if n_max == 3 else ", degrees"))
    return tuple(v)

def house_geometry(at):
    """The test house's camera points from its block (0, 0, 0) at `at` (SN; i east, j up, k north in blocks, B = 2.5 m)."""
    x, y, z = at
    B = BLOCK
    g = {"at": tuple(at)}
    g["in"] = (x + 2 * B, y + 2 * B, z + 2 * B)
    g["corner"] = (x + 1 * B + 0.3, y + 1 * B + 0.5, z + 1 * B + 0.3)
    g["out"] = {
        "south": (x + 2 * B, y + 2 * B, z - B / 2.0 - 2.0),
        "window": (x + 2 * B, y + 2 * B, z + 4.5 * B + 0.3),
        "vent": (x - 0.5 * B - 0.3, y + 1 * B, z + 3 * B),
        "east": (x + 4.5 * B + 0.3, y + 2 * B, z + 2 * B),
        "roof": (x + 2 * B, y + 4.5 * B + 0.3, z + 2 * B),
        # 0.3 m outside the east window's glass (the window's cell is room for SE, its air reaches the glass)
        "glass": (x + 4.5 * B + 0.3, y + 1 * B, z + 2 * B),
    }
    g["face"] = x + 3.5 * B                                 # the room's east face (interior cells 1..3)
    g["crossYZ"] = (y + 2 * B, z + 1 * B)                   # a plain wall block
 # the character pressed against the east window (feet; its eye 1.7 m higher lies in the window's cell, 0.4 m from the glass)
    g["winCharIn"] = (x + 4.5 * B - 0.4, y + 0.5 * B + 0.05, z + 2 * B)
    g["winCharOut"] = (x + 4.5 * B + 0.4, y + 0.5 * B + 0.05, z + 2 * B)
    g["wadeChar"] = (x + 2 * B, y + 0.5 * B + 0.05, z + 2 * B)
    g["holdOut"] = (x + 4.5 * B + 1.0, y + 2 * B, z + 1 * B)
    g["charOut"] = (x + 4.5 * B + 0.4, y + 0.5 * B + 0.05, z + 1 * B)
    g["charIn"] = (x + 2 * B, y + 0.5 * B + 0.35, z + 2 * B - 2.0)
    g["ceiling"] = (x + 1.2 * B, y + 3.5 * B - 0.5, z + 2 * B)   # looking east, 0.5 m under the ceiling
    g["floorCam"] = (x + 2 * B, y + 0.5 * B + 0.6, z + 2 * B)
    g["lightCam"] = (x + 1 * B, y + 2 * B, z + 2 * B)            # S7 n3's: cell (1, 2, 2) looking east at the lit wall
    g["extCam"] = (x + 2 * B, y + 2 * B, z - B / 2.0 - 3.0)      # 3 m south of the house, looking south
    g["fogCam"] = (x + 2 * B, y + 2 * B, z + 3.5 * B - 2.5)      # S7's: 2.5 m from the window wall, looking north
    g["fogB"] = (x + 2 * B, y + 2 * B, z + 3.5 * B + 20.0)       # 20 m beyond the window
 # the far (east) wall's column for the waterline: its inner face at a plain block (z + 1 B), the floor and the ceiling
    g["lineFloor"] = (x + 3.5 * B - 0.02, y + 0.5 * B, z + 1 * B)
    g["lineCeil"] = (x + 3.5 * B - 0.02, y + 3.5 * B, z + 1 * B)
    g["depth"] = -(y + 2 * B)
    return g

def rig_geometry(at):
    """The vent rig's points (its block (0, 0, 0) at `at`): the camera 3 blocks from the vent looking west, the vent's mouth."""
    x, y, z = at
    B = BLOCK
    return {"at": tuple(at), "cam": (x + 3.0 * B, y + 2 * B, z + 2 * B), "mouth": (x + 0.5 * B + 0.05, y + 2 * B, z + 2 * B)}

def cross_path(face, span=CROSS_SPAN_M, step=CROSS_STEP_M, jitter=CROSS_JITTER_M, outward=True):
    """The crossing's x positions: from `span` inside the face to `span` outside (or back), `step` apart, +-`jitter` (sin)."""
    n = int(round(2 * span / step))
    xs = []
    for i in range(n + 1):
        f = -span + i * step
        x = face + (f if outward else -f) + jitter * math.sin(i * 1.7)
        xs.append(round(x, 4))
    return xs

class S12Runner(L6.S6Runner):
    """The S12 parts through launcher_s6's plumbing (the base runs in its S6 mode: S12b as S6b, S12se as S6se)."""

    def __init__(self, LCH, log, a, sess, sn, se, out, stamp, t_start, s12mode):
        self.s12mode = s12mode
        saved = a.mode
        a.mode = "S6b" if s12mode == "S12b" else "S6se"
        try:
            super().__init__(LCH, log, a, sess, sn, se, out, stamp, t_start)
        finally:
            a.mode = saved
        self.geo = house_geometry(getattr(a, "house_at_t", None) or HOUSE_AT)
        self.house = None
        self.rig = None
        self.pose = None
        self.cap_n = 0

    def s12(self, args, label, timeout=10.0):
        return self.call("se", "de.s12", args, timeout=timeout, label=label)

    def sn12(self, args, label):
        if self.s12mode != "S12b":
            return None
        return self.call("sn", "de.s12", args, label=label)

    def mutants_on(self, mutants):
        for m in mutants:
            if m in SN_MUTANTS and self.s12mode == "S12b":
                self.call("sn", "de.mutant", {"name": m, "on": True}, label="mutant.sn.%s" % m)
            if m in SE_MUTANTS:
                self.call("se", "de.mutant", {"name": m, "on": True}, label="mutant.se.%s" % m)

    def cam(self, xyz, yaw, label, settle=1.0):
        self.pose = (tuple(xyz), yaw)
        self.teleport(xyz[0], xyz[1], xyz[2], yaw, label=label)
        self.steady(self.scale(settle, 0.3))

    def repose(self):
        """The camera back at the last cam pose."""
        if self.pose:
            (x, y, z), yaw = self.pose
            self.teleport(x, y, z, yaw)

    def steady(self, seconds):
        """Wait `seconds` with the camera held at its pose (a teleport every STEADY_S)."""
        end = time.monotonic() + seconds
        while self.alive():
            self.repose()
            left = end - time.monotonic()
            if left <= 0:
                break
            self.wait(min(STEADY_S, left))

    def capture(self, name):
        if self.s12mode != "S12b":
            return None
        self.cap_n += 1
        rel = "s1/%s/s12-%s-%02d.png" % (self.stamp, name, self.cap_n)   # collect copies captures/s1/<stamp> into the out folder
        rep = self.call("sn", "de.capture", {"path": rel, "label": name, "kind": 1}, timeout=10.0, label="%s.capture" % name)
        self.sess.event("capture", name=name, side="sn", path=rel, reply=rep)
        self.steady(1.0)
        return rep

    def patch_pair(self, name):
        """S12b: the S7 patch on and off captured at this point (the inside-walkable picture A/B, S7.N1's method), after the
 point's window closed (the patch off lets Subnautica's swimmer move the camera: held by steady)."""
        if self.s12mode != "S12b":
            return
        for on in (True, False):
            self.call("sn", "de.interior", {"patch": "on" if on else "off"}, label="%s.patch" % name)
            self.steady(self.scale(1.5, 0.5))
            self.capture("%s.patch-%s" % (name, "on" if on else "off"))
        self.call("sn", "de.interior", {"patch": "on"}, label="%s.patch.restore" % name)
        self.steady(self.scale(1.0, 0.5))

    # ---- parts --------------------------------------------------------------------------------------------------
    def p_selftest(self):
        """de.s12 status on both sides (the pin rows s12.site)"""
        self.part("s12.selftest", "begin")
        self.part("selftest", "begin")
        self.s12({"status": True}, "selftest.se")
        self.sn12({"status": True}, "selftest.sn")
        self.part("selftest", "end")
        self.part("s12.selftest", "end")

    def pixel(self, pt, label):
        """S12b: Subnautica's screen pixel of an SN point (de.pixel: logged as DE.SnPixel label=...)."""
        if self.s12mode != "S12b":
            return None
        return self.call("sn", "de.pixel", {"pointSn": list(pt), "minSeTick": 0, "label": label}, label=label)

    def ensure_house(self):
        if self.house:
            return self.house
        at = self.geo["at"]
        rep = self.call("se", "de.s3", {"place": {"sn": list(at), "yawDeg": 0.0, "block": "LargeBlockArmorBlock", "plate": dict(HOUSE_PLATE), "label": "s12.house"}},
                        timeout=30.0, label="house.place")
        room = (rep or {}).get("room") if isinstance(rep, dict) else None
        if not self.ok(rep) or not isinstance(room, dict):
            self.log.say("  S12 house: de.s3 room fixture refused (%s)" % (json.dumps(rep)[:200] if rep else "no reply"), ev="s12_house_failed")
            return None
        gid = rep.get("gridId") or rep.get("entityId")
        self.house = {"grid": str(gid), "vent": room.get("vent"), "door": room.get("door"), "light": room.get("light"), "at": list(at)}
        # the project's window: a flat window in the east wall, its glass on the outer face, its cell room for SE
        srep = self.s12({"swap": str(gid), "cell": list(WINDOW_CELL), "block": WINDOW_DEF[0], "forward": WINDOW_DEF[1], "up": WINDOW_DEF[2]}, "house.window")
        self.house["window"] = bool(self.ok(srep) and (srep or {}).get("swapped"))
        if not self.house["window"]:
            self.log.say("  S12 house: the window swap was not done (%s): S12.AIR.WINDOW.* measure a plain wall" %
                         (json.dumps(srep)[:160] if srep else "no reply"), ev="s12_window_failed")
        self.sess.event("house", **self.house)
        self.wait(self.scale(6.0, 3.0))   # the gas system builds the room (the Air Vent makes it pressurize), InteriorRooms snapshots it
        return self.house

    def p_house(self):
        """the test house (de.s3's room fixture: Air Vent against the west wall, a flat window in the east wall) at --house-at"""
        self.part("house", "begin")
        h = self.ensure_house()
        self.cam(self.geo["in"], 0.0, "house.in", 2.0)
        self.s12({"status": True}, "house.status")
        self.s12({"probe": True}, "house.probe")
        self.part("house", "end")
        if not h:
            self.log.say("  S12: no house: the air, flood and look parts will be MISSING", ev="s12_no_house")

    def p_air(self):
        """outside points (no air, the view under water: one 0.3 m outside the window's glass), inside points (air), two
 jittered crossings (one flip), the recompute while leaving (no held air), the character outside and inside, the
 third-person camera inside with the character outside (no air)"""
        if not self.ensure_house():
            return
        g = self.geo
        self.part("air", "begin")
        for name, xyz in sorted(g["out"].items()):
            w = "air.out." + name
            self.cam(xyz, OUT_YAW[name], w + ".cam", 1.5)   # facing the house: a leaking interior would show
            self.part(w, "begin")
            self.steady(self.scale(4.0, 2.0))
            self.part(w, "end")
            self.patch_pair(w)
        for name, xyz in (("centre", g["in"]), ("corner", g["corner"])):
            w = "air.in." + name
            self.cam(xyz, 90.0, w + ".cam", 1.5)
            self.part(w, "begin")
            self.steady(self.scale(4.0, 2.0))
            self.part(w, "end")
            self.patch_pair(w)
        y, z = g["crossYZ"]
        for k, outward in ((1, True), (2, False)):
            xs = cross_path(g["face"], outward=outward)
            w = "air.cross.%d" % k
            self.cam((xs[0], y, z), 90.0, w + ".start", 1.5)
            self.part(w, "begin")
            self.pose = None                             # the path's own teleports move it
            for i, x in enumerate(xs):
                self.teleport(x, y, z, 90.0, label="%s.%d" % (w, i) if i % 25 == 0 else None)
                self.wait(0.03)
            self.wait(self.scale(1.0, 0.5))
            self.part(w, "end")
        door = (self.house or {}).get("door")
        for k in (1, 2, 3):
            self.cam(g["in"], 90.0, "air.hold.%d.in" % k, 1.5)
            if door:
                self.call("se", "de.interior", {"action": "door", "block": str(door), "open": k % 2 == 1}, label="air.hold.%d.door" % k)
            self.pose = (tuple(g["holdOut"]), 90.0)
            self.teleport(g["holdOut"][0], g["holdOut"][1], g["holdOut"][2], 90.0, label="air.hold.%d.out" % k)
            self.part("air.hold.%d" % k, "begin")
            self.steady(self.scale(2.0, 1.0))
            self.part("air.hold.%d" % k, "end")
        if door:
            self.call("se", "de.interior", {"action": "door", "block": str(door), "open": False}, label="air.hold.door.close")
        for name, xyz, yaw in (("out", g["charOut"], 270.0), ("in", g["charIn"], 0.0)):
            self.swim({"teleportChar": list(xyz), "yawDeg": yaw}, "air.char.%s.teleport" % name)
            self.cam(g["extCam"] if name == "out" else g["in"], 0.0, "air.char.%s.cam" % name, 1.0)
            self.part("air.char." + name, "begin")
            self.steady(self.scale(4.0, 2.0))
            self.part("air.char." + name, "end")
        # format air.camera: the camera inside while the character swims outside, as SE's third person
        self.swim({"teleportChar": list(g["charOut"]), "yawDeg": 270.0}, "air.third.teleport")
        self.s12({"third": "on"}, "air.third.on")
        self.cam(g["in"], 90.0, "air.third.cam", 1.0)
        self.part("air.third", "begin")
        self.steady(self.scale(4.0, 2.0))
        self.part("air.third", "end")
        self.s12({"third": "off"}, "air.third.off")
        # the project's window: pressed against it from inside the diver stays in the room (no swim); from outside out
        self.s12({"charrows": "on"}, "air.window.rows")
        for name, xyz, yaw in (("in", g["winCharIn"], 90.0), ("out", g["winCharOut"], 270.0)):
            self.swim({"teleportChar": list(xyz), "yawDeg": yaw}, "air.window.%s.teleport" % name)
            self.cam(g["in"], 90.0, "air.window.%s.cam" % name, 1.5)
            self.part("air.window." + name, "begin")
            self.steady(self.scale(4.0, 2.0))
            self.part("air.window." + name, "end")
        self.s12({"charrows": "off"}, "air.window.rows.off")
        self.park()
        self.part("air", "end")

    def flood_status(self):
        rep = self.s12({"status": True}, None)
        st = (rep or {}).get("status") if isinstance(rep, dict) else None
        fl = (st or {}).get("floods") if isinstance(st, dict) else None
        return [f for f in (fl or []) if isinstance(f, dict) and f.get("grid") == (self.house or {}).get("grid") and f.get("state") != "none"]

    def line_capture(self, name):
        """A waterline capture from the ceiling camera: the far wall's floor and ceiling pixels first (S12.FLOOD.LINE)."""
        self.repose()
        self.pixel(self.geo["lineFloor"], name + ".floor")
        self.pixel(self.geo["lineCeil"], name + ".ceil")
        self.capture(name)

    def p_flood(self):
        """the breach floods the room (Torricelli time) with air above the rising water; the repair drains it at DrainMs
 (the room rebuilt at the planet's oxygen); waterline captures rising and draining; under and over the plane at half
 height; captures dry, half, dry again"""
        h = self.ensure_house()
        if not h:
            return
        g = self.geo
        T = analyzer(self.LCH).T
        fill_s = T.fill_ms(ROOM_CELLS, BLOCK, 1, g["depth"]) / 1000.0
        self.part("flood", "begin")
        # format air.flood: the diver standing inside when the wall opens
        self.swim({"teleportChar": list(g["charIn"]), "yawDeg": 0.0}, "flood.diver")
        self.s12({"charrows": "on"}, "flood.rows")
        self.cam(g["ceiling"], 90.0, "flood.cam", 2.0)
        self.line_capture("flood.dry")
        rep = self.s12({"breach": h["grid"], "cell": list(BREACH_CELL)}, "flood.breach")
        self.sess.event("breach", grid=h["grid"], cell=list(BREACH_CELL), reply=rep)
        self.part("flood.open", "begin")
        t0 = time.monotonic()
        todo = list(RISE_LINES)
        while time.monotonic() - t0 < fill_s * 1.5 + 4.0 and self.alive():
            fl = self.flood_status()
            lv = fl[0].get("level") if fl else None
            if lv is not None and todo and lv >= todo[0]:
                self.line_capture("flood.rise.%d" % (len(RISE_LINES) - len(todo) + 1))
                todo.pop(0)
            if fl and fl[0].get("state") == "full":
                break
            self.steady(0.2)
        self.part("flood.open", "end")
        self.s12({"charrows": "off"}, "flood.rows.off")
        self.steady(1.0)
        self.s12({"status": True}, "flood.full.status")
        rep = self.s12({"repair": h["grid"]}, "flood.repair")
        self.sess.event("repair", grid=h["grid"], reply=rep)
        t0 = time.monotonic()
        half = False
        todo = list(DRAIN_LINES)
        drain_s = T.F["DrainMs"] / 1000.0
        while time.monotonic() - t0 < drain_s + 15.0 and self.alive():
            fl = self.flood_status()
            lv = fl[0].get("level") if fl else None
            draining = bool(fl) and fl[0].get("state") == "draining"
            if draining and todo and lv is not None and lv <= todo[0]:
                self.line_capture("flood.drain.%d" % (len(DRAIN_LINES) - len(todo) + 1))
                todo.pop(0)
            if not half and draining and lv is not None and lv <= HALF_LEVEL:
                half = True
                self.line_capture("flood.half")
                self.cam(g["floorCam"], 90.0, "flood.water.below.cam", 0.5)
                self.part("flood.water.below", "begin")
                self.steady(1.5)
                self.part("flood.water.below", "end")
                self.cam(g["ceiling"], 90.0, "flood.water.above.cam", 0.5)
                self.part("flood.water.above", "begin")
                self.steady(1.5)
                self.part("flood.water.above", "end")
            if half and not fl:
                break
            self.steady(0.3)
        self.steady(1.0)
        self.line_capture("flood.dry2")
        self.part("flood", "end")

    def p_wade(self):
        """the diver walks in the sealed house while its vent (de.s12 vent dev drive) lets the water in and out: the room's
 plane is the diver's surface (format flood.surface); then the dry room: the diver walks within 2 s"""
        h = self.ensure_house()
        if not h or not h.get("vent"):
            return
        g = self.geo
        self.part("wade", "begin")
        self.s12({"charrows": "on"}, "wade.rows")
        self.swim({"teleportChar": list(g["wadeChar"]), "yawDeg": 90.0}, "wade.diver")
        self.cam(g["in"], 90.0, "wade.cam", 1.5)
        total = WADE_DOWN_S + WADE_UP_S + WADE_DRAIN_S + 8.0
        steps = []
        k = 0
        while k * WADE_STEP_S < total:
            steps.append({"seconds": WADE_STEP_S, "forward": 1 if k % 2 == 0 else -1, "lookYawDeg": 90.0, "lookPitchDeg": 0.0, "label": "wade.%d" % k})
            k += 1
        self.swim({"script": steps}, "wade.script")
        self.part("wade.fill", "begin")
        self.s12({"vent": str(h["vent"]), "mode": "depressurize", "seconds": WADE_DOWN_S, "rate": WADE_RATE}, "wade.depressurize")
        self.steady(self.scale(WADE_DOWN_S + 1.0, WADE_DOWN_S))
        self.part("wade.fill", "end")
        self.part("wade.flush", "begin")
        self.s12({"vent": str(h["vent"]), "mode": "pressurize", "seconds": WADE_UP_S, "rate": WADE_RATE}, "wade.pressurize")
        t0 = time.monotonic()
        seen = False
        while time.monotonic() - t0 < WADE_UP_S + WADE_DRAIN_S and self.alive():
            fl = self.flood_status()
            if fl:
                seen = True
            elif seen and time.monotonic() - t0 > 3.0:
                break                                    # the room is dry: the track ended
            self.steady(0.3)
        self.part("wade.flush", "end")
        self.part("wade.dry", "begin")
        self.steady(3.0)
        self.part("wade.dry", "end")
        self.swim({"script": "stop"}, "wade.script.stop")
        self.s12({"charrows": "off"}, "wade.rows.off")
        self.park()
        self.part("wade", "end")

    def p_vent(self):
        """the vent rig: SE's own Air Vent depressurizes the rig's room into its tank, then pressurizes it back; SE's vent
 effect, one Subnautica burst per start, the bubbles at the vent's mouth in a capture"""
        g = self.geo
        at = (g["at"][0] + RIG_OFFSET[0], g["at"][1] + RIG_OFFSET[1], g["at"][2] + RIG_OFFSET[2])
        rg = rig_geometry(at)
        self.part("vent", "begin")
        rep = self.s12({"rig": list(at)}, "vent.rig", timeout=20.0)
        if not self.ok(rep) or not rep.get("vent"):
            self.log.say("  S12 vent: the rig was refused (%s)" % (json.dumps(rep)[:200] if rep else "no reply"), ev="s12_rig_failed")
            self.part("vent", "end")
            return
        self.rig = {"grid": str(rep.get("grid")), "vent": str(rep.get("vent")), "tank": rep.get("tank"), "tankFill": rep.get("tankFill")}
        self.sess.event("rig", **self.rig)
        self.cam(rg["cam"], 270.0, "vent.cam", 2.0)
        self.steady(self.scale(RIG_WAIT_S, 4.0))    # the gas system builds the rig's room (at the planet's oxygen)
        self.pixel(rg["mouth"], "vent.mouth")
        self.capture("vent.idle")
        self.part("vent.depress", "begin")
        self.s12({"ventmode": self.rig["vent"], "depressurize": True}, "vent.depress")
        self.steady(3.0)
        self.pixel(rg["mouth"], "vent.mouth.on")
        self.capture("vent.on")
        self.steady(self.scale(DEPRESS_S, 5.0))
        self.part("vent.depress", "end")
        self.part("vent.press", "begin")
        self.s12({"ventmode": self.rig["vent"], "depressurize": False}, "vent.press")
        self.steady(self.scale(PRESS_S, 6.0))
        self.part("vent.press", "end")
        self.s12({"status": True}, "vent.status")
        self.part("vent", "end")

    def p_look(self):
        """inside at noon and at night with the preset on and off; the water through the window from inside; outside on and off"""
        if not self.ensure_house():
            return
        g = self.geo
        self.part("look", "begin")
        for when, scalar in (("noon", 0.5), ("night", 0.0)):
            self.call("sn", "de.s15", {"time": scalar}, label="look.%s.time" % when)
            self.cam(g["lightCam"], 90.0, "look.%s.cam" % when, 2.0)
            for on in (True, False):
                self.sn12({"argv": ["look", "on" if on else "off"]}, "look.%s.%s" % (when, "on" if on else "off"))   # the project's /de sn s12 look off
                self.steady(self.scale(2.5, 1.5))    # the interior sky follows within 1 s (InteriorView's check)
                self.capture("look.%s.%s" % (when, "on" if on else "off"))
            self.sn12({"look": "on"}, "look.%s.restore" % when)
        self.call("sn", "de.s15", {"time": 0.5}, label="look.time.noon")
        # MEDIUM 2: the exterior seen through the window from inside, the preset on and off
        self.cam(g["fogCam"], 0.0, "look.window.cam", 2.0)
        self.pixel(g["fogB"], "look.window.b")
        for on in (True, False):
            self.sn12({"argv": ["look", "on" if on else "off"]}, "look.window.%s" % ("on" if on else "off"))
            self.steady(self.scale(2.0, 1.0))
            self.capture("look.window.%s" % ("on" if on else "off"))
        self.cam(g["extCam"], 180.0, "look.ext.cam", 2.0)
        for on in (True, False):
            self.sn12({"argv": ["look", "on" if on else "off"]}, "look.ext.%s" % ("on" if on else "off"))
            self.steady(self.scale(2.0, 1.0))
            self.capture("look.ext.%s" % ("on" if on else "off"))
        self.sn12({"look": "on"}, "look.restore")
        self.part("look", "end")

    def p_lookref(self):
        """a capture inside a Subnautica base of the real map (--ref-at; information)"""
        ref = getattr(self.a, "ref_at_t", None)
        self.part("lookref", "begin")
        if not ref:
            self.log.say("  S12 lookref: no --ref-at (a point inside a Subnautica base): S12.LOOK.REF stays INFO", ev="s12_no_ref")
        else:
            self.call("sn", "de.s15", {"time": 0.5}, label="lookref.time")
            self.cam(ref[:3], ref[3] if len(ref) > 3 else 0.0, "lookref.cam", 3.0)
            self.capture("lookref.base")
        self.part("lookref", "end")

    def door(self, block, open_, label):
        rep = self.call("se", "de.interior", {"action": "door", "block": str(block), "open": bool(open_)}, label=label)
        self.sess.event("door", block=str(block), open=bool(open_), label=label, reply=rep)
        return rep

    def p_airlock(self):
        """the project's airlock on the vent rig: one fill and one drain per room through the door cycles, one plane per cell"""
        if not self.rig:
            self.log.say("  S12 airlock: no vent rig (part vent first): S12.FLOOD.DEBOUNCE / ONEPLANE MISSING", ev="s12_no_rig")
            return
        gid = self.rig["grid"]
        self.part("airlock", "begin")
        ok = True
        for c in AIRLOCK_WALL:
            r = self.s12({"swap": gid, "cell": list(c), "block": AIRLOCK_WALL_DEF[0], "forward": AIRLOCK_WALL_DEF[1], "up": AIRLOCK_WALL_DEF[2]}, "airlock.wall")
            ok = ok and bool(self.ok(r) and (r or {}).get("swapped"))
        doors = {}
        for name, c in (("inner", AIRLOCK_INNER), ("outer", AIRLOCK_OUTER)):
            r = self.s12({"swap": gid, "cell": list(c), "block": AIRLOCK_DOOR[0], "forward": AIRLOCK_DOOR[1], "up": AIRLOCK_DOOR[2]}, "airlock." + name)
            doors[name] = (r or {}).get("blockId")
            ok = ok and bool(doors[name])
        self.sess.event("airlock", grid=gid, inner=doors.get("inner"), outer=doors.get("outer"), built=ok)
        if not ok:
            self.log.say("  S12 airlock: the swaps were refused (%s)" % json.dumps(doors), ev="s12_airlock_failed")
            self.part("airlock", "end")
            return
        inner, outer = doors["inner"], doors["outer"]
        self.door(inner, False, "airlock.inner.close0")
        self.door(outer, False, "airlock.outer.close0")
        self.steady(self.scale(8.0, 4.0))                  # two airtight rooms (the west one and the chamber)
        self.door(inner, True, "airlock.inner.open")       # one room
        self.steady(4.0)
        self.part("airlock.cycle", "begin")
        self.door(outer, True, "airlock.outer.open")       # the whole room breached
        self.steady(3.0)
        for k in range(2):                                 # SE flips the west room's airtightness with the inner door
            self.door(inner, False, "airlock.inner.cycle.%d.close" % k)
            self.steady(1.0)
            self.door(inner, True, "airlock.inner.cycle.%d.open" % k)
            self.steady(1.0)
        self.door(inner, False, "airlock.inner.close")     # the west room drains once (after the debounce), the chamber stays open
        self.steady(6.0)
        self.part("airlock.cycle", "end")
        self.door(outer, False, "airlock.outer.close")     # the chamber airtight again
        self.steady(4.0)
        self.part("airlock.again", "begin")
        self.door(outer, True, "airlock.outer.reopen")     # the chamber floods while the west room still drains: one plane per cell
        self.steady(6.0)
        self.door(outer, False, "airlock.outer.reclose")
        self.steady(self.scale(26.0, 24.0))
        self.part("airlock.again", "end")
        self.part("airlock", "end")

    def p_teardown(self):
        """the house and the rig removed, the switches and the look back on"""
        self.part("teardown", "begin")
        if getattr(self, "rig", None):
            self.s12({"remove": self.rig["grid"]}, "teardown.rig")
        if self.house:
            self.call("se", "de.s3", {"remove": True}, label="teardown.house")
        for sw in ("air", "floods", "vents"):
            self.s12({sw: "on"}, "teardown.%s" % sw)
        self.s12({"third": "off"}, "teardown.third")
        self.s12({"charrows": "off"}, "teardown.charrows")
        self.sn12({"look": "on"}, "teardown.look")
        if self.s12mode == "S12b":
            self.call("sn", "de.interior", {"patch": "on"}, label="teardown.patch")
        self.s12({"status": True}, "teardown.status")
        self.part("teardown", "end")

    def keep_breathing(self):
        if self.s12mode != "S12b":
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
    p = os.path.join(LCH.REPO, "tests", "s12")
    if p not in sys.path:
        sys.path.insert(0, p)
    import analyze_s12 as AN   # noqa: E402
    return AN

def env_for(mode, mutants):
    """(Subnautica env, SE env): S12 on (its default; set explicitly), S5/S6 as the s16 sessions; the mutants on their side."""
    sn_env = {"DEEPENGINEERS_S6": "on"}
    se_env = {"DEEPENGINEERS_S5": "on", "DEEPENGINEERS_S6": "on", "DEEPENGINEERS_S12": "on"}
    sn_m = [m for m in mutants if m in SN_MUTANTS]
    se_m = [m for m in mutants if m in SE_MUTANTS]
    if sn_m and mode == "S12b":
        sn_env["DEEPENGINEERS_MUTANT"] = ",".join(sn_m)
    if se_m:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(se_m)
    return sn_env, se_env

def resolve(a, mode):
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    parts = None
    probs = []
    if a.mutant_group:
        g = S12_MUTANT_GROUPS[a.mutant_group]
        mutants, parts = list(g["mutants"]), list(g["parts"])
    if a.parts and a.parts.strip().lower() == "all":
        parts = list(S12_PARTS[mode]) + ([p for p in OPTIONAL_PARTS if mode == "S12b"])
    elif a.parts:
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
    if parts is None:
        parts = list(S12_PARTS[mode])
    known = S12_PARTS[mode] + (OPTIONAL_PARTS if mode == "S12b" else ())
    unknown = [p for p in parts if p not in known]
    if unknown:
        probs.append("parts %s are not %s parts (known: %s)" % (unknown, mode, ",".join(known)))
    bad = [m for m in mutants if m not in ALL_MUTANTS]
    if bad:
        probs.append("unknown mutants %s (known: %s)" % (bad, ",".join(ALL_MUTANTS)))
    sn_only = [m for m in mutants if mode == "S12se" and m in SN_MUTANTS]
    if sn_only:
        probs.append("mutants %s are Subnautica's: they need S12b (S12se runs the stand-in host)" % sn_only)
    try:
        a.house_at_t = parse_xyz(getattr(a, "house_at", None)) or HOUSE_AT
        a.ref_at_t = parse_xyz(getattr(a, "ref_at", None), 3, 4, "--ref-at")
    except ValueError as e:
        probs.append(str(e))
    return parts, mutants, a.world or SURVIVAL_WORLD, probs

def host_cmd(LCH, a):
    return [sys.executable, "-I", os.path.join(LCH.REPO, "tests", "standins", "fake_sn_host.py"), "--name", a.name,
            "--s1", "--s3", "--s6", "--damage-rate", "0", "--damage-seed", str(a.seed), "--quit-on-se-exit", "--quiet"]

def plan_lines(mode, parts, mutants, world, a=None):
    at = getattr(a, "house_at_t", None) or HOUSE_AT
    out = ["  world: %s (Survival: oxygen and pressurization on; the throwaway marker, autosave off; tools/lab/make_survival_copy.py)" % world,
           "  Subnautica: %s" % ("the stand-in host (fake_sn_host.py)" if mode == "S12se" else "the real game, Freedom host slot"),
           "  SE: the real game on the copy",
           "  house: de.s3 room fixture at SN (%g, %g, %g), a flat window at cell %s, breach cell %s; the vent rig %g m east" % (at[0], at[1], at[2], list(WINDOW_CELL), list(BREACH_CELL), RIG_OFFSET[0]),
           "  env: %s" % json.dumps(env_for(mode, mutants))]
    for p in parts:
        out.append("  part %-9s %s" % (p, (getattr(S12Runner, "p_" + p).__doc__ or "").strip().split("\n")[0][:110]))
    return out

def s12_analyze(LCH, log, a, out) -> int:
    AN = analyzer(LCH)
    rows, _S = AN.evaluate(out)
    ok = AN.write_results(out, rows, strict=not a.lenient)
    print(AN.C.table(rows))
    log.say("S12 ORACLE TABLE: %s (%s)" % ("PASS" if ok else "FAIL", os.path.join(out, "s12-table.txt")), ev="s12_verdict", ok=ok)
    return 0 if ok else 1

def s12(LCH, log, a) -> int:
    S = LCH.S
    if a.notes:
        print(OWNER_NOTES)
        return 0
    if a.list_mutant_groups:
        for name, g in S12_MUTANT_GROUPS.items():
            print("  %-9s %-6s mutants %-8s parts %-30s red %s" % (name, g["mode"], ",".join(g["mutants"]), ",".join(g["parts"]), ",".join(g["red"])))
        return 0
    if a.analyze:
        return s12_analyze(LCH, log, a, a.analyze)
    if a.selftest:
        return s12_selftest(LCH, log)
    if a.mutant_group and a.mutant_group not in S12_MUTANT_GROUPS:
        log.say("unknown mutant group %s (known: %s)" % (a.mutant_group, ",".join(S12_MUTANT_GROUPS)))
        return 2
    modes = [a.mode] if a.mode else ([S12_MUTANT_GROUPS[a.mutant_group]["mode"]] if a.mutant_group else None)
    if modes is None:
        if not a.dry_run:
            log.say("s12: choose --mode S12se|S12b (or a --mutant-group); --dry-run without a mode prints both")
            return 2
        modes = list(S12_MODES)
    if a.dry_run:
        rc = 0
        for mode in modes:
            parts, mutants, world, probs = resolve(a, mode)
            print("S12 %s plan: parts %s%s%s" % (mode, ",".join(parts), " (quick)" if a.quick else "", " mutants %s" % ",".join(mutants) if mutants else ""))
            for line in plan_lines(mode, parts, mutants, world, a):
                print(line)
            info, wprobs = L6.world_check(LCH, world, "Survival", getattr(a, "force_unmarked", False))
            for p in wprobs:
                print("  A REAL RUN NEEDS: world %r: %s" % (world, p))
            for p in probs:
                print("  PROBLEM: %s" % p)
                rc = 2
        print("  mutant groups: %s" % ", ".join("%s=%s(%s)" % (n, g["mode"], ",".join(g["mutants"])) for n, g in S12_MUTANT_GROUPS.items()))
        log.say("dry run: nothing started")
        return rc
    mode = modes[0]
    parts, mutants, world, probs = resolve(a, mode)
    if probs:
        for p in probs:
            log.say("REFUSE s12: %s" % p)
        return 2
    info, wprobs = L6.world_check(LCH, world, "Survival", getattr(a, "force_unmarked", False))
    if wprobs:
        for p in wprobs:
            log.say("REFUSE s12 %s: world %r: %s (throwaway-copy rule)" % (mode, world, p))
        return 2
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s12", stamp)
    sn_env, se_env = env_for(mode, mutants)
    a.cont = True
    if mode == "S12se" and not a.sn_cmd:
        a.sn_cmd = json.dumps(host_cmd(LCH, a))
    sn_cmd, se_cmd = LCH.session_commands(a)
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "s12-session.jsonl"))
    sess.event("start", kind="s12", stamp=stamp, mode=mode, parts=parts, mutants=mutants, name=a.name, argv=sys.argv[1:],
               quick=a.quick, standin={"sn": bool(a.sn_cmd), "se": False}, snCmd=sn_cmd, seCmd=se_cmd,
               qpcFrequency=S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env, world=world, houseAt=list(a.house_at_t))
    log.say("S12 session %s (%s): parts %s mutants %s; out %s" % (stamp, mode, ",".join(parts), ",".join(mutants) or "none", out), ev="s12_plan")
    S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            runner = S12Runner(LCH, log, a, sess, sn, se, out, stamp, t_start, mode)
            ok, why = runner.attach()
            if not ok:
                log.say("S12: harness not usable: %s" % why, ev="s12_harness_refused")
                rc = 1
            else:
                if mode == "S12b":
                    runner.park()
                runner.mutants_on(mutants)
                for p in parts:
                    if not runner.alive():
                        log.say("S12: a game exited before part %s" % p, ev="s12_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:  # noqa: BLE001 - a part error is logged, the session goes on
                        log.say("S12 part %s failed: %r" % (p, e), ev="s12_part_error", part=p)
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
    res = s12_analyze(LCH, log, a, out)
    return rc if rc is not None else res

def run(LCH, log, a) -> int:
    return s12(LCH, log, a)

# ----------------------------------------------------------------------------------------------------------------------
# self-test (no game, no section, no process)
# ----------------------------------------------------------------------------------------------------------------------

def s12_selftest(LCH, log) -> int:
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S12 launcher self-test (launcher.py s12 --selftest)")

    class A:
        mutants = ""
        parts = ""
        world = None
        mutant_group = None
        house_at = None
        ref_at = None

    for mode in S12_MODES:
        a = A()
        parts, mutants, world, probs = resolve(a, mode)
        check(parts == list(S12_PARTS[mode]) and not probs and world == SURVIVAL_WORLD, "%s default parts %s on %s" % (mode, ",".join(parts), world))
        check(all(hasattr(S12Runner, "p_" + p) for p in S12_PARTS[mode] + OPTIONAL_PARTS), "%s: every part has a runner method" % mode)
    AN = analyzer(LCH)
    need = {"S12.FLOOD.DEBOUNCE": "airlock", "S12.FLOOD.ONEPLANE": "airlock", "S12.AIR.FLIP": "air", "S12.AIR.OUT": "air", "S12.AIR.HOLD": "air", "S12.AIR.THIRD": "air", "S12.AIR.OPEN": "flood",
            "S12.AIR.WINDOW.IN": "air", "S12.FLOOD.CLIP": "flood", "S12.FLOOD.MOVE": "wade",
            "S12.FLOOD.FILL": "flood", "S12.FLOOD.DRAIN": "flood", "S12.FLOOD.ONCE": "flood", "S12.VENT": "vent", "S12.LOOK.PRESET": "look",
            "S12.FLOOD.PLANE": "flood", "S12.FLOOD.LINE": "flood"}
    for name, g in S12_MUTANT_GROUPS.items():
        a = A()
        a.mutant_group = name
        parts, mutants, world, probs = resolve(a, g["mode"])
        sn_env, se_env = env_for(g["mode"], mutants)
        se_side = [m for m in mutants if m in SE_MUTANTS]
        sn_side = [m for m in mutants if m in SN_MUTANTS]
        ok = not probs and parts == list(g["parts"]) and (not se_side or se_env.get("DEEPENGINEERS_MUTANTS") == ",".join(se_side))
        ok = ok and (not sn_side or sn_env.get("DEEPENGINEERS_MUTANT") == ",".join(sn_side)) and se_env["DEEPENGINEERS_S12"] == "on"
        ok = ok and tuple(g["red"]) == tuple(AN.MUTANT_ROWS[g["mutants"][0]]) and all(need[r] in parts for r in g["red"])
        ok = ok and (g["mode"] == "S12b" or not sn_side)
        check(ok, "group %s: %s %s, red %s (the analyzer's MUTANT_ROWS), env %s / %s" % (name, g["mode"], ",".join(mutants), ",".join(g["red"]),
                                                                                           sn_env.get("DEEPENGINEERS_MUTANT"), se_env.get("DEEPENGINEERS_MUTANTS")))
    grouped = {m for g in S12_MUTANT_GROUPS.values() for m in g["mutants"]}
    check(set(AN.MUTANT_ROWS) == set(ALL_MUTANTS) == grouped, "every analyzer mutant has a group")
    check(tuple(AN.SN_MUTANTS) == SN_MUTANTS, "the analyzer's Subnautica mutants are the launcher's")
    a = A()
    a.mutants = "S12.M6"
    _p, _m, _w, probs = resolve(a, "S12se")
    check(bool(probs), "a Subnautica mutant refused in S12se (the stand-in host has no S12)")
    a = A()
    a.parts = "look"
    _p, _m, _w, probs = resolve(a, "S12se")
    check(bool(probs), "look is not an S12se part (it needs Subnautica's picture)")
    a = A()
    a.house_at = "1,2"
    _p, _m, _w, probs = resolve(a, "S12b")
    check(bool(probs), "a bad --house-at is refused")
    a = A()
    a.ref_at = "10,-200,30,90"
    _p, _m, _w, probs = resolve(a, "S12b")
    check(not probs and a.ref_at_t == (10.0, -200.0, 30.0, 90.0), "--ref-at x,y,z,yaw")
    g = house_geometry(HOUSE_AT)
    T = AN.T
    # the house's room (i east, j up, k north): the 3^3 interior and the east window's cell, its glass (+x face) sealed
    win = (WINDOW_CELL[0], WINDOW_CELL[1], -WINDOW_CELL[2])
    rs = T._room_fn([((1, 1, 1, 3, 3, 3), 1), (win + win, 1)])
    ss = T._seal_fn({win: 1 << 0})
    at = HOUSE_AT

    def local(p):
        """an SN point -> the room fixture's grid-local metres (i east, j up, k north -> cell (i, j, -k))"""
        return ((p[0] - at[0]), (p[1] - at[1]), -(p[2] - at[2]))

    def room(x, y, z):
        return rs(x, y, -z)

    def seal(x, y, z, d):
        return ss(x, y, -z, d if d < 4 else d ^ 1)
    h = T.hysteresis(BLOCK)
    outs = {n: T.depth(local(p), BLOCK, room)[0] for n, p in g["out"].items()}
    check(all(v < -h for v in outs.values()), "every outside point lies outside the room's air by more than the hysteresis (the twin: air up to the window's glass)")
    check(set(OUT_YAW) == set(g["out"]), "every outside point has its heading toward the house (queue 22: KeyError 'slope' ended the air part)")
    eye = lambda feet: (feet[0], feet[1] + 1.7, feet[2])  # noqa: E731
    d_in = T.depth(local(eye(g["winCharIn"])), BLOCK, room)
    d_mid = T.depth(local(eye(g["winCharIn"])), BLOCK, room, seal)
    check(d_in[3] and tuple(d_in[2]) == (WINDOW_CELL[0], WINDOW_CELL[1], WINDOW_CELL[2]) and d_in[0] > h,
          "the diver pressed against the window from inside: the eye in the window's cell, %.2f m inside the air (> h)" % d_in[0])
    check(d_mid[0] < -h, "the same eye under round 2's mid-plane (S12.M9): %.2f m, out (the owner's swim): S12.AIR.WINDOW.IN red" % d_mid[0])
    d_out = T.depth(local(eye(g["winCharOut"])), BLOCK, room)[0]
    check(d_out < -h, "the diver pressed against the glass from outside: %.2f m, out" % d_out)
    ins_ok = all(T.depth(local(p), BLOCK, room)[0] > h for p in (g["in"], g["corner"], eye(g["wadeChar"]), eye(g["charIn"])))
    check(ins_ok, "the inside points and the divers' eyes lie inside it by more than the hysteresis")
    xs = cross_path(g["face"])
    y, z = g["crossYZ"]
    was, flips = True, 0
    for x in xs:
        now = T.hold(was, T.depth(local((x, y, z)), BLOCK, room)[0], h)
        flips += now != was
        was = now
    raw_flips, prev = 0, None
    for x in xs:
        c = local((x, y, z))
        now = room(T.cell_of(c[0], BLOCK), T.cell_of(c[1], BLOCK), T.cell_of(c[2], BLOCK)) > 0
        raw_flips += prev is not None and now != prev
        prev = now
    check(flips == 1 and not was, "the crossing path flips the twin's rule once (%d steps, +-%g m jitter)" % (len(xs), CROSS_JITTER_M))
    check(raw_flips > 1, "the same path flips the raw cell (S12.M1) %d times" % raw_flips)
    fill = T.fill_ms(ROOM_CELLS, BLOCK, 1, g["depth"])
    check(2000 <= fill <= 30000 and g["depth"] > 0, "the house at %g m fills in %.1f s (the twin), under water" % (g["depth"], fill / 1000.0))
    check(0 < local(g["floorCam"])[1] - 0.5 * BLOCK < HALF_LEVEL * 3 * BLOCK and local(g["ceiling"])[1] - 0.5 * BLOCK > HALF_LEVEL * 3 * BLOCK,
          "at half height the floor camera is under the plane and the ceiling camera above it")
    cam_h = (local(g["ceiling"])[1] - 0.5 * BLOCK) / (3 * BLOCK)
    dist = g["lineFloor"][0] - g["ceiling"][0]
    worst = max(math.degrees(math.atan((cam_h - lv) * 3 * BLOCK / dist)) for lv in DRAIN_LINES + (HALF_LEVEL,))
    check(worst < 33.0, "every draining waterline capture sees its line on the far wall (%.1f deg below the view, the half field 35)" % worst)
    # the wade: the vent drive empties the room's oxygen past the diver's eye and back (format flood.drain's oxygen source)
    eye_lv = (local(eye(g["wadeChar"]))[1] - 0.5 * BLOCK) / (3 * BLOCK)
    top = min(1.0, WADE_RATE * WADE_DOWN_S / 0.9)
    check(top > eye_lv + 0.1, "the drive lets the water %.2f of the room high, over the diver's eye (%.2f) by more than 0.1" % (top, eye_lv))
    check(WADE_UP_S * WADE_RATE >= 0.9 and WADE_DRAIN_S * 1000.0 >= T.F["DrainMs"] + 4000, "the pressurize brings the planet's oxygen back and the wait covers DrainMs")
    rig_west = HOUSE_AT[0] + RIG_OFFSET[0] - 2.5 * BLOCK
    check(rig_west - (HOUSE_AT[0] + 4.5 * BLOCK) >= 3.0, "the vent rig (its tank included) stands clear of the house (%.1f m)" % (rig_west - (HOUSE_AT[0] + 4.5 * BLOCK)))
    check(AN.selftest() == 0, "tests/s12/analyze_s12.py --selftest")
    check("look off" in OWNER_NOTES and "Air Vent" in OWNER_NOTES and "third person" in OWNER_NOTES and "never stuck" in OWNER_NOTES,
          "owner notes name the look switch, the vent, third person and the flood's movement")
    ok = all(results)
    print("S12 LAUNCHER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def add_parsers(sub, LCH):
    p = sub.add_parser("s12", help="S12 interiors v2: modes S12se (SE + stand-in host) and S12b (both games)")
    p.add_argument("--mode", choices=S12_MODES, default=None)
    p.add_argument("--parts", default="", help="comma list (default: every part of the mode), or 'all' (adds lookref in S12b)")
    p.add_argument("--mutants", default="", help="S12.M1..S12.M8 (dev builds)")
    p.add_argument("--mutant-group", default=None, choices=sorted(S12_MUTANT_GROUPS))
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--world", default=None, help="the Survival throwaway copy (default %r)" % SURVIVAL_WORLD)
    p.add_argument("--force-unmarked", action="store_true", help="accept a copy without the throwaway marker (owner-approved)")
    p.add_argument("--house-at", default=None, metavar="X,Y,Z", help="SN point of the house's block (0, 0, 0) (default %g,%g,%g)" % HOUSE_AT)
    p.add_argument("--ref-at", default=None, metavar="X,Y,Z[,YAW]", help="a point inside a Subnautica base of the real map (part lookref)")
    p.add_argument("--quick", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--selftest", action="store_true")
    p.add_argument("--notes", action="store_true", help="print the owner-attended free-play notes")
    p.add_argument("--analyze", default=None, metavar="DIR")
    p.add_argument("--lenient", action="store_true", help="MISSING rows do not fail the table")
    p.add_argument("--out", default=None)
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--seed", type=int, default=12)
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    # the session plumbing's options (as launcher_s6's)
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (S12se default: the stand-in host)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--screen", default="2560x1440")
    p.add_argument("--fov", type=float, default=70.0)
    p.add_argument("--owner", action="store_true", help="accepted for symmetry with s6 (S12 has no scripted owner step: --notes)")
