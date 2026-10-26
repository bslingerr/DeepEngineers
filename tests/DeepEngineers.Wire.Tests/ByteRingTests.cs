using System;
using System.Collections.Generic;
using System.Threading;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>S0.N3 byte rings (C3, C4, C11, C12): framing, wrap and pad, full and too-large policies, corruption, and S0.M3.</summary>
    [TestClass]
    public unsafe class ByteRingTests
    {
        public TestContext TestContext { get; set; }

        private static void WriteOk(ByteRing ring, uint type, int payloadBytes, byte fill)
        {
            var p = new byte[payloadBytes];
            for (int i = 0; i < p.Length; i++) p[i] = fill;
            Assert.AreEqual(RingWriteStatus.Ok, ring.TryWrite(type, p, 0, p.Length));
        }

        private static void ReadExpect(ByteRing ring, uint type, int payloadBytes, byte fill)
        {
            var buf = new byte[4096];
            uint t;
            int n;
            Assert.AreEqual(RingReadStatus.Ok, ring.TryRead(out t, buf, out n));
            Assert.AreEqual(type, t, "type");
            Assert.AreEqual(payloadBytes, n, "payloadBytes");
            for (int i = 0; i < n; i++) Assert.AreEqual(fill, buf[i], "payload byte " + i);
        }

        [TestMethod]
        public void S0N3_Empty()
        {
            using (var m = new TestRingMemory(64))
            {
                ByteRing ring = m.NewByteRing();
                uint t; byte* p; int n;
                Assert.AreEqual(RingReadStatus.Empty, ring.TryPeek(out t, out p, out n));
                Assert.AreEqual(0L, ring.Used);
                Assert.AreEqual(64L, ring.Free);
            }
        }

        [TestMethod]
        public void S0N3_Framing_HeaderPlusPayloadPaddedTo8()
        {
            using (var m = new TestRingMemory(256))
            {
                ByteRing ring = m.NewByteRing();
                int[] sizes = { 0, 1, 7, 8, 9, 15, 16 };
                long[] framed = { 8, 16, 16, 16, 24, 24, 24 };
                ulong head = 0;
                for (int i = 0; i < sizes.Length; i++)
                {
                    Assert.AreEqual(framed[i], ByteRing.FramedSize(sizes[i]), "framed " + sizes[i]);
                    WriteOk(ring, 5, sizes[i], 0xAB);
                    head += (ulong)framed[i];
                    Assert.AreEqual(head, *m.Head, "head after payload " + sizes[i]);
                }
                // raw framing of the second message (payload 1): {u32 type, u32 payloadBytes} + 0xAB + 7 zero bytes
                byte* d = m.Data + 8;
                Assert.AreEqual(5u, *(uint*)d);
                Assert.AreEqual(1u, *(uint*)(d + 4));
                Assert.AreEqual((byte)0xAB, d[8]);
                for (int i = 9; i < 16; i++) Assert.AreEqual((byte)0, d[i], "padding is zero");
                for (int i = 0; i < sizes.Length; i++) ReadExpect(ring, 5, sizes[i], 0xAB);
                Assert.AreEqual(*m.Head, *m.Tail);
            }
        }

        [TestMethod]
        public void S0N3_ExactFitAtTheEnd_NeedsNoPad()
        {
            using (var m = new TestRingMemory(64))
            {
                ByteRing ring = m.NewByteRing();
                WriteOk(ring, 1, 24, 1);   // [0, 32)
                WriteOk(ring, 1, 24, 2);   // [32, 64): ends exactly at the end
                Assert.AreEqual(64UL, *m.Head);
                ReadExpect(ring, 1, 24, 1);
                ReadExpect(ring, 1, 24, 2);
                WriteOk(ring, 1, 8, 3);    // head 64 → offset 0, no pad
                Assert.AreEqual(80UL, *m.Head);
                Assert.AreEqual(1u, *(uint*)m.Data, "message at offset 0");
                ReadExpect(ring, 1, 8, 3);
            }
        }

        [TestMethod]
        public void S0N3_MessageThatWouldCrossTheEnd_IsPrecededByAPadRecord()
        {
            using (var m = new TestRingMemory(128)) // largest message: 64
            {
                ByteRing ring = m.NewByteRing();
                WriteOk(ring, 1, 56, 1);    // [0, 64)
                ReadExpect(ring, 1, 56, 1);
                WriteOk(ring, 1, 32, 1);    // [64, 104)
                ReadExpect(ring, 1, 32, 1); // tail 104
                WriteOk(ring, 2, 24, 2);    // needs 32 at offset 104: only 24 left → pad [104, 128) + message [0, 32)
                Assert.AreEqual(104UL + 24 + 32, *m.Head, "head counts the pad");
                Assert.AreEqual(0u, *(uint*)(m.Data + 104), "pad type 0");
                Assert.AreEqual(16u, *(uint*)(m.Data + 108), "pad payloadBytes = bytes to the end − 8");
                Assert.AreEqual(2u, *(uint*)m.Data, "message at the ring start");
                ReadExpect(ring, 2, 24, 2);
                Assert.AreEqual(160UL, *m.Tail, "consumer skipped the pad and the message");
            }
        }

        [TestMethod]
        public void S0N3_PadCountsTowardsFull_AndAFullRingWritesNothing()
        {
            using (var m = new TestRingMemory(128))
            {
                ByteRing ring = m.NewByteRing();
                WriteOk(ring, 1, 56, 1);    // [0, 64), not consumed
                WriteOk(ring, 1, 32, 2);    // [64, 104), not consumed: 24 bytes free, all before the end
                ulong before = m.DataChecksum();
                Assert.AreEqual(RingWriteStatus.Full, ring.TryWrite(2, new byte[24], 0, 24), "24 (pad) + 32 > 24 free");
                Assert.AreEqual(104UL, *m.Head, "head unchanged");
                Assert.AreEqual(before, m.DataChecksum(), "nothing written, not even the pad");
                ReadExpect(ring, 1, 56, 1); // 64 more free at the start
                Assert.AreEqual(RingWriteStatus.Ok, ring.TryWrite(2, new byte[24], 0, 24), "the producer's retry succeeds");
                ReadExpect(ring, 1, 32, 2);
                ReadExpect(ring, 2, 24, 0);
            }
        }

        [TestMethod]
        public void S0N3_FullExactly_ThenRequeueSucceeds()
        {
            using (var m = new TestRingMemory(64))
            {
                ByteRing ring = m.NewByteRing();
                WriteOk(ring, 1, 24, 1);
                WriteOk(ring, 1, 24, 2);
                Assert.AreEqual(64L, ring.Used, "head − tail = capacity");
                Assert.AreEqual(0L, ring.Free);
                Assert.AreEqual(RingWriteStatus.Full, ring.TryWrite(1, new byte[0], 0, 0), "even an empty message");
                // C3/C4/C12 policy: the producer tried once; it re-queues and tries again next tick
                ReadExpect(ring, 1, 24, 1);
                WriteOk(ring, 1, 24, 3);
                ReadExpect(ring, 1, 24, 2);
                ReadExpect(ring, 1, 24, 3);
            }
        }

        [TestMethod]
        public void S0N3_MessagesLargerThanHalfTheRing_AreRejected()
        {
            using (var m = new TestRingMemory(64))
            {
                ByteRing ring = m.NewByteRing();
                Assert.AreEqual(32L, ring.MaxMessageBytes);
                Assert.AreEqual(24L, ring.MaxPayloadBytes);
                Assert.AreEqual(RingWriteStatus.TooLarge, ring.TryWrite(1, new byte[25], 0, 25), "8 + 32 > 32");
                Assert.AreEqual(0UL, *m.Head);
                WriteOk(ring, 1, 24, 9);
            }
            using (var s = SharedSection.Create(TestUtil.UniqueSectionName(), Layout.SectionSize, TestUtil.Sddl, out bool existed))
            {
                var c3 = new ByteRing(s.Base, RingIndex.C3SeModels);
                Assert.AreEqual(0x4000000L, c3.Capacity);
                Assert.AreEqual(0x2000000L - 8, c3.MaxPayloadBytes);
                var chunk = new ModelChunkHeader { modelKey = 1, chunkIndex = 0, bytes = (uint)C3Type.MaxChunkBytes };
                var body = new byte[C3Type.MaxChunkBytes];
                fixed (byte* b = body)
                    Assert.AreEqual(RingWriteStatus.Ok, c3.TryWrite(C3Type.ModelChunk, chunk, b, body.Length), "a 4 MiB chunk fits");
                Assert.AreEqual((ulong)ByteRing.FramedSize(16 + C3Type.MaxChunkBytes), RingControl.ReadHead(s.Base, RingIndex.C3SeModels));
            }
        }

        [TestMethod]
        public void S0N3_TypeZeroIsReservedForThePad()
        {
            using (var m = new TestRingMemory(64))
            {
                Assert.AreEqual(RingWriteStatus.InvalidType, m.NewByteRing().TryWrite(0, new byte[8], 0, 8));
                Assert.AreEqual(0UL, *m.Head);
            }
        }

        [TestMethod]
        public void S0N3_C11Policy_ReplyThatDoesNotFit_IsDroppedAndCounted()
        {
            using (var m = new TestRingMemory(64))
            {
                ByteRing ring = m.NewByteRing();
                var reply = new byte[24];
                Assert.AreEqual(RingWriteStatus.Ok, ring.TryWriteOrDrop(C11Type.Json, reply, 0, 24));
                Assert.AreEqual(RingWriteStatus.Ok, ring.TryWriteOrDrop(C11Type.Json, reply, 0, 24));
                Assert.AreEqual(RingWriteStatus.Full, ring.TryWriteOrDrop(C11Type.Json, reply, 0, 24));
                Assert.AreEqual(RingWriteStatus.TooLarge, ring.TryWriteOrDrop(C11Type.Json, new byte[100], 0, 100));
                Assert.AreEqual(2L, ring.Dropped);
            }
        }

        [TestMethod]
        public void S0N3_BufferTooSmall_LeavesTheMessage()
        {
            using (var m = new TestRingMemory(64))
            {
                ByteRing ring = m.NewByteRing();
                WriteOk(ring, 1, 20, 4);
                uint t; int n;
                Assert.AreEqual(RingReadStatus.BufferTooSmall, ring.TryRead(out t, new byte[10], out n));
                Assert.AreEqual(0UL, *m.Tail);
                ReadExpect(ring, 1, 20, 4);
            }
        }

        [TestMethod]
        public void S0N3_ImpossibleCounters_AreCorrupt_AndResyncRecovers()
        {
            using (var m = new TestRingMemory(64))
            {
                ByteRing ring = m.NewByteRing();
                uint t; byte* p; int n;

                *m.Head = 8; *m.Tail = 0;                       // fine so far: a zero header = pad at offset 0 → corrupt
                ring = m.NewByteRing();
                Assert.AreEqual(RingReadStatus.Corrupt, ring.TryPeek(out t, out p, out n), "pad at the ring start");

                *m.Head = 0; *m.Tail = 64;                      // head behind tail
                ring = m.NewByteRing();
                Assert.AreEqual(RingReadStatus.Corrupt, ring.TryPeek(out t, out p, out n), "head behind tail");

                *m.Head = 200; *m.Tail = 64;                    // more than a ring in flight
                ring = m.NewByteRing();
                Assert.AreEqual(RingReadStatus.Corrupt, ring.TryPeek(out t, out p, out n), "head − tail > capacity");

                *m.Head = 12; *m.Tail = 0;                      // misaligned
                ring = m.NewByteRing();
                Assert.AreEqual(RingReadStatus.Corrupt, ring.TryPeek(out t, out p, out n), "misaligned");

                *m.Head = 64; *m.Tail = 48;                     // a header at 48 claiming 24 bytes crosses the end
                *(uint*)(m.Data + 48) = 1; *(uint*)(m.Data + 52) = 24;
                ring = m.NewByteRing();
                Assert.AreEqual(RingReadStatus.Corrupt, ring.TryPeek(out t, out p, out n), "crosses the end");
                Assert.AreEqual(48UL, *m.Tail, "nothing consumed");

                *m.Head = 16; *m.Tail = 0;                      // a header claiming more than what was published
                *(uint*)m.Data = 1; *(uint*)(m.Data + 4) = 16;
                ring = m.NewByteRing();
                Assert.AreEqual(RingReadStatus.Corrupt, ring.TryPeek(out t, out p, out n), "beyond the head");
                Assert.AreEqual(1L, ring.CorruptCount);

                ring.ResyncTail();
                Assert.AreEqual(16UL, *m.Tail, "tail := head");
                Assert.AreEqual(16UL, *m.Head, "the head is never written by the consumer");
                Assert.AreEqual(RingReadStatus.Empty, ring.TryPeek(out t, out p, out n));

                // producer side: impossible counters or a misaligned head found in the section → Corrupt, nothing written
                *m.Head = 60; *m.Tail = 60;
                ring = m.NewByteRing();
                ulong sum = m.DataChecksum();
                Assert.AreEqual(RingWriteStatus.Corrupt, ring.TryWrite(1, new byte[8], 0, 8), "misaligned head");
                *m.Head = 8; *m.Tail = 200;
                ring = m.NewByteRing();
                Assert.AreEqual(RingWriteStatus.Corrupt, ring.TryWrite(1, new byte[8], 0, 8), "tail ahead of head");
                Assert.AreEqual(sum, m.DataChecksum(), "nothing written");
                Assert.AreEqual(8UL, *m.Head);
            }
        }

        [TestMethod]
        public void S0N3_RealRegions_CapacitiesAndKinds()
        {
            using (var s = SharedSection.Create(TestUtil.UniqueSectionName(), Layout.SectionSize, TestUtil.Sddl, out bool existed))
            {
                RingIndex[] byteRings = { RingIndex.C3SeModels, RingIndex.C4SeParts, RingIndex.C12SnStatics, RingIndex.C11aCtlToSe,
                                          RingIndex.C11bCtlFromSe, RingIndex.C11cCtlToSn, RingIndex.C11dCtlFromSn };
                long[] caps = { 64L << 20, 8L << 20, 4L << 20, 256L << 10, 256L << 10, 256L << 10, 256L << 10 };
                for (int i = 0; i < byteRings.Length; i++)
                {
                    var ring = new ByteRing(s.Base, byteRings[i]);
                    Assert.AreEqual(caps[i], ring.Capacity, byteRings[i].ToString());
                    var msg = new ClearAll { worldEpoch = 3, linkEpoch = 4 };
                    Assert.AreEqual(RingWriteStatus.Ok, ring.TryWrite(9, msg));
                    uint t; byte* p; int n;
                    Assert.AreEqual(RingReadStatus.Ok, ring.TryPeek(out t, out p, out n));
                    Assert.AreEqual(8, n);
                    Assert.AreEqual(4u, ((ClearAll*)p)->linkEpoch);
                    ring.Advance();
                    Assert.AreEqual(16UL, RingControl.ReadTail(s.Base, byteRings[i]));
                }
                try { new ByteRing(s.Base, RingIndex.C8SnDamage); Assert.Fail("C8 is a record ring"); }
                catch (ArgumentException) { }
            }
        }

        /// <summary>Random sizes, interleaved single-thread writes and reads; returns (received, bad, corrupt).</summary>
        private static void RunRandom(ByteRing writer, ByteRing reader, int messages, int maxBody, int seed,
            out long received, out long bad, out long corrupt)
        {
            var rnd = new Random(seed);
            var buf = new byte[Msg.Header + maxBody];
            ulong nextSend = 1, nextExpect = 1;
            received = 0; bad = 0; corrupt = 0;
            var pending = new Queue<int>();
            while (nextSend <= (ulong)messages || nextExpect < nextSend)
            {
                bool write = nextSend <= (ulong)messages && rnd.Next(3) != 0;
                if (write)
                {
                    int body = rnd.Next(maxBody + 1);
                    int n = Msg.Fill(buf, nextSend, body);
                    RingWriteStatus st = writer.TryWrite(7, buf, 0, n);
                    if (st == RingWriteStatus.Ok) nextSend++;
                    else Assert.AreEqual(RingWriteStatus.Full, st);
                }
                else
                {
                    uint t; byte* p; int n;
                    RingReadStatus st = reader.TryPeek(out t, out p, out n);
                    if (st == RingReadStatus.Empty) { if (nextSend > (ulong)messages) break; continue; }
                    if (st == RingReadStatus.Corrupt) { corrupt++; reader.ResyncTail(); nextExpect = nextSend; continue; }
                    ulong seq;
                    if (t != 7 || !Msg.Check(p, n, out seq) || seq != nextExpect) { bad++; if (Msg.Check(p, n, out seq) && seq >= nextExpect) nextExpect = seq + 1; }
                    else { nextExpect++; received++; }
                    reader.Advance();
                }
            }
        }

        [TestMethod]
        public void S0N3_RandomSizes_ManyWraps_EveryMessageIntact()
        {
            using (var m = new TestRingMemory(1024))
            {
                ByteRing ring = m.NewByteRing();
                long received, bad, corrupt;
                RunRandom(ring, ring, 200000, 400, 12345, out received, out bad, out corrupt);
                TestContext.WriteLine("received=" + received + " bad=" + bad + " corrupt=" + corrupt + " head=" + *m.Head);
                Assert.AreEqual(200000L, received);
                Assert.AreEqual(0L, bad);
                Assert.AreEqual(0L, corrupt);
                Assert.IsTrue(*m.Head > 1000UL * 1024, "the ring wrapped many times");
            }
        }

        [TestMethod]
        public void S0N3_TwoThreads_SPSC_EveryMessageIntact()
        {
            using (var m = new TestRingMemory(64 * 1024))
            {
                ByteRing producer = m.NewByteRing();
                ByteRing consumer = m.NewByteRing();
                const int Count = 300000;
                long bad = 0, corrupt = 0, received = 0, fullRetries = 0;
                var prod = new Thread(() =>
                {
                    var rnd = new Random(7);
                    var buf = new byte[Msg.Header + 3000];
                    for (ulong seq = 1; seq <= Count; )
                    {
                        int n = Msg.Fill(buf, seq, rnd.Next(3001));
                        if (producer.TryWrite(3, buf, 0, n) == RingWriteStatus.Ok) seq++;
                        else { fullRetries++; Thread.SpinWait(20); }
                    }
                });
                var cons = new Thread(() =>
                {
                    ulong expect = 1;
                    while (expect <= Count)
                    {
                        uint t; byte* p; int n;
                        RingReadStatus st = consumer.TryPeek(out t, out p, out n);
                        if (st == RingReadStatus.Empty) { Thread.SpinWait(10); continue; }
                        if (st == RingReadStatus.Corrupt) { corrupt++; break; }
                        ulong seq;
                        if (t != 3 || !Msg.Check(p, n, out seq) || seq != expect) { bad++; break; }
                        expect++; received++;
                        consumer.Advance();
                    }
                });
                prod.Start(); cons.Start();
                Assert.IsTrue(cons.Join(TimeSpan.FromSeconds(120)), "consumer finished");
                prod.Join(TimeSpan.FromSeconds(10));
                TestContext.WriteLine("received=" + received + " fullRetries=" + fullRetries + " head=" + *m.Head);
                Assert.AreEqual(0L, bad, "misparsed");
                Assert.AreEqual(0L, corrupt, "corrupt");
                Assert.AreEqual((long)Count, received);
            }
        }

        [TestMethod]
        public void S0M3_WriterWithoutThePad_ExactCase_IsCaught()
        {
            // Ring of 128 (largest message 64). A stale header (message C) sits at offset 104 when F, framed 32, must wrap
 // from offset 104 (24 bytes left): the correct writer puts a pad there, the mutant leaves C's old header.
            foreach (bool mutant in new[] { false, true })
            {
                using (var m = new TestRingMemory(128))
                {
                    ByteRing ring = m.NewByteRing();
                    if (mutant) ring.Mutant = ByteRingMutant.SkipPad;
                    var buf = new byte[128];
                    var seen = new List<ulong>();
                    ulong seq = 1;
                    // framed size = 8 + align8(16 + body)
                    Action<int> send = body => Assert.AreEqual(RingWriteStatus.Ok, ring.TryWrite(7, buf, 0, Msg.Fill(buf, seq++, body)));
                    Action drain = () =>
                    {
                        while (true)
                        {
                            uint t; byte* p; int n;
                            RingReadStatus st = ring.TryPeek(out t, out p, out n);
                            if (st != RingReadStatus.Ok) { if (st == RingReadStatus.Corrupt) seen.Add(0); break; }
                            ulong s;
                            seen.Add(Msg.Check(p, n, out s) ? s : 0);
                            ring.Advance();
                        }
                    };
                    send(40); drain();   // A [0, 64)
                    send(16); drain();   // B [64, 104)
                    send(0);  drain();   // C [104, 128)
                    send(40); drain();   // D [128, 192) = offsets [0, 64)
                    send(16); drain();   // E [192, 232) = offsets [64, 104); offset 104 still holds C's header
                    Assert.AreEqual(232UL, *m.Tail);
                    send(8);             // F framed 32 at offset 104: pad [104, 128) + F [0, 32)
                    drain();
                    Assert.AreEqual(*m.Head, *m.Tail, "drained");
                    var expected = new List<ulong> { 1, 2, 3, 4, 5, 6 };
                    if (!mutant) CollectionAssert.AreEqual(expected, seen, "correct writer");
                    else
                    {
                        TestContext.WriteLine("mutant S0.M3 received: " + string.Join(",", seen.ConvertAll(x => x.ToString()).ToArray()));
                        CollectionAssert.AreNotEqual(expected, seen, "the oracle must go red without the pad (stale message re-read)");
                    }
                }
            }
        }

        [TestMethod]
        public void S0M3_WriterWithoutThePad_RandomRun_IsCaught()
        {
            using (var m = new TestRingMemory(1024))
            {
                ByteRing ring = m.NewByteRing();
                ring.Mutant = ByteRingMutant.SkipPad;
                long received, bad, corrupt;
                RunRandom(ring, ring, 20000, 400, 999, out received, out bad, out corrupt);
                TestContext.WriteLine("mutant S0.M3: received=" + received + " bad=" + bad + " corrupt=" + corrupt);
                Assert.IsTrue(bad + corrupt > 0, "the ring oracle must go red on a writer without the pad record");
            }
        }
    }
}
