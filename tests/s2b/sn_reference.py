"""S2b: Subnautica's underwater movement, oxygen and pressure numbers, as a table (stdlib only).

Every number the S2b swim model uses (src/DeepEngineers.Wire/SwimModel.cs, tests/s2b/swim_replay.py, CONTRACT §2.10)
is a row here: name, value, unit, source, label, and how
the model uses it. The numbers were READ from the decompiled game and from the player's own scene file; no game code is
copied (the model is a clean reimplementation written from these rows).

Two sources, and why both:
 - the decompiled Assembly-CSharp gives the rules and the C# field initializers;
 - Unity overwrites a serialized field's initializer with the value saved in the scene. The Player object of the Main
 scene (Steam build 20241558, game 1.22.83031) holds other swim speeds than the initializers (7.6/7.6/7.6/6.3 m/s
 instead of 6.64/6.64/6.64/6.6). The model uses the scene values, because those are what the game runs with.
 `check-scene` re-reads them from the bundle (read-only, stdlib UnityFS/SerializedFile reader, no type trees).

Labels: VERIFIED-decomp, VERIFIED-scene (serialized value read from the scene),
VERIFIED-unity, REPORTED.

 python -I tests/s2b/sn_reference.py table # the markdown table (CONTRACT §2.10)
 python -I tests/s2b/sn_reference.py derived # the numbers the model implies (top speed, stop distance...)
 python -I tests/s2b/sn_reference.py check [--decomp DIR] # every cited decomp line still holds its token
 python -I tests/s2b/sn_reference.py coverage # every branch of the swim motor's rules and how the model
 # handles it
 python -I tests/s2b/sn_reference.py check-scene [--bundle FILE] # the scene values (reads the game file, read-only)
 python -I tests/s2b/sn_reference.py --selftest # internal consistency of the table and the derived numbers
Exit code 0 = PASS, 1 = FAIL, 2 = usage error or a source missing.
"""
from __future__ import annotations

import argparse
import glob
import math
import os
import re
import struct
import sys

# ------------------------------------------------------------------------------------------------ time base
SE_TICK_DT = 1.0 / 60.0          # SE simulation step (CONTRACT §2.1)
SE_TICKS_PER_SECOND = 60
SN_FIXED_DT = 0.1                # Subnautica Time.fixedDeltaTime for a free diver
SN_STEP_TICKS = 6                # one Subnautica motor step every 6 SE ticks (0.1 s = 6 x 1/60 s)

# ------------------------------------------------------------------------------------------------ motor (scene values)
DIVE_FORWARD_MAX = 7.6           # m/s, motor mode Dive (eye below the surface)
DIVE_BACKWARD_MAX = 7.6
DIVE_STRAFE_MAX = 7.6
DIVE_VERTICAL_MAX = 6.3          # also the vertical maximum in Run mode (not changed by SetMotorMode for Walk/Run)
RUN_FORWARD_MAX = 3.5            # m/s, motor mode Run (swim latch on, eye at or above the surface)
RUN_BACKWARD_MAX = 5.0
RUN_STRAFE_MAX = 5.0
WATER_ACCELERATION = 20.0        # m/s^2
GROUND_ACCELERATION = 45.0       # m/s^2 while the diver touches something (UnderwaterMotor grounded = any contact)
SWIM_DRAG = 2.5                  # 1/s, Rigidbody.drag while swimming
UNDERWATER_GRAVITY = 0.0         # m/s^2 inside the swim motor
PLAYER_SPEED_MODIFIER = 1.0      # UnderwaterMotor.playerSpeedModifier (scene 1)
SURFACE_SPEED_MULTIPLIER = 1.3   # max speed (and acceleration through the ratio) while the eye is above the surface
HELD_TOOL_PENALTY = 1.0          # m/s off the max speed while a tool is held
MIN_MAX_SPEED = 2.0              # floor after the tank penalties only (no tank in SE: inert)
ENTRY_VERTICAL_FACTOR = 0.5      # vertical velocity kept each time the swim motor is (re-)enabled: latch entry and
# every Dive<->Run motor switch (the eye crossing 0) while the latch stays on
SWIM_CAPSULE_HEIGHT = 0.5        # PlayerController.swimheight (scene 0.5): NOT modelled (SE keeps the upright engineer)
CAMERA_OFFSET = -0.25            # PlayerController.cameraOffset (scene): the capsule reaches 0.25 m above the eye
SURFACE_TOP_Y = 0.8 - 0.28       # 0.52 m: the eye height where the upward swim speed reaches 0
SURFACE_BAND_BOTTOM_Y = -0.5     # below this eye height the surface rule does not apply
SURFACE_EXPONENT = 0.3
LOOK_DOWN_Y = -0.3               # rotated horizontal input with y below this = swimming down: no surface rule
NORMALIZE_EPS = 1e-5             # Unity Vector3.Normalize: zero vector at or below this magnitude
SPRINT_FACTOR = 1.0              # Subnautica's swim motor has no sprint
FINS_MAX_SPEED_ADJUST = 1.9      # S5a: Fins, +1.9 m/s on the max speed
ULTRAGLIDE_FINS_MAX_SPEED_ADJUST = 3.2   # S5a: Ultraglide fins, +3.2 m/s
FINS_MODIFIERS = {"off": 0.0, "basic": FINS_MAX_SPEED_ADJUST, "ultra": ULTRAGLIDE_FINS_MAX_SPEED_ADJUST}

# ------------------------------------------------------------------------------------------------ water rules (eye = player root = camera pivot)
UNDERWATER_EYE_Y = 0.0           # isUnderwater: eye < 0 (Dive motor numbers, breathing stops)
SWIM_ENTER_EYE_Y = -0.1          # swim latch on: eye < -0.1
SWIM_EXIT_EYE_Y = 0.8            # swim latch off: eye >= +0.8...
SWIM_EXIT_GROUNDED_EYE_Y = 0.1   #... or >= +0.1 when standing on something (grounded test only above +0.1)
JETPACK_ON_EYE_Y = 0.0
JETPACK_OFF_EYE_Y = -0.1         #... and withdrawn when the eye is back at the swim-entry depth (hysteresis band)

# ------------------------------------------------------------------------------------------------ oxygen (units = seconds of breath)
OXYGEN_CAPACITY = 45.0           # the diver's own Oxygen component (no tank)
OXYGEN_REFILL_PER_S = 30.0       # OxygenManager surface refill
OXYGEN_REFILL_EYE_Y = -1.0       # refill while the eye is above ocean level - 1 (strict)
SURFACE_DEPTH = 0.1              # depth class Safe beyond 0.1 m
UNSAFE_DEPTH = 100.0
CRUSH_DEPTH_CLASS = 200.0        # depth class "Crush": breathing only (no damage to the diver)
BREATH_PERIOD_S = {1: 3.0, 2: 2.25, 3: 1.5}      # depth class -> breath period
BREATH_FACTOR = {1: 1.0, 2: 1.5, 3: 2.0}         # depth class -> oxygen per breath / period
SURFACE_BREATH_PERIOD_S = 99999.0                # class Surface (eye within 0.1 m under the surface): never in practice
DC_SURFACE, DC_SAFE, DC_UNSAFE, DC_CRUSH = 0, 1, 2, 3

