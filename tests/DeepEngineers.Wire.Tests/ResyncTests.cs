using System;
using System.Collections.Generic;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// Re-synchronisation after a restart, in process (S0.N11-like; the stand-in version runs in Python): a Subnautica
 /// restart while SE fills C4 and drains C8, an SE restart, a link-epoch recovery, and the S0.M7 mutant (a creator
 /// that zeroes the producer heads), which must misparse.
 /// </summary>
    [TestClass]
    public unsafe class ResyncTests
    {
        public TestContext TestContext { get; set; }

        private const uint TestType = 100; // payload = Msg (seq + checksum + body)

        private sealed class Outcome
        {
            public long Received, Misparsed, Corrupt, ProducerCorrupt, ClearAllSeen, C8Received, C8Bad;
            public bool ClearAllFirst = true;
            public uint SnGeneration;
            public long ResendExpected, ResendSent;
            /// <summary>Messages with a smaller seq were sent before the re-sync: seeing one is a misparse.</summary>
            public ulong MinSeq;
            public override string ToString()
            {
                return "received=" + Received + " misparsed=" + Misparsed + " corrupt=" + Corrupt + " producerCorrupt=" + ProducerCorrupt +
                       " clearAll=" + ClearAllSeen + " clearAllFirst=" + ClearAllFirst + " c8=" + C8Received + " c8bad=" + C8Bad +
                       " snGeneration=" + SnGeneration + " resend=" + ResendSent + "/" + ResendExpected;
            }
        }

        /// <summary>SN consumer: reads everything available; checks framing, checksums and strictly increasing seq.</summary>
        private static void SnDrain(ByteRing ring, Outcome o, ref ulong lastSeq, ref bool sawAny)
        {
            for (int guard = 0; guard < 1000000; guard++)
            {
                uint t; byte* p; int n;
                RingReadStatus st = ring.TryPeek(out t, out p, out n);
                if (st == RingReadStatus.Empty) return;
                if (st == RingReadStatus.Corrupt) { o.Corrupt++; ring.ResyncTail(); return; }
                if (t == C4Type.ClearAll && n == 8)
                {
                    o.ClearAllSeen++;
                    if (sawAny && o.ClearAllSeen == 1) o.ClearAllFirst = false;
                }
                else
                {
                    ulong seq;
                    if (t != TestType || !Msg.Check(p, n, out seq) || seq <= lastSeq || seq < o.MinSeq) o.Misparsed++;
                    else { lastSeq = seq; o.Received++; }
                }
                sawAny = true;
                ring.Advance();
            }
        }

        private static RingWriteStatus SeSend(ByteRing ring, byte[] buf, ulong seq, int body)
        {
            return ring.TryWrite(TestType, buf, 0, Msg.Fill(buf, seq, body));
        }

        /// <summary>
 /// Subnautica restarts while SE keeps producing on <paramref name="ringIndex"/> and draining C8. Phases are
 /// sequential so the outcome is deterministic.
 /// </summary>
        private Outcome RunSnRestart(RingIndex ringIndex, bool mutantZeroHeads)
        {
            return RunSnRestart(ringIndex, mutantZeroHeads, true);
        }

        private Outcome RunSnRestart(RingIndex ringIndex, bool mutantZeroHeads, bool wrapBeforeRestart)
        {
            var o = new Outcome();
            string name = TestUtil.UniqueSectionName();
            uint deadPid = TestUtil.DeadPid;
            CreatorResult sn1Result = SectionCreator.CreateOrAttach(TestUtil.SnOptions(name, deadPid));
            Assert.AreEqual(CreatorOutcome.Created, sn1Result.Outcome);
            SharedSection sn1 = sn1Result.Section;
            using (SharedSection se = SharedSection.OpenExisting(name, Layout.SectionSize))
            {
                Assert.IsTrue(SectionValidation.Validate(se).Ok);
                se.Header.AttachSe(777, Win32.TickCount64());
                var seProducer = new ByteRing(se.Base, ringIndex);
                var seC8 = new EntryRing(se.Base, RingIndex.C8SnDamage);
                var peer = new PeerWatch();
                Assert.AreEqual(PeerChange.NewPeer, peer.Observe(se.Header.SnPid, se.Header.SnGeneration));

                var sn1Consumer = new ByteRing(sn1.Base, ringIndex);
                var sn1C8 = new EntryRing(sn1.Base, RingIndex.C8SnDamage);
                var rnd = new Random(11);
                var buf = new byte[Msg.Header + 3000];
                ulong seq = 0;
                ulong lastSeq = 0; bool sawAny = false;
                var dummy = new Outcome();

                // Phase A: SE streams (several wraps unless asked otherwise), SN#1 drains it; SN#1 reports damage, SE drains it.
                long capacity = seProducer.Capacity;
                ulong targetBytes = wrapBeforeRestart ? (ulong)capacity * 2 + 12345 : (ulong)capacity / 4;
                uint dedup = 0;
                while (seProducer.ProducerHead < targetBytes)
                {
                    if (SeSend(seProducer, buf, seq + 1, rnd.Next(3001)) != RingWriteStatus.Ok)
                    {
                        SnDrain(sn1Consumer, dummy, ref lastSeq, ref sawAny);
                        continue;
                    }
                    seq++;
                    if ((seq & 63) == 0) sn1C8.TryWrite(new DamageRecord { dedupSeq = dedup++, snGeneration = 1 });
                    DamageRecord r;
                    if ((seq & 127) == 0) while (seC8.TryRead(out r) == RingReadStatus.Ok) { }
                }
                SnDrain(sn1Consumer, dummy, ref lastSeq, ref sawAny);
                Assert.AreEqual(0L, dummy.Misparsed + dummy.Corrupt, "phase A is clean");
                Assert.AreEqual(seq, lastSeq, "phase A fully received");
                for (int i = 0; i < 5; i++) sn1C8.TryWrite(new DamageRecord { dedupSeq = dedup++, snGeneration = 1 }); // in flight
                for (int i = 0; i < 20; i++) if (SeSend(seProducer, buf, seq + 1, 500) == RingWriteStatus.Ok) seq++;     // in flight

 // Subnautica #1 exits (the section lives on: SE holds it).
                sn1.Dispose();

                // Phase B: SE keeps producing until the ring is full; what does not fit stays queued (re-queue policy).
                if (wrapBeforeRestart)
                {
                    int fullCount = 0;
                    while (fullCount < 3)
                    {
                        if (SeSend(seProducer, buf, seq + 1, rnd.Next(3001)) == RingWriteStatus.Ok) seq++;
                        else fullCount++;
                    }
                }

                // Subnautica #2 starts: creator rules on the existing section.
                CreatorOptions opts = TestUtil.SnOptions(name, deadPid + 4);
                opts.MutantZeroProducerHeads = mutantZeroHeads;
                CreatorResult sn2Result = SectionCreator.CreateOrAttach(opts);
                Assert.AreEqual(CreatorOutcome.Reinitialised, sn2Result.Outcome, sn2Result.ToString());
                using (SharedSection sn2 = sn2Result.Section)
                {
                    o.SnGeneration = sn2.Header.SnGeneration;
                    var sn2Consumer = new ByteRing(sn2.Base, ringIndex);
                    var sn2C8 = new EntryRing(sn2.Base, RingIndex.C8SnDamage);
                    if (!mutantZeroHeads)
                        Assert.AreEqual(sn2Consumer.Head, sn2Consumer.Tail, "creator: SN consumer tail := head");

                    // SE notices the new generation: re-syncs the tails it consumes (C8 here) and resends everything:
 // ClearAll, then the stream again (sequence numbers from here on; anything older is stale).
                    Assert.AreEqual(PeerChange.NewPeer, peer.Observe(se.Header.SnPid, se.Header.SnGeneration));
                    seC8.ResyncTail();
                    o.MinSeq = seq + 1;
                    o.ResendExpected = 4000;
                    ulong sawLast = 0; bool sn2SawAny = false;
                    bool clearAllSent = false;
                    for (int budget = 0; budget < 200000 && o.ResendSent < o.ResendExpected; budget++)
                    {
                        RingWriteStatus st = clearAllSent
                            ? SeSend(seProducer, buf, seq + 1, rnd.Next(3001))
                            : seProducer.TryWrite(C4Type.ClearAll, new ClearAll { worldEpoch = 1, linkEpoch = 0 });
                        if (st == RingWriteStatus.Ok)
                        {
                            if (!clearAllSent) clearAllSent = true;
                            else { seq++; o.ResendSent++; }
                        }
                        else if (st == RingWriteStatus.Corrupt) { o.ProducerCorrupt++; break; } // SE would log and wait
                        else SnDrain(sn2Consumer, o, ref sawLast, ref sn2SawAny);
                        if ((budget & 15) == 0)
                        {
                            sn2C8.TryWrite(new DamageRecord { dedupSeq = (uint)budget, snGeneration = 2 });
                            DamageRecord r;
                            while (seC8.TryRead(out r) == RingReadStatus.Ok)
                            {
                                if (r.snGeneration != 2) o.C8Bad++; else o.C8Received++;
                            }
                        }
                    }
                    SnDrain(sn2Consumer, o, ref sawLast, ref sn2SawAny);
                }
            }
            TestContext.WriteLine((mutantZeroHeads ? "S0.M7 mutant: " : "S0.N11: ") + ringIndex + " " + o);
            return o;
        }

        [TestMethod]
        public void S0N11_SnRestart_C4_NoMisparse_TailsResynced_ResendCompletes()
        {
            AssertGreen(RunSnRestart(RingIndex.C4SeParts, false, true));
        }

        [TestMethod]
        public void S0N11_SnRestart_C4_BeforeTheFirstWrap()
        {
            AssertGreen(RunSnRestart(RingIndex.C4SeParts, false, false));
        }

        [TestMethod]
        public void S0N11_SnRestart_C3_NoMisparse()
        {
            AssertGreen(RunSnRestart(RingIndex.C3SeModels, false, true));
        }

        /// <summary>The S0.N11 oracle.</summary>
        private static bool IsGreen(Outcome o)
        {
            return o.SnGeneration == 2 && o.Misparsed == 0 && o.Corrupt == 0 && o.ProducerCorrupt == 0 && o.ClearAllSeen == 1 &&
                   o.ClearAllFirst && o.ResendSent == o.ResendExpected && o.Received == o.ResendExpected && o.C8Bad == 0 && o.C8Received > 0;
        }

        private static void AssertGreen(Outcome o)
        {
            Assert.AreEqual(2u, o.SnGeneration, "generation + 1");
            Assert.AreEqual(0L, o.Misparsed, "misparsed");
            Assert.AreEqual(0L, o.Corrupt, "corrupt (consumer)");
            Assert.AreEqual(0L, o.ProducerCorrupt, "corrupt (producer)");
            Assert.AreEqual(1L, o.ClearAllSeen, "ClearAll received once");
            Assert.IsTrue(o.ClearAllFirst, "ClearAll is the first message the new Subnautica sees");
            Assert.AreEqual(o.ResendExpected, o.ResendSent, "resend sent");
            Assert.AreEqual(o.ResendExpected, o.Received, "resend complete");
            Assert.AreEqual(0L, o.C8Bad, "SE dropped every pre-restart damage record");
            Assert.IsTrue(o.C8Received > 0);
            Assert.IsTrue(IsGreen(o));
        }

        [TestMethod]
        public void S0M7_CreatorZeroingProducerHeads_Misparses()
        {
            // After wraps: the live SE producer finds its tail behind it by more than a ring (detected, link stuck).
            Outcome wrapped = RunSnRestart(RingIndex.C4SeParts, true, true);
            Assert.IsFalse(IsGreen(wrapped), "S0.N11 must go red (" + wrapped + ")");
            Assert.IsTrue(wrapped.Misparsed + wrapped.Corrupt + wrapped.ProducerCorrupt > 0, "corruption observed (" + wrapped + ")");
            // Before the first wrap: the new Subnautica re-reads stale messages from offset 0 (misparse).
            Outcome early = RunSnRestart(RingIndex.C4SeParts, true, false);
            Assert.IsFalse(IsGreen(early), "S0.N11 must go red (" + early + ")");
            Assert.IsTrue(early.Misparsed > 0, "stale messages parsed as new (" + early + ")");
        }

        [TestMethod]
        public void S0N11_SnRestart_Concurrent_SeProducerThread_NoMisparse()
        {
            string name = TestUtil.UniqueSectionName();
            uint deadPid = TestUtil.DeadPid;
            CreatorResult first = SectionCreator.CreateOrAttach(TestUtil.SnOptions(name, deadPid));
            Assert.AreEqual(CreatorOutcome.Created, first.Outcome);
            using (SharedSection se = SharedSection.OpenExisting(name, Layout.SectionSize))
            {
                se.Header.AttachSe(777, Win32.TickCount64());
                int stop = 0;
                long sent = 0, clearAllSent = 0;
                var producer = new Thread(() =>
                {
                    var ring = new ByteRing(se.Base, RingIndex.C4SeParts);
                    var peer = new PeerWatch();
                    var rnd = new Random(5);
                    var buf = new byte[Msg.Header + 4000];
                    ulong seq = 0;
                    var hv = new HeaderView(se.Base);
                    while (Volatile.Read(ref stop) == 0)
                    {
                        if (hv.Magic == WireInfo.Magic && peer.Observe(hv.SnPid, hv.SnGeneration) == PeerChange.NewPeer && peer.Generation > 1)
                        {
                            while (ring.TryWrite(C4Type.ClearAll, new ClearAll { worldEpoch = 1 }) != RingWriteStatus.Ok) Thread.SpinWait(50);
                            clearAllSent++;
                        }
                        if (SeSend(ring, buf, seq + 1, rnd.Next(4001)) == RingWriteStatus.Ok) { seq++; sent++; }
                        else Thread.SpinWait(50);
                    }
                });
                producer.Start();

                var o = new Outcome();
                ulong last = 0; bool any = false;
                var sn1Consumer = new ByteRing(first.Section.Base, RingIndex.C4SeParts);
                var until = DateTime.UtcNow.AddMilliseconds(400);
                while (DateTime.UtcNow < until) SnDrain(sn1Consumer, o, ref last, ref any);
                Assert.AreEqual(0L, o.Misparsed + o.Corrupt, "before the restart");
                first.Section.Dispose();
                Thread.Sleep(50);

                CreatorResult second = SectionCreator.CreateOrAttach(TestUtil.SnOptions(name, deadPid + 4));
                Assert.AreEqual(CreatorOutcome.Reinitialised, second.Outcome);
                using (second.Section)
                {
                    var o2 = new Outcome();
                    ulong last2 = 0; bool any2 = false;
                    var sn2Consumer = new ByteRing(second.Section.Base, RingIndex.C4SeParts);
                    until = DateTime.UtcNow.AddMilliseconds(600);
                    while (DateTime.UtcNow < until) SnDrain(sn2Consumer, o2, ref last2, ref any2);
                    Volatile.Write(ref stop, 1);
                    producer.Join();
                    SnDrain(sn2Consumer, o2, ref last2, ref any2);
                    TestContext.WriteLine("concurrent: sent=" + sent + " clearAllSent=" + clearAllSent + " after restart: " + o2);
                    Assert.AreEqual(0L, o2.Misparsed, "misparsed after the restart");
                    Assert.AreEqual(0L, o2.Corrupt, "corrupt after the restart");
                    Assert.AreEqual(1L, clearAllSent);
                    Assert.AreEqual(1L, o2.ClearAllSeen);
                    Assert.IsTrue(o2.Received > 100);
                }
            }
        }

        [TestMethod]
        public void S0N11_SeRestart_SnResyncsItsTails_ProducerContinuesFromTheHeadFound()
        {
            string name = TestUtil.UniqueSectionName();
            CreatorResult sn = SectionCreator.CreateOrAttach(TestUtil.SnOptions(name, TestUtil.DeadPid));
            using (SharedSection snSec = sn.Section)
            {
                var snConsumer = new ByteRing(snSec.Base, RingIndex.C4SeParts);
                var snPeer = new PeerWatch();
                var buf = new byte[Msg.Header + 100];
                ulong headAtExit;
                using (SharedSection se1 = SharedSection.OpenExisting(name, Layout.SectionSize))
                {
                    se1.Header.AttachSe(1001, 1);
                    Assert.AreEqual(PeerChange.NewPeer, snPeer.Observe(snSec.Header.SePid, snSec.Header.SeGeneration));
                    var p1 = new ByteRing(se1.Base, RingIndex.C4SeParts);
                    for (ulong s = 1; s <= 50; s++) Assert.AreEqual(RingWriteStatus.Ok, SeSend(p1, buf, s, 64));
                    for (int i = 0; i < 20; i++) // SN drains 20 of 50
                    {
                        uint t; byte* p; int n;
                        Assert.AreEqual(RingReadStatus.Ok, snConsumer.TryPeek(out t, out p, out n));
                        snConsumer.Advance();
                    }
                    headAtExit = p1.ProducerHead;
                }
                // SE #2 attaches: generation + 1, its producer continues from the head it finds (never zero).
                using (SharedSection se2 = SharedSection.OpenExisting(name, Layout.SectionSize))
                {
                    Assert.IsTrue(SectionValidation.Validate(se2).Ok);
                    Assert.AreEqual(2u, se2.Header.AttachSe(1002, 2));
                    var p2 = new ByteRing(se2.Base, RingIndex.C4SeParts);
                    Assert.AreEqual(headAtExit, p2.ProducerHead, "continues from the head found");

                    Assert.AreEqual(PeerChange.NewPeer, snPeer.Observe(snSec.Header.SePid, snSec.Header.SeGeneration), "new SE generation");
                    snConsumer.ResyncTail(); // drops SE #1's 30 in-flight messages
                    Assert.AreEqual(headAtExit, snConsumer.Tail);

                    Assert.AreEqual(RingWriteStatus.Ok, p2.TryWrite(C4Type.ClearAll, new ClearAll { worldEpoch = 7 }));
                    for (ulong s = 1; s <= 10; s++) Assert.AreEqual(RingWriteStatus.Ok, SeSend(p2, buf, s, 64));
                    var o = new Outcome(); ulong last = 0; bool any = false;
                    SnDrain(snConsumer, o, ref last, ref any);
                    Assert.AreEqual(1L, o.ClearAllSeen);
                    Assert.IsTrue(o.ClearAllFirst);
                    Assert.AreEqual(10L, o.Received);
                    Assert.AreEqual(0L, o.Misparsed + o.Corrupt);
                }
            }
        }

        [TestMethod]
        public void S0N11_LinkEpoch_BothSidesResyncTheirConsumedRings()
        {
            string name = TestUtil.UniqueSectionName();
            CreatorResult sn = SectionCreator.CreateOrAttach(TestUtil.SnOptions(name, TestUtil.DeadPid));
            using (SharedSection snSec = sn.Section)
            using (SharedSection se = SharedSection.OpenExisting(name, Layout.SectionSize))
            {
                se.Header.AttachSe(1, 1);
                byte* c5 = Layout.RegionPtr(se.Base, RegionId.C5SeState);
                var seC4 = new ByteRing(se.Base, RingIndex.C4SeParts);
                var seC8 = new EntryRing(se.Base, RingIndex.C8SnDamage);
                var snC4 = new ByteRing(snSec.Base, RingIndex.C4SeParts);
                var snC8 = new EntryRing(snSec.Base, RingIndex.C8SnDamage);
                var snLink = new EpochWatch();
                var snState = new SeqlockReader<SeState>(Layout.RegionPtr(snSec.Base, RegionId.C5SeState));

                Seqlock.Write(c5, new SeState { linkEpoch = 0, worldEpoch = 1 });
                Assert.AreEqual(SeqlockReadStatus.Fresh, snState.Poll());
                snLink.Observe(snState.Value.linkEpoch);

                var buf = new byte[256];
                for (ulong s = 1; s <= 5; s++) SeSend(seC4, buf, s, 32);           // in flight during the stall
                for (uint i = 0; i < 3; i++) snC8.TryWrite(new DamageRecord { dedupSeq = i });

                // stall ends: SE bumps linkEpoch and re-syncs what it consumes
                Seqlock.Write(c5, new SeState { linkEpoch = 1, worldEpoch = 1 });
                seC8.ResyncTail();
                Assert.AreEqual(seC8.Head, seC8.Tail);
                // Subnautica sees the new epoch and re-syncs what it consumes
                Assert.AreEqual(SeqlockReadStatus.Fresh, snState.Poll());
                Assert.IsTrue(snLink.Observe(snState.Value.linkEpoch));
                snC4.ResyncTail();
                // SE resends: ClearAll(worldEpoch, linkEpoch), then everything
                Assert.AreEqual(RingWriteStatus.Ok, seC4.TryWrite(C4Type.ClearAll, new ClearAll { worldEpoch = 1, linkEpoch = 1 }));
                for (ulong s = 6; s <= 8; s++) SeSend(seC4, buf, s, 32);
                var o = new Outcome(); ulong last = 0; bool any = false;
                SnDrain(snC4, o, ref last, ref any);
                Assert.AreEqual(1L, o.ClearAllSeen);
                Assert.IsTrue(o.ClearAllFirst);
                Assert.AreEqual(3L, o.Received);
                Assert.AreEqual(0L, o.Misparsed);
            }
        }

        [TestMethod]
        public void PeerWatch_And_EpochWatch()
        {
            var w = new PeerWatch();
            Assert.AreEqual(PeerChange.None, w.Observe(0, 0));
            Assert.AreEqual(PeerChange.NewPeer, w.Observe(100, 1));
            Assert.AreEqual(PeerChange.None, w.Observe(100, 1));
            Assert.AreEqual(PeerChange.NewPeer, w.Observe(100, 2), "same PID, new generation");
            Assert.AreEqual(PeerChange.NewPeer, w.Observe(104, 2), "reused generation, new PID");
            Assert.AreEqual(PeerChange.Gone, w.Observe(0, 2));
            Assert.AreEqual(PeerChange.NewPeer, w.Observe(104, 2), "back");
            var e = new EpochWatch();
            Assert.IsTrue(e.Observe(0));
            Assert.IsFalse(e.Observe(0));
            Assert.IsTrue(e.Observe(1));
        }

        [TestMethod]
        public void Resync_ConsumerTailsToHeads_TouchesOnlyTheConsumersWords()
        {
            using (var buf = new NativeBuffer(Layout.HeaderSize * 2))
            {
                byte* b = buf.Ptr;
                for (int r = 0; r < 8; r++) { RingControl.PublishHead(b, (RingIndex)r, 100UL * (ulong)(r + 1)); RingControl.PublishTail(b, (RingIndex)r, 7); }
                Resync.ConsumerTailsToHeads(b, Party.Se);
                for (int r = 0; r < 8; r++)
                {
                    var ring = (RingIndex)r;
                    Assert.AreEqual(100UL * (ulong)(r + 1), RingControl.ReadHead(b, ring), "head untouched");
                    bool se = ring == RingIndex.C8SnDamage || ring == RingIndex.C12SnStatics || ring == RingIndex.C11aCtlToSe;
                    Assert.AreEqual(se ? 100UL * (ulong)(r + 1) : 7UL, RingControl.ReadTail(b, ring), ring.ToString());
                }
                CollectionAssert.AreEquivalent(new[] { RingIndex.C3SeModels, RingIndex.C4SeParts, RingIndex.C11cCtlToSn }, RingControl.ConsumedBy(Party.Sn));
                CollectionAssert.AreEquivalent(Resync.SnConsumes, RingControl.ConsumedBy(Party.Sn));
                CollectionAssert.AreEquivalent(Resync.SeConsumes, RingControl.ConsumedBy(Party.Se));
                CollectionAssert.AreEquivalent(Resync.HarnessConsumes, RingControl.ConsumedBy(Party.Harness));
                CollectionAssert.AreEquivalent(new[] { RingIndex.C3SeModels, RingIndex.C4SeParts, RingIndex.C11bCtlFromSe, RingIndex.C11cCtlToSn },
                    RingControl.ProducedBy(Party.Se), "C11c only while relaying (devControl bit 1)");
            }
        }

        [TestMethod]
        public void RegionChecksums_DetectASingleByte()
        {
            using (SharedSection s = SharedSection.Create(TestUtil.UniqueSectionName(), Layout.SectionSize, TestUtil.Sddl, out bool existed))
            {
                var before = Resync.Snapshot(s.Base, s.MappedSize);
                Assert.AreEqual(18, before.Count, "17 regions + whole section");
                s.Base[Layout.Region(RegionId.C9SnCreatures).Offset + 100] = 1;
                List<string> diff = Resync.Diff(before, Resync.Snapshot(s.Base, s.MappedSize));
                CollectionAssert.AreEquivalent(new[] { "C9 SnCreatures", "whole section" }, diff);
                s.Base[0x6000] = 1; // reserved space: only the whole-section checksum sees it
                diff = Resync.Diff(before, Resync.Snapshot(s.Base, s.MappedSize));
                CollectionAssert.Contains(diff, "whole section");
                Assert.AreNotEqual(Resync.RegionChecksum(s.Base, RegionId.C1SeFrame), Resync.RegionChecksum(s.Base, RegionId.C9SnCreatures));
            }
        }
    }
}
