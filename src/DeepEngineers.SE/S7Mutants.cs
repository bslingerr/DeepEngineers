using System;
using System.Collections.Generic;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Seeded failures of slice S7 on the SE side (ROADMAP S7 "Seeded failures"; build pass contract: "SE <c>S7Mutants.cs</c>
 /// Dev builds only; switched through
 /// <see cref="Mutants.Set"/> (environment variable <c>DEEPENGINEERS_MUTANTS</c> at Init, C11 <c>de.mutant</c>, or
 /// <c>/de mutant</c>). Game-independent (plain flags), so tests/DeepEngineers.SE.Tests can turn them on. Each flag is
 /// read by the code that owns the feature it breaks:
 /// <list type="bullet">
 /// <item>S7.M8 statics host <c>Save = true</c>: S7.N10 red;</item>
 /// <item>S7.M9 bones truncated to 64 (<see cref="SkinnedExport"/>: CharacterAdd and C1 <c>boneCount</c> 64): S7.N6.count red;</item>
 /// <item>S7.M10 interior flag from <c>GetRooms</c> every tick: S7.N1.flicker red;</item>
 /// <item>S7.M12 <c>MyGunBase.Shoot</c> postfix off: S7.N4.weapon red;</item>
 /// <item>S7.M14 tool sustains never refreshed, start row only: S7.N4.tools red;</item>
 /// <item>S7.M15 attached events sent with <c>entityId</c> 0: S7.N4.weapon and gridimpact position red;</item>
 /// <item>S7.M16 splash never raised: S7.N4.splash red;</item>
 /// <item>S7.M17 event window of 1 tick, no hold: S7.N4.loss red at 30 fps;</item>
 /// <item>S7.M19 muffle postfix off (<see cref="AudioPolish"/>: <c>SelectEffect</c> keeps SE's own answer): S7.N11.muffle red;</item>
 /// <item>S7.M20 event caps off, every raised event accepted: S7.N4.rates red.</item>
 /// </list>
 /// The Subnautica-side S7 mutants (M1–M5, M7, M11, M13, M18) are DeepEngineers.SN's <c>S7Mutants</c>; S7.M6 is a world
 /// copy (<c>DE Lab SunWeather</c>), not a flag.
 /// </summary>
    internal static class S7Mutants
    {
        public static volatile bool M8, M9, M10, M12, M14, M15, M16, M17, M19, M20;

        /// <summary>
 /// Review F19: the save-writing mutant's guard, set by the statics consumer (game side; this file stays
 /// game-free): null = allowed (a throwaway copy, or no session yet: checked again when the world runs), else the
 /// reason S7.M8 is refused (the session's save folder has no <c>DE_THROWAWAY.txt</c>).
 /// </summary>
        public static Func<string> M8Guard;

        /// <summary>The log line of a refused S7.M8.</summary>
        public const string M8RefusedLine = "DE.S7 statics MUTANT S7.M8 REFUSED (not a throwaway copy)";

        /// <summary>The ids this side owns, in order.</summary>
        public static readonly string[] All = { "S7.M8", "S7.M9", "S7.M10", "S7.M12", "S7.M14", "S7.M15", "S7.M16", "S7.M17", "S7.M19", "S7.M20" };

        public static string Active
        {
            get
            {
                var parts = new List<string>(4);
                if (M8) parts.Add("S7.M8");
                if (M9) parts.Add("S7.M9");
                if (M10) parts.Add("S7.M10");
                if (M12) parts.Add("S7.M12");
                if (M14) parts.Add("S7.M14");
                if (M15) parts.Add("S7.M15");
                if (M16) parts.Add("S7.M16");
                if (M17) parts.Add("S7.M17");
                if (M19) parts.Add("S7.M19");
                if (M20) parts.Add("S7.M20");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            M8 = M9 = M10 = M12 = M14 = M15 = M16 = M17 = M19 = M20 = false;
        }

        /// <summary>Switches an SE-side S7 mutant; returns the log text (starting with <c>S7.</c>), or null when not one of this side's ids.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S7.M8":
                {
                    // review F19: a leftover DEEPENGINEERS_MUTANTS must never put statics hosts into a real save
                    Func<string> guard = M8Guard;
                    string why = on && guard != null ? guard() : null;
                    if (why != null)
                    {
                        M8 = false;
                        DeLog.Info(M8RefusedLine + ": " + why);
                        return "S7.M8 REFUSED (" + why + "; the world's save folder needs DE_THROWAWAY.txt)";
                    }
                    M8 = on;
                    return "S7.M8 " + state + " (statics host Save = true: the host would be saved; throwaway copy only)";
                }
                case "S7.M9": M9 = on; return "S7.M9 " + state + " (character bone rows truncated to 64 in CharacterAdd and C1)";
                case "S7.M10": M10 = on; return "S7.M10 " + state + " (interior flag from GetRooms every tick, no processing hold)";
                case "S7.M12": M12 = on; return "S7.M12 " + state + " (MyGunBase.Shoot postfix off: no muzzle or streak events)";
                case "S7.M14": M14 = on; return "S7.M14 " + state + " (tool sustains never refreshed: start row only)";
                case "S7.M15": M15 = on; return "S7.M15 " + state + " (attached events sent with entityId 0)";
                case "S7.M16": M16 = on; return "S7.M16 " + state + " (splash never raised)";
                case "S7.M17": M17 = on; return "S7.M17 " + state + " (event window of 1 tick, no hold)";
                case "S7.M19": M19 = on; return "S7.M19 " + state + " (muffle postfix off: SelectEffect keeps SE's own answer under water)";
                case "S7.M20": M20 = on; return "S7.M20 " + state + " (event caps off: every raised event accepted)";
                default: return null;
            }
        }
    }
}
