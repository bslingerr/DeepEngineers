"""`launcher.py s7` (ROADMAP S7 "Interiors, lights, thrusters + underwater effects, audio polish, statics into SE,
skinned astronaut"; CONTRACT §5.4 "S7 conventions"). Lives next to launcher.py, which imports it and passes itself
(`run(LCH, log, a)`, `add_parsers(sub, LCH)`): the session machinery (preflight, start_games, the in-world wait, the C11
harness, close_session, collect) is launcher.py's, the runner plumbing is launcher_s5.S5Runner's, the oracle is tests/s7/analyze_s7.py.

 python -I src/DeepEngineers.Launcher/launcher.py s7 [--parts n1,n1b,n1c,n2,n3,n4,n5,n6,n7,n8,n9,n10,n11,wm5,teardown]
 [--owner] [--mutant S7.Mx] [--world NAME] [--room BLUEPRINT]
 [--room-at X,Y,Z] [--ship BLUEPRINT] [--quick] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s7 --census [--batches all|FILE] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s7 --list-mutant-groups | --selftest | --analyze tests/out/s7/<stamp>

`s7` runs one scripted S7 session with both games (DE Lab unless --world, Legacy.exe -continue on the world LastSession
points at; the S5 session rules of S5-STATUS §9.1: the S5 mirror on, the character parked at SN (24, -8, 0) first).
SE's environment: DEEPENGINEERS_S5=on, DEEPENGINEERS_PICTURE=site, DEEPENGINEERS_TIMESYNC=on, DEEPENGINEERS_S6=off
unless n9 runs (the S6 proxies are its subject), DEEPENGINEERS_BAND (the S7a band, env or default), and the SE mutants
in DEEPENGINEERS_MUTANTS; Subnautica's: DEEPENGINEERS_MUTANT for the SN mutants. The session start row carries `band`,
the world and its settings (EnableOxygen, EnableOxygenPressurization, read-only from Sandbox_config.sbc). Parts (each in
windows named <part>.<step> in s7-session.jsonl, the names tests/s7/analyze_s7.py reads):
 n1 the sealed room (S7 review F1: built by de.s3 {place: {plate: {orientation: room}}}, the S7 room fixture: a light
 armor 5 x 5 x 5 shell around a 3 x 3 x 3 interior, a round window in the north (+Z) wall, an Air Vent (SE builds
 no airtight room without a gas block), a battery, an Interior Light, three spotlights and a door inside; else
 --room BLUEPRINT pasted by the project) with block (0, 0, 0) at --room-at (default SN (60, -12, 60)); the spectator
 inside 10 s and 2 m outside 10 s, twice; the door toggled 5 times with the camera inside (de.interior {action:
 door}: the gas system recomputes, S7.N1.flicker / S7.M10); Subnautica de.interior {patch: on|off} A/B with de.pixel
 probes (a) the inner wall beside the window, (b) 20 m beyond the window and a capture each; a wall block removed
 then re-placed, an interior block added and removed; the sky rows of S7.N8 come with it
 n1b the same room on the pressurisation-off copy
 n1c the project's drained house: the character on a dry deck (reference walk), then walking in the room
 n2 Subnautica de.interior {action: trace, seconds: 10} inside, then outside (the IsInsideWalkable readers)
 n3 lights: the fixture's Interior Light about 2.5 m from the east inner wall (and its three shadowed spotlights, the
 subject of S7.M18) and a spawned Seamoth's cabin light at that distance from a like wall, night or
 deep; de.pixel probes and a capture; de.lights status on both sides
 n4 effects: the test ship at SN (0, -20, 80):
 thrust override 0/0.25/0.5/0.75/1.0 for 3 s each, 20 x ShootOnce, ship welder/grinder/drill 5 s each, injected
 explosions of radius 2, 10 and 30 m, a small grid dropped on the ship and one into the sea, the N1 room
 breached under water, with Subnautica capped to 30 fps
 de.fx {action: stress, n: 500} in one tick (S7.N4.rates) then {action: stress, n: 500, ticks: 8} (S7.N4.loss),
 the cap cleared; the thrusters at SN y +2 m; project: hand rifle, welder, grinder, drill 5 s each under water,
 optional warhead
 n5 statics rays at three explorable wrecks with box colliders, the camera 12 m above
 each wreck and the SE character beside it (statics hosts follow anchors), 200 rays per site from Subnautica cast by
 both games; a small grid against a wreck 1 km away rests on the census host
 n6 the skinned astronaut: the S7a deck, Subnautica's walk source sebones, a scripted walk with de.bones rows for 60
 frames on both sides, then the female model and the same rows
 n7 census completeness, offline: tools/sn-octrees/sn_prefab_colliders.py --census --out PATH (no game)
 n8 interior sky: inside / outside the room with Subnautica de.interior status (s7.sky rows)
 n9 statics filter: a reaper spawned 60 m away (de.spawn ReaperLeviathan 1 60), the S6 lab ship with its proxies in
 the scan radius (DEEPENGINEERS_S6=on), de.statics status rows
 n10 save content on the throwaway copy (--world "DE Lab S7Save", DE_THROWAWAY.txt marker): hosts alive, the project
 saves, the copy's files scanned read-only for DE.Statics names and host entity ids
 n11 audio: a descent +5 -> -30 m beside the test ship at 50 % thrust, then into the dry room; de.s7audio status
 wm5 one SE day (1,200 s), SE and Subnautica de.s7 {action: sun} every 30 s
 teardown placed grids removed, overrides off, the interior patch on, census off, walk source sebones (the S7a default since 16:50), the 30 fps cap off, mutants off
`--census`: Subnautica with the fake producer as SE, Subnautica de.statics {action: census on}, then off; writes
%LOCALAPPDATA%\\DeepEngineers\\census\\.
Seeded failures (--mutant, one per session; its parts run with teardown): see MUTANT_GROUPS. Output
tests/out/s7/<stamp>[-<mutant>]/ (s7-session.jsonl, se-*.jsonl, sn-*.jsonl, the game logs, captures/, s7-table.txt,
s7-results.json). Games are never piped and only closed by PID; nobody but the orchestrator launches them; world
copies are the orchestrator's (tools/lab/), read here only."""
from __future__ import annotations

import datetime
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import time

S7_PARTS = ("n1", "n1b", "n1c", "n2", "n3", "n4", "n5", "n6", "n7", "n8", "n9", "n10", "n11", "wm5", "teardown")
# the default world's; S7-1 fix: n9 last before teardown (its reaper, spawned 60 m from SN (0, -20, 0), bit the room
# fixture and the lab grids 25 times during wm5: DE.Damage DeepBite 20:15-20:30, one spotlight block lost)
DEFAULT_PARTS = ("n1", "n1c", "n2", "n3", "n4", "n5", "n6", "n8", "n11", "wm5", "n9", "teardown")
OFFLINE_PARTS = ("n7",)
LAB_WORLD = "DE Lab"
PART_WORLD = {"n1b": "DE Lab NoPress", "n10": "DE Lab S7Save"}   # parts that run only on their world copy
THROWAWAY_PARTS = ("n10",)
SE_MUTANTS = ("S7.M8", "S7.M9", "S7.M10", "S7.M12", "S7.M14", "S7.M15", "S7.M16", "S7.M17", "S7.M19", "S7.M20")
SN_MUTANTS = ("S7.M1", "S7.M2", "S7.M3", "S7.M4", "S7.M5", "S7.M7", "S7.M11", "S7.M13", "S7.M18")
WORLD_MUTANTS = ("S7.M6",)
ALL_MUTANTS = SN_MUTANTS + SE_MUTANTS + WORLD_MUTANTS
MUTANT_GROUPS = {   # ROADMAP S7 "Seeded failures" and the launcher line: mutant -> parts (and world)
    "S7.M1": {"parts": ("n1", "n2", "teardown")},
    "S7.M2": {"parts": ("wm5", "teardown")},
    "S7.M3": {"parts": ("n5", "teardown")},
    "S7.M4": {"parts": ("n6", "teardown")},
    "S7.M5": {"parts": ("n1", "teardown")},
    "S7.M6": {"parts": ("wm5", "teardown"), "world": "DE Lab SunWeather"},
    "S7.M7": {"parts": ("n9", "teardown")},
    "S7.M8": {"parts": ("n10", "teardown"), "world": "DE Lab S7Save"},
    "S7.M9": {"parts": ("n6", "teardown")},
    "S7.M10": {"parts": ("n1", "teardown")},
    "S7.M11": {"parts": ("n4", "teardown")},
    "S7.M12": {"parts": ("n4", "teardown")},
    "S7.M13": {"parts": ("n4", "teardown")},
    "S7.M14": {"parts": ("n4", "teardown")},
    "S7.M15": {"parts": ("n4", "teardown")},
    "S7.M16": {"parts": ("n4", "teardown")},
    "S7.M17": {"parts": ("n4", "teardown")},
    "S7.M18": {"parts": ("n3", "teardown")},
    "S7.M19": {"parts": ("n11", "teardown")},
    "S7.M20": {"parts": ("n4", "teardown")},
}

# geometry (SN metres). The room is the de.s3 room fixture: block (0, 0, 0) at
# ROOM_AT (--room-at), a 5 x 5 x 5 shell around the interior cells 1..3 (SN offsets i east, j up, k north in blocks).
ROOM_AT = (60.0, -12.0, 60.0)
ROOM_PLATE = {"orientation": "room"}
BLOCK = 2.5
DOOR_TOGGLES = 5

