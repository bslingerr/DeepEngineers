"""S7 "Interiors, lights, thrusters + underwater effects, audio polish, statics into SE, skinned astronaut" oracle table:
joins the SE and Subnautica JSONL rows, the launcher's session log and the captures of one collected folder (tests/out/s7/<stamp>/
from `launcher.py s7`) into the rows of ROADMAP S7 (S7.N1-N11, S7.WM-5) and the seeded failures S7.M1-M20. Stdlib only.

 python -I tests/s7/analyze_s7.py DIR [--lenient] [--json]
 python -I tests/s7/analyze_s7.py --selftest # the S7 conventions twin (canonical text, hash, the C1 interior,
 # C2 extension, event row, RoomSet, C12 and light codecs, the bone
 # rows and mirror), then synthetic sessions: every row PASS, each
 # mutant red

S7 LOG CONTRACT (CONTRACT §5.4 "S7 conventions" rows; SE rows flat {"t", "ev",...}, Subnautica rows {"t", "event",
"ev", "data": {...}}; SE ticks: SE `tick`, Subnautica `c1Tick` / `seTick` / `presentedSeTick`). The contract's fields
are the base; the fields marked (+) are the detail an oracle needs beyond them, named here so the owners write them
(a row without them leaves its check MISSING, never FAIL):
 SE s7.interior tick, inRoom, processing, breached, roomGridId, roomIndex, roomO2, characterInRoom, candidates,
 settings {oxygen, pressurization}
 s7.rooms tick, entity, boxes, truncated, hash, sent
 s7.lights tick, found, sent, shadowed, owners, (+) maxDistM
 s7.fx tick, raised {type: n}, sent {type: n}, dropped {type: n}, window, stream, firstSeq, (+) why
 (periodic | drop | stress), (+) sustainRows {type: n}, (+) sustainKeys {type: n}, (+) ticks (SE ticks
 the row covers), (+) stepLagMaxTicks (largest ticks from an intensity step >= 0.05 to its row)
 s7.fx.event (+) tick, seq, stream, type, flags, entity, posSn [x, y, z] (SN-world through F), size, lifetimeMs
 (one row per accepted one-shot of the n4 windows: weapons, impacts, explosions, grid impacts, hull
 breaks, splashes; thrusters and tools excluded)
 s7.statics tick, batch, epoch, primitives, host, state created|closed|census|malformed|replaced, ms, entityId, save
 (S7.M8); state rays: n, hits, hostHits, bothHit, p95DiffM
 s7.statics.rays (+) site, rays [{id, distSe (null = no hit), host, census}] (fallback when no state-rays row)
 s7.bones tick, model, boneCount, modelBones, extra, world [[x, y, z]...] (SN-world through F), addBoneCount,
 bindBones (SkinnedExport)
 s7.audio tick, mixActive, inRoom, effect, emitters, muffled, modifiable, expectMuffle, patched (AudioPolish)
 s7a.sun tick, checks,... (S7a; WM-5) text DE.S7 bones <model> <n>..., DE.Interior OFF
 pressurization disabled, DE.Time REFUSE axis
 SN s7.interior snFrame, c1Tick, inside, patched, roomGridId, chunks {...}, skies {...}, clipProxies
 s7.inside window inside|outside, readers {type: calls}, ghost {o2, health, pos, (+) posErrM, (+) c1O2},
 canBeAttacked, music, brightness
 s7.sky entity, chunk, class Inside|Boundary|Exterior (or 2|1|0), applied interior|biome, ok, (+)
 cameraInRoom (C1 InRoom with roomGridId = the chunk's entity or root)
 s7.lights snFrame, rows, applied, shadowed, pool
 s7.fx snFrame, applied {type: n}, spawned {type: n}, failed {type: n}, missed, sustains, sources {kind:
 found|fallback|missing}, (+) soundsStarted
 s7.fx.thruster (+) snFrame, seTick, entity, key, intensity, size, emission, rpm, rpmRule
 s7.fx.event (+) snFrame, seTick, seq, stream, type, flags, entity, posSn [x, y, z], latencyFrames, scale,
 lifetimeMs, source
 s7.fx.sustain (+) snFrame, seTick, type, key, entity, state start|refresh|stop|fade
 s7.statics snFrame, op batch|pass|rays, batch, epoch, primitives, filtered {reason: n} (cumulative; reasons
 creature, freezeWhenFar, proxy,...), census, (+) creaturesReported, (+) insideProxy
 s7.statics.rays (+) site, rays [{id, distSn}] (fallback)
 s7.bones snFrame, presentedSeTick, seTick, alpha, model, boneCount, addBoneCount, bindBones, extraBones,
 source, countsAgree, world [[x, y, z]...], tear [{part, valid, verts, badSum, badIndex, maxIndex}]
 (SkinnedAstronaut)
 s7.audio snFrame, bus {level, target, c5Target, muted}, events {type: started}, refused, above, biome, depth,
 underwater, ambience {...} (AudioPolish)
 s7.probe (+) label, rgb [r, g, b] 0..1
 s7a.sun c6Tick, timePassed, isDay, c6ElevDeg, drive, step (S7a; WM-5) text DE.SnPixel label=... px=(x, y)
 session (s7-session.jsonl): start {kind s7, parts, mutants, band, world, settings {oxygen, pressurization}}, part
 {name, phase, tick}, reply {side, cmd, args, reply, label}, capture {name, side, path} (relative to captures/),
 project {step, phase}, census {path} (the n7 tool's JSON: classes, counted, allowListed, skipped, unlisted), the
 replies n5.<site>.se.rays (n, hits, hostHits, bothHit, p95DiffM) and n5.census.se.probe (hit, host, name),
 savecheck {world, files, hits, ids, hostsAlive}
Image checks: the interiors build pass tests/s7/s7_images.py is called by name when present (`probe_rgb(path, x, y,
radius)` -> [r, g, b] 0..1); else this file's PNG reader (8-bit RGB/RGBA, non-interlaced) averages a 9 x 9 patch.

ROWS (PASS, FAIL, MISSING, INFO): S7.N1.{flag,fog,breach,flicker,capture*}, S7.N1b.{flag,log}, S7.N1c.{walk,look*},
S7.N2.{readers,ghost,music*,brightness*}, S7.N3.{ratio,shadows,cap}, S7.N4.{thruster.rate,thruster.rpm,thruster.log,
weapon,impact,explosion,tools,gridimpact,hullbreak,splash,loss,rates,above,capture*}, S7.WM-5.{bind,isday,monotonic},
S7.N5.{rays,census}, S7.N6.{count,pos,tear}, S7.N7.census, S7.N8.sky, S7.N9.filter, S7.N10.save,
S7.N11.{muffle,bus,ambience*}, S7.project*, S7.Mx.red (* = information). Rows of
parts the session did not run are not listed. A row whose project step was skipped (OWNER_DEPS) reads MISSING with the
step's prompt, never FAIL (S7-1 fix). Exit code 0 when every required row passes (MISSING fails unless --lenient).
"""
from __future__ import annotations

import bisect
import glob
import json
import math
import os
import re
import struct
import sys
import tempfile
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tools", "worldmap")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import s2_common as C   # noqa: E402 (jsonl reader, normalise, rows, table, verdict)

TICKS_PER_S = 60
# S7.N1
N1_ENTRY_TICKS = 2
N1_SN_SHARE = 0.99
FOG_A_MIN, FOG_B_MAX = 0.05, 0.02
BREACH_TICKS, BREACH_SN_FRAMES, RESEAL_TICKS = 120, 3, 120
EDGE_GUARD_TICKS = 60
N1C_SHARE, N1C_SPEED = 0.95, 0.80
# S7.N3
LIGHT_RATIO_TOL, MAX_SHADOWED, MAX_LIGHTS, LIGHT_RADIUS_M = 0.25, 2, 64, 60.0
# S7.N4
R2_MIN, ZERO_AFTER_S, RPM_TOL, RPM_SHARE, SUSTAIN_REFRESH_TICKS = 0.9, 0.25, 0.05, 0.95, 15
LAT_P95_FRAMES, POS_ATTACHED_M, POS_IMPACT_M, POS_EXPLOSION_M, SCALE_TOL = 3, 0.05, 0.1, 0.5, 0.10
TOOL_SHARE, TOOL_GONE_S, GRIDIMPACT_S, POS_GRID_M, HULL_MIN_S, POS_SPLASH_M = 0.95, 0.6, 0.5, 0.5, 2.0, 0.5
STRESS_N, MAX_PER_TICK, WINDOW_TICKS = 500, 128, 8
EVENT_CAPS = {"Thruster": 64, "WeaponMuzzle": 16, "BulletStreak": 32, "Impact": 32, "Explosion": 8, "Welder": 8, "Grinder": 8,
              "Drill": 8, "Repair": 8, "PrawnDrill": 8, "GridImpact": 16, "HullBreak": 8, "Splash": 16}
EVENT_TYPES = ("", "Thruster", "WeaponMuzzle", "BulletStreak", "Impact", "Explosion", "Welder", "Grinder", "Drill", "Repair",
               "PrawnDrill", "GridImpact", "HullBreak", "Splash")
# S7.WM-5
WM5_MIN_SAMPLES, ISDAY_GUARD_DEG = 30, 0.3
# S7.N5
RAY_P95_M = 0.1
# S7.N6
BONES, CANON_BONES, BONE_POS_M, BONE_ALPHA_MIN, BONE_MIN_FRAMES, WEIGHT_TOL = 81, 78, 0.01, 0.999, 30, 1e-4

MUTANT_RED = {
    "S7.M1": ("S7.N1.fog", "S7.N2.readers", "S7.N2.ghost"),
    "S7.M2": ("S7.WM-5.monotonic",),
    "S7.M3": ("S7.N5.rays",),
    "S7.M4": ("S7.N6.pos",),
    "S7.M5": ("S7.N8.sky",),
    "S7.M6": ("S7.WM-5.bind",),
    "S7.M7": ("S7.N9.filter",),
    "S7.M8": ("S7.N10.save",),
    "S7.M9": ("S7.N6.count",),
    "S7.M10": ("S7.N1.flicker",),
    "S7.M11": ("S7.N4.thruster.rate",),
    "S7.M12": ("S7.N4.weapon",),
    "S7.M13": ("S7.N4.explosion",),
    "S7.M14": ("S7.N4.tools",),
    "S7.M15": ("S7.N4.weapon", "S7.N4.impact", "S7.N4.gridimpact"),
    "S7.M16": ("S7.N4.splash",),
    "S7.M17": ("S7.N4.loss",),
    "S7.M18": ("S7.N3.shadows",),
    "S7.M19": ("S7.N11.muffle",),
    "S7.M20": ("S7.N4.rates",),
}

# the 13 direct readers of Player.IsInsideWalkable and the 5 through Player.IsInside (ROADMAP S7, contract-s7)
READERS_DIRECT = ("AttachAndSuck", "BasePipeConnector", "ColorCorrection", "FMODGameParams", "FlyAwayWhenScared",
                  "LargeWorldStreamer", "Player", "PlayerSoundTrigger", "TemperatureDamage", "UnstuckPlayer",
                  "UseableDiveHatch", "WaterscapeVolumeOnCamera", "uGUI_AdaptiveBrightness")
READERS_INSIDE = ("ArmsController", "FootstepSounds", "GasPod", "UwePostProcessingManager", "WarpBall")
# review F4: the readers that run every frame in both windows (S7.N2.readers judges these; the others are INFO notCalled:
# AttachAndSuck, BasePipeConnector, FlyAwayWhenScared, PlayerSoundTrigger, UseableDiveHatch, GasPod and WarpBall run only
# in rare conditions, ArmsController only within 1 m of the water level); one of the brightness pair suffices
READERS_ALWAYS = ("WaterscapeVolumeOnCamera", "FMODGameParams", "LargeWorldStreamer")
READERS_BRIGHTNESS = ("uGUI_AdaptiveBrightness", "ColorCorrection")
# S7-1 fix: the trace named a nested caller by its own type: LargeWorldStreamer's every-frame call comes from
# LargeWorldStreamer.Settings.GetMaxFrameMs (decomp LargeWorldStreamer.cs:42, called at:838) and was logged as "Settings"
# (the SN trace now walks to the outermost type; rows of the deployed build still say "Settings")
READER_ALIASES = {"Settings": "LargeWorldStreamer", "LargeWorldStreamer+Settings": "LargeWorldStreamer",
                  "LargeWorldStreamer.Settings": "LargeWorldStreamer"}
# S7-1 fix: the project steps each row depends on; a row that would
# FAIL or is MISSING while one of its steps was skipped reads MISSING with the step's prompt (never FAIL)
OWNER_DEPS = {
    "S7.N1.breach": ("n1.breach.remove", "n1.breach.replace", "room.paste"),
    "S7.N1.flag": ("room.paste",), "S7.N1.fog": ("room.paste",), "S7.N1.flicker": ("room.paste", "n1.door"),
    "S7.N1c.walk": ("n1c.walk", "n1c.dry", "room.paste"),
    "S7.N2.readers": ("room.paste",), "S7.N2.ghost": ("room.paste",), "S7.N8.sky": ("room.paste",),
    "S7.N3.ratio": ("n3.seamoth", "n3.light", "room.paste"), "S7.N3.shadows": ("n3.light", "room.paste"),
    "S7.N4.thruster.rate": ("ship.paste", "n4.thrust."), "S7.N4.thruster.rpm": ("ship.paste", "n4.thrust."),
    "S7.N4.thruster.log": ("ship.paste", "n4.thrust."),
    "S7.N4.weapon": ("ship.paste", "n4.gun"), "S7.N4.impact": ("ship.paste", "n4.gun"),
    "S7.N4.tools": ("ship.paste", "n4.tool."),
    "S7.N4.gridimpact": ("n4.drop.ship",), "S7.N4.hullbreak": ("n4.breach",), "S7.N4.splash": ("n4.drop.sea",),
    "S7.N5.census": ("n5.census", "n5.census.id"),
    "S7.N10.save": ("n10.save",),
}
# review F6: S7.N5 fails when SE has no host along more than this share of the rays whose Subnautica hit is a static
RAY_SE_MISS_MAX = 0.05

# ----------------------------------------------------------------------------------------------------------------
# the S7 conventions twin (CONTRACT §5.4 "S7 conventions"; Wire S7Conventions, S7Interior, S7Events, S7Lights,
# S7Statics, S7Bones)
# ----------------------------------------------------------------------------------------------------------------
CANONICAL_TEXT = """ext s7 look 1
const BoneLimits AstronautBones 81
const BoneLimits MaxBones 160
const C12Ext Version 1
const C1Interior Version 1
const C1Interior Size 16
const C1Interior Offset 176
const C1InteriorFlags InRoom 1
const C1InteriorFlags Pressurization 2
const C1InteriorFlags Processing 4
const C1InteriorFlags Breached 8
const C1InteriorFlags CharacterInRoom 16
const C2Events WindowTicks 8
const C2Events RowSize 64
const C2Events MaxPerTick 128
const C2Events MaxRows 1024
const C2Events Offset 262144
const C2Ext Version 1
const C2Ext Offset 28
const C2Ext Size 36
const C2ExtFlags Lights 1
const C2ExtFlags Events 2
const C2LightFlagsExt Character 8
const C2LightFlagsExt Version1 268435456
const C2LightFlagsExt VersionMask 4026531840
const CharacterFlagsExt ExtraBones 4
const EventCaps Drill 8
const EventCaps Explosion 8
const EventCaps Grinder 8
const EventCaps HullBreak 8
const EventCaps PrawnDrill 8
const EventCaps Repair 8
const EventCaps Welder 8
const EventCaps GridImpact 16
const EventCaps Splash 16
const EventCaps WeaponMuzzle 16
const EventCaps BulletStreak 32
const EventCaps Impact 32
const EventCaps Thruster 64
const EventFlags Attached 1
const EventFlags Sustain 2
const EventFlags Stop 4
const EventFlags Underwater 8
const EventFlags Surface 16
const EventFlags Hit 32
const EventFlags Local 64
const EventLimits SustainRefreshTicks 15
const EventLimits SurfaceBandCm 50
const EventLimits SustainStepPermille 50
const EventLimits StreakMaxM 60
const EventLimits SplashMinSpeedCmS 200
const EventLimits EffectRadiusM 250
const EventLimits SustainLifetimeMs 500
const EventLimits ExplosionRadiusM 1000
const EventType Thruster 1
const EventType WeaponMuzzle 2
const EventType BulletStreak 3
const EventType Impact 4
const EventType Explosion 5
const EventType Welder 6
const EventType Grinder 7
const EventType Drill 8
const EventType Repair 9
const EventType PrawnDrill 10
const EventType GridImpact 11
const EventType HullBreak 12
const EventType Splash 13
const InteriorClass Exterior 0
const InteriorClass Boundary 1
const InteriorClass Inside 2
const LightLimits MaxShadowedSpots 2
const LightLimits RadiusM 60
const LightLimits MaxLights 64
const RoomLimits MaxBoxes 4096
const RoomSetFlags Truncated 1
const RoomSetFlags Version1 268435456
const StaticFlags Census 1
const StaticFlags AllowListed 2
const StaticFlags SkewApprox 4
const StaticsLimits BatchM 160
const StaticsLimits CreateRadiusM 300
const StaticsLimits ScanRadiusM 320
const StaticsLimits CloseRadiusM 350
const StaticsLimits MaxPerHost 1024
const StaticsLimits MinRescanMs 2000
const StaticsLimits MaxPerBatch 4096
const ThrusterKind Other 0
const ThrusterKind Ion 1
const ThrusterKind Atmospheric 2
const ThrusterKind Hydrogen 3
const ToolSource Hand 1
const ToolSource Ship 2
const WeaponKind Projectile 1
const WeaponKind Missile 2
field interior version 0xb0 1 u8
field interior flags 0xb1 1 u8
field interior roomO2 0xb2 1 u8
field interior pad 0xb3 1 u8
field interior roomIndex 0xb4 4 u32
field interior roomGridId 0xb8 8 u64
field c2ext version 0x1c 1 u8
field c2ext flags 0x1d 1 u8
field c2ext eventCount 0x1e 2 u16
field c2ext eventFirstSeq 0x20 4 u32
field c2ext eventStream 0x24 4 u32
field c2ext eventsDropped 0x28 4 u32
field c2ext lightsFound 0x2c 4 u32
field c2ext tickLow 0x30 4 u32
field c2ext pad 0x34 12 u8[12]
field light falloff 0x3c 4 f32
field event seq 0x0 4 u32
field event type 0x4 1 u8
field event flags 0x5 1 u8
field event lifetimeMs 0x6 2 u16
field event entityId 0x8 8 u64
field event key 0x10 8 u64
field event pos 0x18 12 f32[3]
field event dir 0x24 12 f32[3]
field event intensity 0x30 4 f32
field event size 0x34 4 f32
field event tickLow 0x38 4 u32
field event subtype 0x3c 2 u16
field event pad 0x3e 2 u16
field statics version 0x6 2 u16
field prim flags 0x1 1 u8
field roomset flags 0xc 4 u32
format audio.bus Subnautica event sounds only on Underwater rows while snBusTarget > 0; rows without Underwater draw lights only
format audio.muffle postfix MyEntity3DSoundEmitter.SelectEffect: LowPassNoHelmetNoOxy while seMixActive and C1 interior InRoom clear, sounds ModifiableByHelmetFilters only
format bones.order rows 0..77 canon1; rows 78..boneCount-1 the model's non-canonical bones in Model.Bones order; boneCount = Model.Bones.Length <= 160
format bones.unity Mirror * M^T * Mirror; extra rows posed flat under the Entity from their absolute matrices
format event.frame Attached: pos, dir in the SE-local frame of entityId; else SN-world (F, mapdir)
format event.key fnv1a64(utf8 thr|<fatKey x16>|<flame index>) | fnv1a64(utf8 tool|<fatKey x16>) | fnv1a64(utf8 tool|e<entity id>); one-shots 0
format event.priority over MaxPerTick: Stop rows, Explosion, HullBreak, GridImpact, Splash, Impact, WeaponMuzzle, Repair, Welder, Grinder, Drill, PrawnDrill, Thruster, BulletStreak; newest first within a type
format event.sustain a row on start, on |d intensity| >= SustainStepPermille/1000, on an attached move > 0.05 m, else every SustainRefreshTicks; lifetimeMs = SustainLifetimeMs; Stop row at the end; Subnautica fades a sustain whose lifetime ran out
format event.window rows = accepted events of ticks [tick-7, tick], seq consecutive from eventFirstSeq; eventStream changes when seq restarts; tickLow = low 32 bits of tick
format interior.class Inside: cell round(T/g) inside a room box; Boundary: Chebyshev distance 1 from a room box; else Exterior
format interior.flag GetOxygenRoomForCubeGridPosition(cell of the camera) else GetSafeOxygenBlock(camera).Room; IsAirtight; last value held while IsProcessingData
format interior.sky Inside: SetCustomSky(MarmoSkies.main.GetSky(BaseInterior)); Boundary: the same while C1 roomGridId = its entity, else SetSky(Auto) with dynamic; Exterior: SetSky(Auto)
format light.row ownerId frame as event.frame; a point row per LightOn, a Spot row per ReflectorOn; nearest first within RadiusM; cap MaxLights; Shadows on spot rows only
format roomset.box cells [min, max] of airtight room air cells, MyVoxelSegmentation Simple; Truncated: one box per room (its cell AABB)
format statics.axes staticAxes(q) = (mapdir^-1(q*x), mapdir^-1(q*y), mapdir^-1(q*(-z))), det +1; size_i *= sqrt((a_i.x^2+a_i.z^2)/s^2+a_i.y^2)
format statics.capsule size = (radius, halfSegment, axis 0|1|2); halfSegment = max(0, height/2 - radius); HkCapsuleShape(F^-1(c - a*halfSegment) - o, F^-1(c + a*halfSegment) - o, radius/s(c)); sphere radius/s(c)
format statics.host DE.Statics.<bx>.<by>.<bz>.e<epoch>[.p<k>]: MyEntity, no model, Save false, layer 13, HkStaticCompoundShape baked, origin F^-1(mean centre), identity rotation
"""
PINNED_HASH, CANONICAL_LINES, CANONICAL_BYTES = 0x1f9b9b4a876a97f9, 143, 6583

