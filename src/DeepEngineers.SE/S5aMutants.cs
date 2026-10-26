using System.Collections.Generic;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Seeded failures of slice S5a "Arms and breath" (dev builds only; switched through <see cref="Mutants.Set"/>:
 /// <c>DEEPENGINEERS_MUTANTS</c> at Init, C11 <c>de.mutant</c>, <c>/de mutant</c>). Each makes one row of
 /// tests/s5a/analyze_s5a.py red (launcher <c>s5a --mutants</c>):
 /// <list type="bullet">
 /// <item>S5a.M1 fins not applied: the controller claims its fins setting (the swim row's <c>fin</c>, the C1 <c>fins</c>
 /// byte) but the model runs without the modifier; the replay and the fins steady speeds go red.</item>
 /// <item>S5a.M2 O2 stuck: the C1 <c>o2</c> byte keeps its first written value (the suit and the override are ignored);
 /// the o2 part goes red.</item>
 /// <item>S5a.M3 swim flags frozen: the C1 extension's flags, mode, axes and speeds keep the first non-idle values
 /// (the arms part goes red: no release, no leaving the water).</item>
 /// </list>
 /// </summary>
    internal static class S5aMutants
    {
        /// <summary>S5a.M1: the fins modifier is claimed but not applied by the model.</summary>
        public static volatile bool FinsNotApplied;

        /// <summary>S5a.M2: the mirrored O2 fraction never changes after its first write.</summary>
        public static volatile bool O2Stuck;

        /// <summary>S5a.M3: the swim flags, mode, axes and speeds of the C1 extension never change after the first non-idle write.</summary>
        public static volatile bool WireFrozen;

        public static string Active
        {
            get
            {
                var parts = new List<string>(3);
                if (FinsNotApplied) parts.Add("S5a.M1");
                if (O2Stuck) parts.Add("S5a.M2");
                if (WireFrozen) parts.Add("S5a.M3");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            FinsNotApplied = false;
            O2Stuck = false;
            WireFrozen = false;
        }

        /// <summary>Switches an S5a mutant; returns the log text (starting with <c>S5a.</c>), or null when not an S5a id.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S5A.M1":
                    FinsNotApplied = on;
                    return "S5a.M1 " + state + " (fins claimed in the row and C1 but not applied by the swim model)";
                case "S5A.M2":
                    O2Stuck = on;
                    return "S5a.M2 " + state + " (C1 o2 byte stuck at its first value)";
                case "S5A.M3":
                    WireFrozen = on;
                    return "S5a.M3 " + state + " (C1 swim flags, mode, axes and speeds frozen after the first write)";
                default:
                    return null;
            }
        }
    }
}
