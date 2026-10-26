using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S16 seeded failures of the SE side (dev builds; ROADMAP §3 rule 1; tests/s16/analyze_s16.py turns exactly their rows red):
 /// M1 the upgrade blocks are not seen by the scan (S16.LADDER, S16.T3), M2 the damage starts at the creak band instead of
 /// the limit (S16.BREAK, S16.T3), M3 the damage cap is dropped (S16.CURVE), M4 the creak band is 100 m (S16.CREAK), M5 the
 /// voice fires every period past the limit (S16.VOICE), M6 the HUD depth is the grid's centre, not its lowest point
 /// (S16.HUD), M8 the exposure counts nothing, M9 the mechanical group and docking are ignored
 ///, M10 no wet-faced fallback damage set. S16.M7 is Subnautica's (the creaks
 /// per frame instead of per event, S16.SN).
 /// </summary>
    internal static class S16SeMutants
    {
        public static volatile bool NoTiers, EarlyDamage, NoCap, WideBand, VoiceRepeats, CentreDepth, CountsNothing, NoGroups, NoWetFallback;
        public static readonly string[] All = { "S16.M1", "S16.M2", "S16.M3", "S16.M4", "S16.M5", "S16.M6", "S16.M8", "S16.M9", "S16.M10" };

        public static string Active
        {
            get
            {
                var parts = new List<string>(9);
                if (NoTiers) parts.Add("S16.M1");
                if (EarlyDamage) parts.Add("S16.M2");
                if (NoCap) parts.Add("S16.M3");
                if (WideBand) parts.Add("S16.M4");
                if (VoiceRepeats) parts.Add("S16.M5");
                if (CentreDepth) parts.Add("S16.M6");
                if (CountsNothing) parts.Add("S16.M8");
                if (NoGroups) parts.Add("S16.M9");
                if (NoWetFallback) parts.Add("S16.M10");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll() { NoTiers = EarlyDamage = NoCap = WideBand = VoiceRepeats = CentreDepth = CountsNothing = NoGroups = NoWetFallback = false; }

        /// <summary>Switches an S16 SE mutant; the log text, or null when the id is not an S16 id this side owns.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S16.M1": NoTiers = on; return "S16.M1 " + state + " (the pressure hull upgrade blocks are not seen by the grid scan)";
                case "S16.M2": EarlyDamage = on; return "S16.M2 " + state + " (crush damage from the creak band, 50 m above the limit)";
                case "S16.M3": NoCap = on; return "S16.M3 " + state + " (the crush damage cap is dropped)";
                case "S16.M4": WideBand = on; return "S16.M4 " + state + " (the creak band is 100 m instead of 50 m)";
                case "S16.M5": VoiceRepeats = on; return "S16.M5 " + state + " (the hull-critical voice fires every period past the limit)";
                case "S16.M6": CentreDepth = on; return "S16.M6 " + state + " (the HUD depth is the grid's centre, not its lowest point)";
                case "S16.M8": CountsNothing = on; return "S16.M8 " + state + " (the exposure counts nothing: every hull rated as light armour of its size)";
                case "S16.M9": NoGroups = on; return "S16.M9 " + state + " (the mechanical group's tier and docking are ignored)";
                case "S16.M10": NoWetFallback = on; return "S16.M10 " + state + " (no wet-faced fallback: a grid with nothing counted takes no damage)";
                case "S16.M7": return "REFUSE " + id.Trim() + ": a Subnautica-side S16 mutant (send de.mutant to Subnautica: /de sn mutant ...)";
                default: return null;
            }
        }

        /// <summary>The damage threshold under M2 (the creak band's start), else the limit.</summary>
        public static double DamageThreshold(double limit) { return EarlyDamage ? limit - S16Conventions.Creak.BandM : limit; }

        /// <summary>
 /// The per-second damage of a block at <paramref name="depth"/> on a grid limited at <paramref name="limit"/>: the rule;
 /// under M2 the base rate already inside the creak band; under M3 without the cap.
 /// </summary>
        public static double PerSecond(double depth, double limit)
        {
            if (EarlyDamage && !(depth > limit) && depth > limit - S16Conventions.Creak.BandM) return S16Conventions.Damage.BaseMilliHpS / 1000.0;
            if (!NoCap) return S16Damage.PerSecond(depth, limit);
            if (!(depth > limit)) return 0.0;
            return S16Conventions.Damage.BaseMilliHpS / 1000.0 + S16Conventions.Damage.PerMetreMilliHpS / 1000.0 * (depth - limit);
        }

        public static int CreakBand { get { return WideBand ? 2 * S16Conventions.Creak.BandM : S16Conventions.Creak.BandM; } }
    }

    /// <summary>
 /// The C5 extension's current values (CONTRACT §5.4 "S16 conventions", <c>format c5.s16</c>): set by <c>S16Host</c> on the
 /// game thread, written into each C5 record by <c>Link.WriteC5</c> (same thread; S16 hook). Game-free so the link tests
 /// compile it; inactive = version 0 (no extension).
 /// </summary>
    internal static class S16SeWire
    {
        private static S16WireState _state;
        private static bool _active;
        public static uint CreakSeq, VoiceSeq, DamageSeq;
        public static long Writes;

        public static bool IsActive { get { return _active; } }
        public static S16WireState Current { get { return _state; } }

        public static void Set(S16WireState s)
        {
            s.CreakSeq = CreakSeq;
            s.VoiceSeq = VoiceSeq;
            s.DamageSeq = DamageSeq;
            _state = s;
            _active = true;
        }

        /// <summary>No grid and no diver: the extension carries the counters only (Subnautica keeps its baselines).</summary>
        public static void SetIdle(uint worldEpoch)
        {
            var s = default(S16WireState);
            s.WorldEpoch = worldEpoch;
            Set(s);
        }

        public static void Off() { _active = false; }

        /// <summary>The S16 hook of <c>Link.WriteC5</c>: the extension bytes of this record (cleared when inactive).</summary>
        public static void Fill(ref SeState st)
        {
            if (!_active) { S16Wire.Clear(ref st); return; }
            S16WireState s = _state;
            S16Wire.Write(ref st, ref s);
            Writes++;
        }
    }

    /// <summary>The S16 switches read once per session (env) and changed live by de.s16 (dev).</summary>
    internal static class S16Switch
    {
        public const string EnvVar = "DEEPENGINEERS_S16";

        /// <summary>DEEPENGINEERS_S16=off disables the whole slice (no scan, no damage, no HUD, no C5 extension); default on.</summary>
        public static bool EnabledFromEnvironment()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            return !string.Equals((v ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);
        }
    }
}
