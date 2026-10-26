using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// </summary>
    [TestClass]
    public class S8aOutcropsTests
    {
        private const int N = 4, S = N + 2;

        private static int I(int x, int y, int z) { return x + S * (y + S * z); }

        private static sbyte[] OreTable()
        {
            var t = new sbyte[256];
            for (int i = 0; i < 256; i++) t[i] = -1;
            t[20] = 0;   // Iron_02
            t[21] = 6;   // Gold_01
            return t;
        }

        [TestMethod]
        public void FlatOutcrop_TopLayerExposed_BuriedNot()
        {
            // a 4^3 cell, solid below inner y = 2 (margin included), water above; iron in inner y 0..1
            var content = new byte[S * S * S];
            var material = new byte[S * S * S];
            for (int z = 0; z < S; z++)
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        content[I(x, y, z)] = y <= 2 ? (byte)255 : (byte)0;
                        material[I(x, y, z)] = y <= 2 ? (byte)20 : (byte)1;
                    }
            List<S8aOutcropCellOre> r = S8aOutcrops.ScanCell(content, material, N, OreTable());
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual(0, r[0].Ore);
            Assert.AreEqual(2 * N * N, r[0].OreVoxels);               // inner y 0 and 1 (array y 1, 2)
            Assert.AreEqual(N * N, r[0].ExposedVoxels);               // only the top layer meets the water
            foreach (int p in r[0].Exposed)
            {
                Assert.AreEqual(1, (p >> 5) & 31);                    // inner y 1
                int dx, dy, dz;
                S8aOutcrops.MaskDirection(p >> 15, out dx, out dy, out dz);
                Assert.AreEqual(0, dx);
                Assert.AreEqual(1, dy);                               // outward = up
                Assert.AreEqual(0, dz);
            }
        }

        [TestMethod]
        public void SolidMarginIsNoExposure_AndOtherMaterialsIgnored()
        {
            // everything solid (a tile edge's margin given solid): nothing exposed; a non-ore material is not counted
            var content = new byte[S * S * S];
            var material = new byte[S * S * S];
            for (int i = 0; i < content.Length; i++) { content[i] = 255; material[i] = 1; }
            material[I(2, 2, 2)] = 21;
            List<S8aOutcropCellOre> r = S8aOutcrops.ScanCell(content, material, N, OreTable());
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual(6, r[0].Ore);
            Assert.AreEqual(1, r[0].OreVoxels);
            Assert.AreEqual(0, r[0].ExposedVoxels);
            // drill a hole next to it: the vein wall is exposed
            content[I(3, 2, 2)] = 0;
            r = S8aOutcrops.ScanCell(content, material, N, OreTable());
            Assert.AreEqual(1, r[0].ExposedVoxels);
            int dx, dy, dz;
            S8aOutcrops.MaskDirection(r[0].Exposed[0] >> 15, out dx, out dy, out dz);
            Assert.AreEqual(1, dx);
        }

        [TestMethod]
        public void Scale_ByFirstArea_ShrinksWithVolume_GoneUnderTenPercent()
        {
            double full = S8aOutcrops.ScaleOf(64.0, 800.0, 800.0);
            Assert.AreEqual(2.0, full, 1e-9);                               // sqrt(64) / 4
            double half = S8aOutcrops.ScaleOf(64.0, 400.0, 800.0);
            Assert.IsTrue(half < full && half > 0);
            Assert.AreEqual(2.0 * System.Math.Pow(0.5, 1.0 / 3.0), half, 1e-9);
            Assert.AreEqual(0.0, S8aOutcrops.ScaleOf(64.0, 70.0, 800.0));   // < 10 % left: removed
            Assert.AreEqual(0.0, S8aOutcrops.ScaleOf(4.0, 800.0, 800.0));   // below MinExposedM2: no outcrop
            Assert.AreEqual(S8aOutcrops.MaxScale, S8aOutcrops.ScaleOf(10000.0, 1.0, 1.0), 1e-9);
            Assert.AreEqual(System.Math.Sqrt(8.0) / 4.0, S8aOutcrops.ScaleOf(8.0, 1.0, 1.0), 1e-9);   // the smallest outcrop
        }

        [TestMethod]
        public void Mapping_InverseOfTheCraterRule()
        {
            for (int i = 0; i < S8aOutcrops.Ores.Length; i++)
                Assert.AreEqual(S8aOutcrops.Ores[i], S8aCraterOres.OreOfTechType(S8aOutcrops.TechTypes[i]), S8aOutcrops.TechTypes[i]);
            for (int i = 0; i < S8aOutcrops.Ores.Length; i++)
                Assert.IsTrue(S8aMaterialMap.IsOre(S8aOutcrops.Ores[i]), S8aOutcrops.Ores[i]);
            Assert.AreEqual("DrillableUranium", S8aOutcrops.TechTypeOfOre("Uraninite_01"));
            Assert.IsNull(S8aOutcrops.TechTypeOfOre("Stone_01"));
        }

        [TestMethod]
        public void File_RoundTrip_AndMerge()
        {
            var a = new S8aOutcrop { Key = S8aOutcrops.Key(7, 1, 2, 3, 0), Ore = "Iron_02", X = 10.5, Y = -80.25, Z = 3.125, NX = 0, NY = 1, NZ = 0, ExposedM2 = 40, VolumeM3 = 320,
                                     FirstExposedM2 = 40, FirstVolumeM3 = 400, DetX = 11, DetY = -84, DetZ = 4, Drilled = true, Zone = "tile", Scale = 1.4 };
            var b = a;
            b.Key = S8aOutcrops.Key(8, 1, 2, 3, 0);
            b.X += 1.0;                 // the same face in an overlapping tile
            b.ExposedM2 = 20;
            var c = a;
            c.Key = S8aOutcrops.Key(7, 2, 2, 3, 6);
            c.Ore = "Gold_01";
            c.X += 1.0;                 // another ore at the same spot stays
            List<S8aOutcrop> m = S8aOutcrops.Merge(new[] { b, a, c });
            Assert.AreEqual(2, m.Count);
            Assert.IsTrue(m.Exists(r => r.Key == a.Key) && m.Exists(r => r.Key == c.Key) && !m.Exists(r => r.Key == b.Key));
            m.Sort((p, q) => p.Ore == "Iron_02" ? -1 : q.Ore == "Iron_02" ? 1 : 0);
            string text = S8aOutcrops.Format(5, 2, 0x1234abcdUL, m);
            long seq;
            uint cs;
            ulong ck;
            List<S8aOutcrop> back;
            Assert.IsTrue(S8aOutcrops.TryParse(text, out seq, out cs, out ck, out back));
            Assert.AreEqual(5L, seq);
            Assert.AreEqual(2u, cs);
            Assert.AreEqual(0x1234abcdUL, ck);
            Assert.AreEqual(2, back.Count);
            Assert.AreEqual(a.Key, back[0].Key);
            Assert.AreEqual(-80.25, back[0].Y, 1e-9);
            Assert.IsTrue(back[0].Drilled);
            Assert.AreEqual("tile", back[0].Zone);
            Assert.AreEqual("DrillableTitanium", back[0].TechType);
            Assert.IsFalse(S8aOutcrops.TryParse("de-s8a-crater-deposits 1\n", out seq, out cs, out ck, out back));
            Assert.AreNotEqual(S8aOutcrops.Key(7, 1, 2, 3, 0), S8aOutcrops.Key(7, 1, 2, 3, 1));
        }

        [TestMethod]
        public void LocalOre_ShrinksWithADrillAtTheAnchor_AndOneVoxelIsMined()
        {
            // a test run (S8a.NO.shrink): a cell full of ore 0 (16^3 + margin); a 3-voxel sphere round inner (8, 8, 8): 123 voxels; drill a
 // 2-voxel hole there: the local count falls by its 33 voxels while the cell's falls by 0.8 %
            int n = 16, s = n + 2;
            var content = new byte[s * s * s];
            var material = new byte[s * s * s];
            for (int i = 0; i < content.Length; i++) { content[i] = 255; material[i] = 5; }
            var oreOf = new sbyte[256];
            for (int i = 0; i < 256; i++) oreOf[i] = -1;
            oreOf[5] = 0;
            int before = S8aOutcrops.LocalOreVoxels(content, material, n, oreOf, 0, 8, 8, 8, S8aOutcrops.LocalRadiusVoxels);
            Assert.AreEqual(123, before, "the r = 3 voxel sphere");
            int cut = 0;
            for (int z = -2; z <= 2; z++)
                for (int y = -2; y <= 2; y++)
                    for (int x = -2; x <= 2; x++)
                        if (x * x + y * y + z * z <= 4) { content[(9 + x) + s * ((9 + y) + s * (9 + z))] = 0; cut++; }
            int after = S8aOutcrops.LocalOreVoxels(content, material, n, oreOf, 0, 8, 8, 8, S8aOutcrops.LocalRadiusVoxels);
            Assert.AreEqual(before - cut, after);
            double v = 8.0;
            double sBefore = S8aOutcrops.ScaleOf(40.0, before * v, before * v), sAfter = S8aOutcrops.ScaleOf(40.0, after * v, before * v);
            Assert.IsTrue(sAfter < sBefore * 0.92, "a visible shrink (a 27 % cut, -10 % in size): " + sBefore + " -> " + sAfter);
            Assert.IsTrue(S8aOutcrops.MinedSince(4096 * v - v, 4096 * v, before * v, before * v), "one voxel less in the cell is mined");
            Assert.IsTrue(S8aOutcrops.MinedSince(4096 * v, 4096 * v, (before - 1) * v, before * v), "one voxel less near the anchor is mined");
            Assert.IsFalse(S8aOutcrops.MinedSince(4096 * v, 4096 * v, before * v, before * v));
        }
    }
}
