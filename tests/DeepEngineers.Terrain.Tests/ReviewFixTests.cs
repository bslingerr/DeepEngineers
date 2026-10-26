using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>
 /// S4 review fixes of the core: finding 3 (an I/O failure at first touch is retried with a backoff, reads
 /// in between are counted as failed, a persistent failure ends bad and logged) and finding 4 (the background prefetch
 /// indexes and pre-faults the planned batches, runs the I/O retries, stops on cancel or close). Synthetic batch files;
 /// a sharing violation is made by holding the file open with <c>FileShare.None</c>. One read-only check on the user's
 /// install (Inconclusive without it).
 /// </summary>
    [TestClass]
    public class ReviewFixTests
    {
        private const int N = SnOctreeFormat.BatchVoxels;
        private const int Bx = 12, By = 18, Bz = 12;
        private static int Vx { get { return Bx * N + 70; } }
        private static int Vy { get { return By * N + 10; } }   // deep inside the solid of StandardSurface
        private static int Vz { get { return Bz * N + 70; } }

        private static string NewDir(out string batchPath)
        {
            var type = new byte[N * N * N];
            var density = new byte[N * N * N];
            SyntheticOctrees.StandardSurface(type, density);
            string dir = TestEnv.NewTempDir("rf");
            batchPath = SyntheticOctrees.WriteBatch(dir, Bx, By, Bz, SyntheticOctrees.EncodeBatch(Bx, By, Bz, type, density));
            return dir;
        }

        private static SnOctreeSource Open(string dir)
        {
            string err;
            SnOctreeSource s = SnOctreeSource.Open(dir, out err);
            Assert.IsNotNull(s, err);
            return s;
        }

        private static List<string> Capture(out Action<string> old)
        {
            var lines = new List<string>();
            old = TerrainLog.Sink;
            TerrainLog.Sink = l => { lock (lines) lines.Add(l); };
            return lines;
        }

        private static bool Read(SnOctreeSource src)
        {
            byte t, d;
            int leaf;
            return src.TrySampleVoxel(Vx, Vy, Vz, out t, out d, out leaf);
        }

        [TestMethod]
        public void IoFailure_IsRetriedAfterTheBackoff_AndRecovers()
        {
            TerrainMutants.ClearAll();
            string path;
            string dir = NewDir(out path);
            Action<string> old;
            List<string> lines = Capture(out old);
            SnOctreeSource src = Open(dir);
            src.IoRetryBaseMs = 150;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert.IsFalse(Read(src), "a sharing violation: no data for now");
                    SnSourceStats s = src.GetStats();
                    Assert.AreEqual(1, s.IoFailures);
                    Assert.AreEqual(1, s.IoRetrying);
                    Assert.AreEqual(0, s.BadBatches, "an I/O failure is not bad at once");
                    Assert.IsTrue(s.FailedReads >= 1, "the read answered without data is counted");
                    Assert.AreEqual(SnOctreeSource.StateIoRetry, src.ReadyOf(SnOctreeFormat.BatchSlot(Bx, By, Bz)));
                    Assert.AreEqual(OctreeState.Missing, src.GetOctreeState(Bx * 5 + 1, By * 5, Bz * 5 + 1), "states of a waiting batch are not read");
                    Assert.IsFalse(Read(src), "within the backoff: still no data, no new attempt");
                    Assert.AreEqual(1, src.GetStats().IoFailures);
                }
                System.Threading.Thread.Sleep(250);
                Assert.IsTrue(Read(src), "the retry after the backoff maps the file");
                SnSourceStats e = src.GetStats();
                Assert.AreEqual(0, e.IoRetrying);
                Assert.AreEqual(1, e.IoRecovered);
                Assert.AreEqual(0, e.BadBatches);
                Assert.AreEqual(1, e.BatchesIndexed, "indexed counted once per batch");
                Assert.IsTrue(lines.Any(l => l.StartsWith("DE.Terrain BATCH-IO ", StringComparison.Ordinal) && l.Contains("attempt 1/6")), string.Join("\n", lines));
                Assert.IsTrue(lines.Any(l => l.Contains("recovered after 1 failed attempt")), string.Join("\n", lines));
                Assert.IsFalse(lines.Any(l => l.StartsWith("DE.Terrain BATCH-BAD", StringComparison.Ordinal)));
            }
            finally
            {
                TerrainLog.Sink = old;
                src.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        [TestMethod]
        public void IoFailure_ThatPersists_EndsBad_AfterTheLastAttempt()
        {
            TerrainMutants.ClearAll();
            string path;
            string dir = NewDir(out path);
            Action<string> old;
            List<string> lines = Capture(out old);
            SnOctreeSource src = Open(dir);
            src.IoRetryBaseMs = 5;
            src.IoMaxAttempts = 3;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var sw = Stopwatch.StartNew();
                    while (src.BadBatchCount == 0 && sw.ElapsedMilliseconds < 5000)
                    {
                        Assert.IsFalse(Read(src));
                        System.Threading.Thread.Sleep(5);
                    }
                }
                SnSourceStats s = src.GetStats();
                Assert.AreEqual(1, s.BadBatches);
                Assert.AreEqual(3, s.IoFailures, "exactly IoMaxAttempts attempts");
                Assert.AreEqual(0, s.IoRetrying);
                Assert.IsFalse(Read(src), "bad for the session: no new attempt once the file is readable again");
                Assert.AreEqual(3, src.GetStats().IoFailures);
                Assert.AreEqual(SnOctreeSource.StateBad, src.ReadyOf(SnOctreeFormat.BatchSlot(Bx, By, Bz)));
                Assert.AreEqual(1, lines.Count(l => l.StartsWith("DE.Terrain BATCH-BAD ", StringComparison.Ordinal) && l.Contains(" io ") && l.Contains("after 3 attempts")),
                                string.Join("\n", lines));
                Assert.AreEqual(2, lines.Count(l => l.StartsWith("DE.Terrain BATCH-IO ", StringComparison.Ordinal)), string.Join("\n", lines));
            }
            finally
            {
                TerrainLog.Sink = old;
                src.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        [TestMethod]
        public void Prefetch_IndexesAndFaultsThePlannedBatches_BeforeAnyReader()
        {
            TerrainMutants.ClearAll();
            string path;
            string dir = NewDir(out path);
            Action<string> old;
            List<string> lines = Capture(out old);
            SnOctreeSource src = Open(dir);
            try
            {
                int data = SnOctreeFormat.BatchSlot(Bx, By, Bz), missing = SnOctreeFormat.BatchSlot(Bx, By - 1, Bz);
                SnPrefetcher pf = SnPrefetcher.Start(src, () => new[] { data, missing }, fault: true, lowPriority: true);
                Assert.IsTrue(pf.Wait(10000), "the prefetch ends");
                Assert.AreEqual(SnPrefetcher.StateDone, pf.State, pf.Text);
                Assert.AreEqual(2, pf.Planned);
                Assert.AreEqual(2, pf.Done);
                Assert.AreEqual(1, pf.Data);
                Assert.AreEqual(1, pf.Missing);
                long len = new FileInfo(path).Length;
                Assert.AreEqual((len + 4095) / 4096, pf.PagesTouched, "one touch per 4 KB page of the mapped file");
                Assert.AreEqual(2, src.GetStats().BatchesIndexed);
                Assert.IsTrue(Read(src));
                Assert.AreEqual(2, src.GetStats().BatchesIndexed, "the reader found the batch indexed");
                Assert.IsTrue(lines.Any(l => l.StartsWith("DE.Terrain PREFETCH start batches=2", StringComparison.Ordinal)), string.Join("\n", lines));
                Assert.IsTrue(lines.Any(l => l.StartsWith("DE.Terrain PREFETCH done ", StringComparison.Ordinal)), string.Join("\n", lines));
            }
            finally
            {
                TerrainLog.Sink = old;
                src.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        [TestMethod]
        public void Prefetch_RunsTheIoRetries_SoNoReaderSeesTheFailure()
        {
            TerrainMutants.ClearAll();
            string path;
            string dir = NewDir(out path);
            Action<string> old;
            Capture(out old);
            SnOctreeSource src = Open(dir);
            src.IoRetryBaseMs = 40;
            try
            {
                SnPrefetcher pf;
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    pf = SnPrefetcher.Start(src, () => new[] { SnOctreeFormat.BatchSlot(Bx, By, Bz) }, fault: true, lowPriority: false);
                    var sw = Stopwatch.StartNew();
                    while (src.GetStats().IoFailures == 0 && sw.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(5);
                    Assert.IsTrue(src.GetStats().IoFailures >= 1, "the first attempt failed while the file was held");
                }
                Assert.IsTrue(pf.Wait(10000), pf.Text);
                Assert.AreEqual(SnPrefetcher.StateDone, pf.State, pf.Text);
                Assert.AreEqual(1, pf.Data, pf.Text);
                Assert.IsTrue(pf.IoRetried >= 1, pf.Text);
                SnSourceStats s = src.GetStats();
                Assert.AreEqual(1, s.IoRecovered);
                Assert.AreEqual(0, s.BadBatches);
                Assert.AreEqual(0, s.FailedReads, "no reader was answered without data: the plugin keeps the fail-safe off");
                Assert.IsTrue(Read(src));
            }
            finally
            {
                TerrainLog.Sink = old;
                src.Release();
                TestEnv.DeleteDir(dir);
            }
        }

        [TestMethod]
        public void Prefetch_StopsOnCancel_AndWhenTheSourceCloses()
        {
            TerrainMutants.ClearAll();
            string path;
            string dir = NewDir(out path);
            Action<string> old;
            Capture(out old);
            SnOctreeSource src = Open(dir);
            try
            {
                int[] all = Enumerable.Range(0, SnOctreeFormat.BatchCount).ToArray();
                SnPrefetcher a = SnPrefetcher.Start(src, () => all, fault: true, lowPriority: true);
                a.Cancel();
                Assert.IsTrue(a.Wait(10000), a.Text);
                Assert.IsTrue(a.State == SnPrefetcher.StateCancelled || a.State == SnPrefetcher.StateDone, a.Text);
                SnPrefetcher b = SnPrefetcher.Start(src, () => { System.Threading.Thread.Sleep(100); return all; }, fault: true, lowPriority: true);
                src.Release();                                   // the last reference: closing while the plan runs
                Assert.IsTrue(b.Wait(10000), b.Text);
                Assert.AreEqual(SnPrefetcher.StateCancelled, b.State, b.Text);
                Assert.AreEqual(0, b.Done, "nothing indexed after the close");
                SnPrefetcher c = SnPrefetcher.Start(src, () => { throw new InvalidOperationException("plan failed"); }, fault: false, lowPriority: false);
                Assert.IsTrue(c.Wait(10000));
                Assert.AreEqual(SnPrefetcher.StateFailed, c.State);
                StringAssert.Contains(c.Error, "plan failed");
            }
            finally
            {
                TerrainLog.Sink = old;
                TestEnv.DeleteDir(dir);
            }
        }

        /// <summary>
 /// The user's install (read-only): the 125 batches nearest the Safe Shallows anchor prefetched and faulted on the
 /// background thread; none bad, every later read finds its batch indexed. Reports the time.
 /// With <c>DEEPENG_PREFETCH_ALL=1</c> every batch slot is prefetched (the plugin's worst case; ~1.2 GB).
 /// </summary>
        [TestMethod]
        public void Prefetch_RealInstall_NearestBatches()
        {
            TestEnv.RequireInstall();
            TerrainMutants.ClearAll();
            string err;
            SnOctreeSource src = SnOctreeSource.Open(TestEnv.OctreeDir, out err);
            Assert.IsNotNull(src, err);
            Action<string> old;
            List<string> lines = Capture(out old);
            try
            {
                bool all = Environment.GetEnvironmentVariable("DEEPENG_PREFETCH_ALL") == "1";
                var slots = new List<int>();
                var keys = new List<double>();
                for (int bx = 0; bx < SnOctreeFormat.BatchesX; bx++)
                    for (int by = 0; by < SnOctreeFormat.BatchesY; by++)
                        for (int bz = 0; bz < SnOctreeFormat.BatchesZ; bz++)
                        {
                            double dx = SnOctreeFormat.OriginX + bx * N + 80, dy = SnOctreeFormat.OriginY + by * N + 80 + 17.2, dz = SnOctreeFormat.OriginZ + bz * N + 80;
                            slots.Add(SnOctreeFormat.BatchSlot(bx, by, bz));
                            keys.Add(dx * dx + dy * dy + dz * dz);
                        }
                int[] order = slots.ToArray();
                Array.Sort(keys.ToArray(), order);
                int[] plan = all ? order : order.Take(125).ToArray();
                SnPrefetcher pf = SnPrefetcher.Start(src, () => plan, fault: true, lowPriority: true);
                Assert.IsTrue(pf.Wait(all ? 600000 : 120000), pf.Text);
                Assert.AreEqual(SnPrefetcher.StateDone, pf.State, pf.Text);
                Assert.AreEqual(0, pf.Bad, pf.Text);
                SnSourceStats s = src.GetStats();
                Assert.AreEqual(0, s.BadBatches);
                Assert.AreEqual(0, s.IoFailures);
                long indexed = s.BatchesIndexed;
                var field = new SnField(src);
                bool same;
                field.Sample(0.3, -17.2, 0.4, out same);
                Assert.AreEqual(indexed, src.GetStats().BatchesIndexed, "the reader found the anchor's batch indexed");
                Console.WriteLine("PREFETCH real install: " + pf.Text + "; bytesMapped " + s.BytesMapped);
            }
            finally
            {
                TerrainLog.Sink = old;
                src.Release();
            }
        }
    }
}