INTERIOR_FMT = "<BBBBIQ"          # C1 0xB0-0xBF: version, flags, roomO2, pad, roomIndex, roomGridId
C2EXT_FMT = "<BBHIIIII12s"        # SeEntitiesHeader 0x1C-0x3F
EVENT_FMT = "<IBBHQQ3f3fffIHH"    # the C2 event row (64 B at 0x40000 + i*64)
ROOMSET_FMT, ROOMBOX_FMT = "<QII", "<3i3i"
C12_HDR_FMT, C12_PRIM_FMT = "<hhhHII", "<BBH3f4f3fI"
INTERIOR_FLAGS = {"InRoom": 1, "Pressurization": 2, "Processing": 4, "Breached": 8, "CharacterInRoom": 16}
EVENT_FLAGS = {"Attached": 1, "Sustain": 2, "Stop": 4, "Underwater": 8, "Surface": 16, "Hit": 32, "Local": 64}
VERSION1 = 1 << 28

def fnv1a64(text):
    h = 0xcbf29ce484222325
    for b in text.encode("ascii"):
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def constants(text=CANONICAL_TEXT):
    """{group: {name: value}} of the canonical text's const lines."""
    out = {}
    for line in text.splitlines():
        p = line.split(" ")
        if len(p) == 4 and p[0] == "const":
            out.setdefault(p[1], {})[p[2]] = int(p[3])
    return out

def interior_pack(flags, room_o2, room_index, room_grid_id):
    return struct.pack(INTERIOR_FMT, 1, flags, room_o2, 0, room_index & 0xFFFFFFFF, room_grid_id & 0xFFFFFFFFFFFFFFFF)

def interior_unpack(b):
    v = struct.unpack(INTERIOR_FMT, b)
    return None if v[0] != 1 else {"flags": v[1], "roomO2": v[2], "roomIndex": v[4], "roomGridId": v[5],
                                   "inRoom": bool(v[1] & 1)}

def room_o2_byte(level):
    """roomO2 = OxygenLevel x 255, rounded half away from zero, clamped."""
    x = max(0.0, min(1.0, float(level))) * 255.0
    return int(math.floor(x + 0.5))

def c2ext_pack(flags, count, first_seq, stream, dropped, lights_found, tick):
    return struct.pack(C2EXT_FMT, 1, flags, count, first_seq, stream, dropped, lights_found, tick & 0xFFFFFFFF, b"\x00" * 12)

def c2ext_unpack(b, header_tick):
    v = struct.unpack(C2EXT_FMT, b)
    if v[0] != 1 or v[7] != (header_tick & 0xFFFFFFFF):
        return None     # absent, or stale (an earlier generation's extension)
    return {"flags": v[1], "eventCount": v[2], "eventFirstSeq": v[3], "eventStream": v[4], "eventsDropped": v[5],
            "lightsFound": v[6], "tickLow": v[7]}

def event_pack(seq, etype, flags, lifetime_ms, entity, key, pos, d, intensity, size, tick_low, subtype):
    return struct.pack(EVENT_FMT, seq, etype, flags, lifetime_ms, entity, key, pos[0], pos[1], pos[2], d[0], d[1], d[2],
                       intensity, size, tick_low & 0xFFFFFFFF, subtype, 0)

def event_unpack(b):
    v = struct.unpack(EVENT_FMT, b)
    return {"seq": v[0], "type": v[1], "flags": v[2], "lifetimeMs": v[3], "entityId": v[4], "key": v[5], "pos": v[6:9],
            "dir": v[9:12], "intensity": v[12], "size": v[13], "tickLow": v[14], "subtype": v[15], "pad": v[16]}

def event_key(kind, a, b=None):
    """format event.key: fnv1a64(utf8 thr|<fatKey x16>|<flame index>) | (tool|<fatKey x16>) | (tool|e<entity id>)."""
    if kind == "thr":
        return fnv1a64("thr|%016x|%d" % (a, b))
    if kind == "tool":
        return fnv1a64("tool|%016x" % a)
    return fnv1a64("tool|e%d" % a)

def roomset_pack(entity, boxes, truncated):
    flags = VERSION1 | (1 if truncated else 0)
    return struct.pack(ROOMSET_FMT, entity, len(boxes), flags) + b"".join(struct.pack(ROOMBOX_FMT, *b) for b in boxes)

def roomset_unpack(b):
    entity, count, flags = struct.unpack_from(ROOMSET_FMT, b, 0)
    if flags >> 28 != 1 or len(b) != 16 + 24 * count:
        return None
    boxes = [struct.unpack_from(ROOMBOX_FMT, b, 16 + 24 * i) for i in range(count)]
    return {"entity": entity, "truncated": bool(flags & 1), "boxes": boxes}

def interior_class(cell, boxes):
    """format interior.class: 2 Inside (in a box), 1 Boundary (Chebyshev distance 1), else 0 Exterior."""
    x, y, z = cell
    best = None
    for b in boxes:
        dx = max(b[0] - x, 0, x - b[3])
        dy = max(b[1] - y, 0, y - b[4])
        dz = max(b[2] - z, 0, z - b[5])
        d = max(dx, dy, dz)
        best = d if best is None else min(best, d)
    return 2 if best == 0 else (1 if best == 1 else 0)

def c12_pack(bx, by, bz, epoch, prims):
    out = struct.pack(C12_HDR_FMT, bx, by, bz, 1, epoch, len(prims))
    for p in prims:
        out += struct.pack(C12_PRIM_FMT, p["kind"], p["flags"], 0, *(list(p["c"]) + list(p["q"]) + list(p["size"]) + [p["hash"]]))
    return out

def c12_unpack(b):
    bx, by, bz, version, epoch, count = struct.unpack_from(C12_HDR_FMT, b, 0)
    if version != 1 or len(b) != 16 + 48 * count:
        return None
    prims = []
    for i in range(count):
        v = struct.unpack_from(C12_PRIM_FMT, b, 16 + 48 * i)
        prims.append({"kind": v[0], "flags": v[1], "c": v[3:6], "q": v[6:10], "size": v[10:13], "hash": v[13]})
    return {"batch": (bx, by, bz), "epoch": epoch, "prims": prims}

def host_name(bx, by, bz, epoch, part=None):
    return "DE.Statics.%d.%d.%d.e%d%s" % (bx, by, bz, epoch, "" if part is None else ".p%d" % part)

def light_falloff_pack(row64, falloff):
    """SeLight.pad_3c = f32 falloff (bytes 0x3C-0x3F of the 64-byte row)."""
    return row64[:0x3C] + struct.pack("<f", falloff)

# --- bones (format bones.order, bones.unity; Wire S7Bones) ------------------------------------------------------

def row_order(names, canon):
    """Rows 0..len(canon)-1 = the model index of each canonical name, then the other model bones in model order."""
    idx = {n: i for i, n in enumerate(names)}
    if len(idx) != len(names):
        return None
    rows = []
    for n in canon:
        if n not in idx:
            return None
        rows.append(idx[n])
    seen = set(rows)
    rows += [i for i in range(len(names)) if i not in seen]
    return rows if len(rows) <= 160 else None

def conjugate(r):
    """Mirror * M^T * Mirror of an SE row matrix (12 floats): ([c0, c1, c2], t) in Unity's column form."""
    c0 = (r[0], r[1], -r[2])
    c1 = (r[3], r[4], -r[5])
    c2 = (-r[6], -r[7], r[8])
    return [c0, c1, c2], (r[9], r[10], -r[11])

