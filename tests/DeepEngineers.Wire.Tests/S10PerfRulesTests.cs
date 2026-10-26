using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>S10 part 1: the s10.* rows' percentile rule, windows and stage clock, and the paste ship's layout. The vectors are
 /// the Python twin's (tests/s10/analyze_s10.py --selftest: percentile, blueprint_counts).</summary>
    [TestClass]
    public class S10PerfRulesTests
    {
        private static double[] Range(int n) { var v = new double[n]; for (int i = 0; i < n; i++) v[i] = i + 1; return v; }

        [TestMethod]
        public void PercentileIsNearestRankAsTheTwin()
        {
            double[] v = Range(100);
            Assert.AreEqual(50.0, S10PerfRules.Percentile(v, 100, 0.50));
            Assert.AreEqual(95.0, S10PerfRules.Percentile(v, 100, 0.95));
            Assert.AreEqual(99.0, S10PerfRules.Percentile(v, 100, 0.99));
            Assert.AreEqual(7.0, S10PerfRules.Percentile(new[] { 7.0 }, 1, 0.95));
            Assert.IsTrue(double.IsNaN(S10PerfRules.Percentile(new double[0], 0, 0.5)));
            Assert.AreEqual(3.0, S10PerfRules.Percentile(Range(100), 3, 1.0), "n bounds the read, not the array length");
        }

        [TestMethod]
        public void FrameWindowTakesAndResets()
        {
            var window = new S10FrameWindow(16);
            foreach (double ms in new double[] { 3, 0, 9, 1, 7, 2, 8, 4, 6, 5 }) window.Add(ms);
            window.Add(double.NaN);
            window.Add(-1);
            S10FrameStats s = window.Take();
            Assert.AreEqual(10, s.Frames);
            Assert.AreEqual(4.0, s.P50);
            Assert.AreEqual(9.0, s.P95);
            Assert.AreEqual(9.0, s.Max);
            Assert.AreEqual(4.5, s.Mean, 1e-12);
            S10FrameStats e = window.Take();
            Assert.AreEqual(0, e.Frames);
            Assert.IsTrue(double.IsNaN(e.Max) && double.IsNaN(e.P50) && double.IsNaN(e.Mean));
        }

        [TestMethod]
        public void FrameWindowCountsBeyondItsCapacityWithAnExactMax()
        {
            var w = new S10FrameWindow(4);
            for (int i = 1; i <= 10; i++) w.Add(i);
            Assert.AreEqual(10, w.Count);
            S10FrameStats s = w.Take();
            Assert.AreEqual(10, s.Frames);
            Assert.AreEqual(10.0, s.Max);
            Assert.AreEqual(5.5, s.Mean, 1e-12);
            Assert.AreEqual(2.0, s.P50, "the percentiles come from the stored samples");
        }

        [TestMethod]
        public void StageClockSumsPerWindowAndKeepsTheWorstFrame()
        {
            var c = new S10StageClock();
            c.Add(S10PerfRules.StageKit, 10);
            c.Add(S10PerfRules.StageKit, 5);
            c.Add(S10PerfRules.StageLights, 3);
            c.EndFrame();
            c.Add(S10PerfRules.StageKit, 12);
            c.Add(99, 1000);
            c.Add(S10PerfRules.StageKit, -4);
            c.EndFrame();
            var sum = new double[S10PerfRules.StageCount];
            var max = new double[S10PerfRules.StageCount];
            var calls = new int[S10PerfRules.StageCount];
            c.Take(0.5, sum, max, calls);
            Assert.AreEqual(13.5, sum[S10PerfRules.StageKit], 1e-12);
            Assert.AreEqual(7.5, max[S10PerfRules.StageKit], 1e-12, "frame 1: 15 ticks");
            Assert.AreEqual(3, calls[S10PerfRules.StageKit]);
            Assert.AreEqual(1.5, sum[S10PerfRules.StageLights], 1e-12);
            c.Take(0.5, sum, max, calls);
            Assert.AreEqual(0.0, sum[S10PerfRules.StageKit]);
            Assert.AreEqual(0, calls[S10PerfRules.StageLights]);
            Assert.AreEqual(S10PerfRules.StageCount, S10PerfRules.StageNames.Length);
        }

        [TestMethod]
        public void PasteShipMatchesTheTwin()
        {
            List<S10Block> l = S10Blueprint.Layout(10000);
            Assert.AreEqual(10000, l.Count);
            CollectionAssert.AreEqual(new[] { 7675, 2020, 204, 101 }, S10Blueprint.Counts(l));
            var seen = new HashSet<long>();
            int minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (S10Block b in l)
            {
                Assert.IsTrue(seen.Add(((long)(b.X + 1000) << 40) | ((long)(b.Y + 1000) << 20) | (long)(b.Z + 1000)), "one block per cell");
                minZ = Math.Min(minZ, b.Z);
                maxZ = Math.Max(maxZ, b.Z);
                Assert.IsTrue(b.X >= -S10Blueprint.Width / 2 && b.X < S10Blueprint.Width / 2 && b.Y >= -S10Blueprint.Height / 2 && b.Y < S10Blueprint.Height / 2);
            }
            Assert.AreEqual(101, maxZ - minZ + 1, "101 slices");
            Assert.IsTrue(Math.Abs(minZ + maxZ) <= 1, "centred along the ship");
        }

        [TestMethod]
        public void Part3RulesPreviewLodOrderLights()
        {
            Assert.IsTrue(S10PastePreview.Admit(50000, -1), "no cap: S7p alone");
            Assert.IsFalse(S10PastePreview.Admit(1, 0), "cap 0: never");
            Assert.IsTrue(S10PastePreview.Admit(1000, 1000));
            Assert.IsFalse(S10PastePreview.Admit(1001, 1000));

            Assert.IsFalse(S10Lod.Coarse(160, false, 100, 150, 15, 40000, 40), "inside the hysteresis: stays full");
            Assert.IsTrue(S10Lod.Coarse(166, false, 100, 150, 15, 40000, 40));
            Assert.IsTrue(S10Lod.Coarse(140, true, 100, 150, 15, 40000, 40), "inside the hysteresis: stays coarse");
            Assert.IsFalse(S10Lod.Coarse(134, true, 100, 150, 15, 40000, 40));
            Assert.IsTrue(S10Lod.Coarse(60, false, 40000, 150, 15, 40000, 40), "merged hull: a big grid is coarse beyond 40 m");
            Assert.IsTrue(S10Lod.Coarse(double.NaN, true, 1, 150, 15, 0, 40), "unknown distance keeps the state");

            Assert.IsTrue(S10BuildOrder.Compare(1, 49, 9, 60, 50) < 0, "a near cell before any far one");
            Assert.IsTrue(S10BuildOrder.Compare(9, 40, 1, 10, 50) < 0, "near: newest dirty first");
            Assert.IsTrue(S10BuildOrder.Compare(1, 60, 9, 90, 50) < 0, "far: nearest first");

            int[] order = { 0, 1, 2, 3, 4, 5 };
            long[] owner = { 7, 7, 7, 8, 7, 9 };
            var kept = new int[4];
            Assert.AreEqual(4, S10LightCap.Keep(order, 6, owner, 2, 4, kept, null));
            CollectionAssert.AreEqual(new[] { 0, 1, 3, 5 }, kept, "two per grid, nearest first, then the other grids");
            Assert.AreEqual(4, S10LightCap.Keep(order, 6, owner, 0, 4, kept, null));
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, kept, "no cap: the S7 nearest-first rule");
        }

        [TestMethod]
        public void CurtainCoversEverySeFrameUntilSubnauticaShows()
        {
            Assert.IsTrue(S10Curtain.InZone(true, ViewPicture.Site, 900, 1000, ViewState.Approach), "Site: below the band, above water too");
            Assert.IsFalse(S10Curtain.InZone(true, ViewPicture.Site, 1001, 1000, ViewState.Approach));
            Assert.IsFalse(S10Curtain.InZone(false, ViewPicture.Site, -10, 1000, ViewState.Underwater), "the machine not stepping: no planet, no curtain");
            Assert.IsTrue(S10Curtain.InZone(true, ViewPicture.Waterline, -5, 1000, ViewState.Underwater));
            Assert.IsFalse(S10Curtain.InZone(true, ViewPicture.Waterline, 50, 1000, ViewState.Approach), "Waterline: SE above water");

            uint alive = PeerState.Alive, act = SnPhase.Active;
            Assert.IsTrue(S10Curtain.SnShown(true, true, false, alive, act, 0, false));
            Assert.IsTrue(S10Curtain.SnShown(true, false, true, alive, act, 0, false), "A' lowered counts as shown");
            Assert.IsFalse(S10Curtain.SnShown(true, false, false, alive, act, 0, false), "margins not on yet: SE's picture would show");
            Assert.IsFalse(S10Curtain.SnShown(false, true, false, alive, act, 0, false));
            Assert.IsFalse(S10Curtain.SnShown(true, true, false, PeerState.Stalled, act, 0, false));
            Assert.IsFalse(S10Curtain.SnShown(true, true, false, alive, SnPhase.WarmingUp, 0, false));
            Assert.IsFalse(S10Curtain.SnShown(true, true, false, alive, act, ViewBlock.AreaBuilt, true), "after a jump: the area first");
            Assert.IsTrue(S10Curtain.SnShown(true, true, false, alive, act, ViewBlock.AreaBuilt, false), "no jump: a streaming blip never blinks the curtain");
            Assert.IsTrue(S10Curtain.SnShown(true, true, false, alive, act, ViewBlock.Settle, true), "after a jump: the queue's tail (far cells) never holds the curtain (2026-10-25)");
            Assert.IsFalse(S10Curtain.SnShown(true, true, false, alive, act, ViewBlock.AreaBuilt | ViewBlock.Settle, true), "after a jump: the area first, whatever the queue");

            Assert.AreEqual(1.0, S10Curtain.Alpha(true, 1e9, 250));
            Assert.AreEqual(0.5, S10Curtain.Alpha(false, 125, 250), 1e-12);
            Assert.AreEqual(0.0, S10Curtain.Alpha(false, 300, 250));
            Assert.AreEqual(0.0, S10Curtain.Alpha(false, double.NaN, 250));
            Assert.AreEqual("exited", S10Curtain.Cause(true, true, false, PeerState.Exited, act, false));
            Assert.AreEqual("streaming", S10Curtain.Cause(true, true, false, alive, act, true));
            Assert.AreEqual("margins", S10Curtain.Cause(true, false, false, alive, act, false));
        }

        [TestMethod]
        public void PresetsArePinnedAndPicked()
        {
            Assert.AreEqual(S10Presets.ConventionsHash, LayoutHash.Fnv1a64Ascii(S10Presets.CanonicalText()), S10Presets.CanonicalText());
            StringAssert.StartsWith(S10Presets.CanonicalText(), "ext s10 presets 1\n");
            Assert.AreEqual("low", S10Presets.Auto(true, 64), "a shared GPU is low whatever the RAM");
            Assert.AreEqual("low", S10Presets.Auto(false, 15.9), "a 16 GB machine");
            Assert.AreEqual("mid", S10Presets.Auto(false, 24));
            Assert.AreEqual("high", S10Presets.Auto(false, 31.9), "a 32 GB machine reports 31.9 GB");
            Assert.AreEqual("mid", S10Presets.Of("nonsense").Name);
            Assert.AreEqual(30, S10Presets.Of("LOW").FpsCap);
            Assert.AreEqual(S10PastePreview.DefaultCap, S10Presets.Mid.PreviewCap, "mid is every switch's built-in default");
            Assert.AreEqual(S10LightCap.DefaultPerGrid, S10Presets.Mid.LightsPerGrid);
            int vs = 1, fps = -1;
            S10Presets.CapFrameRate(30, ref vs, ref fps);
            Assert.IsTrue(vs == 0 && fps == 30, "vSync off, 30");
            vs = 0; fps = 10;
            S10Presets.CapFrameRate(30, ref vs, ref fps);
            Assert.AreEqual(10, fps, "a lower rate (the idle recipe) is kept");
            vs = 0; fps = 144;
            S10Presets.CapFrameRate(0, ref vs, ref fps);
            Assert.AreEqual(144, fps, "no cap");
        }

        [TestMethod]
        public void PasteShipClampsAndIsDeterministic()
        {
            Assert.AreEqual(1, S10Blueprint.Layout(0).Count);
            Assert.AreEqual(S10Blueprint.MaxBlocks, S10Blueprint.Layout(int.MaxValue).Count);
            List<S10Block> first = S10Blueprint.Layout(3000), again = S10Blueprint.Layout(3000);
            int k = 0;
            foreach (S10Block blk in first) { S10Block o = again[k++]; Assert.IsTrue(blk.X == o.X && blk.Y == o.Y && blk.Z == o.Z && blk.Kind == o.Kind); }
            Assert.AreEqual(S10Blueprint.Subtypes.Length, S10Blueprint.Counts(first).Length);
        }

        [TestMethod]
        public void RoundThreeE_AllocMeterNestsLapsAndIgnoresCollections()
        {
            var m = new S10AllocMeter(3);
            var kb = new double[3];
            m.Mark(100, 1000);            // stage 2 (outer) starts
            m.Mark(101, 1500);            // stage 0 (inner) starts
            m.Add(0, 101, 1500 + 2048);   // inner: 2 KB
            m.Add(2, 100, 1000 + 4096);   // outer: 4 KB (includes the inner)
            m.Add(1, 999, 9999);          // a stamp never marked: nothing
            m.Mark(200, 50000);
            m.Add(1, 200, 10000);         // the counter went down (a collection): nothing
            m.Add(1, 200, 90000);         // the mark is used once
            m.Take(kb);
            CollectionAssert.AreEqual(new[] { 2.0, 0.0, 4.0 }, kb);
            m.Take(kb);
            CollectionAssert.AreEqual(new[] { 0.0, 0.0, 0.0 }, kb, "the window resets");
            m.Lap(0, 5000);               // no lap start: nothing
            m.LapStart(10000);
            m.Lap(1, 10000 + 1024);
            m.Lap(2, 10000 + 1024 + 3072);
            m.Take(kb);
            CollectionAssert.AreEqual(new[] { 0.0, 1.0, 3.0 }, kb);
            for (int i = 0; i < S10AllocMeter.Ring + 5; i++) m.Mark(1000 + i, 0); // the ring forgets the oldest marks
            m.Add(0, 1000, 1 << 20);
            m.Add(0, 1000 + S10AllocMeter.Ring + 4, 1024);
            m.Take(kb);
            Assert.AreEqual(1.0, kb[0]);
            Assert.IsTrue(S10AllocCuts.Parse("off", true) == false && S10AllocCuts.Parse(" ON ", false) && S10AllocCuts.Parse("maybe", true) && !S10AllocCuts.Parse(null, false));
            Assert.AreEqual(24, S10AllocCuts.RowsPerSecond, "the s7.fx.thruster budget of round 2 D");
        }

        [TestMethod]
        public void RoundThreeF_TerrainSliceKeepsOrderTakesEverythingAndTheFirstOfEachFrame()
        {
            Assert.IsTrue(S10TerrainSlice.Take(false, 50, 999, 4), "off: every item, as before");
            Assert.IsTrue(S10TerrainSlice.Take(true, 0, 999, 4), "on: the frame's first item always (progress)");
            Assert.IsTrue(S10TerrainSlice.Take(true, 3, 3.9, 4));
            Assert.IsFalse(S10TerrainSlice.Take(true, 1, 4.0, 4), "on: over the budget, the rest waits");
            // a sliced FIFO drain (the host's loop: peek, take, dequeue) gives the same items in the same order over more frames
            var costs = new[] { 1.0, 6.0, 0.5, 0.5, 3.0, 9.0, 0.2, 0.2, 0.2, 2.5 };
            foreach (bool on in new[] { false, true })
            {
                var q = new Queue<int>();
                for (int i = 0; i < costs.Length; i++) q.Enqueue(i);
                var order = new List<int>();
                int frames = 0;
                double worst = 0;
                while (q.Count > 0)
                {
                    frames++;
                    double spent = 0;
                    int taken = 0;
                    while (q.Count > 0 && S10TerrainSlice.Take(on, taken, spent, S10TerrainSlice.DefaultBudgetMs))
                    {
                        int item = q.Dequeue();
                        taken++;
                        spent += costs[item];
                        order.Add(item);
                    }
                    worst = Math.Max(worst, spent);
                }
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, order.ToArray(), "on=" + on + ": every item once, in order");
                if (on) { Assert.IsTrue(frames > 1); Assert.IsTrue(worst <= 9.0 + 1e-9, "the worst frame is the worst single item, not the sum"); }
                else { Assert.AreEqual(1, frames); Assert.AreEqual(23.1, worst, 1e-9); }
            }
            Assert.AreEqual(4.0, S10TerrainSlice.ParseMs("bad", 4.0));
            Assert.AreEqual(2.5, S10TerrainSlice.ParseMs(" 2.5 ", 4.0));
            Assert.AreEqual(4.0, S10TerrainSlice.ParseMs("-1", 4.0));
            Assert.AreEqual(S10TerrainSlice.SnSteps.Length, S10TerrainSlice.SnRows + 1);
            Assert.AreEqual(S10TerrainSlice.SeSteps.Length, S10TerrainSlice.SeSample + 1);
        }

        [TestMethod]
        public void RoundThreeE_BufferPoolReusesExactLengthsWithinItsCap()
        {
            var p = new S10BufferPool(100);
            byte[] a = p.Rent(40), b = p.Rent(40), c = p.Rent(30);
            Assert.AreEqual(40, a.Length);
            Assert.AreNotSame(a, b);
            p.Return(a);
            p.Return(a);                       // a duplicate is ignored
            p.Return(null);
            p.Return(b);
            p.Return(c);                       // 40 + 40 + 30 > 100: dropped
            Assert.AreEqual(80, p.KeptBytes);
            Assert.AreEqual(1, p.Dropped);
            byte[] r1 = p.Rent(40), r2 = p.Rent(40), r3 = p.Rent(40);
            Assert.IsTrue((ReferenceEquals(r1, a) || ReferenceEquals(r1, b)) && (ReferenceEquals(r2, a) || ReferenceEquals(r2, b)) && !ReferenceEquals(r1, r2));
            Assert.IsFalse(ReferenceEquals(r3, a) || ReferenceEquals(r3, b), "never handed out twice");
            Assert.AreEqual(30, p.Rent(30).Length, "a length not kept is new");
            Assert.AreEqual(0, p.KeptBytes);
            p.Return(r1);
            p.Clear();
            Assert.AreEqual(0, p.KeptBytes);
            // two threads renting and returning: every rented array is exclusive while held
            var big = new S10BufferPool(1 << 20);
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<byte[], int>();
            bool clash = false;
            System.Threading.Tasks.Parallel.For(0, 4000, i =>
            {
                byte[] x = big.Rent(64);
                if (!seen.TryAdd(x, i)) clash = true;
                int ignored;
                seen.TryRemove(x, out ignored);
                big.Return(x);
            });
            Assert.IsFalse(clash);
        }
    }
}
