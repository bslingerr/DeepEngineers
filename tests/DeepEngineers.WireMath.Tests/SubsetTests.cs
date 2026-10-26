using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>S0.G1-E4a (synthetic part): the embedded in-game subsets and <see cref="SelfTest"/>.</summary>
    [TestClass]
    public class SubsetTests
    {
        private static List<Dictionary<string, object>> ReadJsonl(string path, out Dictionary<string, object> header)
        {
            header = null;
            var list = new List<Dictionary<string, object>>();
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                var rec = (Dictionary<string, object>)MiniJson.Parse(line);
                if (J.S(rec, "kind") == "subset-header") header = rec;
                else list.Add(rec);
            }
            return list;
        }

        /// <summary>tests/wm1/subset-*.jsonl and src/DeepEngineers.Wire/Wm1Subset.cs are what make_subset.py makes from the current wire_ref.py.</summary>
        [TestMethod]
        public void Subset_GeneratedFilesAreUpToDate()
        {
            string output = RepoEnv.RunPython(Path.Combine("tests", "wm1", "make_subset.py"), "--check");
            Console.WriteLine(output.Trim());
            StringAssert.Contains(output, "OK");
        }

        [TestMethod]
        public void Subset_FileHashesAndCounts()
        {
            Assert.AreEqual(Wm1Subset.SeFileSha256, RepoEnv.Sha256File(RepoEnv.PathIn("tests", "wm1", "subset-se.jsonl")));
            Assert.AreEqual(Wm1Subset.SnFileSha256, RepoEnv.Sha256File(RepoEnv.PathIn("tests", "wm1", "subset-sn.jsonl")));
            Assert.AreEqual(50, Wm1Subset.SePoseCount);
            Assert.AreEqual(50, Wm1Subset.SnLocalCount);
            Assert.AreEqual(20, Wm1Subset.SnBoxCount);
            Assert.AreEqual(10, Enumerable.Range(0, Wm1Subset.SnBoxCount).Count(i => Wm1Subset.SnBox(i).Rotated));
            Assert.AreEqual("SITE_V1", Wm1Subset.SiteName);
            Assert.AreEqual(SiteV1.RsExpected, Wm1Subset.SiteRs);
            Assert.AreEqual(SiteV1.TestC, Wm1Subset.Site.C);
            Assert.AreEqual(Wm1Subset.SeDataSha256, Wm1Subset.ComputeSeDataSha256());
            Assert.AreEqual(Wm1Subset.SnDataSha256, Wm1Subset.ComputeSnDataSha256());
        }

        /// <summary>The embedded arrays hold exactly the numbers of the subset files, which are records of the full SITE_V1 WM-1 file.</summary>
        [TestMethod]
        public void Subset_EmbeddedRecordsAreRecordsOfTheFullV1File()
        {
            Wm1File full = Wm1File.ForSite("v1");
            Assert.AreEqual(Wm1Subset.SourceSha256, RepoEnv.Sha256File(full.Path), "the subset was made from another WM-1 file");
            List<Dictionary<string, object>> poses = full.Records.Where(r => J.S(r, "kind") == "pose").ToList();
            List<Dictionary<string, object>> locals = full.Records.Where(r => J.S(r, "kind") == "local").ToList();
            List<Dictionary<string, object>> boxes = full.Records.Where(r => J.S(r, "kind") == "box").ToList();
            List<Dictionary<string, object>> boxPick = boxes.Where(r => !J.B(r, "rotated")).Take(10)
                .Concat(boxes.Where(r => J.B(r, "rotated")).Take(10)).ToList();

            Dictionary<string, object> seHeader, snHeader;
            List<Dictionary<string, object>> se = ReadJsonl(RepoEnv.PathIn("tests", "wm1", "subset-se.jsonl"), out seHeader);
            List<Dictionary<string, object>> sn = ReadJsonl(RepoEnv.PathIn("tests", "wm1", "subset-sn.jsonl"), out snHeader);
            Assert.AreEqual(Wm1Subset.SourceSha256, J.S(seHeader, "source_sha256"));
            Assert.AreEqual(Wm1Subset.SeDataSha256, J.S(seHeader, "data_sha256"));
            Assert.AreEqual(Wm1Subset.SnDataSha256, J.S(snHeader, "data_sha256"));
            Assert.AreEqual(Wm1Subset.SePoseCount, se.Count);
            Assert.AreEqual(Wm1Subset.SnLocalCount + Wm1Subset.SnBoxCount, sn.Count);

            for (int i = 0; i < Wm1Subset.SePoseCount; i++)
            {
                Wm1PoseRecord e = Wm1Subset.SePose(i);
                foreach (Dictionary<string, object> r in new[] { se[i], poses[i] })
                {
                    SeMatrix m = J.Matrix(r, "se_matrix");
                    Assert.AreEqual(m.Right, e.Matrix.Right); Assert.AreEqual(m.Up, e.Matrix.Up);
                    Assert.AreEqual(m.Backward, e.Matrix.Backward); Assert.AreEqual(m.Translation, e.Matrix.Translation);
                    Assert.AreEqual(J.V3(r, "box_min"), e.BoxMin); Assert.AreEqual(J.V3(r, "box_max"), e.BoxMax);
                    Assert.AreEqual(J.V3(r, "camera_se"), e.Camera); Assert.AreEqual(J.V3(r, "pivot_se"), e.ExpectedPivot);
                    Assert.AreEqual(J.V3(r, "o"), e.Expected.O); Assert.AreEqual(J.Q4(r, "q"), e.Expected.Q);
                    Assert.AreEqual(J.D(r, "s"), e.Expected.S);
                }
            }
            for (int i = 0; i < Wm1Subset.SnLocalCount; i++)
            {
                Wm1LocalRecord e = Wm1Subset.SnLocal(i);
                Dictionary<string, object> pose = poses[J.I(locals[i], "pose")];
                Assert.AreEqual(J.I(locals[i], "pose"), J.I(sn[i], "pose"));
                Assert.AreEqual(J.V3(pose, "o"), e.Pose.O); Assert.AreEqual(J.Q4(pose, "q"), e.Pose.Q); Assert.AreEqual(J.D(pose, "s"), e.Pose.S);
                Assert.AreEqual(J.V3(sn[i], "pose_o"), e.Pose.O);
                Assert.AreEqual(J.V3(locals[i], "local_se"), e.LocalSe);
                Assert.AreEqual(J.V3(locals[i], "sn"), e.ExpectedSn);
            }
            for (int i = 0; i < Wm1Subset.SnBoxCount; i++)
            {
                Wm1BoxRecord e = Wm1Subset.SnBox(i);
                Dictionary<string, object> r = boxPick[i];
                Dictionary<string, object> pose = poses[J.I(r, "pose")];
                Assert.AreEqual(J.V3(pose, "o"), e.Pose.O); Assert.AreEqual(J.Q4(pose, "q"), e.Pose.Q); Assert.AreEqual(J.D(pose, "s"), e.Pose.S);
                Assert.AreEqual(J.V3(r, "section_centre_se"), e.SectionCentreSe);
                Assert.AreEqual(J.V3(r, "section_root_sn"), e.ExpectedSectionRootSn);
                Assert.AreEqual(J.V3(r, "box_centre_se"), e.BoxCentreSe);
                Assert.AreEqual(J.V3(r, "half_extents_se"), e.HalfExtentsSe);
                List<object> axes = J.Arr(r, "axes_se");
                Assert.AreEqual(J.V3(axes[0]), e.AxisSe0); Assert.AreEqual(J.V3(axes[1]), e.AxisSe1); Assert.AreEqual(J.V3(axes[2]), e.AxisSe2);
                Assert.AreEqual(J.V3(r, "centre_section_local_unity"), e.ExpectedCentreUnity);
                Assert.AreEqual(J.V3(r, "half_extents_unity"), e.ExpectedHalfExtentsUnity);
                Assert.AreEqual(J.Q4(r, "rotation_section_local_unity"), e.ExpectedRotationUnity);
                Assert.AreEqual(J.B(r, "rotated"), e.Rotated);
            }
        }

        [TestMethod]
        public void SelfTest_Se_Passes()
        {
            SelfTestResult r = SelfTest.Run(SelfTestKind.Se);
            Console.WriteLine(r.LogLine);
            Assert.IsTrue(r.Pass, r.LogLine);
            Assert.AreEqual(50, r.Count);
            StringAssert.StartsWith(r.LogLine, "DE.WireSelfTest PASS n=50 ");
            Assert.AreEqual(Wm1Subset.SeDataSha256, r.DataSha256);
        }

        [TestMethod]
        public void SelfTest_Sn_Passes()
        {
            SelfTestResult r = SelfTest.Run(SelfTestKind.Sn);
            Console.WriteLine(r.LogLine);
            Assert.IsTrue(r.Pass, r.LogLine);
            Assert.AreEqual(70, r.Count);
            StringAssert.StartsWith(r.LogLine, "DE.WireSelfTest PASS n=70 ");
            Assert.AreEqual(Wm1Subset.SnDataSha256, r.DataSha256);
        }

        /// <summary>Seeded failure for the self-check: one corrupted number turns it red (hash and tolerance), and it never throws.</summary>
        [TestMethod]
        public void SelfTest_FailsOnACorruptedRecord()
        {
            foreach (var target in new[] { new { field = "SePoseData", kind = SelfTestKind.Se, index = 24 },        // pose 0, o.x
                                           new { field = "SnLocalData", kind = SelfTestKind.Sn, index = 11 },       // local 0, sn.x
                                           new { field = "SnBoxData", kind = SelfTestKind.Sn, index = 40 + 35 } })  // box 1, rotation.x
            {
                var data = (double[])typeof(Wm1Subset).GetField(target.field, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                double saved = data[target.index];
                try
                {
                    data[target.index] = saved + 1e-3;
                    SelfTestResult r = SelfTest.Run(target.kind);
                    Console.WriteLine(target.field + "[" + target.index + "] + 1e-3: " + r.LogLine);
                    Assert.IsFalse(r.Pass, target.field);
                    Assert.IsTrue(r.Failures >= 1, target.field + ": the tolerance check itself must trip, not only the hash");
                    StringAssert.StartsWith(r.LogLine, "DE.WireSelfTest FAIL ");
                }
                finally
                {
                    data[target.index] = saved;
                }
            }
            Assert.IsTrue(SelfTest.Run(SelfTestKind.Se).Pass && SelfTest.Run(SelfTestKind.Sn).Pass, "restored data must pass again");
        }

        /// <summary>The subset's box records realise as unit-scale section roots: for grid-aligned boxes on a level hull the
 /// collider corners land exactly on the affine image of the SE box corners (CONTRACT 2.6 "exact" case).</summary>
        [TestMethod]
        public void Subset_BoxCornersAgreeWithTheAffineWithinTheContractBound()
        {
            for (int i = 0; i < Wm1Subset.SnBoxCount; i++)
            {
                Wm1BoxRecord b = Wm1Subset.SnBox(i);
                ProxyBox box = Wire.WireMath.PrescaleBox(b.Pose.Q, b.Pose.S, b.SectionCentreSe, b.BoxCentreSe, b.HalfExtentsSe, b.AxisSe0, b.AxisSe1, b.AxisSe2);
                Vec3 root = Wire.WireMath.Apply(b.Pose, b.SectionCentreSe);
                double bound = (b.Pose.S > 1 ? b.Pose.S - 1 : 1 - b.Pose.S) * b.HalfExtentsSe.Length + 1e-9;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vec3 cornerSe = b.BoxCentreSe + b.AxisSe0 * (sx * b.HalfExtentsSe.X) + b.AxisSe1 * (sy * b.HalfExtentsSe.Y) + b.AxisSe2 * (sz * b.HalfExtentsSe.Z);
                            Vec3 exact = Wire.WireMath.Apply(b.Pose, cornerSe);
                            Vec3 collider = root + b.Pose.Q.Rotate(box.CornerSectionLocal(sx, sy, sz));
                            double err = Vec3.Distance(collider, exact);
                            Assert.IsTrue(err <= bound, "box " + i + " corner error " + err + " > (s - 1)|h| " + bound);
                        }
            }
        }
    }
}
