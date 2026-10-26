using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// src/DeepEngineers.Wire/S8aOreOverlay.cs) and the cover rule
 /// (S8aCraterOreRule.Covered): every vein voxel keeps 2 m of the real map above it; the map's surface voxels within 5 m of a vein
 /// take its Subnautica type (types only, idempotent), and lose it when the base no longer holds the vein; S8a.M31 writes nothing.
 /// The map here is a flat seabed at SN y = -50 (solid below).
 /// </summary>
    [TestClass]
    public class S8aOreOverlayTests
    {
        private const double SeabedY = -50.0;
        private const byte Rock = 84;

        private static byte[] Materials() { return new byte[] { 31, 32, 33, 34, 35, 36, 37, 38, 39 }; }

        private static S8aCraterOreRule PlaneRule(bool cube, out int solidCalls)
        {
            var rule = new S8aCraterOreRule(Materials()) { CubeRule = cube };
            rule.SetDeposits(new[] { new S8aCraterDeposit("DrillableGold", 100.0, SeabedY, 200.0) });
            int calls = 0;
            rule.SolidAt = (x, y, z) => { calls++; return y < SeabedY; };
            rule.AllSolidIn = (lx, ly, lz, hx, hy, hz) => hy < SeabedY;
            solidCalls = 0;
            return rule;
        }

        [TestMethod]
        public void Cover_KeepsTwoMetresOfTheMapAboveEveryVeinVoxel()
        {
            int unused;
            S8aCraterOreRule rule = PlaneRule(false, out unused);
            int gold = S8aCraterOres.RowOf("Gold_01");
            double cy = SeabedY - S8aCraterOres.DepositSinkM;
            Assert.AreEqual(S8aCraterOreRule.DefaultMinCoverM, rule.MinCoverM);
            Assert.AreEqual(gold, rule.RowAt(100.0, cy, 200.0), "3 m under the seabed: covered");
            Assert.AreEqual(gold, rule.RowAt(100.0, SeabedY - 2.2, 200.0), "2.2 m under: covered");
            Assert.AreEqual(-1, rule.RowAt(100.0, SeabedY - 1.5, 200.0), "1.5 m under: inside the pocket, within the cover");
            Assert.AreEqual(-1, rule.RowAt(100.0, SeabedY + 1.0, 200.0), "above the seabed");
            Assert.AreEqual(-1, rule.RowAt(100.0 + 5.9, SeabedY - 1.0, 200.0), "the pocket's rim near the surface");
            Assert.IsTrue(rule.CoverDropped >= 2, "the dropped voxels are counted");
            // no map, no cover: the rule as before
            var old = new S8aCraterOreRule(Materials()) { CubeRule = false };
            old.SetDeposits(new[] { new S8aCraterDeposit("DrillableGold", 100.0, SeabedY, 200.0) });
            Assert.AreEqual(gold, old.RowAt(100.0, SeabedY - 1.5, 200.0), "no SolidAt: no cover rule");
            // a pocket the map proves deep needs no probe
            int probes = 0;
            var deep = new S8aCraterOreRule(Materials()) { CubeRule = false };
            deep.SetDeposits(new[] { new S8aCraterDeposit("DrillableGold", 100.0, -300.0, 200.0) });
            deep.SolidAt = (x, y, z) => { probes++; return y < SeabedY; };
            deep.AllSolidIn = (lx, ly, lz, hx, hy, hz) => hy < SeabedY;
            Assert.AreEqual(gold, deep.RowAt(100.0, -303.0, 200.0));
            Assert.AreEqual(gold, deep.RowAt(101.0, -304.0, 200.0));
            Assert.AreEqual(0, probes, "deep pockets are answered by the box test (cached)");
            int g0 = deep.Generation;
            deep.MinCoverM = 3.0;
            Assert.AreNotEqual(g0, deep.Generation, "a cover change is a new generation");
        }

        [TestMethod]
        public void Rows_EqualsRowAtEverywhere()
        {
            int unused;
            S8aCraterOreRule rule = PlaneRule(true, out unused);
            int vx0 = 100 + 2048 - 12, vy0 = -64 + 3040, vz0 = 200 + 2048 - 12;
            int sx = 24, sy = 20, sz = 24;
            var rows = new byte[sx * sy * sz];
            int n = rule.Rows(vx0, vy0, vz0, sx, sy, sz, S8aConvert.SampleOffset, rows);
            Assert.IsTrue(n > 100, "the deposit's pocket is in the box (" + n + ")");
            int count = 0;
            for (int z = 0; z < sz; z++)
                for (int y = 0; y < sy; y++)
                    for (int x = 0; x < sx; x++)
                    {
                        Vec3 p = S8aConvert.SampleCentreSn(vx0 + x, vy0 + y, vz0 + z);
                        int want = rule.RowAt(p.X, p.Y, p.Z);
                        byte got = rows[x + sx * (y + sy * z)];
                        Assert.AreEqual(want < 0 ? 255 : want, (int)got, "voxel " + x + "," + y + "," + z);
                        if (want >= 0)
                        {
                            count++;
                            Assert.IsTrue(p.Y <= SeabedY - 2.0 + 1e-9, "no vein voxel within 2 m of the surface");
                        }
                    }
            Assert.AreEqual(n, count);
        }

        /// <summary>A batch of the flat seabed (rock below SeabedY, encoded as the converter encodes a sample).</summary>
        private static S8aBatchFile Seabed(int bx, int by, int bz)
        {
            S8aBatchFile b = S8aBatchFile.CreateEmpty(bx, by, bz);
            var enc = new S8aOctreeEncoder();
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++)
            {
                if (!b.Has(i)) continue;
                int ix, iy, iz;
                S8aBatchFile.OctreeCoords(i, out ix, out iy, out iz);
                for (int z = 0; z < 32; z++)
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            int wy = by * SnWorld.BatchVoxels + iy * 32 + y;
                            double s = SeabedY - (SnWorld.OriginY + wy + 0.5);
                            int k = S8aOctree.Index(x, y, z);
                            S8aConvert.EncodeSample(s, Rock, out t[k], out d[k]);
                        }
                b.SetOctree(i, enc.Encode(t, d));
            }
            return b;
        }

        private static void Raster(S8aBatchFile b, Func<int, int, int, byte, byte, bool> visit)
        {
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++)
            {
                if (!b.Has(i)) continue;
                int ix, iy, iz;
                S8aBatchFile.OctreeCoords(i, out ix, out iy, out iz);
                b.DecodeOctree(i, t, d);
                for (int z = 0; z < 32; z++)
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            int k = S8aOctree.Index(x, y, z);
                            if (!visit(b.Bx * SnWorld.BatchVoxels + ix * 32 + x, b.By * SnWorld.BatchVoxels + iy * 32 + y, b.Bz * SnWorld.BatchVoxels + iz * 32 + z, t[k], d[k])) return;
                        }
            }
        }

        [TestMethod]
        public void Overlay_PaintsTheSurfaceOverAVein_TypesOnly_UntilItIsMined()
        {
            int unused;
            S8aCraterOreRule rule = PlaneRule(false, out unused);
            byte goldType = S8aMaterialMap.SnType("Gold_01");
            Assert.AreEqual(goldType, S8aOreOverlay.DefaultRowTypes()[S8aCraterOres.RowOf("Gold_01")]);
            // the deposit at SN (100, -50, 200): world voxel (2148, 2990, 2248), batch (13, 18, 14)
            int bx = 13, by = 18, bz = 14;
            S8aBatchFile install = Seabed(bx, by, bz);
            S8aBatchFile batch = Seabed(bx, by, bz);
            var ov = new S8aOreOverlay(rule, S8aOreOverlay.DefaultRowTypes());
            S8aOreOverlayResult r = ov.Apply(batch, () => install);
            Assert.IsTrue(r.Changed && r.Painted > 0, "the surface over the vein is painted (" + r.Painted + ")");
            Assert.IsTrue(r.VeinVoxels > 0 && r.Mined == 0);
            // every voxel against the install: the density unchanged; the vein's type on exactly the surface voxels within 5 m of a vein voxel
            int painted = 0;
            var t1 = new byte[S8aOctree.Voxels];
            var d1 = new byte[S8aOctree.Voxels];
            var t0 = new byte[S8aOctree.Voxels];
            var d0 = new byte[S8aOctree.Voxels];
            for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++)
            {
                if (!batch.Has(i)) continue;
                int ix, iy, iz;
                S8aBatchFile.OctreeCoords(i, out ix, out iy, out iz);
                batch.DecodeOctree(i, t1, d1);
                install.DecodeOctree(i, t0, d0);
                for (int z = 0; z < 32; z++)
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            int k = S8aOctree.Index(x, y, z);
                            Assert.AreEqual(d0[k], d1[k], "densities untouched");
                            int wx = bx * 160 + ix * 32 + x, wy = by * 160 + iy * 32 + y, wz = bz * 160 + iz * 32 + z;
                            bool surface = d1[k] != 0 && S8aOctree.IsBelowSurface(t1[k], d1[k]);
                            Vec3 c = S8aConvert.SampleCentreSn(wx, wy, wz);
                            bool close = Math.Abs(c.X - 100.0) <= 12.0 && Math.Abs(c.Z - 200.0) <= 12.0;
                            bool near = surface && close && VeinWithin(rule, wx, wy, wz, S8aOreOverlay.ShowRadiusM);
                            Assert.AreEqual(near ? goldType : t0[k], t1[k], "voxel " + wx + "," + wy + "," + wz);
                            if (near) painted++;
                        }
            }
            Assert.AreEqual(r.Painted, (long)painted);
            Assert.IsFalse(ov.Apply(batch, () => install).Changed, "a second pass changes nothing");
            // the vein drilled out (the base loses its voxels): the paint goes, the install's types come back
            var t32 = new byte[S8aOctree.Voxels];
            var d32 = new byte[S8aOctree.Voxels];
            var enc = new S8aOctreeEncoder();
            for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++)
            {
                if (!batch.Has(i)) continue;
                int ix, iy, iz;
                S8aBatchFile.OctreeCoords(i, out ix, out iy, out iz);
                batch.DecodeOctree(i, t32, d32);
                bool changed = false;
                for (int z = 0; z < 32; z++)
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            Vec3 p = S8aConvert.SampleCentreSn(bx * 160 + ix * 32 + x, by * 160 + iy * 32 + y, bz * 160 + iz * 32 + z);
                            if (rule.RowAt(p.X, p.Y, p.Z) < 0) continue;
                            int k = S8aOctree.Index(x, y, z);
                            t32[k] = 0; d32[k] = 0;
                            changed = true;
                        }
                if (changed) batch.SetOctree(i, enc.Encode(t32, d32));
            }
            S8aOreOverlayResult m = ov.Apply(batch, () => install);
            Assert.IsTrue(m.Mined > 0 && m.VeinVoxels == 0, "the vein is gone from the base");
            Assert.AreEqual((long)painted, m.Restored, "every painted voxel got its type back");
            Raster(batch, (wx, wy, wz, t, d) => { Assert.IsFalse(ov.IsOreType(t), "no ore type left"); return true; });
        }

        [TestMethod]
        public void M31_WritesNothing()
        {
            int unused;
            S8aCraterOreRule rule = PlaneRule(false, out unused);
            S8aBatchFile batch = Seabed(13, 18, 14);
            ulong fnv = batch.Fnv();
            try
            {
                Assert.IsTrue(S8aWireMutants.Set("S8a.M31", true));
                StringAssert.Contains(S8aWireMutants.Active, "S8a.M31");
                S8aOreOverlayResult r = new S8aOreOverlay(rule, S8aOreOverlay.DefaultRowTypes()).Apply(batch, null);
                Assert.IsTrue(r.M31 && !r.Changed);
                Assert.AreEqual(fnv, batch.Fnv(), "the batch untouched");
            }
            finally { S8aWireMutants.ClearAll(); }
            Assert.IsFalse(S8aWireMutants.M31OverlayOff);
        }

        private static long Key(int x, int y, int z) { return ((long)x << 40) | ((long)y << 20) | (long)z; }

        private static bool VeinWithin(S8aCraterOreRule rule, int wx, int wy, int wz, double radius)
        {
            int r = (int)Math.Ceiling(radius);
            for (int dz = -r; dz <= r; dz++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx * dx + dy * dy + dz * dz > radius * radius + 1e-9) continue;
                        Vec3 p = S8aConvert.SampleCentreSn(wx + dx, wy + dy, wz + dz);
                        if (rule.RowAt(p.X, p.Y, p.Z) >= 0) return true;
                    }
            return false;
        }
    }
}