def room_geometry(at):
    """The room's camera and probe points from its block (0, 0, 0) at `at` (SN): the interior centre (cell 2, 2, 2), 2 m
 outside the south wall, 1.5 m from the window wall, the wall beside the window, 20 m through the window, the
 character's floor point, the Interior Light's lit wall point (the east inner wall at the light's height, cell
 (3, 3, 2)) and the n3 camera (cell (1, 2, 2), looking east)."""
    x, y, z = at
    g = {"at": tuple(at)}
    g["in"] = (x + 2 * BLOCK, y + 2 * BLOCK, z + 2 * BLOCK)
    north = z + 3.5 * BLOCK                                                    # the window wall's inner face
    g["north"] = north
    g["out"] = (g["in"][0], g["in"][1], z - BLOCK / 2.0 - 2.0)
    # S7-1 fix: probe (a) 2 m beside the window seen from 1.5 m lay 53 deg off axis, beyond Subnautica's
 # half horizontal field of view (51 deg at 70 deg vertical, 16:9): DE.SnPixel px 2651 of 2560. The camera now stands
 # 2.5 m from the window wall and the probe 1.8 m beside the window's centre (inside the neighbouring wall block,
 # 36 deg off axis)
    g["fogCam"] = (g["in"][0], g["in"][1], north - 2.5)
    g["fogA"] = (g["in"][0] + 1.8, g["in"][1], north)
    g["fogB"] = (g["in"][0], g["in"][1], north + 20.0)
    g["char"] = (g["in"][0], y + BLOCK / 2.0 + 0.35, g["in"][2] - 2.0)       # 0.35 m above the floor's top face
    g["lightAt"] = (x + 3.5 * BLOCK, y + 3 * BLOCK, z + 2 * BLOCK)
    g["lightCam"] = (x + 1 * BLOCK, y + 2 * BLOCK, z + 2 * BLOCK)
    g["seamothWall"] = (g["in"][0] - 20.0, g["in"][1], g["in"][2])
    # S7-1 fix: the Seamoth's wall point lay behind the n3 camera (looking east; DE.SnPixel depth -17.5): it gets its own
 # camera 6.25 m east of it looking west (yaw 270) and its own capture n3.seamoth
    g["seamothCam"] = (g["seamothWall"][0] + 6.25, g["seamothWall"][1], g["seamothWall"][2])
    return g

def parse_room_at(text):
    """--room-at "x,y,z" (SN metres) -> a tuple, None when absent; ValueError on a bad value."""
    if not text:
        return None
    v = [float(t) for t in str(text).replace(";", ",").split(",") if t.strip()]
    if len(v) != 3:
        raise ValueError("--room-at needs x,y,z (SN metres)")
    return tuple(v)

_G = room_geometry(ROOM_AT)
ROOM_IN, ROOM_OUT, FOG_CAM, FOG_PROBE_A, FOG_PROBE_B = _G["in"], _G["out"], _G["fogCam"], _G["fogA"], _G["fogB"]
ROOM_NORTH_WALL_Z = _G["north"]
SHIP_AT = (0.0, -20.0, 80.0)
ABOVE_AT = (0.0, 2.0, 80.0)
DECK = (40.0, 1.5, 40.0)
DECK_PLATE = {"rows": 8, "cols": 8, "orientation": "floor"}
LIGHT_AT = _G["lightAt"]                                         # the fixture light's lit wall point (east inner wall)
SEAMOTH_WALL_AT = _G["seamothWall"]                              # the Seamoth's lit wall point
# S7-1 fix: three explorable wrecks
# whose colliders are boxes only (no mesh OBB: the 0.1 m budget holds for primitives), read from the player's own install
# (read-only: the census's class verdicts in the S7 fix tool's census_full.json + the root transform of each wreck in
# Subnautica_Data/StreamingAssets/SNUnmanagedData/Build18/CellsCache/baked-batch-cells-<batch>.bin; SN world = land origin
# (-2048, -3040, -2048) + batch x 160 + cell x cell size (32 m at levels 1-3) + half a cell + the root's local position,
# BatchCells.GetCellSize / EntityCell.GetCellRootPosition; the same rule puts Lifepod 3 at (-33, -23, 410) and Lifepod 17
# at (-516, -99, -56), their known positions). Each is kinematic and allow-listed (the census allow-list in %LOCALAPPDATA%
# is installed: the S7-1 statics status read "420 class id(s)"). The camera 12 m above the wreck root; the SE character is
# parked beside it (S7 statics hosts follow anchors: mirrored grids and online characters, never the spectator camera).
N5_SITES = {
    "shallows": (-46.6, -15.3, -401.5),     # ExplorableWreck_SafeShallows_15 at (-46.6, -27.3, -401.5): 97 boxes
    "kelp": (-314.4, -64.0, 249.0),          # ExplorableWreck_KelpForest_18 at (-314.4, -76.0, 249.0): 125 boxes
    "grassy": (-423.5, -97.3, -259.5),       # ExplorableWreck_Grassy_14 at (-423.5, -109.3, -259.5): 123 boxes
}
N5_SETTLE_S = 10.0              # Subnautica's 2 s pass + its first send + SE's host build (<= 256 primitives per tick)
CENSUS_GRID_AT = (384.8, -20.0, -194.5)  # a small grid against ExplorableWreck_SafeShallows_16 (384.8, -22.6, -194.5), camera 1 km away
N9_AT = (0.0, -20.0, 0.0)
N11_TOP, N11_BOTTOM = 5.0, -30.0
WM5_SECONDS, WM5_EVERY = 1200.0, 30.0
WM5_SPOT = (0.0, 100.0, 0.0)
THRUST_LEVELS = (0.0, 0.25, 0.5, 0.75, 1.0)
EXPLOSION_RADII = (2.0, 10.0, 30.0)
STRESS_N = 500
STRESS_BURST_TICKS = 8          # review F10: the burst over 8 ticks (S7.N4.loss)
LOSS_FPS = 30                   # review F10: Subnautica capped during n4.stress
QUICK = 0.25
CENSUS_DIR = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers", "census")

def band_from_env():
    """The S7a picture band (enter, exit): DEEPENGINEERS_BAND='enter,exit' (the variable SE reads), default +1,000/+1,100 m."""
    v = os.environ.get("DEEPENGINEERS_BAND", "")
    try:
        parts = [float(x) for x in v.replace(";", ",").split(",") if x.strip()]
        if len(parts) == 2 and 0.0 <= parts[0] < parts[1]:
            return parts[0], parts[1]
    except ValueError:
        pass
    return 1000.0, 1100.0   # S7t-se-composite (T3): the band back at 1 km (= ViewThresholds' default)

BAND = band_from_env()

def analyzer(LCH):
    p = os.path.join(LCH.REPO, "tests", "s7")
    if p not in sys.path:
        sys.path.insert(0, p)
    import analyze_s7   # noqa: E402
    return analyze_s7

def s5_module(LCH):
    if LCH.HERE not in sys.path:
        sys.path.insert(0, LCH.HERE)
    import launcher_s5   # noqa: E402
    return launcher_s5

def last_session_world(LCH):
    """The world SE's -continue loads (LastSession.sbl's <Path> basename; read-only), None when unclear."""
    hits = glob.glob(os.path.join(LCH.SE_APPDATA, "Saves", "*", "LastSession.sbl"))
    names = set()
    for h in hits:
        try:
            with open(h, encoding="utf-8-sig", errors="replace") as f:
                m = re.search(r"<Path>([^<]*)</Path>", f.read())
            if m:
                names.add(os.path.basename(m.group(1).rstrip("/\\")))
        except OSError:
            continue
    return names.pop() if len(names) == 1 else None

def world_settings(LCH, name):
    """Read-only facts of an SE world: folder, LastSession, EnableOxygen, EnableOxygenPressurization, GameMode, marker."""
    info = LCH.s4_world_info(name)
    out = {"world": name, "folder": info.get("folder"), "lastSession": info.get("lastSession"), "error": info.get("error")}
    if not info.get("folder"):
        return out
    try:
        with open(os.path.join(info["folder"], "Sandbox_config.sbc"), encoding="utf-8-sig", errors="replace") as f:
            txt = f.read()
    except OSError as e:
        out["error"] = "Sandbox_config.sbc unreadable: %s" % e
        return out
    for key in ("EnableOxygen", "EnableOxygenPressurization", "GameMode", "AutoSaveInMinutes"):
        m = re.search(r"<%s>([^<]*)</%s>" % (key, key), txt)
        v = m.group(1).strip() if m else None
        out[key[0].lower() + key[1:]] = (v.lower() == "true") if v in ("true", "false", "True", "False") else v
    out["marked"] = os.path.exists(os.path.join(info["folder"], "DE_THROWAWAY.txt"))
    out["pointed"] = info.get("lastSession") == os.path.basename(info["folder"])
    return out

def world_problems(ws, parts, standin):
    """Why a session cannot run its parts on this world (empty = fine)."""
    probs = []
    if standin:
        return probs
    if not ws.get("folder"):
        return [ws.get("error") or "world %r not found" % ws.get("world")]
    if not ws.get("pointed"):
        probs.append("LastSession.sbl points at %r, not %r (the orchestrator points it with the tools/lab scripts)" % (
            ws.get("lastSession"), ws.get("world")))
    for p in parts:
        want = PART_WORLD.get(p)
        if want and ws.get("world") != want:
            probs.append("part %s runs on %r only (--world %r)" % (p, want, want))
        if p in THROWAWAY_PARTS and not ws.get("marked"):
            probs.append("part %s needs a throwaway copy with the DE_THROWAWAY.txt marker" % p)
    if any(p in ("n1", "n1c", "n2", "n8", "n11") for p in parts) and ws.get("world") != PART_WORLD.get("n1b") and \
            not (ws.get("enableOxygen") and ws.get("enableOxygenPressurization")):
        probs.append("EnableOxygen and EnableOxygenPressurization must be on for the room parts (owner action 1: SE's world "
                     "settings, or tools/lab/set_world_settings.py run by the orchestrator with SE closed)")
    return probs

