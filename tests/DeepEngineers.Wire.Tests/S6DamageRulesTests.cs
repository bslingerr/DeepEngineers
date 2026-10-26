using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// DamageRules and DedupWindow (CONTRACT §4.1 "Application rules"): exactly once per hit id, every reject reason in
 /// the contract's order, the factors and the hit cell with its 3×3×3 fallback; the S6.N1 arithmetic and the S6.M3
 /// bypass.
 /// </summary>
    [TestClass]
    public class DamageRulesTests
    {
        private static DamageRecord Rec(uint seq, uint gen = 3, uint epoch = 9, float amount = 30f, uint kind = DamageTargetKind.GridSection,
            ushort type = 0, ushort flags = 0, uint unit = DamageUnit.SnHp, ulong target = 100, uint section = 7)
        {
            var r = new DamageRecord
            {
                dedupSeq = seq, snGeneration = gen, worldEpoch = epoch, amount = amount, targetKind = kind, snDamageType = type,
                flags = flags, unit = unit, targetEntityId = target, rootEntityId = 100, sectionId = section,
            };
            r.localHit = new F32x3(1, 2, 3);
            return r;
        }

        // ------------------------------------------------------------------------------------------------ dedup

        [TestMethod]
        public void S6Dedup_ExactlyOnce()
        {
            var d = new DedupWindow();
            Assert.AreEqual(8192, d.Window);
            Assert.AreEqual(DamageReject.None, d.Admit(1));
            Assert.AreEqual(DamageReject.None, d.Admit(2));
            Assert.AreEqual(DamageReject.Duplicate, d.Admit(2));
            Assert.AreEqual(DamageReject.None, d.Admit(5));
            Assert.AreEqual(1, d.Gaps);
            Assert.AreEqual(DamageReject.None, d.Admit(4), "reordered, still new");
            Assert.AreEqual(1, d.Reordered);
            Assert.AreEqual(DamageReject.Duplicate, d.Admit(4));
            Assert.AreEqual(DamageReject.Sequence, d.Admit(0));
            Assert.AreEqual(5u, d.HighWater);
            Assert.AreEqual(4, d.Admitted);
            Assert.AreEqual(2, d.Duplicates);
            Assert.IsTrue(d.Contains(5) && d.Contains(4) && !d.Contains(3) && !d.Contains(6));
        }

        [TestMethod]
        public void S6Dedup_WindowSlidesAndTooOld()
        {
            var d = new DedupWindow();
            for (uint s = 1; s <= 9000; s++) Assert.AreEqual(DamageReject.None, d.Admit(s));
            Assert.AreEqual(DamageReject.TooOld, d.Admit(10));
            Assert.AreEqual(DamageReject.TooOld, d.Admit(9000 - 8192));
            Assert.AreEqual(DamageReject.Duplicate, d.Admit(9000 - 8191), "the oldest id in the window is remembered");
            Assert.AreEqual(DamageReject.Duplicate, d.Admit(9000));
            Assert.AreEqual(2, d.TooOld);
            // a jump beyond the window forgets everything older
            Assert.AreEqual(DamageReject.None, d.Admit(30000));
            Assert.AreEqual(DamageReject.TooOld, d.Admit(9000));
            Assert.AreEqual(DamageReject.None, d.Admit(29999), "inside the window and never seen");
            // near uint.MaxValue
            Assert.AreEqual(DamageReject.None, d.Admit(uint.MaxValue - 1));
            Assert.AreEqual(DamageReject.None, d.Admit(uint.MaxValue));
            Assert.AreEqual(DamageReject.Duplicate, d.Admit(uint.MaxValue));
            Assert.AreEqual(DamageReject.Duplicate, d.Admit(uint.MaxValue - 1));
        }

        [TestMethod]
        public void S6Dedup_RandomStreamAgainstASet()
        {
            var rnd = new Random(21);
            foreach (int window in new[] { 64, 256, 8192 })
            {
                var d = new DedupWindow(window);
                var seen = new HashSet<uint>();
                uint hw = 0;
                for (int i = 0; i < 20000; i++)
                {
                    uint seq = (uint)Math.Max(1, (long)hw + rnd.Next(-window - 50, 5));
                    DamageReject want;
                    if (seq > hw) { want = DamageReject.None; hw = seq; seen.Add(seq); }
                    else if ((long)seq <= (long)hw - window) want = DamageReject.TooOld;
                    else if (seen.Contains(seq)) want = DamageReject.Duplicate;
                    else { want = DamageReject.None; seen.Add(seq); }
                    Assert.AreEqual(want, d.Admit(seq), "window " + window + " step " + i + " seq " + seq);
                }
            }
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DedupWindow(100));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DedupWindow(0));
        }

        [TestMethod]
        public void S6Dedup_ResetAndBypass()
        {
            var d = new DedupWindow(64);
            d.Reset(7);
            Assert.AreEqual(7u, d.Generation);
            d.Admit(1); d.Admit(2);
            d.Reset(8);
            Assert.AreEqual(0u, d.HighWater);
            Assert.AreEqual(DamageReject.None, d.Admit(1), "a new generation starts over");
            Assert.AreEqual(3, d.Admitted, "counters are cumulative");
            // S6.M3: every id admitted, duplicates included (seq 0 stays malformed)
            var m3 = new DedupWindow { Bypass = true };
            for (int i = 0; i < 3; i++) Assert.AreEqual(DamageReject.None, m3.Admit(5));
            Assert.AreEqual(DamageReject.Sequence, m3.Admit(0));
            Assert.AreEqual(0, m3.Duplicates);
        }

        // ------------------------------------------------------------------------------------------------ checks

        [TestMethod]
        public void S6Rules_EveryRejectReason()
        {
            Assert.AreEqual(DamageReject.None, DamageRules.CheckHeader(Rec(1), 3, 9));
            Assert.AreEqual(DamageReject.Generation, DamageRules.CheckHeader(Rec(1, gen: 2), 3, 9));
            Assert.AreEqual(DamageReject.Epoch, DamageRules.CheckHeader(Rec(1, epoch: 8), 3, 9));
            Assert.AreEqual(DamageReject.Generation, DamageRules.CheckHeader(Rec(1, gen: 2, epoch: 8), 3, 9), "generation first");

            Assert.AreEqual(DamageReject.None, DamageRules.CheckPayload(Rec(1)));
            Assert.AreEqual(DamageReject.Unit, DamageRules.CheckPayload(Rec(1, unit: 2)));
            Assert.AreEqual(DamageReject.Unit, DamageRules.CheckPayload(Rec(1, unit: 0, amount: 0)), "unit before amount");
            foreach (float a in new[] { 0f, -0f, -1f, 500.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                Assert.AreEqual(DamageReject.Amount, DamageRules.CheckPayload(Rec(1, amount: a)), "amount " + a);
            Assert.AreEqual(DamageReject.None, DamageRules.CheckPayload(Rec(1, amount: 500f)), "500 is the limit");
            Assert.AreEqual(DamageReject.None, DamageRules.CheckPayload(Rec(1, amount: 1e-6f)));
            Assert.AreEqual(DamageReject.Kind, DamageRules.CheckPayload(Rec(1, kind: 0)));
            Assert.AreEqual(DamageReject.Kind, DamageRules.CheckPayload(Rec(1, kind: 3)));
            foreach (ushort t in new ushort[] { 0, 7, 8 }) Assert.AreEqual(DamageReject.None, DamageRules.CheckPayload(Rec(1, type: t)));
            foreach (ushort t in new ushort[] { 1, 2, 3, 6, 9, 12, 65535 }) Assert.AreEqual(DamageReject.Type, DamageRules.CheckPayload(Rec(1, type: t)));
            DamageRecord nan = Rec(1);
            nan.localHit = new F32x3(float.NaN, 0, 0);
            Assert.AreEqual(DamageReject.HitPoint, DamageRules.CheckPayload(nan));
            nan.localHit = new F32x3(0, 0, float.PositiveInfinity);
            Assert.AreEqual(DamageReject.HitPoint, DamageRules.CheckPayload(nan));

            Assert.AreEqual(DamageReject.None, DamageRules.CheckFrame(Rec(1), false));
            Assert.AreEqual(DamageReject.Frame, DamageRules.CheckFrame(Rec(1), true), "subgrid target without the flag");
            Assert.AreEqual(DamageReject.Frame, DamageRules.CheckFrame(Rec(1, flags: DamageFlags.FrameSubgrid), false), "root grid with the flag");
            Assert.AreEqual(DamageReject.None, DamageRules.CheckFrame(Rec(1, flags: DamageFlags.FrameSubgrid), true));
            Assert.AreEqual(DamageReject.Frame, DamageRules.CheckFrame(Rec(1, kind: 2, flags: DamageFlags.FrameSubgrid), false), "never for a character");
            Assert.AreEqual(DamageReject.None, DamageRules.CheckFrame(Rec(1, kind: 2, flags: CombatConventions.C8FlagsExt.GhostPlayer), true));
        }

        [TestMethod]
        public void S6Rules_ValidateOrderAndMarking()
        {
            var d = new DedupWindow();
            DamageTargetCheck target = (in DamageRecord r, out bool sub) =>
            {
                sub = r.targetEntityId == 101;
                if (r.targetKind == DamageTargetKind.Character) return r.targetEntityId == 200 ? (r.sectionId == 0 ? DamageReject.None : DamageReject.Section) : DamageReject.Entity;
                if (r.targetEntityId != 100 && r.targetEntityId != 101) return DamageReject.Entity;
                return r.sectionId == 7 ? DamageReject.None : DamageReject.Section;
            };
            Assert.AreEqual(DamageReject.None, DamageRules.Validate(Rec(1), 3, 9, d, target));
            Assert.AreEqual(3u, d.Generation, "the window follows the generation");
            Assert.AreEqual(DamageReject.Duplicate, DamageRules.Validate(Rec(1), 3, 9, d, target));
            Assert.AreEqual(DamageReject.Generation, DamageRules.Validate(Rec(1, gen: 4), 3, 9, d, target), "header before dedup");
            Assert.AreEqual(DamageReject.Amount, DamageRules.Validate(Rec(2, amount: 0), 3, 9, d, target));
            Assert.AreEqual(DamageReject.Duplicate, DamageRules.Validate(Rec(2), 3, 9, d, target), "the id was marked before the payload check");
            Assert.AreEqual(DamageReject.Entity, DamageRules.Validate(Rec(3, target: 555), 3, 9, d, target));
            Assert.AreEqual(DamageReject.Section, DamageRules.Validate(Rec(4, section: 8), 3, 9, d, target));
            Assert.AreEqual(DamageReject.Frame, DamageRules.Validate(Rec(5, target: 101), 3, 9, d, target), "subgrid without the flag");
            Assert.AreEqual(DamageReject.None, DamageRules.Validate(Rec(6, target: 101, flags: DamageFlags.FrameSubgrid), 3, 9, d, target));
            Assert.AreEqual(DamageReject.None, DamageRules.Validate(Rec(7, kind: 2, target: 200, section: 0), 3, 9, d, target));
            Assert.AreEqual(DamageReject.Section, DamageRules.Validate(Rec(8, kind: 2, target: 200, section: 7), 3, 9, d, target));
            DamageRecord bad = Rec(9, target: 555);
            bad.localHit = new F32x3(float.NaN, 0, 0);
            Assert.AreEqual(DamageReject.HitPoint, DamageRules.Validate(bad, 3, 9, d, target), "a non-finite hit point is a payload defect (before Entity)");
            Assert.AreEqual(DamageReject.Sequence, DamageRules.Validate(Rec(0), 3, 9, null, null));
            Assert.AreEqual(DamageReject.None, DamageRules.Validate(Rec(1), 3, 9, null, null), "no window, no target: root grid assumed");
        }

        [TestMethod]
        public void S6Rules_N1Arithmetic()
        {
            // S6.N1: 30 unique records per target + duplicates; only unique ids are applied, with k_small 1, k_large 25, k_char 1
            var rnd = new Random(22);
            var d = new DedupWindow();
            double small = 0, large = 0, ch = 0;
            uint seq = 0;
            var sent = new List<DamageRecord>();
            for (int t = 0; t < 3; t++)
                for (int i = 0; i < 30; i++)
                {
                    float a = (float)(1 + rnd.NextDouble() * (t == 2 ? 1 : 2));
                    sent.Add(Rec(++seq, amount: a, kind: t == 2 ? DamageTargetKind.Character : DamageTargetKind.GridSection, section: t == 2 ? 0u : 7u));
                }
            for (int i = 0; i < 10; i++) sent.Add(sent[rnd.Next(sent.Count)]);
            double expectSmall = 0, expectLarge = 0, expectChar = 0;
            for (int i = 0; i < 90; i++)
            {
                double k = i < 30 ? 1.0 : i < 60 ? 25.0 : 1.0;
                if (i < 30) expectSmall += sent[i].amount * k; else if (i < 60) expectLarge += sent[i].amount * k; else expectChar += sent[i].amount * k;
            }
            int applied = 0;
            foreach (DamageRecord r in sent)
            {
                if (DamageRules.Validate(r, 3, 9, d, null) != DamageReject.None) continue;
                applied++;
                bool isChar = r.targetKind == DamageTargetKind.Character;
                bool isLarge = !isChar && r.dedupSeq > 30;
                double k = DamageRules.Factor(r.targetKind, isLarge);
                if (isChar) ch += r.amount * k; else if (isLarge) large += r.amount * k; else small += r.amount * k;
            }
            Assert.AreEqual(90, applied);
            Assert.AreEqual(10, d.Duplicates);
            Assert.AreEqual(expectSmall, small, 1e-3);
            Assert.AreEqual(expectLarge, large, 1e-3);
            Assert.AreEqual(expectChar, ch, 1e-3);
        }

        // ------------------------------------------------------------------------------------------------ factors and cells

        [TestMethod]
        public void S6Rules_Factors()
        {
            Assert.AreEqual(1.0, DamageRules.Factor(DamageTargetKind.Character, true));
            Assert.AreEqual(1.0, DamageRules.Factor(DamageTargetKind.Character, false));
            Assert.AreEqual(25.0, DamageRules.Factor(DamageTargetKind.GridSection, true));
            Assert.AreEqual(1.0, DamageRules.Factor(DamageTargetKind.GridSection, false));
            Assert.IsTrue(double.IsNaN(DamageRules.Factor(3, true)));
            Assert.AreEqual(25.0, DamageRules.FactorForClass(DamageTargetKind.GridSection, GridClass.LargeGrid));
            Assert.AreEqual(1.0, DamageRules.FactorForClass(DamageTargetKind.GridSection, GridClass.SmallGrid));
            Assert.AreEqual(1.0, DamageRules.FactorForClass(DamageTargetKind.Character, 0));
            Assert.IsTrue(double.IsNaN(DamageRules.FactorForClass(DamageTargetKind.GridSection, 3)));
            // CONTRACT 4.1 worked numbers: a reaper bite (80) on a large light armor cube = 2,000 (80 %)
            Assert.AreEqual(2000.0, 80 * DamageRules.Factor(DamageTargetKind.GridSection, true));
            Assert.IsTrue(DamageRules.ScaleMatches(25f, 25.0));
            Assert.IsTrue(DamageRules.ScaleMatches(1f, 1.0));
            Assert.IsTrue(DamageRules.ScaleMatches(24.99999f, 25.0));
            Assert.IsFalse(DamageRules.ScaleMatches(24.9999f, 25.0));
            Assert.IsFalse(DamageRules.ScaleMatches(1f, 25.0), "S6.M7: k_small and k_large swapped");
            Assert.IsFalse(DamageRules.ScaleMatches(float.NaN, 1.0));
            Assert.IsFalse(DamageRules.ScaleMatches(1f, double.NaN));
        }

        [TestMethod]
        public void S6Rules_HitCellAndNearestOccupied()
        {
            Assert.AreEqual(new I32x3(1, -1, 1), DamageRules.HitCell(new Vec3(1.25, -1.25, 3.74), 2.5));
            Assert.AreEqual(new I32x3(0, 0, 2), DamageRules.HitCell(new Vec3(0.24, -0.24, 0.75), 0.5));
            Assert.ThrowsException<ArgumentException>(() => DamageRules.HitCell(new Vec3(double.NaN, 0, 0), 2.5));
            I32x3 cell;
            bool searched;
            var occ = new HashSet<Tuple<int, int, int>> { Tuple.Create(0, 0, 0), Tuple.Create(1, 0, 0) };
            Func<I32x3, bool> f = c => occ.Contains(Tuple.Create(c.x, c.y, c.z));
            Assert.IsTrue(DamageRules.NearestOccupied(new Vec3(1.3, 0, 0), 2.5, f, out cell, out searched));
            Assert.AreEqual(new I32x3(1, 0, 0), cell);
            Assert.IsFalse(searched);
            occ = new HashSet<Tuple<int, int, int>> { Tuple.Create(0, 0, 0), Tuple.Create(2, 0, 0) };
            Assert.IsTrue(DamageRules.NearestOccupied(new Vec3(1.3, 0, 0), 2.5, f, out cell, out searched));
            Assert.AreEqual(new I32x3(0, 0, 0), cell, "nearest centre to l");
            Assert.IsTrue(searched);
            // ties: smallest x, then y, then z
            occ = new HashSet<Tuple<int, int, int>> { Tuple.Create(1, 0, 0), Tuple.Create(-1, 0, 0), Tuple.Create(0, 1, 0), Tuple.Create(0, -1, 0) };
            Assert.IsTrue(DamageRules.NearestOccupied(Vec3.Zero, 2.5, f, out cell, out searched));
            Assert.AreEqual(new I32x3(-1, 0, 0), cell);
            occ = new HashSet<Tuple<int, int, int>> { Tuple.Create(0, 1, 0), Tuple.Create(0, 0, -1), Tuple.Create(0, 0, 1) };
            Assert.IsTrue(DamageRules.NearestOccupied(Vec3.Zero, 2.5, f, out cell, out searched));
            Assert.AreEqual(new I32x3(0, 0, -1), cell);
            occ = new HashSet<Tuple<int, int, int>> { Tuple.Create(2, 0, 0) };
            Assert.IsFalse(DamageRules.NearestOccupied(Vec3.Zero, 2.5, f, out cell, out searched), "beyond the 3×3×3 neighbourhood: NoBlock");
            Assert.IsTrue(searched);
            Assert.AreEqual("3:17", DamageRules.HitId(3, 17));
            Assert.AreEqual("4294967295:4294967295", DamageRules.HitId(uint.MaxValue, uint.MaxValue));
        }
    }
}
