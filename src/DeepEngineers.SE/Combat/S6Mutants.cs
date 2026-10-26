using System.Collections.Generic;

namespace DeepEngineers.SE.Combat
{
    /// <summary>
 /// Seeded failures of slice S6 "Collision B + combat" on the SE side (ROADMAP S6 "Seeded failures"). Dev builds only;
 /// switched through <see cref="Mutants.Set"/> (environment variable <c>DEEPENGINEERS_MUTANTS</c> at Init, C11
 /// <c>de.mutant {name, on}</c>, or <c>/de mutant</c>). Game-independent, so tests/DeepEngineers.SE.Tests can turn it on.
 /// <list type="bullet">
 /// <item>S6.M3 dedup off: the damage consumer's <c>DedupWindow.Bypass</c> admits every sequence number, so S6.N1 applies
 /// its 10 duplicates.</item>
 /// <item>S6.M7 <c>k_small</c> and <c>k_large</c> swapped in the factor SE applies (a small-grid block takes × 25, a
 /// large-grid block × 1), so S6.N1 is red on both grids.</item>
 /// </list>
 /// S6.M4 (SE lab world in Creative) is a world choice, not code: the launcher's mutant group <c>creative</c> runs the
 /// SE-alone n1 part on the Creative "DE Lab" (the lab world as it is, instead of the Survival copy that
 /// tools/lab/make_survival_copy.py --destructible makes) and the consumer's own gate refuses the character records
 /// (<c>MyCharacter.CharactersCanDie</c> is false in Creative → <c>CannotDie</c>, one <c>DE.Damage INACTIVE character:
 /// Creative</c> line), so the S6.N1 character row turns red.
 /// S6.M6 is a Subnautica host-slot choice. S6.M1, M2, M5, M8, M9, M10, M11, M12 are Subnautica code switches.
 /// </summary>
    internal static class S6Mutants
    {
        /// <summary>S6.M3: every damage sequence number is admitted by the dedup window (duplicates applied).</summary>
        public static volatile bool DedupOff;

        /// <summary>S6.M7: k_small and k_large swapped in the applied factor.</summary>
        public static volatile bool SwapGridFactors;

        public static string Active
        {
            get
            {
                var parts = new List<string>(2);
                if (DedupOff) parts.Add("S6.M3");
                if (SwapGridFactors) parts.Add("S6.M7");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            DedupOff = false;
            SwapGridFactors = false;
        }

        /// <summary>Switches an S6 mutant; returns the log text (starting with <c>S6.</c> or <c>REFUSE</c>), or null when not an S6 id.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            string key = (id ?? "").Trim().ToUpperInvariant();
            switch (key)
            {
                case "S6.M3":
                    DedupOff = on;
                    return "S6.M3 " + state + " (damage dedup off: every C8 sequence number is admitted; S6.N1 applies the 10 duplicates)";
                case "S6.M7":
                    SwapGridFactors = on;
                    return "S6.M7 " + state + " (k_small and k_large swapped in the applied factor; S6.N1 red on both grids)";
                case "S6.M4":
                    return "REFUSE S6.M4: a world choice, not code (the launcher runs the SE-alone n1 part on a Creative world; the damage gate refuses the character records)";
                case "S6.M6":
                    return "REFUSE S6.M6: a Subnautica host-slot choice (a Creative throwaway copy of the host slot; S6.N2 refuses)";
                case "S6.M1":
                case "S6.M2":
                case "S6.M5":
                case "S6.M8":
                case "S6.M9":
                case "S6.M10":
                case "S6.M11":
                case "S6.M12":
                    return "REFUSE " + id.Trim() + ": a Subnautica-side S6 mutant (proxies, damage patches or records; send de.mutant to Subnautica: /de sn mutant ...)";
                default:
                    return null;
            }
        }
    }
}