def _runner_class(LCH):
    L5 = s5_module(LCH)

    class S7Runner(L5.S5Runner):
        """The scripted S7 parts. The action tables name every command in the plugins' own syntax (CONTRACT §5.4 "S7
 control commands": SE de.interior, de.lights, de.fx, de.statics, de.bones, de.s7audio; Subnautica de.interior,
 de.lights, de.fx, de.statics, de.bones; S7a's de.s7; S6's de.spawn; S3's de.s3; S2's de.teleport/de.motion;
 S2b's de.swim; S1's de.pixel/de.capture), so a renamed command changes in one place."""

        SE_ACTIONS = {
            "interior": ("de.interior", lambda: {"action": "status"}),
            "lights": ("de.lights", lambda: {"action": "status"}),
            "fx": ("de.fx", lambda: {"action": "status"}),
            "thrust": ("de.fx", lambda ent, lvl: {"thrust": ent, "override": float(lvl)}),
            "gun": ("de.fx", lambda ent, n: {"gun": ent, "shots": int(n)}),
            "tool": ("de.fx", lambda ent, on: {"tool": ent, "on": bool(on)}),
            "inject": ("de.fx", lambda kind, at, extra: dict({"inject": kind, "at": list(at)}, **extra)),
            "stress": ("de.fx", lambda n: {"action": "stress", "n": int(n)}),
            "burst": ("de.fx", lambda n, ticks: {"action": "stress", "n": int(n), "ticks": int(ticks)}),
            "fxtrace": ("de.fx", lambda secs: {"action": "trace", "seconds": float(secs)}),
            "door": ("de.interior", lambda block, op: {"action": "door", "block": str(block), "open": bool(op)}),
            "statics": ("de.statics", lambda: {"action": "status"}),
            "hosts": ("de.statics", lambda: {"action": "hosts"}),
            "rays": ("de.statics", lambda site, extra: dict({"action": "rays", "site": site}, **extra)),
            "probe": ("de.statics", lambda entity, down: {"action": "probe", "entity": str(entity), "down": float(down)}),
            "bones": ("de.bones", lambda frames: {"action": "rows", "frames": int(frames)}),
            "bonestatus": ("de.bones", lambda: {"action": "status"}),
            "audio": ("de.s7audio", lambda: {"action": "status"}),
            "effect": ("de.s7audio", lambda e: {"effect": e}),
            "sun": ("de.s7", lambda: {"action": "sun"}),
            "status7a": ("de.s7", lambda: {"action": "status"}),
            "place": ("de.s3", lambda sn, block, plate, yaw, label: {"place": {"sn": list(sn), "yawDeg": float(yaw), "block": block,
                                                                             "plate": dict(plate), "label": label}}),
            "remove": ("de.s3", lambda: {"remove": True}),
            "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
        }
        STANDIN_ONLY = ()
        SN_ACTIONS = {
            "interior": ("de.interior", lambda: {"action": "status"}),
            "patch": ("de.interior", lambda on: {"patch": "on" if on else "off"}),
            "trace": ("de.interior", lambda secs: {"action": "trace", "seconds": float(secs)}),
            "lights": ("de.lights", lambda: {"action": "status"}),
            "fx": ("de.fx", lambda: {"action": "status"}),
            "sources": ("de.fx", lambda: {"action": "sources"}),
            "fps": ("de.fx", lambda cap: {"action": "fps", "cap": float(cap)}),
            "statics": ("de.statics", lambda: {"action": "status"}),
            "census": ("de.statics", lambda on: {"action": "census on" if on else "census off"}),
            "rays": ("de.statics", lambda site, n: {"action": "rays", "n": int(n), "range": 40.0, "site": site}),
            "bones": ("de.bones", lambda frames: {"action": "rows", "frames": int(frames)}),
            "bonestatus": ("de.bones", lambda: {"action": "status"}),
            "walk": ("de.s7", lambda w: {"walk": w}),
            "sun": ("de.s7", lambda: {"action": "sun"}),
            "spawn": ("de.spawn", lambda tt, n, dist: {"techType": tt, "count": int(n), "dist": float(dist)}),
            "pixel": ("de.pixel", lambda pt, label: {"pointSn": list(pt), "minSeTick": 0, "label": label}),
            "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
        }

        def __init__(self, *args, **kw):
            super().__init__(*args, **kw)
            self.placed = 0
            self.cap_n = 0
            self.ship = None          # {"grid": id, "gun": id, "tools": {name: id}} from --ship-ids or de.fx status
            self.room_placed = False
            self.room_by_s3 = False   # de.s3 built it: another de.s3 place (the deck) closes it
            self.room = {}            # the de.s3 room reply's block ids (door, vent, light, spots, battery, window)
            self.geo = room_geometry(parse_room_at(getattr(self.a, "room_at", None)) or ROOM_AT)

        def scale(self, secs, quick=None):
            return secs if not self.q else (quick if quick is not None else max(1.0, secs * QUICK))

        def window(self, name, fn):
            self.part(name, "begin")
            try:
                fn()
            finally:
                self.part(name, "end")

        def capture(self, name):
            self.cap_n += 1
            rel = "s7/%s/%s-%02d.png" % (self.stamp, name, self.cap_n)
            rep = self.call("sn", "de.capture", {"path": rel, "label": name, "kind": 1}, timeout=10.0, label="%s.capture" % name)
            self.sess.event("capture", name=name, side="sn", path=rel, reply=rep, qpc=self.S.qpc())
            self.wait(1.0)

        def cam(self, xyz, yaw, label):
            self.teleport(xyz[0], xyz[1], xyz[2], yaw, label=label)
            self.wait(self.scale(1.5, 1.0))

        # ---- the room and the ship ---------------------------------------------------------------------------------
        def ensure_room(self):
            if self.room_placed:
                return
            room = getattr(self.a, "room", None)
            at = self.geo["at"]
            if not room:
                rep = self.act("se", "place", at, "LargeBlockArmorBlock", ROOM_PLATE, 0.0, "s7.room", label="room.place", timeout=30.0)
                plate = (rep or {}).get("plate") if isinstance(rep, dict) else None
                if self.ok(rep) and isinstance(plate, dict) and plate.get("orientation") == "room":
                    self.placed += 1
                    self.room_placed = True
                    self.room_by_s3 = True
                    # S7-1 fix: de.s3 keeps one grid: this place closed the deck (S7-1: "closed 1 previous S3 grid(s)" at
 # n1c), so n6 walked a character that had no deck under it (swimming: Subnautica's Retarget path)
                    self._deck = False
                    self.room = rep.get("room") if isinstance(rep.get("room"), dict) else {}
                    self.sess.event("room", at=list(at), ids=self.room)
                    self.wait(self.scale(5.0, 2.0))   # the gas system builds the room (the Air Vent makes it pressurize)
                    return
                if self.ok(rep):
                    self.placed += 1   # a plate without the room shape: removed in teardown
            self.owner("room.paste", "paste the sealed room%s (light armor 5 x 5 x 5 around a 3 x 3 x 3 interior, one window block in "
                       "the north wall, an Air Vent inside: SE builds no airtight room without one; a battery, an Interior Light on "
                       "the ceiling row 2.5 m from the east inner wall, three spotlights, a door inside) with block (0, 0, 0) at SN "
                       "(%g, %g, %g)" % (" blueprint '%s'" % room if room else "", at[0], at[1], at[2]), 90.0)
            self.room_placed = True

        def ensure_ship(self):
            if self.ship is not None:
                return self.ship
            ids = getattr(self.a, "ship_ids", None)
            if ids:
                self.ship = json.loads(ids)
                return self.ship
            ship = getattr(self.a, "ship", None)
            self.owner("ship.paste", "paste the test ship%s (4 hydrogen and 4 ion thrusters, a gatling gun, a ship welder, grinder and "
                       "drill, a target plate in front of the gun) at SN (%g, %g, %g)" % (" blueprint '%s'" % ship if ship else "",
                                                                                         SHIP_AT[0], SHIP_AT[1], SHIP_AT[2]), 120.0)
            rep = self.act("se", "fx", label="ship.status")
            s = {}
            if isinstance(rep, dict):
                s = {"grid": rep.get("thrusterGrid") or rep.get("ship"), "gun": rep.get("gun"), "tools": rep.get("tools") or {}}
            self.ship = s
            self.sess.event("ship", ids=s)
            return s

        # ---- parts ------------------------------------------------------------------------------------------------
        def p_n1(self):
            """S7.N1 (and S7.N8's sky rows): the de.s3 room fixture (Air Vent, battery, door, lights) at --room-at; the
 spectator inside 10 s and 2 m outside 10 s, twice (each window starts after the teleport); the door toggled 5
 times with the camera inside (de.interior {action: door}: the gas system recomputes, S7.N1.flicker, S7.M10);
 Subnautica de.interior {patch: on|off} A/B with de.pixel probes (a) the wall beside the window, (b) 20 m beyond
 the window and a capture each; a wall block removed and re-placed, an interior block added and removed"""
            self.part("n1", "begin")
            self.ensure_room()
            g = self.geo
            for k in (1, 2):
                for name, xyz in (("n1.inside.%d" % k, g["in"]), ("n1.outside.%d" % k, g["out"])):
                    self.cam(xyz, 0.0, name)
                    self.part(name, "begin")   # review F7: the window starts after the teleport (no row of the previous state)
                    self.act("se", "interior", label="%s.se.interior" % name)
                    self.act("sn", "interior", label="%s.sn.interior" % name)
                    self.wait(self.scale(10.0, 2.0))
                    self.part(name, "end")
            # review F8: the door toggled with the camera inside: each toggle makes the gas system recompute (the room stays
 # sealed), the hold keeps the flag (S7.N1.flicker), S7.M10's GetRooms flag flickers
            door = (self.room or {}).get("door")
            self.cam(g["in"], 0.0, "n1.door.cam")
            self.part("n1.door", "begin")
            if door:
                for i in range(DOOR_TOGGLES):
                    self.act("se", "door", door, i % 2 == 0, label="n1.door.%d" % (i + 1))
                    self.wait(self.scale(2.5, 1.0))
                self.act("se", "door", door, False, label="n1.door.close")
            else:
                self.owner("n1.door", "open and close the door inside the room 5 times (2 s apart), camera inside", 20.0)
            self.wait(self.scale(2.0, 1.0))
            self.act("se", "interior", label="n1.door.se.interior")
            self.part("n1.door", "end")
            self.cam(g["fogCam"], 0.0, "n1.fog.cam")
            for on in (True, False):
                name = "n1.fog.%s" % ("on" if on else "off")
                self.part(name, "begin")
                self.act("sn", "patch", on, label="%s.patch" % name)
                self.wait(self.scale(2.0, 1.0))
                self.act("sn", "pixel", g["fogA"], "%s.a" % name, label="%s.pixel.a" % name)
                self.act("sn", "pixel", g["fogB"], "%s.b" % name, label="%s.pixel.b" % name)
                self.capture(name)
                self.part(name, "end")
            self.act("sn", "patch", True, label="n1.fog.restore")
            self.cam(g["in"], 0.0, "n1.breach.cam")
            for name, prompt in (("n1.breach.remove", "remove ONE wall block of the room (grind it or Ctrl+right-click), keep the camera inside"),
                                 ("n1.breach.replace", "place the wall block back so the room is airtight again"),
                                 ("n1.interior.add", "place one block inside the room (on the floor)"),
                                 ("n1.interior.remove", "remove that interior block again")):
                self.part(name, "begin")
                if not self.owner(name, prompt, 20.0):
                    self.wait(self.scale(2.0, 1.0))
                self.act("se", "interior", label="%s.se.interior" % name)
                self.wait(self.scale(5.0, 1.0))
                self.part(name, "end")
            self.part("n1", "end")

        def p_n1b(self):
            """S7.N1b: the same room on the pressurisation-off copy "DE Lab NoPress": the camera inside 10 s"""
            self.part("n1b", "begin")
            self.ensure_room()
            self.cam(self.geo["in"], 0.0, "n1b.inside")
            self.part("n1b.inside", "begin")   # review F7: after the teleport
            self.act("se", "interior", label="n1b.se.interior")
            self.wait(self.scale(10.0, 2.0))
            self.part("n1b.inside", "end")
            self.part("n1b", "end")

        def p_n1c(self):
            """S7.N1c: the drained house. A dry-land reference walk on the S7a deck, then the character walks into
 the room (30 s); swim mode and speed from s7a.loco, characterInRoom from s7.interior"""
            self.part("n1c", "begin")
            self.ensure_deck()
            self.part("n1c.dry", "begin")
            self.call("se", "de.swim", {"teleportChar": [DECK[0], DECK[1] + 1.6, DECK[2] - 6.0], "yawDeg": 0.0}, label="n1c.dry.char")
            rep = self.call("se", "de.swim", {"script": [{"label": "n1c.dry.walk", "seconds": self.scale(6.0, 2.0), "forward": 0.3,
                                                          "strafe": 0.0, "vertical": 0.0}], "label": "n1c.dry"}, label="n1c.dry.script")
            if not self.ok(rep):
                self.owner("n1c.dry", "walk across the deck for 6 s (not running)", 8.0)
            else:
                self.wait(self.scale(6.0, 2.0))
            self.part("n1c.dry", "end")
            self.ensure_room()
            self.call("se", "de.swim", {"teleportChar": list(self.geo["char"]), "yawDeg": 0.0}, label="n1c.char")
            self.part("n1c.walk", "begin")
            if not self.owner("n1c.walk", "walk around inside the drained room for 30 s (walk, not run), third person", 30.0):
                self.wait(self.scale(2.0, 1.0))
            self.part("n1c.walk", "end")
            self.owner("n1c.look", "judge: a dry interior with the water seen through the window?", 10.0)
            self.part("n1c", "end")

        def ensure_deck(self):
            if getattr(self, "_deck", False):
                return
            rep = self.act("se", "place", DECK, "LargeBlockArmorBlock", DECK_PLATE, 0.0, "s7.deck", label="deck.place", timeout=20.0)
            if self.ok(rep):
                self.placed += 1
                if getattr(self, "room_by_s3", False):
                    self.room_placed = False   # de.s3 closes its previous grid: the room it built is gone (a pasted one stays)
                self._deck = True              # S7-1 fix: only a placed deck counts (a refused place is tried again)

        def p_n2(self):
            """S7.N2: Subnautica de.interior {action: trace, seconds: 10} with the camera inside the room, then outside"""
            self.part("n2", "begin")
            self.ensure_room()
            for window, xyz in (("inside", self.geo["in"]), ("outside", self.geo["out"])):
                name = "n2.%s" % window
                self.cam(xyz, 0.0, name)
                self.part(name, "begin")   # review F7/F4: the analyzer picks the s7.inside rows by these windows
                self.act("sn", "trace", self.scale(10.0, 3.0), label="%s.trace" % name)
                self.wait(self.scale(11.0, 3.5))
                self.part(name, "end")
            self.part("n2", "end")

        def p_n3(self):
            """S7.N3: the room fixture's Interior Light (default settings) about 2.5 m from the east inner wall and its three
 spotlights (shadows on: S7.M18), a Seamoth (de.spawn Seamoth 1 3) with its cabin light at that distance from a
 like wall, night or deep; de.pixel probes, a capture of each wall from its own camera (the
 Seamoth's looking west), de.lights"""
            self.part("n3", "begin")
            self.ensure_room()
            g = self.geo
            if not (self.room or {}).get("light"):
                self.owner("n3.light", "place an Interior Light (default settings) 2.5 m from the room's east inner wall at the ceiling "
                           "row, facing it, and three spotlights (shadows on)", 30.0)
            self.act("sn", "spawn", "Seamoth", 1, 3.0, label="n3.seamoth.spawn")
            self.owner("n3.seamoth", "put the Seamoth's cabin light 2.5 m from a light-armor wall like the room's; night or >= 200 m deep", 45.0)
            self.cam(g["lightCam"], 90.0, "n3.cam")
            self.act("sn", "pixel", g["lightAt"], "n3.se", label="n3.pixel.se")
            self.capture("n3.lights")
            for _ in range(int(self.scale(5.0, 2.0))):
                self.act("se", "lights", label="n3.se.lights")
                self.act("sn", "lights", label="n3.sn.lights")
                self.wait(1.0)
            # S7-1 fix: the Seamoth's wall point from its own camera (it was behind the n3 camera: depth -17.5)
            self.cam(g["seamothCam"], 270.0, "n3.seamoth.cam")
            self.act("sn", "pixel", g["seamothWall"], "n3.seamoth", label="n3.pixel.seamoth")
            self.capture("n3.seamoth")
            self.part("n3", "end")

        def p_n4(self):
            """S7.N4: the test ship at SN (0, -20, 80): thrust 0/0.25/0.5/0.75/1.0 3 s each, 20 x ShootOnce, ship tools
 5 s each, injected explosions r 2/10/30 m, grid drops, the room breached, stress 500, the
 thrusters at SN y +2 m; project: hand tools under water"""
            self.part("n4", "begin")
            s = self.ensure_ship()
            grid, gun, tools = s.get("grid"), s.get("gun"), s.get("tools") or {}
            self.cam((SHIP_AT[0] - 15.0, SHIP_AT[1], SHIP_AT[2]), 90.0, "n4.cam")
            self.act("sn", "sources", label="n4.sources")
            # review F18d: SE's sustain rows (s7.fx.event with sustain) during the thrust steps: S7.N4.thruster.rate and.rpm fit
 # Subnautica's emission and rpm against SE's own CurrentStrength x size, not against Subnautica's received values
            self.act("se", "fxtrace", len(THRUST_LEVELS) * 3.0 + 15.0, label="n4.se.fxtrace")
            for lvl in THRUST_LEVELS:
                name = "n4.thrust.%g" % lvl
                self.part(name, "begin")
                if grid is not None:
                    self.act("se", "thrust", grid, lvl, label="%s.override" % name)
                else:
                    self.owner(name, "set the ship's thrust override to %d %% on every thruster" % int(lvl * 100), 3.0)
                self.wait(self.scale(3.0, 1.0))
                self.part(name, "end")
            if grid is not None:
                self.act("se", "thrust", grid, 0.0, label="n4.thrust.off")
            self.part("n4.gun", "begin")
            shots = 20
            if gun is not None:
                self.act("se", "gun", gun, shots, label="n4.gun")
            else:
                self.owner("n4.gun", "fire the ship's gatling gun 20 single shots at the target plate", 20.0)
            # review F11: the window holds every shot (one per 10 ticks) plus 1.5 s for the last impacts and rows
            self.wait(self.scale(gun_wait_s(shots), 2.0))
            self.part("n4.gun", "end")
            for tname in ("Welder", "Grinder", "PrawnDrill"):
                name = "n4.tool.%s" % tname
                ent = tools.get(tname) or tools.get(tname.lower())
                self.part(name, "begin")
                if ent is not None:
                    self.act("se", "tool", ent, True, label="%s.on" % name)
                else:
                    self.owner(name, "switch the ship %s on for 5 s" % tname.replace("PrawnDrill", "drill").lower(), 5.0)
                self.wait(self.scale(5.0, 2.0))
                self.part(name, "end")
                if ent is not None:
                    self.act("se", "tool", ent, False, label="%s.off" % name)
                self.wait(self.scale(1.5, 1.0))
            for r in EXPLOSION_RADII:
                name = "n4.explosion.%g" % r
                self.part(name, "begin")
                self.act("se", "inject", "Explosion", (SHIP_AT[0] + 10.0, SHIP_AT[1], SHIP_AT[2] + 20.0), {"radius": r}, label=name)
                self.wait(self.scale(3.0, 1.0))
                self.part(name, "end")
            for name, prompt in (("n4.drop.ship", "drop a small grid onto the ship from +10 m"),
                                 ("n4.drop.sea", "drop a small grid into the sea from +10 m above the surface"),
                                 ("n4.breach", "breach the N1 room under water (grind one wall block)")):
                self.part(name, "begin")
                if not self.owner(name, prompt, 30.0):
                    self.wait(self.scale(1.0, 0.5))
                self.wait(self.scale(3.0, 1.0))
                self.part(name, "end")
            # review F10: Subnautica capped to 30 fps (it reads every other SE tick), the 500 events once in one tick (the
 # rates and caps of S7.N4.rates) and once spread over 8 ticks (the 8-tick window must hold them: S7.N4.loss;
 # S7.M17's 1-tick window then loses rows for certain); the cap cleared at the end and in teardown
            self.act("sn", "fps", LOSS_FPS, label="n4.stress.fps")
            self.wait(self.scale(1.0, 0.5))
            self.part("n4.stress", "begin")
            self.act("se", "stress", STRESS_N, label="n4.stress.tick")
            self.wait(self.scale(1.0, 0.5))
            self.act("se", "burst", STRESS_N, STRESS_BURST_TICKS, label="n4.stress.burst")
            self.wait(self.scale(2.0, 1.0))
            self.part("n4.stress", "end")
            self.act("sn", "fps", 0, label="n4.stress.fps.off")
            self.part("n4.above", "begin")
            self.cam((ABOVE_AT[0] - 15.0, ABOVE_AT[1] + 3.0, ABOVE_AT[2]), 90.0, "n4.above.cam")
            if grid is not None:
                self.owner("n4.above.move", "move the test ship up so its thrusters are at SN y +2 m (or skip: the injected thruster stands in)", 30.0)
                self.act("se", "thrust", grid, 1.0, label="n4.above.override")
            self.act("se", "inject", "Thruster", ABOVE_AT, {"intensity": 1.0, "seconds": 3.0}, label="n4.above.inject")
            self.wait(self.scale(3.0, 1.0))
            if grid is not None:
                self.act("se", "thrust", grid, 0.0, label="n4.above.off")
            self.part("n4.above", "end")
            for kind, prompt in (("rifle", "fire the hand rifle under water for 5 s"), ("welder", "use the hand welder on a block under water for 5 s"),
                                 ("grinder", "use the hand grinder on a block under water for 5 s"), ("drill", "drill the seabed for 5 s"),
                                 ("warhead", "optional: detonate a warhead under water away from everything")):
                name = "n4.hand.%s" % kind
                self.part(name, "begin")
                self.owner(name, prompt, 10.0)
                self.part(name, "end")
            self.cam((SHIP_AT[0] - 15.0, SHIP_AT[1], SHIP_AT[2]), 90.0, "n4.capture.cam")
            self.capture("n4.capture.ship")
            self.act("se", "fx", label="n4.se.fx")
            self.act("sn", "fx", label="n4.sn.fx")
            self.part("n4", "end")

        def p_n5(self):
            """S7.N5: three explorable wrecks (never the Aurora), the camera 12 m above each and the SE character beside it
 (the statics host's anchor), 200 rays each from Subnautica cast by both games; a small grid against a wreck 1 km
 away rests on the census host"""
            self.part("n5", "begin")
            sites = dict(N5_SITES)
            if getattr(self.a, "n5_sites", None):
                for tok in self.a.n5_sites.split(";"):
                    if ":" in tok:
                        k, v = tok.split(":", 1)
                        sites[k.strip()] = tuple(float(x) for x in v.split(","))
            for site, xyz in sites.items():
                name = "n5.%s" % site
                if "aurora" in site.lower():   # project decision: the Aurora is hidden and never a site
                    self.log.say("  S7 n5 %s: skipped (the Aurora is not a site: owner decision 2026-10-17)" % site)
                    continue
                self.part(name, "begin")
                # S7-1 fix: SE raises statics hosts around anchors (mirrored grids, online characters) only: the character
 # goes with the camera (S7-1 cast every site's rays with the character 1 km away: no host anywhere)
                self.call("se", "de.swim", {"teleportChar": [xyz[0] + 2.0, xyz[1], xyz[2] + 2.0], "yawDeg": 0.0}, label="%s.char" % name)
                self.cam(xyz, 0.0, name)
                self.wait(self.scale(N5_SETTLE_S, 3.0))
                rep = self.act("sn", "rays", site, 200, label="%s.sn.rays" % name, timeout=20.0)
                rays = rep.get("rays") if isinstance(rep, dict) else None
                if rays:   # the statics build pass SE half casts exactly the rays Subnautica's reply carries
                    self.act("se", "rays", site, {"rays": rays}, label="%s.se.rays" % name, timeout=30.0)
                else:
                    self.log.say("  S7 n5 %s: Subnautica's de.statics rays reply carried no rays (%s)" % (site, json.dumps(rep)[:120] if rep else "no reply"))
                self.part(name, "end")
            self.part("n5.census", "begin")
            self.owner("n5.census", "place a small grid against the wreck at SN (%g, %g, %g); the camera is moved 1 km away" % CENSUS_GRID_AT, 60.0)
            self.cam((CENSUS_GRID_AT[0] - 1000.0, CENSUS_GRID_AT[1], CENSUS_GRID_AT[2]), 0.0, "n5.census.cam")
            self.wait(self.scale(5.0, 2.0))
            grid = getattr(self.a, "census_grid", None)
            if grid:
                self.act("se", "probe", grid, 60.0, label="n5.census.se.probe", timeout=20.0)
            else:
                self.owner("n5.census.id", "no --census-grid id: read the grid's entity id (de.s5 status) for the probe", 5.0)
            self.act("se", "hosts", label="n5.census.hosts")
            self.part("n5.census", "end")
            self.part("n5", "end")

        def p_n6(self):
            """S7.N6: the S7a deck, Subnautica walk source sebones, third person, a scripted walk with de.bones rows for 60
 frames on both sides; then the female model and the same"""
            self.part("n6", "begin")
            self.ensure_deck()
            self.act("sn", "walk", "sebones", label="n6.walk.sebones")
            self.owner("n6.camera", "switch SE to THIRD person (V) and keep it", 10.0)
            for model in ("male", "female"):
                if model == "female" and not self.owner("n6.female", "switch the character to the FEMALE model (character "
                                                                       "customisation), then come back to the deck", 60.0):
                    continue
                name = "n6.walk" if model == "male" else "n6.walk.female"
                self.call("se", "de.swim", {"teleportChar": [DECK[0], DECK[1] + 1.6, DECK[2] - 6.0], "yawDeg": 0.0}, label="%s.char" % name)
                self.wait(self.scale(3.0, 1.0))
                self.part(name, "begin")
                self.call("se", "de.swim", {"script": [{"label": name, "seconds": 3.0, "forward": 0.3, "strafe": 0.0, "vertical": 0.0}],
                                            "label": name}, label="%s.script" % name)
                self.wait(1.0)          # the 0.25 s source blend settles
                self.act("se", "bones", 60, label="%s.se.bones" % name)
                self.act("sn", "bones", 60, label="%s.sn.bones" % name)
                self.wait(2.0)
                self.act("se", "bonestatus", label="%s.se.status" % name)
                self.act("sn", "bonestatus", label="%s.sn.status" % name)
                self.part(name, "end")
            self.act("sn", "walk", "sebones", label="n6.walk.default")   # the S7a default since
            self.part("n6", "end")

        def p_n7(self):
            """S7.N7 (offline): tools/sn-octrees/sn_prefab_colliders.py --census --out <out>/s7-census.json (its verdict in s7-census.txt)"""
            self.part("n7", "begin")
            run_census_tool(self.LCH, self.log, self.a, self.out, self.sess)
            self.part("n7", "end")

        def p_n8(self):
            """S7.N8: inside and outside the room with Subnautica de.interior status (the s7.sky rows)"""
            self.part("n8", "begin")
            self.ensure_room()
            for name, xyz in (("n8.inside", self.geo["in"]), ("n8.outside", self.geo["out"])):
                self.cam(xyz, 0.0, name)
                self.part(name, "begin")   # review F7: after the teleport
                self.act("sn", "interior", label="%s.sn.interior" % name)
                self.wait(self.scale(3.0, 1.0))
                self.part(name, "end")
            self.part("n8", "end")

        def p_n9(self):
            """S7.N9: a reaper spawned 60 m from the camera (kinematic beyond 48 m), the S6 lab ship with proxies in the
 scan radius; Subnautica de.statics status"""
            self.part("n9", "begin")
            self.cam(N9_AT, 0.0, "n9.cam")
            self.act("sn", "spawn", "ReaperLeviathan", 1, 60.0, label="n9.reaper")
            self.owner("n9.ship", "make sure the S6 lab ship (de.s6 local or the owner's hull) is within 300 m of the camera", 20.0)
            for _ in range(int(self.scale(10.0, 3.0))):
                self.act("sn", "statics", label="n9.sn.statics")
                self.wait(1.0)
            self.part("n9", "end")

        def p_n10(self):
            """S7.N10 (throwaway copy "DE Lab S7Save"): hosts alive, the project saves, the copy scanned read-only"""
            self.part("n10", "begin")
            self.wait(self.scale(10.0, 3.0))
            rep = self.act("se", "hosts", label="n10.hosts")
            hosts = rep.get("hosts") if isinstance(rep, dict) else None
            self.owner("n10.save", "save the game now (Esc -> Save); this is the throwaway copy", 45.0)
            self.wait(self.scale(10.0, 3.0))
            save_check(self.LCH, self.log, self.a, self.sess, hosts)
            self.part("n10", "end")

        def p_n11(self):
            """S7.N11: a descent +5 -> -30 m beside the test ship at 50 % thrust (two injected GridImpacts under water on the
 way down), then into the dry room; de.s7audio status"""
            self.part("n11", "begin")
            s = self.ensure_ship()
            grid = s.get("grid")
            if grid is not None:
                self.act("se", "thrust", grid, 0.5, label="n11.thrust")
            x, z = SHIP_AT[0] - 15.0, SHIP_AT[2]
            self.cam((x, N11_TOP, z), 90.0, "n11.top")
            self.part("n11.above", "begin")
            self.act("se", "audio", label="n11.above.audio")
            self.wait(self.scale(3.0, 1.0))
            self.part("n11.above", "end")
            self.part("n11.under", "begin")
            secs = (N11_TOP - N11_BOTTOM) / 2.0
            self.motion({"kind": "line", "fromSn": [x, N11_TOP, z], "toSn": [x, N11_BOTTOM, z], "speedMps": 2.0}, "n11.descent")
            end = time.monotonic() + self.scale(secs + 1.0, 4.0)
            # S7-1 fix: n11's only event sound was the engine loop, started before the descent (the camera still above water)
 # and then looping: no start under water for S7.N11.bus. Two one-shots with a sound (GridImpact: HitMedium/Fast)
 # are injected 4 m beside the camera once it is about 10 m and 20 m deep
            inject_at = {int(secs * 0.45), int(secs * 0.75)}
            k = 0
            while time.monotonic() < end and self.alive():
                self.act("se", "audio", label="n11.under.audio")
                if k in inject_at:
                    y = N11_TOP - 2.0 * k - 1.0
                    self.act("se", "inject", "GridImpact", (x + 4.0, y, z), {"intensity": 0.8}, label="n11.under.inject")
                k += 1
                self.wait(1.0)
            self.part("n11.under", "end")
            if grid is not None:
                self.act("se", "thrust", grid, 0.0, label="n11.thrust.off")
            self.ensure_room()
            self.cam(self.geo["in"], 0.0, "n11.room")
            self.part("n11.room", "begin")
            self.act("se", "audio", label="n11.room.audio")
            self.wait(self.scale(5.0, 2.0))
            self.part("n11.room", "end")
            self.owner("n11.ear", "listen: SE muffled under water, Subnautica's event sounds, the interior ambience in the room", 15.0)
            self.part("n11", "end")

        def p_wm5(self):
            """S7.WM-5: one SE day (1,200 s), SE and Subnautica de.s7 {action: sun} every 30 s"""
            self.part("wm5", "begin")
            self.cam(WM5_SPOT, 0.0, "wm5.spot")
            end = time.monotonic() + self.scale(WM5_SECONDS, 60.0)
            while time.monotonic() < end and self.alive():
                self.act("se", "sun", label="wm5.se.sun")
                self.act("sn", "sun", label="wm5.sn.sun")
                self.wait(max(0.0, min(self.scale(WM5_EVERY, 5.0), end - time.monotonic())))
            self.part("wm5", "end")

        def p_teardown(self):
            """placed grids removed (de.s3 remove), thrust overrides and tools off, Subnautica's interior patch on and census
 off, walk source sebones (the S7a default), the audio effect default, every mutant of the session off"""
            self.part("teardown", "begin")
            s = self.ship or {}
            if s.get("grid") is not None:
                self.act("se", "thrust", s["grid"], 0.0, label="teardown.thrust")
            for ent in (s.get("tools") or {}).values():
                self.act("se", "tool", ent, False, label="teardown.tool")
            for _ in range(self.placed):
                self.act("se", "remove", label="teardown.remove")
            self.act("sn", "patch", True, label="teardown.patch")
            self.act("sn", "census", False, label="teardown.census")
            self.act("sn", "walk", "sebones", label="teardown.walk")   # the S7a default since
            self.act("se", "effect", "default", label="teardown.effect")
            self.act("sn", "fps", 0, label="teardown.fps")             # review F10: the 30 fps cap never outlives the session
            for m in getattr(self, "mutants", []) or []:
                if m in WORLD_MUTANTS:
                    continue
                self.act("sn" if m in SN_MUTANTS else "se", "mutant", m, False, label="teardown.mutant.%s" % m)
            self.part("teardown", "end")

    return S7Runner

