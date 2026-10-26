using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C2S8a = DeepEngineers.Wire.S8aConventions.C2S8a;
using C2S8aFlags = DeepEngineers.Wire.S8aConventions.C2S8aFlags;
using NoticeKind = DeepEngineers.Wire.S8aConventions.NoticeKind;
using NoticeLayer = DeepEngineers.Wire.S8aConventions.NoticeLayer;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S8a change feed (CONTRACT §5.4 "S8a conventions": the C2 S8a header, the notice rows, the C7 S8a block; formats
 /// feed.ack and feed.resync): the record bytes against the Python twin (tests/s8a/vectors/feed.tsv), the scripted
 /// writer scenario twin (header hex and rows FNV per step: MaxPerTick, acks, a wrong stream, the overflow resync and
 /// its echo, no rows without an S8a Subnautica), and the reader's seq rule (in order, gaps, a new stream, a full
 /// queue, the Resync epoch) end to end from the writer's bytes.
 /// </summary>
    [TestClass]
    public unsafe class S8aFeedTests
    {
        private static S8aNotice N(uint seq, byte kind, byte layer, ushort flags, int bx, int by, int bz, uint version, int[] mn, int[] mx, int octrees, uint cells)
        {
            var n = new S8aNotice { seq = seq, kind = kind, layer = layer, flags = flags, version = version, octrees = (ushort)octrees, cells = cells };
            n.SetId(bx, by, bz);
            n.SetBox(mn[0], mn[1], mn[2], mx[0], mx[1], mx[2]);
            return n;
        }

        [TestMethod]
        public void Vectors_NoticeHeaderC7Bytes()
        {
            int notices = 0, headers = 0, c7s = 0;
            foreach (string[] f in S8aVectors.Lines("feed.tsv"))
            {
                switch (f[0])
                {
                    case "notice":
                    {
                        notices++;
                        S8aNotice n = notices == 1
                            ? N(0x01020304, 1, 2, 0x0105, -35, 19, 60, 0xA0B0C0D0, new[] { 1, 2, 3 }, new[] { 157, 158, 159 }, 125, 0xFEDCBA98)
                            : S8aNotice.ChartStep(4, 24, -24);
                        if (notices == 2) n.seq = 9;
                        Assert.AreEqual(f[1], S8aVectors.Hex(n.ToBytes()), "notice " + notices);
                        S8aNotice back = S8aNotice.FromBytes(S8aVectors.Unhex(f[1]), 0);
                        Assert.AreEqual(n.ToString(), back.ToString());
                        Assert.IsTrue(back.IsValid);
                        break;
                    }
                    case "header":
                    {
                        headers++;
                        var h = new S8aHeader
                        {
                            version = 1, flags = 0x2B, rowCount = 1024, firstSeq = 0xFFFFFFF0, stream = 0x01000001, dropped = 4096, tickLow = 0x89ABCDEF,
                            resyncEpoch = 3, storeKey = 0x8877665544332211, overlayGen = 12, chartSeq = 2, cameraBiome = 7, nearPendingCount = 3,
                            seabedBelowCamM = -1.5f, windowGen = 300, drillGen = 17, chartKey = 0xDEADBEEFCAFEF00D,
                        };
                        Assert.AreEqual(f[1], S8aVectors.Hex(h.ToBytes()));
                        S8aHeader back = S8aHeader.FromBytes(S8aVectors.Unhex(f[1]), 0);
                        Assert.AreEqual(h.chartKey, back.chartKey);
                        Assert.AreEqual(h.seabedBelowCamM, back.seabedBelowCamM);
                        Assert.IsTrue(back.Has(C2S8aFlags.DrillOn) && back.Has(C2S8aFlags.WindowOn) && back.Has(C2S8aFlags.NearPending) && back.Has(C2S8aFlags.Resync));
                        Assert.IsFalse(back.Has(C2S8aFlags.TreadmillOn));
                        break;
                    }
                    case "c7":
                    {
                        c7s++;
                        var c = new S8aC7
                        {
                            version = 1, flags = 0x1F, reloadsPending = 8, ackSeq = 1000, appliedSeq = 990, stream = 7, resyncEpoch = 2, chartSeqApplied = 1,
                            batchesServed = 123456, reloadFailures = 3, lastLatencyMs = 640, nearPendingSeen = 2, storeKey = 0x0102030405060708,
                        };
                        Assert.AreEqual(f[1], S8aVectors.Hex(c.ToBytes()));
                        // into and out of a SnStatus record at 0x8C
                        var st = default(SnStatus);
                        c.WriteInto(ref st);
                        S8aC7 back = S8aC7.ReadFrom(ref st);
                        Assert.AreEqual(f[1], S8aVectors.Hex(back.ToBytes()));
                        var raw = new byte[256];
                        fixed (byte* p = raw) *(SnStatus*)p = st;
                        for (int i = 0; i < 0x8C; i++) Assert.AreEqual(0, raw[i], "SnStatus byte " + i + " untouched");
                        for (int i = 0xBC; i < 256; i++) Assert.AreEqual(0, raw[i], "SnStatus byte " + i + " untouched");
                        break;
                    }
                    case "scenario":
                        break;
                    default:
                        Assert.Fail("unknown row " + f[0]);
                        break;
                }
            }
            Assert.AreEqual(2, notices);
            Assert.AreEqual(1, headers);
            Assert.AreEqual(1, c7s);
        }

        private static S8aHeader ScenarioState()
        {
            return new S8aHeader
            {
                flags = (byte)(C2S8aFlags.DrillOn | C2S8aFlags.WindowOn), storeKey = 0x1122334455667788, overlayGen = 3, chartSeq = 0, cameraBiome = 255,
                nearPendingCount = 2, seabedBelowCamM = 42.5f, windowGen = 9, drillGen = 4, chartKey = 0x0A0B0C0D0E0F1011,
            };
        }

        /// <summary>The writer scenario of the twin (s8a_ref.feed_scenario): label → (header bytes, rows fnv, rows).</summary>
        private static List<Tuple<string, string, ulong, int>> RunScenario(byte* region, S8aFeedReader reader)
        {
            var out_ = new List<Tuple<string, string, ulong, int>>();
            var w = new S8aFeedWriter(7);
            S8aHeader st = ScenarioState();
            Func<ulong, bool, string> step = (tick, rows) =>
            {
                int n = w.WriteInto(region, tick, st, rows);
                if (reader != null) { reader.CopyInWindow(region); reader.Commit(tick); }
                var hdr = new byte[64];
                for (int i = 0; i < 64; i++) hdr[i] = region[C2S8a.Offset + i];
                ulong fnv = LayoutHash.Fnv1a64(region + C2S8a.RowsOffset, (long)n * C2S8a.RowSize);
                out_.Add(Tuple.Create("", S8aVectors.Hex(hdr), fnv, n));
                return null;
            };
            Action<string> label = l => { var t = out_[out_.Count - 1]; out_[out_.Count - 1] = Tuple.Create(l, t.Item2, t.Item3, t.Item4); };
            for (int i = 0; i < 100; i++)
                Assert.IsTrue(w.TryEnqueue(S8aNotice.BatchWritten((byte)(1 + i % 2), i - 40, i % 20, 60 - i, (uint)(i + 1), (ushort)(i % 3),
                                                                  i % 160, 0, 3, 159, i % 160, 150, i % 126, (uint)(i * 1000))));
            step(1000, true); label("t1");
            step(1001, true); label("t2");
            w.OnAck(7, 50);
            step(1002, true); label("t3-ack50");
            w.OnAck(8, 90);
            step((1UL << 32) + 5, true); label("t4-wrongstream");
            for (int i = 0; i < 4096; i++)
                w.TryEnqueue(S8aNotice.BatchCleared(2, -35 + i % 96, i % 20, 60 - i % 96, (uint)(i + 1000), 0));
            w.TryEnqueue(S8aNotice.ChartStep(5, 24, -24));
            w.TryEnqueue(S8aNotice.StoreReset(1, 77));
            step(2000, true); label("t5-overflow");
            w.OnAck(7, w.FirstSeq + 9, 0);
            step(2001, true); label("t6-epoch0");
            w.OnAck(7, w.FirstSeq + 19, w.ResyncEpoch);
            S8aHeader st2 = st;
            st2.flags |= C2S8aFlags.Resync;
            st = st2;
            step(2002, true); label("t7-echo");
            st = ScenarioState();
            step(2003, false); label("t8-norows");
            Assert.AreEqual(4096u, w.Dropped);
            Assert.AreEqual(1u, w.ResyncEpoch);
            Assert.IsFalse(w.ResyncPending);
            Assert.AreEqual(1L, w.Overflows);
            return out_;
        }

        [TestMethod]
        public void Scenario_MatchesTheTwin()
        {
            var expected = new Dictionary<string, string[]>();
            foreach (string[] f in S8aVectors.Lines("feed.tsv"))
                if (f[0] == "scenario") expected[f[1]] = f;
            Assert.AreEqual(8, expected.Count);
            using (var buf = new NativeBuffer(0x80000))
            {
                List<Tuple<string, string, ulong, int>> got = RunScenario(buf.Ptr, null);
                Assert.AreEqual(8, got.Count);
                foreach (var g in got)
                {
                    string[] e = expected[g.Item1];
                    Assert.AreEqual(e[2], g.Item2, g.Item1 + " header");
                    Assert.AreEqual(S8aVectors.X16(e[3]), g.Item3, g.Item1 + " rows fnv");
                    Assert.AreEqual(S8aVectors.I(e[4]), g.Item4, g.Item1 + " rows");
                }
            }
        }

        [TestMethod]
        public void Scenario_ThroughTheReader_TakesEveryNoticeOnceInOrder_AndSeesTheResync()
        {
            using (var buf = new NativeBuffer(0x80000))
            {
                var reader = new S8aFeedReader();
                RunScenario(buf.Ptr, reader);
                Assert.AreEqual(8, reader.Commits);
                Assert.AreEqual(0, reader.Stale);
                Assert.AreEqual(7u, reader.CurrentStream);
                Assert.IsTrue(reader.Resyncs >= 1, "the overflow raised Resync");
                Assert.IsTrue(reader.TakeResyncNeeded());
                Assert.IsFalse(reader.TakeResyncNeeded(), "reported once");
                Assert.AreEqual(1u, reader.ResyncEpochSeen);
                // the first 100 notices (t1 + t2) and then the post-overflow rows t5 (52), t6 (+ none new), t7 (+ none new)
                uint last = 0;
                int n = 0;
                S8aNotice x;
                while (reader.TryDequeue(out x))
                {
                    if (n > 0 && n != 100) Assert.AreEqual(last + 1, x.seq, "consecutive at " + n);
                    last = x.seq;
                    n++;
                }
                Assert.AreEqual(100 + 52, n, "100 before the overflow, 52 rows after it (seq restarts past the dropped ones)");
                Assert.IsTrue(reader.Missed >= 4096 - 52, "the 4,096 dropped notices count as missed: " + reader.Missed);
                Assert.IsTrue(reader.LastHeaderValid);
                Assert.AreEqual(0x1122334455667788UL, reader.LastHeader.storeKey);
                Assert.AreEqual(0, reader.LastHeader.rowCount, "the last write had no rows");
            }
        }

        [TestMethod]
        public void Reader_StreamChangeGapAndOverflow()
        {
            var r = new S8aFeedReader();
            var rows = new S8aNotice[4];
            for (int i = 0; i < 4; i++) { rows[i] = S8aNotice.BatchWritten(1, 12, 18, 12, (uint)(i + 1), 0, 0, 0, 0, 159, 159, 159, 125, 1); rows[i].seq = (uint)(10 + i); }
            r.Apply(3, 10, rows, 4);
            Assert.AreEqual(4, r.PendingCount);
            Assert.AreEqual(13u, r.ReceivedSeq);
            Assert.IsFalse(r.TakeResyncNeeded(), "the first stream needs no resync");
            r.Apply(3, 10, rows, 4);
            Assert.AreEqual(4, r.PendingCount, "repeated rows are not taken again");
            Assert.AreEqual(4, r.AlreadyTaken);
            // a gap: firstSeq 20 while 14 was expected
            for (int i = 0; i < 4; i++) rows[i].seq = (uint)(20 + i);
            r.Apply(3, 20, rows, 4);
            Assert.AreEqual(8, r.PendingCount);
            Assert.AreEqual(6, r.Missed);
            Assert.IsTrue(r.TakeResyncNeeded(), "a gap asks for a manifest re-read");
            // a new stream restarts the seq
            for (int i = 0; i < 4; i++) rows[i].seq = (uint)(1 + i);
            r.Apply(4, 1, rows, 4);
            Assert.AreEqual(1, r.StreamChanges);
            Assert.AreEqual(4u, r.CurrentStream);
            Assert.AreEqual(4, r.PendingCount, "pending notices of the old stream dropped");
            Assert.IsTrue(r.TakeResyncNeeded());
            S8aNotice n;
            Assert.IsTrue(r.TryDequeue(out n));
            Assert.AreEqual(1u, n.seq);
            // an invalid row is counted and skipped but its seq is consumed
            var bad = new[] { new S8aNotice { seq = 5, kind = 9, layer = 1 } };
            r.Apply(4, 5, bad, 1);
            Assert.AreEqual(1, r.BadRows);
            Assert.AreEqual(5u, r.ReceivedSeq);
            // overflow of the pending queue
            var many = new S8aNotice[C2S8a.MaxRows];
            uint seq = 6;
            for (int k = 0; k < 5; k++)
            {
                for (int i = 0; i < many.Length; i++) { many[i] = S8aNotice.BatchCleared(2, 30, 1, 30, 1, 0); many[i].seq = seq++; }
                r.Apply(4, many[0].seq, many, many.Length);
            }
            Assert.IsTrue(r.Overflow >= 1, "a full pending queue is counted");
            Assert.AreEqual(S8aFeedReader.PendingCapacity, r.PendingCount);
            Assert.IsTrue(r.TakeResyncNeeded());
            r.Reset();
            Assert.AreEqual(0, r.PendingCount);
            Assert.AreEqual(0u, r.CurrentStream);
        }

        [TestMethod]
        public void Writer_RestartKeepsPendingRenumbered_AndInvalidNoticesRefused()
        {
            var w = new S8aFeedWriter(1);
            uint seq;
            Assert.IsTrue(w.TryEnqueue(S8aNotice.BatchWritten(NoticeLayer.Drill, 1, 2, 3, 1, 0, 0, 0, 0, 1, 1, 1, 1, 1), out seq));
            Assert.AreEqual(1u, seq);
            Assert.IsTrue(w.TryEnqueue(S8aNotice.StoreReset(NoticeLayer.Window, 2), out seq));
            Assert.AreEqual(2u, seq);
            Assert.IsFalse(w.TryEnqueue(new S8aNotice { kind = NoticeKind.BatchWritten, layer = 0 }, out seq), "a batch notice needs a layer");
            Assert.IsFalse(w.TryEnqueue(new S8aNotice { kind = NoticeKind.ChartStep, layer = 1 }, out seq), "a chart step has layer 0");
            Assert.IsFalse(w.TryEnqueue(new S8aNotice { kind = 0, layer = 1 }, out seq));
            Assert.AreEqual(3L, w.Invalid);
            Assert.AreEqual(2, w.Pending);
            w.OnAck(1, 1);
            Assert.AreEqual(1, w.Pending);
            Assert.AreEqual(2u, w.FirstSeq);
            w.Restart(2, true);
            Assert.AreEqual(2u, w.Stream);
            Assert.AreEqual(1, w.Pending);
            Assert.AreEqual(1u, w.FirstSeq, "kept notices renumbered from 1");
            Assert.AreEqual(2u, w.NextSeq);
            Assert.AreEqual(0u, w.SentUpTo);
            var copy = new S8aNotice[4];
            Assert.AreEqual(1, w.CopyPending(copy));
            Assert.AreEqual(NoticeKind.StoreReset, copy[0].kind);
            w.Restart(3, false);
            Assert.AreEqual(0, w.Pending);
            w.RaiseResync();
            Assert.IsTrue(w.ResyncPending);
            Assert.AreEqual(1u, w.ResyncEpoch);
            w.OnAck(3, 0, 1);
            Assert.IsFalse(w.ResyncPending);
            // the header carries version 1 even without rows; WriteOff zeroes it
            using (var buf = new NativeBuffer(0x80000))
            {
                Assert.AreEqual(0, w.WriteInto(buf.Ptr, 77, new S8aHeader()));
                S8aHeader h;
                Assert.IsTrue(S8aHeader.TryRead(buf.Ptr, 77, out h));
                Assert.AreEqual(1, h.version);
                Assert.AreEqual(3u, h.stream);
                Assert.AreEqual(1u, h.firstSeq, "the next seq when the queue is empty");
                Assert.IsFalse(S8aHeader.TryRead(buf.Ptr, 78, out h), "another tick: stale");
                S8aHeader.WriteOff(buf.Ptr);
                Assert.IsFalse(S8aHeader.TryRead(buf.Ptr, 77, out h));
                Assert.AreEqual(0, h.version);
            }
        }
    }
}