# ------------------------------------------------------------------------------------------------ pressure
DIVER_PRESSURE_DAMAGE = 0.0      # the diver takes none (Player.CrushDamageUpdate is never invoked)
VEHICLE_BASE_CRUSH_DEPTH = 100.0  # CrushDamage initializers (vehicle prefabs not read): S5 reference only
VEHICLE_DAMAGE_PER_CRUSH = 20.0
VEHICLE_CRUSH_PERIOD_S = 3.0

# ------------------------------------------------------------------------------------------------ seeded failures
MUTANT_M1_SCALE = 1.5            # S2b.M1: max speeds and acceleration x1.5
MUTANT_M3_EYE_Y = 0.35           # S2b.M3: one swim threshold, the centre of the -0.1.. +0.8 band; jetpack at 0
MUT_M1, MUT_M2, MUT_M3, MUT_M4 = 1, 2, 4, 8

UW = "UnderwaterMotor.cs"
PC = "PlayerController.cs"
PL = "Player.cs"
SCENE = "scene Main, PlayerController (pathID 527)"
SCENE_UM = "scene Main, UnderwaterMotor (pathID 403)"
SCENE_OX = "scene Main, Oxygen (pathID 405)"

# name, value, unit, source, token (on the cited line; for `check`), label, use
ROWS = [
    # time base
    ("fixedDeltaTime (free diver)", SN_FIXED_DT, "s", "MainGameController.cs:396", "fixedDeltaTime = 0.1f",
     "VERIFIED-decomp", "the motor step: one Subnautica step every 6 SE ticks (0.02 s only with a registered "
     "high-precision behaviour: Player.cs:2755-2770 = moving sub, GrapplingHook, PropulsionCannon)"),
    ("high-precision timestep", 0.02, "s", "MainGameController.cs:400", "fixedDeltaTime = 0.02f", "VERIFIED-decomp",
     "not used: never registered for a swimming diver"),
    # motor
    ("swimForwardMaxSpeed", DIVE_FORWARD_MAX, "m/s", SCENE + "; initializer 6.64 at PlayerController.cs:54",
     "swimForwardMaxSpeed = 6.64f", "VERIFIED-scene", "Dive max speed with forward input"),
    ("swimBackwardMaxSpeed", DIVE_BACKWARD_MAX, "m/s", SCENE + "; initializer 6.64 at PlayerController.cs:56",
     "swimBackwardMaxSpeed = 6.64f", "VERIFIED-scene", "Dive max speed with backward input"),
    ("swimStrafeMaxSpeed", DIVE_STRAFE_MAX, "m/s", SCENE + "; initializer 6.64 at PlayerController.cs:58",
     "swimStrafeMaxSpeed = 6.64f", "VERIFIED-scene", "Dive max speed with strafe input"),
    ("swimVerticalMaxSpeed", DIVE_VERTICAL_MAX, "m/s", SCENE + "; initializer 6.6 at PlayerController.cs:60",
     "swimVerticalMaxSpeed = 6.6f", "VERIFIED-scene", "floor of the max speed in every mode (UnderwaterMotor.cs:214)"),
    ("swimWaterAcceleration", WATER_ACCELERATION, "m/s^2", SCENE + "; PlayerController.cs:62",
     "swimWaterAcceleration = 20f", "VERIFIED-scene", "velocity added per step: input x 20 x 0.1 s"),
    ("defaultSwimDrag", SWIM_DRAG, "1/s", SCENE + "; PlayerController.cs:42", "defaultSwimDrag = 2.5f",
     "VERIFIED-scene", "Rigidbody.drag while swimming (set by SetMotorMode, PlayerController.cs:210/237)"),
    ("groundAcceleration (contact)", GROUND_ACCELERATION, "m/s^2", SCENE_UM + " word 24; initializer at PlayerMotor.cs:50",
     "groundAcceleration = 45f", "VERIFIED-scene",
     "the acceleration while grounded (UnderwaterMotor.cs:233-242), and grounded = any collision contact of the last "
     "physics step (OnCollisionStay UnderwaterMotor.cs:344-372, reset :314); not overwritten by SetMotorMode. Model "
     "input: SE's character support (walls are not seen, CONTRACT §2.10 'Not modelled')"),
    ("walkRunForwardMaxSpeed", RUN_FORWARD_MAX, "m/s", SCENE + "; PlayerController.cs:76",
     "walkRunForwardMaxSpeed = 3.5f", "VERIFIED-scene", "Run mode (eye >= 0): forward maximum (below the vertical one)"),
    ("walkRunBackwardMaxSpeed", RUN_BACKWARD_MAX, "m/s", SCENE + "; PlayerController.cs:78",
     "walkRunBackwardMaxSpeed = 5f", "VERIFIED-scene", "Run mode backward maximum"),
    ("walkRunStrafeMaxSpeed", RUN_STRAFE_MAX, "m/s", SCENE + "; PlayerController.cs:80",
     "walkRunStrafeMaxSpeed = 5f", "VERIFIED-scene", "Run mode strafe maximum"),
    ("Walk/Run keep the swim vertical maximum", DIVE_VERTICAL_MAX, "m/s", "PlayerController.cs:223",
     "case Player.MotorMode.Walk:", "VERIFIED-decomp", "the Walk/Run case sets forward/backward/strafe only"),
    ("underWaterGravity", UNDERWATER_GRAVITY, "m/s^2", "PlayerController.cs:209", "float underWaterGravity = 0f",
     "VERIFIED-decomp", "no gravity in the swim motor (every motor mode)"),
    ("playerSpeedModifier", PLAYER_SPEED_MODIFIER, "", SCENE_UM + "; UnderwaterMotor.cs:16",
     "playerSpeedModifier = 1f", "VERIFIED-scene", "acceleration and max-speed multiplier (1)"),
    ("finsMaxSpeedAdjust", FINS_MAX_SPEED_ADJUST, "m/s", UW + ":52", "finsMaxSpeedAdjust = 1.9f", "VERIFIED-decomp",
     "S5a fins: added to the selected max speed after the tank/suit penalties, before the tool -1 and the surface x1.3 "
     "(UnderwaterMotor.cs:157-164); the acceleration follows through the ratio (:216-217, 244-247)"),
    ("ultraGlideFinsMaxSpeedAdjust", ULTRAGLIDE_FINS_MAX_SPEED_ADJUST, "m/s", UW + ":54",
     "ultraGlideFinsMaxSpeedAdjust = 3.2f", "VERIFIED-decomp", "S5a Ultraglide fins (the owner's default): same rule"),
    ("motor mode Dive while isUnderwater", 0.0, "m", "Player.cs:2266", "else if (isUnderwater.value)",
     "VERIFIED-decomp", "Dive numbers below the surface (eye < 0), Run numbers above (Player.cs:2289)"),
    ("max speed = max(applicable maxima, vertical)", DIVE_VERTICAL_MAX, "m/s", UW + ":214",
     "a = Mathf.Max(a, verticalMaxSpeed);", "VERIFIED-decomp",
     "forward (z>0) or backward (z<0), then max with strafe (x!=0), then max with vertical (UnderwaterMotor.cs:201-214)"),
    ("input magnitude", 1.0, "", UW + ":198", "Mathf.Min(1f, vector.magnitude)", "VERIFIED-decomp",
     "num = min(1, |(strafe, vertical, forward)|) scales the velocity added"),
    ("surfaceSpeedMultiplier", SURFACE_SPEED_MULTIPLIER, "", UW + ":44", "surfaceSpeedMultiplier = 1.3f",
     "VERIFIED-decomp", "x1.3 on the max speed while the eye is above the water level (UnderwaterMotor.cs:171-174); "
     "the acceleration follows the altered/base ratio when > 1 (UnderwaterMotor.cs:217, 244-247)"),
    ("handsOccupiedMaxSpeedPenalty", HELD_TOOL_PENALTY, "m/s", UW + ":56", "handsOccupiedMaxSpeedPenalty = 1f",
     "VERIFIED-decomp", "max speed - 1 while a tool is held (applied as num-- at UnderwaterMotor.cs:167-170)"),
    ("minSpeed", MIN_MAX_SPEED, "m/s", UW + ":50", "minSpeed = 2f", "VERIFIED-decomp",
     "floor after tank penalties (UnderwaterMotor.cs:145-148); SE has no tank equivalent: inert"),
    ("cap = max(|v|, max speed)", 0.0, "m/s", UW + ":223", "Mathf.Max(b: a * debugSpeedMult, a: velocity.magnitude)",
     "VERIFIED-decomp", "the added velocity never raises the speed above max(current speed, max speed)"),
    ("direction", 1.0, "", UW + ":224", "playerController.forwardReference.rotation * vector", "VERIFIED-decomp",
     "normalize(camera rotation x normalize(strafe, 0, forward) + (0, vertical, 0)) (UnderwaterMotor.cs:199-200, "
     "224-227): free 3-D, in the look direction"),
    ("velocity step", 1.0, "", UW + ":250", "velocity + vector * num6", "VERIFIED-decomp",
     "v1 = v0 + dir x num x accel x dt, clamped to the cap (UnderwaterMotor.cs:243-255); no input: v unchanged "
     "(UnderwaterMotor.cs:286-289)"),
    ("surface rule top", SURFACE_TOP_Y, "m", UW + ":264", "waterLevel + 0.8f - 0.28f", "VERIFIED-decomp",
     "vy x pow(clamp01((0.52 - eye) / (0.52 + 0.5)), 0.3) while eye >= -0.5, no down input, not swimming down "
     "(UnderwaterMotor.cs:261-270): the diver cannot swim his eye above +0.52 m"),
    ("surface rule bottom", SURFACE_BAND_BOTTOM_Y, "m", UW + ":265", "waterLevel - 0.5f", "VERIFIED-decomp",
     "the rule applies from eye >= -0.5 m"),
    ("surface rule exponent", SURFACE_EXPONENT, "", UW + ":268", "0.3f", "VERIFIED-decomp", "shape of the clamp"),
    ("swimming-down test", LOOK_DOWN_Y, "", UW + ":262", "vector2.y < -0.3f", "VERIFIED-decomp",
     "rotated horizontal input pointing down: the surface rule is skipped"),
    ("Vector3.Normalize epsilon", NORMALIZE_EPS, "", "UnityEngine.CoreModule Vector3.Normalize (ilspycmd, "
     "scratchpad/unitydecomp/Vector3.cs:302-310)", None, "VERIFIED-unity", "a zero input or direction stays zero"),
    ("Rigidbody drag (PhysX linear damping)", SWIM_DRAG, "1/s", UW + ":309 (rigidbody.drag = drag); PhysX 4.1 "
     "bodyCoreComputeUnconstrainedVelocity", "rigidbody.drag = drag;", "REPORTED",
     "after each step v x max(0, 1 - dt x drag) = x0.75; the body moves with that velocity for the whole step"),
    ("entry vertical factor", ENTRY_VERTICAL_FACTOR, "", UW + ":74", "velocity.y * 0.5f", "VERIFIED-decomp",
     "each time the swim motor is enabled (UnderwaterMotor.SetEnabled(true), UnderwaterMotor.cs:73-74) the vertical "
     "velocity is halved: at the swim latch entry, and at every Dive<->Run motor switch while the latch stays on "
     "(PlayerController.cs:246, next row)"),
    ("motor switch re-enables the swim motor", ENTRY_VERTICAL_FACTOR, "", "Player.cs:2346-2347 (UpdateIsUnderwater, "
     "UpdateMotorMode), Player.cs:2266-2289 (Dive while isUnderwater, else Run), Player.cs:2239-2244 (SetMotorMode only "
     "on a change), PlayerController.cs:166-178 (HandleControllerState: with underWater the swim motor is enabled "
     "again), PlayerController.cs:246", "HandleControllerState();", "VERIFIED-decomp",
     "the eye crossing 0 with the swim latch on switches Dive<->Run and halves vy (event MotorSwitch, 64): the eye "
     "stops lower at the surface and a dive from the surface starts slower"),
    ("swimheight (capsule)", SWIM_CAPSULE_HEIGHT, "m", SCENE + " word 10; initializer at PlayerController.cs:27",
     "swimheight = 0.5f", "VERIFIED-scene",
     "NOT modelled: the swim capsule spans 0.5 m below to 0.25 m above the eye (cameraOffset -0.25, "
     "PlayerController.cs:152/175, UnderwaterMotor.cs:101-110); SE keeps its upright engineer (owner decision)"),
    ("swim sprint", SPRINT_FACTOR, "", UW + ":119", "return false;", "VERIFIED-decomp",
     "UnderwaterMotor.IsSprinting is false; the sprint modifiers act in GroundMotor on the ground only "
     "(GroundMotor.cs:443); fast swim (x1000, UnderwaterMotor.cs:194) is a debug cheat"),
    ("ledge step-up", 1.85, "m", UW + ":18", "stepHeight = 1.85f", "VERIFIED-decomp",
     "NOT modelled (physics traces, UnderwaterMotor.cs:271-282): in SE the engineer leaves the water by jump + jetpack"),
    # water rules
    ("reference point = camera pivot", 0.0, "m", "scene Main: camera object at local (0,0,0) under the Player root; "
     "PlayerController cameraOffset -0.25, standheight 1.5", None, "VERIFIED-scene",
     "every water rule tests the player root = eye height (capsule 0.25 m above, 1.5 m below standing)"),
    ("isUnderwater", UNDERWATER_EYE_Y, "m", PL + ":1875", "position.y < oceanLevel", "VERIFIED-decomp",
     "eye < 0: Dive numbers, no breathing (CanBreathe Player.cs:1444)"),
    ("swim enter", SWIM_ENTER_EYE_Y, "m", PL + ":1889", "waterLevel + -0.1f", "VERIFIED-decomp",
     "swim latch on when the eye is 0.1 m under the surface"),
    ("swim exit", SWIM_EXIT_EYE_Y, "m", PL + ":1903", "(flag ? 0.1f : 0.8f)", "VERIFIED-decomp",
     "swim latch off at eye >= +0.8 m, or >= +0.1 m when grounded (tested only above +0.1, Player.cs:1893)"),
    ("ocean level", 0.0, "m", "Ocean.cs:26", "main.transform.position.y", "VERIFIED-decomp",
     "the site's sea is SN y = 0 (CONTRACT §3)"),
    # oxygen
    ("oxygenCapacity (diver, no tank)", OXYGEN_CAPACITY, "s", SCENE_OX + " (oxygenCapacity 45, isPlayer 1); no "
     "initializer at Oxygen.cs:8; uGUI_OxygenBar.cs:27 pulseReferenceCapacity 45", "pulseReferenceCapacity = 45f",
     "VERIFIED-scene", "the SE suit's full level maps to 45 Subnautica seconds"),
    ("depth", 0.0, "m", "Ocean.cs:58", "Mathf.Max(0f, GetOceanLevel() - pos.y)", "VERIFIED-decomp",
     "depth = max(0, -eye)"),
    ("depth class Safe", SURFACE_DEPTH, "m", PL + ":2156", "return 0.1f;", "VERIFIED-decomp",
     "depth > 0.1 m (GetSurfaceDepth, Player.cs:2184)"),
    ("depth class Unsafe", UNSAFE_DEPTH, "m", PL + ":2180", "depthOf > 100f", "VERIFIED-decomp", "depth > 100 m"),
    ("depth class Crush (breathing)", CRUSH_DEPTH_CLASS, "m", PL + ":2176", "depthOf > 200f", "VERIFIED-decomp",
     "depth > 200 m: faster breathing, no damage"),
    ("breath period Safe", BREATH_PERIOD_S[1], "s", PL + ":2211", "Ocean.DepthClass.Safe => 3f", "VERIFIED-decomp",
     "one breath every 3 s (180 SE ticks)"),
    ("breath period Unsafe", BREATH_PERIOD_S[2], "s", PL + ":2210", "Ocean.DepthClass.Unsafe => 2.25f",
     "VERIFIED-decomp", "one breath every 2.25 s (135 ticks)"),
    ("breath period Crush", BREATH_PERIOD_S[3], "s", PL + ":2209", "Ocean.DepthClass.Crush => 1.5f",
     "VERIFIED-decomp", "one breath every 1.5 s (90 ticks)"),
    ("breath period Surface", SURFACE_BREATH_PERIOD_S, "s", PL + ":2212", "_ => 99999f", "VERIFIED-decomp",
     "no breath in practice (the model takes none)"),
    ("oxygen per breath Unsafe", BREATH_FACTOR[2], "x period", PL + ":2224", "num = 1.5f", "VERIFIED-decomp",
     "3.375 per breath: 1.5 per second"),
    ("oxygen per breath Crush", BREATH_FACTOR[3], "x period", PL + ":2227", "num = 2f", "VERIFIED-decomp",
     "3 per breath: 2 per second (Safe: 1 per second, Player.cs:2218)"),
    ("breath timing", 0.0, "", "Utils.cs:99", "Mathf.FloorToInt((prevValue + offset) / intervalSize)",
     "VERIFIED-decomp", "a breath when the clock crosses a multiple of the period (Player.cs:2352-2361): SE tick % "
     "period ticks == 0"),
    ("oxygenUnitsPerSecondSurface", OXYGEN_REFILL_PER_S, "1/s", "OxygenManager.cs:7",
     "oxygenUnitsPerSecondSurface = 30f", "VERIFIED-decomp", "refill 30 per second (0.5 per SE tick); private, "
     "not serialized"),
    ("surface refill height", OXYGEN_REFILL_EYE_Y, "m", "OxygenManager.cs:143", "Ocean.GetOceanLevel() - 1f",
     "VERIFIED-decomp", "refill while the eye is above -1 m (or CanBreathe, OxygenManager.cs:157)"),
    ("suffocationTime", 8.0, "s", PL + ":206", "suffocationTime = 8f", "VERIFIED-decomp",
     "informative: SE's own suffocation applies at an empty suit"),
    # pressure
    ("diver crush damage", DIVER_PRESSURE_DAMAGE, "hp", PL + ":873", "private void CrushDamageUpdate()",
     "VERIFIED-decomp", "dead code: private and never invoked (the only CrushDamageUpdate invokes are "
     "CrushDamage.cs:47 and BaseHullStrength.cs:43, on their own components); the diver takes no pressure damage"),
    ("vehicle base crush depth", VEHICLE_BASE_CRUSH_DEPTH, "m", "CrushDamage.cs:11", "kBaseCrushDepth = 100f",
     "VERIFIED-decomp", "S5 reference (vehicle prefab values and depth modules not read)"),
    ("vehicle damage per crush", VEHICLE_DAMAGE_PER_CRUSH, "hp", "CrushDamage.cs:13", "damagePerCrush = 20f",
     "VERIFIED-decomp", "S5 reference"),
    ("vehicle crush period", VEHICLE_CRUSH_PERIOD_S, "s", "CrushDamage.cs:15", "crushPeriod = 3f", "VERIFIED-decomp",
     "S5 reference"),
]

