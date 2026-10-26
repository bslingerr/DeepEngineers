"""S8a "The terrain converter" scripted sessions (ROADMAP "### S8a": the launcher line, the sessions S8a-0..S8a-3, the
mutant groups; CONTRACT §5.4 "S8a control commands"; the rows of tests/s8a/analyze_s8a.py). Stage A: the offline parts
c1 and c3 and the drilling parts n1-n6; Stage B: c2 (the sculpt verified), the window paths n7-n10 and --treadmill (n8);
Stage C: c2g (the planet's height measured in game against the sculpt tool) and n10's ore probes.

 python -I src/DeepEngineers.Launcher/launcher.py s8a [--parts c1,c2,c2g,c3,n1,n2,n3,n4,n5,n6,n7,n8,n9,n10,teardown]
 Mx] [--treadmill]
 [--sites FILE] [--quick] [--dry-run] [--selftest]
 [--list-mutant-groups] [--analyze <folder>]

 c1 offline: the converter round trip on the install (read-only): the C# S8aRoundTripTests (>= 100 batch files
 through S8aOctree and back through the S4 reader) and the Python twin (sn_octree_write.py --roundtrip N);
 --mutant S8a.M13 runs the twin with the mutant (the C# test judges M13 by itself)
 c3 offline: the pin: S8aConventionsTests, s8a_ref.py --pin / --selftest, layout_py.py compare
 c2g online, SE commands only (Stage C step 1): de.s8a probe {planet: true, points} at the 20 SN columns of
 sculpt_de4546b.C2G_POINTS (GetClosestSurfacePointGlobal, the LOD-0 voxel geometry, the Water Mod depth, the planet's
 radii, Rs and sculptHash), against sculpt_planet.py --depth-at on the planet data folder SE loaded (read-only):
 S8a.C2.ingame (|measured - tool| <= 1 m), S8a.C2.
 n1 drills: de.s8a drill at the six sites (sphere x 4 = PerformCutOutSphereFast, hand x 2 = CutOutShapeWithPropertiesAsync;
 one on the tile seam at SN x = 0), 10 s apart, camera 12 m away; before each: the SE storage surface over every
 column set (the reference); after each: the feed applied, one Subnautica ray down the axis
 n2 footprint rays: 9 x 9 Subnautica rays over each hole (+ SE and SN rays over the seam site's inner columns)
 n3 collision rays: 64 columns within 0.7 r and 32 rim columns per hole, SE CastRay and Subnautica layer-30
 n4 the save (world copy "DE Lab S8aSave"): 3 holes, rays, de.s8a overlay, de.s8a save (refused outside a
 DE_THROWAWAY.txt copy
 relaunched on the copy (two launches), rays again, the save folder scanned read-only (S4.G2-E2)
 n5 de.s8a reload {all: true} on Subnautica, rays again, the store's manifest and files checked (size, FNV)
 n6 the install listing before/after (tests/s8a/s8a_install_check.py); taken in every online session
 teardown every mutant of the session off; after Stage B parts the window throttle off, the treadmill back on chart 0, the spawns cleared, the plate removed

Both games on a throwaway copy (tools/lab/make_terrain_copy.py --name "DE Lab S8a" --point-last-session; n4 on
"DE Lab S8aSave"), the S5 session rules (character parked at SN (24, -8, 0)); SE environment DEEPENGINEERS_S5=on,
DEEPENGINEERS_PICTURE=site, DEEPENGINEERS_S8A=on (+ DEEPENGINEERS_S8A_TREADMILL=on for n8 / --treadmill); the session
start row carries the band, the world, the S8a settings, the sites and the store key. Output tests/out/s8a/<stamp>[-<mutant>]/
(s8a-session.jsonl, se-*.jsonl, sn-*.jsonl, logs, s8a-install-before.txt / -after.txt, s8a-table.txt, s8a-results.json).
Mutant groups (--mutant): M1 -> n1,n3; M2 -> n2,n3; M3 -> n4; M4 -> n5; M5 -> n1,n6; M6 -> n7; M7, M8 -> n8; M9 -> n9;
M10, M11 -> n10; M12 -> n1; M13 -> c1; M14 -> n2. Never pipes or touches a game window; games are only closed by PID.
"""
from __future__ import annotations

import datetime
import glob
import json
import math
import os
import re
import shutil
import subprocess
import sys
import time

S8A_PARTS = ("c1", "c2", "c2g", "c3", "n1", "n2", "n3", "n4", "n5", "n6", "n7", "n8", "n9", "n10", "n10c", "n11", "nd", "nf", "nl", "no", "ns", "nv", "teardown")
OFFLINE_PARTS = ("c1", "c2", "c3")
STAGE_B_PARTS = ("c2", "n7", "n8", "n9", "n10")
STAGE_C_PARTS = ("c2g",)                                             # Stage C: the in-game height measurement (SE commands only)
STAGE_E_PARTS = ("n10c", "n11")                                      # Stage E: the Subnautica map's ores, the Aurora distress beacon
STAGE_D_PARTS = ("nd",)                                              # Stage D: drilling like SE's own (live edits, the per-frame oracle)
STAGE_F_PARTS = ("nf",)                                              # Stage F2: the ring field served by the planet's generator
LOOK_PARTS = ("nl",)                                                 # md section 6)
ORE_V2_PARTS = ("no",)                                               # planet v2 R8: ores you can see (SE's outcrops -> Subnautica's deposits)
SAMPLE_PARTS = ("ns",)
ORE_OVERLAY_PARTS = ("nv",)                                           # docs/S8A-PLANET-V2.md section 13) # planet v2 route B: the sample chunk (docs/S8A-PLANET-V2.md section 12)
# no: per ore site (n10's sites: the deposits of the planet SE runs) the camera 12 m above the seabed, held; SE's outcrop records and its
# own detector within 100 m, Subnautica's deposits; on the first site a drill on the biggest undrilled outcrop (it must shrink) and 60
# of Subnautica's drill hits on the nearest deposit (it must stay whole)
NO_SITES, QUICK_NO_SITES = 3, 2
NO_ABOVE_M = 12.0
# S8a.NV (the crater's ore overlay): spots near the crater's large deposits (else these grid points), the camera NV_UP_M above the
# seabed; SE's detector over NV_DETECT_R m; a hit's surface voxels within NV_RADIUS_M (the row's reach = the overlay's ShowRadiusM)
NV_GRID = ((-500.0, -500.0), (500.0, 500.0), (-500.0, 500.0), (500.0, -500.0))
NV_SPOTS, QUICK_NV_SPOTS = 4, 2
NV_DEPOSIT_REACH_M = 400.0
NV_UP_M = 20.0
NV_DETECT_R = 60.0
NV_RADIUS_M = 5.0
NV_WAIT_S = 60.0              # the overlay's queue near the camera drained
NV_RELOAD_S = 5.0             # Subnautica reloads the batches written
NV_COVER_R = 24.0
NV_CAPTURES, QUICK_NV_CAPTURES = 4, 2
NV_LOOK_UP_M, NV_LOOK_BACK_M, NV_LOOK_PITCH = 5.0, 4.0, -50
NV_VEIN_R = 8                 # voxels: the vein around a painted hit
NV_PAINT_R = 10.0             # metres: the paint read around the vein's centroid
NV_LIST = 256
NV_MINE_PAD_M, NV_MINE_MAX_R = 1.0, 8.0
NV_MINE_WAIT_S = 6.0
NO_RECORDS_WAIT_S = 40.0
NO_DEPOSITS_WAIT_S = 30.0
NO_DRILL_R = 3.0
NO_IMMUNE_HITS = 60
# planet v2 R8 (S8a.N10.oredensity): SE's own detector over NO_DENSITY_R round points of the crater (a 4 x 4 grid) and the planet
# (32 headings at 15, 22 and 30 km), the camera NO_DENSITY_UP_M above the seabed
NO_DENSITY_R = 300.0
# planet v2, ores as voxels (docs/S8A-PLANET-V2.md section 13): at the drilled vein the camera ORE_LOOK_BACK_M from the hole, pitch
# ORE_LOOK_PITCH; captures with our ore materials, as the template rock (16 GreyRock: the "no ore look" reference) and as each fallback
# candidate of the paint test (Subnautica palette types that may read as ore)
ORE_LOOK_BACK_M, ORE_LOOK_UP_M, ORE_LOOK_PITCH = 7.0, 5.0, -35
ORE_LOOK_ROCK = 16
ORE_LOOK_CANDIDATES = (206, 155, 159, 157, 129)
NO_DENSITY_UP_M = 30.0
NO_DENSITY_CRATER, QUICK_NO_DENSITY_CRATER = 16, 4
NO_DENSITY_PLANET, QUICK_NO_DENSITY_PLANET = 32, 8
NO_DENSITY_RADII_M = (15000.0, 22000.0, 30000.0)
# nl: Subnautica's picture at the spawn (3 headings) and at 2 far points per planet biome (the deployed files' biome map, a seeded
# spiral 13.5-40 km out), each at NL_HEIGHTS above SE's seabed, pitch NL_PITCH_DEG, SE's noon; SE's own shots from NL_ORBIT_KM up
NL_HEIGHTS = (8.0, 25.0)
NL_PITCH_DEG = -15.0
NL_SPAWN_CANDIDATES = ((480.0, 82.0), (250.0, 140.0), (-150.0, 260.0), (60.0, -260.0))   # inside the map, a seabed deeper than 30 m
NL_SPAWN_HEADINGS = (0.0, 120.0, 240.0)
NL_PER_BIOME = 2
NL_ORBIT_KM = (5, 30)
NL_SETTLE_S = 4.0
# a test run (nl 20261024-033738: all 16 far captures black): beyond 7.9 km SE's zone is FAR and Subnautica's picture is parked unless
# the charts follow the camera, so nl runs the treadmill for each far point (off, chart 0, the teleport in site metres, on, the charts
# settled), waits for Subnautica built twice and retries a black capture; far points only on seabeds Subnautica's noon still lights
NL_LIT_DEPTH_M = 450.0
NL_EXCLUDE_BIOMES = ("void",)
NL_BLACK_LUMA = 3.0                 # a capture whose mean luma (0-255) is under this is no picture
NL_RETRIES = 3
NL_CHART_SETTLE_S = 6.0             # the chart unchanged this long (no automatic step pending), applied by Subnautica, zone not FAR
NL_CHART_WAIT_S = 150.0
# nf (Stage F2): ring paths through stamped spots, rays across window batch seams on steep spots, S8a.F.blend over the served
# planet, the served bytes against the twin's vectors, the fallback (S8a.M15 on a plain column), a drill pass on a stamp, the cost
NF_MAX_RHO = 7600.0                  # the ring spots stay inside the dive zone (7,800 m) with the treadmill off
NF_PATH_HALF_M = 150.0               # each ring path: 300 m along x through a stamp
NF_SPEED = 20.0
NF_PATHS, QUICK_NF_PATHS = 4, 2
NF_SEAMS, QUICK_NF_SEAMS = 3, 1
NF_SEAM_DX = (-3.0, -2.0, -1.0, -0.5, 0.5, 1.0, 2.0, 3.0)
NF_SEAM_DZ = (-4.0, 0.0, 4.0)
NF_UP_M, NF_DOWN_M = 150.0, 150.0    # SE rays from 150 m over the heightmap seabed to 150 m under it (stamps up to 128 m tall)
NF_BLEND_N = 12
NF_BLEND_WAIT_S = 120.0
NF_FALLBACK_D = (-8.0, -4.0, -2.0, -1.0, 0.0, 1.0, 2.0, 4.0, 8.0)
NF_SITE = 400                        # rays site ids 400.. (paths 400 + k, seams 420 + k, fallback 440, drill 450)
NF_VECTORS = os.path.join("tests", "s8a", "vectors", "ring3d", "ring3d-install-vectors.tsv")
NF_READY_WAIT_S = 60.0               # the ring field made (SE's first planet read, or the world-up kick) before nf judges
# planet v2 route B, the sample (part ns; S8aSample on SE, S8aSampleSpawns on Subnautica; tools/planet/s8a_chunks.py)
NS_READY_WAIT_S = 90.0               # the sample made (the world-up kick, or SE's first read of its zone) before ns judges
NS_ABOVE_M = 40.0                    # the camera over the chunk's own seabed on arrival and over each ray segment
NS_LINE_STEP_M, QUICK_NS_LINE_STEP_M = 8.0, 32.0
NS_LINE_EXT_M = 60.0                 # the lines reach this far beyond the square (the planet's heightmap on both ends)
NS_SEGMENT_M = 200.0                 # the camera moves along a line in segments: Subnautica's rays need its terrain built round them
NS_SEAM_IN_M, NS_SEAM_OUT_M, NS_SEAM_STEP_M = 120.0, 40.0, 4.0
NS_UP_M, NS_DOWN_M = 120.0, 220.0    # SE rays from 120 m over the chunk's own seabed to 220 m under it
NS_ENTITY_R = 60.0                   # S8a.NS.entities: Subnautica's live objects per family within this radius vs the chunk's records
NS_ENTITY_UP_M = 8.0
NS_ENTITY_WAIT_S = 25.0
NS_SITE = 600                        # rays site ids 600.. (lines 600 + k, seams 610 + k)
NS_VECTORS = os.path.join("tests", "s8a", "vectors", "sample", "sample-install-vectors.tsv")
NL_SAMPLE_POINTS = 2                 # nl with the sample on: its look spots (and one design-3 Grassy Plateaus point beside them)
NS_ROUNDTRIP_N = 500                 # S8a.NS.roundtrip in the session: this many octrees (the tool's verify checks all of them)
NS_RECHECK_M, NS_RECHECK_WAIT_S = 5.0, 8.0   # Subnautica's rays this far off SE's: verify the batch, wait, cast those columns again
# nd: two continuous drill passes (de.s8a drillpass: a cut of ND_RADIUS every ND_EVERY_MS for ND_SECONDS along ND_LENGTH_M), one in
# the Subnautica map (the drill layer: the Safe Shallows seabed, S4's shallow spot) and one on the planet (the window layer: the
# n7 drill spot), 1 m under SE's seabed; the camera ND_CAMERA_BACK_M behind the line's middle, Subnautica's per-frame cell rows on
ND_SECONDS, QUICK_ND_SECONDS = 20.0, 8.0
ND_EVERY_MS, ND_RADIUS, ND_LENGTH_M, ND_CAMERA_BACK_M = 250, 2.5, 40.0, 14.0
ND_PASSES = (("crater", (20.0, 60.0)), ("planet", (4380.0, 0.0)))
ND_HOLD_EXTRA_S = 30.0      # the camera hold lasts the pass plus the settle and the round trip
ND_BUILT_WAIT_S = 30.0      # Subnautica's area built after the teleport, before the cell rows start
# n10c: spots in the Subnautica map where large resource deposits may start, the camera 15 m above; Subnautica wakes the cells there and reports what starts
CRATER_SPOTS = ((-1338.0, -295.0, 566.0), (1139.0, -241.0, -679.0), (1212.0, -255.0, 754.0), (-1228.0, -420.0, -221.0))
CRATER_ORE_CUBES, QUICK_CRATER_ORE_CUBES = 48, 24
ORE_PROBES, QUICK_ORE_PROBES = 12, 4                                  # n10: deposits probed (SE storage at the row's depths)
ORE_DRILLS, QUICK_ORE_DRILLS = 3, 1                                   # n10: of those, deposits drilled (a closed cavity in the vein; Subnautica's wall type)
ORE_NEAR_PATH_M = 4000.0                                              # deposits within this of the n10 path (Stage E: the ring's deposits come in fields with
 # empty stretches, none lies on the path; SE's storage is probed anywhere, a drilled deposit
 # gets the camera and a settled window, an undrilled interior voxel is information only)
ORE_MAX_SLOPE = 0.6                                                   # flat enough for a vertical probe to meet the vein near its rows' depths
ORE_DRILL_R = 4.0                                                     # the cavity radius; its equator sits at the vein's middle
DEFAULT_PARTS = ("n1", "n2", "n3", "n5", "n6", "teardown")        # S8a-1
LAB_WORLD = "DE Lab S8a"
PART_WORLD = {"n4": "DE Lab S8aSave"}
THROWAWAY_PARTS = ("n4",)                                           # de.s8a save needs the DE_THROWAWAY.txt marker
SE_MUTANTS = ("S8a.M1", "S8a.M2", "S8a.M3", "S8a.M5", "S8a.M6", "S8a.M8", "S8a.M9", "S8a.M10", "S8a.M12", "S8a.M14", "S8a.M23", "S8a.M27")
SN_MUTANTS = ("S8a.M4", "S8a.M7", "S8a.M11", "S8a.M29", "S8a.M21", "S8a.M22", "S8a.M24", "S8a.M25", "S8a.M26")
WIRE_MUTANTS = ("S8a.M13", "S8a.M30")
ALL_MUTANTS = ("S8a.M1", "S8a.M2", "S8a.M3", "S8a.M4", "S8a.M5", "S8a.M6", "S8a.M7", "S8a.M8", "S8a.M9", "S8a.M10", "S8a.M11",
               "S8a.M12", "S8a.M13", "S8a.M14", "S8a.M29", "S8a.M21", "S8a.M22", "S8a.M23", "S8a.M24", "S8a.M25", "S8a.M26", "S8a.M27", "S8a.M30")
MUTANT_GROUPS = {   # ROADMAP "### S8a" launcher line: mutant -> parts (and world)
    "S8a.M1": {"parts": ("n1", "n3", "teardown")}, "S8a.M2": {"parts": ("n2", "n3", "teardown")}, "S8a.M3": {"parts": ("n4", "teardown"), "world": PART_WORLD["n4"]},
    "S8a.M4": {"parts": ("n5", "teardown")}, "S8a.M5": {"parts": ("n1", "n6", "teardown")}, "S8a.M6": {"parts": ("n7", "teardown")},
    "S8a.M7": {"parts": ("n8", "teardown"), "treadmill": True}, "S8a.M8": {"parts": ("n8", "teardown"), "treadmill": True}, "S8a.M9": {"parts": ("n9", "teardown")},
    "S8a.M10": {"parts": ("n10", "teardown")}, "S8a.M11": {"parts": ("n10", "teardown")}, "S8a.M12": {"parts": ("n1", "teardown")},
    "S8a.M13": {"parts": ("c1",)}, "S8a.M14": {"parts": ("n2", "teardown")}, "S8a.M29": {"parts": ("nl", "teardown")},
    "S8a.M21": {"parts": ("no", "teardown")}, "S8a.M22": {"parts": ("no", "teardown")}, "S8a.M23": {"parts": ("no", "teardown")},
    "S8a.M24": {"parts": ("no", "teardown")}, "S8a.M25": {"parts": ("no", "teardown")}, "S8a.M26": {"parts": ("nd", "teardown")},
    "S8a.M27": {"parts": ("no", "teardown")}, "S8a.M30": {"parts": ("no", "teardown")},
}
# the six drill sites (SN x, z; the Safe Shallows seabed about -17 m, S4's shallow spot): four explosion-path holes, two
# hand-drill holes; site 5 on the tile seam (the 4 m overlap of tiles i = 1 and 2 is SN x in [-2, 2), site v1 lattice)
DEFAULT_SITES = ({"x": 40.0, "z": 40.0, "path": "sphere", "seam": False}, {"x": 85.0, "z": 35.0, "path": "sphere", "seam": False},
                 {"x": 35.0, "z": 85.0, "path": "sphere", "seam": False}, {"x": 90.0, "z": 90.0, "path": "hand", "seam": False},
                 {"x": 62.0, "z": 120.0, "path": "hand", "seam": False}, {"x": 0.0, "z": 70.0, "path": "sphere", "seam": True})
RADIUS_M = 2.5
RS = 60000.00390625                 # the sea sphere radius (site v1): the hole's horizontal radius is r * Rs / (Rs + y)
DRILL_GAP_S = 10.0
CAMERA_BACK_M, CAMERA_UP_M = 12.0, 6.0
APPLY_TIMEOUT_S = 10.0
APPLY_MIN_S = 2.0                   # the witness (1.5 s) and the converter's coalescing (250 ms) come first
RELOAD_TIMEOUT_S = 60.0
SAVE_FILE_TIMEOUT_S = 90.0
RELAUNCH_COOLDOWN_S = 20.0          # CONTRACT §7 "Relaunch" (the S8 renderer restart's cool-down)
FOOTPRINT_N, N3_N, RIM_N = 9, 8, 32
QUICK_FOOTPRINT_N, QUICK_N3_N, QUICK_RIM_N = 5, 4, 8
SE_RAY_CHUNK = 48                   # columns per de.s8a rays call (the reply carries the hits; the JSONL row too)
OVERLAY_FILE = "DE_S8a_overlay.vx2"
TICKS_PER_S = 60.0
# Stage B (S8a-2): the window paths (ROADMAP S8a.N7: SN (1,800, y, 0) -> (4,600, y, 0) and along the ring at rhoInf 4.6 km,
# 60 m above the SE seabed, 20 and 80 m/s; a 6 m drill into the generated seabed), the treadmill path (N8: to rhoInf 12 km and
# back at 80 m/s with a test plate 30 m beside the camera, forced steps in open water and over terrain), the throttled path (N9),
# the material probes and spawns (N10)
WINDOW_PATH = ((1800.0, 0.0), (4600.0, 0.0))
WINDOW_RING = ((4600.0, 0.0), (4600.0, 4600.0), (0.0, 4600.0))
WINDOW_SPEEDS = (20.0, 80.0)
WINDOW_ABOVE_M = 60.0
WINDOW_FALLBACK_Y = -10.0
WINDOW_COLUMNS, QUICK_WINDOW_COLUMNS = 200, 40
WINDOW_DRILL_AT = (4400.0, 0.0)      # Stage C: a flat spot of the designed seabed (SN y -142, slope 0.02; (3,200, 0) is now the crater wall at -2,449 m)
WINDOW_RING_BATCHES = 5             # S8aConventions.Window.RingBatches: the window generates RingBatches^3 ids around the streaming centre
WINDOW_STREAM_DOWN_M = 8            # S8aConventions.Window.StreamCenterDownM
MATERIAL_PROBE_DEPTH_M = 1.2        # n10: Subnautica's voxel 1.2 m under SE's surface: its sample centre lies >= 0.7 m inside the rock (a solid surface voxel)
MATERIAL_PROBE_DEEPER_M = (2.0, 3.0)        # n10: while the voxel is outside the rock (a cliff)
MATERIAL_PROBE_SHALLOWER_M = (0.7, 0.4)     # n10: while it is far-solid (a binary cut: the octree's stand-in type)
WALL_STEPS_M = (3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0)   # n10 cavities: outward from the centre, the first solid voxel is the wall's surface voxel
WINDOW_DRILL_R = 6.0
WINDOW_APPLY_WAIT_S = 90.0
TREADMILL_FAR_M = 12000.0
TREADMILL_SPEED = 80.0
TREADMILL_PLATE_OFFSET_M = 30.0
TREADMILL_STEP_OPEN_X, TREADMILL_STEP_TERRAIN_X = 2600.0, 6000.0
TREADMILL_STEP_ABOVE_X, TREADMILL_ABOVE_Y = 9000.0, 40.0      # terrain 4: a forced step 40 m above the sea (N8.picture: no picture edge under 1 km)
TREADMILL_PLATE_WAIT_S = 6.0                                  # n8: the plate placed beside the camera before each step, mirrored within this
THROTTLE_MS = 2000
N10_COLUMNS, QUICK_N10_COLUMNS = 200, 40
WINDOW_DRILL_SITE = 100         # the site index of the window drill (keeps it out of the Stage A site rows)

def band_from_env():
    v = os.environ.get("DEEPENGINEERS_BAND", "")
    try:
        parts = [float(x) for x in v.replace(";", ",").split(",") if x.strip()]
        if len(parts) == 2 and 0.0 <= parts[0] < parts[1]:
            return parts[0], parts[1]
    except ValueError:
        pass
    return 1000.0, 1100.0

BAND = band_from_env()

# ----------------------------------------------------------------------------------------------------------------
# modules
# ----------------------------------------------------------------------------------------------------------------

def _path_module(LCH, rel, name):
    p = os.path.join(LCH.REPO, *rel)
    if p not in sys.path:
        sys.path.insert(0, p)
    return __import__(name)

def analyzer(LCH):
    return _path_module(LCH, ("tests", "s8a"), "analyze_s8a")

def install_check(LCH):
    return _path_module(LCH, ("tests", "s8a"), "s8a_install_check")

def ref_module(LCH):
    return _path_module(LCH, ("tools", "planet"), "s8a_ref")

def se_surface(hit):
    """SE's surface (SN y) of one de.s8a rays hit: castY where SE had physics, else storageY (tiles or the planet's mesh); NaN when neither."""
    for key in ("castY", "storageY"):
        y = hit.get(key) if isinstance(hit, dict) else None
        if isinstance(y, (int, float)) and not math.isnan(y):
            return float(y)
    return float("nan")

def sculpt_module(LCH):
    return _path_module(LCH, ("tools", "planet"), "sculpt_de4546b")

def chunks_module(LCH):
    return _path_module(LCH, ("tools", "planet"), "s8a_chunks")

def _isnum(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool) and not math.isnan(v)

def sample_points(LCH, n=None):
    """The sample's spots (s8a_chunks.sample_points: the centre and n Grassy Plateaus spots in its core, site-v1 SN); the centre of
 the placement text (or the slice's constant) when the store's chunk cannot be read."""
    n = NL_SAMPLE_POINTS if n is None else n
    try:
        return chunks_module(LCH).sample_points(n=n)
    except Exception:
        x, z = 4158.0, -19563.0
        try:
            C = chunks_module(LCH)
            pl = C.read_placement(C.chunk_paths()[2])
            x, z = pl["sn"]
        except Exception:
            pass
        return [{"u": u, "v": v, "x": x + u, "z": z + v, "seabed": -121.0, "nativeX": None, "nativeZ": None, "biome": "grassyPlateaus"}
                for u, v in ((0.0, 0.0), (0.0, 180.0), (180.0, 140.0))][:n + 1]

def s4_analyzer(LCH):
    return _path_module(LCH, ("tests", "s4"), "analyze_s4")

def s5_module(LCH):
    if LCH.HERE not in sys.path:
        sys.path.insert(0, LCH.HERE)
    import launcher_s5   # noqa: E402
    return launcher_s5

# ----------------------------------------------------------------------------------------------------------------
# worlds (read-only), sites, columns
# ----------------------------------------------------------------------------------------------------------------

def last_session_world(LCH):
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
    """Read-only facts of an SE world: folder, LastSession, autosave, the throwaway marker, the world mod."""
    info = LCH.s4_world_info(name)
    out = {"world": name, "folder": info.get("folder"), "lastSession": info.get("lastSession"), "error": info.get("error"),
           "autosaveMinutes": info.get("autosaveMinutes"), "worldMod": info.get("worldMod")}
    if not info.get("folder"):
        return out
    out["marked"] = throwaway_marked(info["folder"])
    out["pointed"] = info.get("lastSession") == os.path.basename(info["folder"])
    return out

def throwaway_marked(folder):
    """The copy's marker: the DE_THROWAWAY subfolder or the older top-level DE_THROWAWAY.txt."""
    return os.path.isdir(os.path.join(folder, "DE_THROWAWAY")) or os.path.exists(os.path.join(folder, "DE_THROWAWAY.txt"))

def world_problems(ws, parts, standin):
    probs = []
    if standin:
        return probs
    if not ws.get("folder"):
        return [ws.get("error") or "world %r not found" % ws.get("world")]
    if (ws.get("world") or "").strip().lower() == "de lab":
        probs.append("--world is the lab world itself; S8a sessions run on a throwaway copy (tools/lab/make_terrain_copy.py --name %r)" % LAB_WORLD)
    if not ws.get("pointed"):
        probs.append("LastSession.sbl points at %r, not %r (make_terrain_copy.py --point-last-session)" % (ws.get("lastSession"), ws.get("world")))
    if ws.get("autosaveMinutes") not in (0,):
        probs.append("autosave is %s min in the copy (must be 0: make_terrain_copy.py sets it)" % ws.get("autosaveMinutes"))
    if ws.get("worldMod") is False:
        probs.append("the copy has no DeepEngineers.World mod item (DE_4546B)")
    online = [p for p in parts if p not in OFFLINE_PARTS]
    if online and not ws.get("marked"):
        probs.append("every online part needs a throwaway copy with the DE_THROWAWAY marker (tools/lab/make_terrain_copy.py); %r carries none" % ws.get("world"))
    for p in parts:
        want = PART_WORLD.get(p)
        if want and ws.get("world") != want:
            probs.append("part %s runs on %r only (--world %r)" % (p, want, want))
    return probs

