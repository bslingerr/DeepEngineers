using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ChartC = DeepEngineers.Wire.S8aConventions.Chart;
using WindowC = DeepEngineers.Wire.S8aConventions.Window;
using NoticeFlags = DeepEngineers.Wire.S8aConventions.NoticeFlags;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S8a charts (CONTRACT §3.3 "Chart and treadmill", §5.4 formats chart.step, chart.anchor, store.key) against the
 /// Python twin's vectors (tests/s8a/vectors/chart.tsv): the R text of doubles, the chart key text and hash, the
 /// decision table, the step construction (F_k+1(camera) = F_k(camera) − T, the frame continuous at the camera).
 /// </summary>
    [TestClass]
    public class S8aChartTests
    {
        [TestMethod]
        public void Vectors_RTextKeysDecisionsSteps()
        {
            int rs = 0, keys = 0, decides = 0, steps = 0;
            SiteMap site = SiteMap.SiteV1Test;
            foreach (string[] f in S8aVectors.Lines("chart.tsv"))
            {
                switch (f[0])
                {
                    case "r":
                        Assert.AreEqual(f[2], S8aChart.R(S8aVectors.D(f[1])), "R of " + f[1]);
                        rs++;
                        break;
                    case "key":
                    {
                        Vec3 u0 = S8aVectors.V(f[1]), n0 = S8aVectors.V(f[2]), o = S8aVectors.V(f[4]);
                        double psi = S8aVectors.D(f[3]);
                        ulong sh = S8aVectors.X16(f[5]);
                        Assert.AreEqual(f[6], S8aChart.ChartKeyText(u0, n0, psi, o, sh));
                        Assert.AreEqual(S8aVectors.X16(f[7]), S8aChart.ChartKey(u0, n0, psi, o, sh));
                        keys++;
                        break;
                    }
                    case "decide":
                    {
                        uint seq = S8aVectors.U(f[1]);
                        S8aChartDecision d = S8aChart.Decide(seq, S8aVectors.V(f[2]), S8aVectors.V(f[3]), S8aVectors.V(f[4]), f[5] == "1");
                        string at = string.Join(" ", f);
                        Assert.AreEqual(S8aVectors.I(f[6]), d.Tx, "tx " + at);
                        Assert.AreEqual(S8aVectors.I(f[7]), d.Tz, "tz " + at);
                        Assert.AreEqual(f[8] == "1", d.ToSite, "toSite " + at);
                        Assert.AreEqual(f[9] == "1", d.Step, "step " + at);
                        Assert.AreEqual(f[10] == "-" ? null : f[10], d.Reason, "reason " + at);
                        decides++;
                        break;
                    }
                    case "step":
                    {
                        Vec3 camSe = S8aVectors.V(f[1]);
                        int tx = S8aVectors.I(f[2]), tz = S8aVectors.I(f[3]);
                        SiteMap k1 = S8aChart.Step(site, camSe, tx, tz);
                        Vec3 u0 = S8aVectors.V(f[4]), n0 = S8aVectors.V(f[5]), o = S8aVectors.V(f[6]), after = S8aVectors.V(f[7]);
                        Assert.AreEqual(0.0, (k1.U0 - u0).Length, 1e-12, "U0");
                        Assert.AreEqual(0.0, (k1.N0 - n0).Length, 1e-12, "N0");
                        Assert.AreEqual(0.0, (k1.O - o).Length, 1e-6, "O");
                        Vec3 got = k1.SeToSn(camSe);
                        Assert.AreEqual(0.0, (got - after).Length, 1e-6, "F_k+1(camera)");
                        Vec3 before = site.SeToSn(camSe);
                        Assert.AreEqual(before.X - 160.0 * tx, got.X, 1e-6, "T along x");
                        Assert.AreEqual(before.Z - 160.0 * tz, got.Z, 1e-6, "T along z");
                        Assert.AreEqual(before.Y, got.Y, 1e-6, "height kept");
                        Vec3 e, u, n;
                        site.FrameAt(camSe, out e, out u, out n);
                        Assert.AreEqual(0.0, (k1.N0 - n).Length, 1e-12, "north continuous at the camera");
                        Assert.AreEqual(0.0, (k1.E0 - e).Length, 1e-9, "east continuous at the camera");
                        Assert.AreEqual(0.0, (k1.U0 - u).Length, 1e-12, "the anchor is the camera's radial up");
                        steps++;
                        break;
                    }
                    default:
                        Assert.Fail("unknown row " + f[0]);
                        break;
                }
            }
            Assert.AreEqual(17, rs);
            Assert.AreEqual(4, keys);
            Assert.AreEqual(13, decides);
            Assert.AreEqual(4, steps);
        }

        [TestMethod]
        public void Decide_FromSeCamera_AndTheDistortionBoundOfTheStep()
        {
            SiteMap site = SiteMap.SiteV1Test;
            // chart 0, treadmill off: nothing ever
            Vec3 far = site.SnToSe(new Vec3(4801, -60, 0));
            Assert.IsFalse(S8aChart.Decide(site, site, 0, far, false).Any);
            S8aChartDecision d = S8aChart.Decide(site, site, 0, far, true);
            Assert.IsTrue(d.Step && d.Tx == ChartC.Step0Batches && d.Tz == 0 && d.Reason == "site-out");
            SiteMap k1 = S8aChart.Step(site, far, d.Tx, d.Tz);
            Assert.AreEqual(0.0, S8aChart.ArcFromAnchor(k1, far), 1e-9, "the camera sits on chart 1's anchor");
            // on chart 1 near the anchor: nothing; back to chart 0 when the site rho drops under ReturnM
            Assert.IsFalse(S8aChart.Decide(k1, site, 1, far, true).Any);
            Vec3 home = site.SnToSe(new Vec3(4000, -60, 0));
            Assert.IsTrue(S8aChart.Decide(k1, site, 1, home, true).ToSite);
            Assert.IsTrue(S8aChart.Decide(k1, site, 1, far, false).ToSite, "treadmill switched off: back to the site chart");
            // the distortion of chart 0 at the 0 -> 1 step: a point 100 m from the camera moves by <= 1.1e-3 * 100 + 0.01 m
            var rng = new Random(11);
            for (int i = 0; i < 200; i++)
            {
                Vec3 pSn = new Vec3(4801 + (rng.NextDouble() - 0.5) * 200, -60 + (rng.NextDouble() - 0.5) * 100, (rng.NextDouble() - 0.5) * 200);
                Vec3 pSe = site.SnToSe(pSn);
                Vec3 camK = k1.SeToSn(far), p1 = k1.SeToSn(pSe);
                Vec3 cam0 = site.SeToSn(far), p0 = site.SeToSn(pSe);
                Vec3 rel1 = p1 - camK, rel0 = p0 - cam0;
                double dist = rel0.Length;
                Assert.IsTrue((rel1 - rel0).Length <= 1.1e-3 * dist + 0.01, "jump " + (rel1 - rel0).Length + " at " + dist + " m");
            }
            // chart key of the site chart with sculpt hash 0 equals the first key vector's text
            Assert.AreEqual("chart|0|0|1|0|-1|0|0|0|0|0|0000000000000000", S8aChart.ChartKeyText(site.U0, site.N0, site.Psi * 180.0 / Math.PI, site.O, 0));
            Assert.AreEqual(S8aChart.ChartKey(site, 0), S8aChart.ChartKey(site.U0, site.N0, 0.0, site.O, 0));
            Assert.AreNotEqual(S8aChart.ChartKey(site, 0), S8aChart.ChartKey(k1, 0), "another anchor, another key");
            Assert.AreNotEqual(S8aChart.ChartKey(site, 0), S8aChart.ChartKey(site, 1), "another sculpt, another key");
        }

        [TestMethod]
        public void StepMetres_M8Shape_TakesAnyT()
        {
            SiteMap site = SiteMap.SiteV1Test;
            Vec3 cam = site.SnToSe(new Vec3(4900, -40, 10));
            SiteMap k = S8aChart.StepMetres(site, cam, 1234.5, -7.25);
            Vec3 after = k.SeToSn(cam), before = site.SeToSn(cam);
            Assert.AreEqual(before.X - 1234.5, after.X, 1e-6);
            Assert.AreEqual(before.Z + 7.25, after.Z, 1e-6);
            int tx, tz;
            bool toSite;
            S8aChart.Decide(1, new Vec3(7041, 0, 0), Vec3.Zero, new Vec3(9000, 0, 0), true).Deconstruct(out tx, out tz, out toSite);
            Assert.AreEqual(ChartC.StepBatches, tx); Assert.AreEqual(0, tz); Assert.IsFalse(toSite);
        }
    }

    /// <summary>
 /// S8a window plan (CONTRACT §3.3 "Window", §5.4 formats window.set, window.ready) against the Python twin's vectors
 /// (tests/s8a/vectors/window.tsv): the octree ring centre as Subnautica computes it, the ring plus look-ahead nearest
 /// first, the near box; and the universe rule (crater ids never, by in [0, 19]).
 /// </summary>
    [TestClass]
    public class S8aWindowPlanTests
    {
        [TestMethod]
        public void Vectors_CentrePlanNear()
        {
            int centres = 0, plans = 0, nears = 0;
            var ids = new List<Int3Key>();
            foreach (string[] f in S8aVectors.Lines("window.tsv"))
            {
                switch (f[0])
                {
                    case "centre":
                    {
                        Int3Key c = S8aWindowPlan.StreamCentre(S8aVectors.V(f[1]));
                        Assert.AreEqual(f[2], c.X + "," + c.Y + "," + c.Z, "centre of " + f[1]);
                        centres++;
                        break;
                    }
                    case "plan":
                        S8aWindowPlan.Plan(S8aVectors.V(f[1]), S8aVectors.V(f[2]), ids);
                        Assert.AreEqual(f[3], S8aVectors.Ids(ids), "plan " + f[1] + " / " + f[2]);
                        plans++;
                        break;
                    case "near":
                        S8aWindowPlan.NearBox(S8aVectors.V(f[1]), ids);
                        Assert.AreEqual(f[2], S8aVectors.Ids(ids), "near " + f[1]);
                        nears++;
                        break;
                    default:
                        Assert.Fail("unknown row " + f[0]);
                        break;
                }
            }
            Assert.AreEqual(8, centres);
            Assert.AreEqual(40, plans);
            Assert.AreEqual(8, nears);
        }

        [TestMethod]
        public void Universe_CraterNever_ByInRange_TopRowCut()
        {
            Assert.IsFalse(S8aWindowPlan.IsWindowId(12, 18, 12), "the crater");
            Assert.IsFalse(S8aWindowPlan.IsWindowId(0, 7, 25), "the crater's edge batch");
            Assert.IsTrue(S8aWindowPlan.IsWindowId(26, 18, 12));
            Assert.IsTrue(S8aWindowPlan.IsWindowId(-1, 0, 0));
            Assert.IsTrue(S8aWindowPlan.IsWindowId(WindowC.UniverseMin, 19, WindowC.UniverseMax));
            Assert.IsFalse(S8aWindowPlan.IsWindowId(WindowC.UniverseMin - 1, 19, 0));
            Assert.IsFalse(S8aWindowPlan.IsWindowId(30, 20, 30), "by 20 lies above the universe");
            Assert.IsFalse(S8aWindowPlan.IsWindowId(30, -1, 30));
            var ids = new List<Int3Key>();
            S8aWindowPlan.Plan(new Vec3(0, -50, 0), Vec3.Zero, ids);
            Assert.AreEqual(0, ids.Count, "no window ids around the site origin");
            S8aWindowPlan.Plan(new Vec3(4600, -60, 0), Vec3.Zero, ids);
            Assert.AreEqual(100, ids.Count, "60 m deep the ring's top row (by 20) is cut");
            S8aWindowPlan.Plan(new Vec3(4600, -400, 0), Vec3.Zero, ids);
            Assert.AreEqual(125, ids.Count, "a full ring 400 m deep");
            Assert.AreEqual(S8aWindowPlan.BatchOf(new Vec3(4600, -400, 0)), ids[0], "the camera's batch first");
            var ahead = new List<Int3Key>();
            S8aWindowPlan.Plan(new Vec3(4600, -400, 0), new Vec3(20, 0, 0), ahead);
            Assert.AreEqual(150, ahead.Count, "look-ahead: one column of 25 ids");
            foreach (Int3Key k in ids) Assert.IsTrue(ahead.Contains(k));
            S8aWindowPlan.Plan(new Vec3(4600, -400, 0), new Vec3(3, 0, 3), ahead);
            Assert.AreEqual(125, ahead.Count, "below LookAheadMinSpeedCmS: no look-ahead");
            S8aWindowPlan.Plan(new Vec3(4600, -400, 0), new Vec3(10, 0, 10), ahead);
            Assert.AreEqual(170, ahead.Count, "a diagonal velocity shifts the ring along both axes (45 new ids)");
            // the centre's truncating cast and FloorDiv
            Assert.AreEqual(12, S8aWindowPlan.StreamCentre(new Vec3(0, -50, 0)).X);
            Assert.AreEqual(0, S8aWindowPlan.StreamCentre(new Vec3(-2048.4, 0, 0)).X, "local -0.4 truncates to 0 -> batch 0");
            Assert.AreEqual(-1, S8aWindowPlan.StreamCentre(new Vec3(-2049.0, 0, 0)).X);
            Assert.AreEqual(WindowC.UniverseMax, S8aWindowPlan.StreamCentre(new Vec3(1e6, 0, 0)).X, "clamped to the widened centre");
            Assert.AreEqual(20, S8aWindowPlan.StreamCentre(new Vec3(0, 5000, 0)).Y);
            Assert.IsTrue(S8aWindowPlan.InWindow(new Vec3(3000, -20, 0)));
            Assert.IsFalse(S8aWindowPlan.InWindow(new Vec3(0, -20, 0)));
            Vec3 mn, mx;
            S8aWindowPlan.BatchBounds(new Int3Key(12, 18, 12), out mn, out mx);
            Assert.AreEqual(-128.0, mn.X); Assert.AreEqual(-160.0, mn.Y); Assert.AreEqual(32.0, mx.X); Assert.AreEqual(0.0, mx.Y);
        }

        [TestMethod]
        public void RecordGenerated_WaterBatchHasNoManifestLine_SurfaceBatchHas_AndTheTextRoundTrips()
        {
            //  S8aManifest.Set throws on an empty path, so a water batch recorded with "" never reached the
 // manifest and was regenerated every pass; the rule removes its line instead
            var m = new S8aManifest { Layer = S8aStore.WindowLayer, Key = 0x51, Hash = 0x099b72611d426788UL };
            var id = new Int3Key(40, 18, 7);
            string rel = S8aStore.RelativeWindowPath(0x00c0ffee00c0ffeeUL, 0x51, 40, 18, 7, 1);
            Assert.IsTrue(S8aWindowPlan.RecordGenerated(m, id, false, 1, 1234, 0xabcUL, NoticeFlags.AllSolid, rel));
            S8aManifest.Entry e;
            Assert.IsTrue(m.TryGet(40, 18, 7, out e) && e.RelativePath == rel && e.Version == 1);
            // the same id regenerated as water (a planet edit removed its rock): no line, no throw
            Assert.IsFalse(S8aWindowPlan.RecordGenerated(m, id, true, 2, 0, 0, NoticeFlags.AllEmpty, null));
            Assert.IsFalse(m.TryGet(40, 18, 7, out e));
            Assert.AreEqual(0, m.Count);
            // a water batch of an id never served: still no line, and the text round-trips without it
            Assert.IsFalse(S8aWindowPlan.RecordGenerated(m, new Int3Key(41, 18, 7), true, 1, 0, 0, NoticeFlags.AllEmpty, ""));
            Assert.IsTrue(S8aWindowPlan.RecordGenerated(m, new Int3Key(42, 18, 7), false, 3, 99, 0x1UL, 0, S8aStore.RelativeWindowPath(0x00c0ffee00c0ffeeUL, 0x51, 42, 18, 7, 3)));
            S8aManifest back;
            string err;
            Assert.IsTrue(S8aManifest.TryParse(m.ToText(), out back, out err), err);
            Assert.AreEqual(1, back.Count);
            Assert.IsTrue(back.TryGet(42, 18, 7, out e) && e.Version == 3);
            Assert.IsFalse(back.TryGet(41, 18, 7, out e));
            // the trap itself stays: an empty path is refused by the manifest
            try { m.Set(new S8aManifest.Entry { Bx = 1, By = 1, Bz = 1, Version = 1, RelativePath = "" }); Assert.Fail("empty path accepted"); }
            catch (ArgumentException) { }
        }
    }
}
