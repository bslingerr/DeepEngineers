using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S16, SE side, the game-free part (src/DeepEngineers.SE/S16/S16SeLogic.cs): the seeded failures and their effect on the
 /// damage threshold, curve and creak band; the C5 extension holder that Link.WriteC5 fills (S16 hook), cleared when
 /// inactive; the S16 mutant ids routed by BuildFlavor's registry (S16 hook).
 /// </summary>
    [TestClass]
    public class S16Tests
    {
        [TestCleanup]
        public void Clean()
        {
            S16SeMutants.ClearAll();
            S16SeWire.Off();
        }

        [TestMethod]
        public void Mutants_SetActiveClear_AndRegistry()
        {
            Assert.IsNull(S16SeMutants.Set("S11.M1", true), "not an S16 id");
            StringAssert.StartsWith(S16SeMutants.Set("S16.M7", true), "REFUSE", "S16.M7 is Subnautica's");
            StringAssert.StartsWith(S16SeMutants.Set("s16.m3", true), "S16.M3 ON");
            Assert.AreEqual("S16.M3", S16SeMutants.Active);
            Assert.AreEqual("S16.M3", Mutants.Active, "BuildFlavor's registry lists the S16 mutants (S16 hook)");
            StringAssert.StartsWith(Mutants.Set("S16.M6", true), "S16.M6 ON", "BuildFlavor routes the S16 ids (S16 hook)");
            Assert.IsTrue(S16SeMutants.CentreDepth);
            StringAssert.StartsWith(Mutants.Set("S16.M8", true), "S16.M8 ON");
            StringAssert.StartsWith(Mutants.Set("S16.M9", true), "S16.M9 ON");
            StringAssert.StartsWith(Mutants.Set("S16.M10", true), "S16.M10 ON");
            Assert.IsTrue(S16SeMutants.CountsNothing && S16SeMutants.NoGroups && S16SeMutants.NoWetFallback);
            Assert.AreEqual("S16.M3,S16.M6,S16.M8,S16.M9,S16.M10", S16SeMutants.Active);
            Mutants.ClearAll();
            Assert.AreEqual("", S16SeMutants.Active, "BuildFlavor's ClearAll clears them (S16 hook)");
        }

        [TestMethod]
        public void Mutants_ChangeOnlyTheirRule()
        {
            Assert.AreEqual(200.0, S16SeMutants.DamageThreshold(200.0));
            Assert.AreEqual(0.0, S16SeMutants.PerSecond(190.0, 200.0), "no damage above the limit");
            Assert.AreEqual(S16Damage.PerSecond(400.0, 200.0), S16SeMutants.PerSecond(400.0, 200.0), "the rule");
            Assert.AreEqual(50, S16SeMutants.CreakBand);
            S16SeMutants.EarlyDamage = true;
            Assert.AreEqual(150.0, S16SeMutants.DamageThreshold(200.0), "M2: from the creak band");
            Assert.AreEqual(10.0, S16SeMutants.PerSecond(190.0, 200.0), "M2: the base rate inside the band (depth 3: 10 hp/s)");
            Assert.AreEqual(S16Damage.PerSecond(250.0, 200.0), S16SeMutants.PerSecond(250.0, 200.0), "M2: the curve past the limit unchanged");
            S16SeMutants.ClearAll();
            S16SeMutants.NoCap = true;
            Assert.AreEqual(210.0, S16SeMutants.PerSecond(400.0, 200.0), 1e-12, "M3: 10 + 1.0 x 200 uncapped");
            Assert.AreEqual(100.0, S16Damage.PerSecond(400.0, 200.0), "the rule's cap (depth 3: 100 hp/s)");
            S16SeMutants.ClearAll();
            S16SeMutants.WideBand = true;
            Assert.AreEqual(100, S16SeMutants.CreakBand, "M4");
        }

        [TestMethod]
        public void C5Holder_FillsTheExtension_AndClearsWhenOff()
        {
            var st = new SeState();
            S16SeWire.Fill(ref st);
            S16WireState r;
            Assert.IsFalse(S16Wire.TryRead(ref st, out r), "inactive: no extension");
            S16SeWire.CreakSeq = 7;
            S16SeWire.VoiceSeq = 2;
            S16SeWire.DamageSeq = 3;
            S16SeWire.Set(new S16WireState { Flags = (byte)S16Conventions.C5Flags.Grid, CreakLevel = 2, DepthM = 175.5f, CrushM = 200f, WorldEpoch = 4, GridId = 99, HullPct = 73, PowerPct = 255 });
            S16SeWire.Fill(ref st);
            Assert.IsTrue(S16Wire.TryRead(ref st, out r));
            Assert.AreEqual(7u, r.CreakSeq, "the counters are the holder's");
            Assert.AreEqual(2u, r.VoiceSeq);
            Assert.AreEqual(3u, r.DamageSeq);
            Assert.AreEqual(175.5f, r.DepthM);
            Assert.AreEqual(99L, r.GridId);
            Assert.AreEqual(73, r.HullPct, "hull integrity for Subnautica's Seamoth readout (HUD B)");
            Assert.AreEqual(255, r.PowerPct, "no battery");
            S16SeWire.SetIdle(5);
            S16SeWire.Fill(ref st);
            Assert.IsTrue(S16Wire.TryRead(ref st, out r));
            Assert.AreEqual(0, r.Flags, "idle: no grid, no diver");
            Assert.AreEqual(7u, r.CreakSeq, "idle keeps the counters (Subnautica keeps its baselines)");
            S16SeWire.Off();
            S16SeWire.Fill(ref st);
            Assert.IsFalse(S16Wire.TryRead(ref st, out r), "off: cleared");
            S16SeWire.CreakSeq = S16SeWire.VoiceSeq = S16SeWire.DamageSeq = 0;
        }

        [TestMethod]
        public void Switch_DefaultOn()
        {
            string old = Environment.GetEnvironmentVariable(S16Switch.EnvVar);
            try
            {
                Environment.SetEnvironmentVariable(S16Switch.EnvVar, null);
                Assert.IsTrue(S16Switch.EnabledFromEnvironment());
                Environment.SetEnvironmentVariable(S16Switch.EnvVar, "OFF");
                Assert.IsFalse(S16Switch.EnabledFromEnvironment());
                Environment.SetEnvironmentVariable(S16Switch.EnvVar, "on");
                Assert.IsTrue(S16Switch.EnabledFromEnvironment());
            }
            finally { Environment.SetEnvironmentVariable(S16Switch.EnvVar, old); }
        }
    }
}
