using System;
using DeepEngineers.SE;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S8a, the game-free part of the SE side: the mutant registry (ROADMAP "### S8a" "Seeded failures": every SE flag
 /// through <see cref="Mutants.Set"/>, S8a.M13 forwarded to the Wire core), the release rule (de.s8a is a developer
 /// command: not in UserCommandRules' table).
 /// </summary>
    [TestClass]
    public class S8aTests
    {
        [TestCleanup]
        public void Cleanup() { S8aMutants.ClearAll(); }

        [TestMethod]
        public void Mutants_EverySeFlagThroughTheRegistry_M13ForwardedToWire()
        {
            if (!BuildFlavor.IsDev) Assert.Inconclusive("mutants exist in dev builds only");
            foreach (string id in S8aMutants.All)
            {
                string r = Mutants.Set(id, true);
                StringAssert.StartsWith(r, id + " ON", r);
            }
            Assert.IsTrue(S8aMutants.M1 && S8aMutants.M2 && S8aMutants.M3 && S8aMutants.M5 && S8aMutants.M6 && S8aMutants.M8 && S8aMutants.M9 && S8aMutants.M10 && S8aMutants.M12 && S8aMutants.M14);
            Assert.IsTrue(S8aWireMutants.M13Child0Interior, "S8a.M13 lives in the Wire core");
            Assert.AreEqual(string.Join(",", S8aMutants.All), S8aMutants.Active);
            StringAssert.StartsWith(Mutants.Set("s8a.m13", false), "S8a.M13 OFF");
            Assert.IsFalse(S8aWireMutants.M13Child0Interior);
            StringAssert.StartsWith(Mutants.Set("S8a.M4", true), "UNKNOWN", "M4 is the Subnautica side's");
            StringAssert.Contains(Mutants.Set("S8a.M99", true), "S8a.M1");
            S8aMutants.ClearAll();
            Assert.AreEqual("", S8aMutants.Active);
        }

        [TestMethod]
        public void ReleaseRule_DeS8aIsADeveloperCommand()
        {
            Assert.IsTrue(UserCommandRules.RequestAllowed(true, "de.s8a", null));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s8a", null), "a release build refuses de.s8a");
            Assert.IsFalse(UserCommandRules.ChatAllowed(false, new[] { "s8a", "status" }));
        }
    }
}