def load_sites(path):
    """--sites FILE: a JSON list of {x, z, path?, seam?} or [x, z] pairs (SN metres); the index is the site number."""
    if not path:
        return [dict(s, i=i) for i, s in enumerate(DEFAULT_SITES)]
    with open(path, encoding="utf-8") as f:
        raw = json.load(f)
    out = []
    for i, s in enumerate(raw):
        if isinstance(s, (list, tuple)) and len(s) >= 2:
            out.append({"i": i, "x": float(s[0]), "z": float(s[1]), "path": "sphere", "seam": False})
        elif isinstance(s, dict):
            out.append({"i": i, "x": float(s["x"]), "z": float(s["z"]), "path": str(s.get("path", "sphere")), "seam": bool(s.get("seam", False))})
        else:
            raise ValueError("site %d: [x, z] or {x, z, path, seam}" % i)
    return out

def horizontal_scale(y):
    return RS / (RS + y)

def columns_of(site, kind, quick=False, rh=None):
    """The column sets of a site (SN x, z): footprint 9 x 9 over +-1.2 rh, n3 8 x 8 inside 0.7 r, rim 32 on 1.5 r-3 r, axis."""
    from analyze_s8a import grid_columns, ring_columns   # noqa: E402 (the analyzer's generators: one definition)
    x, z = site["x"], site["z"]
    r = RADIUS_M
    rh = rh or r
    if kind == "footprint":
        return grid_columns(x, z, 1.2 * rh, QUICK_FOOTPRINT_N if quick else FOOTPRINT_N)
    if kind in ("n3", "seam"):
        return grid_columns(x, z, 0.7 * rh / math.sqrt(2.0), QUICK_N3_N if quick else N3_N)
    if kind == "rim":
        return ring_columns(x, z, rh, QUICK_RIM_N if quick else RIM_N)
    return [(x, z)]

# ----------------------------------------------------------------------------------------------------------------
# the runner
# ----------------------------------------------------------------------------------------------------------------

def se_current_path(folder):
    """MySession.Static.CurrentPath as SE forms it for a save folder: absolute, no trailing separator (the key lower-cases it)."""
    return os.path.abspath(folder).rstrip("\\/")