# ----------------------------------------------------------------------------------------------------------------
# offline helpers: the census tool (n7) and the save check (n10)
# ----------------------------------------------------------------------------------------------------------------

def census_tool_cmd(LCH, a, out_json):
    """The statics build pass census (tools/sn-octrees/sn_prefab_colliders.py --census --out PATH: every class id the
 BatchObjectsCache and the baked cells reference, its colliders, handled or skipped with a reason; prints the S7.N7
 verdict, exit code 0/1). --out keeps the session's document in its folder."""
    custom = getattr(a, "census_cmd", None)
    if custom:
        return json.loads(custom) + ["--out", out_json]
    tool = os.path.join(LCH.REPO, "tools", "sn-octrees", "sn_prefab_colliders.py")
    return ["uv", "run", "--no-project", "--with", "UnityPy", "python", "-I", tool, "--census", "--out", out_json]

def gun_wait_s(shots):
    """Review F11: n4.gun waits max(4, shots x 10 ticks / 60 + 1.5) s (ShootOnce one per 10 ticks, then the last impacts)."""
    return max(4.0, shots * 10.0 / 60.0 + 1.5)

def run_census_tool(LCH, log, a, out, sess):
    out_json = os.path.join(out, "s7-census.json")
    out_log = os.path.join(out, "s7-census.txt")
    cmd = census_tool_cmd(LCH, a, out_json)
    log.say("S7 n7 census tool: %s (output %s)" % (subprocess.list2cmdline(cmd), out_log), ev="s7_census_tool")
    rc = None
    try:
        os.makedirs(out, exist_ok=True)
        with open(out_log, "w", encoding="utf-8") as f:
            rc = subprocess.call(cmd, cwd=LCH.REPO, timeout=3600, stdout=f, stderr=subprocess.STDOUT)
    except (OSError, subprocess.TimeoutExpired) as e:
        log.say("S7 n7 census tool not run: %s" % e, ev="s7_census_failed")
    sess.event("census", path=out_json, log=out_log, rc=rc, cmd=cmd)
    return rc

