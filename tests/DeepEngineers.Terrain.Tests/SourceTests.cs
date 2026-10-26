using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>SnOctreeSource and SnSourceManifest against synthetic batch files and the user's install (read-only).</summary>
    [TestClass]
    public class SourceTests
    {
        private const int N = SnOctreeFormat.BatchVoxels;
        private const int Bx = 12, By = 18, Bz = 12;

        private static string NewSyntheticDir(out byte[] type, out byte[] density)
        {
            type = new byte[N * N * N];
            density = new byte[N * N * N];
            SyntheticOctrees.StandardSurface(type, density);
            string dir = TestEnv.NewTempDir("src");
            SyntheticOctrees.WriteBatch(dir, Bx, By, Bz, SyntheticOctrees.EncodeBatch(Bx, By, Bz, type, density));
            return dir;
        }

        private static SnOctreeSource Open(string dir)
        {
            string err;
            SnOctreeSource s = SnOctreeSource.Open(dir, out err);
            Assert.IsNotNull(s, err);
            return s;
        }

        [TestMethod]
        public void Synthetic_RasterAndVoxelReadsEqualTheEncodedContent()
        {
            TerrainMutants.ClearAll();
            byte[] type, density;
            string dir = NewSyntheticDir(out type, out density);
            SnOctreeSource src = Open(dir);
            try
            {
                var t = new byte[N * N * N];
                var d = new byte[N * N * N];
                var l = new byte[N * N * N];
                SnBatchSummary s;
                Assert.IsTrue(SnBatchRaster.Rasterize(src, Bx, By, Bz, t, d, l, out s));
                CollectionAssert.AreEqual(type, t, "type raster");
                CollectionAssert.AreEqual(density, d, "density raster");
                Assert.IsTrue(s.FilePresent && !s.Bad);
                Assert.AreEqual(0, l[SyntheticOctrees.Idx(140, 10, 140)], "the no-data octree has leaf size 0");
                Assert.IsTrue(s.LeafSizes.ContainsKey(1) && s.NearSizes.ContainsKey(1), "the surface is made of size-1 near leaves");
                Assert.IsTrue(s.Crossings.Total > 20000 && s.Crossings.BothDistance > 0);
                var rng = new Random(5);
                for (int i = 0; i < 20000; i++)
                {
                    int x = rng.Next(N), y = rng.Next(N), z = rng.Next(N);
                    byte tt, dd;
                    int leaf;
                    bool has = src.TrySampleVoxel(Bx * N + x, By * N + y, Bz * N + z, out tt, out dd, out leaf);
                    int k = SyntheticOctrees.Idx(x, y, z);
                    Assert.AreEqual(l[k] != 0, has, "data flag at " + x + "," + y + "," + z);
                    Assert.AreEqual(type[k], tt);
                    Assert.AreEqual(density[k], dd);
                    Assert.AreEqual(l[k], leaf);
                }
                byte t0, d0;
                int l0;
                Assert.IsFalse(src.TrySampleVoxel(Bx * N + 3, (By + 1) * N + 3, Bz * N + 3, out t0, out d0, out l0), "missing batch = no data");
                Assert.IsFalse(src.TrySampleVoxel(-1, 5, 5, out t0, out d0, out l0), "outside the world = no data");
                Assert.IsFalse(src.TrySampleVoxel(5, SnOctreeFormat.VoxelsY, 5, out t0, out d0, out l0));
                SnSourceStats st = src.GetStats();
                Assert.AreEqual(1, st.BatchesMapped);
                Assert.AreEqual(0, st.BadBatches);
            }
            finally
            {
                src.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        /// <summary>One octree whose root has 8 leaf children; only the child of octant (x = 1, y = 0, z = 0) is solid.</summary>
        private static byte[] OneOctantFile(int bx, int by, int bz)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(4);
            for (int i = 0; i < 125; i++)
            {
                int ix = i / 25, iy = i / 5 % 5, iz = i % 5;
                if (bx * 5 + ix >= 128 || by * 5 + iy >= 100 || bz * 5 + iz >= 128) continue;
                if (i != 0) { w.Write((ushort)0); continue; }
                w.Write((ushort)9);
                w.Write(new byte[] { 0, 0, 1, 0 });                    // root, firstChild = 1
                for (int c = 0; c < 8; c++)
                    w.Write(c == 4 ? new byte[] { 5, 0, 0, 0 } : new byte[] { 0, 0, 0, 0 });   // slot 4 = 4·1 + 2·0 + 0
            }
            return ms.ToArray();
        }

        [TestMethod]
        public void ChildOrder_XIsTheHighBit_AndMutantM1dMovesIt()
        {
            TerrainMutants.ClearAll();
            string dir = TestEnv.NewTempDir("child");
            SyntheticOctrees.WriteBatch(dir, Bx, By, Bz, OneOctantFile(Bx, By, Bz));
            SnOctreeSource src = Open(dir);
            try
            {
                int vx = Bx * N, vy = By * N, vz = Bz * N;
                byte t, d;
                int leaf;
                Assert.IsTrue(src.TrySampleVoxel(vx + 20, vy + 3, vz + 3, out t, out d, out leaf));
                Assert.AreEqual(5, t, "octant x = 1 is solid");
                Assert.AreEqual(16, leaf);
                Assert.IsTrue(src.TrySampleVoxel(vx + 3, vy + 3, vz + 20, out t, out d, out leaf));
                Assert.AreEqual(0, t, "octant z = 1 is empty");
                Assert.AreEqual(OctreeState.Mixed, src.GetOctreeState(Bx * 5, By * 5, Bz * 5));
                TerrainMutants.ChildOrderZYX = true;
                Assert.IsTrue(src.TrySampleVoxel(vx + 3, vy + 3, vz + 20, out t, out d, out leaf));
                Assert.AreEqual(5, t, "S4.M1d reads the slot of x as z");
            }
            finally
            {
                TerrainMutants.ClearAll();
                src.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        [TestMethod]
        public void OctreeStates_FollowTheStatsClasses()
        {
            TerrainMutants.ClearAll();
            string dir = TestEnv.NewTempDir("states");
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(4);
            for (int i = 0; i < 125; i++)
            {
                switch (i)
                {
                    case 0: w.Write((ushort)0); break;                                         // no nodes: Empty
                    case 1: w.Write((ushort)1); w.Write(new byte[] { 3, 0, 0, 0 }); break;     // far solid: Solid
                    case 2: w.Write((ushort)1); w.Write(new byte[] { 0, 0, 0, 0 }); break;     // far empty: Empty
                    case 3: w.Write((ushort)1); w.Write(new byte[] { 3, 140, 0, 0 }); break;   // near root leaf: Mixed
                    default: w.Write((ushort)1); w.Write(new byte[] { 2, 0, 0, 0 }); break;
                }
            }
            SyntheticOctrees.WriteBatch(dir, Bx, By, Bz, ms.ToArray());
            SnOctreeSource src = Open(dir);
            try
            {
                int ox = Bx * 5, oy = By * 5, oz = Bz * 5;
                Assert.AreEqual(OctreeState.Empty, src.GetOctreeState(ox, oy, oz + 0));
                Assert.AreEqual(OctreeState.Solid, src.GetOctreeState(ox, oy, oz + 1));
                Assert.AreEqual(OctreeState.Empty, src.GetOctreeState(ox, oy, oz + 2));
                Assert.AreEqual(OctreeState.Mixed, src.GetOctreeState(ox, oy, oz + 3));
                Assert.AreEqual(OctreeState.Missing, src.GetOctreeState(ox, oy + 5, oz), "no batch file");
                Assert.AreEqual(OctreeState.Missing, src.GetOctreeState(-1, 0, 0), "outside the world");
                Assert.AreEqual(OctreeState.Missing, src.GetOctreeState(0, 100, 0));
                byte t, d;
                int leaf;
                Assert.IsTrue(src.TrySampleVoxel(Bx * N + 1, By * N + 1, Bz * N + 32 + 7, out t, out d, out leaf));
                Assert.AreEqual(32, leaf, "a one-node octree is one 32 m leaf");
            }
            finally
            {
                src.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        [TestMethod]
        public void BadBatches_AreMissing_AndLoggedOnce()
        {
            TerrainMutants.ClearAll();
            byte[] type, density;
            string dir = NewSyntheticDir(out type, out density);
            byte[] good = SyntheticOctrees.EncodeBatch(Bx, By, Bz, type, density);
            byte[] v3 = (byte[])good.Clone();
            v3[0] = 3;
            SyntheticOctrees.WriteBatch(dir, 11, 18, 12, v3);                                // version 3
            byte[] longer = new byte[good.Length + 1];
            Buffer.BlockCopy(good, 0, longer, 0, good.Length);
            SyntheticOctrees.WriteBatch(dir, 13, 18, 12, longer);                           // not consumed exactly
            byte[] shorter = new byte[good.Length - 3];
            Buffer.BlockCopy(good, 0, shorter, 0, shorter.Length);
            SyntheticOctrees.WriteBatch(dir, 12, 17, 12, shorter);                          // truncated
            var lines = new List<string>();
            Action<string> old = TerrainLog.Sink;
            TerrainLog.Sink = l => { lock (lines) lines.Add(l); };
            SnOctreeSource src = Open(dir);
            try
            {
                foreach (int[] b in new[] { new[] { 11, 18, 12 }, new[] { 13, 18, 12 }, new[] { 12, 17, 12 } })
                {
                    for (int rep = 0; rep < 3; rep++)
                    {
                        byte t, d;
                        int leaf;
                        Assert.IsFalse(src.TrySampleVoxel(b[0] * N + 50, b[1] * N + 50, b[2] * N + 50, out t, out d, out leaf));
                        Assert.AreEqual(OctreeState.Missing, src.GetOctreeState(b[0] * 5 + 1, b[1] * 5 + 1, b[2] * 5 + 1));
                    }
                    var tt = new byte[N * N * N];
                    SnBatchSummary s;
                    Assert.IsTrue(SnBatchRaster.Rasterize(src, b[0], b[1], b[2], tt, new byte[tt.Length], new byte[tt.Length], out s));
                    Assert.IsTrue(s.Bad && s.FilePresent && s.Solid == 0);
                }
                Assert.AreEqual(3, src.GetStats().BadBatches);
                Assert.AreEqual(3, lines.Count(l => l.StartsWith("DE.Terrain BATCH-BAD ", StringComparison.Ordinal)), string.Join("\n", lines));
                Assert.IsTrue(lines.Any(l => l.Contains("version 3")));
                Assert.IsTrue(lines.Any(l => l.Contains("not consumed exactly")));
            }
            finally
            {
                TerrainLog.Sink = old;
                src.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        [TestMethod]
        public void RefCount_LastReleaseWaitsForReaders_ThenAnswersEmpty()
        {
            TerrainMutants.ClearAll();
            byte[] type, density;
            string dir = NewSyntheticDir(out type, out density);
            SnOctreeSource src = Open(dir);
            byte t, d;
            int leaf;
            int vx = Bx * N + 70, vy = By * N + 10, vz = Bz * N + 70;      // deep inside the solid
            Assert.IsTrue(src.TrySampleVoxel(vx, vy, vz, out t, out d, out leaf));
            src.AddRef();
            Assert.AreEqual(2, src.RefCount);
            src.Release();
            Assert.IsFalse(src.IsClosing);
            Assert.IsTrue(src.BeginRead(), "reader enters");
            var closer = Task.Run(() => src.Release());
            Assert.IsFalse(closer.Wait(300), "the last Release must wait for the in-flight reader");
            Assert.IsTrue(src.IsClosing);
            Assert.IsFalse(src.TrySampleVoxel(vx, vy, vz, out t, out d, out leaf), "a new reader sees closing");
            src.EndRead();
            Assert.IsTrue(closer.Wait(5000), "Release completes once the reader left");
            Assert.IsFalse(src.TrySampleVoxel(vx, vy, vz, out t, out d, out leaf), "after close: no data");
            Assert.AreNotEqual(OctreeState.Missing, src.GetOctreeState(Bx * 5 + 2, By * 5, Bz * 5 + 2), "indexed states stay readable");
            Assert.AreEqual(OctreeState.Mixed, src.GetOctreeState(3, 15, 3), "an unindexed batch answers Mixed while closing");
            var field = new SnField(src);
            bool same;
            Assert.AreEqual(-1f, field.Sample(-60, -100, -60, out same));
            Assert.ThrowsException<ObjectDisposedException>(() => src.AddRef());
            src.Release();                                                   // extra release: ignored
            TestEnv.DeleteDir(dir);
        }

        [TestMethod]
        public void ConcurrentFirstTouch_GivesTheSequentialAnswers()
        {
            TerrainMutants.ClearAll();
            byte[] type, density;
            string dir = NewSyntheticDir(out type, out density);
            // more batches so several threads race on first touches
            for (int k = 0; k < 6; k++)
                SyntheticOctrees.WriteBatch(dir, 10 + k % 3, 18, 10 + k / 3, SyntheticOctrees.EncodeBatch(10 + k % 3, 18, 10 + k / 3, type, density));
            var pts = new List<double[]>();
            var rng = new Random(9);
            for (int i = 0; i < 40000; i++)
                pts.Add(new[] { -448 + rng.NextDouble() * 480, -160 + rng.NextDouble() * 160, -448 + rng.NextDouble() * 320 });
            SnOctreeSource a = Open(dir), b = Open(dir);
            try
            {
                var fa = new SnField(a);
                var want = new float[pts.Count];
                for (int i = 0; i < pts.Count; i++) { bool s; want[i] = fa.Sample(pts[i][0], pts[i][1], pts[i][2], out s); }
                var fb = new SnField(b);
                var got = new float[pts.Count];
                Parallel.For(0, pts.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                {
                    bool s;
                    got[i] = fb.Sample(pts[i][0], pts[i][1], pts[i][2], out s);
                });
                CollectionAssert.AreEqual(want, got);
                Assert.AreEqual(a.GetStats().BatchesMapped, b.GetStats().BatchesMapped, "each batch mapped once");
            }
            finally
            {
                a.Release();
                b.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        [TestMethod]
        public void SampleReads_AllocateNothingInTheSteadyState()
        {
            TerrainMutants.ClearAll();
            byte[] type, density;
            string dir = NewSyntheticDir(out type, out density);
            SnOctreeSource src = Open(dir);
            try
            {
                var field = new SnField(src);
                bool s;
                float acc = 0;
                for (int i = 0; i < 1000; i++) acc += field.Sample(-60 + i * 0.01, -80, -60, out s);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++)
                {
                    acc += field.Sample(-60 + i * 0.003, -80 + i * 0.001, -60, out s);
                    byte t, d;
                    int leaf;
                    src.TrySampleVoxel(Bx * N + i % 160, By * N + 80, Bz * N + 3, out t, out d, out leaf);
                }
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.AreEqual(0, after - before, "allocations in 10,000 sample reads");
                Assert.IsFalse(float.IsNaN(acc));
            }
            finally
            {
                src.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        // ------------------------------------------------------------------------------------------------ manifest
        [TestMethod]
        public void Manifest_PinsEqualSiteV1()
        {
            Assert.AreEqual(SiteV1.SnManifestBuildId, SnSourceManifest.ExpectedBuildId);
            Assert.AreEqual(20241558, SnSourceManifest.ExpectedBuildId);
            Assert.AreEqual(5416, SnSourceManifest.ExpectedFiles);
            Assert.AreEqual(1203085204L, SnSourceManifest.ExpectedBytes);
            Assert.AreEqual(7, SnSourceManifest.ExpectedRowMin);
            Assert.AreEqual(19, SnSourceManifest.ExpectedRowMax);
            Assert.AreEqual(0x2cc277ce91763285UL, SnSourceManifest.ExpectedHash);
        }

        [TestMethod]
        public void Manifest_OfTheInstall_Matches()
        {
            TestEnv.RequireInstall();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SnSourceManifest m = SnSourceManifest.Measure(TestEnv.SubnauticaDir);
            Console.WriteLine(m.LogLine() + " (" + sw.ElapsedMilliseconds + " ms)");
            string field, found, expected;
            Assert.IsNull(m.Error, m.Error);
            Assert.AreEqual(5416, m.Files, "batch files in the install (REPORT if different)");
            Assert.AreEqual(1203085204L, m.Bytes, "batch bytes in the install (REPORT if different)");
            Assert.IsTrue(m.Matches(out field, out found, out expected), field + " found=" + found + " expected=" + expected);
            Assert.IsTrue(m.LogLine().StartsWith("DE.Terrain SOURCE path=", StringComparison.Ordinal));
            StringAssert.Contains(m.LogLine(), " build=20241558 files=5416 bytes=1203085204 rows=7..19 manifestHash=0x2cc277ce91763285 index=ok");
            Assert.IsFalse(m.Matches(1, out field, out found, out expected), "S4.M5: expected files + 1");
            Assert.AreEqual("files", field);
            Assert.AreEqual("5416", found);
            Assert.AreEqual("5417", expected);
        }

        [TestMethod]
        public void Manifest_OfASyntheticInstall_NamesTheFirstDifference()
        {
            string root = TestEnv.NewTempDir("manifest");
            try
            {
                string sn = Path.Combine(root, "steamapps", "common", "Subnautica");
                string oct = SnSourceManifest.OctreeDirOf(sn);
                Directory.CreateDirectory(oct);
                string field, found, expected;
                SnSourceManifest m = SnSourceManifest.Measure(sn);
                Assert.IsFalse(m.Matches(out field, out found, out expected));
                Assert.AreEqual("index", field, "index.txt missing comes first");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(oct), "index.txt"), "0\n4096 3200 4096\n128 100 128\n32\n5 5 5\n0\n");
                m = SnSourceManifest.Measure(sn);
                Assert.IsTrue(m.IndexOk);
                m.Matches(out field, out found, out expected);
                Assert.AreEqual("build", field);
                Assert.AreEqual("-1", found);
                File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_264710.acf"),
                    "\"AppState\"\n{\n\t\"appid\"\t\t\"264710\"\n\t\"buildid\"\t\t\"20241558\"\n\t\"TargetBuildID\"\t\t\"1\"\n}\n");
                File.WriteAllBytes(Path.Combine(oct, "compiled-batch-12-18-12.optoctrees"), new byte[] { 4, 0, 0, 0 });
                File.WriteAllText(Path.Combine(oct, "notes.txt"), "x");
                m = SnSourceManifest.Measure(sn);
                Assert.AreEqual(20241558, m.BuildId);
                Assert.AreEqual(1, m.Files);
                Assert.AreEqual(4, m.Bytes);
                Assert.AreEqual(1, m.OtherFiles);
                Assert.AreEqual(18, m.RowMin);
                Assert.IsFalse(m.Matches(out field, out found, out expected));
                Assert.AreEqual("files", field);
                // the listing hash is FNV-1a 64 over "<name>\t<size>\n"
                byte[] line = System.Text.Encoding.UTF8.GetBytes("compiled-batch-12-18-12.optoctrees\t4\n");
                Assert.AreEqual(SiteV1.Fnv1a64(line), m.Hash);
                SnSourceManifest gone = SnSourceManifest.Measure(Path.Combine(root, "nowhere"));
                gone.Matches(out field, out found, out expected);
                Assert.AreEqual("folder", field, "a missing folder is reported as such");
                Assert.IsNotNull(gone.Error);
                Assert.IsFalse(SnSourceManifest.Measure(null).Matches(out field, out found, out expected));
                Assert.AreEqual("folder", field);
            }
            finally
            {
                TestEnv.DeleteDir(root);
            }
        }

        [TestMethod]
        public void Manifest_ResolveOrder()
        {
            Assert.AreEqual(@"D:\Games\SN", SnSourceManifest.ResolveSubnauticaDir(@" D:\Games\SN "));
            string env = Environment.GetEnvironmentVariable("DEEPENG_SUBNAUTICA");
            string want = string.IsNullOrWhiteSpace(env) ? SnSourceManifest.SteamDefaultDir : env.Trim();
            Assert.AreEqual(want, SnSourceManifest.ResolveSubnauticaDir(null));
            Assert.AreEqual(want, SnSourceManifest.ResolveSubnauticaDir("  "));
        }
    }
}