# Rule coverage: every branch of the motor's per-step rules (UnderwaterMotor.UpdateMove,
# AlterMaxSpeed, SetEnabled, PlayerController.SetMotorMode / HandleControllerState, Player.UpdateMotorMode /
# SetMotorMode) and how the model handles it. `check` re-reads every token on its cited line; the replay
# (tests/s2b/swim_replay.py) shares the model's reading, so a branch missing here is a branch neither side checks.
MODELLED, NOT_MODELLED, NOT_APPLICABLE = "modelled", "not modelled", "n/a"
COVERAGE = [
    (UW + ":190", "if (playerController == null || playerController.forwardReference == null)",
     "UpdateMove: no controller or camera", NOT_APPLICABLE, "always set for the diver"),
    (UW + ":194", "fastSwimMode = Player.main.debugFastSwimAllowed", "UpdateMove: debug fast swim", NOT_MODELLED,
     "developer cheat (x1000 speed, :219-222; drag 0, :310-313)"),
    (UW + ":198", "float num = Mathf.Min(1f, vector.magnitude);", "input magnitude", MODELLED, "num"),
    (UW + ":200", "vector.Normalize();", "horizontal input normalised (Unity epsilon)", MODELLED, "NormalizeEpsilon"),
    (UW + ":202", "if (vector.z > 0f)", "max speed: forward / backward", MODELLED, "Dive/Run maxima"),
    (UW + ":210", "if (vector.x != 0f)", "max speed: strafe", MODELLED, ""),
    (UW + ":214", "a = Mathf.Max(a, verticalMaxSpeed);", "max speed: vertical floor", MODELLED, ""),
    (UW + ":128", "switch (equipment.GetTechTypeInSlot(\"Tank\"))", "AlterMaxSpeed: tank penalties (+ the 2 m/s floor)",
     NOT_MODELLED, "no SE counterpart"),
    (UW + ":144", "num -= (float)count * 1.275f;", "AlterMaxSpeed: carried high-capacity tanks", NOT_MODELLED,
     "no SE counterpart"),
    (UW + ":150", "if (techTypeInSlot == TechType.ReinforcedDiveSuit)", "AlterMaxSpeed: reinforced suit", NOT_MODELLED,
     "no SE counterpart"),
    (UW + ":157", "switch (equipment.GetTechTypeInSlot(\"Foots\"))", "AlterMaxSpeed: fins", MODELLED,
     "S5a: input fins modifier 0 / 1.9 / 3.2 (SE setting, default ultra; no Seaglide in SE)"),
    (UW + ":167", "if (!flag && main.GetHeldTool() != null)", "AlterMaxSpeed: held tool -1 m/s", MODELLED,
     "ToolHeld = SE CurrentWeapon"),
    (UW + ":171", "if (gameObject.transform.position.y > Player.main.GetWaterLevel())", "AlterMaxSpeed: above water x1.3",
     MODELLED, "eye > 0"),
    (UW + ":175", "currentPlayerSpeedMultipler = Mathf.MoveTowards", "AlterMaxSpeed: speed-modifier transition",
     MODELLED, "constant 1 (playerSpeedModifier 1 in the scene)"),
    (UW + ":218", "a *= playerController.player.mesmerizedSpeedMultiplier;", "mesmerised (creature effect)",
     NOT_MODELLED, "no SE counterpart"),
    (UW + ":223", "float num4 = Mathf.Max(b: a * debugSpeedMult, a: velocity.magnitude);", "speed cap", MODELLED, ""),
    (UW + ":224", "Vector3 vector2 = playerController.forwardReference.rotation * vector;", "direction: camera rotation",
     MODELLED, "look forward + horizontal right (roll removed)"),
    (UW + ":228", "if (!canSwim)", "no-swim projection of the direction", NOT_APPLICABLE,
     "canSwim is true in every motor mode (PlayerController.cs:211, 238)"),
    (UW + ":234", "if (grounded)", "acceleration while touching something", MODELLED,
     "GroundAcceleration with SwimInputs.Grounded (SE support; walls not seen)"),
    (UW + ":238", "else if (underWater)", "water acceleration x playerSpeedModifier", MODELLED, "20 x 1"),
    (UW + ":233", "float num5 = airAcceleration;", "air acceleration", NOT_APPLICABLE,
     "underWater is the swim latch, always on while the model controls"),
    (UW + ":244", "if (num3 > 1f)", "acceleration x altered/base ratio above 1", MODELLED, "x1.3 above the surface"),
    (UW + ":248", "if (num6 > 0f)", "input: add and clamp; none: keep the velocity (:286-289)", MODELLED, ""),
    (UW + ":257", "if (!canSwim)", "no-swim projection on the surface normal", NOT_APPLICABLE, "canSwim is true"),
    (UW + ":266", "if (transform.position.y >= num9 && !flag && !flag2)", "surface rule", MODELLED, ""),
    (UW + ":272", "if (transform.position.y >= waterLevel && (recentlyCollided || TestWillCollide(vector4))",
     "ledge step-up", NOT_MODELLED, "physics traces; SE's jump + jetpack instead"),
    (UW + ":290", "float num11 = (underWater ? underWaterGravity : gravity);", "gravity", MODELLED,
     "0 while latched (PlayerController.cs:209)"),
    (UW + ":301", "if (underWater)", "drag while swimming", MODELLED, "PhysX damping x0.75 per step (REPORTED)"),
    (UW + ":314", "grounded = false;", "contact flag reset each step", MODELLED, "SE support read every tick"),
    (UW + ":366", "grounded = true;", "any contact sets grounded (OnCollisionStay)", MODELLED,
     "SE support only (walls and ceilings not seen)"),
    (UW + ":69", "if (enabled)", "SetEnabled(true)", MODELLED, "entry and motor switch: vy halved"),
    (UW + ":73", "Vector3 velocity = playerController.velocity;", "SetEnabled: from playerController.velocity",
     NOT_MODELLED, "SE's measured velocity instead (Subnautica's copy can be one frame old)"),
    (UW + ":78", "isKinematic: true", "SetEnabled(false)", NOT_APPLICABLE, "the latch exit hands the engineer to SE"),
    ("PlayerController.cs:170", "if (!inVehicle)", "HandleControllerState: in a vehicle", NOT_APPLICABLE, "S5"),
    ("PlayerController.cs:174", "player.IsInSub() ? groundController : underWaterController",
     "HandleControllerState: in a sub or base", NOT_APPLICABLE, "S5"),
    ("PlayerController.cs:175", "desiredControllerHeight = swimheight - cameraOffset;", "swim capsule size",
     NOT_MODELLED, "owner decision (S2B-STATUS review finding 2)"),
    ("PlayerController.cs:177", "activeController.SetEnabled(enabled);", "swim motor enabled again", MODELLED,
     "event MotorSwitch / Entered"),
    ("PlayerController.cs:181", "activeController = groundController;", "latch off: ground motor", MODELLED,
     "mode Walk: SE's own movement"),
    ("PlayerController.cs:215", "case Player.MotorMode.Seaglide:", "SetMotorMode: Seaglide numbers", NOT_MODELLED,
     "no SE counterpart"),
    ("PlayerController.cs:223", "case Player.MotorMode.Walk:", "SetMotorMode: Walk/Run numbers", MODELLED,
     "Run numbers with the eye at or above 0"),
    ("PlayerController.cs:245", "cameraControl.minimumY = minimumY;", "SetMotorMode: camera pitch limit", NOT_APPLICABLE,
     "SE's camera"),
    ("PlayerController.cs:246", "HandleControllerState();", "SetMotorMode re-enables the active motor", MODELLED,
     "MotorSwitch halving"),
    (PL + ":2241", "if (newMotorMode != motorMode)", "SetMotorMode only on a change", MODELLED,
     "one MotorSwitch per crossing"),
    (PL + ":2256", "if (Inventory.main.GetPickupCount(TechType.Exosuit) > 0)", "UpdateMotorMode: Mech", NOT_APPLICABLE,
     "S5"),
    (PL + ":2262", "if (mode == Mode.LockedPiloting)", "UpdateMotorMode: Vehicle", NOT_APPLICABLE, "S5"),
    (PL + ":2266", "else if (isUnderwater.value)", "UpdateMotorMode: Dive below 0", MODELLED, "eye < 0"),
    (PL + ":2280", "SetMotorMode(MotorMode.Seaglide);", "UpdateMotorMode: Seaglide held", NOT_MODELLED,
     "no SE counterpart"),
    (PL + ":2289", "SetMotorMode(MotorMode.Run);", "UpdateMotorMode: Run at or above 0", MODELLED, "eye >= 0"),
    (PL + ":2294", "SetMotorMode(MotorMode.Walk);", "UpdateMotorMode: in a sub or escape pod", NOT_APPLICABLE, "S5"),
    (PL + ":2347", "UpdateMotorMode();", "motor mode per frame, after the swim latch", MODELLED,
     "per SE tick (the one-frame lag behind isUnderwater is not modelled)"),
]