def save_check(LCH, log, a, sess, hosts):
    """S7.N10: the world copy's files scanned read-only for the DE.Statics name and the host entity ids."""
    world = getattr(a, "world", None) or LAB_WORLD
    info = LCH.s4_world_info(world)
    folder = info.get("folder")
    ids = []
    for h in hosts or []:
        if isinstance(h, dict) and h.get("id") is not None:
            ids.append(str(h["id"]))
        elif isinstance(h, (int, str)):
            ids.append(str(h))
    files = hits = 0
    found = []
    if folder:
        for p in glob.glob(os.path.join(folder, "**", "*"), recursive=True):
            if not os.path.isfile(p) or not p.lower().endswith((".sbc", ".sbs", ".sbsb5", ".vx2", ".xml")):
                continue
            files += 1
            try:
                with open(p, "rb") as f:
                    data = f.read()
            except OSError:
                continue
            for needle in [b"DE.Statics"] + [i.encode("ascii") for i in ids]:
                if needle in data:
                    hits += 1
                    found.append("%s: %s" % (os.path.basename(p), needle.decode("ascii", "replace")))
    sess.event("savecheck", world=world, folder=folder, files=files, hits=hits, found=found[:20], ids=ids, hostsAlive=len(ids))
    log.say("S7 n10 save check of %r: %d files, %d hits %s" % (world, files, hits, found[:3]), ev="s7_savecheck")

