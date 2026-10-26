using System;
using System.Collections.Generic;
using System.IO;
using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoticeLayer = DeepEngineers.Wire.S8aConventions.NoticeLayer;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S8a, the game-free rules of the Subnautica side (S8aRules.cs; ROADMAP "### S8a" SN plugin, CONTRACT §3.3): the store
 /// view (one reader, newest version wins, counted answers), the notice file check (parse, size, FNV against the
 /// manifest), the clipmap cell ranges (ClipmapLevel.GetCellRange's formula) and the per-notice cap, the reload tracker
 /// (ackSeq when scheduled, appliedSeq when built, in seq order; the 5 s retry and the 10 s give-up), the S8a.M4 rule and
 /// the mutant names known to DevMutants.
 /// </summary>
    [TestClass]
    public class S8aTests
    {
        [TestMethod]
        public void StoreView_NewestVersionWins_AnswersCounted()
        {
            var v = new S8aStoreView();
            Assert.IsNull(v.PathOf(12, 18, 12));
            Assert.AreEqual(1, v.Misses);
            Assert.IsTrue(v.Set(new S8aServed { Bx = 12, By = 18, Bz = 12, Version = 2, Path = "p2" }));
            Assert.IsFalse(v.Set(new S8aServed { Bx = 12, By = 18, Bz = 12, Version = 1, Path = "p1" }), "an older version is ignored");
            Assert.IsTrue(v.Set(new S8aServed { Bx = 12, By = 18, Bz = 12, Version = 2, Path = "p2b" }), "the same version replaces (a re-read manifest)");
            Assert.AreEqual("p2b", v.PathOf(12, 18, 12));
            Assert.AreEqual(1, v.Answers);
            Assert.AreEqual(1, v.Count);
            Assert.IsNull(S8aNoticeRule.Serve("p", true), "S8a.M4 withholds the store's answer");
            Assert.AreEqual("p", S8aNoticeRule.Serve("p", false));
            v.Clear();
            Assert.AreEqual(0, v.Count);
        }

        [TestMethod]
        public void StoreView_LoadManifest_TakesValidatedFilesOnly_ReplaceDropsTheRest()
        {
            var m = new S8aManifest { Layer = S8aStore.DrillLayer, Key = 0xAB, Hash = 1 };
            m.Set(new S8aManifest.Entry { Bx = 1, By = 2, Bz = 3, Version = 4, Bytes = 100, Fnv = 7, RelativePath = "a\\b" });
            m.Set(new S8aManifest.Entry { Bx = 5, By = 6, Bz = 7, Version = 1, Bytes = 50, Fnv = 8, RelativePath = "a\\c" });
            m.Set(new S8aManifest.Entry { Bx = 9, By = 9, Bz = 9, Version = 1, Bytes = 50, Fnv = 9, RelativePath = "a\\missing" });
            var v = new S8aStoreView();
            Assert.IsTrue(v.Set(new S8aServed { Bx = 20, By = 18, Bz = 20, Version = 9, Path = "gone" }), "served before the manifest");
            Assert.IsTrue(v.Set(new S8aServed { Bx = 1, By = 2, Bz = 3, Version = 6, Path = "newer" }), "a newer notice than the manifest's version");
            // the worker's validation (exists, parses, size and FNV): only a\b passes
            int n = v.LoadManifest(m, "R", (p, e) => p.EndsWith("b") && e.Fnv == 7);
            Assert.AreEqual(0, n, "merge: the newer served version wins over the manifest's older one; the invalid files are not taken");
            Assert.AreEqual("newer", v.PathOf(1, 2, 3));
            Assert.IsNull(v.PathOf(5, 6, 7), "failed the check: not served");
            Assert.IsNull(v.PathOf(9, 9, 9), "missing: not served");
            n = v.LoadManifest(m, "R", (p, e) => p.EndsWith("b"), replace: true);
            Assert.AreEqual(1, n, "replace: the manifest is the view");
            Assert.AreEqual(Path.Combine("R", "a\\b"), v.PathOf(1, 2, 3), "the manifest's version replaces the newer served one on a reset");
            Assert.IsNull(v.PathOf(20, 18, 20), "a batch the manifest does not name is no longer served");
            Assert.AreEqual(1, v.Count);
            Assert.IsTrue(v.Remove(new Int3Key(1, 2, 3)));
            Assert.IsFalse(v.Remove(new Int3Key(1, 2, 3)));
            Assert.AreEqual(0, v.Count);
        }

        [TestMethod]
        public void Paths_NewestExistingVersion_FallbackWhenAServedFileIsGone()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "s8a-sn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                uint v;
                Assert.IsNull(S8aSnPaths.NewestExisting(tmp, 12, 18, 12, out v));
                File.WriteAllBytes(Path.Combine(tmp, S8aStore.BatchFileName(12, 18, 12, 2)), new byte[] { 4, 0, 0, 0 });
                File.WriteAllBytes(Path.Combine(tmp, S8aStore.BatchFileName(12, 18, 12, 10)), new byte[] { 4, 0, 0, 0 });
                File.WriteAllBytes(Path.Combine(tmp, S8aStore.BatchFileName(12, 18, 13, 11)), new byte[] { 4, 0, 0, 0 });
                string p = S8aSnPaths.NewestExisting(tmp, 12, 18, 12, out v);
                Assert.AreEqual(10u, v, "the highest version of that batch, not another batch's");
                StringAssert.EndsWith(p, S8aStore.BatchFileName(12, 18, 12, 10));
                Assert.IsNull(S8aSnPaths.NewestExisting(Path.Combine(tmp, "nope"), 12, 18, 12, out v), "a missing folder answers null");
            }
            finally { Directory.Delete(tmp, true); }
        }

        [TestMethod]
        public void NoticeRule_ParsesAndChecksSizeAndFnv()
        {
            S8aBatchFile b = S8aBatchFile.CreateUniform(12, 18, 12, 84, 0);
            byte[] file = b.Serialize();
            ulong fnv;
            string err;
            Assert.IsTrue(S8aNoticeRule.Validate(file, 12, 18, 12, null, out fnv, out err), err);
            Assert.AreEqual(LayoutHash.Fnv1a64(file), fnv);
            var good = new S8aManifest.Entry { Bytes = file.Length, Fnv = fnv };
            Assert.IsTrue(S8aNoticeRule.Validate(file, 12, 18, 12, good, out fnv, out err), err);
            Assert.IsFalse(S8aNoticeRule.Validate(file, 12, 18, 12, new S8aManifest.Entry { Bytes = file.Length + 1, Fnv = fnv }, out fnv, out err));
            StringAssert.Contains(err, "size");
            Assert.IsFalse(S8aNoticeRule.Validate(file, 12, 18, 12, new S8aManifest.Entry { Bytes = file.Length, Fnv = fnv ^ 1 }, out fnv, out err));
            StringAssert.Contains(err, "fnv");
            Assert.IsFalse(S8aNoticeRule.Validate(file, 25, 18, 25, null, out fnv, out err), "another id's layout does not consume the file");
            Assert.IsFalse(S8aNoticeRule.Validate(new byte[] { 3, 0, 0, 0 }, 12, 18, 12, null, out fnv, out err), "version 3");
            Assert.IsFalse(S8aNoticeRule.Validate(null, 12, 18, 12, null, out fnv, out err));
        }

        [TestMethod]
        public void CellRange_GetCellRangeFormula_AndTheCap()
        {
            // ClipmapLevel.GetCellRange: FloorDiv(mins - (3 << id), cellSize).. CeilDiv(maxs + 2 * (3 << id), cellSize)
            int[] cmin, cmax;
            S8aCellRange.Range(0, 32, new[] { 1920, 2880, 1920 }, new[] { 1930, 2890, 1930 }, out cmin, out cmax);
            CollectionAssert.AreEqual(new[] { 59, 89, 59 }, cmin, "level 0 min: (1920 - 3) / 32 floored");
            CollectionAssert.AreEqual(new[] { 61, 91, 61 }, cmax, "level 0 max: (1930 + 6) / 32 ceiled");
            S8aCellRange.Range(3, 256, new[] { 1920, 2880, 1920 }, new[] { 1930, 2890, 1930 }, out cmin, out cmax);
            CollectionAssert.AreEqual(new[] { 7, 11, 7 }, cmin, "level 3 min: (1920 - 24) / 256 floored");
            CollectionAssert.AreEqual(new[] { 8, 12, 8 }, cmax, "level 3 max: (1930 + 48) / 256 ceiled");
            Assert.AreEqual(-1, S8aCellRange.FloorDiv(-1, 32));
            Assert.AreEqual(0, S8aCellRange.CeilDiv(-1, 32));
            Assert.AreEqual(1, S8aCellRange.CeilDiv(1, 32));
            // a small box, uncapped: every level's cells: level 0 3x3x3 = 27, level 1 3x3x3 = 27, level 2 3x2x3 = 18 (y: floor((2880-12)/128)
 // = 22.. ceil((2890+24)/128) = 23), level 3 2x2x2 = 8: 80; capped at 64: levels 0-1 kept, levels 2-3 one cell each (27 + 27 + 1 + 1)
            var sizes = new[] { 32, 64, 128, 256 };
            List<S8aCellRange.Cell> small = S8aCellRange.Plan(4, sizes, new[] { 1920, 2880, 1920 }, new[] { 1930, 2890, 1930 }, 0);
            int l0 = 0, l2 = 0, l3 = 0;
            foreach (S8aCellRange.Cell c in small) { if (c.Level == 0) l0++; if (c.Level == 2) l2++; if (c.Level == 3) l3++; }
            Assert.AreEqual(27, l0, "3 x 3 x 3 level-0 cells");
            Assert.AreEqual(18, l2, "3 x 2 x 3 level-2 cells");
            Assert.AreEqual(8, l3, "2 x 2 x 2 level-3 cells");
            Assert.AreEqual(80, small.Count);
            List<S8aCellRange.Cell> capped = S8aCellRange.Plan(4, sizes, new[] { 1920, 2880, 1920 }, new[] { 1930, 2890, 1930 }, 64);
            Assert.AreEqual(56, capped.Count, "27 + 27 + 1 + 1");
            List<S8aCellRange.Cell> wide = S8aCellRange.Plan(4, sizes, new[] { 1920, 2880, 1920 }, new[] { 2079, 3039, 2079 }, 64);
            int w2 = 0, w3 = 0, w0 = 0;
            foreach (S8aCellRange.Cell c in wide) { if (c.Level == 0) w0++; if (c.Level == 2) w2++; if (c.Level == 3) w3++; }
            Assert.AreEqual(1, w2, "capped: one level-2 cell");
            Assert.AreEqual(1, w3, "capped: one level-3 cell");
            Assert.IsTrue(w0 >= 6 * 6 * 6, "level 0 keeps every cell of the box: " + w0);
            List<S8aCellRange.Cell> uncapped = S8aCellRange.Plan(4, sizes, new[] { 1920, 2880, 1920 }, new[] { 2079, 3039, 2079 }, 0);
            Assert.IsTrue(uncapped.Count > wide.Count);
        }

        private static S8aNotice Notice(uint seq, int bx = 12, int by = 18, int bz = 12, uint version = 1)
        {
            S8aNotice n = S8aNotice.BatchWritten(NoticeLayer.Drill, bx, by, bz, version, 0, 10, 20, 30, 40, 50, 60, 2, 100);
            n.seq = seq;
            return n;
        }

        [TestMethod]
        public void Tracker_AckWhenScheduled_AppliedWhenBuilt_InSeqOrder()
        {
            var t = new S8aReloadTracker();
            t.Reset(7);
            S8aReloadTracker.Item a = t.Add(Notice(1), 1000), b = t.Add(Notice(2), 1010), c = t.Add(Notice(3), 1020);
            CollectionAssert.AreEqual(new[] { 12 * 160 + 10, 18 * 160 + 20, 12 * 160 + 30 }, a.Mins, "the notice box in world blocks");
            CollectionAssert.AreEqual(new[] { 12 * 160 + 40, 18 * 160 + 50, 12 * 160 + 60 }, a.Maxs);
            Assert.AreEqual(0u, t.AckSeq);
            t.MarkScheduled(b, 1100);
            Assert.AreEqual(0u, t.AckSeq, "seq 2 scheduled but 1 is still validating: the ack waits");
            t.MarkScheduled(a, 1200);
            Assert.AreEqual(2u, t.AckSeq, "1 and 2 scheduled: ack 2");
            Assert.AreEqual(0u, t.AppliedSeq);
            t.MarkApplied(b, 1500, false, null);
            Assert.AreEqual(0u, t.AppliedSeq, "2 applied before 1: applied waits");
            t.MarkApplied(a, 1600, false, null);
            Assert.AreEqual(2u, t.AppliedSeq);
            Assert.AreEqual(600, t.LastLatencyMs, "seen 1000 -> applied 1600");
            Assert.AreEqual(1, t.Pending, "1 and 2 dropped, 3 pending");
            t.MarkScheduled(c, 1700);
            Assert.AreEqual(3u, t.AckSeq);
            t.MarkApplied(c, 1800, true, "bad file");
            Assert.AreEqual(3u, t.AppliedSeq, "a failed notice still advances applied (the feed never stalls)");
            Assert.AreEqual(1, t.Failures);
            Assert.AreEqual(2, t.Applied);
            Assert.AreEqual(0, t.Pending);
            // a gap after a resync: the unseen seqs count as done
            t.SkipTo(10);
            Assert.AreEqual(9u, t.AckSeq);
            Assert.AreEqual(9u, t.AppliedSeq);
            t.Reset(8);
            Assert.AreEqual(0u, t.AckSeq);
            Assert.AreEqual(8u, t.Stream);
        }

        [TestMethod]
        public void Tracker_RetryAt5s_GiveUpAt10s()
        {
            var t = new S8aReloadTracker();
            t.Reset(1);
            S8aReloadTracker.Item a = t.Add(Notice(1), 0);
            t.MarkScheduled(a, 10);
            Assert.IsNull(t.Tick(4999), "nothing before 5 s");
            List<S8aReloadTracker.Item> retry = t.Tick(5000);
            Assert.IsNotNull(retry);
            Assert.AreSame(a, retry[0]);
            Assert.IsTrue(a.Retried);
            Assert.IsNull(t.Tick(7000), "retried once only");
            Assert.AreEqual(0u, t.AppliedSeq);
            Assert.IsNull(t.Tick(10000));
            Assert.AreEqual(1u, t.AppliedSeq, "at 10 s the notice counts as applied with a failure");
            Assert.AreEqual(1, t.Failures);
            Assert.AreEqual(1, t.Timeouts);
            Assert.AreEqual(0, t.InFlight);
            // a validating notice is never retried (its file check is still running) but gives up at 10 s
            S8aReloadTracker.Item b = t.Add(Notice(2), 20000);
            Assert.IsNull(t.Tick(26000));
            Assert.IsNull(t.Tick(30000));
            Assert.AreEqual(2u, t.AppliedSeq);
        }

        [TestMethod]
        public void Mutants_KnownToDevMutants()
        {
            string err;
            foreach (string m in S8aMutants.All)
            {
                Assert.IsTrue(DevMutants.IsKnown(m), m);
                Assert.IsTrue(DevMutants.Set(m, true, out err), err);
            }
            Assert.IsTrue(S8aMutants.M4 && S8aMutants.M7 && S8aMutants.M11);
            foreach (string m in S8aMutants.All) DevMutants.Set(m, false, out err);
            Assert.IsFalse(S8aMutants.M4);
            Assert.IsFalse(DevMutants.IsKnown("S8a.M1"), "M1 is the SE side's");
        }

        [TestMethod]
        public void Paths_UnderTheStoreRoot()
        {
            string root = S8aSnPaths.Root();
            Assert.IsTrue(root.EndsWith("\\DeepEngineers\\terrain", StringComparison.OrdinalIgnoreCase), root);
            string f = S8aSnPaths.DrillFile(0xAB, -3, 2, 40, 9);
            Assert.IsTrue(f.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(f.EndsWith("\\00000000000000ab\\drill\\compiled-batch--3-2-40.v9.optoctrees", StringComparison.Ordinal), f);
            Assert.AreEqual("00000000000000ab", S8aSnPaths.Hex(0xAB));
        }

        [TestMethod]
        public void VersionOfPath_StoreNameInstallNameAndJunk()
        {
            // fix check: a load of a newer version of the same batch covers an older reload job
            Assert.AreEqual(7, S8aSnPaths.VersionOfPath(@"C:\x\terrain\k\drill\compiled-batch-12-18-12.v7.optoctrees"));
            Assert.AreEqual(12, S8aSnPaths.VersionOfPath("compiled-batch-0-0-0.v12.optoctrees"));
            Assert.AreEqual(0, S8aSnPaths.VersionOfPath(@"D:\Build18\CompiledOctreesCache\compiled-batch-12-18-12.optoctrees"));
            Assert.AreEqual(0, S8aSnPaths.VersionOfPath(null));
            Assert.AreEqual(0, S8aSnPaths.VersionOfPath("compiled-batch-1-1-1.vx.optoctrees"));
        }
}
}
