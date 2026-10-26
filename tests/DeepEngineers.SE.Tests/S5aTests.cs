using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S5a "Arms and breath", SE side, game-independent parts: the fins setting through the model seam (default ultra,
 /// the S5a.M1 claim-but-not-apply mutant), the C1 swim extension fill (<see cref="SwimWireFill"/>: flags, axes, speed
 /// fractions, the O2 fraction and its override, the idle state, the S5a.M2/M3 mutants), the <c>/de swim fins</c> and
 /// <c>/de swim o2</c> chat syntax and the mutant registry ids.
 /// </summary>
    [TestClass]
    public class S5aTests
    {
        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); SwimWireFill.Reset(); }

        private static SwimTickIn In(double eyeY, double fwd = 0, double strafe = 0, double vert = 0, double fins = 3.2, bool notApplied = false, ulong tick = 600)
        {
            Vec3 f, r;
            SwimInputMap.LookFromYawPitch(0, 0, out f, out r);
            return new SwimTickIn
            {
                Tick = tick, Forward = fwd, Strafe = strafe, Vertical = vert, LookForwardSn = f, LookRightSn = r, EyeY = eyeY,
                EyeSn = new Vec3(0, eyeY, 0), ChestY = eyeY - 0.4, OxygenUnits = 45, FinsModifier = fins, FinsNotApplied = notApplied,
            };
        }

        /// <summary>Holds one input for <paramref name="ticks"/> with a perfect SE (the imposed velocity comes back).</summary>
        private static SwimTickOut Hold(SwimModelAdapter m, double fwd, double strafe, double vert, int ticks, double fins, bool notApplied = false)
        {
            var v = Vec3.Zero;
            SwimTickOut o = default(SwimTickOut);
            for (int k = 0; k < ticks; k++)
            {
                SwimTickIn t = In(-50, fwd, strafe, vert, fins, notApplied, (ulong)(600 + k));
                t.VelocitySn = v;
                o = m.Tick(ref t);
                if (o.ControlsVelocity) v = o.VelocitySn;
            }
            return o;
        }

        [TestMethod]
        public void Fins_DefaultUltra_ThroughTheSeam_NoFinsKeepsS2b()
        {
            Assert.AreEqual(SwimFins.Ultra, SwimFins.Default);
            Assert.AreEqual(3.2, SwimFins.Modifier(SwimFins.Default));
            Assert.AreEqual(8.1, Hold(new SwimModelAdapter(), 1, 0, 0, 240, 3.2).VelocitySn.Z, 1e-9, "Ultraglide forward (7.6 + 3.2) x 0.75");
            Assert.AreEqual(7.125, Hold(new SwimModelAdapter(), 0, 0, 1, 240, 3.2).VelocitySn.Y, 1e-9, "Ultraglide vertical (6.3 + 3.2) x 0.75");
            Assert.AreEqual(7.125, Hold(new SwimModelAdapter(), 1, 0, 0, 240, 1.9).VelocitySn.Z, 1e-9, "fins (7.6 + 1.9) x 0.75");
            Assert.AreEqual(5.7, Hold(new SwimModelAdapter(), 1, 0, 0, 240, 0.0).VelocitySn.Z, 1e-9, "off: the S2b number");
            var m = new SwimModelAdapter();
            SwimTickOut o = Hold(m, 1, 0, 0, 1, 3.2);
            Assert.IsTrue(o.Stepped);
            Assert.AreEqual(10.8, o.MaxSpeed, 1e-9, "the step's max speed carries the fins (the C1 speed reference)");
            Assert.AreEqual(8.1, SwimWire.SpeedReference(o.MaxSpeed), 1e-9);
            var n = SwimModelAdapter.Numbers();
            CollectionAssert.AreEqual(new object[] { 0.0, 1.9, 3.2 }, (object[])n["finsSpeedAdjust"]);
        }

        [TestMethod]
        public void Fins_S5aM1_ClaimsTheFinsInTheRow_ButSwimsWithout()
        {
            var m = new SwimModelAdapter();
            SwimTickOut o = Hold(m, 1, 0, 0, 240, 3.2, notApplied: true);
            Assert.AreEqual(5.7, o.VelocitySn.Z, 1e-9, "S5a.M1: the model ran without the modifier");
            var buf = new char[SwimTrace.MaxRowChars];
            var x = new SwimRowExtras { Utc = "2026-10-16T12:00:00.0000000Z", Src = "script", VelocitySource = "poststep" };
            string row = m.Row(buf, null, ref x, new System.Text.StringBuilder());
            StringAssert.Contains(row, "\"fin\":\"400999999999999a\"", "the row claims 3.2: the Python replay goes red");
            var ok = new SwimModelAdapter();
            x = new SwimRowExtras { Utc = "2026-10-16T12:00:00.0000000Z", Src = "script", VelocitySource = "poststep" };
            Hold(ok, 1, 0, 0, 240, 3.2);
            StringAssert.Contains(ok.Row(buf, null, ref x, new System.Text.StringBuilder()), "\"fin\":\"400999999999999a\"");
        }

        // ================================================================================================ the C1 fill

        private static SwimWireSource Swimming(double fwd = 1, double strafe = 0, double vert = 0, double vz = 8.1, double vy = 0.0, double o2 = 0.5)
        {
            return new SwimWireSource
            {
                Active = true, Latched = true, Mode = SwimMode.Swim, Controls = true, Grounded = false, JetpackAllowed = false, EyeY = -50,
                Forward = fwd, Strafe = strafe, Vertical = vert, Velocity = new Vec3(0, vy, vz), LastMaxSpeed = 10.8, Fins = SwimFins.Ultra,
                O2Fraction = o2, O2Override = double.NaN, DepthClass = SwimDepthClass.Safe,
            };
        }

        [TestMethod]
        public void Wire_Swimming_FlagsAxesSpeedsO2()
        {
            SwimWireSource s = Swimming();
            SwimWireState w = SwimWireFill.Compose(ref s);
            Assert.AreEqual(SwimWire.Version, w.Version);
            Assert.IsTrue(w.Has(SwimWireFlags.InWater) && w.Has(SwimWireFlags.Underwater) && w.Has(SwimWireFlags.Moving) && w.Has(SwimWireFlags.Controls) && w.Has(SwimWireFlags.JetpackSuppressed));
            Assert.IsFalse(w.Has(SwimWireFlags.AtSurface) || w.Has(SwimWireFlags.Grounded) || w.Has(SwimWireFlags.O2Override));
            Assert.AreEqual((byte)SwimMode.Swim, w.Mode);
            Assert.AreEqual((byte)SwimFins.Ultra, w.Fins);
            Assert.AreEqual(127, w.MoveForward);
            Assert.AreEqual(0, w.MoveStrafe);
            Assert.AreEqual(0, w.MoveVertical);
            Assert.AreEqual(255, w.SpeedH, "8.1 m/s over the 8.1 m/s steady reference of a 10.8 max");
            Assert.AreEqual(0, w.SpeedV);
            Assert.AreEqual(0, w.VyFrac);
            Assert.AreEqual(128, w.O2, "0.5 -> 128");
            Assert.AreEqual((byte)SwimDepthClass.Safe, w.DepthClass);

            s = Swimming(0, -1, 1, 4.05, -3.0375, 0.2);
            w = SwimWireFill.Compose(ref s);
            Assert.AreEqual(-127, w.MoveStrafe);
            Assert.AreEqual(127, w.MoveVertical);
            Assert.IsTrue(w.SpeedH == 127 || w.SpeedH == 128, "half the reference (4.05 / 8.1 sits on the rounding boundary): " + w.SpeedH);
            Assert.AreEqual(96, w.SpeedV, "|-3.0375| / 8.1 = 0.375");
            Assert.AreEqual(-48, w.VyFrac, "signed: sinking");
            Assert.AreEqual(51, w.O2);

            // the surface: Run mode, the head out, SE's jetpack allowed again
            s = Swimming();
            s.Mode = SwimMode.Surface; s.EyeY = 0.3; s.JetpackAllowed = true; s.DepthClass = SwimDepthClass.Surface;
            w = SwimWireFill.Compose(ref s);
            Assert.IsTrue(w.Has(SwimWireFlags.InWater) && w.Has(SwimWireFlags.AtSurface) && !w.Has(SwimWireFlags.Underwater) && !w.Has(SwimWireFlags.JetpackSuppressed));
            Assert.AreEqual((byte)SwimMode.Surface, w.Mode);

            // Fly: the latch on, the model inert, SE's jetpack moves the engineer: no speed fractions
            s = Swimming(); s.Mode = SwimMode.Fly; s.Controls = false; s.JetpackAllowed = true; s.EyeY = 0.5;
            w = SwimWireFill.Compose(ref s);
            Assert.IsTrue(w.Has(SwimWireFlags.InWater) && !w.Has(SwimWireFlags.Controls));
            Assert.AreEqual(0, w.SpeedH);
            Assert.AreEqual((byte)SwimMode.Fly, w.Mode);

            // an unreadable suit reads as full
            s = Swimming(); s.O2Fraction = double.NaN;
            Assert.AreEqual(255, SwimWireFill.Compose(ref s).O2);
            // no motor step yet: no reference, no speed
            s = Swimming(); s.LastMaxSpeed = 0;
            Assert.AreEqual(0, SwimWireFill.Compose(ref s).SpeedH);
        }

        [TestMethod]
        public void Wire_Idle_FlagsZero_O2Full_FinsKept_OverrideStillApplies()
        {
            var s = new SwimWireSource { Active = false, Fins = SwimFins.Basic, O2Override = double.NaN, O2Fraction = 0.3 };
            SwimWireState w = SwimWireFill.Compose(ref s);
            Assert.AreEqual(SwimWire.Version, w.Version);
            Assert.AreEqual(0, w.Flags, "out of the water / no character: the arms hide");
            Assert.AreEqual((byte)SwimMode.Walk, w.Mode);
            Assert.AreEqual((byte)SwimFins.Basic, w.Fins);
            Assert.AreEqual(255, w.O2, "idle: full (no choking out of the water)");
            Assert.AreEqual(0, w.SpeedH + w.SpeedV + w.MoveForward + w.MoveStrafe + w.MoveVertical + w.VyFrac);
            s.O2Override = 0.05;
            w = SwimWireFill.Compose(ref s);
            Assert.AreEqual(13, w.O2, "the override is mirrored even idle (test only)");
            Assert.IsTrue(w.Has(SwimWireFlags.O2Override));
            Assert.AreEqual(SwimWireFlags.O2Override, w.Flags);
        }

        [TestMethod]
        public void Wire_O2Override_ReplacesTheSuitFraction_AutoRestoresIt()
        {
            SwimWireSource s = Swimming(o2: 0.9);
            Assert.AreEqual(230, SwimWireFill.Compose(ref s).O2, "0.9 x 255 = 229.5 -> 230");
            s.O2Override = 1.0;
            SwimWireState w = SwimWireFill.Compose(ref s);
            Assert.AreEqual(255, w.O2);
            Assert.IsTrue(w.Has(SwimWireFlags.O2Override));
            s.O2Override = 0.2;
            Assert.AreEqual(51, SwimWireFill.Compose(ref s).O2);
            s.O2Override = 0.05;
            Assert.AreEqual(13, SwimWireFill.Compose(ref s).O2);
            s.O2Override = double.NaN;
            w = SwimWireFill.Compose(ref s);
            Assert.AreEqual(230, w.O2, "auto: the suit again");
            Assert.IsFalse(w.Has(SwimWireFlags.O2Override));
        }

        [TestMethod]
        public void Wire_Mutants_M2StuckO2_M3FrozenFlags()
        {
            Mutants.Set("S5a.M2", true);
            SwimWireSource s = Swimming(o2: 0.9);
            Assert.AreEqual(230, SwimWireFill.Compose(ref s).O2);
            s.O2Fraction = 0.1;
            Assert.AreEqual(230, SwimWireFill.Compose(ref s).O2, "S5a.M2: stuck at the first value");
            s.O2Override = 0.05;
            SwimWireState w = SwimWireFill.Compose(ref s);
            Assert.AreEqual(230, w.O2, "the override cannot move it either");
            Assert.IsFalse(w.Has(SwimWireFlags.O2Override));
            Mutants.ClearAll();
            SwimWireFill.Reset();
            s.O2Override = double.NaN;
            Assert.AreEqual(26, SwimWireFill.Compose(ref s).O2, "cleared: 0.1 -> 26");

            Mutants.Set("S5a.M3", true);
            s = Swimming();
            w = SwimWireFill.Compose(ref s);
            Assert.AreEqual(127, w.MoveForward);
            s = Swimming(0, 0, 0, 0.0, 0.0, 0.3);
            s.Latched = false; s.Mode = SwimMode.Walk; s.Controls = false; s.EyeY = 3.0;
            w = SwimWireFill.Compose(ref s);
            Assert.IsTrue(w.Has(SwimWireFlags.InWater) && w.Has(SwimWireFlags.Moving), "S5a.M3: the flags never change");
            Assert.AreEqual(127, w.MoveForward);
            Assert.AreEqual(255, w.SpeedH);
            Assert.AreEqual(77, w.O2, "the O2 byte still follows (0.3 -> 77)");
            s.Active = false;
            Assert.AreEqual(0, SwimWireFill.Compose(ref s).Flags, "idle is still idle (the controller stopped): M3 freezes active ticks only");
        }

        [TestMethod]
        public void Wire_WriteIntoTheFrame_RoundTrip()
        {
            SwimWireSource s = Swimming();
            SwimWireState w = SwimWireFill.Compose(ref s);
            var f = new SeFrame { seq = 2, flags = SeFrameFlags.InWorld | SeFrameFlags.CharacterSwimming };
            SwimWire.Write(ref f, ref w);
            SwimWireState r;
            Assert.IsTrue(SwimWire.TryRead(ref f, out r));
            Assert.AreEqual(w.Flags, r.Flags);
            Assert.AreEqual(w.SpeedH, r.SpeedH);
            Assert.AreEqual(w.O2, r.O2);
            Assert.AreEqual(SeFrameFlags.InWorld | SeFrameFlags.CharacterSwimming, f.flags, "the layout-1.0 flags are untouched");
            StringAssert.Contains(SwimWire.Describe(ref r), "mode=Swim fins=ultra speedH=255");
        }

        // ================================================================================================ chat and mutants

        [TestMethod]
        public void Chat_DeSwim_FinsAndO2Override()
        {
            string usage;
            Dictionary<string, object> a = S2bArgs.ParseChat(new[] { "swim", "fins", "ultra" }, out usage);
            Assert.AreEqual("ultra", a["fins"]);
            a = S2bArgs.ParseChat(new[] { "swim", "fins", "OFF" }, out usage);
            Assert.AreEqual("off", a["fins"]);
            a = S2bArgs.ParseChat(new[] { "swim", "fins", "Ultraglide" }, out usage);
            Assert.AreEqual("ultra", a["fins"]);
            a = S2bArgs.ParseChat(new[] { "swim", "fins=basic" }, out usage);
            Assert.AreEqual("basic", a["fins"]);
            Assert.IsNull(S2bArgs.ParseChat(new[] { "swim", "fins", "turbo" }, out usage));
            Assert.IsNull(S2bArgs.ParseChat(new[] { "swim", "fins" }, out usage));
            StringAssert.Contains(usage, "fins off|basic|ultra");
            a = S2bArgs.ParseChat(new[] { "swim", "o2" }, out usage);
            Assert.AreEqual("dump", a["o2"], "plain o2 still dumps the rule's numbers");
            a = S2bArgs.ParseChat(new[] { "swim", "o2", "0.2" }, out usage);
            Assert.AreEqual(0.2, a["o2"]);
            a = S2bArgs.ParseChat(new[] { "swim", "o2", "1" }, out usage);
            Assert.AreEqual(1.0, a["o2"]);
            a = S2bArgs.ParseChat(new[] { "swim", "o2", "auto" }, out usage);
            Assert.AreEqual("auto", a["o2"]);
            a = S2bArgs.ParseChat(new[] { "swim", "o2=0.05" }, out usage);
            Assert.AreEqual(0.05, a["o2"]);
            Assert.IsNull(S2bArgs.ParseChat(new[] { "swim", "o2", "1.5" }, out usage), "out of range");
            Assert.IsNull(S2bArgs.ParseChat(new[] { "swim", "o2", "full" }, out usage));
        }

        [TestMethod]
        public void Mutants_S5aIds_ThroughTheRegistry()
        {
            Assert.AreEqual("", S5aMutants.Active);
            foreach (string id in new[] { "S5a.M1", "s5a.m2", "S5A.M3" })
            {
                string r = Mutants.Set(id, true);
                StringAssert.StartsWith(r, "S5a.M", id + " -> " + r);
                StringAssert.Contains(r, "ON");
            }
            Assert.IsTrue(S5aMutants.FinsNotApplied && S5aMutants.O2Stuck && S5aMutants.WireFrozen);
            StringAssert.Contains(Mutants.Active, "S5a.M1,S5a.M2,S5a.M3");
            StringAssert.Contains(Mutants.Set("S5a.M2", false), "OFF");
            Assert.IsFalse(S5aMutants.O2Stuck);
            Mutants.ClearAll();
            Assert.AreEqual("", S5aMutants.Active);
            StringAssert.StartsWith(Mutants.Set("S5a.M9", true), "UNKNOWN");
            StringAssert.Contains(Mutants.Set("S5a.M9", true), "S5a.M3");
            Assert.IsNull(S5aMutants.Set("S2b.M1", true));
        }
    }
}
