using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>S0.WM-1: WireMath / SiteMap against every record of the generated WM-1 files (both sites).</summary>
    [TestClass]
    public class Wm1Tests
    {
        private static readonly Dictionary<string, int> ExpectedCounts = new Dictionary<string, int>
        {
            { "point", 10009 }, { "dir", 2000 }, { "pose", 2000 }, { "camera", 2000 }, { "local", 5000 },
            { "box", 2000 }, { "mesh", 1 }, { "motion", 1000 }, { "static", 1000 },
        };

        private static void AssertAllPass(string site)
        {
            Wm1File f = Wm1File.ForSite(site);
            Wm1Report rep = Wm1Checker.Run(f, new WireImpl());
            Console.WriteLine(rep.Summary());
            foreach (KeyValuePair<string, int> kv in ExpectedCounts)
                Assert.AreEqual(kv.Value, f.KindCounts.ContainsKey(kv.Key) ? f.KindCounts[kv.Key] : 0, "record count of kind " + kv.Key);
            Assert.AreEqual(25010, rep.TotalRecords, "records checked (header excluded)");
            Assert.AreEqual(0, rep.TotalFailed, rep.Summary());
        }

        [TestMethod]
        public void Wm1_SiteV1_EveryRecordWithinTolerance()
        {
            AssertAllPass("v1");
        }

        [TestMethod]
        public void Wm1_Example_EveryRecordWithinTolerance()
        {
            AssertAllPass("example");
        }

        [TestMethod]
        public void Wm1_HeaderTolerancesAreTheContractValues()
        {
            foreach (string site in new[] { "v1", "example" })
            {
                Wm1File f = Wm1File.ForSite(site);
                Assert.AreEqual(1e-6, f.TolPos, site);
                Assert.AreEqual(1e-3, f.TolPos32, site);
                Assert.AreEqual(1e-9, f.TolDir, site);
                Assert.AreEqual(1e-9, f.TolQuat, site);
                Assert.AreEqual(1e-12, f.TolScale, site);
                Assert.AreEqual(1e-9, f.TolRoundTrip, site);
                Assert.AreEqual(1.0, J.D(f.Header, "wire_version"), site);
            }
        }

        [TestMethod]
        public void Wm1_HeaderSitesAreSiteV1AndExample()
        {
            Wm1File v1 = Wm1File.ForSite("v1");
            Assert.AreEqual("SITE_V1", v1.SiteName);
            SiteMap s = SiteMap.SiteV1Test;
            Assert.AreEqual(s.C, v1.Site.C);
            Assert.AreEqual(s.Rs, v1.Site.Rs);
            Assert.AreEqual(SiteV1.RsExpected, v1.Site.Rs);
            Assert.AreEqual(s.U0, v1.Site.U0);
            Assert.AreEqual(s.N0, v1.Site.N0);
            Assert.AreEqual(s.E0, v1.Site.E0);
            Assert.AreEqual(SiteV1.AnchorE0, J.V3(J.Obj(v1.Header, "site"), "E0"));
            Assert.AreEqual((double)SiteV1.SiteVersion, J.D(J.Obj(v1.Header, "site"), "siteVersion"));

            Wm1File ex = Wm1File.ForSite("example");
            Assert.AreEqual("EXAMPLE", ex.SiteName);
            Assert.AreEqual(SiteMap.Example.C, ex.Site.C);
            Assert.AreEqual(SiteMap.Example.Rs, ex.Site.Rs);
            Assert.AreEqual(SiteMap.Example.E0, ex.Site.E0);
        }

        /// <summary>CONTRACT O1: pose and camera samples cover SN y in [-3,040, +350].</summary>
        [TestMethod]
        public void Wm1_PoseAndCameraSamplesCoverTheExtendedDepthRange()
        {
            Wm1File f = Wm1File.ForSite("v1");
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (Dictionary<string, object> r in f.Records)
            {
                string kind = J.S(r, "kind");
                if (kind == "camera")
                {
                    double y = J.V3(r, "pos").Y;
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
            List<object> range = J.Arr(J.Obj(f.Header, "ranges"), "pose_camera_sn_y");
            Assert.AreEqual(-3040.0, (double)range[0]);
            Assert.AreEqual(350.0, (double)range[1]);
            Console.WriteLine("camera SN y range in the v1 file: [" + minY + ", " + maxY + "]");
            Assert.IsTrue(minY < -2900.0 && maxY > 250.0, "camera samples do not span the O1 range: [" + minY + ", " + maxY + "]");
        }

        /// <summary>The test-only re-implementation used by the mutants is faithful when no mutation is switched on.</summary>
        [TestMethod]
        public void Wm1_MutantHarnessWithoutMutationPasses()
        {
            Wm1Report rep = Wm1Checker.Run(Wm1File.ForSite("v1"), new MutantWireImpl(Mutant.None));
            Console.WriteLine(rep.Summary());
            Assert.AreEqual(0, rep.TotalFailed, rep.Summary());
        }
    }

    /// <summary>S0.M4: every WM-1 mutant of gap-1 section 5 / CONTRACT 2.9 turns S0.WM-1 red.</summary>
    [TestClass]
    public class Wm1MutantTests
    {
        private static void AssertRed(Mutant m, params string[] kinds)
        {
            Wm1Report rep = Wm1Checker.Run(Wm1File.ForSite("v1"), new MutantWireImpl(m));
            Console.WriteLine(rep.Summary());
            Assert.IsTrue(rep.TotalFailed > 0, m + ": WM-1 stayed green");
            foreach (string k in kinds)
                Assert.IsTrue(rep.Failed(k) > 0, m + ": kind '" + k + "' stayed green\n" + rep.Summary());
        }

        [TestMethod] public void S0M4_MirrorDropped_IsRed() { AssertRed(Mutant.MirrorDropped, "local", "box", "mesh"); }
        [TestMethod] public void S0M4_TriangleSwapDropped_IsRed() { AssertRed(Mutant.TriangleSwapDropped, "mesh"); }
        [TestMethod] public void S0M4_MapdirAtEntityOriginInsteadOfPivot_IsRed() { AssertRed(Mutant.MapdirAtEntityOrigin, "pose"); }
        [TestMethod] public void S0M4_ScaleDropped_IsRed() { AssertRed(Mutant.ScaleDropped, "pose", "local", "box", "motion"); }
        [TestMethod] public void S0M4_ScaleAppliedToY_IsRed() { AssertRed(Mutant.ScaleAppliedToY, "pose", "local", "box", "motion"); }
        [TestMethod] public void S0M4_AngularVelocityWithoutSignFlip_IsRed() { AssertRed(Mutant.AngVelNoSignFlip, "motion"); }
        [TestMethod] public void S0M4_DoubleMirrorOnSubnauticaSide_IsRed() { AssertRed(Mutant.DoubleMirror, "local", "box"); }
        [TestMethod] public void S0M4_StaticAxesWithoutMirror_IsRed() { AssertRed(Mutant.StaticAxesWithoutMirror, "static"); }
    }
}
