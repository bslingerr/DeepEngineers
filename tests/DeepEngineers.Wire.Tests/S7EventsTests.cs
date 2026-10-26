using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using EvType = DeepEngineers.Wire.S7Conventions.EventType;
using EvFlags = DeepEngineers.Wire.S7Conventions.EventFlags;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7 effect events (CONTRACT §5.4 "S7 conventions", "C2 effect events"): the 64-byte row and the 36-byte header
 /// extension against the pinned <c>field</c> lines, the sustain keys against FNV-1a 64 of their texts, the window
 /// (caps, priority, 128 per tick, the 500-event stress tick of S7.N4 rates, 8 ticks, seq and stream, S7.M17, S7.M20),
 /// the sustain rule (start, step, move, refresh, stop, S7.M14), the reader's seq rule (once each, gaps, streams, stale
 /// extensions) through a real C2-sized buffer, and the look rules (thruster emission linear in the intensity, S7.M11,
 /// the engine rpm, the explosion scale and S7.M13).
 /// </summary>
    [TestClass]
    public unsafe class S7EventsTests
    {
        // ------------------------------------------------------------------------------------------------ layout

        [TestMethod]
        public void Row_And_Extension_MatchTheFieldLines()
        {
            Assert.AreEqual(64, Marshal.SizeOf(typeof(S7EventRow)));
            Assert.AreEqual(64, sizeof(S7EventRow));
            Assert.AreEqual(S7Conventions.C2Events.RowSize, sizeof(S7EventRow));
            Assert.AreEqual(S7Conventions.C2Ext.Size, sizeof(C2Ext));
            Assert.AreEqual(36, Marshal.SizeOf(typeof(C2Ext)));

            var fields = S7Conventions.FieldLines().Select(l => l.Split(' ')).ToList();
            var ev = fields.Where(f => f[0] == "event").ToList();
            var c2 = fields.Where(f => f[0] == "c2ext").ToList();
            Assert.AreEqual(13, ev.Count, "event field lines");
            Assert.AreEqual(9, c2.Count, "c2ext field lines");
            foreach (string[] f in ev)
            {
                if (f[1] == "pad") { Assert.AreEqual(0x3E, Hex(f[2])); continue; }
                Assert.AreEqual(Hex(f[2]), (int)Marshal.OffsetOf(typeof(S7EventRow), f[1]), "event." + f[1]);
            }
            foreach (string[] f in c2)
            {
                int rel = Hex(f[2]) - S7Conventions.C2Ext.Offset;
                if (f[1] == "pad") { Assert.AreEqual(0x18, rel); continue; }
                Assert.AreEqual(rel, (int)Marshal.OffsetOf(typeof(C2Ext), f[1]), "c2ext." + f[1]);
            }
        }

        [TestMethod]
        public void Extension_InPad1c_RowsPastTheTables_InsideTheRegion()
        {
            Assert.AreEqual(0x1C, S7Conventions.C2Ext.Offset);
            Assert.AreEqual(0x40, S7Conventions.C2Ext.Offset + S7Conventions.C2Ext.Size, "ends exactly at the entity table");
            Assert.AreEqual(0x40, sizeof(SeEntitiesHeader));
            Assert.IsTrue(S7Conventions.C2Events.Offset >= SeEntitiesLimits.End, "rows after the light table");
            int end = S7Conventions.C2Events.Offset + S7Conventions.C2Events.MaxRows * S7Conventions.C2Events.RowSize;
            Assert.AreEqual(0x50000, end);
            Assert.IsTrue(end <= Layout.Region(RegionId.C2SeEntities).Size, "inside the 512 KiB C2 region");
            Assert.AreEqual(S7Conventions.C2Events.MaxRows, S7Conventions.C2Events.MaxPerTick * S7Conventions.C2Events.WindowTicks,
                "8 full ticks fit the window");
        }

        [TestMethod]
        public void Extension_WriteRead_RejectsStaleAndVersion0()
        {
            var region = new byte[0x50000];
            fixed (byte* r = region)
            {
                for (int i = 0; i < 0x40; i++) r[i] = 0xEE; // garbage in the pad before
                C2Ext e = C2Ext.For(0x1_0000_0005UL, S7Conventions.C2ExtFlags.Events | S7Conventions.C2ExtFlags.Lights, 17, 40, 0x00030002, 9, 33);
                C2Ext.Write(r, e);
                for (int i = 0x34; i < 0x40; i++) Assert.AreEqual(0, r[i], "pad byte " + i);
                Assert.AreEqual(0xEE, r[0x1B], "byte before the extension untouched");
                C2Ext back;
                Assert.IsTrue(C2Ext.TryRead(r, 0x1_0000_0005UL, out back));
                Assert.AreEqual(17, back.eventCount);
                Assert.AreEqual(40u, back.eventFirstSeq);
                Assert.AreEqual(0x00030002u, back.eventStream);
                Assert.AreEqual(9u, back.eventsDropped);
                Assert.AreEqual(33u, back.lightsFound);
                Assert.AreEqual(5u, back.tickLow);
                Assert.IsTrue(back.HasEvents && back.HasLights);
                Assert.IsFalse(C2Ext.TryRead(r, 6UL, out back), "stale: tickLow of another header tick");
                r[0x1C] = 0;
                Assert.IsFalse(C2Ext.TryRead(r, 5UL, out back), "version 0 = an SE before S7");
            }
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => C2Ext.For(1, 0, 1025, 1, 0, 0, 0));
        }

        // ------------------------------------------------------------------------------------------------ keys

        [TestMethod]
        public void Keys_AreFnv1a64OfTheirTexts()
        {
            ulong[] fat = { 0UL, 1UL, 0xfedcba9876543210UL, MirrorConventions.FatPartKey(-3, 4, 0) };
            foreach (ulong f in fat)
            {
                for (int i = 0; i < 13; i += 4)
                {
                    string t = "thr|" + f.ToString("x16", CultureInfo.InvariantCulture) + "|" + i.ToString(CultureInfo.InvariantCulture);
                    Assert.AreEqual(t, S7EventKeys.ThrusterText(f, i));
                    Assert.AreEqual(Fnv(t), S7EventKeys.Thruster(f, i), t);
                }
                string tool = "tool|" + f.ToString("x16", CultureInfo.InvariantCulture);
                Assert.AreEqual(Fnv(tool), S7EventKeys.ShipTool(f), tool);
            }
            foreach (long id in new[] { 0L, 7L, 123456789012345L, -42L, long.MaxValue, long.MinValue })
            {
                string t = "tool|e" + id.ToString(CultureInfo.InvariantCulture);
                Assert.AreEqual(t, S7EventKeys.HandToolText(id));
                Assert.AreEqual(Fnv(t), S7EventKeys.HandTool(id), t);
            }
            Assert.AreNotEqual(S7EventKeys.Thruster(5, 0), S7EventKeys.Thruster(5, 1));
        }

        // ------------------------------------------------------------------------------------------------ window

        [TestMethod]
        public void Window_StressTick_Exactly128Accepted_372Dropped_CapsHeld_EmptyNextTick()
        {
            var w = new S7EventWindow();
            w.SetStream(0x10001);
            const int n = 500;
            var raised = new int[S7EventRules.TypeSlots];
            for (int i = 0; i < n; i++)
            {
                byte type = (byte)(1 + i % 13);
                raised[type]++;
                Assert.IsTrue(w.TryAdd(Ev(type, i)));
            }
            int accepted = w.EndTick(1000);
            Assert.AreEqual(128, accepted);
            Assert.AreEqual(128, w.LastAcceptedTotal);
            Assert.AreEqual(372, w.LastDroppedTotal);
            Assert.AreEqual(500, w.LastRaisedTotal);
            Assert.AreEqual(372u, w.EventsDropped);
            for (byte t = 1; t <= 13; t++)
            {
                Assert.IsTrue(w.LastAccepted[t] <= S7Conventions.EventCaps.Of(t), "cap of " + EvType.Name(t));
                Assert.AreEqual(raised[t], w.LastAccepted[t] + w.LastDropped[t], "accounting of " + EvType.Name(t));
            }
            // priority: Explosion..Drill fill exactly 128 (8+8+16+16+32+16+8+8+8+8); PrawnDrill, Thruster, BulletStreak none
            Assert.AreEqual(8, w.LastAccepted[EvType.Explosion]);
            Assert.AreEqual(32, w.LastAccepted[EvType.Impact]);
            Assert.AreEqual(8, w.LastAccepted[EvType.Drill]);
            Assert.AreEqual(0, w.LastAccepted[EvType.PrawnDrill]);
            Assert.AreEqual(0, w.LastAccepted[EvType.Thruster]);
            Assert.AreEqual(0, w.LastAccepted[EvType.BulletStreak]);
            Assert.AreEqual(0, w.Pending, "the buffer is empty after the tick");
            Assert.AreEqual(0, w.EndTick(1001), "nothing carried over");
            Assert.AreEqual(0, w.LastRaisedTotal);
            Assert.AreEqual(128, w.Count, "the stress tick stays in the window");
        }

        [TestMethod]
        public void Window_Priority_StopRowsFirst_NewestFirstWithinAType_RaiseOrderKept()
        {
            var w = new S7EventWindow();
            w.SetStream(1);
            // 70 thruster refreshes, then 10 thruster stops: cap 64 → all 10 stops + the 54 newest refreshes
            for (int i = 0; i < 70; i++) w.TryAdd(Ev(EvType.Thruster, i));
            for (int i = 0; i < 10; i++)
            {
                S7EventRow s = Ev(EvType.Thruster, 1000 + i);
                s.flags |= EvFlags.Stop | EvFlags.Sustain;
                w.TryAdd(s);
            }
            Assert.AreEqual(64, w.EndTick(5));
            var rows = new S7EventRow[1024];
            int c = w.CopyRows(rows);
            Assert.AreEqual(64, c);
            Assert.AreEqual(10, rows.Take(c).Count(r => r.IsStop));
            var kept = rows.Take(c).Where(r => !r.IsStop).Select(r => (int)r.size).ToList();
            CollectionAssert.AreEqual(Enumerable.Range(16, 54).ToList(), kept, "the 54 newest refreshes, in raise order");
            for (int i = 1; i < c; i++) Assert.AreEqual(rows[i - 1].seq + 1, rows[i].seq, "consecutive seq");
            Assert.AreEqual(1u, rows[0].seq);
        }

        [TestMethod]
        public void Window_HoldsEightTicks_SeqConsecutive_StreamRestart()
        {
            var w = new S7EventWindow();
            w.SetStream(7);
            var rows = new S7EventRow[1024];
            for (ulong t = 1; t <= 20; t++)
            {
                w.TryAdd(Ev(EvType.Impact, (int)t));
                w.TryAdd(Ev(EvType.GridImpact, (int)t));
                w.EndTick(t);
                int c = w.CopyRows(rows);
                Assert.AreEqual((int)Math.Min(t, 8) * 2, c, "rows of ticks [t-7, t] at tick " + t);
                Assert.AreEqual(rows[0].seq, w.FirstSeq);
                for (int i = 1; i < c; i++) Assert.AreEqual(rows[i - 1].seq + 1, rows[i].seq);
                Assert.IsTrue(rows.Take(c).All(r => r.tickLow + 7 >= (uint)t), "no row older than 8 ticks");
            }
            Assert.AreEqual(40u, rows[15].seq);
            C2Ext e = w.Extension(20, 16, 0, 0);
            Assert.AreEqual(25u, e.eventFirstSeq);
            Assert.AreEqual(7u, e.eventStream);
            Assert.IsTrue(e.HasEvents && !e.HasLights);
            // a stream restart: seq from 1, the window empty
            Assert.IsTrue(w.SetStream(8));
            Assert.IsFalse(w.SetStream(8));
            Assert.AreEqual(0, w.Count);
            Assert.AreEqual(1u, w.FirstSeq);
            w.TryAdd(Ev(EvType.Splash, 1));
            w.EndTick(21);
            Assert.AreEqual(1, w.CopyRows(rows));
            Assert.AreEqual(1u, rows[0].seq);
            // ticks that jump (C2 written every other tick): the window is in ticks, not writes
            for (ulong t = 22; t <= 40; t += 2) { w.TryAdd(Ev(EvType.Impact, (int)t)); w.EndTick(t); }
            Assert.AreEqual(4, w.CopyRows(rows), "ticks 34, 36, 38, 40");
        }

        [TestMethod]
        public void Window_M17_OneTick_And_M20_CapsOff()
        {
            var w = new S7EventWindow { WindowTicks = 1 };
            w.SetStream(1);
            var rows = new S7EventRow[1024];
            w.TryAdd(Ev(EvType.Impact, 1)); w.EndTick(1);
            w.TryAdd(Ev(EvType.Impact, 2)); w.EndTick(2);
            Assert.AreEqual(1, w.CopyRows(rows), "S7.M17: only this tick's rows");
            Assert.AreEqual(2u, rows[0].seq);

            var m20 = new S7EventWindow { CapsOff = true };
            m20.SetStream(1);
            for (int i = 0; i < 500; i++) m20.TryAdd(Ev((byte)(1 + i % 13), i));
            Assert.AreEqual(500, m20.EndTick(1), "S7.M20: every raised event accepted");
            Assert.AreEqual(0, m20.LastDroppedTotal);
            Assert.IsTrue(m20.LastAccepted[EvType.Explosion] > S7Conventions.EventCaps.Explosion);
        }

        [TestMethod]
        public void Window_CandidateOverflow_AndInvalidRows_AreCountedNotQueued()
        {
            var w = new S7EventWindow();
            w.SetStream(1);
            for (int i = 0; i < S7EventWindow.CandidateCapacity + 10; i++) w.TryAdd(Ev(EvType.BulletStreak, i));
            Assert.AreEqual(S7EventWindow.CandidateCapacity, w.Pending, "fixed buffer");
            S7EventRow bad = Ev(EvType.Impact, 1);
            bad.pos.x = float.NaN;
            Assert.IsFalse(w.TryAdd(bad));
            S7EventRow badType = Ev(EvType.Impact, 1);
            badType.type = 14;
            Assert.IsFalse(w.TryAdd(badType));
            Assert.AreEqual(2, w.Invalid);
            w.EndTick(1);
            Assert.AreEqual(32, w.LastAcceptedTotal, "the BulletStreak cap");
            Assert.AreEqual(S7EventWindow.CandidateCapacity + 10 - 32, w.LastDroppedTotal);
            Assert.AreEqual((uint)(S7EventWindow.CandidateCapacity + 10 - 32), w.EventsDropped);
        }

        // ------------------------------------------------------------------------------------------------ sustain

        [TestMethod]
        public void Sustain_Start_Refresh15_Step_Move_Flags_Stop()
        {
            var s = new S7SustainTracker();
            var stops = new S7EventRow[16];
            var kinds = new List<SustainEmit>();
            ulong key = S7EventKeys.Thruster(99, 0);
            for (ulong t = 1; t <= 40; t++)
            {
                s.BeginTick();
                S7EventRow r = Sus(42, key, 0.5f, 1, 2, 3);
                SustainEmit e = s.Observe(ref r, t);
                kinds.Add(e);
                if (e != SustainEmit.None)
                {
                    Assert.IsTrue(r.IsSustain);
                    Assert.AreEqual((ushort)500, r.lifetimeMs);
                }
                Assert.AreEqual(0, s.CollectStops(t, stops));
            }
            // constant: a start at tick 1, refreshes at 16 and 31: ≤ 1 row per 15 ticks (S7.N4 thruster.log)
            CollectionAssert.AreEqual(new[] { 0, 15, 30 }, kinds.Select((k, i) => new { k, i }).Where(x => x.k != SustainEmit.None).Select(x => x.i).ToArray());
            Assert.AreEqual(SustainEmit.Start, kinds[0]);
            Assert.AreEqual(SustainEmit.Refresh, kinds[15]);

            s.BeginTick();
            S7EventRow small = Sus(42, key, 0.53f, 1, 2, 3);
            Assert.AreEqual(SustainEmit.None, s.Observe(ref small, 41), "a 0.03 change is not a step");
            s.BeginTick();
            S7EventRow step = Sus(42, key, 0.55f, 1, 2, 3);
            Assert.AreEqual(SustainEmit.Step, s.Observe(ref step, 42), "0.05 from the last row: answered in the same tick");
            s.BeginTick();
            S7EventRow move = Sus(42, key, 0.55f, 1, 2, 3.06f);
            Assert.AreEqual(SustainEmit.Move, s.Observe(ref move, 43), "an attached move over 5 cm");
            s.BeginTick();
            S7EventRow wet = Sus(42, key, 0.55f, 1, 2, 3.06f);
            wet.flags |= EvFlags.Underwater;
            Assert.AreEqual(SustainEmit.Flags, s.Observe(ref wet, 44), "under the surface now");

            s.BeginTick(); // not observed: ends
            Assert.AreEqual(1, s.CollectStops(45, stops));
            Assert.IsTrue(stops[0].IsStop && stops[0].IsSustain);
            Assert.AreEqual(0f, stops[0].intensity);
            Assert.AreEqual(key, stops[0].key);
            Assert.AreEqual(42UL, stops[0].entityId);
            Assert.AreEqual(45u, stops[0].tickLow);
            Assert.IsTrue(stops[0].Underwater, "the last flags kept");
            Assert.AreEqual(0, s.Count);
            s.BeginTick();
            S7EventRow again = Sus(42, key, 0.2f, 1, 2, 3);
            Assert.AreEqual(SustainEmit.Start, s.Observe(ref again, 46), "a new sustain after its stop");
        }

        [TestMethod]
        public void Sustain_ReviewF18c_StepLagMeasured_ZeroWhenAnswered_GrowsUnderM14()
        {
            var s = new S7SustainTracker();
            ulong key = S7EventKeys.Thruster(5, 0);
            s.BeginTick();
            S7EventRow r = Sus(9, key, 0.5f, 0, 0, 0);
            Assert.AreEqual(SustainEmit.Start, s.Observe(ref r, 1));
            s.BeginTick();
            r = Sus(9, key, 0.8f, 0, 0, 0);
            Assert.AreEqual(SustainEmit.Step, s.Observe(ref r, 2));
            Assert.AreEqual(0UL, s.TakeStepLagMax(2), "a step answered in its own tick: lag 0");
            Assert.AreEqual(1L, s.StepLagSamples);
            // S7.M14 (no refresh): the step waits, the lag grows tick by tick and is reported while open
            var m = new S7SustainTracker();
            m.BeginTick();
            S7EventRow t = Sus(9, key, 0.2f, 0, 0, 0);
            Assert.AreEqual(SustainEmit.Start, m.Observe(ref t, 10, refresh: false));
            for (ulong k = 11; k <= 20; k++)
            {
                m.BeginTick();
                t = Sus(9, key, 1f, 0, 0, 0);
                Assert.AreEqual(SustainEmit.None, m.Observe(ref t, k, refresh: false));
            }
            Assert.AreEqual(9UL, m.TakeStepLagMax(20), "the step seen at tick 11 still waits at tick 20");
            // back within 0.05 of the last row: nothing waits any more
            m.BeginTick();
            t = Sus(9, key, 0.22f, 0, 0, 0);
            m.Observe(ref t, 21, refresh: false);
            Assert.AreEqual(0UL, m.TakeStepLagMax(21));
        }

        [TestMethod]
        public void Sustain_M14_StartRowOnly()
        {
            var s = new S7SustainTracker();
            int rows = 0;
            for (ulong t = 1; t <= 60; t++)
            {
                s.BeginTick();
                S7EventRow r = Sus(7, S7EventKeys.HandTool(1234), t % 2 == 0 ? 1f : 0.25f, 0, 0, t * 0.1f);
                if (s.Observe(ref r, t, refresh: false) != SustainEmit.None) rows++;
            }
            Assert.AreEqual(1, rows, "S7.M14: the start row only (Subnautica fades the effect after 500 ms)");
        }

        [TestMethod]
        public void Sustain_ManyKeys_IndependentPerEntity()
        {
            var s = new S7SustainTracker();
            var stops = new S7EventRow[64];
            s.BeginTick();
            for (ulong e = 1; e <= 3; e++)
                for (int f = 0; f < 4; f++)
                {
                    S7EventRow r = Sus(e, S7EventKeys.Thruster(77, f), 1f, f, 0, 0);
                    Assert.AreEqual(SustainEmit.Start, s.Observe(ref r, 1));
                }
            Assert.AreEqual(12, s.Count, "the same key on three grids: three sustains (keyed by entity id)");
            var byType = new int[S7EventRules.TypeSlots];
            s.CountByType(byType);
            Assert.AreEqual(12, byType[EvType.Thruster]);
            Assert.AreEqual(12, byType.Sum());
            s.BeginTick();
            for (int f = 0; f < 4; f++) { S7EventRow r = Sus(2, S7EventKeys.Thruster(77, f), 1f, f, 0, 0); s.Observe(ref r, 2); }
            Assert.AreEqual(8, s.CollectStops(2, stops));
            Assert.IsTrue(stops.Take(8).All(r => r.entityId != 2));
        }

        // ------------------------------------------------------------------------------------------------ reader

        [TestMethod]
        public void Reader_ThroughARealRegion_AppliesEachSeqOnce_InOrder()
        {
            var w = new S7EventWindow();
            var reader = new S7EventReader();
            var region = new byte[0x50000];
            var rows = new S7EventRow[1024];
            w.SetStream(S7EventRules.Stream(3, 1));
            var got = new List<uint>();
            for (ulong t = 100; t < 130; t++)
            {
                for (int k = 0; k < 3; k++) w.TryAdd(Ev(EvType.WeaponMuzzle, (int)t * 10 + k));
                w.EndTick(t);
                Publish(region, w, rows, t);
                // Subnautica reads every other tick
                if (t % 2 == 1) Read(region, reader, got);
            }
            Read(region, reader, got);
            Assert.AreEqual(90, got.Count, "every event once");
            CollectionAssert.AreEqual(Enumerable.Range(1, 90).Select(i => (uint)i).ToList(), got, "seq order");
            Assert.AreEqual(0, reader.Missed);
            Assert.IsTrue(reader.AlreadyApplied > 0, "the windows overlapped");
        }

        [TestMethod]
        public void Reader_MoreThanEightTicksBehind_CountsMissed_NewStreamResets_StaleIgnored()
        {
            var w = new S7EventWindow();
            var reader = new S7EventReader();
            var region = new byte[0x50000];
            var rows = new S7EventRow[1024];
            var got = new List<uint>();
            w.SetStream(0x10000);
            for (ulong t = 1; t <= 4; t++) { w.TryAdd(Ev(EvType.Impact, (int)t)); w.EndTick(t); }
            Publish(region, w, rows, 4);
            Read(region, reader, got);
            Assert.AreEqual(4, got.Count);
            for (ulong t = 5; t <= 24; t++) { w.TryAdd(Ev(EvType.Impact, (int)t)); w.EndTick(t); }
            Publish(region, w, rows, 24);
            Read(region, reader, got);
            Assert.AreEqual(12, reader.Missed, "ticks 5..16 left the window before Subnautica read (S7.N4 loss)");
            Assert.AreEqual(12, got.Count);
            Assert.AreEqual(24u, got.Last());

            // an empty window after the events left it: the gap still counts
            var r2 = new S7EventReader();
            r2.Apply(5, 1, rows, 0);
            r2.Apply(5, 11, rows, 0);
            Assert.AreEqual(10, r2.Missed);

            // a new stream (SE restarted the S5 stream): seq from 1 again, applied
            w.SetStream(0x10001);
            w.TryAdd(Ev(EvType.Splash, 1)); w.EndTick(25);
            Publish(region, w, rows, 25);
            Read(region, reader, got);
            Assert.AreEqual(1, reader.StreamResets);
            Assert.AreEqual(1u, got.Last());

            // a stale extension (tickLow of an older header) is ignored
            long before = reader.Queued;
            fixed (byte* r = region)
            {
                *(ulong*)(r + 8) = 26; // header tick moved on, extension still says 25
                reader.CopyInWindow(r);
                reader.Commit(26);
            }
            Assert.AreEqual(1, reader.StaleExtension);
            Assert.AreEqual(before, reader.Queued);
            // an SE before S7 (version 0)
            Array.Clear(region, 0, 0x40);
            fixed (byte* r = region) { reader.CopyInWindow(r); reader.Commit(0); }
            Assert.AreEqual(1, reader.NoExtension);
        }

        [TestMethod]
        public void Reader_PendingQueue_IsBounded()
        {
            var reader = new S7EventReader();
            var rows = new S7EventRow[1024];
            uint seq = 1;
            for (int win = 0; win < 3; win++)
            {
                for (int i = 0; i < 1024; i++) { rows[i] = Ev(EvType.BulletStreak, i); rows[i].seq = seq++; }
                reader.Apply(9, rows[0].seq, rows, 1024);
            }
            Assert.AreEqual(S7EventReader.PendingCapacity, reader.PendingCount);
            Assert.AreEqual(3 * 1024 - S7EventReader.PendingCapacity, reader.Overflow);
            S7EventRow first;
            Assert.IsTrue(reader.TryDequeue(out first));
            Assert.AreEqual(1u, first.seq, "oldest first");
        }

        // ------------------------------------------------------------------------------------------------ rules

        [TestMethod]
        public void Rule_ThrusterEmission_LinearInIntensity_ZeroAtZero_M11Constant()
        {
            float[] strengths = { 0f, 0.25f, 0.5f, 0.75f, 1f };
            float[] rates = strengths.Select(i => S7EventRules.ThrusterEmissionRate(40f, i, 1.5f, false)).ToArray();
            Assert.AreEqual(0f, rates[0], "zero at strength 0");
            Assert.AreEqual(1.0, RSquared(strengths, rates), 1e-9, "S7.N4 thruster.rate: linear (r² = 1)");
            Assert.AreEqual(60f, rates[4], 1e-4f, "base × intensity × size / 1 m");
            Assert.AreEqual(2 * S7EventRules.ThrusterEmissionRate(40f, 1f, 1f, false), S7EventRules.ThrusterEmissionRate(40f, 1f, 2f, false), 1e-4f, "∝ size");
            float[] m11 = strengths.Skip(1).Select(i => S7EventRules.ThrusterEmissionRate(40f, i, 1.5f, true)).ToArray();
            Assert.IsTrue(m11.All(r => r == m11[0] && r > 0), "S7.M11: constant");
            Assert.AreEqual(0f, S7EventRules.ThrusterEmissionRate(0f, 1f, 1f, false), "no source rate, no bubbles");
            Assert.IsTrue(S7EventRules.CavitationEdge(0.6f, 0.75f));
            Assert.IsFalse(S7EventRules.CavitationEdge(0.75f, 0.9f));
        }

        [TestMethod]
        public void Rule_EngineRpm_WeightedBySizeSquared()
        {
            double a = 0, b = 0;
            S7EventRules.RpmAdd(ref a, ref b, 1f, 2f);   // big flame at full
            S7EventRules.RpmAdd(ref a, ref b, 0f, 1f);   // small flame idle
            Assert.AreEqual(4f / 5f, S7EventRules.EngineRpm(a, b), 1e-6f);
            Assert.AreEqual(0f, S7EventRules.EngineRpm(0, 0), "no live thruster");
            double c = 0, d = 0;
            for (int i = 0; i < 4; i++) S7EventRules.RpmAdd(ref c, ref d, 0.5f, 1.25f);
            Assert.AreEqual(0.5f, S7EventRules.EngineRpm(c, d), 1e-6f, "equal flames: the common strength");
        }

        [TestMethod]
        public void Rule_ExplosionScale_ProportionalToRadius_M13Fixed()
        {
            Assert.AreEqual(0.4f, S7EventRules.ExplosionScale(2f, false), 1e-6f);
            Assert.AreEqual(2f, S7EventRules.ExplosionScale(10f, false), 1e-6f);
            Assert.AreEqual(6f, S7EventRules.ExplosionScale(30f, false), 1e-6f);
            Assert.AreEqual(1f, S7EventRules.ExplosionScale(30f, true), "S7.M13: radius ignored");
            Assert.AreEqual(1f, S7EventRules.ExplosionScale(float.NaN, false));
        }

        [TestMethod]
        public void Rule_PerTypeNumbers()
        {
            Assert.AreEqual(EvFlags.Underwater, S7EventRules.WaterFlags(-20));
            Assert.AreEqual(EvFlags.Underwater | EvFlags.Surface, S7EventRules.WaterFlags(-0.3));
            Assert.AreEqual(EvFlags.Surface, S7EventRules.WaterFlags(0));
            Assert.AreEqual(EvFlags.Surface, S7EventRules.WaterFlags(0.5));
            Assert.AreEqual(0, S7EventRules.WaterFlags(2));
            Assert.AreEqual(S7Conventions.ThrusterKind.Ion, S7EventRules.ThrusterKindOf("Ion"));
            Assert.AreEqual(S7Conventions.ThrusterKind.Hydrogen, S7EventRules.ThrusterKindOf("Hydrogen"));
            Assert.AreEqual(S7Conventions.ThrusterKind.Atmospheric, S7EventRules.ThrusterKindOf("Atmospheric"));
            Assert.AreEqual(S7Conventions.ThrusterKind.Other, S7EventRules.ThrusterKindOf(null));
            Assert.AreEqual(0.125f, S7EventRules.StreakIntensity(1));
            Assert.AreEqual(1f, S7EventRules.StreakIntensity(30));
            Assert.AreEqual(60f, S7EventRules.StreakLength(800f));
            Assert.AreEqual(25f, S7EventRules.StreakLength(25f));
            Assert.AreEqual((ushort)150, S7EventRules.StreakLifetimeMs(60f, 400f));
            Assert.AreEqual((ushort)100, S7EventRules.StreakLifetimeMs(10f, 1000f), "at least 100 ms");
            Assert.AreEqual(0.5f, S7EventRules.GridImpactIntensity(10f));
            Assert.AreEqual(1f, S7EventRules.GridImpactIntensity(50f));
            Assert.AreEqual(4f, S7EventRules.GridImpactSize(1e7f));
            Assert.AreEqual(0.5f, S7EventRules.ExplosionIntensity(500f));
            Assert.IsTrue(S7EventRules.SplashCrossing(0.4, -0.1, -3));
            Assert.IsTrue(S7EventRules.SplashCrossing(-0.4, 0.0, 2.5));
            Assert.IsFalse(S7EventRules.SplashCrossing(0.4, -0.1, -1.5), "slower than 2 m/s");
            Assert.IsFalse(S7EventRules.SplashCrossing(-0.4, -0.1, -3), "no crossing");
            Assert.AreEqual(0.25f, S7EventRules.ToolIntensity(false));
            Assert.AreEqual(0x00050007u, S7EventRules.Stream(5, 0x10007));
            Assert.IsTrue(S7EventRules.InRadius(EvType.Explosion, 900));
            Assert.IsFalse(S7EventRules.InRadius(EvType.Impact, 300));
            Assert.AreEqual(0, S7EventRules.PriorityRank(EvType.BulletStreak, EvFlags.Stop));
            Assert.AreEqual(1, S7EventRules.PriorityRank(EvType.Explosion, 0));
            Assert.AreEqual(13, S7EventRules.PriorityRank(EvType.BulletStreak, 0));
        }

        // ------------------------------------------------------------------------------------------------ helpers

        private static void Publish(byte[] region, S7EventWindow w, S7EventRow[] rows, ulong tick)
        {
            int c = w.CopyRows(rows);
            fixed (byte* r = region)
            {
                *(ulong*)(r + 8) = tick;
                C2Ext.Write(r, w.Extension(tick, c, 0, 0));
                var dst = (S7EventRow*)(r + S7Conventions.C2Events.Offset);
                for (int i = 0; i < c; i++) dst[i] = rows[i];
            }
        }

        private static void Read(byte[] region, S7EventReader reader, List<uint> got)
        {
            fixed (byte* r = region)
            {
                reader.CopyInWindow(r);
                reader.Commit(*(ulong*)(r + 8));
            }
            S7EventRow e;
            while (reader.TryDequeue(out e)) got.Add(e.seq);
        }

        private static S7EventRow Ev(byte type, int tag)
        {
            return new S7EventRow { type = type, pos = new F32x3(tag, 0, 0), dir = new F32x3(0, 1, 0), intensity = 1f, size = tag, entityId = 0 };
        }

        private static S7EventRow Sus(ulong entity, ulong key, float intensity, float x, float y, float z)
        {
            return new S7EventRow
            {
                type = EvType.Thruster, entityId = entity, key = key, intensity = intensity, size = 1f,
                pos = new F32x3(x, y, z), dir = new F32x3(0, 0, 1), flags = EvFlags.Attached,
            };
        }

        private static int Hex(string s) { return int.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture); }

        private static ulong Fnv(string text) { return LayoutHash.Fnv1a64(Encoding.UTF8.GetBytes(text)); }

        private static double RSquared(float[] x, float[] y)
        {
            double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < x.Length; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
            return sxy * sxy / (sxx * syy);
        }
    }
}
