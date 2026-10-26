using System;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7 lights (CONTRACT §5.4 "S7 conventions", "C2 lights (S7; S7Lights)"): the C2 light table of layout 1.0 (64 rows of
 // 64 B at 0x38040, header lightCount) carries S7 meaning: SeLight.flags bits 0-2 are the layout's (Spot, Shadows,
 // On), bit 3 Character, the version nibble 28-31 = 1; SeLight.pad_3c holds f32 falloff. Rows are nearest first within
 // LightLimits.RadiusM of the SE camera, at most LightLimits.MaxLights. Subnautica draws them with a pool of Unity
 // lights, at most LightLimits.MaxShadowedSpots of them shadowed (the nearest spot rows with Shadows). Pure.
 // =================================================================================================================

 /// <summary>The S7 meaning of a C2 light row (<c>format light.row</c>).</summary>
    public static unsafe class S7LightRow
    {
        /// <summary>The f32 falloff in <c>SeLight.pad_3c</c>.</summary>
        public const int OffFalloff = 0x3C;

        public static void SetFalloff(byte* row, float falloff) { *(float*)(row + OffFalloff) = falloff; }

        public static float GetFalloff(byte* row) { return *(float*)(row + OffFalloff); }

        public static void SetFalloff(ref SeLight row, float falloff)
        {
            fixed (ulong* p = &row.ownerId) SetFalloff((byte*)p, falloff);
        }

        public static float GetFalloff(ref SeLight row)
        {
            fixed (ulong* p = &row.ownerId) return GetFalloff((byte*)p);
        }

        /// <summary>The row flags: layout bits (Spot, Shadows, On), Character, and the version nibble 1.</summary>
        public static uint Flags(bool spot, bool shadows, bool on, bool character)
        {
            uint f = S7Conventions.C2LightFlagsExt.Version1;
            if (spot) f |= SeLightFlags.Spot;
            if (shadows) f |= SeLightFlags.Shadows;
            if (on) f |= SeLightFlags.On;
            if (character) f |= S7Conventions.C2LightFlagsExt.Character;
            return f;
        }

        /// <summary>The row carries S7 meaning (version nibble 1); any other nibble: ignored and counted by the reader.</summary>
        public static bool IsVersion1(uint flags) { return (flags & S7Conventions.C2LightFlagsExt.VersionMask) == S7Conventions.C2LightFlagsExt.Version1; }

        public static bool IsSpot(uint flags) { return (flags & SeLightFlags.Spot) != 0; }
        public static bool HasShadows(uint flags) { return (flags & SeLightFlags.Shadows) != 0; }
        public static bool IsOn(uint flags) { return (flags & SeLightFlags.On) != 0; }
        public static bool IsCharacter(uint flags) { return (flags & S7Conventions.C2LightFlagsExt.Character) != 0; }

        /// <summary>A row the reader accepts: version 1, finite numbers, a positive range, a non-negative intensity.</summary>
        public static bool Valid(ref SeLight r)
        {
            if (!IsVersion1(r.flags)) return false;
            if (!Finite(r.pos.x) || !Finite(r.pos.y) || !Finite(r.pos.z)) return false;
            if (!Finite(r.dir.x) || !Finite(r.dir.y) || !Finite(r.dir.z)) return false;
            if (!Finite(r.colour.x) || !Finite(r.colour.y) || !Finite(r.colour.z)) return false;
            if (!Finite(r.range) || r.range <= 0f || r.range > 10000f) return false;
            if (!Finite(r.intensity) || r.intensity < 0f) return false;
            if (!Finite(r.coneDeg) || r.coneDeg < 0f || r.coneDeg > 180f) return false;
            if (Math.Abs(r.pos.x) > 1e6f || Math.Abs(r.pos.y) > 1e6f || Math.Abs(r.pos.z) > 1e6f) return false;
            return true;
        }

        private static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
    }

    /// <summary>The SE side's row order: nearest first within the radius, at most the cap (<c>format light.row</c>).</summary>
    public static class LightSelect
    {
        /// <summary>
 /// Fills <paramref name="order"/> with the indices of the candidates within <paramref name="radiusM"/> (squared
 /// distances in <paramref name="distSq"/>), nearest first (ties by index), at most <paramref name="max"/>; returns
 /// how many. NaN distances never qualify. Allocation-free (insertion into the bounded output).
 /// </summary>
        public static int Select(double[] distSq, int n, double radiusM, int max, int[] order)
        {
            if (distSq == null || order == null) throw new ArgumentNullException(distSq == null ? "distSq" : "order");
            if (max > order.Length) max = order.Length;
            if (max <= 0) return 0;
            double r2 = radiusM * radiusM;
            int count = 0;
            for (int i = 0; i < n && i < distSq.Length; i++)
            {
                double d = distSq[i];
                if (!(d <= r2)) continue;
                // full: only a strictly nearer candidate replaces the farthest kept one (ties keep the lower index)
                if (count == max && !(distSq[order[max - 1]] > d)) continue;
                int k = count < max ? count : max - 1;
                while (k > 0 && distSq[order[k - 1]] > d) { order[k] = order[k - 1]; k--; }
                order[k] = i;
                if (count < max) count++;
            }
            return count;
        }
    }

    /// <summary>The Subnautica pool's pure rules (<c>LightLimits</c>; CONTRACT "C2 lights").</summary>
    public static class LightPoolRules
    {
        /// <summary>The shortest and widest Unity spot angles used (degrees).</summary>
        public const float MinSpotAngle = 1f, MaxSpotAngle = 179f;

        /// <summary>
 /// Which rows get shadows: rows arrive nearest first; the first <paramref name="maxShadowed"/> spot rows with the
 /// Shadows flag get them, every other row none (a negative cap = no cap: S7.M18). Returns the shadowed count.
 /// </summary>
        public static int ShadowPlan(uint[] flags, int count, int maxShadowed, bool[] shadows)
        {
            if (flags == null || shadows == null) throw new ArgumentNullException(flags == null ? "flags" : "shadows");
            int n = Math.Min(count, Math.Min(flags.Length, shadows.Length));
            int shadowed = 0;
            for (int i = 0; i < n; i++)
            {
                bool want = S7LightRow.IsSpot(flags[i]) && S7LightRow.HasShadows(flags[i]);
                bool give = want && (maxShadowed < 0 || shadowed < maxShadowed);
                shadows[i] = give;
                if (give) shadowed++;
            }
            for (int i = n; i < shadows.Length && i < count; i++) shadows[i] = false;
            return shadowed;
        }

        /// <summary>The Unity intensity: SE's intensity × the tunable scale (never negative, NaN → 0).</summary>
        public static float Intensity(float seIntensity, float scale)
        {
            float v = seIntensity * scale;
            return float.IsNaN(v) || v < 0f ? 0f : float.IsInfinity(v) ? 8f * 1000f : v;
        }

        /// <summary>The Unity range: SE's range × the tunable range scale (at least 0.01 m).</summary>
        public static float Range(float seRange, float scale)
        {
            float v = seRange * scale;
            return float.IsNaN(v) || v < 0.01f ? 0.01f : v;
        }

        /// <summary>The Unity <c>spotAngle</c> (the full cone, as SE's <c>ReflectorConeDegrees</c>), clamped to 1..179°.</summary>
        public static float SpotAngle(float coneDeg)
        {
            if (float.IsNaN(coneDeg)) return 60f;
            return Math.Max(MinSpotAngle, Math.Min(MaxSpotAngle, coneDeg));
        }

        /// <summary>SE's cone (<c>ReflectorConeDegrees</c>) from <c>ReflectorConeMaxAngleCos</c>: 2·acos(1 − c) in degrees.</summary>
        public static float ConeDegreesFromMaxAngleCos(float maxAngleCos)
        {
            double a = 1.0 - maxAngleCos;
            if (a > 1) a = 1; if (a < -1) a = -1;
            return (float)(2.0 * Math.Acos(a) * 180.0 / Math.PI);
        }
    }

    /// <summary>
 /// Subnautica's half of the light table (CONTRACT "C2 lights (S7)"): an <see cref="IC2ExtensionReader"/> that copies
 /// <c>lightCount</c> rows (≤ 64) inside the C2 seqlock window and, after a coherent read, publishes them with the
 /// header's tick. Rows whose version nibble is not 1 (an SE before S7 writes none) are dropped and counted; the
 /// <c>lightsFound</c> of the header extension is taken when the extension is valid for the tick. No allocation after
 /// construction; the published copy is read by the light pool on the main thread.
 /// </summary>
    public sealed unsafe class S7LightReader : IC2ExtensionReader
    {
        private readonly object _lock = new object();
        private readonly SeLight[] _copy = new SeLight[SeEntitiesLimits.MaxLights];
        private int _copyCount;
        private uint _copyRawCount;
        private C2Ext _copyExt;
        private readonly SeLight[] _rows = new SeLight[SeEntitiesLimits.MaxLights];
        private int _count;
        private ulong _tick;
        private uint _found;
        private bool _foundValid;
        private int _serial;

        public long Commits, RowsTruncated, RowsNotV1, RowsInvalid;
        /// <summary>Review F20: the <c>Stopwatch</c> timestamp of the last commit (0 = none since the last reset).</summary>
        private long _commitStamp;

        /// <summary>
 /// Seconds since the last commit (infinity when nothing was committed since the last reset): the light pool turns
 /// itself off when the newest snapshot is older than <see cref="StaleSeconds"/> (SE exited to the menu or stalled:
 /// its world lights must not stay lit in Subnautica until a new SE peer arrives).
 /// </summary>
        public double SecondsSinceCommit
        {
            get
            {
                long c;
                lock (_lock) c = _commitStamp;
                return c == 0 ? double.PositiveInfinity : (System.Diagnostics.Stopwatch.GetTimestamp() - c) / (double)System.Diagnostics.Stopwatch.Frequency;
            }
        }

        /// <summary>Review F20: a snapshot older than this is not drawn.</summary>
        public const double StaleSeconds = 1.0;

        public void CopyInWindow(byte* region)
        {
            if (region == null) return;
            uint n = *(uint*)(region + 0x18); // SeEntitiesHeader.lightCount
            _copyRawCount = n;
            int k = (int)Math.Min(n, (uint)SeEntitiesLimits.MaxLights);
            _copyCount = k;
            if (k > 0)
                fixed (SeLight* dst = _copy)
                    Buffer.MemoryCopy(region + SeEntitiesLimits.LightsOffset, dst, (long)SeEntitiesLimits.MaxLights * SeEntitiesLimits.LightSize, (long)k * SeEntitiesLimits.LightSize);
            _copyExt = *(C2Ext*)(region + S7Conventions.C2Ext.Offset);
        }

        public void Commit(ulong headerTick)
        {
            lock (_lock)
            {
                Commits++;
                if (_copyRawCount > SeEntitiesLimits.MaxLights) RowsTruncated++;
                int m = 0;
                for (int i = 0; i < _copyCount; i++)
                {
                    if (!S7LightRow.IsVersion1(_copy[i].flags)) { RowsNotV1++; continue; }
                    if (!S7LightRow.Valid(ref _copy[i])) { RowsInvalid++; continue; }
                    _rows[m++] = _copy[i];
                }
                _count = m;
                _tick = headerTick;
                _foundValid = _copyExt.version == S7Conventions.C2Ext.Version && _copyExt.tickLow == (uint)headerTick;
                _found = _foundValid ? _copyExt.lightsFound : (uint)m;
                _serial++;
                _commitStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }

        /// <summary>Copies the newest published rows into <paramref name="rows"/> (length ≥ 64); returns how many.</summary>
        public int Snapshot(SeLight[] rows, out ulong tick, out uint found, out int serial)
        {
            lock (_lock)
            {
                int n = Math.Min(_count, rows.Length);
                Array.Copy(_rows, rows, n);
                tick = _tick;
                found = _found;
                serial = _serial;
                return n;
            }
        }

        /// <summary>A new SE generation or world: nothing published until the next commit.</summary>
        public void Reset()
        {
            lock (_lock) { _count = 0; _tick = 0; _found = 0; _serial++; _commitStamp = 0; }
        }
    }

    /// <summary>
 /// S7.N3 calibration (HYPOTHESIS until the real run): the wall luminance under an SE Interior Light and under the
 /// Seamoth cabin light at the same distance from a like wall; the ratio passes within ±25 %; the suggested
 /// <c>LightIntensityScale</c> makes them equal (luminance is linear in the light intensity for an unsaturated pixel).
 /// </summary>
    public static class LightCalibration
    {
        public const double Tolerance = 0.25;
        public const double MinScale = 0.05, MaxScale = 50.0;

        /// <summary>sRGB (0-1) to linear.</summary>
        public static double Linear(double c)
        {
            if (double.IsNaN(c)) return 0;
            if (c <= 0) return 0;
            if (c >= 1) return 1;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>Relative luminance (Rec. 709) of an sRGB pixel (0-1 per channel).</summary>
        public static double LuminanceSrgb(double r, double g, double b)
        {
            return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
        }

        /// <summary>SE light luminance over the Seamoth's (NaN when the Seamoth probe is black).</summary>
        public static double Ratio(double seLum, double seamothLum)
        {
            if (!(seamothLum > 0)) return double.NaN;
            return seLum / seamothLum;
        }

        /// <summary>|ratio − 1| ≤ 25 %.</summary>
        public static bool Pass(double ratio)
        {
            return !double.IsNaN(ratio) && Math.Abs(ratio - 1.0) <= Tolerance;
        }

        /// <summary>The scale that makes the SE light match the Seamoth's: current × seamoth / se, clamped to 0.05..50.</summary>
        public static double SuggestScale(double currentScale, double seLum, double seamothLum)
        {
            if (!(seLum > 0) || !(seamothLum > 0) || !(currentScale > 0)) return currentScale > 0 ? currentScale : 1.0;
            double s = currentScale * seamothLum / seLum;
            return Math.Max(MinScale, Math.Min(MaxScale, s));
        }
    }
}
