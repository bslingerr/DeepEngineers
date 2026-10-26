using System.Collections.Generic;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Seeded failures of slice S5 "Whole grids + character" (ROADMAP S5 "Seeded failures" and "Builder contract": the
 /// mutant insertion points). Dev builds only; switched through <see cref="Mutants.Set"/> (environment variable
 /// <c>DEEPENGINEERS_MUTANTS</c> at Init, C11 <c>de.mutant</c>, or <c>/de mutant</c>). Game-independent, so
 /// tests/DeepEngineers.SE.Tests can turn it on.
 /// <list type="bullet">
 /// <item>S5.M3 PartRemove dropped: the SE C4 producer does not write PartRemove but counts it as sent (the held set
 /// forgets the part, so the consumer audit does not resend); S5.N1 must show a count mismatch after a grind.</item>
 /// </list>
 /// S5.M1 (grids sampled one tick behind the camera: the S3.M4 "Previous" path moved to the consumer's pose sampler),
 /// S5.M2, S5.M4 (Full instead of Idle in Space) and S5.M5 (cameras kept on
 /// in Track) are Subnautica-side: the SE producer has no half of them (the perf recipes run in Subnautica).
 /// </summary>
    internal static class S5Mutants
    {
        /// <summary>S5.M3: PartRemove not written, still counted as sent.</summary>
        public static volatile bool DropPartRemove;

        public static string Active
        {
            get
            {
                var parts = new List<string>(1);
                if (DropPartRemove) parts.Add("S5.M3");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            DropPartRemove = false;
        }

        /// <summary>Switches an S5 mutant; returns the log text (starting with <c>S5.</c>), or null when not an S5 id.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S5.M3":
                    DropPartRemove = on;
                    return "S5.M3 " + state + " (the C4 producer does not write PartRemove but counts it as sent; S5.N1 count mismatch after a grind)";
                case "S5.M1":
                case "S5.M2":
                case "S5.M4":
                case "S5.M5":
                    return "REFUSE " + id.Trim() + ": a Subnautica-side S5 mutant (pose sampler, mesh builder flip or perf recipes; send de.mutant to Subnautica: /de sn mutant ...)";
                default:
                    return null;
            }
        }
    }
}
