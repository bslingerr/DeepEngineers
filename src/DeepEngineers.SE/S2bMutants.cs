using System.Collections.Generic;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Seeded failures of slice S2b "Swimming" (ROADMAP S2b "Seeded failures", §3 rule 1). Dev builds only; switched
 /// through <see cref="Mutants.Set"/> (environment variable <c>DEEPENGINEERS_MUTANTS</c> at Init, C11
 /// <c>de.mutant</c>, or <c>/de mutant</c>). Game-independent, so tests/DeepEngineers.SE.Tests can turn each one on.
 /// <list type="bullet">
 /// <item>S2b.M1 model numbers ×1.5: every speed and acceleration of the movement model ×1.5 (drag unchanged);
 /// S2b.N1 must go red against the Python replay of the true numbers.</item>
 /// <item>S2b.M2 jetpack left on in the water: the controller never switches the jetpack off on entry, never refuses
 /// it while the head is under and never restores it on exit; S2b.N2 must go red (jetpack on with the head under).</item>
 /// <item>S2b.M3 no hysteresis: the swim state enters and leaves at the same line; S2b.N3 must show bouncing.</item>
 /// <item>S2b.M4 oxygen rule off: SE's stock suit consumption underwater, no Subnautica refill at the surface; S2b.N4
 /// must go red.</item>
 /// </list>
 /// </summary>
    internal static class S2bMutants
    {
        public const uint BitM1 = 1u << 0, BitM2 = 1u << 1, BitM3 = 1u << 2, BitM4 = 1u << 3;

        /// <summary>S2b.M1: model speeds and accelerations ×1.5.</summary>
        public static volatile bool NumbersX15;

        /// <summary>S2b.M2: the jetpack is left alone in the water.</summary>
        public static volatile bool JetpackLeftOn;

        /// <summary>S2b.M3: one swim threshold both ways.</summary>
        public static volatile bool NoHysteresis;

        /// <summary>S2b.M4: the oxygen rule is off (stock SE suit consumption).</summary>
        public static volatile bool OxygenRuleOff;

        /// <summary>The active S2b mutants as bits (logged in every swim row; M1 and M3 go to the model).</summary>
        public static uint ModelBits
        {
            get
            {
                uint b = 0;
                if (NumbersX15) b |= BitM1;
                if (JetpackLeftOn) b |= BitM2;
                if (NoHysteresis) b |= BitM3;
                if (OxygenRuleOff) b |= BitM4;
                return b;
            }
        }

        public static string Active
        {
            get
            {
                var parts = new List<string>(4);
                if (NumbersX15) parts.Add("S2b.M1");
                if (JetpackLeftOn) parts.Add("S2b.M2");
                if (NoHysteresis) parts.Add("S2b.M3");
                if (OxygenRuleOff) parts.Add("S2b.M4");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            NumbersX15 = false;
            JetpackLeftOn = false;
            NoHysteresis = false;
            OxygenRuleOff = false;
        }

        /// <summary>Switches an S2b mutant; returns the log text (starting with <c>S2b.</c>), or null when not an S2b id.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S2B.M1":
                    NumbersX15 = on;
                    return "S2b.M1 " + state + " (swim model: every speed and acceleration x1.5)";
                case "S2B.M2":
                    JetpackLeftOn = on;
                    return "S2b.M2 " + state + " (jetpack left alone in the water: never switched off, refused or restored)";
                case "S2B.M3":
                    NoHysteresis = on;
                    return "S2b.M3 " + state + " (swim state: one threshold both ways, no hysteresis)";
                case "S2B.M4":
                    OxygenRuleOff = on;
                    return "S2b.M4 " + state + " (oxygen rule off: stock SE suit consumption, no surface refill)";
                default:
                    return null;
            }
        }
    }
}