# ----------------------------------------------------------------------------------------------------------------
# sessions
# ----------------------------------------------------------------------------------------------------------------

def env_plan(mutants, parts):
    """(Subnautica env, SE env) of a session."""
    sn = [m for m in mutants if m in SN_MUTANTS]
    se = [m for m in mutants if m in SE_MUTANTS]
    sn_env = {"DEEPENGINEERS_MUTANT": ",".join(sn)} if sn else {}
    se_env = {"DEEPENGINEERS_S5": "on", "DEEPENGINEERS_PICTURE": "site", "DEEPENGINEERS_TIMESYNC": "on", "DEEPENGINEERS_GHOST": "off",
              "DEEPENGINEERS_S6": "on" if "n9" in parts else "off", "DEEPENGINEERS_BAND": "%g,%g" % BAND}
    if "n9" in parts:
        sn_env["DEEPENGINEERS_S6"] = "on"
    if se:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(se)
    return sn_env, se_env

def resolve(a):
    """(parts, mutants, world, problems) from the arguments."""
    probs = []
    mutants = [m.strip() for m in (a.mutant or "").split(",") if m.strip()]
    bad = [m for m in mutants if m not in ALL_MUTANTS]
    if bad:
        probs.append("unknown mutants %s (known: %s)" % (bad, ",".join(ALL_MUTANTS)))
    if len(mutants) > 1:
        probs.append("one mutant per session (its rows are judged alone): %s" % ",".join(mutants))
    world = a.world
    if mutants and not bad:
        g = MUTANT_GROUPS[mutants[0]]
        parts = list(g["parts"])
        world = world or g.get("world")
    else:
        parts = list(DEFAULT_PARTS)
    if a.parts:
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
    unknown = [x for x in parts if x not in S7_PARTS]
    if unknown:
        probs.append("unknown parts %s (known: %s)" % (unknown, ",".join(S7_PARTS)))
    try:
        parse_room_at(getattr(a, "room_at", None))
    except ValueError as e:
        probs.append(str(e))
    if not world:
        # a part that runs on its own copy names the world; else the caller takes the world LastSession points at
        needs = sorted(set(PART_WORLD[p] for p in parts if p in PART_WORLD))
        world = needs[0] if len(needs) == 1 else None
        if len(needs) > 1:
            probs.append("parts %s need different world copies: one session each" % ",".join(p for p in parts if p in PART_WORLD))
    return parts, mutants, world, probs

def plan_lines(LCH, a, parts, mutants, world, ws, sn_env, se_env) -> list:
    R = _runner_class(LCH)
    out = ["  both games (%s, Legacy.exe -continue); character parked at SN (24, -8, 0) first (S5-STATUS 9.1)" % world,
           "  world %r: folder %s, LastSession %s%s, EnableOxygen %s, EnableOxygenPressurization %s, GameMode %s, marker %s" % (
               world, ws.get("folder"), ws.get("lastSession"), "" if ws.get("pointed") else " (NOT pointed at it)", ws.get("enableOxygen"),
               ws.get("enableOxygenPressurization"), ws.get("gameMode"), ws.get("marked")),
           "  Subnautica env %s; SE env %s" % (json.dumps(sn_env) or "-", json.dumps(se_env)),
           "  band %g / %g m (DEEPENGINEERS_BAND or the default)" % BAND,
           "  mutants: %s%s" % (",".join(mutants) or "none", (" -> red rows " + ",".join(
               r for m in mutants for r in analyzer(LCH).MUTANT_RED.get(m, ()))) if mutants else ""),
           "  owner steps: %s" % ("waited for (--owner)" if a.owner else "skipped (their rows may stay MISSING)")]
    g = room_geometry(parse_room_at(getattr(a, "room_at", None)) or ROOM_AT)
    for p in parts:
        doc = " ".join((getattr(R, "p_" + p).__doc__ or "").split())
        out.append("  part %-9s %s" % (p, doc))
        if p in ("n1", "n1b", "n1c", "n2", "n3", "n8", "n11"):
            out.append("      room block (0,0,0) at SN %s (%s), inside %s, outside %s, fog camera %s, probes %s / %s, light wall %s" % (
                g["at"], "blueprint %r" % a.room if a.room else "de.s3 room fixture (Air Vent, battery, light, 3 spots, door) or owner paste",
                g["in"], g["out"], g["fogCam"], g["fogA"], g["fogB"], g["lightAt"]))
        if p == "n4":
            out.append("      ship at SN %s (%s); thrust %s; explosions r %s; stress %d in one tick, then %d over %d ticks with "
                       "Subnautica at %d fps; gun window %.2f s" % (
                           SHIP_AT, "blueprint %r" % a.ship if a.ship else "owner paste", ",".join("%g" % x for x in THRUST_LEVELS),
                           ",".join("%g" % x for x in EXPLOSION_RADII), STRESS_N, STRESS_N, STRESS_BURST_TICKS, LOSS_FPS, gun_wait_s(20)))
        if p == "n5":
            out.append("      sites %s (explorable wrecks, camera 12 m above, the character beside it, %g s settle; --n5-sites "
                       "overrides; never the Aurora); census grid at %s" % (json.dumps(N5_SITES), N5_SETTLE_S, CENSUS_GRID_AT))
        if p == "n7":
            out.append("      %s" % subprocess.list2cmdline(census_tool_cmd(LCH, a, "<out>/s7-census.json")))
        if p == "wm5":
            out.append("      %g s, samples every %g s at SN %s" % (WM5_SECONDS, WM5_EVERY, WM5_SPOT))
    out.append("  SE actions: " + ", ".join("%s=%s" % (k, v[0]) for k, v in R.SE_ACTIONS.items()))
    out.append("  Subnautica actions: " + ", ".join("%s=%s" % (k, v[0]) for k, v in R.SN_ACTIONS.items()))
    return out

def s7_analyze(LCH, log, a, out) -> int:
    AN = analyzer(LCH)
    try:
        rows = AN.evaluate(out)
    except Exception as e:     # an analyser bug must not hide the session
        import traceback
        rows = [{"id": "S7", "status": "FAIL", "measured": "analyser error %s" % type(e).__name__, "threshold": "-",
                 "what": str(e)[:120] + " | " + traceback.format_exc().strip().splitlines()[-1][:120], "part": "all",
                 "required": True}]
    strict = not getattr(a, "lenient", False)
    ok = AN.write_results(out, rows, strict, {"tool": "launcher s7"})
    print("\nS7 ORACLE TABLE (%s)  (* = not required; INFO = information)" % out)
    try:
        print(AN.table(rows))
    except UnicodeEncodeError:
        print(AN.table(rows).encode("ascii", "replace").decode("ascii"))
    log.say("S7 ORACLE %s: %d rows -> %s" % ("PASS" if ok else "FAIL", len(rows), os.path.join(out, "s7-results.json")),
            ev="s7_result", ok=ok)
    return 0 if ok else 1

