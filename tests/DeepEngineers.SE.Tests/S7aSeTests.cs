using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S7a, SE side, game-independent parts (CONTRACT §5.4 "C1 locomotion extension", "C6 written by SE"): the C1
 /// locomotion composer (<see cref="LocoCompose"/>: state, flags, the velocity on the horizontal forward/right and up,
 /// the yaw rate sign and scale, the reset), the s7a.loco row cadence, and the C6 core (<see cref="SunSyncCore"/>: the
 /// flags per bind/sync state, the jump counter's 1° rule, the sky pole kept from the last valid tick, the dev phase's
 /// sun direction and hour angle).
 /// </summary>
    [TestClass]
    public unsafe class S7aSeTests
    {
        private const ushort Standing = 0, Running = 1040, Walking = 16, Sitting = 1, Falling = 4, Flying = 3, CrouchWalking = 18;

        // SE character frame: forward −Z, right +X, up +Y (VRage)
        private static LocoSource Src(ushort movement, Vec3 v, double yawDeg = 0.0)
        {
            double y = yawDeg * Math.PI / 180.0;
            // turning right (clockwise seen from above, +Y up) takes forward −Z towards +X
            var fwd = new Vec3(Math.Sin(y), 0, -Math.Cos(y));
            var right = new Vec3(Math.Cos(y), 0, Math.Sin(y));
            return new LocoSource { Present = true, Movement = movement, Grounded = true, Velocity = v, Forward = fwd, Right = right, Up = Vec3.UnitY };
        }

        [TestMethod]
        public void Loco_NoCharacter_Version1StateNone_AllZero()
        {
            var c = new LocoCompose();
            var src = new LocoSource();
            LocoWireState w = c.Compose(ref src, 1.0 / 60);
            Assert.AreEqual(S7aConventions.C1LocoState.None, w.State);
            Assert.AreEqual(0, w.Flags + w.VForwardCm + w.VStrafeCm + w.VUpCm + w.YawRateCdeg + w.SeMovement);
            var f = new SeFrame();
            S7aLoco.Write(ref f, ref w);
            LocoWireState r;
            Assert.IsTrue(S7aLoco.TryRead(ref f, out r), "version 1 written");
        }

        [TestMethod]
        public void Loco_Velocity_OnHorizontalForwardRightAndUp()
        {
            var c = new LocoCompose();
            // walking forward (−Z) at 1.5 m/s, drifting right 0.25 m/s, falling 0.5 m/s
            var src = Src(Walking, new Vec3(0.25, -0.5, -1.5));
            LocoWireState w = c.Compose(ref src, 1.0 / 60);
            Assert.AreEqual(S7aConventions.C1LocoState.Walk, w.State);
            Assert.AreEqual((short)150, w.VForwardCm);
            Assert.AreEqual((short)25, w.VStrafeCm);
            Assert.AreEqual((short)-50, w.VUpCm);
            Assert.AreEqual(S7aConventions.C1LocoFlags.Grounded, w.Flags);
            Assert.AreEqual((ushort)Walking, w.SeMovement);
            // a pitched forward (looking down 30°) still gives the horizontal forward
            var pitched = src;
            pitched.Forward = new Vec3(0, -0.5, -Math.Sqrt(0.75));
            w = new LocoCompose().Compose(ref pitched, 1.0 / 60);
            Assert.AreEqual((short)150, w.VForwardCm);
        }

        [TestMethod]
        public void Loco_YawRate_PositiveTurningRight_CdegPerSimSecond()
        {
            var c = new LocoCompose();
            var a = Src(Standing, Vec3.Zero, 0.0);
            c.Compose(ref a, 1.0 / 60);
            var b = Src(Standing, Vec3.Zero, 1.5);      // 1.5° right in one 1/60 s tick = 90°/s
            LocoWireState w = c.Compose(ref b, 1.0 / 60);
            Assert.AreEqual(9000, w.YawRateCdeg, 2, "90 deg/s right");
            var d = Src(Standing, Vec3.Zero, 0.5);      // back left by 1° = −60°/s
            w = c.Compose(ref d, 1.0 / 60);
            Assert.AreEqual(-6000, w.YawRateCdeg, 2, "60 deg/s left");
            w = c.Compose(ref d, 0.0);
            Assert.AreEqual(0, w.YawRateCdeg, "paused: no rate");
            c.Reset();
            var e = Src(Standing, Vec3.Zero, 45.0);
            w = c.Compose(ref e, 1.0 / 60);
            Assert.AreEqual(0, w.YawRateCdeg, "after a reset the first tick has no rate");
        }

        [TestMethod]
        public void Loco_StatesAndFlags()
        {
            var c = new LocoCompose();
            var s = Src(Running, new Vec3(0, 0, -5));
            Assert.AreEqual(S7aConventions.C1LocoState.Run, c.Compose(ref s, 1.0 / 60).State);
            s.SwimLatch = true;
            LocoWireState w = c.Compose(ref s, 1.0 / 60);
            Assert.AreEqual(S7aConventions.C1LocoState.Swim, w.State);
            Assert.IsTrue(w.Has(S7aConventions.C1LocoFlags.InWater));
            s = Src(Sitting, Vec3.Zero);
            w = c.Compose(ref s, 1.0 / 60);
            Assert.AreEqual(S7aConventions.C1LocoState.Seated, w.State);
            Assert.IsTrue(w.Has(S7aConventions.C1LocoFlags.Seated));
            s = Src(Flying, Vec3.Zero);
            s.Jetpack = true;
            s.Grounded = false;
            w = c.Compose(ref s, 1.0 / 60);
            Assert.AreEqual(S7aConventions.C1LocoState.Fly, w.State);
            Assert.AreEqual(S7aConventions.C1LocoFlags.Jetpack, w.Flags);
            s = Src(CrouchWalking, Vec3.Zero);
            s.Crouching = true;
            w = c.Compose(ref s, 1.0 / 60);
            Assert.AreEqual(S7aConventions.C1LocoState.CrouchWalk, w.State);
            Assert.IsTrue(w.Has(S7aConventions.C1LocoFlags.Crouching));
            s = Src(Falling, new Vec3(0, -3, 0));
            s.Grounded = false;
            w = c.Compose(ref s, 1.0 / 60);
            Assert.AreEqual(S7aConventions.C1LocoState.Fall, w.State);
            Assert.AreEqual((short)-300, w.VUpCm);
            s = Src(6, Vec3.Zero);
            s.Dead = true;
            w = c.Compose(ref s, 1.0 / 60);
            Assert.AreEqual(S7aConventions.C1LocoState.Dead, w.State);
            Assert.IsTrue(w.Has(S7aConventions.C1LocoFlags.Dead));
            s = Src(16384, Vec3.Zero);
            w = c.Compose(ref s, 1.0 / 60);
            Assert.AreEqual(S7aConventions.C1LocoState.Ladder, w.State);
            Assert.IsTrue(w.Has(S7aConventions.C1LocoFlags.Ladder));
        }

        [TestMethod]
        public void Loco_RowCadence_EveryChangeAndTwoHertz()
        {
            var r = new LocoRowCadence();
            var w = new LocoWireState { State = 1 };
            Assert.IsTrue(r.Due(ref w, 1000));
            Assert.IsFalse(r.Due(ref w, 1100));
            Assert.IsTrue(r.Due(ref w, 1500), "2 Hz");
            w.State = 2;
            Assert.IsTrue(r.Due(ref w, 1510), "state change");
            w.Flags = 1;
            Assert.IsTrue(r.Due(ref w, 1520), "flag change");
            Assert.IsFalse(r.Due(ref w, 1530));
        }

        // ---------------------------------------------------------------------------------------------------- C6 core

        private static SunSky Sky(double hDeg, bool poleValid = true, short az = 0, short el = 0)
        {
            return new SunSky
            {
                HourAngleRad = hDeg * Math.PI / 180.0, ElevationRad = 0.5, SunriseHourAngleRad = Math.PI / 2, PoleValid = poleValid,
                PoleAzCdeg = az, PoleElCdeg = el, SunDirSn = new Vec3(0, 1, 0),
            };
        }

        private static SunTickInputs In(ulong tick, double dt, bool checks = true, bool sync = true, bool readable = true)
        {
            return new SunTickInputs { Tick = tick, SimDt = dt, DayLengthSec = 1200.0, SimSpeed = 1f, ChecksOk = checks, RotationOn = true, SyncOn = sync, Readable = readable };
        }

        [TestMethod]
        public void C6_Flags_PerChecksAndSync_PoleBit()
        {
            var core = new SunSyncCore();
            core.OnBind();
            var t = new SeTime();
            var sky = Sky(10, true, -9000, 12);
            var i = In(100, 1.0 / 60);
            core.Compose(ref sky, ref i, ref t);
            Assert.AreEqual(SeTimeFlags.TimeSyncValid | S7aConventions.C6FlagsExt.SkyPoleValid, t.flags);
            short az, el;
            S7aSky.ReadPole(ref t, out az, out el);
            Assert.AreEqual((short)-9000, az);
            Assert.AreEqual((short)12, el);
            Assert.AreEqual(100UL, t.tick);
            Assert.AreEqual(1200f, t.dayLengthSec);
            i = In(101, 1.0 / 60, checks: false);
            core.Compose(ref sky, ref i, ref t);
            Assert.AreEqual(SeTimeFlags.SkyOnly | S7aConventions.C6FlagsExt.SkyPoleValid, t.flags, "a bind check failed: sky only");
            i = In(102, 1.0 / 60, sync: false);
            core.Compose(ref sky, ref i, ref t);
            Assert.AreEqual(S7aConventions.C6FlagsExt.SkyPoleValid, t.flags, "sync off clears bits 0 and 1");
            var noPole = Sky(10.1, false, 1, 1);
            i = In(103, 1.0 / 60);
            core.Compose(ref noPole, ref i, ref t);
            Assert.AreEqual(SeTimeFlags.TimeSyncValid, t.flags, "no pole this tick: bit 2 clear");
            S7aSky.ReadPole(ref t, out az, out el);
            Assert.AreEqual((short)-9000, az, "the last pole stays");
            i = In(104, 1.0 / 60, readable: false);
            core.Compose(ref sky, ref i, ref t);
            Assert.AreEqual(0u, t.flags, "unreadable: flags 0");
        }

        [TestMethod]
        public void C6_JumpSeq_BindAndMoreThanOneDegreeBeyondTheExpectedStep()
        {
            var core = new SunSyncCore();
            var t = new SeTime();
            core.OnBind();
            uint j0 = core.JumpSeq;
            Assert.AreEqual(1u, j0, "a bind counts as a jump");
            double dt = 1.0 / 60, step = 360.0 * dt / 1200.0;   // 0.005° per tick
            double h = 20.0;
            var i = In(1, dt);
            var sky = Sky(h);
            Assert.IsFalse(core.Compose(ref sky, ref i, ref t), "first tick after the bind: no extra jump");
            for (int k = 0; k < 600; k++)
            {
                h += step;
                sky = Sky(h);
                i = In((ulong)(2 + k), dt);
                Assert.IsFalse(core.Compose(ref sky, ref i, ref t), "the natural rotation");
            }
            Assert.AreEqual(j0, t.jumpSeq);
            h += step + 0.9;                                    // a 0.9° teleport: below the 1° rule
            sky = Sky(h);
            Assert.IsFalse(core.Compose(ref sky, ref i, ref t));
            h += step + 2.86;                                   // 3 km east on Rs 60 km
            sky = Sky(h);
            Assert.IsTrue(core.Compose(ref sky, ref i, ref t));
            Assert.AreEqual(j0 + 1, t.jumpSeq);
            Assert.AreEqual(2.86, core.LastJumpDeg, 1e-6);
            h -= 2.86;                                          // and back west (paused tick)
            sky = Sky(h);
            i = In(900, 0.0);
            Assert.IsTrue(core.Compose(ref sky, ref i, ref t));
            Assert.AreEqual(j0 + 2, t.jumpSeq);
            core.ForceJump();
            Assert.AreEqual(j0 + 3, core.JumpSeq, "the dev phase counts at once");
            h = 179.999;                                        // wrap across ±180° is not a jump
            sky = Sky(h);
            core.Compose(ref sky, ref i, ref t);
            sky = Sky(-179.999 + step);
            i = In(901, dt);
            Assert.IsFalse(core.Compose(ref sky, ref i, ref t), "H wraps at ±180°");
        }

        [TestMethod]
        public void C6_DevPhase_SunForHourAngle_GivesTheRequestedCycleTime_KeepsDeclination()
        {
            SiteMap map = SiteMap.SiteV1Test;
            Vec3 cam = map.SnToSe(new Vec3(1200, 40, -3000));
            Vec3 axis = Vec3.UnitY;
            foreach (double c in new[] { 0.0, 0.1, 0.25, 0.5, 0.74, 0.99 })
            {
                Vec3 s = SunSyncCore.SunForHourAngle(map, cam, new Vec3(0, 0, 1), axis, SunSyncCore.HourAngleOfCycle(c));
                SunSky k;
                SunMath.SeSky(map, cam, s, axis, out k);
                Assert.AreEqual(c, SunMath.CycleTime(k.HourAngleRad), 1e-12, "c=" + c);
                Assert.AreEqual(0.0, k.DeclinationRad, 1e-12);
            }
            Vec3 tilted = new Vec3(0.3, 0.2, 0.9).Normalized();
            Vec3 s2 = SunSyncCore.SunForHourAngle(map, cam, tilted, axis, 0.3);
            Assert.AreEqual(Vec3.Dot(tilted, axis), Vec3.Dot(s2, axis), 1e-12, "declination kept");
            Assert.AreEqual(1.0, s2.Length, 1e-12);
        }
    }
}
