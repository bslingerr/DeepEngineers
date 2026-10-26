using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S17 conventions pin (CONTRACT §5.4 "S17 conventions"): the canonical text equals the fenced block byte for byte, its
 /// FNV-1a 64 is pinned, both blocks sit inside their pads after the earlier ones, layout 1.0 and the earlier pins are
 /// unchanged, de.s17 is dev-only; the codec and the aim rule against the Python twin's vectors (tools/s17/s17_ref.py
 /// --vectors), and SE's one-in-flight bookkeeping.
 /// </summary>
    [TestClass]
    public class S17ConventionsTests
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static List<string[]> Lines(string name)
        {
            string path = Path.Combine(S5Repo.Root, "tests", "s17", "vectors", name);
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python -I tools/s17/s17_ref.py --vectors)");
            var rows = new List<string[]>();
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                if (raw.Length > 0 && raw[0] != '#') rows.Add(raw.Split('\t'));
            Assert.IsTrue(rows.Count > 0, name + " has no rows");
            return rows;
        }

        private static double D(string s) { return double.Parse(s, NumberStyles.Float, Inv); }

        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2", Inv));
            return sb.ToString();
        }

        [TestMethod]
        public void CanonicalText_PinnedHashLinesAndBytes()
        {
            string t = S17Conventions.CanonicalText();
            Assert.AreEqual(S17Conventions.Header + "\n", t.Substring(0, S17Conventions.Header.Length + 1));
            int lines = 0;
            foreach (char c in t) { if (c == '\n') lines++; Assert.IsTrue(c < 128, "ASCII only"); }
            Assert.AreEqual(S17Conventions.CanonicalLines, lines, "lines");
            Assert.AreEqual(S17Conventions.CanonicalBytes, Encoding.ASCII.GetByteCount(t), "bytes");
            Assert.AreEqual(S17Conventions.PinnedHash, S17Conventions.Hash(), "hash 0x" + S17Conventions.Hash().ToString("x16"));
        }

        [TestMethod]
        public void CanonicalText_EqualsTheContractBlock()
        {
            string block = S8aVectors.ContractBlock(S17Conventions.Header);
            Assert.IsNotNull(block, "docs/CONTRACT.md has no S17 block");
            Assert.AreEqual(S17Conventions.CanonicalText(), block);
        }

        [TestMethod]
        public void Blocks_InsideThePads_AndEarlierPinsUnchanged()
        {
            Assert.AreEqual(256, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SeState)));
            Assert.AreEqual(256, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SnStatus)));
            Assert.IsTrue(S17Conventions.C5.Offset >= S15Conventions.C5.Offset + S15Conventions.C5.Size && S17Conventions.C5.Offset + S17Conventions.C5.Size <= 0x100,
                          "C5 block after S15's, inside pad_80");
            Assert.IsTrue(S17Conventions.C7.Offset >= S14Conventions.C7.Offset + S14Conventions.C7.Size && S17Conventions.C7.Offset + S17Conventions.C7.Size <= 0x100,
                          "C7 block after S14's, inside pad_8c");
            int o = S17Conventions.C5.Offset;
            foreach (string[] f in S17Conventions.C5Fields) { Assert.AreEqual(o, Convert.ToInt32(f[1], 16), f[0]); o += int.Parse(f[2], Inv); }
            Assert.AreEqual(S17Conventions.C5.Offset + S17Conventions.C5.Size, o, "the C5 fields fill the block");
            o = S17Conventions.C7.Offset;
            foreach (string[] f in S17Conventions.C7Fields) { Assert.AreEqual(o, Convert.ToInt32(f[1], 16), f[0]); o += int.Parse(f[2], Inv); }
            Assert.AreEqual(S17Conventions.C7.Offset + S17Conventions.C7.Size, o, "the C7 fields fill the block");
            Assert.AreEqual(0u, S17Conventions.C9Flags.Catchable & 3u, "Catchable leaves the layout's bits 0-1");
            Assert.IsTrue(SnCreaturesLimits.RecordsOffset + S17Conventions.C9.RowsMax * SnCreaturesLimits.RecordSize <= Layout.Region(RegionId.C9SnCreatures).Size);
            Assert.AreEqual(LayoutHash.PinnedLayout10, LayoutHash.Compiled, "layout 1.0 unchanged");
            Assert.AreEqual(S11Conventions.PinnedHash, S11Conventions.Hash(), "S11 pin unchanged");
            Assert.AreEqual(S14Conventions.PinnedHash, S14Conventions.Hash(), "S14 pin unchanged");
            Assert.AreEqual(S15Conventions.PinnedHash, S15Conventions.Hash(), "S15 pin unchanged");
            Assert.AreEqual(S16Conventions.PinnedHash, S16Conventions.Hash(), "S16 pin unchanged");
        }

        [TestMethod]
        public void Release_DeS17IsDevOnly()
        {
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s17", new Dictionary<string, object> { { "status", true } }), "release: de.s17 is dev-only");
            Assert.IsTrue(UserCommandRules.RequestAllowed(true, "de.s17", null));
            Assert.IsFalse(UserCommandRules.ChatAllowed(false, new[] { "s17", "status" }), "release chat: /de s17 refused");
        }

        [TestMethod]
        public void Kinds_FromTheS8aSpawnTable_FoodOnSEsScale()
        {
            string spawn = null;
            foreach (string f in S8aConventions.CanonicalText().Split('\n')) if (f.StartsWith("format spawn.table", StringComparison.Ordinal)) spawn = f;
            Assert.IsNotNull(spawn, "S8a spawn table");
            var seen = new HashSet<int>();
            foreach (S17Kind k in S17Conventions.Kinds)
            {
                Assert.IsTrue(spawn.Contains(" " + k.Name + " "), k.Name + " is spawned on the planet (S8a spawn.table)");
                Assert.IsTrue(seen.Add(k.TechType), "unique TechType " + k.TechType);
                Assert.AreEqual("DE_Fish_" + k.Name + "_Raw", k.RawSubtype);
            }
            Assert.IsFalse(seen.Contains(2535), "Mesmer is not edible");
            Assert.AreEqual(2670, S17Conventions.Food.RawMicro, "MealPack_KelpCrisp 0.00267");
            Assert.AreEqual(2 * S17Conventions.Food.RawMicro, S17Conventions.Food.CookedMicro);
            Assert.IsTrue(S17Conventions.Food.CookedMicro <= 33500, "under SE's largest ration");
        }

        [TestMethod]
        public void Wire_Vectors()
        {
            foreach (string[] f in Lines("wire.tsv"))
            {
                if (f[0] == "req")
                {
                    var r = new S17Request { Flags = byte.Parse(f[1], Inv), ReqSeq = uint.Parse(f[2], Inv), InstanceId = uint.Parse(f[3], Inv), TechType = int.Parse(f[4], Inv),
                                             X = (float)D(f[5]), Y = (float)D(f[6]), Z = (float)D(f[7]), WorldEpoch = uint.Parse(f[8], Inv) };
                    byte[] b = S17Wire.RequestBytes(ref r);
                    Assert.AreEqual(f[9], Hex(b), string.Join(" ", f));
                    var st = default(SeState);
                    S17Wire.WriteRequest(ref st, ref r);
                    S17Request back;
                    Assert.IsTrue(S17Wire.TryReadRequest(ref st, out back));
                    Assert.AreEqual(r.ReqSeq, back.ReqSeq);
                    Assert.AreEqual(r.InstanceId, back.InstanceId);
                    Assert.AreEqual(r.Z, back.Z);
                    Assert.AreEqual(r.Active, back.Active);
                    S17Wire.ClearRequest(ref st);
                    Assert.IsFalse(S17Wire.TryReadRequest(ref st, out back), "version 0 = absent");
                }
                else
                {
                    var a = new S17Ack { Result = byte.Parse(f[1], Inv), AckSeq = uint.Parse(f[2], Inv), InstanceId = uint.Parse(f[3], Inv), TechType = int.Parse(f[4], Inv) };
                    Assert.AreEqual(f[5], Hex(S17Wire.AckBytes(ref a)), string.Join(" ", f));
                    var st = default(SnStatus);
                    S17Ack back;
                    Assert.IsFalse(S17Wire.TryReadAck(ref st, out back), "version 0 = absent");
                    S17Wire.WriteAck(ref st, ref a);
                    Assert.IsTrue(S17Wire.TryReadAck(ref st, out back));
                    Assert.AreEqual(a.AckSeq, back.AckSeq);
                    Assert.AreEqual(a.Result, back.Result);
                }
            }
        }

        [TestMethod]
        public void Aim_Vectors()
        {
            foreach (string[] f in Lines("aim.tsv"))
            {
                var v = new double[f.Length];
                for (int i = 0; i < 12; i++) v[i] = D(f[i]);
                double reach;
                bool ok = S17Aim.Qualifies(new Vec3(v[0], v[1], v[2]), new Vec3(v[3], v[4], v[5]), new Vec3(v[6], v[7], v[8]), new Vec3(v[9], v[10], v[11]), out reach);
                Assert.AreEqual(f[12] == "1", ok, string.Join(" ", f));
                Assert.AreEqual(D(f[13]), reach, 1e-12, string.Join(" ", f));
            }
            Assert.AreEqual(0.2, S17Aim.LeadS(0.5), 1e-12);
            Assert.AreEqual(0.0, S17Aim.LeadS(-1), 1e-12);
            Assert.IsTrue(S17Aim.WithinSlack(1.5, 0, 0));
            Assert.IsFalse(S17Aim.WithinSlack(1.0, 1.2, 0));
        }

        [TestMethod]
        public void Book_OneInFlight_NoFishTwice_AckAndTimeout()
        {
            var b = new S17CatchBook(100);
            Assert.AreEqual(0, b.TryRequest(1000, 7, 2504, 1, 2, 3, 1));
            Assert.AreEqual(100u, b.Current.ReqSeq);
            Assert.IsTrue(b.Current.Active);
            Assert.AreEqual(S17Conventions.Refusal.Busy, b.TryRequest(1010, 8, 2504, 0, 0, 0, 1), "one in flight");
            var stale = new S17Ack { Version = 1, Result = 1, AckSeq = 99, InstanceId = 5 };
            Assert.AreEqual(S17CatchBook.AckOutcome.None, b.OnAck(ref stale), "another request's answer");
            var a = new S17Ack { Version = 1, Result = S17Conventions.Result.Caught, AckSeq = 100, InstanceId = 7 };
            Assert.AreEqual(S17CatchBook.AckOutcome.Matched, b.OnAck(ref a));
            Assert.IsFalse(b.Pending);
            Assert.IsFalse(b.Current.Active, "Active cleared on the ack");
            Assert.AreEqual(S17CatchBook.AckOutcome.None, b.OnAck(ref a), "the same answer is used once");
            Assert.AreEqual(S17Conventions.Refusal.Again, b.TryRequest(2000, 7, 2504, 0, 0, 0, 1), "the same fish not twice");
            //  the timeout counts from the request's first write into C5 (a game-thread stall after F does not eat it)
            Assert.AreEqual(0, b.TryRequest(3000, 9, 2515, 0, 0, 0, 1));
            Assert.IsFalse(b.TimedOut(4200), "not written into C5 yet: UnpublishedMs from F");
            b.MarkPublished(101, 4300);
            b.MarkPublished(101, 4400);
            Assert.AreEqual(4300UL, b.PublishedMs, "the first write starts the clock");
            Assert.IsFalse(b.TimedOut(5299));
            Assert.IsTrue(b.TimedOut(5300));
            Assert.IsFalse(b.TimedOut(5400), "once");
            Assert.IsFalse(b.Current.Active);
            var late = new S17Ack { Version = 1, Result = 1, AckSeq = 101, InstanceId = 9 };
            Assert.AreEqual(S17CatchBook.AckOutcome.Late, b.OnAck(ref late), "a later Caught answer is Late (the item is still given)");
            Assert.AreEqual(0, b.TryRequest(5500, 9, 2515, 0, 0, 0, 1), "review 8: a timed-out fish is forgotten");
            Assert.IsFalse(b.TimedOut(5500 + (ulong)S17Conventions.Catch.UnpublishedMs - 1));
            Assert.IsTrue(b.TimedOut(5500 + (ulong)S17Conventions.Catch.UnpublishedMs), "a request never written times out after UnpublishedMs");
            // a Moved answer forgets the fish; ForgetMs lets it be requested again
            Assert.AreEqual(0, b.TryRequest(5000, 10, 2504, 0, 0, 0, 1));
            var moved = new S17Ack { Version = 1, Result = S17Conventions.Result.Moved, AckSeq = b.Current.ReqSeq, InstanceId = 10 };
            Assert.AreEqual(S17CatchBook.AckOutcome.Matched, b.OnAck(ref moved));
            Assert.AreEqual(0, b.TryRequest(5100, 10, 2504, 0, 0, 0, 1), "a fish that moved may be tried again");
            b.MarkPublished(b.Current.ReqSeq, 5100);
            Assert.IsTrue(b.TimedOut(7000));
            Assert.AreEqual(0, b.TryRequest(1000 + (ulong)S17Conventions.Catch.ForgetMs, 7, 2504, 0, 0, 0, 1), "after ForgetMs");
            // the answer found in C7 at the start is not ours
            var fresh = new S17CatchBook(5);
            var old = new S17Ack { Version = 1, Result = 1, AckSeq = 5, InstanceId = 1 };
            fresh.Prime(ref old);
            Assert.AreEqual(0, fresh.TryRequest(10, 1, 2504, 0, 0, 0, 1));
            Assert.AreEqual(S17CatchBook.AckOutcome.None, fresh.OnAck(ref old), "the primed answer never matches");
        }

        [TestMethod]
        public unsafe void C9_WriterReader_RoundTrip_NoTornCopy()
        {
            var region = new byte[Layout.Region(RegionId.C9SnCreatures).Size];
            var scratch = new byte[S17C9.Bytes];
            var table = new byte[S17C9.Bytes];
            var rows = new SnCreature[S17Conventions.C9.RowsMax + 4];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new SnCreature { instanceId = (uint)(1000 + i), techType = 2504, posSn = new F32x3(i, -2f, 3f), velSn = new F32x3(0.5f, 0f, 0f), flags = S17Conventions.C9Flags.Catchable };
            fixed (byte* r = region)
            {
                uint seq;
                int count;
                Assert.IsFalse(S17C9.TryRead(r, scratch, table, out seq, out count), "never written");
                S17C9.Write(r, scratch, rows, rows.Length, 77);
                Assert.IsTrue(S17C9.TryRead(r, scratch, table, out seq, out count));
                Assert.AreEqual(S17Conventions.C9.RowsMax, count, "capped at RowsMax");
                Assert.AreEqual(0u, seq & 1u);
                SnCreature last = S17C9.Row(table, count - 1);
                Assert.AreEqual(1000u + (uint)count - 1u, last.instanceId);
                Assert.AreEqual(S17Conventions.C9Flags.Catchable, last.flags);
                Assert.AreEqual((float)(count - 1), last.posSn.x);
                Assert.AreEqual(77UL, ((SnCreaturesHeader*)r)->snFrame);
                //  a read while the writer holds the seqlock odd leaves the table as it was
                S17C9.Write(r, scratch, rows, 2, 78);
                *(uint*)r |= 1u;
                Assert.IsFalse(S17C9.TryRead(r, scratch, table, out seq, out count), "busy");
                Assert.AreEqual(1000u + (uint)S17Conventions.C9.RowsMax - 1u, S17C9.Row(table, S17Conventions.C9.RowsMax - 1).instanceId, "the cached table untouched");
                *(uint*)r += 1u;
                Assert.IsTrue(S17C9.TryRead(r, scratch, table, out seq, out count));
                Assert.AreEqual(2, count);
            }
        }
    }
}