def census_session(LCH, log, a) -> int:
    """`s7 --census`: Subnautica with the fake producer at each batch centre."""
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s7", stamp + "-census")
    producer = os.path.join(LCH.REPO, "tests", "standins", "fake_se_producer.py")
    batches = getattr(a, "batches", None) or "all"
    se_cmd = [sys.executable, "-I", producer, "--s7-census", "--batches", batches]
    log.say("S7 census session %s: Subnautica + the fake producer (%s), census mode on, writes %s; out %s" % (
        stamp, subprocess.list2cmdline(se_cmd), CENSUS_DIR, out), ev="s7_census_plan")
    if a.dry_run:
        print("  Subnautica with the bridge token; SE stand-in %s" % subprocess.list2cmdline(se_cmd))
        print("  Subnautica de.statics {action: census on} once in the world, {action: census off} at the end")
        print("  cache %s (derived, never shipped); owner approval: an unattended session (about 2,975 stops of 3 s)" % CENSUS_DIR)
        log.say("dry run: nothing started")
        return 0
    os.makedirs(out, exist_ok=True)
    a.se_cmd = json.dumps(se_cmd)
    a.cont = False
    sess = LCH.S2SessionLog(os.path.join(out, "s7-session.jsonl"))
    sess.event("start", kind="s7-census", stamp=stamp, batches=batches, seCmd=se_cmd, band=list(BAND))
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            R = _runner_class(LCH)
            runner = R(LCH, log, a, sess, sn, se, out, stamp, False, True, t_start=t_start)
            ok, why = runner.attach()
            if ok:
                runner.act("sn", "census", True, label="census.on")
                while runner.alive():
                    runner.wait(5.0)
                    if se.poll() is not None:
                        break
                runner.act("sn", "census", False, label="census.off")
            else:
                log.say("S7 census: harness not usable: %s" % why, ev="s7_harness_refused")
                rc = 1
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if not a.keep_running:
            LCH.close_session(log, a, sn, se, True, False)
        sess.event("end", rc=rc)
        sess.close()
        LCH.collect(log, out, t_start, sn, False, True, stamp)
    return rc or 0

