using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>
 /// S4.A-T0b (offline part): a synthetic SN plane (untruncated distance) served through the tile sampler, the pure
 /// seam the SE provider wraps (TileSampler over ISnField), on tile (2, 2, 1) of site v1 around the test centre.
 /// Per LOD 0, 1, 2 (content in LOD-voxel units):
 /// (1) every served content byte equals the byte computed independently from the plane at the sample's nominal
 /// position (tileMin + i·2^L, SN y = |w − C| − Rs), so positions, LOD scaling and the byte expression are right;
 /// (2) the iso height predicted from the bytes (linear interpolation along the tile's column) lies within the byte
 /// quantization of the plane for every column: |Δ| ≤ 0.5/126.5 · 2^L m at iso byte 127 (the unbiased iso of the
 /// truncating byte); iso 127.5 is reported (its bias is −0.5/127.5 · 2^L m);
 /// (3) the mean of Δ at iso 127 over 32 heights × 64 columns is 0 within 1e-3 m ("exact to 1e-3 m").
 /// S4.M6 (samples shifted half a voxel) turns (1) and (3) red (0.5 m off). The part meshed by SE's own iso-surface
 /// code (VRage.NativeWrapper IsoMesher) runs in SE as "de.terrain at0b" (it records SE's iso byte).
 /// </summary>
    [TestClass]
    public class At0bTests
    {
        private static readonly SiteMap Map = SiteMap.SiteV1Test;
        private static readonly int[] Tile = { 2, 2, 1 };

        internal sealed class LodResult
        {
            public int Lod;
            public long Samples, ByteMismatch, ByteMismatchOver1;
            public double MeanErr127, MaxAbsErr127, MeanErr1275, MaxAbsErr1275, Bound;
            public int Columns;
        }

        private static Vec3 TileMin()
        {
            return Map.C + SiteV1.TileMinRelative(Tile[0], Tile[1], Tile[2]);
        }

        private static byte ExpectedByte(Vec3 w, double h, int lod)
        {
            Vec3 r = w - Map.C;
            double snY = Math.Sqrt(r.X * r.X + r.Y * r.Y + r.Z * r.Z) - Map.Rs;
            float d0 = (float)(h - snY);
            float d = d0 / (1 << lod);
            if (d > 1f) d = 1f;
            if (d < -1f) d = -1f;
            if (!(d > -1f)) return 0;
            if (d >= 1f) return 255;
            return (byte)((d * 0.5f + 0.5f) * 255f);
        }

        internal static LodResult RunLod(int lod)
        {
            var bands = new MaterialBands(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
            Vec3 tmin = TileMin();
            Vec3 rel = SiteV1.TileMinRelative(Tile[0], Tile[1], Tile[2]);
            double step = 1 << lod;
            int size = TileSampler.TileSize >> lod;
            var res = new LodResult { Lod = lod, Bound = 0.5 / 126.5 * step + 1e-6 };
            var heights = new List<double>();
            for (int j = 0; j < 16; j++) heights.Add(-17.212 + j * 0.0371);
            for (int j = 0; j < 16; j++) heights.Add(-235.138 + j * 0.0533);
            double sum127 = 0, sum1275 = 0;
            long n = 0;
            var buf = new byte[16];
            foreach (double h in heights)
            {
                var sampler = new TileSampler(new SyntheticPlaneField(h), Map, tmin, bands);
                for (int ci = 0; ci < 8; ci++)
                    for (int cj = 0; cj < 8; cj++)
                    {
                        int i = ci * (size - 1) / 7, j = cj * (size - 1) / 7;
                        double rx = rel.X + i * step, ry = rel.Y + j * step;
                        double rz = Math.Sqrt((Map.Rs + h) * (Map.Rs + h) - rx * rx - ry * ry);
                        int k0 = (int)Math.Floor((rz - rel.Z) / step) - 3;
                        int k1 = k0 + 7;
                        Assert.IsTrue(k0 >= 0 && k1 < size, "plane outside the tile column");
                        bool allEmpty, allFull;
                        sampler.ReadContent(lod, i, j, k0, i, j, k1, buf, 0, 1, 1, out allEmpty, out allFull);
                        Assert.IsFalse(allEmpty || allFull, "a column across the plane is neither empty nor full");
                        for (int k = k0; k <= k1; k++)
                        {
                            byte want = ExpectedByte(new Vec3(tmin.X + i * step, tmin.Y + j * step, tmin.Z + k * step), h, lod);
                            byte got = buf[k - k0];
                            res.Samples++;
                            if (got != want)
                            {
                                res.ByteMismatch++;
                                if (Math.Abs(got - want) > 1) res.ByteMismatchOver1++;
                            }
                        }
                        double e127 = PredictedErr(buf, k0, k1, 127.0, tmin, i, j, step, h);
                        double e1275 = PredictedErr(buf, k0, k1, 127.5, tmin, i, j, step, h);
                        sum127 += e127;
                        sum1275 += e1275;
                        n++;
                        res.MaxAbsErr127 = Math.Max(res.MaxAbsErr127, Math.Abs(e127));
                        res.MaxAbsErr1275 = Math.Max(res.MaxAbsErr1275, Math.Abs(e1275));
                    }
            }
            res.Columns = (int)n;
            res.MeanErr127 = sum127 / n;
            res.MeanErr1275 = sum1275 / n;
            return res;
        }

        /// <summary>SN y of the iso crossing predicted from the column's bytes (inside = byte ≥ iso), minus the plane height.</summary>
        private static double PredictedErr(byte[] buf, int k0, int k1, double iso, Vec3 tmin, int i, int j, double step, double h)
        {
            for (int k = k0; k < k1; k++)
            {
                double a = buf[k - k0], b = buf[k + 1 - k0];
                if (a >= iso && b < iso)
                {
                    double t = (a - iso) / (a - b);
                    Vec3 w = new Vec3(tmin.X + i * step, tmin.Y + j * step, tmin.Z + (k + t) * step);
                    return Map.SeToSn(w).Y - h;
                }
            }
            Assert.Fail("no iso crossing in the column");
            return double.NaN;
        }

        private static string Line(LodResult r)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "A-T0b LOD {0}: {1} samples, byte mismatches {2} (>1: {3}); iso 127: mean {4:E2} m, max |Δ| {5:F5} m (bound {6:F5}); iso 127.5: mean {7:F5} m, max |Δ| {8:F5} m; {9} columns",
                r.Lod, r.Samples, r.ByteMismatch, r.ByteMismatchOver1, r.MeanErr127, r.MaxAbsErr127, r.Bound, r.MeanErr1275, r.MaxAbsErr1275, r.Columns);
        }

        [TestMethod]
        public void At0b_PlaneThroughTheSampler_Lod0To2()
        {
            TerrainMutants.ClearAll();
            for (int lod = 0; lod <= 2; lod++)
            {
                LodResult r = RunLod(lod);
                Console.WriteLine(Line(r));
                Assert.AreEqual(0, r.ByteMismatchOver1, "LOD " + lod + ": served bytes differ by more than 1 from the plane's");
                Assert.IsTrue(r.ByteMismatch <= r.Samples / 1000, "LOD " + lod + ": too many byte mismatches (" + r.ByteMismatch + ")");
                Assert.IsTrue(r.MaxAbsErr127 <= r.Bound, "LOD " + lod + ": iso-127 height off by " + r.MaxAbsErr127 + " m > quantization bound " + r.Bound);
                Assert.IsTrue(Math.Abs(r.MeanErr127) <= 1e-3, "LOD " + lod + ": mean iso-127 height bias " + r.MeanErr127 + " m > 1e-3 m");
                double bias1275 = -0.5 / 127.5 * (1 << lod);
                Assert.IsTrue(Math.Abs(r.MeanErr1275 - bias1275) <= 1e-3, "LOD " + lod + ": iso 127.5 bias " + r.MeanErr1275 + " m, expected about " + bias1275);
            }
        }

        [TestMethod]
        public void At0b_MutantM6_HalfVoxelShift_Red()
        {
            TerrainMutants.ClearAll();
            TerrainMutants.HalfVoxelShift = true;
            try
            {
                for (int lod = 0; lod <= 2; lod++)
                {
                    LodResult r = RunLod(lod);
                    Console.WriteLine("S4.M6 " + Line(r));
                    Assert.IsTrue(Math.Abs(r.MeanErr127) > 0.4, "S4.M6 must move the iso height by about 0.5 m at LOD " + lod + " (got " + r.MeanErr127 + ")");
                    Assert.IsTrue(r.ByteMismatchOver1 > 0, "S4.M6 must change the served bytes");
                }
            }
            finally
            {
                TerrainMutants.ClearAll();
            }
        }

        [TestMethod]
        public void At0b_StridedBoxEqualsSingleSamples()
        {
            TerrainMutants.ClearAll();
            var bands = new MaterialBands(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
            Vec3 tmin = TileMin();
            double h = -17.212;
            var sampler = new TileSampler(new SyntheticPlaneField(h), Map, tmin, bands);
            int lod = 1;
            double rz = Map.Rs + h;
            int kMid = (int)((rz - SiteV1.TileMinRelative(2, 2, 1).Z) / 2);
            int minX = 10, minY = 20, minZ = kMid - 2, maxX = 14, maxY = 23, maxZ = kMid + 1;
            const int baseIndex = 7, strideY = 9, strideZ = 50;
            var box = new byte[baseIndex + 4 + 3 * strideY + 3 * strideZ + 1];
            for (int i = 0; i < box.Length; i++) box[i] = 0xAB;
            bool e, f;
            sampler.ReadContent(lod, minX, minY, minZ, maxX, maxY, maxZ, box, baseIndex, strideY, strideZ, out e, out f);
            var mat = new byte[box.Length];
            bool me;
            sampler.ReadMaterial(lod, minX, minY, minZ, maxX, maxY, maxZ, mat, baseIndex, strideY, strideZ, out me);
            var one = new byte[1];
            var slots = new HashSet<int>();
            for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        sampler.ReadContent(lod, x, y, z, x, y, z, one, 0, 0, 0, out e, out f);
                        int idx = baseIndex + (x - minX) + (y - minY) * strideY + (z - minZ) * strideZ;
                        Assert.AreEqual(one[0], box[idx], "strided box differs at " + x + "," + y + "," + z);
                        byte wantMat = one[0] == 0 ? (byte)255 : bands.For(Map.SeToSn(sampler.SamplePoint(lod, x, y, z)).Y);
                        Assert.AreEqual(wantMat, mat[idx], "material differs at " + x + "," + y + "," + z);
                        slots.Add(idx);
                    }
            Assert.AreEqual(5 * 4 * 4, slots.Count);
            for (int i = 0; i < box.Length; i++)
                if (!slots.Contains(i)) Assert.AreEqual((byte)0xAB, box[i], "the box wrote outside its strided slots at " + i);
        }
    }
}
