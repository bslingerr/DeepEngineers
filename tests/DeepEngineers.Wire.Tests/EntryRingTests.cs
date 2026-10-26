using System;
using System.Collections.Generic;
using System.Threading;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>S0.N3 for C8 (SPSC fixed-record ring) and the C8 overflow-queue policy (4,096, drop oldest, count).</summary>
    [TestClass]
    public unsafe class EntryRingTests
    {
        public TestContext TestContext { get; set; }

        private sealed class SmallRing : IDisposable
        {
            private readonly NativeBuffer _buf;
            public ulong* Head { get { return (ulong*)_buf.Ptr; } }
            public ulong* Tail { get { return (ulong*)(_buf.Ptr + 64); } }
            public byte* Slots { get { return _buf.Ptr + 128; } }
            public SmallRing(int capacity) { _buf = new NativeBuffer(128 + capacity * 128L); Capacity = capacity; }
            public int Capacity { get; }
            public EntryRing New() { return new EntryRing(Head, Tail, Slots, Capacity, 128); }
            public void Dispose() { _buf.Dispose(); }
        }

        private static DamageRecord Rec(uint n) { return new DamageRecord { dedupSeq = n, amount = n * 0.5f, unit = DamageUnit.SnHp }; }

        [TestMethod]
        public void S0N3_C8_FullWhenHeadMinusTailEqualsCapacity_SlotIsCounterModCapacity()
        {
            using (var m = new SmallRing(4))
            {
                EntryRing ring = m.New();
                for (uint i = 0; i < 4; i++) Assert.AreEqual(RingWriteStatus.Ok, ring.TryWrite(Rec(i)));
                Assert.AreEqual(4UL, *m.Head - *m.Tail);
                Assert.AreEqual(RingWriteStatus.Full, ring.TryWrite(Rec(4)), "head − tail = capacity");
                Assert.AreEqual(0L, ring.Free);
                DamageRecord r;
                Assert.AreEqual(RingReadStatus.Ok, ring.TryRead(out r));
                Assert.AreEqual(0u, r.dedupSeq);
                Assert.AreEqual(RingWriteStatus.Ok, ring.TryWrite(Rec(4)));
                Assert.AreEqual(4u, ((DamageRecord*)m.Slots)[0].dedupSeq, "counter 4 → slot 4 mod 4 = 0");
                for (uint i = 1; i <= 4; i++) { Assert.AreEqual(RingReadStatus.Ok, ring.TryRead(out r)); Assert.AreEqual(i, r.dedupSeq); }
                Assert.AreEqual(RingReadStatus.Empty, ring.TryRead(out r));
                Assert.AreEqual(RingWriteStatus.TooLarge, ring.TryWrite(new SeTime()), "a C8 slot is exactly one 128-byte record");
            }
        }

        [TestMethod]
        public void S0N3_C8_ManyWraps_OrderKept()
        {
            using (var m = new SmallRing(4))
            {
                EntryRing ring = m.New();
                var rnd = new Random(3);
                uint next = 0, expect = 0;
                while (expect < 10000)
                {
                    if (next < 10000 && rnd.Next(2) == 0) { if (ring.TryWrite(Rec(next)) == RingWriteStatus.Ok) next++; }
                    else
                    {
                        DamageRecord r;
                        RingReadStatus st = ring.TryRead(out r);
                        if (st == RingReadStatus.Ok) { Assert.AreEqual(expect, r.dedupSeq); expect++; }
                        else Assert.AreEqual(RingReadStatus.Empty, st);
                    }
                }
                Assert.AreEqual(10000UL, *m.Head);
            }
        }

        [TestMethod]
        public void S0N3_C8_ImpossibleCounters_AreCorrupt_ResyncDropsInFlight()
        {
            using (var m = new SmallRing(4))
            {
                *m.Head = 3; *m.Tail = 10;
                EntryRing ring = m.New();
                DamageRecord r;
                Assert.AreEqual(RingReadStatus.Corrupt, ring.TryRead(out r), "head behind tail");
                Assert.AreEqual(RingWriteStatus.Corrupt, ring.TryWrite(Rec(1)), "the producer never overwrites when the counters are impossible");
                Assert.AreEqual(3UL, *m.Head, "nothing written");
                *m.Head = 20; *m.Tail = 10;
                ring = m.New();
                Assert.AreEqual(RingReadStatus.Corrupt, ring.TryRead(out r), "more than capacity in flight");
                ring.ResyncTail();
                Assert.AreEqual(20UL, *m.Tail);
                Assert.AreEqual(20UL, *m.Head, "head untouched");
                Assert.AreEqual(RingReadStatus.Empty, ring.TryRead(out r));
            }
        }

        [TestMethod]
        public void S0N3_C8_RealRegion_1024Records()
        {
            using (var s = SharedSection.Create(TestUtil.UniqueSectionName(), Layout.SectionSize, TestUtil.Sddl, out bool existed))
            {
                var ring = new EntryRing(s.Base, RingIndex.C8SnDamage);
                Assert.AreEqual(1024L, ring.Capacity);
                Assert.AreEqual(128, ring.RecordSize);
                for (uint i = 0; i < 1024; i++) Assert.AreEqual(RingWriteStatus.Ok, ring.TryWrite(Rec(i)));
                Assert.AreEqual(RingWriteStatus.Full, ring.TryWrite(Rec(1024)));
                Assert.AreEqual(1024UL, RingControl.ReadHead(s.Base, RingIndex.C8SnDamage));
                Assert.AreEqual(1023u, ((DamageRecord*)(s.Base + 0xB0000))[1023].dedupSeq, "slot 1023 at 0xB0000 + 1023 × 128");
                try { new EntryRing(s.Base, RingIndex.C4SeParts); Assert.Fail("C4 is a byte ring"); }
                catch (ArgumentException) { }
            }
        }

        [TestMethod]
        public void S0N3_C8_OverflowPolicy_Queue4096_ThenDropOldestAndCount()
        {
            using (var s = SharedSection.Create(TestUtil.UniqueSectionName(), Layout.SectionSize, TestUtil.Sddl, out bool existed))
            {
                var producer = new EntryRing(s.Base, RingIndex.C8SnDamage);
                var consumer = new EntryRing(s.Base, RingIndex.C8SnDamage);
                var queue = new OverflowQueue<DamageRecord>();
                Assert.AreEqual(4096, queue.Capacity);

                // One Subnautica frame with 6,024 hits and SE not draining: 1,024 reach the ring, 4,096 wait, 904 dropped.
                for (uint i = 0; i < 6024; i++) queue.WriteOrQueue(producer, Rec(i));
                Assert.AreEqual(1024L, producer.Count);
                Assert.AreEqual(4096, queue.Count);
                Assert.AreEqual(904L, queue.Dropped, "C7 damageDropped");
                DamageRecord oldest;
                Assert.IsTrue(queue.TryPeek(out oldest));
                Assert.AreEqual(1024u + 904u, oldest.dedupSeq, "the OLDEST queued records were dropped");

                // Following frames: SE drains everything each tick; Subnautica flushes the queue first.
                var got = new List<uint>();
                for (int frame = 0; frame < 20 && (queue.Count > 0 || consumer.Count > 0); frame++)
                {
                    DamageRecord r;
                    while (consumer.TryRead(out r) == RingReadStatus.Ok) got.Add(r.dedupSeq);
                    queue.FlushTo(producer);
                }
                Assert.AreEqual(0, queue.Count);
                Assert.AreEqual(1024 + 4096, got.Count);
                for (int i = 0; i < 1024; i++) Assert.AreEqual((uint)i, got[i]);
                for (int i = 0; i < 4096; i++) Assert.AreEqual((uint)(1928 + i), got[1024 + i], "FIFO after the drop");
            }
        }

        [TestMethod]
        public void S0N3_C8_WriteOrQueue_KeepsOrderWhileTheQueueIsNotEmpty()
        {
            using (var m = new SmallRing(2))
            {
                EntryRing ring = m.New();
                var q = new OverflowQueue<DamageRecord>(8);
                Assert.AreEqual(RingWriteStatus.Ok, q.WriteOrQueue(ring, Rec(0)));
                Assert.AreEqual(RingWriteStatus.Ok, q.WriteOrQueue(ring, Rec(1)));
                Assert.AreEqual(RingWriteStatus.Full, q.WriteOrQueue(ring, Rec(2)));
                DamageRecord r;
                Assert.AreEqual(RingReadStatus.Ok, ring.TryRead(out r)); // one slot free now
                Assert.AreEqual(RingWriteStatus.Full, q.WriteOrQueue(ring, Rec(3)), "2 is flushed first; 3 waits");
                var got = new List<uint> { r.dedupSeq };
                while (got.Count < 4)
                {
                    while (ring.TryRead(out r) == RingReadStatus.Ok) got.Add(r.dedupSeq);
                    q.FlushTo(ring);
                }
                CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 3 }, got);
                Assert.AreEqual(0L, q.Dropped);
            }
        }

        [TestMethod]
        public void S0N3_C8_TwoThreads_SPSC()
        {
            using (var s = SharedSection.Create(TestUtil.UniqueSectionName(), Layout.SectionSize, TestUtil.Sddl, out bool existed))
            {
                var producer = new EntryRing(s.Base, RingIndex.C8SnDamage);
                var consumer = new EntryRing(s.Base, RingIndex.C8SnDamage);
                const uint Count = 1000000;
                long bad = 0;
                var p = new Thread(() =>
                {
                    for (uint i = 0; i < Count; )
                    {
                        var r = Rec(i);
                        r.snTimeSec = i * 3.0;
                        r.targetEntityId = 0xABCDEF00000000UL | i;
                        if (producer.TryWrite(r) == RingWriteStatus.Ok) i++; else Thread.SpinWait(10);
                    }
                });
                var c = new Thread(() =>
                {
                    for (uint expect = 0; expect < Count; )
                    {
                        DamageRecord r;
                        RingReadStatus st = consumer.TryRead(out r);
                        if (st == RingReadStatus.Empty) { Thread.SpinWait(10); continue; }
                        if (st != RingReadStatus.Ok || r.dedupSeq != expect || r.snTimeSec != expect * 3.0 ||
                            r.targetEntityId != (0xABCDEF00000000UL | expect)) { bad++; break; }
                        expect++;
                    }
                });
                p.Start(); c.Start();
                Assert.IsTrue(c.Join(TimeSpan.FromSeconds(120)));
                p.Join(TimeSpan.FromSeconds(10));
                Assert.AreEqual(0L, bad);
            }
        }
    }
}
