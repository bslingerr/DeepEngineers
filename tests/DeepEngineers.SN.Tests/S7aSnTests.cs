using System;
using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S7a, Subnautica side, Unity-free parts of the astronaut and the sun (CONTRACT §5.4 "C1 locomotion extension",
 /// "Time drive"): the out-of-water animator parameters (<see cref="S7aAnimRules.OutOfWater"/>: grounded y 0, the
 /// 0.45 s jump, the diving rule, S7a.M5 red), the Retarget source rule out of the water, the above-water arms gate,
 /// the walk-source switch, the C1 reader's loco decode (<see cref="FrameReader"/>), and the time drive's decisions
 /// (<see cref="DayNightRules"/>: drive/release by C6 freshness and flags, pause and external changes, forward-only
 /// advance, hold, jumps on the first apply and a new jumpSeq, S7a.M6 stepping back, the sky angles and the NPO clamp).
 /// </summary>
    [TestClass]
    public unsafe class S7aSnTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            string e;
            DevMutants.Set(DevMutants.S7aMoveSpeedZero, false, out e);
            DevMutants.Set(DevMutants.S7aTimeDirect, false, out e);
            S7aSnSwitches.WalkRetarget = true;
        }

        private static LocoWireState Loco(byte state, short vF = 0, short vS = 0, short vU = 0, bool grounded = true)
        {
            return new LocoWireState { State = state, VForwardCm = vF, VStrafeCm = vS, VUpCm = vU, Flags = grounded ? S7aConventions.C1LocoFlags.Grounded : (byte)0 };
        }

        [TestMethod]
        public void Anim_Walking_SpeedFromC1_GroundedY0_NotUnderwater()
        {
            var s = Loco(S7aConventions.C1LocoState.Walk, vF: 150, vS: -30, vU: -20);
            AnimParams p = S7aAnimRules.OutOfWater(ref s, 1.0f, 0.0, 1f);
            Assert.IsFalse(p.IsUnderwater);
            Assert.IsFalse(p.OnSurface);
            Assert.AreEqual(1.5f, p.Z, 1e-6f);
            Assert.AreEqual(-0.3f, p.X, 1e-6f);
            Assert.AreEqual(0f, p.Y, "grounded: Subnautica's above-water rule");
            Assert.AreEqual((float)Math.Sqrt(1.5 * 1.5 + 0.3 * 0.3), p.MoveSpeed, 1e-5f);
            Assert.IsTrue(p.MoveSpeed >= 0.5f, "S7a.N4 walking threshold");
            Assert.IsFalse(p.Jump || p.Diving || p.DivingLand);
            var idle = Loco(S7aConventions.C1LocoState.Idle, vF: 3);
            Assert.IsTrue(S7aAnimRules.OutOfWater(ref idle, 1.0f, 0.0, 1f).MoveSpeed < 0.1f, "S7a.N4 standing threshold");
            var air = Loco(S7aConventions.C1LocoState.Fall, vF: 100, vU: -300, grounded: false);
            Assert.AreEqual(-3f, S7aAnimRules.OutOfWater(ref air, 5f, 0.0, 1f).Y, 1e-6f, "airborne: vertical speed");
        }

        [TestMethod]
        public void Anim_Jump_After450ms_DivingTowardsTheSea()
        {
            var j = Loco(S7aConventions.C1LocoState.Jump, vU: 200, grounded: false);
            Assert.IsFalse(S7aAnimRules.OutOfWater(ref j, 2f, 0.44, 1f).Jump);
            Assert.IsTrue(S7aAnimRules.OutOfWater(ref j, 2f, 0.45, 1f).Jump, "Player.GetPlayFallingAnimation's 0.45 s");
            var f = Loco(S7aConventions.C1LocoState.Fall, vU: -250, grounded: false);
            Assert.IsTrue(S7aAnimRules.OutOfWater(ref f, 3f, 1.0, 1f).Diving);
            Assert.IsFalse(S7aAnimRules.OutOfWater(ref f, 0.4f, 1.0, 1f).Diving, "pelvis at the sea");
            var slow = Loco(S7aConventions.C1LocoState.Fall, vU: -50, grounded: false);
            Assert.IsFalse(S7aAnimRules.OutOfWater(ref slow, 3f, 1.0, 1f).Diving, "slower than 1 m/s");
            var clock = new AirClock();
            Assert.AreEqual(0.0, clock.Update(S7aConventions.C1LocoState.Walk, 10.0));
            Assert.AreEqual(0.0, clock.Update(S7aConventions.C1LocoState.Jump, 10.1));
            Assert.AreEqual(0.3, clock.Update(S7aConventions.C1LocoState.Fall, 10.4), 1e-12, "Jump then Fall is one air time");
            Assert.AreEqual(0.0, clock.Update(S7aConventions.C1LocoState.Idle, 10.5));
        }

        [TestMethod]
        public void Anim_MutantM5_ForcesMoveSpeedZero()
        {
            string e;
            Assert.IsTrue(DevMutants.Set(DevMutants.S7aMoveSpeedZero, true, out e), e);
            var s = Loco(S7aConventions.C1LocoState.Run, vF: 400);
            AnimParams p = S7aAnimRules.OutOfWater(ref s, 1f, 0.0, 1f);
            Assert.AreEqual(0f, p.MoveSpeed);
            Assert.AreEqual(0f, p.Z);
            Assert.IsTrue(DevMutants.IsKnown("S7a.M2") && DevMutants.IsKnown("S7a.M5") && DevMutants.IsKnown("S7a.M6"));
        }

        [TestMethod]
        public void Source_RetargetOutOfWater_FootStatesOnly()
        {
            Assert.IsTrue(S7aAnimRules.RetargetOutOfWater(1, S7aConventions.C1LocoState.Walk, false, true, false, false));
            Assert.IsTrue(S7aAnimRules.RetargetOutOfWater(1, S7aConventions.C1LocoState.Idle, false, true, false, false));
            Assert.IsTrue(S7aAnimRules.RetargetOutOfWater(1, S7aConventions.C1LocoState.Fall, false, true, false, false));
            Assert.IsFalse(S7aAnimRules.RetargetOutOfWater(0, S7aConventions.C1LocoState.Walk, false, true, false, false), "an SE build before S7a");
            Assert.IsFalse(S7aAnimRules.RetargetOutOfWater(1, S7aConventions.C1LocoState.Walk, true, true, false, false), "in the water: S5's rule");
            Assert.IsFalse(S7aAnimRules.RetargetOutOfWater(1, S7aConventions.C1LocoState.Walk, false, false, false, false), "walk source sebones");
            Assert.IsFalse(S7aAnimRules.RetargetOutOfWater(1, S7aConventions.C1LocoState.Walk, false, true, true, false), "seated");
            Assert.IsFalse(S7aAnimRules.RetargetOutOfWater(1, S7aConventions.C1LocoState.Walk, false, true, false, true), "dead");
            foreach (byte st in new[] { S7aConventions.C1LocoState.Fly, S7aConventions.C1LocoState.CrouchWalk, S7aConventions.C1LocoState.CrouchIdle,
                                        S7aConventions.C1LocoState.Ladder, S7aConventions.C1LocoState.Seated, S7aConventions.C1LocoState.None, S7aConventions.C1LocoState.Swim })
                Assert.IsFalse(S7aAnimRules.RetargetOutOfWater(1, st, false, true, false, false), "SE-only pose " + st);
        }

        [TestMethod]
        public void Arms_AboveWater_NeedEveryCondition()
        {
            byte walk = S7aConventions.C1LocoState.Walk;
            Assert.IsTrue(S7aAnimRules.ArmsOutOfWater(true, true, true, true, true, walk, true));
            Assert.IsNull(S7aAnimRules.ArmsOutHiddenReason(true, true, true, true, true, walk, true));
            bool[] all = { true, true, true, true, true, true };
            for (int k = 0; k < 6; k++)
            {
                bool[] c = (bool[])all.Clone();
                c[k] = false;
                Assert.IsFalse(S7aAnimRules.ArmsOutOfWater(c[0], c[1], c[2], c[3], c[4], walk, c[5]), "condition " + k);
                Assert.IsNotNull(S7aAnimRules.ArmsOutHiddenReason(c[0], c[1], c[2], c[3], c[4], walk, c[5]), "reason " + k);
            }
            Assert.IsFalse(S7aAnimRules.ArmsOutOfWater(true, true, true, true, true, S7aConventions.C1LocoState.Fly, true), "jetpack: SE's pose");
        }

        [TestMethod]
        public void WalkSource_Parse()
        {
            bool r;
            Assert.IsTrue(S7aAnimRules.TryParseWalkSource("retarget", out r) && r);
            Assert.IsTrue(S7aAnimRules.TryParseWalkSource("SEBONES", out r) && !r);
            Assert.IsFalse(S7aAnimRules.TryParseWalkSource("x", out r));
            Assert.AreEqual("sebones", S7aAnimRules.WalkSourceName(false));
        }

        [TestMethod]
        public void FrameReader_DecodesTheLocoExtension()
        {
            byte* mem = stackalloc byte[256 + 64];
            for (int i = 0; i < 320; i++) mem[i] = 0;
            var f = new SeFrame { tick = 77 };
            f.pelvisSn = new F32x3(0, 1.25f, 0);
            var w = new LocoWireState { State = S7aConventions.C1LocoState.Run, Flags = 1, VForwardCm = 420 };
            S7aLoco.Write(ref f, ref w);
            var sw = new SwimWireState { Fins = 2 };
            SwimWire.Write(ref f, ref sw);
            Seqlock.Write(mem, f);
            var r = new FrameReader(mem, 10000000);
            Assert.AreEqual(C1Poll.Fresh, r.Poll());
            Assert.IsTrue(r.LocoValid);
            Assert.AreEqual((byte)1, r.LocoVersion);
            Assert.AreEqual(S7aConventions.C1LocoState.Run, r.Loco.State);
            Assert.AreEqual((short)420, r.Loco.VForwardCm);
            Assert.AreEqual(1.25f, r.PelvisSnY);
            S7aLoco.Erase(ref f);
            Seqlock.Write(mem, f);
            Assert.AreEqual(C1Poll.Fresh, r.Poll());
            Assert.IsFalse(r.LocoValid, "version 0");
            Assert.AreEqual((byte)0, r.LocoVersion);
        }

        // ---------------------------------------------------------------------------------------------- time drive

        private const double Day = 1200.0;

        private static DayNightInputs In(double hDeg, double timePassed, uint jumpSeq = 1, bool fresh = true, uint flags = 1u | 4u, float speed = 0f,
                                         short az = 0, short el = 0)
        {
            return new DayNightInputs
            {
                C6Fresh = fresh, Flags = flags, JumpSeq = jumpSeq, HourAngleRad = hDeg * Math.PI / 180.0, TimePassed = timePassed,
                Rise = 0.125, Set = 0.875, DayNightSpeed = speed, PoleAzCdeg = az, PoleElCdeg = el,
            };
        }

        [TestMethod]
        public void Time_FirstApply_IsAJumpToTheTarget_ThenForwardAdvances()
        {
            var r = new DayNightRules();
            DayNightInputs i = In(0.0, 5000.0, speed: 1f);           // SE noon; Subnautica somewhere in day 4
            DayNightOutputs o = r.Frame(ref i);
            Assert.IsTrue(o.Drive && o.FirstApply && o.Pause && !o.External);
            Assert.AreEqual(SunStep.Jump, o.Step);
            Assert.IsTrue(o.WriteTime && o.NewTimePassed > i.TimePassed && o.NewTimePassed - i.TimePassed < Day, "forward, less than a day");
            Assert.AreEqual(0.5, SunMath.CycleFromDayScalar(SunMath.Frac(o.NewTimePassed / Day), 0.125, 0.875), 1e-9, "noon reached");
            double tp = o.NewTimePassed;
            i = In(0.3, tp);                                         // SE moved on by 0.3 deg (1 s)
            o = r.Frame(ref i);
            Assert.AreEqual(SunStep.Advance, o.Step);
            Assert.IsFalse(o.Pause);
            Assert.AreEqual(0.5 + 0.3 / 360.0, SunMath.CycleFromDayScalar(SunMath.Frac(o.NewTimePassed / Day), 0.125, 0.875), 1e-9);
            Assert.IsTrue(o.NewTimePassed > tp);
            i = In(0.3, o.NewTimePassed);
            Assert.AreEqual(SunStep.None, r.Frame(ref i).Step, "in sync: nothing written");
        }

        [TestMethod]
        public void Time_WestTeleport_HoldsWithinTwoDegrees_ElseOneForwardJump_NeverBackward()
        {
            var r = new DayNightRules();
            DayNightInputs i = In(10.0, 0.0);
            DayNightOutputs o = r.Frame(ref i);
            double tp = o.NewTimePassed;
            i = In(10.0, tp);
            r.Frame(ref i);
            i = In(8.5, tp);                                        // the camera 1.5 deg west, same jumpSeq: hold
            o = r.Frame(ref i);
            Assert.AreEqual(SunStep.Hold, o.Step);
            Assert.IsFalse(o.WriteTime);
            i = In(10.0 - 2.86, tp, jumpSeq: 2);                     // 3 km west: a new jumpSeq and 2.86 deg ahead: one forward jump
            o = r.Frame(ref i);
            Assert.AreEqual(SunStep.Jump, o.Step);
            Assert.IsTrue(o.NewTimePassed > tp && o.NewTimePassed - tp < Day, "forward, less than a day");
            i = In(10.0 + 2.86, o.NewTimePassed, jumpSeq: 3);        // 3 km east: forward by the hour-angle difference
            double before = i.TimePassed;
            o = r.Frame(ref i);
            Assert.IsTrue(o.Step == SunStep.Jump || o.Step == SunStep.Advance);
            Assert.IsTrue(o.NewTimePassed > before && o.NewTimePassed - before < 30.0, "east: a small advance");
        }

        [TestMethod]
        public void Time_MutantM6_StepsTimeBack()
        {
            string e;
            Assert.IsTrue(DevMutants.Set(DevMutants.S7aTimeDirect, true, out e), e);
            var r = new DayNightRules();
            DayNightInputs i = In(10.0, 1200.0 * 3 + 600.0);
            i.Direct = true;
            DayNightOutputs o = r.Frame(ref i);
            double tp = o.NewTimePassed;
            i = In(10.0 - 2.86, tp, jumpSeq: 2);
            i.Direct = true;
            o = r.Frame(ref i);
            Assert.IsTrue(o.WriteTime && o.NewTimePassed < tp, "S7a.M6: timePassedAsDouble decreases (monotonic row red)");
        }

        [TestMethod]
        public void Time_Release_OnStaleC6_SyncOff_NoPole_AndExternalSpeed()
        {
            var r = new DayNightRules();
            DayNightInputs i = In(0.0, 0.0);
            Assert.IsTrue(r.Frame(ref i).Drive);
            i = In(0.0, 0.0, speed: 1f);
            DayNightOutputs o = r.Frame(ref i);
            Assert.IsTrue(o.Pause && o.External, "the console's day/night set the speed back");
            i = In(0.0, 0.0, flags: 4u);
            o = r.Frame(ref i);
            Assert.IsTrue(o.Release && !o.Drive && o.ReleaseWhy.Contains("sync off"));
            Assert.IsFalse(r.Holding);
            i = In(0.0, 0.0);
            Assert.IsTrue(r.Frame(ref i).FirstApply, "on again");
            i = In(0.0, 0.0, flags: 1u);
            o = r.Frame(ref i);
            Assert.IsTrue(o.Release && o.ReleaseWhy.Contains("pole"));
            i = In(0.0, 0.0, flags: 2u | 4u);
            Assert.IsTrue(r.Frame(ref i).Drive, "skyOnly drives the same mapping");
            i = In(0.0, 0.0, fresh: false);
            o = r.Frame(ref i);
            Assert.IsTrue(o.Release && o.ReleaseWhy.Contains("stale"));
            i = In(0.0, 0.0, fresh: false);
            Assert.IsFalse(r.Frame(ref i).Release, "released once");
        }

        [TestMethod]
        public void Time_SkyAngles_FromThePole_NpoClamped()
        {
            var r = new DayNightRules();
            DayNightInputs i = In(0.0, 0.0, az: 0, el: 0);           // site v1 anchor: the pole is SN north (0, 0, 1)
            DayNightOutputs o = r.Frame(ref i);
            Assert.AreEqual(-90.0, o.SunDirectionDeg, 1e-9);
            Assert.AreEqual(0.0, o.NorthPoleOffsetDeg, 1e-9);
            i = In(0.0, 0.0, az: 0, el: 745);
            o = r.Frame(ref i);
            Assert.AreEqual(7.45, o.NorthPoleOffsetDeg, 1e-6, "rho_inf: |NPO| 7.45 deg (a pole tilted up gives a positive NPO)");
            Assert.IsFalse(o.NpoClamped);
            i = In(0.0, 0.0, az: 0, el: 7000);
            o = r.Frame(ref i);
            Assert.IsTrue(o.NpoClamped);
            Assert.AreEqual(60.0, o.NorthPoleOffsetDeg, 1e-9);
        }

        [TestMethod]
        public void Cadence_ChangeOrPeriod()
        {
            var c = new S7aRowCadence(2000);
            Assert.IsTrue(c.Due("a", 0));
            Assert.IsFalse(c.Due("a", 1999));
            Assert.IsTrue(c.Due("a", 2000));
            Assert.IsTrue(c.Due("b", 2001));
        }
    }
}