DEFAULT_DECOMP = os.environ.get("DEEPENG_DECOMP", "")   # a decompiled-source folder, if one is used locally
DEFAULT_GAME = os.environ.get("DEEPENG_SUBNAUTICA", r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica")

# ------------------------------------------------------------------------------------------------ derived numbers
def breath_ticks(dc: int) -> int:
    return int(round(BREATH_PERIOD_S[dc] * SE_TICKS_PER_SECOND))

def oxygen_rate(dc: int) -> float:
    """Average oxygen used per second underwater in depth class dc (Safe 1, Unsafe 1.5, Crush 2)."""
    return BREATH_PERIOD_S[dc] * BREATH_FACTOR[dc] / BREATH_PERIOD_S[dc]

def damping() -> float:
    return max(0.0, 1.0 - SN_FIXED_DT * SWIM_DRAG)

def steady_speed(cap: float, accel_ratio: float = 1.0) -> float:
    """Speed the body moves at once the input has been held long enough (cap after the add, then damped)."""
    k = damping()
    v = 0.0
    for _ in range(1000):
        v1 = min(v + WATER_ACCELERATION * SN_FIXED_DT * accel_ratio, cap)
        v = v1 * k
    return v

def accel_curve(cap: float, steps: int, accel_ratio: float = 1.0) -> list:
    k = damping()
    v, out = 0.0, []
    for _ in range(steps):
        v = min(v + WATER_ACCELERATION * SN_FIXED_DT * accel_ratio, cap) * k
        out.append(v)
    return out

def stop_distance(v: float) -> float:
    """Distance glided after the input is released at speed v (the body moves v*k, v*k^2,... for 0.1 s each)."""
    k = damping()
    return SN_FIXED_DT * v * k / (1.0 - k)

def derived() -> dict:
    fwd = steady_speed(DIVE_FORWARD_MAX)
    curve = accel_curve(DIVE_FORWARD_MAX, 30)
    # time from the first step with input to the step whose velocity reaches 90 % (all) of the top speed: the body
 # moves with that velocity from then on (the velocity of step n holds from (n - 1) x 0.1 s)
    t90 = next(n for n, v in enumerate(curve) if v >= 0.9 * fwd) * SN_FIXED_DT
    t_full = next(n for n, v in enumerate(curve) if abs(v - fwd) < 1e-12) * SN_FIXED_DT
    run_cap = max(RUN_FORWARD_MAX, DIVE_VERTICAL_MAX) * SURFACE_SPEED_MULTIPLIER
    return {
        "forwardSteady": fwd,
        "backwardSteady": steady_speed(DIVE_BACKWARD_MAX),
        "strafeSteady": steady_speed(DIVE_STRAFE_MAX),
        "verticalSteady": steady_speed(DIVE_VERTICAL_MAX),
        "surfaceSteady": steady_speed(run_cap, SURFACE_SPEED_MULTIPLIER),
        "toolSteady": steady_speed(DIVE_FORWARD_MAX - HELD_TOOL_PENALTY),
        # S5a fins: cap + modifier, acceleration x (cap + modifier) / cap (the ratio rule)
        "finsUltraForwardSteady": steady_speed(DIVE_FORWARD_MAX + ULTRAGLIDE_FINS_MAX_SPEED_ADJUST,
                                               (DIVE_FORWARD_MAX + ULTRAGLIDE_FINS_MAX_SPEED_ADJUST) / DIVE_FORWARD_MAX),
        "finsUltraVerticalSteady": steady_speed(DIVE_VERTICAL_MAX + ULTRAGLIDE_FINS_MAX_SPEED_ADJUST,
                                                (DIVE_VERTICAL_MAX + ULTRAGLIDE_FINS_MAX_SPEED_ADJUST) / DIVE_VERTICAL_MAX),
        "finsBasicForwardSteady": steady_speed(DIVE_FORWARD_MAX + FINS_MAX_SPEED_ADJUST,
                                               (DIVE_FORWARD_MAX + FINS_MAX_SPEED_ADJUST) / DIVE_FORWARD_MAX),
        "accel90S": t90,
        "accelFullS": t_full,
        "stopDistanceM": stop_distance(fwd),
        # glide until the speed drops below 0.05 m/s (the S2b.N1 measure): the steps whose velocity is still >= 0.05
        "stopDistanceTo005M": SN_FIXED_DT * sum(fwd * damping() ** n for n in range(1, 1000) if fwd * damping() ** n >= 0.05),
        "stopTimeS": math.ceil(math.log(0.05 / fwd) / math.log(damping())) * SN_FIXED_DT,
        "o2SecondsSafe": OXYGEN_CAPACITY / oxygen_rate(1),
        "o2SecondsUnsafe": OXYGEN_CAPACITY / oxygen_rate(2),
        "o2SecondsCrush": OXYGEN_CAPACITY / oxygen_rate(3),
        "refillFullS": OXYGEN_CAPACITY / OXYGEN_REFILL_PER_S,
        "initializerForwardSteady": steady_speed(6.64),
    }

# ------------------------------------------------------------------------------------------------ table and checks
def table() -> str:
    lines = ["| Name | Value | Unit | Source | Label | Use |", "|---|---|---|---|---|---|"]
    for name, val, unit, src, _tok, label, use in ROWS:
        lines.append("| %s | %s | %s | %s | %s | %s |" % (name, ("%g" % val), unit, src, label, use))
    return "\n".join(lines)

def coverage_table() -> str:
    lines = ["| Decomp | Branch | Handling | Note |", "|---|---|---|---|"]
    for ref, _tok, branch, how, note in COVERAGE:
        lines.append("| %s | %s | %s | %s |" % (ref, branch, how, note))
    return "\n".join(lines)

def check_coverage(decomp: str) -> int:
    bad = 0
    for ref, tok, branch, _how, _note in COVERAGE:
        fname, line = ref.split(":")
        path = os.path.join(decomp, fname)
        text = ""
        if os.path.exists(path):
            with open(path, encoding="utf-8", errors="replace") as fh:
                lines = fh.read().split("\n")
            text = lines[int(line) - 1] if 0 < int(line) <= len(lines) else ""
        good = tok in text
        if not good:
            bad += 1
        print("  coverage %-58s %s %s%s" % (branch[:58], "ok   " if good else "WRONG", ref,
                                           "" if good else "  line is: %s" % text.strip()[:100]))
    print("SN REFERENCE COVERAGE %s (%d branches, %d wrong)" % ("PASS" if bad == 0 else "FAIL", len(COVERAGE), bad))
    return 0 if bad == 0 else 1

def check_decomp(decomp: str) -> int:
    if not os.path.isdir(decomp):
        print("SN REFERENCE CHECK MISSING: decomp folder not found: %s" % decomp)
        return 2
    bad = 0
    checked = 0
    for name, _val, _unit, src, tok, _label, _use in ROWS:
        if not tok:
            continue
        refs = re.findall(r"([A-Za-z_]+\.cs):(\d+)", src)
        if not refs:
            continue
        fname, line = refs[-1][0], int(refs[-1][1])  # the token sits on the last decomp line the source cites
        path = os.path.join(decomp, fname)
        if not os.path.exists(path):
            print("  %-46s MISSING %s" % (name, path))
            bad += 1
            continue
        with open(path, encoding="utf-8", errors="replace") as fh:
            lines = fh.read().split("\n")
        text = lines[line - 1] if 0 < line <= len(lines) else ""
        good = tok in text
        checked += 1
        if not good:
            bad += 1
        print("  %-46s %s %s:%d  %s" % (name, "ok   " if good else "WRONG", fname, line,
                                         "" if good else "line is: %s" % text.strip()[:100]))
    print("SN REFERENCE CHECK %s (%d lines checked, %d wrong or missing)" % ("PASS" if bad == 0 else "FAIL", checked, bad))
    cov = check_coverage(decomp)
    return 0 if bad == 0 and cov == 0 else 1

# ---- read-only scene reader (UnityFS, uncompressed data blocks; SerializedFile v21 without type trees)
def _lz4_block(src: bytes, usize: int) -> bytes:
    dst = bytearray()
    i, n = 0, len(src)
    while i < n:
        tok = src[i]
        i += 1
        lit = tok >> 4
        if lit == 15:
            while True:
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]
        i += lit
        if i >= n:
            break
        off = src[i] | (src[i + 1] << 8)
        i += 2
        ml = tok & 15
        if ml == 15:
            while True:
                b = src[i]
                i += 1
                ml += b
                if b != 255:
                    break
        ml += 4
        start = len(dst) - off
        for k in range(ml):
            dst.append(dst[start + k])
    if len(dst) != usize:
        raise ValueError("lz4: %d bytes, expected %d" % (len(dst), usize))
    return bytes(dst)

