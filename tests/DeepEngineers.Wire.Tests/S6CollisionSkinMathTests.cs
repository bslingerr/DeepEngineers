using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// CollisionSkinMath and ShapeBudget (CONTRACT §2.6, §2.7, §5.4 "C4 type 6 SkinSet (S6)"): the lattice, the splits,
 /// the segmentation stand-in, sections, OBB placement, the refresh rule, subgrid placement, hit point frames, the
 /// character capsule, the K corner error and the per-step budget.
 /// </summary>
    [TestClass]
    public class CollisionSkinMathTests
    {
        private static readonly SeMatrix Identity = InstanceMath.Identity;

        private static SeMatrix RandomRotation(Random rnd, Vec3 t)
        {
            Vec3 up = RandomUnit(rnd), f = RandomUnit(rnd);
            f = (f - up * Vec3.Dot(f, up)).Normalized();
            return new SeMatrix(Vec3.Cross(f, up), up, -f, t);
        }

        private static Vec3 RandomUnit(Random rnd)
        {
            while (true)
            {
                var v = new Vec3(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1);
                double n = v.Length;
                if (n > 0.1 && n <= 1) return v / n;
            }
        }

        private static Quat RandomQuat(Random rnd)
        {
            return new Quat(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5).Normalized().WithNonNegativeW();
        }

        // ------------------------------------------------------------------------------------------------ lattice

        [TestMethod]
        public void S6Skin_RoundAwayAndFloorDiv()
        {
            Assert.AreEqual(1, CollisionSkinMath.RoundAway(0.5));
            Assert.AreEqual(-1, CollisionSkinMath.RoundAway(-0.5));
            Assert.AreEqual(2, CollisionSkinMath.RoundAway(1.5));
            Assert.AreEqual(-3, CollisionSkinMath.RoundAway(-2.5));
            Assert.AreEqual(0, CollisionSkinMath.RoundAway(0.49999999999999994));
            Assert.AreEqual(0, CollisionSkinMath.RoundAway(-0.0));
            for (double x = -50; x <= 50; x += 0.125)
                Assert.AreEqual((int)Math.Round(x, MidpointRounding.AwayFromZero), CollisionSkinMath.RoundAway(x), "x=" + x);
            Assert.ThrowsException<OverflowException>(() => CollisionSkinMath.RoundAway(double.NaN));
            Assert.ThrowsException<OverflowException>(() => CollisionSkinMath.RoundAway(3e9));
            Assert.AreEqual(-1, CollisionSkinMath.FloorDiv(-1, 6));
            Assert.AreEqual(-1, CollisionSkinMath.FloorDiv(-6, 6));
            Assert.AreEqual(-2, CollisionSkinMath.FloorDiv(-7, 6));
            Assert.AreEqual(0, CollisionSkinMath.FloorDiv(5, 6));
            Assert.AreEqual(1, CollisionSkinMath.FloorDiv(6, 6));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => CollisionSkinMath.FloorDiv(1, 0));
        }

        [TestMethod]
        public void S6Skin_SectionIdsAndCentres()
        {
            Assert.AreEqual(6, CollisionSkinMath.SectionCells(true));
            Assert.AreEqual(32, CollisionSkinMath.SectionCells(false));
            Assert.AreEqual(3, CollisionSkinMath.MaxBoxCells(true));
            Assert.AreEqual(16, CollisionSkinMath.MaxBoxCells(false));
            Assert.AreEqual((512u << 20) | (512u << 10) | 512u, CollisionSkinMath.SectionId(0, 0, 0));
            Assert.AreEqual(0u, CollisionSkinMath.SectionId(-512, -512, -512));
            Assert.AreEqual((1023u << 20) | (1023u << 10) | 1023u, CollisionSkinMath.SectionId(511, 511, 511));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => CollisionSkinMath.SectionId(512, 0, 0));
            var rnd = new Random(6);
            for (int i = 0; i < 2000; i++)
            {
                int x = rnd.Next(-512, 512), y = rnd.Next(-512, 512), z = rnd.Next(-512, 512);
                int a, b, c;
                CollisionSkinMath.SectionIndexOf(CollisionSkinMath.SectionId(x, y, z), out a, out b, out c);
                Assert.AreEqual(x, a); Assert.AreEqual(y, b); Assert.AreEqual(z, c);
            }
            Assert.AreEqual(new Vec3(6.25, 6.25, 6.25), CollisionSkinMath.SectionCentre(0, 0, 0, 2.5, true));
            Assert.AreEqual(new Vec3(-8.25, 7.75, 7.75), CollisionSkinMath.SectionCentre(-1, 0, 0, 0.5, false));
            Assert.AreEqual(CollisionSkinMath.SectionCentre(3, -2, 7, 2.5, true), CollisionSkinMath.SectionCentre(CollisionSkinMath.SectionId(3, -2, 7), 2.5, true));
        }

        [TestMethod]
        public void S6Skin_SectionIndexOfBoxCentres()
        {
            int x, y, z;
            Assert.IsTrue(CollisionSkinMath.TrySectionIndex(new Vec3(0, 0, 0), 2.5, true, out x, out y, out z));
            Assert.AreEqual(0, x);
            // cell 5 is the last of section 0, cell 6 the first of section 1; cell -1 is section -1
            Assert.IsTrue(CollisionSkinMath.TrySectionIndex(new Vec3(5 * 2.5, 6 * 2.5, -1 * 2.5), 2.5, true, out x, out y, out z));
            Assert.AreEqual(0, x); Assert.AreEqual(1, y); Assert.AreEqual(-1, z);
            // half way between cells rounds away from zero (as VRage Vector3I.Round)
            Assert.IsTrue(CollisionSkinMath.TrySectionIndex(new Vec3(5.5 * 2.5, -0.5 * 2.5, 0), 2.5, true, out x, out y, out z));
            Assert.AreEqual(1, x); Assert.AreEqual(-1, y);
            // range ±512 sections: large to +7.68 km
            Assert.IsTrue(CollisionSkinMath.TrySectionIndex(new Vec3(3071 * 2.5, 0, 0), 2.5, true, out x, out y, out z));
            Assert.AreEqual(511, x);
            Assert.IsFalse(CollisionSkinMath.TrySectionIndex(new Vec3(3072 * 2.5, 0, 0), 2.5, true, out x, out y, out z));
            Assert.IsTrue(CollisionSkinMath.TrySectionIndex(new Vec3(-16384 * 0.5, 0, 0), 0.5, false, out x, out y, out z));
            Assert.AreEqual(-512, x);
            Assert.IsFalse(CollisionSkinMath.TrySectionIndex(new Vec3(-16385 * 0.5, 0, 0), 0.5, false, out x, out y, out z));
            Assert.IsFalse(CollisionSkinMath.TrySectionIndex(new Vec3(double.NaN, 0, 0), 0.5, false, out x, out y, out z));
            Assert.IsFalse(CollisionSkinMath.TrySectionIndex(new Vec3(1e300, 0, 0), 0.5, false, out x, out y, out z));
            Assert.IsFalse(CollisionSkinMath.TrySectionIndex(new Vec3(0, 0, 0), 0.0, false, out x, out y, out z));
        }

        // ------------------------------------------------------------------------------------------------ splits

        [TestMethod]
        public void S6Skin_SplitSegmentAtLatticeAndMaxCells()
        {
            var o = new List<CellRange>();
            CollisionSkinMath.SplitSegment(new CellRange(0, 0, 0, 6, 0, 0), true, o);
            CollectionAssert.AreEqual(new[] { new CellRange(0, 0, 0, 2, 0, 0), new CellRange(3, 0, 0, 5, 0, 0), CellRange.Cell(6, 0, 0) }, o);
            o.Clear();
            CollisionSkinMath.SplitSegment(new CellRange(0, 0, 0, 4, 0, 0), true, o);
            CollectionAssert.AreEqual(new[] { new CellRange(0, 0, 0, 2, 0, 0), new CellRange(3, 0, 0, 4, 0, 0) }, o, "larger piece first");
            o.Clear();
            CollisionSkinMath.SplitSegment(new CellRange(-1, 0, 0, 5, 0, 0), true, o);
            CollectionAssert.AreEqual(new[] { CellRange.Cell(-1, 0, 0), new CellRange(0, 0, 0, 2, 0, 0), new CellRange(3, 0, 0, 5, 0, 0) }, o);
            o.Clear();
            CollisionSkinMath.SplitSegment(new CellRange(0, 0, 0, 31, 0, 0), false, o);
            CollectionAssert.AreEqual(new[] { new CellRange(0, 0, 0, 15, 0, 0), new CellRange(16, 0, 0, 31, 0, 0) }, o);
            o.Clear();
            CollisionSkinMath.SplitSegment(new CellRange(0, 0, 0, 1, 1, 1), true, o);
            Assert.AreEqual(1, o.Count);
            Assert.ThrowsException<ArgumentException>(() => CollisionSkinMath.SplitSegment(new CellRange(1, 0, 0, 0, 0, 0), true, o));

            var rnd = new Random(61);
            for (int k = 0; k < 300; k++)
            {
                bool large = rnd.Next(2) == 0;
                int s = CollisionSkinMath.SectionCells(large), m = CollisionSkinMath.MaxBoxCells(large);
                var seg = new CellRange(rnd.Next(-90, 90), rnd.Next(-90, 90), rnd.Next(-90, 90), 0, 0, 0);
                seg.MaxX = seg.MinX + rnd.Next(0, 70); seg.MaxY = seg.MinY + rnd.Next(0, 70); seg.MaxZ = seg.MinZ + rnd.Next(0, 70);
                o.Clear();
                CollisionSkinMath.SplitSegment(seg, large, o);
                Assert.AreEqual(seg.CellCount, o.Sum(p => p.CellCount), "pieces cover the segment exactly");
                foreach (CellRange p in o)
                {
                    Assert.IsTrue(p.SizeX <= m && p.SizeY <= m && p.SizeZ <= m, "≤ max cells");
                    Assert.AreEqual(CollisionSkinMath.FloorDiv(p.MinX, s), CollisionSkinMath.FloorDiv(p.MaxX, s), "inside one lattice cell");
                    Assert.AreEqual(CollisionSkinMath.FloorDiv(p.MinY, s), CollisionSkinMath.FloorDiv(p.MaxY, s));
                    Assert.AreEqual(CollisionSkinMath.FloorDiv(p.MinZ, s), CollisionSkinMath.FloorDiv(p.MaxZ, s));
                    Assert.IsTrue(seg.Contains(p.MinX, p.MinY, p.MinZ) && seg.Contains(p.MaxX, p.MaxY, p.MaxZ));
                }
                for (int i = 1; i < o.Count; i++)
                {
                    CellRange a = o[i - 1], b = o[i];
                    int c = a.MinZ != b.MinZ ? a.MinZ.CompareTo(b.MinZ) : a.MinY != b.MinY ? a.MinY.CompareTo(b.MinY) : a.MinX.CompareTo(b.MinX);
                    Assert.IsTrue(c < 0, "z, y, x order");
                }
            }
        }

        [TestMethod]
        public void S6Skin_CellBox()
        {
            SkinBoxData b = CollisionSkinMath.CellBox(new CellRange(0, 0, 0, 2, 2, 2), 2.5);
            Assert.AreEqual(new Vec3(2.5, 2.5, 2.5), b.Centre);
            Assert.AreEqual(new Vec3(3.75, 3.75, 3.75), b.Half);
            Assert.IsTrue(b.HasIdentityAxes);
            Assert.AreEqual(0u, b.Flags);
            Assert.AreEqual(CombatConventions.SkinBoxSource.Segment, b.Source);
            b = CollisionSkinMath.CellBox(new CellRange(-3, 1, 0, -3, 1, 15), 0.5);
            Assert.AreEqual(new Vec3(-1.5, 0.5, 3.75), b.Centre);
            Assert.AreEqual(new Vec3(0.25, 0.25, 4.0), b.Half);
        }

        [TestMethod]
        public void S6Skin_ObbFromModelAabb()
        {
            var noRot = new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, Vec3.Zero);
            SkinBoxData b = CollisionSkinMath.ObbFromModelAabb(new Vec3(-1, -2, -3), new Vec3(1, 2, 5), noRot, new Vec3(10, 0, 0), 2.0);
            Assert.AreEqual(new Vec3(10, 0, 2), b.Centre);
            Assert.AreEqual(new Vec3(2, 4, 8), b.Half);
            Assert.IsTrue(b.HasIdentityAxes);
            Assert.IsFalse(b.IsRotated);
            Assert.AreEqual(CombatConventions.SkinBoxSource.ModelShapeObb, b.Source);

            // a 90° orientation (Right = -z, Up = y, Backward = x): grid-aligned, half extents permuted
            var turned = new SeMatrix(new Vec3(0, 0, -1), Vec3.UnitY, Vec3.UnitX, Vec3.Zero);
            b = CollisionSkinMath.ObbFromModelAabb(new Vec3(-1, -2, -3), new Vec3(1, 2, 3), turned, new Vec3(0, 0, 0), 1.0);
            Assert.AreEqual(new Vec3(3, 2, 1), b.Half);
            Assert.IsTrue(b.HasIdentityAxes && !b.IsRotated);

            // flat shape: the half extent is floored at 5 mm
            b = CollisionSkinMath.ObbFromModelAabb(new Vec3(-1, 0, -1), new Vec3(1, 0, 1), noRot, Vec3.Zero, 1.0);
            Assert.AreEqual(CollisionSkinMath.MinObbHalfExtent, b.Half.Y);

            // random rotation: the OBB's corners are the transformed model AABB's corners
            var rnd = new Random(7);
            for (int k = 0; k < 200; k++)
            {
                SeMatrix r = RandomRotation(rnd, Vec3.Zero);
                var mn = new Vec3(rnd.NextDouble() * -3, rnd.NextDouble() * -3, rnd.NextDouble() * -3);
                var mx = new Vec3(rnd.NextDouble() * 3 + 0.1, rnd.NextDouble() * 3 + 0.1, rnd.NextDouble() * 3 + 0.1);
                var t = new Vec3(rnd.NextDouble() * 20 - 10, rnd.NextDouble() * 20 - 10, rnd.NextDouble() * 20 - 10);
                double scale = 0.5 + rnd.NextDouble() * 2;
                b = CollisionSkinMath.ObbFromModelAabb(mn, mx, r, t, scale);
                string err;
                Assert.IsTrue(SkinCodec.IsValidBox(b, out err) || b.Half.X > SkinCodec.MaxHalfAccepted || b.Half.Y > SkinCodec.MaxHalfAccepted || b.Half.Z > SkinCodec.MaxHalfAccepted, err);
                Assert.IsTrue(b.IsRotated);
                for (int c = 0; c < 8; c++)
                {
                    var p = new Vec3((c & 1) != 0 ? mx.X : mn.X, (c & 2) != 0 ? mx.Y : mn.Y, (c & 4) != 0 ? mx.Z : mn.Z);
                    Vec3 placed = t + InstanceMath.TransformDir(r, p * scale);
                    Vec3 obb = b.PointAt((c & 1) != 0 ? 1 : -1, (c & 2) != 0 ? 1 : -1, (c & 4) != 0 ? 1 : -1);
                    Assert.IsTrue((placed - obb).Length < 1e-9, "corner " + c);
                }
            }
            Assert.ThrowsException<ArgumentException>(() => CollisionSkinMath.ObbFromModelAabb(new Vec3(double.NaN, 0, 0), Vec3.Zero, noRot, Vec3.Zero, 1));
            Assert.ThrowsException<ArgumentException>(() => CollisionSkinMath.ObbFromModelAabb(Vec3.Zero, Vec3.Zero, noRot, Vec3.Zero, 0));
        }

        [TestMethod]
        public void S6Skin_SplitObb()
        {
            var rnd = new Random(8);
            SeMatrix r = RandomRotation(rnd, Vec3.Zero);
            SkinBoxData b = CollisionSkinMath.ObbFromModelAabb(new Vec3(-1, -5, -10), new Vec3(1, 5, 10), r, new Vec3(1, 2, 3), 1.0);
            var o = new List<SkinBoxData>();
            CollisionSkinMath.SplitObb(b, 4.0, o);
            Assert.AreEqual(1 * 2 * 3, o.Count);
            double volume = 0;
            Vec3 mean = Vec3.Zero;
            foreach (SkinBoxData p in o)
            {
                Assert.IsTrue(p.Half.X <= 4.0 && p.Half.Y <= 4.0 && p.Half.Z <= 4.0);
                Assert.AreEqual(b.Flags, p.Flags);
                Assert.AreEqual(b.E0, p.E0);
                volume += p.Half.X * p.Half.Y * p.Half.Z;
                mean = mean + p.Centre / o.Count;
            }
            Assert.AreEqual(b.Half.X * b.Half.Y * b.Half.Z, volume, 1e-9);
            Assert.IsTrue((mean - b.Centre).Length < 1e-9);
            o.Clear();
            CollisionSkinMath.SplitObb(CollisionSkinMath.CellBox(new CellRange(0, 0, 0, 2, 2, 2), 2.5), 4.0, o);
            Assert.AreEqual(1, o.Count, "3.75 m stays whole");
            o.Clear();
            SkinBoxData exact = SkinBoxData.Aligned(Vec3.Zero, new Vec3(4.0, 8.0, 4.0000001), 0);
            CollisionSkinMath.SplitObb(exact, 4.0, o);
            Assert.AreEqual(1 * 2 * 2, o.Count);
        }

        // ------------------------------------------------------------------------------------------------ segmentation stand-in

        [TestMethod]
        public void S6Skin_MergeCellsGreedyCoversExactly()
        {
            var rnd = new Random(9);
            for (int k = 0; k < 200; k++)
            {
                var cells = new List<I32x3>();
                int n = rnd.Next(1, 500);
                for (int i = 0; i < n; i++) cells.Add(new I32x3(rnd.Next(-8, 8), rnd.Next(-4, 4), rnd.Next(-8, 8)));
                var boxes = new List<CellRange>();
                CollisionSkinMath.MergeCellsGreedy(cells, boxes);
                string err;
                Assert.IsTrue(CollisionSkinMath.CheckCoverage(cells, boxes, out err), err);
                Assert.IsNull(err);
                var again = new List<CellRange>();
                var shuffled = cells.OrderBy(c => rnd.Next()).ToList();
                CollisionSkinMath.MergeCellsGreedy(shuffled, again);
                CollectionAssert.AreEqual(boxes, again, "deterministic for any input order");
            }
            // a full block merges to one box
            var block = new List<I32x3>();
            for (int x = 0; x < 4; x++) for (int y = 0; y < 3; y++) for (int z = 0; z < 5; z++) block.Add(new I32x3(x, y, z));
            var one = new List<CellRange>();
            CollisionSkinMath.MergeCellsGreedy(block, one);
            CollectionAssert.AreEqual(new[] { new CellRange(0, 0, 0, 3, 2, 4) }, one);
        }

        [TestMethod]
        public void S6Skin_CheckCoverageFindsDefects()
        {
            var cells = new List<I32x3> { new I32x3(0, 0, 0), new I32x3(1, 0, 0), new I32x3(2, 0, 0) };
            string err;
            Assert.IsFalse(CollisionSkinMath.CheckCoverage(cells, new[] { new CellRange(0, 0, 0, 1, 0, 0) }, out err));
            StringAssert.Contains(err, "not covered");
            Assert.IsFalse(CollisionSkinMath.CheckCoverage(cells, new[] { new CellRange(0, 0, 0, 1, 0, 0), new CellRange(1, 0, 0, 2, 0, 0) }, out err));
            StringAssert.Contains(err, "overlap");
            Assert.IsFalse(CollisionSkinMath.CheckCoverage(cells, new[] { new CellRange(0, 0, 0, 3, 0, 0) }, out err));
            StringAssert.Contains(err, "outside");
            Assert.IsTrue(CollisionSkinMath.CheckCoverage(cells, new[] { new CellRange(0, 0, 0, 2, 0, 0) }, out err));
        }

        // ------------------------------------------------------------------------------------------------ sections

        [TestMethod]
        public void S6Skin_BuildSectionsInvariants()
        {
            var rnd = new Random(10);
            for (int k = 0; k < 40; k++)
            {
                bool large = rnd.Next(2) == 0;
                double g = large ? 2.5 : 0.5;
                int s = CollisionSkinMath.SectionCells(large);
                var cells = new List<I32x3>();
                int n = rnd.Next(1, 800);
                for (int i = 0; i < n; i++) cells.Add(new I32x3(rnd.Next(-40, 40), rnd.Next(-6, 6), rnd.Next(-40, 40)));
                var segs = new List<CellRange>();
                CollisionSkinMath.MergeCellsGreedy(cells, segs);
                var obbs = new List<SkinBoxData>();
                for (int i = 0; i < rnd.Next(0, 6); i++)
                    obbs.Add(CollisionSkinMath.ObbFromModelAabb(new Vec3(-1, -1, -6), new Vec3(1, 1, 6), RandomRotation(rnd, Vec3.Zero),
                        new Vec3(rnd.NextDouble() * 60 - 30, rnd.NextDouble() * 10 - 5, rnd.NextDouble() * 60 - 30), 1.0));
                int dropped;
                List<SkinSectionData> secs = CollisionSkinMath.BuildSections(segs, obbs, g, large, out dropped);
                Assert.AreEqual(0, dropped);
                long cellCount = 0;
                int obbPieces = 0;
                for (int i = 0; i < secs.Count; i++)
                {
                    SkinSectionData sec = secs[i];
                    if (i > 0) Assert.IsTrue(sec.SectionId > secs[i - 1].SectionId, "ascending ids");
                    Assert.AreEqual(SkinSectionKind.Grid, sec.Kind);
                    Assert.AreEqual(CollisionSkinMath.SectionCentre(sec.SectionId, g, large), sec.Centre);
                    Assert.AreEqual(CollisionSkinMath.SectionContentHash(sec), sec.ContentHash);
                    Assert.IsTrue(sec.Boxes.Count >= 1);
                    bool seenObb = false;
                    SkinBoxData prev = default(SkinBoxData);
                    bool havePrev = false;
                    foreach (SkinBoxData b in sec.Boxes)
                    {
                        string err;
                        Assert.IsTrue(SkinCodec.IsValidBox(b, out err), err);
                        int ix, iy, iz;
                        Assert.IsTrue(CollisionSkinMath.TrySectionIndex(b.Centre, g, large, out ix, out iy, out iz));
                        Assert.AreEqual(sec.SectionId, CollisionSkinMath.SectionId(ix, iy, iz), "a box belongs to the section of its centre");
                        if (b.Source == CombatConventions.SkinBoxSource.Segment)
                        {
                            Assert.IsFalse(seenObb, "Segment boxes come first");
                            double halfSec = s * g / 2.0;
                            for (int a = 0; a < 3; a++)
                                Assert.IsTrue(Math.Abs(b.Centre[a] - sec.Centre[a]) + b.Half[a] <= halfSec + 1e-9, "cell box inside the section cube");
                            cellCount += (long)Math.Round(8 * b.Half.X * b.Half.Y * b.Half.Z / (g * g * g));
                            if (havePrev)
                            {
                                Vec3 pm = prev.Centre - prev.Half, bm = b.Centre - b.Half;
                                int c = pm.Z != bm.Z ? pm.Z.CompareTo(bm.Z) : pm.Y != bm.Y ? pm.Y.CompareTo(bm.Y) : pm.X.CompareTo(bm.X);
                                Assert.IsTrue(c < 0, "Segment boxes by minimum cell (z, y, x)");
                            }
                            prev = b;
                            havePrev = true;
                        }
                        else
                        {
                            seenObb = true;
                            obbPieces++;
                        }
                    }
                }
                Assert.AreEqual(cells.Select(c => Tuple.Create(c.x, c.y, c.z)).Distinct().Count(), cellCount, "every cell once");
                Assert.AreEqual(obbs.Count * 2, obbPieces, "6 m half extents split in two");
            }
        }

        [TestMethod]
        public void S6Skin_BuildSectionsDropsOutOfRange()
        {
            int dropped;
            List<SkinSectionData> secs = CollisionSkinMath.BuildSections(new[] { new CellRange(3060, 0, 0, 3079, 0, 0) }, null, 2.5, true, out dropped);
            Assert.AreEqual(3, dropped);
            Assert.AreEqual(2, secs.Count);
            Assert.AreEqual(0, CollisionSkinMath.BuildSections(null, null, 2.5, true).Count);
        }

        [TestMethod]
        public void S6Skin_RhoMax()
        {
            var sec = new SkinSectionData { Centre = new Vec3(6.25, 6.25, 6.25) };
            Assert.AreEqual(0.0, CollisionSkinMath.RhoMax(sec));
            sec.Boxes.Add(SkinBoxData.Aligned(new Vec3(6.25, 6.25, 6.25), new Vec3(4, 4, 4), 0));
            Assert.AreEqual(Math.Sqrt(48), CollisionSkinMath.RhoMax(sec), 1e-12);
            sec.Boxes.Add(SkinBoxData.Aligned(new Vec3(9.25, 6.25, 2.25), new Vec3(1, 2, 2), 0));
            Assert.AreEqual(5 + 3, CollisionSkinMath.RhoMax(sec), 1e-12);
        }

        // ------------------------------------------------------------------------------------------------ Subnautica side

        [TestMethod]
        public void S6Skin_RefreshRule()
        {
            // CONTRACT 2.6: a 1,000 m depth change moves each face of a 4 m box by 6.8 cm: refreshed
            double rs = 60000.00390625, s0 = 1.0, s1 = rs / (rs - 1000.0);
            Assert.AreEqual(0.0678, (s1 - s0) * 4.0, 1e-3);
            var one = new SkinSectionData { Centre = Vec3.Zero };
            one.Boxes.Add(SkinBoxData.Aligned(Vec3.Zero, new Vec3(4, 4, 4), 0));
            double rho = CollisionSkinMath.RhoMax(one);
            Assert.IsTrue(CollisionSkinMath.NeedsRefresh(s0, Vec3.UnitY, s1, Vec3.UnitY, rho));
            Assert.IsFalse(CollisionSkinMath.NeedsRefresh(1.02, Vec3.UnitY, 1.02 + 1e-5, Vec3.UnitY, rho));
            // exactly 1 cm is not "exceeds"
            Assert.IsFalse(CollisionSkinMath.NeedsRefresh(1.0, Vec3.UnitY, 1.001, Vec3.UnitY, 10.0 - 1e-9));
            // a turn: (s - 1)*|Δu|*ρ
            Vec3 u2 = new Vec3(Math.Sin(0.1), Math.Cos(0.1), 0);
            double du = (u2 - Vec3.UnitY).Length;
            Assert.IsTrue(CollisionSkinMath.NeedsRefresh(1.03, Vec3.UnitY, 1.03, u2, 0.0101 / (0.03 * du)));
            Assert.IsFalse(CollisionSkinMath.NeedsRefresh(1.03, Vec3.UnitY, 1.03, u2, 0.0099 / (0.03 * du)));
            // level: s = 1 never refreshes on a turn
            Assert.IsFalse(CollisionSkinMath.NeedsRefresh(1.0, Vec3.UnitY, 1.0, Vec3.UnitX, 100));
            Quat q = Quat.LookRotation(new Vec3(0, 0, 1), new Vec3(0.3, 1, 0));
            Assert.IsTrue((CollisionSkinMath.UpInSection(q) - q.Conjugate().Rotate(Vec3.UnitY)).Length < 1e-15);
            Assert.IsTrue((CollisionSkinMath.UpInSection(Quat.Identity) - Vec3.UnitY).Length < 1e-15);
        }

        [TestMethod]
        public void S6Skin_SubgridPlacementEqualsCarryingBoxesIntoTheRootFrame()
        {
            var rnd = new Random(11);
            Assert.AreEqual(0.0, Quat.RotationDistance(Quat.Identity, CollisionSkinMath.SubgridRotation(Identity)), 1e-15);
            for (int k = 0; k < 500; k++)
            {
                var o = new Vec3(rnd.NextDouble() * 4000 - 2000, rnd.NextDouble() * -2000, rnd.NextDouble() * 4000 - 2000);
                Quat q = RandomQuat(rnd);
                double s = 0.99 + rnd.NextDouble() * 0.07;
                SeMatrix rel = RandomRotation(rnd, new Vec3(rnd.NextDouble() * 40 - 20, rnd.NextDouble() * 10 - 5, rnd.NextDouble() * 40 - 20));
                var cSec = new Vec3(rnd.NextDouble() * 40 - 20, rnd.NextDouble() * 10 - 5, rnd.NextDouble() * 40 - 20);
                Vec3 h = new Vec3(0.5 + rnd.NextDouble() * 3.5, 0.5 + rnd.NextDouble() * 3.5, 0.5 + rnd.NextDouble() * 3.5);
                Vec3 b = cSec + new Vec3(rnd.NextDouble() * 16 - 8, rnd.NextDouble() * 16 - 8, rnd.NextDouble() * 16 - 8);
                Vec3 root = CollisionSkinMath.SectionRootPosition(o, q, s, rel, cSec);
                Quat rq = CollisionSkinMath.SectionRootRotation(q, rel);
                Assert.IsTrue(rq.W >= 0);
                ProxyBox pb = CollisionSkinMath.Prescale(rq, s, cSec, SkinBoxData.Aligned(b, h, 0));
                // the box centre lands where the drawn subgrid puts it
                Vec3 placed = root + rq.Rotate(pb.Centre);
                Vec3 exact = WireMath.Apply(o, q, s, rel.Transform(b));
                Assert.IsTrue((placed - exact).Length < 1e-8, "centre " + (placed - exact).Length);
                // a rotated box of the subgrid: its corners deviate from the drawn ones by exactly the predicted K error
                SeMatrix ax = RandomRotation(rnd, Vec3.Zero);
                var rb = new SkinBoxData { Centre = b, Half = h, E0 = ax.Right, E1 = ax.Up, E2 = ax.Backward, Flags = CombatConventions.SkinBoxFlags.Rotated };
                ProxyBox rp = CollisionSkinMath.Prescale(rq, s, cSec, rb);
                double worstCorner = 0;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vec3 drawn = WireMath.Apply(o, q, s, rel.Transform(rb.PointAt(sx, sy, sz)));
                            worstCorner = Math.Max(worstCorner, (root + rq.Rotate(rp.CornerSectionLocal(sx, sy, sz)) - drawn).Length);
                        }
                Assert.AreEqual(CollisionSkinMath.PrescaleCornerError(rq, s, h, ax.Right, ax.Up, ax.Backward), worstCorner, 1e-8);
                // root grid: rel = identity
                Assert.IsTrue((CollisionSkinMath.SectionRootPosition(o, q, s, Identity, cSec) - WireMath.Apply(o, q, s, cSec)).Length < 1e-9);
                Assert.IsTrue(Quat.RotationDistance(CollisionSkinMath.SectionRootRotation(q, Identity), q) < 1e-12);
                // the subgrid rotation is the S5 conjugation of rel
                Assert.IsTrue(Quat.RotationDistance(CollisionSkinMath.SubgridRotation(rel), InstanceMath.ToUnity(rel).Rotation) < 1e-12);
            }
        }

        [TestMethod]
        public void S6Skin_HitLocalRoundTrip()
        {
            var rnd = new Random(12);
            for (int k = 0; k < 500; k++)
            {
                var o = new Vec3(rnd.NextDouble() * 4000 - 2000, rnd.NextDouble() * -2000, rnd.NextDouble() * 4000 - 2000);
                Quat q = RandomQuat(rnd);
                double s = 0.99 + rnd.NextDouble() * 0.07;
                bool sub = k % 2 == 0;
                SeMatrix rel = sub ? RandomRotation(rnd, new Vec3(rnd.NextDouble() * 40 - 20, 3, rnd.NextDouble() * 40 - 20)) : Identity;
                var l = new Vec3(rnd.NextDouble() * 60 - 30, rnd.NextDouble() * 20 - 10, rnd.NextDouble() * 60 - 30);
                Vec3 w = CollisionSkinMath.LocalToWorld(o, q, s, rel, sub, l);
                Assert.IsTrue((CollisionSkinMath.HitLocal(o, q, s, rel, sub, w) - l).Length < 1e-9);
                // S6.M11: leaving a subgrid hit in the root frame is a different point (by |l - rel·l|)
                if (sub) Assert.IsTrue((WireMath.ToLocal(o, q, s, w) - l).Length > 1e-3);
            }
        }

        [TestMethod]
        public void S6Skin_CharacterCapsule()
        {
            Vec3 c, a;
            CollisionSkinMath.CharacterCapsule(null, true, out c, out a);
            Assert.AreEqual(new Vec3(0, 0.9, 0), c);
            Assert.AreEqual(Vec3.UnitY, a);
            var rows = new float[78 * 12];
            int p = MirrorSkeleton.Pelvis * 12 + 9, h = MirrorSkeleton.Head * 12 + 9;
            rows[p] = 0.1f; rows[p + 1] = 1.0f; rows[p + 2] = -0.05f;
            rows[h] = 0.1f; rows[h + 1] = 1.7f; rows[h + 2] = 0.05f;
            CollisionSkinMath.CharacterCapsule(rows, true, out c, out a);
            Assert.AreEqual(new Vec3(0.1f, 1.0f, -0.05f), c);
            Vec3 d = new Vec3(0.1f, 1.7f, 0.05f) - c;
            Assert.IsTrue((a - d / d.Length).Length < 1e-12);
            CollisionSkinMath.CharacterCapsule(rows, false, out c, out a);
            Assert.AreEqual(new Vec3(0, 0.9, 0), c, "bones not valid");
            rows[h] = rows[p]; rows[h + 1] = rows[p + 1]; rows[h + 2] = rows[p + 2];
            CollisionSkinMath.CharacterCapsule(rows, true, out c, out a);
            Assert.AreEqual(Vec3.UnitY, a, "head on the pelvis: fallback");
            CollisionSkinMath.CharacterCapsule(new float[12 * 13], true, out c, out a);
            Assert.AreEqual(new Vec3(0, 0.9, 0), c, "too few bones");
            Assert.AreEqual(0.25, CollisionSkinMath.CapsuleRadius);
            Assert.AreEqual(1.8, CollisionSkinMath.CapsuleHeight);

            var rnd = new Random(13);
            for (int k = 0; k < 200; k++)
            {
                Quat q = RandomQuat(rnd);
                var o = new Vec3(rnd.NextDouble() * 100, -rnd.NextDouble() * 100, rnd.NextDouble() * 100);
                double s = 1 + rnd.NextDouble() * 0.05;
                Vec3 axis = RandomUnit(rnd);
                var centre = new Vec3(0, 1, 0);
                Vec3 cw;
                Quat qw;
                CollisionSkinMath.CharacterCapsuleWorld(o, q, s, centre, axis, out cw, out qw);
                Assert.IsTrue((cw - WireMath.Apply(o, q, s, centre)).Length < 1e-9);
                Vec3 want = WireMath.ApplyDir(q, s, axis).Normalized();
                Assert.IsTrue((qw.Rotate(Vec3.UnitY) - want).Length < 1e-9, "capsule Y along the mapped axis");
                Assert.IsTrue(qw.W >= 0);
            }
        }

        [TestMethod]
        public void S6Skin_PrescaleCornerError()
        {
            var rnd = new Random(14);
            for (int k = 0; k < 300; k++)
            {
                double s = 0.99 + rnd.NextDouble() * 0.06;
                var h = new Vec3(0.5 + rnd.NextDouble() * 3.5, 0.5 + rnd.NextDouble() * 3.5, 0.5 + rnd.NextDouble() * 3.5);
                // level hull (yaw only) with level/vertical axes: exact
                Quat yaw = Quat.LookRotation(new Vec3(Math.Sin(k), 0, Math.Cos(k)), Vec3.UnitY);
                Assert.IsTrue(CollisionSkinMath.PrescaleCornerError(yaw, s, h, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ) < 1e-12);
                Assert.IsTrue(Math.Abs(CollisionSkinMath.TiltDegrees(yaw)) < 1e-6);
                // anything else: within (s - 1)*|h| (CONTRACT 2.6)
                Quat q = RandomQuat(rnd);
                SeMatrix ax = RandomRotation(rnd, Vec3.Zero);
                double e = CollisionSkinMath.PrescaleCornerError(q, s, h, ax.Right, ax.Up, ax.Backward);
                Assert.IsTrue(e <= Math.Abs(s - 1) * h.Length * (1 + 1e-9) + 1e-12, e + " vs " + Math.Abs(s - 1) * h.Length);
                Assert.IsTrue(CollisionSkinMath.PrescaleCornerError(q, 1.0, h, ax.Right, ax.Up, ax.Backward) < 1e-12, "s = 1: rigid, exact");
            }
            Assert.AreEqual(90.0, CollisionSkinMath.TiltDegrees(Quat.LookRotation(Vec3.UnitY, Vec3.UnitX)), 1e-9);
        }

        // ------------------------------------------------------------------------------------------------ budget

        [TestMethod]
        public void S6Budget_RoundRobin()
        {
            var pending = new[] { 1000 };
            var grant = new int[1];
            int cursor = 0, steps = 0;
            while (pending[0] > 0)
            {
                cursor = ShapeBudget.Plan(pending, 1, cursor, CombatConventions.Limits.MaxShapesPerStep, grant);
                pending[0] -= grant[0];
                steps++;
            }
            Assert.AreEqual(4, steps, "a 1,000-box skin takes 4 steps");

            pending = new[] { 300, 0, 50, 10 };
            grant = new int[4];
            cursor = ShapeBudget.Plan(pending, 4, 0, 256, grant);
            CollectionAssert.AreEqual(new[] { 256, 0, 0, 0 }, grant);
            Assert.AreEqual(0, cursor, "cut short: resume the same item");
            pending[0] -= 256;
            cursor = ShapeBudget.Plan(pending, 4, cursor, 256, grant);
            CollectionAssert.AreEqual(new[] { 44, 0, 50, 10 }, grant);
            Assert.AreEqual(0, cursor, "after item 3 comes item 0");
            Assert.AreEqual(2, ShapeBudget.Plan(new[] { 0, 0, 0 }, 3, 2, 256, grant), "nothing pending: cursor kept");
            Assert.AreEqual(0, ShapeBudget.Plan(new int[0], 0, 5, 256, new int[0]));
            cursor = ShapeBudget.Plan(new[] { 5, 5, 5 }, 3, 7, 4, grant);
            CollectionAssert.AreEqual(new[] { 0, 4, 0 }, grant.Take(3).ToArray());
            Assert.AreEqual(1, cursor);

            // fairness: with a big section first, every item is served within ⌈total/256⌉ steps
            var rnd = new Random(15);
            for (int k = 0; k < 100; k++)
            {
                int n = rnd.Next(1, 12);
                int[] p = Enumerable.Range(0, n).Select(_ => rnd.Next(0, 600)).ToArray();
                int total = p.Sum();
                var g = new int[n];
                int c = rnd.Next(0, 30), st = 0;
                while (p.Sum() > 0)
                {
                    c = ShapeBudget.Plan(p, n, c, 256, g);
                    Assert.IsTrue(g.Sum() <= 256);
                    Assert.IsTrue(g.Sum() == Math.Min(256, p.Sum()), "the budget is used whenever work is pending");
                    for (int i = 0; i < n; i++) p[i] -= g[i];
                    st++;
                }
                Assert.AreEqual((total + 255) / 256, st);
            }
        }
    }
}
