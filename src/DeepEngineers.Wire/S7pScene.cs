using System;
using System.Collections.Generic;
using System.Globalization;
using EventFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;
using ThrusterKind = DeepEngineers.Wire.S7Conventions.ThrusterKind;
using WeaponKind = DeepEngineers.Wire.S7Conventions.WeaponKind;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // CONTRACT §5.4
 // "S7p scene-above"): the shared conventions and the pure rules of the three items, used by both plugins and pinned
 // by Wire.Tests S7pSceneTests (no game or Unity type here, replay-exact, no allocation per call except the names).
 // P1 SE's bullets, missiles, tool sparks, thruster flames and explosions above the waterline under Site: route B
 // (the S7 event stream played by Subnautica with above-water variants; route A, SE's own transparent pass over
 // the picture, is impossible through the render hooks: MyRender11.FullDraw skips DrawScene as a whole and the
 // billboard/particle passes live inside DrawGameScene against MyGBuffer.Main) + missiles mirrored as S5
 // floating entities with the Missile GridAdd bit (Subnautica adds their trail).
 // P6b block subparts SE spins in the render thread (drill head, grinder blade, propeller, turbine, fans, H2 engine)
 // posed every tick with an integrated spin; the held tool's own subparts (hand drill spike, grinder disc).
 // P7 a projector's blueprint (MyProjectorBase.ProjectedGrid, an IsPreview grid) on the S5 grid stream with the
 // Projection GridAdd bit; blocks SE hides (|Dithering| ≥ 1) are never exported; Subnautica draws it with one
 // hologram material (Standard Fade, SE blue), no collision skin, no rooms, no lights.
 // Layout 1.0 and every pinned conventions text are unchanged: the two GridAdd bits are free bits of the S5 GridAdd
 // flags (0-2 S5, 3 S7a Ghost, nibble 28-31 version), the tool subpart keys are character part keys beyond 1-3.
 // =================================================================================================================

 /// <summary>The above-water effect route (<c>de.s7p {above}</c>, <c>DEEPENGINEERS_S7P_ABOVE</c>; default Events).</summary>
    public enum S7pAboveMode : byte
    {
        /// <summary>Subnautica plays the above-water variants of every event row without <c>Underwater</c>; missiles mirrored with their trail.</summary>
        Events = 0,
        /// <summary>The S7 behaviour above water (SE's own renderer, which the scene skip removes under the latch; Subnautica's flash lights only): kept to compare. Missiles still mirrored, trails under water only.</summary>
        Se = 1,
        /// <summary>Nothing of P1: no above-water variant, no missile mirror.</summary>
        Off = 2,
    }

    /// <summary>The S7p scene-above conventions (bits, keys, toggles, caps).</summary>
    public static class S7pScene
    {
        /// <summary>The live toggle verb (both plugins): <c>{above: events|se|off, projections: on|off, subparts: on|off}</c>.</summary>
        public const string Cmd = "de.s7p";
        public const string AboveEnvVar = "DEEPENGINEERS_S7P_ABOVE";
        public const string ProjectionsEnvVar = "DEEPENGINEERS_S7P_PROJECTIONS";
        public const string SubpartsEnvVar = "DEEPENGINEERS_S7P_SUBPARTS";

        /// <summary>GridAdd <c>flags</c> bit 4: the entity is a projector's blueprint (P7; hologram, no skin, no rooms, no lights).</summary>
        public const uint GridFlagProjection = 1u << 4;
        /// <summary>GridAdd <c>flags</c> bit 5 (with FloatingObject): the entity is an SE missile in flight (P1; Subnautica adds the trail).</summary>
        public const uint GridFlagMissile = 1u << 5;

        /// <summary>Character part keys of the held tool's subparts: <c>ToolSubpartBase | index</c> (class Tool, Dynamic, posed by C2 rows).</summary>
        public const ulong ToolSubpartBase = 0x7470000000000000UL; // "tp"
        public const int MaxToolSubparts = 8;

        // caps (every per-tick path is capped; the drops are counted in the s7p rows)
        public const int MaxMissiles = 24;
        public const double MissileRadiusM = 1500.0;
        public const int MaxProjections = 4;
        public const int MaxProjectedBlocks = 20000;
        public const int MaxSpinners = 1024;
        public const int MaxAboveSustains = 256;
        public const int MaxEmitsPerFrame = 768;
        public const int MaxMissileTrails = 24;

        public static bool IsProjection(uint gridFlags)
        {
            return MirrorConventions.Classify(gridFlags) == MirrorRecordVersion.Version1 && (gridFlags & GridFlagProjection) != 0;
        }

        public static bool IsMissile(uint gridFlags)
        {
            return MirrorConventions.Classify(gridFlags) == MirrorRecordVersion.Version1 && (gridFlags & GridFlagMissile) != 0 && (gridFlags & MirrorGridFlags.FloatingObject) != 0;
        }

        /// <summary>A version-1 GridAdd's flags with the S7p bits (a projection is a grid, a missile a floating object).</summary>
        public static uint GridFlags(uint s5Flags, bool projection, bool missile)
        {
            uint f = s5Flags;
            if (projection) f |= GridFlagProjection;
            if (missile) f |= GridFlagMissile | MirrorGridFlags.FloatingObject;
            return f;
        }

        public static ulong ToolSubpartKey(int index)
        {
            if (index < 0 || index >= MaxToolSubparts) throw new ArgumentOutOfRangeException("index", "0.." + (MaxToolSubparts - 1));
            return ToolSubpartBase | (ulong)(uint)index;
        }

        public static bool IsToolSubpartKey(ulong key) { return (key & 0xFFFF000000000000UL) == ToolSubpartBase && (key & 0xFFFFFFFFUL) < MaxToolSubparts; }

        // ------------------------------------------------------------------------------------------------ toggles

        public static bool TryParseAbove(string s, out S7pAboveMode m)
        {
            m = S7pAboveMode.Events;
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "events": case "event": case "on": case "sn": m = S7pAboveMode.Events; return true;
                case "se": m = S7pAboveMode.Se; return true;
                case "off": case "none": m = S7pAboveMode.Off; return true;
                default: return false;
            }
        }

        public static string Name(S7pAboveMode m)
        {
            switch (m)
            {
                case S7pAboveMode.Events: return "events";
                case S7pAboveMode.Se: return "se";
                default: return "off";
            }
        }

        /// <summary>The start value from the environment (unset or unknown: Events, <paramref name="fromEnv"/> false).</summary>
        public static S7pAboveMode AboveFromEnv(string raw, out bool fromEnv)
        {
            S7pAboveMode m;
            fromEnv = TryParseAbove(raw, out m) && !string.IsNullOrEmpty(raw);
            return fromEnv ? m : S7pAboveMode.Events;
        }

        /// <summary>on/off words (on, true, 1, yes / off, false, 0, no); false for anything else.</summary>
        public static bool TryParseOnOff(string s, out bool on)
        {
            on = false;
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "on": case "true": case "1": case "yes": on = true; return true;
                case "off": case "false": case "0": case "no": on = false; return true;
                default: return false;
            }
        }

        /// <summary>An on/off environment value; unset or unknown keeps <paramref name="def"/>.</summary>
        public static bool OnOffFromEnv(string raw, bool def)
        {
            bool on;
            return TryParseOnOff(raw, out on) ? on : def;
        }

        /// <summary>Subnautica draws the above-water variants (route B).</summary>
        public static bool PlaysAboveVariants(S7pAboveMode m) { return m == S7pAboveMode.Events; }

        /// <summary>SE mirrors its missiles in flight (bodies on the S5 stream).</summary>
        public static bool MirrorsMissiles(S7pAboveMode m) { return m != S7pAboveMode.Off; }

        /// <summary>Subnautica draws a missile's trail: every depth with Events, only under water with Se (SE never draws under the skip), never with Off.</summary>
        public static bool MissileTrail(S7pAboveMode m, bool underwater) { return m == S7pAboveMode.Events || (m == S7pAboveMode.Se && underwater); }

        /// <summary>
 /// The argument list of a <c>de.s7p</c> chat/relay call (<c>argv</c> words) as key/value pairs: <c>above events</c>,
 /// <c>above=se</c>, <c>projections off</c>, <c>subparts=on</c>, <c>status</c>; unknown words are returned in
 /// <paramref name="bad"/>.
 /// </summary>
        public static Dictionary<string, string> ParseArgv(IList<string> argv, out string bad)
        {
            bad = null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (argv == null) return d;
            for (int i = 0; i < argv.Count; i++)
            {
                string w = (argv[i] ?? "").Trim();
                if (w.Length == 0) continue;
                string key = w, val = null;
                int eq = w.IndexOf('=');
                if (eq > 0) { key = w.Substring(0, eq); val = w.Substring(eq + 1); }
                key = key.ToLowerInvariant();
                switch (key)
                {
                    case "above": case "projections": case "subparts":
                        if (val == null && i + 1 < argv.Count) val = argv[++i];
                        if (string.IsNullOrEmpty(val)) { bad = key + " needs a value"; return d; }
                        d[key] = val;
                        break;
                    case "status":
                    case "scene":
                        d["action"] = key;
                        break;
                    default:
                        bad = "unknown word '" + w + "'";
                        return d;
                }
            }
            return d;
        }

        public const string Usage = "de.s7p scene | above events|se|off | projections on|off | subparts on|off";

        /// <summary>The keys scene-above answers on the shared <c>de.s7p</c> verb (each S7p feature answers only its own keys).</summary>
        public static readonly string[] Keys = { "above", "projections", "subparts" };

        /// <summary>
 /// <c>de.s7p</c> is shared by the S7p features (CONTRACT §5.4): scene-above answers a request with one of its
 /// <see cref="Keys"/>, <c>action</c> <c>scene</c> or <c>status</c>, an argv whose words name one of those, or no
 /// argument at all (its status); anything else is another feature's (the next handler answers).
 /// </summary>
        public static bool IsMine(ICollection<string> dictKeys, string action, IList<string> argv)
        {
            if (action != null)
            {
                string a = action.Trim().ToLowerInvariant();
                if (a == "scene" || a == "status") return true;
            }
            if (dictKeys != null)
            {
                foreach (string k in Keys) if (dictKeys.Contains(k)) return true;
            }
            if (argv != null && argv.Count > 0)
            {
                foreach (string w in argv)
                {
                    string x = (w ?? "").Trim().ToLowerInvariant();
                    int eq = x.IndexOf('=');
                    if (eq > 0) x = x.Substring(0, eq);
                    if (x == "scene" || x == "status") return true;
                    foreach (string k in Keys) if (x == k) return true;
                }
                return false;
            }
            return (dictKeys == null || dictKeys.Count == 0) && action == null;
        }

        /// <summary>
 /// The chat words of <c>/de s7p …</c> as the C11 arguments every S7p handler reads: <c>k=v</c> → k: v; <c>k v</c>
 /// (a word followed by a word without '=') → k: v; a last lone word → action. So <c>above se</c>, <c>hold=0.5</c>,
 /// <c>scene</c>, <c>gameplay</c> reach the feature owning them.
 /// </summary>
        public static Dictionary<string, object> ChatArgs(IList<string> words)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            if (words == null) return d;
            for (int i = 0; i < words.Count; i++)
            {
                string w = (words[i] ?? "").Trim();
                if (w.Length == 0) continue;
                int eq = w.IndexOf('=');
                if (eq > 0) { d[w.Substring(0, eq)] = w.Substring(eq + 1); continue; }
                if (i + 1 < words.Count && (words[i + 1] ?? "").IndexOf('=') < 0 && (words[i + 1] ?? "").Trim().Length > 0) { d[w] = words[i + 1].Trim(); i++; continue; }
                d["action"] = w;
            }
            return d;
        }
    }

    // =====================================================================================================================
 // P1: the above-water looks (route B). Subnautica keeps one world-space particle system per look and emits through
 // explicit parameters; these recipes are its numbers (metres, seconds, linear colours).
 // =====================================================================================================================

 /// <summary>The particle families Subnautica keeps (one system each; additive: Flash, Fire, Spark, Tracer; alpha: Smoke, Dust, Vapor).</summary>
    public enum S7pLook : byte
    {
        Flash = 0, Fire = 1, Smoke = 2, Spark = 3, Tracer = 4, Dust = 5, Vapor = 6,
    }

    /// <summary>One emission: <see cref="Count"/> particles of a look at the event point.</summary>
    public struct S7pPuff
    {
        public S7pLook Look;
        public int Count;
        /// <summary>Start size (m).</summary>
        public float Size;
        /// <summary>Start speed along the cone around the event direction (m/s); 0 = still.</summary>
        public float Speed;
        /// <summary>Cone spread 0 (along the direction).. 1 (every direction).</summary>
        public float Spread;
        /// <summary>Lifetime (s).</summary>
        public float Life;
        /// <summary>Extra upward velocity (m/s; smoke rising).</summary>
        public float Up;
        /// <summary>Random start offset radius (m).</summary>
        public float Jitter;
        /// <summary>Linear colour and alpha (vertex colour; additive looks multiply by the alpha).</summary>
        public float R, G, B, A;
        /// <summary>Velocity only in the plane perpendicular to the direction (an explosion's shock ring).</summary>
        public bool Ring;
    }

    /// <summary>A sustain's emission per second for one look (the player accumulates fractional counts per frame).</summary>
    public struct S7pRate
    {
        public S7pPuff Puff;
        /// <summary>Particles per second (Puff.Count is ignored).</summary>
        public float PerSecond;
    }

    /// <summary>P1 route B: which rows Subnautica plays above water and how (the numbers of each above-water variant).</summary>
    public static class S7pAboveRules
    {
        public const int MaxPuffs = 6;
        public const int MaxRates = 3;
        /// <summary>The tracer speed when the row carries none (m/s).</summary>
        public const float TracerSpeed = 420f;
        /// <summary>The largest explosion radius the look scales with (m).</summary>
        public const float ExplosionMaxRadius = 60f;

        /// <summary>
 /// The row is played above water by S7p: route Events, a valid type, no <c>Underwater</c> flag (the waterline is the
 /// picture's: SN y = 0, set by SE's <c>S7EventRules.WaterFlags</c>), not a Stop of a type S7p does not hold, and not
 /// a Splash (the S7 effect player keeps the surface splash).
 /// </summary>
        public static bool Plays(S7pAboveMode mode, byte type, byte flags)
        {
            if (!S7pScene.PlaysAboveVariants(mode) || !EventType.IsValid(type) || type == EventType.Splash) return false;
            if ((flags & EventFlags.Stop) != 0) return true; // a Stop always ends the sustain S7p may hold
            return (flags & EventFlags.Underwater) == 0;
        }

        /// <summary>The sustain types (keyed rows): thrusters, tools, ship drills; HullBreak becomes a timed jet.</summary>
        public static bool IsSustainType(byte type)
        {
            switch (type)
            {
                case EventType.Thruster: case EventType.Welder: case EventType.Grinder: case EventType.Drill:
                case EventType.Repair: case EventType.PrawnDrill:
                    return true;
                default: return false;
            }
        }

        public static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : (float.IsNaN(v) ? lo : v); }

        /// <summary>The flame colour of a thruster kind (SE's default flame tints: ion blue, hydrogen orange-white, atmospheric dim warm).</summary>
        public static void ThrusterColour(ushort kind, out float r, out float g, out float b, out float a)
        {
            switch (kind)
            {
                case ThrusterKind.Ion: r = 0.42f; g = 0.72f; b = 1.0f; a = 0.85f; return;
                case ThrusterKind.Hydrogen: r = 1.0f; g = 0.62f; b = 0.30f; a = 0.9f; return;
                case ThrusterKind.Atmospheric: r = 1.0f; g = 0.74f; b = 0.46f; a = 0.30f; return;
                default: r = 1.0f; g = 0.80f; b = 0.60f; a = 0.7f; return;
            }
        }

        /// <summary>SE's default <c>FlameLengthScale</c> (the length a flame without S7p values is drawn with).</summary>
        public const float FlameLengthDefault = 1.15f;

        /// <summary>
 /// The above-water flame colour and length scale of a thruster row: SE's own colour and FlameLengthScale class when
 /// the row carries them (S7p-effects-under's <c>extra</c> at 0x3E, <see cref="S7pFxCodec.TryDecodeFlame"/>: the same
 /// tint the under-water flame takes, so the colour is SE's above and below), else the kind's
 /// (<see cref="ThrusterColour"/>, scale <see cref="FlameLengthDefault"/>). Alpha stays the kind's.
 /// </summary>
        public static void FlameColour(ushort kind, ushort extra, out float r, out float g, out float b, out float a, out float lengthScale)
        {
            ThrusterColour((ushort)(kind & 0xFF), out r, out g, out b, out a);
            lengthScale = FlameLengthDefault;
            float er, eg, eb, ls;
            if (extra != 0 && S7pFxCodec.TryDecodeFlame(extra, out er, out eg, out eb, out ls))
            {
                r = er; g = eg; b = eb;
                lengthScale = ls;
            }
        }

        private static S7pPuff P(S7pLook look, int count, float size, float speed, float spread, float life, float r, float g, float b, float a, float up = 0f, float jitter = 0f, bool ring = false)
        {
            return new S7pPuff { Look = look, Count = count, Size = size, Speed = speed, Spread = spread, Life = life, R = r, G = g, B = b, A = a, Up = up, Jitter = jitter, Ring = ring };
        }

        /// <summary>
 /// The one-shot look of a row above water into <paramref name="into"/> (≥ <see cref="MaxPuffs"/>); the count of
 /// puffs (0: nothing for this type). <paramref name="lifetimeMs"/> is the row's; <paramref name="size"/> the row's
 /// (streak length, explosion radius, impact size).
 /// </summary>
 /// <summary>
 /// R4: the row's <paramref name="extra"/> and <paramref name="flags"/> too — a BulletStreak
 /// with the Hit flag and a hit distance (extra, cm) flies only to the hit, at the bullet's speed
 /// (<see cref="S7pFxCodec.StreakTravel"/>), instead of the whole S7 length through the target.
 /// </summary>
        public static int OneShot(byte type, ushort subtype, float intensity, float size, ushort lifetimeMs, ushort extra, byte flags, S7pPuff[] into)
        {
            if (type == EventType.BulletStreak && (flags & EventFlags.Hit) != 0 && extra != 0)
            {
                float length;
                ushort travelMs;
                if (S7pFxCodec.StreakTravel(size, lifetimeMs, extra, out length, out travelMs))
                {
                    size = length;
                    lifetimeMs = travelMs;
                }
            }
            return OneShot(type, subtype, intensity, size, lifetimeMs, into);
        }

        /// </summary>
        public const float ExplosionScaleAbove = 2f;

        public static int OneShot(byte type, ushort subtype, float intensity, float size, ushort lifetimeMs, S7pPuff[] into)
        {
            if (into == null || into.Length < MaxPuffs) throw new ArgumentException("need " + MaxPuffs + " puffs", "into");
            subtype = (ushort)(subtype & 0xFF); // S7p-effects-under's format event.subtype: bits 0-7 keep the S7 meaning
            float i = Clamp(intensity, 0f, 1f);
            int n = 0;
            switch (type)
            {
                case EventType.WeaponMuzzle:
                    if (subtype == WeaponKind.Missile)
                    {
                        into[n++] = P(S7pLook.Flash, 1, 2.2f, 0f, 0f, 0.10f, 1f, 0.78f, 0.45f, 1f);
                        into[n++] = P(S7pLook.Fire, 6, 0.7f, 4f, 0.35f, 0.25f, 1f, 0.6f, 0.25f, 0.9f);
                        into[n++] = P(S7pLook.Smoke, 10, 0.9f, 2.5f, 0.6f, 2.2f, 0.62f, 0.62f, 0.62f, 0.55f, 0.4f, 0.3f);
                    }
                    else
                    {
                        into[n++] = P(S7pLook.Flash, 1, 0.9f, 0f, 0f, 0.05f, 1f, 0.85f, 0.55f, 1f);
                        into[n++] = P(S7pLook.Spark, 4, 0.05f, 16f, 0.25f, 0.14f, 1f, 0.8f, 0.45f, 1f);
                        into[n++] = P(S7pLook.Smoke, 2, 0.35f, 1.2f, 0.3f, 0.9f, 0.7f, 0.7f, 0.7f, 0.35f, 0.3f, 0.05f);
                    }
                    break;
                case EventType.BulletStreak:
                {
                    // a moving tracer: one stretched particle at the muzzle flying along the shot for the streak's lifetime
                    float life = Clamp(lifetimeMs / 1000f, 0.05f, 0.6f);
                    float len = Clamp(size, 1f, S7Conventions.EventLimits.StreakMaxM);
                    float speed = Clamp(len / life, 60f, 900f);
                    into[n++] = P(S7pLook.Tracer, 1, 0.07f + 0.05f * i, speed, 0f, life, 1f, 0.82f, 0.5f, 0.9f);
                    break;
                }
                case EventType.Impact:
                    if (subtype == 2) // voxel: dust and a few sparks
                    {
                        into[n++] = P(S7pLook.Dust, 4, 0.45f, 1.2f, 0.7f, 1.4f, 0.55f, 0.5f, 0.42f, 0.45f, 0.2f, 0.1f);
                        into[n++] = P(S7pLook.Spark, 3, 0.04f, 6f, 0.6f, 0.25f, 1f, 0.75f, 0.4f, 1f);
                    }
                    else
                    {
                        into[n++] = P(S7pLook.Spark, 10, 0.045f, 7f, 0.55f, 0.35f, 1f, 0.72f, 0.35f, 1f);
                        into[n++] = P(S7pLook.Flash, 1, 0.35f, 0f, 0f, 0.05f, 1f, 0.8f, 0.5f, 0.8f);
                        into[n++] = P(S7pLook.Dust, 2, 0.3f, 0.8f, 0.6f, 0.9f, 0.5f, 0.5f, 0.5f, 0.35f, 0.2f, 0.05f);
                    }
                    break;
                case EventType.Explosion:
                {
                    // " - the above-water blast draws at twice SE's radius
                    float r = Clamp(size * ExplosionScaleAbove, 0.5f, ExplosionMaxRadius);
                    float k = 0.6f + 0.4f * i;
                    into[n++] = P(S7pLook.Flash, 1, 2.4f * r, 0f, 0f, 0.14f, 1f, 0.8f, 0.5f, 1f);
                    into[n++] = P(S7pLook.Fire, (int)Clamp(10f + 6f * r, 10f, 90f), 0.55f * r * k, 1.3f * r, 1f, 0.9f, 1f, 0.55f, 0.2f, 0.95f, 0.4f, 0.25f * r);
                    into[n++] = P(S7pLook.Smoke, (int)Clamp(8f + 4f * r, 8f, 70f), 0.8f * r, 0.45f * r, 1f, 3.6f, 0.22f, 0.21f, 0.2f, 0.6f, 1.2f, 0.35f * r);
                    into[n++] = P(S7pLook.Spark, (int)Clamp(16f + 6f * r, 16f, 120f), 0.06f + 0.01f * r, 4f * r + 10f, 1f, 1.3f, 1f, 0.7f, 0.3f, 1f);
                    into[n++] = P(S7pLook.Vapor, 28, 0.35f * r, 3.2f * r, 0f, 0.55f, 0.95f, 0.93f, 0.9f, 0.35f, 0f, 0.15f * r, true);
                    break;
                }
                case EventType.GridImpact:
                {
                    float s = Clamp(size, 0.2f, 6f);
                    into[n++] = P(S7pLook.Spark, (int)(4 + 22 * i), 0.05f, 5f + 6f * i, 0.65f, 0.45f, 1f, 0.7f, 0.32f, 1f, 0f, 0.2f * s);
                    into[n++] = P(S7pLook.Dust, (int)(1 + 5 * i), 0.4f * s, 1f, 0.8f, 1.6f, 0.52f, 0.5f, 0.47f, 0.4f, 0.3f, 0.2f * s);
                    break;
                }
                case EventType.HullBreak:
                    into[n++] = P(S7pLook.Vapor, 18, 0.6f, 7f, 0.25f, 1.1f, 0.95f, 0.97f, 1f, 0.45f, 0f, 0.1f);
                    break;
            }
            return n;
        }

        /// <summary>
 /// A sustain's emission rates above water into <paramref name="into"/> (≥ <see cref="MaxRates"/>); the count. Thrusters:
 /// a flame along the row's direction (the flame dummy's), its colour by kind, rate ∝ intensity × size;
 /// welder/repair: blue-white sparks with a flicker; grinder: an orange fan while on something; drills: dust and
 /// sparks while hitting.
 /// </summary>
 /// </summary>
        public const float IdleFloor = 0.3f;

        public static int Sustain(byte type, ushort subtype, float intensity, float size, bool hit, S7pRate[] into, ushort extra = 0)
        {
            if (into == null || into.Length < MaxRates) throw new ArgumentException("need " + MaxRates + " rates", "into");
            subtype = (ushort)(subtype & 0xFF);
            float i = Clamp(intensity, 0f, 1f);
            float s = Clamp(size, 0.05f, 12f);
            int n = 0;
            switch (type)
            {
                case EventType.Thruster:
                {
                    if (i <= 0f) return 0;
                    float r, g, b, a, lengthScale;
                    FlameColour(subtype, extra, out r, out g, out b, out a, out lengthScale);

                    // SE's own idle flame is a visible soft cone; the geometry uses a floor (IdleFloor) under the strength
                    float v = IdleFloor + (1f - IdleFloor) * i;
                    float speed = (5f + 18f * v) * Clamp(s, 0.3f, 4f) * Clamp(lengthScale / FlameLengthDefault, 0.5f, 2f);
                    float life = 0.08f + 0.10f * v;

                    float rate = Clamp((40f + 220f * v * Clamp(s, 0.3f, 4f)) * 1.5f, 0f, 600f);
                    if (subtype == ThrusterKind.Atmospheric) rate *= 0.35f;
                    into[n++] = new S7pRate { PerSecond = rate, Puff = P(S7pLook.Fire, 0, s * (0.33f + 0.36f * v), speed, 0.08f, life, r, g, b, a) };
                    if (subtype == ThrusterKind.Hydrogen && i > 0.5f)
                        into[n++] = new S7pRate { PerSecond = 12f * i, Puff = P(S7pLook.Vapor, 0, s * 0.7f, speed * 0.25f, 0.2f, 0.9f, 0.9f, 0.9f, 0.92f, 0.06f, 0.3f, 0.1f * s) };
                    break;
                }
                case EventType.Welder:
                case EventType.Repair:
                    into[n++] = new S7pRate { PerSecond = hit ? 45f : 10f, Puff = P(S7pLook.Spark, 0, 0.035f, 3.2f, 0.9f, 0.32f, 0.72f, 0.86f, 1f, 1f) };
                    into[n++] = new S7pRate { PerSecond = hit ? 14f : 4f, Puff = P(S7pLook.Flash, 0, 0.22f, 0f, 0f, 0.05f, 0.7f, 0.85f, 1f, 0.9f) };
                    break;
                case EventType.Grinder:
                    if (!hit) return 0;
                    into[n++] = new S7pRate { PerSecond = 120f, Puff = P(S7pLook.Spark, 0, 0.04f, 8f, 0.45f, 0.45f, 1f, 0.62f, 0.22f, 1f) };
                    into[n++] = new S7pRate { PerSecond = 6f, Puff = P(S7pLook.Flash, 0, 0.18f, 0f, 0f, 0.05f, 1f, 0.7f, 0.35f, 0.7f) };
                    break;
                case EventType.Drill:
                case EventType.PrawnDrill:
                    if (!hit) return 0;
                    into[n++] = new S7pRate { PerSecond = type == EventType.PrawnDrill ? 30f : 18f, Puff = P(S7pLook.Dust, 0, 0.35f * Clamp(s, 0.5f, 3f), 1.5f, 0.8f, 1.6f, 0.5f, 0.45f, 0.38f, 0.5f, 0.25f, 0.15f) };
                    into[n++] = new S7pRate { PerSecond = 14f, Puff = P(S7pLook.Spark, 0, 0.035f, 5f, 0.7f, 0.3f, 1f, 0.75f, 0.4f, 1f) };
                    break;
            }
            return n;
        }

        /// <summary>The missile trail per second (Events at every depth, Se under water only): flame and smoke above, a bubble stream below.</summary>
        public static int MissileTrail(bool underwater, S7pRate[] into)
        {
            if (into == null || into.Length < MaxRates) throw new ArgumentException("need " + MaxRates + " rates", "into");
            int n = 0;
            if (underwater)
            {
                into[n++] = new S7pRate { PerSecond = 60f, Puff = P(S7pLook.Vapor, 0, 0.18f, 1.2f, 0.4f, 1.6f, 0.85f, 0.92f, 0.95f, 0.5f, 1.4f, 0.1f) };
                into[n++] = new S7pRate { PerSecond = 40f, Puff = P(S7pLook.Fire, 0, 0.35f, 2f, 0.1f, 0.06f, 1f, 0.7f, 0.4f, 0.6f) };
            }
            else
            {
                into[n++] = new S7pRate { PerSecond = 90f, Puff = P(S7pLook.Fire, 0, 0.55f, 3f, 0.1f, 0.10f, 1f, 0.72f, 0.35f, 1f) };
                into[n++] = new S7pRate { PerSecond = 40f, Puff = P(S7pLook.Smoke, 0, 0.6f, 0.6f, 0.4f, 2.6f, 0.68f, 0.68f, 0.68f, 0.5f, 0.3f, 0.1f) };
            }
            return n;
        }

        /// <summary>
 /// Whole particles to emit this frame from a rate (<paramref name="carry"/> keeps the fraction between frames;
 /// a non-positive rate or dt clears it). Capped at <paramref name="max"/> (the excess is counted by the caller).
 /// </summary>
        public static int Accumulate(float perSecond, float dt, ref float carry, int max)
        {
            if (!(perSecond > 0f) || !(dt > 0f)) { carry = 0f; return 0; }
            float x = carry + perSecond * Math.Min(dt, 0.1f);
            int n = (int)Math.Floor(x);
            carry = x - n;
            if (n < 0) n = 0;
            return n > max ? max : n;
        }

        /// <summary>The per-frame emission budget: what is left of <see cref="S7pScene.MaxEmitsPerFrame"/>; <paramref name="capped"/> counts the cut.</summary>
        public static int Budget(int wanted, ref int left, ref long capped)
        {
            if (wanted <= 0) return 0;
            if (left <= 0) { capped += wanted; return 0; }
            int give = wanted <= left ? wanted : left;
            capped += wanted - give;
            left -= give;
            return give;
        }
    }

    // =====================================================================================================================
 // P6b: spinning block subparts. SE spins these in the render thread only (a rotation animator fed by a speed; the
 // entity's local matrix never moves), so the S5 motion detector never sees them: the producer integrates the spin
 // from the same speed and composes it into the subpart's C2 row every tick.
 // =====================================================================================================================

    public enum S7pSpinAxis : byte { X = 0, Y = 1, Z = 2 }

    /// <summary>Where a spinner's speed is read and how it spins.</summary>
    public struct S7pSpinner
    {
        /// <summary>The subpart render component's type name suffix (<c>Declaring+Nested</c>).</summary>
        public string RenderType;
        /// <summary>The private float holding the target speed (rad/s).</summary>
        public string SpeedField;
        /// <summary>The field lives on the owning block's render component, not on the subpart's.</summary>
        public bool OnBlockRender;
        public S7pSpinAxis Axis;
        /// <summary>Spin-up / spin-down rates (rad/s²); 0 = relative: |target| / <see cref="S7pSpinRules.RelativeSpinS"/>.</summary>
        public double Up, Down;
    }

    /// <summary>The spin integrator and the spinner table (P6b).</summary>
    public static class S7pSpinRules
    {
        /// <summary>A relative spinner reaches its target in this time (the drill head's <c>speed / 0.5</c>).</summary>
        public const double RelativeSpinS = 0.5;
        /// <summary>At most this angle per 60 Hz tick (Subnautica slerps between rows: below π the shortest path is the true one).</summary>
        public const double MaxRadPerTick = 0.9 * Math.PI;
        public const double MaxOmega = MaxRadPerTick * 60.0;

        /// <summary>
 /// The render components whose subparts SE spins: the ship drill head
 /// (<c>MyShipDrillRenderComponent+MyDrillHeadRenderComponent</c>, <c>m_speed</c>, AxisZ, spin speed/0.5), the ship grinder
 /// blade (<c>MyRenderComponentShipGrinder+MyRenderComponentShipGrinderBlade</c>, <c>m_speed</c>, AxisX, 41.8879 / 20.94395),
 /// the atmospheric thruster propeller (<c>MyRenderComponentThrust+MyPropellerRenderComponent</c>, the block render's
 /// <c>m_propellerSpeed</c>, AxisZ), the wind turbine (<c>MyRenderComponentWindTurbine+TurbineRenderComponent</c>, <c>m_speed</c>,
 /// AxisY), the air vent wall fan (<c>MyRenderComponentAirVent+MyWallFanRenderComponent</c>, <c>m_speed</c> of its generic base,
 /// AxisX) and the hydrogen engine rotor (<c>MyRenderComponentHydrogenEngine+MyRotatingSubpartRenderComponent</c>,
 /// <c>m_speed</c> of its generic base, AxisZ).
 /// </summary>
        public static readonly S7pSpinner[] SpinTable =
        {
            new S7pSpinner { RenderType = "MyShipDrillRenderComponent+MyDrillHeadRenderComponent", SpeedField = "m_speed", Axis = S7pSpinAxis.Z },
            new S7pSpinner { RenderType = "MyRenderComponentShipGrinder+MyRenderComponentShipGrinderBlade", SpeedField = "m_speed", Axis = S7pSpinAxis.X, Up = 41.8879, Down = 20.94395 },
            new S7pSpinner { RenderType = "MyRenderComponentThrust+MyPropellerRenderComponent", SpeedField = "m_propellerSpeed", OnBlockRender = true, Axis = S7pSpinAxis.Z },
            new S7pSpinner { RenderType = "MyRenderComponentWindTurbine+TurbineRenderComponent", SpeedField = "m_speed", Axis = S7pSpinAxis.Y },
            new S7pSpinner { RenderType = "MyRenderComponentAirVent+MyWallFanRenderComponent", SpeedField = "m_speed", Axis = S7pSpinAxis.X },
            new S7pSpinner { RenderType = "MyRenderComponentHydrogenEngine+MyRotatingSubpartRenderComponent", SpeedField = "m_speed", Axis = S7pSpinAxis.Z },
        };

        /// <summary>The table entry of a render component type (its <c>FullName</c>; generic arguments ignored); false for anything else.</summary>
        public static bool TryFind(string renderTypeFullName, out S7pSpinner s)
        {
            s = default(S7pSpinner);
            if (string.IsNullOrEmpty(renderTypeFullName)) return false;
            string n = renderTypeFullName;
            int tick = n.IndexOf('`');
            if (tick >= 0) n = n.Substring(0, tick);
            for (int i = 0; i < SpinTable.Length; i++)
            {
                string t = SpinTable[i].RenderType;
                if (n.Length >= t.Length && string.CompareOrdinal(n, n.Length - t.Length, t, 0, t.Length) == 0 &&
                    (n.Length == t.Length || n[n.Length - t.Length - 1] == '.'))
                {
                    s = SpinTable[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>
 /// One step: <paramref name="omega"/> approaches <paramref name="target"/> (rad/s) at the spin-up rate while it grows in
 /// magnitude (or changes sign) and at the spin-down rate while it shrinks; a relative rate (≤ 0) is
 /// <paramref name="nominal"/> / <see cref="RelativeSpinS"/>, the nominal being the largest |target| this spinner has had
 /// (SE's drill: its definition speed / 0.5 both ways); |ω| ≤ <see cref="MaxOmega"/>; the angle advances by ω·dt and
 /// wraps into [0, 2π). dt is clamped to [0, 0.1] s.
 /// </summary>
        public static void Step(ref double angle, ref double omega, ref double nominal, double target, double up, double down, double dt)
        {
            if (double.IsNaN(target) || double.IsInfinity(target)) target = 0;
            if (target > MaxOmega) target = MaxOmega;
            if (target < -MaxOmega) target = -MaxOmega;
            if (!(dt > 0)) return;
            if (dt > 0.1) dt = 0.1;
            double absT = Math.Abs(target);
            if (!(nominal >= absT)) nominal = absT;
            if (!(up > 0)) up = nominal / RelativeSpinS;
            if (!(down > 0)) down = nominal / RelativeSpinS;
            bool growing = Math.Abs(target) > Math.Abs(omega) || Math.Sign(target) * Math.Sign(omega) < 0;
            double rate = growing ? up : down;
            if (!(rate > 0)) omega = target;
            else if (omega < target) omega = Math.Min(target, omega + rate * dt);
            else if (omega > target) omega = Math.Max(target, omega - rate * dt);
            if (omega > MaxOmega) omega = MaxOmega;
            if (omega < -MaxOmega) omega = -MaxOmega;
            angle += omega * dt;
            double twoPi = 2 * Math.PI;
            angle %= twoPi;
            if (angle < 0) angle += twoPi;
        }

        /// <summary>VRage's <c>Matrix.CreateRotationX/Y/Z(angle)</c> as an SE row matrix (no translation).</summary>
        public static SeMatrix Rotation(S7pSpinAxis axis, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            switch (axis)
            {
                case S7pSpinAxis.X: return new SeMatrix(new Vec3(1, 0, 0), new Vec3(0, c, s), new Vec3(0, -s, c), Vec3.Zero);
                case S7pSpinAxis.Y: return new SeMatrix(new Vec3(c, 0, -s), new Vec3(0, 1, 0), new Vec3(s, 0, c), Vec3.Zero);
                default: return new SeMatrix(new Vec3(c, s, 0), new Vec3(-s, c, 0), new Vec3(0, 0, 1), Vec3.Zero);
            }
        }

        /// <summary>
 /// The spun subpart's grid-local matrix: the spin in the subpart's own frame first, then its composed matrix
 /// (<c>R(axis, angle) × composed</c>, VRage row order; HYPOTHESIS: SE's animator turns the subpart about its own axis).
 /// </summary>
        public static SeMatrix Spun(SeMatrix composed, S7pSpinAxis axis, double angle)
        {
            return InstanceMath.Multiply(Rotation(axis, angle), composed);
        }
    }

    // =====================================================================================================================
 // P7: the projector's blueprint.
 // =====================================================================================================================

 /// <summary>Why a preview grid is (not) mirrored as a projection.</summary>
    public enum S7pProjectionVerdict : byte
    {
        Admit = 0, Off = 1, NotPreview = 2, NoProjector = 3, NotProjecting = 4, NotProjectedGrid = 5, Scaled = 6, TooManyBlocks = 7, TooManyProjections = 8,
    }

    public static class S7pProjectionRules
    {
        /// <summary>A projected block SE hides (already built, or not buildable with "show only buildable"): |Dithering| ≥ 1 (<c>MyProjectorBase.HideCube</c> sets −1; shown blocks −0.25 or −PROJECTOR_TRANSPARENCY).</summary>
        public static bool Hidden(float dithering) { return float.IsNaN(dithering) || Math.Abs(dithering) >= 0.999f; }

        /// <summary>SE's hologram tint for Subnautica's projection material (Standard Fade): colour and emission.</summary>
        public const float R = 0.32f, G = 0.66f, B = 1.0f, A = 0.30f;
        public const float EmissionR = 0.08f, EmissionG = 0.24f, EmissionB = 0.55f;

        /// <summary>
 /// The admission of one preview grid: projections on, a preview grid with its projector (<c>MyCubeGrid.Projector</c>)
 /// projecting, the projector's first preview grid (<c>ProjectedGrid</c>: one per projector), scale 1 (a console's
 /// miniature is not mirrored), at most <see cref="S7pScene.MaxProjectedBlocks"/> blocks and
 /// <see cref="S7pScene.MaxProjections"/> mirrored projections (an already mirrored one keeps its place).
 /// </summary>
 /// <summary>

 /// no projector while <c>MyGridClipboard.IsActive</c> holds it) rides the hologram path: admitted under the same block and
 /// projection caps, no projector checks (SE draws it at 0.25 transparency; its blocks' Dithering 0.25 keeps them exported).
 /// </summary>
        public static S7pProjectionVerdict Admit(bool on, bool isPreview, bool hasProjector, bool projecting, bool isProjectedGrid, float scale,
                                                int blocks, int mirroredProjections, bool alreadyMirrored, bool pasting)
        {
            if (on && isPreview && !hasProjector && pasting)
            {
                if (blocks > S7pScene.MaxProjectedBlocks) return S7pProjectionVerdict.TooManyBlocks;
                if (!alreadyMirrored && mirroredProjections >= S7pScene.MaxProjections) return S7pProjectionVerdict.TooManyProjections;
                return S7pProjectionVerdict.Admit;
            }
            return Admit(on, isPreview, hasProjector, projecting, isProjectedGrid, scale, blocks, mirroredProjections, alreadyMirrored);
        }

        public static S7pProjectionVerdict Admit(bool on, bool isPreview, bool hasProjector, bool projecting, bool isProjectedGrid, float scale,
                                                int blocks, int mirroredProjections, bool alreadyMirrored)
        {
            if (!on) return S7pProjectionVerdict.Off;
            if (!isPreview) return S7pProjectionVerdict.NotPreview;
            if (!hasProjector) return S7pProjectionVerdict.NoProjector;
            if (!projecting) return S7pProjectionVerdict.NotProjecting;
            if (!isProjectedGrid) return S7pProjectionVerdict.NotProjectedGrid;
            if (!(Math.Abs(scale - 1f) < 1e-4f)) return S7pProjectionVerdict.Scaled;
            if (blocks > S7pScene.MaxProjectedBlocks) return S7pProjectionVerdict.TooManyBlocks;
            if (!alreadyMirrored && mirroredProjections >= S7pScene.MaxProjections) return S7pProjectionVerdict.TooManyProjections;
            return S7pProjectionVerdict.Admit;
        }

        public static string Name(S7pProjectionVerdict v)
        {
            switch (v)
            {
                case S7pProjectionVerdict.Admit: return "admit";
                case S7pProjectionVerdict.Off: return "off";
                case S7pProjectionVerdict.NotPreview: return "notPreview";
                case S7pProjectionVerdict.NoProjector: return "noProjector";
                case S7pProjectionVerdict.NotProjecting: return "notProjecting";
                case S7pProjectionVerdict.NotProjectedGrid: return "notProjectedGrid";
                case S7pProjectionVerdict.Scaled: return "scaled";
                case S7pProjectionVerdict.TooManyBlocks: return "tooManyBlocks";
                case S7pProjectionVerdict.TooManyProjections: return "tooManyProjections";
                default: return "unknown(" + ((int)v).ToString(CultureInfo.InvariantCulture) + ")";
            }
        }
    }

    // =====================================================================================================================
 // P1: missiles in flight on the S5 stream.
 // =====================================================================================================================

    public static class S7pMissileRules
    {
        /// <summary>
 /// The missiles to mirror this tick: the nearest within <see cref="S7pScene.MissileRadiusM"/>, at most
 /// <paramref name="cap"/>, ties by id (deterministic). <paramref name="pick"/> receives the indices into the inputs,
 /// nearest first; returns the count; <paramref name="dropped"/> the in-radius missiles over the cap.
 /// </summary>
        public static int Select(long[] ids, double[] distances, int count, int cap, int[] pick, out int dropped)
        {
            dropped = 0;
            if (ids == null || distances == null || pick == null || count <= 0) return 0;
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                double d = distances[i];
                if (!(d <= S7pScene.MissileRadiusM)) continue;
                // insertion into the sorted pick list (cap small)
                int k = n;
                while (k > 0 && Before(d, ids[i], distances[pick[k - 1]], ids[pick[k - 1]])) k--;
                if (k >= cap) { dropped++; continue; }
                if (n == cap) { dropped++; n--; }
                for (int j = n; j > k; j--) pick[j] = pick[j - 1];
                pick[k] = i;
                n++;
            }
            return n;
        }

        private static bool Before(double d, long id, double d2, long id2) { return d < d2 || (d == d2 && id < id2); }
    }
}