def _cstr(b: bytes, p: int):
    e = b.index(b"\0", p)
    return b[p:e].decode("utf-8", "replace"), e + 1

def read_scene_file(bundle: str):
    """Returns (serialized-file bytes of the scene node, node name). Raises ValueError on an unsupported bundle."""
    with open(bundle, "rb") as fh:
        head = fh.read(512)
        sig, p = _cstr(head, 0)
        if sig != "UnityFS":
            raise ValueError("not a UnityFS bundle")
        ver, = struct.unpack(">I", head[p:p + 4])
        p += 4
        _uv, p = _cstr(head, p)
        _ur, p = _cstr(head, p)
        _size, csz, usz, flags = struct.unpack(">qIII", head[p:p + 20])
        p += 20
        if ver >= 7:
            p = (p + 15) & ~15
        if flags & 0x80:
            raise ValueError("blocks info at the end: not supported")
        fh.seek(p)
        info = fh.read(csz)
        if flags & 0x3F in (2, 3):
            info = _lz4_block(info, usz)
        elif flags & 0x3F:
            raise ValueError("blocks info compression %d not supported" % (flags & 0x3F))
        q = 16
        nblk, = struct.unpack(">I", info[q:q + 4])
        q += 4
        blocks = []
        for _ in range(nblk):
            blocks.append(struct.unpack(">IIH", info[q:q + 10]))
            q += 10
        if any(b[2] & 0x3F for b in blocks):
            raise ValueError("compressed data blocks not supported")
        nnode, = struct.unpack(">I", info[q:q + 4])
        q += 4
        nodes = []
        for _ in range(nnode):
            o, s, f = struct.unpack(">qqI", info[q:q + 20])
            q += 20
            name, q = _cstr(info, q)
            nodes.append((o, s, name))
        data0 = p + csz
        if flags & 0x200:
            data0 = (data0 + 15) & ~15
        scene = [n for n in nodes if "." not in n[2]]
        if len(scene) != 1:
            raise ValueError("scene node not found: %s" % [n[2] for n in nodes])
        fh.seek(data0 + scene[0][0])
        return fh.read(scene[0][1]), scene[0][2]

