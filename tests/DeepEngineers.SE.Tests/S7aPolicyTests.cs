using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S7a picture policy on the SE side (game-independent parts): <see cref="PicturePolicy"/> (selection, the
 /// <c>de.s7 {picture}</c> handler, the entity-set radius rule), the S7a.M1/M3 switches in the SE mutant registry, the
 /// driver feeding the policy and S7a.M1 into the machine, and the controller's re-assert and see-through rules.
 /// </summary>
    [TestClass]
    public class S7aPolicyTests
    {
        /// <summary>The band is a live setting since (default +1,000 / +1,100 m); these tests pin the mechanics at +300 / +350 m.</summary>
        [TestInitialize]
        public void PinTheTestBand()
        {
            Assert.IsTrue(ViewThresholds.SetBand(300.0, 350.0, "test"));
        }

        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); PicturePolicy.Set(ViewPicture.Site, "test cleanup"); }

        [TestMethod]
        public void PicturePolicy_DefaultSite_CommandSwitches_BadValueRefused()
        {
            uint p;
            Assert.IsTrue(PicturePolicy.TryParse(" Site ", out p) && p == ViewPicture.Site);
            Assert.IsTrue(PicturePolicy.TryParse("WATERLINE", out p) && p == ViewPicture.Waterline);
            Assert.IsFalse(PicturePolicy.TryParse("orbit", out p));
            Assert.IsFalse(PicturePolicy.TryParse(null, out p));
            Assert.AreEqual(ViewPicture.Site, PicturePolicy.Current, "the default (DEEPENGINEERS_PICTURE unset in tests)");

            Dictionary<string, object> r = PicturePolicy.Command(new Dictionary<string, object> { { "picture", "waterline" } });
            Assert.AreEqual(true, r["ok"]);
            Assert.AreEqual("waterline", r["picture"]);
            Assert.AreEqual("site", r["was"]);
            Assert.AreEqual(true, r["changed"]);
            Assert.AreEqual(ViewPicture.Waterline, PicturePolicy.Current);
            StringAssert.StartsWith((string)r["line"], "DE.S7a picture waterline (was site)");
            r = PicturePolicy.Command(new Dictionary<string, object> { { "picture", "waterline" } });
            Assert.AreEqual(false, r["changed"]);
            r = PicturePolicy.Command(new Dictionary<string, object> { { "picture", 3 } });
            Assert.AreEqual(false, r["ok"]);
            Assert.AreEqual(ViewPicture.Waterline, PicturePolicy.Current, "a refused value changes nothing");

            Mutants.Set("S7a.M1", true);
            r = PicturePolicy.Command(new Dictionary<string, object> { { "picture", "site" } });
            Assert.AreEqual("waterline", r["effective"], "S7a.M1: the machine runs waterline whatever is selected");
            StringAssert.Contains(PicturePolicy.Describe(), "MUTANT S7a.M1");
        }

        [TestMethod]
        public void Mutants_S7aM1M3_OnTheSeSide_M2M5M6NotSe_M4ToTheGhost()
        {
            StringAssert.StartsWith(Mutants.Set("S7a.M1", true), "S7a.M1 ON");
            StringAssert.StartsWith(Mutants.Set("s7a.m3", true), "S7a.M3 ON");
            Assert.IsTrue(S7aPolicyMutants.MachineWaterline && S7aPolicyMutants.NoFade);
            StringAssert.Contains(Mutants.Active, "S7a.M1,S7a.M3");
            foreach (string id in new[] { "S7a.M2", "S7a.M5", "S7a.M6" }) StringAssert.StartsWith(Mutants.Set(id, true), "NOT-SE");
            Assert.IsNull(S7aPolicyMutants.Set("S7a.M4", true), "S7a.M4 belongs to the ghost's switch");
            StringAssert.StartsWith(Mutants.Set("S7a.M4", true), "S7a.M4 ON");
            Mutants.ClearAll();
            Assert.IsFalse(S7aPolicyMutants.MachineWaterline || S7aPolicyMutants.NoFade);
        }

        [TestMethod]
        public void EntityRadii_AboveWaterUnderSite_1000And1050_ElseTheS5Radii()
        {
            double e = 250, l = 300;
            PicturePolicy.EntityRadii(true, 300.0, ref e, ref l);
            Assert.AreEqual(1500.0, e); Assert.AreEqual(1600.0, l);
            e = 250; l = 300;
            PicturePolicy.EntityRadii(true, -3.0, ref e, ref l);
            Assert.AreEqual(250.0, e, "under water: S5's enter radius"); Assert.AreEqual(1600.0, l, "review F10: the leave radius is kept under water (no pop-in after a dive)");
            PicturePolicy.EntityRadii(false, 300.0, ref e, ref l);
            Assert.AreEqual(250.0, e, "Waterline: S5's radii");
            PicturePolicy.EntityRadii(true, double.NaN, ref e, ref l);
            Assert.AreEqual(250.0, e, "no C5 altitude: S5's radii");
            e = 2000; l = 2100;
            PicturePolicy.EntityRadii(true, 300.0, ref e, ref l);
            Assert.AreEqual(2000.0, e, "a larger configured radius is kept"); Assert.AreEqual(2100.0, l);
        }

        [TestMethod]
        public void Driver_FeedsThePolicyAndS7aM1_SpaceOutputsCarryThePolicy()
        {
            Assert.AreEqual(ViewPicture.Site, ViewStateDriver.SpaceOutputs().PicturePolicy);
            PicturePolicy.Set(ViewPicture.Waterline, "test");
            Assert.AreEqual(ViewPicture.Waterline, ViewStateDriver.SpaceOutputs().PicturePolicy);
            PicturePolicy.Set(ViewPicture.Site, "test");
            Mutants.Set("S7a.M1", true);
            Assert.AreEqual(ViewPicture.Waterline, ViewStateDriver.SpaceOutputs().PicturePolicy, "S7a.M1");
            Mutants.ClearAll();

            var d = new ViewStateDriver { ClockUs = () => 1000000 };
            uint seen = 99;
            d.PictureSource = () => { seen = ViewPicture.Waterline; return ViewPicture.Waterline; };
            var f = new FrameFacts { Tick = 10, CamSn = new Vec3(100, 50, 100), Zone = Zone.Core };
            var l = new LinkFacts { Linked = true, PeerAlive = true, Peer = PeerState.Alive, SnPhase = SnPhase.Active, HelloEpoch = 1 };
            var g = new GameFacts { Foreground = true };
            var fb = new DisplayFeedback();
            ViewOutputs o = d.Step(ref f, ref l, ref g, ref fb, true, DisplayDesign.F, 0);
            Assert.AreEqual(ViewPicture.Waterline, seen);
            Assert.AreEqual(ViewPicture.Waterline, d.LastInputs.Picture);
            Assert.AreEqual(ViewPicture.Waterline, o.PicturePolicy);
            Mutants.Set("S7a.M1", true);
            d.PictureSource = () => ViewPicture.Site;
            f.Tick = 11;
            d.Step(ref f, ref l, ref g, ref fb, true, DisplayDesign.F, 0);
            Assert.AreNotEqual(0u, d.LastInputs.Mutants & ViewMutants.S7aM1Waterline, "S7a.M1 reaches the machine");
            Assert.AreEqual(ViewPicture.Waterline, d.Last.PicturePolicy);
        }
    }
}
