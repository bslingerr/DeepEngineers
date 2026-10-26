using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7p-effects-under (ROADMAP "### S7p Polish pass" P2, P3, P4, P5, P6a
 // the S7 event row's free bits (CONTRACT §5.4 "S7p effects-under"), pinned on its own by the canonical text
 // "ext s7p fx 1" (layout 1.0 and "ext s7 look 1" unchanged: the row stays 64 bytes, every offset of the S7 text
 // holds; the thruster's flame colour and length scale live in the S7 pad at 0x3E, the drill's contact class in bits
 // 8-11 of `subtype`, whose low byte keeps its S7 meaning for every type; a thruster's subtype is untouched, since the
 // S7 and S7p readers switch on it), and the pure rules both plugins use: the flame on the wire (SE's own rule of
 // MyThrustFlameAnimator: colour = lerp(FlameIdleColor, FlameFullColor, strength / MAX_THRUST)), the drill contact
 // class, and Subnautica's look numbers (flame cone, the vapor plume where a flame meets the sea surface, the layered
 // explosion, muzzle/tracer/bubble streak, drill dust and sparks, light caps), the live scales of `de.s7p fx` and the
 // donor scoring that picks Subnautica's own particle materials at run time. Pure: no game, no Unity; replay-exact.
 // =================================================================================================================

 /// <summary>The S7p effect conventions (CONTRACT §5.4 "S7p effects-under"): one nested class per <c>const</c> group.</summary>
    public static class S7pFxConventions
    {
        /// <summary>The first line of the canonical text.</summary>
        public const string Header = "ext s7p fx 1";
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4 "S7p effects-under".</summary>
        public const ulong PinnedHash = 0xc83edaca61ce1545UL;
        /// <summary>Lines and bytes of the canonical text.</summary>
        public const int CanonicalLines = 35, CanonicalBytes = 2304;

        /// <summary>Drill / PrawnDrill rows: <c>subtype</c> bits 8–11 (what the bit touches).</summary>
        public static class DrillContact
        {
            public const byte None = 0;
            /// <summary>A voxel whose material type is rock-like (or unknown).</summary>
            public const byte Rock = 1;
            /// <summary>A voxel of sand, soil, grass, gravel or mud: the fine sediment cloud.</summary>
            public const byte Sediment = 2;
            /// <summary>A voxel of ice or snow.</summary>
            public const byte Ice = 3;
            /// <summary>A grid (blocks): sparks, no dust.</summary>
            public const byte Metal = 4;
            /// <summary>Anything else (a character, a floating object, an environment item).</summary>
            public const byte Other = 5;

            public static string Name(int c)
            {
                switch (c)
                {
                    case None: return "None";
                    case Rock: return "Rock";
                    case Sediment: return "Sediment";
                    case Ice: return "Ice";
                    case Metal: return "Metal";
                    case Other: return "Other";
                    default: return "c" + c.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        /// <summary>
 /// Thruster rows: <c>extra</c> (0x3E) = <c>class&lt;&lt;13 | r4&lt;&lt;9 | g5&lt;&lt;4 | b4</c>: the flame length scale class
 /// (1..7; 0 = no S7p flame values: an older SE) and SE's flame colour (green, the eye's channel, gets 5 bits).
 /// </summary>
        public static class FlameExtra
        {
            public const int ClassShift = 13, ClassBits = 3;
            public const int RShift = 9, RBits = 4;
            public const int GShift = 4, GBits = 5;
            public const int BBits = 4;
        }

        /// <summary>The <c>subtype</c> split for Drill / PrawnDrill rows: the S7 meaning in the low byte, the contact in bits 8–11.</summary>
        public static class SubtypeBits
        {
            public const int LowMask = 0xFF;
            public const int HighShift = 8;
            public const int ContactMask = 0xF;
        }

        /// <summary>Numbers of the S7p producer (SE).</summary>
        public static class S7pLimits
        {
            /// <summary>The flame length scale class c (1..7) is FlameLengthScale = (750 + 125·c) / 1000: 0.875 … 1.625.</summary>
            public const int LengthClassBaseMilli = 750, LengthClassStepMilli = 125;
            /// <summary>The object build pass default FlameLengthScale (1.15), used without a class.</summary>
            public const int LengthScaleDefaultMilli = 1150;
            /// <summary>SE's MyConstants.MAX_THRUST (1.5) in thousandths: the colour lerp divides the strength by it.</summary>
            public const int MaxThrustPermille = 1500;
            /// <summary>BulletStreak hit rays per SE tick (the rest keep the S7 length, counted).</summary>
            public const int StreakRaysPerTick = 16;
            /// <summary>The ray's Havok filter layer.</summary>
            public const int StreakRayLayer = 15;
            /// <summary>Voxel material lookups for the drill contact per SE tick (the rest keep their last class, counted).</summary>
            public const int ContactLookupsPerTick = 4;
            /// <summary>A drill sustain re-reads its voxel material every this many ticks.</summary>
            public const int ContactRefreshTicks = 30;
            /// <summary>The drill contact point is quantised to this grid in the row's frame (fewer Move rows).</summary>
            public const int ContactQuantumCm = 25;
            /// <summary>WeaponMuzzle <c>size</c>: the muzzle flash scale of a large-grid, small-grid and hand weapon.</summary>
            public const int MuzzleLargeCm = 100;
            public const int MuzzleSmallCm = 50;
            public const int MuzzleHandCm = 30;
        }

        /// <summary>The <c>field</c> line (the S7 text names the same bytes <c>pad</c>; S7p gives them their meaning).</summary>
        public static readonly string[] FieldLines = { "field event extra 0x3e 2 u16" };

        /// <summary>The <c>format</c> lines (sorted by key).</summary>
        public static readonly string[] FormatLines =
        {
            "format event.drill Drill and PrawnDrill: subtype = ToolSource | DrillContact<<HighShift (DrilledEntity: voxel by MaterialTypeName, grid Metal, other Other; None while not drilling); pos = DrilledEntityPoint quantised to ContactQuantumCm in the row frame while Hit, else the sensor FrontPoint",
            "format event.flame Thruster: extra = class<<ClassShift | round(r*15)<<RShift | round(g*31)<<GShift | round(b*15); class = clamp(round((FlameLengthScale*1000 - LengthClassBaseMilli) / LengthClassStepMilli), 1, 7), 0 = absent (the kind's colour, LengthScaleDefaultMilli); rgb = lerp(FlameIdleColor, FlameFullColor, clamp01(CurrentStrength / MAX_THRUST)).xyz clamped 0..1 (the definition's gamma numbers)",
            "format event.muzzle WeaponMuzzle: size = MuzzleLargeCm/100 for a large-grid weapon, MuzzleSmallCm/100 small grid, MuzzleHandCm/100 hand-held",
            "format event.streak BulletStreak: extra = round(distance*100) clamped 1..65535 (cm) to the first hit of MyPhysics.CastRay(origin, origin + dir*size, list, StreakRayLayer) not on the shooter, flag Hit; 0 = no hit or no ray (at most StreakRaysPerTick rays per tick on the update thread); size and lifetimeMs keep their S7 meaning",
            "format event.subtype bits 0-7 keep the S7 meaning for every type (read subtype & LowMask); bits 8-15 are 0 except Drill and PrawnDrill (DrillContact); extra is 0 for every type but Thruster and BulletStreak",
        };

        /// <summary>The <c>const</c> lines, built from the constants above, sorted by group (ordinal), value, name (ordinal).</summary>
        public static string[] ConstLines()
        {
            var l = new List<KeyValuePair<string, KeyValuePair<string, int>>>
            {
                C("DrillContact", "None", DrillContact.None),
                C("DrillContact", "Rock", DrillContact.Rock),
                C("DrillContact", "Sediment", DrillContact.Sediment),
                C("DrillContact", "Ice", DrillContact.Ice),
                C("DrillContact", "Metal", DrillContact.Metal),
                C("DrillContact", "Other", DrillContact.Other),
                C("FlameExtra", "ClassShift", FlameExtra.ClassShift),
                C("FlameExtra", "ClassBits", FlameExtra.ClassBits),
                C("FlameExtra", "RShift", FlameExtra.RShift),
                C("FlameExtra", "RBits", FlameExtra.RBits),
                C("FlameExtra", "GShift", FlameExtra.GShift),
                C("FlameExtra", "GBits", FlameExtra.GBits),
                C("FlameExtra", "BBits", FlameExtra.BBits),
                C("S7pLimits", "StreakRaysPerTick", S7pLimits.StreakRaysPerTick),
                C("S7pLimits", "StreakRayLayer", S7pLimits.StreakRayLayer),
                C("S7pLimits", "LengthClassBaseMilli", S7pLimits.LengthClassBaseMilli),
                C("S7pLimits", "LengthClassStepMilli", S7pLimits.LengthClassStepMilli),
                C("S7pLimits", "LengthScaleDefaultMilli", S7pLimits.LengthScaleDefaultMilli),
                C("S7pLimits", "ContactLookupsPerTick", S7pLimits.ContactLookupsPerTick),
                C("S7pLimits", "ContactQuantumCm", S7pLimits.ContactQuantumCm),
                C("S7pLimits", "ContactRefreshTicks", S7pLimits.ContactRefreshTicks),
                C("S7pLimits", "MuzzleHandCm", S7pLimits.MuzzleHandCm),
                C("S7pLimits", "MuzzleSmallCm", S7pLimits.MuzzleSmallCm),
                C("S7pLimits", "MuzzleLargeCm", S7pLimits.MuzzleLargeCm),
                C("S7pLimits", "MaxThrustPermille", S7pLimits.MaxThrustPermille),
                C("SubtypeBits", "ContactMask", SubtypeBits.ContactMask),
                C("SubtypeBits", "HighShift", SubtypeBits.HighShift),
                C("SubtypeBits", "LowMask", SubtypeBits.LowMask),
            };
            l.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Key, b.Key);
                if (c != 0) return c;
                c = a.Value.Value.CompareTo(b.Value.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Value.Key, b.Value.Key);
            });
            var lines = new string[l.Count];
            for (int i = 0; i < l.Count; i++)
                lines[i] = "const " + l[i].Key + " " + l[i].Value.Key + " " + l[i].Value.Value.ToString(CultureInfo.InvariantCulture);
            return lines;
        }

        private static KeyValuePair<string, KeyValuePair<string, int>> C(string group, string name, int value)
        {
            return new KeyValuePair<string, KeyValuePair<string, int>>(group, new KeyValuePair<string, int>(name, value));
        }

        /// <summary>The canonical text: the header, the const lines, the field line, the format lines; '\n' after each.</summary>
        public static string CanonicalText()
        {
            var sb = new StringBuilder(2048);
            sb.Append(Header).Append('\n');
            foreach (string s in ConstLines()) sb.Append(s).Append('\n');
            foreach (string s in FieldLines) sb.Append(s).Append('\n');
            foreach (string s in FormatLines) sb.Append(s).Append('\n');
            return sb.ToString();
        }

        private static ulong _hash;

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> (equals <see cref="PinnedHash"/> when nothing drifted).</summary>
        public static ulong Hash
        {
            get
            {
                if (_hash == 0) _hash = LayoutHash.Fnv1a64Ascii(CanonicalText());
                return _hash;
            }
        }
    }

    /// <summary>
 /// The S7p wire values of an S7 event row (<c>ext s7p fx 1</c>): the thruster's flame (colour and length scale class) in
 /// <c>extra</c>, the drill contact in bits 8–11 of <c>subtype</c>. An older SE writes 0 in both: Subnautica then uses the
 /// S7 defaults (the kind's vanilla colour, scale 1.15, contact None).
 /// </summary>
    public static class S7pFxCodec
    {
        /// <summary>
 /// The thruster's <c>extra</c>: <c>class&lt;&lt;13 | round(r·15)&lt;&lt;9 | round(g·31)&lt;&lt;4 | round(b·15)</c> (channels
 /// clamped 0..1, NaN = 0; the class of <paramref name="lengthScale"/>, 1..7, never 0 so the value is never absent).
 /// </summary>
        public static ushort EncodeFlame(float r, float g, float b, float lengthScale)
        {
            int c = LengthClass(lengthScale);
            int ri = Channel(r, 15), gi = Channel(g, 31), bi = Channel(b, 15);
            return (ushort)((c << S7pFxConventions.FlameExtra.ClassShift) | (ri << S7pFxConventions.FlameExtra.RShift) |
                            (gi << S7pFxConventions.FlameExtra.GShift) | bi);
        }

        /// <summary>False when <paramref name="v"/> carries no flame (class 0: an older SE or <c>colour</c> off); else the colour and the scale.</summary>
        public static bool TryDecodeFlame(ushort v, out float r, out float g, out float b, out float lengthScale)
        {
            r = g = b = 0f;
            lengthScale = S7pFxConventions.S7pLimits.LengthScaleDefaultMilli / 1000f;
            int c = (v >> S7pFxConventions.FlameExtra.ClassShift) & 0x7;
            if (c == 0) return false;
            r = ((v >> S7pFxConventions.FlameExtra.RShift) & 0xF) / 15f;
            g = ((v >> S7pFxConventions.FlameExtra.GShift) & 0x1F) / 31f;
            b = (v & 0xF) / 15f;
            lengthScale = LengthScaleOfClass(c);
            return true;
        }

        /// <summary>The class (1..7) of a FlameLengthScale: round((s·1000 − 750) / 125) clamped; ≤ 0 or NaN = the default's (1.15 → 3).</summary>
        public static int LengthClass(float lengthScale)
        {
            float s = float.IsNaN(lengthScale) || lengthScale <= 0f ? S7pFxConventions.S7pLimits.LengthScaleDefaultMilli / 1000f : lengthScale;
            double c = Math.Round((s * 1000.0 - S7pFxConventions.S7pLimits.LengthClassBaseMilli) / S7pFxConventions.S7pLimits.LengthClassStepMilli, MidpointRounding.AwayFromZero);
            return (int)Math.Max(1, Math.Min(7, c));
        }

        /// <summary>FlameLengthScale of a class (1..7): (750 + 125·c) / 1000; 0 = the default 1.15.</summary>
        public static float LengthScaleOfClass(int c)
        {
            if (c <= 0) return S7pFxConventions.S7pLimits.LengthScaleDefaultMilli / 1000f;
            return (S7pFxConventions.S7pLimits.LengthClassBaseMilli + S7pFxConventions.S7pLimits.LengthClassStepMilli * Math.Min(7, c)) / 1000f;
        }

        /// <summary>A thruster row's flame length scale (the default 1.15 without S7p values).</summary>
        public static float LengthScaleOf(ushort extra)
        {
            return LengthScaleOfClass((extra >> S7pFxConventions.FlameExtra.ClassShift) & 0x7);
        }

        private static int Channel(float v, int max)
        {
            if (float.IsNaN(v) || v <= 0f) return 0;
            if (v >= 1f) return max;
            return (int)Math.Round(v * max, MidpointRounding.AwayFromZero);
        }

        /// <summary>
 /// SE's flame colour (VERIFIED <c>Sandbox.RenderDirect.ActorComponents.MyThrustFlameAnimator</c>:
 /// <c>Vector4.Lerp(m_idleColor, m_fullColor, m_strength / MyConstants.MAX_THRUST)</c>): the lerp factor
 /// <paramref name="strength"/> / <paramref name="maxThrust"/> clamped to 0..1 (the vectors are the definition's
 /// <c>FlameIdleColor</c> / <c>FlameFullColor</c>; SE then converts to linear for its billboards, Subnautica takes the
 /// gamma numbers as a Unity colour).
 /// </summary>
        public static void FlameColour(float idleR, float idleG, float idleB, float fullR, float fullG, float fullB,
                                       float strength, float maxThrust, out float r, out float g, out float b)
        {
            float m = maxThrust > 0 && !float.IsInfinity(maxThrust) ? maxThrust : S7pFxConventions.S7pLimits.MaxThrustPermille / 1000f;
            float t = S7EventRules.Clamp01(strength / m);
            r = idleR + (fullR - idleR) * t;
            g = idleG + (fullG - idleG) * t;
            b = idleB + (fullB - idleB) * t;
        }

        /// <summary>The S7 meaning of any <c>subtype</c> (ThrusterKind, ToolSource, WeaponKind, …): the low byte.</summary>
        public static ushort Low(ushort subtype) { return (ushort)(subtype & S7pFxConventions.SubtypeBits.LowMask); }

        /// <summary>Drill / PrawnDrill <c>subtype</c>: the ToolSource in the low byte, the contact class in bits 8–11.</summary>
        public static ushort ToolSubtype(ushort source, byte contact)
        {
            return (ushort)((source & S7pFxConventions.SubtypeBits.LowMask) |
                            ((contact & S7pFxConventions.SubtypeBits.ContactMask) << S7pFxConventions.SubtypeBits.HighShift));
        }

        /// <summary>A drill row's contact class (bits 8–11).</summary>
        public static byte ContactOf(ushort subtype)
        {
            return (byte)((subtype >> S7pFxConventions.SubtypeBits.HighShift) & S7pFxConventions.SubtypeBits.ContactMask);
        }

        /// <summary>The contact class of a voxel material's <c>MaterialTypeName</c> (null or unknown: Rock).</summary>
        public static byte ContactOfMaterial(string materialTypeName)
        {
            if (string.IsNullOrEmpty(materialTypeName)) return S7pFxConventions.DrillContact.Rock;
            string m = materialTypeName.ToLowerInvariant();
            if (m.Contains("ice") || m.Contains("snow") || m.Contains("frost")) return S7pFxConventions.DrillContact.Ice;
            if (m.Contains("sand") || m.Contains("soil") || m.Contains("grass") || m.Contains("dirt") || m.Contains("mud") ||
                m.Contains("gravel") || m.Contains("moss") || m.Contains("silt") || m.Contains("desert")) return S7pFxConventions.DrillContact.Sediment;
            return S7pFxConventions.DrillContact.Rock;
        }

        /// <summary><paramref name="v"/> rounded to a multiple of <paramref name="quantum"/> (0 = unchanged).</summary>
        public static float Quantise(float v, float quantum)
        {
            if (!(quantum > 0) || float.IsNaN(v) || float.IsInfinity(v)) return v;
            return (float)(Math.Round(v / quantum, MidpointRounding.AwayFromZero) * quantum);
        }

        /// <summary>A BulletStreak's <c>extra</c>: the hit distance in centimetres (1..65535); ≤ 0 or NaN = 0 (no hit).</summary>
        public static ushort EncodeHitCm(float metres)
        {
            if (float.IsNaN(metres) || metres <= 0f) return 0;
            double cm = Math.Round(metres * 100.0, MidpointRounding.AwayFromZero);
            return (ushort)Math.Max(1.0, Math.Min(65535.0, cm));
        }

        /// <summary>
 /// The travel of a shot's tracer: from the muzzle to the hit (<paramref name="extra"/> cm, at most the S7 length
 /// <paramref name="size"/>) or the whole S7 length without one, at the bullet's speed (size / lifetimeMs, SE's
 /// DesiredSpeed): <paramref name="length"/> and its lifetime (ms, ≥ 1). False for a degenerate row.
 /// </summary>
        public static bool StreakTravel(float size, ushort lifetimeMs, ushort extra, out float length, out ushort travelMs)
        {
            length = 0f;
            travelMs = 0;
            if (!(size > 0f) || float.IsInfinity(size) || lifetimeMs == 0) return false;
            float hit = extra / 100f;
            length = extra != 0 ? Math.Min(size, hit) : size;
            travelMs = (ushort)Math.Max(1.0, Math.Round(lifetimeMs * (double)length / size, MidpointRounding.AwayFromZero));
            return true;
        }

        /// <summary>The muzzle flash scale of a weapon (m): large grid 1.0, small grid 0.5, hand-held 0.3.</summary>
        public static float MuzzleSize(bool onGrid, bool largeGrid)
        {
            int cm = !onGrid ? S7pFxConventions.S7pLimits.MuzzleHandCm : largeGrid ? S7pFxConventions.S7pLimits.MuzzleLargeCm : S7pFxConventions.S7pLimits.MuzzleSmallCm;
            return cm / 100f;
        }

        /// <summary>
 /// The vanilla flame colour of a kind (an older SE row without colour, or <c>colour</c> off): the definitions' gamma
 /// numbers of the stock thrusters (ion 0.2745/0.4090/0.6506; hydrogen idle 1/0.85/0.3 → full 1/0.75/0.2;
 /// atmospheric 0.3 grey; other: ion), lerped like <see cref="FlameColour"/>.
 /// </summary>
        public static void KindColour(ushort kind, float strength, out float r, out float g, out float b)
        {
            switch (kind & S7pFxConventions.SubtypeBits.LowMask)
            {
                case S7Conventions.ThrusterKind.Hydrogen:
                    FlameColour(1f, 0.85f, 0.3f, 1f, 0.75f, 0.2f, strength, 1.5f, out r, out g, out b);
                    return;
                case S7Conventions.ThrusterKind.Atmospheric:
                    r = g = b = 0.3f;
                    return;
                default:
                    r = 0.2745098f; g = 0.4090196f; b = 0.6505882f;
                    return;
            }
        }
    }

    /// <summary>One plume candidate (a thruster flame whose hot exhaust reaches the sea surface within the band).</summary>
    public struct S7pPlume
    {
        /// <summary>The flame's heat reaches the surface band.</summary>
        public bool Hit;
        /// <summary>Distance along the flame (m) of the point nearest the surface.</summary>
        public float T;
        /// <summary>Its distance to the surface (m; 0 = the flame crosses it).</summary>
        public float Distance;
        /// <summary>0..1: intensity × (1 − d/band)² × (1 − 0.5·t/reach).</summary>
        public float Density;
        /// <summary>The plume's point on the surface (SN x, z; y = 0).</summary>
        public float X, Z;
        /// <summary>The flame's own point is below the sea (boiling bubbles under the steam).</summary>
        public bool FromBelow;
    }

    /// <summary>The layered explosion of P4 (S7p): every number scaled by SE's radius, bounded.</summary>
    public struct S7pExplosionRecipe
    {
        public float FlashIntensity, FlashRange, FlashSeconds;
        /// <summary>Subnautica's own explosion prefab: the Seamoth destruction effect from this radius on, else the crashfish.</summary>
        public bool Destruction;
        public float PrefabScale;
        public int FireballCount;
        public float FireballSize, FireballSpeed, FireballLife;
        /// <summary>Above water: dark smoke; under water: a rising bubble cloud.</summary>
        public int SmokeCount;
        public float SmokeSize, SmokeSpeed, SmokeLife;
        public int RingCount;
        public float RingRadius, RingSpeed, RingLife, RingSize;
        public int SparkCount;
        public float SparkSpeed, SparkLife;
        public int BubbleCount;
        /// <summary>When the pooled layers are released.</summary>
        public float Seconds;
    }

    /// <summary>The gun of P5 (S7p): muzzle flash, tracer, bubble streak, impact.</summary>
    public struct S7pGunRecipe
    {
        public float FlashSize, FlashLife, LightIntensity, LightRange, LightSeconds;
        public int FlashCount, MuzzleSparks, MuzzleBubbles;
        public float TracerSpeed, TracerLife, TracerStretch, TracerWidth;
        public int StreakBubbles;
        public float StreakBubbleSize, StreakBubbleLife, StreakRise;
    }

    /// <summary>
 /// The look rules of S7p (Subnautica's side
 /// scaled live by <c>de.s7p fx &lt;type&gt; &lt;scale&gt;</c>). Pure; the tests pin every rule.
 /// </summary>
    public static class S7pEffectRules
    {
        // ------------------------------------------------------------------------------------- P2 the flame cone

 /// <summary>SE's flame length factor at the mean random draw: strength × 10 × U(0.9..1) × FlameLengthScale × radius.</summary>
        public const float SeFlameLengthFactor = 9.5f;
        /// <summary>A flame particle's lifetime (s): the cone's length = its speed × this.</summary>
        public const float FlameLifetimeS = 0.14f;
        /// <summary>Flame particles per second, ceiling per flame.</summary>
        public const float FlameMaxRate = 600f;

        /// <summary>The flame length (m): SE's own visible length (<c>MyThrustFlameAnimator</c>: strength × 10 × U(0.9..1) × FlameLengthScale × radius × grid scale) × <paramref name="tune"/>.</summary>
        public static float FlameLength(float intensity, float radius, float lengthScale, float tune)
        {
            if (!(radius > 0) || !(tune > 0) || float.IsInfinity(radius)) return 0f;
            float ls = lengthScale > 0 && !float.IsInfinity(lengthScale) ? lengthScale : 1.15f;
            return S7EventRules.Clamp01(intensity) * SeFlameLengthFactor * ls * radius * tune;
        }

        /// <summary>The flame's diameter at the nozzle (m): SE's thickness = radius × U(0.98..1.02) on each side.</summary>
        public static float FlameDiameter(float radius, float tune)
        {
            if (!(radius > 0) || !(tune > 0)) return 0f;
            return 2f * radius * Math.Min(2f, Math.Max(0.25f, tune));
        }

        /// <summary>Flame particle speed (m/s) so the cone reaches <paramref name="length"/> in <see cref="FlameLifetimeS"/>.</summary>
        public static float FlameSpeed(float length)
        {
            return length > 0 ? length / FlameLifetimeS : 0f;
        }

        /// <summary>Flame particles per second: (30 + 170·i) × clamp(radius / 0.5, 0.5, 6), at most <see cref="FlameMaxRate"/>; 0 when off.</summary>
        public static float FlameRate(float intensity, float radius, float tune)
        {
            if (!(tune > 0) || !(radius > 0)) return 0f;
            float i = S7EventRules.Clamp01(intensity);
            if (i <= 0f) return 0f;
            float k = Math.Min(6f, Math.Max(0.5f, radius / 0.5f));
            return Math.Min(FlameMaxRate, (30f + 170f * i) * k);
        }

        /// <summary>
 /// The flame light under water (intensity, range): 0.15 + 0.35·i, range clamp(0.8·L + 1, 1.5, 12), × the light scale.
 /// 6 + 1.4·i at up
 /// to 25 m, stacked one Unity light per thruster of a multi-thruster ship into a white blob around the hull.
 /// </summary>
        public static void FlameLight(float intensity, float length, float lightTune, out float lightIntensity, out float range)
        {
            float i = S7EventRules.Clamp01(intensity);
            float t = lightTune > 0 ? lightTune : 0f;
            lightIntensity = i > 0 ? (0.15f + 0.35f * i) * t : 0f;
            range = Math.Min(12f, Math.Max(1.5f, 0.8f * Math.Max(0f, length) + 1f));
        }

        /// <summary>Heat-shimmer particles per second (a quarter of the flame's).</summary>
        public static float ShimmerRate(float flameRate, float tune)
        {
            if (!(tune > 0) || !(flameRate > 0)) return 0f;
            return flameRate * 0.25f * Math.Min(4f, tune);
        }

        /// <summary>The colour scale ("glow") applied to the flame tint: rgb × g clamped to 1, alpha min(1, g).</summary>
        public static void Glow(float r, float g, float b, float glow, out float or, out float og, out float ob, out float oa)
        {
            float k = glow > 0 && !float.IsInfinity(glow) ? glow : 0f;
            or = Math.Min(1f, r * k);
            og = Math.Min(1f, g * k);
            ob = Math.Min(1f, b * k);
            oa = Math.Min(1f, k);
        }

        // ------------------------------------------------------------------------------------- P3 the vapor plume

 /// <summary>"A few metres": the flame's heat makes vapor when it comes this close to the sea surface (above or below).</summary>
        public const float PlumeBandM = 4f;
        /// <summary>The hot exhaust reaches beyond the visible flame by this factor.</summary>
        public const float PlumeReachFactor = 1.5f;
        /// <summary>Weaker candidates are none.</summary>
        public const float PlumeMinDensity = 0.03f;
        /// <summary>Plumes per mirrored grid and in all (the strongest kept; the rest counted dropped).</summary>
        public const int PlumePerEntity = 3, PlumeTotal = 12;

        /// <summary>
 /// The plume of one flame: origin (SN world) <paramref name="px"/>, <paramref name="py"/>, <paramref name="pz"/>,
 /// exhaust direction (unit) <paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>, the flame length,
 /// intensity and radius. The flame's reach (length × 1.5 + radius) either crosses SN y = 0 (distance 0, at the
 /// crossing) or comes closest to it at one of its ends; within <paramref name="band"/> metres the plume sits at that
 /// point on the surface with density intensity × (1 − d/band)² × (1 − 0.5·t/reach) (closer to the nozzle is hotter).
 /// </summary>
        public static S7pPlume Plume(float px, float py, float pz, float dx, float dy, float dz, float flameLength, float intensity, float radius, float band)
        {
            var h = new S7pPlume();
            float i = S7EventRules.Clamp01(intensity);
            if (i <= 0f || !(band > 0) || float.IsNaN(px + py + pz + dx + dy + dz)) return h;
            float reach = Math.Max(0.5f, Math.Max(0f, flameLength) * PlumeReachFactor + Math.Max(0f, radius));
            float endY = py + dy * reach;
            float t, d;
            if ((py <= 0f && endY >= 0f) || (py >= 0f && endY <= 0f))
            {
                t = Math.Abs(dy) > 1e-6f ? Math.Min(reach, Math.Max(0f, -py / dy)) : 0f;
                d = 0f;
            }
            else if (Math.Abs(py) <= Math.Abs(endY)) { t = 0f; d = Math.Abs(py); }
            else { t = reach; d = Math.Abs(endY); }
            if (d > band) return h;
            float f = 1f - d / band;
            float along = 1f - 0.5f * (t / reach);
            float density = i * f * f * along;
            if (density < PlumeMinDensity) return h;
            h.Hit = true;
            h.T = t;
            h.Distance = d;
            h.Density = Math.Min(1f, density);
            h.X = px + dx * t;
            h.Z = pz + dz * t;
            h.FromBelow = py < 0f;
            return h;
        }

        /// <summary>Steam particles per second of a plume: density × 50 × clamp(2·radius, 0.5, 6) × tune.</summary>
        public static float PlumeRate(float density, float radius, float tune)
        {
            if (!(tune > 0) || !(density > 0)) return 0f;
            return density * 50f * Math.Min(6f, Math.Max(0.5f, 2f * radius)) * Math.Min(4f, tune);
        }

        /// <summary>A steam particle's start size (m): clamp(2.5·radius, 0.8, 8) × (0.6 + 0.4·density).</summary>
        public static float PlumeSize(float radius, float density)
        {

            return Math.Min(4f, Math.Max(0.5f, 1.25f * radius)) * (0.6f + 0.4f * S7EventRules.Clamp01(density));
        }

        /// <summary>A surface spray (Subnautica's own surface splash) is pulsed while the density is at least this.</summary>
        public const float PlumeSprayDensity = 0.5f;
        /// <summary>Seconds between two spray pulses of one plume.</summary>
        public const float PlumeSprayEveryS = 0.6f;

        /// <summary>
 /// The caps: at most <paramref name="perEntity"/> plumes per entity and <paramref name="total"/> in all, the
 /// strongest first (density descending, then index ascending: deterministic). Marks <paramref name="take"/>;
 /// returns the number kept (the rest are the dropped count).
 /// </summary>
        public static int SelectPlumes(float[] density, ulong[] entity, int n, int perEntity, int total, bool[] take)
        {
            if (density == null || entity == null || take == null) throw new ArgumentNullException();
            if (n < 0 || n > density.Length || n > entity.Length || n > take.Length) throw new ArgumentOutOfRangeException("n");
            for (int i = 0; i < n; i++) take[i] = false;
            int kept = 0;
            var keptIdx = new int[Math.Max(0, Math.Min(total, n))]; // ≤ PlumeTotal ints: no per-entity dictionary per frame
            while (kept < keptIdx.Length)
            {
                int best = -1;
                for (int i = 0; i < n; i++)
                {
                    if (take[i] || !(density[i] > 0)) continue;
                    if (best >= 0 && !(density[i] > density[best])) continue;
                    int c = 0;
                    for (int k = 0; k < kept; k++) if (entity[keptIdx[k]] == entity[i]) c++;
                    if (c >= perEntity) continue;
                    best = i;
                }
                if (best < 0) break;
                take[best] = true;
                keptIdx[kept++] = best;
            }
            return kept;
        }

        // ------------------------------------------------------------------------------------- P4 the explosion

 /// <summary>From this radius on, Subnautica's Seamoth destruction effect is the explosion's prefab (else the crashfish's).</summary>
        public const float DestructionMinRadiusM = 4f;
        /// <summary>The Seamoth destruction effect's own radius (HYPOTHESIS: a Seamoth is ~6 m long).</summary>
        public const float DestructionRadiusM = 6f;

        /// <summary>
 /// The layered explosion for SE's radius (m), intensity (damage / 1,000) and the water side: a flash, Subnautica's
 /// own explosion prefab, a fireball, smoke (above) or a bubble cloud (under), a shock ring (2 × radius), debris
 /// sparks and (under water) a bubble burst. <paramref name="tune"/> scales sizes and speeds (√ on counts); 0 = the
 /// S7p layers off (the S7 flash and prefab stay).
 /// </summary>
        public static S7pExplosionRecipe Explosion(float radius, float intensity, bool underwater, float tune)
        {
            var e = new S7pExplosionRecipe();
            float r = float.IsNaN(radius) || radius <= 0 ? 1f : Math.Min(200f, Math.Max(0.5f, radius));
            float i = S7EventRules.Clamp01(intensity);
            e.Destruction = r >= DestructionMinRadiusM;
            e.PrefabScale = e.Destruction ? Math.Max(0.1f, Math.Min(40f, r / DestructionRadiusM)) : S7EventRules.ExplosionScale(r, false);
            e.FlashIntensity = Math.Min(12f, 8f * (underwater ? 0.6f : 1f) * (0.5f + 0.5f * i));
            e.FlashRange = Math.Min(120f, Math.Max(6f, 4f * r));
            e.FlashSeconds = Math.Min(0.8f, Math.Max(0.2f, 0.15f + 0.03f * r));
            if (!(tune > 0) || float.IsInfinity(tune)) { e.Seconds = e.FlashSeconds; return e; }
            float s = Math.Min(4f, tune);
            float cs = (float)Math.Sqrt(s);
            e.FireballLife = underwater ? Math.Min(0.8f, Math.Max(0.25f, 0.25f + 0.03f * r)) : Math.Min(1.5f, Math.Max(0.5f, 0.5f + 0.05f * r));
            e.FireballCount = Count((12f + 6f * r) * cs, 160);
            e.FireballSize = Math.Min(40f, Math.Max(0.4f, 0.7f * r)) * s;
            e.FireballSpeed = r * (underwater ? 1.2f : 2f) * s / e.FireballLife;
            e.SmokeLife = underwater ? Math.Min(8f, Math.Max(2.5f, 2.5f + 0.1f * r)) : Math.Min(12f, Math.Max(3f, 3f + 0.2f * r));
            e.SmokeCount = Count((10f + 4f * r) * cs, 200);
            e.SmokeSize = Math.Min(50f, Math.Max(0.5f, 0.8f * r)) * s;
            e.SmokeSpeed = Math.Min(15f, Math.Max(0.5f, 0.4f * r)) * s;
            e.RingLife = Math.Min(1.2f, Math.Max(0.3f, 0.3f + 0.02f * r));
            e.RingRadius = 2f * r * s;
            e.RingSpeed = e.RingRadius / e.RingLife;
            e.RingCount = Count(Math.Min(96f, 24f + 2f * r) * cs, 96);
            e.RingSize = Math.Min(20f, Math.Max(0.3f, 0.35f * r)) * s;
            e.SparkCount = Count((8f + 3f * r) * cs, 120);
            e.SparkSpeed = Math.Min(60f, 6f + 2f * r) * (underwater ? 0.5f : 1f) * s;
            e.SparkLife = underwater ? 0.5f : 1f;
            e.BubbleCount = underwater ? Count(Math.Max(10f, Math.Min(200f, 20f * r / S7EventRules.CrashRadiusM)) * cs, 200) : 0;
            e.Seconds = Math.Max(Math.Max(e.FireballLife, e.SmokeLife), Math.Max(e.RingLife, e.SparkLife)) + 0.5f;
            return e;
        }

        private static int Count(float v, int max)
        {
            if (float.IsNaN(v) || v <= 0) return 0;
            return (int)Math.Min(max, Math.Round(v, MidpointRounding.AwayFromZero));
        }

        // ------------------------------------------------------------------------------------- P5 the gun

 /// <summary>Bubble-streak bubbles per metre of the shot's path (one every 0.6 m) and their ceiling per shot.</summary>
        public const float StreakBubbleSpacingM = 0.6f;
        public const int StreakBubbleMax = 48;

        /// <summary>
 /// The gun's look for a muzzle size (m; <see cref="S7pFxCodec.MuzzleSize"/>), a streak length (m) and lifetime
 /// (SE: length / DesiredSpeed) and the water side; each part scaled by its own knob (0 = off): a bright 50 ms
 /// flash of glow particles and a light, a few forward sparks, a fine bubble puff under water; a stretched tracer
 /// flying muzzle → hit at the bullet's speed; a trail of fine bubbles along the path under water (never the
 /// diver's breath bubbles).
 /// </summary>
        public static S7pGunRecipe Gun(float muzzleSize, float streakLength, ushort streakLifetimeMs, bool underwater,
                                       float muzzleTune, float tracerTune, float streakTune, float lightTune)
        {
            var g = new S7pGunRecipe();
            float m = muzzleSize > 0 && !float.IsInfinity(muzzleSize) ? Math.Min(4f, muzzleSize) : 0.5f;
            if (muzzleTune > 0)
            {
                float k = Math.Min(4f, muzzleTune);
                g.FlashSize = Math.Min(6f, Math.Max(0.15f, 1.6f * m * k));
                g.FlashLife = 0.05f;
                g.FlashCount = 3;
                g.MuzzleSparks = Count(6f * Math.Min(2f, k), 16);
                g.MuzzleBubbles = underwater ? Count((4f + 4f * m) * Math.Min(2f, k), 16) : 0;
            }
            if (lightTune > 0)
            {
                g.LightIntensity = (underwater ? 3f : 5f) * Math.Min(4f, lightTune) * (muzzleTune > 0 ? 1f : 0.5f);
                g.LightRange = Math.Min(16f, Math.Max(3f, 8f * m));
                g.LightSeconds = 0.06f;
            }
            float len = streakLength > 0 && !float.IsInfinity(streakLength) ? streakLength : 0f;
            float life = Math.Max(0.03f, streakLifetimeMs / 1000f);
            if (tracerTune > 0 && len > 0)
            {
                g.TracerSpeed = len / life;
                g.TracerLife = len / g.TracerSpeed;
                g.TracerStretch = Math.Min(6f, Math.Max(1.2f, g.TracerSpeed * 0.01f)) * Math.Min(3f, tracerTune);
                g.TracerWidth = Math.Max(0.03f, 0.08f * Math.Min(3f, tracerTune) * Math.Max(0.5f, m));
            }
            if (streakTune > 0 && underwater && len > 0)
            {
                g.StreakBubbles = Count(len / StreakBubbleSpacingM * Math.Min(2f, streakTune), StreakBubbleMax);
                g.StreakBubbleSize = 0.06f;
                g.StreakBubbleLife = 2f;
                g.StreakRise = 0.35f;
            }
            return g;
        }

        /// <summary>Impact sparks by surface (S7 subtype 1 grid, 2 voxel, else other) × the knob; bubbles under water.</summary>
        public static void Impact(ushort surface, bool underwater, float impactTune, float sparksTune, out int sparks, out int bubbles)
        {
            int baseSparks = surface == 1 ? 8 : surface == 2 ? 4 : 2;
            sparks = impactTune > 0 && sparksTune > 0 ? Count(baseSparks * Math.Min(3f, sparksTune) * Math.Min(2f, impactTune), 24) : 0;
            bubbles = impactTune > 0 && underwater ? Count(6f * Math.Min(2f, impactTune), 16) : 0;
        }

        // ------------------------------------------------------------------------------------- P6a the drill

 /// <summary>The drill's light ceiling (the S7 look was "blinding"): intensity and range.</summary>
        public const float DrillLightMaxIntensity = 1f, DrillLightMaxRange = 4f;
        /// <summary>The ceiling applied to every light inside a borrowed impact/tool/explosion prefab.</summary>
        public const float BorrowedLightMaxIntensity = 1.5f, BorrowedLightMaxRange = 6f;

        /// <summary>Sediment/dust particles per second while the bit touches (0 otherwise): rock 18, sediment 26, ice 10, other 6, none 8, metal 0; × max(0.5, size) × tune.</summary>
        public static float DustRate(bool hit, byte contact, float size, float tune)
        {
            if (!hit || !(tune > 0)) return 0f;
            float b;
            switch (contact)
            {
                case S7pFxConventions.DrillContact.Rock: b = 18f; break;
                case S7pFxConventions.DrillContact.Sediment: b = 26f; break;
                case S7pFxConventions.DrillContact.Ice: b = 10f; break;
                case S7pFxConventions.DrillContact.Metal: b = 0f; break;
                case S7pFxConventions.DrillContact.Other: b = 6f; break;
                default: b = 8f; break;
            }
            return b * Math.Min(4f, Math.Max(0.5f, size)) * Math.Min(4f, tune);
        }

        /// <summary>Sparks per second while the bit touches: metal 30, rock 6, ice 3, other 2, none 3, sediment 0; × the knob.</summary>
        public static float SparkRate(bool hit, byte contact, float tune)
        {
            if (!hit || !(tune > 0)) return 0f;
            float b;
            switch (contact)
            {
                case S7pFxConventions.DrillContact.Metal: b = 30f; break;
                case S7pFxConventions.DrillContact.Rock: b = 6f; break;
                case S7pFxConventions.DrillContact.Ice: b = 3f; break;
                case S7pFxConventions.DrillContact.Sediment: b = 0f; break;
                case S7pFxConventions.DrillContact.Other: b = 2f; break;
                default: b = 3f; break;
            }
            return b * Math.Min(4f, tune);
        }

        /// <summary>The dust tint by contact (alpha-blended, never additive): rock grey-brown, sediment sandy, ice white.</summary>
        public static void DustColour(byte contact, out float r, out float g, out float b, out float a)
        {
            switch (contact)
            {
                case S7pFxConventions.DrillContact.Sediment: r = 0.62f; g = 0.55f; b = 0.42f; a = 0.6f; return;
                case S7pFxConventions.DrillContact.Ice: r = 0.85f; g = 0.9f; b = 0.95f; a = 0.5f; return;
                case S7pFxConventions.DrillContact.Rock: r = 0.42f; g = 0.38f; b = 0.33f; a = 0.55f; return;
                default: r = 0.45f; g = 0.42f; b = 0.38f; a = 0.5f; return;
            }
        }

        /// <summary>The drill's contact light: metal 0.8, rock 0.4, else 0; × the light scale; capped at <see cref="DrillLightMaxIntensity"/>, range 2.5 ≤ <see cref="DrillLightMaxRange"/>.</summary>
        public static void DrillLight(bool hit, byte contact, float lightTune, out float intensity, out float range)
        {
            float b = !hit ? 0f : contact == S7pFxConventions.DrillContact.Metal ? 0.8f : contact == S7pFxConventions.DrillContact.Rock ? 0.4f : 0f;
            intensity = CapIntensity(b * Math.Max(0f, lightTune), DrillLightMaxIntensity);
            range = Math.Min(DrillLightMaxRange, 2.5f);
        }

        public static float CapIntensity(float v, float cap)
        {
            if (float.IsNaN(v) || v <= 0f) return 0f;
            return Math.Min(v, cap);
        }

        public static float CapRange(float v, float cap)
        {
            if (float.IsNaN(v) || v <= 0f) return 0f;
            return Math.Min(v, cap);
        }

        // ------------------------------------------------------------------------------------- donors

 /// <summary>What a borrowed Subnautica particle system lends its material to.</summary>
        public enum DonorRole { Glow = 0, Smoke, Spark, Dust, Shimmer, Count }

        /// <summary>
 /// How well one of Subnautica's particle systems (found in a source prefab at run time) suits a role: its names
 /// (system, material, shader, texture; case-insensitive keywords), its blending and render mode, and the source's
 /// rank in the role's preference list (earlier = +). ≤ 0 = unsuitable. Glow wants additive fire/flare/glow; smoke
 /// and dust want alpha-blended smoke/steam/dust (never additive: the drill's "blinding" white-out); spark wants
 /// additive sparks, stretched; shimmer wants a distortion/refraction shader.
 /// </summary>
        public static int DonorScore(DonorRole role, int sourceRank, string psName, string material, string shader, string texture, bool additive, bool stretched)
        {
            string all = ((psName ?? "") + "|" + (material ?? "") + "|" + (texture ?? "")).ToLowerInvariant();
            string sh = (shader ?? "").ToLowerInvariant();
            int s = Math.Max(0, 10 - Math.Max(0, sourceRank)) * 2;
            switch (role)
            {
                case DonorRole.Glow:
                    if (!additive) return 0;
                    s += 10;
                    s += Any(all, 8, "flame", "fire", "flare", "glow", "flash", "burn", "core", "light");
                    s -= Any(all, 10, "smoke", "bubble", "dust", "spark", "debris", "rock", "drip");
                    if (stretched) s -= 5;
                    break;
                case DonorRole.Smoke:
                case DonorRole.Dust:
                    if (additive) return 0;
                    s += 10;
                    s += Any(all, 8, role == DonorRole.Smoke ? new[] { "smoke", "steam", "vapor", "vapour", "cloud", "fog", "mist" } : new[] { "dust", "sand", "sediment", "smoke", "cloud", "dirt" });
                    s -= Any(all, 10, "bubble", "spark", "drip", "debris", "chunk", "rock", "flare");
                    if (stretched) s -= 5;
                    break;
                case DonorRole.Spark:
                    if (!additive) return 0;
                    s += 6;
                    s += Any(all, 10, "spark", "ember", "weld", "metal", "debris");
                    s -= Any(all, 8, "smoke", "bubble", "dust", "glow", "flare");
                    if (stretched) s += 6;
                    break;
                case DonorRole.Shimmer:
                    if (!(sh.Contains("distort") || sh.Contains("refract") || sh.Contains("heat") || sh.Contains("haze") ||
                          all.Contains("distort") || all.Contains("refract") || all.Contains("heathaze"))) return 0;
                    s += 20;
                    break;
                default:
                    return 0;
            }
            return Math.Max(0, s);
        }

        /// <summary>
 /// The relaxed pass (a custom shader whose blending cannot be read): the role's own keywords in the system's,
 /// material's or texture's name (glow: flame, fire, flare, glow, flash, burn; smoke: smoke, steam, vapor, cloud,
 /// fog, mist; dust: dust, sand, sediment, smoke, dirt; spark: spark, ember, weld; shimmer: distort, refract, haze).
 /// </summary>
        public static bool DonorKeyword(DonorRole role, string psName, string material, string texture)
        {
            string all = ((psName ?? "") + "|" + (material ?? "") + "|" + (texture ?? "")).ToLowerInvariant();
            switch (role)
            {
                case DonorRole.Glow: return Any(all, 1, "flame", "fire", "flare", "glow", "flash", "burn") > 0;
                case DonorRole.Smoke: return Any(all, 1, "smoke", "steam", "vapor", "vapour", "cloud", "fog", "mist") > 0;
                case DonorRole.Dust: return Any(all, 1, "dust", "sand", "sediment", "smoke", "dirt") > 0;
                case DonorRole.Spark: return Any(all, 1, "spark", "ember", "weld") > 0;
                case DonorRole.Shimmer: return Any(all, 1, "distort", "refract", "haze") > 0;
                default: return false;
            }
        }

        private static int Any(string text, int weight, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++) if (text.Contains(keys[i])) return weight;
            return 0;
        }
    }

    /// <summary>
 /// The live scales of S7p (<c>de.s7p fx &lt;type&gt; &lt;scale&gt;</c>, environment <c>DEEPENGINEERS_S7P_FX</c>
 /// "type=scale,…" with the same defaults): one multiplier per look, 0 = that S7p layer off (the S7 look stays), and the
 /// switches <c>above</c> (draw the S7p looks above water too; off: scene-above owns the picture above water),
 /// <c>drillgame</c> (the drill uses Subnautica's own exoDrill/Prawn effect, one held instance, instead of the composed
 /// dust), and the SE producer's <c>colour</c>, <c>ray</c>, <c>contact</c> (1 on, 0 off). Thread-safe enough for one
 /// writer (the command) and per-frame readers (floats).
 /// </summary>
    public sealed class S7pFxSettings
    {
        public const string EnvVar = "DEEPENGINEERS_S7P_FX";

        public enum Knob
        {
            Flame = 0, Glow, Shimmer, Plume, Explosion, Muzzle, Tracer, Streak, Impact, Drill, Sparks, Light, Above, DrillGame, Colour, Ray, Contact, Count,
        }

        private static readonly string[] Names =
        {
            "flame", "glow", "shimmer", "plume", "explosion", "muzzle", "tracer", "streak", "impact", "drill", "sparks", "light", "above", "drillgame", "colour", "ray", "contact",
        };

        // glow 0.
 // hydrogen thruster) whited out the ship under water; 0.4 keeps the colour, not the blob (de.s7p fx glow <scale> to taste)
        private static readonly float[] Defaults =
        {
            // "): sizes/speeds ×2, counts ×1.4
 // 4 -> 0.25
 // light 1 -> 0.3 (every S7p Unity light: flame, muzzle flash, explosion flash, drill)
 // 25/0.3 still "too much" -> glow 0.2, light 0.2
 // 02:0x: "the closer I get the brighter", "the muzzle flash is too much again", the plume "like Roblox":
 // glow 0.15, plume 0.5, muzzle 0.5, light 0.15

 // the lights are tamed; the S7p spark recipe stays behind the knob
            1f, 0.15f, 1f, 0.5f, 2f, 0.5f, 1f, 1f, 1f, 1f, 0f, 0.15f, 0f, 0f, 1f, 1f, 1f,
        };

        /// <summary>The largest scale accepted (a multiplier; the rules clamp their own outputs too).</summary>
        public const double MaxScale = 10.0;

        private readonly float[] _v = new float[(int)Knob.Count];
        /// <summary>Increments on every change (rows and caches compare it).</summary>
        public int Version { get; private set; }

        public S7pFxSettings() { Reset(); }

        public static string NameOf(Knob k) { return Names[(int)k]; }

        public static bool TryParseKnob(string name, out Knob k)
        {
            k = Knob.Count;
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim().ToLowerInvariant();
            if (n == "color") n = "colour";
            if (n == "fire" || n == "flames" || n == "thruster") n = "flame";
            if (n == "vapor" || n == "vapour" || n == "steam") n = "plume";
            if (n == "gun") n = "muzzle";
            for (int i = 0; i < Names.Length; i++)
                if (Names[i] == n) { k = (Knob)i; return true; }
            return false;
        }

        public float this[Knob k] { get { return _v[(int)k]; } }

        public bool On(Knob k) { return _v[(int)k] > 0f; }

        public void Reset()
        {
            for (int i = 0; i < _v.Length; i++) _v[i] = Defaults[i];
            Version++;
        }

        /// <summary>Sets one scale (0..<see cref="MaxScale"/>; switches 0/1). False with a reason otherwise.</summary>
        public bool TrySet(Knob k, double value, out string error)
        {
            error = null;
            if ((int)k < 0 || k >= Knob.Count) { error = "unknown knob"; return false; }
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > MaxScale)
            {
                error = NameOf(k) + ": scale 0.." + MaxScale.ToString("0", CultureInfo.InvariantCulture) + " (0 = off)";
                return false;
            }
            if (IsSwitch(k)) value = value > 0 ? 1 : 0;
            _v[(int)k] = (float)value;
            Version++;
            return true;
        }

        /// <summary>above, drillgame, colour, ray, contact are switches (any value &gt; 0 = on).</summary>
        public static bool IsSwitch(Knob k)
        {
            return k == Knob.Above || k == Knob.DrillGame || k == Knob.Colour || k == Knob.Ray || k == Knob.Contact;
        }

        /// <summary>
 /// Applies "type=scale,type=scale" (also ';' or spaces; on/off/true/false for switches). Unknown names and bad values
 /// are skipped and returned as the error text (null when everything applied).
 /// </summary>
        public string ApplyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var errors = new List<string>();
            foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) { errors.Add("'" + part + "' is not type=scale"); continue; }
                Knob k;
                if (!TryParseKnob(part.Substring(0, eq), out k)) { errors.Add("unknown type '" + part.Substring(0, eq) + "'"); continue; }
                double v;
                if (!TryValue(part.Substring(eq + 1), out v)) { errors.Add(NameOf(k) + ": '" + part.Substring(eq + 1) + "' is not a number"); continue; }
                string e;
                if (!TrySet(k, v, out e)) errors.Add(e);
            }
            return errors.Count == 0 ? null : string.Join("; ", errors.ToArray());
        }

        /// <summary>A number, or on/off/true/false/yes/no (1/0).</summary>
        public static bool TryValue(string s, out double v)
        {
            v = 0;
            if (s == null) return false;
            string t = s.Trim().ToLowerInvariant();
            if (t == "on" || t == "true" || t == "yes") { v = 1; return true; }
            if (t == "off" || t == "false" || t == "no") { v = 0; return true; }
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>
 /// The <c>de.s7p fx …</c> tokens (after the verb): <c>[status]</c>, <c>reset</c>, <c>&lt;type&gt; &lt;scale&gt;</c>,
 /// <c>&lt;type&gt;=&lt;scale&gt; …</c>. Returns the action ("status", "reset", "set") or null with an error.
 /// </summary>
        public string Command(IList<string> tokens, out string error)
        {
            error = null;
            if (tokens == null || tokens.Count == 0) return "status";
            string a0 = (tokens[0] ?? "").Trim().ToLowerInvariant();
            if (a0 == "status" || a0 == "") return "status";
            if (a0 == "reset" || a0 == "default" || a0 == "defaults") { Reset(); return "reset"; }
            if (a0.IndexOf('=') > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < tokens.Count; i++) sb.Append(tokens[i]).Append(',');
                error = ApplyText(sb.ToString());
                return error == null ? "set" : null;
            }
            Knob k;
            if (!TryParseKnob(a0, out k)) { error = "unknown type '" + a0 + "' (" + string.Join(", ", Names) + ", status, reset)"; return null; }
            if (tokens.Count < 2) { error = "usage: de.s7p fx " + NameOf(k) + " <scale 0.." + MaxScale.ToString("0", CultureInfo.InvariantCulture) + ">"; return null; }
            double v;
            if (!TryValue(tokens[1], out v)) { error = NameOf(k) + ": '" + tokens[1] + "' is not a number"; return null; }
            return TrySet(k, v, out error) ? "set" : null;
        }

        /// <summary>
 /// The tokens of a <c>de.s7p</c> request that belong to the effects (<paramref name="mine"/>): the shared verb's
 /// other keys (above, hold, yawfix, …) are another S7p feature's, so a request without <c>fx</c> is not ours. Forms:
 /// <c>{argv: ["fx", …]}</c> (Subnautica's chat relay <c>/de sn s7p fx …</c>), <c>{fx: "flame 1.5"}</c>,
 /// <c>{fx: {flame: 1.5}}</c>, <c>{fx: "flame", action: "1.5"}</c> and <c>{action: "fx", flame: "1.5"}</c> (SE's
 /// chat <c>/de s7p fx flame 1.5</c> and <c>/de s7p fx flame=1.5</c> through the shared chat parser),
 /// <c>{action: "fx", type, scale}</c>.
 /// </summary>
        public static List<string> Tokens(object args, out bool mine)
        {
            mine = false;
            var t = new List<string>();
            var dict = args as IDictionary<string, object>;
            var list = args as IList<object>;
            object v;
            if (dict != null && dict.Count == 1 && dict.TryGetValue("argv", out v) && v is IList<object>) list = (IList<object>)v;
            if (list != null)
            {
                if (list.Count == 0 || !IsFx(list[0])) return t;
                mine = true;
                for (int i = 1; i < list.Count; i++) t.Add(Str(list[i]));
                return t;
            }
            if (dict == null) return t;
            if (dict.TryGetValue("fx", out v))
            {
                mine = true;
                var inner = v as IDictionary<string, object>;
                if (inner != null)
                {
                    var keys = new List<string>(inner.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (string k in keys) t.Add(k + "=" + Str(inner[k]));
                    return t;
                }
                t.AddRange(Str(v).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries));
                object x;
                if (dict.TryGetValue("scale", out x)) t.Add(Str(x));
                else if (dict.TryGetValue("action", out x) && !IsFx(x)) t.Add(Str(x));
                return t;
            }
            if (dict.TryGetValue("action", out v) && IsFx(v))
            {
                mine = true;
                object ty, sc;
                if (dict.TryGetValue("type", out ty))
                {
                    t.Add(Str(ty));
                    if (dict.TryGetValue("scale", out sc)) t.Add(Str(sc));
                    return t;
                }
                var keys = new List<string>(dict.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys) if (k != "action") t.Add(k + "=" + Str(dict[k]));
            }
            return t;
        }

        private static bool IsFx(object o) { return string.Equals(Str(o).Trim(), "fx", StringComparison.OrdinalIgnoreCase); }

        private static string Str(object o)
        {
            if (o == null) return "";
            if (o is bool) return (bool)o ? "1" : "0";
            if (o is double) return ((double)o).ToString("R", CultureInfo.InvariantCulture);
            if (o is float) return ((float)o).ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
        }

        /// <summary>"flame=1 glow=1 …" in knob order (rows, replies).</summary>
        public string Describe()
        {
            var sb = new StringBuilder(200);
            for (int i = 0; i < _v.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Names[i]).Append('=').Append(_v[i].ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>The knobs that differ from their defaults ("" when none): the short form for rows.</summary>
        public string Changed()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _v.Length; i++)
            {
                if (_v[i] == Defaults[i]) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Names[i]).Append('=').Append(_v[i].ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
