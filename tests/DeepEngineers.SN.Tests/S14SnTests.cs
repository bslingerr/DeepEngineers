using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S14, Subnautica side, the Unity-free part (src/DeepEngineers.SN/S14/S14SnRules.cs): the engine loop's envelope
 /// (attack, release, no restart while releasing, S14.M5's instant S7 behaviour), its volume and the listener math.
 /// </summary>
    [TestClass]
    public class S14SnTests
    {
        private const double Dt = 1000.0 / 60.0;

        private static void Run(S14EngineEnv e, bool want, double rpm, int frames, ref double t, bool noEnv = false)
        {
            for (int i = 0; i < frames; i++) { t += Dt / 1000.0; e.Step(want, rpm, Dt, t, noEnv); }
        }

        [TestMethod]
        public void Envelope_AttackAndReleaseTimes()
        {
            var e = new S14EngineEnv();
            double t = 10.0;
            Assert.AreEqual(S14EnvEvent.Start, e.Step(true, 0.8, Dt, t, false));
            Assert.IsTrue(e.Playing);
            Run(e, true, 0.8, 30, ref t);
            Assert.AreEqual(1.0, e.Level, 1e-12);
            Assert.AreEqual(1, e.Attacks);
            Assert.AreEqual(200.0, e.LastAttackMs, Dt + 1e-6, "the attack takes 200 ms");
            Assert.AreEqual(0.8, e.RpmS, 1e-12, "the rpm reached its target");
            t += Dt / 1000.0;
            Assert.AreEqual(S14EnvEvent.Release, e.Step(false, 0, Dt, t, false));
            Assert.IsTrue(e.Playing, "the release keeps the loop");
            Assert.AreEqual(0.8, e.RpmS, 1e-12, "the tail keeps its pitch");
            Run(e, false, 0, 30, ref t);
            Assert.IsFalse(e.Playing);
            Assert.AreEqual(1, e.Stops);
            Assert.AreEqual(300.0, e.LastReleaseMs, Dt + 1e-6, "the release takes 300 ms");
        }

        [TestMethod]
        public void Envelope_ACorrectionDuringTheReleaseResumesWithoutRestart()
        {
            var e = new S14EngineEnv();
            double t = 0;
            e.Step(true, 0.3, Dt, t, false);
            Run(e, true, 0.3, 20, ref t);
            t += Dt / 1000.0;
            e.Step(false, 0, Dt, t, false);
            Run(e, false, 0, 5, ref t);
            double level = e.Level;
            Assert.IsTrue(level > 0 && level < 1);
            t += Dt / 1000.0;
            Assert.AreEqual(S14EnvEvent.Resume, e.Step(true, 0.3, Dt, t, false));
            Assert.AreEqual(1, e.Starts, "no second start");
            Assert.AreEqual(1, e.Resumes);
            Assert.AreEqual(0, e.Stops, "never stopped");
        }

        [TestMethod]
        public void Envelope_MutantIsInstant()
        {
            var e = new S14EngineEnv();
            double t = 0;
            Assert.AreEqual(S14EnvEvent.Start, e.Step(true, 0.2, Dt, t, true));
            Assert.AreEqual(1.0, e.Level);
            Assert.AreEqual(0.0, e.LastAttackMs, 1e-12, "S14.M5: no attack");
            Assert.AreEqual(1.0, e.Volume(true), "S7's constant level");
            t += Dt / 1000.0;
            Assert.AreEqual(S14EnvEvent.Stopped, e.Step(false, 0, Dt, t, true));
            Assert.AreEqual(0.0, e.LastReleaseMs, 1e-12, "S14.M5: no release");
            Assert.AreEqual(0.2, e.RpmParam(true, 0.2));
        }

        [TestMethod]
        public void Envelope_VolumeFollowsTheGainCurve()
        {
            var e = new S14EngineEnv();
            double t = 0;
            e.Step(true, 0.2, Dt, t, false);
            Run(e, true, 0.2, 30, ref t);
            Assert.AreEqual(S14Rules.EngineGain(0.2), e.Volume(false), 1e-12, "a small correction plays quieter");
            var f = new S14EngineEnv();
            f.Step(true, 1.0, Dt, t, false);
            Run(f, true, 1.0, 30, ref t);
            Assert.IsTrue(f.Volume(false) > 2.5 * e.Volume(false), "full thrust much louder than a 20 % correction");
        }

        [TestMethod]
        public void ListenerMath()
        {
            Assert.AreEqual(0.0, S14ListenerMath.AngleDeg(0, 0, 1, 0, 0, 2), 1e-9);
            Assert.AreEqual(90.0, S14ListenerMath.AngleDeg(1, 0, 0, 0, 0, 1), 1e-9);
            Assert.AreEqual(180.0, S14ListenerMath.AngleDeg(0, 0, 0, 0, 0, 1), 1e-9, "a zero vector is the worst case");
            Assert.AreEqual(5.0, S14ListenerMath.Distance(0, 0, 0, 3, 4, 0), 1e-12);
            CollectionAssert.AreEquivalent(new[] { "S14.M4", "S14.M5", "S14.M6" }, S14SnMutants.All);
        }
    }
}