def parse_serialized(blob: bytes) -> dict:
    meta, _fsize, ver, doff = struct.unpack_from(">IIII", blob, 0)
    if ver >= 22 or ver < 17:
        raise ValueError("serialized file version %d not supported" % ver)
    le = blob[16] == 0
    e = "<" if le else ">"
    p = 20
    _uver, p = _cstr(blob, p)
    p += 4
    tree = blob[p]
    p += 1
    if tree:
        raise ValueError("type trees present: not supported")
    ntypes, = struct.unpack_from(e + "i", blob, p)
    p += 4
    types = []
    for _ in range(ntypes):
        cid, = struct.unpack_from(e + "i", blob, p)
        p += 4 + 1 + 2
        if cid == 114:
            p += 16
        p += 16
        types.append(cid)
    nobj, = struct.unpack_from(e + "i", blob, p)
    p += 4
    objs = {}
    for _ in range(nobj):
        p = (p + 3) & ~3
        pid, start, size, tidx = struct.unpack_from(e + "qIIi", blob, p)
        p += 20
        objs[pid] = (doff + start, size, types[tidx])
    return {"version": ver, "objects": objs}

def _mono_fields(blob: bytes, off: int):
    go_pid = struct.unpack_from("<q", blob, off + 4)[0]
    p = off + 12 + 4
    p += 12
    n, = struct.unpack_from("<i", blob, p)
    p = (p + 4 + n + 3) & ~3
    return go_pid, p