def _runner_class(LCH):
    L5 = s5_module(LCH)
    analyzer(LCH)

    class S8aRunner(L5.S5Runner):
        """The scripted S8a parts. The action tables name every command in the plugins' own syntax (CONTRACT §5.4 "S8a
 control commands": SE de.s8a {status|drill|overlay|save|rays|probe}; Subnautica de.s8a {status|probe|verify|reload|rays}
 + S4's de.s4 status for the ray results; S2's de.teleport; the mutants through de.mutant)."""

        SE_ACTIONS = {
            "status": ("de.s8a", lambda: {"action": "status"}),
            "drill": ("de.s8a", lambda sn, r, path: {"action": "drill", "sn": list(sn), "radius": float(r), "path": path}),
            "overlay": ("de.s8a", lambda: {"action": "overlay", "cells": True}),
            "save": ("de.s8a", lambda: {"action": "save"}),
            "rays": ("de.s8a", lambda cols, label, from_y, to_y, material=False: dict({"action": "rays", "columns": [[float(x), float(z)] for x, z in cols], "label": label,
                                                                                      "fromY": float(from_y), "toY": float(to_y)}, **({"material": True} if material else {}))),
            "window": ("de.s8a", lambda kw=None: dict({"action": "window"}, **(kw or {}))),
            "step": ("de.s8a", lambda axis, sign: {"action": "step", "axis": axis, "sign": int(sign)}),
            "chart": ("de.s8a", lambda entity=None: dict({"action": "chart"}, **({"entity": entity} if entity is not None else {}))),
            "probe": ("de.s8a", lambda points, label, depths=None: dict({"action": "probe", "planet": True, "points": [[float(x), float(z)] for x, z in points], "label": label},
                                                                        **({"depths": [float(d) for d in depths]} if depths else {}))),
            "place": ("de.s3", lambda sn, block, plate, yaw, label: {"place": {"sn": list(sn), "yawDeg": float(yaw), "block": block, "plate": dict(plate), "label": label}}),
            "remove": ("de.s3", lambda: {"remove": True}),
            "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
            "ores": ("de.s8a", lambda kw=None: dict({"action": "ores"}, **(kw or {}))),
            "materialat": ("de.s8a", lambda points: {"action": "materialat", "points": [[float(x), float(y), float(z)] for x, y, z in points]}),
            "ring": ("de.s8a", lambda kw=None: dict({"action": "ring"}, **(kw or {}))),
            "beacon": ("de.s8a", lambda kw=None: dict({"action": "beacon"}, **(kw or {}))),
            "drillpass": ("de.s8a", lambda kw=None: dict({"action": "drillpass"}, **(kw or {}))),
            "picture": ("de.s7", lambda: {"action": "status"}),
            "outcrops": ("de.s8a", lambda kw=None: dict({"action": "outcrops"}, **(kw or {}))),
            "treadmill": ("de.s8a", lambda on: {"action": "chart", "treadmill": bool(on)}),
            "sample": ("de.s8a", lambda kw=None: dict({"action": "sample"}, **(kw or {}))),
            "orevoxels": ("de.s8a", lambda kw=None: dict({"action": "orevoxels"}, **(kw or {}))),
            "oreoverlay": ("de.s8a", lambda kw=None: dict({"action": "oreoverlay"}, **(kw or {}))),
        }
        STANDIN_ONLY = ()
        NS_OFFLINE = True           # ns reads the store's chunk and the install (read-only) for its offline rows and twin witnesses
        SN_ACTIONS = {
            "status": ("de.s8a", lambda: {"action": "status"}),
            "rays": ("de.s8a", lambda cols, label: {"action": "rays", "columns": [list(c) for c in cols], "label": label}),
            "raystatus": ("de.s4", lambda frm, mx: {"status": True, "from": int(frm), "max": int(mx)}),
            "rayscancel": ("de.s4", lambda: {"cancel": True}),
            "verify": ("de.s8a", lambda bx, by, bz, octrees=False: dict({"action": "verify", "bx": int(bx), "by": int(by), "bz": int(bz)}, **({"octrees": True} if octrees else {}))),
            "cellvis": ("de.s8a", lambda kw=None: dict({"action": "cellvis"}, **(kw or {}))),
            "reload": ("de.s8a", lambda: {"action": "reload", "all": True}),
            "probe": ("de.s8a", lambda sn: {"action": "probe", "sn": list(sn)}),
            "pose": ("de.s8a", lambda entity=None: dict({"action": "pose"}, **({"entity": entity} if entity is not None else {}))),
            "spawns": ("de.s8a", lambda kw=None: dict({"action": "spawns"}, **(kw or {}))),
            "deposits": ("de.s8a", lambda kw=None: dict({"action": "deposits"}, **(kw or {}))),
            "props": ("de.s8a", lambda kw=None: dict({"action": "props"}, **(kw or {}))),
            "life": ("de.s8a", lambda radius=40.0: {"action": "life", "radius": float(radius)}),
            "sample": ("de.s8a", lambda kw=None: dict({"action": "sample"}, **(kw or {}))),
            "orelook": ("de.s8a", lambda kw=None: dict({"action": "orelook"}, **(kw or {}))),
            "oresurface": ("de.s8a", lambda kw=None: dict({"action": "oresurface"}, **(kw or {}))),
            "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
        }

        def __init__(self, *args, **kw):
            super().__init__(*args, **kw)
            self.sites = load_sites(getattr(self.a, "sites", None))
            self.holes = {}             # i -> {"sn": [x, y, z], "s": scale, "landed": bool}
            self.seabed = {}            # i -> {(x, z): storageY} of the reference (SE storage before the drill)
            self.relaunch_needed = False
            self.relaunched = False
            self.mutants = []
            self.stage_b_used = False
            self.placed = 0

        def scale(self, secs, quick=None):
            return secs if not self.q else (quick if quick is not None else max(1.0, secs * 0.25))

        def tick_now(self):
            return self.state().get("c1Tick")

        # ---- rays --------------------------------------------------------------------------------------------------
        def se_rays(self, site, kind, when, cols, y_ref=None, material=False, span=(20.0, 40.0)):
            """de.s8a rays on SE: storage surface and CastRay per column (SN y) from y_ref + span[0] down to y_ref - span[1], with material
 the SE surface material too; chunked; one rays event with every result."""
            label = "%s.%d.%s.se" % (when, site, kind)
            y0 = y_ref if y_ref is not None else -17.0
            results, state = [], "done"
            for k in range(0, len(cols), SE_RAY_CHUNK):
                chunk = cols[k:k + SE_RAY_CHUNK]
                rep = self.act("se", "rays", chunk, label, y0 + span[0], y0 - span[1], material, label="%s.%d" % (label, k // SE_RAY_CHUNK), timeout=20.0)
                if not self.ok(rep):
                    state = "failed"
                    continue
                hits = rep.get("hits")
                if isinstance(hits, list):
                    results += [h for h in hits if isinstance(h, dict)]
            self.sess.event("rays", label=label, side="se", site=site, kind=kind, when=when, columns=[[x, z, y0] for x, z in cols], results=results, state=state)
            return {(round(float(h["x"]), 3), round(float(h["z"]), 3)): h for h in results if "x" in h and "z" in h}

        def sn_rays(self, site, kind, when, cols, hints=None, y_ref=None):
            """de.s8a rays on Subnautica (the S4 probe: layer-30 rays, camera held over each column), the results paged
 through de.s4 status until the batch is done; one rays event."""
            label = "%s.%d.%s.sn" % (when, site, kind)
            hints = hints or {}
            cols3 = []
            for x, z in cols:
                h = hints.get((round(x, 3), round(z, 3)))
                cols3.append([float(x), float(z), float(h if h is not None else (y_ref if y_ref is not None else -17.0))])
            rep = self.act("sn", "rays", cols3, label, label=label, timeout=15.0)
            results, state = [], None
            if self.ok(rep):
                t0 = time.monotonic()
                limit = self.scale(30.0 + 6.0 * len(cols3), 10.0)
                fresh = False
                while time.monotonic() - t0 < limit and self.alive():
                    if not fresh:
                        self.wait(0.5)
                    st = self.act("sn", "raystatus", len(results), 64, label="%s.status" % label)
                    fresh = False
                    if not self.ok(st):
                        continue
                    got = st.get("results") or []
                    if isinstance(got, list):
                        seen = {q.get("i") for q in results}
                        for r in got:
                            if isinstance(r, dict) and r.get("i") not in seen:
                                results.append(r)
                                seen.add(r.get("i"))
                                fresh = True
                    state = st.get("state")
                    done = st.get("done") if isinstance(st.get("done"), int) else len(results)
                    if state in ("done", "cancelled", "failed") and (len(results) >= done or not fresh):
                        break
                else:
                    if self.alive():
                        self.act("sn", "rayscancel", label="%s.cancel" % label)
                        state = "timeout"
            else:
                state = "refused"
            self.sess.event("rays", label=label, side="sn", site=site, kind=kind, when=when, columns=cols3, results=results, state=state, reply=rep)
            self.log.say("  S8a Subnautica rays %s: %d of %d columns (%s)" % (label, len(results), len(cols3), state))
            return results

        # ---- the drill ---------------------------------------------------------------------------------------------
        def wait_applied(self, site, since, timeout=None, phase="after"):
            """Until SE's feed reports every notice applied (C7 appliedSeq = the last seq, nothing pending), at least
 APPLY_MIN_S after the drill (the witness and the coalescing come first), at most `timeout` (APPLY_TIMEOUT_S; the window
 drills wait WINDOW_APPLY_WAIT_S: a teleport makes the ring's burst of notices come first). phase "settle" = before a drill."""
            timeout = APPLY_TIMEOUT_S if timeout is None else timeout
            t0 = time.monotonic()
            feed = {}
            ok = False
            while self.alive():
                el = time.monotonic() - since
                if el >= APPLY_MIN_S:
                    rep = self.act("se", "status", label="n1.%d.status" % site)
                    feed = (rep or {}).get("feed") if isinstance(rep, dict) else {}
                    feed = feed if isinstance(feed, dict) else {}
                    pend, applied, nxt = feed.get("pending"), feed.get("appliedSeq"), feed.get("nextSeq")
                    if pend == 0 and isinstance(applied, (int, float)) and isinstance(nxt, (int, float)) and applied >= nxt - 1:
                        ok = True
                        break
                if time.monotonic() - t0 >= timeout:
                    break
                self.wait(0.5)
            self.sess.event("applied", i=site, ok=ok, phase=phase, waited=round(time.monotonic() - since, 2),
                            feed={k: feed.get(k) for k in ("pending", "ackSeq", "appliedSeq", "nextSeq", "snVersion")})
            return ok

        def drill(self, site):
            s = self.sites[site]
            i = s["i"]
            x, z = s["x"], s["z"]
            quick = self.q
            # the reference: SE's storage surface before the cut, over every column set the later parts use
            before = {}
            for kind in ("footprint", "n3", "rim") + (("seam",) if s["seam"] else ()):
                before.update(self.se_rays(i, kind, "before", columns_of(s, kind, quick)))
            self.seabed[i] = {k: v.get("storageY") for k, v in before.items() if isinstance(v.get("storageY"), (int, float)) and not math.isnan(v["storageY"])}
            centre = self.seabed[i].get((round(x, 3), round(z, 3)))
            if centre is None:
                vals = sorted(self.seabed[i].values())
                centre = vals[len(vals) // 2] if vals else None
            if centre is None:
                self.log.say("  S8a site %d at SN (%g, %g): no storage surface (not on a tile?): skipped" % (i, x, z), ev="s8a_site_skipped")
                self.sess.event("drill", i=i, sn=[x, None, z], radius=RADIUS_M, path=s["path"], seam=s["seam"], skipped="no storage surface")
                return False
            y = float(centre)
            sc = horizontal_scale(y)
            self.teleport(x + CAMERA_BACK_M, y + CAMERA_UP_M, z, 270.0, label="n1.%d.camera" % i)
            self.wait(self.scale(1.5, 1.0))
            t_drill = time.monotonic()
            tick = self.tick_now()
            rep = self.act("se", "drill", (x, y, z), RADIUS_M, s["path"], label="n1.%d.drill" % i, timeout=10.0)
            self.sess.event("drill", i=i, sn=[x, y, z], radius=RADIUS_M, path=s["path"], seam=s["seam"], s=sc, tick=tick, reply=rep)
            landed = self.ok(rep)
            self.holes[i] = {"sn": [x, y, z], "s": sc, "landed": landed}
            if landed:
                self.wait_applied(i, t_drill)
                self.sn_rays(i, "axis", "after", [(x, z)], self.seabed[i], y)
            rest = DRILL_GAP_S - (time.monotonic() - t_drill)
            if rest > 0:
                self.wait(self.scale(rest, 1.0))
            return landed

        def drill_sites(self, indices):
            for k in indices:
                if k < len(self.sites) and not self.alive():
                    break
                if k < len(self.sites):
                    self.drill(k)

        def ensure_holes(self, indices=None):
            idx = list(indices) if indices is not None else list(range(len(self.sites)))
            missing = [k for k in idx if k not in self.holes]
            if missing:
                self.drill_sites(missing)

        def hole_rh(self, i):
            h = self.holes.get(i) or {}
            return RADIUS_M * float(h.get("s") or 1.0)

        def hole_y(self, i):
            h = self.holes.get(i) or {}
            sn = h.get("sn") or [0, None, 0]
            return sn[1] if sn[1] is not None else -17.0

        def both_rays(self, i, kind, when):
            s = self.sites[i]
            cols = columns_of(s, kind, self.q, self.hole_rh(i))
            y = self.hole_y(i)
            self.se_rays(i, kind, when, cols, y)
            self.sn_rays(i, kind, when, cols, self.seabed.get(i), y)

        # ---- parts -------------------------------------------------------------------------------------------------
        def p_n1(self):
            """S8a.N1: de.s8a drill at the six sites (sphere x 4, hand x 2, one on the tile seam), 10 s apart, camera 12 m
 away; SE's witness (IsRangeModified, storage column), the feed applied, a Subnautica ray down each axis"""
            self.part("n1", "begin")
            self.drill_sites(range(len(self.sites)))
            self.part("n1", "end")

        def p_n2(self):
            """S8a.N2: 9 x 9 Subnautica rays over each hole's footprint (vs the analytic carve); at the seam site SE and
 Subnautica rays over the inner columns (both overlapping tiles carved)"""
            self.part("n2", "begin")
            self.ensure_holes()
            for s in self.sites:
                i = s["i"]
                if not (self.holes.get(i) or {}).get("landed"):
                    continue
                cols = columns_of(s, "footprint", self.q, self.hole_rh(i))
                self.sn_rays(i, "footprint", "after", cols, self.seabed.get(i), self.hole_y(i))
                if s["seam"]:
                    self.both_rays(i, "seam", "after")
            self.part("n2", "end")

        def p_n3(self):
            """S8a.N3: 64 columns within 0.7 r of each axis and 32 rim columns (1.5 r-3 r): SE CastRay vs Subnautica layer-30"""
            self.part("n3", "begin")
            self.ensure_holes()
            for s in self.sites:
                i = s["i"]
                if not (self.holes.get(i) or {}).get("landed"):
                    continue
                self.both_rays(i, "n3", "after")
                self.both_rays(i, "rim", "after")
            self.part("n3", "end")

        def p_n4(self):
            """S8a.N4 (world copy "DE Lab S8aSave", two launches): 3 holes, rays, de.s8a overlay, the scripted save (de.s8a
 save; then both games are closed and relaunched (p_n4b)"""
            self.part("n4", "begin")
            self.ensure_holes(range(3))
            for s in self.sites[:3]:
                i = s["i"]
                if (self.holes.get(i) or {}).get("landed"):
                    self.both_rays(i, "n3", "presave")
            rep = self.act("se", "overlay", label="n4.overlay")
            self.sess.event("overlay", reply=rep)
            t_save = time.time()
            srep = self.act("se", "save", label="n4.save", timeout=15.0)
            how = "scripted"
            if not self.ok(srep):
                how = "owner" if self.owner("n4.save", "save the game now (Esc -> Save); this is the throwaway copy %r" % getattr(self.a, "world", None), 45.0) else "none"
            self.sess.event("save", reply=srep, how=how)
            self.wait(self.scale(5.0, 1.0))
            self.check_save_file(t_save)
            self.relaunch_needed = True
            self.part("n4", "end")

        def check_save_file(self, t_save):
            """The overlay file of the save decoded (tools/planet/s8a_ref.py's twin of S8aOverlayFile): cells and hash."""
            world = getattr(self.a, "world", None) or PART_WORLD["n4"]
            info = self.LCH.s4_world_info(world)
            folder = info.get("folder")
            path = os.path.join(folder, OVERLAY_FILE) if folder else None
            ev = {"path": path, "exists": False, "bytes": 0, "ok": False, "error": None}
            if not folder:
                ev["error"] = info.get("error") or "world folder not found"
                self.sess.event("savefile", **ev)
                return
            t0 = time.monotonic()
            limit = self.scale(SAVE_FILE_TIMEOUT_S, 3.0)
            last_size = -1
            while time.monotonic() - t0 < limit and self.alive():
                try:
                    st = os.stat(path)
                    if st.st_mtime >= t_save - 2.0 and st.st_size == last_size and st.st_size > 0:
                        break
                    last_size = st.st_size
                except OSError:
                    pass
                self.wait(1.0)
            try:
                with open(path, "rb") as f:
                    data = f.read()
                ev["exists"], ev["bytes"] = True, len(data)
                ref = ref_module(self.LCH)
                ver, site, cells, stored = ref.overlay_decode(data)
                ev.update(ok=True, version=ver, site=site, cells=len(cells), hash="%016x" % stored)
            except FileNotFoundError:
                ev["error"] = "missing after %.0f s" % (time.monotonic() - t0)
            except Exception as e:
                ev["error"] = "%s: %s" % (type(e).__name__, e)
            self.sess.event("savefile", **ev)
            self.log.say("  S8a save file %s: %s" % (path, "cells %s hash %s" % (ev.get("cells"), ev.get("hash")) if ev["ok"] else ev["error"]), ev="s8a_savefile")

        def p_n4b(self):
            """S8a.N4 after the relaunch on the copy: SE and Subnautica rays at the 3 holes (the save kept them)"""
            self.part("n4b", "begin")
            for s in self.sites[:3]:
                i = s["i"]
                if (self.holes.get(i) or {}).get("landed"):
                    self.both_rays(i, "n3", "relaunch")
            self.part("n4b", "end")

        def p_n5(self):
            """S8a.N5: de.s8a reload {all: true} on Subnautica (every served batch and its clipmap cells), N3's rays again,
 the store's manifest and files checked (size, FNV)"""
            self.part("n5", "begin")
            self.ensure_holes()
            rep = self.act("sn", "reload", label="n5.reload", timeout=15.0)
            t0 = time.monotonic()
            limit = self.scale(RELOAD_TIMEOUT_S, 3.0)
            self.wait(self.scale(2.0, 1.0))
            while time.monotonic() - t0 < limit and self.alive():
                st = self.act("sn", "status", label="n5.status")
                if self.ok(st) and st.get("inFlight") == 0 and (st.get("jobs") or 0) == 0:
                    break
                self.wait(1.0)
            self.sess.event("reloaded", reply=rep, waited=round(time.monotonic() - t0, 2))
            for s in self.sites:
                i = s["i"]
                if s["seam"] or not (self.holes.get(i) or {}).get("landed"):
                    continue
                self.both_rays(i, "n3", "reload")
            self.store_check()
            self.part("n5", "end")

        def store_check(self):
            """The drill layer's manifest and files, read-only (CONTRACT "store.manifest"): size and FNV-1a 64 per listed file."""
            rep = self.act("se", "status", label="n5.se.status")
            ov = (rep or {}).get("overlay") if isinstance(rep, dict) else None
            cv = (rep or {}).get("converter") if isinstance(rep, dict) else None
            key = (ov or {}).get("storeKey") if isinstance(ov, dict) else None
            folder = (cv or {}).get("folder") if isinstance(cv, dict) else None
            root = (cv or {}).get("root") if isinstance(cv, dict) else None
            ev = {"storeKey": key, "folder": folder, "root": root, "manifest": {"entries": 0, "key": None, "hash": None, "error": None}, "files": []}
            if not folder:
                ev["error"] = "no converter folder in de.s8a status"
                self.sess.event("store_check", **ev)
                return
            try:
                ref = ref_module(self.LCH)
                with open(os.path.join(folder, "manifest.v1.txt"), encoding="utf-8") as f:
                    lines = [ln.rstrip("\r\n") for ln in f]
                head = lines[0].split(" ") if lines else []
                ev["manifest"].update(key=head[3] if len(head) > 3 else None, hash=head[4] if len(head) > 4 else None)
                for ln in lines[1:]:
                    if not ln.strip():
                        continue
                    p = ln.split(" ", 7)
                    bx, by, bz, version, size, fnv, flags, rel = int(p[0]), int(p[1]), int(p[2]), int(p[3]), int(p[4]), p[5].lower(), int(p[6]), p[7]
                    full = os.path.join(root or os.path.dirname(os.path.dirname(folder)), rel)
                    fe = {"bx": bx, "by": by, "bz": bz, "version": version, "bytes": size, "fnv": fnv, "flags": flags, "path": full, "exists": os.path.exists(full),
                          "bytesOnDisk": -1, "fnvOnDisk": None, "ok": False}
                    if fe["exists"]:
                        with open(full, "rb") as f:
                            data = f.read()
                        fe["bytesOnDisk"] = len(data)
                        fe["fnvOnDisk"] = "%016x" % ref.fnv1a64(data)
                        fe["ok"] = fe["bytesOnDisk"] == size and fe["fnvOnDisk"] == fnv
                    ev["files"].append(fe)
                ev["manifest"]["entries"] = len(ev["files"])
            except Exception as e:
                ev["manifest"]["error"] = "%s: %s" % (type(e).__name__, e)
                ev["error"] = ev["manifest"]["error"]
            self.sess.event("store_check", **ev)
            self.log.say("  S8a store check %s: %d entries, %d ok" % (folder, len(ev["files"]), sum(1 for f in ev["files"] if f["ok"])), ev="s8a_store_check")

        def p_n6(self):
            """S8a.N6: the install listing before/after is taken by the session around the games; this part only marks the window"""
            self.part("n6", "begin")
            self.part("n6", "end")

        # ---- Stage B: the window, the treadmill, the throttle, materials and spawns ------------------------------
        def seabed_at(self, x, z, label):
            """SE's surface at one column: the physics hit (castY) where SE has physics there, else the storage surface (the tiles'
 or, beyond them, the planet's LOD-0 mesh, storageY; Stage C fix); NaN when neither."""
            hits = self.se_rays(WINDOW_DRILL_SITE, "spot", label, [(x, z)], WINDOW_FALLBACK_Y)
            return se_surface(hits.get((round(x, 3), round(z, 3))))

        def path_columns(self, p0, p1, n):
            return [(round(p0[0] + (p1[0] - p0[0]) * k / (n - 1), 3), round(p0[1] + (p1[1] - p0[1]) * k / (n - 1), 3)) for k in range(n)]

        def window_path(self, name, p0, p1, speed, columns=True):
            """One spectator path over the generated seabed: the camera WINDOW_ABOVE_M above SE's seabed at p0 (or WINDOW_FALLBACK_Y), a
 line at `speed`; then SE and Subnautica rays at columns along it (rays kind "path", when = name)."""
            seabed = self.seabed_at(p0[0], p0[1], "%s.seabed" % name)
            y = seabed + WINDOW_ABOVE_M if not math.isnan(seabed) else WINDOW_FALLBACK_Y
            self.teleport(p0[0], y, p0[1], 0.0, label="%s.start" % name)
            self.wait(self.scale(3.0, 1.0))
            dist = math.hypot(p1[0] - p0[0], p1[1] - p0[1])
            self.part(name, "begin")
            self.sess.event("path", name=name, fromSn=[p0[0], y, p0[1]], toSn=[p1[0], y, p1[1]], speed=speed, seabedAtStart=seabed, seconds=dist / speed)
            self.motion({"kind": "line", "fromSn": [p0[0], y, p0[1]], "toSn": [p1[0], y, p1[1]], "speedMps": float(speed)}, "%s.motion" % name)
            self.wait(self.scale(dist / speed + 2.0, 4.0))
            self.part(name, "end")
            if columns:
                cols = self.path_columns(p0, p1, QUICK_WINDOW_COLUMNS if self.q else WINDOW_COLUMNS)
                self.se_rays(WINDOW_DRILL_SITE, "path", name, cols, y - WINDOW_ABOVE_M)
                self.sn_rays(WINDOW_DRILL_SITE, "path", name, cols, None, y - WINDOW_ABOVE_M)
            return y

        def wait_sn_built(self, label, timeout=None):
            """Until Subnautica reports its area built with no reload in flight, twice in a row (the teleport's streaming over), or the
 timeout; one event with the wait."""
            t0 = time.monotonic()
            good, rep = 0, None
            while time.monotonic() - t0 < (timeout or self.scale(ND_BUILT_WAIT_S, 2.0)) and self.alive():
                rep = self.act("sn", "status", label=label)
                if self.ok(rep) and rep.get("areaBuilt") and not rep.get("inFlight") and not rep.get("jobs"):
                    good += 1
                    if good >= 2:
                        break
                else:
                    good = 0
                self.wait(0.5)
            self.sess.event("sn_built", label=label, waited=round(time.monotonic() - t0, 1), ok=good >= 2)
            return good >= 2

        def wait_window_quiet(self, label, timeout):
            """Until SE's window reports nothing pending (the ring generated) or the timeout."""
            t0 = time.monotonic()
            rep = None
            while time.monotonic() - t0 < timeout and self.alive():
                rep = self.act("se", "window", label=label)
                if self.ok(rep) and rep.get("pending") == 0 and not rep.get("workerBusy"):
                    break
                self.wait(1.0)
            self.sess.event("window_quiet", label=label, waited=round(time.monotonic() - t0, 1), reply=rep)
            return rep

        def settle_window(self, label, site):
            """The window ring at the camera generated (SE nothing pending) and applied by Subnautica (C7 appliedSeq caught up)."""
            self.wait_window_quiet(label, self.scale(WINDOW_APPLY_WAIT_S, 3.0))
            self.wait_applied(site, time.monotonic() - APPLY_MIN_S, timeout=self.scale(WINDOW_APPLY_WAIT_S, 3.0), phase="settle")

        def p_n7(self):
            """S8a.N7 (the sculpt deployed, TreadmillOn off): the window paths (1,800, y, 0) -> (4,600, y, 0) and along the ring at
 20 and 80 m/s, 60 m above SE's seabed, rays at 200 columns; a 6 m drill into the generated seabed at (3,200, 0)"""
            self.stage_b_used = True
            self.part("n7", "begin")
            self.act("se", "window", {"throttleMs": 0, "on": True}, label="n7.window")
            for speed in WINDOW_SPEEDS:
                self.window_path("n7.path.%d" % int(speed), WINDOW_PATH[0], WINDOW_PATH[1], speed)
                for k in range(len(WINDOW_RING) - 1):
                    self.window_path("n7.ring.%d.%d" % (int(speed), k), WINDOW_RING[k], WINDOW_RING[k + 1], speed, columns=(k == 0))
            # the drill into the generated seabed: the batch regenerates (version + 1) and Subnautica's rays see the hole
            x, z = WINDOW_DRILL_AT
            seabed = self.seabed_at(x, z, "n7.drill.seabed")
            if math.isnan(seabed):
                self.sess.event("drill", i=WINDOW_DRILL_SITE, sn=[x, None, z], radius=WINDOW_DRILL_R, path="sphere", seam=False, skipped="no SE surface at the window drill spot")
            else:
                site = {"i": WINDOW_DRILL_SITE, "x": x, "z": z, "path": "sphere", "seam": False}
                cols = columns_of(site, "footprint", self.q, WINDOW_DRILL_R * horizontal_scale(seabed))
                before = self.se_rays(WINDOW_DRILL_SITE, "wfootprint", "before", cols, seabed)
                self.seabed[WINDOW_DRILL_SITE] = {k: se_surface(v) for k, v in before.items() if not math.isnan(se_surface(v))}
                self.teleport(x + CAMERA_BACK_M, seabed + CAMERA_UP_M, z, 270.0, label="n7.drill.camera")
                self.wait(self.scale(2.0, 1.0))
                # Stage C fix (session 20261023-150323): the teleport's ring (~170 window notices) must be generated and applied
 # before the drill, else the regenerated batch queues behind it in Subnautica and the rays run before it applies
                self.settle_window("n7.drill.settle", WINDOW_DRILL_SITE)
                tick = self.tick_now()
                rep = self.act("se", "drill", (x, seabed, z), WINDOW_DRILL_R, "sphere", label="n7.drill", timeout=10.0)
                self.sess.event("drill", i=WINDOW_DRILL_SITE, sn=[x, seabed, z], radius=WINDOW_DRILL_R, path="sphere", seam=False, s=horizontal_scale(seabed), tick=tick, reply=rep, window=True)
                self.holes[WINDOW_DRILL_SITE] = {"sn": [x, seabed, z], "s": horizontal_scale(seabed), "landed": self.ok(rep)}
                if self.ok(rep):
                    # the regenerated version written by SE, then applied by Subnautica (collision rebuilt) before the rays
                    self.wait(self.scale(APPLY_MIN_S, 1.0))
                    self.wait_window_quiet("n7.drill.window", self.scale(WINDOW_APPLY_WAIT_S, 3.0))
                    self.wait_applied(WINDOW_DRILL_SITE, time.monotonic() - APPLY_MIN_S, timeout=self.scale(WINDOW_APPLY_WAIT_S, 3.0))
                    self.sn_rays(WINDOW_DRILL_SITE, "wfootprint", "after", cols, self.seabed[WINDOW_DRILL_SITE], seabed)
            self.part("n7", "end")

        def p_n8(self):
            """S8a.N8 (TreadmillOn): a path from the crater to rhoInf 12 km and back at 80 m/s; de.s8a step forced in open water (the
 moat), over terrain and 40 m above the sea, each with a test plate placed 30 m beside the camera and mirrored first, the
 plate and camera positions probed before and after (de.s8a chart {entity}, de.s8a pose) and SE's picture latch (de.s7
 status: no picture edge under 1 km, terrain 4 "chart.offsite")"""
            self.stage_b_used = True
            self.part("n8", "begin")
            y = WINDOW_FALLBACK_Y
            self.teleport(0.0, y, 0.0, 0.0, label="n8.start")
            self.wait(self.scale(2.0, 1.0))
            rep = self.act("se", "place", (TREADMILL_PLATE_OFFSET_M, y, 0.0), "LargeBlockArmorBlock", {"rows": 3, "cols": 3, "orientation": "floor"}, 0.0, "s8a.plate", label="n8.plate", timeout=30.0)
            plate = None
            if self.ok(rep):
                plate = rep.get("entityId") or rep.get("gridId")
                self.placed += 1
            self.sess.event("plate", reply=rep, entity=plate)
            self.plate = plate
            self.sess.event("path", name="n8.out", fromSn=[0.0, y, 0.0], toSn=[TREADMILL_FAR_M, y, 0.0], speed=TREADMILL_SPEED, seconds=TREADMILL_FAR_M / TREADMILL_SPEED)
            self.motion({"kind": "line", "fromSn": [0.0, y, 0.0], "toSn": [TREADMILL_FAR_M, y, 0.0], "speedMps": TREADMILL_SPEED}, "n8.out")
            steps = [("open", TREADMILL_STEP_OPEN_X, y), ("terrain", TREADMILL_STEP_TERRAIN_X, y), ("above", TREADMILL_STEP_ABOVE_X, TREADMILL_ABOVE_Y)]
            t0 = time.monotonic()
            for kind, at_x, sy in steps:
                # the step when the camera's site-frame x (de.s8a chart camSite: chart 0's frame, unchanged by the steps) reaches at_x; else by time
                self.wait_site_x(at_x, self.scale(at_x / TREADMILL_SPEED, 1.0) + 30.0, "n8.watch.%s" % kind)
                self.forced_step(kind, sy)
            far = self.wait_site_x(TREADMILL_FAR_M - 10.0, self.scale(TREADMILL_FAR_M / TREADMILL_SPEED, 1.0) + 30.0, "n8.far")
            self.sess.event("path", name="n8.back", fromSn=[TREADMILL_FAR_M, y, 0.0], toSn=[0.0, y, 0.0], speed=TREADMILL_SPEED, seconds=TREADMILL_FAR_M / TREADMILL_SPEED)
            cam, off = self.chart_offset(far)
            self.motion({"kind": "line", "fromSn": [cam[0], y, cam[2]] if cam else [TREADMILL_FAR_M, y, 0.0], "toSn": [0.0 + off[0], y, 0.0 + off[2]], "speedMps": TREADMILL_SPEED}, "n8.back")
            self.wait(self.scale(TREADMILL_FAR_M / TREADMILL_SPEED + 2.0, 2.0))
            self.act("se", "chart", label="n8.chart.end")
            # night queue (S8a.N8.snap): a step of more than 20 m resets Subnautica's C1 history (no bracket across it), so
 # the snap is judged on a change Subnautica brackets: the same-chart return (step site on chart 0: T = 0, no jump)
            self.act("se", "step", "site", 1, label="n8.step.same")
            self.wait(self.scale(2.0, 2.0))
            self.part("n8", "end")

        def chart_offset(self, chart):
            """(camSn, camSn - camSite) of a de.s8a chart reply: the current chart frame's offset from the site frame (None, [0, 0, 0] without a probe)."""
            cam = (chart or {}).get("camSn") if self.ok(chart) else None
            site = (chart or {}).get("camSite") if self.ok(chart) else None
            if not cam or not site:
                return None, [0.0, 0.0, 0.0]
            return cam, [cam[i] - site[i] for i in range(3)]

        def wait_site_x(self, x, timeout, label):
            """Polls de.s8a chart (2 Hz) until the camera's site-frame x reaches x or the timeout; the last reply."""
            t0 = time.monotonic()
            rep = None
            while self.alive():
                rep = self.act("se", "chart", label=label)
                site = (rep or {}).get("camSite") if self.ok(rep) else None
                if site and site[0] >= x:
                    break
                if time.monotonic() - t0 >= timeout:
                    break
                self.wait(0.5)
            return rep

        def plate_beside(self, kind, sy):
            """terrain 4 (N8.jump MISSING: the start plate lay kilometres behind, never mirrored at the step): the previous plate removed,
 a new one 30 m beside the camera (+z, the current chart's SN frame) at the camera's height, then Subnautica's mirror polled
 (de.s8a pose {entity}) until it draws it (TREADMILL_PLATE_WAIT_S)."""
            cur = self.act("se", "chart", label="n8.step.%s.cam" % kind)
            cam = (cur or {}).get("camSn") if self.ok(cur) else None
            if getattr(self, "plate", None) is not None:
                self.act("se", "remove", label="n8.step.%s.unplace" % kind)
                self.plate = None
            if not cam:
                return None
            rep = self.act("se", "place", (cam[0], sy, cam[2] + TREADMILL_PLATE_OFFSET_M), "LargeBlockArmorBlock", {"rows": 3, "cols": 3, "orientation": "floor"}, 0.0,
                           "s8a.plate.%s" % kind, label="n8.step.%s.plate" % kind, timeout=30.0)
            plate = (rep.get("entityId") or rep.get("gridId")) if self.ok(rep) else None
            if plate is not None:
                self.placed += 1
            self.plate = plate
            t0 = time.monotonic()
            found = False
            while plate is not None and time.monotonic() - t0 < self.scale(TREADMILL_PLATE_WAIT_S, 1.0) and self.alive():
                sp = self.act("sn", "pose", plate, label="n8.step.%s.mirrored" % kind)
                if self.ok(sp) and sp.get("entityFound"):
                    found = True
                    break
                self.wait(0.3)
            self.sess.event("plate", kind=kind, reply=rep, entity=plate, mirrored=found, waited=round(time.monotonic() - t0, 2))
            return plate

        def forced_step(self, kind, sy=WINDOW_FALLBACK_Y):
            """One forced treadmill step with the motion paused across it: a plate placed beside the camera and
 mirrored first; the poses on both sides before (de.s8a chart: SE's camera and the plate under the current chart; de.s8a
 pose: Subnautica's camera and the plate's drawn frame), SE's picture (de.s7 status: the latch and its edge counter), the
 step, the wait until Subnautica applied the chart (status chartSeqApplied), the poses and the picture after; then the
 outbound motion resumes under the new chart (the far end shifted by the chart's offset from the site frame). The
 "above" step lifts the camera to sy (40 m above the sea) first (N8.picture: no picture edge under 1 km)."""
            self.motion({"kind": "stop"}, "n8.step.%s.hold" % kind)
            self.wait(self.scale(1.5, 0.3))
            if sy != WINDOW_FALLBACK_Y:
                cur = self.act("se", "chart", label="n8.step.%s.lift" % kind)
                cam = (cur or {}).get("camSn") if self.ok(cur) else None
                if cam:
                    self.teleport(cam[0], sy, cam[2], 0.0, label="n8.step.%s.up" % kind)
                    self.wait(self.scale(3.0, 0.5))
            plate = self.plate_beside(kind, sy)
            pic_before = self.act("se", "picture", label="n8.step.%s.picture.before" % kind)
            before = self.act("se", "chart", plate, label="n8.step.%s.before" % kind)
            sn_before = self.act("sn", "pose", plate, label="n8.step.%s.sn.before" % kind)
            rep = self.act("se", "step", "x", 1, label="n8.step.%s" % kind, timeout=10.0)
            want = (rep or {}).get("chartSeq") if self.ok(rep) else None
            applied = None
            t0 = time.monotonic()
            while time.monotonic() - t0 < self.scale(5.0, 0.5) and self.alive():
                st = self.act("sn", "status", label="n8.step.%s.applied" % kind)
                applied = (st or {}).get("chartSeqApplied") if self.ok(st) else None
                if want is None or (applied is not None and applied >= want):
                    break
                self.wait(0.2)
            self.wait(self.scale(0.5, 0.1))
            after = self.act("se", "chart", plate, label="n8.step.%s.after" % kind)
            sn_after = self.act("sn", "pose", plate, label="n8.step.%s.sn.after" % kind)
            self.wait(self.scale(2.0, 0.3))
            pic_after = self.act("se", "picture", label="n8.step.%s.picture.after" % kind)
            self.sess.event("chart_step", kind=kind, entity=plate, before=before, step=rep, after=after, snBefore=sn_before, snAfter=sn_after, snApplied=applied, tick=self.tick_now(),
                            altitude=sy, pictureBefore=pic_before if isinstance(pic_before, dict) else None, pictureAfter=pic_after if isinstance(pic_after, dict) else None)
            cam, off = self.chart_offset(after)
            y = WINDOW_FALLBACK_Y
            self.motion({"kind": "line", "fromSn": [cam[0], y, cam[2]] if cam else [0.0, y, 0.0], "toSn": [TREADMILL_FAR_M + off[0], y, 0.0 + off[2]], "speedMps": TREADMILL_SPEED},
                        "n8.step.%s.resume" % kind)
            self.wait(self.scale(3.0, 1.0))

        def store_key_check(self, derived):
            """Review Stage A minor: the store key SE derived from MySession.CurrentPath (de.s8a status overlay.storeKey) against the launcher's; a mismatch is logged (the pre-launch store reset used the derived one)."""
            st = self.act("se", "status", label="store.key")
            se_key = ((st or {}).get("overlay") or {}).get("storeKey") if self.ok(st) else None
            match = bool(derived) and bool(se_key) and str(se_key).lower() == str(derived).lower()
            self.sess.event("store_key", derived=derived, se=se_key, match=match)
            if derived and se_key and not match:
                self.log.say("  WARN: store key %s derived from the world folder, SE uses %s (the pre-launch store reset emptied the wrong folder)" % (derived, se_key))
            return match

        def p_n9(self):
            """S8a.N9: the generator throttled (de.s8a window {throttleMs}) on the 80 m/s window path: Subnautica reports not built
 while SE's surface is near and a near-box batch is pending, and its picture is back within 1.5 s of the last near notice"""
            self.stage_b_used = True
            self.part("n9", "begin")
            ms = int(getattr(self.a, "throttle_ms", THROTTLE_MS) or THROTTLE_MS)
            self.act("se", "window", {"throttleMs": ms, "on": True}, label="n9.throttle")
            self.sess.event("throttle", ms=ms)
            self.window_path("n9.path.80", WINDOW_PATH[0], WINDOW_PATH[1], 80.0, columns=False)
            self.wait(self.scale(10.0, 2.0))
            self.act("se", "window", {"throttleMs": 0}, label="n9.throttle.off")
            self.part("n9", "end")

        def p_n10(self):
            """S8a.N10: spawns on, the 20 m/s window path and ring (>= 1 km^2 swept); then at 200 columns SE's surface material (de.s8a rays {material}) vs
 Subnautica's top solid voxel type (de.s8a probe at the served file's seabed); then the ore probes (S8a.N10.ore, Stage C)"""
            self.stage_b_used = True
            self.part("n10", "begin")
            self.act("sn", "spawns", {"on": True}, label="n10.spawns.on")
            y = self.window_path("n10.path.20", WINDOW_PATH[0], WINDOW_PATH[1], 20.0, columns=False)
            self.window_path("n10.ring.20", WINDOW_RING[0], WINDOW_RING[1], 20.0, columns=False)   # >= 1 km^2 of swept seabed for S8a.N10.spawn
            n = QUICK_N10_COLUMNS if self.q else N10_COLUMNS
            cols = self.path_columns(WINDOW_PATH[0], WINDOW_PATH[1], n)
            se = self.se_rays(WINDOW_DRILL_SITE, "material", "n10", cols, y - WINDOW_ABOVE_M, material=True)
            # the window's columns only (outside the crater box: the install's own terrain is not converted) in the batch layers the
 # ring generated around the path's camera (RingBatches^3 around the streaming centre); the rest is reported, not probed
            by_c = int(math.floor((y - WINDOW_STREAM_DOWN_M + 3040.0) / 160.0))
            half = WINDOW_RING_BATCHES // 2
            probes, skipped = [], {"crater": 0, "noSurface": 0, "notGenerated": 0}
            for x, z in cols:
                h = se.get((round(x, 3), round(z, 3)))
                cy = se_surface(h)
                if -2048.0 <= x < 2112.0 and -2048.0 <= z < 2112.0:
                    skipped["crater"] += 1
                    continue
                if math.isnan(cy):
                    skipped["noSurface"] += 1
                    continue
                py = cy - MATERIAL_PROBE_DEPTH_M
                if abs(int(math.floor((py + 3040.0) / 160.0)) - by_c) > half:
                    skipped["notGenerated"] += 1
                    continue
                # the converted surface voxel (density != 0) under SE's surface:
 # MATERIAL_PROBE_DEPTH_M first, then deeper while outside, shallower while far-solid
                depth = MATERIAL_PROBE_DEPTH_M
                rep = self.act("sn", "probe", (x, cy - depth, z), label="n10.probe")
                if self.ok(rep) and not rep.get("solid"):
                    for d2 in MATERIAL_PROBE_DEEPER_M:
                        depth = d2
                        rep = self.act("sn", "probe", (x, cy - depth, z), label="n10.probe")
                        if not self.ok(rep) or rep.get("solid"):
                            break
                elif self.ok(rep) and (rep.get("density") or 0) == 0:
                    for d2 in MATERIAL_PROBE_SHALLOWER_M:
                        r2 = self.act("sn", "probe", (x, cy - d2, z), label="n10.probe")
                        if self.ok(r2) and r2.get("solid") and (r2.get("density") or 0) != 0:
                            rep, depth = r2, d2
                            break
                probes.append({"x": x, "z": z, "seY": cy, "seMaterial": h.get("material"), "snType": rep.get("type") if self.ok(rep) else None,
                               "snDensity": rep.get("density") if self.ok(rep) else None, "solid": rep.get("solid") if self.ok(rep) else None,
                               "source": rep.get("source") if self.ok(rep) else None, "file": rep.get("file", True) if self.ok(rep) else None, "probeDepth": depth})
            self.sess.event("material_probe", columns=probes, n=len(cols), skipped=skipped, ringLayers=[by_c - half, by_c + half])
            self.ore_probes()
            self.act("sn", "spawns", label="n10.spawns.status")
            self.part("n10", "end")

        def p_n10c(self):
            """S8a.N10.crater (Stage E): the camera visits CRATER_SPOTS (Subnautica wakes their cells; any large resource deposit it
 starts there is reported into the store), SE's watcher reads the report (5 s), then de.s8a ores: the cube rule's pockets
 read back from the tiles' voxels over the depth bands and the pockets under the reported deposits"""
            self.part("n10c", "begin")
            for k, (x, y, z) in enumerate(CRATER_SPOTS[:2] if self.q else CRATER_SPOTS):
                self.teleport(x, y, z, 0.0, label="n10c.spot.%d" % k)
                self.wait(self.scale(12.0, 4.0))
            self.wait(self.scale(8.0, 6.0))
            rep = self.act("se", "ores", {"count": QUICK_CRATER_ORE_CUBES if self.q else CRATER_ORE_CUBES}, label="n10c.ores", timeout=90.0)
            sn = self.act("sn", "status", label="n10c.sn.status")
            self.sess.event("crater_ores", reply=rep if isinstance(rep, dict) else None,
                            snDeposits={k: sn.get(k) for k in ("craterDeposits", "craterDepositsWritten", "drillablePatched")} if self.ok(sn) else None)
            self.part("n10c", "end")

        def p_nd(self):
            """S8a.ND (Stage D, drilling like SE's own): per pass (the Subnautica map, the planet) the camera beside the line, Subnautica's
 per-frame cell rows on (de.s8a cellvis), a continuous drill (de.s8a drillpass: a cut every 250 ms for 20 s along 40 m, 1 m
 under SE's seabed); then the counters, Subnautica's status and the round trip of the drilled batches (de.s8a verify
 {octrees}: the octrees Subnautica holds = the served file loaded the loader's way)"""
            self.stage_b_used = True
            self.part("nd", "begin")
            secs = QUICK_ND_SECONDS if self.q else ND_SECONDS
            for name, (x0, z0) in ND_PASSES:
                seabed = self.seabed_at(x0 + ND_LENGTH_M / 2.0, z0, "nd.%s.seabed" % name)
                y = (seabed if not math.isnan(seabed) else -17.0) - 1.0
                a, b = [x0, y, z0], [x0 + ND_LENGTH_M, y, z0]
                cam = (x0 + ND_LENGTH_M / 2.0, y + 6.0, z0 - ND_CAMERA_BACK_M)
                self.teleport(cam[0], cam[1], cam[2], 0.0, label="nd.%s.camera" % name)
                # nd rerun (session 20261023-213453): after a teleport SE gave the camera back to the swimming character, which
 # floated to the surface and drifted 46 m (crater) or snapped back 4.4 km to the crater (planet), so the uncovered runs
 # were the streaming of a moving camera, not the swap: the camera is held at the spot (de.motion bob, amplitude 0, every
 # SE tick re-asserts the spectator) until the pass is over
                self.motion({"kind": "bob", "x": cam[0], "z": cam[2], "centreY": cam[1], "amplitudeM": 0.0, "hz": 0.0,
                             "seconds": secs + ND_HOLD_EXTRA_S}, "nd.%s.hold" % name)
                self.wait(self.scale(4.0, 2.0))
                if name == "planet":
                    self.settle_window("nd.%s.settle" % name, 300)
                self.wait_sn_built("nd.%s.built" % name)
                if name == "crater":
                    # planet v2 R9 (S8a.ND.props): one test prop per family on the seabed over the pass's middle
                    mid = [x0 + ND_LENGTH_M / 2.0, seabed if not math.isnan(seabed) else y + 1.0, z0]
                    self.act("sn", "props", {"at": mid}, label="nd.props.set")
                    self.wait(self.scale(6.0, 3.0))
                    self.sess.event("nd_props", phase="before", at=mid, reply=self.act("sn", "props", {"status": True}, label="nd.props.before"))
                self.act("sn", "cellvis", {"seconds": secs + 8.0}, label="nd.%s.cellvis.on" % name)
                rep = self.act("se", "drillpass", {"from": a, "to": b, "seconds": secs, "everyMs": ND_EVERY_MS, "radius": ND_RADIUS}, label="nd.%s.pass" % name)
                self.wait(secs + self.scale(5.0, 3.0))
                vis = self.act("sn", "cellvis", {"seconds": 0}, label="nd.%s.cellvis.off" % name)
                if name == "crater":
                    self.sess.event("nd_props", phase="after", reply=self.act("sn", "props", {"status": True}, label="nd.props.after"))
                    self.act("sn", "props", {"clear": True}, label="nd.props.clear")
                self.motion({"kind": "stop"}, "nd.%s.hold.stop" % name)
                st = self.act("sn", "status", label="nd.%s.sn.status" % name)
                ids = sorted({(int(math.floor((px + 2048.0) / 160.0)), int(math.floor((y + 3040.0) / 160.0)), int(math.floor((z0 + 2048.0) / 160.0)))
                              for px in (a[0], (a[0] + b[0]) / 2.0, b[0])})
                ver = [self.act("sn", "verify", bx, by, bz, True, label="nd.%s.verify" % name) for bx, by, bz in ids]
                self.sess.event("drill_pass", name=name, fromSn=a, toSn=b, seconds=secs, everyMs=ND_EVERY_MS, radius=ND_RADIUS, seabed=seabed, camSn=list(cam),
                                passReply=rep if isinstance(rep, dict) else None, vis=vis if isinstance(vis, dict) else None,
                                snStatus={k: st.get(k) for k in ("liveJobs", "liveApplied", "liveFallbacks", "octreesPatched", "cellsSwapped", "cellsDiscarded",
                                                                  "cellsDeferred", "liveErrors", "liveMsMax", "liveOn")} if self.ok(st) else None,
                                verify=[v if isinstance(v, dict) else None for v in ver], batches=[list(i) for i in ids])
            self.part("nd", "end")

        def nf_rays(self, site, kind, when, cols, h):
            """SE rays over the ring field's whole reach, Subnautica rays hinted at SE's surface, the field's own top surface (de.s8a
 ring {columns}): the three witnesses of a ring column (rays events and the field reply)."""
            se = self.se_rays(site, kind, when, cols, h, span=(NF_UP_M, NF_DOWN_M))
            hints = {}
            for k, v in se.items():
                y = se_surface(v)
                if not math.isnan(y):
                    hints[k] = y
            self.sn_rays(site, kind, when, cols, hints, h)
            rep = self.act("se", "ring", {"columns": [[float(x), float(z)] for x, z in cols], "fromY": h + NF_UP_M, "toY": h - NF_DOWN_M},
                           label="%s.%d.%s.field" % (when, site, kind))
            self.sess.event("ring_field", label="%s.%d.%s.field" % (when, site, kind), site=site, kind=kind, when=when,
                            results=rep.get("results") if self.ok(rep) else None)
            return se

        def nf_vectors(self):
            """The install set's serve boxes and the inputs they were made with (tests/s8a/vectors/ring3d/ring3d-install-vectors.tsv)."""
            path = os.path.join(self.LCH.REPO, NF_VECTORS)
            out = {"path": path, "serve": [], "head": {}}
            try:
                for ln in open(path, encoding="utf-8").read().splitlines():
                    f = ln.split("\t")
                    if f[0] == "serve" and len(f) >= 13:
                        out["serve"].append({"name": f[1], "box": [float(f[2]), float(f[3]), float(f[4]), int(f[5]), int(f[6]), int(f[7]), int(f[8])], "fnv": f[9]})
                    elif f[0] in ("rules", "heights", "biomes", "library", "placements"):
                        out["head"][f[0]] = f[1:]
            except OSError as e:
                out["error"] = str(e)
            return out

        def p_nf(self):
            """S8a.NF (Stage F2, DEEPENGINEERS_S8A_RING=on, the treadmill off): the ring field served by the planet's generator: paths
 through stamps of four biomes (SE rays, Subnautica rays, the field's own surface), rays across window batch seams on steep
 spots, S8a.F.blend over the served planet, the served bytes against the twin's vectors (de.s8a ring {serve}), the fallback
 (S8a.M15: the field fails on a plain column, SE's heightmap serves it), a drill pass on a stamp (live edits, round trip), the
 ring status (repeat, forbidden, the cost per LOD against SE's own generator, the scope counters)"""
            self.stage_b_used = True
            self.part("nf", "begin")
            self.act("se", "window", {"throttleMs": 0, "on": True}, label="nf.window")
            # a test run (nf 20261023-214851): the field is made on SE's first planet read; wait for it (ready or refused) before judging
            t0 = time.monotonic()
            st = None
            while time.monotonic() - t0 < self.scale(NF_READY_WAIT_S, 2.0) and self.alive():
                st = self.act("se", "ring", label="nf.status.start")
                if self.ok(st) and st.get("state") in ("ready", "refused"):
                    break
                self.wait(1.0)
            self.sess.event("nf_ready", waited=round(time.monotonic() - t0, 1), state=(st or {}).get("state"), refusal=(st or {}).get("refusal"))
            pick = self.act("se", "ring", {"pick": True, "maxRho": NF_MAX_RHO, "perBiome": 2, "cliffs": 4}, label="nf.pick")
            if not self.ok(pick):
                self.sess.event("nf_skip", reason=(pick or {}).get("error") or "no reply", reply=pick)
                self.part("nf", "end")
                return
            visited = []
            # 1. ring paths through stamps of distinct biomes (the preferred ones first)
            spots, seen = [], set()
            order = ("grandReef", "bloodKelp", "mushroomForest", "kelpForest", "sparseReef", "mountains", "underwaterIslands", "grassyPlateaus")
            cand = sorted(pick.get("spots") or [], key=lambda d: order.index(d.get("biome")) if d.get("biome") in order else 99)
            for sp in cand:
                if sp.get("biome") in seen:
                    continue
                seen.add(sp.get("biome"))
                spots.append(sp)
                if len(spots) >= (QUICK_NF_PATHS if self.q else NF_PATHS):
                    break
            for k, sp in enumerate(spots):
                x, z, h = float(sp["x"]), float(sp["z"]), float(sp["h"])
                p0, p1 = (x - NF_PATH_HALF_M, z), (x + NF_PATH_HALF_M, z)
                name = "nf.path.%d" % k
                y = self.window_path(name, p0, p1, NF_SPEED, columns=False)
                visited.append((x, z))
                self.settle_window("%s.settle" % name, NF_SITE + k)
                cols = self.path_columns(p0, p1, QUICK_WINDOW_COLUMNS if self.q else WINDOW_COLUMNS)
                self.nf_rays(NF_SITE + k, "path", name, cols, h)
                self.sess.event("nf_path", name=name, spot=sp, y=y)
            # 2. window batch seams on steep spots
            for k, sm in enumerate((pick.get("seams") or [])[:QUICK_NF_SEAMS if self.q else NF_SEAMS]):
                x, z, h = float(sm["seamX"]), float(sm["seamZ"]), float(sm["h"])
                self.teleport(x + CAMERA_BACK_M * 2.0, h + 40.0, z, 270.0, label="nf.seam.%d.camera" % k)
                visited.append((x, z))
                self.wait(self.scale(2.0, 1.0))
                self.settle_window("nf.seam.%d.settle" % k, NF_SITE + 20 + k)
                cols = [(round(x + dx, 3), round(z + dz, 3)) for dz in NF_SEAM_DZ for dx in NF_SEAM_DX]
                self.nf_rays(NF_SITE + 20 + k, "seam", "nf.seam.%d" % k, cols, h)
                self.sess.event("nf_seam", name="nf.seam.%d" % k, spot=sm)
            # 3. S8a.F.blend over the served planet (a pool thread on SE; read back through the status)
            self.act("se", "ring", {"blend": NF_BLEND_N}, label="nf.blend.start")
            t0 = time.monotonic()
            blend = None
            while time.monotonic() - t0 < self.scale(NF_BLEND_WAIT_S, 2.0) and self.alive():
                st = self.act("se", "ring", label="nf.blend.poll")
                b = (st or {}).get("blend") if self.ok(st) else None
                if isinstance(b, dict) and not b.get("running"):
                    blend = b
                    break
                self.wait(2.0)
            self.sess.event("nf_blend", blend=blend)
            # 4. the served bytes against the twin (S8a.NF.determinism)
            vec = self.nf_vectors()
            if vec["serve"]:
                rep = self.act("se", "ring", {"serve": [v["box"] for v in vec["serve"]]}, label="nf.serve", timeout=30.0)
                self.sess.event("nf_serve", vectors=vec, reply=rep if isinstance(rep, dict) else None)
            else:
                self.sess.event("nf_serve", vectors=vec, reply=None)
            # 5. the fallback: S8a.M15 makes the field fail on the reads of a plain column far from every spot visited
            plain = pick.get("plain") or []
            if plain:
                pl = max(plain, key=lambda d: min([math.hypot(d["x"] - vx, d["z"] - vz) for vx, vz in visited] or [1e9]))
                x, z, h = float(pl["x"]), float(pl["z"]), float(pl["h"])
                self.act("se", "ring", {"failAt": [x, z]}, label="nf.fallback.failat")
                self.act("se", "mutant", "S8a.M15", True, label="nf.fallback.m15.on")
                before = self.act("se", "ring", label="nf.fallback.status.before")
                self.teleport(x + CAMERA_BACK_M, h + 30.0, z, 270.0, label="nf.fallback.camera")
                self.wait(self.scale(4.0, 2.0))
                self.settle_window("nf.fallback.settle", NF_SITE + 40)
                cols = [(round(x + d, 3), round(z, 3)) for d in NF_FALLBACK_D] + [(round(x, 3), round(z + d, 3)) for d in NF_FALLBACK_D if d != 0.0]
                self.nf_rays(NF_SITE + 40, "fallback", "nf.fallback", cols, h)
                after = self.act("se", "ring", label="nf.fallback.status.after")
                self.act("se", "mutant", "S8a.M15", False, label="nf.fallback.m15.off")
                self.act("se", "ring", {"failAt": None}, label="nf.fallback.clear")
                self.sess.event("nf_fallback", spot=pl, before=before if isinstance(before, dict) else None, after=after if isinstance(after, dict) else None)
            else:
                self.sess.event("nf_fallback", spot=None, reason="no plain spot from de.s8a ring pick")
            # 6. a drill pass on a stamp (Stage D's live edit over the ring field)
            if spots:
                sp = spots[0]
                x0, z0 = float(sp["x"]) - ND_LENGTH_M / 2.0, float(sp["z"])
                seabed = self.seabed_at(float(sp["x"]), z0, "nf.drill.seabed")
                y = (seabed if not math.isnan(seabed) else float(sp["h"])) - 1.0
                a, b = [x0, y, z0], [x0 + ND_LENGTH_M, y, z0]
                secs = QUICK_ND_SECONDS if self.q else ND_SECONDS
                self.teleport(float(sp["x"]), y + 6.0, z0 - ND_CAMERA_BACK_M, 0.0, label="nf.drill.camera")
                self.motion({"kind": "bob", "x": float(sp["x"]), "z": z0 - ND_CAMERA_BACK_M, "centreY": y + 6.0, "amplitudeM": 0.0, "hz": 0.0,
                             "seconds": secs + ND_HOLD_EXTRA_S}, "nf.drill.hold")
                self.wait(self.scale(4.0, 2.0))
                self.settle_window("nf.drill.settle", NF_SITE + 50)
                self.wait_sn_built("nf.drill.built")
                st0 = self.act("sn", "status", label="nf.drill.sn.before")
                self.act("sn", "cellvis", {"seconds": secs + 8.0}, label="nf.drill.cellvis.on")
                rep = self.act("se", "drillpass", {"from": a, "to": b, "seconds": secs, "everyMs": ND_EVERY_MS, "radius": ND_RADIUS}, label="nf.drill.pass")
                self.wait(secs + self.scale(5.0, 3.0))
                vis = self.act("sn", "cellvis", {"seconds": 0}, label="nf.drill.cellvis.off")
                self.motion({"kind": "stop"}, "nf.drill.hold.stop")
                st1 = self.act("sn", "status", label="nf.drill.sn.after")
                ids = sorted({(int(math.floor((px + 2048.0) / 160.0)), int(math.floor((y + 3040.0) / 160.0)), int(math.floor((z0 + 2048.0) / 160.0)))
                              for px in (a[0], (a[0] + b[0]) / 2.0, b[0])})
                ver = [self.act("sn", "verify", bx, by, bz, True, label="nf.drill.verify") for bx, by, bz in ids]
                keys = ("liveJobs", "liveApplied", "liveFallbacks", "octreesPatched", "cellsSwapped", "liveErrors")
                self.sess.event("nf_drill", fromSn=a, toSn=b, seconds=secs, everyMs=ND_EVERY_MS, radius=ND_RADIUS, seabed=seabed, spot=sp,
                                passReply=rep if isinstance(rep, dict) else None, vis=vis if isinstance(vis, dict) else None,
                                snBefore={k: st0.get(k) for k in keys} if self.ok(st0) else None, snAfter={k: st1.get(k) for k in keys} if self.ok(st1) else None,
                                verify=[v if isinstance(v, dict) else None for v in ver], batches=[list(i) for i in ids])
            # 7. the ring status: repeat, forbidden, the cost table, the scope counters
            self.act("se", "ring", label="nf.status.end")
            self.part("nf", "end")

        # ---- planet v2 route B: the sample (part ns) -----------------------------------------------------------------
        def ns_where(self, pts, label):
            """SN columns of the current chart (and the field's top) of tangent points of the sample (de.s8a sample {where})."""
            rep = self.act("se", "sample", {"where": [[float(u), float(v)] for u, v in pts]}, label=label, timeout=30.0)
            res = rep.get("results") if self.ok(rep) else None
            return (res if isinstance(res, list) else []), (rep.get("camera") if self.ok(rep) else None)

        def ns_rays(self, site, kind, when, cols, ref):
            """SE rays over the chunk's reach, Subnautica rays hinted at SE's surface, the sample's own witnesses (de.s8a sample
 {columns}: the C# field's top, the chunk's own top at the native column, the heightmap, the chunk's weight)."""
            se = self.se_rays(site, kind, when, cols, ref, span=(NS_UP_M, NS_DOWN_M))
            hints = {}
            for k, v in se.items():
                y = se_surface(v)
                if not math.isnan(y):
                    hints[k] = y
            self._ns_last_sn = self.sn_rays(site, kind, when, cols, hints, ref)
            rep = self.act("se", "sample", {"columns": [[float(x), float(z)] for x, z in cols], "fromY": ref + NS_UP_M, "toY": ref - NS_DOWN_M},
                           label="%s.%d.%s.field" % (when, site, kind), timeout=60.0)
            res = rep.get("results") if self.ok(rep) else None
            # the chunk's own top at the native columns from the store's chunk (the Python twin): the third witness, offline
            twin = None
            ch = self.ns_chunk()
            if ch is not None and isinstance(res, list):
                try:
                    import numpy as np  # noqa: E402
                    nx = np.array([float(r[6]) for r in res]) - ch.x0
                    nz = np.array([float(r[7]) for r in res]) - ch.z0
                    t = ch.top_at(np.clip(nx, 0.0, ch.size_x), np.clip(nz, 0.0, ch.size_z)) + ch.y_floor + float(rep.get("rebaseM") or 0.0)
                    inside = (nx >= 0) & (nx <= ch.size_x) & (nz >= 0) & (nz <= ch.size_z)
                    twin = [float(v) if ok_ else None for v, ok_ in zip(t.tolist(), inside.tolist())]
                except Exception:
                    twin = None
            self.sess.event("ns_field", label="%s.%d.%s.field" % (when, site, kind), site=site, kind=kind, when=when, results=res,
                            rebaseM=rep.get("rebaseM") if self.ok(rep) else None, chunkFnv=rep.get("chunkFnv") if self.ok(rep) else None, twinOwn=twin,
                            twinChunkFnv="%016x" % ch.bin_fnv if ch is not None else None)
            return se

        def ns_chunk(self):
            """The store's chunk read by the Python twin (once per session; None without it)."""
            if not hasattr(self, "_ns_chunk"):
                self._ns_chunk = None
                if not self.NS_OFFLINE:
                    return None
                try:
                    C = chunks_module(self.LCH)
                    cb, ct, _cs = C.chunk_paths()
                    if os.path.exists(cb) and os.path.exists(ct):
                        self._ns_chunk = C.Chunk(cb, ct)
                except Exception:
                    self._ns_chunk = None
            return self._ns_chunk

        def ns_expected(self, native, radius):
            """The chunk's records per family within the radius of a native point (the real map's own entities there)."""
            ch = self.ns_chunk()
            if ch is None or not isinstance(native, (list, tuple)) or len(native) < 3 or not all(_isnum(v) for v in native[:3]):
                return None
            out = {}
            r2 = radius * radius
            for e in ch.entities:
                p_ = e["pos"]
                if (p_[0] - native[0]) ** 2 + (p_[1] - native[1]) ** 2 + (p_[2] - native[2]) ** 2 <= r2:
                    out[e["family"]] = out.get(e["family"], 0) + 1
            return out

        def ns_recheck(self, site, kind, when, cols, ref, se_hits):
            """ns 20261024-124136 (SN-SE 28-55 m at 192 columns, Subnautica hitting the top of the batch row under the surface's): when
 Subnautica's rays miss SE's surface by > NS_RECHECK_M at a column, the loaded octrees of that column's surface batch are
 verified against the served file (de.s8a verify {octrees}), Subnautica's area is waited for once more and its rays are cast
 again (the later rays event wins in the analyzer; the first stays in the session). Returns the number of columns retried."""
            bad = []
            for r in getattr(self, "_ns_last_sn", None) or []:
                k = (round(float(r.get("x", 0.0)), 3), round(float(r.get("z", 0.0)), 3))
                h = se_hits.get(k)
                y_se = se_surface(h) if h else float("nan")
                y_sn = r.get("y")
                if _isnum(y_se) and _isnum(y_sn) and abs(float(y_sn) - y_se) > NS_RECHECK_M:
                    bad.append((k[0], k[1], y_se, float(y_sn)))
            if not bad:
                return 0
            ids = sorted({(int(math.floor((x + 2048.0) / 160.0)), int(math.floor((y + 3040.0) / 160.0)), int(math.floor((z + 2048.0) / 160.0))) for x, z, y, _s in bad})[:4]
            ver = [self.act("sn", "verify", bx, by, bz, True, label="%s.verify" % when) for bx, by, bz in ids]
            self.wait_sn_built("%s.rebuilt" % when, self.scale(30.0, 3.0))
            self.wait(self.scale(NS_RECHECK_WAIT_S, 1.0))
            hints = {}
            for k_, h_ in se_hits.items():
                y_ = se_surface(h_)
                if not math.isnan(y_):
                    hints[k_] = y_
            self.sn_rays(site, kind, when, cols, hints, ref)
            self.sess.event("ns_recheck", when=when, kind=kind, columns=len(bad), worst=max(abs(s - y) for _x, _z, y, s in bad), batches=[list(i) for i in ids],
                            verify=[v if isinstance(v, dict) else None for v in ver])
            return len(bad)

        def ns_camera_at(self, x, z, y, heading, label):
            """The camera at an SN point of the current chart, held there (a zero bob), the window settled and Subnautica built."""
            self.teleport(x, y, z, heading, label=label)
            self.motion({"kind": "bob", "x": x, "z": z, "centreY": y, "amplitudeM": 0.0, "hz": 0.0, "seconds": 240.0}, "%s.hold" % label)
            self.wait(self.scale(3.0, 1.0))
            self.settle_window("%s.settle" % label, NS_SITE + 30)
            self.wait_sn_built("%s.built" % label, self.scale(45.0, 5.0))

        def ns_vectors(self):
            """The install vectors' serve boxes and the inputs they were made with (tests/s8a/vectors/sample/sample-install-vectors.tsv)."""
            path = os.path.join(self.LCH.REPO, NS_VECTORS)
            out = {"path": path, "serve": [], "head": {}}
            try:
                for ln in open(path, encoding="utf-8").read().splitlines():
                    f = ln.split("\t")
                    if f[0] == "serve" and len(f) >= 10:
                        out["serve"].append({"name": f[1], "box": [float(f[2]), float(f[3]), float(f[4]), int(f[5]), int(f[6]), int(f[7]), int(f[8])], "fnv": f[9]})
                    elif f[0] in ("chunk", "sample"):
                        out["head"][f[0]] = f[1:]
            except OSError as e:
                out["error"] = str(e)
            return out

        def p_ns(self):
            """S8a.NS (planet v2 route B's first slice, DEEPENGINEERS_S8A_SAMPLE=on): one Grassy Plateaus chunk of the real map served by
 the planet's generator 20 km from the site. SE's noon; the charts carry the camera there (treadmill off for the teleport,
 on after it); lines through the chunk past its edges and a ray line across each side's seam (SE rays, Subnautica rays, the
 field's own top, the chunk's own top, the heightmap); per spot (the centre and the core's Grassy Plateaus spots) the camera
 held 8 m over the seabed and Subnautica's spawned entities per family against the chunk's records; the served bytes
 against the twin's vectors; the sample's status (the cost table against SE's own generator, the scope counters)"""
            self.stage_b_used = True
            self.part("ns", "begin")
            st0 = self.act("se", "chart", label="ns.chart.begin")
            self.treadmill_session = bool(st0.get("treadmill")) if self.ok(st0) else False
            # SE's own clock at noon (de.s15 clock: S7's dev phase moves only Subnautica's sun; SE's renderer keeps the real one), S7's phase off first
            self.call("se", "de.s7", {"time": "off"}, label="ns.noon.devoff")
            self.call("se", "de.s15", {"clock": 0.5}, label="ns.noon")
            self.act("se", "window", {"throttleMs": 0, "on": True}, label="ns.window")
            t0 = time.monotonic()
            st = None
            waited = 0.0
            while waited < self.scale(NS_READY_WAIT_S, 2.0) and self.alive():
                st = self.act("se", "sample", label="ns.status.start")
                if self.ok(st) and st.get("state") in ("ready", "refused"):
                    break
                self.wait(1.0)
                waited += 1.0
            self.sess.event("ns_ready", waited=round(time.monotonic() - t0, 1), state=(st or {}).get("state"), refusal=(st or {}).get("refusal"))
            if not self.ok(st) or st.get("state") != "ready":
                self.sess.event("ns_skip", reason="the sample is %s%s" % ((st or {}).get("state"), " (%s)" % st.get("refusal") if (st or {}).get("refusal") else ""))
                self.call("se", "de.s7", {"time": "off"}, label="ns.clock")
                self.part("ns", "end")
                return
            half = float(st.get("halfM") or 320.0)
            # the offline rows on the user's machine (the store's chunk against the install, read-only): S8a.NS.cut,.roundtrip,.placement
            try:
                if not self.NS_OFFLINE:
                    raise RuntimeError("offline rows not run (selftest recorder)")
                rows_off = chunks_module(self.LCH).verify(log=lambda *_a: None, roundtrip_n=NS_ROUNDTRIP_N)
                self.sess.event("ns_offline", rows=[[bool(ok_), line] for ok_, line in rows_off])
            except Exception as e:
                self.sess.event("ns_offline", rows=None, error=repr(e))
            pts = sample_points(self.LCH)
            self.sess.event("ns_points", points=pts)
            centre = pts[0]
            # 1. there: chart 0 for the teleport in site metres, then the charts follow the camera
            self.act("se", "treadmill", False, label="ns.treadmill.off")
            self.nl_chart_settled("ns.chart0", False)
            self.teleport(centre["x"], (centre["seabed"] if _isnum(centre.get("seabed")) else -120.0) + NS_ABOVE_M, centre["z"], 0.0, label="ns.arrive")
            self.act("se", "treadmill", True, label="ns.treadmill.on")
            chart = self.nl_chart_settled("ns.chart", True)
            self.sess.event("ns_arrive", chart=chart)
            step = QUICK_NS_LINE_STEP_M if self.q else NS_LINE_STEP_M
            ext = half + NS_LINE_EXT_M
            # 2. the lines (S8a.NS.terrain): along the sample's east axis at v = 0.1 half, and along its north axis at u = -0.2 half
            lines = [("u", 0.1 * half)] + ([] if self.q else [("v", -0.2 * half)])
            for k, (axis, off) in enumerate(lines):
                ts = [-ext + step * i for i in range(int(2 * ext / step) + 1)]
                tp = [(t, off) if axis == "u" else (off, t) for t in ts]
                where, _cam = self.ns_where(tp, "ns.line.%d.where" % k)
                cols = [(round(float(r[2]), 3), round(float(r[3]), 3), r[4]) for r in where if isinstance(r, list) and len(r) >= 5]
                nseg = max(1, int(math.ceil(2 * ext / NS_SEGMENT_M)))
                for g in range(nseg):
                    seg = cols[g * len(cols) // nseg:(g + 1) * len(cols) // nseg]
                    if not seg:
                        continue
                    tops = sorted(c[2] for c in seg if _isnum(c[2]))
                    ref = tops[len(tops) // 2] if tops else -120.0
                    mid = seg[len(seg) // 2]
                    name = "ns.line.%d.%d" % (k, g)
                    self.ns_camera_at(mid[0], mid[1], ref + NS_ABOVE_M, 0.0, name)
                    se_hits = self.ns_rays(NS_SITE + k, "line", name, [(c[0], c[1]) for c in seg], ref)
                    retried = self.ns_recheck(NS_SITE + k, "line", name, [(c[0], c[1]) for c in seg], ref, se_hits)
                    self.sess.event("ns_line", name=name, line=k, axis=axis, offset=off, segment=g, columns=len(seg), ref=ref, retried=retried)
            # 3. the seams (S8a.NS.seam): across each side of the square, from NS_SEAM_IN_M inside to NS_SEAM_OUT_M outside
            sides = ((1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0))
            for k, (su, sv) in enumerate(sides[:2] if self.q else sides):
                along = 0.15 * half * (1 if k % 2 == 0 else -1)
                ds = [half - NS_SEAM_IN_M + NS_SEAM_STEP_M * i for i in range(int((NS_SEAM_IN_M + NS_SEAM_OUT_M) / NS_SEAM_STEP_M) + 1)]
                tp = [(su * d + (along if su == 0 else 0.0), sv * d + (along if sv == 0 else 0.0)) for d in ds]
                where, _cam = self.ns_where(tp, "ns.seam.%d.where" % k)
                cols = [(round(float(r[2]), 3), round(float(r[3]), 3), r[4]) for r in where if isinstance(r, list) and len(r) >= 5]
                if not cols:
                    continue
                tops = sorted(c[2] for c in cols if _isnum(c[2]))
                ref = tops[len(tops) // 2] if tops else -120.0
                name = "ns.seam.%d" % k
                mid = cols[min(len(cols) - 1, int(len(cols) * NS_SEAM_IN_M / (NS_SEAM_IN_M + NS_SEAM_OUT_M)))]
                self.ns_camera_at(mid[0], mid[1], ref + NS_ABOVE_M, 0.0, name)
                se_hits = self.ns_rays(NS_SITE + 10 + k, "seam", name, [(c[0], c[1]) for c in cols], ref)
                retried = self.ns_recheck(NS_SITE + 10 + k, "seam", name, [(c[0], c[1]) for c in cols], ref, se_hits)
                self.sess.event("ns_seam", name=name, side=[su, sv], along=along, distances=ds, columns=len(cols), ref=ref, retried=retried)
            # 4. the entities (S8a.NS.entities): per spot the camera 8 m over the seabed, held; Subnautica's census against the chunk's records
            spots = pts[:2] if self.q else pts
            where, _cam = self.ns_where([(q["u"], q["v"]) for q in spots], "ns.spots.where")
            for k, q in enumerate(spots):
                r = where[k] if k < len(where) and isinstance(where[k], list) and len(where[k]) >= 5 else None
                if r is None:
                    continue
                top = r[4] if _isnum(r[4]) else q.get("seabed")
                name = "ns.spot.%d" % k
                self.ns_camera_at(float(r[2]), float(r[3]), (top if _isnum(top) else -120.0) + NS_ENTITY_UP_M, 90.0 * k, name)
                self.wait(self.scale(NS_ENTITY_WAIT_S, 3.0))
                cen = self.act("sn", "sample", {"radius": NS_ENTITY_R}, label="%s.census" % name)
                lf = self.act("sn", "life", 40.0, label="%s.life" % name)
                _wh, cam = self.ns_where([(0.0, 0.0)], "%s.camera" % name)
                se_st = self.act("se", "sample", label="%s.status" % name)
                self.motion({"kind": "stop"}, "%s.hold.stop" % name)
                native = [cen.get("camNativeX"), cen.get("camNativeY"), cen.get("camNativeZ")] if isinstance(cen, dict) else None
                self.sess.event("ns_entities", name=name, spot=q, radius=NS_ENTITY_R, census=cen if isinstance(cen, dict) else None,
                                expected=self.ns_expected(native, NS_ENTITY_R),
                                life=lf if isinstance(lf, dict) else None, camera=cam,
                                se={kk: (se_st or {}).get(kk) for kk in ("entityWrites", "entitiesInFile", "entityWriteErrors", "entityFile")} if self.ok(se_st) else None)
            # 5. the served bytes against the twin (S8a.NS.determinism)
            vec = self.ns_vectors()
            rep = self.act("se", "sample", {"serve": [v["box"] for v in vec["serve"]]}, label="ns.serve", timeout=60.0) if vec["serve"] else None
            self.sess.event("ns_serve", vectors=vec, reply=rep if isinstance(rep, dict) else None)
            # 6. the status (the cost table, the scope counters), the clock and the treadmill as the session had them
            self.act("se", "sample", label="ns.status.end")
            self.call("se", "de.s7", {"time": "off"}, label="ns.clock")
            self.act("se", "treadmill", bool(getattr(self, "treadmill_session", False)), label="ns.treadmill.restore")
            self.nl_chart_settled("ns.chart.end", False)
            self.part("ns", "end")

        def no_records(self, label, timeout):
            """SE's outcrop records within 100 m of the camera, polled until some exist or the timeout (de.s8a outcrops {probe})."""
            rep = None
            for _k in range(int(max(1, timeout))):
                rep = self.act("se", "outcrops", {"probe": True, "radius": 100.0, "label": label}, label=label)
                if self.ok(rep) and rep.get("records"):
                    break
                self.wait(1.0)
            return rep

        def no_detect(self, label):
            """SE's own detector (MyDepositQuery) over the cells within 100 m: started, then its list read back."""
            self.act("se", "outcrops", {"detect": True, "radius": 100.0, "label": label}, label=label + ".start")
            st = None
            for _k in range(30):
                self.wait(0.5)
                st = self.act("se", "outcrops", label=label + ".status")
                if self.ok(st) and not st.get("detectPending") and st.get("detect") is not None:
                    break
            return (st or {}).get("detect") if self.ok(st) else None

        def no_deposits(self, label, want, timeout):
            """Subnautica's deposits near the camera (de.s8a deposits): until at least `want` are live or the timeout."""
            rep = None
            for _k in range(int(max(1, timeout))):
                rep = self.act("sn", "deposits", label=label)
                if self.ok(rep) and (rep.get("liveNear") or 0) >= want:
                    break
                self.wait(1.0)
            return rep

        def p_no(self):
            """S8a.NO (planet v2 R8, ores you can see): per ore site the camera NO_ABOVE_M above the seabed, held; SE's outcrop records
 and SE's own detector within 100 m, Subnautica's deposits; on the first site with records a drill on the biggest undrilled
 outcrop (S8a.NO.shrink) and NO_IMMUNE_HITS of Subnautica's drill hits on the nearest deposit (S8a.NO.immune)"""
            self.part("no", "begin")
            sites = [st for st in self.ore_sites() if (st.get("seabedY") or -1000.0) > -400.0]
            sites = sites[:QUICK_NO_SITES if self.q else NO_SITES]
            drilled = False
            for k, st in enumerate(sites):
                seabed = self.seabed_at(st["x"], st["z"], "no.%d.seabed" % k)
                y = (seabed if not math.isnan(seabed) else st["seabedY"]) + NO_ABOVE_M
                self.teleport(st["x"], y, st["z"], 0.0, label="no.%d.camera" % k)
                self.motion({"kind": "bob", "x": st["x"], "z": st["z"], "centreY": y, "amplitudeM": 0.0, "hz": 0.0, "seconds": 240.0}, "no.%d.hold" % k)
                self.wait(self.scale(3.0, 1.0))
                self.settle_window("no.%d.settle" % k, 600 + k)
                self.wait_sn_built("no.%d.built" % k, self.scale(45.0, 10.0))
                recs = self.no_records("no.%d.records" % k, self.scale(NO_RECORDS_WAIT_S, 5.0))
                records = (recs or {}).get("records") or [] if self.ok(recs) else []
                deps = self.no_deposits("no.%d.deposits" % k, len(records), self.scale(NO_DEPOSITS_WAIT_S, 5.0))
                det = self.no_detect("no.%d.detect" % k)
                # a test run (S8a.NO.agree): SE's own material 1 m inside each anchor along its normal (an edge exposure's deposit sits on its ore)
                anchor_ore = {}
                pts = [(r["x"] - (r.get("nx") or 0.0), r["y"] - (r.get("ny") if r.get("ny") is not None else 1.0), r["z"] - (r.get("nz") or 0.0)) for r in records]
                if pts:
                    mrep = self.act("se", "materialat", pts, label="no.%d.anchors" % k)
                    if self.ok(mrep) and isinstance(mrep.get("materials"), list):
                        anchor_ore = {r.get("key"): m for r, m in zip(records, mrep["materials"])}
                ev = {"i": k, "site": st, "seabedY": seabed, "cameraY": y, "records": records, "detect": det, "anchorOre": anchor_ore,
                      "deposits": {kk: deps.get(kk) for kk in ("near", "liveNear", "depositsLive", "records", "camX", "camY", "camZ")} if self.ok(deps) else None}
                if not drilled and records:
                    big = max((r for r in records if not r.get("drilled")), key=lambda r: r.get("volumeM3") or 0.0, default=None)
                    if big is not None:
                        drilled = True
                        centre = (big["x"], big["y"] - 1.0, big["z"])
                        drep = self.act("se", "drill", centre, NO_DRILL_R, "sphere", label="no.drill", timeout=10.0)
                        self.wait(self.scale(3.0, 2.0))
                        self.wait_window_quiet("no.drill.window", self.scale(WINDOW_APPLY_WAIT_S, 3.0))
                        self.wait(self.scale(4.0, 2.0))
                        after = self.no_records("no.drill.records", 2)
                        self.wait(self.scale(3.0, 1.0))
                        dafter = self.no_deposits("no.drill.deposits", 0, 1)
                        imm = self.act("sn", "deposits", {"immune": NO_IMMUNE_HITS}, label="no.immune")
                        ev["drill"] = {"key": big.get("key"), "centre": list(centre), "radius": NO_DRILL_R, "ok": self.ok(drep), "before": big,
                                       "after": (after or {}).get("records") if self.ok(after) else None,
                                       "depositsAfter": dafter.get("near") if self.ok(dafter) else None}
                        ev["immune"] = {kk[len("immune."):]: vv for kk, vv in (imm or {}).items() if kk.startswith("immune.")} if self.ok(imm) else None
                        ev["oreLook"] = self.ore_look(centre, k)
                self.motion({"kind": "stop"}, "no.%d.hold.stop" % k)
                self.sess.event("no_site", **ev)
            if not sites:
                self.sess.event("no_skip", reason="no ore site with a seabed above -400 m near the n10 path")
            # ores as voxels (S8a.NO.voxel): the converters' census of the ore surface voxels written this session
            ov = self.act("se", "orevoxels", label="no.orevoxels")
            self.sess.event("ore_voxels", reply=ov if isinstance(ov, dict) else None)
            self.no_density()
            self.part("no", "end")

        def capture_png(self, name, label):
            """Subnautica's picture now (de.capture) copied into the session folder as look/<name>.png: (relative path or None, mean luma)."""
            rel = "s8a/%s/%s.png" % (self.stamp, name)
            self.call("sn", "de.capture", {"path": rel, "checkId": "S8a.NO", "label": label, "kind": 1}, timeout=10.0, label=label)
            src = os.path.join(self.LCH.CAPTURES, *rel.split("/"))
            got = False
            for _k in range(40):
                if not self.alive():
                    break
                if os.path.isfile(src) and os.path.getsize(src) > 0:
                    got = True
                    break
                self.wait(0.25)
            dst_rel = os.path.join("look", "%s.png" % name)
            if got and self.out and os.path.isdir(str(self.out)):
                os.makedirs(os.path.join(self.out, "look"), exist_ok=True)
                shutil.copyfile(src, os.path.join(self.out, dst_rel))
            return (dst_rel if got else None), (self.luma_of(src) if got else None)

        def ore_look(self, centre, k):
            """Ores as voxels at the drilled vein (S8a.NO.look, NO.palette): the deposit props off (they would sit in the hole), the camera
 ORE_LOOK_BACK_M south of the hole and ORE_LOOK_UP_M over it, looking at it; captures with our ore materials, as the template rock
 (the same view without an ore look) and as each paint-test candidate; our look back; the props on again."""
            self.act("sn", "deposits", {"on": False}, label="no.orelook.props.off")
            # a test run (no 20261024-143113: every ore-look capture black, SE's real time was night): SE's own clock at noon, S7's phase off
            self.call("se", "de.s7", {"time": "off"}, label="no.orelook.noon.devoff")
            self.call("se", "de.s15", {"clock": 0.5}, label="no.orelook.noon")
            x, y, z = centre[0], centre[1] + ORE_LOOK_UP_M, centre[2] - ORE_LOOK_BACK_M
            self.teleport(x, y, z, 0.0, label="no.orelook.camera")
            self.call("se", "de.look", {"dir": "pitch%+d" % ORE_LOOK_PITCH}, label="no.orelook.look")
            self.motion({"kind": "bob", "x": x, "z": z, "centreY": y, "amplitudeM": 0.0, "hz": 0.0, "seconds": 120.0}, "no.orelook.hold")
            self.wait(self.scale(3.0, 1.0))
            self.settle_window("no.orelook.settle", 650 + k)
            self.wait_sn_built("no.orelook.built", self.scale(30.0, 5.0))
            pal = self.act("sn", "orelook", label="no.orelook.palette")
            caps = []
            for look in ["ours", str(ORE_LOOK_ROCK)] + [str(t) for t in ORE_LOOK_CANDIDATES]:
                rep = self.act("sn", "orelook", {"as": look}, label="no.orelook.as.%s" % look)
                self.wait(self.scale(1.5, 0.5))
                png, luma = self.capture_png("ore-%s" % look, "no.orelook.capture.%s" % look)
                caps.append({"as": look, "png": png, "luma": luma, "result": (rep or {}).get("result") if self.ok(rep) else None})
            self.act("sn", "orelook", {"as": "ours"}, label="no.orelook.as.ours.end")
            self.motion({"kind": "stop"}, "no.orelook.hold.stop")
            self.act("sn", "deposits", {"on": True}, label="no.orelook.props.on")
            out = {"camera": [x, y, z], "pitch": ORE_LOOK_PITCH, "palette": pal if isinstance(pal, dict) else None, "captures": caps}
            self.sess.event("ore_look", **out)
            return out

        def no_density_points(self):
            """S8a.N10.oredensity's points: the crater's 4 x 4 grid (x, z in -1500, -500, 500, 1500), then the planet's headings
 (11.25 deg apart, radii 15, 22, 30 km in turn); the quick run takes the first of each."""
            grid = (-1500.0, -500.0, 500.0, 1500.0)
            crater = [("crater", x, z) for z in grid for x in grid]
            planet = []
            for k in range(NO_DENSITY_PLANET):
                a = math.radians(5.625 + 360.0 * k / NO_DENSITY_PLANET)
                r = NO_DENSITY_RADII_M[k % len(NO_DENSITY_RADII_M)]
                planet.append(("planet", round(r * math.cos(a), 1), round(r * math.sin(a), 1)))
            nc = QUICK_NO_DENSITY_CRATER if self.q else NO_DENSITY_CRATER
            npl = QUICK_NO_DENSITY_PLANET if self.q else NO_DENSITY_PLANET
            # the quick run: spread over the grid (7 is prime to 16: 0, 7, 14, 5,... visits every row and column early) and the headings
            return [crater[(7 * i) % len(crater)] for i in range(nc)] + [planet[int(i * len(planet) / npl)] for i in range(npl)]

        def no_density(self):
            """S8a.N10.oredensity (planet v2 R8): per point SE's own detector (MyDepositQuery, de.s8a outcrops {detect, radius}) over
 NO_DENSITY_R m; the crater rule's state (cube rule, cap, pockets) from the outcrops status."""
            for k, (zone, x, z) in enumerate(self.no_density_points()):
                seabed = self.seabed_at(x, z, "no.density.%d.seabed" % k)
                if math.isnan(seabed):
                    self.sess.event("no_density", i=k, zone=zone, x=x, z=z, seabedY=None, detect=None, note="no seabed")
                    continue
                self.teleport(x, seabed + NO_DENSITY_UP_M, z, 0.0, label="no.density.%d.camera" % k)
                self.wait(self.scale(2.0, 0.5))
                self.act("se", "outcrops", {"detect": True, "radius": NO_DENSITY_R, "label": "no.density.%d" % k}, label="no.density.%d.start" % k)
                st = None
                for _j in range(60):
                    self.wait(0.5)
                    st = self.act("se", "outcrops", label="no.density.%d.status" % k)
                    if self.ok(st) and not st.get("detectPending") and st.get("detect") is not None:
                        break
                rule = {kk: st.get(kk) for kk in ("craterCubeRule", "craterDepositsPerKm2", "craterPockets", "craterCapped", "planetRules")} if self.ok(st) else None
                self.sess.event("no_density", i=k, zone=zone, x=x, z=z, seabedY=seabed, radius=NO_DENSITY_R,
                                detect=(st or {}).get("detect") if self.ok(st) else None, rule=rule)

        def nv_spots(self):
            """S8a.NV's spots: the crater's large deposits SE knows (de.s8a ores: one pocket under each, the overlay's surest paint), the
 nearest to each of NV_GRID's points; the grid point itself where none is reported."""
            rep = self.act("se", "ores", {"count": 1}, label="nv.deposits", timeout=30.0)
            deps = [r["sn"] for r in ((rep or {}).get("rows") or []) if self.ok(rep) and r.get("kind") == "deposit" and isinstance(r.get("sn"), list)]
            spots = []
            for gx, gz in NV_GRID:
                best = min(deps, key=lambda p: (p[0] - gx) ** 2 + (p[2] - gz) ** 2, default=None)
                if best is not None and math.hypot(best[0] - gx, best[2] - gz) <= NV_DEPOSIT_REACH_M:
                    spots.append((round(best[0], 1), round(best[2], 1), "deposit"))
                else:
                    spots.append((gx, gz, "grid"))
            return spots[:QUICK_NV_SPOTS if self.q else NV_SPOTS]

        def nv_quiet(self, label, timeout):
            """The ore overlay near the camera done: its queue empty on two polls (the planner queues every second), then the drill
 converter's jobs written; the last status."""
            st, quiet = None, 0
            for _k in range(int(max(2, timeout))):
                st = self.act("se", "oreoverlay", label=label)
                quiet = quiet + 1 if self.ok(st) and (st.get("queued") or 0) == 0 and (st.get("passes") or 0) > 0 else 0
                if quiet >= 2 or not self.alive():
                    break
                self.wait(1.0)
            return st

        def nv_detect(self, label):
            """SE's own detector (MyDepositQuery) over NV_DETECT_R m: started, then its list read back."""
            self.act("se", "outcrops", {"detect": True, "radius": NV_DETECT_R, "label": label}, label=label + ".start")
            st = None
            for _k in range(40):
                self.wait(0.5)
                st = self.act("se", "outcrops", label=label + ".status")
                if self.ok(st) and not st.get("detectPending") and st.get("detect") is not None:
                    break
            return (st or {}).get("detect") if self.ok(st) else None

        def p_nv(self):
            """S8a.
 NV_UP_M above the seabed until the overlay near it is done; SE's ore voxels against the real map there (de.s8a oreoverlay cover:
 S8a.NV.cover); SE's own detector over NV_DETECT_R m and, per hit inside the map, Subnautica's served surface voxels within
 NV_RADIUS_M (de.s8a oresurface: S8a.NV.detector) with a capture over the nearest painted one (first NV_CAPTURES); on the
 first spot with paint the vein under it drilled out and the paint read again (S8a.NV.mined). Noon by SE's own clock."""
            self.part("nv", "begin")
            self.call("se", "de.s7", {"time": "off"}, label="nv.noon.devoff")
            self.call("se", "de.s15", {"clock": 0.5}, label="nv.noon")
            mined = False
            ncap = 0
            for k, (x, z, kind) in enumerate(self.nv_spots()):
                seabed = self.seabed_at(x, z, "nv.%d.seabed" % k)
                if math.isnan(seabed):
                    self.sess.event("nv_spot", i=k, x=x, z=z, kind=kind, seabedY=None, note="no seabed")
                    continue
                y = seabed + NV_UP_M
                self.teleport(x, y, z, 0.0, label="nv.%d.camera" % k)
                self.motion({"kind": "bob", "x": x, "z": z, "centreY": y, "amplitudeM": 0.0, "hz": 0.0, "seconds": 300.0}, "nv.%d.hold" % k)
                self.wait(self.scale(3.0, 1.0))
                st = self.nv_quiet("nv.%d.overlay" % k, self.scale(NV_WAIT_S, 6.0))
                self.wait(self.scale(NV_RELOAD_S, 1.0))
                self.wait_sn_built("nv.%d.built" % k, self.scale(30.0, 5.0))
                cover = self.act("se", "oreoverlay", {"cover": True, "radius": NV_COVER_R, "sn": [x, seabed, z]}, label="nv.%d.cover" % k, timeout=60.0)
                det = self.nv_detect("nv.%d.detect" % k) or []
                hits = [dict(h, inMap=abs(h.get("x", 9e9)) <= 2048.0 and abs(h.get("z", 9e9)) <= 2048.0) for h in det if isinstance(h, dict)]
                inmap = [h for h in hits if h["inMap"]]
                surf = None
                if inmap:
                    pts = [[h["x"], h["y"], h["z"], h.get("ore")] for h in inmap]
                    surf = self.act("sn", "oresurface", {"points": pts, "radius": NV_RADIUS_M}, label="nv.%d.surface" % k, timeout=30.0)
                    srows = (surf or {}).get("rows") or [] if self.ok(surf) else []
                    for h, r in zip(inmap, srows):
                        h.update({kk: r.get(kk) for kk in ("surface", "nearestSurfaceM", "expected", "typed", "nearestTypedM", "typeAt", "typedAt", "anyOre")})
                caps = []
                for j, h in enumerate(inmap):
                    if ncap >= (QUICK_NV_CAPTURES if self.q else NV_CAPTURES) or not h.get("typedAt"):
                        continue
                    tx, ty, tz = h["typedAt"]
                    self.teleport(tx, ty + NV_LOOK_UP_M, tz - NV_LOOK_BACK_M, 0.0, label="nv.%d.%d.look" % (k, j))
                    self.call("se", "de.look", {"dir": "pitch%+d" % NV_LOOK_PITCH}, label="nv.%d.%d.pitch" % (k, j))
                    self.motion({"kind": "bob", "x": tx, "z": tz - NV_LOOK_BACK_M, "centreY": ty + NV_LOOK_UP_M, "amplitudeM": 0.0, "hz": 0.0, "seconds": 60.0}, "nv.%d.%d.hold" % (k, j))
                    self.wait(self.scale(2.5, 1.0))
                    png, luma = self.capture_png("nv-%d-%d" % (k, j), "nv.%d.%d.capture" % (k, j))
                    caps.append({"hit": j, "ore": h.get("ore"), "at": [tx, ty, tz], "png": png, "luma": luma})
                    ncap += 1
                ev = {"i": k, "x": x, "z": z, "kind": kind, "seabedY": seabed, "cameraY": y, "overlay": st if isinstance(st, dict) else None,
                      "cover": {kk: cover.get(kk) for kk in ("octrees", "sampled", "ore", "above", "face", "cube", "examples", "ms", "minCoverM", "coverOn", "bySnType")} if self.ok(cover) else None,
                      "hits": hits, "surfaceReply": {kk: surf.get(kk) for kk in ("points", "withSurface", "withTyped", "sources")} if self.ok(surf) else None, "captures": caps}
                judged = [h for h in inmap if h.get("surface")]
                if not mined and judged:
                    mined = True
                    self.nv_mine(k, judged)
                self.motion({"kind": "stop"}, "nv.%d.hold.stop" % k)
                self.sess.event("nv_spot", **ev)
            st = self.act("se", "oreoverlay", label="nv.status.end")
            self.sess.event("nv_status", reply=st if isinstance(st, dict) else None)
            self.part("nv", "end")

        def nv_mine(self, k, judged):
            """S8a.NV.mined: the vein under the first painted hit (else the first judged one) and its paint (Subnautica's voxels of its ore
 within NV_PAINT_R m of the vein's centroid); the vein drilled out (a sphere over the vein voxels SE's rule and the base still
 hold within NV_VEIN_R voxels), then the painted voxels read again with the overlay's own expectation (de.s8a oreoverlay
 expect: 0 = no vein reaches the voxel any more). No paint: nothing drilled (the row fails)."""
            h = next((x for x in judged if x.get("typedAt")), judged[0])
            at = h.get("typedAt") or [h["x"], h["y"], h["z"]]
            vein = self.act("se", "oreoverlay", {"vein": True, "sn": list(at), "radius": NV_VEIN_R}, label="nv.vein.before", timeout=30.0)
            ev = {"spot": k, "hit": {kk: h.get(kk) for kk in ("ore", "x", "y", "z", "typed", "typedAt")}, "painted": [],
                  "veinBefore": {kk: vein.get(kk) for kk in ("count", "centroid", "extentM", "rows")} if self.ok(vein) else None}
            if self.ok(vein) and vein.get("centroid"):
                c = vein["centroid"]
                # the painted voxels around the vein (Subnautica's served voxels of the hit's ore within NV_PAINT_R m of its centroid)
                pr = self.act("sn", "oresurface", {"points": [[c[0], c[1], c[2], h.get("ore")]], "radius": NV_PAINT_R, "list": NV_LIST}, label="nv.painted", timeout=30.0)
                prow = ((pr or {}).get("rows") or [{}])[0] if self.ok(pr) else {}
                painted = [p[:3] for p in (prow.get("typedList") or [])]
                ev["painted"] = painted
                if painted:
                    r = min(NV_MINE_MAX_R, float(vein.get("extentM") or 2.0) + NV_MINE_PAD_M)
                    drep = self.act("se", "drill", c, r, "sphere", label="nv.mine", timeout=10.0)
                    ev.update({"centre": c, "radius": r, "drillOk": self.ok(drep)})
                    self.wait(self.scale(NV_MINE_WAIT_S, 3.0))
                    self.nv_quiet("nv.mine.overlay", self.scale(NV_WAIT_S, 6.0))
                    self.wait(self.scale(NV_RELOAD_S, 1.0))
                    after = self.act("se", "oreoverlay", {"vein": True, "sn": list(c), "radius": NV_VEIN_R}, label="nv.vein.after", timeout=30.0)
                    exp = self.act("se", "oreoverlay", {"expect": True, "points": painted}, label="nv.expect", timeout=30.0)
                    sv = self.act("sn", "oresurface", {"voxels": painted}, label="nv.after", timeout=30.0)
                    ev.update({"veinAfter": after.get("count") if self.ok(after) else None, "expect": exp.get("types") if self.ok(exp) else None,
                               "after": sv.get("voxels") if self.ok(sv) else None})
            self.sess.event("nv_mine", **ev)

        def look_points(self):
            """The far points of nl from the planet SE runs (de.s8a probe planetDataDir, as n10's ore sites): 2 per Subnautica biome of its
 face-1 biome map beyond the ring (sculpt_de4546b.look_points_from_files); [] when the files are not found."""
            try:
                S = sculpt_module(self.LCH)
                rep = self.act("se", "probe", [(0.0, 0.0)], "nl.where", label="nl.where", timeout=30.0)
                pdd = rep.get("planetDataDir") if self.ok(rep) else None
                files_dir = os.path.normpath(os.path.join(pdd, "..", "..", "..")) if isinstance(pdd, str) and os.path.isdir(pdd) else None
                pts = S.look_points_from_files(files_dir or S.REPO_MOD_DIR, per_biome=NL_PER_BIOME, max_depth=NL_LIT_DEPTH_M, exclude=NL_EXCLUDE_BIOMES) or []
                self.sess.event("look_points", source=files_dir or "repository", points=pts)
                return pts
            except Exception as e:
                self.sess.event("look_points", error=repr(e))
                return []

        def nl_chart_settled(self, label, far):
            """a test run: until SE's chart is unchanged for NL_CHART_SETTLE_S, applied by Subnautica, and (far) the zone is not FAR; the
 chart status at the end."""
            # the time is counted in this loop's own waits (the selftest's recorder has no wall clock)
            waited, last, since, st, settled = 0.0, None, 0.0, None, False
            while waited < self.scale(NL_CHART_WAIT_S, 5.0) and self.alive():
                st = self.act("se", "chart", label=label)
                if self.ok(st):
                    key = (st.get("chartSeq"), st.get("chartIndex"))
                    if key != last:
                        last, since = key, waited
                    applied = st.get("snChartSeqApplied") == st.get("chartSeq") and not st.get("inTransition")
                    zone_ok = not far or st.get("zone") not in ("FAR", "off-site")
                    if applied and zone_ok and waited - since >= self.scale(NL_CHART_SETTLE_S, 1.0):
                        settled = True
                        break
                self.wait(1.0)
                waited += 1.0
            self.sess.event("nl_chart", label=label, waited=waited, settled=settled, chart=st if self.ok(st) else None)
            return st if self.ok(st) else None

        @staticmethod
        def luma_of(path):
            """The mean luma (0-255) of a capture, None when it cannot be read."""
            try:
                from PIL import Image  # noqa: E402
                im = Image.open(path).convert("L").resize((160, 90))
                px = list(im.getdata())
                return sum(px) / float(len(px)) if px else None
            except Exception:
                return None

        def look_capture(self, kind, biome, x, z, seabed, h, heading, rho, n, same_spot=None):
            """One view: the camera h above SE's seabed (at least 2 m under the sea), the heading, pitch NL_PITCH_DEG, held; the window
 settled and Subnautica built; Subnautica's capture (de.capture) copied into the session folder; its status (entities)."""
            y = min(seabed + h, -2.0) if not math.isnan(seabed) else -2.0 - h
            far = kind == "far" and rho > 4000.0
            if far and same_spot is None:
                # a test run: chart 0 for the teleport (site metres), then the charts follow the camera out to the point
                self.act("se", "treadmill", False, label="nl.treadmill.off.%d" % n)
                self.nl_chart_settled("nl.chart0.%d" % n, False)
                self.teleport(x, y, z, heading, label="nl.%s.%d" % (kind, n))
                self.act("se", "treadmill", True, label="nl.treadmill.on.%d" % n)
            elif far:
                # the same point at the next height: the chart stays, the camera moves up in its metres
                self.teleport(same_spot[0], y, same_spot[1], heading, label="nl.%s.%d" % (kind, n))
            else:
                self.teleport(x, y, z, heading, label="nl.%s.%d" % (kind, n))
            self.call("se", "de.look", {"dir": "pitch%+d" % int(NL_PITCH_DEG)}, label="nl.look.%d" % n)      # S1Math.LookDirections: relative to the teleport's heading
            self.wait(self.scale(3.0, 1.0))
            chart = self.nl_chart_settled("nl.chart.%d" % n, far) if far else None
            # the hold in the chart's own metres (the camera's position after the steps)
            cx, cz = x, z
            if chart and isinstance(chart.get("camSn"), list) and len(chart["camSn"]) >= 3:
                cx, cz = float(chart["camSn"][0]), float(chart["camSn"][2])
            self.motion({"kind": "bob", "x": cx, "z": cz, "centreY": y, "amplitudeM": 0.0, "hz": 0.0, "seconds": 240.0}, "nl.hold.%d" % n)
            if rho > 3000:
                self.settle_window("nl.settle.%d" % n, 500 + n)
            self.wait_sn_built("nl.built.%d" % n, self.scale(45.0, 10.0))
            self.wait(self.scale(NL_SETTLE_S, 2.0))
            self.wait_sn_built("nl.built2.%d" % n, self.scale(30.0, 5.0))
            dst_rel = os.path.join("look", "look-%02d.png" % n)
            got, luma, rep, tries = False, None, None, 0
            for tries in range(1, NL_RETRIES + 1):
                rel = "s8a/%s/look-%02d-%d.png" % (self.stamp, n, tries)
                rep = self.call("sn", "de.capture", {"path": rel, "checkId": "S8a.NL", "label": "nl.%d.%d" % (n, tries), "kind": 1}, timeout=10.0, label="nl.capture.%d" % n)
                src = os.path.join(self.LCH.CAPTURES, *rel.split("/"))
                got = False
                for _k in range(40):                    # 10 s (self.wait: the recorder's clock in the selftest)
                    if not self.alive():
                        break
                    if os.path.isfile(src) and os.path.getsize(src) > 0:
                        got = True
                        break
                    self.wait(0.25)
                if got and self.out and os.path.isdir(str(self.out)):
                    os.makedirs(os.path.join(self.out, "look"), exist_ok=True)
                    shutil.copyfile(src, os.path.join(self.out, dst_rel))
                luma = self.luma_of(src) if got else None
                if got and (luma is None or luma >= NL_BLACK_LUMA):
                    break
                # a black frame (or none): Subnautica's picture not presented yet; wait and take it again
                self.wait(self.scale(6.0, 1.0))
                self.wait_sn_built("nl.retry.%d.%d" % (n, tries), self.scale(20.0, 3.0))
            st = self.act("sn", "status", label="nl.status.%d" % n)
            ents = (st.get("spawnsLive") or 0) + (st.get("depositsLive") or 0) if self.ok(st) else None
            # a test run (S8a.NL.look): plants and creatures round the camera (the registry of started entities, this mod's own apart)
            lf = self.act("sn", "life", 40.0, label="nl.life.%d" % n)
            life = {kk: lf.get(kk) for kk in ("life", "flora", "creatures", "other", "ours")} if self.ok(lf) else None
            pic = self.act("se", "picture", label="nl.picture.%d" % n)
            win = self.act("se", "window", label="nl.window.%d" % n)
            self.motion({"kind": "stop"}, "nl.hold.stop.%d" % n)
            self.sess.event("look_capture", kind=kind, biome=biome, x=x, z=z, seabedY=seabed, height=h, cameraY=y, heading=heading, pitchDeg=NL_PITCH_DEG,
                            rho=rho, png=dst_rel if got else None, written=got, entities=ents, reply=rep, luma=luma, black=bool(got and luma is not None and luma < NL_BLACK_LUMA),
                            tries=tries, chart=chart, picture=pic if isinstance(pic, dict) else None, sameSpot=same_spot is not None, life=life,
                            window={kk: (win or {}).get(kk) for kk in ("pending", "nearPending", "workerBusy", "chartSeq", "generated")} if self.ok(win) else None)
            return (cx, cz) if far else None

        def p_nl(self):
            """S8a. Subnautica's picture at the spawn inside the map (three headings)
 and at two far points per planet biome, each at 8 and 25 m above the seabed, pitch -15 deg; SE's shots from 5 and 30 km up
 over the site (the white fragments, the tile grid lines); the contact sheet look-sheet.png (spawn left, far right). Planet v2
 route B: the sample is on for nl (env_plan), so the far points are the sample's two core spots and one design-3 Grassy Plateaus
 point beside them (S8a.NL.look judges the sample's; the design-3 one is the sheet's before)"""
            self.part("nl", "begin")
            st0 = self.act("se", "chart", label="nl.chart.begin")
            self.treadmill_session = bool(st0.get("treadmill")) if self.ok(st0) else False
            # both pictures at noon: SE's own clock (de.s15 clock; S7's dev phase moves only Subnautica's sun, so the SE frames from 5 and
 # 30 km were at SE's real time: the sea's terminator seam), S7's phase off first; Subnautica follows SE's clock (the time sync)
            self.call("se", "de.s7", {"time": "off"}, label="nl.noon.devoff")
            self.call("se", "de.s15", {"clock": 0.5}, label="nl.noon")
            n = 0
            spawn = None
            for sx, sz in NL_SPAWN_CANDIDATES:
                sb = self.seabed_at(sx, sz, "nl.spawn.seabed")
                if not math.isnan(sb) and sb <= -30.0:
                    spawn = (sx, sz, sb)
                    break
            if spawn is None:
                sx, sz = NL_SPAWN_CANDIDATES[0]
                spawn = (sx, sz, self.seabed_at(sx, sz, "nl.spawn.seabed"))
            for h in NL_HEIGHTS:
                for hd in NL_SPAWN_HEADINGS:
                    n += 1
                    self.look_capture("spawn", "spawn", spawn[0], spawn[1], spawn[2], h, hd, math.hypot(spawn[0], spawn[1]), n)
            far_pts = self.look_points()
            # planet v2 route B (the sample on for nl: env_plan): nl looks at the chunk's core spots, one design-3 Grassy Plateaus point beside them
            sp = sample_points(self.LCH)[1:1 + NL_SAMPLE_POINTS]
            #... and first at the very spots of the real map they were cut from (the reference S8a.NL.look judges them against: the same
 # biome, heights and noon; the heading turned back by the placement's quarter turns so the view is the same scene)
            turns = 0
            try:
                C = chunks_module(self.LCH)
                turns = int(C.read_placement(C.chunk_paths()[2]).get("turns") or 0)
            except Exception:
                turns = 0
            for q in sp:
                if not (_isnum(q.get("nativeX")) and _isnum(q.get("nativeZ"))):
                    continue
                # ns 20261024-124136: SE's rays inside the crater box search only -50..+10 m (the tiles' span), so the plateau (-90..-150 m)
 # read NaN and the camera sat at the surface; the real map's seabed comes from the chunk's own tops (the install's octrees)
                sb = q["nativeSeabed"] if _isnum(q.get("nativeSeabed")) else self.seabed_at(q["nativeX"], q["nativeZ"], "nl.ref.seabed")
                for h in NL_HEIGHTS:
                    n += 1
                    self.look_capture("ref", "real:grassyPlateaus", q["nativeX"], q["nativeZ"], sb, h, (0.0 - 90.0 * turns) % 360.0,
                                      math.hypot(q["nativeX"], q["nativeZ"]), n)
            old = [q for q in far_pts if q.get("biome") == "grassyPlateaus"][:1]
            far_pts = [{"biome": "sample:grassyPlateaus", "x": q["x"], "z": q["z"], "rho": math.hypot(q["x"], q["z"])} for q in sp] + \
                      [dict(q, biome="design3:grassyPlateaus") for q in old]
            self.sess.event("look_points", source="sample", points=far_pts)
            for pt in far_pts:
                # the seabed read on chart 0 (site metres): the treadmill off and back on the site first
                self.act("se", "treadmill", False, label="nl.treadmill.seabed")
                self.nl_chart_settled("nl.chart.seabed", False)
                sb = self.seabed_at(pt["x"], pt["z"], "nl.far.seabed")
                spot = None
                for h in NL_HEIGHTS:
                    n += 1
                    spot = self.look_capture("far", pt["biome"], pt["x"], pt["z"], sb, h, 0.0, pt["rho"], n, same_spot=spot)
            # SE's own picture from above (the S15 plugin's orbit and shot, when it is loaded)
            for km in NL_ORBIT_KM:
                rep = self.call("se", "de.s15", {"orbit": km}, label="nl.orbit.%d" % km)
                path = os.path.join(str(self.out), "look", "orbit-%dkm.png" % km) if self.out else None
                got = False
                if self.ok(rep) and path:
                    self.wait(self.scale(8.0, 3.0))
                    self.call("se", "de.s15", {"shot": path}, label="nl.shot.%d" % km)
                    for _k in range(60):
                        got = os.path.isfile(path) and os.path.getsize(path) > 0
                        if got or not self.alive():
                            break
                        self.wait(0.25)
                self.sess.event("look_orbit", km=km, png=os.path.join("look", "orbit-%dkm.png" % km) if got else None, written=got, reply=rep)
            self.call("se", "de.s7", {"time": "off"}, label="nl.clock")
            # back on chart 0, the treadmill as the session had it (off unless --treadmill / n8)
            self.act("se", "treadmill", bool(getattr(self, "treadmill_session", False)), label="nl.treadmill.restore")
            self.nl_chart_settled("nl.chart.end", False)
            try:
                look_sheet(str(self.out), self.sess)
            except Exception as e:
                self.sess.event("look_sheet", error=repr(e))
            if hasattr(self, "park"):
                self.park()
            self.part("nl", "end")

        def p_n11(self):
            """S8a.N11.beacon (Stage E): de.s8a beacon re-arms the Aurora distress beacon (the GPS and the marker removed); the parked
 character is in DE_4546B's gravity, so SE adds the GPS and shows the radio text once; two status replies 4 s apart
 agree (no second add)"""
            self.part("n11", "begin")
            self.act("se", "beacon", label="n11.rearm")
            self.wait(self.scale(4.0, 3.0))
            s1 = self.act("se", "beacon", {"status": True}, label="n11.status.1")
            self.wait(self.scale(4.0, 3.0))
            s2 = self.act("se", "beacon", {"status": True}, label="n11.status.2")
            self.sess.event("beacon", first=s1 if isinstance(s1, dict) else None, second=s2 if isinstance(s2, dict) else None)
            self.part("n11", "end")

        def p_c2g(self):
            """S8a.C2.ingame (Stage C step 1): SE's planet at the 20 c2g columns (de.s8a probe {planet: true}: GetClosestSurfacePointGlobal,
 the LOD-0 voxel geometry, the Water Mod depth, the radii, Rs, sculptHash) against sculpt_planet.py --depth-at on the planet data
 folder SE reports it loaded (read-only; the repository's world mod when that folder is unknown)"""
            self.part("c2g", "begin")
            S = sculpt_module(self.LCH)
            pts = [[float(x), float(z)] for x, z in S.C2G_POINTS]
            rep = self.act("se", "probe", pts, "c2g", label="c2g.probe", timeout=60.0)
            files_dir, where = None, None
            pdd = rep.get("planetDataDir") if self.ok(rep) else None
            if isinstance(pdd, str) and os.path.isdir(pdd):
                files_dir, where = os.path.normpath(os.path.join(pdd, "..", "..", "..")), "the planet data folder SE loaded"
            if files_dir is None or S.read_heightmaps(files_dir) is None:
                files_dir, where = os.path.join(self.LCH.REPO, "src", "DeepEngineers.WorldMod"), "the repository's world mod (SE's folder unknown or unreadable)"
            try:
                expected, shash = S.depth_at_points(files_dir, [tuple(p) for p in pts])
            except Exception as e:      # the tool's failure leaves the row MISSING, never the session
                expected, shash = None, "error %r" % e
            self.sess.event("c2g", points=pts, expected=expected, files=files_dir, filesWhere=where, filesSculptHash=shash,
                            probe=rep if isinstance(rep, dict) else None)
            self.part("c2g", "end")

        def ore_sites(self):
            """The design's deposits within ORE_NEAR_PATH_M of the n10 path (its window batches were generated and served) with a slope
 <= ORE_MAX_SLOPE, nearest to the path's end first, one ore after the other; [] when the tool fails. Rows of rare ores lie
 below 600 m, never near the path: they are not probed (the row says which ores it judged)."""
            # n7/n10 confirm (SE veins 3/5): the deposits of the planet SE runs, read from the files it loaded (de.s8a probe
 # planetDataDir), never the layout's newer design; the layout only when no deployed folder is known
            try:
                S = sculpt_module(self.LCH)
                rep = self.act("se", "probe", [(0.0, 0.0)], "n10.ore.where", label="n10.ore.where", timeout=30.0)
                pdd = rep.get("planetDataDir") if self.ok(rep) else None
                files_dir = os.path.normpath(os.path.join(pdd, "..", "..", "..")) if isinstance(pdd, str) and os.path.isdir(pdd) else None
                sites = S.ore_sites_from_files(files_dir) if files_dir else None
                source = files_dir
                if sites is None:
                    design = S.Design(S.load_layout(), S.DEFAULT_SEED)
                    sites = S.ore_sites(design, [S.sculpt_face(1, design, S.RESOLUTION, keep=True)])   # the ring lies on face 1
                    source = "layout"
                self.sess.event("ore_sites", source=source, n=len(sites))
            except Exception as e:
                self.sess.event("ore_sites", error=repr(e))
                return []
            (ax, az), (ex, ez) = WINDOW_PATH
            vx, vz = ex - ax, ez - az
            L2 = vx * vx + vz * vz

            def path_dist(s):
                u = max(0.0, min(1.0, ((s["x"] - ax) * vx + (s["z"] - az) * vz) / L2))
                return math.hypot(s["x"] - (ax + u * vx), s["z"] - (az + u * vz))

            near = sorted((s for s in sites if path_dist(s) <= ORE_NEAR_PATH_M and (s.get("slope") or 0.0) <= ORE_MAX_SLOPE),
                          key=lambda s: math.hypot(s["x"] - ex, s["z"] - ez))
            n = QUICK_ORE_PROBES if self.q else ORE_PROBES
            by_ore = {}
            for s in near:
                by_ore.setdefault(s["ore"], []).append(s)
            out = []
            while len(out) < n and any(by_ore.values()):
                for ore in list(by_ore):
                    if by_ore[ore] and len(out) < n:
                        out.append(by_ore[ore].pop(0))
            return out

        def ore_probes(self):
            """S8a.N10.ore: per deposit SE's storage material on the vertical under the seabed at one depth above the vein and three
 inside it (de.s8a probe {depths}); SE's vein lies at perpendicular depths Start + 0.5.. Start + Depth + 0.5, so the
 vertical depths are scaled by sqrt(1 + slope^2) and keep 1 m from either end. Then the first deposits whose vein SE holds
 are drilled (a closed cavity of ORE_DRILL_R whose equator sits at the vein's middle: the window regenerates the batch) and
 Subnautica's served voxels just inside the cavity wall (density != 0: a converted surface voxel) must carry the ore row's
 type; an undrilled interior voxel is reported only (Subnautica's own octree collapse keeps far-solid types as blocks)."""
            sites = self.ore_sites()
            rows = []
            for s in sites:
                k = math.sqrt(1.0 + float(s.get("slope") or 0.0) ** 2)
                st, dp = float(s["startM"]), float(s["depthBelowM"])
                # a test run (n10 20261023-234428: a Nickel row's last sample on the vein's lower edge, SE's slope rounding): the last one
 # 1.5 m inside the vein's end (Start + Depth + 0.5), as the first is 1 m inside its start
                depths = [round(max(0.5, st - 1.0) * k, 3), round((st + 1.5) * k, 3), round((st + 0.5 + 0.5 * dp) * k, 3), round((st + dp - 1.0) * k, 3)]
                roles = ["above", "inside", "inside", "inside"]
                rep = self.act("se", "probe", [(s["x"], s["z"])], "n10.ore." + s["type"], depths, label="n10.ore.se", timeout=20.0)
                r0 = (rep.get("rows") or [{}])[0] if self.ok(rep) else {}
                sy = r0.get("storageY") if isinstance(r0.get("storageY"), (int, float)) and not math.isnan(r0["storageY"]) else r0.get("closestY")
                interior = None
                if isinstance(sy, (int, float)) and not math.isnan(sy):
                    sp = self.act("sn", "probe", (s["x"], sy - depths[2], s["z"]), label="n10.ore.sn")
                    interior = {"type": sp.get("type"), "density": sp.get("density"), "solid": sp.get("solid"), "source": sp.get("source")} if self.ok(sp) else None
                rows.append({"ore": s["ore"], "type": s["type"], "x": s["x"], "z": s["z"], "seabedY": s["seabedY"], "slope": s.get("slope"), "pixels": s["pixels"],
                             "startM": st, "endM": st + dp, "k": round(k, 4), "depths": depths, "roles": roles, "seMaterials": r0.get("materials"), "seY": sy,
                             "sculptHash": rep.get("sculptHash") if self.ok(rep) else None, "snInterior": interior, "wall": None})
            # the drilled cavities: Subnautica's converted wall voxels
            drills = 0
            want = QUICK_ORE_DRILLS if self.q else ORE_DRILLS
            for r in rows:
                if drills >= want:
                    break
                mats = r.get("seMaterials") or []
                inside = [m for m, role in zip(mats, r["roles"]) if role == "inside"]
                if not inside or any(m != r["type"] for m in inside) or not isinstance(r.get("seY"), (int, float)):
                    continue
                c = (r["startM"] + 0.5 + 0.5 * (r["endM"] - r["startM"])) * r["k"]
                centre = (r["x"], r["seY"] - c, r["z"])
                self.teleport(r["x"] + CAMERA_BACK_M, r["seY"] + CAMERA_UP_M, r["z"], 270.0, label="n10.ore.camera")
                self.wait(self.scale(2.0, 1.0))
                self.settle_window("n10.ore.settle", 200 + drills)
                drep = self.act("se", "drill", centre, ORE_DRILL_R, "sphere", label="n10.ore.drill", timeout=10.0)
                drills += 1
                r["drill"] = {"centre": list(centre), "radius": ORE_DRILL_R, "ok": self.ok(drep)}
                if not self.ok(drep):
                    continue
                self.wait(self.scale(2.0, 2.0))            # the drill's witness (1.5 s) holds the next drill
                self.wait_window_quiet("n10.ore.window", self.scale(WINDOW_APPLY_WAIT_S, 3.0))
                self.wait_applied(200 + drills, time.monotonic() - APPLY_MIN_S, timeout=self.scale(WINDOW_APPLY_WAIT_S, 3.0))
                # SE's cut is binary (MyVoxelGenerator.CutOutSphere: content 0 within the radius), so the converted wall is the
 # first solid voxel going outward from the centre: its type is the SE material of the solid sample next to the cut
                wall = []
                for ux, uz in ((1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0)):
                    found = {"dx": ux, "dz": uz, "type": None, "density": None, "radius": None}
                    for rr in WALL_STEPS_M:
                        sp = self.act("sn", "probe", (r["x"] + ux * rr, centre[1], r["z"] + uz * rr), label="n10.ore.wall")
                        if not self.ok(sp):
                            found["error"] = (sp or {}).get("error")
                            break
                        if (sp.get("type") or 0) > 0:
                            found.update({"type": sp.get("type"), "density": sp.get("density"), "solid": sp.get("solid"), "source": sp.get("source"), "radius": rr})
                            break
                    wall.append(found)
                # night queue: SE's own material at each wall sample (0.5 m further out: inside SE's rock, past the cut), so
 # the rule compares Subnautica's wall type with SE's there, not with the row's ore (a thin or sloped vein may end)
                hits = [w for w in wall if w.get("radius")]
                if hits:
                    mrep = self.act("se", "materialat", [(r["x"] + w["dx"] * (w["radius"] + 0.5), centre[1], r["z"] + w["dz"] * (w["radius"] + 0.5)) for w in hits],
                                    label="n10.ore.wall.se")
                    if self.ok(mrep) and isinstance(mrep.get("materials"), list):
                        for w, m in zip(hits, mrep["materials"]):
                            w["seType"] = m
                r["wall"] = wall
            self.sess.event("ore_probe", rows=rows, n=len(sites), drills=drills)

        def p_c1(self):
            self.part("c1", "begin")
            self.part("c1", "end")

        def p_c3(self):
            self.part("c3", "begin")
            self.part("c3", "end")

        def p_teardown(self):
            """every mutant of the session off; after Stage B parts: the window throttle off, the treadmill returned to chart 0, the
 spawns cleared, the placed plate removed"""
            self.part("teardown", "begin")
            if self.stage_b_used:
                self.act("se", "window", {"throttleMs": 0, "on": True}, label="teardown.throttle")
                self.act("se", "step", "site", 1, label="teardown.treadmill")
                self.act("sn", "spawns", {"clear": True}, label="teardown.spawns")
                for _ in range(self.placed):
                    self.act("se", "remove", label="teardown.remove")
            for m in self.mutants or []:
                if m in WIRE_MUTANTS:
                    self.act("se", "mutant", m, False, label="teardown.mutant.%s" % m)
                    continue
                self.act("sn" if m in SN_MUTANTS else "se", "mutant", m, False, label="teardown.mutant.%s" % m)
            self.part("teardown", "end")

    return S8aRunner

# ----------------------------------------------------------------------------------------------------------------
# offline parts: c1 (the round trip), c3 (the pin)
# ----------------------------------------------------------------------------------------------------------------

def look_sheet(out_dir, sess):
    """look-sheet.png in the session folder: the spawn's captures on the left (a column per height), the far points on the right
 (a row per point: its 8 m and 25 m captures), each labelled with biome, distance and depth; the orbit shots under them."""
    from PIL import Image, ImageDraw  # noqa: E402
    evs = [e for e in getattr(sess, "events_list", lambda: [])()] if hasattr(sess, "events_list") else []
    if not evs:
        path = os.path.join(out_dir, "s8a-session.jsonl")
        if os.path.isfile(path):
            evs = [json.loads(ln) for ln in open(path, encoding="utf-8") if ln.strip()]
    caps = [e for e in evs if e.get("ev") == "look_capture" and e.get("written") and e.get("png")]
    orbit = [e for e in evs if e.get("ev") == "look_orbit" and e.get("written") and e.get("png")]
    if not caps:
        return None
    W, H, pad, lab = 384, 216, 6, 16
    spawn = [c for c in caps if c.get("kind") == "spawn"]
    far = [c for c in caps if c.get("kind") in ("ref", "far")]       # the real map's reference rows first, then the far points
    pts = []
    for c in far:
        key = (c.get("biome"), c.get("x"), c.get("z"))
        if key not in pts:
            pts.append(key)
    rows_n = max(len(NL_SPAWN_HEADINGS), len(pts)) + (1 if orbit else 0)
    cols_n = len(NL_HEIGHTS) * 2
    sheet = Image.new("RGB", (cols_n * (W + pad) + pad + 20, rows_n * (H + lab + pad) + pad + 30), (245, 245, 245))
    dr = ImageDraw.Draw(sheet)
    dr.text((pad, 6), "S8a look: the spawn inside the Subnautica map (left) against far planet biomes (right), at 8 and 25 m above the seabed, noon", fill=(0, 0, 0))

    def paste(c, col, rowi, text):
        try:
            im = Image.open(os.path.join(out_dir, c["png"])).convert("RGB").resize((W, H))
        except Exception:
            return
        x0 = pad + col * (W + pad) + (20 if col >= len(NL_HEIGHTS) else 0)
        y0 = 30 + rowi * (H + lab + pad)
        sheet.paste(im, (x0, y0 + lab))
        dr.text((x0, y0 + 2), text, fill=(0, 0, 0))
    for c in spawn:
        hi = NL_HEIGHTS.index(c.get("height")) if c.get("height") in NL_HEIGHTS else 0
        ri = NL_SPAWN_HEADINGS.index(c.get("heading")) if c.get("heading") in NL_SPAWN_HEADINGS else 0
        paste(c, hi, ri, "spawn h%g m heading %.0f, seabed %.0f m" % (c.get("height"), c.get("heading") or 0, c.get("seabedY") or 0))
    for c in far:
        hi = NL_HEIGHTS.index(c.get("height")) if c.get("height") in NL_HEIGHTS else 0
        ri = pts.index((c.get("biome"), c.get("x"), c.get("z")))
        paste(c, len(NL_HEIGHTS) + hi, ri, "%s %.1f km, seabed %.0f m, h%g m" % (c.get("biome"), (c.get("rho") or 0) / 1000.0, c.get("seabedY") or 0, c.get("height")))
    for k, o in enumerate(orbit):
        paste(o, k, rows_n - 1, "SE from %d km up (noon)" % o.get("km"))
    p = os.path.join(out_dir, "look-sheet.png")
    sheet.save(p)
    return p

def _run(cmd, cwd, timeout, env=None):
    try:
        p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout, env=env, stdin=subprocess.DEVNULL,
                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        out = (p.stdout or "") + (p.stderr or "")
        return p.returncode, "\n".join(out.strip().splitlines()[-25:])
    except (OSError, subprocess.SubprocessError) as e:
        return -1, "%s: %s" % (type(e).__name__, e)

def run_c1(LCH, log, a, out, sess, mutants):
    """S8a.C1: the C# round trip (S8aRoundTripTests, writes tests/out/s8a/c1/s8a-c1-cs.json) and the Python twin."""
    sess.event("part", name="c1", phase="begin", tick=0)
    n = int(getattr(a, "c1_files", 100) or 100)
    env = dict(os.environ, DEEPENG_S8A_FILES=str(n))
    cs_json_src = os.path.join(LCH.REPO, "tests", "out", "s8a", "c1", "s8a-c1-cs.json")
    cs_cmd = ["dotnet", "test", os.path.join(LCH.REPO, "tests", "DeepEngineers.Terrain.Tests", "DeepEngineers.Terrain.Tests.csproj"), "-c", "Release", "-nologo", "-v", "q",
              "--filter", "FullyQualifiedName~S8aRoundTripTests"]
    log.say("S8a c1: %s" % subprocess.list2cmdline(cs_cmd))
    rc_cs, tail_cs = _run(cs_cmd, LCH.REPO, 1800, env)
    cs_json = os.path.join(out, "s8a-c1-cs.json")
    try:
        shutil.copyfile(cs_json_src, cs_json)
    except OSError:
        cs_json = None
    c1 = {"cs": {"path": cs_json_src, "rc": rc_cs, "tail": tail_cs, "json": cs_json, "files": n}}
    if not getattr(a, "no_python_roundtrip", False):
        py_json = os.path.join(out, "s8a-c1-py.json")
        py_cmd = [sys.executable, "-I", os.path.join(LCH.REPO, "tools", "sn-octrees", "sn_octree_write.py"), "--roundtrip", str(n), "--json", py_json, "--quiet"]
        if "S8a.M13" in mutants:
            py_cmd += ["--mutant", "S8a.M13"]
        log.say("S8a c1: %s" % subprocess.list2cmdline(py_cmd))
        rc_py, tail_py = _run(py_cmd, LCH.REPO, 3600)
        c1["py"] = {"path": py_json, "rc": rc_py, "tail": tail_py, "json": py_json if os.path.exists(py_json) else None, "mutant": "S8a.M13" if "S8a.M13" in mutants else ""}
    sess.event("c1", **c1)
    sess.event("part", name="c1", phase="end", tick=0)
    log.say("S8a c1: C# rc %s%s" % (rc_cs, ", Python rc %s" % c1["py"]["rc"] if "py" in c1 else ""))

def run_c2(LCH, log, a, out, sess):
    """S8a.C2: the sculpt verified against the rules (sculpt_planet.py --verify on the world mod; MISSING files = not written yet)."""
    sess.event("part", name="c2", phase="begin", tick=0)
    cmd = [sys.executable, "-I", os.path.join(LCH.REPO, "tools", "planet", "sculpt_planet.py"), "--verify", os.path.join(LCH.REPO, "src", "DeepEngineers.WorldMod")]
    if getattr(a, "seed", None) is not None:
        cmd += ["--seed", str(a.seed)]
    log.say("S8a c2: %s" % subprocess.list2cmdline(cmd))
    rc, tail = _run(cmd, LCH.REPO, 3600)
    sess.event("c2", rc=rc, tail=tail, cmd=cmd)
    sess.event("part", name="c2", phase="end", tick=0)
    log.say("S8a c2: rc %s" % rc)

def run_c3(LCH, log, a, out, sess):
    """S8a.C3: the pin (S8aConventionsTests, s8a_ref.py --pin and --selftest, layout_py.py compare)."""
    sess.event("part", name="c3", phase="begin", tick=0)
    wire_cmd = ["dotnet", "test", os.path.join(LCH.REPO, "tests", "DeepEngineers.Wire.Tests", "DeepEngineers.Wire.Tests.csproj"), "-c", "Release", "-nologo", "-v", "q",
                "--filter", "FullyQualifiedName~S8aConventionsTests"]
    rc_w, tail_w = _run(wire_cmd, LCH.REPO, 1800)
    ref = os.path.join(LCH.REPO, "tools", "planet", "s8a_ref.py")
    rc_p, tail_p = _run([sys.executable, "-I", ref, "--pin"], LCH.REPO, 300)
    rc_s, tail_s = _run([sys.executable, "-I", ref, "--selftest"], LCH.REPO, 600)
    rc_l, tail_l = _run([sys.executable, "-I", os.path.join(LCH.REPO, "tests", "wire", "layout_py.py"), "compare"], LCH.REPO, 300)
    c3 = {"wire": {"rc": rc_w, "tail": tail_w}, "ref": {"rc": 0 if rc_p == 0 and rc_s == 0 else (rc_p or rc_s), "tail": tail_p + "\n" + tail_s},
          "layout": {"rc": rc_l, "tail": tail_l}}
    sess.event("c3", **c3)
    sess.event("part", name="c3", phase="end", tick=0)
    log.say("S8a c3: wire rc %s, ref rc %s/%s, layout rc %s" % (rc_w, rc_p, rc_s, rc_l))

# ----------------------------------------------------------------------------------------------------------------
# plan, env, session
# ----------------------------------------------------------------------------------------------------------------

def env_plan(mutants, parts, treadmill=False):
    """(Subnautica env, SE env) of a session (ROADMAP "### S8a" launcher line)."""
    sn = [m for m in mutants if m in SN_MUTANTS]
    se = [m for m in mutants if m in SE_MUTANTS or m in WIRE_MUTANTS]
    sn_env = {"DEEPENGINEERS_S8A": "on"}          # the Subnautica side's loader prefix and the widened centre clamp (window ids)
    if sn:
        sn_env["DEEPENGINEERS_MUTANT"] = ",".join(sn)
    se_env = {"DEEPENGINEERS_S5": "on", "DEEPENGINEERS_PICTURE": "site", "DEEPENGINEERS_TIMESYNC": "on", "DEEPENGINEERS_GHOST": "off",
              "DEEPENGINEERS_S6": "off", "DEEPENGINEERS_BAND": "%g,%g" % BAND, "DEEPENGINEERS_S8A": "on", "DEEPENGINEERS_S8A_DRILL": "on", "DEEPENGINEERS_S8A_WINDOW": "on"}
    if treadmill or "n8" in parts:
        se_env["DEEPENGINEERS_S8A_TREADMILL"] = "on"
    if "nf" in parts:
        se_env["DEEPENGINEERS_S8A_RING"] = "on"        # Stage F2: off by default, on only for nf sessions (no ring switch in run)
    if "ns" in parts or "nl" in parts:
        se_env["DEEPENGINEERS_S8A_SAMPLE"] = "on"      # planet v2 route B: the sample chunk, on only for ns and nl (nl looks at it)
    if "no" in parts:
        sn_env["DEEPENGINEERS_S8A_ORE_PROPS"] = "on"   # ores as voxels: the deposit props are off by default; no's NO.cover..immune judge them
    if se:
        se_env["DEEPENGINEERS_MUTANTS"] = ",".join(se)
    return sn_env, se_env

def nf_notes(LCH):
    """nf's inputs (Stage F2): the store's brush library (SE reads it; without it the ring field refuses and the nf rows stay MISSING)
 and whether the install vectors were made with it (S8a.NF.determinism needs them equal)."""
    store = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "DeepEngineers", "terrain", "brushes")
    lib, idx = os.path.join(store, "library-v1.bin"), os.path.join(store, "index-v1.tsv")
    out = []
    if not (os.path.exists(lib) and os.path.exists(idx)):
        out.append("no brush library in %s (python tools/planet/s8a_brushes.py build): the ring field will refuse" % store)
        return out

    def fnv(path):
        h = 0xcbf29ce484222325
        for b in open(path, "rb").read():
            h = ((h ^ b) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
        return "%016x" % h
    vec = os.path.join(LCH.REPO, NF_VECTORS)
    want = None
    try:
        for ln in open(vec, encoding="utf-8").read().splitlines():
            f = ln.split("\t")
            if f[0] == "library":
                want = (f[1], f[2])
    except OSError:
        pass
    have = (fnv(lib), fnv(idx))
    out.append("brush library %s (fnv %s / %s); the install vectors %s" % (
        store, have[0], have[1], "match it" if want == have else "were made with another library (python tools/planet/s8a_ring3d.py vectors): S8a.NF.determinism MISSING"))
    out.append("SE env DEEPENGINEERS_S8A_RING=on for this session only (run never sets it)")
    return out

def ns_notes(LCH):
    """The sample's inputs (planet v2 route B): the store's chunk and placement (SE reads them; without them the sample refuses and
 the ns rows stay MISSING) and whether the install vectors were made with them (S8a.NS.determinism needs them equal)."""
    try:
        C = chunks_module(LCH)
        cb, ct, cs = C.chunk_paths()
    except Exception as e:
        return ["s8a_chunks not loadable (%s)" % e]
    if not (os.path.exists(cb) and os.path.exists(ct) and os.path.exists(cs)):
        return ["no chunk in %s (python tools/planet/s8a_chunks.py build): the sample will refuse" % os.path.dirname(cb)]

    def fnv(path):
        h = 0xcbf29ce484222325
        for b in open(path, "rb").read():
            h = ((h ^ b) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
        return "%016x" % h
    want = None
    try:
        for ln in open(os.path.join(LCH.REPO, NS_VECTORS), encoding="utf-8").read().splitlines():
            f = ln.split("\t")
            if f[0] == "chunk":
                want = f[1]
    except OSError:
        pass
    have = fnv(cb)
    return ["chunk %s (fnv %s); the install vectors %s" % (cb, have, "match it" if want == have else "were made with another chunk (python tools/planet/s8a_chunks.py vectors): S8a.NS.determinism MISSING"),
            "SE env DEEPENGINEERS_S8A_SAMPLE=on for this session only (ns and nl; run never sets it)"]

def resolve(a):
    """(parts, mutants, world, treadmill, problems) from the arguments."""
    probs = []
    mutants = [m.strip() for m in (getattr(a, "mutant", "") or "").split(",") if m.strip()]
    bad = [m for m in mutants if m not in ALL_MUTANTS]
    if bad:
        probs.append("unknown mutants %s (known: %s)" % (bad, ",".join(ALL_MUTANTS)))
    if len(mutants) > 1:
        probs.append("one mutant per session (its rows are judged alone): %s" % ",".join(mutants))
    world = getattr(a, "world", None)
    treadmill = bool(getattr(a, "treadmill", False))
    if mutants and not bad:
        g = MUTANT_GROUPS[mutants[0]]
        parts = list(g["parts"])
        world = world or g.get("world")
        treadmill = treadmill or bool(g.get("treadmill"))
    else:
        parts = list(DEFAULT_PARTS)
    if getattr(a, "parts", ""):
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
    unknown = [x for x in parts if x not in S8A_PARTS]
    if unknown:
        probs.append("unknown parts %s (known: %s)" % (unknown, ",".join(S8A_PARTS)))
    try:
        load_sites(getattr(a, "sites", None))
    except (OSError, ValueError, KeyError, TypeError) as e:
        probs.append("--sites: %s" % e)
    if not world:
        needs = sorted(set(PART_WORLD[p] for p in parts if p in PART_WORLD))
        world = needs[0] if len(needs) == 1 else None
        if len(needs) > 1:
            probs.append("parts %s need different world copies: one session each" % ",".join(p for p in parts if p in PART_WORLD))
    return parts, mutants, world, treadmill, probs

def plan_lines(LCH, a, parts, mutants, world, ws, sn_env, se_env, sites) -> list:
    R = _runner_class(LCH)
    AN = analyzer(LCH)
    online = [p for p in parts if p not in OFFLINE_PARTS]
    out = []
    if online:
        out += ["  both games (%s, Legacy.exe -continue); character parked at SN (24, -8, 0) first (S5-STATUS 9.1); install listing before and after" % world,
                "  world %r: folder %s, LastSession %s%s, autosave %s, marker %s, world mod %s" % (
                    world, ws.get("folder"), ws.get("lastSession"), "" if ws.get("pointed") else " (NOT pointed at it)", ws.get("autosaveMinutes"), ws.get("marked"), ws.get("worldMod")),
                "  Subnautica env %s; SE env %s" % (json.dumps(sn_env) or "-", json.dumps(se_env)),
                "  band %g / %g m (DEEPENGINEERS_BAND or the default)" % BAND]
    else:
        out.append("  offline (no game): %s" % ",".join(parts))
    out.append("  mutants: %s%s" % (",".join(mutants) or "none", (" -> red rows " + ",".join(r for m in mutants for r in AN.MUTANT_RED.get(m, ()))) if mutants else ""))
    out.append("  owner steps: %s" % ("waited for (--owner)" if getattr(a, "owner", False) else "skipped (their rows may stay MISSING)"))
    out.append("  sites (SN x, z, path%s): %s" % (", seam" if any(s["seam"] for s in sites) else "", "; ".join("%d: (%g, %g) %s%s" % (s["i"], s["x"], s["z"], s["path"], " seam" if s["seam"] else "") for s in sites)))
    q = getattr(a, "quick", False)
    out.append("  rays per hole: footprint %d x %d, inner %d x %d (within 0.7 r), rim %d; radius %g m; drills %g s apart; camera %g m away" % (
        (QUICK_FOOTPRINT_N if q else FOOTPRINT_N,) * 2 + (QUICK_N3_N if q else N3_N,) * 2 + (QUICK_RIM_N if q else RIM_N, RADIUS_M, DRILL_GAP_S, CAMERA_BACK_M)))
    for p in parts:
        fn = getattr(R, "p_" + p, None)
        doc = " ".join((fn.__doc__ or "").split()) if fn else ""
        if p == "c1":
            doc = "offline: S8aRoundTripTests (C#, %s files) + sn_octree_write.py --roundtrip (the Python twin)%s" % (getattr(a, "c1_files", 100), " with S8a.M13" if "S8a.M13" in mutants else "")
        if p == "c2":
            doc = "offline: sculpt_planet.py --verify src/DeepEngineers.WorldMod (S8a.C2.sculpt, S8a.C2.flat; MISSING until the owner's PNG write)"
        if p == "c3":
            doc = "offline: S8aConventionsTests, s8a_ref.py --pin/--selftest, layout_py.py compare"
        out.append("  part %-9s %s" % (p, doc))
        if p == "c2g":
            try:
                pts = sculpt_module(LCH).C2G_POINTS
                out.append("      %d SN columns: %s" % (len(pts), " ".join("(%g, %g)" % xz for xz in pts)))
            except Exception as e:
                out.append("      the c2g points: sculpt tool unavailable (%r)" % e)
        if p in ("n7", "n9", "n10"):
            out.append("      path SN %s -> %s%s at %s m/s, %g m above SE's seabed (else y %g); %d columns%s" % (
                WINDOW_PATH[0], WINDOW_PATH[1], " and the ring " + " -> ".join(str(r) for r in WINDOW_RING) if p == "n7" else "",
                "20 and 80" if p == "n7" else "80" if p == "n9" else "20", WINDOW_ABOVE_M, WINDOW_FALLBACK_Y, QUICK_WINDOW_COLUMNS if getattr(a, "quick", False) else WINDOW_COLUMNS,
                "; drill r %g m at SN %s" % (WINDOW_DRILL_R, WINDOW_DRILL_AT) if p == "n7" else "; throttle %d ms" % int(getattr(a, "throttle_ms", THROTTLE_MS) or THROTTLE_MS) if p == "n9" else "; spawns on over the path and the ring, material probes"))
        if p == "n8":
            out.append("      treadmill path SN (0, %g, 0) -> (%g, %g, 0) and back at %g m/s; plate %g m beside the camera; forced steps at x %g (open water) and %g (terrain)" % (
                WINDOW_FALLBACK_Y, TREADMILL_FAR_M, WINDOW_FALLBACK_Y, TREADMILL_SPEED, TREADMILL_PLATE_OFFSET_M, TREADMILL_STEP_OPEN_X, TREADMILL_STEP_TERRAIN_X))
    out.append("  SE actions: " + ", ".join("%s=%s" % (k, v[0]) for k, v in R.SE_ACTIONS.items()))
    out.append("  Subnautica actions: " + ", ".join("%s=%s" % (k, v[0]) for k, v in R.SN_ACTIONS.items()))
    return out

def s8a_analyze(LCH, log, a, out) -> int:
    AN = analyzer(LCH)
    try:
        rows = AN.evaluate(out)
    except Exception as e:
        log.say("S8a analyzer failed: %r" % e)
        return 1
    ok = AN.write_results(out, rows, not getattr(a, "lenient", False))
    print(AN.table(rows))
    log.say("S8a ORACLE TABLE %s: %s" % ("PASS" if ok else "FAIL", os.path.join(out, "s8a-table.txt")), ev="s8a_table", ok=ok)
    return 0 if ok else 1

def install_listing(LCH, log, sess, out, when):
    IC = install_check(LCH)
    path = os.path.join(out, "s8a-install-%s.txt" % when)
    se_dir = os.path.dirname(LCH.SE_BIN64)
    t0 = time.monotonic()
    try:
        L = IC.list_to_file(path, LCH.SUBNAUTICA, se_dir)
        sess.event("install_listing", when=when, path=path, roots=L["roots"], seconds=round(time.monotonic() - t0, 1))
        log.say("S8a install listing %s: %s (%.1f s)" % (when, "; ".join("%s %d files" % (r["kind"], r["files"]) for r in L["roots"]), time.monotonic() - t0))
        return path
    except Exception as e:
        sess.event("install_listing", when=when, path=path, error="%s: %s" % (type(e).__name__, e))
        log.say("S8a install listing %s failed: %r" % (when, e))
        return None

def install_compare(LCH, log, sess, before, after):
    IC = install_check(LCH)
    if not before or not after:
        sess.event("install_compare", ok=False, error="a listing is missing", counts={})
        return
    try:
        c = IC.compare(IC.read_listing(before), IC.read_listing(after))
    except Exception as e:
        sess.event("install_compare", ok=False, error="%s: %s" % (type(e).__name__, e), counts={})
        return
    sess.event("install_compare", **c)
    log.say("S8a install compare: %s (changed %d, added %d, removed %d)" % ("unchanged" if c["ok"] else "CHANGED", c["counts"]["changed"], c["counts"]["added"], c["counts"]["removed"]),
            ev="s8a_install_compare", ok=c["ok"])

def world_scan(LCH, log, sess, world):
    """S8a.N4.portable: the save folder scanned read-only with the S4 analyzer's scanner."""
    info = LCH.s4_world_info(world)
    folder = info.get("folder")
    if not folder:
        sess.event("world_scan", folder=None, error=info.get("error") or "world not found")
        return
    try:
        AS = s4_analyzer(LCH)
        sc = AS.scan_world(folder)
        sess.event("world_scan", folder=folder, vx2=sc.get("vx2"), tileVx2=sc.get("tileVx2"), storageNames=sc.get("storageNames"), tileVoxelMaps=sc.get("tileVoxelMaps"),
                   grids=len(sc.get("grids") or []), error=sc.get("error"))
    except Exception as e:
        sess.event("world_scan", folder=folder, error="%s: %s" % (type(e).__name__, e))

def host_slot_event(LCH, sess, a, when, **kw):
    slot = LCH.host_slot_dir(a)
    if slot:
        sess.event("host_slot", when=when, dir=slot, files=LCH.hash_tree(slot) if os.path.isdir(slot) else None, **kw)

def s8a(LCH, log, a) -> int:
    if a.list_mutant_groups:
        AN = analyzer(LCH)
        for m, g in MUTANT_GROUPS.items():
            side = "SN" if m in SN_MUTANTS else ("Wire" if m in WIRE_MUTANTS else "SE")
            print("  %-8s %-5s parts %-16s world %-16s red %s" % (m, side, ",".join(g["parts"]), g.get("world") or LAB_WORLD, ",".join(AN.MUTANT_RED.get(m, ()))))
        return 0
    if a.selftest:
        return selftest(LCH, log, a)
    if a.analyze:
        return s8a_analyze(LCH, log, a, a.analyze)
    parts, mutants, world, treadmill, probs = resolve(a)
    if probs:
        for p in probs:
            log.say("REFUSE s8a: %s" % p)
        return 2
    standin = bool(a.sn_cmd or a.se_cmd)
    online = [p for p in parts if p not in OFFLINE_PARTS]
    world = world or last_session_world(LCH) or LAB_WORLD
    ws = world_settings(LCH, world) if online else {"world": world}
    sn_env, se_env = env_plan(mutants, parts, treadmill)
    sites = load_sites(a.sites)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s8a", stamp + ("-" + mutants[0] if mutants else ""))
    a.cont = not a.no_continue
    sn_cmd, se_cmd = LCH.session_commands(a)
    store_key = None
    try:
        if ws.get("folder"):
            # review Stage A minor: the key as SE derives it
 # the runner checks
 # it against de.s8a status overlay.storeKey once online (event store_key)
            store_key = "%016x" % ref_module(LCH).store_key(se_current_path(ws["folder"]), 1)
    except Exception:
        store_key = None
    log.say("S8a session %s: parts %s%s%s; world %s; Subnautica: %s; SE: %s; out %s" % (
        stamp, ",".join(parts), " (quick)" if a.quick else "", " mutant %s" % mutants[0] if mutants else "", world if online else "(offline)",
        subprocess.list2cmdline(sn_cmd), subprocess.list2cmdline(se_cmd), out), ev="s8a_plan")
    wprobs = world_problems(ws, parts, standin) if online else []
    stage_b = [p for p in parts if p in STAGE_B_PARTS]
    if a.dry_run:
        for line in plan_lines(LCH, a, parts, mutants, world, ws, sn_env, se_env, sites):
            print(line)
        for p in wprobs:
            print("  WOULD REFUSE: %s" % p)
        if stage_b:
            print("  NOTE: parts %s need the sculpt deployed (ROADMAP S8a owner action 1); the window paths run over SE's planet as it is" % ",".join(stage_b))
        if "nf" in parts:
            for line in nf_notes(LCH):
                print("  NOTE: nf: %s" % line)
        if "ns" in parts or "nl" in parts:
            for line in ns_notes(LCH):
                print("  NOTE: ns/nl: %s" % line)
        log.say("dry run: nothing started")
        return 0
    if wprobs:
        for p in wprobs:
            log.say("REFUSE s8a: %s" % p)
        return 2
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "s8a-session.jsonl"))
    if online and not standin:
        #  the store is derived data; a fresh session on a copy never inherits an earlier session's
 # (possibly unsaved) drill layer: the copy's store folder is removed before the games start (the plugin's dirty
 # sentinel covers the key mismatch case)
        store_dir = os.path.join(LCH.LOCAL, "terrain", store_key) if store_key else None
        removed = False
        if store_dir and os.path.isdir(store_dir):
            try:
                shutil.rmtree(store_dir)
                removed = True
            except OSError as e:
                log.say("S8a: the store folder %s could not be removed: %s" % (store_dir, e))
        sess.event("store_reset", folder=store_dir, removed=removed)
        log.say("S8a store %s: %s" % (store_dir, "removed (derived data)" if removed else "absent"))
    sess.event("start", kind="s8a", stamp=stamp, parts=parts, mutants=mutants, name=a.name, argv=sys.argv[1:], quick=a.quick, band=list(BAND),
               world=world if online else None, settings={"autosave": ws.get("autosaveMinutes"), "marked": ws.get("marked"), "treadmill": treadmill,
                                                          "s8a": se_env.get("DEEPENGINEERS_S8A"), "drill": se_env.get("DEEPENGINEERS_S8A_DRILL")},
               storeRoot=os.path.join(LCH.LOCAL, "terrain"), storeKey=store_key, sites=sites, owner=a.owner, standin={"sn": bool(a.sn_cmd), "se": bool(a.se_cmd)},
               snCmd=sn_cmd, seCmd=se_cmd, qpcFrequency=LCH.S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env)
    for p in parts:
        if p == "c1":
            run_c1(LCH, log, a, out, sess, mutants)
        elif p == "c3":
            run_c3(LCH, log, a, out, sess)
        elif p == "c2":
            run_c2(LCH, log, a, out, sess)
    if not online:
        sess.event("end", rc=None)
        sess.close()
        return s8a_analyze(LCH, log, a, out)
    before = install_listing(LCH, log, sess, out, "before")
    host_slot_event(LCH, sess, a, "before")
    LCH.S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    stats = {}
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            R = _runner_class(LCH)
            runner = R(LCH, log, a, sess, sn, se, out, stamp, bool(a.sn_cmd), bool(a.se_cmd), t_start=t_start)
            runner.mutants = mutants
            ok, why = runner.attach()
            if ok:
                runner.store_key_check(store_key)
            if not ok:
                log.say("S8a: harness not usable: %s" % why, ev="s8a_harness_refused")
                rc = 1
            else:
                runner.park()
                for m in mutants:
                    runner.act("sn" if m in SN_MUTANTS else "se", "mutant", m, True, label="mutant.%s" % m)
                for p in [x for x in parts if x not in OFFLINE_PARTS]:
                    if not runner.alive():
                        log.say("S8a: a game exited before part %s" % p, ev="s8a_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:
                        log.say("S8a part %s failed: %r" % (p, e), ev="s8a_part_error", part=p)
                        sess.event("part_error", name=p, error=repr(e))
                    if p == "n4" and runner.relaunch_needed and not runner.relaunched:
                        # the second launch of the N4 session: both games closed, the copy loaded again, the rays repeated
                        stats.update(runner.stats)
                        runner.close()
                        sess.event("relaunch", phase="closing", ok=True)
                        LCH.close_session(log, a, sn, se, bool(a.se_cmd), bool(a.sn_cmd))
                        for pid in (se.pid if se else None, sn.pid if sn else None):
                            if pid:
                                LCH.wait_pid(pid, 60.0)
                        time.sleep(RELAUNCH_COOLDOWN_S if not (a.se_cmd or a.sn_cmd) else 1.0)
                        rc2, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
                        sess.event("relaunch", phase="started", ok=rc2 is None)
                        runner = None
                        if rc2 is None and LCH.wait_in_world(log, sess, a, sn, se):
                            runner = R(LCH, log, a, sess, sn, se, out, stamp, bool(a.sn_cmd), bool(a.se_cmd), t_start=t_start)
                            runner.mutants = mutants
                            runner.relaunched = True
                            ok, why = runner.attach()
                            if ok:
                                runner.store_key_check(store_key)
                            sess.event("relaunch", phase="inworld", ok=ok, why=why)
                            if ok:
                                runner.park()
                                try:
                                    runner.p_n4b()
                                except Exception as e:
                                    log.say("S8a part n4b failed: %r" % e, ev="s8a_part_error", part="n4b")
                                    sess.event("part_error", name="n4b", error=repr(e))
                            else:
                                log.say("S8a: harness not usable after the relaunch: %s" % why, ev="s8a_harness_refused")
                        else:
                            sess.event("relaunch", phase="inworld", ok=False)
                            rc = rc2 if rc2 is not None else 1
                            break
                if runner:
                    log.say("S8a script finished: %s" % json.dumps(runner.stats), ev="s8a_script_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            stats.update(runner.stats)
            runner.close()
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            LCH.close_session(log, a, sn, se, bool(a.se_cmd), bool(a.sn_cmd))
        LCH.S.fine_timer(False)
        sn_alive = sn is not None and sn.poll() is None
        if sn_alive and not a.keep_running and not a.sn_cmd:
            sn_alive = not LCH.wait_pid(sn.pid, 60.0)
        host_slot_event(LCH, sess, a, "after", snExited=not sn_alive)
        if "n4" in parts:
            world_scan(LCH, log, sess, world)
        after = install_listing(LCH, log, sess, out, "after")
        install_compare(LCH, log, sess, before, after)
        sess.event("end", rc=rc, stats=stats or None, unsupported=sorted("%s.%s" % k for k in runner.unsupported) if runner else None)
        sess.close()
        LCH.collect(log, out, t_start, sn, bool(a.sn_cmd), bool(a.se_cmd), stamp)
    if rc is not None:
        log.say("S8a session aborted (rc %d); analysing what was collected" % rc)
    res = s8a_analyze(LCH, log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------
# self-test: every part against a recording runner (no game, no section), the groups, the analyzer's and the install check's
# ----------------------------------------------------------------------------------------------------------------

def selftest(LCH, log, a) -> int:
    import argparse
    ok = True
    R = _runner_class(LCH)
    AN = analyzer(LCH)
    IC = install_check(LCH)

    class Rec(R):
        NS_OFFLINE = False

        def __init__(self, a2, sess):   # noqa: D401 - no section, no samplers: calls are recorded
            self.LCH, self.L, self.S = LCH, LCH.L, LCH.S
            self.log, self.a, self.sess, self.out, self.stamp = log, a2, sess, "<out>", "selftest"
            self.q, self.sn, self.se, self.standin_sn, self.standin_se, self.se_alone = True, None, None, False, False, False
            self.stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "unknown": []}
            self.unsupported, self.bg = set(), []
            self.sites = load_sites(None)
            self.holes, self.seabed, self.relaunch_needed, self.relaunched, self.mutants, self.stage_b_used = {}, {}, False, False, [], False
            self.placed = 0
            self.calls = []
            self.tick = 0
            self.status_n = 0

        def alive(self):
            return True

        def wait(self, seconds):
            self.tick += int(seconds * 60)

        def state(self):
            return {"c1Tick": self.tick}

        def call(self, side, cmd, args=None, timeout=None, label=None):
            self.calls.append((side, cmd, args, label))
            args = args or {}
            if side == "se" and cmd == "de.s8a":
                act = args.get("action")
                if act == "orevoxels":
                    return {"ok": True, "surface": 900, "typed": 900, "m30": False}
                if act == "oreoverlay":
                    if args.get("cover"):
                        return {"ok": True, "action": "cover", "octrees": 4, "sampled": 131072, "ore": 300, "above": 0, "face": 0, "cube": 0, "examples": [], "ms": 80.0,
                                "minCoverM": 2.0, "coverOn": True, "bySnType": {"95": 300}}
                    if args.get("vein"):
                        self._veins = getattr(self, "_veins", 0) + 1
                        return {"ok": True, "action": "vein", "count": 120 if self._veins == 1 else 0, "centroid": [-498.5, -60.5, -499.5], "extentM": 3.0, "rows": {"Iron_02": 120}}
                    if args.get("expect"):
                        return {"ok": True, "action": "expect", "types": [0 for _p in args.get("points") or []]}
                    return {"ok": True, "on": True, "queued": 0, "done": 40, "passes": 40, "writes": 6, "painted": 900, "restored": 0, "m31": False}
                if act == "sample":
                    if "where" in args:
                        res = [[u, v, 4158.0 + u, -19563.0 + v, -121.0] for u, v in args["where"]]
                        return {"ok": True, "results": res, "halfM": 320.0, "blendM": 80.0, "rebaseM": -25.0,
                                "camera": [0.0, 0.0, -113.0, -640.0, -88.0, -192.0, 4158.0, -113.0, -19563.0]}
                    if "columns" in args:
                        return {"ok": True, "results": [[x, z, -121.0, -121.0, -121.0, 1.0, -640.0, -192.0] for x, z in args["columns"]], "rebaseM": -25.0}
                    if "serve" in args:
                        return {"ok": True, "results": [{"box": b, "fieldFnv": "0" * 16, "providerFnv": "0" * 16} for b in args["serve"]], "chunkFnv": "0" * 16}
                    return {"ok": True, "state": "ready", "setting": True, "patch": True, "halfM": 320.0, "blendM": 80.0, "reads": 10, "servedOutside": 0,
                            "entityWrites": 3, "entitiesInFile": 900, "cost": [], "vanilla": []}
                if act == "rays":
                    return {"ok": True, "label": args.get("label"), "hits": [{"x": x, "z": z, "storageY": -17.2, "castY": -17.2, "tile": "1,1,1", "castTile": "1,1,1", "material": "Sand_02" if args.get("material") else None} for x, z in args["columns"]]}
                if act == "status":
                    self.status_n += 1
                    return {"ok": True, "feed": {"pending": 0, "ackSeq": 3, "appliedSeq": 3, "nextSeq": 4, "snVersion": 1}, "overlay": {"storeKey": "00c0ffee00c0ffee"},
                            "converter": {"folder": "<store>/00c0ffee00c0ffee/drill", "root": "<store>"}}
                if act == "drill":
                    return {"ok": True, "pending": True, "tile": "1,1,1", "beforeY": -17.2}
                if act == "overlay":
                    return {"ok": True, "cells": 24, "hash": "0123456789abcdef", "overlayGen": 3, "savedHash": "0" * 16}
                if act == "save":
                    return {"ok": True, "world": "<world>", "cells": 24}
                if act == "window":
                    return {"ok": True, "pending": 0, "workerBusy": False, "generated": 40, "on": True, "throttleMs": args.get("throttleMs", 0)}
                if act == "chart":
                    if "treadmill" in args:
                        self.treadmill_rec = bool(args["treadmill"])
                    self.chart_n = getattr(self, "chart_n", 0) + 1
                    x = min(13000.0, 100.0 * self.chart_n)       # the camera's site x advances with every probe (the n8 triggers)
                    return {"ok": True, "chartSeq": 0, "chartIndex": 0, "snChartSeqApplied": 0, "inTransition": False, "zone": "OUTER VOID",
                            "treadmill": getattr(self, "treadmill_rec", False), "camSn": [x, -10.0, 0.0], "camSite": [x, -10.0, 0.0],
                            "entitySn": [x + 30.0, -10.0, 0.0], "entitySite": [x + 30.0, -10.0, 0.0]}
                if act == "step":
                    return {"ok": True, "chartSeq": 1, "line": "de.s8a step: chart 1"}
                if act == "outcrops":
                    if args.get("probe"):
                        return {"ok": True, "records": [{"key": "%016x" % (0xabc000 + j), "ore": "Iron_02", "x": 4000.0 + 20.0 * j, "y": -72.0, "z": 10.0, "exposedM2": 40.0,
                                                         "volumeM3": 320.0 - (100.0 if str(label).startswith("no.drill") and j == 0 else 0.0), "firstVolumeM3": 320.0,
                                                         "scale": 1.6, "drilled": False, "detX": 4000.5 + 20.0 * j, "detY": -76.0, "detZ": 10.0, "zone": "planet",
                                                         "cell": "77:%d,1,1" % j} for j in range(3)]}
                    if args.get("detect"):
                        return {"ok": True, "started": True}
                    if str(label).startswith("nv."):
                        return {"ok": True, "detectPending": 0, "detect": [{"ore": "Iron_02", "x": -500.0 + 9.0 * j, "y": -22.0, "z": -500.0, "body": 88, "cell": [j, 2, 2]}
                                                                           for j in range(3)] + [{"ore": "Iron_02", "x": 4000.0, "y": -76.0, "z": 0.0, "body": 77, "cell": [9, 1, 1]}]}
                    return {"ok": True, "detectPending": 0, "detect": [{"ore": "Iron_02", "x": 4000.5 + 20.0 * j, "y": -76.0, "z": 10.0, "body": 77, "cell": [j, 1, 1]} for j in range(3)],
                            "records": 3, "cells": 40}
                if act == "ring":
                    if args.get("pick"):
                        bios = ("grandReef", "bloodKelp", "mushroomForest", "kelpForest", "dunes")
                        return {"ok": True, "spots": [{"i": k, "x": 4500.0 + 300.0 * k, "z": 600.0 * k, "h": -150.0, "biome": b, "class": "pillar", "size": 32, "scale": 1.0,
                                                       "radius": 16.0} for k, b in enumerate(bios)],
                                "seams": [{"i": 9, "x": 5120.0 + 3.0, "z": 900.0, "h": -160.0, "seamX": 5152.0, "seamZ": 902.0, "slope": 1.4, "biome": "kelpForest", "class": "wall"}],
                                "plain": [{"x": -5200.0, "z": -2100.0, "h": -210.0, "biome": "dunes"}]}
                    if "columns" in args:
                        return {"ok": True, "results": [[x, z, -150.0, -150.0] for x, z in args["columns"]]}
                    if "serve" in args:
                        return {"ok": True, "results": [{"box": b, "fieldFnv": "0" * 16, "providerFnv": "0" * 16} for b in args["serve"]]}
                    if "blend" in args:
                        return {"ok": True, "running": True}
                    if "failAt" in args:
                        return {"ok": True, "failAt": args.get("failAt")}
                    return {"ok": True, "state": "ready", "placements": 15894, "nearestSameBrushM": 300.0, "forbiddenPlacements": 0, "failInjected": 3,
                            "blend": {"running": False, "ok": True, "borderPairs": 300, "innerPairs": 400, "borderP95": 1.0, "innerP95": 6.0}, "cost": []}
                if act == "probe":
                    ore = str(args.get("label") or "").split("n10.ore.")[-1] if str(args.get("label") or "").startswith("n10.ore.") else "Stone_01"
                    rows = [{"x": x, "z": z, "closestY": -100.0, "storageY": -100.2, "waterDepth": -100.0, "material": "Sand_02", "biomeMap": 3,
                             "depths": args.get("depths"), "materials": (["Sand_02"] + [ore] * (len(args.get("depths") or []) - 1)) if args.get("depths") else []}
                            for x, z in args["points"]]
                    return {"ok": True, "rows": rows, "planetDataDir": None, "sculptHash": "0" * 16, "averageRadius": 60000.0, "minimumRadius": 57000.0,
                            "maximumRadius": 67200.0, "rs": 60000.00390625}
                return {"ok": True}
            if side == "se" and cmd == "de.s3" and "place" in args:
                return {"ok": True, "entityId": "123456789", "plate": dict(args["place"].get("plate") or {})}
            if side == "sn" and cmd == "de.s8a":
                if args.get("action") == "rays":
                    self._pending = [{"i": k, "x": c[0], "z": c[1], "yHint": c[2], "state": "ok", "hit": True, "y": -17.2} for k, c in enumerate(args["columns"])]
                    return {"ok": True, "pending": True, "batch": args.get("label"), "columns": len(args["columns"])}
                if args.get("action") == "status":
                    return {"ok": True, "inFlight": 0, "jobs": 0, "chartSeqApplied": 9, "areaBuilt": True}
                if args.get("action") == "deposits":
                    near = ";".join("%016x:DrillableTitanium:%.1f:-72.4:10.0:%.2f" % (0xabc000 + j, 4000.0 + 20.0 * j, 1.36 if str(label).startswith("no.drill") and j == 0 else 1.6)
                                    for j in range(3))
                    rep_ = {"ok": True, "near": near, "liveNear": 3, "depositsLive": 3, "records": 3, "camX": 4000.0, "camY": -60.0, "camZ": 0.0}
                    if args.get("immune"):
                        rep_.update({"immune.ok": True, "immune.hits": args["immune"], "immune.calls": args["immune"], "immune.healthBefore": 600.0, "immune.healthAfter": 600.0})
                    return rep_
                if args.get("action") == "life":
                    return {"ok": True, "life": 60, "flora": 50, "creatures": 10, "other": 3, "ours": 0}
                if args.get("action") == "orelook":
                    return {"ok": True, "result": args.get("as"), "patched": True, "applied": True, "filled": 9, "taken": 0, "texturesReplaced": 18, "template": "GreyRock"}
                if args.get("action") == "oresurface":
                    if args.get("voxels"):
                        return {"ok": True, "voxels": [{"data": True, "type": 84, "density": 140, "solid": True, "ore": False} for _v in args["voxels"]]}
                    rows = [{"surface": 40, "nearestSurfaceM": 2.5, "ore": p[3] if len(p) > 3 else None, "expected": 95, "typed": 12, "nearestTypedM": 2.6, "typeAt": 95,
                             "typedAt": [p[0], p[1] + 2.0, p[2]], "anyOre": 12, "typedList": [[p[0] + j, p[1] + 2.0, p[2]] for j in range(6)] if args.get("list") else None}
                            for p in args.get("points") or []]
                    return {"ok": True, "points": len(rows), "withSurface": len(rows), "withTyped": len(rows), "rows": rows, "voxels": [], "sources": {}}
                if args.get("action") == "sample":
                    return {"ok": True, "radius": args.get("radius"), "records": "flora:40;slot:300", "live": "flora:40;slot:120", "slotRecords": 300,
                            "slotFilled": 120, "slotEmpty": 180, "slotPending": 0, "camNativeX": -640.0, "camNativeY": -88.0, "camNativeZ": -192.0}
                if args.get("action") == "props":
                    if args.get("at"):
                        return {"ok": True, "spawning": 5}
                    hid = 1 if str(label) == "nd.props.after" else 0
                    return {"ok": True, "list": ";".join("%s:%s:%d:-17.2:" % (t, f, hid) for t, f in (("PinkMushroom", "pickflora"), ("ScrapMetal", "pickup"),
                                                                                                      ("LimestoneChunk", "outcrop"), ("CoralShellPlate", "coral"), ("BigCoralTubes", "coral")))}
                if args.get("action") == "pose":
                    return {"ok": True, "haveCam": True, "camX": 0.0, "camY": -10.0, "camZ": 0.0, "entity": str(args.get("entity")), "entityFound": True,
                            "entityX": 30.0, "entityY": -10.0, "entityZ": 0.0, "entityTick": 100, "chartSeqApplied": 9}
                if args.get("action") == "probe":
                    if label == "n10.ore.wall":     # a cavity of radius 4: empty at 3.0 and 3.5 m, the ore wall at 4.0 m
                        self._wall_n = getattr(self, "_wall_n", 0) + 1
                        if self._wall_n % 3:
                            return {"ok": True, "type": 0, "density": 0, "solid": False, "source": "store v2"}
                        return {"ok": True, "type": 160, "density": 150, "solid": True, "source": "store v2"}
                    return {"ok": True, "type": 1, "density": 0, "solid": True, "source": "store v1"}
                return {"ok": True}
            if side == "sn" and cmd == "de.s4" and args.get("status"):
                frm = int(args.get("from") or 0)
                res = getattr(self, "_pending", [])[frm:frm + 64]
                return {"ok": True, "state": "done", "done": len(getattr(self, "_pending", [])), "results": res}
            return {"ok": True}

        def part(self, name, phase):
            self.sess.event("part", name=name, phase=phase, tick=self.tick)

        def owner(self, step, prompt, seconds):
            self.sess.event("owner", step=step, phase="skipped", prompt=prompt)
            return False

        def park(self):
            pass

        def check_save_file(self, t_save):
            self.sess.event("savefile", path="<world>/DE_S8a_overlay.vx2", exists=False, bytes=0, ok=False, error="selftest: no world")

        def store_check(self):
            self.sess.event("store_check", storeKey="00c0ffee00c0ffee", folder="<store>", manifest={"entries": 0, "key": None, "hash": None, "error": "selftest"}, files=[])

    class Mem:
        def __init__(self):
            self.rows = []

        def event(self, ev, **kw):
            self.rows.append(dict(kw, ev=ev))

    a2 = argparse.Namespace(owner=False, quick=True, sites=None, world="(selftest: no world)", throttle_ms=THROTTLE_MS)
    sess = Mem()
    r = Rec(a2, sess)
    r.motion = lambda args, label: r.call("se", "de.motion", dict(args, label=label), label=label)
    online = [p for p in S8A_PARTS if p not in OFFLINE_PARTS]
    for p in online:
        getattr(r, "p_" + p)()
        if p == "n4":
            r.p_n4b()
    begins = [x["name"] for x in sess.rows if x["ev"] == "part" and x["phase"] == "begin"]
    ends = [x["name"] for x in sess.rows if x["ev"] == "part" and x["phase"] == "end"]
    good = sorted(begins) == sorted(ends) and all(p in begins for p in online) and "n4b" in begins
    print("  parts: %d windows, balanced %s, every part ran %s" % (len(begins), sorted(begins) == sorted(ends), all(p in begins for p in online)))
    ok &= good
    drills = [x for x in sess.rows if x["ev"] == "drill" and not x.get("window")]
    good = len(drills) == 6 and [d["i"] for d in drills] == list(range(6)) and sum(1 for d in drills if d["seam"]) == 1 and \
        [d["path"] for d in drills].count("sphere") == 4 and [d["path"] for d in drills].count("hand") == 2 and all(abs(d["sn"][1] + 17.2) < 1e-9 for d in drills)
    print("  drills: %d (4 sphere, 2 hand, 1 seam, at the storage surface): %s" % (len(drills), "PASS" if good else "FAIL"))
    ok &= good
    rays = [x for x in sess.rows if x["ev"] == "rays"]
    kinds = {(x["side"], x["kind"], x["when"]) for x in rays}
    need = {("se", "footprint", "before"), ("se", "n3", "before"), ("se", "rim", "before"), ("se", "seam", "before"), ("sn", "axis", "after"), ("sn", "footprint", "after"),
            ("se", "seam", "after"), ("sn", "seam", "after"), ("se", "n3", "after"), ("sn", "n3", "after"), ("se", "rim", "after"), ("sn", "rim", "after"),
            ("se", "n3", "presave"), ("sn", "n3", "presave"), ("se", "n3", "relaunch"), ("sn", "n3", "relaunch"), ("se", "n3", "reload"), ("sn", "n3", "reload")}
    miss = sorted(need - kinds)
    print("  ray sets present: %s" % ("all" if not miss else "MISSING " + ",".join("%s.%s.%s" % k for k in miss)))
    ok &= not miss
    fp = [x for x in rays if x["side"] == "sn" and x["kind"] == "footprint"]
    good = all(len(x["results"]) == QUICK_FOOTPRINT_N ** 2 and len(x["columns"]) == QUICK_FOOTPRINT_N ** 2 for x in fp) and len(fp) == 6
    print("  footprint rays: %d sites, %d results each (quick): %s" % (len(fp), QUICK_FOOTPRINT_N ** 2, "PASS" if good else "FAIL"))
    ok &= good
    labels = [c[3] for c in r.calls]
    for lab in ("n1.0.camera", "n1.0.drill", "n1.5.drill", "n1.0.status", "after.0.axis.sn", "after.0.footprint.sn", "after.5.seam.se.0", "after.5.seam.sn", "after.0.n3.se.0",
                "after.0.rim.sn", "before.0.footprint.se.0", "n4.overlay", "n4.save", "presave.0.n3.se.0", "relaunch.0.n3.sn", "n5.reload", "n5.status", "reload.0.n3.sn"):
        if lab not in labels:
            print("  missing call label %s" % lab)
            ok = False
    ev_names = {x["ev"] for x in sess.rows}
    for e in ("drill", "applied", "rays", "overlay", "save", "savefile", "store_check", "reloaded"):
        if e not in ev_names:
            print("  missing session event %s" % e)
            ok = False
    good = r.relaunch_needed
    print("  n4 asks for the relaunch: %s" % ("PASS" if good else "FAIL"))
    ok &= good
    # Stage B: the window paths with their rays, the window drill, the forced steps with their chart probes, the throttle, the material probes
    paths = [x["name"] for x in sess.rows if x["ev"] == "path"]
    steps = [x for x in sess.rows if x["ev"] == "chart_step"]
    good = {"n7.path.20", "n7.path.80", "n7.ring.20.0", "n9.path.80", "n10.path.20", "n8.out", "n8.back"} <= set(paths)
    good &= ("se", "path", "n7.path.20") in kinds and ("sn", "path", "n7.path.80") in kinds and ("se", "wfootprint", "before") in kinds and ("sn", "wfootprint", "after") in kinds
    good &= len(steps) == 3 and [s["kind"] for s in steps] == ["open", "terrain", "above"] and all(s["before"] and s["after"] and s["entity"] == "123456789" for s in steps)
    good &= [s["altitude"] for s in steps] == [WINDOW_FALLBACK_Y, WINDOW_FALLBACK_Y, TREADMILL_ABOVE_Y] and all(s["pictureBefore"] and s["pictureAfter"] for s in steps)
    good &= sum(1 for c in r.calls if str(c[3]).endswith(".mirrored")) >= 3 and any(c[3] == "n8.step.above.up" for c in r.calls)
    good &= all(s["snBefore"]["entityFound"] and s["snAfter"]["entityFound"] and s["snApplied"] == 9 for s in steps)
    holds = [c for c in r.calls if c[1] == "de.motion" and c[2].get("kind") == "stop" and str(c[3]).startswith("n8.")]
    good &= len(holds) == 3 and any(c[3] == "n8.step.open.resume" for c in r.calls) and any(c[3] == "n8.step.terrain.sn.before" for c in r.calls)
    good &= any(x["ev"] == "throttle" and x["ms"] == THROTTLE_MS for x in sess.rows) and any(x["ev"] == "material_probe" and len(x["columns"]) + sum((x.get("skipped") or {}).values()) == QUICK_N10_COLUMNS for x in sess.rows)
    good &= any(d["i"] == WINDOW_DRILL_SITE and d.get("window") for d in drills + [x for x in sess.rows if x["ev"] == "drill"])
    good &= any(c[3] == "teardown.treadmill" for c in r.calls) and any(c[3] == "teardown.spawns" for c in r.calls) and any(c[3] == "n10.spawns.on" for c in r.calls)
    print("  Stage B parts (paths, rays, window drill, forced steps, throttle, material probes, teardown): %s" % ("PASS" if good else "FAIL"))
    ok &= good
    # Stage C: c2g probes the 20 points and records the tool's expectation from the files; n10 probes the ore deposits on both sides
    c2g = [x for x in sess.rows if x["ev"] == "c2g"]
    ore = [x for x in sess.rows if x["ev"] == "ore_probe"]
    good = len(c2g) == 1 and len(c2g[0]["points"]) == 20 and isinstance(c2g[0]["expected"], list) and len(c2g[0]["expected"]) == 20 and c2g[0]["probe"]["ok"]
    good &= len(ore) == 1 and ore[0]["n"] >= 1 and all(r["seMaterials"] and r["snInterior"] for r in ore[0]["rows"])
    good &= any(c[3] == "c2g.probe" and c[2].get("planet") is True and len(c[2]["points"]) == 20 for c in r.calls) and any(c[3] == "n10.ore.sn" for c in r.calls)
    good &= ore[0]["drills"] == QUICK_ORE_DRILLS and any(c[3] == "n10.ore.drill" for c in r.calls) and sum(1 for c in r.calls if c[3] == "n10.ore.wall") == 12 * QUICK_ORE_DRILLS
    good &= all(w.get("radius") == WALL_STEPS_M[2] for rr in ore[0]["rows"] if rr.get("wall") for w in rr["wall"])
    good &= any(x["ev"] == "applied" and x.get("phase") == "settle" for x in sess.rows)
    mp = [x for x in sess.rows if x["ev"] == "material_probe"]
    good &= len(mp) == 1 and all(c["seMaterial"] for c in mp[0]["columns"]) and all(-2048 > c["x"] or c["x"] >= 2112 for c in mp[0]["columns"])
    print("  Stage C parts (c2g: 20 probe points + the tool's expectation; n10 ore probes on both sides): %s" % ("PASS" if good else "FAIL"))
    # Stage E: n10c visits the crater spots and asks de.s8a ores; n11 re-arms the beacon and reads its status twice
    co = [x for x in sess.rows if x["ev"] == "crater_ores"]
    bc = [x for x in sess.rows if x["ev"] == "beacon"]
    good_e = len(co) == 1 and len(bc) == 1 and any(c[3] == "n10c.ores" and c[2].get("action") == "ores" and c[2].get("count") == QUICK_CRATER_ORE_CUBES for c in r.calls)
    good_e &= sum(1 for c in r.calls if str(c[3]).startswith("n10c.spot.")) == 2 and any(c[3] == "n11.rearm" and c[2] == {"action": "beacon"} for c in r.calls)
    good_e &= any(c[3] == "n11.status.2" and c[2].get("status") is True for c in r.calls)
    print("  Stage E parts (n10c: crater spots + de.s8a ores; n11: beacon re-arm + two status reads): %s" % ("PASS" if good_e else "FAIL"))
    ok &= good_e
    # Stage D: nd runs two passes (crater, planet): cellvis on, the drill pass, cellvis off, the round trip per drilled batch
    dp = [x for x in sess.rows if x["ev"] == "drill_pass"]
    good_d = len(dp) == 2 and [x["name"] for x in dp] == ["crater", "planet"] and all(x["batches"] for x in dp)
    good_d &= sum(1 for c in r.calls if c[3] in ("nd.crater.pass", "nd.planet.pass") and c[2].get("action") == "drillpass" and c[2].get("everyMs") == ND_EVERY_MS) == 2
    good_d &= any(c[3] == "nd.crater.verify" and c[2].get("octrees") is True for c in r.calls) and any(c[3] == "nd.planet.cellvis.on" for c in r.calls)
    # nd rerun: the camera held at each pass's spot (bob, amplitude 0) and released after it; Subnautica's area built first
    nd_holds = [c for c in r.calls if c[1] == "de.motion" and str(c[3]).startswith("nd.")]
    good_d &= [c[2].get("kind") for c in nd_holds] == ["bob", "stop", "bob", "stop"] and all(c[2].get("amplitudeM") == 0.0 for c in nd_holds if c[2].get("kind") == "bob")
    good_d &= all(isinstance(x.get("camSn"), list) for x in dp) and sum(1 for x in sess.rows if x["ev"] == "sn_built" and str(x.get("label")).startswith("nd.")) == 2
    print("  Stage D part (nd: two drill passes, per-frame cell rows, the octree round trip): %s" % ("PASS" if good_d else "FAIL"))
    ok &= good_d
    # Stage F2: nf: ring paths (2 quick), a seam, the blend probe read back, the serve boxes of the install vectors, the fallback with
 # S8a.M15 switched on then off around it, a drill pass on a stamp; the SE env carries DEEPENGINEERS_S8A_RING only for nf
    ev = lambda name: [x for x in sess.rows if x["ev"] == name]   # noqa: E731
    good_f = len(ev("nf_path")) == QUICK_NF_PATHS and len(ev("nf_seam")) == QUICK_NF_SEAMS and len(ev("nf_blend")) == 1 and (ev("nf_blend")[0].get("blend") or {}).get("ok")
    good_f &= len(ev("nf_serve")) == 1 and len((ev("nf_serve")[0].get("vectors") or {}).get("serve") or []) >= 3
    fb = ev("nf_fallback")
    good_f &= len(fb) == 1 and fb[0].get("spot") is not None
    m15 = [c for c in r.calls if c[1] == "de.mutant" and c[2].get("name") == "S8a.M15"]
    good_f &= [c[2].get("on") for c in m15] == [True, False]
    good_f &= len(ev("nf_drill")) == 1 and any(c[3] == "nf.drill.pass" and c[2].get("action") == "drillpass" for c in r.calls)
    good_f &= any(c[3] == "nf.path.0.400.path.field" and c[2].get("action") == "ring" and c[2].get("columns") for c in r.calls)
    good_f &= any(x.get("kind") == "seam" and x.get("side") == "sn" for x in ev("rays"))
    _sn, se_nf = env_plan([], ["nf", "teardown"])
    _sn, se_n7 = env_plan([], ["n7", "teardown"])
    good_f &= se_nf.get("DEEPENGINEERS_S8A_RING") == "on" and "DEEPENGINEERS_S8A_RING" not in se_n7
    print("  Stage F2 part (nf: ring paths, seams, blend, served bytes, the S8a.M15 fallback, a drill pass; the ring env only for nf): %s" % ("PASS" if good_f else "FAIL"))
    ok &= good_f
    # planet v2 route B: ns: ready, the treadmill off for the teleport and on after it, a line in segments and two seams (quick) with SE,
 # Subnautica and the sample's own witnesses, two spots with Subnautica's census, the serve boxes of the install vectors; the sample env
 # for ns and nl only
    good_s = len(ev("ns_ready")) == 1 and len(ev("ns_line")) >= 2 and len(ev("ns_seam")) == 2 and len(ev("ns_entities")) == 2
    good_s &= all((x.get("census") or {}).get("records") for x in ev("ns_entities")) and len(ev("ns_serve")) == 1
    good_s &= any(x.get("kind") == "seam" and x.get("side") == "sn" and str(x.get("when")).startswith("ns.seam") for x in ev("rays"))
    good_s &= sum(1 for x in ev("ns_field") if x.get("results")) == len(ev("ns_line")) + len(ev("ns_seam"))
    tm = [c[2].get("treadmill") for c in r.calls if str(c[3]).startswith("ns.treadmill")]
    good_s &= tm[:2] == [False, True]
    _sn, se_ns = env_plan([], ["ns", "teardown"])
    _sn, se_nl = env_plan([], ["nl", "teardown"])
    good_s &= se_ns.get("DEEPENGINEERS_S8A_SAMPLE") == "on" and se_nl.get("DEEPENGINEERS_S8A_SAMPLE") == "on" and "DEEPENGINEERS_S8A_SAMPLE" not in se_n7
    good_s &= any(x.get("source") == "sample" for x in ev("look_points"))
    print("  sample part (ns: lines, seams, spots with the census, served bytes; the sample env for ns and nl only; nl looks at the sample): %s" % ("PASS" if good_s else "FAIL"))
    ok &= good_s
    # planet v2 R8 / R9: no: per ore site the records, SE's detector, Subnautica's deposits, one drill and the immune hits; nd's props
    nos = ev("no_site")
    good_o = len(nos) >= 1 and all(x.get("records") and x.get("detect") and (x.get("deposits") or {}).get("near") for x in nos)
    good_o &= sum(1 for x in nos if x.get("drill")) == 1 and any((x.get("immune") or {}).get("hits") == NO_IMMUNE_HITS for x in nos)
    good_o &= [x.get("phase") for x in ev("nd_props")] == ["before", "after"]
    # ores as voxels: at the drilled vein the props off, captures as ours, as the rock and as each candidate, our look back, the props on;
 # the converters' census at the end; the props env only for no
    ol = ev("ore_look")
    good_o &= len(ol) == 1 and [c.get("as") for c in ol[0].get("captures") or []] == ["ours", str(ORE_LOOK_ROCK)] + [str(t) for t in ORE_LOOK_CANDIDATES]
    good_o &= [c[2].get("on") for c in r.calls if str(c[3]).startswith("no.orelook.props")] == [False, True] and len(ev("ore_voxels")) == 1
    _sn_no, _se_no = env_plan([], ["no", "teardown"])
    _sn_n7, _se_n7b = env_plan([], ["n7", "teardown"])
    good_o &= _sn_no.get("DEEPENGINEERS_S8A_ORE_PROPS") == "on" and "DEEPENGINEERS_S8A_ORE_PROPS" not in _sn_n7
    dens = ev("no_density")
    good_o &= len(dens) in (QUICK_NO_DENSITY_CRATER + QUICK_NO_DENSITY_PLANET, NO_DENSITY_CRATER + NO_DENSITY_PLANET)
    good_o &= all(isinstance(x.get("detect"), list) and x.get("radius") == NO_DENSITY_R for x in dens)
    good_o &= sum(1 for x in dens if x.get("zone") == "crater") in (QUICK_NO_DENSITY_CRATER, NO_DENSITY_CRATER)
    print("  planet v2 parts (no: %d ore sites, records, SE's detector, Subnautica's deposits, a drill, the immune hits, %d density points; nd's test props): %s" % (
        len(nos), len(dens), "PASS" if good_o else "FAIL"))
    ok &= good_o
    # planet v2: nl: SE's noon then the clock back, the spawn at 3 headings x 2 heights, 2 heights per far point (the deployed files'
 # biome map: at least 6 points), pitch -15 by de.look, each view held and released; the orbit shots asked for
    caps = ev("look_capture")
    pts = (ev("look_points") or [{}])[0].get("points") or []
    # planet v2 route B: nl looks at the sample's spots (and one design-3 Grassy Plateaus point): the last look_points event
    fpts = (ev("look_points") or [{}])[-1].get("points") or []
    # a test run: only lit far points (design 3's repository files: the void and the deep plains left out), the treadmill off then on
 # round each far teleport and restored at the end, each capture's chart, picture and window state recorded
    good_l = len(pts) >= 3 and all(p.get("biome") not in NL_EXCLUDE_BIOMES and p.get("seabedY", 0) >= -NL_LIT_DEPTH_M for p in pts)
    good_l &= len([c for c in caps if c.get("kind") == "spawn"]) == len(NL_SPAWN_HEADINGS) * len(NL_HEIGHTS)
    tm = [(c[3], c[2].get("treadmill")) for c in r.calls if c[1] == "de.s8a" and str(c[3]).startswith("nl.treadmill")]
    nfar = len([c for c in caps if c.get("kind") == "far"])
    good_l &= len([t for t in tm if t[1] is False and ".off." in t[0]]) == len(fpts) and len([t for t in tm if t[1] is True]) == len(fpts) and tm[-1][0] == "nl.treadmill.restore"
    good_l &= sum(1 for c in caps if c.get("sameSpot")) == nfar - len(fpts)
    good_l &= all("tries" in c and "chart" in c and "window" in c and (c.get("life") or {}).get("life") == 60 for c in caps)
    good_l &= len([c for c in caps if c.get("kind") == "far"]) == len(fpts) * len(NL_HEIGHTS)
    good_l &= any(str(q.get("biome")).startswith("sample:") for q in fpts) and any(str(q.get("biome")).startswith("design3:") for q in fpts)
    good_l &= [c[2].get("time") for c in r.calls if c[1] == "de.s7" and str(c[3]).startswith("nl.") and "time" in (c[2] or {})] == ["off", "off"]
    good_l &= [c[2].get("clock") for c in r.calls if c[1] == "de.s15" and str(c[3]) == "nl.noon"] == [0.5]
    good_l &= all(c[2].get("dir") == "pitch-15" for c in r.calls if c[1] == "de.look" and str(c[3]).startswith("nl."))
    holds = [c[2].get("kind") for c in r.calls if c[1] == "de.motion" and str(c[3]).startswith("nl.")]
    good_l &= holds == ["bob", "stop"] * len(caps) and len(ev("look_orbit")) == len(NL_ORBIT_KM)
    if not good_l:
        print("    nl detail: pts %d %s; spawn %d; far %d; treadmill %s; s7 %s; holds %s; orbit %d; capture keys %s" % (
            len(pts), [(p.get("biome"), p.get("seabedY")) for p in pts], len([c for c in caps if c.get("kind") == "spawn"]), nfar, tm[-3:],
            [c[2].get("time") for c in r.calls if c[1] == "de.s7" and str(c[3]).startswith("nl.")], holds[:6], len(ev("look_orbit")), sorted((caps or [{}])[0].keys())))
    print("  planet v2 part (nl: noon, the spawn and %d far points at 8 and 25 m, held, pitch -15, the orbit shots): %s" % (len(pts), "PASS" if good_l else "FAIL"))
    if not good_l:
        print("    nl detail: caps %d (spawn %d, far %d), de.s7 %s, looks %s, holds %d of %d, orbit %d" % (
            len(caps), len([c for c in caps if c.get("kind") == "spawn"]), len([c for c in caps if c.get("kind") == "far"]),
            [c[2].get("time") for c in r.calls if c[1] == "de.s7"], sorted({c[2].get("dir") for c in r.calls if c[1] == "de.look" and str(c[3]).startswith("nl.")}),
            len(holds), 2 * len(caps), len(ev("look_orbit"))))
    ok &= good_l
    ok &= good
    # resolve, env, worlds
    good = set(MUTANT_GROUPS) == set(AN.MUTANT_RED) == set(ALL_MUTANTS)
    print("  mutant groups = analyzer red sets = known mutants: %s" % good)
    ok &= good
    for m in ALL_MUTANTS:
        ns = argparse.Namespace(mutant=m, parts="", world=None, treadmill=False, sites=None)
        parts, mutants, world, treadmill, probs = resolve(ns)
        want_world = MUTANT_GROUPS[m].get("world") or (PART_WORLD.get(parts[0]) if parts and parts[0] in PART_WORLD else None)
        if probs or world != want_world or parts != list(MUTANT_GROUPS[m]["parts"]) or treadmill != bool(MUTANT_GROUPS[m].get("treadmill")):
            print("  %s resolves to %s %s %s %s" % (m, parts, world, treadmill, probs))
            ok = False
    ns = argparse.Namespace(mutant="", parts="n1,n4", world=None, treadmill=False, sites=None)
    good = bool(resolve(ns)[4]) is False and resolve(ns)[2] == PART_WORLD["n4"]
    print("  n1 + n4 run on the save copy: %s" % ("PASS" if good else "FAIL"))
    ok &= good
    good = True
    sn_env, se_env = env_plan(["S8a.M4"], ["n5"])
    good &= sn_env.get("DEEPENGINEERS_MUTANT") == "S8a.M4" and se_env["DEEPENGINEERS_S8A"] == "on" and se_env["DEEPENGINEERS_PICTURE"] == "site" and "DEEPENGINEERS_S8A_TREADMILL" not in se_env
    sn_env, se_env = env_plan(["S8a.M8"], ["n8"], True)
    good &= se_env.get("DEEPENGINEERS_MUTANTS") == "S8a.M8" and se_env["DEEPENGINEERS_S8A_TREADMILL"] == "on"
    sn_env, se_env = env_plan(["S8a.M13"], ["c1"])
    good &= se_env.get("DEEPENGINEERS_MUTANTS") == "S8a.M13"
    ws = {"world": "DE Lab S8a", "folder": "x", "pointed": True, "autosaveMinutes": 0, "marked": True, "worldMod": True}
    good &= not world_problems(ws, ["n1", "n2"], False)
    good &= any("DE Lab S8aSave" in p for p in world_problems(ws, ["n4"], False))
    good &= any("autosave" in p for p in world_problems(dict(ws, autosaveMinutes=5), ["n1"], False))
    good &= any("lab world itself" in p for p in world_problems(dict(ws, world="DE Lab"), ["n1"], False))
    good &= any("marker" in p for p in world_problems(dict(ws, world="DE Lab S8aSave", marked=False), ["n4"], False))
    good &= any("marker" in p for p in world_problems(dict(ws, marked=False), ["n1"], False)) and not world_problems(dict(ws, marked=False), ["c1", "c3"], False)
    import tempfile as _tf
    d = _tf.mkdtemp(prefix="s8a-marker-")
    good &= not throwaway_marked(d)
    os.makedirs(os.path.join(d, "DE_THROWAWAY"))
    good &= throwaway_marked(d)
    print("  env plans and world checks: %s" % ("PASS" if good else "FAIL"))
    ok &= good
    # columns: the inner set lies within 0.7 r of the axis (inside the 4 m overlap at the seam site); columns are rounded to 1 mm
    s5 = load_sites(None)[5]
    inner = columns_of(s5, "n3", False, RADIUS_M * 1.0003)
    good = len(inner) == N3_N ** 2 and all(math.hypot(x - s5["x"], z - s5["z"]) <= 0.7 * RADIUS_M * 1.0003 + 2e-3 for x, z in inner) and all(abs(x) < 2.0 for x, _z in inner)
    rim = columns_of(s5, "rim", False)
    good &= len(rim) == RIM_N and all(1.5 * RADIUS_M - 2e-3 <= math.hypot(x - s5["x"], z - s5["z"]) <= 3.0 * RADIUS_M + 2e-3 for x, z in rim)
    good &= abs(horizontal_scale(-17.2) - RS / (RS - 17.2)) < 1e-12
    print("  column sets (inner within 0.7 r and the seam overlap, rim 1.5-3 r): %s" % ("PASS" if good else "FAIL"))
    ok &= good
    rc = AN.selftest()
    ok &= rc == 0
    rc = IC.selftest()
    ok &= rc == 0
    print("launcher s8a selftest %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

# ----------------------------------------------------------------------------------------------------------------
# argparse and dispatch (called by launcher.py)
# ----------------------------------------------------------------------------------------------------------------

def add_parsers(sub, LCH):
    p = sub.add_parser("s8a", help="one S8a terrain-converter session: offline c1/c3, or both games on a throwaway copy for the "
                                   "drilling parts n1-n6 (Stage A); the S8a oracle table")
    p.add_argument("--parts", default="", help="comma list of %s (default: %s; with --mutant: its parts)" % (",".join(S8A_PARTS), ",".join(DEFAULT_PARTS)))
    p.add_argument("--mutant", default="", help="one seeded failure: %s" % ",".join(ALL_MUTANTS))
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--selftest", action="store_true", help="every part against a recording runner, the groups, the analyzer's and the install check's selftests")
    p.add_argument("--world", default=None, help="the SE world copy LastSession points at (default %r; n4 %r)" % (LAB_WORLD, PART_WORLD["n4"]))
    p.add_argument("--owner", action="store_true", help="wait for the owner's steps (the Esc -> Save fallback of n4)")
    p.add_argument("--treadmill", action="store_true", help="DEEPENGINEERS_S8A_TREADMILL=on (Stage B: n8)")
    p.add_argument("--throttleMs", dest="throttle_ms", type=int, default=THROTTLE_MS, help="n9: the window worker's sleep per batch (default %d)" % THROTTLE_MS)
    p.add_argument("--seed", type=int, default=None, help="c2: the detail seed to verify against (default: the layout's, 4546)")
    p.add_argument("--sites", default=None, metavar="FILE", help="JSON list of drill sites [{x, z, path, seam}] or [[x, z], ...] (SN metres; default: six Safe Shallows sites, the sixth on the x = 0 seam)")
    p.add_argument("--c1-files", type=int, default=100, help="c1: batch files through the round trip (>= 100 for S8a.C1)")
    p.add_argument("--no-python-roundtrip", action="store_true", help="c1: skip the Python twin (about a minute)")
    p.add_argument("--quick", action="store_true", help="smaller ray sets, short holds (smoke runs)")
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--host-slot", default="slot0000", help="Subnautica host slot hashed before/after (S8a.N6.hostsave, S1.N4)")
    p.add_argument("--host-slot-dir", default=None, help="explicit folder to hash (stand-in tests)")
    p.add_argument("--dry-run", action="store_true", help="print the plan (parts, sites, environment, world); start nothing")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in plumbing only)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (stand-in plumbing only)")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--out", default=None)
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S8a oracles on a collected folder")

def run(LCH, log, a) -> int:
    if a.cmd == "s8a":
        return s8a(LCH, log, a)
    return 2
