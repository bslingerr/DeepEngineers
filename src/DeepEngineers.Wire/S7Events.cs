using System;
using System.Globalization;
using System.Runtime.InteropServices;
using EventFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EventLimits = DeepEngineers.Wire.S7Conventions.EventLimits;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;
#pragma warning disable 0169, 0649 // explicit pads are never read or written on purpose

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7 effect events, the record half: the 64-byte event row at C2 0x40000 + i·64, the header extension in SeEntitiesHeader.pad_1c, the
 // allocation-free sustain keys (format event.key) and the shared rules both plugins use (flags from the SN height,
 // priority, the per-type numbers of the table, the Subnautica look rules: thruster emission and the engine rpm,
 // the explosion scale). Layout 1.0 is unchanged; the names and values are S7Conventions' (ext s7 look 1). Pure: no
 // game, no Unity, no allocation per event. The window, the sustain tracker and the reader are in S7EventsWindow.cs
 // and S7EventsReader.cs.
 // =================================================================================================================

 /// <summary>
 /// One C2 effect-event row (64 B at C2 offset <c>0x40000 + i·64</c>; CONTRACT §5.4 "C2 effect events", <c>field
 /// event.*</c>). Not a layout record: the C2 region tail is unpinned space, pinned by <c>ext s7 look 1</c>.
 /// <c>Attached</c>: <see cref="pos"/>/<see cref="dir"/> in the SE-local metres of <see cref="entityId"/>; else
 /// SN-world through F and mapdir.
 /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64, Pack = 1)]
    public struct S7EventRow
    {
        /// <summary>Event number, consecutive in the window, from 1 per stream.</summary>
        [FieldOffset(0x00)] public uint seq;
        /// <summary><see cref="S7Conventions.EventType"/>.</summary>
        [FieldOffset(0x04)] public byte type;
        /// <summary><see cref="S7Conventions.EventFlags"/>.</summary>
        [FieldOffset(0x05)] public byte flags;
        /// <summary>One-shot: the effect's duration; sustain: <see cref="S7Conventions.EventLimits.SustainLifetimeMs"/>.</summary>
        [FieldOffset(0x06)] public ushort lifetimeMs;
        /// <summary>The mirrored grid, subgrid or character the effect is attached to; 0 = world.</summary>
        [FieldOffset(0x08)] public ulong entityId;
        /// <summary>Sustain identity (<c>format event.key</c>); one-shots 0.</summary>
        [FieldOffset(0x10)] public ulong key;
        [FieldOffset(0x18)] public F32x3 pos;
        /// <summary>Unit vector in the frame of <see cref="pos"/>; 0 = none.</summary>
        [FieldOffset(0x24)] public F32x3 dir;
        /// <summary>0..1, per type.</summary>
        [FieldOffset(0x30)] public float intensity;
        /// <summary>Metres, per type.</summary>
        [FieldOffset(0x34)] public float size;
        /// <summary>Low 32 bits of the SE tick that raised it.</summary>
        [FieldOffset(0x38)] public uint tickLow;
        /// <summary>
 /// Per type (<see cref="S7Conventions.ThrusterKind"/>, <see cref="S7Conventions.WeaponKind"/>, <see cref="S7Conventions.ToolSource"/>, …).
 /// S7p-effects-under: the low byte keeps that S7 meaning for every type;
 /// Drill/PrawnDrill rows carry the contact class in bits 8–11 (<see cref="S7pFxCodec.ContactOf"/>), every other
 /// type leaves bits 8–15 at 0. Read the S7 value with <c>subtype &amp; 0xFF</c>.
 /// </summary>
        [FieldOffset(0x3C)] public ushort subtype;
        /// <summary>
 /// The S7 pad (0 in <c>ext s7 look 1</c>). S7p-effects-under: Thruster rows carry SE's flame, <c>class&lt;&lt;13 | r4&lt;&lt;9 | g5&lt;&lt;4 | b4</c> (the flame length
 /// scale class and the colour; <see cref="S7pFxCodec.EncodeFlame"/>); 0 = none (an older SE); every other type 0.
 /// </summary>
        [FieldOffset(0x3E)] public ushort extra;

        public bool Has(byte flag) { return (flags & flag) != 0; }
        public bool Attached { get { return (flags & EventFlags.Attached) != 0; } }
        public bool IsStop { get { return (flags & EventFlags.Stop) != 0; } }
        public bool IsSustain { get { return (flags & EventFlags.Sustain) != 0; } }
        public bool Underwater { get { return (flags & EventFlags.Underwater) != 0; } }

        /// <summary>Type 1..13 and every float finite (positions within ±1e7 m).</summary>
        public bool IsValid
        {
            get
            {
                return EventType.IsValid(type) && S7EventRules.Finite(pos.x, 1e7f) && S7EventRules.Finite(pos.y, 1e7f) && S7EventRules.Finite(pos.z, 1e7f) &&
                       S7EventRules.Finite(dir.x, 2f) && S7EventRules.Finite(dir.y, 2f) && S7EventRules.Finite(dir.z, 2f) &&
                       S7EventRules.Finite(intensity, 1e6f) && S7EventRules.Finite(size, 1e6f);
            }
        }

        public override string ToString()
        {
            return "#" + seq.ToString(CultureInfo.InvariantCulture) + " " + EventType.Name(type) + " flags=0x" + flags.ToString("x2", CultureInfo.InvariantCulture) +
                   " entity=" + entityId.ToString(CultureInfo.InvariantCulture) + " key=0x" + key.ToString("x16", CultureInfo.InvariantCulture) +
                   " pos=(" + pos.x.ToString("R", CultureInfo.InvariantCulture) + ", " + pos.y.ToString("R", CultureInfo.InvariantCulture) + ", " +
                   pos.z.ToString("R", CultureInfo.InvariantCulture) + ") i=" + intensity.ToString("R", CultureInfo.InvariantCulture) +
                   " size=" + size.ToString("R", CultureInfo.InvariantCulture) + " sub=" + subtype.ToString(CultureInfo.InvariantCulture) +
                   " life=" + lifetimeMs.ToString(CultureInfo.InvariantCulture) + "ms tickLow=" + tickLow.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
 /// The C2 header extension in <c>SeEntitiesHeader.pad_1c</c> (0x1C–0x3F, 36 B; CONTRACT §5.4 "C2 effect events",
 /// <c>field c2ext.*</c>): offsets here are relative to 0x1C. Valid only when <see cref="version"/> = 1 and
 /// <see cref="tickLow"/> = the low 32 bits of the header's <c>tick</c> (a stale extension of an earlier write is
 /// rejected). SE writes it inside the C2 seqlock window (B2's <c>Link.WriteSeEntities</c> overload); Subnautica reads it
 /// in the same window (<see cref="S7EventReader"/>).
 /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 36, Pack = 1)]
    public unsafe struct C2Ext
    {
        [FieldOffset(0x00)] public byte version;
        /// <summary><see cref="S7Conventions.C2ExtFlags"/>: Lights 1 (the light rows carry S7 meaning), Events 2 (the window is written).</summary>
        [FieldOffset(0x01)] public byte flags;
        [FieldOffset(0x02)] public ushort eventCount;
        [FieldOffset(0x04)] public uint eventFirstSeq;
        /// <summary>(SE generation &lt;&lt; 16) | (S5 stream restarts &amp; 0xFFFF): a new value restarts seq at 1.</summary>
        [FieldOffset(0x08)] public uint eventStream;
        /// <summary>Cumulative cap drops in this stream.</summary>
        [FieldOffset(0x0C)] public uint eventsDropped;
        /// <summary>Light candidates before the cap (B2 lights).</summary>
        [FieldOffset(0x10)] public uint lightsFound;
        [FieldOffset(0x14)] public uint tickLow;
        [FieldOffset(0x18)] private fixed byte pad_18[12];

        /// <summary>A version-1 extension for the C2 header of <paramref name="tick"/>.</summary>
        public static C2Ext For(ulong tick, byte flags, int eventCount, uint eventFirstSeq, uint eventStream, uint eventsDropped, uint lightsFound)
        {
            if (eventCount < 0 || eventCount > S7Conventions.C2Events.MaxRows) throw new ArgumentOutOfRangeException("eventCount");
            return new C2Ext
            {
                version = (byte)S7Conventions.C2Ext.Version,
                flags = flags,
                eventCount = (ushort)eventCount,
                eventFirstSeq = eventFirstSeq,
                eventStream = eventStream,
                eventsDropped = eventsDropped,
                lightsFound = lightsFound,
                tickLow = (uint)tick,
            };
        }

        /// <summary>Writes the 36 bytes at <c>header + 0x1C</c> (the 12 pad bytes 0). <paramref name="header"/> = the C2 region start.</summary>
        public static void Write(byte* header, in C2Ext ext)
        {
            if (header == null) throw new ArgumentNullException("header");
            C2Ext copy = ext;
            for (int i = 0; i < 12; i++) copy.pad_18[i] = 0;
            *(C2Ext*)(header + S7Conventions.C2Ext.Offset) = copy;
        }

        /// <summary>Reads the extension; true only for version 1 with <c>tickLow</c> = low 32 bits of <paramref name="tick"/> (the header's tick).</summary>
        public static bool TryRead(byte* header, ulong tick, out C2Ext ext)
        {
            if (header == null) throw new ArgumentNullException("header");
            ext = *(C2Ext*)(header + S7Conventions.C2Ext.Offset);
            return ext.version == S7Conventions.C2Ext.Version && ext.tickLow == (uint)tick;
        }

        public bool HasEvents { get { return (flags & S7Conventions.C2ExtFlags.Events) != 0; } }
        public bool HasLights { get { return (flags & S7Conventions.C2ExtFlags.Lights) != 0; } }
    }

    /// <summary>
 /// Sustain keys (<c>format event.key</c>): FNV-1a 64 of the UTF-8 (here ASCII) texts <c>thr|&lt;fatKey x16&gt;|&lt;flame
 /// index&gt;</c>, <c>tool|&lt;fatKey x16&gt;</c> and <c>tool|e&lt;entity id&gt;</c>, computed without building a string;
 /// one-shots carry key 0. <c>fatKey</c> is S5's <c>partKey.fat</c> (<see cref="MirrorConventions.FatPartKey"/>),
 /// formatted as 16 lower-case hex digits.
 /// </summary>
    public static class S7EventKeys
    {
        /// <summary><c>thr|&lt;fatKey x16&gt;|&lt;flame index&gt;</c>: one thruster flame (a sustain per flame of <c>MyThrust.Flames</c>).</summary>
        public static ulong Thruster(ulong fatKey, int flameIndex)
        {
            if (flameIndex < 0) throw new ArgumentOutOfRangeException("flameIndex");
            ulong h = Ascii(LayoutHash.FnvOffsetBasis, "thr|");
            h = Hex16(h, fatKey);
            h = Byte(h, (byte)'|');
            return Decimal(h, flameIndex);
        }

        /// <summary><c>tool|&lt;fatKey x16&gt;</c>: a ship tool (welder, grinder, drill block).</summary>
        public static ulong ShipTool(ulong fatKey)
        {
            ulong h = Ascii(LayoutHash.FnvOffsetBasis, "tool|");
            return Hex16(h, fatKey);
        }

        /// <summary><c>tool|e&lt;entity id&gt;</c>: a hand tool by its entity id (invariant decimal, '-' for a negative id).</summary>
        public static ulong HandTool(long toolEntityId)
        {
            ulong h = Ascii(LayoutHash.FnvOffsetBasis, "tool|e");
            return Decimal(h, toolEntityId);
        }

        /// <summary>The key texts (reference and logs; allocates).</summary>
        public static string ThrusterText(ulong fatKey, int flameIndex)
        {
            return "thr|" + fatKey.ToString("x16", CultureInfo.InvariantCulture) + "|" + flameIndex.ToString(CultureInfo.InvariantCulture);
        }

        public static string ShipToolText(ulong fatKey) { return "tool|" + fatKey.ToString("x16", CultureInfo.InvariantCulture); }

        public static string HandToolText(long toolEntityId) { return "tool|e" + toolEntityId.ToString(CultureInfo.InvariantCulture); }

        private static ulong Byte(ulong h, byte b)
        {
            h ^= b;
            return h * LayoutHash.FnvPrime;
        }

        private static ulong Ascii(ulong h, string s)
        {
            for (int i = 0; i < s.Length; i++) h = Byte(h, (byte)s[i]);
            return h;
        }

        private static ulong Hex16(ulong h, ulong v)
        {
            for (int shift = 60; shift >= 0; shift -= 4)
            {
                int d = (int)((v >> shift) & 0xF);
                h = Byte(h, (byte)(d < 10 ? '0' + d : 'a' + d - 10));
            }
            return h;
        }

        private static ulong Decimal(ulong h, long v)
        {
            if (v < 0)
            {
                h = Byte(h, (byte)'-');
                // long.MinValue has no positive counterpart: digits of the unsigned magnitude
                return DecimalUnsigned(h, (ulong)(-(v + 1)) + 1UL);
            }
            return DecimalUnsigned(h, (ulong)v);
        }

        private static ulong DecimalUnsigned(ulong h, ulong v)
        {
            ulong div = 1;
            while (v / div >= 10) div *= 10;
            while (div > 0)
            {
                h = Byte(h, (byte)('0' + (int)(v / div % 10)));
                div /= 10;
            }
            return h;
        }
    }

    /// <summary>
 /// The shared rules of the effect events (CONTRACT §5.4 "C2 effect events", the type table; ROADMAP S7.N4): the water
 /// flags from the SN height, the priority over <c>MaxPerTick</c>, the per-type numbers SE computes, the stream id, and
 /// the Subnautica look rules the oracle checks (thruster emission ∝ intensity × size, the engine rpm, the explosion
 /// scale). Pure functions
 /// </summary>
    public static class S7EventRules
    {
        /// <summary><c>event.priority</c> after the Stop rows: Explosion first … BulletStreak last.</summary>
        public static readonly byte[] PriorityOrder =
        {
            EventType.Explosion, EventType.HullBreak, EventType.GridImpact, EventType.Splash, EventType.Impact, EventType.WeaponMuzzle,
            EventType.Repair, EventType.Welder, EventType.Grinder, EventType.Drill, EventType.PrawnDrill, EventType.Thruster, EventType.BulletStreak,
        };

        /// <summary>Highest type value + 1 (array sizes indexed by type).</summary>
        public const int TypeSlots = EventType.Splash + 1;

        /// <summary>Priority rank: 0 for a Stop row of any type, then 1 (Explosion) … 13 (BulletStreak); 14 unknown.</summary>
        public static int PriorityRank(byte type, byte flags)
        {
            if ((flags & EventFlags.Stop) != 0) return 0;
            for (int i = 0; i < PriorityOrder.Length; i++) if (PriorityOrder[i] == type) return i + 1;
            return PriorityOrder.Length + 1;
        }

        /// <summary><c>Underwater</c> when SN y &lt; 0, <c>Surface</c> when |SN y| ≤ <c>SurfaceBandCm</c>/100 m.</summary>
        public static byte WaterFlags(double snY)
        {
            byte f = 0;
            if (snY < 0) f |= EventFlags.Underwater;
            if (Math.Abs(snY) <= EventLimits.SurfaceBandCm / 100.0) f |= EventFlags.Surface;
            return f;
        }

        /// <summary>The flags a sustain row keeps from its source (everything but Sustain/Stop, which the tracker sets).</summary>
        public const byte SourceFlagMask = EventFlags.Attached | EventFlags.Underwater | EventFlags.Surface | EventFlags.Hit | EventFlags.Local;

        /// <summary><c>eventStream</c> = (SE generation &lt;&lt; 16) | (S5 stream restarts &amp; 0xFFFF).</summary>
        public static uint Stream(uint seGeneration, long streamRestarts)
        {
            return (seGeneration << 16) | (uint)(streamRestarts & 0xFFFF);
        }

        /// <summary>Within <c>EffectRadiusM</c> (explosions <c>ExplosionRadiusM</c>) of the camera.</summary>
        public static bool InRadius(byte type, double distanceM)
        {
            double r = type == EventType.Explosion ? EventLimits.ExplosionRadiusM : EventLimits.EffectRadiusM;
            return distanceM <= r;
        }

        // ------------------------------------------------------------------------------------- SE: per-type numbers

 /// <summary><c>ThrusterKind</c> of <c>MyThrustDefinition.ThrusterType</c> ("Ion" 1, "Atmospheric" 2, "Hydrogen" 3, else 0).</summary>
        public static ushort ThrusterKindOf(string thrusterType)
        {
            if (string.Equals(thrusterType, "Ion", StringComparison.OrdinalIgnoreCase)) return S7Conventions.ThrusterKind.Ion;
            if (string.Equals(thrusterType, "Atmospheric", StringComparison.OrdinalIgnoreCase)) return S7Conventions.ThrusterKind.Atmospheric;
            if (string.Equals(thrusterType, "Hydrogen", StringComparison.OrdinalIgnoreCase)) return S7Conventions.ThrusterKind.Hydrogen;
            return S7Conventions.ThrusterKind.Other;
        }

        /// <summary>A thruster flame is live (a sustain) while its strength is above this (CurrentStrength 0 = Stop).</summary>
        public const float ThrusterLiveStrength = 1e-3f;

        /// <summary>BulletStreak intensity = ProjectileCount / 8, at most 1.</summary>
        public static float StreakIntensity(int projectileCount)
        {
            return Clamp01(projectileCount / 8f);
        }

        /// <summary>BulletStreak length = min(MaxTrajectory, StreakMaxM).</summary>
        public static float StreakLength(float maxTrajectory)
        {
            if (!(maxTrajectory > 0) || float.IsInfinity(maxTrajectory)) return EventLimits.StreakMaxM;
            return Math.Min(maxTrajectory, EventLimits.StreakMaxM);
        }

        /// <summary>BulletStreak lifetime = length / DesiredSpeed, at least 100 ms (and at most 65,535 ms).</summary>
        public static ushort StreakLifetimeMs(float length, float desiredSpeed)
        {
            double ms = desiredSpeed > 0 && !float.IsInfinity(desiredSpeed) ? length / desiredSpeed * 1000.0 : 100.0;
            return Ms(Math.Max(100.0, ms));
        }

        /// <summary>Explosion intensity = min(1, Damage / 1,000).</summary>
        public static float ExplosionIntensity(float damage) { return Clamp01(damage / 1000f); }

        /// <summary>GridImpact intensity = min(1, separatingSpeed / 20 m/s).</summary>
        public static float GridImpactIntensity(float separatingSpeed) { return Clamp01(Math.Abs(separatingSpeed) / 20f); }

        /// <summary>GridImpact size = min(4, impulse / 1e5) m.</summary>
        public static float GridImpactSize(float impulse)
        {
            if (!(impulse > 0) || float.IsInfinity(impulse)) return 0f;
            return Math.Min(4f, impulse / 1e5f);
        }

        /// <summary>Splash intensity = min(1, |vy| / 20 m/s).</summary>
        public static float SplashIntensity(double vy) { return Clamp01((float)(Math.Abs(vy) / 20.0)); }

        /// <summary>
 /// The splash test: the entity's lowest point went from one side of SN y = 0 to the other (or onto it) since the
 /// previous tick while its vertical speed is at least <c>SplashMinSpeedCmS</c>.
 /// </summary>
        public static bool SplashCrossing(double prevBottomY, double bottomY, double vy)
        {
            if (double.IsNaN(prevBottomY) || double.IsNaN(bottomY)) return false;
            if (Math.Abs(vy) * 100.0 < EventLimits.SplashMinSpeedCmS) return false;
            bool down = prevBottomY > 0 && bottomY <= 0;
            bool up = prevBottomY < 0 && bottomY >= 0;
            return down || up;
        }

        /// <summary>Tool intensity: 1 while it touches something (<c>Hit</c>), else 0.25 (Grinder, Drill, Repair, PrawnDrill).</summary>
        public static float ToolIntensity(bool hit) { return hit ? 1f : 0.25f; }

        /// <summary>One-shot lifetimes (ms) of the types that carry no length of their own (HYPOTHESIS look).</summary>
        public static ushort OneShotLifetimeMs(byte type)
        {
            switch (type)
            {
                case EventType.WeaponMuzzle: return 60;
                case EventType.Impact: return 1000;
                case EventType.Explosion: return 3000;
                case EventType.GridImpact: return 1500;
                case EventType.HullBreak: return 3000;
                case EventType.Splash: return 2000;
                default: return 1000;
            }
        }

        // ------------------------------------------------------------------------------------- Subnautica: the look

 /// <summary>Flame size giving the source's own emission rate at intensity 1 (HYPOTHESIS look;</summary>
        public const float ThrusterRefSizeM = 1f;

        /// <summary>Above this intensity a thruster adds a cavitation burst on the way up (HYPOTHESIS look).</summary>
        public const float CavitationThreshold = 0.7f;

        /// <summary>Particles of one cavitation burst per unit of flame size (HYPOTHESIS look).</summary>
        public const int CavitationBurstPerM = 12;

        /// <summary>
 /// Thruster bubble emission (particles per second) of one flame: <paramref name="baseRate"/> (the source's own
 /// <c>rateOverTimeMultiplier</c>) × intensity × size / <see cref="ThrusterRefSizeM"/>: linear in intensity (S7.N4
 /// thruster.rate) and zero at intensity 0. <paramref name="constant"/> is S7.M11 (the rate ignores the intensity).
 /// </summary>
        public static float ThrusterEmissionRate(float baseRate, float intensity, float size, bool constant)
        {
            if (!(baseRate > 0) || float.IsInfinity(baseRate)) return 0f;
            float s = size > 0 && !float.IsInfinity(size) ? size / ThrusterRefSizeM : 0f;
            if (constant) return baseRate * Math.Max(s, 1f);
            return baseRate * Clamp01(intensity) * s;
        }

        /// <summary>True when the intensity crossed <see cref="CavitationThreshold"/> upwards (a burst is emitted once).</summary>
        public static bool CavitationEdge(float previous, float now)
        {
            return previous <= CavitationThreshold && now > CavitationThreshold;
        }

        /// <summary>
 /// The engine loop's <c>rpm</c> parameter of one entity: Σ intensity·size² / Σ size² over its live thruster
 /// sustains (0 when none or all sizes 0). Accumulate with <see cref="RpmAdd"/>.
 /// </summary>
        public static float EngineRpm(double sumIntensitySize2, double sumSize2)
        {
            if (!(sumSize2 > 0)) return 0f;
            return Clamp01((float)(sumIntensitySize2 / sumSize2));
        }

        /// <summary>Adds one live thruster sustain to the rpm sums.</summary>
        public static void RpmAdd(ref double sumIntensitySize2, ref double sumSize2, float intensity, float size)
        {
            double s2 = (double)size * size;
            if (!(s2 > 0) || double.IsInfinity(s2)) return;
            sumIntensitySize2 += Clamp01(intensity) * s2;
            sumSize2 += s2;
        }

        /// <summary>The crashfish detonation's own radius (m): the explosion prefab is scaled radius / this.</summary>
        public const float CrashRadiusM = 5f;

        /// <summary>Explosion prefab scale = radius / <see cref="CrashRadiusM"/> (bounded 0.05..200); 1 under S7.M13 (radius ignored).</summary>
        public static float ExplosionScale(float radius, bool fixedScale)
        {
            if (fixedScale) return 1f;
            if (!(radius > 0) || float.IsInfinity(radius)) return 1f;
            return Math.Max(0.05f, Math.Min(200f, radius / CrashRadiusM));
        }

        /// <summary>Muzzle flash light under water: dimmed × 0.35, 60 ms (HYPOTHESIS look).</summary>
        public const float MuzzleUnderwaterDim = 0.35f;

        /// <summary>The grinder's surface effect repeats at this rate while it hits (10 Hz).</summary>
        public const float GrinderRepeatS = 0.1f;

        /// <summary>A sustain's fade-out after its Stop row or its lapsed lifetime (bubbles stop emitting, the loop fades).</summary>
        public const float SustainFadeS = 0.25f;

        // ------------------------------------------------------------------------------------- helpers

        public static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }

        public static ushort Ms(double ms)
        {
            if (double.IsNaN(ms) || ms <= 0) return 0;
            return ms >= 65535.0 ? (ushort)65535 : (ushort)Math.Round(ms);
        }

        public static bool Finite(float v, float bound)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= bound;
        }
    }
}
