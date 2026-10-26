using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>
 /// The tile sampler (the VRage-free core of SnTerrainProvider): content and material bytes and their flags,
 /// classification soundness (never Empty/Full unless every byte is), the curvature bound of CONTRACT 3.1, line walks
 /// (false over open water), thread safety and allocation-free reads. Synthetic batches always; the user's install
 /// when present (Safe Shallows, tile (2, 2, 1) of site v1).
 /// </summary>
    [TestClass]
    public class SamplerTests
    {
        private const int N = SnOctreeFormat.BatchVoxels;
        private static readonly SiteMap Map = SiteMap.SiteV1Test;
        private static readonly MaterialBands Bands = new MaterialBands(new byte[] { 11, 12, 13, 14, 15, 16, 17 });
        private static string _dir;
        private static SnOctreeSource _syn;

        [ClassInitialize]
        public static void Init(TestContext ctx)
        {
            var type = new byte[N * N * N];
            var density = new byte[N * N * N];
            SyntheticOctrees.StandardSurface(type, density);
            _dir = TestEnv.NewTempDir("sampler");
            SyntheticOctrees.WriteBatch(_dir, 12, 18, 12, SyntheticOctrees.EncodeBatch(12, 18, 12, type, density));
            string err;
            _syn = SnOctreeSource.Open(_dir, out err);
            Assert.IsNotNull(_syn, err);
        }

        [ClassCleanup]
        public static void Cleanup()
        {
            if (_syn != null) _syn.Release();
            _syn = null;
            TestEnv.DeleteDir(_dir);
        }

        private static Vec3 TileMin(int i, int j, int k) { return Map.C + SiteV1.TileMinRelative(i, j, k); }

        private static TileSampler Sampler(SnOctreeSource src, int i = 2, int j = 2, int k = 1)
        {
            return new TileSampler(new SnField(src), Map, TileMin(i, j, k), Bands);
        }

        /// <summary>Tile-local metres of an SN point.</summary>
        private static Vec3 Local(TileSampler s, double x, double y, double z)
        {
            return Map.SnToSe(new Vec3(x, y, z)) - s.TileMinWorld;
        }

        [TestMethod]
        public void ContentByteAndLodDistance_Table()
        {
            Assert.AreEqual(127, TileSampler.ContentByte(0f));
            Assert.AreEqual(255, TileSampler.ContentByte(1f));
            Assert.AreEqual(0, TileSampler.ContentByte(-1f));
            Assert.AreEqual(128, TileSampler.ContentByte(1f / 126f));
            Assert.AreEqual(0, TileSampler.ContentByte(float.NaN));
            Assert.AreEqual(1f, TileSampler.LodDistance(0.3f, true, 2));
            Assert.AreEqual(-1f, TileSampler.LodDistance(-0.3f, true, 0));
            Assert.AreEqual(0.25f, TileSampler.LodDistance(0.5f, false, 1));
            Assert.AreEqual(1f, TileSampler.LodDistance(9f, false, 3));
            Assert.AreEqual(-1f, TileSampler.LodDistance(-9f, false, 0));
            Assert.AreEqual(0, MaterialBands.BandOf(2.0));
            Assert.AreEqual(1, MaterialBands.BandOf(1.999));
            Assert.AreEqual(1, MaterialBands.BandOf(-40.0));
            Assert.AreEqual(2, MaterialBands.BandOf(-40.001));
            Assert.AreEqual(5, MaterialBands.BandOf(-1300.0));
            Assert.AreEqual(6, MaterialBands.BandOf(-1300.5));
            Assert.AreEqual(6, MaterialBands.BandOf(double.NaN));
            Assert.AreEqual(17, Bands.For(-1900));
            CollectionAssert.AreEqual(new[] { "Grass", "Sand_02", "Stone_01", "Stone_02", "Stone_03", "Stone_04", "Stone_05" }, MaterialBands.Names);
        }

        /// <summary>Classify must never say Empty or Full unless every byte of the box (at LOD 0 and at the LOD grid inside it) is.</summary>
        private static int[] CheckClassifySound(TileSampler s, Vec3 centre, int count, int maxSize, int seed)
        {
            var rng = new Random(seed);
            var counts = new int[3];
            var buf = new byte[64 * 64 * 64];
            var mat = new byte[buf.Length];
            for (int n = 0; n < count; n++)
            {
                int sx = 1 + rng.Next(maxSize), sy = 1 + rng.Next(maxSize), sz = 1 + rng.Next(maxSize);
                int x0 = (int)centre.X - 60 + rng.Next(120), y0 = (int)centre.Y - 60 + rng.Next(120), z0 = (int)centre.Z - 90 + rng.Next(150);
                int x1 = x0 + sx - 1, y1 = y0 + sy - 1, z1 = z0 + sz - 1;
                ContentClass c = s.Classify(x0, y0, z0, x1, y1, z1);
                counts[(int)c]++;
                // SE only reads the in-tile part (MyOctreeStorage clamps and fills the rest with content 0)
                bool outside = x0 < 0 || y0 < 0 || z0 < 0 || x1 > 1023 || y1 > 1023 || z1 > 1023;
                x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); z0 = Math.Max(z0, 0);
                x1 = Math.Min(x1, 1023); y1 = Math.Min(y1, 1023); z1 = Math.Min(z1, 1023);
                if (x1 < x0 || y1 < y0 || z1 < z0)
                {
                    Assert.AreEqual(ContentClass.Empty, c, "a box entirely outside the tile is empty for SE");
                    continue;
                }
                if (outside) Assert.AreNotEqual(ContentClass.Full, c, "a box reaching outside the tile is never Full");
                sx = x1 - x0 + 1; sy = y1 - y0 + 1; sz = z1 - z0 + 1;
                bool allEmpty, allFull, matEmpty;
                s.ReadContent(0, x0, y0, z0, x1, y1, z1, buf, 0, sx, sx * sy, out allEmpty, out allFull);
                s.ReadMaterial(0, x0, y0, z0, x1, y1, z1, mat, 0, sx, sx * sy, out matEmpty);
                bool e = true, f = true, me = true;
                for (int i = 0; i < sx * sy * sz; i++)
                {
                    if (buf[i] != 0) e = false;
                    if (buf[i] != 255) f = false;
                    if (mat[i] != 255) me = false;
                    if ((buf[i] == 0) != (mat[i] == 255)) Assert.Fail("material 255 must mean content 0");
                }
                Assert.AreEqual(e, allEmpty, "allEmpty flag");
                Assert.AreEqual(f, allFull, "allFull flag");
                Assert.AreEqual(me, matEmpty, "material allEmpty flag");
                if (c == ContentClass.Empty) Assert.IsTrue(e, "Classify said Empty over non-empty bytes at " + x0 + "," + y0 + "," + z0 + " size " + sx + "," + sy + "," + sz);
                if (c == ContentClass.Full) Assert.IsTrue(f, "Classify said Full over non-full bytes at " + x0 + "," + y0 + "," + z0);
                // the clipmap's use: an LOD-L cell inside the LOD-0 box
                for (int lod = 1; lod <= 2 && c != ContentClass.Mixed; lod++)
                {
                    int lx0 = (x0 + (1 << lod) - 1) >> lod, ly0 = (y0 + (1 << lod) - 1) >> lod, lz0 = (z0 + (1 << lod) - 1) >> lod;
                    int lx1 = x1 >> lod, ly1 = y1 >> lod, lz1 = z1 >> lod;
                    if (lx1 < lx0 || ly1 < ly0 || lz1 < lz0) continue;
                    int w = lx1 - lx0 + 1, hh = ly1 - ly0 + 1;
                    s.ReadContent(lod, lx0, ly0, lz0, lx1, ly1, lz1, buf, 0, w, w * hh, out allEmpty, out allFull);
                    if (c == ContentClass.Empty) Assert.IsTrue(allEmpty, "LOD " + lod + " bytes not empty in an Empty box");
                    if (c == ContentClass.Full) Assert.IsTrue(allFull, "LOD " + lod + " bytes not full in a Full box");
                }
            }
            return counts;
        }

        [TestMethod]
        public void Classify_IsSound_Synthetic()
        {
            TerrainMutants.ClearAll();
            TileSampler s = Sampler(_syn);
            Vec3 c = Local(s, -60, -80, -60);
            int[] counts = CheckClassifySound(s, c, 3000, 24, 1);
            Console.WriteLine("synthetic boxes: Empty " + counts[0] + ", Full " + counts[1] + ", Mixed " + counts[2]);
            Assert.IsTrue(counts[0] > 0 && counts[1] > 0 && counts[2] > 0, "all three classes must occur");
        }

        [TestMethod]
        public void Classify_IsSound_SafeShallows()
        {
            TestEnv.RequireInstall();
            TerrainMutants.ClearAll();
            TileSampler s = Sampler(TestEnv.Install);
            Vec3 c = Local(s, -40, -25, -40);
            int[] counts = CheckClassifySound(s, c, 2000, 24, 2);
            Console.WriteLine("Safe Shallows boxes: Empty " + counts[0] + ", Full " + counts[1] + ", Mixed " + counts[2]);
            Assert.IsTrue(counts[0] > 0 && counts[2] > 0);
        }

        [TestMethod]
        public void Classify_OutsideTheTileIsNeverFull()
        {
            var deep = new TileSampler(new SyntheticPlaneField(1500.0), Map, TileMin(2, 2, 1), Bands);
            Assert.AreEqual(ContentClass.Full, deep.Classify(0, 0, 0, 10, 10, 10));
            Assert.AreEqual(ContentClass.Mixed, deep.Classify(0, 0, -5, 10, 10, 10));
            Assert.AreEqual(ContentClass.Mixed, deep.Classify(1015, 0, 0, 1030, 10, 10));
            Assert.AreEqual(ContentClass.Empty, deep.Classify(-50, 0, 0, -1, 10, 10), "entirely outside: empty for SE");
            Assert.AreEqual(ContentClass.Empty, deep.Classify(5, 5, 5, 4, 5, 5), "an inverted box is empty");
            var high = new TileSampler(new SyntheticPlaneField(-3000.0), Map, TileMin(2, 2, 1), Bands);
            Assert.AreEqual(ContentClass.Empty, high.Classify(0, 0, 0, 1023, 1023, 1023));
        }

        /// <summary>CONTRACT 3.1 (2): every point of a box maps inside its corners' SN AABB expanded by L²/(2·rMin) + 0.01 m.</summary>
        [TestMethod]
        public void CurvatureBound_HoldsOnDenseSamplesOfRandomBoxes()
        {
            var rng = new Random(3);
            int[][] tiles = SiteV1.TilesCreated();
            double worst = double.NegativeInfinity;
            for (int n = 0; n < 400; n++)
            {
                int[] t = tiles[rng.Next(tiles.Length)];
                Vec3 tmin = TileMin(t[0], t[1], t[2]);
                int size = n < 40 ? 1024 : 1 + rng.Next(n < 200 ? 1024 : 64);
                int x0 = rng.Next(1025 - size), y0 = rng.Next(1025 - size), z0 = rng.Next(1025 - size);
                int x1 = Math.Min(1023, x0 + size - 1 + rng.Next(3)), y1 = Math.Min(1023, y0 + size - 1), z1 = Math.Min(1023, z0 + size - 1);
                double lo0 = double.PositiveInfinity, lo1 = double.PositiveInfinity, lo2 = double.PositiveInfinity;
                double hi0 = double.NegativeInfinity, hi1 = double.NegativeInfinity, hi2 = double.NegativeInfinity;
                double rCorner = double.PositiveInfinity;
                for (int c = 0; c < 8; c++)
                {
                    Vec3 w = tmin + new Vec3((c & 1) == 0 ? x0 : x1, (c & 2) == 0 ? y0 : y1, (c & 4) == 0 ? z0 : z1);
                    Vec3 p = Map.SeToSn(w);
                    lo0 = Math.Min(lo0, p.X); lo1 = Math.Min(lo1, p.Y); lo2 = Math.Min(lo2, p.Z);
                    hi0 = Math.Max(hi0, p.X); hi1 = Math.Max(hi1, p.Y); hi2 = Math.Max(hi2, p.Z);
                    rCorner = Math.Min(rCorner, (w - Map.C).Length);
                }
                double l = Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
                double e = l * l / (2 * rCorner) + 0.01;                          // the CONTRACT's own rMin (corner) form
                for (int a = 0; a <= 8; a++)
                    for (int b = 0; b <= 8; b++)
                        for (int c = 0; c <= 8; c++)
                        {
                            Vec3 w = tmin + new Vec3(x0 + (x1 - x0) * a / 8.0, y0 + (y1 - y0) * b / 8.0, z0 + (z1 - z0) * c / 8.0);
                            Vec3 p = Map.SeToSn(w);
                            double over = Math.Max(Math.Max(Math.Max(lo0 - p.X, p.X - hi0), Math.Max(lo1 - p.Y, p.Y - hi1)), Math.Max(lo2 - p.Z, p.Z - hi2));
                            worst = Math.Max(worst, over - e);
                            Assert.IsTrue(over <= e, "point outside the expanded AABB by " + (over - e) + " m (box size " + size + ")");
                        }
            }
            Console.WriteLine("curvature bound: worst (excess over the AABB) − margin = " + worst.ToString("F4", CultureInfo.InvariantCulture) + " m (must be ≤ 0)");
        }

        [TestMethod]
        public void IntersectLine_OpenWaterIsFalse_SeabedIsTrue_Synthetic()
        {
            TerrainMutants.ClearAll();
            TileSampler s = Sampler(_syn);
            // surface at batch-local (68.5, ·, 68.5): y_s ≈ 85 → SN y ≈ −75
            Vec3 a = Local(s, -59.5, -10, -59.5), b = Local(s, -59.5, -60, -59.5), c = Local(s, -59.5, -120, -59.5);
            double st, en, cf, ct;
            Assert.IsFalse(s.IntersectLine(a.X, a.Y, a.Z, b.X, b.Y, b.Z, out st, out en, out cf, out ct), "open water must not block");
            Assert.AreEqual(0.0, cf, 1e-12);
            Assert.AreEqual(1.0, ct, 1e-12);
            // open water inside the 32 m octree that holds the surface (octree y −96..−64; band −76..−74): leaf-level
            Vec3 g = Local(s, -59.5, -64.5, -59.5), h = Local(s, -59.5, -72.0, -59.5);
            Assert.IsFalse(s.IntersectLine(g.X, g.Y, g.Z, h.X, h.Y, h.Z, out st, out en, out cf, out ct), "water in the surface's octree must not block");
            Assert.IsTrue(s.IntersectLine(a.X, a.Y, a.Z, c.X, c.Y, c.Z, out st, out en, out cf, out ct), "the seabed blocks");
            double len = (c - a).Length;
            double surfaceAt = (Local(s, -59.5, -75, -59.5) - a).Length / len;
            Assert.IsTrue(st <= surfaceAt + 2.0 / len && en >= surfaceAt, "the sub-segment holds the surface: " + st + ".." + en + " vs " + surfaceAt);
            Assert.IsTrue(st * len >= (60 - 16 - 3), "starts close to the surface (16 m steps), start " + st * len + " m");
            // a line entirely outside the tile box
            Assert.IsFalse(s.IntersectLine(-10, 5, 5, -20, 5, 5, out st, out en, out cf, out ct));
            // a line crossing the tile box partly: clip fractions
            s.IntersectLine(-512, 300, 300, 512, 300, 300, out st, out en, out cf, out ct);
            Assert.AreEqual(0.5, cf, 1e-9);
            Assert.AreEqual(1.0, ct, 1e-9);
            // below the slab: the missing batch under it is empty (the plug)
            Vec3 d = Local(s, -59.5, -170, -59.5), e = Local(s, -59.5, -300, -59.5);
            Assert.IsFalse(s.IntersectLine(d.X, d.Y, d.Z, e.X, e.Y, e.Z, out st, out en, out cf, out ct), "a missing batch never blocks");
        }

        [TestMethod]
        public void SafeShallows_ContentSurfaceAtSn00_AndOpenWater()
        {
            TestEnv.RequireInstall();
            TerrainMutants.ClearAll();
            TileSampler s = Sampler(TestEnv.Install);
            // the anchor column: tile-local (2, 2, z) is SN (0, y, 0) exactly
            int z0 = (int)Math.Floor(Map.Rs - 17.2 - SiteV1.TileMinRelative(2, 2, 1).Z) - 6;
            var buf = new byte[13];
            bool e, f;
            s.ReadContent(0, 2, 2, z0, 2, 2, z0 + 12, buf, 0, 1, 1, out e, out f);
            double y = double.NaN;
            for (int k = 0; k < 12; k++)
                if (buf[k] >= 127 && buf[k + 1] < 127)
                {
                    double t = (buf[k] - 127.0) / (buf[k] - (double)buf[k + 1]);
                    y = Map.SeToSn(s.TileMinWorld + new Vec3(2, 2, z0 + k + t)).Y;
                    break;
                }
            Console.WriteLine("content surface at SN (0, 0): " + y.ToString("F4", CultureInfo.InvariantCulture) +
                              " m (sn_field.py −17.2077; S3 layer-30 hit −17.212)");
            // SE samples sit about 0.5 m off Subnautica's sample planes here (SN y = n - 0.0039 vs m + 0.5), so the
 // linear interpolation of SE's bytes crosses a kink of the piecewise-trilinear field: a resampling error of
 // a few cm (the A-T0 offset grid measures it; P95 <= 0.05 m)
            Assert.AreEqual(-17.2077, y, 0.05, "content surface vs the Python reference field");
            Vec3 a = Local(s, 0, -2, 0), b = Local(s, 0, -12, 0), c = Local(s, 0, -40, 0);
            double st, en, cf, ct;
            Assert.IsFalse(s.IntersectLine(a.X, a.Y, a.Z, b.X, b.Y, b.Z, out st, out en, out cf, out ct), "the Safe Shallows water column");
            Assert.IsTrue(s.IntersectLine(a.X, a.Y, a.Z, c.X, c.Y, c.Z, out st, out en, out cf, out ct), "the Safe Shallows seabed");
        }

        /// <summary>
 /// Informational cost of a physics-cell request (LOD 0, 8c − 1 … 8c + 10 = 12³ samples, content then material) over
 /// the Safe Shallows seabed, first touch included; S4.A-T1(3) measures the real budget in SE (p99 ≤ 2 ms, &lt; 1 ms
 /// per 8 m cell). The assertion is loose (a regression guard, not the budget).
 /// </summary>
        [TestMethod]
        public void PhysicsCellRequest_Cost_SafeShallows()
        {
            TestEnv.RequireInstall();
            TerrainMutants.ClearAll();
            string err;
            SnOctreeSource fresh = SnOctreeSource.Open(TestEnv.OctreeDir, out err);
            Assert.IsNotNull(fresh, err);
            try
            {
                TileSampler s = Sampler(fresh);
                Vec3 c = Local(s, -40, -20, -40);
                var content = new byte[12 * 12 * 12];
                var mat = new byte[content.Length];
                var us = new List<double>();
                var sw = new System.Diagnostics.Stopwatch();
                bool e, f;
                for (int cx = 0; cx < 16; cx++)
                    for (int cz = 0; cz < 16; cz++)
                    {
                        int x0 = ((int)c.X / 8 + cx) * 8 - 1, y0 = ((int)c.Y / 8 + cz) * 8 - 1, z0 = ((int)c.Z / 8) * 8 - 1;
                        sw.Restart();
                        s.ReadContent(0, x0, y0, z0, x0 + 11, y0 + 11, z0 + 11, content, 0, 12, 144, out e, out f);
                        s.ReadMaterial(0, x0, y0, z0, x0 + 11, y0 + 11, z0 + 11, mat, 0, 12, 144, out e);
                        sw.Stop();
                        us.Add(sw.Elapsed.TotalMilliseconds * 1000.0);
                    }
                us.Sort();
                double p50 = us[us.Count / 2], p99 = us[(int)Math.Ceiling(0.99 * us.Count) - 1], max = us[us.Count - 1];
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "physics-cell request (12^3 content + material, LOD 0, Safe Shallows, cold source): p50 {0:F0} us, p99 {1:F0} us, max {2:F0} us, n={3}; batches mapped {4}",
                    p50, p99, max, us.Count, fresh.GetStats().BatchesMapped));
                Assert.IsTrue(p50 < 5000, "a physics-cell request costs " + p50 + " us at the median");
            }
            finally
            {
                fresh.Release();
            }
        }

        [TestMethod]
        public void ReadsClassifyAndLines_AllocateNothing()
        {
            TerrainMutants.ClearAll();
            TileSampler s = Sampler(_syn);
            Vec3 c = Local(s, -60, -80, -60);
            int x = (int)c.X, y = (int)c.Y, z = (int)c.Z;
            var buf = new byte[11 * 11 * 11];
            bool e, f;
            double st, en, cf, ct;
            for (int i = 0; i < 200; i++)
            {
                s.ReadContent(0, x, y, z - 5 + i % 10, x + 10, y + 10, z + 5 + i % 10, buf, 0, 11, 121, out e, out f);
                s.Classify(x, y, z, x + 7, y + 7, z + 7);
                s.IntersectLine(x, y, z + 40, x, y, z - 40, out st, out en, out cf, out ct);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                int dz = i % 10;
                s.ReadContent(0, x, y, z - 5 + dz, x + 3, y + 3, z - 2 + dz, buf, 0, 4, 16, out e, out f);
                s.ReadMaterial(0, x, y, z - 5 + dz, x + 3, y + 3, z - 2 + dz, buf, 0, 4, 16, out e);
                if (i % 10 == 0)
                {
                    s.Classify(x, y, z - dz, x + 7, y + 7, z + 7);
                    s.IntersectLine(x, y, z + 40, x, y, z - 40, out st, out en, out cf, out ct);
                }
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.AreEqual(0, after - before, "allocations in 10,000 ReadContent + ReadMaterial calls");
        }

        [TestMethod]
        public void ParallelReads_EqualSequentialReads()
        {
            TerrainMutants.ClearAll();
            TileSampler s = Sampler(_syn);
            Vec3 c = Local(s, -60, -80, -60);
            const int boxes = 400;
            var rng = new Random(4);
            var mins = new int[boxes][];
            for (int i = 0; i < boxes; i++) mins[i] = new[] { (int)c.X - 40 + rng.Next(80), (int)c.Y - 40 + rng.Next(80), (int)c.Z - 40 + rng.Next(80), rng.Next(3) };
            var want = new byte[boxes][];
            for (int i = 0; i < boxes; i++)
            {
                want[i] = new byte[512];
                bool e, f;
                int l = mins[i][3];
                s.ReadContent(l, mins[i][0] >> l, mins[i][1] >> l, mins[i][2] >> l, (mins[i][0] >> l) + 7, (mins[i][1] >> l) + 7, (mins[i][2] >> l) + 7, want[i], 0, 8, 64, out e, out f);
            }
            var got = new byte[boxes][];
            Parallel.For(0, boxes * 4, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
            {
                int i = n % boxes;
                var b = new byte[512];
                bool e, f;
                int l = mins[i][3];
                s.ReadContent(l, mins[i][0] >> l, mins[i][1] >> l, mins[i][2] >> l, (mins[i][0] >> l) + 7, (mins[i][1] >> l) + 7, (mins[i][2] >> l) + 7, b, 0, 8, 64, out e, out f);
                if (n >= boxes * 3) got[i] = b;
                else CollectionAssert.AreEqual(want[i], b);
            });
            for (int i = 0; i < boxes; i++) CollectionAssert.AreEqual(want[i], got[i]);
        }

        [TestMethod]
        public void LodContent_IsInLodUnits_AndSaturatesOnFarStencils()
        {
            TerrainMutants.ClearAll();
            TileSampler s = Sampler(_syn);
            Vec3 c = Local(s, -59.5, -75, -59.5);
            var b0 = new byte[64];
            var b2 = new byte[64];
            bool e, f;
            int zc = (int)c.Z;
            s.ReadContent(0, (int)c.X, (int)c.Y, zc - 32, (int)c.X, (int)c.Y, zc + 31, b0, 0, 1, 1, out e, out f);
            s.ReadContent(2, (int)c.X >> 2, (int)c.Y >> 2, (zc - 32) >> 2, (int)c.X >> 2, (int)c.Y >> 2, ((zc - 32) >> 2) + 15, b2, 0, 1, 1, out e, out f);
            int mid0 = 0, mid2 = 0;
            for (int i = 0; i < 64; i++) if (b0[i] != 0 && b0[i] != 255) mid0++;
            for (int i = 0; i < 16; i++)
            {
                if (b2[i] != 0 && b2[i] != 255)
                {
                    mid2++;
                    Assert.IsTrue(b2[i] >= 95 && b2[i] <= 159, "LOD 2 near-surface bytes are D/4: " + b2[i]);
                }
            }
            Assert.IsTrue(mid0 >= 1 && mid0 <= 4, "LOD 0: a thin band of partial bytes (" + mid0 + ")");
            Assert.IsTrue(mid2 >= 1, "LOD 2 still sees the surface");
        }
    }
}
