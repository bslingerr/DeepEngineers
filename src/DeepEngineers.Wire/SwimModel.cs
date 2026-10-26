using System;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S2b swimming model (ROADMAP S2b, CONTRACT §2.10): Subnautica's underwater motor, water rules and oxygen rule as a
 // pure, deterministic, allocation-free step function, called once per SE tick (1/60 s) by the SE plugin's swim
 // controller. Clean reimplementation from the numbers of tests/s2b/sn_reference.py
 // no game code is copied. The independent Python replay is
 // tests/s2b/swim_replay.py (same numbers, its own code); rows are written by SwimTrace ("swim" rows of se-*.jsonl).
 //
 // Frame. Every vector is in the swim frame: +Y up (away from the planet centre at the character), X and Z any
 // horizontal basis; the SE plugin uses the SN-world axes of the site map at the character (mapdir). The model never
 // builds a rotation from the look vectors, so the handedness of the frame does not matter.
 //
 // Reference point. Subnautica tests every water rule at the player root, which is the camera pivot (VERIFIED scene:
 // the camera object sits at local 0 under the root; the capsule reaches 0.25 m above it and 1.5 m below standing).
 // The model's height input is therefore the eye: the SE character's head / first-person camera point.
 //
 // Time. Subnautica steps a free diver's physics at 0.1 s (MainGameController 0.1 s fixed step; the 0.02 s step is
 // only registered in a moving sub or by two tools). The model runs one Subnautica motor step every 6 SE ticks
 // (SnStepTicks) and holds the resulting velocity in between, so a 60 Hz SE trajectory equals Subnautica's 10 Hz
 // trajectory at every 0.1 s and is linear in between (the body moves with the damped velocity for the whole step).
 // =================================================================================================================

 /// <summary>
 /// Every number of the swim model, with its Subnautica source
 /// Doubles; Subnautica computes in float32 (differences below
 /// 1e-6 relative).
 /// </summary>
    public static class SwimThresholds
    {
        // ---- time base
 /// <summary>SE simulation step (CONTRACT §2.1).</summary>
        public const double SeTickDt = 1.0 / 60.0;
        /// <summary>One Subnautica motor step every 6 SE ticks.</summary>
        public const int SnStepTicks = 6;
        /// <summary>Subnautica <c>Time.fixedDeltaTime</c> for a free diver.</summary>
        public const double SnStepDt = 0.1;

        // ---- motor
 /// <summary>Dive (eye below the surface) forward maximum, m/s.</summary>
        public const double DiveForwardMax = 7.6;
        /// <summary>Dive backward maximum, m/s.</summary>
        public const double DiveBackwardMax = 7.6;
        /// <summary>Dive strafe maximum, m/s.</summary>
        public const double DiveStrafeMax = 7.6;
        /// <summary>Vertical maximum in every mode, and the floor of the max speed.</summary>
        public const double VerticalMax = 6.3;
        /// <summary>Run mode (swim latch on, eye at or above the surface) forward maximum.</summary>
        public const double RunForwardMax = 3.5;
        /// <summary>Run mode backward maximum.</summary>
        public const double RunBackwardMax = 5.0;
        /// <summary>Run mode strafe maximum.</summary>
        public const double RunStrafeMax = 5.0;
        /// <summary>Water acceleration, m/s².</summary>
        public const double WaterAcceleration = 20.0;
        /// <summary>
 /// Acceleration while the diver touches something.
 /// takes <c>groundAcceleration</c> instead of the water acceleration whenever
 /// <c>grounded</c>, which OnCollisionStay sets for any contact of the last physics step (reset).
 /// The model's contact input is <see cref="SwimInputs.Grounded"/>.
 /// </summary>
        public const double GroundAcceleration = 45.0;
        /// <summary>Rigidbody drag while swimming, 1/s.</summary>
        public const double SwimDrag = 2.5;
        /// <summary>×1.3 on the max speed while the eye is above the water level.</summary>
        public const double SurfaceSpeedMultiplier = 1.3;
        /// <summary>Max speed −1 m/s while a tool is held.</summary>
        public const double HeldToolPenalty = 1.0;
        /// <summary>
 /// Vertical velocity kept each time the swim motor is (re-)enabled:
 /// at the swim latch entry, and at every Dive↔Run motor-mode switch while the latch stays on, i.e. each time the
 /// eye crosses 0
 /// </summary>
        public const double EntryVerticalFactor = 0.5;
        /// <summary>Eye height where the upward swim speed reaches 0.</summary>
        public const double SurfaceTopY = 0.8 - 0.28;
        /// <summary>The surface rule applies from this eye height up.</summary>
        public const double SurfaceBandBottomY = -0.5;
        /// <summary>Exponent of the surface rule.</summary>
        public const double SurfaceExponent = 0.3;
        /// <summary>Rotated horizontal input with y below this is "swimming down": no surface rule.</summary>
        public const double LookDownY = -0.3;
        /// <summary>Unity <c>Vector3.Normalize</c>: a vector at or below this length becomes zero (UnityEngine.CoreModule).</summary>
        public const double NormalizeEpsilon = 1e-5;
        /// <summary>Subnautica's swim motor has no sprint. Documentation only.</summary>
        public const double SprintFactor = 1.0;
        /// <summary>
 /// Fins (S5a): +1.9 m/s on the max speed
 /// Input <see cref="SwimInputs.FinsModifier"/>; <see cref="SwimFins"/>.
 /// </summary>
        public const double FinsSpeedAdjust = 1.9;
        /// 2 m/s.</summary>
        public const double UltraglideFinsSpeedAdjust = 3.2;

        // ---- water rules (eye height in SN metres, the sea at 0)
 /// <summary>isUnderwater: eye below 0 → Dive numbers, no breathing.</summary>
        public const double UnderwaterEyeY = 0.0;
        /// <summary>Swim latch on when the eye is below −0.1 m.</summary>
        public const double SwimEnterEyeY = -0.1;
        /// <summary>Swim latch off when the eye reaches +0.8 m...</summary>
        public const double SwimExitEyeY = 0.8;
        /// <summary>... or +0.1 m while grounded; the grounded test is made only above +0.1 m.</summary>
        public const double SwimExitGroundedEyeY = 0.1;
        /// </summary>
        public const double JetpackOnEyeY = 0.0;
        /// <summary>... and is withdrawn again when the eye is below −0.1 m (the swim-entry depth; a 0.1 m band).</summary>
        public const double JetpackOffEyeY = -0.1;

        // ---- oxygen (Subnautica units: seconds of breath)
 /// <summary>The diver's own oxygen, no tank.</summary>
        public const double OxygenCapacity = 45.0;
        /// <summary>Refill per second near the surface.</summary>
        public const double OxygenRefillPerSecond = 30.0;
        /// <summary>Refill while the eye is above −1 m.</summary>
        public const double OxygenRefillEyeY = -1.0;
        /// <summary>Depth class Safe beyond 0.1 m.</summary>
        public const double SafeDepth = 0.1;
        /// <summary>Depth class Unsafe beyond 100 m.</summary>
        public const double UnsafeDepth = 100.0;
        /// <summary>Depth class Crush beyond 200 m: faster breathing only; the diver takes no pressure damage.</summary>
        public const double CrushDepth = 200.0;
        /// <summary>Breath periods 3 s / 2.25 s / 1.5 s in SE ticks.</summary>
        public const int BreathTicksSafe = 180, BreathTicksUnsafe = 135, BreathTicksCrush = 90;
        /// <summary>Breath periods in seconds.</summary>
        public const double BreathPeriodSafe = 3.0, BreathPeriodUnsafe = 2.25, BreathPeriodCrush = 1.5;
        /// <summary>Oxygen per breath = period × factor; factors 1 / 1.5 / 2: 1, 1.5, 2 per second.</summary>
        public const double BreathFactorSafe = 1.0, BreathFactorUnsafe = 1.5, BreathFactorCrush = 2.0;

        // ---- seeded failures
 /// <summary>S2b.M1: max speeds and acceleration ×1.5.</summary>
        public const double MutantM1Scale = 1.5;
        /// <summary>S2b.M3: one swim threshold, the centre of the −0.1 … +0.8 band, both ways; the jetpack at 0 both ways.</summary>
        public const double MutantM3EyeY = 0.35;
    }

    /// <summary>Movement state (<see cref="SwimOutputs.Mode"/>).</summary>
    public static class SwimMode
    {
        /// <summary>Swim latch off: SE's stock movement (walking, jetpack, Water Mod) applies; the model is inert.</summary>
        public const uint Walk = 0;
        /// <summary>Swim latch on, eye below the surface: Subnautica's Dive motor; jetpack withdrawn.</summary>
        public const uint Swim = 1;
        /// <summary>Swim latch on, eye at or above the surface: Subnautica's Run motor numbers ×1.3 above 0, surface rule.</summary>
        public const uint Surface = 2;
        /// <summary>Swim latch on, jetpack on and allowed (head out): SE's jetpack moves the engineer (the climb out); the model is inert.</summary>
        public const uint Fly = 3;

        public static string Name(uint mode)
        {
            switch (mode)
            {
                case Walk: return "Walk";
                case Swim: return "Swim";
                case Surface: return "Surface";
                case Fly: return "Fly";
                default: return "?";
            }
        }
    }

    /// <summary>Subnautica's depth classes (Ocean.DepthClass).</summary>
    public static class SwimDepthClass
    {
        public const uint Surface = 0, Safe = 1, Unsafe = 2, Crush = 3;
    }

    /// <summary>Bits of <see cref="SwimOutputs.Events"/>.</summary>
    public static class SwimEvents
    {
        /// <summary>The swim latch turned on this tick (Walk → in the water).</summary>
        public const uint Entered = 1u << 0;
        /// <summary>The swim latch turned off this tick.</summary>
        public const uint Exited = 1u << 1;
        /// <summary>The jetpack became allowed this tick.</summary>
        public const uint JetpackGranted = 1u << 2;
        /// <summary>The jetpack became forbidden this tick (the SE controller switches it off).</summary>
        public const uint JetpackRevoked = 1u << 3;
        /// <summary>A breath was taken this tick (oxygen removed).</summary>
        public const uint Breath = 1u << 4;
        /// <summary>The model took control of the velocity this tick.</summary>
        public const uint Resumed = 1u << 5;
        /// <summary>
 /// The eye crossed the surface (0) while the swim latch stayed on: Subnautica's Dive↔Run motor-mode switch, which
 /// re-enables the swim motor and halves the vertical velocity (<see cref="SwimThresholds.EntryVerticalFactor"/>)
 /// when the model controls it.
 /// </summary>
        public const uint MotorSwitch = 1u << 6;
    }

    /// <summary>Seeded failures the SE plugin can switch on through <see cref="SwimInputs.Mutants"/> (0 in normal play).</summary>
    public static class SwimMutants
    {
        /// <summary>S2b.M1: max speeds and acceleration ×1.5 (S2b.N1 red).</summary>
        public const uint M1Numbers = 1u << 0;
        /// <summary>S2b.M2: the jetpack stays allowed in the water (S2b.N2 red).</summary>
        public const uint M2JetpackInWater = 1u << 1;
        /// <summary>S2b.M3: no hysteresis: one swim threshold <see cref="SwimThresholds.MutantM3EyeY"/>, one jetpack threshold (S2b.N3 red).</summary>
        public const uint M3NoHysteresis = 1u << 2;
        /// <summary>S2b.M4: the oxygen rule is off; SE's stock suit consumption applies (S2b.N4 red).</summary>
        public const uint M4OxygenOff = 1u << 3;
    }

    /// <summary>What the SE plugin knows this tick (filled once per SE tick, InWorld, on the bound site planet).</summary>
    public struct SwimInputs
    {
        /// <summary>SE tick (<c>MySession.GameplayFrameCounter</c>); breaths happen on multiples of the breath period in ticks.</summary>
        public ulong Tick;
        /// <summary>Move axes in [−1, 1]: strafe (+right), vertical (+up: SE's up/jump key, −: crouch/down key), forward (+forward).</summary>
        public double MoveStrafe, MoveVertical, MoveForward;
        /// <summary>SE's sprint key. Logged only: Subnautica's swim motor has no sprint.</summary>
        public bool Sprint;
        /// <summary>A hand tool or weapon is held (Subnautica: max speed −1 m/s).</summary>
        public bool ToolHeld;
        /// <summary>
 /// The character touches something solid (SE: the character proxy's support). Subnautica's <c>grounded</c>: the
 /// grounded swim exit (+0.1 m) and the contact acceleration (<see cref="SwimThresholds.GroundAcceleration"/>).
 /// </summary>
        public bool Grounded;
        /// <summary>SE's jetpack is switched on.</summary>
        public bool JetpackOn;
        /// <summary>
 /// IsOnLadder</c>).
 /// SE's ladder component moves the character; the model yields (<see cref="SwimYield.Ladder"/>).
 /// </summary>
        public bool OnLadder;
        /// <summary>

 /// <c>MyCharacterProxy.GetSupportingEntities</c> holds a grid). With the jetpack off and no up input, the model yields
 /// (<see cref="SwimYield.Deck"/>): SE walks the character on the deck as it does in space; a push up swims again.

 /// </summary>
        public bool OnGrid;
        /// <summary>Look direction (unit, swim frame): the camera forward with pitch.</summary>
        public Vec3 LookForward;
        /// <summary>Camera right (unit, swim frame). Any roll is removed by the model (only its horizontal part is used).</summary>
        public Vec3 LookRight;
        /// <summary>The character's velocity as SE measured it at the start of this tick (swim frame, m/s), after SE's contacts.</summary>
        public Vec3 Velocity;
        /// <summary>The eye point in SN-world metres (<c>Y</c> = height above the sea; the rules use <c>Y</c> only, X/Z are logged).</summary>
        public Vec3 EyePos;
        /// <summary>The suit's oxygen in Subnautica units (SE suit fill level × <see cref="SwimThresholds.OxygenCapacity"/>).</summary>
        public double Oxygen;
        /// <summary><see cref="SwimMutants"/> (seeded failures; 0 in normal play).</summary>
        public uint Mutants;
        /// <summary>
 /// S5a fins: metres per second added to the motor's max speed (0 none, <see cref="SwimThresholds.FinsSpeedAdjust"/>
 /// 1.9, <see cref="SwimThresholds.UltraglideFinsSpeedAdjust"/> 3.2; <see cref="SwimFins.Modifier"/>). Applied as
 /// UnderwaterMotor.AlterMaxSpeed does: to the selected direction maximum (forward, backward, strafe or
 /// the vertical floor) before the held-tool penalty and the surface ×1.3; the acceleration scales with the ratio.
 /// The default of a fresh struct is 0 (the S2b numbers); the SE controller sends its fins setting (default ultra).
 /// </summary>
        public double FinsModifier;
    }

    /// <summary>
 /// Everything the model remembers between ticks (a plain struct: copy it to snapshot, log it to reseed the replay).
 /// <c>default(SwimState)</c> is the initial state (on land, jetpack allowed).
 /// </summary>
    public struct SwimState
    {
        /// <summary>Number of Step calls before this one.</summary>
        public ulong Steps;
        /// <summary>The swim latch (Subnautica's isUnderwaterForSwimming).</summary>
        public bool Latched;
        /// <summary>The jetpack latch while in the water (on at eye ≥ 0, off below −0.1 m).</summary>
        public bool JetLatch;
        /// <summary>The last output denied the jetpack.</summary>
        public bool JetpackDenied;
        /// <summary>The model controlled the velocity on the last tick.</summary>
        public bool Controlling;
        /// <summary>Ticks since the last Subnautica motor step, modulo <see cref="SwimThresholds.SnStepTicks"/>: a step runs when 0.</summary>
        public int Phase;
        /// <summary>The velocity the last motor step produced (held until the next step); zero while not controlling.</summary>
        public Vec3 Held;
        /// <summary>The last <see cref="SwimMode"/>.</summary>
        public uint Mode;
        /// <summary>The eye was below the surface (Subnautica's isUnderwater: motor mode Dive, else Run) on the last tick.</summary>
        public bool EyeUnder;
        /// <summary>The last <see cref="SwimYield"/>.</summary>
        public uint Yield;
    }

    /// <summary>What the model decided this tick.</summary>
    public struct SwimOutputs
    {
        /// <summary><see cref="SwimMode"/>.</summary>
        public uint Mode;
        /// <summary>The controller sets the character's velocity to <see cref="Velocity"/> this tick (Swim and Surface); SE gravity, Water Mod forces and dampeners must not act on it.</summary>
        public bool ControlsVelocity;
        /// <summary>The velocity to set (swim frame, m/s); the input velocity unchanged while not controlling.</summary>
        public Vec3 Velocity;
        /// <summary><see cref="Velocity"/> × <see cref="SwimThresholds.SeTickDt"/>: the expected motion over this tick if nothing is in the way.</summary>
        public Vec3 PositionDelta;
        /// <summary>A Subnautica motor step ran this tick.</summary>
        public bool SnStep;
        /// <summary>The motor's max speed at this step (direction maxima, tool, surface ×1.3, M1), 0 without a step.</summary>
        public double MaxSpeed;
        /// <summary>The speed cap at this step: max(|v|, <see cref="MaxSpeed"/>), 0 without a step.</summary>
        public double SpeedCap;
        /// <summary>The jetpack may be on (on land always; in the water only with the head out). The controller switches it off otherwise.</summary>
        public bool JetpackAllowed;
        /// <summary>The oxygen rule is in force (in the water, rule not disabled): the controller applies <see cref="OxygenDelta"/> instead of SE's suit consumption.</summary>
        public bool OxygenRuleActive;
        /// <summary>Oxygen change this tick, Subnautica units (0 while the rule is not in force), already clamped to [0, capacity].</summary>
        public double OxygenDelta;
        /// <summary><see cref="SwimDepthClass"/> of the eye.</summary>
        public uint DepthClass;
        /// <summary><see cref="SwimEvents"/> of this tick.</summary>
        public uint Events;
        /// <summary>The mode changed this tick.</summary>
        public bool Changed;
        /// <summary><see cref="SwimYield"/>: the latch holds (oxygen, arms, HUD) but SE moves the character this tick.</summary>
        public uint Yield;
    }

    /// <summary>
 /// Why the model let SE move the character while the swim latch holds: on a ladder SE's ladder component
 /// climbs; on a grid deck with the jetpack off and no up input SE walks, its magnetic boots included. Either way no
 /// velocity is written and the flying state is not forced; the oxygen rule, the arms and the bubbles stay.
 /// </summary>
    public static class SwimYield
    {
        public const uint None = 0, Ladder = 1, Deck = 2;

        public static string Name(uint y)
        {
            switch (y)
            {
                case None: return "none";
                case Ladder: return "ladder";
                case Deck: return "deck";
                default: return "?";
            }
        }

        /// <summary>The rule (pure): the ladder first, then the deck while swimming with the head under, standing on a grid, jetpack off, no up input.</summary>
        public static uint Of(bool latched, uint mode, bool onLadder, bool onGrid, bool grounded, bool jetpackOn, double moveVertical)
        {
            if (!latched) return None;
            if (onLadder) return Ladder;
            if (mode == SwimMode.Swim && onGrid && grounded && !jetpackOn && moveVertical <= 0.0) return Deck;
            return None;
        }
    }

    /// <summary>
 /// The step function. Per tick, in this order:
 /// <list type="number">
 /// <item>Swim latch: on below −0.1 m; off at +0.8 m, or
 /// at +0.1 m when grounded (the grounded test only above +0.1 m). S2b.M3: one threshold +0.35 m both ways.</item>
 /// <item>Jetpack: always allowed out of the water; in the water a latch, on at eye ≥ 0, off below −0.1 m (S2b.M3: eye
 /// ≥ 0 both ways; S2b.M2: always allowed).</item>
 /// <item>Mode: Walk (latch off), Fly (jetpack on and allowed), Swim (eye &lt; 0), Surface.</item>
 /// <item>Motor (Swim, Surface): a Subnautica step when the phase is 0 or control resumes, else the held velocity. The
 /// step: max speed = max(forward or backward, strafe, vertical maxima that apply; vertical always), Dive numbers below
 /// the surface and Run numbers at or above it; −1 m/s with a tool; ×1.3 above 0 with the acceleration ×1.3;
 /// cap = max(|v0|, max speed); direction = normalize(right·x + forward·z normalized horizontally, plus the vertical
 /// input up); v1 = v0 + dir·min(1, |input|)·20·0.1 (45·0.1 while touching something), clamped to the cap; near the
 /// surface (eye ≥ −0.5, no down input, not swimming down) v1.y × pow(clamp01((0.52 − eye)/1.02), 0.3); no input:
 /// v1 = v0; then the physics engine's damping v2 = v1·max(0, 1 − 0.1·2.5). The swim motor being (re-)enabled halves
 /// the vertical velocity first: on the latch entry and whenever the eye crosses 0 with the latch on (the Dive↔Run
 /// motor switch, <see cref="SwimEvents.MotorSwitch"/>); between two motor steps the held velocity is halved.</item>
 /// <item>Oxygen (in the water, rule on): a breath when the tick is a multiple of the depth class's period (eye &lt; 0),
 /// then +30/s refill while the eye is above −1 m; clamped to [0, 45].</item>
 /// </list>
 /// The model yields no rotation at all: roll stays disabled (only the horizontal part of the camera right is used).
 /// </summary>
    public static class SwimModel
    {
        public static void Step(ref SwimState s, ref SwimInputs i, out SwimOutputs o)
        {
            o = default(SwimOutputs);
            uint mut = i.Mutants;
            bool noHyst = (mut & SwimMutants.M3NoHysteresis) != 0;
            double eye = i.EyePos.Y;
            bool wasLatched = s.Latched;
            uint events = 0;

            // 1. swim latch
            bool latched;
            if (noHyst) latched = eye < SwimThresholds.MutantM3EyeY;
            else if (!wasLatched) latched = eye < SwimThresholds.SwimEnterEyeY;
            else
            {
                bool groundedTest = eye > SwimThresholds.SwimExitGroundedEyeY && i.Grounded;
                latched = eye < (groundedTest ? SwimThresholds.SwimExitGroundedEyeY : SwimThresholds.SwimExitEyeY);
            }
            if (latched && !wasLatched) events |= SwimEvents.Entered;
            if (!latched && wasLatched) events |= SwimEvents.Exited;
            // Subnautica's motor mode (Dive below 0, Run at or above) switches while the swim motor stays active: the
 // motor is re-enabled and the vertical velocity halved
            bool under = eye < SwimThresholds.UnderwaterEyeY;
            if (latched && wasLatched && under != s.EyeUnder) events |= SwimEvents.MotorSwitch;

            // 2. jetpack
            bool jetLatch;
            if (!latched) jetLatch = false;
            else if (noHyst || !wasLatched) jetLatch = eye >= SwimThresholds.JetpackOnEyeY;
            else jetLatch = s.JetLatch ? eye >= SwimThresholds.JetpackOffEyeY : eye >= SwimThresholds.JetpackOnEyeY;
            bool jetAllowed = !latched || jetLatch || (mut & SwimMutants.M2JetpackInWater) != 0;
            if (jetAllowed && s.JetpackDenied) events |= SwimEvents.JetpackGranted;
            if (!jetAllowed && !s.JetpackDenied) events |= SwimEvents.JetpackRevoked;

            // 3. mode
            uint mode;
            if (!latched) mode = SwimMode.Walk;
            else if (i.JetpackOn && jetAllowed) mode = SwimMode.Fly;
            else if (eye < SwimThresholds.UnderwaterEyeY) mode = SwimMode.Swim;
            else mode = SwimMode.Surface;
            bool controls = mode == SwimMode.Swim || mode == SwimMode.Surface;
            // 3b. yield: a ladder or a grid deck under the feet hands the tick to SE's own movement
            uint yield = SwimYield.Of(latched, mode, i.OnLadder, i.OnGrid, i.Grounded, i.JetpackOn, i.MoveVertical);
            if (yield != SwimYield.None) controls = false;

            // 4. motor
            Vec3 velocity;
            if (controls)
            {
                bool resume = !s.Controlling;
                if (resume) events |= SwimEvents.Resumed;
                int phase = resume ? 0 : s.Phase;
                bool halve = (events & (SwimEvents.Entered | SwimEvents.MotorSwitch)) != 0;
                if (phase == 0)
                {
                    Vec3 v0 = i.Velocity;
                    if (halve) v0 = new Vec3(v0.X, v0.Y * SwimThresholds.EntryVerticalFactor, v0.Z);
                    double maxSpeed, cap;
                    s.Held = MotorStep(v0, i.MoveStrafe, i.MoveVertical, i.MoveForward, i.ToolHeld, i.Grounded, i.LookForward,
                                       i.LookRight, eye, (mut & SwimMutants.M1Numbers) != 0, i.FinsModifier, out maxSpeed, out cap);
                    o.SnStep = true;
                    o.MaxSpeed = maxSpeed;
                    o.SpeedCap = cap;
                }
                else if (halve)
                {
                    // between two 0.1 s steps: Subnautica halves the body's current vertical velocity at once
                    s.Held = new Vec3(s.Held.X, s.Held.Y * SwimThresholds.EntryVerticalFactor, s.Held.Z);
                }
                velocity = s.Held;
                s.Phase = (phase + 1) % SwimThresholds.SnStepTicks;
            }
            else
            {
                velocity = i.Velocity;
                s.Held = Vec3.Zero;
                s.Phase = 0;
            }

            // 5. oxygen
            double depth = Math.Max(0.0, -eye);
            uint dc = depth > SwimThresholds.CrushDepth ? SwimDepthClass.Crush
                : depth > SwimThresholds.UnsafeDepth ? SwimDepthClass.Unsafe
                : depth > SwimThresholds.SafeDepth ? SwimDepthClass.Safe
                : SwimDepthClass.Surface;
            bool ruleActive = (mut & SwimMutants.M4OxygenOff) == 0 && (latched || eye < SwimThresholds.UnderwaterEyeY);
            double delta = 0.0;
            if (ruleActive)
            {
                double o2 = i.Oxygen;
                double after = o2;
                if (eye < SwimThresholds.UnderwaterEyeY)
                {
                    int period;
                    double amount;
                    BreathOf(dc, out period, out amount);
                    if (period > 0 && i.Tick % (ulong)period == 0)
                    {
                        double removed = Math.Min(amount, after);
                        after = Math.Max(0.0, after - removed);
                        events |= SwimEvents.Breath;
                    }
                }
                if (eye > SwimThresholds.OxygenRefillEyeY)
                {
                    double add = SwimThresholds.OxygenRefillPerSecond * SwimThresholds.SeTickDt;
                    double added = Math.Min(add, SwimThresholds.OxygenCapacity - after);
                    after += added;
                }
                delta = after - o2;
            }

            o.Mode = mode;
            o.ControlsVelocity = controls;
            o.Velocity = velocity;
            o.PositionDelta = new Vec3(velocity.X * SwimThresholds.SeTickDt, velocity.Y * SwimThresholds.SeTickDt,
                                       velocity.Z * SwimThresholds.SeTickDt);
            o.JetpackAllowed = jetAllowed;
            o.OxygenRuleActive = ruleActive;
            o.OxygenDelta = delta;
            o.DepthClass = dc;
            o.Events = events;
            o.Changed = mode != s.Mode || yield != s.Yield;
            o.Yield = yield;

            s.Steps++;
            s.Latched = latched;
            s.JetLatch = jetLatch;
            s.JetpackDenied = !jetAllowed;
            s.Controlling = controls;
            s.Mode = mode;
            s.EyeUnder = under;
            s.Yield = yield;
        }

        /// <summary>Breath period (SE ticks; 0 = no breath) and oxygen per breath of a depth class.</summary>
        public static void BreathOf(uint depthClass, out int periodTicks, out double amount)
        {
            switch (depthClass)
            {
                case SwimDepthClass.Crush:
                    periodTicks = SwimThresholds.BreathTicksCrush;
                    amount = SwimThresholds.BreathPeriodCrush * SwimThresholds.BreathFactorCrush;
                    return;
                case SwimDepthClass.Unsafe:
                    periodTicks = SwimThresholds.BreathTicksUnsafe;
                    amount = SwimThresholds.BreathPeriodUnsafe * SwimThresholds.BreathFactorUnsafe;
                    return;
                case SwimDepthClass.Safe:
                    periodTicks = SwimThresholds.BreathTicksSafe;
                    amount = SwimThresholds.BreathPeriodSafe * SwimThresholds.BreathFactorSafe;
                    return;
                default:
                    // Surface class (eye within 0.1 m under the surface): Subnautica's period is 99,999 s; no breath.
                    periodTicks = 0;
                    amount = 0.0;
                    return;
            }
        }

        /// <summary>
 /// One Subnautica motor step of 0.1 s from the velocity <paramref name="v0"/>: returns the velocity the body keeps
 /// for the step (after the physics engine's damping). <paramref name="contact"/>: the diver touches something
 /// (the contact acceleration). See <see cref="SwimModel"/> remarks for the rule.
 /// </summary>
        public static Vec3 MotorStep(Vec3 v0, double moveX, double moveY, double moveZ, bool toolHeld, bool contact, Vec3 lookForward,
                                     Vec3 lookRight, double eyeY, bool mutantM1, out double maxSpeed, out double cap)
        {
            return MotorStep(v0, moveX, moveY, moveZ, toolHeld, contact, lookForward, lookRight, eyeY, mutantM1, 0.0, out maxSpeed, out cap);
        }

        /// <summary>
 /// As <see cref="MotorStep(Vec3, double, double, double, bool, bool, Vec3, Vec3, double, bool, out double, out double)"/>
 /// with the S5a fins modifier <paramref name="fins"/>
 /// </summary>
        public static Vec3 MotorStep(Vec3 v0, double moveX, double moveY, double moveZ, bool toolHeld, bool contact, Vec3 lookForward,
                                     Vec3 lookRight, double eyeY, bool mutantM1, double fins, out double maxSpeed, out double cap)
        {
            double scale = mutantM1 ? SwimThresholds.MutantM1Scale : 1.0;
            bool dive = eyeY < SwimThresholds.UnderwaterEyeY;
            double fwdMax = (dive ? SwimThresholds.DiveForwardMax : SwimThresholds.RunForwardMax) * scale;
            double backMax = (dive ? SwimThresholds.DiveBackwardMax : SwimThresholds.RunBackwardMax) * scale;
            double strafeMax = (dive ? SwimThresholds.DiveStrafeMax : SwimThresholds.RunStrafeMax) * scale;
            double vertMax = SwimThresholds.VerticalMax * scale;
            // groundAcceleration while grounded (any contact), else the water acceleration
            double accel = (contact ? SwimThresholds.GroundAcceleration : SwimThresholds.WaterAcceleration) * scale;

            double num = Math.Min(1.0, Math.Sqrt(moveX * moveX + moveY * moveY + moveZ * moveZ));
            double hl = Math.Sqrt(moveX * moveX + moveZ * moveZ);
            double hx = 0.0, hz = 0.0;
            if (hl > SwimThresholds.NormalizeEpsilon)
            {
                hx = moveX / hl;
                hz = moveZ / hl;
            }

            // max speed and AlterMaxSpeed for SE (no tank or suit; fins since S5a)
            double a = 0.0;
            if (hz > 0.0) a = fwdMax;
            else if (hz < 0.0) a = backMax;
            if (hx != 0.0) a = Math.Max(a, strafeMax);
            a = Math.Max(a, vertMax);
            double baseA = a;
            if (fins > 0.0) a += fins;
            if (toolHeld) a -= SwimThresholds.HeldToolPenalty;
            if (eyeY > SwimThresholds.UnderwaterEyeY) a *= SwimThresholds.SurfaceSpeedMultiplier;
            double ratio = a / baseA;
            double speed0 = Math.Sqrt(v0.X * v0.X + v0.Y * v0.Y + v0.Z * v0.Z);
            cap = Math.Max(speed0, a);
            maxSpeed = a;

            // direction: the camera's horizontal right (roll removed) and forward (with pitch), then the vertical input up
            double rx = lookRight.X, rz = lookRight.Z;
            double rl = Math.Sqrt(rx * rx + rz * rz);
            if (rl > SwimThresholds.NormalizeEpsilon)
            {
                rx = rx / rl;
                rz = rz / rl;
            }
            else
            {
                rx = 0.0;
                rz = 0.0;
            }
            double wx = hx * rx + hz * lookForward.X;
            double wy = hz * lookForward.Y;
            double wz = hx * rz + hz * lookForward.Z;
            double dx = wx, dy = wy + moveY, dz = wz;
            double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dl > SwimThresholds.NormalizeEpsilon)
            {
                dx = dx / dl;
                dy = dy / dl;
                dz = dz / dl;
            }
            else
            {
                dx = 0.0;
                dy = 0.0;
                dz = 0.0;
            }

            double dv = num * accel * SwimThresholds.SnStepDt;
            if (ratio > 1.0) dv = dv * ratio;
            double v1x, v1y, v1z;
            if (dv > 0.0)
            {
                v1x = v0.X + dx * dv;
                v1y = v0.Y + dy * dv;
                v1z = v0.Z + dz * dv;
                double l = Math.Sqrt(v1x * v1x + v1y * v1y + v1z * v1z);
                if (l > cap)
                {
                    v1x = v1x / l * cap;
                    v1y = v1y / l * cap;
                    v1z = v1z / l * cap;
                }
                bool downInput = moveY < 0.0;
                bool swimmingDown = wy < SwimThresholds.LookDownY;
                if (eyeY >= SwimThresholds.SurfaceBandBottomY && !downInput && !swimmingDown)
                {
                    double t = (SwimThresholds.SurfaceTopY - eyeY) / (SwimThresholds.SurfaceTopY - SwimThresholds.SurfaceBandBottomY);
                    if (t < 0.0) t = 0.0;
                    else if (t > 1.0) t = 1.0;
                    v1y = v1y * Math.Pow(t, SwimThresholds.SurfaceExponent);
                }
            }
            else
            {
                v1x = v0.X;
                v1y = v0.Y;
                v1z = v0.Z;
            }

            double k = Math.Max(0.0, 1.0 - SwimThresholds.SnStepDt * SwimThresholds.SwimDrag);
            return new Vec3(v1x * k, v1y * k, v1z * k);
        }
    }

    /// <summary>
 /// One JSONL row per swim step ("swim" rows of <c>se-*.jsonl</c>), replayed by the independent oracle
 /// <c>tests/s2b/swim_replay.py</c>. The row holds the step index, the inputs, the state before the step and the
 /// outputs, so the oracle can replay contiguous rows with its own state and reseed after a gap. Every double is
 /// logged as its exact IEEE-754 bits (16 lower-case hex digits, in quotes); a few readable decimals are added for
 /// people (<c>y</c>, <c>spd</c>, <c>o2</c>).
 /// <code>
 /// {"ev":"swim","v","n":step,"tick":T,["seg":label,]
 /// readable "md":mode,"st":"Walk|Swim|Surface|Fly","inW":latch,"y":eye,"yHead":eye,"spd":|v out|,"o2":suit fraction,
 /// "o2u":oxygen in (Subnautica units),"move":[x,y,z],"vel":[v out],"pos":[eye],"look":[forward],
 /// "jetOk":allowed,["tr":"enter"|"exit",]
 /// inputs "mx","my","mz","fx","fy","fz","rx","ry","rz","vx","vy","vz","px","py","pz","ox","fin" (bits; "fin" =
 /// the S5a fins modifier, absent in rows written before S5a = 0),
 /// "spr","tool","gnd","jet","mut",
 /// state "s_lat","s_jl","s_jd","s_ctl","s_ph","s_hx","s_hy","s_hz"(bits),"s_md","s_eu" (before the step)
 /// outputs "o_md","o_ctl","o_vx","o_vy","o_vz"(bits),"o_st","o_max","o_cap"(bits),"o_ja","o_or","o_do2"(bits),
 /// "o_dc","o_evt","o_chg"}
 /// </code>
 /// Booleans are 0/1. The readable keys (decimals, rounded) serve people and the S2b analyser
 /// (<c>tests/s2b/analyze_swim.py</c>: <c>vel</c> is the velocity set this tick, <c>pos</c> and <c>yHead</c> the eye, <c>o2</c>
 /// the suit fill level 0..1); the replay reads the bits only. Formatting allocates nothing (<see cref="Format"/>
 /// writes into a caller-owned buffer; the optional segment label is copied without its quotes and backslashes).
 /// </summary>
    public static class SwimTrace
    {
        public const string Event = "swim";
        public const int Version = 1;
        /// <summary>A buffer of this many chars always holds one row.</summary>
        public const int MaxRowChars = 2048;

        /// <summary>
 /// Formats the row of one step into <paramref name="buf"/>; returns its length, or −1 if the buffer is too
 /// small. <paramref name="pre"/> is the state before <see cref="SwimModel.Step"/>.
 /// </summary>
        public static int Format(ref SwimState pre, ref SwimInputs i, ref SwimOutputs o, char[] buf)
        {
            return Format(ref pre, ref i, ref o, buf, null);
        }

        /// <summary>As <see cref="Format(ref SwimState, ref SwimInputs, ref SwimOutputs, char[])"/>, with a script segment label (<c>"seg"</c>; null for none).</summary>
        public static int Format(ref SwimState pre, ref SwimInputs i, ref SwimOutputs o, char[] buf, string segment)
        {
            var w = new Sink(buf);
            w.Raw("{\"ev\":\"swim\",\"v\":1,\"n\":"); w.U64(pre.Steps);
            w.Raw(",\"tick\":"); w.U64(i.Tick);
            if (segment != null) { w.Raw(",\"seg\":\""); w.Label(segment); w.Raw("\""); }
            w.Raw(",\"md\":"); w.U64(o.Mode);
            w.Raw(",\"st\":\""); w.Raw(SwimMode.Name(o.Mode)); w.Raw("\"");
            w.Raw(",\"inW\":"); w.Bit(o.Mode != SwimMode.Walk);
            w.Raw(",\"y\":"); w.Fixed(i.EyePos.Y, 4);
            w.Raw(",\"yHead\":"); w.Fixed(i.EyePos.Y, 4);
            w.Raw(",\"spd\":"); w.Fixed(o.Velocity.Length, 4);
            w.Raw(",\"o2\":"); w.Fixed(i.Oxygen / SwimThresholds.OxygenCapacity, 5);
            w.Raw(",\"o2u\":"); w.Fixed(i.Oxygen, 3);
            w.Raw(",\"move\":"); w.Vec(i.MoveStrafe, i.MoveVertical, i.MoveForward, 6);
            w.Raw(",\"vel\":"); w.Vec(o.Velocity.X, o.Velocity.Y, o.Velocity.Z, 6);
            w.Raw(",\"pos\":"); w.Vec(i.EyePos.X, i.EyePos.Y, i.EyePos.Z, 4);
            w.Raw(",\"look\":"); w.Vec(i.LookForward.X, i.LookForward.Y, i.LookForward.Z, 6);
            w.Raw(",\"jetOk\":"); w.Bit(o.JetpackAllowed);
            if ((o.Events & SwimEvents.Entered) != 0) w.Raw(",\"tr\":\"enter\"");
            else if ((o.Events & SwimEvents.Exited) != 0) w.Raw(",\"tr\":\"exit\"");

            w.Raw(",\"mx\":"); w.Bits(i.MoveStrafe);
            w.Raw(",\"my\":"); w.Bits(i.MoveVertical);
            w.Raw(",\"mz\":"); w.Bits(i.MoveForward);
            w.Raw(",\"fx\":"); w.Bits(i.LookForward.X);
            w.Raw(",\"fy\":"); w.Bits(i.LookForward.Y);
            w.Raw(",\"fz\":"); w.Bits(i.LookForward.Z);
            w.Raw(",\"rx\":"); w.Bits(i.LookRight.X);
            w.Raw(",\"ry\":"); w.Bits(i.LookRight.Y);
            w.Raw(",\"rz\":"); w.Bits(i.LookRight.Z);
            w.Raw(",\"vx\":"); w.Bits(i.Velocity.X);
            w.Raw(",\"vy\":"); w.Bits(i.Velocity.Y);
            w.Raw(",\"vz\":"); w.Bits(i.Velocity.Z);
            w.Raw(",\"px\":"); w.Bits(i.EyePos.X);
            w.Raw(",\"py\":"); w.Bits(i.EyePos.Y);
            w.Raw(",\"pz\":"); w.Bits(i.EyePos.Z);
            w.Raw(",\"ox\":"); w.Bits(i.Oxygen);
            w.Raw(",\"fin\":"); w.Bits(i.FinsModifier);
            w.Raw(",\"spr\":"); w.Bit(i.Sprint);
            w.Raw(",\"tool\":"); w.Bit(i.ToolHeld);
            w.Raw(",\"gnd\":"); w.Bit(i.Grounded);
            w.Raw(",\"jet\":"); w.Bit(i.JetpackOn);
            w.Raw(",\"lad\":"); w.Bit(i.OnLadder);
            w.Raw(",\"grd\":"); w.Bit(i.OnGrid);
            w.Raw(",\"mut\":"); w.U64(i.Mutants);

            w.Raw(",\"s_lat\":"); w.Bit(pre.Latched);
            w.Raw(",\"s_jl\":"); w.Bit(pre.JetLatch);
            w.Raw(",\"s_jd\":"); w.Bit(pre.JetpackDenied);
            w.Raw(",\"s_ctl\":"); w.Bit(pre.Controlling);
            w.Raw(",\"s_ph\":"); w.U64((ulong)Math.Max(0, pre.Phase));
            w.Raw(",\"s_hx\":"); w.Bits(pre.Held.X);
            w.Raw(",\"s_hy\":"); w.Bits(pre.Held.Y);
            w.Raw(",\"s_hz\":"); w.Bits(pre.Held.Z);
            w.Raw(",\"s_md\":"); w.U64(pre.Mode);
            w.Raw(",\"s_eu\":"); w.Bit(pre.EyeUnder);
            w.Raw(",\"s_yld\":"); w.U64(pre.Yield);

            w.Raw(",\"o_md\":"); w.U64(o.Mode);
            w.Raw(",\"o_ctl\":"); w.Bit(o.ControlsVelocity);
            w.Raw(",\"o_vx\":"); w.Bits(o.Velocity.X);
            w.Raw(",\"o_vy\":"); w.Bits(o.Velocity.Y);
            w.Raw(",\"o_vz\":"); w.Bits(o.Velocity.Z);
            w.Raw(",\"o_st\":"); w.Bit(o.SnStep);
            w.Raw(",\"o_max\":"); w.Bits(o.MaxSpeed);
            w.Raw(",\"o_cap\":"); w.Bits(o.SpeedCap);
            w.Raw(",\"o_ja\":"); w.Bit(o.JetpackAllowed);
            w.Raw(",\"o_or\":"); w.Bit(o.OxygenRuleActive);
            w.Raw(",\"o_do2\":"); w.Bits(o.OxygenDelta);
            w.Raw(",\"o_dc\":"); w.U64(o.DepthClass);
            w.Raw(",\"o_evt\":"); w.U64(o.Events);
            w.Raw(",\"o_chg\":"); w.Bit(o.Changed);
            w.Raw(",\"o_yld\":"); w.U64(o.Yield);
            w.Raw("}");
            return w.Overflow ? -1 : w.Length;
        }

        /// <summary>Convenience for logs and tests (allocates the string).</summary>
        public static string FormatString(ref SwimState pre, ref SwimInputs i, ref SwimOutputs o)
        {
            var buf = new char[MaxRowChars];
            int n = Format(ref pre, ref i, ref o, buf);
            return n < 0 ? null : new string(buf, 0, n);
        }

        /// <summary>Appends chars to a fixed buffer without allocating. A mutable struct: keep it in a local.</summary>
        private struct Sink
        {
            private static readonly ulong[] Pow10 = { 1UL, 10UL, 100UL, 1000UL, 10000UL, 100000UL, 1000000UL };
            private const string Hex = "0123456789abcdef";
            private readonly char[] _buf;
            private int _n;

            public Sink(char[] buf)
            {
                _buf = buf;
                _n = 0;
                Overflow = buf == null;
            }

            public bool Overflow { get; private set; }
            public int Length { get { return _n; } }

            private bool Room(int count)
            {
                if (Overflow) return false;
                if (_n + count > _buf.Length) { Overflow = true; return false; }
                return true;
            }

            public void Raw(string s)
            {
                if (!Room(s.Length)) return;
                for (int k = 0; k < s.Length; k++) _buf[_n++] = s[k];
            }

            public void Bit(bool b)
            {
                if (!Room(1)) return;
                _buf[_n++] = b ? '1' : '0';
            }

            public void U64(ulong v)
            {
                int len = 1;
                for (ulong t = v / 10; t != 0; t /= 10) len++;
                if (!Room(len)) return;
                for (int k = len - 1; k >= 0; k--)
                {
                    _buf[_n + k] = (char)('0' + (int)(v % 10));
                    v /= 10;
                }
                _n += len;
            }

            /// <summary>Fixed-point decimal (half away from zero); <c>null</c> for NaN, infinities and huge values.</summary>
            public void Fixed(double v, int decimals)
            {
                if (double.IsNaN(v) || double.IsInfinity(v) || decimals < 0 || decimals >= Pow10.Length) { Raw("null"); return; }
                ulong scale = Pow10[decimals];
                double scaled = Math.Round(Math.Abs(v) * scale, MidpointRounding.AwayFromZero);
                if (scaled >= 9.0e18) { Raw("null"); return; }
                ulong q = (ulong)scaled;
                if (v < 0 && q != 0) Raw("-");
                U64(q / scale);
                if (decimals == 0) return;
                Raw(".");
                ulong frac = q % scale;
                for (int k = decimals - 1; k >= 0; k--)
                {
                    if (!Room(1)) return;
                    _buf[_n++] = (char)('0' + (int)(frac / Pow10[k] % 10));
                }
            }

            /// <summary>Three fixed-point decimals as a JSON array.</summary>
            public void Vec(double x, double y, double z, int decimals)
            {
                Raw("[");
                Fixed(x, decimals);
                Raw(",");
                Fixed(y, decimals);
                Raw(",");
                Fixed(z, decimals);
                Raw("]");
            }

            /// <summary>A label's characters without quotes, backslashes and control characters (no allocation).</summary>
            public void Label(string s)
            {
                for (int k = 0; k < s.Length; k++)
                {
                    char c = s[k];
                    if (c == '"' || c == '\\' || c < ' ') continue;
                    if (!Room(1)) return;
                    _buf[_n++] = c;
                }
            }

            /// <summary>A double's IEEE-754 bits as 16 lower-case hex digits, in quotes.</summary>
            public void Bits(double v)
            {
                ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(v));
                if (!Room(18)) return;
                _buf[_n++] = '"';
                for (int k = 15; k >= 0; k--) _buf[_n++] = Hex[(int)((bits >> (4 * k)) & 0xF)];
                _buf[_n++] = '"';
            }
        }
    }
}
