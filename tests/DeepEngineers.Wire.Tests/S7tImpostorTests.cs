using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7t T3: the impostor conventions (<see cref="S7tImpostorRules"/>): the GridAdd bit is free (outside S5's
 /// 0–2, S7a's Ghost 3, S7p's Projection 4 / Missile 5 and the version nibble) and read only on a version-1 record; the
 /// part classes an impostor carries; the vertical reach with its hysteresis; the admission order and caps; the diff filter
 /// that holds back edges and subparts and keeps the held copies current.
 /// </summary>
    [TestClass]
    public class S7tImpostorTests
    {
        [TestMethod]
        public void Bit_IsFree_VersionGated_AndToggles()
        {
            uint bit = S7tImpostorRules.GridFlagImpostor;
            Assert.AreEqual(64u, bit, "GridAdd bit 6");
            uint used = MirrorGridFlags.Static | MirrorGridFlags.Subgrid | MirrorGridFlags.FloatingObject | S7aConventions.GridFlagsExt.Ghost |
                        S7pScene.GridFlagProjection | S7pScene.GridFlagMissile | 0xF0000000u;
            Assert.AreEqual(0u, bit & used, "no overlap with S5, S7a, S7p or the version nibble");
            uint v1 = MirrorGridFlags.Version1;
            Assert.IsTrue(S7tImpostorRules.IsImpostor(S7tImpostorRules.GridFlags(v1, true)));
            Assert.IsFalse(S7tImpostorRules.IsImpostor(S7tImpostorRules.GridFlags(v1 | bit, false)), "an upgrade clears it");
            Assert.IsFalse(S7tImpostorRules.IsImpostor(bit), "a legacy (S3) record never is one");
            Assert.IsFalse(S7tImpostorRules.IsImpostor(bit | (2u << 28)), "a rejected record never is one");
            Assert.AreEqual(v1 | MirrorGridFlags.Static | bit, S7tImpostorRules.GridFlags(v1 | MirrorGridFlags.Static, true), "other bits kept");
        }

        [TestMethod]
        public void Parts_CubePartsAndFatBlocksOnly()
        {
            uint v1 = MirrorPartFlags.Version1;
            Assert.IsTrue(S7tImpostorRules.PartAllowed(v1 | MirrorPartClass.CubePart));
            Assert.IsTrue(S7tImpostorRules.PartAllowed(v1 | MirrorPartClass.FatBlock | MirrorPartFlags.BuildStage));
            Assert.IsTrue(S7tImpostorRules.PartAllowed(v1), "version-1 class 0 = cube part");
            Assert.IsFalse(S7tImpostorRules.PartAllowed(v1 | MirrorPartClass.Edge));
            Assert.IsFalse(S7tImpostorRules.PartAllowed(v1 | MirrorPartClass.Subpart | MirrorPartFlags.Dynamic));
            Assert.IsFalse(S7tImpostorRules.PartAllowed(v1 | MirrorPartClass.FloatingObject));
        }

        [TestMethod]
        public void Reach_SplitAndHysteresis()
        {
            double h, u;
            S7tImpostorRules.Split(new Vec3(300, 2000, 400), Vec3.UnitY, out h, out u);
            Assert.AreEqual(500.0, h, 1e-9);
            Assert.AreEqual(2000.0, u, 1e-9);
            Vec3 tilted = new Vec3(1, 1, 0).Normalized();
            S7tImpostorRules.Split(tilted * 100.0, tilted, out h, out u);
            Assert.AreEqual(0.0, h, 1e-9);
            Assert.AreEqual(100.0, u, 1e-9);

            Assert.IsTrue(S7tImpostorRules.InReach(0, 2000, 50, false), "a ship parked 2 km above");
            Assert.IsTrue(S7tImpostorRules.InReach(1500, 4990, 0, false));
            Assert.IsFalse(S7tImpostorRules.InReach(1501, 2000, 0, false), "horizontal enter 1,500 m");
            Assert.IsTrue(S7tImpostorRules.InReach(1600, 2000, 0, true), "horizontal leave 1,600 m");
            Assert.IsFalse(S7tImpostorRules.InReach(1601, 2000, 0, true));
            Assert.IsTrue(S7tImpostorRules.InReach(1550, 2000, 100, false), "the clearance counts the bounding sphere");
            Assert.IsFalse(S7tImpostorRules.InReach(0, 5001, 0, false), "up enter 5,000 m");
            Assert.IsTrue(S7tImpostorRules.InReach(0, 5400, 0, true), "up leave 5,500 m");
            Assert.IsFalse(S7tImpostorRules.InReach(0, 5501, 0, true));
            Assert.IsFalse(S7tImpostorRules.InReach(0, -10, 5, false), "below the camera: the S5 / S7a radius rule");
            Assert.IsTrue(S7tImpostorRules.InReach(0, -10, 20, false), "reaching the camera's level");
            Assert.IsTrue(S7tImpostorRules.InReach(0, -90, 0, true), "a member stays down to -100 m");
            // R1: from +1,000 m a ship on the sea 1.4 km out stays an impostor through the crossfade
            Assert.IsTrue(S7tImpostorRules.InReach(1400, -1000, 20, false, 1000), "candidate: the column runs from the sea up");
            Assert.IsTrue(S7tImpostorRules.InReach(1400, -1000, 20, true, 1000), "member: the same");
            Assert.IsFalse(S7tImpostorRules.InReach(1400, -1200, 20, true, 1000), "below the sea margin: out");
            Assert.IsFalse(S7tImpostorRules.InReach(1700, -1000, 20, false, 1000), "the horizontal rule still holds");
            Assert.IsFalse(S7tImpostorRules.InReach(0, -10, 5, false, double.NaN), "a non-finite height: out");
            double cu, cr;
            S7tImpostorRules.QuerySphere(1000, out cu, out cr);
            Assert.AreEqual((S7tImpostorRules.UpLeaveM - 1100) / 2.0, cu, 1e-9, "centre between the sea margin and the top");
            double colHalf = (S7tImpostorRules.UpLeaveM + 1100) / 2.0;
            Assert.AreEqual(Math.Sqrt(colHalf * colHalf + S7tImpostorRules.HorizontalLeaveM * S7tImpostorRules.HorizontalLeaveM) + 1.0, cr, 1e-9);
            Assert.IsTrue(Math.Sqrt(1400.0 * 1400.0 + (cu + 1000) * (cu + 1000)) <= cr, "a sea-level ship 1.4 km out is inside the query sphere");
            Assert.IsFalse(S7tImpostorRules.InReach(0, -101, 0, true));
            Assert.IsFalse(S7tImpostorRules.InReach(double.NaN, 100, 0, true));
            Assert.IsFalse(S7tImpostorRules.InReach(0, double.PositiveInfinity, 0, false));
            // the query sphere contains the whole leave cylinder
            double half = (S7tImpostorRules.UpLeaveM + S7tImpostorRules.BelowLeaveM) / 2.0;
            Assert.IsTrue(S7tImpostorRules.QueryRadiusM >= Math.Sqrt(half * half + S7tImpostorRules.HorizontalLeaveM * S7tImpostorRules.HorizontalLeaveM));
            Assert.AreEqual(2700.0, S7tImpostorRules.QueryCentreUpM, 1e-9);
        }

        [TestMethod]
        public void Admission_LargeOnly_CappedNearestFirst()
        {
            Assert.IsNull(S7tImpostorRules.Refusal(true, 500, false));
            Assert.AreEqual("small", S7tImpostorRules.Refusal(false, 50, false), "large blocks only");
            Assert.AreEqual("tooBig", S7tImpostorRules.Refusal(true, S7tImpostorRules.MaxBlocks + 1, false));
            Assert.AreEqual("preview", S7tImpostorRules.Refusal(true, 10, true));

            long[] ids = { 9, 3, 7, 5 };
            double[] dist = { 3000, 1800, 1800, 4000 };
            int[] pick = new int[S7tImpostorRules.MaxAdmitsPerQuery];
            int drops;
            int k = S7tImpostorRules.Admit(ids, dist, 4, 0, pick, out drops);
            Assert.AreEqual(2, k, "two per query");
            Assert.AreEqual(1, pick[0], "nearest first, ties by id");
            Assert.AreEqual(2, pick[1]);
            Assert.AreEqual(2, drops);
            k = S7tImpostorRules.Admit(ids, dist, 4, S7tImpostorRules.MaxImpostors - 1, pick, out drops);
            Assert.AreEqual(1, k, "one place left");
            Assert.AreEqual(3, drops);
            k = S7tImpostorRules.Admit(ids, dist, 4, S7tImpostorRules.MaxImpostors, pick, out drops);
            Assert.AreEqual(0, k);
            Assert.AreEqual(4, drops);
            Assert.AreEqual(0, S7tImpostorRules.Admit(ids, dist, 0, 0, pick, out drops));
        }

        private struct P
        {
            public ulong Key;
            public uint Flags;
            public float H;
            public P(ulong k, uint f, float h = 0f) { Key = k; Flags = f; H = h; }
        }

        private static int Filter(bool impostor, Dictionary<ulong, P> held, List<P> adds, List<P> replaces, List<P> removes, List<P> recolors)
        {
            return S7tImpostorRules.Filter(impostor, held, adds, replaces, removes, recolors, p => p.Key, p => p.Flags, (h, now) => { h.H = now.H; return h; });
        }

        [TestMethod]
        public void Filter_HoldsEdgesAndSubparts_KeepsHeldCopiesCurrent_SendsAfterTheUpgrade()
        {
            uint v1 = MirrorPartFlags.Version1;
            var held = new Dictionary<ulong, P>();
            var adds = new List<P> { new P(1, v1 | MirrorPartClass.CubePart), new P(2, v1 | MirrorPartClass.Edge), new P(3, v1 | MirrorPartClass.FatBlock), new P(4, v1 | MirrorPartClass.Subpart) };
            var none = new List<P>();
            int newly = Filter(true, held, adds, new List<P>(), new List<P>(), new List<P>());
            Assert.AreEqual(2, newly);
            CollectionAssert.AreEqual(new ulong[] { 1, 3 }, adds.ConvertAll(p => p.Key), "the cube part and the fat block go, in order");
            CollectionAssert.AreEquivalent(new ulong[] { 2, 4 }, new List<ulong>(held.Keys));

            // a replace and a recolour of a held part update the held copy; a remove drops it unsent; others pass
 // (a diff names a key in one list only)
            var replaces = new List<P> { new P(2, v1 | MirrorPartClass.Edge, 0.25f), new P(1, v1 | MirrorPartClass.CubePart, 0.5f) };
            var recolors = new List<P> { new P(4, v1 | MirrorPartClass.Subpart, 0.75f), new P(3, v1 | MirrorPartClass.FatBlock, 0.1f) };
            Filter(true, held, new List<P>(), replaces, new List<P>(), recolors);
            CollectionAssert.AreEqual(new ulong[] { 1 }, replaces.ConvertAll(p => p.Key));
            CollectionAssert.AreEqual(new ulong[] { 3 }, recolors.ConvertAll(p => p.Key));
            Assert.AreEqual(0.25f, held[2].H, "the replace is in the held copy");
            Assert.AreEqual(0.75f, held[4].H, "the recolour is in the held copy");
            var removes = new List<P> { new P(2, v1 | MirrorPartClass.Edge), new P(1, v1 | MirrorPartClass.CubePart) };
            Filter(true, held, new List<P>(), new List<P>(), removes, new List<P>());
            CollectionAssert.AreEqual(new ulong[] { 1 }, removes.ConvertAll(p => p.Key), "a held part was never sent: no PartRemove; a sent one is removed");
            Assert.IsFalse(held.ContainsKey(2));

            // after the upgrade (impostor false) new edges pass; the held ones stay held until flushed
            var adds2 = new List<P> { new P(5, v1 | MirrorPartClass.Edge) };
            Assert.AreEqual(0, Filter(false, held, adds2, none, new List<P>(), new List<P>()));
            CollectionAssert.AreEqual(new ulong[] { 5 }, adds2.ConvertAll(p => p.Key));
            Assert.IsTrue(held.ContainsKey(4));
            Assert.AreEqual(0, Filter(true, null, adds2, none, none, none), "no held set: nothing to do");
        }

        [TestMethod]
        public void Compact_KeepsOrder_InPlace()
        {
            var l = new List<int> { 1, 2, 3, 4, 5, 6 };
            S7tImpostorRules.Compact(l, x => x % 2 == 0);
            CollectionAssert.AreEqual(new[] { 2, 4, 6 }, l);
            S7tImpostorRules.Compact(l, x => false);
            Assert.AreEqual(0, l.Count);
            S7tImpostorRules.Compact<int>(null, x => true);
        }
    }
}
