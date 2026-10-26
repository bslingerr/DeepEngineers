using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S7p-gameplay, the SE side's rules (ROADMAP "### S7p Polish pass" P12, P13, P14): the depth hold against the Water
 /// Mod's buoyancy (checked in a replay of SE's own dampener formula), the yaw-lock predicate and its persistence, the
 /// camera's per-axis window and the export read debt.
 /// </summary>
    [TestClass]
    public class S7pGameplayTests
    {
        // ------------------------------------------------------------------------------------------ P12 depth hold
        [TestMethod]
        public void HoldForce_NegatesTheBuoyancy_FiniteOnly()
        {
            double hx, hy, hz;
            Assert.IsTrue(DampenerHoldRules.HoldForce(0, 1000, 0, 1, out hx, out hy, out hz));
            Assert.AreEqual(0.0, hx); Assert.AreEqual(-1000.0, hy); Assert.AreEqual(0.0, hz);
            Assert.IsTrue(DampenerHoldRules.HoldForce(10, 1000, -5, 0.5, out hx, out hy, out hz)); Assert.AreEqual(-5.0, hx, 1e-12); Assert.AreEqual(-500.0, hy, 1e-12); Assert.AreEqual(2.5, hz, 1e-12);
            Assert.IsFalse(DampenerHoldRules.HoldForce(0, 1000, 0, 0, out hx, out hy, out hz), "factor 0: off");
            Assert.IsFalse(DampenerHoldRules.HoldForce(double.NaN, 1000, 0, 1, out hx, out hy, out hz));
            Assert.IsFalse(DampenerHoldRules.HoldForce(0, 0, 0, 1, out hx, out hy, out hz), "no buoyancy: nothing to hold");
        }

        [TestMethod]
        public void CounterTorque_LeverTimesForce_Negated_FiniteOnly()
        {
            // centre of buoyancy 2 m above the centre of mass, buoyancy straight up: no torque
            double tx, ty, tz;
            Assert.IsFalse(DampenerHoldRules.CounterTorque(0, 2, 0, 0, 0, 0, 0, 1000, 0, 1, out tx, out ty, out tz));
            // centre of buoyancy 3 m ahead (+z), force up: r × F = (3·1000 − 0, 0, 0)... r = (0,0,3), F = (0,1000,0): r×F = (0·0 − 3·1000, 3·0 − 0·0, 0) = (−3000, 0, 0); negated → +3000
            Assert.IsTrue(DampenerHoldRules.CounterTorque(0, 0, 3, 0, 0, 0, 0, 1000, 0, 1, out tx, out ty, out tz));
            Assert.AreEqual(3000.0, tx, 1e-9); Assert.AreEqual(0.0, ty, 1e-9); Assert.AreEqual(0.0, tz, 1e-9);
            // the factor scales; NaN and a lever over MaxLeverM refuse
            Assert.IsTrue(DampenerHoldRules.CounterTorque(0, 0, 3, 0, 0, 0, 0, 1000, 0, 0.5, out tx, out ty, out tz)); Assert.AreEqual(1500.0, tx, 1e-9);
            Assert.IsFalse(DampenerHoldRules.CounterTorque(double.NaN, 0, 3, 0, 0, 0, 0, 1000, 0, 1, out tx, out ty, out tz));
            Assert.IsFalse(DampenerHoldRules.CounterTorque(0, 0, 600, 0, 0, 0, 0, 1000, 0, 1, out tx, out ty, out tz));
            Assert.IsFalse(DampenerHoldRules.CounterTorque(0, 0, 3, 0, 0, 0, 0, 1000, 0, 0, out tx, out ty, out tz), "factor 0: off");
        }

        [TestMethod]
        public void Hold_OffsetIsMinusForceOverTwoMass()
        {
            double ox, oy, oz;
            Assert.IsTrue(DampenerHoldRules.Offset(0, 40000, 0, 10000, 1.0, out ox, out oy, out oz));
            Assert.AreEqual(0, ox, 1e-12); Assert.AreEqual(-2.0, oy, 1e-12); Assert.AreEqual(0, oz, 1e-12);
            Assert.IsTrue(DampenerHoldRules.Offset(3000, 4000, 0, 1000, 0.5, out ox, out oy, out oz));
            Assert.AreEqual(-0.75, ox, 1e-12); Assert.AreEqual(-1.0, oy, 1e-12);
            Assert.IsFalse(DampenerHoldRules.Offset(0, 1, 0, 0, 1, out ox, out oy, out oz), "no mass");
            Assert.IsFalse(DampenerHoldRules.Offset(0, double.NaN, 0, 100, 1, out ox, out oy, out oz), "non-finite force");
            Assert.IsFalse(DampenerHoldRules.Offset(0, 1e9, 0, 100, 1, out ox, out oy, out oz), "|F|/M above the sanity cap");
            Assert.IsFalse(DampenerHoldRules.Offset(0, 1000, 0, 100, 0, out ox, out oy, out oz), "factor 0 = off");
            Assert.AreEqual(0, oy);
        }

        [TestMethod]
        public void Hold_BuoyancyAppliedGate_IsTheWaterModsOwn()
        {
            Assert.IsTrue(DampenerHoldRules.BuoyancyApplied(-30, 25, 1f, true), "deep under water");
            Assert.IsTrue(DampenerHoldRules.BuoyancyApplied(10, 25, 0.3f, true), "partly in the water (depth below the radius)");
            Assert.IsFalse(DampenerHoldRules.BuoyancyApplied(30, 25, 0.3f, true), "out of the water: the field keeps its last value");
            Assert.IsFalse(DampenerHoldRules.BuoyancyApplied(-30, 25, 0f, true), "no block under water");
            Assert.IsFalse(DampenerHoldRules.BuoyancyApplied(-30, 25, 1f, false), "static or not simulated");
            Assert.IsFalse(DampenerHoldRules.BuoyancyApplied(double.NaN, 25, 1f, true));
        }

        /// <summary>
 /// A 1D replay of SE's dampener along the local up
 /// with gravity and a constant buoyancy, 60 Hz: without the offset the ship rises at
 /// a weak ship rises slower.
 /// </summary>
        [TestMethod]
        public void Hold_ReplayOfSeDampener_HoldsDepth()
        {
            const double M = 50000, g = -9.81, dt = 1.0 / 60.0;
            double F = 0.12 * M * 9.81; // 12 % net positive buoyancy
            double vFree, zFree, vHold, zHold, vWeak, zWeak;
            Simulate(M, g, F, 0.0, 1e12, dt, 1200, out vFree, out zFree);
            Simulate(M, g, F, 1.0, 1e12, dt, 1200, out vHold, out zHold);
            Assert.AreEqual(F / (2 * M), vFree, 1e-6, "dampeners on, no offset: the steady drift F/(2M)");
            Assert.IsTrue(zFree > 10, "it rose " + zFree + " m in 20 s");
            Assert.AreEqual(0, vHold, 1e-6, "with the offset the speed goes to 0");
            Assert.AreEqual(0, zHold, 1e-6, "and the depth holds");
            // buoyancy above the weight (an airtight hull): the hold needs downward thrust; a ship with half of what it needs
 // saturates and still rises (the hold is the thrusters' work, never a free force)
            double F2 = 1.12 * M * 9.81, need = F2 - M * 9.81;
            double vStrong, zStrong;
            Simulate(M, g, F2, 1.0, 2 * need, dt, 1200, out vStrong, out zStrong);
            Assert.AreEqual(0, vStrong, 1e-6, "enough downward thrust: it holds");
            Simulate(M, g, F2, 1.0, need / 2, dt, 1200, out vWeak, out zWeak);
            Assert.IsTrue(vWeak > 0 && zWeak > 0, "a weak ship still rises");
        }

        private static void Simulate(double m, double g, double f, double factor, double maxDown, double dt, int ticks, out double v, out double z)
        {
            v = 0; z = 0;
            double ox, oy, oz;
            double target = DampenerHoldRules.Offset(0, f, 0, m, factor, out ox, out oy, out oz) ? oy : 0;
            for (int i = 0; i < ticks; i++)
            {
                double thrust = DampenerHoldRules.DampenerThrust(m, target, v, g);
                if (thrust < -maxDown) thrust = -maxDown; // thrusters pushing down (against the buoyancy) at most maxDown
                double a = g + (f + thrust) / m;
                v += a * dt;
                z += v * dt;
            }
        }

        [TestMethod]
        public void Hold_Switch()
        {
            double f;
            string e;
            Assert.IsTrue(DampenerHoldRules.TryParse(null, out f, out e)); Assert.AreEqual(1.0, f);
            Assert.IsTrue(DampenerHoldRules.TryParse("off", out f, out e)); Assert.AreEqual(0.0, f);
            Assert.IsTrue(DampenerHoldRules.TryParse("0.5", out f, out e)); Assert.AreEqual(0.5, f);
            Assert.IsTrue(DampenerHoldRules.TryParse(true, out f, out e)); Assert.AreEqual(1.0, f);
            Assert.IsFalse(DampenerHoldRules.TryParse("3", out f, out e)); Assert.IsNotNull(e);
            Assert.IsFalse(DampenerHoldRules.TryParse("x", out f, out e));
        }

        // ------------------------------------------------------------------------------------------ P13 yaw lock

        [TestMethod]
        public void YawLock_PredicateIsSesYawCondition()
        {
            const int onGround = 0, jumping = 1, inAir = YawLockRules.StateInAir, climbing = 3, flying = YawLockRules.StateFlying;
            Assert.IsTrue(YawLockRules.YawBlocked(false, false, false, false, false, false, inAir), "in the air, not falling: yaw ignored");
            Assert.IsTrue(YawLockRules.YawBlocked(false, false, false, false, false, false, flying));
            Assert.IsFalse(YawLockRules.YawBlocked(false, false, false, false, true, false, inAir), "falling: yaw works");
            Assert.IsFalse(YawLockRules.YawBlocked(false, false, false, false, false, true, inAir), "jumping: yaw works");
            Assert.IsFalse(YawLockRules.YawBlocked(true, false, false, false, false, false, inAir), "jetpack: its own rotation");
            Assert.IsFalse(YawLockRules.YawBlocked(false, false, false, false, false, false, onGround));
            Assert.IsFalse(YawLockRules.YawBlocked(false, false, false, false, false, false, climbing));
            Assert.IsFalse(YawLockRules.YawBlocked(false, false, false, false, false, false, jumping));
            Assert.IsFalse(YawLockRules.YawBlocked(false, false, true, false, false, false, inAir), "seated");
            Assert.IsFalse(YawLockRules.YawBlocked(false, false, false, true, false, false, inAir), "ladder");
            Assert.IsFalse(YawLockRules.YawBlocked(false, true, false, false, false, false, inAir), "dead");
        }

        [TestMethod]
        public void YawLock_PersistenceThenRetries()
        {
            int t = 0;
            int fired = 0, firstAt = -1;
            for (int i = 1; i <= YawLockRules.PersistTicks + 2 * YawLockRules.RetryTicks; i++)
                if (YawLockRules.Step(true, ref t)) { fired++; if (firstAt < 0) firstAt = i; }
            Assert.AreEqual(YawLockRules.PersistTicks, firstAt, "the fix runs once the state held PersistTicks ticks");
            Assert.AreEqual(3, fired, "then every RetryTicks while it still holds");
            Assert.IsFalse(YawLockRules.Step(false, ref t));
            Assert.AreEqual(0, t, "a tick without the state restarts the count");
            for (int i = 1; i < YawLockRules.PersistTicks; i++) Assert.IsFalse(YawLockRules.Step(true, ref t), "transient states never trigger it");
            bool on;
            string e;
            Assert.IsTrue(YawLockRules.TryParse("off", out on, out e)); Assert.IsFalse(on);
            Assert.IsTrue(YawLockRules.TryParse(null, out on, out e)); Assert.IsTrue(on);
            Assert.IsFalse(YawLockRules.TryParse("maybe", out on, out e));
        }

        [TestMethod]
        public void CameraAxis_AnglesOnAPlanetAndStuckYaw()
        {
            double yaw, pitch;
            Assert.IsTrue(CameraAxisRules.Angles(0, 0, -1, 0, 1, 0, out yaw, out pitch));
            Assert.AreEqual(0, pitch, 1e-9);
            double c = Math.Sqrt(0.5);
            Assert.IsTrue(CameraAxisRules.Angles(0, c, -c, 0, 1, 0, out yaw, out pitch));
            Assert.AreEqual(45, pitch, 1e-9);
            // a tilted local up (a point on the planet): pitch is measured against it
            double ux = 0.6, uy = 0.8, uz = 0;
            Assert.IsTrue(CameraAxisRules.Angles(ux, uy, uz, ux, uy, uz, out yaw, out pitch));
            Assert.AreEqual(90, pitch, 1e-9, "looking straight up the local up");
            Assert.IsFalse(CameraAxisRules.Angles(0, 0, 0, 0, 1, 0, out yaw, out pitch));

            var w = new CameraAxisRules();
            for (int i = 0; i < 60; i++)
            {
                double p = i * 0.5 * Math.PI / 180.0;
                w.Add(0, Math.Sin(p), -Math.Cos(p), 0, 1, 0, 0.3, 0.8, true);
            }
            Assert.IsTrue(w.YawStuck);
            Assert.IsFalse(w.PitchStuck);
            Assert.AreEqual(60, w.BlockedTicks);
            Assert.AreEqual(29.5, w.PitchDeg, 1e-6);
            w.Restart();
            // free turning: both axes move with their input
            for (int i = 0; i < 60; i++)
            {
                double a = i * Math.PI / 180.0;
                w.Add(Math.Sin(a), 0, -Math.Cos(a), 0, 1, 0, 0, 0.5, false);
            }
            Assert.IsFalse(w.YawStuck);
            Assert.AreEqual(59, w.YawDeg, 1e-6);
        }

        // ------------------------------------------------------------------------------------------ P14 export read debt

        [TestMethod]
        public void ReadDebt_SpikeIsPaidBackAtTheAverage()
        {
            var d = new ExportReadDebt { AvgMs = 4.0 };
            Assert.IsTrue(d.Allow());
            d.Spent(40.0); // a workshop-ship cell: 36 ms over the average
            int skipped = 0;
            while (!d.Allow()) skipped++;
            Assert.AreEqual(9, skipped, "36 ms of debt at 4 ms per tick");
            Assert.AreEqual(0, d.DebtMs, 1e-9);
            d.Spent(3.0);
            Assert.IsTrue(d.Allow(), "a tick under the average owes nothing");
            d.Spent(10000);
            Assert.AreEqual(ExportReadDebt.DefaultCapMs, d.DebtMs, 1e-9, "the debt is capped");
            d.AvgMs = 0;
            Assert.IsTrue(d.Allow(), "off: never skips");
            double ms;
            string e;
            Assert.IsTrue(ExportReadDebt.TryParse("off", out ms, out e)); Assert.AreEqual(0, ms);
            Assert.IsTrue(ExportReadDebt.TryParse("6", out ms, out e)); Assert.AreEqual(6, ms);
            Assert.IsTrue(ExportReadDebt.TryParse(null, out ms, out e)); Assert.AreEqual(ExportReadDebt.DefaultAvgMs, ms);
            Assert.IsFalse(ExportReadDebt.TryParse("99", out ms, out e));
        }
    }
}