def quat_from_columns(cx, cy, cz):
    m00, m01, m02 = cx[0], cy[0], cz[0]
    m10, m11, m12 = cx[1], cy[1], cz[1]
    m20, m21, m22 = cx[2], cy[2], cz[2]
    tr = m00 + m11 + m22
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        q = ((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s)
    elif m00 > m11 and m00 > m22:
        s = math.sqrt(1.0 + m00 - m11 - m22) * 2
        q = (0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s)
    elif m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22) * 2
        q = ((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s)
    else:
        s = math.sqrt(1.0 + m22 - m00 - m11) * 2
        q = ((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s)
    n = math.sqrt(sum(x * x for x in q))
    q = tuple(x / n for x in q)
    return q if q[3] >= 0 else tuple(-x for x in q)

def qrot(q, v):
    x, y, z, w = q
    tx, ty, tz = 2 * (y * v[2] - z * v[1]), 2 * (z * v[0] - x * v[2]), 2 * (x * v[1] - y * v[0])
    return (v[0] + w * tx + y * tz - z * ty, v[1] + w * ty + z * tx - x * tz, v[2] + w * tz + x * ty - y * tx)

def component_wise(r):
    """S7.M4's mirror: SE rotation q (from the rows as columns), then (x, y, -z, w); position (x, y, -z)."""
    q = quat_from_columns(r[0:3], r[3:6], r[6:9])
    q = (q[0], q[1], -q[2], q[3])
    return (r[9], r[10], -r[11]), (q if q[3] >= 0 else tuple(-x for x in q))

def check_weights(indices, weights, bone_count, tol=WEIGHT_TOL):
    bad_sum = bad_index = 0
    max_index = -1
    for v in range(len(weights) // 4):
        s = 0.0
        out = False
        for k in range(4):
            w = weights[4 * v + k]
            s += w
            if w != 0:
                i = indices[4 * v + k]
                max_index = max(max_index, i)
                out |= i >= bone_count
        bad_sum += abs(s - 1.0) > tol
        bad_index += out
    return bad_sum, bad_index, max_index

def conventions_selftest():
    ok = True
    txt = CANONICAL_TEXT
    h = fnv1a64(txt)
    good = h == PINNED_HASH and txt.count("\n") == CANONICAL_LINES and len(txt.encode("ascii")) == CANONICAL_BYTES
    print("S7 conventions twin: hash 0x%016x (pinned 0x%016x), %d lines, %d bytes %s" % (
        h, PINNED_HASH, txt.count("\n"), len(txt.encode("ascii")), "PASS" if good else "FAIL"))
    ok &= good
    contract = os.path.join(ROOT, "docs", "CONTRACT.md")
    if os.path.exists(contract):
        with open(contract, encoding="utf-8") as f:
            t = f.read()
        i = t.find("```\next s7 look 1\n")
        same = i >= 0 and t[i + 4:t.index("```", i + 4)] == txt
        print("  CONTRACT section 5.4 block equals the twin: %s" % ("PASS" if same else "FAIL"))
        ok &= same
    k = constants()
    ok &= k["C1Interior"] == {"Version": 1, "Size": 16, "Offset": 0xB0} and k["C2Ext"]["Offset"] + k["C2Ext"]["Size"] == 0x40
    ok &= k["C2Events"]["Offset"] + k["C2Events"]["MaxRows"] * k["C2Events"]["RowSize"] == 0x50000
    ok &= k["C2Events"]["WindowTicks"] * k["C2Events"]["MaxPerTick"] == k["C2Events"]["MaxRows"]
    ok &= {n: v for n, v in k["EventCaps"].items()} == EVENT_CAPS and k["BoneLimits"] == {"AstronautBones": 81, "MaxBones": 160}
    ok &= k["CharacterFlagsExt"]["ExtraBones"] == 4 and k["C1InteriorFlags"] == INTERIOR_FLAGS and k["EventFlags"] == EVENT_FLAGS
    ok &= struct.calcsize(INTERIOR_FMT) == 16 and struct.calcsize(C2EXT_FMT) == 36 and struct.calcsize(EVENT_FMT) == 64
    ok &= struct.calcsize(ROOMSET_FMT) == 16 and struct.calcsize(ROOMBOX_FMT) == 24
    ok &= struct.calcsize(C12_HDR_FMT) == 16 and struct.calcsize(C12_PRIM_FMT) == 48
    b = interior_pack(1 | 2 | 16, room_o2_byte(0.5), 3, 0x0123456789ABCDEF)
    ok &= interior_unpack(b) == {"flags": 19, "roomO2": 128, "roomIndex": 3, "roomGridId": 0x0123456789ABCDEF, "inRoom": True}
    ok &= interior_unpack(b"\x00" * 16) is None and room_o2_byte(1.0) == 255 and room_o2_byte(0.0) == 0
    e = c2ext_pack(3, 7, 101, (2 << 16) | 1, 5, 40, 0x1_0000_0010)
    ok &= c2ext_unpack(e, 0x1_0000_0010) == {"flags": 3, "eventCount": 7, "eventFirstSeq": 101, "eventStream": 0x20001,
                                             "eventsDropped": 5, "lightsFound": 40, "tickLow": 0x10}
    ok &= c2ext_unpack(e, 0x11) is None   # a stale extension (tickLow differs) is ignored
    r = event_pack(9, 2, 1 | 8, 500, 77, 0, (1.0, 2.0, 3.0), (0.0, 0.0, -1.0), 1.0, 1.0, 1234, 1)
    u = event_unpack(r)
    ok &= len(r) == 64 and u["seq"] == 9 and u["type"] == 2 and u["flags"] == 9 and u["pos"] == (1.0, 2.0, 3.0) and u["subtype"] == 1
    ok &= event_key("thr", 0xABC, 2) != event_key("thr", 0xABC, 3) != event_key("tool", 0xABC)
    rs = roomset_pack(42, [(0, 0, 0, 2, 1, 2), (5, 5, 5, 5, 5, 5)], False)
    ok &= roomset_unpack(rs) == {"entity": 42, "truncated": False, "boxes": [(0, 0, 0, 2, 1, 2), (5, 5, 5, 5, 5, 5)]}
    ok &= interior_class((1, 1, 1), [(0, 0, 0, 2, 1, 2)]) == 2 and interior_class((3, 1, 1), [(0, 0, 0, 2, 1, 2)]) == 1
    ok &= interior_class((5, 1, 1), [(0, 0, 0, 2, 1, 2)]) == 0
    c12 = c12_pack(3, -1, 2, 4, [{"kind": 2, "flags": 3, "c": (1.0, -2.0, 3.0), "q": (0.0, 0.0, 0.0, 1.0), "size": (0.5, 2.0, 1.0),
                                  "hash": 0xDEADBEEF}])
    d = c12_unpack(c12)
    ok &= d is not None and d["batch"] == (3, -1, 2) and d["epoch"] == 4 and d["prims"][0]["size"] == (0.5, 2.0, 1.0)
    ok &= host_name(3, -1, 2, 4) == "DE.Statics.3.-1.2.e4" and host_name(0, 0, 0, 1, 2) == "DE.Statics.0.0.0.e1.p2"
    ok &= struct.unpack_from("<f", light_falloff_pack(b"\x00" * 64, 1.5), 0x3C)[0] == 1.5
    # bones: 81 rows = canon (78) then the extras in model order; the conjugate; the component-wise mirror differs
    canon = ["c%d" % i for i in range(78)]
    names = canon[:5] + ["x0"] + canon[5:40] + ["x1"] + canon[40:] + ["x2"]
    rows = row_order(names, canon)
    ok &= rows is not None and len(rows) == 81 and [names[i] for i in rows[78:]] == ["x0", "x1", "x2"]
    ok &= row_order(names[:-2], canon + ["missing"]) is None
    th = math.radians(40.0)
    m = [math.cos(th), 0.0, -math.sin(th), 0.0, 1.0, 0.0, math.sin(th), 0.0, math.cos(th), 0.1, 1.2, 0.3]
    cols, t = conjugate(m)
    ok &= t == (0.1, 1.2, -0.3) and abs(cols[2][0] + m[6]) < 1e-12
    q_good = quat_from_columns(*cols)
    p_bad, q_bad = component_wise(m)
    v = (0.0, 0.0, 1.0)
    ok &= p_bad == t and max(abs(a - b) for a, b in zip(qrot(q_good, v), qrot(q_bad, v))) > 0.1
    ok &= check_weights([0, 1, 0, 0, 80, 0, 0, 0], [0.5, 0.5, 0, 0, 1, 0, 0, 0], 78) == (0, 1, 80)
    print("S7 conventions twin codecs %s" % ("PASS" if ok else "FAIL"))
    return ok

# ----------------------------------------------------------------------------------------------------------------
# reading a folder
# ----------------------------------------------------------------------------------------------------------------
PREFIXES = ("s7.", "s7a.", "s6.")
_PIXEL = re.compile(r"DE\.SnPixel label=(\S+) .*?px=\(([-0-9.eE+]+), ([-0-9.eE+]+)\)")
_PIXEL_DEPTH = re.compile(r"\bdepth=([-0-9.eE+]+)")
_PIXEL_SCREEN = re.compile(r"\bscreen=\((\d+), (\d+)\)")

def _num(d, *names):
    for n in names:
        v = d.get(n) if isinstance(d, dict) else None
        if isinstance(v, bool):
            return 1.0 if v else 0.0
        if isinstance(v, (int, float)):
            return float(v)
        if isinstance(v, str):
            try:
                return float(int(v, 16)) if v.lower().startswith("0x") else float(v)
            except ValueError:
                continue
    return None

def _flag(d, name):
    v = d.get(name) if isinstance(d, dict) else None
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return v != 0
    if isinstance(v, str):
        return v.lower() in ("true", "1", "on", "yes")
    return None

def _dist(a, b):
    if not a or not b or len(a) < 3 or len(b) < 3 or any(x is None for x in list(a) + list(b)):
        return None
    return math.sqrt(sum((float(a[i]) - float(b[i])) ** 2 for i in range(3)))

def _p(values, q):
    v = sorted(values)
    if not v:
        return None
    return v[min(len(v) - 1, max(0, int(math.ceil(q * len(v))) - 1))]

def _share(n, d):
    return (n / float(d)) if d else None

def _pct(x):
    return "-" if x is None else "%.1f %%" % (x * 100)

def _type_name(v):
    if isinstance(v, str):
        return v
    if isinstance(v, (int, float)) and 0 < int(v) < len(EVENT_TYPES):
        return EVENT_TYPES[int(v)]
    return str(v)

def _sumd(rows, key, name):
    n = 0.0
    for r in rows:
        d = r.get(key)
        if isinstance(d, dict):
            n += float(d.get(name) or 0)
    return n

class Logs:
    """A collected S7 session folder."""

    def __init__(self, folder):
        self.folder = folder
        self.session = C.read_jsonl(os.path.join(folder, "s7-session.jsonl"))
        raw_se = C.read_jsonl(sorted(glob.glob(os.path.join(folder, "se-*.jsonl"))))
        raw_sn = C.read_jsonl(sorted(glob.glob(os.path.join(folder, "sn-*.jsonl"))))
        self.se = [e for e in C.events(raw_se, PREFIXES)]
        self.sn = [e for e in C.events(raw_sn, PREFIXES)]
        self.se_text = C.text_lines(raw_se)
        self.sn_text = C.text_lines(raw_sn)
        self.start = next((r for r in self.session if r.get("ev") == "start"), {})
        # Subnautica rows that carry only snFrame are placed on SE ticks through the launcher's 10 Hz section samples
 # (C7 snFrame with the C1 tick of the same sample)
        fm = []
        for r in self.session:
            if r.get("ev") == "sample" and isinstance(r.get("c7"), dict) and r.get("c1Tick") is not None:
                f = r["c7"].get("snFrame")
                if isinstance(f, (int, float)):
                    fm.append((float(f), float(r["c7"].get("presentedSeTick") or r["c1Tick"])))
        self._fmap = sorted(fm)

    def tick_of(self, d):
        """The SE tick of a Subnautica row: seTick / c1Tick / presentedSeTick / c6Tick, else its snFrame through the samples."""
        for k in ("seTick", "c1Tick", "presentedSeTick", "c6Tick", "tick"):
            v = _num(d, k)
            if v is not None and v > 0:
                return v
        f = _num(d, "snFrame")
        if f is None or not self._fmap:
            return None
        i = bisect.bisect_left(self._fmap, (f, -1.0))
        if i <= 0:
            return self._fmap[0][1]
        if i >= len(self._fmap):
            return self._fmap[-1][1]
        (f0, t0), (f1, t1) = self._fmap[i - 1], self._fmap[i]
        return t0 if f1 == f0 else t0 + (t1 - t0) * (f - f0) / (f1 - f0)

    def sn_in(self, rows, t0, t1):
        out = []
        for d in rows:
            t = self.tick_of(d)
            if t is not None and t0 <= t <= t1:
                out.append(d)
        return out

    def sn_in_windows(self, rows, prefix):
        out = []
        for _n, t0, t1 in self.windows(prefix):
            out += self.sn_in(rows, t0, t1)
        return out

    def se_ev(self, name):
        return [e for e in self.se if e.get("_ev") == name]

    def sn_ev(self, name):
        return [e for e in self.sn if e.get("_ev") == name]

    def parts_run(self):
        return [r.get("name") for r in self.session if r.get("ev") == "part" and r.get("phase") == "begin"]

    def ran(self, part):
        return part in self.parts_run()

    def windows(self, prefix):
        """[(name, t0, t1)] of the parts whose name starts with `prefix` (SE ticks of the launcher's C1 samples)."""
        out = []
        for r in self.session:
            if r.get("ev") == "part" and r.get("phase") == "begin" and str(r.get("name", "")).startswith(prefix):
                name = r["name"]
                end = next((e for e in self.session if e.get("ev") == "part" and e.get("name") == name and e.get("phase") == "end"
                            and (e.get("tick") or 0) >= (r.get("tick") or 0)), None)
                t0 = r.get("tick")
                t1 = end.get("tick") if end else None
                if t0 is not None:
                    out.append((name, t0, t1 if t1 is not None else 1 << 62))
        return out

    def window(self, name):
        w = [x for x in self.windows(name) if x[0] == name]
        return w[0] if w else None

    @staticmethod
    def in_ticks(rows, t0, t1, *keys):
        out = []
        for d in rows:
            t = next((_num(d, k) for k in keys if _num(d, k) is not None), None)
            if t is not None and t0 <= t <= t1:
                out.append(d)
        return out

    def in_windows(self, rows, prefix, *keys):
        out = []
        for _n, t0, t1 in self.windows(prefix):
            out += self.in_ticks(rows, t0, t1, *keys)
        return out

    def replies(self, label_prefix=None, cmd=None, side=None):
        return [r for r in self.session if r.get("ev") == "reply" and (label_prefix is None or str(r.get("label") or "").startswith(label_prefix))
                and (cmd is None or r.get("cmd") == cmd) and (side is None or r.get("side") == side)]

    def events(self, ev):
        return [r for r in self.session if r.get("ev") == ev]

    def pixels(self):
        """{label: (x, y, depth or None, (width, height) or None)} of the DE.SnPixel lines (S7-1 fix: depth and screen kept,
 so a probe behind the camera or off screen reads MISSING instead of a pixel of something else)."""
        out = {}
        for line in self.sn_text:
            m = _PIXEL.search(line)
            if m:
                d = _PIXEL_DEPTH.search(line)
                s = _PIXEL_SCREEN.search(line)
                out[m.group(1)] = (float(m.group(2)), float(m.group(3)), float(d.group(1)) if d else None,
                                   (int(s.group(1)), int(s.group(2))) if s else None)
        return out

    def owner_skipped(self):
        """{step: prompt} of the project steps the session skipped."""
        out = {}
        for r in self.session:
            if r.get("ev") != "owner":
                continue
            st = str(r.get("step") or "")
            if r.get("phase") == "skipped":
                out[st] = r.get("prompt") or ""
            elif r.get("phase") in ("begin", "end", "done"):
                out.pop(st, None)
        return out

    def probes(self):
        return {d.get("label"): d.get("rgb") for d in self.sn_ev("s7.probe") + self.se_ev("s7.probe") if d.get("label")}

    def capture(self, name, side="sn"):
        for r in self.session:
            if r.get("ev") == "capture" and r.get("name") == name and r.get("side", "sn") == side and r.get("path"):
                for base in (os.path.join(self.folder, "captures"), self.folder):
                    p = os.path.join(base, r["path"].replace("\\", "/"))
                    if os.path.exists(p):
                        return p
                    p2 = os.path.join(base, os.path.basename(r["path"]))
                    if os.path.exists(p2):
                        return p2
        return None

def row(rows, cid, status, measured, threshold, what, part="", required=True, **detail):
    return C.row(rows, cid, status, measured, threshold, what, part, required, **detail)

# ----------------------------------------------------------------------------------------------------------------
# images (S7.N1.fog, S7.N3.ratio): the interiors build pass s7_images when present, else a minimal PNG reader
# ----------------------------------------------------------------------------------------------------------------

def read_png(path):
    """(width, height, channels, rows of bytes) of an 8-bit RGB/RGBA non-interlaced PNG."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG")
    pos, idat, w, h, ch = 8, b"", 0, 0, 0
    while pos < len(data):
        n = struct.unpack(">I", data[pos:pos + 4])[0]
        kind = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + n]
        pos += 12 + n
        if kind == b"IHDR":
            w, h, depth, ctype, _c, _f, inter = struct.unpack(">IIBBBBB", body)
            if depth != 8 or ctype not in (2, 6) or inter != 0:
                raise ValueError("PNG depth %d colour type %d interlace %d not read" % (depth, ctype, inter))
            ch = 3 if ctype == 2 else 4
        elif kind == b"IDAT":
            idat += body
        elif kind == b"IEND":
            break
    raw = zlib.decompress(idat)
    stride = w * ch
    rows, prev, i = [], bytearray(stride), 0
    for _y in range(h):
        ft = raw[i]
        line = bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - ch] if x >= ch else 0
            b = prev[x]
            c = prev[x - ch] if x >= ch else 0
            if ft == 1:
                line[x] = (line[x] + a) & 255
            elif ft == 2:
                line[x] = (line[x] + b) & 255
            elif ft == 3:
                line[x] = (line[x] + ((a + b) >> 1)) & 255
            elif ft == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else (b if pb <= pc else c))) & 255
        rows.append(bytes(line))
        prev = line
    return w, h, ch, rows

def write_png(path, w, h, pixel):
    """A test PNG (RGB, filter 0): pixel(x, y) -> (r, g, b) bytes."""
    raw = b"".join(b"\x00" + b"".join(bytes(pixel(x, y)) for x in range(w)) for y in range(h))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) +
                chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))

def probe_rgb(path, x, y, radius=4):
    try:
        import s7_images   # the interiors build pass image checks, called by name when present
        fn = getattr(s7_images, "probe_rgb", None)
        if fn is not None:
            v = fn(path, x, y, radius)
            return list(v) if v is not None else None   # S7-1 fix: None = the patch lies outside the image
    except ImportError:
        pass
    w, h, ch, rows = read_png(path)
    xi, yi = int(round(x)), int(round(y))
    acc, n = [0.0, 0.0, 0.0], 0
    for yy in range(max(0, yi - radius), min(h, yi + radius + 1)):
        line = rows[yy]
        for xx in range(max(0, xi - radius), min(w, xi + radius + 1)):
            for k in range(3):
                acc[k] += line[xx * ch + k]
            n += 1
    if n == 0:
        return None
    return [a / (255.0 * n) for a in acc]

def _probe(L, label, capture_name):
    """[r, g, b] 0..1 of a probe: an s7.probe row, else the capture `capture_name` at the DE.SnPixel of `label`."""
    pr = L.probes()
    if pr.get(label) is not None:
        return pr[label], "probe row"
    px = L.pixels().get(label)
    path = L.capture(capture_name)
    if px is None or path is None:
        return None, "no %s" % ("DE.SnPixel " + label if px is None else "capture " + capture_name)
    # S7-1 fix: the S7-1 fog probe (a) was off screen (px 2651 of 2560) and the n3 Seamoth probe behind the camera
 # (depth -17.5): neither is a pixel of the probed point
    if px[2] is not None and px[2] <= 0:
        return None, "%s behind the camera (depth %.1f)" % (label, px[2])
    if px[3] is not None and not (0 <= px[0] < px[3][0] and 0 <= px[1] < px[3][1]):
        return None, "%s off screen (px %.0f, %.0f)" % (label, px[0], px[1])
    try:
        v = probe_rgb(path, px[0], px[1])
    except (OSError, ValueError, zlib.error) as e:
        return None, "image unreadable: %s" % e
    return (v, "capture") if v is not None else (None, "%s outside the capture" % label)

def _mad(a, b):
    return sum(abs(a[i] - b[i]) for i in range(3)) / 3.0

def _lum(c):
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]

# ----------------------------------------------------------------------------------------------------------------
# S7.N1 interiors, N1b, N1c, N2, N8
# ----------------------------------------------------------------------------------------------------------------

def _rows_sorted(rows, key="tick"):
    return sorted(rows, key=lambda d: _num(d, key) or 0)

def n1_rows(L, rows):
    if not L.ran("n1"):
        return
    se = _rows_sorted(L.se_ev("s7.interior"))
    sn = _rows_sorted(L.sn_ev("s7.interior"), "snFrame")
    what = "S7.N1 sealed room (ROADMAP S7): SE inRoom on 100 % of rows 2 ticks after each entry, false outside; " \
           "Subnautica inside = C1 on >= 99 %"
    bad_se, n_se, good_sn, n_sn = 0, 0, 0, 0
    for name, t0, t1 in L.windows("n1.inside.") + L.windows("n1.outside."):
        want = name.startswith("n1.inside.")
        for d in L.in_ticks(se, t0 + N1_ENTRY_TICKS, t1, "tick"):
            n_se += 1
            bad_se += _flag(d, "inRoom") != want
        for d in L.in_ticks(sn, t0 + N1_ENTRY_TICKS, t1, "c1Tick"):
            n_sn += 1
            good_sn += _flag(d, "inside") == want
    if not n_se:
        row(rows, "S7.N1.flag", "MISSING", "no s7.interior rows in the windows", "100 % / 99 %", what, "n1")
    else:
        sh = _share(good_sn, n_sn)
        ok = bad_se == 0 and sh is not None and sh >= N1_SN_SHARE
        row(rows, "S7.N1.flag", "PASS" if ok else ("MISSING" if sh is None else "FAIL"),
            "SE %d/%d bad; SN %s" % (bad_se, n_se, _pct(sh)), "SE 0 bad, SN >= 99 %", what, "n1")
    # fog A/B
    what = "S7.N1.fog: probe (a) the inner wall differs between patch on and off by >= 0.05 (mean |RGB|), probe (b) " \
           "20 m beyond the window by <= 0.02"
    a_on, why1 = _probe(L, "n1.fog.on.a", "n1.fog.on")
    a_off, why2 = _probe(L, "n1.fog.off.a", "n1.fog.off")
    b_on, why3 = _probe(L, "n1.fog.on.b", "n1.fog.on")
    b_off, why4 = _probe(L, "n1.fog.off.b", "n1.fog.off")
    if None in (a_on, a_off, b_on, b_off):
        row(rows, "S7.N1.fog", "MISSING", "; ".join(w for w, v in ((why1, a_on), (why2, a_off), (why3, b_on), (why4, b_off)) if v is None)[:60],
            ">= 0.05 / <= 0.02", what, "n1.fog")
    else:
        da, db = _mad(a_on, a_off), _mad(b_on, b_off)
        row(rows, "S7.N1.fog", "PASS" if da >= FOG_A_MIN and db <= FOG_B_MAX else "FAIL", "a %.3f, b %.3f" % (da, db),
            ">= 0.05 / <= 0.02", what, "n1.fog", a=[a_on, a_off], b=[b_on, b_off])
    # breach and re-seal
    what = "S7.N1.breach: a wall removed: inRoom false and breached within 2 s of the gas system's completion, Subnautica " \
           "inside false within 3 frames after; re-placed: inRoom true within 2 s"
    wr, wp = L.window("n1.breach.remove"), L.window("n1.breach.replace")
    if not wr or not wp:
        row(rows, "S7.N1.breach", "MISSING", "no breach windows", "2 s / 3 frames / 2 s", what, "n1.breach")
    else:
        r1 = L.in_ticks(se, wr[1], wr[2], "tick")
        done = next((_num(d, "tick") for i, d in enumerate(r1) if not _flag(d, "processing") and i > 0 and _flag(r1[i - 1], "processing")), wr[1])
        flip = next((d for d in r1 if _flag(d, "inRoom") is False and _flag(d, "breached")), None)
        t_flip = _num(flip, "tick") if flip else None
        sn_r = L.in_ticks(sn, wr[1], wr[2], "c1Tick")
        first_after = next((d for d in sn_r if t_flip is not None and _num(d, "c1Tick") >= t_flip), None)
        sn_false = next((d for d in sn_r if t_flip is not None and _num(d, "c1Tick") >= t_flip and _flag(d, "inside") is False), None)
        frames = (_num(sn_false, "snFrame") - _num(first_after, "snFrame")) if sn_false and first_after else None
        r2 = L.in_ticks(se, wp[1], wp[2], "tick")
        back = next((_num(d, "tick") for d in r2 if _flag(d, "inRoom")), None)
        ok = t_flip is not None and t_flip - done <= BREACH_TICKS and frames is not None and frames <= BREACH_SN_FRAMES and \
            back is not None and back - wp[1] <= RESEAL_TICKS
        row(rows, "S7.N1.breach", "PASS" if ok else "FAIL",
            "flip %s t, SN %s fr, reseal %s t" % (None if t_flip is None else int(t_flip - done), None if frames is None else int(frames),
                                                  None if back is None else int(back - wp[1])), "<= 120 t, <= 3 fr, <= 120 t", what, "n1.breach")
    # flicker
    what = "S7.N1.flicker: no inRoom change while processing is set, none outside the camera's entry/exit and the breach " \
           "and re-seal edges (judged only with a recompute in the n1 windows: a processing row, or the door toggled)"
    edges = []
    for name, t0, t1 in L.windows("n1.inside.") + L.windows("n1.outside."):
        edges += [t0, t1]
    for name, t0, t1 in L.windows("n1.door"):
        edges.append(t0)    # review F8: the camera moved into the room just before the door window began
    breach = [(w[1], w[2]) for w in (wr, wp) if w]
    changes, bad = 0, []
    prev = None
    for d in [x for x in se if L.in_windows([x], "n1.", "tick")]:
        v = _flag(d, "inRoom")
        t = _num(d, "tick")
        if prev is not None and v != prev[0]:
            changes += 1
            near_edge = any(abs(t - e) <= EDGE_GUARD_TICKS for e in edges) and not _flag(d, "processing")
            in_breach = any(a <= t <= b for a, b in breach) and not (_flag(d, "processing") and _flag(prev[1], "processing"))
            if _flag(d, "processing") and _flag(prev[1], "processing"):
                bad.append(("processing", t))
            elif not near_edge and not in_breach:
                bad.append(("outside the edges", t))
        prev = (v, d)
    # review F8: without a gas-system recompute in the n1 windows (no processing row, no door toggled) the hold is never
 # exercised and S7.M10 cannot flicker: INFO instead of a vacuous PASS
    n1_rows_se = [x for x in se if L.in_windows([x], "n1.", "tick")]
    processing_seen = any(_flag(x, "processing") for x in n1_rows_se)
    doors_ok = [r for r in L.replies("n1.door.", side="se") if isinstance(r.get("reply"), dict) and r["reply"].get("ok")
                and (r.get("args") or {}).get("action") == "door"]
    if not se:
        row(rows, "S7.N1.flicker", "MISSING", "no s7.interior rows", "0 changes outside edges", what, "n1")
    elif not processing_seen and not doors_ok and not bad:
        row(rows, "S7.N1.flicker", "INFO", "%d changes, no recompute" % changes, "0 bad", what, "n1", required=False)
    else:
        row(rows, "S7.N1.flicker", "PASS" if not bad else "FAIL", "%d changes, %d bad, %d door" % (changes, len(bad), len(doors_ok)),
            "0 bad", what, "n1", bad=bad[:5])
    caps = [r for r in L.events("capture") if str(r.get("name", "")).startswith("n1.")]
    row(rows, "S7.N1.capture", "INFO", "%d captures" % len(caps), "owner look", "S7.N1 capture pair (patch on/off) looked at",
        "n1", required=False, paths=[r.get("path") for r in caps][:6])

def n1b_rows(L, rows):
    if not L.ran("n1b"):
        return
    se = []
    for _n, t0, t1 in L.windows("n1b.inside"):
        se += L.in_ticks(L.se_ev("s7.interior"), t0 + N1_ENTRY_TICKS, t1, "tick")
    what = "S7.N1b pressurisation off: flag false on 100 % of rows with the camera inside"
    bad = [d for d in se if _flag(d, "inRoom")]
    row(rows, "S7.N1b.flag", "MISSING" if not se else ("PASS" if not bad else "FAIL"), "%d rows, %d inRoom" % (len(se), len(bad)),
        "0 inRoom", what, "n1b")
    lines = [x for x in L.se_text if "DE.Interior OFF pressurization disabled" in x]
    rooms = L.in_windows(L.se_ev("s7.rooms"), "n1b", "tick")
    what = "S7.N1b: 'DE.Interior OFF pressurization disabled' exactly once, no RoomSet sent"
    row(rows, "S7.N1b.log", "PASS" if len(lines) == 1 and not rooms else "FAIL", "%d lines, %d RoomSets" % (len(lines), len(rooms)),
        "1 line, 0 RoomSets", what, "n1b")

def n1c_rows(L, rows):
    if not L.ran("n1c"):
        return
    what = "S7.N1c the drained house: characterInRoom and swim Walk on >= 95 % of rows inside; forward speed >= 80 % of " \
           "the dry-land walk (O25 HYPOTHESIS)"
    # S7-1 fix: the rows of the entry transition (the character teleported in on the window's first tick: S7-1 read 4 of 5
 # rows true, the false one at the window start) are not judged, as S7.N1.flag's 2 ticks after each entry
    inside, loco_in = [], []
    for _n, t0, t1 in L.windows("n1c.walk"):
        inside += L.in_ticks(L.se_ev("s7.interior"), t0 + N1_ENTRY_TICKS, t1, "tick")
        loco_in += L.in_ticks(L.se_ev("s7a.loco"), t0 + N1_ENTRY_TICKS, t1, "tick")   # not the dry walk's last row
    loco_dry = L.in_windows(L.se_ev("s7a.loco"), "n1c.dry", "tick")
    if not inside or not loco_in or not loco_dry:
        row(rows, "S7.N1c.walk", "MISSING", "owner walk not recorded", ">= 95 %, >= 80 %", what, "n1c")
    else:
        sh_room = _share(sum(1 for d in inside if _flag(d, "characterInRoom")), len(inside))
        walking = [d for d in loco_in if str(d.get("state")) in ("Walk", "Run", "Sprint", "2", "3", "4")]
        sh_walk = _share(sum(1 for d in loco_in if not _flag(d, "inWater")), len(loco_in))
        v_in = _p([abs(_num(d, "vF") or 0) for d in walking], 0.5) or 0
        v_dry = _p([abs(_num(d, "vF") or 0) for d in loco_dry if str(d.get("state")) in ("Walk", "Run", "Sprint", "2", "3", "4")], 0.5) or 0
        ratio = v_in / v_dry if v_dry else None
        ok = sh_room >= N1C_SHARE and sh_walk >= N1C_SHARE and ratio is not None and ratio >= N1C_SPEED
        row(rows, "S7.N1c.walk", "PASS" if ok else "FAIL", "room %s, dry %s, v %.2f" % (_pct(sh_room), _pct(sh_walk), ratio or 0),
            ">= 95 %, >= 0.80", what, "n1c")
    row(rows, "S7.N1c.look", "INFO", "owner verdict", "dry interior, water through the window", "S7.N1c owner verdict",
        "n1c", required=False)

def _inside_rows(L, window):
    """Review F4: the s7.inside rows of the launcher's n2.<window> part window (by ticks, not by the row's own `window`
 field: under S7.M1 the inside trace sees no inside frame and would call itself "outside"); fallback the field."""
    rs = L.sn_in_windows(L.sn_ev("s7.inside"), "n2.%s" % window)
    return rs or [d for d in L.sn_ev("s7.inside") if d.get("window") == window]

def n2_rows(L, rows):
    if not L.ran("n2"):
        return
    ins, out = _inside_rows(L, "inside"), _inside_rows(L, "outside")
    what = "S7.N2 IsInsideWalkable trace (review F4): the always-on readers (WaterscapeVolumeOnCamera, FMODGameParams, " \
           "LargeWorldStreamer, uGUI_AdaptiveBrightness or ColorCorrection) called in both windows, inside frames and inside " \
           "calls in the inside window, UnstuckPlayer never inside (our prefix skips TrySavePosition); the other readers INFO"
    if not ins or not out:
        row(rows, "S7.N2.readers", "MISSING", "no s7.inside rows", "always-on readers", what, "n2")
    else:
        a, b = ins[-1], out[-1]
        ri = a.get("readersInside") if isinstance(a.get("readersInside"), dict) else (a.get("readers") or {})
        ro = b.get("readersOutside") if isinstance(b.get("readersOutside"), dict) else (b.get("readers") or {})

        def canon(rd):     # S7-1 fix: nested caller types named by their outer type (READER_ALIASES)
            out = {}
            for k, v in rd.items():
                k2 = READER_ALIASES.get(k, k)
                out[k2] = float(out.get(k2) or 0) + float(v or 0)
            return out
        ri, ro = canon(ri), canon(ro)
        missing = [r for r in READERS_ALWAYS if not ri.get(r) or not ro.get(r)]
        if not any(ri.get(r) for r in READERS_BRIGHTNESS) or not any(ro.get(r) for r in READERS_BRIGHTNESS):
            missing.append("|".join(READERS_BRIGHTNESS))
        calls_in = sum(float(v or 0) for v in ri.values())
        frames_in = _num(a, "insideFrames")
        unstuck_in = float(ri.get("UnstuckPlayer") or 0)
        not_called = [r for r in READERS_DIRECT + READERS_INSIDE if not (ri.get(r) or ro.get(r))]
        why = []
        if missing:
            why.append("missing " + ",".join(missing))
        if calls_in <= 0:
            why.append("no calls inside")
        if frames_in is not None and frames_in <= 0:
            why.append("0 inside frames")
        if unstuck_in > 0:
            why.append("UnstuckPlayer inside %d" % unstuck_in)
        row(rows, "S7.N2.readers", "PASS" if not why else "FAIL",
            ", ".join(why)[:34] or "%d calls in, %d not called" % (calls_in, len(not_called)), "always-on readers, calls in", what, "n2",
            missing=missing, notCalled=not_called, why=why)
        row(rows, "S7.N2.notcalled", "INFO", "%d not called" % len(not_called), "information",
            "S7.N2 readers that ran in neither window (conditional readers)", "n2", required=False, notCalled=not_called)
    what = "S7.N2.ghost: health equal inside/outside, position within 0.05 m of the camera rule, O2 = C1, CanBeAttacked " \
           "false inside and true outside"
    if not ins or not out:
        row(rows, "S7.N2.ghost", "MISSING", "no s7.inside rows", "unchanged", what, "n2")
    else:
        gi, go = ins[-1].get("ghost") or {}, out[-1].get("ghost") or {}
        why = []
        if _num(gi, "health") != _num(go, "health"):
            why.append("health %s/%s" % (_num(gi, "health"), _num(go, "health")))
        for g in (gi, go):
            pe = _num(g, "posErrM")
            if pe is not None and pe > 0.05:
                why.append("pos %.3f m" % pe)
            if _num(g, "c1O2") is not None and abs((_num(g, "o2") or 0) - _num(g, "c1O2")) > 0.01:
                why.append("o2 %s != %s" % (_num(g, "o2"), _num(g, "c1O2")))
        if _flag(ins[-1], "canBeAttacked") is not False:
            why.append("attackable inside")
        # review F21: CanBeAttacked is !GameModeUtils.IsInvisible outside: a Creative / NoAggression save skips that half
        if _flag(out[-1], "noAggression") is not True and _flag(out[-1], "canBeAttacked") is not True:
            why.append("not attackable outside")
        row(rows, "S7.N2.ghost", "PASS" if not why else "FAIL", ", ".join(why) or "unchanged", "unchanged", what, "n2",
            noAggression=_flag(out[-1], "noAggression"))
    row(rows, "S7.N2.music", "INFO", "%s / %s" % (str(ins[-1].get("music") if ins else None)[:15], str(out[-1].get("music") if out else None)[:15]),
        "owner verdict", "S7.N2 FMODGameParams interior events inside/outside (accepted or patched by the owner)", "n2", required=False)
    row(rows, "S7.N2.brightness", "INFO", "%s / %s" % (ins[-1].get("brightness") if ins else None, out[-1].get("brightness") if out else None),
        "owner verdict", "S7.N2 uGUI_AdaptiveBrightness / ColorCorrection inside/outside", "n2", required=False)

def _sky_class(v):
    if isinstance(v, str):
        return {"inside": 2, "boundary": 1, "exterior": 0}.get(v.lower(), -1)
    return int(v) if isinstance(v, (int, float)) else -1

def n8_rows(L, rows):
    sky = L.sn_ev("s7.sky")
    if not (L.ran("n1") or L.ran("n8")):
        return
    what = "S7.N8 interior sky: Inside chunks the base-interior custom sky, Exterior the biome sky, Boundary the interior " \
           "sky while the camera is in that room, else the biome sky (100 % of s7.sky rows)"
    if not sky:
        row(rows, "S7.N8.sky", "MISSING", "no s7.sky rows", "100 %", what, "n1")
        return
    bad = []
    for d in sky:
        cls = _sky_class(d.get("class"))
        want = "interior" if cls == 2 or (cls == 1 and _flag(d, "cameraInRoom")) else "biome"
        if str(d.get("applied")).lower() != want or _flag(d, "ok") is False:
            bad.append({"class": d.get("class"), "applied": d.get("applied"), "want": want, "ok": d.get("ok")})
    row(rows, "S7.N8.sky", "PASS" if not bad else "FAIL", "%d rows, %d bad" % (len(sky), len(bad)), "0 bad", what, "n1", bad=bad[:4])

# ----------------------------------------------------------------------------------------------------------------
# S7.N3 lights
# ----------------------------------------------------------------------------------------------------------------

def n3_rows(L, rows):
    if not L.ran("n3"):
        return
    what = "S7.N3 lights: luminance of the SE Interior Light's wall point vs the Seamoth cabin light's within +-25 %"
    sn = L.sn_in_windows(L.sn_ev("s7.lights"), "n3") or L.sn_ev("s7.lights")
    se_n3 = L.in_windows(L.se_ev("s7.lights"), "n3", "tick")
    # S7-1 fix: a ratio of two pixels is only the lights' ratio when SE's light reached Subnautica in the n3 window (S7-1:
 # SE found 0 lights, every Subnautica row applied 0, yet the two probes gave 0.991 from the ambient water)
    applied = max([_num(d, "applied") or 0 for d in sn] or [0])
    se_sent = max([_num(d, "sent") or 0 for d in se_n3] or [0])
    a, w1 = _probe(L, "n3.se", "n3.lights")
    b, w2 = _probe(L, "n3.seamoth", "n3.seamoth" if L.capture("n3.seamoth") else "n3.lights")
    if not applied:
        row(rows, "S7.N3.ratio", "MISSING", "no SE light applied (SE sent %d)" % se_sent, "+-25 %", what, "n3", seSent=se_sent)
    elif a is None or b is None:
        row(rows, "S7.N3.ratio", "MISSING", (w1 if a is None else w2)[:34], "+-25 %", what, "n3")
    else:
        ratio = _lum(a) / _lum(b) if _lum(b) > 1e-6 else None
        row(rows, "S7.N3.ratio", "PASS" if ratio is not None and abs(ratio - 1.0) <= LIGHT_RATIO_TOL else "FAIL",
            "ratio %s" % (None if ratio is None else round(ratio, 3)), "0.75..1.25", what, "n3")
    what = "S7.N3.shadows: <= 2 shadowed lights on every Subnautica s7.lights frame, judged only with >= 3 spot rows asking " \
           "for shadows (review F9: the room fixture's three spotlights)"
    over = [d for d in sn if (_num(d, "shadowed") or 0) > MAX_SHADOWED]
    cand = max([_num(d, "shadowCandidates") or 0 for d in sn] or [0])
    if not sn or cand < 3:
        row(rows, "S7.N3.shadows", "MISSING", "%d rows, %d shadow spots%s" % (len(sn), cand, ", SE sent 0" if not se_sent else ""),
            "<= 2 of >= 3", what, "n3", seSent=se_sent)
    else:
        row(rows, "S7.N3.shadows", "PASS" if not over else "FAIL",
            "%d rows, max %s of %d" % (len(sn), max([_num(d, "shadowed") or 0 for d in sn] or [0]), cand), "<= 2 of >= 3", what, "n3")
    se = L.se_ev("s7.lights")
    what = "S7.N3.cap: lightCount <= 64 on both sides, no row beyond 60 m"
    over = [d for d in se if (_num(d, "sent") or 0) > MAX_LIGHTS or (_num(d, "maxDistM") or 0) > LIGHT_RADIUS_M] + \
           [d for d in sn if (_num(d, "rows") or 0) > MAX_LIGHTS]
    row(rows, "S7.N3.cap", "MISSING" if not se and not sn else ("PASS" if not over else "FAIL"), "%d+%d rows, %d over" % (len(se), len(sn), len(over)),
        "<= 64, <= 60 m", what, "n3")

# ----------------------------------------------------------------------------------------------------------------
# S7.N4 effects
# ----------------------------------------------------------------------------------------------------------------

def _linreg_r2(xs, ys):
    n = len(xs)
    if n < 3:
        return None
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    syy = sum((y - my) ** 2 for y in ys)
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    if sxx <= 0 or syy <= 0:
        return 0.0
    return (sxy * sxy) / (sxx * syy)

def _join_events(se_ev, sn_ev):
    """[(se, sn)] joined by (stream, seq)."""
    idx = {(d.get("stream"), _num(d, "seq")): d for d in se_ev}
    out = []
    for d in sn_ev:
        k = (d.get("stream"), _num(d, "seq"))
        if k in idx:
            out.append((idx[k], d))
    return out

def _events_of(rows, tname):
    return [d for d in rows if _type_name(d.get("type")) == tname]

def _span(rows, key):
    """The growth of a cumulative counter over rows (max - min; 0 without values)."""
    v = [_num(d, key) for d in rows if _num(d, key) is not None]
    return (max(v) - min(v)) if len(v) > 1 else 0.0

def _se_sustains(rows):
    """Review F18d: SE's sustain rows (de.fx trace) by (entity, key): [(tick, row)] sorted by tick."""
    out = {}
    for d in rows:
        if _flag(d, "sustain") is False:
            continue
        t = _num(d, "tick")
        if t is None:
            continue
        out.setdefault((str(d.get("entity")), str(d.get("key"))), []).append((t, d))
    for v in out.values():
        v.sort(key=lambda x: x[0])
    return out

def _se_at(sus, d):
    """The SE sustain row of a Subnautica s7.fx.thruster row: the same (entity, key), the newest at or before its seTick."""
    lst = sus.get((str(d.get("entity")), str(d.get("key"))))
    t = _num(d, "seTick")
    if not lst or t is None:
        return None
    i = bisect.bisect_right([x[0] for x in lst], t) - 1
    return lst[i][1] if i >= 0 else None

def _se_rpm(sus, d):
    """Review F18d: the engine rule over SE's rows of the entity at the row's seTick: sum(i s^2) / sum(s^2) over its keys."""
    t = _num(d, "seTick")
    ent = str(d.get("entity"))
    num = den = 0.0
    n = 0
    for (e, _k), lst in sus.items():
        if e != ent:
            continue
        i = bisect.bisect_right([x[0] for x in lst], t) - 1 if t is not None else -1
        if i < 0:
            continue
        r = lst[i][1]
        if _flag(r, "stop"):
            continue
        s = _num(r, "size") or 0.0
        num += (_num(r, "intensity") or 0.0) * s * s
        den += s * s
        n += 1
    return (num / den) if n and den > 0 else None

def n4_rows(L, rows):
    if not L.ran("n4"):
        return
    se_fx, sn_fx = L.se_ev("s7.fx"), L.sn_ev("s7.fx")
    se_ev, sn_ev = L.se_ev("s7.fx.event"), L.sn_ev("s7.fx.event")
    thr = L.sn_ev("s7.fx.thruster")
    # thruster.rate
    what = "S7.N4.thruster.rate: emission per flame vs SE's CurrentStrength x size (the SE sustain rows of the same key, " \
           "review F18d) linear (r^2 >= 0.9), zero within 0.25 s of strength 0"
    tw = L.windows("n4.thrust.")
    pts = L.in_windows(thr, "n4.thrust.", "seTick")
    se_sus = _se_sustains(L.in_windows([d for d in se_ev if _type_name(d.get("type")) == "Thruster"], "n4.thrust.", "tick"))
    joined = [(d, _se_at(se_sus, d)) for d in pts]
    joined = [(d, s) for d, s in joined if s is not None]
    if not tw or not pts:
        row(rows, "S7.N4.thruster.rate", "MISSING", "no s7.fx.thruster rows", "r2 >= 0.9", what, "n4.thrust")
    elif not joined:
        row(rows, "S7.N4.thruster.rate", "MISSING", "no SE sustain rows (de.fx trace)", "r2 >= 0.9", what, "n4.thrust")
    else:
        xs = [(_num(s, "intensity") or 0) * (_num(s, "size") or 1) for _d, s in joined]
        ys = [_num(d, "emission") or 0 for d, _s in joined]
        r2 = _linreg_r2(xs, ys)
        zero_bad = 0
        for name, t0, t1 in tw:
            if name.split("n4.thrust.")[-1] in ("0", "0.0"):
                late = [d for d in L.in_ticks(thr, t0 + ZERO_AFTER_S * TICKS_PER_S, t1, "seTick")]
                zero_bad += sum(1 for d in late if (_num(d, "emission") or 0) > 1e-6)
        row(rows, "S7.N4.thruster.rate", "PASS" if r2 is not None and r2 >= R2_MIN and zero_bad == 0 else "FAIL",
            "r2 %s, %d nonzero at 0" % (None if r2 is None else round(r2, 3), zero_bad), "r2 >= 0.9, 0", what, "n4.thrust")
    # thruster.rpm
    what = "S7.N4.thruster.rpm: the engine loop's rpm = the rule over SE's sustain rows of that entity (sum intensity*size^2 / " \
           "sum size^2, review F18d) +- 0.05 on >= 95 % of frames"
    rp = []
    for d in pts:
        if _num(d, "rpm") is None or _flag(d, "enginePlaying") is False:
            continue        # the loop plays under water only: a row without it has no rpm to compare
        rule = _se_rpm(se_sus, d)
        if rule is not None:
            rp.append((d, rule))
    if not rp:
        row(rows, "S7.N4.thruster.rpm", "MISSING", "no rpm samples joined to SE", ">= 95 %", what, "n4.thrust")
    else:
        sh = _share(sum(1 for d, rule in rp if abs(_num(d, "rpm") - rule) <= RPM_TOL), len(rp))
        row(rows, "S7.N4.thruster.rpm", "PASS" if sh >= RPM_SHARE else "FAIL", _pct(sh), ">= 95 %", what, "n4.thrust")
    # thruster.log
    what = "S7.N4.thruster.log: at constant strength <= 1 sustain row per flame per 15 ticks (+1), each step answered " \
           "within 1 tick"
    fx_thr = [d for d in L.in_windows(se_fx, "n4.thrust.", "tick") if isinstance(d.get("sustainRows"), dict)]
    if not fx_thr:
        row(rows, "S7.N4.thruster.log", "MISSING", "no sustainRows in s7.fx", "<= 1/15 t, lag <= 1", what, "n4.thrust")
    else:
        over, lag = 0, 0
        for d in fx_thr:
            n = float(d["sustainRows"].get("Thruster") or 0)
            k = float((d.get("sustainKeys") or {}).get("Thruster") or 0)
            ticks = _num(d, "ticks") or TICKS_PER_S
            if k and n > k * (ticks / SUSTAIN_REFRESH_TICKS + 1):
                over += 1
            lag = max(lag, _num(d, "stepLagMaxTicks") or 0)
        row(rows, "S7.N4.thruster.log", "PASS" if over == 0 and lag <= 1 else "FAIL", "%d rows over, lag %d t" % (over, lag),
            "0, <= 1 t", what, "n4.thrust")
    # weapon
    what = "S7.N4.weapon: 20 muzzles and 20 streaks sent, none dropped, all applied (no seq gap), latency <= 3 frames p95, " \
           "attached positions within 0.05 m"
    w = L.window("n4.gun")
    if not w:
        row(rows, "S7.N4.weapon", "MISSING", "no n4.gun window", "20/20/0", what, "n4.gun")
    else:
        shots = next((int(_num(r.get("args") or {}, "shots") or 0) for r in L.replies("n4.gun", side="se")), 20)
        # review F11: count the per-event rows (one per accepted one-shot) of [w1, w2 + 1 s], not the 1 Hz delta rows that the
 # window edges cut; the deltas only for the drops
        fs = L.in_ticks(se_fx, w[1], w[2] + TICKS_PER_S, "tick")
        evs = L.in_ticks(se_ev, w[1], w[2] + TICKS_PER_S, "tick")
        sent_m, sent_s = len(_events_of(evs, "WeaponMuzzle")), len(_events_of(evs, "BulletStreak"))
        drop = _sumd(fs, "dropped", "WeaponMuzzle") + _sumd(fs, "dropped", "BulletStreak")
        fn = L.sn_in(sn_fx, w[1], w[2] + TICKS_PER_S)
        appl = _sumd(fn, "applied", "WeaponMuzzle")
        missed = sum(_num(d, "missed") or 0 for d in fn)
        pairs = _join_events(_events_of(evs, "WeaponMuzzle") + _events_of(evs, "BulletStreak"),
                             _events_of(sn_ev, "WeaponMuzzle") + _events_of(sn_ev, "BulletStreak"))
        dists = [x for x in (_dist(a.get("posSn"), b.get("posSn")) for a, b in pairs) if x is not None]
        lat = _p([_num(b, "latencyFrames") for _a, b in pairs if _num(b, "latencyFrames") is not None], 0.95)
        why = []
        if sent_m < shots or sent_s < shots:
            why.append("sent %d/%d" % (sent_m, sent_s))
        if drop:
            why.append("dropped %d" % drop)
        if appl < shots or missed:
            why.append("applied %d missed %d" % (appl, missed))
        if not dists or max(dists) > POS_ATTACHED_M:
            why.append("pos %s" % (None if not dists else round(max(dists), 3)))
        if lat is None or lat > LAT_P95_FRAMES:
            why.append("lat p95 %s" % lat)
        row(rows, "S7.N4.weapon", "PASS" if not why else "FAIL", ", ".join(why) or "%d/%d sent, pos %.3f m" % (sent_m, sent_s, max(dists)),
            "20/20, 0 drop, <= 3 fr, <= 0.05 m", what, "n4.gun")
        # impact
        what = "S7.N4.impact: every SE Impact event of the window (OnHit on the plate) applied in Subnautica, within 0.1 m " \
               "(review F11: the SE event rows, not the deltas)"
        se_imp = _events_of(evs, "Impact")
        raised = len(se_imp)
        ip = _join_events(se_imp, _events_of(sn_ev, "Impact"))
        idist = [x for x in (_dist(a.get("posSn"), b.get("posSn")) for a, b in ip) if x is not None]
        if not raised and not ip:
            row(rows, "S7.N4.impact", "MISSING", "no impacts", "n = OnHit, <= 0.1 m", what, "n4.gun")
        else:
            ok = len(ip) == raised and raised > 0 and idist and max(idist) <= POS_IMPACT_M
            row(rows, "S7.N4.impact", "PASS" if ok else "FAIL", "%d/%d, pos %s" % (len(ip), raised, None if not idist else round(max(idist), 3)),
                "n = OnHit, <= 0.1 m", what, "n4.gun")
    # explosion
    what = "S7.N4.explosion: at the blast position within 0.5 m, scale proportional to the radius within +-10 %, source logged"
    ew = L.windows("n4.explosion.")
    ep = []
    for name, t0, t1 in ew:
        ep += _join_events(L.in_ticks(_events_of(se_ev, "Explosion"), t0, t1, "tick"), _events_of(sn_ev, "Explosion"))
    if not ep:
        row(rows, "S7.N4.explosion", "MISSING", "no joined explosions", "<= 0.5 m, +-10 %", what, "n4.explosion")
    else:
        dd = [x for x in (_dist(a.get("posSn"), b.get("posSn")) for a, b in ep) if x is not None]
        ratios = [(_num(b, "scale") or 0) / (_num(a, "size") or 1) for a, b in ep if _num(a, "size")]
        mid = _p(ratios, 0.5) or 0
        spread = max(abs(r / mid - 1.0) for r in ratios) if mid else 1.0
        sources = [b.get("source") for _a, b in ep]
        ok = dd and max(dd) <= POS_EXPLOSION_M and spread <= SCALE_TOL and all(sources)
        row(rows, "S7.N4.explosion", "PASS" if ok else "FAIL", "pos %s, scale spread %.0f %%" % (None if not dd else round(max(dd), 2), spread * 100),
            "<= 0.5 m, <= 10 %", what, "n4.explosion", sources=sorted(set(str(s) for s in sources))[:3])
    # tools
    what = "S7.N4.tools: each tool sustain shown >= 95 % of its active window, gone <= 0.6 s after the stop"
    sus = L.sn_ev("s7.fx.sustain")
    tws = L.windows("n4.tool.")
    if not tws or not sus:
        row(rows, "S7.N4.tools", "MISSING", "no s7.fx.sustain rows", ">= 95 %, <= 0.6 s", what, "n4.tool")
    else:
        why = []
        for name, t0, t1 in tws:
            tname = name.split(".")[-1]
            rs = sorted([d for d in sus if _type_name(d.get("type")) == tname and t0 <= (_num(d, "seTick") or -1) <= t1 + 2 * TICKS_PER_S],
                        key=lambda d: _num(d, "seTick"))
            shown, on_at = 0.0, None
            gone_at = None
            for d in rs:
                st, t = d.get("state"), _num(d, "seTick")
                if st in ("start", "refresh") and on_at is None:
                    on_at = t
                elif st in ("stop", "fade") and on_at is not None:
                    shown += max(0.0, min(t, t1) - max(on_at, t0))
                    on_at = None
                    if t >= t1:
                        gone_at = t
            if on_at is not None:
                shown += max(0.0, t1 - max(on_at, t0))
            sh = shown / float(max(1, t1 - t0))
            if sh < TOOL_SHARE:
                why.append("%s shown %s" % (tname, _pct(sh)))
            if gone_at is None or (gone_at - t1) > TOOL_GONE_S * TICKS_PER_S:
                why.append("%s gone %s" % (tname, None if gone_at is None else gone_at - t1))
        row(rows, "S7.N4.tools", "PASS" if not why else "FAIL", ", ".join(why)[:34] or "%d tools ok" % len(tws), ">= 95 %, <= 0.6 s", what, "n4.tool",
            why=why)
    # grid impact, hull break, splash
    for rid, wname, tname, tol, extra in (("S7.N4.gridimpact", "n4.drop.ship", "GridImpact", POS_GRID_M, "within 0.5 s of contact"),
                                          ("S7.N4.hullbreak", "n4.breach", "HullBreak", POS_GRID_M, "bubbles >= 2 s"),
                                          ("S7.N4.splash", "n4.drop.sea", "Splash", POS_SPLASH_M, "a Surface event")):
        w = L.window(wname)
        what = "%s: >= 1 %s %s, within %.1f m" % (rid, tname, extra, tol)
        if not w:
            row(rows, rid, "MISSING", "no %s window" % wname, ">= 1, <= %.1f m" % tol, what, wname)
            continue
        se_e = L.in_ticks(_events_of(se_ev, tname), w[1], w[2], "tick")
        pairs = _join_events(se_e, _events_of(sn_ev, tname))
        dd = [x for x in (_dist(a.get("posSn"), b.get("posSn")) for a, b in pairs) if x is not None]
        why = []
        if not pairs:
            why.append("none applied (SE %d)" % len(se_e))
        elif not dd or min(dd) > tol:
            why.append("pos %s" % (None if not dd else round(min(dd), 2)))
        if pairs and tname == "GridImpact":
            first_sn = min((_num(b, "seTick") or 0) - (_num(a, "tick") or 0) for a, b in pairs)
            if first_sn > GRIDIMPACT_S * TICKS_PER_S:
                why.append("late %d t" % first_sn)
        if pairs and tname == "HullBreak" and max((_num(b, "lifetimeMs") or 0) for _a, b in pairs) < HULL_MIN_S * 1000:
            why.append("bubbles < 2 s")
        if pairs and tname == "Splash" and not any((int(_num(a, "flags") or 0) & EVENT_FLAGS["Surface"]) for a, _b in pairs):
            why.append("no Surface flag")
        row(rows, rid, "PASS" if not why else "FAIL", ", ".join(why) or "%d applied, pos %.2f m" % (len(pairs), min(dd)),
            ">= 1, <= %.1f m" % tol, what, wname)
    # loss
    what = "S7.N4.loss: eventsMissed 0 with Subnautica at 30 fps (de.fx {action: fps, cap: 30}) during the stress, its burst " \
           "spread over 8 ticks (review F10)"
    sw = L.window("n4.stress")
    rs = L.sn_in(sn_fx, sw[1], sw[2] + TICKS_PER_S) if sw else []
    capped = any(isinstance(r.get("reply"), dict) and r["reply"].get("ok") and float((r.get("args") or {}).get("cap") or 0) > 0
                 for r in L.replies("n4.stress.fps", side="sn"))
    burst = any(isinstance(r.get("reply"), dict) and r["reply"].get("ok") for r in L.replies("n4.stress.burst", side="se"))
    if not rs:
        row(rows, "S7.N4.loss", "MISSING", "no s7.fx rows in n4.stress", "0", what, "n4.stress")
    else:
        missed = sum(_num(d, "missed") or 0 for d in rs)
        row(rows, "S7.N4.loss", "PASS" if missed == 0 else "FAIL", "missed %d%s%s" % (missed, "" if capped else ", no 30 fps cap",
                                                                                   "" if burst else ", no burst"),
            "0", what, "n4.stress", capped=capped, burst=burst)
    # rates
    what = "S7.N4.rates: exactly 128 accepted and 372 dropped in the stress tick, per-type caps held, the buffer empty next " \
           "tick, no row older than 8 ticks"
    rep = next((r.get("reply") for r in L.replies("n4.stress", side="se") if isinstance(r.get("reply"), dict) and r["reply"].get("ok")
                and int(_num(r.get("args") or {}, "ticks") or 1) <= 1 and r.get("label") != "n4.stress.burst"), None)
    srow = next((d for d in se_fx if d.get("why") == "stress"), None)
    src = rep or srow
    if not src:
        row(rows, "S7.N4.rates", "MISSING", "no stress reply/row", "128 + 372", what, "n4.stress")
    else:
        acc = _num(src, "accepted")
        drp = _num(src, "dropped") if not isinstance(src.get("dropped"), dict) else sum(float(v) for v in src["dropped"].values())
        per = src.get("perType") or src.get("sent") or {}
        caps_bad = [k for k, v in per.items() if k in EVENT_CAPS and float(v) > EVENT_CAPS[k]]
        why = []
        if acc != MAX_PER_TICK or drp != STRESS_N - MAX_PER_TICK:
            why.append("%s + %s" % (acc, drp))
        if caps_bad:
            why.append("caps %s" % ",".join(caps_bad))
        if (_num(src, "nextTickPending") or 0) != 0:
            why.append("buffer not empty")
        if (_num(src, "oldestTicks") or 0) > WINDOW_TICKS - 1:
            why.append("row older than 8 t")
        row(rows, "S7.N4.rates", "PASS" if not why else "FAIL", ", ".join(why) or "128 + 372", "128 + 372, caps, 0, <= 7 t", what, "n4.stress")
    # above
    what = "S7.N4.above: the thruster test at SN y +2 m: lights only, no Subnautica event sound (review F18b: the requests " \
           "counted by flag class; none allowed without Underwater but a surface splash)"
    aw = L.window("n4.above")
    all_au = sorted([(L.tick_of(d), d) for d in L.sn_ev("s7.audio") if L.tick_of(d) is not None], key=lambda x: x[0])
    au = [d for t, d in all_au if aw and aw[1] <= t <= aw[2]]
    if not aw or not au:
        row(rows, "S7.N4.above", "MISSING", "no s7.audio rows above water", "0 sounds", what, "n4.above")
    elif any(_num(d, "allowedDry") is None for d in au):
        row(rows, "S7.N4.above", "MISSING", "no request counters", "0 sounds", what, "n4.above")
    else:
        # S7-1 fix: the rule is the gate by flag class: no sound for a request without Underwater (allowedDry,
 # the splash exception apart). An s7.audio row's `events` are the starts since the previous row: the S7-1 window's
 # first row carried the stress burst's underwater starts of the seconds before it (16 = WeaponMuzzle 8 + GridImpact
 # 8, rows at depth 22, allowed while the bus was still up), which the old count read as the above-water test's.
 # Underwater starts in the window (a submerged ship) are reported, not failed; the counters grow from the last row
 # before the window (else the first row in it)
        base = next((d for t, d in reversed(all_au) if t < aw[1]), None)
        first = base if base is not None and _num(base, "allowedDry") is not None else au[0]
        allowed_dry = (_num(au[-1], "allowedDry") or 0) - (_num(first, "allowedDry") or 0)
        dry_req = (_num(au[-1], "requestsDry") or 0) - (_num(first, "requestsDry") or 0)
        if all(_num(d, "startedTotal") is not None for d in au) and _num(first, "startedTotal") is not None:
            wet = (_num(au[-1], "startedTotal") or 0) - (_num(first, "startedTotal") or 0)
        else:
            wet = sum(sum(float(v) for k, v in (d.get("events") or {}).items() if k != "Splash") for d in au[1:])
        refused = sum(sum(float(v) for v in (d.get("refused") or {}).values()) for d in au[1:])
        thr = _sumd(L.sn_in(sn_fx, aw[1], aw[2]), "applied", "Thruster")    # the injected thruster at y +2 drew its light
        status = "FAIL" if allowed_dry else ("PASS" if thr > 0 else "MISSING")
        row(rows, "S7.N4.above", status, "%d allowed dry, %d dry asked%s" % (allowed_dry, dry_req, "" if thr else ", no thruster"),
            "0 allowed dry", what, "n4.above", dryRequests=dry_req, wetStarts=wet, refused=refused, thrusterApplied=thr)
    caps = [r for r in L.events("capture") if str(r.get("name", "")).startswith("n4.")]
    row(rows, "S7.N4.capture", "INFO", "%d captures" % len(caps), "owner look", "S7.N4 one capture per effect kind looked at",
        "n4", required=False)

# ----------------------------------------------------------------------------------------------------------------
# S7.WM-5 day/night, N5 statics, N6 bones, N7 census, N9 filter, N10 save, N11 audio
# ----------------------------------------------------------------------------------------------------------------

def wm5_rows(L, rows):
    if not L.ran("wm5"):
        return
    se = L.in_windows(L.se_ev("s7a.sun"), "wm5", "tick") or L.se_ev("s7a.sun")
    what = "S7.WM-5.bind: the bind-time checks logged and passed (|axis.U0| < 1e-3, |base.axis| < 1e-3, interval 20)"
    # S7-1 fix: the evidence is S7a's (SunSync.BindCore): the s7a.sun row why "bind" with `checks`, and one line "DE.Time
 # BIND OK..." or "DE.Time REFUSE <reasons> —..." (any reason: the axis, the base, rotation off, intervalMin; the old
 # rule matched only "REFUSE axis", so S7-1's "REFUSE intervalMin=119.999992 (want 20)" read as "0 refused")
    refused = [x for x in L.se_text if "DE.Time REFUSE" in x]
    bound = [x for x in L.se_text if "DE.Time BIND OK" in x]
    binds = [d for d in L.se_ev("s7a.sun") if d.get("why") == "bind"]
    if not se and not refused and not bound and not binds:
        row(rows, "S7.WM-5.bind", "MISSING", "no s7a.sun rows", "checks true", what, "wm5")
    else:
        why = []
        for x in refused:
            m = re.search(r"DE\.Time REFUSE (.*?)(?: — | - |$)", x)
            why.append((m.group(1) if m else x).strip())
        bad_rows = [d for d in (binds or se) if _flag(d, "checks") is False]
        ok = not refused and not bad_rows and (bool(bound) or bool(binds or se))
        row(rows, "S7.WM-5.bind", "PASS" if ok else "FAIL",
            ("; ".join(why) or ("%d rows, checks false" % len(bad_rows) if bad_rows else "%d rows, checks true" % len(binds or se)))[:34],
            "checks true", what, "wm5", refused=why[:3], bindRows=len(binds))
    sn = sorted([d for d in (L.in_windows(L.sn_ev("s7a.sun"), "wm5", "c6Tick") or L.sn_ev("s7a.sun")) if _flag(d, "drive") is not False],
                key=lambda d: _num(d, "c6Tick") or 0)
    what = "S7.WM-5.isday: Subnautica IsDay() true exactly when the SE sun is above the horizon (+- one sample, beyond +-0.3 deg)"
    if len(sn) < WM5_MIN_SAMPLES:
        row(rows, "S7.WM-5.isday", "MISSING", "%d driven samples" % len(sn), ">= %d samples" % WM5_MIN_SAMPLES, what, "wm5")
    else:
        bad = 0
        for i, d in enumerate(sn):
            e = _num(d, "c6ElevDeg")
            if e is None or abs(e) <= ISDAY_GUARD_DEG or _flag(d, "isDay") == (e > 0):
                continue
            neigh = [_num(sn[j], "c6ElevDeg") for j in (i - 1, i + 1) if 0 <= j < len(sn)]
            if any(n is not None and (n > 0) != (e > 0) for n in neigh):
                continue    # +- one sample at a sign change
            bad += 1
        row(rows, "S7.WM-5.isday", "PASS" if bad == 0 else "FAIL", "%d samples, %d bad" % (len(sn), bad), "0 bad", what, "wm5")
    what = "S7.WM-5.monotonic: timePassedAsDouble never decreases"
    tp = [_num(d, "timePassed") for d in sn if _num(d, "timePassed") is not None]
    if len(tp) < 2:
        row(rows, "S7.WM-5.monotonic", "MISSING", "no timePassed", "never decreases", what, "wm5")
    else:
        back = [round(b - a, 3) for a, b in zip(tp, tp[1:]) if b < a]
        row(rows, "S7.WM-5.monotonic", "PASS" if not back else "FAIL", "%d back steps" % len(back), "0", what, "wm5", steps=back[:3])

def _site_from_rays(name, d):
    """Review F6: one site's verdict tuple (name, staticRays, seMiss, hostHits, bothHit, p95, firstHitNotHost) from SE's
 reply with per-ray rows (diff of the first host hit; a static ray without one counts as +inf), else from its counters."""
    rays = d.get("rays") if isinstance(d.get("rays"), list) else None
    per = [r for r in (rays or []) if isinstance(r, dict) and "host" in r]
    if per and any("static" in r for r in per):
        st = [r for r in per if r.get("static") is True]
        diffs = [float(r["diff"]) if r.get("host") and r.get("diff") is not None else float("inf") for r in st]
        miss = sum(1 for r in st if not r.get("host"))
        return (name, len(st), miss, sum(1 for r in per if r.get("host")), sum(1 for r in st if r.get("host")), _p(diffs, 0.95),
                sum(1 for r in per if r.get("hit") and not r.get("firstHost")))
    st = _num(d, "snStatic")
    if st is None:
        return None
    miss = _num(d, "seMissWhereSnStatic") or 0
    p95 = _num(d, "p95DiffM")
    if st and miss / st > 1.0 - 0.95:
        p95 = float("inf")      # more than 5 % of the static rays have no SE host: the 95th percentile is a miss (+inf)
    return (name, st, miss, _num(d, "hostHits") or 0, _num(d, "bothHit") or 0, p95, _num(d, "firstHitNotHost") or 0)

def _char_distance(L, window):
    """S7-1: the distance (m) from the camera of an n5 site window (its de.teleport args) to the SE character's last
 teleport before it (de.swim teleportChar), None when either is unknown (session rows in time order)."""
    char = None
    for r in L.session:
        if r.get("ev") != "reply":
            continue
        a = r.get("args") or {}
        if r.get("cmd") == "de.swim" and isinstance(a.get("teleportChar"), list) and len(a["teleportChar"]) == 3:
            char = a["teleportChar"]
        if r.get("cmd") == "de.teleport" and window in (r.get("label"), a.get("label")):
            cam = [_num(a, "x"), _num(a, "y"), _num(a, "z")]
            return _dist(cam, char) if char and None not in cam else None
    return None

def n5_rows(L, rows):
    if not L.ran("n5"):
        return
    what = "S7.N5.rays (review F6): 200 rays per census site from Subnautica aimed at its static colliders, cast by both " \
           "games; over the rays whose Subnautica hit is a static, SE's first statics-host hit: P95 |d_SE - d_SN| <= 0.1 m " \
           "(no host = +inf) and no host along <= 5 % of them (terrain or fish in front no longer count)"
    # SE's reply with the per-ray rows (static, host, diff), else SE's s7.statics state rays row of that site (S7-1 fix: by
 # its `site` field only: the S7-1 rows carried the statics class's own tick counter, ~590 ticks ahead of C1, and were
 # filed under the next site's window). project decision: no Aurora site (its sites read INFO, skipped)
    sites, skipped, no_sn, anchors = [], [], [], {}
    for name, t0, t1 in L.windows("n5."):
        if name == "n5.census":
            continue
        site = name.split(".", 1)[1]
        if "aurora" in site.lower():
            skipped.append(site)
            continue
        snr = [r.get("reply") for r in L.replies("%s.sn.rays" % name) if isinstance(r.get("reply"), dict)]
        if snr and not snr[-1].get("ok"):
            no_sn.append("%s: %s" % (site, str(snr[-1].get("error") or "no rays")[:60]))
            continue
        reps = [r.get("reply") for r in L.replies("%s.se.rays" % name) if isinstance(r.get("reply"), dict) and r["reply"].get("ok")]
        agg = [d for d in L.se_ev("s7.statics") if d.get("state") == "rays" and d.get("site") == site]
        got = None
        for d in reps[-1:] + agg[-1:]:
            got = got or _site_from_rays(name, d)
            if _num(d, "nearestAnchorM") is not None:
                anchors[site] = _num(d, "nearestAnchorM")
        if got and site not in anchors:
            cd = _char_distance(L, name)    # an older SE reply has no nearestAnchorM: the last character teleport tells
            if cd is not None:
                anchors[site] = cd
        if got and got[3] == 0 and anchors.get(site) is not None and anchors[site] > 300.0:
            # S7-1 fix: SE raises statics hosts only within 300 m of an anchor (a mirrored grid or an online character), never
 # around the spectator camera; with the character this far away nothing was measured at this site
            no_sn.append("%s: no SE anchor within 300 m (character %.0f m away)" % (site, anchors[site]))
            continue
        if got:
            sites.append(got)
    if not sites:
        se, sn = {}, {}
        for d in L.se_ev("s7.statics.rays"):
            for r in d.get("rays") or []:
                se[(d.get("site"), r.get("id"))] = r
        for d in L.sn_ev("s7.statics.rays"):
            for r in d.get("rays") or []:
                sn[(d.get("site"), r.get("id"))] = r
        for site in sorted(set(k[0] for k in sn if k[0] != "census")):
            keys = [k for k in sn if k[0] == site and k in se and sn[k].get("static", True)]
            host = [k for k in keys if se[k].get("distSe") is not None and str(se[k].get("host") or "").startswith("DE.Statics.")]
            diffs = [abs(float(se[k]["distSe"]) - float(sn[k].get("distSn") or 0)) if k in host else float("inf") for k in keys]
            sites.append(("n5." + site, len(keys), len(keys) - len(host), len(host), len(host), _p(diffs, 0.95), 0))
    if not sites:
        row(rows, "S7.N5.rays", "MISSING", ("; ".join(no_sn) or "no ray results")[:34], "p95 <= 0.1 m", what, "n5",
            notMeasured=no_sn, skippedSites=skipped)
    else:
        bad = [s for s in sites if not s[1] or s[5] is None or s[5] > RAY_P95_M or s[2] / float(s[1]) > RAY_SE_MISS_MAX]
        # S7-1 fix: a site where SE built no host at all: hosts follow anchors (mirrored grids, online characters) within
 # 300 m, never the spectator camera (S7-1: the character stayed 1 km away)
        far = ["%s anchor %.0f m" % (s[0].split(".")[-1], anchors[s[0].split(".", 1)[1]]) for s in sites
               if s[3] == 0 and anchors.get(s[0].split(".", 1)[1]) is not None and anchors[s[0].split(".", 1)[1]] > 300.0]
        meas = "; ".join(far + ["%s miss %d/%d, p95 %s" % (s[0].split(".")[-1], s[2], s[1], None if s[5] is None else
                                                            ("inf" if s[5] == float("inf") else round(s[5], 3))) for s in sites])
        # a site without Subnautica statics within 40 m is a site choice, not a result: the row is not judged complete
        row(rows, "S7.N5.rays", "FAIL" if bad else ("MISSING" if no_sn else "PASS"), meas[:34], "<= 0.1 m, <= 5 % miss", what, "n5",
            sites=[list(s) for s in sites], notMeasured=no_sn, skippedSites=skipped, anchors=anchors)
    what = "S7.N5.census: with the census cache, a grid placed against a wreck 1 km away rests on the cached host after 5 s " \
           "(an SE ray from it hits the host)"
    probe = [r.get("reply") for r in L.replies("n5.census.se.probe") if isinstance(r.get("reply"), dict)]
    cs = [x for d in L.se_ev("s7.statics.rays") if d.get("site") == "census" for x in (d.get("rays") or [])]
    if probe:
        p = probe[-1]
        ok = bool(p.get("ok")) and _flag(p, "hit") and _flag(p, "host")
        row(rows, "S7.N5.census", "PASS" if ok else "FAIL", "hit %s host %s %s" % (p.get("hit"), p.get("host"), str(p.get("name"))[:12]),
            "hits a statics host", what, "n5.census")
    elif cs:
        ok = all(r.get("distSe") is not None and str(r.get("host") or "").startswith("DE.Statics.") for r in cs)
        row(rows, "S7.N5.census", "PASS" if ok else "FAIL", "%d rays" % len(cs), "hits a statics host", what, "n5.census")
    else:
        row(rows, "S7.N5.census", "MISSING", "no census probe", "hits a statics host", what, "n5.census")

def _extra_bone_err(by_tick, sn):
    """S7-1 diagnostic: the extra rows 78.. (posed flat from C1's absolute matrices on every source path) against SE's row
 of seTick and of seTick - 1: {shift: [frames, median m, max m]} (the S7-1 run: shift -1 fitted best, 1.1 / 2.7 cm,
 shift 0 2.8 / 4.7 cm: HYPOTHESIS, SE's row of tick T holds the pose C1 sends with T + 1)."""
    out = {}
    for shift in (0, -1):
        errs = []
        for d in sn:
            t = _num(d, "seTick")
            w = d.get("world")
            if t is None or not w or len(w) <= CANON_BONES:
                continue
            wa = (by_tick.get(int(t) + shift) or {}).get("world")
            if not wa or len(wa) <= CANON_BONES:
                continue
            e = [_dist(wa[i], w[i]) for i in range(CANON_BONES, min(len(wa), len(w)))]
            e = [x for x in e if x is not None]
            if e:
                errs.append(max(e))
        if errs:
            out[str(shift)] = [len(errs), round(_p(errs, 0.5), 4), round(max(errs), 4)]
    return out

def n6_rows(L, rows):
    if not L.ran("n6"):
        return
    se = L.se_ev("s7.bones")
    sn = L.sn_ev("s7.bones")
    lines = [x for x in L.se_text if x.startswith("DE.S7 bones ") or " DE.S7 bones " in x]
    what = "S7.N6.count: CharacterAdd, SkinnedBind and C1 boneCount = Model.Bones.Length = 81 for each model (DE.S7 bones)"
    if not se and not sn:
        row(rows, "S7.N6.count", "MISSING", "no s7.bones rows", "81 = 81 = 81", what, "n6")
    else:
        bad = []
        for d in se:
            vals = [_num(d, "boneCount"), _num(d, "modelBones"), _num(d, "addBoneCount"), _num(d, "bindBones")]
            if any(v is not None and v != BONES for v in vals) or _num(d, "boneCount") != BONES:
                bad.append(("se", vals))
        for d in sn:
            vals = [_num(d, "boneCount"), _num(d, "addBoneCount"), _num(d, "bindBones")]
            if any(v != BONES for v in vals) or _flag(d, "countsAgree") is False:
                bad.append(("sn", vals))
        models = sorted(set(m.group(1) for m in (re.search(r"DE\.S7 bones (\S+) (\d+) sent=(\d+)", x) for x in lines) if m))
        line_bad = [x for x in lines if re.search(r"DE\.S7 bones \S+ \d+ sent=", x) and "boneCount==Model.Bones.Length PASS" not in x]
        ok = not bad and not line_bad and models
        row(rows, "S7.N6.count", "PASS" if ok else "FAIL", "%d bad rows, %d models, %d bad lines" % (len(bad), len(models), len(line_bad)),
            "81 everywhere", what, "n6", models=models, bad=bad[:3])
    what = "S7.N6.pos: |SE bone world (mapped) - Subnautica bone| <= 1 cm for all 81 bones on all frames (source SeBones): " \
           "alpha >= 0.999 against SE's row of seTick, else against SE's rows of seTick - 1 and seTick lerped by alpha " \
           "(review F12, as S5 n3_rows)"
    by_tick = {int(_num(d, "tick")): d for d in se if _num(d, "tick") is not None and d.get("world")}
    pairs = []
    interp = 0
    sb_rows = []
    for d in sn:
        t = _num(d, "seTick")
        if t is None or str(d.get("source", "SeBones")) != "SeBones" or not d.get("world"):
            continue
        t = int(t)
        a = _num(d, "alpha")
        a = 1.0 if a is None else a
        sb_rows.append(d)
        newer = by_tick.get(t)
        if newer is None:
            continue
        if a >= BONE_ALPHA_MIN:
            pairs.append((newer["world"], d))
            continue
        older = by_tick.get(t - 1)
        if older is None:
            continue
        wo, wn = older["world"], newer["world"]
        lerped = [[wo[i][k] + (wn[i][k] - wo[i][k]) * a for k in range(3)] for i in range(min(len(wo), len(wn)))]
        pairs.append((lerped, d))
        interp += 1
    if len(pairs) < BONE_MIN_FRAMES:
        # S7-1 fix: say why nothing joined. S7-1: all 60 Subnautica rows had source Retarget (path G, by design while the
 # character swims: the de.s3 room placed at n1c had closed the n6 deck, so the walk ran in the water), so no SeBones
 # frame existed to compare; the extra rows 78-80 (posed from C1's matrices on any path) are reported as detail
        srcs = {}
        for d in sn:
            srcs[str(d.get("source"))] = srcs.get(str(d.get("source")), 0) + 1
        why = "%d joined frames" % len(pairs)
        if sn and not sb_rows:
            why = "source %s on %d/%d frames" % ("/".join(sorted(srcs)), len(sn), len(sn))
        extra = _extra_bone_err(by_tick, sn)
        row(rows, "S7.N6.pos", "MISSING", why[:34], ">= %d frames" % BONE_MIN_FRAMES, what, "n6", sources=srcs, extraRows=extra)
    else:
        worst, nb = 0.0, 0
        for wa, b in pairs:
            wb = b["world"]
            nb = max(nb, min(len(wa), len(wb)))
            for i in range(min(len(wa), len(wb))):
                dd = _dist(wa[i], wb[i])
                worst = max(worst, float("inf") if dd is None else dd)
        ok = worst <= BONE_POS_M and nb >= BONES
        row(rows, "S7.N6.pos", "PASS" if ok else "FAIL", "%d frames (%d lerped), %d bones, max %.4f m" % (len(pairs), interp, nb, worst),
            "<= 0.01 m, 81 bones", what, "n6")
    what = "S7.N6.tear: every vertex's weights sum to 1 +- 1e-4 with indices < boneCount"
    tears = [t for d in sn for t in (d.get("tear") or [])]
    if not tears:
        row(rows, "S7.N6.tear", "MISSING", "no tear data", "0 bad", what, "n6")
    else:
        bad = [t for t in tears if not t.get("valid", True) or (t.get("badSum") or 0) or (t.get("badIndex") or 0)]
        row(rows, "S7.N6.tear", "PASS" if not bad else "FAIL", "%d parts, %d bad" % (len(tears), len(bad)), "0 bad", what, "n6")

def n7_rows(L, rows):
    if not L.ran("n7"):
        return
    what = ("S7.N7 census completeness (sn_prefab_colliders.py --census): every class id the BatchObjectsCache references "
            "counted; each handled (allow-listed) or skipped with a reason; FAIL on an unlisted class")
    ev = next((r for r in L.events("census") if r.get("path")), None) or {}

    def found(*cands):
        for c in cands:
            if c and os.path.exists(c):
                return c
        return None
    path = found(ev.get("path"), ev.get("path") and os.path.join(L.folder, os.path.basename(ev["path"])), os.path.join(L.folder, "s7-census.json"))
    log = found(ev.get("log"), ev.get("log") and os.path.join(L.folder, os.path.basename(ev["log"])), os.path.join(L.folder, "s7-census.txt"))
    verdict = None
    if log:
        with open(log, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = re.search(r"(PASS|FAIL) S7\.N7\.census(.*)", line)
                if m:
                    verdict = (m.group(1), m.group(2).strip(": ").strip())
    doc = None
    if path:
        try:
            with open(path, encoding="utf-8") as f:
                doc = json.load(f)
        except (OSError, ValueError):
            doc = None
    if verdict is None and doc is None:
        row(rows, "S7.N7.census", "MISSING", "no census output", "0 unlisted", what, "n7")
        return
    if verdict is not None:
        s = (doc or {}).get("summary") or {}
        row(rows, "S7.N7.census", verdict[0], (verdict[1] or "handled %s skipped %s" % (s.get("handled"), s.get("skipped")))[:34],
            "0 unlisted", what, "n7", rc=ev.get("rc"), summary=s)
        return
    if "classes" in doc and isinstance(doc["classes"], dict):     # the tool's document without its printed verdict
        bad = [cid for cid, e in doc["classes"].items() if e.get("status") not in ("handled", "skip") or (e.get("status") == "skip" and not e.get("reason"))]
        ok = not bad and ev.get("rc") in (0, None) and len(doc["classes"]) > 0
        row(rows, "S7.N7.census", "PASS" if ok else "FAIL", "%d classes, %d unlisted, rc %s" % (len(doc["classes"]), len(bad), ev.get("rc")),
            "0 unlisted", what, "n7", unlisted=bad[:5])
        return
    unlisted = doc.get("unlisted") or []
    no_reason = [x for x in (doc.get("skipped") or []) if not x.get("reason")]
    ok = not unlisted and not no_reason and doc.get("counted") == doc.get("classes") and (doc.get("classes") or 0) > 0
    row(rows, "S7.N7.census", "PASS" if ok else "FAIL", "%s/%s counted, %d unlisted" % (doc.get("counted"), doc.get("classes"), len(unlisted)),
        "all counted, 0 unlisted", what, "n7", unlisted=unlisted[:5])

def n9_rows(L, rows):
    if not L.ran("n9"):
        return
    st = L.sn_in_windows(L.sn_ev("s7.statics"), "n9")
    what = ("S7.N9 statics filter: the frozen reaper (60 m, kinematic by FreezeRigidbodyWhenFar) filtered by its entity type "
            "(never reported), nothing inside a proxy section reported; the wreck batch near the camera reports >= 1 primitive")
    if not st:
        row(rows, "S7.N9.filter", "MISSING", "no s7.statics rows", "filtered, 0, >= 1", what, "n9")
        return

    def fmax(names):     # the reporter's filtered counts are cumulative: the largest of the window
        return max(sum(float((d.get("filtered") or {}).get(n) or 0) for n in names) for d in st)
    # review F17: the reporter's own counters (cumulative): primitives under a Creature (only S7.M7) and primitives whose
 # centre lies in an S6 proxy section box; a row without them leaves the check MISSING (never a vacuous PASS)
    if any(_num(d, "creaturesReported") is None or _num(d, "insideProxy") is None for d in st):
        row(rows, "S7.N9.filter", "MISSING", "no creaturesReported/insideProxy", "filtered, 0, 0, >= 1", what, "n9")
        return
    by_type = fmax(("creature", "Creature", "freezeWhenFar"))
    creatures = max(_num(d, "creaturesReported") or 0 for d in st)
    proxy = max(_num(d, "insideProxy") or 0 for d in st)
    prims = max(_num(d, "primitives") or 0 for d in st)
    ok = by_type >= 1 and creatures == 0 and proxy == 0 and prims >= 1
    row(rows, "S7.N9.filter", "PASS" if ok else "FAIL", "by type %d, reported %d, proxy %d, prims %d" % (by_type, creatures, proxy, prims),
        "filtered >= 1, 0, 0, >= 1", what, "n9")

def n10_rows(L, rows):
    if not L.ran("n10"):
        return
    what = "S7.N10 save content (throwaway copy): >= 1 host alive when the owner saved; no DE.Statics name and no host " \
           "entity id in the save files"
    chk = next((r for r in L.events("savecheck")), None)
    created = [d for d in L.se_ev("s7.statics") if d.get("state") == "created"]
    if not chk:
        row(rows, "S7.N10.save", "MISSING", "no save check (owner save)", "0 hits", what, "n10")
        return
    hits = int(chk.get("hits") or 0)
    alive = int(chk.get("hostsAlive") or len(created))
    row(rows, "S7.N10.save", "PASS" if hits == 0 and alive >= 1 and (chk.get("files") or 0) > 0 else "FAIL",
        "%d files, %d hits, %d hosts" % (chk.get("files") or 0, hits, alive), "0 hits, >= 1 host", what, "n10")

def n11_rows(L, rows):
    if not L.ran("n11"):
        return
    se = L.in_windows(L.se_ev("s7.audio"), "n11", "tick") or L.se_ev("s7.audio")
    what = "S7.N11.muffle: every modifiable 3D emitter muffled while seMixActive and not in a room, none above water or in " \
           "the room (100 % of SE s7.audio rows); INFO in an Arcade-sound world (review F2: SE's effect path is off there)"
    arcade = [d for d in se if _flag(d, "realisticSound") is False]
    if not se:
        row(rows, "S7.N11.muffle", "MISSING", "no SE s7.audio rows", "100 %", what, "n11")
    elif arcade:
        unexpected = sum(_num(d, "muffled") or 0 for d in arcade)
        # the postfix must force nothing in Arcade: a muffled count there is the F2 regression (looping sounds restarted)
        row(rows, "S7.N11.muffle", "INFO" if unexpected == 0 else "FAIL", "arcade world, %d muffled" % unexpected, "100 %", what, "n11",
            required=unexpected != 0, arcadeRows=len(arcade))
    else:
        bad, muffled_rows = [], 0
        for d in se:
            expect = _flag(d, "mixActive") and not _flag(d, "inRoom") and d.get("effect") != "off"
            mod, muf = _num(d, "modifiable"), _num(d, "muffled") or 0
            if expect:
                if mod is None:
                    mod = _num(d, "emitters") or 0
                if muf < mod or (mod > 0 and muf == 0):
                    bad.append({"tick": d.get("tick"), "modifiable": mod, "muffled": muf})
                muffled_rows += muf > 0
            elif muf > 0:
                bad.append({"tick": d.get("tick"), "unexpected": muf})
        ok = not bad and muffled_rows > 0
        row(rows, "S7.N11.muffle", "PASS" if ok else "FAIL", "%d rows, %d bad, %d muffled" % (len(se), len(bad), muffled_rows),
            "100 %", what, "n11", bad=bad[:3])
    sn = L.sn_in_windows(L.sn_ev("s7.audio"), "n11")
    what = "S7.N11.bus (review F18a): every sound request counted by flag class; none allowed without Underwater but the " \
           "documented surface-splash exception (allowedDry 0), `above` 0, sounds started under water"
    if not sn:
        row(rows, "S7.N11.bus", "MISSING", "no SN s7.audio rows", "0 allowed dry", what, "n11")
    elif any(_num(d, "allowedDry") is None for d in sn):
        row(rows, "S7.N11.bus", "MISSING", "no request counters", "0 allowed dry", what, "n11")
    else:
        above = _span(sn, "above")
        allowed_dry = _span(sn, "allowedDry")
        wet = sum(sum(float(v) for v in (d.get("events") or {}).values()) for d in sn if (_num(d.get("bus") or {}, "c5Target") or 0) > 0)
        dry_req = _span(sn, "requestsDry")
        status = "FAIL" if above or allowed_dry else ("PASS" if wet > 0 else "MISSING")
        # S7-1: the counters are wired (requests 351 underwater, 281 refused over the session) but n11's only event sound,
 # the engine loop, started before the descent while the camera was still above water (one start, then it loops):
 # no start under water to judge; the launcher now injects one under water and one dry request during n11
        meas = "above %d, allowed dry %d, under %d, dry asked %d" % (above, allowed_dry, wet, dry_req) if status != "MISSING" else \
            "no wet start (req %d wet, %d dry)" % (_span(sn, "requestsUnderwater"), dry_req)
        row(rows, "S7.N11.bus", status, meas, "0, 0, > 0", what, "n11", splashAllowed=_span(sn, "splashAllowed"))
    deep = [d for d in sn if _num(d, "depth") is not None]
    row(rows, "S7.N11.ambience", "INFO", "%d rows, biomes %s" % (len(deep), ",".join(sorted(set(str(d.get("biome")) for d in deep)))[:20]),
        "recorded", "S7.N11 biome and depth ambience (A5, unchanged) and the owner's ear", "n11", required=False,
        depths=[round(_num(d, "depth"), 1) for d in deep][:12])

# ----------------------------------------------------------------------------------------------------------------
# evaluation
# ----------------------------------------------------------------------------------------------------------------

def owner_gate(L, rows):
    """S7-1 fix: a row whose is a prefix) reads MISSING
 with the step's prompt when it would FAIL or is MISSING (a PASS stands: the evidence came anyway); an INFO row lists
 every skipped step."""
    skipped = L.owner_skipped()
    if not skipped:
        return
    for r in rows:
        deps = OWNER_DEPS.get(r["id"])
        if not deps or r["status"] not in ("FAIL", "MISSING"):
            continue
        hit = [s for s in sorted(skipped) if any(s == d or (d.endswith(".") and s.startswith(d)) for d in deps)]
        if not hit:
            continue
        was = "%s %s" % (r["status"], r.get("measured"))
        r["status"] = "MISSING"
        r["measured"] = ("owner step %s skipped" % hit[0])[:34]
        det = r.setdefault("detail", {}) if isinstance(r.get("detail"), dict) or "detail" not in r else None
        if det is not None:
            det["ownerSkipped"] = hit
            det["ownerPrompt"] = skipped[hit[0]]
            det["wouldHaveBeen"] = was
    row(rows, "S7.owner", "INFO", "%d owner steps skipped" % len(skipped), "owner free play",
        "S7 owner steps this session skipped (the rows that need them read MISSING): " + ", ".join(sorted(skipped)),
        "owner", required=False, skipped=skipped)

def evaluate(folder):
    L = Logs(folder)
    rows = []
    for fn in (n1_rows, n1b_rows, n1c_rows, n2_rows, n3_rows, n4_rows, wm5_rows, n5_rows, n6_rows, n7_rows, n8_rows, n9_rows,
               n10_rows, n11_rows):
        try:
            fn(L, rows)
        except Exception as e:     # one check's bug must not hide the others
            row(rows, "S7.%s" % fn.__name__.replace("_rows", "").upper(), "FAIL", "analyser error %s" % type(e).__name__, "-",
                str(e)[:120], fn.__name__)
    owner_gate(L, rows)
    for m in L.start.get("mutants") or []:
        want = MUTANT_RED.get(m, ())
        got = [r for r in rows if r["id"] in want]
        for r in got:
            r["required"] = False      # judged by the.red row
        red = [r["id"] for r in got if r["status"] == "FAIL"]
        status = "MISSING" if not got else ("PASS" if len(red) >= 1 and all(r["status"] == "FAIL" for r in got if r["status"] != "MISSING") else "FAIL")
        if got and all(r["status"] == "INFO" for r in got):
            status = "INFO"            # not judgeable in this session
        row(rows, "%s.red" % m, status, "red: %s" % (",".join(red) or "none"), "red: %s" % ",".join(want),
            "seeded failure %s turns its rows red" % m, "mutants")
    return rows

def table(rows):
    return C.table(rows)

def write_results(folder, rows, strict=True, extra=None, name="s7") -> bool:
    ok = C.verdict(rows, strict)
    res = {"tool": "analyze_s7", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "%s-results.json" % name), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "%s-table.txt" % name), "w", encoding="utf-8") as f:
        f.write(C.table(rows) + "\n")
    return ok

# ----------------------------------------------------------------------------------------------------------------
# self-test: synthetic sessions in the log contract
# ----------------------------------------------------------------------------------------------------------------
SYNTH_PARTS = ("n1", "n1b", "n1c", "n2", "n3", "n4", "wm5", "n5", "n6", "n7", "n8", "n9", "n10", "n11")

class _Synth:
    """A synthetic S7 session: the launcher's parts, samples, replies and captures, SE rows and Subnautica rows."""

    def __init__(self, folder, mutant=None, arcade=False, idle=False):
        self.folder, self.m, self.arcade = folder, mutant, arcade
        self.idle = idle   # S7-1: an idle run: dependent rows MISSING, never FAIL
        self.tick, self.frame, self.seq = 1000, 5000, 0
        self.sess, self.se, self.sn = [], [], []
        os.makedirs(os.path.join(folder, "captures", "s7", "synth"), exist_ok=True)
        self.sess.append({"ev": "start", "kind": "s7", "parts": list(SYNTH_PARTS), "mutants": [mutant] if mutant else [],
                          "band": [1000.0, 1100.0], "world": "DE Lab", "settings": {"oxygen": True, "pressurization": True}})

    # plumbing
    def part(self, name, phase):
        self.sess.append({"ev": "part", "name": name, "phase": phase, "tick": self.tick})

    def se_row(self, ev, **kw):
        r = {"t": "2026-10-17T18:00:00Z", "ev": ev, "tick": self.tick}
        r.update(kw)
        self.se.append(r)

    def sn_row(self, ev, **kw):
        d = dict(kw)
        d.setdefault("snFrame", self.frame)
        self.sn.append({"t": "2026-10-17T18:00:00Z", "event": ev, "ev": ev, "data": d})

    def se_text(self, msg):
        self.se.append({"t": "2026-10-17T18:00:00Z", "msg": msg})

    def sn_text(self, msg):
        self.sn.append({"t": "2026-10-17T18:00:00Z", "event": "log", "msg": msg})

    def reply(self, side, cmd, label, args=None, reply=None):
        self.sess.append({"ev": "reply", "side": side, "cmd": cmd, "label": label, "args": args or {}, "reply": reply or {"ok": True}})

    def owner_skip(self, step):
        self.sess.append({"ev": "owner", "step": step, "phase": "skipped", "prompt": "synthetic prompt for %s" % step})

    def sample(self):
        self.sess.append({"ev": "sample", "c1Tick": self.tick, "c7": {"snFrame": self.frame, "presentedSeTick": self.tick}})

    def step(self, ticks=1):
        for _ in range(ticks):
            self.tick += 1
            self.frame += 1
            if self.tick % 6 == 0:
                self.sample()

    def png(self, name, colours):
        """A 32 x 32 capture: colours {(x, y): (r, g, b)} paint the 9 x 9 patch around each point, grey elsewhere."""
        rel = "s7/synth/%s.png" % name
        pts = list(colours.items())

        def px(x, y):
            for (cx, cy), c in pts:
                if abs(x - cx) <= 4 and abs(y - cy) <= 4:
                    return c
            return (90, 90, 90)
        write_png(os.path.join(self.folder, "captures", rel), 32, 32, px)
        self.sess.append({"ev": "capture", "name": name, "side": "sn", "path": rel})

    def ev_pair(self, tname, pos, size=1.0, flags=1 | 8, lifetime=500, scale=None, sn_off=0.0, lat=2):
        self.seq += 1
        t = EVENT_TYPES.index(tname)
        self.se_row("s7.fx.event", seq=self.seq, stream=65537, type=tname, flags=flags, entity=77, posSn=list(pos), size=size,
                    lifetimeMs=lifetime)
        self.sn_row("s7.fx.event", seTick=self.tick + 1, seq=self.seq, stream=65537, type=t, flags=flags, entity=77,
                    posSn=[pos[0] + sn_off, pos[1], pos[2] + 0.001], latencyFrames=lat, lifetimeMs=lifetime,
                    scale=scale if scale is not None else size,
                    source="Crash.detonateParticlePrefab" if tname == "Explosion" else "SeaMoth.bubbles")

    # parts
    def n1(self):
        m = self.m
        self.part("n1", "begin")

        def interior(in_room, processing=False, breached=False):
            self.se_row("s7.interior", inRoom=in_room, processing=processing, breached=breached, roomGridId=77 if in_room else 0,
                        roomIndex=3 if in_room else -1, roomO2=200 if in_room else 0, characterInRoom=False, candidates=1,
                        settings={"oxygen": True, "pressurization": True})
            self.sn_row("s7.interior", c1Tick=self.tick, inside=in_room, patched=True, roomGridId=77 if in_room else 0)

        def skies(in_room):
            inside_applied = "biome" if m == "S7.M5" else "interior"
            self.sn_row("s7.sky", entity=77, chunk=1, **{"class": "Inside", "applied": inside_applied, "ok": m != "S7.M5"})
            self.sn_row("s7.sky", entity=77, chunk=2, **{"class": "Boundary", "applied": "interior" if in_room else "biome", "ok": True,
                                                        "cameraInRoom": in_room})
            self.sn_row("s7.sky", entity=77, chunk=3, **{"class": "Exterior", "applied": "biome", "ok": True})

        for k in (1, 2):
            for name, in_room in (("n1.inside.%d" % k, True), ("n1.outside.%d" % k, False)):
                self.part(name, "begin")
                for i in range(20):
                    self.step(30)
                    interior(in_room)
                    if i % 5 == 0:
                        skies(in_room)
                self.part(name, "end")
        # fog A/B (camera inside)
        wall_on, beyond = (153, 128, 102), (25, 77, 102)
        wall_off = wall_on if m == "S7.M1" else (77, 115, 128)
        for state, wall in (("on", wall_on), ("off", wall_off)):
            name = "n1.fog.%s" % state
            self.part(name, "begin")
            self.reply("sn", "de.interior", "%s.patch" % name, {"patch": state})
            for _ in range(4):
                self.step(30)
                interior(True)
            for probe, px in (("a", "(40, 8)" if self.idle else "(8, 8)"), ("b", "(24, 24)")):   # S7-1: probe (a) off screen
                self.sn_text("DE.SnPixel label=%s.%s seTick=%d newestTick=%d frame=%d px=%s depth=1.5 screen=(32, 32)" % (
                    name, probe, self.tick, self.tick, self.frame, px))
            self.png(name, {(8, 8): wall, (24, 24): beyond if state == "on" else (beyond[0], beyond[1], beyond[2] + 1)})
            self.part(name, "end")

        # breach and re-seal, an interior block added and removed (the gas system recomputes each time)
        def recompute(name, before, after, breached_after, hold=2):
            self.part(name, "begin")
            if self.idle:     # S7-1: the project step skipped: the room stays sealed, nothing recomputes
                self.owner_skip(name)
                for _ in range(5):
                    self.step(30)
                    interior(True)
                self.part(name, "end")
                return
            self.step(30)
            for i in range(hold):
                flick = (m == "S7.M10") and i % 2 == 0
                interior(before if not flick else not before, processing=True, breached=False)
                self.step(10)
            interior(after, processing=False, breached=breached_after)
            for _ in range(5):
                self.step(30)
                interior(after, breached=breached_after)
            self.part(name, "end")

        # review F8: the door toggled with the camera inside (the gas system recomputes; the hold keeps the flag; S7.M10's
 # GetRooms flag flickers with no processing bit)
        self.step(30)
        interior(True)
        self.part("n1.door", "begin")
        for i in range(5):
            self.reply("se", "de.interior", "n1.door.%d" % (i + 1), {"action": "door", "block": "901", "open": i % 2 == 0},
                       {"ok": True, "working": True, "gasProcessing": False})
            self.step(30)
            if m == "S7.M10":
                interior(False, processing=False)
                self.step(5)
                interior(True, processing=False)
            else:
                interior(True, processing=True)
                self.step(5)
                interior(True, processing=False)
            self.step(100)
        self.part("n1.door", "end")
        recompute("n1.breach.remove", True, False, True)
        recompute("n1.breach.replace", False, True, False)
        self.se_row("s7.rooms", entity=77, boxes=1, truncated=False, hash="0x1234", sent=True)
        recompute("n1.interior.add", True, True, False, hold=3)
        recompute("n1.interior.remove", True, True, False, hold=3)
        self.part("n1", "end")

    def n1b(self):
        self.part("n1b", "begin")
        self.se_text("DE.Interior OFF pressurization disabled (EnableOxygen=True EnableOxygenPressurization=False): the flag stays false")
        self.part("n1b.inside", "begin")
        for _ in range(10):
            self.step(30)
            self.se_row("s7.interior", inRoom=False, processing=False, breached=False, roomGridId=0, characterInRoom=False,
                        settings={"oxygen": True, "pressurization": False})
        self.part("n1b.inside", "end")
        self.part("n1b", "end")

    def n1c(self):
        self.part("n1c", "begin")
        self.part("n1c.dry", "begin")
        for _ in range(10):
            self.step(30)
            self.se_row("s7a.loco", state="Walk", inWater=False, vF=450, flags=1)
        self.part("n1c.dry", "end")
        self.part("n1c.walk", "begin")
        if self.idle:     # S7-1: the walk skipped; the entry row on the window's first tick says not yet in the room
            self.owner_skip("n1c.walk")
            self.se_row("s7.interior", inRoom=True, characterInRoom=False, processing=False, breached=False)
        for _ in range(20):
            self.step(30)
            self.se_row("s7.interior", inRoom=True, characterInRoom=True, processing=False, breached=False)
            self.se_row("s7a.loco", state="Idle" if self.idle else "Walk", inWater=False, vF=0 if self.idle else 420, flags=1)
        self.part("n1c.walk", "end")
        self.part("n1c", "end")

    def n2(self):
        self.part("n2", "begin")
        # review F4: the always-on readers both ways, the conditional ones (rare) absent, UnstuckPlayer only outside; under
 # S7.M1 the inside trace never sees an inside frame (its own window field says "outside")
        always = READERS_ALWAYS + ("uGUI_AdaptiveBrightness", "TemperatureDamage", "Player")
        if self.idle:     # S7-1: the deployed trace names LargeWorldStreamer's nested caller "Settings"
            always = tuple("Settings" if r == "LargeWorldStreamer" else r for r in always)
        m1 = self.m == "S7.M1"
        for window, attack in (("inside", m1), ("outside", True)):
            self.part("n2.%s" % window, "begin")
            self.step(600)
            inside = window == "inside" and not m1
            r_in = {n: 3 for n in always} if inside else {}
            r_out = dict({n: 2 for n in always}, UnstuckPlayer=1) if not inside else {}
            self.sn_row("s7.inside", window="inside" if inside else "outside", insideFrames=600 if inside else 0,
                        outsideFrames=0 if inside else 600, readers=r_in if inside else r_out, readersInside=r_in, readersOutside=r_out,
                        ghost={"o2": 0.9, "c1O2": 0.9, "health": 100.0, "pos": [0, -10, 0], "posErrM": 0.01}, canBeAttacked=attack,
                        noAggression=False, music=["interior" if window == "inside" else "biome"],
                        brightness=0.6 if window == "inside" else 0.8)
            self.part("n2.%s" % window, "end")
        self.part("n2", "end")

    def n3(self):
        self.part("n3", "begin")
        if self.idle:     # S7-1: SE's light table found nothing (grid-local positions), the Seamoth placement skipped
            self.owner_skip("n3.seamoth")
        for _ in range(10):
            self.step(30)
            n = 0 if self.idle else 5
            self.se_row("s7.lights", found=n, sent=n, shadowed=0 if self.idle else 4, owners=4 if self.idle else 5, maxDistM=0 if self.idle else 8.0)
            self.sn_row("s7.lights", rows=n, applied=n, shadowed=0 if self.idle else (4 if self.m == "S7.M18" else 2), pool=8,
                        shadowCandidates=0 if self.idle else 4)
        for label, px, depth in (("n3.se", "(8, 8)", 3), ("n3.seamoth", "(24, 24)", -17.5 if self.idle else 3)):
            self.sn_text("DE.SnPixel label=%s seTick=%d newestTick=%d frame=%d px=%s depth=%s" % (label, self.tick, self.tick, self.frame, px, depth))
        self.png("n3.lights", {(8, 8): (180, 170, 150), (24, 24): (170, 165, 150)})
        self.part("n3", "end")

    def n4(self):
        m = self.m
        self.part("n4", "begin")
        for lvl in (0.0, 0.25, 0.5, 0.75, 1.0):
            name = "n4.thrust.%g" % lvl
            self.part(name, "begin")
            for i in range(30):
                self.step(6)
                for key, size in (("k1", 1.0), ("k2", 0.5)):
                    em = 20.0 if m == "S7.M11" else 40.0 * lvl * size
                    # review F18d: SE's sustain row of the key (de.fx trace) is what the emission is fitted against
                    self.se_row("s7.fx.event", type="Thruster", sustain=True, stop=False, entity="77", key=key, intensity=lvl, size=size,
                                seq=0, stream=65537, flags=1 | 2 | 8)
                    self.sn_row("s7.fx.thruster", seTick=self.tick, entity=77, key=key, intensity=lvl, size=size, emission=em, rpm=lvl,
                                rpmRule=lvl, enginePlaying=True)
                if i % 10 == 9:
                    self.se_row("s7.fx", why="periodic", raised={"Thruster": 8}, sent={"Thruster": 8}, dropped={},
                                sustainRows={"Thruster": 8}, sustainKeys={"Thruster": 2}, ticks=60, stepLagMaxTicks=1, window=8,
                                stream=65537, firstSeq=self.seq + 1)
            self.part(name, "end")
        # the gun: 20 ShootOnce, muzzles and streaks attached to the ship, impacts on the plate
        self.part("n4.gun", "begin")
        if self.idle:     # S7-1: the thruster grid had no gun: an project step, skipped
            self.owner_skip("n4.gun")
            self.step(130)
        else:
            self.reply("se", "de.fx", "n4.gun", {"gun": 77, "shots": 20}, {"ok": True})
            off = 2.0 if m == "S7.M15" else 0.0
            for i in range(20):
                self.step(6)
                if m != "S7.M12":
                    self.ev_pair("WeaponMuzzle", (10.0 + 0.01 * i, -20.0, 80.0), sn_off=off)
                    self.ev_pair("BulletStreak", (10.0 + 0.01 * i, -20.0, 80.5), size=30.0, sn_off=off)
                self.ev_pair("Impact", (10.0, -20.0, 90.0 + 0.01 * i), size=0.5, sn_off=off)
            self.step(10)
            n_w = 0 if m == "S7.M12" else 20
            self.se_row("s7.fx", why="periodic", raised={"WeaponMuzzle": n_w, "BulletStreak": n_w, "Impact": 20},
                        sent={"WeaponMuzzle": n_w, "BulletStreak": n_w, "Impact": 20}, dropped={}, window=8, stream=65537)
            self.sn_row("s7.fx", applied={"WeaponMuzzle": n_w, "BulletStreak": n_w, "Impact": 20}, spawned={}, failed={}, missed=0, sustains=0)
        self.part("n4.gun", "end")
        for r in (2.0, 10.0, 30.0):
            name = "n4.explosion.%g" % r
            self.part(name, "begin")
            self.step(30)
            self.ev_pair("Explosion", (0.0, -25.0, 85.0), size=r, flags=8, lifetime=2000, scale=1.0 if m == "S7.M13" else r / 5.0)
            self.step(30)
            self.part(name, "end")
        for tname in ("Welder", "Grinder", "PrawnDrill"):
            name = "n4.tool.%s" % tname
            key = "tool|%s" % tname
            self.part(name, "begin")
            if self.idle:     # S7-1: no ship tool found: project steps, skipped
                self.owner_skip(name)
                self.step(300)
                self.part(name, "end")
                continue
            self.step(2)
            self.sn_row("s7.fx.sustain", seTick=self.tick, type=tname, key=key, entity=77, state="start")
            if m == "S7.M14":
                self.step(30)
                self.sn_row("s7.fx.sustain", seTick=self.tick, type=tname, key=key, entity=77, state="fade")
                self.step(300 - 32)
            else:
                for _ in range(19):
                    self.step(15)
                    self.sn_row("s7.fx.sustain", seTick=self.tick, type=tname, key=key, entity=77, state="refresh")
                self.step(300 - 2 - 19 * 15)
            self.part(name, "end")
            self.step(5)
            if m != "S7.M14":
                self.sn_row("s7.fx.sustain", seTick=self.tick, type=tname, key=key, entity=77, state="stop")
        for name, tname, flags, life in (("n4.drop.ship", "GridImpact", 1 | 8, 500), ("n4.drop.sea", "Splash", 16, 1500),
                                         ("n4.breach", "HullBreak", 1 | 8, 3000)):
            self.part(name, "begin")
            self.step(30)
            if self.idle:     # S7-1: the drops and the breach are project steps, skipped
                self.owner_skip(name)
            elif not (tname == "Splash" and m == "S7.M16"):
                self.ev_pair(tname, (5.0, 0.0 if tname == "Splash" else -18.0, 82.0), flags=flags, lifetime=life,
                             sn_off=2.0 if (m == "S7.M15" and tname == "GridImpact") else 0.0)
            self.step(30)
            self.part(name, "end")
        self.reply("sn", "de.fx", "n4.stress.fps", {"action": "fps", "cap": 30.0}, {"ok": True, "fpsCap": 30})
        self.part("n4.stress", "begin")
        acc, drp = (500, 0) if m == "S7.M20" else (128, 372)
        per = {"Thruster": 300, "Explosion": 200} if m == "S7.M20" else \
            {"Thruster": 64, "WeaponMuzzle": 16, "BulletStreak": 32, "Explosion": 8, "GridImpact": 8}
        self.reply("se", "de.fx", "n4.stress.tick", {"action": "stress", "n": 500},
                   {"ok": True, "accepted": acc, "dropped": drp, "perType": per, "nextTickPending": 0, "oldestTicks": 7})
        self.reply("se", "de.fx", "n4.stress.burst", {"action": "stress", "n": 500, "ticks": 8},
                   {"ok": True, "scheduled": True, "n": 500, "ticks": 8, "perTick": 63})
        for _ in range(6):
            self.step(10)
            self.sn_row("s7.fx", applied={"Thruster": 20}, spawned={}, failed={}, missed=40 if m == "S7.M17" else 0, sustains=4)
        self.part("n4.stress", "end")
        self.reply("sn", "de.fx", "n4.stress.fps.off", {"action": "fps", "cap": 0.0}, {"ok": True, "fpsCap": 0})
        self.part("n4.above", "begin")
        for k in range(5):
            self.step(30)
            # S7-1: the first row in the window carries starts from before it (the stress tail under water): not the test's
            self.sn_row("s7.audio", bus={"level": 0.0, "target": 0.0, "c5Target": 0.0, "muted": True},
                        events={"WeaponMuzzle": 8} if k == 0 else {}, refused={"Thruster": 4},
                        above=0, biome="safeShallows", depth=-2.0, underwater=False, requestsUnderwater=10, requestsDry=4 * (k + 1),
                        splashAllowed=0, allowedDry=0, startedTotal=20)
            self.sn_row("s7.fx", applied={"Thruster": 6}, spawned={}, failed={}, missed=0, sustains=1)
        self.part("n4.above", "end")
        self.sess.append({"ev": "capture", "name": "n4.capture.thruster", "side": "sn", "path": "s7/synth/none.png"})
        self.part("n4", "end")

    def wm5(self):
        self.part("wm5", "begin")
        bad = self.m == "S7.M6"
        if bad:
            self.se_text("DE.Time REFUSE axis |axis.U0| = 0.4 (the sector weather base direction alone): sky-only")
        self.se_row("s7a.sun", why="bind", checks=not bad)
        tp = 12000.0
        for i in range(41):
            self.step(1800)
            elev = 60.0 * math.sin(2 * math.pi * (i + 0.5) / 40.0)
            step = "advance"
            if self.m == "S7.M2" and i == 20:
                tp -= 30.0
                step = "jump"
            else:
                tp += 30.0
            self.se_row("s7a.sun", why="periodic", checks=not bad, elevDeg=elev)
            self.sn_row("s7a.sun", c6Tick=self.tick, timePassed=tp, isDay=elev > 0, c6ElevDeg=elev, drive=not bad, step=step)
        self.part("wm5", "end")

    def n5(self):
        self.part("n5", "begin")
        for site in ("shallows", "kelp", "grassy"):     # S7-1: wrecks with box colliders, never the Aurora
            name = "n5.%s" % site
            self.part(name, "begin")
            self.step(60)
            bad = self.m == "S7.M3" and site == "grassy"
            # review F6: the rays whose Subnautica hit is a static; under S7.M3 the Lost River capsules have no SE host
            agg = {"n": 200, "hits": 200, "hostHits": 140 if bad else 196, "bothHit": 140 if bad else 196, "p95DiffM": 0.03,
                   "snStatic": 200, "seMissWhereSnStatic": 60 if bad else 4, "firstHitNotHost": 12, "snNotStatic": 0}
            self.reply("sn", "de.statics", "%s.sn.rays" % name, {"action": "rays", "n": 200}, {"ok": True, "rays": []})
            self.reply("se", "de.statics", "%s.se.rays" % name, {"action": "rays"}, dict(agg, ok=True))
            self.se_row("s7.statics", state="rays", **agg)
            self.part(name, "end")
        self.part("n5.census", "begin")
        self.step(300)
        self.reply("se", "de.statics", "n5.census.se.probe", {"action": "probe", "entity": "42", "down": 60},
                   {"ok": True, "hit": True, "host": True, "name": host_name(6, -1, 6, 1), "distance": 0.4})
        self.part("n5.census", "end")
        self.part("n5", "end")

    def n6(self):
        m = self.m
        self.part("n6", "begin")
        for model in ("Models\\Characters\\Astronaut\\SE_astronaut.mwm", "Models\\Characters\\Astronaut\\SE_astronaut_female.mwm"):
            n = 64 if m == "S7.M9" else BONES
            self.se_text("DE.S7 bones %s 81 sent=%d ExtraBones %s" % (
                model, n, "boneCount==Model.Bones.Length PASS" if n == 81 else "boneCount!=Model.Bones.Length (MUTANT S7.M9 truncated to 64)"))
        self.part("n6.walk", "begin")
        for f in range(60):
            self.step(1)
            n = 64 if m == "S7.M9" else BONES
            world = [[1.0 + 0.01 * i + 0.001 * f, -10.0 + 0.02 * i, 2.0 - 0.005 * i] for i in range(n)]
            self.se_row("s7.bones", model="SE_astronaut.mwm", boneCount=n, modelBones=81, addBoneCount=n, bindBones=81, extra=True, world=world)
            if m == "S7.M9":
                self.sn_row("s7.bones", presentedSeTick=self.tick, seTick=self.tick, alpha=1.0, boneCount=64, addBoneCount=64, bindBones=81,
                            extraBones=True, source="Bind", bonesValid=False, countsAgree=False, world=[],
                            tear=[{"part": 1, "valid": True, "verts": 9000, "badSum": 0, "badIndex": 0, "maxIndex": 76, "boneCount": 78}])
                continue
            # review F12: every third frame drawn between SE ticks (alpha 0.5): the analyzer lerps SE's rows of seTick - 1 and seTick
            alpha = 0.5 if f % 3 == 1 and f > 0 else 1.0
            base = world if alpha >= 1.0 else [[(1.0 + 0.01 * i + 0.001 * (f - 1)) * 0.5 + w[0] * 0.5, w[1], w[2]] for i, w in enumerate(world)]
            sn_world = [[w[0], w[1] + (0.03 if (m == "S7.M4" and i > 2) else 0.0005), w[2]] for i, w in enumerate(base)]
            self.sn_row("s7.bones", presentedSeTick=self.tick, seTick=self.tick, alpha=alpha, boneCount=81, addBoneCount=81, bindBones=81,
                        extraBones=True, source="Retarget" if self.idle else "SeBones", bonesValid=True, countsAgree=True, world=sn_world,
                        tear=[{"part": 1, "valid": True, "verts": 9000, "badSum": 0, "badIndex": 0, "maxIndex": 76, "boneCount": 78}])
        self.part("n6.walk", "end")
        self.part("n6", "end")

    def n7(self):
        self.part("n7", "begin")
        path = os.path.join(self.folder, "s7-census.json")
        log = os.path.join(self.folder, "s7-census.txt")
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"version": 1, "allow": ["a"], "handled": ["a", "b"], "skip": {"c": "trigger only"},
                       "summary": {"handled": 2, "skipped": 1}, "classes": {"a": {"status": "handled", "allowListed": True},
                                                                         "b": {"status": "handled"}, "c": {"status": "skip", "reason": "trigger only"}}}, f)
        with open(log, "w", encoding="utf-8") as f:
            f.write("classes referenced 3, listed 3 (handled 2, skipped 1), allow-listed 1\nPASS S7.N7.census: every referenced class is "
                    "handled or skipped with a reason\n")
        self.sess.append({"ev": "census", "path": path, "log": log, "rc": 0})
        self.part("n7", "end")

    def n8(self):
        self.part("n8", "begin")
        self.step(60)
        self.part("n8", "end")

    def n9(self):
        self.part("n9", "begin")
        bad = self.m == "S7.M7"
        for k in range(5):
            self.step(30)
            self.sn_row("s7.statics", op="pass", primitives=37, batches=3, census=False,
                        filtered={"creature": 0 if bad else 1 + k, "proxy": 12, "trigger": 40},
                        creaturesReported=(1 + k) if bad else 0, insideProxy=0)
        self.part("n9", "end")

    def n10(self):
        self.part("n10", "begin")
        self.se_row("s7.statics", batch=[13, 18, 12], epoch=1, primitives=37, host=host_name(13, 18, 12, 1), state="created", ms=3.1)
        self.step(600)
        self.sess.append({"ev": "savecheck", "world": "DE Lab S7Save", "files": 14, "hits": 2 if self.m == "S7.M8" else 0,
                          "ids": [123456789], "hostsAlive": 1})
        self.part("n10", "end")

    def n11(self):
        m = self.m
        self.part("n11", "begin")
        for name, mix, in_room, target, ev in (("n11.above", False, False, 0.0, {}), ("n11.under", True, False, 1.0, {"Thruster": 3}),
                                               ("n11.room", True, True, 1.0, {})):
            self.part(name, "begin")
            for _ in range(8):
                self.step(60)
                expect = mix and not in_room
                muf = 30 if expect and m != "S7.M19" and not self.arcade else 0
                self.se_row("s7.audio", mixActive=mix, inRoom=in_room, effect="LowPassNoHelmetNoOxy", emitters=40,
                            modifiable=0 if self.arcade else 30, muffled=muf, expectMuffle=expect and not self.arcade, patched=True,
                            realisticSound=not self.arcade)
                self.sn_row("s7.audio", bus={"level": target, "target": target, "c5Target": target, "muted": target == 0}, events=ev,
                            refused={}, above=0, biome="safeShallows", depth=-12.0 if mix else -1.0, underwater=mix,
                            requestsUnderwater=3, requestsDry=2, splashAllowed=0, allowedDry=0)
            self.part(name, "end")
        self.part("n11", "end")

    def write(self):
        for p in SYNTH_PARTS:
            getattr(self, p)()
        for name, recs in (("s7-session.jsonl", self.sess), ("se-synth.jsonl", self.se), ("sn-synth.jsonl", self.sn)):
            with open(os.path.join(self.folder, name), "w", encoding="utf-8") as f:
                for r in recs:
                    f.write(json.dumps(r) + "\n")
        return self.folder

