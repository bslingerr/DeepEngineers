using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S14, SE side, the game-free part (src/DeepEngineers.SE/S14/S14SeRules.cs): the seeded failures routed by BuildFlavor's
 /// registry (S14 hook); the Subnautica-side ids refused here.
 /// </summary>
    [TestClass]
    public class S14SeTests
    {
        [TestCleanup]
        public void Clean() { S14SeMutants.ClearAll(); }

        [TestMethod]
        public void Mutants_SetActiveClear_AndRegistry()
        {
            Assert.IsNull(S14SeMutants.Set("S16.M1", true), "not an S14 id");
            foreach (string sn in new[] { "S14.M4", "S14.M5", "S14.M6" })
                StringAssert.StartsWith(S14SeMutants.Set(sn, true), "REFUSE", sn + " is Subnautica's");
            StringAssert.StartsWith(S14SeMutants.Set("s14.m1", true), "S14.M1 ON");
            Assert.IsTrue(S14SeMutants.JetpackLoud);
            Assert.AreEqual("S14.M1", S14SeMutants.Active);
            Assert.AreEqual("S14.M1", Mutants.Active, "BuildFlavor's registry lists the S14 mutants (S14 hook)");
            StringAssert.StartsWith(Mutants.Set("S14.M2", true), "S14.M2 ON", "BuildFlavor routes the S14 ids (S14 hook)");
            StringAssert.StartsWith(Mutants.Set("S14.M3", true), "S14.M3 ON");
            StringAssert.StartsWith(Mutants.Set("S14.M7", true), "S14.M7 ON");
            StringAssert.StartsWith(Mutants.Set("S14.M8", true), "S14.M8 ON", "round 4: the pan mutant");
            StringAssert.StartsWith(Mutants.Set("S14.M9", true), "S14.M9 ON", "round 4: the chain mutant");
            StringAssert.StartsWith(Mutants.Set("S14.M10", true), "S14.M10 ON", "ext s14 audio 5: the muffle by the view's mix again");
            Assert.IsTrue(S14SeMutants.ArcadeOff && S14SeMutants.WeaponsReverted && S14SeMutants.ThrusterLoud && S14SeMutants.PanRaw && S14SeMutants.RealisticRecording && S14SeMutants.MuffleByMix);
            Assert.AreEqual("S14.M1,S14.M2,S14.M3,S14.M7,S14.M8,S14.M9,S14.M10", S14SeMutants.Active);
            StringAssert.StartsWith(Mutants.Set("S14.M2", false), "S14.M2 OFF");
            Assert.IsFalse(S14SeMutants.ArcadeOff);
            Mutants.ClearAll();
            Assert.AreEqual("", S14SeMutants.Active, "BuildFlavor's ClearAll clears them (S14 hook)");
            CollectionAssert.AreEquivalent(new[] { "S14.M1", "S14.M2", "S14.M3", "S14.M7", "S14.M8", "S14.M9", "S14.M10" }, S14SeMutants.All);
        }
    }
}
