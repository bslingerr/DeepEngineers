using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S5a: the C1 swim extension (<see cref="SwimWire"/>, CONTRACT §5.4 "C1 swim extension": bytes 0x94-0x9F of the C1
 /// pad of layout 1.0) and the fins modifier of the swim model (<see cref="SwimInputs.FinsModifier"/>, CONTRACT §2.10).
 /// The layout-1.0 hash stays <c>0x4f3a35f6fc1eaf31</c> (LayoutTests); the extension is pinned on its own here and in
 /// tests/s5a/analyze_s5a.py (table <c>C1_SWIM</c>, the same canonical text and hash).
 /// </summary>
    [TestClass]
    public class SwimWireTests
    {
        /// <summary>FNV-1a 64 of <see cref="SwimWire.CanonicalText"/>; the Python twin pins the same value.</summary>
        private const ulong PinnedExtensionHash = 0x832ba93ca25bc307UL;
        private const double Tight = 1e-12;

        // ------------------------------------------------------------------------------------------- placement
        [TestMethod]
        public void S5a_Extension_LiesInsideTheLayout10Pad_HashUnchanged()
        {
            RecordDef frame = Layout.Records.First(r => r.Name == "SeFrame");
            FieldDef pad = frame.Fields.First(f => f.Offset == SwimWire.PadOffset);
            Assert.IsTrue(pad.IsPad, "0x94 of SeFrame is a pad in layout 1.0");
            Assert.AreEqual(SwimWire.PadSize, pad.Size, "the C1 pad is 0x6C bytes");
            Assert.IsTrue(SwimWire.Offset >= pad.Offset && SwimWire.Offset + SwimWire.Size <= pad.Offset + pad.Size,
                          "the extension lies inside the pad: no field of layout 1.0 is touched");
            Assert.AreEqual(LayoutHash.PinnedLayout10, LayoutHash.Compiled, "layout 1.0 unchanged (no re-pin)");
            Assert.AreEqual(12, SwimWire.Size);
            Assert.AreEqual(0x94, SwimWire.OffVersion);
            Assert.AreEqual(0x9F, SwimWire.OffVyFrac);
            int[] offs = { SwimWire.OffVersion, SwimWire.OffFlags, SwimWire.OffMode, SwimWire.OffFins, SwimWire.OffSpeedH, SwimWire.OffSpeedV,
                           SwimWire.OffMoveForward, SwimWire.OffMoveStrafe, SwimWire.OffMoveVertical, SwimWire.OffO2, SwimWire.OffDepthClass, SwimWire.OffVyFrac };
            for (int k = 0; k < offs.Length; k++) Assert.AreEqual(SwimWire.Offset + k, offs[k], "byte " + k + " in offset order, no gap");
        }

        [TestMethod]
        public void S5a_Extension_CanonicalTextAndHash_Pinned()
        {
            string[] lines = SwimWire.CanonicalText.Split('\n');
            Assert.AreEqual("", lines.Last(), "a newline ends the last line");
            Assert.AreEqual(13, lines.Length - 1, "one ext line and 12 field lines");
            Assert.AreEqual("ext C1 swim 1 0x94 12", lines[0]);
            Assert.AreEqual("field swim version 0x94 1 u8", lines[1]);
            Assert.AreEqual("field swim vyFrac 0x9f 1 i8", lines[12]);
            Assert.AreEqual(PinnedExtensionHash, SwimWire.Hash,
                            "C1 swim extension hash changed: " + LayoutHash.Format(SwimWire.Hash) + " (re-pin here, in analyze_s5a.py C1_SWIM and CONTRACT §5.4 together)");
            // every field line names a distinct byte inside the extension
            var seen = new HashSet<int>();
            for (int k = 1; k <= 12; k++)
            {
                string[] w = lines[k].Split(' ');
                Assert.AreEqual("field", w[0]);
                int off = Convert.ToInt32(w[3].Substring(2), 16);
                Assert.IsTrue(seen.Add(off));
                Assert.IsTrue(off >= SwimWire.Offset && off < SwimWire.Offset + SwimWire.Size);
                Assert.AreEqual("1", w[4]);
                Assert.IsTrue(w[5] == "u8" || w[5] == "i8");
            }
        }

        // ------------------------------------------------------------------------------------------- codec
        [TestMethod]
        public void S5a_Extension_RoundTrip_AndVersionZeroReadsAsAbsent()
        {
            var f = new SeFrame { seq = 7, flags = SeFrameFlags.InWorld | SeFrameFlags.CharacterSwimming, tick = 1234, charS = 1f };
            SwimWireState none;
            Assert.IsFalse(SwimWire.TryRead(ref f, out none), "a fresh frame carries no extension");
            Assert.AreEqual(0, none.Version);

            var s = new SwimWireState
            {
                Flags = (byte)(SwimWireFlags.InWater | SwimWireFlags.Underwater | SwimWireFlags.Moving | SwimWireFlags.Controls | SwimWireFlags.JetpackSuppressed),
                Mode = (byte)SwimMode.Swim, Fins = (byte)SwimFins.Ultra, SpeedH = 191, SpeedV = 3,
                MoveForward = 127, MoveStrafe = -64, MoveVertical = 0, O2 = 128, DepthClass = (byte)SwimDepthClass.Unsafe, VyFrac = -12,
            };
            SwimWire.Write(ref f, ref s);
            SwimWireState r;
            Assert.IsTrue(SwimWire.TryRead(ref f, out r));
            Assert.AreEqual(SwimWire.Version, r.Version);
            Assert.AreEqual(s.Flags, r.Flags);
            Assert.AreEqual(s.Mode, r.Mode);
            Assert.AreEqual(s.Fins, r.Fins);
            Assert.AreEqual(s.SpeedH, r.SpeedH);
            Assert.AreEqual(s.SpeedV, r.SpeedV);
            Assert.AreEqual(s.MoveForward, r.MoveForward);
            Assert.AreEqual(s.MoveStrafe, r.MoveStrafe);
            Assert.AreEqual(s.MoveVertical, r.MoveVertical);
            Assert.AreEqual(s.O2, r.O2);
            Assert.AreEqual(s.DepthClass, r.DepthClass);
            Assert.AreEqual(s.VyFrac, r.VyFrac);
            Assert.IsTrue(r.Has(SwimWireFlags.InWater) && r.Has(SwimWireFlags.Underwater) && !r.Has(SwimWireFlags.AtSurface) && !r.Has(SwimWireFlags.O2Override));
            // the layout-1.0 fields around the pad are untouched
            Assert.AreEqual(7u, f.seq);
            Assert.AreEqual(SeFrameFlags.InWorld | SeFrameFlags.CharacterSwimming, f.flags);
            Assert.AreEqual(1234UL, f.tick);
            Assert.AreEqual(1f, f.charS);

            SwimWire.Clear(ref f);
            Assert.IsFalse(SwimWire.TryRead(ref f, out none));
            Assert.AreEqual(1234UL, f.tick);
        }

        [TestMethod]
        public void S5a_Extension_FractionsAndAxes_ClampAndRound()
        {
            Assert.AreEqual(0, SwimWire.Fraction(-0.5));
            Assert.AreEqual(0, SwimWire.Fraction(0.0));
            Assert.AreEqual(255, SwimWire.Fraction(1.0));
            Assert.AreEqual(255, SwimWire.Fraction(7.0));
            Assert.AreEqual(255, SwimWire.Fraction(double.NaN), "unknown = full: the ghost never chokes on missing data");
            Assert.AreEqual(128, SwimWire.Fraction(0.5), "127.5 rounds away from zero");
            Assert.AreEqual(51, SwimWire.Fraction(0.2));
            Assert.AreEqual(13, SwimWire.Fraction(0.05));
            Assert.AreEqual(0.2, SwimWire.FromFraction(51), 0.5 / 255);
            Assert.AreEqual(127, SwimWire.Axis(1.0));
            Assert.AreEqual(127, SwimWire.Axis(3.0));
            Assert.AreEqual(-127, SwimWire.Axis(-1.0));
            Assert.AreEqual(-127, SwimWire.Axis(-9.0));
            Assert.AreEqual(0, SwimWire.Axis(0.0));
            Assert.AreEqual(0, SwimWire.Axis(double.NaN));
            Assert.AreEqual(64, SwimWire.Axis(0.5));
            Assert.AreEqual(-64, SwimWire.Axis(-0.5));
            Assert.AreEqual(0.5, SwimWire.FromAxis(64), 0.5 / 127);
            for (double x = -1.5; x <= 1.5; x += 0.01)
            {
                Assert.AreEqual(Math.Max(-1.0, Math.Min(1.0, x)), SwimWire.FromAxis(SwimWire.Axis(x)), 0.5 / 127 + 1e-12, "axis " + x);
                Assert.AreEqual(Math.Max(0.0, Math.Min(1.0, x)), SwimWire.FromFraction(SwimWire.Fraction(x)), 0.5 / 255 + 1e-12, "fraction " + x);
            }
        }

        [TestMethod]
        public void S5a_Extension_SpeedReference_IsTheSteadyTopSpeedOfTheLastStep()
        {
            Assert.AreEqual(5.7, SwimWire.SpeedReference(7.6), Tight, "no fins forward: 7.6 x 0.75");
            Assert.AreEqual(8.1, SwimWire.SpeedReference(7.6 + 3.2), Tight, "Ultraglide: 10.8 x 0.75");
            Assert.AreEqual(0.0, SwimWire.SpeedReference(0.0));
            Assert.AreEqual(0.0, SwimWire.SpeedReference(double.NaN));
            Assert.AreEqual(255, SwimWire.SpeedFraction(5.7, SwimWire.SpeedReference(7.6)), "a full-speed swim reads 255");
            Assert.AreEqual(255, SwimWire.SpeedFraction(-5.7, 5.7), "unsigned");
            Assert.AreEqual(128, SwimWire.SpeedFraction(2.85, 5.7));
            Assert.AreEqual(0, SwimWire.SpeedFraction(3.0, 0.0), "no reference: 0");
            Assert.AreEqual(-127, SwimWire.SignedFraction(-4.725, 4.725), "sinking at the vertical top speed");
            Assert.AreEqual(64, SwimWire.SignedFraction(2.3625, 4.725));
        }

        [TestMethod]
        public void S5a_Fins_NamesModifiersParse()
        {
            Assert.AreEqual(SwimFins.Ultra, SwimFins.Default, "owner default 2026-10-16: Ultraglide on");
            Assert.AreEqual(0.0, SwimFins.Modifier(SwimFins.Off));
            Assert.AreEqual(1.9, SwimFins.Modifier(SwimFins.Basic));
            Assert.AreEqual(3.2, SwimFins.Modifier(SwimFins.Ultra));
            Assert.AreEqual(0.0, SwimFins.Modifier(77));
            Assert.AreEqual("ultra", SwimFins.Name(SwimFins.Ultra));
            uint f;
            Assert.IsTrue(SwimFins.TryParse("OFF", out f) && f == SwimFins.Off);
            Assert.IsTrue(SwimFins.TryParse(" basic ", out f) && f == SwimFins.Basic);
            Assert.IsTrue(SwimFins.TryParse("Ultraglide", out f) && f == SwimFins.Ultra);
            Assert.IsTrue(SwimFins.TryParse("2", out f) && f == SwimFins.Ultra);
            Assert.IsFalse(SwimFins.TryParse("turbo", out f));
            Assert.IsFalse(SwimFins.TryParse(null, out f));
        }

        // ------------------------------------------------------------------------------------------- the model with fins
        private static SwimOutputs[] Hold(double fins, double mx, double my, double mz, int ticks, double eyeY = -50.0, bool tool = false, bool latched = false)
        {
            var s = default(SwimState);
            if (latched) { s.Latched = true; s.Mode = SwimMode.Surface; s.EyeUnder = eyeY < 0.0; } // already in the water (a head-out hold)
            var outs = new SwimOutputs[ticks];
            var v = Vec3.Zero;
            var p = new Vec3(0.0, eyeY, 0.0);
            for (int t = 0; t < ticks; t++)
            {
                var i = new SwimInputs
                {
                    Tick = (ulong)(600 + t), MoveStrafe = mx, MoveVertical = my, MoveForward = mz, ToolHeld = tool,
                    LookForward = new Vec3(0, 0, 1), LookRight = new Vec3(1, 0, 0), Velocity = v, EyePos = p,
                    Oxygen = SwimThresholds.OxygenCapacity, FinsModifier = fins,
                };
                SwimOutputs o;
                SwimModel.Step(ref s, ref i, out o);
                outs[t] = o;
                if (o.ControlsVelocity) v = o.Velocity;
                if (eyeY < -5.0) p = new Vec3(p.X + v.X / 60.0, p.Y + v.Y / 60.0, p.Z + v.Z / 60.0); // deep: the eye may move
            }
            return outs;
        }

        [TestMethod]
        public void S5a_Fins_UltraglideAddsToEveryDirectionMaximum_NoFinsKeepsS2b()
        {
            // num += 3.2 after the direction selection, before the tool and the surface factor
            Assert.AreEqual(5.7, Hold(0.0, 0, 0, 1, 240).Last().Velocity.Z, Tight, "no fins: the S2b number 7.6 x 0.75");
            Assert.AreEqual(8.1, Hold(3.2, 0, 0, 1, 240).Last().Velocity.Z, Tight, "Ultraglide forward: (7.6 + 3.2) x 0.75");
            Assert.AreEqual(-8.1, Hold(3.2, 0, 0, -1, 240).Last().Velocity.Z, Tight, "backward too");
            Assert.AreEqual(8.1, Hold(3.2, 1, 0, 0, 240).Last().Velocity.X, Tight, "strafe too");
            Assert.AreEqual(7.125, Hold(3.2, 0, 1, 0, 240).Last().Velocity.Y, Tight, "vertical: (6.3 + 3.2) x 0.75");
            Assert.AreEqual(-7.125, Hold(3.2, 0, -1, 0, 240).Last().Velocity.Y, Tight);
            Assert.AreEqual(7.125, Hold(1.9, 0, 0, 1, 240).Last().Velocity.Z, Tight, "fins: (7.6 + 1.9) x 0.75");
            Assert.AreEqual(7.35, Hold(3.2, 0, 0, 1, 240, -50.0, true).Last().Velocity.Z, Tight, "tool after the fins: (7.6 + 3.2 - 1) x 0.75");
            SwimOutputs first = Hold(3.2, 0, 0, 1, 1)[0];
            Assert.AreEqual(10.8, first.MaxSpeed, Tight, "the max speed carries the fins");
            // the acceleration follows the ratio a / a_before: the first step adds 2 x 10.8/7.6
            Assert.AreEqual(2.0 * 10.8 / 7.6 * 0.75, first.Velocity.Z, Tight, "first step: dv x ratio, then x0.75");
            int t90 = Array.FindIndex(Hold(3.2, 0, 0, 1, 240), o => o.Velocity.Length >= 0.9 * 8.1);
            int t90Plain = Array.FindIndex(Hold(0.0, 0, 0, 1, 240), o => o.Velocity.Length >= 0.9 * 5.7);
            Assert.AreEqual(t90Plain, t90, "the same step count to 90 % (the acceleration scales with the max speed)");
        }

        [TestMethod]
        public void S5a_Fins_SurfaceFactorAppliesAfterTheFins()
        {
            // Run mode at the surface, eye +0.3 m, forward: the vertical floor 6.3 beats the Run forward 3.5,
 // then + 3.2 fins, x1.3 above the water, x0.75 steady; the surface rule only touches vy
            SwimOutputs[] outs = Hold(3.2, 0, 0, 1, 240, 0.3, false, true);
            Assert.AreEqual(SwimMode.Surface, outs.Last().Mode);
            Assert.AreEqual((6.3 + 3.2) * 1.3 * 0.75, outs.Last().Velocity.Z, 1e-9, "surface forward with Ultraglide: 9.2625 m/s");
            Assert.AreEqual((6.3 + 3.2) * 1.3, outs.First(o => o.SnStep).MaxSpeed, 1e-9);
            Assert.AreEqual(6.3 * 1.3 * 0.75, Hold(0.0, 0, 0, 1, 240, 0.3, false, true).Last().Velocity.Z, 1e-9, "no fins: the S2b surface number 6.14");
        }

        [TestMethod]
        public void S5a_Fins_TraceRowCarriesTheModifier_DefaultZero()
        {
            var s = default(SwimState);
            var i = new SwimInputs
            {
                Tick = 1, MoveForward = 1, LookForward = new Vec3(0, 0, 1), LookRight = new Vec3(1, 0, 0),
                EyePos = new Vec3(0, -50, 0), Oxygen = 45, FinsModifier = 3.2,
            };
            SwimOutputs o;
            SwimState pre = s;
            SwimModel.Step(ref s, ref i, out o);
            string row = SwimTrace.FormatString(ref pre, ref i, ref o);
            StringAssert.Contains(row, "\"fin\":\"400999999999999a\"", "3.2 as exact IEEE bits");
            Assert.IsTrue(row.IndexOf("\"fin\":", StringComparison.Ordinal) > row.IndexOf("\"ox\":", StringComparison.Ordinal), "after ox, before spr");
            Assert.IsTrue(row.IndexOf("\"fin\":", StringComparison.Ordinal) < row.IndexOf("\"spr\":", StringComparison.Ordinal));
            Assert.AreEqual(0.0, default(SwimInputs).FinsModifier, "a fresh input has no fins (the S2b numbers)");
            Assert.IsTrue(SwimModel.MotorStep(Vec3.Zero, 0, 0, 1, false, false, new Vec3(0, 0, 1), new Vec3(1, 0, 0), -50, false, out double a, out double c).Z == 1.5 && a == 7.6,
                          "the S2b overload is the fins-0 case");
        }
    }
}
