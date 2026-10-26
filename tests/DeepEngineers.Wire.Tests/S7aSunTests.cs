using System;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// ROADMAP S7a: the C1 locomotion codec (<see cref="S7aLoco"/>), the C6 sky-pole codec (<see cref="S7aSky"/>) and the
 /// rules of <see cref="SunMath"/> that need no vectors (CONTRACT §5.4 "C1 locomotion extension", "S7a conventions"):
 /// byte placement inside the layout-1.0 pads, the movement-state mapping of every <c>MyCharacterMovementEnum</c>
 /// value, rounding and clamping, the step rule's hold band, the IsDay equivalence and the time-drive arithmetic.
 /// </summary>
    [TestClass]
    public unsafe class S7aSunTests
    {
        // MyCharacterMovementEnum
        private const ushort Standing = 0, Sitting = 1, Crouching = 2, Flying = 3, Falling = 4, Jump = 5, Died = 6, Ladder = 7,
            LadderOut = 16384, RotatingLeft = 4096, RotatingRight = 8192, Walking = 16, BackWalking = 32, WalkStrafingLeft = 64,
            WalkingRightFront = 144, Running = 1040, Backrunning = 1056, RunStrafingRight = 1152, CrouchWalking = 18,
            CrouchStrafingLeft = 66, CrouchRotatingLeft = 4098, Sprinting = 2064, LadderUp = 263, LadderDown = 519;

        [TestMethod]
        public void Loco_WriteRead_RoundTrip_LittleEndian_PadsZero_SwimBytesUntouched()
        {
            var f = new SeFrame();
            byte* b = (byte*)&f;
            for (int i = 0x94; i < 0x100; i++) b[i] = 0xEE;
            var s = new LocoWireState { State = 3, Flags = 0x49, VForwardCm = -1234, VStrafeCm = 32767, VUpCm = -32767, YawRateCdeg = 4500, SeMovement = 1040 };
            S7aLoco.Write(ref f, ref s);
            Assert.AreEqual(1, b[0xA0]);
            Assert.AreEqual(3, b[0xA1]);
            Assert.AreEqual(0x49, b[0xA2]);
            Assert.AreEqual(0, b[0xA3]);
            Assert.AreEqual(unchecked((byte)(-1234 & 0xFF)), b[0xA4]);
            Assert.AreEqual(unchecked((byte)((-1234 >> 8) & 0xFF)), b[0xA5]);
            Assert.AreEqual(1040 & 0xFF, b[0xAC]);
            Assert.AreEqual(1040 >> 8, b[0xAD]);
            Assert.AreEqual(0, b[0xAE]);
            Assert.AreEqual(0, b[0xAF]);
            for (int i = 0x94; i < 0xA0; i++) Assert.AreEqual(0xEE, b[i], "swim extension byte 0x" + i.ToString("x"));
            for (int i = 0xB0; i < 0x100; i++) Assert.AreEqual(0xEE, b[i], "pad byte 0x" + i.ToString("x"));
            LocoWireState r;
            Assert.IsTrue(S7aLoco.TryRead(ref f, out r));
            Assert.AreEqual(s.State, r.State);
            Assert.AreEqual(s.Flags, r.Flags);
            Assert.AreEqual(s.VForwardCm, r.VForwardCm);
            Assert.AreEqual(s.VStrafeCm, r.VStrafeCm);
            Assert.AreEqual(s.VUpCm, r.VUpCm);
            Assert.AreEqual(s.YawRateCdeg, r.YawRateCdeg);
            Assert.AreEqual(s.SeMovement, r.SeMovement);
            Assert.AreEqual((byte)1, S7aLoco.ReadVersion(ref f));
        }

        [TestMethod]
        public void Loco_Clear_IsVersion1StateNone_Erase_IsVersion0_TryReadRefusesOtherVersions()
        {
            var f = new SeFrame();
            byte* b = (byte*)&f;
            for (int i = 0xA0; i < 0xB0; i++) b[i] = 0x55;
            S7aLoco.Clear(ref f);
            Assert.AreEqual(1, b[0xA0]);
            for (int i = 0xA1; i < 0xB0; i++) Assert.AreEqual(0, b[i]);
            LocoWireState r;
            Assert.IsTrue(S7aLoco.TryRead(ref f, out r));
            Assert.AreEqual(S7aConventions.C1LocoState.None, r.State);
            S7aLoco.Erase(ref f);
            Assert.IsFalse(S7aLoco.TryRead(ref f, out r), "version 0 = an SE build before S7a");
            b[0xA0] = 2;
            Assert.IsFalse(S7aLoco.TryRead(ref f, out r), "unknown version");
            Assert.AreEqual((byte)2, S7aLoco.ReadVersion(ref f));
        }

        [TestMethod]
        public void Loco_Offsets_AreInsideLayoutPad94_AfterTheSwimExtension()
        {
            Assert.AreEqual(SwimWire.Offset + SwimWire.Size, S7aLoco.Offset, "right after the swim extension (0x94-0x9F)");
            Assert.AreEqual(0x94 + 0x6C, 0x100);
            Assert.IsTrue(S7aLoco.Offset + S7aLoco.Size <= SwimWire.PadOffset + SwimWire.PadSize, "inside P(0x94, 0x6C)");
            Assert.AreEqual(S7aConventions.C1Loco.Offset, S7aLoco.Offset);
            Assert.AreEqual(S7aConventions.C1Loco.Size, S7aLoco.Size);
            Assert.AreEqual(S7aConventions.C1Loco.Version, S7aLoco.Version);
            Assert.AreEqual(0xA0, S7aLoco.OffVersion);
            Assert.AreEqual(0xAE, S7aLoco.OffPad2);
            Assert.AreEqual(0x88, (int)Marshal.OffsetOf(typeof(SeFrame), "pelvisSn"));
        }

        [TestMethod]
        public void Loco_StateFromSe_EveryMovementValue()
        {
            Func<ushort, byte> m = v => S7aLoco.StateFromSe(v, true, false);
            Assert.AreEqual(S7aConventions.C1LocoState.None, S7aLoco.StateFromSe(Running, false, false), "no character");
            Assert.AreEqual(S7aConventions.C1LocoState.Swim, S7aLoco.StateFromSe(Running, true, true), "the swim latch wins");
            Assert.AreEqual(S7aConventions.C1LocoState.Swim, S7aLoco.StateFromSe(Died, true, true), "swim latch before Died (first match)");
            Assert.AreEqual(S7aConventions.C1LocoState.Dead, m(Died));
            Assert.AreEqual(S7aConventions.C1LocoState.Seated, m(Sitting));
            Assert.AreEqual(S7aConventions.C1LocoState.Ladder, m(Ladder));
            Assert.AreEqual(S7aConventions.C1LocoState.Ladder, m(LadderUp));
            Assert.AreEqual(S7aConventions.C1LocoState.Ladder, m(LadderDown));
            Assert.AreEqual(S7aConventions.C1LocoState.Ladder, m(LadderOut), "LadderOut has mode bits 0");
            Assert.AreEqual(S7aConventions.C1LocoState.Fly, m(Flying));
            Assert.AreEqual(S7aConventions.C1LocoState.Fall, m(Falling));
            Assert.AreEqual(S7aConventions.C1LocoState.Jump, m(Jump));
            Assert.AreEqual(S7aConventions.C1LocoState.CrouchIdle, m(Crouching));
            Assert.AreEqual(S7aConventions.C1LocoState.CrouchIdle, m(CrouchRotatingLeft), "rotating in place has no direction bits");
            Assert.AreEqual(S7aConventions.C1LocoState.CrouchWalk, m(CrouchWalking));
            Assert.AreEqual(S7aConventions.C1LocoState.CrouchWalk, m(CrouchStrafingLeft));
            Assert.AreEqual(S7aConventions.C1LocoState.Idle, m(Standing));
            Assert.AreEqual(S7aConventions.C1LocoState.Idle, m(RotatingLeft));
            Assert.AreEqual(S7aConventions.C1LocoState.Idle, m(RotatingRight));
            Assert.AreEqual(S7aConventions.C1LocoState.Walk, m(Walking));
            Assert.AreEqual(S7aConventions.C1LocoState.Walk, m(BackWalking));
            Assert.AreEqual(S7aConventions.C1LocoState.Walk, m(WalkStrafingLeft));
            Assert.AreEqual(S7aConventions.C1LocoState.Walk, m(WalkingRightFront));
            Assert.AreEqual(S7aConventions.C1LocoState.Run, m(Running));
            Assert.AreEqual(S7aConventions.C1LocoState.Run, m(Backrunning));
            Assert.AreEqual(S7aConventions.C1LocoState.Run, m(RunStrafingRight));
            Assert.AreEqual(S7aConventions.C1LocoState.Sprint, m(Sprinting));
            Assert.AreEqual(S7aConventions.C1LocoState.None, m(8), "an unknown mode");
        }

        [TestMethod]
        public void Loco_FootStates_AreIdleToSprintJumpFall()
        {
            for (byte s = 0; s < 20; s++)
            {
                bool foot = s == S7aConventions.C1LocoState.Idle || s == S7aConventions.C1LocoState.Walk || s == S7aConventions.C1LocoState.Run ||
                            s == S7aConventions.C1LocoState.Sprint || s == S7aConventions.C1LocoState.Jump || s == S7aConventions.C1LocoState.Fall;
                Assert.AreEqual(foot, S7aConventions.C1LocoState.IsFoot(s), "state " + s);
            }
        }

        [TestMethod]
        public void Loco_CmAndCdeg_RoundHalfAwayFromZero_Clamp_NaN()
        {
            Assert.AreEqual((short)123, S7aLoco.Cm(1.234));
            Assert.AreEqual((short)125, S7aLoco.Cm(1.245000001));
            Assert.AreEqual((short)-50, S7aLoco.Cm(-0.4951));
            Assert.AreEqual((short)32767, S7aLoco.Cm(1000.0));
            Assert.AreEqual((short)-32767, S7aLoco.Cm(-1000.0));
            Assert.AreEqual((short)0, S7aLoco.Cm(double.NaN));
            Assert.AreEqual((short)9000, S7aLoco.Cdeg(90.0));
            Assert.AreEqual((short)-32767, S7aLoco.Cdeg(-720.0));
            Assert.AreEqual((short)1, S7aLoco.Cdeg(0.005));
            Assert.AreEqual((short)-1, S7aLoco.Cdeg(-0.005));
        }

        [TestMethod]
        public void Sky_PoleBytes_OnPad3c_RoundTrip_OtherFieldsUntouched()
        {
            Assert.AreEqual(0x3C, (int)Marshal.OffsetOf(typeof(SeTime), "pad_3c"));
            Assert.AreEqual(64, Marshal.SizeOf(typeof(SeTime)));
            Assert.AreEqual(S7aConventions.C6Sky.Offset, S7aSky.Offset);
            Assert.AreEqual(S7aConventions.C6Sky.Size, S7aSky.Size);
            var t = new SeTime { sunriseHourAngleRad = 1.5f, flags = 7 };
            S7aSky.WritePole(ref t, -9000, 745);
            short az, el;
            S7aSky.ReadPole(ref t, out az, out el);
            Assert.AreEqual((short)-9000, az);
            Assert.AreEqual((short)745, el);
            Assert.AreEqual(1.5f, t.sunriseHourAngleRad);
            Assert.AreEqual(7u, t.flags);
            byte* b = (byte*)&t;
            Assert.AreEqual(unchecked((byte)(-9000 & 0xFF)), b[0x3C]);
            Assert.AreEqual(745 & 0xFF, b[0x3E]);
            S7aSky.ClearPole(ref t);
            S7aSky.ReadPole(ref t, out az, out el);
            Assert.AreEqual(0, az + el);
        }

        [TestMethod]
        public void Sun_PoleCdegRoundTrip_Within001Degree()
        {
            var rnd = new Random(11);
            for (int i = 0; i < 2000; i++)
            {
                var q = new Vec3(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1);
                if (q.Length < 0.1) continue;
                double az, el;
                SunMath.PoleAzEl(q, out az, out el);
                Vec3 back = SunMath.PoleFromCdeg(SunMath.PackCdeg(az), SunMath.PackCdeg(el));
                Assert.IsTrue(SunMath.AngleDeg(back, q.Normalized()) <= 0.0072, "pole quantisation of " + q);
            }
        }

        [TestMethod]
        public void Sun_Step_HoldBand_IsTwoDegreesOfCycleAngle()
        {
            Assert.AreEqual(2.0, SunMath.HoldMaxDeg, 1e-12, "SunHoldMaxCdeg 200");
            double rise = SunMath.SnSunRiseDefault, set = SunMath.SnSunSetDefault, adv;
            double cT = 0.5, sT = SunMath.DayScalarFromCycle(cT, rise, set);
            foreach (double aheadDeg in new[] { 0.5, 1.0, 1.99 })
            {
                double c = cT + aheadDeg / 360.0;
                Assert.AreEqual(SunStep.Hold, SunMath.Step(SunMath.DayScalarFromCycle(c, rise, set), c, sT, cT, false, out adv), "ahead " + aheadDeg);
                Assert.AreEqual(0.0, adv);
                Assert.AreEqual(SunStep.Hold, SunMath.Step(SunMath.DayScalarFromCycle(c, rise, set), c, sT, cT, true, out adv), "a forced jump still holds within 2 deg");
            }
            double c3 = cT + 3.0 / 360.0;
            Assert.AreEqual(SunStep.Jump, SunMath.Step(SunMath.DayScalarFromCycle(c3, rise, set), c3, sT, cT, false, out adv), "ahead beyond 2 deg: a jump of nearly a day");
            Assert.IsTrue(adv > 0.98 && adv < 1.0, "nearly a day (3 deg of cycle = 0.0125 day at noon)");
            double cb = cT - 3.0 / 360.0;
            Assert.AreEqual(SunStep.Advance, SunMath.Step(SunMath.DayScalarFromCycle(cb, rise, set), cb, sT, cT, false, out adv), "behind by 3 deg");
            Assert.AreEqual(SunStep.Jump, SunMath.Step(SunMath.DayScalarFromCycle(cb, rise, set), cb, sT, cT, true, out adv), "forced: logged as a jump");
            Assert.AreEqual(SunStep.None, SunMath.Step(sT, cT, sT, cT, true, out adv), "in sync");
            double cBig = cT - 120.0 / 360.0;
            Assert.AreEqual(SunStep.Jump, SunMath.Step(SunMath.DayScalarFromCycle(cBig, rise, set), cBig, sT, cT, false, out adv), "more than a quarter day");
        }

        [TestMethod]
        public void Sun_IsDay_IsCycleBetweenSunriseAndSunset_ForEveryRemap()
        {
            foreach (var rs in new[] { new[] { 0.125, 0.875 }, new[] { 0.2, 0.7 }, new[] { 0.05, 0.95 } })
                for (int i = 1; i < 1000; i++)
                {
                    double c = i / 1000.0;
                    if (Math.Abs(c - 0.25) < 1e-9 || Math.Abs(c - 0.75) < 1e-9) continue;
                    double s = SunMath.DayScalarFromCycle(c, rs[0], rs[1]);
                    Assert.AreEqual(c > 0.25 && c < 0.75, s > rs[0] && s < rs[1], "c=" + c);
                    Assert.AreEqual(c, SunMath.CycleFromDayScalar(s, rs[0], rs[1]), 1e-12, "round trip c=" + c);
                }
        }

        [TestMethod]
        public void Sun_CycleTime_NoonHalf_SunriseQuarter_SunsetThreeQuarters()
        {
            Assert.AreEqual(0.5, SunMath.CycleTime(0.0), 1e-15);
            Assert.AreEqual(0.25, SunMath.CycleTime(-Math.PI / 2), 1e-15);
            Assert.AreEqual(0.75, SunMath.CycleTime(Math.PI / 2), 1e-15);
            Assert.AreEqual(0.0, SunMath.CycleTime(Math.PI), 1e-15);
            Assert.AreEqual(Math.PI / 2, SunMath.SunriseHourAngle(0.0, 0.3), 0.0);
            Assert.AreEqual(0.0, SunMath.WrapRad(2 * Math.PI), 1e-15);
            Assert.AreEqual(Math.PI, SunMath.WrapRad(-Math.PI), 1e-15);
        }
    }
}
