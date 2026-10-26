using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>
 /// S6.G1-E3 offline (CONTRACT §2.6, ROADMAP S6): the bound tool (<c>wire_ref.py s6bound</c>) against an independent C#
 /// sampling with <see cref="CollisionSkinMath.PrescaleCornerError"/> on <see cref="SiteMap.SiteV1Test"/>; every WM-1
 /// <c>box</c> record within (s − 1)·|h| and exact for level hulls with level boxes; and the S6.M5 mutant family
 /// ("colliders parented under the scaled Frame, Entity rotated 30°") beyond the bound (red), with the 30° turn
 /// being what makes it red (a level hull hides it).
 /// </summary>
    [TestClass]
    public class S6BoundTests
    {
        private static Dictionary<string, object> Bound(params string[] kv)
        {
            var args = new List<string> { Path.Combine("tools", "worldmap", "wire_ref.py"), "s6bound" };
            args.AddRange(kv);
            string text = RepoEnv.RunPython(args.ToArray()).Trim();
            Console.WriteLine("s6bound " + string.Join(" ", kv) + " -> " + text);
            return (Dictionary<string, object>)MiniJson.Parse(text);
        }

        /// <summary>A hull at SN y = −depth, up tilted by exactly <paramref name="tiltDeg"/>, random heading (wire_ref._s6_hull).</summary>
        private static WirePose Hull(SiteMap site, Random rnd, double depth, double tiltDeg)
        {
            Vec3 t = site.SnToSe(new Vec3(rnd.NextDouble() * 4000 - 2000, -depth, rnd.NextDouble() * 4000 - 2000));
            Vec3 e, u, n;
            site.FrameAt(t, out e, out u, out n);
            double az = rnd.NextDouble() * 2 * Math.PI, tl = tiltDeg * Math.PI / 180;
            Vec3 up = u * Math.Cos(tl) + (e * Math.Cos(az) + n * Math.Sin(az)) * Math.Sin(tl);
            Vec3 f = RandomUnit(rnd);
            f = (f - up * Vec3.Dot(f, up)).Normalized();
            var m = new SeMatrix(Vec3.Cross(f, up), up, -f, t);
            return global::DeepEngineers.Wire.WireMath.Pose(site, m, t);
        }

        private static Vec3 RandomUnit(Random rnd)
        {
            while (true)
            {
                var v = new Vec3(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1);
                double l = v.Length;
                if (l > 0.1 && l <= 1) return v / l;
            }
        }

        [TestMethod]
        public void S6Bound_PrintsTheContractFields()
        {
            Dictionary<string, object> b = Bound("depth=1000", "tilt=30");
            double worst = J.D(b, "worstM"), bound = J.D(b, "boundM"), s = J.D(b, "s");
            Assert.AreEqual(1000.0, J.D(b, "depth"));
            Assert.AreEqual(30.0, J.D(b, "tilt"));
            Assert.AreEqual(Math.Max(1.02 * worst, 0.001), bound, 1e-15);
            Assert.AreEqual(SiteMap.SiteV1Test.Rs / (SiteMap.SiteV1Test.Rs - 1000.0), s, 1e-9);
            Assert.IsTrue(worst > 0.01 && worst <= (s - 1) * Math.Sqrt(48), "worst " + worst);
            Dictionary<string, object> lvl = Bound("depth=150", "tilt=0", "axes=level", "samples=50");
            Assert.AreEqual(0.001, J.D(lvl, "boundM"), "level hull, level boxes: the 1 mm floor");
            Assert.ThrowsException<InvalidOperationException>(() => Bound("depth=100"), "tilt is required");
            Assert.ThrowsException<InvalidOperationException>(() => Bound("depth=100", "tilt=0", "bogus=1"));
        }

        [TestMethod]
        public void S6Bound_IndependentCSharpSamplingAgrees()
        {
            SiteMap site = SiteMap.SiteV1Test;
            var h = new Vec3(4, 4, 4);
            foreach (double[] dt in new[] { new[] { 150.0, 0.0 }, new[] { 1000.0, 30.0 }, new[] { 2000.0, 45.0 }, new[] { 500.0, 90.0 } })
            {
                Dictionary<string, object> b = Bound("depth=" + dt[0].ToString(CultureInfo.InvariantCulture), "tilt=" + dt[1].ToString(CultureInfo.InvariantCulture));
                double toolWorst = J.D(b, "worstM"), toolBound = J.D(b, "boundM");
                var rnd = new Random(600 + (int)dt[0]);
                double worst = 0;
                for (int i = 0; i < 2000; i++)
                {
                    WirePose p = Hull(site, rnd, dt[0], dt[1]);
                    Assert.AreEqual(dt[1], CollisionSkinMath.TiltDegrees(p.Q), 1e-6, "the hull has the tested tilt");
                    Vec3 up = RandomUnit(rnd), f = RandomUnit(rnd);
                    f = (f - up * Vec3.Dot(f, up)).Normalized();
                    worst = Math.Max(worst, CollisionSkinMath.PrescaleCornerError(p.Q, p.S, h, Vec3.Cross(f, up), up, -f));
                }
                Console.WriteLine("depth " + dt[0] + " tilt " + dt[1] + ": C# worst " + worst.ToString("F5") + " m over 2,000 samples, tool worst " +
                                  toolWorst.ToString("F5") + " m, bound " + toolBound.ToString("F5") + " m");
                Assert.IsTrue(worst <= toolBound * 1.05, "the bound holds for an independent sampling (within sampling noise)");
                Assert.IsTrue(worst >= toolWorst * 0.9, "the tool finds the worst case");
            }
        }

        [TestMethod]
        public void S6Bound_Wm1BoxRecordsWithinTheContractBound()
        {
            Wm1File f = Wm1File.ForSite("v1");
            var poses = new List<WirePose>();
            int boxes = 0, levelExact = 0;
            double worstRatio = 0;
            foreach (Dictionary<string, object> r in f.Records)
            {
                string kind = J.S(r, "kind");
                if (kind == "pose") poses.Add(new WirePose(J.V3(r, "o"), J.Q4(r, "q"), J.D(r, "s")));
                if (kind != "box") continue;
                WirePose p = poses[J.I(r, "pose")];
                List<object> axes = J.Arr(r, "axes_se");
                Vec3 h = J.V3(r, "half_extents_se");
                double e = CollisionSkinMath.PrescaleCornerError(p.Q, p.S, h, J.V3(axes[0]), J.V3(axes[1]), J.V3(axes[2]));
                double bound = Math.Abs(p.S - 1) * h.Length;
                worstRatio = Math.Max(worstRatio, bound > 0 ? e / bound : 0);
                Assert.IsTrue(e <= bound * (1 + 1e-9) + 1e-12, "box record " + boxes + ": " + e + " > (s - 1)*|h| = " + bound);
                boxes++;
                if (!J.B(r, "rotated") && CollisionSkinMath.TiltDegrees(p.Q) < 1e-6)
                {
                    Assert.IsTrue(e < 1e-9);
                    levelExact++;
                }
            }
            Console.WriteLine("WM-1 v1: " + boxes + " box records, worst corner error / ((s - 1)*|h|) = " + worstRatio.ToString("F4"));
            Assert.AreEqual(2000, boxes);
        }

        [TestMethod]
        public void S6Bound_M5FamilyIsRedOnlyWithTheTurn()
        {
            foreach (string depth in new[] { "150", "1000", "2000" })
            {
                Dictionary<string, object> turned = Bound("depth=" + depth, "tilt=30", "m5=1", "samples=150");
                double bound = J.D(turned, "boundM");
                Assert.IsTrue(J.D(turned, "m5LevelWorstM") > 2 * bound, "S6.M5 red at " + depth + " m, 30 deg");
                // under the lossyScale hypothesis a rotated box on its own child is no worse than the K method: the
 // in-game M5 check needs the level boxes on the Entity (CONTRACT 2.9 "fails the in-game box check")
                Assert.IsTrue(J.D(turned, "m5RotatedWorstM") <= bound * 1.05);
                Dictionary<string, object> level = Bound("depth=" + depth, "tilt=0", "m5=1", "samples=60");
                Assert.IsTrue(J.D(level, "m5LevelWorstM") < 1e-6, "a level hull hides the mutant: the 30 deg turn is needed");
            }
        }
    }
}
