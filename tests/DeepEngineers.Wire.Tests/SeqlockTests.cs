using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>A 256-byte self-checking seqlock record: seq, counter, 29 derived words, checksum.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256, Pack = 1)]
    internal unsafe struct TortureRecord
    {
        public const int Words = 29;
        [FieldOffset(0)] public uint seq;
        [FieldOffset(8)] public ulong counter;
        [FieldOffset(16)] public fixed ulong words[Words];
        [FieldOffset(248)] public ulong checksum;

        public static TortureRecord Make(ulong counter)
        {
            var r = new TortureRecord { counter = counter };
            ulong sum = counter * 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < Words; i++)
            {
                ulong w = (counter + 1) * 0xBF58476D1CE4E5B9UL ^ ((ulong)i * 0x94D049BB133111EBUL);
                r.words[i] = w;
                sum = (sum ^ w) * 0x100000001b3UL;
            }
            r.checksum = sum;
            return r;
        }

        public bool IsCoherent()
        {
            ulong sum = counter * 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < Words; i++)
            {
                ulong w = words[i];
                if (w != ((counter + 1) * 0xBF58476D1CE4E5B9UL ^ ((ulong)i * 0x94D049BB133111EBUL))) return false;
                sum = (sum ^ w) * 0x100000001b3UL;
            }
            return sum == checksum;
        }
    }

    /// <summary>S0.N2 seqlock torture and the S0.M2 mutant, plus the protocol details of CONTRACT §5.3.</summary>
    [TestClass]
    public unsafe class SeqlockTests
    {
        public TestContext TestContext { get; set; }

        private sealed class TortureStats
        {
            public long Writes, Accepted, Torn, Backwards, Busy, Unchanged, Never;
            public double Seconds;
            public override string ToString()
            {
                return "writes=" + Writes + " accepted=" + Accepted + " torn=" + Torn + " backwards=" + Backwards +
                       " busy=" + Busy + " unchanged=" + Unchanged + " never=" + Never + " in " + Seconds.ToString("F2") + " s";
            }
        }

        /// <summary>One writer thread, one reader thread on the same record. With stopOnTorn the run ends at the first torn copies.</summary>
        private static TortureStats RunTorture(byte* record, long writes, bool mutant, long stopAfterTorn)
        {
            var stats = new TortureStats();
            int done = 0;
            long writerCount = 0;
            var writer = new Thread(() =>
            {
                for (long n = 1; n <= writes; n++)
                {
                    TortureRecord r = TortureRecord.Make((ulong)n);
                    if (mutant) Seqlock.WriteCore(record, (byte*)&r, sizeof(TortureRecord), SeqlockMutant.SkipOdd);
                    else Seqlock.Write(record, r);
                    writerCount = n;
                    if (stopAfterTorn > 0 && (n & 1023) == 0 && Volatile.Read(ref stats.Torn) >= stopAfterTorn) break;
                }
                Volatile.Write(ref done, 1);
            });
            var reader = new Thread(() =>
            {
                var rd = new SeqlockReader<TortureRecord>(record);
                ulong last = 0;
                while (Volatile.Read(ref done) == 0)
                {
                    SeqlockReadStatus st = rd.Poll();
                    if (st == SeqlockReadStatus.Fresh)
                    {
                        stats.Accepted++;
                        TortureRecord v = rd.Value;
                        if (!v.IsCoherent()) Interlocked.Increment(ref stats.Torn);
                        else
                        {
                            if (v.counter < last) stats.Backwards++;
                            last = v.counter;
                        }
                    }
                    else if (st == SeqlockReadStatus.Busy) stats.Busy++;
                    else if (st == SeqlockReadStatus.Unchanged) stats.Unchanged++;
                    else stats.Never++;
                }
            });
            var sw = Stopwatch.StartNew();
            reader.Start();
            writer.Start();
            writer.Join();
            reader.Join();
            stats.Seconds = sw.Elapsed.TotalSeconds;
            stats.Writes = writerCount;
            return stats;
        }

        [TestMethod]
        public void S0N2_Torture_TenMillionWrites_NoTornCopyAccepted()
        {
            // Run on the C5 record of a real section (the mapping the plugins use).
            using (SharedSection s = SharedSection.Create(TestUtil.UniqueSectionName(), Layout.SectionSize, TestUtil.Sddl, out bool existed))
            {
                Assert.IsFalse(existed);
                byte* rec = Layout.RegionPtr(s.Base, RegionId.C5SeState);
                TortureStats st = RunTorture(rec, 10000000, false, 0);
                TestContext.WriteLine("S0.N2 " + st);
                Assert.AreEqual(10000000L, st.Writes, "writes");
                Assert.AreEqual(0L, st.Torn, "torn copies accepted");
                Assert.AreEqual(0L, st.Backwards, "copies going backwards");
                Assert.IsTrue(st.Accepted >= 1000, "the reader must have overlapped the writer (accepted " + st.Accepted + ")");
                Assert.AreEqual(Seqlock.NextPublished(0) + 2u * (10000000u - 1u), Seqlock.Current(rec), "seq = 2 × writes");
            }
        }

        [TestMethod]
        public void S0M2_WriterSkippingTheOddStep_IsCaught()
        {
            using (var buf = new NativeBuffer(256))
            {
                TortureStats st = RunTorture(buf.Ptr, 10000000, true, 100);
                TestContext.WriteLine("S0.M2 mutant " + st);
                Assert.IsTrue(st.Torn > 0, "the torture oracle must go red on a writer without the odd step (" + st + ")");
            }
        }

        [TestMethod]
        public void S0N2_WriterMakesSeqOddDuringTheWrite()
        {
            using (var buf = new NativeBuffer(256))
            {
                byte* rec = buf.Ptr;
                Assert.AreEqual(0u, Seqlock.Current(rec));
                uint odd = Seqlock.BeginWrite(rec);
                Assert.AreEqual(1u, odd);
                Assert.AreEqual(1u, Seqlock.Current(rec) & 1u, "odd while writing");
                uint s;
                Assert.IsFalse(Seqlock.ReadBegin(rec, out s), "a reader sees busy");
                Seqlock.EndWrite(rec, odd);
                Assert.AreEqual(2u, Seqlock.Current(rec));
                Seqlock.Write(rec, new SeState { tick = 5 });
                Assert.AreEqual(4u, Seqlock.Current(rec), "even → odd → even (+2 per write)");
            }
        }

        [TestMethod]
        public void S0N2_RestartedWriter_ContinuesFromTheValueFound()
        {
            using (var buf = new NativeBuffer(256))
            {
                byte* rec = buf.Ptr;
                *(uint*)rec = 1000; // left by a previous writer
                Assert.AreEqual(1000u, Seqlock.ContinueFrom(rec));
                Seqlock.RewriteInPlace(rec, new SnStatus { snPhase = SnPhase.Booting });
                Assert.AreEqual(1002u, Seqlock.Current(rec), "never reset to 0");
                *(uint*)rec = 1003; // a writer died mid-write (odd): the record stays busy until a complete write
                uint s;
                Assert.IsFalse(Seqlock.ReadBegin(rec, out s));
                Seqlock.Write(rec, new SnStatus());
                Assert.AreEqual(1004u, Seqlock.Current(rec));
                Assert.AreEqual(1004u, Seqlock.NextPublished(1003));
                Assert.AreEqual(1004u, Seqlock.NextPublished(1002));
            }
        }

        [TestMethod]
        public void S0N2_SequenceWrap_NeverPublishesZero()
        {
            using (var buf = new NativeBuffer(256))
            {
                byte* rec = buf.Ptr;
                *(uint*)rec = 0xFFFFFFFE;
                Seqlock.Write(rec, new SeTime { tick = 42 });
                Assert.AreEqual(2u, Seqlock.Current(rec), "0xFFFFFFFE → 0xFFFFFFFF (odd) → 2, never 0");
                Assert.AreEqual(2u, Seqlock.NextPublished(0xFFFFFFFE));
                var rd = new SeqlockReader<SeTime>(rec);
                Assert.AreEqual(SeqlockReadStatus.Fresh, rd.Poll());
                Assert.AreEqual(42UL, rd.Value.tick);
            }
        }

        [TestMethod]
        public void S0N2_Reader_NeverPublished_BusyKeepsLastGood_Unchanged()
        {
            using (var buf = new NativeBuffer(256))
            {
                byte* rec = buf.Ptr;
                var rd = new SeqlockReader<SeTime>(rec);
                Assert.AreEqual(SeqlockReadStatus.Never, rd.Poll(), "seq 0 = never published");
                Assert.IsFalse(rd.HasValue);

                Seqlock.Write(rec, new SeTime { tick = 7 });
                Assert.AreEqual(SeqlockReadStatus.Fresh, rd.Poll());
                Assert.AreEqual(7UL, rd.Value.tick);
                Assert.AreEqual(SeqlockReadStatus.Unchanged, rd.Poll(), "same seq");

                uint odd = Seqlock.BeginWrite(rec);            // a writer stuck mid-write
                ((SeTime*)rec)->tick = 999;                    // half-written payload
                Assert.AreEqual(SeqlockReadStatus.Busy, rd.Poll(), "≤ 16 tries, then busy");
                Assert.AreEqual(7UL, rd.Value.tick, "the last coherent copy is kept");
                Assert.AreEqual(1L, rd.BusyCount);
                Seqlock.EndWrite(rec, odd);
                Assert.AreEqual(SeqlockReadStatus.Fresh, rd.Poll());
                Assert.AreEqual(999UL, rd.Value.tick);

                rd.Reset();
                Assert.IsFalse(rd.HasValue);
                Assert.AreEqual(SeqlockReadStatus.Fresh, rd.Poll(), "after Reset the same seq is fresh again");
            }
        }

        [TestMethod]
        public void S0N2_TableReader_BeginEnd()
        {
            using (var buf = new NativeBuffer(0x4000))
            {
                byte* rec = buf.Ptr; // a C9-like table: header + rows written between BeginWrite/EndWrite
                uint odd = Seqlock.BeginWrite(rec);
                ((SnCreaturesHeader*)rec)->count = 3;
                for (int i = 0; i < 3; i++) ((SnCreature*)(rec + SnCreaturesLimits.RecordsOffset))[i].instanceId = (uint)(100 + i);
                Seqlock.EndWrite(rec, odd);

                uint s;
                Assert.IsTrue(Seqlock.ReadBegin(rec, out s));
                uint count = ((SnCreaturesHeader*)rec)->count;
                var ids = new uint[count];
                for (int i = 0; i < count; i++) ids[i] = ((SnCreature*)(rec + SnCreaturesLimits.RecordsOffset))[i].instanceId;
                Assert.IsTrue(Seqlock.ReadEnd(rec, s));
                CollectionAssert.AreEqual(new uint[] { 100, 101, 102 }, ids);

                Assert.IsTrue(Seqlock.ReadBegin(rec, out s));
                Seqlock.Write(rec, new SnCreaturesHeader { count = 0 }); // the writer moved on during the copy
                Assert.IsFalse(Seqlock.ReadEnd(rec, s), "changed during the copy → retry");
            }
        }
    }
}