def _words(blob: bytes, p: int, n: int):
    return [struct.unpack_from("<f", blob, p + 4 * k)[0] for k in range(n)], \
           [struct.unpack_from("<i", blob, p + 4 * k)[0] for k in range(n)]

# PlayerController field words after the MonoBehaviour header
PC_WORDS = {"standheight": 9, "swimheight": 10, "cameraOffset": 11, "controllerRadius": 24, "defaultSwimDrag": 25,
            "swimForwardMaxSpeed": 27, "swimBackwardMaxSpeed": 28, "swimStrafeMaxSpeed": 29,
            "swimVerticalMaxSpeed": 30, "swimWaterAcceleration": 31, "walkRunForwardMaxSpeed": 38,
            "walkRunBackwardMaxSpeed": 39, "walkRunStrafeMaxSpeed": 40}
PC_EXPECT = {"swimForwardMaxSpeed": DIVE_FORWARD_MAX, "swimBackwardMaxSpeed": DIVE_BACKWARD_MAX,
             "swimStrafeMaxSpeed": DIVE_STRAFE_MAX, "swimVerticalMaxSpeed": DIVE_VERTICAL_MAX,
             "swimWaterAcceleration": WATER_ACCELERATION, "defaultSwimDrag": SWIM_DRAG,
             "walkRunForwardMaxSpeed": RUN_FORWARD_MAX, "walkRunBackwardMaxSpeed": RUN_BACKWARD_MAX,
             "walkRunStrafeMaxSpeed": RUN_STRAFE_MAX, "cameraOffset": CAMERA_OFFSET, "standheight": 1.5,
             "swimheight": SWIM_CAPSULE_HEIGHT}
PLAYER_CONTROLLER_PID = 527   # Main scene of Steam build 20241558; the links below re-validate it

def check_scene(bundle: str) -> int:
    if not bundle or not os.path.exists(bundle):
        print("SN REFERENCE SCENE MISSING: bundle not found: %s" % bundle)
        return 2
    try:
        blob, node = read_scene_file(bundle)
        sf = parse_serialized(blob)
    except (ValueError, struct.error) as ex:
        print("SN REFERENCE SCENE FAIL: %s" % ex)
        return 1
    objs = sf["objects"]
    bad = []

    def f32(x):
        return struct.unpack("<f", struct.pack("<f", x))[0]

    pc = objs.get(PLAYER_CONTROLLER_PID)
    if pc is None or pc[2] != 114:
        print("SN REFERENCE SCENE FAIL: no MonoBehaviour %d in %s (game updated? re-verify)" % (PLAYER_CONTROLLER_PID, node))
        return 1
    go, fp = _mono_fields(blob, pc[0])
    fl, it = _words(blob, fp, 43)
    um_pid, cam_pid = it[16], it[7]
    um = objs.get(um_pid)
    um_fl, um_it = _words(blob, _mono_fields(blob, um[0])[1], 36) if um else ([], [])
    if not um or um_it[4] != PLAYER_CONTROLLER_PID:
        bad.append("underWaterController %d does not point back to the PlayerController" % um_pid)
    print("  scene node %s, serialized file v%d, %d objects, PlayerController pathID %d on GameObject %d" % (
        node, sf["version"], len(objs), PLAYER_CONTROLLER_PID, go))
    for name, k in PC_WORDS.items():
        want = PC_EXPECT.get(name)
        good = want is None or fl[k] == f32(want)
        if not good:
            bad.append(name)
        print("  PlayerController.%-24s %10.4f %s" % (name, fl[k], "" if want is None else ("ok" if good else
                                                                                          "WRONG (table %g)" % want)))
    if um:
        # UnderwaterMotor words: PlayerMotor's fields in declaration order
 # then UnderwaterMotor's (vel 28-30, fastSwimMode 31, capsulecollider 32-34, 35)
        for name, k, want in (("waterAcceleration (overwritten)", 23, None),
                              ("groundAcceleration", 24, GROUND_ACCELERATION),
                              ("playerSpeedModifier", 35, PLAYER_SPEED_MODIFIER)):
            good = want is None or um_fl[k] == f32(want)
            if not good:
                bad.append(name)
            print("  UnderwaterMotor.%-25s %10.4f %s" % (name, um_fl[k], "" if want is None else ("ok" if good else
                                                                                               "WRONG")))
    # the diver's Oxygen: the MonoBehaviour on the Player GameObject whose fields are (float capacity, bool isPlayer=1)
    oxy = []
    for pid, (off, size, cls) in objs.items():
        if cls != 114:
            continue
        g, p = _mono_fields(blob, off)
        if g == go and off + size - p == 8:
            cap, = struct.unpack_from("<f", blob, p)
            is_player = blob[p + 4]
            if is_player == 1:
                oxy.append((pid, cap))
    good = len(oxy) == 1 and oxy[0][1] == f32(OXYGEN_CAPACITY)
    if not good:
        bad.append("oxygenCapacity")
    print("  Oxygen (isPlayer) %-29s %s %s" % ("", oxy, "ok" if good else "WRONG (table %g)" % OXYGEN_CAPACITY))
    # camera pivot: MainCameraControl's transform chain up to the Player root, every local position 0
    chain = []
    cam = objs.get(cam_pid)
    if cam:
        cgo = _mono_fields(blob, cam[0])[0]
        tr = {}
        for pid, (off, size, cls) in objs.items():
            if cls in (4, 224):
                tr[struct.unpack_from("<q", blob, off + 4)[0]] = (pid, off)
        cur = tr.get(cgo)
        while cur is not None:
            off = cur[1] + 12
            pos = struct.unpack_from("<3f", blob, off + 16)
            nch, = struct.unpack_from("<i", blob, off + 40)
            father = struct.unpack_from("<q", blob, off + 44 + 12 * nch + 4)[0]
            owner = struct.unpack_from("<q", blob, cur[1] + 4)[0]
            chain.append((owner, pos))
            cur = next((v for k, v in tr.items() if v[0] == father), None) if father else None
    good = len(chain) >= 2 and chain[-1][0] == go and all(c[1] == (0.0, 0.0, 0.0) for c in chain[:-1])
    if not good:
        bad.append("camera pivot")
    print("  camera pivot chain (GameObject, localPosition) %s %s" % (chain, "ok" if good else "WRONG"))
    print("SN REFERENCE SCENE %s%s" % ("PASS" if not bad else "FAIL", "" if not bad else " " + ", ".join(bad)))
    return 0 if not bad else 1

