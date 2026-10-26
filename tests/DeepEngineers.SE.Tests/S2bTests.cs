using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S2b "Swimming", SE side, game-independent parts (ROADMAP S2b): input mapping (SE move indicator → swim input, the
 /// scripted look), the scripted input source (parsing and tick stepping), the swim state's hysteresis through the
 /// model seam (<see cref="SwimModelAdapter"/>), the jetpack/dampener/oxygen restore policy, the oxygen rule's
 /// arithmetic, the velocity frame conversions, the S2b seeded failures and the <c>/de swim</c> chat syntax. The model
 /// behind the seam is Wire's <see cref="SwimModel"/>: the tests
 /// named <c>Model_*</c> pin its Subnautica numbers through the seam, and <c>Row_*</c> replays the plugin's own
 /// <c>swim</c> rows with the independent Python oracle (tests/s2b/swim_replay.py) and parses them with the analyser.
 /// </summary>
    [TestClass]
    public class S2bTests
    {
        private const double Dt = 1.0 / 60.0;

        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); }

        private static IDictionary<string, object> Args(string json) { return (IDictionary<string, object>)MiniJson.Parse(json); }

        // ================================================================================================ input mapping

        [TestMethod]
        public void InputMap_MoveIndicatorToSwimInput_SeControlSemantics()
        {
            // SE: X = STRAFE_RIGHT - STRAFE_LEFT, Y = JUMP - CROUCH, Z = BACKWARD - FORWARD (MyInputExtensions.GetPositionDelta)
            SwimInputs w = SwimInputMap.FromMoveIndicator(0, 0, -1, false);
            Assert.AreEqual(1.0, w.Forward); Assert.AreEqual(0.0, w.Strafe); Assert.AreEqual(0.0, w.Vertical); Assert.IsFalse(w.Sprint);
            SwimInputs s = SwimInputMap.FromMoveIndicator(0, 0, 1, false);
            Assert.AreEqual(-1.0, s.Forward, "S = backward");
            SwimInputs d = SwimInputMap.FromMoveIndicator(1, 0, 0, true);
            Assert.AreEqual(1.0, d.Strafe, "D = strafe right"); Assert.IsTrue(d.Sprint);
            SwimInputs a = SwimInputMap.FromMoveIndicator(-1, 0, 0, false);
            Assert.AreEqual(-1.0, a.Strafe, "A = strafe left");
            SwimInputs up = SwimInputMap.FromMoveIndicator(0, 1, 0, false);
            Assert.AreEqual(1.0, up.Vertical, "jump = up");
            SwimInputs down = SwimInputMap.FromMoveIndicator(0, -1, 0, false);
            Assert.AreEqual(-1.0, down.Vertical, "crouch = down");
            SwimInputs big = SwimInputMap.FromMoveIndicator(1.7, -3, double.NaN, false);
            Assert.AreEqual(1.0, big.Strafe); Assert.AreEqual(-1.0, big.Vertical); Assert.AreEqual(0.0, big.Forward, "NaN reads as no input");
            Assert.IsFalse(SwimInputMap.FromMoveIndicator(0, 0, 0, false).Any);
            Assert.IsTrue(SwimInputMap.FromMoveIndicator(0, 0, 0, true).Any);

            // the scripted source drives SE's own walk with the inverse map
            double x, y, z;
            SwimInputMap.ToMoveIndicator(new SwimInputs { Forward = 1, Strafe = -0.5, Vertical = 0.25 }, out x, out y, out z);
            Assert.AreEqual(-0.5, x); Assert.AreEqual(0.25, y); Assert.AreEqual(-1.0, z);
            SwimInputs back = SwimInputMap.FromMoveIndicator(x, y, z, false);
            Assert.AreEqual(1.0, back.Forward); Assert.AreEqual(-0.5, back.Strafe); Assert.AreEqual(0.25, back.Vertical);
        }

        [TestMethod]
        public void InputMap_ScriptedLook_YawPitchInSnDegrees_NoRoll()
        {
            Vec3 f, r;
            SwimInputMap.LookFromYawPitch(0, 0, out f, out r);
            AssertVec(new Vec3(0, 0, 1), f, "yaw 0 = SN north");
            AssertVec(new Vec3(1, 0, 0), r, "right of north = east");
            SwimInputMap.LookFromYawPitch(90, 0, out f, out r);
            AssertVec(new Vec3(1, 0, 0), f, "yaw 90 = east");
            AssertVec(new Vec3(0, 0, -1), r, "right of east = south");
            SwimInputMap.LookFromYawPitch(0, 90, out f, out r);
            AssertVec(new Vec3(0, 1, 0), f, "pitch 90 = straight up");
            foreach (double yaw in new[] { -170.0, -30.0, 0.0, 45.0, 135.0, 400.0 })
                foreach (double pitch in new[] { -89.0, -30.0, 0.0, 60.0 })
                {
                    SwimInputMap.LookFromYawPitch(yaw, pitch, out f, out r);
                    Assert.AreEqual(1.0, f.Length, 1e-12);
                    Assert.AreEqual(1.0, r.Length, 1e-12);
                    Assert.AreEqual(0.0, r.Y, 1e-15, "right stays horizontal (roll disabled)");
                    Assert.AreEqual(0.0, Vec3.Dot(f, r), 1e-12, "right is perpendicular to forward");
                    Assert.AreEqual(Math.Sin(pitch * Math.PI / 180), f.Y, 1e-12);
                }
        }

        // ================================================================================================ scripts

        [TestMethod]
        public void Script_ListForm_TicksStepsAndInheritedLook()
        {
            bool stop;
            string err;
            SwimScript s = SwimScript.Parse(Args("{\"s\": [{\"seconds\": 10, \"forward\": 1}, {\"seconds\": 1.5}, " +
                                                 "{\"seconds\": 0.004, \"strafe\": -1, \"lookYawDeg\": 90, \"lookPitchDeg\": -20, \"sprint\": true, \"label\": \"x\"}]}")["s"],
                                            out stop, out err);
            Assert.IsNotNull(s, err);
            Assert.IsFalse(stop);
            Assert.AreEqual(3, s.Steps.Length);
            Assert.AreEqual(600, s.Steps[0].Ticks);
            Assert.AreEqual(90, s.Steps[1].Ticks);
            Assert.AreEqual(1, s.Steps[2].Ticks, "a step lasts at least one tick");
            Assert.AreEqual(691, s.TotalTicks);
            Assert.AreEqual(1.0, s.Steps[0].Input.Forward);
            Assert.AreEqual(0.0, s.Steps[1].Input.Forward, "missing inputs are 0");
            Assert.AreEqual(0.0, s.Steps[1].LookYawDeg, "the first look defaults to yaw 0");
            Assert.AreEqual(90.0, s.Steps[2].LookYawDeg);
            Assert.AreEqual(-20.0, s.Steps[2].LookPitchDeg);
            Assert.IsTrue(s.Steps[2].Input.Sprint);
            Assert.AreEqual(-1, s.Steps[0].Jetpack, "no jetpack request by default");
            Assert.AreEqual("x", s.Steps[2].Label);

            // stepping: one tick per SE simulation step
            var starts = new List<int>();
            int[] stepAt = new int[s.TotalTicks];
            for (int k = 0; k < s.TotalTicks; k++)
            {
                Assert.IsFalse(s.Done, "tick " + k);
                if (s.AtStepStart) starts.Add(k);
                stepAt[k] = s.StepIndex;
                s.Advance();
            }
            Assert.IsTrue(s.Done);
            Assert.AreEqual(s.TotalTicks, s.Elapsed);
            CollectionAssert.AreEqual(new[] { 0, 600, 690 }, starts.ToArray());
            Assert.AreEqual(0, stepAt[599]); Assert.AreEqual(1, stepAt[600]); Assert.AreEqual(1, stepAt[689]); Assert.AreEqual(2, stepAt[690]);
            s.Advance();
            Assert.AreEqual(s.TotalTicks, s.Elapsed, "advancing a finished script does nothing");
        }

        [TestMethod]
        public void Script_CompactForms_SameAsList_AndStop()
        {
            bool stop;
            string err;
            SwimScript list = SwimScript.Parse(Args("{\"s\": [{\"seconds\": 2, \"forward\": 1, \"lookYawDeg\": 45}, {\"seconds\": 1, \"vertical\": 1, \"jetpack\": true}, " +
                                                    "{\"seconds\": 1, \"jetpack\": false}]}")["s"], out stop, out err);
            Assert.IsNotNull(list, err);
            SwimScript compact = SwimScript.Parse("2,1,0,0,0,45,0; 1,0,0,1,0,,,1 | 1,0,0,0,0,,,0", out stop, out err);
            Assert.IsNotNull(compact, err);
            Assert.AreEqual(list.Steps.Length, compact.Steps.Length);
            for (int k = 0; k < list.Steps.Length; k++)
            {
                SwimScriptStep a = list.Steps[k], b = compact.Steps[k];
                Assert.AreEqual(a.Ticks, b.Ticks, "step " + k);
                Assert.AreEqual(a.Input.Forward, b.Input.Forward);
                Assert.AreEqual(a.Input.Vertical, b.Input.Vertical);
                Assert.AreEqual(a.LookYawDeg, b.LookYawDeg, "step " + k + ": the look carries over in both forms");
                Assert.AreEqual(a.Jetpack, b.Jetpack, "step " + k);
            }
            Assert.AreEqual(1, compact.Steps[1].Jetpack);
            Assert.AreEqual(0, compact.Steps[2].Jetpack);

            // se_cmd turns "script=10,1,0,0" into a number list: one compact step
            SwimScript nums = SwimScript.Parse(Args("{\"s\": [10, 1, 0, 0]}")["s"], out stop, out err);
            Assert.IsNotNull(nums, err);
            Assert.AreEqual(1, nums.Steps.Length);
            Assert.AreEqual(600, nums.TotalTicks);
            Assert.AreEqual(1.0, nums.Steps[0].Input.Forward);

            // one object
            SwimScript one = SwimScript.Parse(Args("{\"s\": {\"seconds\": 0.5, \"vertical\": -1}}")["s"], out stop, out err);
            Assert.IsNotNull(one, err);
            Assert.AreEqual(30, one.TotalTicks);

            foreach (string w in new[] { "stop", "STOP", "off", "cancel" })
            {
                Assert.IsNull(SwimScript.Parse(w, out stop, out err));
                Assert.IsTrue(stop, w);
                Assert.IsNull(err);
            }
        }

        [TestMethod]
        public void Script_BadInput_IsRefusedWithAReason()
        {
            bool stop;
            string err;
            string[] bad =
            {
                "{\"s\": [{\"forward\": 1}]}",                      // seconds missing
                "{\"s\": [{\"seconds\": 0}]}",
                "{\"s\": [{\"seconds\": 5000}]}",
                "{\"s\": [{\"seconds\": 1, \"forward\": 2}]}",
                "{\"s\": [{\"seconds\": 1, \"lookPitchDeg\": 100}]}",
                "{\"s\": [{\"seconds\": 1, \"jetpack\": \"maybe\"}]}",
                "{\"s\": [1, {\"seconds\": 1}]}",
                "{\"s\": []}",
                "{\"s\": 12}",
            };
            foreach (string b in bad)
            {
                Assert.IsNull(SwimScript.Parse(Args(b)["s"], out stop, out err), b);
                Assert.IsFalse(stop, b);
                Assert.IsFalse(string.IsNullOrEmpty(err), b);
            }
            Assert.IsNull(SwimScript.Parse("1,x", out stop, out err));
            StringAssert.Contains(err, "not a number");
            Assert.IsNull(SwimScript.Parse("1,0,0,0,0,0,0,0,9", out stop, out err), "9 fields");
            Assert.IsNull(SwimScript.Parse("3000;3000", out stop, out err), "longer than an hour in total");
        }

        // ================================================================================================ hysteresis

        private static SwimTickIn In(double eyeY, bool grounded = false, bool jetpack = false, uint mutants = 0)
        {
            Vec3 f, r;
            SwimInputMap.LookFromYawPitch(0, 0, out f, out r);
            return new SwimTickIn { LookForwardSn = f, LookRightSn = r, EyeY = eyeY, ChestY = eyeY - 0.4, Grounded = grounded, JetpackOn = jetpack, Mutants = mutants, OxygenUnits = 45 };
        }

        [TestMethod]
        public void Hysteresis_EnterBelowTheLine_LeaveOnlyWellAbove()
        {
            var m = new SwimModelAdapter();
            SwimTickIn t = In(-0.05);
            Assert.AreEqual(0, m.Tick(ref t).Transition, "eye 5 cm under: still out");
            Assert.IsFalse(m.Swimming);
            t = In(-0.2);
            SwimTickOut o = m.Tick(ref t);
            Assert.AreEqual(1, o.Transition, "entered");
            Assert.IsTrue(o.Swimming && m.Swimming);
            foreach (double y in new[] { -0.05, 0.0, 0.3, 0.52, 0.79 })
            {
                t = In(y);
                o = m.Tick(ref t);
                Assert.IsTrue(o.Swimming, "still swimming at eye " + y + " (free in the water)");
                Assert.AreEqual(0, o.Transition);
            }
            t = In(0.8);
            o = m.Tick(ref t);
            Assert.AreEqual(-1, o.Transition, "left at the free exit line");
            Assert.AreEqual(SwimExit.Surface, o.ExitReason);

            // standing on something: the low exit line
            t = In(-0.5);
            Assert.AreEqual(1, m.Tick(ref t).Transition);
            t = In(0.05, grounded: true);
            Assert.AreEqual(0, m.Tick(ref t).Transition, "grounded but under the grounded exit line");
            t = In(0.15, grounded: true);
            o = m.Tick(ref t);
            Assert.AreEqual(-1, o.Transition);
            Assert.AreEqual(SwimExit.Grounded, o.ExitReason);
        }

        [TestMethod]
        public void Hysteresis_JetpackFliesOutWithTheHeadOut_ExitReasonJetpack()
        {
            var m = new SwimModelAdapter();
            SwimTickIn t = In(-3);
            m.Tick(ref t);
            t = In(-0.3, jetpack: true);
            SwimTickOut o = m.Tick(ref t);
            Assert.IsTrue(o.Swimming && o.ControlsVelocity, "jetpack with the head under: no exit, the model keeps the velocity (the controller switches the jetpack off)");
            Assert.AreEqual(SwimMode.Swim, o.Mode);
            Assert.IsFalse(o.JetpackAllowed);
            t = In(0.2);
            o = m.Tick(ref t);
            Assert.IsTrue(o.Swimming && o.ControlsVelocity && o.JetpackAllowed, "head out: the jetpack is allowed, still swimming (Surface)");
            Assert.AreEqual(SwimMode.Surface, o.Mode);
            t = In(0.2, jetpack: true);
            o = m.Tick(ref t);
            Assert.AreEqual(0, o.Transition, "jetpack on with the head out: still in the water (latch), no transition");
            Assert.AreEqual(SwimMode.Fly, o.Mode);
            Assert.IsTrue(o.Swimming && !o.ControlsVelocity, "Fly: SE's jetpack moves the engineer, the model is inert");
            t = In(0.6, jetpack: true);
            Assert.AreEqual(0, m.Tick(ref t).Transition, "climbing, below the exit line");
            t = In(0.85, jetpack: true);
            o = m.Tick(ref t);
            Assert.AreEqual(-1, o.Transition, "out at the free exit line");
            Assert.AreEqual(SwimExit.Jetpack, o.ExitReason, "the jetpack running at the exit: the climb out");
            Assert.AreEqual(SwimMode.Walk, o.Mode);

            // Fly ends when the head goes back under: the model takes the velocity again, the jetpack is refused
            t = In(-3);
            m.Tick(ref t);
            t = In(0.1, jetpack: true);
            Assert.AreEqual(SwimMode.Fly, m.Tick(ref t).Mode);
            t = In(-0.05, jetpack: true);
            Assert.AreEqual(SwimMode.Fly, m.Tick(ref t).Mode, "jetpack latch: kept down to -0.1 m");
            t = In(-0.2, jetpack: true);
            o = m.Tick(ref t);
            Assert.AreEqual(SwimMode.Swim, o.Mode);
            Assert.IsTrue(o.ControlsVelocity && !o.JetpackAllowed && o.Transition == 0);
        }

        /// <summary>The surface float of S2b.N3/N2: the eye wanders between −0.3 and +0.6 m (waves of a float, a jump).</summary>
        private static int Transitions(uint mutants)
        {
            var m = new SwimModelAdapter();
            int n = 0;
            for (int k = 0; k < 600; k++)
            {
                double y = 0.15 + 0.45 * Math.Sin(2 * Math.PI * k / 90.0);
                SwimTickIn t = In(y, mutants: mutants);
                if (m.Tick(ref t).Transition != 0) n++;
            }
            return n;
        }

        [TestMethod]
        public void Hysteresis_S2bM3_BouncesWhereTheModelDoesNot()
        {
            Assert.AreEqual(1, Transitions(0), "one entry, no bouncing at the surface");
            S2bMutants.NoHysteresis = true;
            int bouncing = Transitions(S2bMutants.ModelBits);
            Assert.IsTrue(bouncing >= 10, "S2b.M3: one line both ways bounces (" + bouncing + " transitions)");
        }

        // ================================================================================================ the model through the seam

        private struct Run
        {
            public double SpeedEnd, MaxEyeY, Distance;
            public int TicksTo90;
        }

        /// <summary>Integrates the model's velocity at 60 Hz like SE does (position += v × dt).</summary>
        private static Run Swim(SwimModelAdapter m, ref Vec3 pos, ref Vec3 v, int ticks, double fwd, double vert, double pitch, uint mutants, double steady = 0)
        {
            var run = new Run { TicksTo90 = -1, MaxEyeY = double.MinValue };
            Vec3 f, r;
            SwimInputMap.LookFromYawPitch(0, pitch, out f, out r);
            Vec3 start = pos;
            for (int k = 0; k < ticks; k++)
            {
                var t = new SwimTickIn { Forward = fwd, Vertical = vert, LookForwardSn = f, LookRightSn = r, EyeY = pos.Y, EyeSn = pos, ChestY = pos.Y - 0.4, VelocitySn = v, Mutants = mutants, OxygenUnits = 45 };
                SwimTickOut o = m.Tick(ref t);
                Assert.IsTrue(o.Swimming, "tick " + k);
                v = o.VelocitySn;
                pos = pos + v * Dt;
                run.MaxEyeY = Math.Max(run.MaxEyeY, pos.Y);
                if (steady > 0 && run.TicksTo90 < 0 && v.Length >= 0.9 * steady) run.TicksTo90 = k + 1;
            }
            run.SpeedEnd = v.Length;
            run.Distance = (pos - start).Length;
            return run;
        }

        [TestMethod]
        public void Model_ForwardReleaseVertical_OfflineN1Shape()
        {
            var m = new SwimModelAdapter();
            var pos = new Vec3(0, -50, 0);
            var v = Vec3.Zero;
            Run fwd = Swim(m, ref pos, ref v, 600, 1, 0, 0, 0);
            Assert.IsTrue(fwd.SpeedEnd > 2.0 && fwd.SpeedEnd <= 7.6 * 1.0001, "steady forward speed " + fwd.SpeedEnd);
            Assert.AreEqual(0.0, v.Y, 1e-9, "level look: no vertical drift, no gravity");
            Assert.AreEqual(-50.0, pos.Y, 1e-9);
            Assert.IsTrue(v.Z > 0 && Math.Abs(v.X) < 1e-9, "along the look (north)");
            Run release = Swim(m, ref pos, ref v, 300, 0, 0, 0, 0);
            Assert.IsTrue(release.SpeedEnd < 0.01, "drag stops the diver: " + release.SpeedEnd);
            Assert.IsTrue(release.Distance > 0.2 && release.Distance < 5, "stop distance " + release.Distance);
            Run up = Swim(m, ref pos, ref v, 120, 0, 1, 0, 0);
            Assert.IsTrue(v.Y > 2.0 && Math.Abs(v.Z) < 0.01, "vertical input is world up: " + SwimText.V(v));
        }

        [TestMethod]
        public void Model_SurfaceStopsTheHead_NoRiseAbove()
        {
            var m = new SwimModelAdapter();
            var pos = new Vec3(0, -20, 0);
            var v = Vec3.Zero;
            Run up = Swim(m, ref pos, ref v, 1200, 0, 1, 0, 0);
            // Holding "up" from -20 m: the head comes out and stops; the surface factor reaches 0 at the band top, and a
 // motor stepping at 10 Hz can overshoot it by one step (at most +0.618 m with the Dive->Run motor switch
 // halving vy at the crossing, swim_replay.surface_head_max; ), never up to the
 // free exit line, so the diver stays in the water without the jetpack (S2b.N2). The oracle's replay of the
 // model gives the exact stop height for the run.
            Assert.IsTrue(up.MaxEyeY > 0.0, "the head is out: " + up.MaxEyeY);
            Assert.IsTrue(up.MaxEyeY < 0.8, "no rise to the exit line: " + up.MaxEyeY);
            Assert.IsTrue(up.MaxEyeY >= SwimThresholds.SurfaceTopY - 0.2 && up.MaxEyeY <= 0.618 + 1e-3, "stop height " + up.MaxEyeY);
            Assert.AreEqual(up.MaxEyeY, pos.Y, 1e-9, "stopped: the highest point is the final one");
            Assert.IsTrue(Math.Abs(v.Y) < 1e-6, "no vertical speed left at the surface: " + v.Y);
            Assert.IsTrue(m.Swimming, "still swimming (free exit line not reached)");
            Run more = Swim(m, ref pos, ref v, 600, 0, 1, 0, 0);
            Assert.AreEqual(up.MaxEyeY, more.MaxEyeY, 1e-9, "10 s more of 'up' does not lift the head");
        }

        [TestMethod]
        public void Model_S2bM1_IsFasterByHalf()
        {
            var a = new SwimModelAdapter();
            var pa = new Vec3(0, -50, 0);
            var va = Vec3.Zero;
            Run normal = Swim(a, ref pa, ref va, 600, 1, 0, 0, 0);
            S2bMutants.NumbersX15 = true;
            var b = new SwimModelAdapter();
            var pb = new Vec3(0, -50, 0);
            var vb = Vec3.Zero;
            Run fast = Swim(b, ref pb, ref vb, 600, 1, 0, 0, S2bMutants.ModelBits);
            Assert.AreEqual(1.5, fast.SpeedEnd / normal.SpeedEnd, 0.02, "S2b.M1 steady speed ratio");
            Assert.IsTrue(fast.SpeedEnd / normal.SpeedEnd > 1.05, "far outside the 5 % N1 tolerance");
        }

        [TestMethod]
        public void Model_SubnauticaNumbers_SceneValuesTenHertzMotorWithDamping()
        {
            // Wire's model runs Subnautica's motor at its 0.1 s fixed step and holds the
 // velocity for 6 SE ticks; the Main-scene speeds (7.6 m/s, not the C# initializers' 6.64): steady forward
 // = 7.6 x (1 - 2.5 x 0.1) = 5.70 m/s (CONTRACT §2.10 "Implied"); release: x0.75 per step.
            var m = new SwimModelAdapter();
            var pos = new Vec3(0, -50, 0);
            var v = Vec3.Zero;
            Run fwd = Swim(m, ref pos, ref v, 600, 1, 0, 0, 0, 5.70);
            Assert.AreEqual(5.70, fwd.SpeedEnd, 1e-9);
            Assert.IsTrue(fwd.TicksTo90 > 30 && fwd.TicksTo90 <= 42, "90 % of the top speed from about 0.6 s: " + fwd.TicksTo90 + " ticks");
            Run release = Swim(m, ref pos, ref v, 600, 0, 0, 0, 0);
            Assert.AreEqual(5.70 * 3 * 0.1, release.Distance, 1e-6, "stop distance 1.71 m = v x dt x 0.75 / 0.25");
            Run up = Swim(m, ref pos, ref v, 600, 0, 1, 0, 0);
            Assert.AreEqual(4.725, up.SpeedEnd, 1e-9, "vertical top speed 6.3 x 0.75");
            Dictionary<string, object> n = SwimModelAdapter.Numbers();
            Assert.AreEqual(7.6, n["forwardMaxSpeed"]);
            Assert.AreEqual(6.3, n["verticalMaxSpeed"]);
            Assert.AreEqual(0.1, n["fixedStepSec"]);
            Assert.AreEqual(1.0, n["sprintFactor"]);
            Assert.AreEqual(45.0, SwimModelAdapter.TankSeconds);
            StringAssert.StartsWith(SwimModelAdapter.ModelName, "Wire SwimModel");
        }

        [TestMethod]
        public void Model_OxygenRates_ByDepthClassAndSurfaceRefill()
        {
            var m = new SwimModelAdapter();
            var cases = new[]
            {
                new { y = -50.0, use = 1.0, refill = 0.0, cls = 1u },
                new { y = -150.0, use = 1.5, refill = 0.0, cls = 2u },
                new { y = -250.0, use = 2.0, refill = 0.0, cls = 3u },
                new { y = -0.5, use = 1.0, refill = 30.0, cls = 1u },   // within 1 m of the surface: both (refill dominates)
                new { y = -0.05, use = 0.0, refill = 30.0, cls = 0u },  // surface class
                new { y = 0.3, use = 0.0, refill = 30.0, cls = 0u },    // head out
            };
            foreach (var c in cases)
            {
                SwimTickIn t = In(c.y);
                SwimTickOut o = m.Tick(ref t);
                Assert.AreEqual(c.use, o.O2UseUnitsPerSec, 1e-12, "use at " + c.y);
                Assert.AreEqual(c.refill, o.O2RefillUnitsPerSec, 1e-12, "refill at " + c.y);
                Assert.AreEqual(c.cls, o.DepthClass, "class at " + c.y);
            }
        }

        // ================================================================================================ the seam's own rules

        [TestMethod]
        public void Adapter_OutsideChanges_SkipTheStepIndex_ExitReasons_Rates_MutantBits()
        {
            var m = new SwimModelAdapter();
            SwimTickIn t = In(-5);
            m.Tick(ref t);
            Assert.AreEqual(1ul, m.Steps);
            Assert.IsTrue(m.Swimming && m.Controls);
            m.RestartStep();
            Assert.AreEqual(2ul, m.Steps, "a teleport: the next row's n skips one (a replay gap, not a state mismatch)");
            Assert.IsTrue(m.Swimming, "a teleport keeps the latch");
            t = In(-5);
            SwimTickOut o = m.Tick(ref t);
            Assert.IsTrue(o.Stepped && o.Transition == 0, "a fresh motor step, no transition");
            m.ForceOut();
            Assert.IsFalse(m.Swimming || m.Controls);
            Assert.AreEqual(SwimMode.Walk, m.Mode);
            Assert.AreEqual(4ul, m.Steps, "a controller exit: the step index skips one");
            t = In(-5);
            Assert.AreEqual(1, m.Tick(ref t).Transition, "back in on the next tick in the water");
            m.Reset();
            Assert.AreEqual(0ul, m.Steps);
            Assert.IsFalse(m.Swimming);

            Assert.AreEqual(SwimExit.Jetpack, SwimModelAdapter.ExitReasonOf(0.9, false, true, 0));
            Assert.AreEqual(SwimExit.Grounded, SwimModelAdapter.ExitReasonOf(0.15, true, false, 0));
            Assert.AreEqual(SwimExit.Surface, SwimModelAdapter.ExitReasonOf(0.15, true, false, SwimMutants.M3NoHysteresis), "S2b.M3: one line");
            Assert.AreEqual(SwimExit.Surface, SwimModelAdapter.ExitReasonOf(0.8, false, false, 0));

            Assert.AreEqual(SwimMutants.M1Numbers | SwimMutants.M2JetpackInWater | SwimMutants.M3NoHysteresis | SwimMutants.M4OxygenOff,
                            SwimModelAdapter.ModelMutantBits(S2bMutants.BitM1 | S2bMutants.BitM2 | S2bMutants.BitM3 | S2bMutants.BitM4));
            Assert.AreEqual(SwimMutants.M3NoHysteresis, SwimModelAdapter.ModelMutantBits(S2bMutants.BitM3));

            // S2b.M2 reaches the model: the jetpack flies underwater (mode Fly, the model inert)
            var m2 = new SwimModelAdapter();
            t = In(-20, jetpack: true, mutants: S2bMutants.BitM2);
            o = m2.Tick(ref t);
            Assert.IsTrue(o.Swimming && o.JetpackAllowed && o.Mode == SwimMode.Fly && !o.ControlsVelocity, "S2b.M2");
            // S2b.M4 reaches the model: the oxygen rule is off, no rates
            var m4 = new SwimModelAdapter();
            t = In(-50, mutants: S2bMutants.BitM4);
            o = m4.Tick(ref t);
            Assert.IsFalse(o.OxygenRuleActive);
            Assert.AreEqual(0.0, o.O2UseUnitsPerSec);
            Assert.AreEqual(0.0, o.O2RefillUnitsPerSec);
        }

        // ================================================================================================ the plugin's swim row

 /// <summary>The canonical S2b.N1 script (swim_replay.py N1_SCRIPT): seconds, strafe, vertical, forward, sprint.</summary>
        private static readonly double[][] N1Script =
        {
            new[] { 10.0, 0, 0, 1, 0 }, new[] { 4.0, 0, 0, 0, 0 },
            new[] { 6.0, 1, 0, 0, 0 }, new[] { 4.0, 0, 0, 0, 0 },
            new[] { 6.0, 0, 0, -1, 0 }, new[] { 4.0, 0, 0, 0, 0 },
            new[] { 6.0, 0, 1, 0, 0 }, new[] { 4.0, 0, 0, 0, 0 },
            new[] { 6.0, 0, -1, 0, 0 }, new[] { 4.0, 0, 0, 0, 0 },
            new[] { 6.0, 0, 0, 1, 1 }, new[] { 4.0, 0, 0, 0, 0 },
        };

        /// <summary>
 /// The plugin's rows for the N1 script at −50 m, written exactly as SwimController.WriteRow does
 /// (<see cref="SwimModelAdapter.Row"/>), with a perfect SE: the imposed velocity comes back unchanged and moves
 /// the eye. A teleport (<see cref="SwimModelAdapter.RestartStep"/>) at the start of the strafe segment.
 /// </summary>
        private static List<string> PluginRows(uint mutants)
        {
            var m = new SwimModelAdapter();
            var rows = new List<string>();
            var buf = new char[SwimTrace.MaxRowChars];
            var sb = new StringBuilder();
            var pos = new Vec3(10, -50, 20);
            var v = Vec3.Zero;
            Vec3 f, r;
            SwimInputMap.LookFromYawPitch(30, 0, out f, out r);
            ulong tick = 6000;
            double o2 = 45;
            for (int s = 0; s < N1Script.Length; s++)
            {
                double[] st = N1Script[s];
                if (s == 2) m.RestartStep();
                int ticks = (int)Math.Round(st[0] * 60);
                for (int k = 0; k < ticks; k++)
                {
                    var t = new SwimTickIn
                    {
                        Tick = tick, Strafe = st[1], Vertical = st[2], Forward = st[3], Sprint = st[4] > 0, LookForwardSn = f, LookRightSn = r,
                        EyeY = pos.Y, EyeSn = pos, ChestY = pos.Y - 0.4, VelocitySn = v, OxygenUnits = o2, Mutants = mutants,
                    };
                    SwimTickOut o = m.Tick(ref t);
                    Vec3 vNext = o.ControlsVelocity ? o.VelocitySn : v;
                    Vec3 after = pos + vNext * Dt;
                    var x = new SwimRowExtras
                    {
                        Utc = "2026-10-16T12:00:00.0000000Z", SiteVersion = 1, Dt = Dt, Src = "script", Step = s, VelocitySource = "poststep",
                        Exit = o.Transition < 0 ? SwimExit.Name(o.ExitReason) : "", ChestY = t.ChestY, Applied = o.ControlsVelocity, Gain = 1.0,
                        VApplied = vNext, VSe = vNext, PosAfter = after, Damp = false, O2Use = o.O2UseUnitsPerSec, O2Refill = o.O2RefillUnitsPerSec,
                        HaveSuit = true, O2Litres = o2 / 45.0 * 30.0, O2Capacity = 30, O2Multiplier = 1, Helmet = true,
                    };
                    rows.Add(m.Row(buf, "n1.s" + s, ref x, sb));
                    v = vNext;
                    pos = after;
                    o2 = Math.Max(0.0, Math.Min(45.0, o2 + o.OxygenDelta));
                    tick++;
                }
            }
            return rows;
        }

        private static string WriteRows(string dir, string name, List<string> rows)
        {
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, string.Join("\n", rows.ToArray()) + "\n", new UTF8Encoding(false));
            return file;
        }

        private const string ParseWithAnalyser =
            "import sys, json; sys.path.insert(0, 'tests/s2b'); import analyze_swim as A; " +
            "rows = [A.swim_row(json.loads(l)) for l in open(sys.argv[1], encoding='utf-8') if l.strip()]; " +
            "print(json.dumps({'rows': len(rows), 'fmt': sorted(set(r['fmt'] for r in rows)), " +
            "'velMeas': sum(1 for r in rows if r['velMeas'] is not None), 'damp': sum(1 for r in rows if r['damp'] is not None), " +
            "'helmet': sum(1 for r in rows if r.get('helmet') is not None), 'segs': len(set(r['seg'] for r in rows)), " +
            "'script': sum(1 for r in rows if r['src'] == 'script' and r['step'] is not None), 'utc': sum(1 for r in rows if r.get('t')), " +
            "'enter': sum(1 for r in rows if r['tr'] == 'enter'), 'o2': sum(1 for r in rows if r['o2'] is not None)}))";

        [TestMethod]
        public void Row_PluginRowsReplayInSwimReplay_N1Passes_AnalyserReadsThemAsLayoutA()
        {
            List<string> rows = PluginRows(0);
            int ticks = 0;
            foreach (double[] st in N1Script) ticks += (int)Math.Round(st[0] * 60);
            Assert.AreEqual(ticks, rows.Count);
            foreach (string row in rows)
            {
                Assert.IsNotNull(row);
                StringAssert.StartsWith(row, "{\"t\":\"2026-10-16T12:00:00.0000000Z\",\"ev\":\"swim\",\"v\":1,\"n\":");
                StringAssert.EndsWith(row, ",\"helmet\":1}");
                Assert.IsInstanceOfType(MiniJson.Parse(row), typeof(IDictionary<string, object>), "one JSON object per row");
            }
            string dir = Path.Combine(Path.GetTempPath(), "de-s2b-rows-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = WriteRows(dir, "se-plugin.jsonl", rows);
                // exit 0 = PASS (PyOracle throws otherwise): every row's outputs replayed, the state linked across rows, one gap
                string replay = PyOracle.Run(Path.Combine("tests", "s2b", "swim_replay.py"), "replay", file);
                StringAssert.Contains(replay, "PASS", replay);
                StringAssert.Contains(replay, "gaps=1 localMismatch=0 linkMismatch=0", replay);
                string n1 = PyOracle.Run(Path.Combine("tests", "s2b", "swim_replay.py"), "n1", file);
                StringAssert.Contains(n1, "PASS", n1);
                var parsed = (IDictionary<string, object>)MiniJson.Parse(PyOracle.Run("-c", ParseWithAnalyser, file));
                Assert.AreEqual((double)ticks, parsed["rows"]);
                CollectionAssert.AreEqual(new object[] { "A" }, (List<object>)parsed["fmt"], "the analyser reads the plugin's rows as the Wire layout");
                Assert.AreEqual((double)ticks, parsed["velMeas"], "vSe read as the measured motion");
                Assert.AreEqual((double)ticks, parsed["damp"]);
                Assert.AreEqual((double)ticks, parsed["helmet"]);
                Assert.AreEqual((double)ticks, parsed["script"], "src/step read");
                Assert.AreEqual((double)ticks, parsed["utc"]);
                Assert.AreEqual((double)ticks, parsed["o2"]);
                Assert.AreEqual((double)N1Script.Length, parsed["segs"], "one segment label per script step");
                Assert.AreEqual(1.0, parsed["enter"], "one entry, on the first tick");

                // S2b.M1 rows from the plugin: the replay of the true model is red
                string m1 = WriteRows(dir, "se-m1.jsonl", PluginRows(S2bMutants.BitM1));
                try
                {
                    PyOracle.Run(Path.Combine("tests", "s2b", "swim_replay.py"), "replay", m1);
                    Assert.Fail("S2b.M1 rows replayed green");
                }
                catch (InvalidOperationException e)
                {
                    StringAssert.Contains(e.Message, "exited 1", "red, not a usage error");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        // ================================================================================================ restore policy

 /// </summary>
        [TestMethod]
        public void Policy_ProxyFlying_WhileSwimmingHeadUnder_NotOutOrOffLatch()
        {
            var p = new SwimSuitPolicy();
            Assert.IsFalse(p.ProxyFlyingWanted(false, false), "not swimming: SE's own state");
            p.OnEnter(false, true);
            Assert.IsTrue(p.ProxyFlyingWanted(false, false), "head under: flying (no walking)");
            Assert.IsFalse(p.ProxyFlyingWanted(true, false), "head out: the jetpack decides");
            Assert.IsFalse(p.ProxyFlyingWanted(false, true), "S2b.M2: never touched");
            p.OnExit(SwimExit.Surface, false);
            Assert.IsFalse(p.ProxyFlyingWanted(false, false), "after the exit: SE's own state");
        }

        /// none: Subnautica's tank.</summary>
        [TestMethod]
        public void O2Rule_BottleWithGas_UsesSeOwnTiming()
        {
            Assert.AreEqual(1.0, SwimO2Rule.WantedMultiplier(true, 1.0, 17.637), "bottle: SE's own multiplier");
            Assert.AreEqual(17.637, SwimO2Rule.WantedMultiplier(false, 1.0, 17.637), "no bottle: Subnautica's tank");
            Assert.AreEqual(2.5, SwimO2Rule.WantedMultiplier(true, 2.5, 35.273), "SE's own multiplier as found (a mod may have set it)");
        }

        /// <summary>The lifepod respawn rule: lifts only a post-death character that is free under the waterline inside the site and not in a pressurized room.</summary>
        [TestMethod]
        public void RespawnRules_Lifepod_FiresOnlyFreeUnderWaterAfterADeath()
        {
            Assert.IsTrue(RespawnRules.Fire(true, false, false, true, true, 0f, -1551.0), "the drop pod case: free 1.5 km under the water");
            Assert.IsFalse(RespawnRules.Fire(false, false, false, true, true, 0f, -1551.0), "no death seen (the world load's character)");
            Assert.IsFalse(RespawnRules.Fire(true, true, false, true, true, 0f, -1551.0), "still in the pod's seat");
            Assert.IsFalse(RespawnRules.Fire(true, false, true, true, true, 0f, -1551.0), "dead");
            Assert.IsFalse(RespawnRules.Fire(true, false, false, false, true, 0f, -1551.0), "no physics yet");
            Assert.IsFalse(RespawnRules.Fire(true, false, false, true, false, 0f, -1551.0), "outside the site");
            Assert.IsFalse(RespawnRules.Fire(true, false, false, true, true, 0.9f, -20.0), "a pressurized room: an underwater base");
            Assert.IsFalse(RespawnRules.Fire(true, false, false, true, true, 0f, 0.6), "out of the water");
            Assert.IsTrue(RespawnRules.Settled(false, false, true, 0.6), "free out of the water: the watch ends");
            Assert.IsFalse(RespawnRules.Settled(true, false, true, 0.6), "seated: not settled yet");
            Assert.AreEqual(0.6, RespawnRules.LifepodSn.Y, "the lifepod point is at the surface, head out");
        }

        [TestMethod]
        public void Policy_JetpackAndDampeners_OffInTheWater_RestoredOnExit()
        {
            var p = new SwimSuitPolicy();
            Assert.IsFalse(p.MustSwitchJetpackOff(true, false, false), "not swimming: nothing forced");
            Assert.IsFalse(p.OnExit(SwimExit.Surface, false).SetJetpack, "not swimming: nothing restored");

            p.OnEnter(jetpackOn: true, dampenersOn: true);
            Assert.IsTrue(p.Active && p.JetpackWas && p.DampenersWas);
            Assert.IsTrue(p.MustSwitchJetpackOff(true, jetpackAllowed: false, mutantM2: false), "head under: off");
            Assert.IsFalse(p.MustSwitchJetpackOff(true, jetpackAllowed: true, mutantM2: false), "head out: the player's");
            Assert.IsFalse(p.MustSwitchJetpackOff(false, false, false), "already off");
            Assert.IsFalse(p.MustSwitchJetpackOff(true, false, mutantM2: true), "S2b.M2: left on");
            Assert.IsTrue(p.MustSwitchDampenersOff(true));
            Assert.IsFalse(p.MustSwitchDampenersOff(false));
            p.RememberO2Multiplier(1f);
            p.RememberO2Multiplier(17.6f);
            Assert.AreEqual(1f, p.O2MultiplierWas, "the first value is kept");

            SwimRestore r = p.OnExit(SwimExit.Surface, false);
            Assert.IsTrue(r.SetJetpack && r.JetpackTo && r.SetDampeners && r.DampenersTo && r.RestoreO2Multiplier, r.ToString());
            Assert.IsFalse(p.Active);
            Assert.IsFalse(p.OnExit(SwimExit.Surface, false).SetDampeners, "exit is once");

            p.OnEnter(false, true);
            r = p.OnExit(SwimExit.Jetpack, false);
            Assert.IsFalse(r.SetJetpack, "jetpack exit: the player switched it on, it stays");
            Assert.IsTrue(r.SetDampeners && r.DampenersTo);

            p.OnEnter(true, false);
            r = p.OnExit(SwimExit.Grounded, false);
            Assert.IsTrue(r.SetJetpack && r.JetpackTo, "walked out: the jetpack comes back as it was");
            Assert.IsTrue(r.SetDampeners && !r.DampenersTo);

            p.OnEnter(true, true);
            p.RememberO2Multiplier(1f);
            r = p.OnExit(SwimExit.Seated, false);
            Assert.IsFalse(r.SetJetpack, "seated: the jetpack waits for the seat to be left");
            Assert.IsTrue(r.JetpackParked && r.SetDampeners && r.DampenersTo && r.RestoreO2Multiplier, r.ToString());
            StringAssert.Contains(r.ToString(), "jetpack->parked");
            Assert.IsTrue(p.Active && p.Parked, "the remembered states are kept across the seat");
            Assert.IsFalse(p.OnExit(SwimExit.ModelOff, false).SetJetpack, "parked: no second restore");
            Assert.IsTrue(p.Active && p.Parked);
            r = p.LeaveSeatOutOfWater(false);
            Assert.IsTrue(r.SetJetpack && r.JetpackTo && r.SetDampeners && r.DampenersTo,
                          "out of the water: the jetpack as before the dive, set after the cockpit's own restore (its copy is the swim's off)");
            Assert.IsFalse(p.Active || p.Parked);
            Assert.IsFalse(p.LeaveSeatOutOfWater(false).SetJetpack, "once");

            p.OnEnter(true, false);
            p.OnExit(SwimExit.Seated, false);
            p.Unpark();
            Assert.IsTrue(p.Active && !p.Parked, "back in the water from the seat: the swim goes on");
            Assert.IsTrue(p.JetpackWas && !p.DampenersWas, "with the states of the first entry");
            r = p.OnExit(SwimExit.Surface, false);
            Assert.IsTrue(r.SetJetpack && r.JetpackTo && r.SetDampeners && !r.DampenersTo);

            p.OnEnter(true, true);
            p.OnExit(SwimExit.Seated, false);
            r = p.OnExit(SwimExit.CharacterChanged, false);
            Assert.IsFalse(r.SetJetpack || r.SetDampeners, "another character while parked: nothing");
            Assert.IsFalse(p.Active || p.Parked);
            p.OnEnter(true, true);
            p.OnExit(SwimExit.Seated, false);
            Assert.IsFalse(p.LeaveSeatOutOfWater(mutantM2: true).SetJetpack, "S2b.M2: the jetpack is never restored");

            p.OnEnter(true, true);
            p.RememberO2Multiplier(1f);
            r = p.OnExit(SwimExit.Dead, false);
            Assert.IsFalse(r.SetJetpack || r.SetDampeners || r.RestoreO2Multiplier, "dead: nothing to restore");

            p.OnEnter(true, true);
            r = p.OnExit(SwimExit.ModelOff, mutantM2: true);
            Assert.IsFalse(r.SetJetpack, "S2b.M2: the jetpack is never restored");
            Assert.IsTrue(r.SetDampeners);
            Assert.IsFalse(r.RestoreO2Multiplier, "nothing remembered this time");
        }

        // ================================================================================================ oxygen rule

        [TestMethod]
        public void O2Rule_EngineerSuit_AtSubnauticasRate()
        {
            // the default engineer (Content/Data/Characters.sbc): 30 L suit, OxygenConsumption 0.063, multiplier 1
 // -> stock 0.0378 L/s (13 min); Subnautica's 45 s tank at 1 unit/s -> 30/45 L/s
            double m1 = SwimO2Rule.Multiplier(1.0, 45, 30, 0.063);
            Assert.AreEqual(30.0 / 45.0 / (0.063 * 0.6), m1, 1e-9);
            Assert.AreEqual(17.636684, m1, 1e-6);
            Assert.AreEqual(1.5 * m1, SwimO2Rule.Multiplier(1.5, 45, 30, 0.063), 1e-9);
            Assert.AreEqual(2.0 * m1, SwimO2Rule.Multiplier(2.0, 45, 30, 0.063), 1e-9);
            Assert.AreEqual(0.0, SwimO2Rule.Multiplier(0, 45, 30, 0.063), "head out: no consumption");
            Assert.IsTrue(double.IsNaN(SwimO2Rule.Multiplier(1, 45, 0, 0.063)));
            Assert.IsTrue(double.IsNaN(SwimO2Rule.Multiplier(1, 45, 30, 0)));
            // SE then breathes rate x mult x 0.6 L/s = the same fraction of its tank per second as Subnautica's diver
            Assert.AreEqual(SwimO2Rule.FractionPerSecond(1.0, 45) * 30, 0.063 * m1 * SwimO2Rule.SeSinkPerSecondFactor, 1e-12);

            Assert.AreEqual(30.0 / 45.0 * 30.0 / 6.0, SwimO2Rule.RefillLitres(30, 45, 30, 1.0 / 6.0), 1e-12, "10 ticks of refill");
            Assert.AreEqual(0.0, SwimO2Rule.RefillLitres(0, 45, 30, 1));
            Assert.IsTrue(SwimO2Rule.NeedsWrite(double.NaN, 1.0));
            Assert.IsFalse(SwimO2Rule.NeedsWrite(17.6367, 17.6366));
            Assert.IsTrue(SwimO2Rule.NeedsWrite(1.0, 17.6));
            Assert.IsFalse(SwimO2Rule.NeedsWrite(1.0, double.NaN), "unknown: leave SE's value alone");
        }

        // ================================================================================================ in-step compensation

        [TestMethod]
        public void Gain_LearnsTheInStepDamping_IgnoresContactsAndSlowSamples()
        {
            var g = new SwimGain();
            Assert.AreEqual("auto", g.Mode);
            Assert.AreEqual(1.0, g.Gain, "no samples: no compensation");
            var applied = new Vec3(3, -1, 4);
            double k = 1.0 - 0.7 / 60.0; // what a Havok linear damping of 0.7 would leave after one 1/60 s step
            for (int i = 0; i < SwimGain.MinSamples - 1; i++) Assert.IsTrue(g.Observe(applied, applied * k, false));
            Assert.AreEqual(1.0, g.Gain, "not enough samples yet");
            Assert.IsTrue(g.Observe(applied, applied * k, false));
            Assert.AreEqual(1.0 / k, g.Gain, 1e-12, "auto: 1 / in-step ratio");

            Assert.IsFalse(g.Observe(applied, applied * k, true), "grounded: contact, ignored");
            Assert.IsFalse(g.Observe(new Vec3(0.1, 0, 0.1), new Vec3(0.09, 0, 0.09), false), "too slow");
            Assert.IsFalse(g.Observe(applied, applied * 0.3, false), "head-on contact: ratio far below 0.9");
            Assert.IsFalse(g.Observe(applied, applied * k + new Vec3(0, 0.5, 0), false), "deflected: residual across the motion");
            Assert.AreEqual(2, g.Rejected, "free, fast samples refused by the ratio or residual filters (grounded and slow ones are not eligible)");
            Assert.AreEqual(1.0 / k, g.Gain, 1e-12, "rejected samples leave the estimate alone");

            string err;
            Assert.IsTrue(g.SetMode("off", out err));
            Assert.AreEqual(1.0, g.Gain);
            Assert.IsTrue(g.SetMode(1.05, out err));
            Assert.AreEqual(1.05, g.Gain);
            Assert.IsTrue(g.SetMode("1.02", out err));
            Assert.AreEqual(1.02, g.Gain, 1e-12);
            Assert.IsFalse(g.SetMode(3.0, out err));
            Assert.IsFalse(g.SetMode("fast", out err));
            Assert.IsTrue(g.SetMode("auto", out err));
            g.Reset();
            Assert.AreEqual(1.0, g.Gain, "reset: back to no compensation");
        }

        [TestMethod]
        public void Gain_BiasDiagnostic_ShowsAnAdditiveGravityLeak()
        {
            //  a gravity leak (-9.81/60 m/s per step) passes the ratio filter on upward
 // samples (r ~ 0.966) and would set a +3.6 % gain; the per-axis mean residual shows it as a vertical bias
            var g = new SwimGain();
            var up = new Vec3(0, 4.725, 0);
            var leak = new Vec3(0, -9.81 / 60.0, 0);
            for (int i = 0; i < 60; i++) g.Observe(up, up + leak, false);
            Assert.IsTrue(g.Samples >= SwimGain.MinSamples, "the leak passes the ratio filter: " + g.Samples);
            Assert.AreEqual(leak.Y, g.Bias.Y, 1e-9, "vertical bias = the leak");
            Assert.AreEqual(0.0, g.Bias.X, 1e-12);
            Assert.AreEqual(60, g.BiasSamples);
            StringAssert.Contains(g.Describe(), "bias=(0,-0.164,0)");
            g.Observe(up, up, true);
            Assert.AreEqual(60, g.BiasSamples, "grounded samples are not free water");
            g.Reset();
            Assert.AreEqual(0, g.BiasSamples);
            StringAssert.Contains(g.Describe(), "bias=n/a");
        }

        // ================================================================================================ row text

        [TestMethod]
        public void RowText_FixedUtcInt_NoAllocationOnceWarm()
        {
            //  the plugin's row is formatted digit by digit into a reused build pass
            var sb = new StringBuilder(256);
            SwimText.AppendFixed(sb, -1.5, 6);
            sb.Append('|');
            SwimText.AppendFixed(sb, -0.0000004, 6);
            sb.Append('|');
            SwimText.AppendFixed(sb, 1.0 / 60.0, 9);
            sb.Append('|');
            SwimText.AppendFixed(sb, 17.6370004, 6);
            sb.Append('|');
            SwimText.AppendFixed(sb, double.NaN, 6);
            sb.Append('|');
            SwimText.AppendFixed(sb, 2e15, 6);
            sb.Append('|');
            SwimText.AppendInt(sb, -1);
            sb.Append('|');
            SwimText.AppendInt(sb, 1234567890123);
            Assert.AreEqual("-1.500000|0.000000|0.016666667|17.637000|null|null|-1|1234567890123", sb.ToString());
            var t = new DateTime(2026, 10, 16, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);
            sb.Length = 0;
            SwimText.AppendUtc(sb, t.Ticks);
            Assert.AreEqual(t.ToString("o", System.Globalization.CultureInfo.InvariantCulture), sb.ToString(), "the round-trip 'o' text");

            var m = new SwimModelAdapter();
            var tin = new SwimTickIn
            {
                Tick = 600, Forward = 1, LookForwardSn = new Vec3(0, 0, 1), LookRightSn = new Vec3(1, 0, 0), EyeY = -50,
                EyeSn = new Vec3(0, -50, 0), ChestY = -50.4, OxygenUnits = 45,
            };
            m.Tick(ref tin);
            var buf = new char[SwimTrace.MaxRowChars];
            var rowSb = new StringBuilder(SwimTrace.MaxRowChars + 1024);
            var x = new SwimRowExtras
            {
                UtcTicks = t.Ticks, SiteVersion = 1, Dt = Dt, Src = "script", Step = 0, VelocitySource = "poststep", Exit = "", ChestY = -50.4,
                Applied = true, Gain = 1.0, VApplied = new Vec3(0, 0, 1.5), VSe = new Vec3(0, 0, 1.5), PosAfter = new Vec3(0, -50, 0.025),
                O2Use = 1, HaveSuit = true, O2Litres = 30, O2Capacity = 30, O2Multiplier = 17.637, Helmet = true,
            };
            Assert.IsTrue(m.AppendRow(buf, "seg", ref x, rowSb));
            string row = rowSb.ToString();
            var parsed = (IDictionary<string, object>)MiniJson.Parse(row);
            Assert.AreEqual(t.ToString("o", System.Globalization.CultureInfo.InvariantCulture), parsed["t"]);
            Assert.AreEqual(0.016666667, (double)parsed["dt"], 1e-12);
            Assert.AreEqual(17.637, (double)parsed["o2Mult"], 1e-12);
            Assert.AreEqual(0.0, (double)parsed["s_eu"], "the model's EyeUnder state before the first step");
            StringAssert.Contains(row, "\"vSe\":[0.000000,0.000000,1.500000]");

            try { AppDomain.MonitoringIsEnabled = true; }
            catch (Exception e) { Assert.Inconclusive("AppDomain monitoring unavailable: " + e.Message); }
            for (int k = 0; k < 100; k++) m.AppendRow(buf, "seg", ref x, rowSb);
            GC.Collect();
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int k = 0; k < 20000; k++)
            {
                tin.Tick = (ulong)(601 + k);
                m.Tick(ref tin);
                x.UtcTicks = t.Ticks + k * 166667L;
                m.AppendRow(buf, "seg", ref x, rowSb);
            }
            long after = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            Console.WriteLine("20,000 ticks + rows allocated " + (after - before) + " bytes (whole AppDomain)");
            Assert.IsTrue(after - before < 256 * 1024, "allocated " + (after - before) + " bytes for 20,000 rows");
        }

        // ================================================================================================ frames

        [TestMethod]
        public void Velocity_SeToSnAndBack_ScalesOnlyTheHorizontal()
        {
            SiteMap site = SiteMap.SiteV1Test;
            Vec3 p = site.SnToSe(new Vec3(120, -50, -300));
            var vSn = new Vec3(3.0, -1.5, 4.0);
            Vec3 vSe = SwimModelAdapter.SnToSeVelocity(site, p, vSn);
            Vec3 back = SwimModelAdapter.SeToSnVelocity(site, p, vSe);
            AssertVec(vSn, back, "round trip");
            double s = site.ScaleAt(p);
            Assert.IsTrue(s > 1.0 && s < 1.001, "scale at -50 m: " + s);
            Vec3 e, u, n;
            site.FrameAt(p, out e, out u, out n);
            Assert.AreEqual(-1.5, Vec3.Dot(vSe, u), 1e-12, "vertical speed unchanged (radial up)");
            double hSe = Math.Sqrt(Math.Pow(Vec3.Dot(vSe, e), 2) + Math.Pow(Vec3.Dot(vSe, n), 2));
            Assert.AreEqual(5.0 / s, hSe, 1e-9, "horizontal SE speed = SN speed / s");
            Vec3 dir = SwimModelAdapter.SeToSnDirection(site, p, u * 3.0);
            AssertVec(Vec3.UnitY, dir, "radial up is SN up");
        }

        // ================================================================================================ mutants and chat

        [TestMethod]
        public void Mutants_S2bIds_ThroughTheRegistry()
        {
            Assert.AreEqual("", S2bMutants.Active);
            Assert.AreEqual(0u, S2bMutants.ModelBits);
            foreach (string id in new[] { "S2b.M1", "s2b.m2", "S2B.M3", "S2b.M4" })
            {
                string r = Mutants.Set(id, true);
                StringAssert.StartsWith(r, "S2b.M", id + " -> " + r);
                StringAssert.Contains(r, "ON");
            }
            Assert.IsTrue(S2bMutants.NumbersX15 && S2bMutants.JetpackLeftOn && S2bMutants.NoHysteresis && S2bMutants.OxygenRuleOff);
            Assert.AreEqual(S2bMutants.BitM1 | S2bMutants.BitM2 | S2bMutants.BitM3 | S2bMutants.BitM4, S2bMutants.ModelBits);
            StringAssert.Contains(Mutants.Active, "S2b.M1,S2b.M2,S2b.M3,S2b.M4");
            StringAssert.Contains(Mutants.Set("S2b.M3", false), "OFF");
            Assert.IsFalse(S2bMutants.NoHysteresis);
            Mutants.ClearAll();
            Assert.AreEqual("", S2bMutants.Active);
            StringAssert.StartsWith(Mutants.Set("S2b.M9", true), "UNKNOWN");
            Assert.IsNull(S2bMutants.Set("S2.M1", true), "S2 ids are not S2b's");
        }

        [TestMethod]
        public void Chat_DeSwim_Syntax()
        {
            string usage;
            Dictionary<string, object> a = S2bArgs.ParseChat(new[] { "swim" }, out usage);
            Assert.AreEqual(true, a["dump"]);
            a = S2bArgs.ParseChat(new[] { "swim", "dump" }, out usage);
            Assert.AreEqual(true, a["dump"]);
            a = S2bArgs.ParseChat(new[] { "swim", "off" }, out usage);
            Assert.AreEqual("off", a["model"]);
            a = S2bArgs.ParseChat(new[] { "swim", "model", "ON" }, out usage);
            Assert.AreEqual("on", a["model"]);
            Assert.IsNull(S2bArgs.ParseChat(new[] { "swim", "model" }, out usage));
            Assert.AreEqual(S2bArgs.SwimUsage, usage);
            a = S2bArgs.ParseChat(new[] { "swim", "stop" }, out usage);
            Assert.AreEqual("stop", a["script"]);
            a = S2bArgs.ParseChat(new[] { "swim", "script", "10,1", "5,0" }, out usage);
            Assert.AreEqual("10,1;5,0", a["script"]);
            bool stop;
            string err;
            SwimScript s = SwimScript.Parse(a["script"], out stop, out err);
            Assert.IsNotNull(s, err);
            Assert.AreEqual(900, s.TotalTicks);
            a = S2bArgs.ParseChat(new[] { "swim", "tp", "0", "-50", "12.5", "90" }, out usage);
            CollectionAssert.AreEqual(new object[] { 0.0, -50.0, 12.5 }, (List<object>)a["teleportChar"]);
            Assert.AreEqual(90.0, a["yawDeg"]);
            Assert.IsNull(S2bArgs.ParseChat(new[] { "swim", "tp", "0", "x", "1" }, out usage));
            a = S2bArgs.ParseChat(new[] { "swim", "o2" }, out usage);
            Assert.AreEqual("dump", a["o2"]);
            a = S2bArgs.ParseChat(new[] { "swim", "model=off" }, out usage);
            Assert.AreEqual("off", a["model"]);
            Assert.IsFalse(a.ContainsKey("dump"), "key=value only: no default dump");
            Assert.IsNull(S2bArgs.ParseChat(new[] { "swim", "fly" }, out usage));
        }

        private static void AssertVec(Vec3 expected, Vec3 actual, string what)
        {
            Assert.AreEqual(expected.X, actual.X, 1e-9, what + " x");
            Assert.AreEqual(expected.Y, actual.Y, 1e-9, what + " y");
            Assert.AreEqual(expected.Z, actual.Z, 1e-9, what + " z");
        }
    }
}
