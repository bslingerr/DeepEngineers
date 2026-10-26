using System;
using System.Globalization;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S6 damage rules (CONTRACT §4.1 "Application rules", §5.4 C8 field meanings), pure and shared: the checks SE applies
 // to every C8 record, the dedup window (exactly once per hit id (snGeneration, dedupSeq)), the factors k, the hit cell
 // and its 3×3×3 fallback. Check order: Generation, Epoch; Sequence/Duplicate/TooOld (dedup); Unit, Amount, Kind, Type,
 // HitPoint; Entity, Section (the caller's target lookup); Frame; then the SE application gates (NoBlock,
 // NotDestructible, CannotDie, NotApplied). Python twin: tools/worldmap/wire_ref.py (S6Dedup, s6_check_*).
 // =================================================================================================================

 /// <summary>
 /// SE's dedup window for one Subnautica generation (CONTRACT §4.1): the highest seq seen <see cref="HighWater"/> and
 /// a bitmap of the last <c>window</c> ids. seq &gt; hw → new (the window slides; a jump over hw + 1 counts
 /// <see cref="Gaps"/> once); hw − window &lt; seq ≤ hw → new if its bit is clear (counted <see cref="Reordered"/>), else
 /// <see cref="DamageReject.Duplicate"/>; seq ≤ hw − window → <see cref="DamageReject.TooOld"/>; seq 0 →
 /// <see cref="DamageReject.Sequence"/>. Counters are cumulative over <see cref="Reset"/>. Not thread-safe.
 /// </summary>
    public sealed class DedupWindow
    {
        private readonly ulong[] _bits;
        private readonly int _window;

        /// <summary>Counters (cumulative).</summary>
        public long Admitted, Duplicates, TooOld, Reordered, Gaps;

        /// <summary>S6.M3 (SE mutant "dedup off"): every seq ≥ 1 is admitted and nothing is remembered.</summary>
        public bool Bypass;

        /// <param name="window">Ids remembered, a positive multiple of 64 (default 8,192).</param>
        public DedupWindow(int window = CombatConventions.Limits.DedupWindow)
        {
            if (window <= 0 || window % 64 != 0) throw new ArgumentOutOfRangeException("window", "a positive multiple of 64");
            _window = window;
            _bits = new ulong[window / 64];
        }

        /// <summary>The window size (ids).</summary>
        public int Window { get { return _window; } }

        /// <summary>The generation the window belongs to (<see cref="Reset"/>).</summary>
        public uint Generation { get; private set; }

        /// <summary>The highest seq admitted in this generation (0 = none yet).</summary>
        public uint HighWater { get; private set; }

        /// <summary>Starts a new generation: forgets every id (counters are kept).</summary>
        public void Reset(uint generation)
        {
            Array.Clear(_bits, 0, _bits.Length);
            HighWater = 0;
            Generation = generation;
        }

        private bool Get(uint seq) { int i = (int)(seq % (uint)_window); return (_bits[i >> 6] & (1UL << (i & 63))) != 0; }
        private void Set(uint seq) { int i = (int)(seq % (uint)_window); _bits[i >> 6] |= 1UL << (i & 63); }
        private void Clear(uint seq) { int i = (int)(seq % (uint)_window); _bits[i >> 6] &= ~(1UL << (i & 63)); }

        /// <summary>True when <paramref name="seq"/> was admitted and is still inside the window (no state change).</summary>
        public bool Contains(uint seq)
        {
            if (seq == 0 || seq > HighWater) return false;
            if ((long)seq <= (long)HighWater - _window) return false;
            return Get(seq);
        }

        /// <summary>Admits a seq: <see cref="DamageReject.None"/> when it is new (marked), else Sequence, Duplicate or TooOld.</summary>
        public DamageReject Admit(uint seq)
        {
            if (seq == 0) return DamageReject.Sequence;
            if (Bypass) { Admitted++; return DamageReject.None; }
            uint hw = HighWater;
            if (seq > hw)
            {
                if ((long)seq > (long)hw + 1) Gaps++;
                long jump = (long)seq - hw;
                if (jump >= _window) Array.Clear(_bits, 0, _bits.Length);
                else for (long k = (long)hw + 1; k <= seq; k++) Clear((uint)k);
                Set(seq);
                HighWater = seq;
                Admitted++;
                return DamageReject.None;
            }
            if ((long)seq <= (long)hw - _window) { TooOld++; return DamageReject.TooOld; }
            if (Get(seq)) { Duplicates++; return DamageReject.Duplicate; }
            Set(seq);
            Reordered++;
            Admitted++;
            return DamageReject.None;
        }
    }

    /// <summary>
 /// The target lookup a consumer supplies to <see cref="DamageRules.Validate"/>: <see cref="DamageReject.Entity"/> or
 /// <see cref="DamageReject.Section"/> (or None), and whether the target entity is a subgrid (for the Frame check).
 /// </summary>
    public delegate DamageReject DamageTargetCheck(in DamageRecord r, out bool targetIsSubgrid);

    /// <summary>The S6 damage checks, factors and hit cell (see the file header).</summary>
    public static class DamageRules
    {
        /// <summary>The largest accepted amount, SN_HP.</summary>
        public const double MaxAmount = CombatConventions.Limits.MaxAmountSnHp;

        /// <summary>Step 1: <c>Generation</c> (≠ the current Subnautica generation), then <c>Epoch</c> (≠ SE's world epoch).</summary>
        public static DamageReject CheckHeader(in DamageRecord r, uint snGeneration, uint worldEpoch)
        {
            if (r.snGeneration != snGeneration) return DamageReject.Generation;
            if (r.worldEpoch != worldEpoch) return DamageReject.Epoch;
            return DamageReject.None;
        }

        /// <summary>
 /// The payload checks, in order: <c>Unit</c> (≠ 1), <c>Amount</c> (not finite, ≤ 0 or &gt; 500), <c>Kind</c> (not 1
 /// or 2), <c>Type</c> (not Normal 0, Explosive 7 or Electrical 8), <c>HitPoint</c> (a non-finite localHit).
 /// </summary>
 /// <summary>Review F8: a finite but huge localHit (beyond the ±8.2 km lattice) is a HitPoint reject, not an overflow in the cell math.</summary>
        public const double MaxHitAbsM = 1e5;

        public static DamageReject CheckPayload(in DamageRecord r)
        {
            if (r.unit != DamageUnit.SnHp) return DamageReject.Unit;
            float a = r.amount;
            if (float.IsNaN(a) || float.IsInfinity(a) || !(a > 0f) || a > MaxAmount) return DamageReject.Amount;
            if (r.targetKind != DamageTargetKind.GridSection && r.targetKind != DamageTargetKind.Character) return DamageReject.Kind;
            if (!CombatConventions.IsForwarded(r.snDamageType)) return DamageReject.Type;
            Vec3 lh = LocalHit(r);
            if (!lh.IsFinite || Math.Abs(lh.X) > MaxHitAbsM || Math.Abs(lh.Y) > MaxHitAbsM || Math.Abs(lh.Z) > MaxHitAbsM) return DamageReject.HitPoint;   // review F8
            return DamageReject.None;
        }

        /// <summary>
 /// <c>Frame</c>: the flag "frame = subgrid" must be set exactly when the target is a subgrid, and never for a
 /// character record (kind 2).
 /// </summary>
        public static DamageReject CheckFrame(in DamageRecord r, bool targetIsSubgrid)
        {
            bool flag = (r.flags & DamageFlags.FrameSubgrid) != 0;
            if (r.targetKind == DamageTargetKind.Character) return flag ? DamageReject.Frame : DamageReject.None;
            return flag == targetIsSubgrid ? DamageReject.None : DamageReject.Frame;
        }

        /// <summary>
 /// Every check SE makes before applying, in the contract's order: <see cref="CheckHeader"/>; the dedup window
 /// (reset to <paramref name="snGeneration"/> first when it holds another generation; the id is marked here, so a
 /// replay of a record rejected later is a Duplicate); <see cref="CheckPayload"/>; the caller's
 /// <paramref name="target"/> lookup (Entity, Section; null skips it and treats the target as a root grid);
 /// <see cref="CheckFrame"/>. Returns the first failure, or None.
 /// </summary>
        public static DamageReject Validate(in DamageRecord r, uint snGeneration, uint worldEpoch, DedupWindow dedup, DamageTargetCheck target)
        {
            DamageReject x = CheckHeader(r, snGeneration, worldEpoch);
            if (x != DamageReject.None) return x;
            if (dedup != null)
            {
                if (dedup.Generation != snGeneration) dedup.Reset(snGeneration);
                x = dedup.Admit(r.dedupSeq);
                if (x != DamageReject.None) return x;
            }
            else if (r.dedupSeq == 0) return DamageReject.Sequence;
            x = CheckPayload(r);
            if (x != DamageReject.None) return x;
            bool sub = false;
            if (target != null)
            {
                x = target(r, out sub);
                if (x != DamageReject.None) return x;
            }
            return CheckFrame(r, sub);
        }

        /// <summary>The factor k (SE units per SN_HP): character 1.0; grid large 25, small 1.0; NaN for another kind.</summary>
        public static double Factor(uint targetKind, bool largeGrid)
        {
            if (targetKind == DamageTargetKind.Character) return CombatConventions.DamageScale.KChar;
            if (targetKind == DamageTargetKind.GridSection) return largeGrid ? CombatConventions.DamageScale.KLarge : CombatConventions.DamageScale.KSmall;
            return double.NaN;
        }

        /// <summary>The factor by the GridAdd <c>targetClass</c> (1 large, 2 small); character 1.0; NaN for an unknown kind or class.</summary>
        public static double FactorForClass(uint targetKind, uint targetClass)
        {
            if (targetKind == DamageTargetKind.Character) return CombatConventions.DamageScale.KChar;
            if (targetKind != DamageTargetKind.GridSection) return double.NaN;
            if (targetClass == GridClass.LargeGrid) return CombatConventions.DamageScale.KLarge;
            if (targetClass == GridClass.SmallGrid) return CombatConventions.DamageScale.KSmall;
            return double.NaN;
        }

        /// <summary>The record's <c>seScaleExpected</c> agrees with SE's factor: |Δ| ≤ 1e-6·max(1, k) (else counted and logged; SE uses its own).</summary>
        public static bool ScaleMatches(float expected, double factor)
        {
            if (double.IsNaN(factor) || float.IsNaN(expected)) return false;
            return Math.Abs(expected - factor) <= 1e-6 * Math.Max(1.0, Math.Abs(factor));
        }

        /// <summary>The record's <c>localHit</c> as doubles.</summary>
        public static Vec3 LocalHit(in DamageRecord r)
        {
            F32x3 l = r.localHit;
            return new Vec3(l.x, l.y, l.z);
        }

        /// <summary>The hit cell <c>RoundAway(l / GridSize)</c> per axis (as VRage <c>Vector3I.Round</c> in <c>WorldToGridInteger</c>).</summary>
        public static I32x3 HitCell(Vec3 localHit, double gridSize)
        {
            if (!(gridSize > 0)) throw new ArgumentOutOfRangeException("gridSize");
            if (!localHit.IsFinite) throw new ArgumentException("localHit not finite");
            return new I32x3(CollisionSkinMath.RoundAway(localHit.X / gridSize), CollisionSkinMath.RoundAway(localHit.Y / gridSize),
                             CollisionSkinMath.RoundAway(localHit.Z / gridSize));
        }

        /// <summary>
 /// The block cell for a hit: its own cell (<see cref="HitCell"/>) when occupied (<paramref name="searched"/>
 /// false); else the occupied cell of the 26 neighbours whose centre (cell·GridSize) is nearest to l, ties by
 /// smallest x, then y, then z (<paramref name="searched"/> true). False when none is occupied (NoBlock).
 /// </summary>
        public static bool NearestOccupied(Vec3 localHit, double gridSize, Func<I32x3, bool> occupied, out I32x3 cell, out bool searched)
        {
            if (occupied == null) throw new ArgumentNullException("occupied");
            I32x3 c = HitCell(localHit, gridSize);
            searched = false;
            if (occupied(c)) { cell = c; return true; }
            searched = true;
            bool found = false;
            I32x3 best = c;
            double bestD = double.PositiveInfinity;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        var n = new I32x3(c.x + dx, c.y + dy, c.z + dz);
                        double ex = n.x * gridSize - localHit.X, ey = n.y * gridSize - localHit.Y, ez = n.z * gridSize - localHit.Z;
                        double d = ex * ex + ey * ey + ez * ez;
                        bool better = d < bestD || (d == bestD && Less(n, best));
                        if (!better || !occupied(n)) continue;
                        best = n;
                        bestD = d;
                        found = true;
                    }
            cell = found ? best : c;
            return found;
        }

        private static bool Less(I32x3 a, I32x3 b)
        {
            if (a.x != b.x) return a.x < b.x;
            if (a.y != b.y) return a.y < b.y;
            return a.z < b.z;
        }

        /// <summary>The hit id text <c>gen:seq</c>.</summary>
        public static string HitId(uint generation, uint seq)
        {
            return generation.ToString(CultureInfo.InvariantCulture) + ":" + seq.ToString(CultureInfo.InvariantCulture);
        }
    }
}
