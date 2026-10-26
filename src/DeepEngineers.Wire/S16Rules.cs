using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S16 rules (pure; the SE and Subnautica plugins and the Python twin tools/s16/s16_ref.py implement the same numbers):
 // the block class, the exposure of a block to water (the S7 air rule: an empty cell inside an airtight room is dry,
 // any other empty cell wet), the hull ladder and the grid's crush depth, the damage curve below the limit, the creak
 // level and cadence, the voice rule (the S15 helper: once per event, cooldown), the HUD state and the diver's breathing
 // multiplier, and the C5 extension codec. CONTRACT §5.4 "S16 conventions" pins every number (S16Conventions).
 // =================================================================================================================

 /// <summary>The three rule functions of the brief by their names (the twin's crush_depth_of, damage_per_second, creak_level).</summary>
    public static class S16Rule
    {
        /// <summary><c>format crush.depth</c> of a grid's scan summary.</summary>
        public static int CrushDepthOf(S16GridSummary s) { return S16Ladder.CrushDepthOf(s); }
        /// <summary><c>format damage.block</c>: hull points per second of a block at <paramref name="depth"/> on a grid limited at <paramref name="limit"/>.</summary>
        public static double DamagePerSecond(double depth, double limit) { return S16Damage.PerSecond(depth, limit); }
        /// <summary><c>format creak.level</c> of the HUD grid's lowest point.</summary>
        public static int CreakLevel(double depth, double limit) { return S16Creak.Level(depth, limit); }
    }

    /// <summary>A grid cell (block coordinates of one grid).</summary>
    public struct S16Cell : IEquatable<S16Cell>
    {
        public int X, Y, Z;
        public S16Cell(int x, int y, int z) { X = x; Y = y; Z = z; }
        public bool Equals(S16Cell o) { return X == o.X && Y == o.Y && Z == o.Z; }
        public override bool Equals(object obj) { return obj is S16Cell && Equals((S16Cell)obj); }
        public override int GetHashCode() { unchecked { return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791); } }
        public override string ToString() { return X.ToString(CultureInfo.InvariantCulture) + "," + Y.ToString(CultureInfo.InvariantCulture) + "," + Z.ToString(CultureInfo.InvariantCulture); }
    }

    /// <summary>What the scan of one grid found (<c>format crush.depth</c>): the counted blocks per class and the best functional tier.</summary>
    public struct S16GridSummary
    {
        public int Size;
        /// <summary>
 /// Counted (pressure-relevant: exposed per <c>format exposure</c>) blocks per rated class; a counted Fitting is in
 /// Light or Heavy by the armour it touches and also counted in <see cref="Fitting"/> (information).
 /// </summary>
        public int Light, Heavy, Fitting;
        /// <summary>Blocks with at least one wet face (any class): the damage set when nothing counts.</summary>
        public int Wet;
        /// <summary>The best functional pressure hull upgrade (0 none, 1..3).</summary>
        public int Tier;
        /// <summary>Every block scanned (counted or not).</summary>
        public int Blocks;

        public int Counted { get { return Light + Heavy; } }
        public override string ToString()
        {
            return S16Conventions.Size.Name(Size) + " light " + Light.ToString(CultureInfo.InvariantCulture) + " heavy " + Heavy.ToString(CultureInfo.InvariantCulture) +
                   " (fittings " + Fitting.ToString(CultureInfo.InvariantCulture) + ") wet " + Wet.ToString(CultureInfo.InvariantCulture) + " tier " + Tier.ToString(CultureInfo.InvariantCulture) +
                   " of " + Blocks.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary><c>format block.class</c>: a block's pressure class from its definition, and the tier of an upgrade block.</summary>
    public static class S16Classify
    {
        /// <summary>
 /// The class of a definition: <paramref name="typeId"/> as SE names it (<c>MyObjectBuilder_CubeBlock</c> or
 /// <c>CubeBlock</c>), the SubtypeId and the definition's EdgeType (null when absent).
 /// </summary>
        public static int ClassOf(string typeId, string subtypeId, string edgeType)
        {
            if (string.Equals(edgeType, "Heavy", StringComparison.Ordinal)) return S16Conventions.BlockClass.HeavyArmor;
            string t = typeId ?? "";
            if (t.StartsWith("MyObjectBuilder_", StringComparison.Ordinal)) t = t.Substring("MyObjectBuilder_".Length);
            if (t == "CubeBlock" && (subtypeId ?? "").IndexOf("Armor", StringComparison.OrdinalIgnoreCase) >= 0) return S16Conventions.BlockClass.LightArmor;
            return S16Conventions.BlockClass.Fitting;
        }

        /// <summary>1..3 for an S16 upgrade block's SubtypeId (<c>DE_PressureHullMk&lt;n&gt;Large|Small</c>), else 0.</summary>
        public static int TierOf(string subtypeId)
        {
            if (string.IsNullOrEmpty(subtypeId)) return 0;
            foreach (S16TierRow r in S16Conventions.TierTable)
                if (subtypeId == r.SubtypeBase + "Large" || subtypeId == r.SubtypeBase + "Small") return r.Tier;
            return 0;
        }

        /// <summary>The grid size of a tier block's SubtypeId (Large/Small suffix), else 0.</summary>
        public static int TierBlockSize(string subtypeId)
        {
            if (TierOf(subtypeId) == 0) return 0;
            return subtypeId.EndsWith("Large", StringComparison.Ordinal) ? S16Conventions.Size.Large : S16Conventions.Size.Small;
        }
    }

    /// <summary><c>format exposure</c>: the wet/dry face neighbours of a block's box, the armour it touches, and whether it counts.</summary>
    public static class S16Exposure
    {
        public const int Wet = 1, Dry = 2;

        /// <summary>
 /// Bit set of <see cref="Wet"/> and <see cref="Dry"/> over the face-neighbour cells of the box
 /// [<paramref name="min"/>, <paramref name="max"/>] (the block's own cells excluded): <paramref name="blockAt"/> gives the
 /// class of the block in a cell (0 = empty); an occupied cell is neither wet nor dry and, when it holds armour, is
 /// counted in <paramref name="lightNbr"/> / <paramref name="heavyNbr"/>; an empty cell in an airtight room is dry, any
 /// other empty cell wet. With <paramref name="all"/> false it stops once both bits are set (armour needs no neighbour count).
 /// </summary>
        public static int Of(S16Cell min, S16Cell max, Func<int, int, int, int> blockAt, Func<int, int, int, bool> airtight, bool all,
                             out int lightNbr, out int heavyNbr)
        {
            int bits = 0;
            lightNbr = heavyNbr = 0;
            for (int face = 0; face < 6 && (all || bits != (Wet | Dry)); face++)
            {
                int axis = face >> 1;
                bool plus = (face & 1) != 0;
                int fixedV = axis == 0 ? (plus ? max.X + 1 : min.X - 1) : axis == 1 ? (plus ? max.Y + 1 : min.Y - 1) : (plus ? max.Z + 1 : min.Z - 1);
                int a0 = axis == 0 ? min.Y : min.X, a1 = axis == 0 ? max.Y : max.X;
                int b0 = axis == 2 ? min.Y : min.Z, b1 = axis == 2 ? max.Y : max.Z;
                for (int a = a0; a <= a1 && (all || bits != (Wet | Dry)); a++)
                    for (int b = b0; b <= b1 && (all || bits != (Wet | Dry)); b++)
                    {
                        int x, y, z;
                        if (axis == 0) { x = fixedV; y = a; z = b; }
                        else if (axis == 1) { x = a; y = fixedV; z = b; }
                        else { x = a; y = b; z = fixedV; }
                        int c = blockAt(x, y, z);
                        if (c != 0)
                        {
                            if (c == S16Conventions.BlockClass.LightArmor) lightNbr++;
                            else if (c == S16Conventions.BlockClass.HeavyArmor) heavyNbr++;
                            continue;
                        }
                        bits |= airtight(x, y, z) ? Dry : Wet;
                    }
            }
            return bits;
        }

        /// <summary>Armour counts when it touches water; a Fitting only on the pressure boundary (water on one side, an airtight room on another).</summary>
        public static bool Counts(int cls, int exposure)
        {
            if (S16Conventions.BlockClass.IsArmor(cls)) return (exposure & Wet) != 0;
            return (exposure & Wet) != 0 && (exposure & Dry) != 0;
        }

        /// <summary>The class a counted block is rated as: armour its own; a Fitting the majority of the armour cells it touches (heavy only with more heavy than light; else light).</summary>
        public static int RatedClass(int cls, int lightNbr, int heavyNbr)
        {
            if (S16Conventions.BlockClass.IsArmor(cls)) return cls;
            return heavyNbr > lightNbr ? S16Conventions.BlockClass.HeavyArmor : S16Conventions.BlockClass.LightArmor;
        }
    }

    /// <summary>The hull ladder and the grid's crush depth (<c>format crush.depth</c>).</summary>
    public static class S16Ladder
    {
        public static int HullCrushM(int size, int cls)
        {
            foreach (S16HullRow r in S16Conventions.HullTable) if (r.Size == size && r.Class == cls) return r.CrushM;
            return 0;
        }

        public static int TierCrushM(int tier)
        {
            S16TierRow r;
            return S16Conventions.TryTierRow(tier, out r) ? r.CrushM : 0;
        }

        /// <summary>The hull's depth (the weakest counted class; light armour of the size when none counts) and that class.</summary>
        public static int HullOf(S16GridSummary s, out int weakestClass)
        {
            int best = int.MaxValue;
            weakestClass = 0;
            if (s.Light > 0) Take(ref best, ref weakestClass, s.Size, S16Conventions.BlockClass.LightArmor);
            if (s.Heavy > 0) Take(ref best, ref weakestClass, s.Size, S16Conventions.BlockClass.HeavyArmor);
            if (weakestClass == 0)
            {
                weakestClass = S16Conventions.BlockClass.LightArmor;
                best = HullCrushM(s.Size, weakestClass);
            }
            return best;
        }

        private static void Take(ref int best, ref int cls, int size, int c)
        {
            int m = HullCrushM(size, c);
            if (m < best || (m == best && c < cls)) { best = m; cls = c; }
        }

        public static int CrushDepthOf(S16GridSummary s)
        {
            int weakest;
            return Math.Max(HullOf(s, out weakest), TierCrushM(s.Tier));
        }

        /// <summary>
 /// <c>format group.crush</c>: the grid's own crush depth raised by the best functional tier of its mechanical group
 /// (rotors, hinges, pistons); a docked grid (connector, landing gear) is never below its carrier.
 /// </summary>
        public static int EffectiveCrush(int ownCrush, int groupTier, bool docked, int carrierCrush)
        {
            int l = Math.Max(ownCrush, TierCrushM(groupTier));
            return docked ? Math.Max(l, carrierCrush) : l;
        }

        /// <summary><c>format group.crush</c>: no crush damage for a docked grid lying wholly in the carrier's airtight room.</summary>
        public static bool DamageApplies(bool docked, bool insideCarrierRoom) { return !(docked && insideCarrierRoom); }

        /// <summary><c>format damage.block</c>: the damage set is the wet-faced blocks when nothing counts.</summary>
        public static bool DamageSetIsWet(S16GridSummary s) { return s.Counted == 0; }

        /// <summary>The C5 hull code: (size − 1) × 3 + class (1..6), 0 = none.</summary>
        public static int HullCode(int size, int cls)
        {
            if (size < 1 || size > 2 || cls < 1 || cls > 3) return 0;
            return (size - 1) * 3 + cls;
        }
    }

    /// <summary><c>format damage.block</c>: hull points per second past the limit and the SE integrity per period.</summary>
 /// <summary><c>format damage.gate</c>: destructible blocks, whatever the game mode; de.s16 damage off overrides.</summary>
    public static class S16DamageGateRule
    {
        public static int Of(bool haveSession, bool destructibleBlocks, bool switchOn)
        {
            if (!haveSession) return S16Conventions.DamageGate.None;
            if (!switchOn) return S16Conventions.DamageGate.Off;
            return destructibleBlocks ? S16Conventions.DamageGate.Destructible : S16Conventions.DamageGate.Indestructible;
        }

        public static bool Open(int gate) { return gate == S16Conventions.DamageGate.Destructible; }
    }

    public static class S16Damage
    {
        public static double PerSecond(double depth, double limit)
        {
            if (!(depth > limit)) return 0.0;
            double hps = S16Conventions.Damage.BaseMilliHpS / 1000.0 + S16Conventions.Damage.PerMetreMilliHpS / 1000.0 * (depth - limit);
            return Math.Min(S16Conventions.Damage.CapMilliHpS / 1000.0, hps);
        }

        public static double K(int size)
        {
            return (size == S16Conventions.Size.Large ? S16Conventions.Damage.KLargePermille : S16Conventions.Damage.KSmallPermille) / 1000.0;
        }

        public static double PerPeriodSe(double depth, double limit, int size)
        {
            return PerSecond(depth, limit) * (S16Conventions.Damage.PeriodTicks / 60.0) * K(size);
        }

        /// <summary>Indices of the at most MaxBlocksPerPeriod deepest depths strictly past the limit (ties: lower index first).</summary>
        public static List<int> Pick(IList<double> depths, double limit, int max = S16Conventions.Damage.MaxBlocksPerPeriod)
        {
            var idx = new List<int>();
            for (int i = 0; i < depths.Count; i++) if (depths[i] > limit) idx.Add(i);
            idx.Sort((a, b) => { int c = depths[b].CompareTo(depths[a]); return c != 0 ? c : a.CompareTo(b); });
            if (idx.Count > max) idx.RemoveRange(max, idx.Count - max);
            return idx;
        }
    }

    /// <summary>
 /// The <see cref="S16Damage.Pick"/> rule without allocation or sort: offer depths in index order; keeps the
 /// <see cref="S16Conventions.Damage.MaxBlocksPerPeriod"/> deepest strictly past the limit, deepest first, ties by the
 /// lower index (an equal depth offered later never displaces an earlier one).
 /// </summary>
    public sealed class S16TopK
    {
        public const int K = S16Conventions.Damage.MaxBlocksPerPeriod;
        private readonly double[] _d = new double[K];
        private readonly int[] _i = new int[K];
        public int Count { get; private set; }
        public double Limit { get; private set; }

        public void Reset(double limit) { Count = 0; Limit = limit; }

        public void Offer(double depth, int index)
        {
            if (!(depth > Limit)) return;
            if (Count == K && !(depth > _d[K - 1])) return;
            int at = Count < K ? Count : K - 1;
            while (at > 0 && depth > _d[at - 1])
            {
                if (at < K) { _d[at] = _d[at - 1]; _i[at] = _i[at - 1]; }
                at--;
            }
            _d[at] = depth;
            _i[at] = index;
            if (Count < K) Count++;
        }

        public int Index(int k) { return _i[k]; }
        public double Depth(int k) { return _d[k]; }
    }

    /// <summary><c>format creak.level</c> and the cadence periods.</summary>
    public static class S16Creak
    {
        public static int Level(double depth, double limit, int bandM = S16Conventions.Creak.BandM)
        {
            if (depth > limit) return 3;
            if (depth < limit - bandM) return 0;
            if (depth < limit - S16Conventions.Creak.Level2M) return 1;
            return 2;
        }

        public static int PeriodTicks(int level)
        {
            switch (level)
            {
                case 1: return S16Conventions.Creak.PeriodTicks1;
                case 2: return S16Conventions.Creak.PeriodTicks2;
                case 3: return S16Conventions.Creak.PeriodTicks3;
                default: return 0;
            }
        }
    }

    /// <summary>Why a creak was made (<c>format creak.cadence</c>).</summary>
    public enum S16CreakReason { None = 0, Enter = 1, Rise = 2, Period = 3 }

    /// <summary><c>format creak.cadence</c> for one grid, on SE ticks (deterministic: the Python twin replays it).</summary>
    public sealed class S16CreakClock
    {
        public bool Active { get; private set; }
        /// <summary>The tick and level of the last creak (valid while <see cref="Active"/> or after one).</summary>
        public ulong LastTick { get; private set; }
        public int LastLevel { get; private set; }

        public void Reset() { Active = false; LastTick = 0; LastLevel = 0; }

        /// <summary>One SE tick at <paramref name="level"/>; the reason of a creak made now, else None.</summary>
        public S16CreakReason Step(ulong tick, int level)
        {
            if (level <= 0) { Active = false; return S16CreakReason.None; }
            S16CreakReason why;
            if (!Active) why = S16CreakReason.Enter;
            else if (level > LastLevel) why = S16CreakReason.Rise;
            else if (tick >= LastTick + (ulong)S16Creak.PeriodTicks(LastLevel)) why = S16CreakReason.Period;
            else return S16CreakReason.None;
            Active = true;
            LastTick = tick;
            LastLevel = level;
            return why;
        }
    }

    /// <summary><c>format voice.rule</c>, the arming half: one event per crossing below the limit, re-armed RearmM above it.</summary>
 /// <summary>
 /// <c>format voice.rule</c>: an event when the grid crosses its limit while armed, one more every RepeatMs while it stays
 /// Pure; time in ms of the caller's clock.
 /// </summary>
    public sealed class S16VoiceArm
    {
        private long _lastMs;
        public bool Armed { get; private set; } = true;
        public void Reset() { Armed = true; _lastMs = 0; }

        public bool Step(double depth, double limit, long nowMs)
        {
            if (Armed && depth > limit) { Armed = false; _lastMs = nowMs; return true; }
            if (Armed) return false;
            if (depth < limit - S16Conventions.Voice.RearmM) { Armed = true; return false; }
            if (depth > limit && nowMs - _lastMs >= S16Conventions.Voice.RepeatMs) { _lastMs = nowMs; return true; }
            return false;
        }
    }

    /// <summary><c>format voice.</summary>
    public static class S16VoiceMix
    {
        public static float VoiceVolume { get { return S16Conventions.Voice.VolumePermille / 1000f; } }

        /// <summary>A creak or groan's volume <paramref name="msSinceVoice"/> ms after the last voice play (negative: none yet).</summary>
        public static float EffectVolume(float volume, double msSinceVoice)
        {
            return msSinceVoice >= 0 && msSinceVoice < S16Conventions.Voice.DuckMs ? volume * (S16Conventions.Voice.DuckPermille / 1000f) : volume;
        }
    }

    /// <summary>
 /// The voice rule shared with S15 (ROADMAP §14 S15: the Cyclops voice "once per grid entry with a cooldown"): an event
 /// for a key plays when that key played none within the cooldown. Pure; time in ms of the caller's clock.
 /// </summary>
    public sealed class S16VoiceRule
    {
        private readonly Dictionary<long, long> _last = new Dictionary<long, long>();
        public long CooldownMs { get; }
        public S16VoiceRule(long cooldownMs = S16Conventions.Voice.CooldownMs) { CooldownMs = cooldownMs; }

        public bool TryFire(long key, long nowMs)
        {
            long last;
            if (_last.TryGetValue(key, out last) && nowMs - last < CooldownMs) return false;
            _last[key] = nowMs;
            return true;
        }

        public void Clear() { _last.Clear(); }
    }

    /// <summary><c>format hud.state</c>, <c>format hud.diver</c> and the SE HUD line.</summary>
    public static class S16Hud
    {
        public const int PlaceTopCentre = 1;

        /// <summary><c>format hud.sn</c>: the readout's tone for a HUD state.</summary>
        public static int Tone(int hudState)
        {
            if (hudState == S16Conventions.HudState.Limit || hudState == S16Conventions.HudState.Damage) return S16Conventions.Tone.Danger;
            return hudState == S16Conventions.HudState.Creak ? S16Conventions.Tone.Caution : S16Conventions.Tone.Normal;
        }

        /// <summary>
 /// <c>format hud.sn</c> (depth 7
 /// readout in one field, the Cyclops helm's "depth / crush" with spaces: "10 m / 120 m".
 /// </summary>
        public static string SeatedText(int depthM, int crushM, string meterSuffix)
        {
            string m = string.IsNullOrEmpty(meterSuffix) ? "m" : meterSuffix.Trim();
            string d = depthM.ToString(CultureInfo.InvariantCulture), c = crushM.ToString(CultureInfo.InvariantCulture);
            if (crushM <= 0) return d + " " + m;                                            // no crush depth (a station): the depth only
            if (d.Length + c.Length > S16Conventions.Hud.SeatedLongDigits) return d + " / " + c + " " + m;   // depth 8: "1603 / 1700 m"
            return d + " " + m + " / " + c + " " + m;
        }

        /// <summary><c>format hud.sn</c>: the width the seated text must fit in (canvas or local units, the compass's): FitPermille of it.</summary>
        public static double SeatedTarget(double compassWidth)
        {
            return compassWidth * S16Conventions.Hud.FitPermille / 1000.0;
        }

        /// <summary><c>format hud.sn</c>: the measured font size: the field's own, scaled down so the preferred width fits the target, floored.</summary>
        public static double SeatedFontSize(double ownSize, double preferredWidthAtOwn, double targetWidth)
        {
            double floor = ownSize * S16Conventions.Hud.MinFontPermille / 1000.0;
            double size = preferredWidthAtOwn > targetWidth && preferredWidthAtOwn > 0 ? ownSize * targetWidth / preferredWidthAtOwn : ownSize;
            return Math.Max(floor, Math.Min(ownSize, size));
        }

        /// <summary><c>format hud.sn</c> (depth 9): the seated text centred on the compass: |its centre - the half-moon's centre| within CentreTolPermille of the compass width.</summary>
        public static bool SeatedCentred(double centreDx, double compassWidth)
        {
            return compassWidth > 0 && Math.Abs(centreDx) <= compassWidth * S16Conventions.Hud.CentreTolPermille / 1000.0;
        }

        /// <summary><c>format hud.sn</c>: the live TMP check: one line, the rendered text no wider than the target (half a unit of slack).</summary>
        public static bool SeatedTextFits(int lineCount, double textWidth, double compassWidth)
        {
            return lineCount == 1 && textWidth <= SeatedTarget(compassWidth) + 0.5;
        }

        /// <summary><c>format hud.place</c>: canvas units (origin the centre, y up) to screen pixels (top-left origin).</summary>
        public static void ToScreenPx(double minX, double minY, double maxX, double maxY, double w, double h, double scale, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = (minX + w / 2) * scale;
            x1 = (maxX + w / 2) * scale;
            y0 = (h / 2 - maxY) * scale;
            y1 = (h / 2 - minY) * scale;
        }

        /// <summary>
 /// <c>format hud.place</c>: the union of the readout parts (rects as minX, minY, maxX, maxY quadruples, canvas units) no
 /// wider than W x MaxPartPermille / 1000 and no taller than H x MaxPartPermille / 1000 (larger ones are backdrops);
 /// false when no part is left.
 /// </summary>
        public static bool ReadoutBounds(IList<double> rects, double w, double h, out double minX, out double minY, out double maxX, out double maxY, out int used, out int backdrops)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            used = backdrops = 0;
            double mw = w * S16Conventions.Hud.MaxPartPermille / 1000.0, mh = h * S16Conventions.Hud.MaxPartPermille / 1000.0;
            for (int i = 0; i + 3 < rects.Count; i += 4)
            {
                double x0 = rects[i], y0 = rects[i + 1], x1 = rects[i + 2], y1 = rects[i + 3];
                if (x1 - x0 > mw || y1 - y0 > mh) { backdrops++; continue; }
                used++;
                if (x0 < minX) minX = x0;
                if (y0 < minY) minY = y0;
                if (x1 > maxX) maxX = x1;
                if (y1 > maxY) maxY = y1;
            }
            return used > 0;
        }

        /// <summary><c>format hud.place</c>: Subnautica's own HUD scale for a screen.</summary>
        public static double CanvasScale(int screenW, int screenH, double uiScale)
        {
            if (screenW <= 0 || screenH <= 0) return 1.0;
            double s = Math.Min(screenW / (double)S16Conventions.Hud.RefWidth, screenH / (double)S16Conventions.Hud.RefHeight);
            return s * (uiScale > 0 ? uiScale : 1.0);
        }

        /// <summary>
 /// <c>format hud.place</c>: the move (canvas units, origin the canvas centre, y up) that puts content bounds
 /// [minX, maxX] x [minY, maxY] at its anchor on a W x H canvas, then back inside the canvas when it fits.
 /// </summary>
        public static void Place(int anchor, double minX, double minY, double maxX, double maxY, double w, double h, out double dx, out double dy)
        {
            // depth 6: one anchor, the top centre (the Seamoth panel's left-middle went with the panel)
            dx = 0 - (minX + maxX) / 2;
            dy = (h / 2 - h * S16Conventions.Hud.TopPermille / 1000.0) - maxY;
            if (maxX - minX <= w)
            {
                if (minX + dx < -w / 2) dx = -w / 2 - minX;
                if (maxX + dx > w / 2) dx = w / 2 - maxX;
            }
            if (maxY - minY <= h)
            {
                if (minY + dy < -h / 2) dy = -h / 2 - minY;
                if (maxY + dy > h / 2) dy = h / 2 - maxY;
            }
        }

        public static int State(double depth, double limit, bool damageOn)
        {
            if (depth < S16Conventions.Hud.MinDepthDm / 10.0) return S16Conventions.HudState.Off;
            if (depth > limit) return damageOn ? S16Conventions.HudState.Damage : S16Conventions.HudState.Limit;
            if (depth >= limit - S16Conventions.Creak.BandM) return S16Conventions.HudState.Creak;
            return S16Conventions.HudState.Safe;
        }

        public static int BreathX10(double depth)
        {
            if (depth > S16Conventions.Diver.CrushDepthM) return S16Conventions.Diver.BreathX10Crush;
            if (depth > S16Conventions.Diver.UnsafeDepthM) return S16Conventions.Diver.BreathX10Unsafe;
            return S16Conventions.Diver.BreathX10Safe;
        }

        private static string M(double v) { return Math.Round(v).ToString("0", CultureInfo.InvariantCulture); }

        /// <summary><c>format hud.hull</c>: integrity left over the largest total seen, 0..100 (ceil, as uGUI_SeamothHUD shows a vehicle's health).</summary>
        public static int HullPct(double integrity, double maxSeen)
        {
            if (!(maxSeen > 0)) return 100;
            return (int)Math.Max(0, Math.Min(100, Math.Ceiling(100.0 * integrity / maxSeen - 1e-9)));
        }

        /// <summary><c>format hud.hull</c>: stored over capacity, 0..100 (ceil); 255 = no battery.</summary>
        public static int PowerPct(double stored, double capacity)
        {
            if (!(capacity > 0)) return 255;
            return (int)Math.Max(0, Math.Min(100, Math.Ceiling(100.0 * stored / capacity - 1e-9)));
        }

        /// <summary><c>format c7.s16</c>: SE hides its own line while Subnautica shows the same mode (a fresh C7 record).</summary>
        public static bool SeLineSuppressed(bool c7Fresh, int c7Flags, bool gridMode)
        {
            if (!c7Fresh) return false;
            return (c7Flags & (gridMode ? S16Conventions.C7Flags.GridShown : S16Conventions.C7Flags.DiverShown)) != 0;
        }

        /// <summary><c>format hud.se</c>: SE's text line for this frame: none for the diver; for a seated grid the line unless Subnautica shows its readouts.</summary>
        public static int SeLine(bool c7Fresh, int c7Flags, bool gridMode)
        {
            if (!gridMode) return S16Conventions.SeLine.None;
            return SeLineSuppressed(c7Fresh, c7Flags, true) ? S16Conventions.SeLine.Suppressed : S16Conventions.SeLine.Grid;
        }

        /// <summary>The Subnautica HUD modes of <c>format hud.sn</c>.</summary>
        public const int SnHudNone = 0, SnHudDiver = 1, SnHudGrid = 2;

        /// <summary><c>format hud.sn</c>: what Subnautica shows for a C5 extension and the view state.</summary>
        public static int SnHudMode(bool have, int flags, int hudState, uint viewState)
        {
            if (!have || hudState == S16Conventions.HudState.Off) return SnHudNone;
            if (viewState != ViewState.Underwater && viewState != ViewState.Surfacing) return SnHudNone;
            if ((flags & S16Conventions.C5Flags.Grid) != 0) return SnHudGrid;
            if ((flags & S16Conventions.C5Flags.Diver) != 0) return SnHudDiver;
            return SnHudNone;
        }

        /// <summary>The grid line: "Depth 312 m | crush depth 300 m (heavy armour, small grid, Mk2) | HULL DAMAGE".</summary>
        public static string GridLine(double depth, int crushM, int size, int weakestClass, int tier, int state)
        {
            return GridLine(depth, crushM, size, weakestClass, tier, state, 0, 0);
        }

        /// <summary>The grid line with the C5 flags (Docked, Exempt, Grace) and the grace seconds left.</summary>
        public static string GridLine(double depth, int crushM, int size, int weakestClass, int tier, int state, int flags, int graceS)
        {
            string cls = weakestClass == S16Conventions.BlockClass.HeavyArmor ? "heavy armour" : "light armour";
            string what = cls + ", " + (size == S16Conventions.Size.Large ? "large grid" : "small grid") + (tier > 0 ? ", pressure hull Mk" + tier.ToString(CultureInfo.InvariantCulture) : "") +
                          ((flags & S16Conventions.C5Flags.Docked) != 0 ? ", docked" : "");
            if ((flags & S16Conventions.C5Flags.Exempt) != 0)
                return "Depth " + M(depth) + " m | docked inside a pressurised hull: no crush damage (carrier crush depth " + crushM.ToString(CultureInfo.InvariantCulture) + " m)";
            if ((flags & S16Conventions.C5Flags.Grace) != 0 && state >= S16Conventions.HudState.Limit)
                return "Depth " + M(depth) + " m | crush depth " + crushM.ToString(CultureInfo.InvariantCulture) + " m (" + what + ") | PAST CRUSH DEPTH: pressure damage starts in " +
                       graceS.ToString(CultureInfo.InvariantCulture) + " s";
            string tail;
            switch (state)
            {
                case S16Conventions.HudState.Creak: tail = " | hull creaking: " + M(crushM - depth) + " m to crush depth"; break;
                case S16Conventions.HudState.Limit: tail = " | PAST CRUSH DEPTH by " + M(depth - crushM) + " m (no damage: indestructible blocks or de.s16 damage off)"; break;
                case S16Conventions.HudState.Damage: tail = " | HULL INTEGRITY CRITICAL: " + M(depth - crushM) + " m past crush depth"; break;
                default: tail = ""; break;
            }
            return "Depth " + M(depth) + " m | crush depth " + crushM.ToString(CultureInfo.InvariantCulture) + " m (" + what + ")" + tail;
        }

        /// <summary>SE HUD font of a state (MyFontEnum names): red from the creak band on.</summary>
        public static string Font(int state) { return state >= S16Conventions.HudState.Creak ? "Red" : "White"; }
    }

    /// <summary>The values the C5 extension carries (<c>format c5.s16</c>).</summary>
    public struct S16WireState
    {
        public byte Version, Flags, CreakLevel, HullCode, Tier, BreathX10;
        public ushort HudState;
        public float DepthM, CrushM;
        public uint CreakSeq, VoiceSeq, DamageSeq, WorldEpoch;
        public long GridId;
        public byte HullPct, PowerPct;

        public bool Has(int flag) { return (Flags & flag) != 0; }
    }

    /// <summary>Codec of the C5 extension (SeState bytes 0x80-0xA9, inside layout 1.0's pad_80) and of the C7 block (SnStatus 0xBC-0xBF).</summary>
    public static unsafe class S16Wire
    {
        public const int Offset = S16Conventions.C5.Offset, Size = S16Conventions.C5.Size;

        public static void Write(ref SeState st, ref S16WireState w)
        {
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + Offset;
                b[0x00] = (byte)S16Conventions.C5.Version;
                b[0x01] = w.Flags;
                b[0x02] = w.CreakLevel;
                b[0x03] = w.HullCode;
                b[0x04] = w.Tier;
                b[0x05] = w.BreathX10;
                *(ushort*)(b + 0x06) = w.HudState;
                *(float*)(b + 0x08) = w.DepthM;
                *(float*)(b + 0x0C) = w.CrushM;
                *(uint*)(b + 0x10) = w.CreakSeq;
                *(uint*)(b + 0x14) = w.VoiceSeq;
                *(uint*)(b + 0x18) = w.DamageSeq;
                *(uint*)(b + 0x1C) = w.WorldEpoch;
                *(long*)(b + 0x20) = w.GridId;
                b[0x28] = w.HullPct;
                b[0x29] = w.PowerPct;
            }
        }

        public static void Clear(ref SeState st)
        {
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + Offset;
                for (int i = 0; i < Size; i++) b[i] = 0;
            }
        }

        /// <summary>False (and a default state carrying the version byte) when the record has no extension (version 0 or unknown).</summary>
        public static bool TryRead(ref SeState st, out S16WireState w)
        {
            w = default(S16WireState);
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + Offset;
                w.Version = b[0x00];
                if (w.Version != S16Conventions.C5.Version) return false;
                w.Flags = b[0x01];
                w.CreakLevel = b[0x02];
                w.HullCode = b[0x03];
                w.Tier = b[0x04];
                w.BreathX10 = b[0x05];
                w.HudState = *(ushort*)(b + 0x06);
                w.DepthM = *(float*)(b + 0x08);
                w.CrushM = *(float*)(b + 0x0C);
                w.CreakSeq = *(uint*)(b + 0x10);
                w.VoiceSeq = *(uint*)(b + 0x14);
                w.DamageSeq = *(uint*)(b + 0x18);
                w.WorldEpoch = *(uint*)(b + 0x1C);
                w.GridId = *(long*)(b + 0x20);
                w.HullPct = b[0x28];
                w.PowerPct = b[0x29];
            }
            return true;
        }

        /// <summary>The C7 S16 block (SnStatus 0xBC-0xBF): version and the HUD flags (C7Flags).</summary>
        public static void WriteStatus(ref SnStatus st, byte flags)
        {
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S16Conventions.C7.Offset;
                b[0] = (byte)S16Conventions.C7.Version;
                b[1] = flags;
                b[2] = 0;
                b[3] = 0;
            }
        }

        /// <summary>False when the record carries no S16 block (version 0 or unknown).</summary>
        public static bool TryReadStatus(ref SnStatus st, out byte flags)
        {
            flags = 0;
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S16Conventions.C7.Offset;
                if (b[0] != S16Conventions.C7.Version) return false;
                flags = b[1];
            }
            return true;
        }

        /// <summary>The extension's bytes (tests and the Python twin's vectors).</summary>
        public static byte[] Bytes(ref SeState st)
        {
            var o = new byte[Size];
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + Offset;
                for (int i = 0; i < Size; i++) o[i] = b[i];
            }
            return o;
        }
    }

    /// <summary>
 /// A reader's view of the C5 event counters (<c>format c5.s16</c>): the first value seen in a world epoch is the
 /// baseline; afterwards each increase is one event (several missed at once are coalesced into one sound and counted).
 /// </summary>
    public sealed class S16SeqReader
    {
        private bool _have;
        private uint _epoch, _last;
        public long Coalesced { get; private set; }

        public void Reset() { _have = false; }

        /// <summary>The number of new events (0 or 1 sound to play; <see cref="Coalesced"/> counts the extra).</summary>
        public int Step(uint epoch, uint value)
        {
            if (!_have || epoch != _epoch) { _have = true; _epoch = epoch; _last = value; return 0; }
            if (value == _last) return 0;
            uint d = unchecked(value - _last);
            _last = value;
            if (d > 0x7FFFFFFFu) return 0; // went backwards (a new SE session in the same epoch): rebaseline, no sound
            if (d > 1) Coalesced += d - 1;
            return 1;
        }
    }
}
