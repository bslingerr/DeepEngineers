using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S2b swim model (<see cref="SwimModel"/>, CONTRACT §2.10): Subnautica's motor numbers and shapes, the surface rule,
 /// the swim and jetpack hysteresis, the oxygen rule, the seeded failures S2b.M1–M4, the "swim" rows
 /// (<see cref="SwimTrace"/>) and the cross-check against the independent Python replay <c>tests/s2b/swim_replay.py</c>.
 /// </summary>
    [TestClass]
    public class SwimModelTests
    {
        private const double Tight = 1e-12;
        private const double Dt = SwimThresholds.SeTickDt;

        /// <summary>
 /// A scripted SE stand-in around the model: perfect SE physics while the model controls (the next measured velocity
 /// is the imposed one), a 3 m/s jetpack climb in Fly, gravity and an optional floor in Walk.
 /// </summary>
        private sealed class SwimSim
        {
            public SwimState S;
            public ulong Tick = 600;
            public Vec3 P = new Vec3(100.0, -50.0, -200.0);
            public Vec3 V = Vec3.Zero;
            public double O2 = SwimThresholds.OxygenCapacity;
            public uint Mutants;
            public double Mx, My, Mz;
            public bool Sprint, Tool, Grounded, Jet, OnLadder, OnGrid;
            public Vec3 F = new Vec3(0.0, 0.0, 1.0);
            public Vec3 Rt = new Vec3(1.0, 0.0, 0.0);
            public double? Floor;
            public bool ApplyOxygen = true;
            public readonly List<string> Rows = new List<string>();
            public readonly List<SwimOutputs> Outs = new List<SwimOutputs>();
            public readonly List<Vec3> Eyes = new List<Vec3>();
            public bool TraceRows;

            public SwimSim(double eyeY = -50.0) { P = new Vec3(100.0, eyeY, -200.0); }

            public void Move(double x, double y, double z) { Mx = x; My = y; Mz = z; }

            public void Look(double pitchDeg)
            {
                double r = pitchDeg * Math.PI / 180.0;
                F = new Vec3(0.0, Math.Sin(r), Math.Cos(r));
            }

            public SwimInputs Inputs()
            {
                return new SwimInputs
                {
                    Tick = Tick, MoveStrafe = Mx, MoveVertical = My, MoveForward = Mz, Sprint = Sprint, ToolHeld = Tool,
                    Grounded = Grounded, JetpackOn = Jet, OnLadder = OnLadder, OnGrid = OnGrid, LookForward = F, LookRight = Rt, Velocity = V, EyePos = P,
                    Oxygen = O2, Mutants = Mutants,
                };
            }

            public SwimOutputs Step()
            {
                SwimInputs i = Inputs();
                SwimState pre = S;
                SwimOutputs o;
                SwimModel.Step(ref S, ref i, out o);
                if (TraceRows) Rows.Add(SwimTrace.FormatString(ref pre, ref i, ref o));
                Outs.Add(o);
                Eyes.Add(P);
                Vec3 v;
                if (o.ControlsVelocity) v = o.Velocity;
                else if (o.Mode == SwimMode.Fly) v = new Vec3(0.0, My > 0 ? 3.0 : 0.0, 0.0);
                else if (Floor.HasValue && P.Y <= Floor.Value + 1e-9 && V.Y <= 0.0) v = Vec3.Zero;
                else v = new Vec3(V.X * 0.98, V.Y - 9.81 * Dt, V.Z * 0.98);
                V = v;
                double ny = P.Y + v.Y * Dt;
                if (Floor.HasValue && o.Mode == SwimMode.Walk && ny < Floor.Value)
                {
                    ny = Floor.Value;
                    V = Vec3.Zero;
                }
                P = new Vec3(P.X + v.X * Dt, ny, P.Z + v.Z * Dt);
                if (ApplyOxygen && o.OxygenRuleActive) O2 = O2 + o.OxygenDelta;
                Tick++;
                return o;
            }

            public void Run(int ticks)
            {
                for (int k = 0; k < ticks; k++) Step();
            }

            /// <summary>Sets the eye height directly (a scripted path, not physics) and steps once.</summary>
            public SwimOutputs At(double eyeY)
            {
                P = new Vec3(P.X, eyeY, P.Z);
                V = Vec3.Zero;
                return Step();
            }
        }

        private static double Recurrence(double cap, int steps, double accelPerStep = 2.0)
        {
            double v = 0.0;
            for (int n = 0; n < steps; n++) v = Math.Min(v + accelPerStep, cap) * 0.75;
            return v;
        }

        // ------------------------------------------------------------------------------------------- motor numbers
        [TestMethod]
        public void Forward_FromRest_FollowsSubnauticasTenHertzSteps()
        {
            var sim = new SwimSim();
            sim.Move(0, 0, 1);
            sim.Run(180);
            for (int t = 0; t < 180; t++)
            {
                SwimOutputs o = sim.Outs[t];
                Assert.AreEqual(SwimMode.Swim, o.Mode);
                Assert.IsTrue(o.ControlsVelocity);
                Assert.AreEqual(t % 6 == 0, o.SnStep, "a Subnautica step every 6 SE ticks (tick " + t + ")");
                double want = Recurrence(7.6, t / 6 + 1);
                Assert.AreEqual(want, o.Velocity.Z, Tight, "tick " + t);
                Assert.AreEqual(0.0, o.Velocity.X, Tight);
                Assert.AreEqual(0.0, o.Velocity.Y, Tight);
                Assert.AreEqual(o.Velocity.Z * Dt, o.PositionDelta.Z, Tight);
            }
            Assert.AreEqual(5.7, sim.Outs[179].Velocity.Z, Tight, "steady = 7.6 x 0.75");
            Assert.AreEqual(1.5, sim.Outs[0].Velocity.Z, Tight, "first step: +2 m/s, x0.75");
            int t90 = sim.Outs.FindIndex(o => o.Velocity.Length >= 0.9 * 5.7);
            Assert.AreEqual(36, t90, "90 % of the top speed from the 7th step (0.6 s)");
            int full = sim.Outs.FindIndex(o => Math.Abs(o.Velocity.Length - 5.7) < Tight);
            Assert.AreEqual(60, full, "the cap from the 11th step (1.0 s)");
            Assert.AreEqual(7.6, sim.Outs[60].SpeedCap, Tight);
            Assert.AreEqual(7.6, sim.Outs[60].MaxSpeed, Tight);
        }

        [TestMethod]
        public void Release_GlidesTheSubnauticaStopDistance()
        {
            var sim = new SwimSim();
            sim.Move(0, 0, 1);
            sim.Run(180);
            double z0 = sim.P.Z;
            sim.Move(0, 0, 0);
            sim.Run(240);
            double want = 0.0;
            for (int n = 1; n <= 40; n++) want += 0.1 * 5.7 * Math.Pow(0.75, n);
            Assert.AreEqual(want, sim.P.Z - z0, 1e-9, "stop distance over 4 s (1.71 m in the limit)");
            Assert.AreEqual(1.71, 0.1 * 5.7 * 0.75 / 0.25, Tight);
            SwimOutputs first = sim.Outs[180];
            Assert.IsTrue(first.SnStep);
            Assert.AreEqual(5.7 * 0.75, first.Velocity.Z, Tight, "no input: only the damping");
            Assert.AreEqual(6.3, first.SpeedCap, Tight, "cap = max(current speed, the vertical maximum), unused without input");
            int stopped = sim.Outs.FindIndex(180, o => o.Velocity.Length < 0.05) - 180;
            Assert.AreEqual(96, stopped, "below 0.05 m/s after 17 steps");
        }

        [TestMethod]
        public void DirectionMaxima_StrafeBackwardVerticalDiagonalToolSprint()
        {
            Func<Action<SwimSim>, Vec3> steady = setup =>
            {
                var s = new SwimSim();
                setup(s);
                s.Run(240);
                return s.Outs[239].Velocity;
            };
            Vec3 back = steady(s => s.Move(0, 0, -1));
            Assert.AreEqual(-5.7, back.Z, Tight, "backward 7.6 (scene value)");
            Vec3 strafe = steady(s => s.Move(1, 0, 0));
            Assert.AreEqual(5.7, strafe.X, Tight, "strafe 7.6 along the camera right");
            Vec3 left = steady(s => s.Move(-1, 0, 0));
            Assert.AreEqual(-5.7, left.X, Tight);
            Vec3 up = steady(s => s.Move(0, 1, 0));
            Assert.AreEqual(6.3 * 0.75, up.Y, Tight, "vertical 6.3 (scene value)");
            Vec3 down = steady(s => s.Move(0, -1, 0));
            Assert.AreEqual(-6.3 * 0.75, down.Y, Tight);
            Vec3 diag = steady(s => s.Move(0, 1, 1));
            Assert.AreEqual(5.7, diag.Length, 1e-9, "diagonal: one cap (input magnitude clamped to 1)");
            Assert.AreEqual(diag.Y, diag.Z, 1e-9, "forward + up = 45 degrees up");
            Vec3 tool = steady(s => { s.Move(0, 0, 1); s.Tool = true; });
            Assert.AreEqual(6.6 * 0.75, tool.Z, Tight, "a held tool: -1 m/s");
            Vec3 toolUp = steady(s => { s.Move(0, 1, 0); s.Tool = true; });
            Assert.AreEqual(5.3 * 0.75, toolUp.Y, Tight);
            Vec3 half = steady(s => s.Move(0, 0, 0.5));
            Assert.AreEqual(3.0, half.Z, 1e-3, "an analog input scales the acceleration: equilibrium 6 x input below the cap");

            var a = new SwimSim();
            var b = new SwimSim { Sprint = true };
            a.Move(0, 0, 1);
            b.Move(0, 0, 1);
            a.Run(300);
            b.Run(300);
            for (int t = 0; t < 300; t++)
                Assert.AreEqual(a.Outs[t].Velocity.Z, b.Outs[t].Velocity.Z, 0.0, "Subnautica's swim motor has no sprint");
        }

        [TestMethod]
        public void LookDirection_IsFree3D_PitchOnly_RollIgnored()
        {
            var down = new SwimSim();
            down.Look(-45.0);
            down.Move(0, 0, 1);
            down.Run(240);
            Vec3 v = down.Outs[239].Velocity;
            Assert.AreEqual(5.7, v.Length, 1e-9);
            Assert.AreEqual(-v.Y, v.Z, 1e-9, "45 degrees down");
            Assert.AreEqual(0.0, v.X, Tight);

            var level = new SwimSim();
            level.Move(1, 0, 0);
            level.Run(120);
            var rolled = new SwimSim { Rt = new Vec3(Math.Cos(0.4), Math.Sin(0.4), 0.0) };
            rolled.Move(1, 0, 0);
            rolled.Run(120);
            for (int t = 0; t < 120; t++)
            {
                Assert.AreEqual(level.Outs[t].Velocity.X, rolled.Outs[t].Velocity.X, 1e-12, "roll removed (tick " + t + ")");
                Assert.AreEqual(0.0, rolled.Outs[t].Velocity.Y, Tight, "strafe stays horizontal");
            }

            // forward + up while looking straight down: the two cancel (Unity's Normalize gives zero), only damping
            var cancel = new SwimSim();
            cancel.Look(-90.0);
            cancel.F = new Vec3(0.0, -1.0, 0.0);
            cancel.Move(0, 1, 1);
            cancel.V = new Vec3(0.0, 0.0, 2.0);
            SwimOutputs o = cancel.Step();
            Assert.AreEqual(1.5, o.Velocity.Z, Tight);
            Assert.AreEqual(0.0, o.Velocity.Y, Tight);
        }

        [TestMethod]
        public void Surface_RisesToTheHeadOut_ThenNoHigher_DownStillWorks()
        {
            var sim = new SwimSim(-20.0);
            sim.Move(0, 1, 0);
            sim.Run(12 * 60);
            double maxEye = sim.Eyes.Where((p, k) => sim.Outs[k].ControlsVelocity).Max(p => p.Y);
            Assert.IsTrue(maxEye > SwimThresholds.SurfaceTopY && maxEye < SwimThresholds.SwimExitEyeY,
                          "the 0.1 s steps overshoot +0.52 m a little, never the +0.8 m exit: " + maxEye);
            Assert.AreEqual(SwimMode.Surface, sim.Outs.Last().Mode);
            Assert.AreEqual(0.0, sim.Outs.Last().Velocity.Y, 0.0, "up held: no vertical speed above +0.52 m");
            Assert.IsTrue(sim.Outs.Last().JetpackAllowed, "head out: the jetpack is allowed again");
            Assert.IsTrue(sim.Outs.Take(sim.Outs.Count - 1).All(o => o.Mode != SwimMode.Walk), "never left the water");

            // surface speed: Run numbers (max(3.5, 6.3) = 6.3) x1.3 above 0, acceleration x1.3
            var surf = new SwimSim(0.3);
            surf.S.Latched = true;
            surf.S.Mode = SwimMode.Surface;
            surf.Move(0, 0, 1);
            SwimOutputs first = surf.Step();
            Assert.AreEqual(SwimMode.Surface, first.Mode);
            Assert.AreEqual(6.3 * 1.3, first.MaxSpeed, Tight);
            Assert.AreEqual(2.6 * 0.75, first.Velocity.Z, Tight, "acceleration x1.3");
            surf.Run(300);
            Assert.AreEqual(6.3 * 1.3 * 0.75, surf.Outs.Last().Velocity.Z, 1e-9, "6.14 m/s at the surface");
            Assert.AreEqual(0.3, surf.P.Y, Tight, "horizontal swim keeps the eye height");

            // at the surface: the down key and swimming down (looking down) bypass the rule
            var dn = new SwimSim(0.4);
            dn.S.Latched = true;
            dn.Move(0, -1, 0);
            Assert.AreEqual(-2.6 * 0.75, dn.Step().Velocity.Y, Tight, "Run numbers x1.3 above 0, no surface rule");
            var lookDown = new SwimSim(0.4);
            lookDown.S.Latched = true;
            lookDown.Look(-30.0);
            lookDown.Move(0, 0, 1);
            Assert.IsTrue(lookDown.Step().Velocity.Y < -0.9, "swimming down at the surface");
            // just below: the rule scales the vertical speed of a step from -0.5 m up
            var below = new SwimSim(-0.2);
            below.S.Latched = true;
            below.Move(0, 1, 0);
            double f = Math.Pow((0.52 + 0.2) / 1.02, 0.3);
            Assert.AreEqual(2.0 * f * 0.75, below.Step().Velocity.Y, 1e-12);
            var deeper = new SwimSim(-0.6);
            deeper.S.Latched = true;
            deeper.Move(0, 1, 0);
            Assert.AreEqual(1.5, deeper.Step().Velocity.Y, Tight, "no rule below -0.5 m");
        }

        // ------------------------------------------------------------------------------------------- water rules
        [TestMethod]
        public void SwimLatch_EntersBelowMinus01_LeavesAt08_Or01Grounded()
        {
            var sim = new SwimSim(1.0);
            Assert.AreEqual(SwimMode.Walk, sim.At(1.0).Mode);
            Assert.AreEqual(SwimMode.Walk, sim.At(-0.05).Mode, "eye under the surface, not yet 0.1 m");
            Assert.AreEqual(SwimMode.Walk, sim.At(-0.1).Mode, "strict");
            SwimOutputs enter = sim.At(-0.1000001);
            Assert.AreEqual(SwimMode.Swim, enter.Mode);
            Assert.AreEqual(SwimEvents.Entered, enter.Events & SwimEvents.Entered);
            Assert.IsTrue(enter.Changed);
            Assert.AreEqual(SwimMode.Surface, sim.At(0.5).Mode, "head out, still swimming");
            Assert.AreEqual(SwimMode.Surface, sim.At(0.7999).Mode);
            SwimOutputs exit = sim.At(0.8);
            Assert.AreEqual(SwimMode.Walk, exit.Mode, "+0.8 m: out of the water");
            Assert.AreEqual(SwimEvents.Exited, exit.Events & SwimEvents.Exited);
            Assert.IsFalse(exit.ControlsVelocity);

            var g = new SwimSim(-1.0);
            Assert.AreEqual(SwimMode.Swim, g.At(-1.0).Mode);
            g.Grounded = true;
            Assert.AreEqual(SwimMode.Surface, g.At(0.1).Mode, "the grounded test only above +0.1 m");
            Assert.AreEqual(SwimMode.Walk, g.At(0.1000001).Mode, "standing on something: out at +0.1 m");

            // bobbing inside the band after the entry: no transition; the S2b.M3 mutant bounces
            foreach (uint mut in new[] { 0u, SwimMutants.M3NoHysteresis })
            {
                var bob = new SwimSim(-1.0) { Mutants = mut };
                bob.At(-1.0);
                int changes = 0;
                for (int k = 0; k < 600; k++)
                {
                    SwimOutputs o = bob.At(0.35 + 0.4 * Math.Sin(2.0 * Math.PI * k / 120.0));
                    if ((o.Events & (SwimEvents.Entered | SwimEvents.Exited)) != 0) changes++;
                }
                if (mut == 0) Assert.AreEqual(0, changes, "hysteresis: no bouncing inside -0.1 .. +0.8");
                else Assert.IsTrue(changes >= 9, "S2b.M3 bounces at +0.35 m: " + changes);
            }
        }

        [TestMethod]
        public void Jetpack_OffInTheWater_BackWithTheHeadOut_FlyYields()
        {
            var sim = new SwimSim(2.0);
            Assert.IsTrue(sim.At(2.0).JetpackAllowed, "on land");
            SwimOutputs enter = sim.At(-1.0);
            Assert.IsFalse(enter.JetpackAllowed);
            Assert.AreEqual(SwimEvents.JetpackRevoked, enter.Events & SwimEvents.JetpackRevoked);
            Assert.IsFalse(sim.At(-0.05).JetpackAllowed, "still under the surface");
            SwimOutputs grant = sim.At(0.0);
            Assert.IsTrue(grant.JetpackAllowed, "head out");
            Assert.AreEqual(SwimEvents.JetpackGranted, grant.Events & SwimEvents.JetpackGranted);
            Assert.IsTrue(sim.At(-0.05).JetpackAllowed, "hysteresis band");
            Assert.IsFalse(sim.At(-0.1000001).JetpackAllowed, "back under at the swim-entry depth");

            sim.At(0.3);
            sim.Jet = true;
            sim.V = new Vec3(0.0, 1.0, 0.0);
            SwimInputs i = sim.Inputs();
            SwimOutputs fly;
            SwimModel.Step(ref sim.S, ref i, out fly);
            Assert.AreEqual(SwimMode.Fly, fly.Mode);
            Assert.IsFalse(fly.ControlsVelocity);
            Assert.AreEqual(1.0, fly.Velocity.Y, 0.0, "Fly: SE's jetpack moves the engineer");
            sim.Jet = false;
            SwimOutputs back = sim.At(0.3);
            Assert.AreEqual(SwimMode.Surface, back.Mode);
            Assert.IsTrue(back.SnStep, "control resumes with an immediate step");
            Assert.AreEqual(SwimEvents.Resumed, back.Events & SwimEvents.Resumed);

            var jetUnder = new SwimSim(-30.0) { Jet = true };
            SwimOutputs u = jetUnder.Step();
            Assert.AreEqual(SwimMode.Swim, u.Mode, "jetpack on under water: not allowed, the model swims");
            Assert.IsFalse(u.JetpackAllowed);
            var m2 = new SwimSim(-30.0) { Jet = true, Mutants = SwimMutants.M2JetpackInWater };
            SwimOutputs m = m2.Step();
            Assert.IsTrue(m.JetpackAllowed, "S2b.M2: jetpack left on in the water");
            Assert.AreEqual(SwimMode.Fly, m.Mode);
        }

        [TestMethod]
        public void MotorSwitch_EveryCrossingWithTheLatchOn_HalvesTheVerticalVelocity()
        {
            //  Player.Update -> UpdateMotorMode switches
 // Dive<->Run when the eye crosses 0; SetMotorMode -> PlayerController.SetMotorMode ->
 // HandleControllerState enables the swim motor again: halves vy.
            var s = new SwimState { Latched = true, Controlling = true, Mode = SwimMode.Swim, EyeUnder = true, Phase = 3, Held = new Vec3(0.0, 2.0, 1.0) };
            var i = new SwimInputs
            {
                MoveVertical = 1.0, LookForward = new Vec3(0.0, 0.0, 1.0), LookRight = new Vec3(1.0, 0.0, 0.0),
                Velocity = new Vec3(0.0, 2.0, 1.0), EyePos = new Vec3(0.0, 0.01, 0.0), Oxygen = 45.0,
            };
            SwimOutputs o;
            SwimModel.Step(ref s, ref i, out o);
            Assert.AreEqual(SwimEvents.MotorSwitch, o.Events & SwimEvents.MotorSwitch, "the eye crossed 0 upward");
            Assert.IsFalse(o.SnStep, "between two motor steps");
            Assert.AreEqual(1.0, o.Velocity.Y, Tight, "held vy halved at once");
            Assert.AreEqual(1.0, o.Velocity.Z, Tight, "horizontal kept");
            Assert.IsFalse(s.EyeUnder);
            i.EyePos = new Vec3(0.0, 0.02, 0.0);
            SwimModel.Step(ref s, ref i, out o);
            Assert.AreEqual(0u, o.Events & SwimEvents.MotorSwitch, "no second switch on the same side");
            Assert.AreEqual(1.0, o.Velocity.Y, Tight);

            // on a motor-step tick the measured velocity is halved before the step (eye going down through 0)
            var d = new SwimState { Latched = true, Controlling = true, Mode = SwimMode.Surface, EyeUnder = false, Phase = 0 };
            var di = new SwimInputs
            {
                LookForward = new Vec3(0.0, 0.0, 1.0), LookRight = new Vec3(1.0, 0.0, 0.0), Velocity = new Vec3(0.0, -2.0, 0.0),
                EyePos = new Vec3(0.0, -0.3, 0.0), Oxygen = 45.0,
            };
            SwimModel.Step(ref d, ref di, out o);
            Assert.AreEqual(SwimEvents.MotorSwitch, o.Events & SwimEvents.MotorSwitch);
            Assert.IsTrue(o.SnStep);
            Assert.AreEqual(-1.0 * 0.75, o.Velocity.Y, Tight, "-2 halved, no input, damped");

            // the latch off (Walk) crossing 0: no switch (the ground motor is active)
            var w = new SwimSim(0.05);
            Assert.AreEqual(SwimMode.Walk, w.At(0.05).Mode);
            Assert.AreEqual(0u, w.At(-0.05).Events & SwimEvents.MotorSwitch, "latch off: no swim motor to re-enable");

            // swimming up from -20 m: one switch on the way up, and the head stops lower than without it
            var up = new SwimSim(-20.0);
            up.Move(0, 1, 0);
            up.Run(12 * 60);
            Assert.AreEqual(1, up.Outs.Count(x => (x.Events & SwimEvents.MotorSwitch) != 0), "one crossing");
            double maxEye = up.Eyes.Where((p, k) => up.Outs[k].ControlsVelocity).Max(p => p.Y);
            Assert.AreEqual(0.5839, maxEye, 5e-5, "peak eye with the switch (+0.679 m without it)");
        }

        [TestMethod]
        public void ContactAcceleration_TouchingSomething_Uses45()
        {
            //  takes groundAcceleration
 // while grounded (any contact): 0 -> 3.375 -> 5.70 m/s in two 0.1 s steps (water: 1.5, 2.625,...)
            var sim = new SwimSim(-30.0) { Grounded = true };
            sim.S.Latched = true;
            sim.S.EyeUnder = true;
            sim.Move(0, 0, 1);
            Assert.AreEqual(4.5 * 0.75, sim.Step().Velocity.Z, Tight);
            sim.Run(5);
            Assert.AreEqual(Math.Min(3.375 + 4.5, 7.6) * 0.75, sim.Step().Velocity.Z, Tight, "the cap is reached on the second step");
            var water = new SwimSim(-30.0);
            water.S.Latched = true;
            water.S.EyeUnder = true;
            water.Move(0, 0, 1);
            Assert.AreEqual(2.0 * 0.75, water.Step().Velocity.Z, Tight);
            Assert.AreEqual(45.0, SwimThresholds.GroundAcceleration);
        }

        [TestMethod]
        public void Entry_HalvesTheVerticalVelocity()
        {
            var sim = new SwimSim(0.5);
            sim.At(0.5);
            sim.P = new Vec3(sim.P.X, -0.15, sim.P.Z);
            sim.V = new Vec3(0.0, -2.0, 1.0);
            SwimOutputs o = sim.Step();
            Assert.AreEqual(SwimEvents.Entered, o.Events & SwimEvents.Entered);
            Assert.AreEqual(-1.0 * 0.75, o.Velocity.Y, Tight, "-2 halved, then damped");
            Assert.AreEqual(1.0 * 0.75, o.Velocity.Z, Tight);
        }

        // ------------------------------------------------------------------------------------------- oxygen and pressure
        [TestMethod]
        public void Oxygen_SubnauticaBreathsByDepth_RefillNearTheSurface()
        {
            var sim = new SwimSim(-50.0) { Tick = 1 };
            sim.Run(50 * 60);
            List<int> breaths = Enumerable.Range(0, sim.Outs.Count).Where(k => (sim.Outs[k].Events & SwimEvents.Breath) != 0).ToList();
            Assert.AreEqual(16, breaths.Count, "a breath on every multiple of 180 ticks over 50 s");
            Assert.IsTrue(breaths.All(k => (k + 1) % 180 == 0));
            Assert.AreEqual(-3.0, sim.Outs[breaths[0]].OxygenDelta, Tight);
            Assert.AreEqual(0.0, sim.O2, Tight, "45 s of air at <= 100 m, then empty");
            Assert.AreEqual(0.0, sim.Outs[breaths[15]].OxygenDelta, Tight, "an empty suit loses nothing more");
            Assert.IsTrue(sim.Outs.All(o => o.OxygenRuleActive && o.DepthClass == SwimDepthClass.Safe));

            foreach (var c in new[] { new { y = -150.0, dc = SwimDepthClass.Unsafe, p = 135, a = 3.375 },
                                      new { y = -250.0, dc = SwimDepthClass.Crush, p = 90, a = 3.0 },
                                      new { y = -1000.0, dc = SwimDepthClass.Crush, p = 90, a = 3.0 } })
            {
                var d = new SwimSim(c.y) { Tick = 1 };
                d.Run(c.p * 4);
                Assert.IsTrue(d.Outs.All(o => o.DepthClass == c.dc));
                var br = d.Outs.Where(o => (o.Events & SwimEvents.Breath) != 0).ToList();
                Assert.AreEqual(4, br.Count);
                Assert.IsTrue(br.All(o => Math.Abs(o.OxygenDelta + c.a) < Tight), "breath of " + c.a);
                Assert.AreEqual(45.0 - 4 * c.a, d.O2, 1e-9, c.a / (c.p * Dt) + " per second; no pressure damage at any depth");
            }

            var refill = new SwimSim(-0.5) { O2 = 0.0, Tick = 1 };
            refill.Run(90);
            Assert.AreEqual(45.0, refill.O2, 1e-9, "+30 per second: full in 1.5 s");
            Assert.AreEqual(0.5, refill.Outs[0].OxygenDelta, Tight);
            Assert.AreEqual(0.0, refill.Step().OxygenDelta, Tight, "capped at the capacity");
            var edge = new SwimSim(-1.0) { O2 = 10.0, Tick = 7 };
            Assert.AreEqual(0.0, edge.Step().OxygenDelta, Tight, "no refill at -1 m (strict)");
            var edge2 = new SwimSim(-0.999) { O2 = 10.0, Tick = 7 };
            Assert.AreEqual(0.5, edge2.Step().OxygenDelta, Tight);

            var both = new SwimSim(-0.5) { O2 = 20.0, Tick = 180 };
            SwimOutputs ob = both.Step();
            Assert.AreEqual(-3.0 + 0.5, ob.OxygenDelta, Tight, "a breath and the refill on the same tick");

            var land = new SwimSim(5.0) { O2 = 10.0 };
            SwimOutputs ol = land.Step();
            Assert.IsFalse(ol.OxygenRuleActive, "out of the water: SE's suit rules");
            Assert.AreEqual(0.0, ol.OxygenDelta, 0.0);

            var m4 = new SwimSim(-50.0) { Mutants = SwimMutants.M4OxygenOff, Tick = 1 };
            m4.Run(400);
            Assert.IsTrue(m4.Outs.All(o => !o.OxygenRuleActive && o.OxygenDelta == 0.0), "S2b.M4: the rule is off");
        }

        // ------------------------------------------------------------------------------------------- mutants, determinism
        [TestMethod]
        public void MutantM1_ScalesSpeedsAndAcceleration()
        {
            var sim = new SwimSim { Mutants = SwimMutants.M1Numbers };
            sim.Move(0, 0, 1);
            sim.Run(240);
            Assert.AreEqual(7.6 * 1.5 * 0.75, sim.Outs.Last().Velocity.Z, 1e-9, "8.55 m/s instead of 5.70");
            Assert.AreEqual(3.0 * 0.75, sim.Outs[0].Velocity.Z, Tight, "acceleration x1.5");
        }

        [TestMethod]
        public void Step_IsDeterministic_AndAllocationFree()
        {
            try { AppDomain.MonitoringIsEnabled = true; }
            catch (Exception e) { Assert.Inconclusive("AppDomain monitoring unavailable: " + e.Message); }
            var a = new SwimSim(-5.0);
            var b = new SwimSim(-5.0);
            var rnd = new Random(77);
            var buf = new char[SwimTrace.MaxRowChars];
            for (int k = 0; k < 3000; k++)
            {
                if (k % 37 == 0)
                {
                    double x = rnd.Next(-1, 2), y = rnd.Next(-1, 2), z = rnd.Next(-1, 2);
                    a.Move(x, y, z);
                    b.Move(x, y, z);
                    double pitch = rnd.NextDouble() * 160.0 - 80.0;
                    a.Look(pitch);
                    b.Look(pitch);
                }
                SwimOutputs oa = a.Step(), ob = b.Step();
                Assert.AreEqual(oa.Velocity.X, ob.Velocity.X, 0.0);
                Assert.AreEqual(oa.Velocity.Y, ob.Velocity.Y, 0.0);
                Assert.AreEqual(oa.Mode, ob.Mode);
            }

            SwimState s = default(SwimState);
            var inputs = new SwimInputs
            {
                LookForward = new Vec3(0.0, 0.0, 1.0), LookRight = new Vec3(1.0, 0.0, 0.0),
                EyePos = new Vec3(0.0, -10.0, 0.0), Oxygen = 45.0, MoveForward = 1.0,
            };
            SwimOutputs o;
            SwimModel.Step(ref s, ref inputs, out o);
            SwimState pre0 = s;
            SwimTrace.Format(ref pre0, ref inputs, ref o, buf);
            GC.Collect();
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int k = 0; k < 100000; k++)
            {
                inputs.Tick = (ulong)k;
                inputs.EyePos = new Vec3(0.0, -10.0 + 12.0 * Math.Sin(k * 0.001), 0.0);
                inputs.Velocity = o.Velocity;
                inputs.MoveVertical = (k / 500) % 3 - 1;
                SwimState pre = s;
                SwimModel.Step(ref s, ref inputs, out o);
                if (k % 4 == 0) SwimTrace.Format(ref pre, ref inputs, ref o, buf);
            }
            long after = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            Console.WriteLine("100,000 steps + 25,000 rows allocated " + (after - before) + " bytes (whole AppDomain)");
            Assert.IsTrue(after - before < 512 * 1024, "allocated " + (after - before) + " bytes");
        }

        // ------------------------------------------------------------------------------------------- trace rows
        private static Dictionary<string, string> ParseFlat(string json)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(json, @"""([A-Za-z_0-9]+)"":(""[^""]*""|\[[^\]]*\]|[^,}]+)"))
                d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
            return d;
        }

        private static double FromBits(string hex)
        {
            return BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }

        [TestMethod]
        public void SwimTrace_RowHoldsInputsStateOutputs_ExactBits()
        {
            var sim = new SwimSim(-0.3) { TraceRows = true };
            sim.Look(-12.5);
            sim.Move(0.25, 1, -0.75);
            sim.V = new Vec3(0.1, -0.2, 0.3);
            sim.Tool = true;
            sim.Run(8);
            string row = sim.Rows[7];
            Assert.IsTrue(row.Length < SwimTrace.MaxRowChars);
            Dictionary<string, string> d = ParseFlat(row);
            Assert.AreEqual("swim", d["ev"]);
            Assert.AreEqual("1", d["v"]);
            Assert.AreEqual("7", d["n"]);
            Assert.AreEqual(sim.Eyes[7].Y, FromBits(d["py"]), 0.0);
            Assert.AreEqual(sim.Outs[7].Velocity.Z, FromBits(d["o_vz"]), 0.0);
            Assert.AreEqual(sim.F.Y, FromBits(d["fy"]), 0.0);
            Assert.AreEqual(0.25, FromBits(d["mx"]), 0.0);
            Assert.AreEqual("1", d["tool"]);
            Assert.AreEqual("1", d["s_lat"]);
            Assert.AreEqual(((sim.S.Phase + 5) % 6).ToString(CultureInfo.InvariantCulture), d["s_ph"]);
            foreach (string k in new[] { "tick", "md", "y", "spd", "o2", "mz", "fx", "fz", "rx", "ry", "rz", "vx", "vy", "vz", "px", "pz",
                                         "ox", "spr", "gnd", "jet", "lad", "grd", "mut", "s_jl", "s_jd", "s_ctl", "s_hx", "s_hy", "s_hz", "s_md", "s_eu", "s_yld",
                                         "o_md", "o_ctl", "o_vx", "o_vy", "o_st", "o_max", "o_cap", "o_ja", "o_or", "o_do2", "o_dc",
                                         "o_evt", "o_chg", "o_yld" })
                Assert.IsTrue(d.ContainsKey(k), "key " + k);
            // the readable keys the S2b analyser reads (tests/s2b/analyze_swim.py): rounded decimals, o2 as the suit fraction
            foreach (string k in new[] { "st", "inW", "yHead", "o2u", "move", "vel", "pos", "look", "jetOk" })
                Assert.IsTrue(row.Contains("\"" + k + "\":"), "readable key " + k);
            Assert.AreEqual("Swim", d["st"]);
            Assert.AreEqual("1", d["inW"]);
            StringAssert.Contains(row, "\"move\":[0.250000,1.000000,-0.750000]");
            StringAssert.Contains(row, "\"o2\":1.00000,\"o2u\":45.000");
            Assert.IsFalse(row.Contains("\"tr\":"), "no transition on this tick");
            Assert.IsTrue(sim.Rows[0].Contains("\"tr\":\"enter\""), "the entry tick says so");

            SwimState ts = sim.S;
            SwimInputs ti = sim.Inputs();
            SwimOutputs to = sim.Outs.Last();
            var big = new char[SwimTrace.MaxRowChars];
            int n = SwimTrace.Format(ref ts, ref ti, ref to, big, "n1.\"fwd\"\\x");
            string labelled = new string(big, 0, n);
            StringAssert.Contains(labelled, "\"seg\":\"n1.fwdx\"", "label copied without quotes and backslashes");
            Assert.AreEqual(-1, SwimTrace.Format(ref ts, ref ti, ref to, new char[40]), "too small a buffer");
        }

        // ------------------------------------------------------------------------------------------- Python cross-check
        private static string RepoRoot()
        {
            string dir = Path.GetDirectoryName(typeof(SwimModelTests).Assembly.Location);
            while (dir != null && !File.Exists(Path.Combine(dir, "DeepEngineers.sln"))) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static string PythonExe()
        {
            string env = Environment.GetEnvironmentVariable("DEEPENG_PYTHON");
            if (!string.IsNullOrEmpty(env)) return env;
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
            {
                string p = Path.Combine(local, "Programs", "Python", "Python310", "python.exe");
                if (File.Exists(p)) return p;
            }
            return "python";
        }

        private static string Quote(string a)
        {
            return a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
        }

        /// <summary>Runs <c>python -I tests/s2b/swim_replay.py args</c>; the exit code (output in <paramref name="output"/>), −1 when Python cannot start.</summary>
        private static int RunReplay(string root, out string output, params string[] args)
        {
            var sb = new StringBuilder("-I ");
            sb.Append(Quote(Path.Combine(root, "tests", "s2b", "swim_replay.py")));
            foreach (string a in args) sb.Append(' ').Append(Quote(a));
            var psi = new ProcessStartInfo(PythonExe(), sb.ToString())
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception e) { output = e.Message; return -1; }
            using (p)
            {
                var err = p.StandardError.ReadToEndAsync();
                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch (InvalidOperationException) { } output = "timeout"; return -1; }
                output = stdout + err.Result;
                return p.ExitCode;
            }
        }

        private static string WriteRows(string root, string name, IEnumerable<string> rows, List<string> written)
        {
            string dir = Path.Combine(root, "tests", "out", "s2b");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "cs-swim-" + name + "-" + Process.GetCurrentProcess().Id + ".jsonl");
            File.WriteAllText(path, string.Join("\n", rows) + "\n", new UTF8Encoding(false));
            written.Add(path);
            return path;
        }

        /// <summary>The canonical S2b.N1 script (same as <c>swim_replay.py script n1</c>): seconds, strafe, vertical, forward, sprint.</summary>
        private static readonly double[][] N1Script =
        {
            new[] { 10.0, 0, 0, 1, 0 }, new[] { 4.0, 0, 0, 0, 0 }, new[] { 6.0, 1, 0, 0, 0 }, new[] { 4.0, 0, 0, 0, 0 },
            new[] { 6.0, 0, 0, -1, 0 }, new[] { 4.0, 0, 0, 0, 0 }, new[] { 6.0, 0, 1, 0, 0 }, new[] { 4.0, 0, 0, 0, 0 },
            new[] { 6.0, 0, -1, 0, 0 }, new[] { 4.0, 0, 0, 0, 0 }, new[] { 6.0, 0, 0, 1, 1 }, new[] { 4.0, 0, 0, 0, 0 },
        };

        private static SwimSim N1Session(uint mutants)
        {
            var sim = new SwimSim(-50.0) { TraceRows = true, Mutants = mutants };
            sim.Run(30);
            foreach (double[] seg in N1Script)
            {
                sim.Move(seg[1], seg[2], seg[3]);
                sim.Sprint = seg[4] != 0;
                sim.Run((int)Math.Round(seg[0] * 60));
            }
            return sim;
        }

        private static IEnumerable<KeyValuePair<string, SwimSim>> CrossScenarios()
        {
            yield return new KeyValuePair<string, SwimSim>("n1", N1Session(0));
            var surface = new SwimSim(-20.0) { TraceRows = true };
            surface.Move(0, 1, 0);
            surface.Run(12 * 60);
            surface.Jet = true;
            surface.Floor = 1.6;
            surface.Run(180);
            surface.Move(0, 0, 0);
            surface.Run(60);
            yield return new KeyValuePair<string, SwimSim>("surface", surface);
            var entry = new SwimSim(1.2) { TraceRows = true, V = new Vec3(0.0, -0.4, 2.0) };
            entry.Move(0, 0, 1);
            entry.Run(360);
            entry.Move(0, 1, 0);
            entry.Run(300);
            entry.Jet = true;
            entry.Floor = 1.5;
            entry.Run(120);
            entry.Jet = false;
            entry.Grounded = true;
            entry.Move(0, 0, 0);
            entry.Run(120);
            yield return new KeyValuePair<string, SwimSim>("entry", entry);
            var oxy = new SwimSim(-50.0) { TraceRows = true, Tick = 1 };
            oxy.Run(3000);
            oxy.P = new Vec3(oxy.P.X, -0.5, oxy.P.Z);
            oxy.Run(180);
            oxy.P = new Vec3(oxy.P.X, -150.0, oxy.P.Z);
            oxy.O2 = 45.0;
            oxy.Run(1200);
            oxy.P = new Vec3(oxy.P.X, -250.0, oxy.P.Z);
            oxy.O2 = 45.0;
            oxy.Run(1200);
            yield return new KeyValuePair<string, SwimSim>("oxygen", oxy);
            var look = new SwimSim(-60.0) { TraceRows = true };
            look.Look(-45.0);
            look.Move(0, 0, 1);
            look.Run(240);
            look.Look(30.0);
            look.Run(240);
            look.Look(0.0);
            look.Rt = new Vec3(Math.Cos(0.4), Math.Sin(0.4), 0.0);
            look.Move(1, 0, 0);
            look.Run(180);
            look.Rt = new Vec3(1.0, 0.0, 0.0);
            look.Tool = true;
            look.Move(0.3, 1, 0.7);
            look.Run(240);
            yield return new KeyValuePair<string, SwimSim>("look", look);
            var rnd = new Random(1657);
            var mixed = new SwimSim(-5.0) { TraceRows = true };
            for (int k = 0; k < 240; k++)
            {
                mixed.Move(rnd.Next(-1, 2), rnd.Next(-1, 2), rnd.Next(-1, 2));
                mixed.Look(rnd.NextDouble() * 160.0 - 80.0);
                mixed.Sprint = rnd.NextDouble() < 0.2;
                mixed.Tool = rnd.NextDouble() < 0.2;
                mixed.Jet = rnd.NextDouble() < 0.1;
                mixed.Grounded = rnd.NextDouble() < 0.2;
                mixed.Run(rnd.Next(1, 40));
                if (mixed.P.Y > 3.0)
                {
                    mixed.P = new Vec3(mixed.P.X, -2.0, mixed.P.Z);
                    mixed.V = Vec3.Zero;
                }
            }
            yield return new KeyValuePair<string, SwimSim>("random", mixed);
            // s 1 and 3: crossings of the surface with the latch on (the Dive<->Run motor switch
 // halves vy each time) and a swim along the seabed in contact (the 45 m/s^2 contact acceleration)
            var cross = new SwimSim(-20.0) { TraceRows = true };
            cross.Move(0, 1, 0);
            cross.Run(8 * 60);
            cross.Look(-40.0);
            cross.Move(0, 0, 1);
            cross.Run(90);
            cross.Look(0.0);
            cross.Move(0, 1, 0);
            cross.Run(4 * 60);
            cross.Move(0, -1, 0);
            cross.Run(40);
            cross.Move(0, 1, 0);
            cross.Run(3 * 60);
            cross.Grounded = true;
            cross.P = new Vec3(cross.P.X, -30.0, cross.P.Z);
            cross.V = Vec3.Zero;
            cross.Move(1, 0, 1);
            cross.Run(120);
            cross.Grounded = false;
            cross.Run(120);
            yield return new KeyValuePair<string, SwimSim>("crossings", cross);
        }

        private static double Num(string output, string key)
        {
            Match m = Regex.Match(output, key + @"[ =]+([0-9.eE+-]+)");
            Assert.IsTrue(m.Success, "no '" + key + "' in:\n" + output);
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        [TestMethod]
        public void PythonReplay_ReplaysTheCSharpModel_N1Curve_AndCatchesM1M3()
        {
            string root = RepoRoot();
            Assert.IsNotNull(root, "repository root not found");
            string probe;
            int ver = RunReplay(root, out probe, "--help");
            if (ver != 0) Assert.Inconclusive("Python replay not runnable (" + ver + "): " + probe);
            var written = new List<string>();
            try
            {
                foreach (KeyValuePair<string, SwimSim> sc in CrossScenarios())
                {
                    string path = WriteRows(root, sc.Key, sc.Value.Rows, written);
                    string output;
                    int code = RunReplay(root, out output, "replay", path);
                    Console.WriteLine(output);
                    Assert.AreEqual(0, code, "replay of the C# scenario '" + sc.Key + "':\n" + output);
                    StringAssert.Contains(output, "localMismatch=0 linkMismatch=0");
                    Assert.IsTrue(Num(output, "maxVelDiff") <= 1e-6, "velocity per step within 1e-6:\n" + output);
                }

                // S2b.N1: the C# speed curve against the Python open-loop model, per step within 1e-6; metrics within 5 %
                string n1 = WriteRows(root, "n1-oracle", N1Session(0).Rows, written);
                string o1;
                int c1 = RunReplay(root, out o1, "n1", n1, "--curve-tol", "1e-6");
                Console.WriteLine(o1);
                Assert.AreEqual(0, c1, "S2b.N1 on the C# session:\n" + o1);
                StringAssert.Contains(o1, "S2b.N1 PASS");
                Assert.IsTrue(Num(o1, "max") <= 1e-6, "curve within 1e-6:\n" + o1);
                StringAssert.Matches(o1, new Regex(@"forward steady speed \(tick \d+-\d+\)\s+PASS\s+measured 5\.7000\s+model 5\.7000"));

                // sparse rows (every 5th): the replay reseeds from the logged state
                string sparse = WriteRows(root, "n1-sparse", N1Session(0).Rows.Where((r, k) => k % 5 == 0), written);
                string os;
                Assert.AreEqual(0, RunReplay(root, out os, "replay", sparse), os);

                // S2b.M1 (numbers x1.5): replay red and N1 red; the logged mutant flag replays green with --honour-mutant
                string m1 = WriteRows(root, "n1-m1", N1Session(SwimMutants.M1Numbers).Rows, written);
                string om;
                Assert.AreEqual(1, RunReplay(root, out om, "n1", m1), "S2b.M1 must be red:\n" + om);
                StringAssert.Contains(om, "SWIM REPLAY FAIL");
                StringAssert.Contains(om, "S2b.N1 FAIL");
                string oh;
                Assert.AreEqual(0, RunReplay(root, out oh, "replay", m1, "--honour-mutant"), "the Python M1 equals the C# M1:\n" + oh);

                // S2b.M3 (no hysteresis): the surface hold bounces; the replay is red, honoured it is green
                var m3 = new SwimSim(-20.0) { TraceRows = true, Mutants = SwimMutants.M3NoHysteresis };
                m3.Move(0, 1, 0);
                m3.Run(12 * 60);
                int bounces = m3.Outs.Count(o => (o.Events & (SwimEvents.Entered | SwimEvents.Exited)) != 0);
                Assert.IsTrue(bounces > 4, "S2b.M3 bounces at the surface: " + bounces);
                string p3 = WriteRows(root, "surface-m3", m3.Rows, written);
                string o3;
                Assert.AreEqual(1, RunReplay(root, out o3, "replay", p3), "S2b.M3 must be red:\n" + o3);
                string o3h;
                Assert.AreEqual(0, RunReplay(root, out o3h, "replay", p3, "--honour-mutant"), o3h);
            }
            finally
            {
                foreach (string f in written)
                    try { File.Delete(f); } catch (IOException) { }
            }
        }
    }

    /// </summary>
    [TestClass]
    public class SwimYieldTests
    {
        private static SwimOutputs Step(ref SwimState s, double eyeY, bool ladder, bool grid, bool grounded, bool jet, double vertical)
        {
            var i = new SwimInputs
            {
                Tick = 600, MoveForward = 1.0, MoveVertical = vertical, Grounded = grounded, JetpackOn = jet, OnLadder = ladder, OnGrid = grid,
                LookForward = new Vec3(0.0, 0.0, 1.0), LookRight = new Vec3(1.0, 0.0, 0.0), EyePos = new Vec3(0.0, eyeY, 0.0),
                Oxygen = SwimThresholds.OxygenCapacity,
            };
            SwimOutputs o;
            SwimModel.Step(ref s, ref i, out o);
            return o;
        }

        [TestMethod]
        public void Rule_LadderFirst_ThenDeck_OnlySwimmingUnder()
        {
            Assert.AreEqual(SwimYield.None, SwimYield.Of(false, SwimMode.Walk, true, true, true, false, 0.0), "not latched: SE already moves it");
            Assert.AreEqual(SwimYield.Ladder, SwimYield.Of(true, SwimMode.Swim, true, false, false, false, 1.0), "a ladder yields whatever the input");
            Assert.AreEqual(SwimYield.Ladder, SwimYield.Of(true, SwimMode.Surface, true, true, true, false, 0.0), "a ladder at the surface too");
            Assert.AreEqual(SwimYield.Deck, SwimYield.Of(true, SwimMode.Swim, false, true, true, false, 0.0));
            Assert.AreEqual(SwimYield.Deck, SwimYield.Of(true, SwimMode.Swim, false, true, true, false, -1.0), "crouching down on the deck stays");
            Assert.AreEqual(SwimYield.None, SwimYield.Of(true, SwimMode.Swim, false, true, true, false, 1.0), "a push up swims again");
            Assert.AreEqual(SwimYield.None, SwimYield.Of(true, SwimMode.Swim, false, true, false, false, 0.0), "a grid without contact: swimming");
            Assert.AreEqual(SwimYield.None, SwimYield.Of(true, SwimMode.Swim, false, false, true, false, 0.0), "the seabed never counts (no walking on voxels)");
            Assert.AreEqual(SwimYield.None, SwimYield.Of(true, SwimMode.Swim, false, true, true, true, 0.0), "jetpack on: not a deck walk");
            Assert.AreEqual(SwimYield.None, SwimYield.Of(true, SwimMode.Surface, false, true, true, false, 0.0), "at the surface the model keeps the body");
        }

        [TestMethod]
        public void Ladder_StopsTheVelocityWrites_LatchAndOxygenStay()
        {
            var s = default(SwimState);
            SwimOutputs o = Step(ref s, -20.0, false, false, false, false, 0.0);
            Assert.IsTrue(o.ControlsVelocity && s.Latched && o.Yield == SwimYield.None);
            o = Step(ref s, -20.0, true, false, false, false, 1.0);
            Assert.AreEqual(SwimYield.Ladder, o.Yield);
            Assert.IsFalse(o.ControlsVelocity, "SE's ladder moves the character");
            Assert.IsTrue(s.Latched, "still in the water");
            Assert.IsTrue(o.OxygenRuleActive, "still breathing the suit's air");
            Assert.AreEqual(SwimMode.Swim, o.Mode);
            Assert.IsTrue(o.Changed, "the yield change is a change");
            o = Step(ref s, -20.0, true, false, false, false, 1.0);
            Assert.IsFalse(o.Changed, "steady on the ladder");
            o = Step(ref s, -20.0, false, false, false, false, 0.0);
            Assert.AreEqual(SwimYield.None, o.Yield);
            Assert.IsTrue(o.ControlsVelocity, "off the ladder: the model swims again");
            Assert.IsTrue((o.Events & SwimEvents.Resumed) != 0, "the motor resumes from the body's velocity");
        }

        [TestMethod]
        public void Deck_WalksWithTheBootsUntilAPushUp()
        {
            var s = default(SwimState);
            Step(ref s, -30.0, false, false, false, false, 0.0);
            SwimOutputs o = Step(ref s, -30.0, false, true, true, false, 0.0);
            Assert.AreEqual(SwimYield.Deck, o.Yield);
            Assert.IsFalse(o.ControlsVelocity);
            o = Step(ref s, -30.0, false, true, true, false, 1.0);
            Assert.AreEqual(SwimYield.None, o.Yield, "space: swim up");
            Assert.IsTrue(o.ControlsVelocity);
            o = Step(ref s, -30.0, false, true, false, false, 0.0);
            Assert.AreEqual(SwimYield.None, o.Yield, "off the deck: swimming");
        }
    }
}