def s7(LCH, log, a) -> int:
    if a.list_mutant_groups:
        AN = analyzer(LCH)
        for m, g in MUTANT_GROUPS.items():
            side = "SN" if m in SN_MUTANTS else ("world" if m in WORLD_MUTANTS else "SE")
            print("  %-7s %-5s parts %-16s world %-18s red %s" % (m, side, ",".join(g["parts"]), g.get("world") or "-",
                                                                   ",".join(AN.MUTANT_RED.get(m, ()))))
        return 0
    if a.selftest:
        return selftest(LCH, log, a)
    if a.analyze:
        return s7_analyze(LCH, log, a, a.analyze)
    if a.census:
        return census_session(LCH, log, a)
    parts, mutants, world, probs = resolve(a)
    if probs:
        for p in probs:
            log.say("REFUSE s7: %s" % p)
        return 2
    standin = bool(a.sn_cmd or a.se_cmd)
    world = world or last_session_world(LCH) or LAB_WORLD   # -continue loads LastSession's world (DE Lab or a lab copy)
    ws = world_settings(LCH, world)
    sn_env, se_env = env_plan(mutants, parts)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s7", stamp + ("-" + mutants[0] if mutants else ""))
    a.cont = not a.no_continue
    online = [p for p in parts if p not in OFFLINE_PARTS and p != "teardown"]
    sn_cmd, se_cmd = LCH.session_commands(a)
    log.say("S7 session %s: parts %s%s%s; world %s; Subnautica: %s; SE: %s; out %s" % (
        stamp, ",".join(parts), " (quick)" if a.quick else "", " mutant %s" % mutants[0] if mutants else "", world,
        subprocess.list2cmdline(sn_cmd), subprocess.list2cmdline(se_cmd), out), ev="s7_plan")
    wprobs = world_problems(ws, parts, standin) if online else []
    if a.dry_run:
        for line in plan_lines(LCH, a, parts, mutants, world, ws, sn_env, se_env):
            print(line)
        for p in wprobs:
            print("  WOULD REFUSE: %s" % p)
        log.say("dry run: nothing started")
        return 0
    if wprobs:
        for p in wprobs:
            log.say("REFUSE s7: %s" % p)
        return 2
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "s7-session.jsonl"))
    sess.event("start", kind="s7", stamp=stamp, parts=parts, mutants=mutants, name=a.name, argv=sys.argv[1:], quick=a.quick, band=list(BAND),
               world=world, settings={"oxygen": ws.get("enableOxygen"), "pressurization": ws.get("enableOxygenPressurization"),
                                      "gameMode": ws.get("gameMode")},
               owner=a.owner, room=a.room, roomAt=list(parse_room_at(getattr(a, "room_at", None)) or ROOM_AT), ship=a.ship,
               standin={"sn": bool(a.sn_cmd), "se": bool(a.se_cmd)},
               snCmd=sn_cmd, seCmd=se_cmd, qpcFrequency=LCH.S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env)
    if not online:
        R = _runner_class(LCH)
        for p in parts:
            if p in OFFLINE_PARTS:
                sess.event("part", name=p, phase="begin", tick=0)
                run_census_tool(LCH, log, a, out, sess)
                sess.event("part", name=p, phase="end", tick=0)
        sess.event("end", rc=None)
        sess.close()
        return s7_analyze(LCH, log, a, out)
    LCH.S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            R = _runner_class(LCH)
            runner = R(LCH, log, a, sess, sn, se, out, stamp, bool(a.sn_cmd), bool(a.se_cmd), t_start=t_start)
            runner.mutants = mutants
            ok, why = runner.attach()
            if not ok:
                log.say("S7: harness not usable: %s" % why, ev="s7_harness_refused")
                rc = 1
            else:
                runner.park()
                for m in mutants:
                    if m not in WORLD_MUTANTS:
                        runner.act("sn" if m in SN_MUTANTS else "se", "mutant", m, True, label="mutant.%s" % m)
                for p in parts:
                    if not runner.alive():
                        log.say("S7: a game exited before part %s" % p, ev="s7_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:
                        log.say("S7 part %s failed: %r" % (p, e), ev="s7_part_error", part=p)
                        sess.event("part_error", name=p, error=repr(e))
                log.say("S7 script finished: %s" % json.dumps(runner.stats), ev="s7_script_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            LCH.close_session(log, a, sn, se, bool(a.se_cmd), bool(a.sn_cmd))
        LCH.S.fine_timer(False)
        sess.event("end", rc=rc, stats=runner.stats if runner else None,
                   unsupported=sorted("%s.%s" % k for k in runner.unsupported) if runner else None)
        sess.close()
        LCH.collect(log, out, t_start, sn, bool(a.sn_cmd), bool(a.se_cmd), stamp)
        cap = os.path.join(LCH.CAPTURES, "s7", stamp)
        if os.path.isdir(cap):
            shutil.copytree(cap, os.path.join(out, "captures", "s7", stamp), dirs_exist_ok=True)
    if rc is not None:
        log.say("S7 session aborted (rc %d); analysing what was collected" % rc)
    res = s7_analyze(LCH, log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------
# self-test: every part against a recording runner (no game, no section), the tables, the groups, the analyzer's
# ----------------------------------------------------------------------------------------------------------------

def selftest(LCH, log, a) -> int:
    ok = True
    R = _runner_class(LCH)
    AN = analyzer(LCH)

    class Rec(R):
        def __init__(self, a2, sess):   # noqa: D401 - no section, no samplers: calls are recorded
            self.LCH, self.L, self.S = LCH, LCH.L, LCH.S
            self.log, self.a, self.sess, self.out, self.stamp = log, a2, sess, "<out>", "selftest"
            self.q, self.sn, self.se, self.standin_sn, self.standin_se, self.se_alone = True, None, None, False, False, False
            self.stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "unknown": []}
            self.unsupported, self.bg = set(), []
            self.placed, self.cap_n, self.ship, self.room_placed = 0, 0, None, False
            self.room_by_s3, self.room, self.geo = False, {}, room_geometry(ROOM_AT)
            self.calls, self.mutants = [], []
            self.tick = 0

        def alive(self):
            return True

        def wait(self, seconds):
            self.tick += int(seconds * 60)

        def call(self, side, cmd, args=None, timeout=None, label=None):
            self.calls.append((side, cmd, args, label))
            if cmd == "de.fx" and (args or {}).get("action") == "status":
                return {"ok": True, "thrusterGrid": 77, "gun": 78, "tools": {"Welder": 79, "Grinder": 80, "PrawnDrill": 81}}
            if side == "sn" and cmd == "de.statics" and (args or {}).get("action") == "rays":
                return {"ok": True, "rays": [{"i": 0, "o": [0.0, -10.0, 0.0], "d": [0.0, -1.0, 0.0], "len": 40.0, "dSn": 6.5, "static": True}]}
            if cmd == "de.s3" and "place" in (args or {}):
                plate = dict((args["place"] or {}).get("plate") or {})
                rep = {"ok": True, "plate": plate}
                if plate.get("orientation") == "room":
                    rep["room"] = {"door": "901", "vent": "902", "battery": "903", "light": "904", "window": "905", "spots": ["906", "907", "908"]}
                return rep
            return {"ok": True}

        def part(self, name, phase):
            self.sess.event("part", name=name, phase=phase, tick=self.tick)

        def owner(self, step, prompt, seconds):
            self.sess.event("owner", step=step, phase="skipped", prompt=prompt)
            return False

        def park(self):
            pass

    class Mem:
        def __init__(self):
            self.rows = []

        def event(self, ev, **kw):
            self.rows.append(dict(kw, ev=ev))

    import argparse
    a2 = argparse.Namespace(owner=False, room=None, ship=None, ship_ids=None, n5_sites=None, census_grid=None, quick=True, census_cmd=json.dumps(
        [sys.executable, "-c", "import sys; sys.exit(0)"]), world="(selftest: no world)")   # the save check finds no folder
    sess = Mem()
    r = Rec(a2, sess)
    online = [p for p in S7_PARTS if p not in OFFLINE_PARTS]
    for p in online:
        getattr(r, "p_" + p)()
    begins = [x["name"] for x in sess.rows if x["ev"] == "part" and x["phase"] == "begin"]
    ends = [x["name"] for x in sess.rows if x["ev"] == "part" and x["phase"] == "end"]
    good = sorted(begins) == sorted(ends) and all(p in begins for p in online)
    print("  parts: %d windows, balanced %s, every part ran %s" % (len(begins), sorted(begins) == sorted(ends), all(p in begins for p in online)))
    ok &= good
    need = ["n1.inside.1", "n1.outside.1", "n1.inside.2", "n1.outside.2", "n1.door", "n1.fog.on", "n1.fog.off", "n1.breach.remove",
            "n1.breach.replace", "n1b.inside", "n1c.dry", "n1c.walk", "n2.inside", "n2.outside", "n4.gun", "n4.stress", "n4.above",
            "n4.drop.ship", "n4.drop.sea", "n4.breach", "n6.walk", "n11.above", "n11.under", "n11.room", "wm5"] + \
           ["n4.thrust.%g" % x for x in THRUST_LEVELS] + ["n4.explosion.%g" % x for x in EXPLOSION_RADII] + \
           ["n4.tool.%s" % t for t in ("Welder", "Grinder", "PrawnDrill")]
    miss = [n for n in need if n not in begins]
    print("  the analyzer's window names present: %s" % ("all" if not miss else "MISSING " + ",".join(miss)))
    ok &= not miss
    labels = [c[3] for c in r.calls]
    for lab in ("n1.fog.on.pixel.a", "n1.fog.off.pixel.b", "n4.gun", "n4.stress.tick", "n4.se.fxtrace", "n4.stress.burst", "n4.stress.fps", "teardown.fps",
                "n6.walk.se.bones", "n6.walk.sn.bones", "n3.pixel.se", "n3.pixel.seamoth", "n5.shallows.sn.rays", "n5.shallows.char",
                "n5.grassy.se.rays", "room.place", "n1.door.1", "n1.door.5", "n11.under.inject", "deck.place"):
        if lab not in labels:
            print("  missing call label %s" % lab)
            ok = False
    # S7-1 fixes: no Aurora site; the deck placed again for n6 after the room closed it; the fog probe (a) and the Seamoth
 # probe inside the camera's view; the n5 character next to each site's camera
    ok &= not any("aurora" in str(c[3]) for c in r.calls)
    decks = [i for i, c in enumerate(r.calls) if c[3] == "deck.place"]
    rooms = [i for i, c in enumerate(r.calls) if c[3] == "room.place"]
    n6walk = next((i for i, c in enumerate(r.calls) if c[3] == "n6.walk.script"), None)
    ok &= len(decks) >= 2 and n6walk is not None and max(i for i in decks + rooms if i < n6walk) in decks
    g0 = room_geometry(ROOM_AT)

    def off_axis_deg(cam, pt, yaw):
        import math as _m
        fx, fz = _m.sin(_m.radians(yaw)), _m.cos(_m.radians(yaw))
        dx, dz = pt[0] - cam[0], pt[2] - cam[2]
        depth = dx * fx + dz * fz
        side = dx * fz - dz * fx
        return _m.degrees(_m.atan2(abs(side), depth)) if depth > 0 else 180.0
    ok &= off_axis_deg(g0["fogCam"], g0["fogA"], 0.0) < 45.0 and off_axis_deg(g0["seamothCam"], g0["seamothWall"], 270.0) < 1.0
    print("  S7-1 fixes (no Aurora, deck re-placed before n6, probes in view, n5 character, n11 injections): %s" % ("PASS" if ok else "FAIL"))
    # review F1/F7/F8/F10: the room fixture asked for, the door toggled through de.interior, the windows after the teleports,
 # the burst over 8 ticks with Subnautica capped to 30 fps
    place = next((c for c in r.calls if c[3] == "room.place"), None)
    ok &= place is not None and ((place[2] or {}).get("place") or {}).get("plate", {}).get("orientation") == "room"
    doors = [c for c in r.calls if c[1] == "de.interior" and (c[2] or {}).get("action") == "door"]
    ok &= len(doors) == DOOR_TOGGLES + 1 and all((c[2] or {}).get("block") == "901" and str(c[3]).startswith("n1.door.") for c in doors)
    burst = next((c for c in r.calls if c[3] == "n4.stress.burst"), None)
    ok &= burst is not None and (burst[2] or {}).get("ticks") == STRESS_BURST_TICKS
    fps = [c for c in r.calls if c[3] in ("n4.stress.fps", "n4.stress.fps.off", "teardown.fps")]
    ok &= [(c[2] or {}).get("cap") for c in fps] == [float(LOSS_FPS), 0.0, 0.0]
    print("  room fixture, door toggles, burst and fps cap: %s" % ("PASS" if ok else "FAIL"))
    g2 = room_geometry((10.0, -20.0, 30.0))
    ok &= g2["in"] == (15.0, -15.0, 35.0) and g2["lightAt"] == (18.75, -12.5, 35.0) and g2["out"] == (15.0, -15.0, 26.75)
    ok &= parse_room_at("10,-20,30") == (10.0, -20.0, 30.0) and parse_room_at(None) is None
    try:
        parse_room_at("1,2")
        ok = False
    except ValueError:
        pass
    ok &= abs(gun_wait_s(20) - (20 * 10 / 60.0 + 1.5)) < 1e-9 and gun_wait_s(5) == 4.0
    print("  --room-at geometry and the gun window: %s" % ("PASS" if ok else "FAIL"))
    ok &= set(MUTANT_GROUPS) == set(AN.MUTANT_RED) == set(ALL_MUTANTS)
    print("  mutant groups = analyzer red sets = known mutants: %s" % (set(MUTANT_GROUPS) == set(AN.MUTANT_RED) == set(ALL_MUTANTS)))
    for m in ALL_MUTANTS:
        ns = argparse.Namespace(mutant=m, parts="", world=None)
        parts, mutants, world, probs = resolve(ns)
        want_world = MUTANT_GROUPS[m].get("world") or (PART_WORLD.get(parts[0]) if parts and parts[0] in PART_WORLD else None)
        if probs or world != want_world or parts != list(MUTANT_GROUPS[m]["parts"]):
            print("  %s resolves to %s %s %s" % (m, parts, world, probs))
            ok = False
    ns = argparse.Namespace(mutant="", parts="n1b,n10", world=None)
    ok &= bool(resolve(ns)[3])
    print("  n1b + n10 in one session refused: %s" % bool(resolve(ns)[3]))
    sn_env, se_env = env_plan(["S7.M4"], ["n6"])
    ok &= sn_env.get("DEEPENGINEERS_MUTANT") == "S7.M4" and se_env["DEEPENGINEERS_S6"] == "off" and se_env["DEEPENGINEERS_PICTURE"] == "site"
    sn_env, se_env = env_plan(["S7.M9"], ["n9"])
    ok &= se_env.get("DEEPENGINEERS_MUTANTS") == "S7.M9" and se_env["DEEPENGINEERS_S6"] == "on"
    ws = {"world": "DE Lab", "folder": "x", "pointed": True, "enableOxygen": False, "enableOxygenPressurization": True}
    ok &= any("EnableOxygen" in p for p in world_problems(ws, ["n1"], False))
    ok &= any("DE Lab S7Save" in p for p in world_problems(dict(ws, enableOxygen=True), ["n10"], False))
    print("  env plans and world checks: %s" % ("PASS" if ok else "FAIL"))
    rc = AN.selftest()
    ok &= rc == 0
    print("launcher s7 selftest %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

# ----------------------------------------------------------------------------------------------------------------
# argparse and dispatch (called by launcher.py)
# ----------------------------------------------------------------------------------------------------------------

def add_parsers(sub, LCH):
    p = sub.add_parser("s7", help="one scripted S7 session (both games): interiors, lights, effects, statics, skinned "
                                  "astronaut, audio; the S7 oracle table")
    p.add_argument("--parts", default="", help="comma list of %s (default: %s; with --mutant: its parts)" % (
        ",".join(S7_PARTS), ",".join(DEFAULT_PARTS)))
    p.add_argument("--mutant", default="", help="one seeded failure: %s" % ",".join(ALL_MUTANTS))
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--selftest", action="store_true", help="every part against a recording runner, the groups and the analyzer's selftest")
    p.add_argument("--census", action="store_true", help="the census session (Subnautica + the fake producer, unattended)")
    p.add_argument("--batches", default=None, help="--census: all | a file of batch ids")
    p.add_argument("--census-cmd", default=None, help="n7: JSON list replacing the census tool command (its --out PATH is appended)")
    p.add_argument("--world", default=None, help="the SE world LastSession points at (default %r; n1b %r, n10 %r, S7.M6 %r)" % (
        LAB_WORLD, PART_WORLD["n1b"], PART_WORLD["n10"], MUTANT_GROUPS["S7.M6"]["world"]))
    p.add_argument("--room", default=None, metavar="BLUEPRINT", help="the sealed room is this blueprint, pasted by the owner")
    p.add_argument("--room-at", default=None, metavar="X,Y,Z", help="SN point of the room fixture's block (0, 0, 0) (default %g,%g,%g; "
                                                                      "n1, n1b, n1c, n2, n3, n8, n11 and the room mutants)" % ROOM_AT)
    p.add_argument("--ship", default=None, metavar="BLUEPRINT", help="n4/n11: the test ship blueprint, pasted by the owner")
    p.add_argument("--ship-ids", default=None, help='n4: JSON {"grid": id, "gun": id, "tools": {"Welder": id, ...}} (else de.fx status)')
    p.add_argument("--census-grid", default=None, help="n5: the entity id of the small grid placed against the wreck (de.statics probe)")
    p.add_argument("--n5-sites", default=None, help="n5: name:x,y,z;name:x,y,z (SN; the camera 20 m from the structure)")
    p.add_argument("--owner", action="store_true", help="wait for the owner's steps (placements, drops, breach, hand tools, saves)")
    p.add_argument("--quick", action="store_true", help="short holds (smoke runs)")
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="print the plan (parts, steps, environment, world); start nothing")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in plumbing only)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (stand-in plumbing only)")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--out", default=None)
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S7 oracles on a collected folder")

def run(LCH, log, a) -> int:
    if a.cmd == "s7":
        return s7(LCH, log, a)
    return 2