def find_bundle(game: str):
    hits = glob.glob(os.path.join(game, "Subnautica_Data", "StreamingAssets", "aa", "StandaloneWindows64",
                                  "main.unity_*.bundle"))
    return hits[0] if len(hits) == 1 else None

# ------------------------------------------------------------------------------------------------ selftest
def selftest() -> int:
    ok = True

    def line(name, good, detail=""):
        nonlocal ok
        ok = ok and good
        print("  %-58s %s  %s" % (name, "ok" if good else "WRONG", detail))

    print("SN REFERENCE SELFTEST")
    d = derived()
    line("SN step = 6 SE ticks", abs(SN_STEP_TICKS * SE_TICK_DT - SN_FIXED_DT) < 1e-15)
    line("damping per step 0.75", damping() == 0.75)
    line("forward steady 5.70 m/s (7.6 x 0.75)", abs(d["forwardSteady"] - 5.7) < 1e-12, "%.6f" % d["forwardSteady"])
    line("vertical steady 4.725 m/s (6.3 x 0.75)", abs(d["verticalSteady"] - 4.725) < 1e-12)
    line("surface steady 6.1425 m/s (6.3 x 1.3 x 0.75)", abs(d["surfaceSteady"] - 6.1425) < 1e-12)
    line("held tool steady 4.95 m/s", abs(d["toolSteady"] - 4.95) < 1e-12)
    line("90 % of the top speed from 0.6 s, all of it from 1.0 s", abs(d["accel90S"] - 0.6) < 1e-9 and
         abs(d["accelFullS"] - 1.0) < 1e-9, "%.1f / %.1f s" % (d["accel90S"], d["accelFullS"]))
    line("stop distance 1.71 m (0.1 x 5.7 x 0.75 / 0.25)", abs(d["stopDistanceM"] - 1.71) < 1e-12)
    line("O2: 45 s / 30 s / 22.5 s by depth class", (d["o2SecondsSafe"], d["o2SecondsUnsafe"],
                                                    d["o2SecondsCrush"]) == (45.0, 30.0, 22.5))
    line("breath periods are whole SE ticks (180/135/90)", [breath_ticks(k) for k in (1, 2, 3)] == [180, 135, 90] and
         all(abs(breath_ticks(k) * SE_TICK_DT - BREATH_PERIOD_S[k]) < 1e-12 for k in (1, 2, 3)))
    line("refill to full in 1.5 s", d["refillFullS"] == 1.5)
    line("initializer speeds would give 4.98 m/s (the scene value matters)",
         abs(d["initializerForwardSteady"] - 4.98) < 1e-12)
    line("surface top 0.52 m", abs(SURFACE_TOP_Y - 0.52) < 1e-15)
    line("M3 threshold is the centre of the swim band",
         abs(MUTANT_M3_EYE_Y - (SWIM_ENTER_EYE_Y + SWIM_EXIT_EYE_Y) / 2) < 1e-12)
    line("contact acceleration: 0 -> 3.375 -> 5.7 m/s in two steps (4.5 m/s per step, x0.75)",
         abs(min(GROUND_ACCELERATION * SN_FIXED_DT, DIVE_FORWARD_MAX) * damping() - 3.375) < 1e-12 and
         abs(min(3.375 + GROUND_ACCELERATION * SN_FIXED_DT, DIVE_FORWARD_MAX) * damping() - 5.7) < 1e-12)
    names = [r[0] for r in ROWS]
    line("table rows unique and complete", len(names) == len(set(names)) and all(len(r) == 7 for r in ROWS),
         "%d rows" % len(ROWS))
    line("coverage rows complete (file:line, token, branch, handling, note)", all(
        len(c) == 5 and re.match(r"^[A-Za-z]+\.cs:\d+$", c[0]) and c[1] and c[3] in (MODELLED, NOT_MODELLED,
                                                                                      NOT_APPLICABLE) for c in COVERAGE),
         "%d branches (%d modelled, %d not modelled, %d n/a)" % (
             len(COVERAGE), sum(1 for c in COVERAGE if c[3] == MODELLED),
             sum(1 for c in COVERAGE if c[3] == NOT_MODELLED), sum(1 for c in COVERAGE if c[3] == NOT_APPLICABLE)))
    line("every row has a label", all(r[5] in ("VERIFIED-decomp", "VERIFIED-scene", "VERIFIED-unity", "REPORTED")
                                      for r in ROWS))
    print("SN REFERENCE SELFTEST %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Subnautica swim/oxygen/pressure numbers for S2b")
    ap.add_argument("--selftest", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    sub.add_parser("table")
    sub.add_parser("coverage")
    sub.add_parser("derived")
    p = sub.add_parser("check")
    p.add_argument("--decomp", default=os.environ.get("DEEPENG_SN_DECOMP", DEFAULT_DECOMP))
    p = sub.add_parser("check-scene")
    p.add_argument("--bundle")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.cmd == "table":
        print(table())
        return 0
    if a.cmd == "coverage":
        print(coverage_table())
        return 0
    if a.cmd == "derived":
        for k, v in derived().items():
            print("%-26s %.6g" % (k, v))
        return 0
    if a.cmd == "check":
        return check_decomp(a.decomp)
    if a.cmd == "check-scene":
        return check_scene(a.bundle or find_bundle(DEFAULT_GAME))
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main())
