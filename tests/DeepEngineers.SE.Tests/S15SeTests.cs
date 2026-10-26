using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S15, SE side, the game-free part (src/DeepEngineers.SE/S15/S15SeLogic.cs): the C5 block holder Link.WriteC5 fills (S15
 /// hook; cleared when inactive), the C6 writer hook of Link.WriteSeTime (never throws), the counters, the switches, and the
 /// S15 mutant ids routed by BuildFlavor's registry (S15 hook).
 /// </summary>
    [TestClass]
    public unsafe class S15SeTests
    {
        [TestCleanup]
        public void Clean()
        {
            S15SeMutants.ClearAll();
            S15SeWire.Off();
            S15SeWire.C6Extra = null;
        }

        [TestMethod]
        public void Mutants_SetActiveClear_AndRegistry()
        {
            Assert.IsNull(S15SeMutants.Set("S16.M1", true), "not an S15 id");
            StringAssert.StartsWith(S15SeMutants.Set("S15.M5", true), "REFUSE", "S15.M5 is Subnautica's");
            StringAssert.StartsWith(S15SeMutants.Set("s15.m2", true), "S15.M2 ON");
            Assert.AreEqual("S15.M2", S15SeMutants.Active);
            StringAssert.Contains(Mutants.Active, "S15.M2", "BuildFlavor's registry lists the S15 mutants (S15 hook)");
            StringAssert.StartsWith(Mutants.Set("S15.M4", true), "S15.M4 ON", "BuildFlavor routes the S15 ids (S15 hook)");
            Assert.IsTrue(S15SeMutants.WaterOff);
            StringAssert.StartsWith(S15SeMutants.Set("S15.M3", true), "REFUSE", "S15.M3 is Subnautica's (review MEDIUM 5)");
            StringAssert.StartsWith(S15SeMutants.Set("S15.M16", true), "S15.M16 ON");
            StringAssert.StartsWith(S15SeMutants.Set("S15.M17", true), "S15.M17 ON");
            StringAssert.StartsWith(S15SeMutants.Set("S15.M18", true), "S15.M18 ON");
            Assert.IsTrue(S15SeMutants.ThrustPower && S15SeMutants.HeldWelcome && S15SeMutants.SizeIgnored);
            StringAssert.Contains(S15SeMutants.Active, "S15.M16,S15.M17,S15.M18");
            foreach (string retired in new[] { "S15.M8", "S15.M9", "S15.M10" })
                StringAssert.Contains(S15SeMutants.Set(retired, true), "retired", retired + " seeded a removed rule");
            foreach (string sn in new[] { "S15.M11", "S15.M12", "S15.M13", "S15.M14", "S15.M15", "S15.M19", "S15.M20" })
                StringAssert.StartsWith(S15SeMutants.Set(sn, true), "REFUSE", sn + " is Subnautica's");
            Mutants.ClearAll();
            Assert.AreEqual("", S15SeMutants.Active, "BuildFlavor's ClearAll clears S15's (S15 hook)");
        }

        [TestMethod]
        public void C5_FillsWhenActive_ClearsWhenNot()
        {
            uint before = S15SeWire.PowerDownSeq;
            Assert.AreEqual(before + 1, S15SeWire.Bump(S15Conventions.Event.EnginePowerDown));
            uint small = S15SeWire.SmallWelcomeSeq;
            Assert.AreEqual(small + 1, S15SeWire.Bump(S15Conventions.Event.SmallWelcome));
            Assert.AreEqual(0u, S15SeWire.Bump(5), "only the owner's four lines are counted");
            S15SeWire.Set(new S15WireState { Flags = (byte)S15Conventions.C5Flags.Aboard, WorldEpoch = 4, GridId = 77 });
            var st = new SeState();
            S15SeWire.Fill(ref st);
            S15WireState w;
            Assert.IsTrue(S15Wire.TryRead(ref st, out w));
            Assert.AreEqual(77L, w.GridId);
            Assert.AreEqual(before + 1, w.PowerDownSeq, "the counters ride every record");
            Assert.AreEqual(small + 1, w.SmallWelcomeSeq);
            S15SeWire.Off();
            S15SeWire.Fill(ref st);
            Assert.IsFalse(S15Wire.TryRead(ref st, out w), "inactive: version 0 = absent");
        }

        [TestMethod]
        public void C6Hook_NeverThrows()
        {
            byte* region = stackalloc byte[0x1000];
            S15SeWire.C6Extra = null;
            S15SeWire.WriteC6(region, 1);
            long errors = S15SeWire.C6Errors;
            S15SeWire.C6Extra = (r, t) => { throw new InvalidOperationException("boom"); };
            S15SeWire.WriteC6(region, 2);
            Assert.AreEqual(errors + 1, S15SeWire.C6Errors);
            S15SeWire.C6Extra = (r, t) => { var f = new S15SkyFrame { Flags = 1, Tick = t }; S15SkyWire.WriteFrame(r, ref f); };
            S15SeWire.WriteC6(region, 3);
            S15SkyFrame back;
            uint seq;
            Assert.AreEqual(SeqlockReadStatus.Fresh, S15SkyWire.TryReadFrame(region, out back, out seq));
            Assert.AreEqual(3UL, back.Tick);
        }
    }
}