def synth_session(folder, mutant=None, arcade=False, idle=False):
    os.makedirs(folder, exist_ok=True)
    return _Synth(folder, mutant, arcade, idle).write()

# S7-1: the idle run's rows: what the project skipped reads MISSING (never FAIL), the S7-1 artifacts are judged
IDLE_MISSING = ("S7.N1.fog", "S7.N1.breach", "S7.N1c.walk", "S7.N3.ratio", "S7.N3.shadows", "S7.N4.weapon", "S7.N4.impact",
                "S7.N4.tools", "S7.N4.gridimpact", "S7.N4.hullbreak", "S7.N4.splash", "S7.N6.pos")
IDLE_PASS = ("S7.N2.readers", "S7.N4.above", "S7.N1.flag")

# rows a mutant may leave MISSING besides its red ones (a pose the seeded failure removes: S7.M9 sends no valid bones, so
# the positions cannot be joined; S7.M6's sky-only world has no driven sun samples)
MUTANT_ALSO_MISSING = {"S7.M9": ("S7.N6.pos",), "S7.M6": ("S7.WM-5.isday", "S7.WM-5.monotonic")}

def selftest() -> int:
    ok = conventions_selftest()
    tmp = tempfile.mkdtemp(prefix="s7-selftest-")
    rows = evaluate(synth_session(os.path.join(tmp, "good")))
    bad = [r for r in rows if r["status"] not in ("PASS", "INFO")]
    print("S7 synthetic session: %d rows, %d not PASS%s" % (len(rows), len(bad), ": " + ", ".join("%s %s (%s)" % (
        r["id"], r["status"], r["measured"]) for r in bad) if bad else ""))
    ok &= not bad and len(rows) >= 44
    for m, want in MUTANT_RED.items():
        mrows = evaluate(synth_session(os.path.join(tmp, m.replace(".", "_")), m))
        red = [r["id"] for r in mrows if r["id"] in want and r["status"] == "FAIL"]
        mr = next((r for r in mrows if r["id"] == "%s.red" % m), None)
        others = [r["id"] for r in mrows if r["status"] in ("FAIL", "MISSING") and r["id"] not in want and r.get("required", True)
                  and r["id"] not in MUTANT_ALSO_MISSING.get(m, ())]
        good = sorted(red) == sorted(want) and mr is not None and mr["status"] == "PASS" and not others
        print("  %-6s red %s (want %s), .red %s%s" % (m, ",".join(red) or "none", ",".join(want), mr["status"] if mr else "-",
                                                     ", other required: " + ",".join(others) if others else ""))
        ok &= good
    # review F2: an Arcade-sound world: S7.N11.muffle INFO (not a false PASS), S7.M19 not judgeable there (INFO)
    arows = evaluate(synth_session(os.path.join(tmp, "arcade"), arcade=True))
    mu = next((r for r in arows if r["id"] == "S7.N11.muffle"), None)
    m19 = evaluate(synth_session(os.path.join(tmp, "arcade_m19"), "S7.M19", arcade=True))
    m19r = next((r for r in m19 if r["id"] == "S7.M19.red"), None)
    good = mu is not None and mu["status"] == "INFO" and m19r is not None and m19r["status"] == "INFO"
    print("  arcade world: S7.N11.muffle %s, S7.M19.red %s %s" % (mu and mu["status"], m19r and m19r["status"], "PASS" if good else "FAIL"))
    ok &= good
    # S7-1: an idle run: no FAIL, the project rows MISSING
    irows = evaluate(synth_session(os.path.join(tmp, "idle"), idle=True))
    st = {r["id"]: r["status"] for r in irows}
    fails = [r["id"] for r in irows if r["status"] == "FAIL"]
    wrong = [i for i in IDLE_MISSING if st.get(i) != "MISSING"] + [i for i in IDLE_PASS if st.get(i) != "PASS"]
    owner_row = st.get("S7.owner") == "INFO"
    good = not fails and not wrong and owner_row
    print("  idle run (owner steps skipped): FAIL %s, wrong %s, S7.owner %s %s" % (",".join(fails) or "none",
          ",".join("%s=%s" % (i, st.get(i)) for i in wrong) or "none", st.get("S7.owner"), "PASS" if good else "FAIL"))
    ok &= good
    print("analyze_s7 selftest %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv) -> int:
    if "--selftest" in argv:
        return selftest()
    lenient = "--lenient" in argv
    args = [a for a in argv if not a.startswith("--")]
    if not args:
        print(__doc__)
        return 2
    folder = args[0]
    rows = evaluate(folder)
    ok = write_results(folder, rows, not lenient)
    if "--json" in argv:
        print(json.dumps(rows, indent=1, default=str))
    else:
        print(table(rows))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
