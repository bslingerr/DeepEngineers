using System;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Seeded failures of slice S8a on the Subnautica side (ROADMAP "### S8a" "Seeded failures"; build pass contract: "SN
 /// <c>S8aMutants.cs</c> (M4, M7, M11)"). Unity-free; the switch is <see cref="DevMutants"/> (dev builds,
 /// <c>DEEPENGINEERS_MUTANT</c> at load, C11 <c>de.mutant S8a.Mx on|off</c>); these are the names and the read side the

 /// <list type="bullet">
 /// <item>S8a.M4 the <c>GetPath</c> postfix serves the install from the n5 reload on (<see cref="S8aPatches"/>): S8a.N5.reload red;</item>
 /// <item>S8a.M7 interpolation across a <c>chartSeq</c> change (S8a-2, the S5 interpolation sites): S8a.N8.snap red;</item>
 /// <item>S8a.M11 spawn density × 10 (S8a-2, <c>S8aSpawns</c>): S8a.N10.spawn red;</item>
 /// <item>S8a.M29 (planet v2, the look) no spawns beyond the Subnautica map (<c>S8aSpawns</c> off): S8a.NL.look red;</item>
 /// <item>S8a.M21 / M22 / M24 / M25 (planet v2, ores you can see, <c>S8aOreDeposits</c>): every third record skipped (NO.cover),
 /// a phantom deposit (NO.vein), updates ignored (NO.shrink), Subnautica's drill gets through (NO.immune);</item>
 /// <item>S8a.M26 (planet v2, props) S8aFlora v1's candidate rule (no pickupable item): S8a.ND.props red.</item>
 /// </list>
 /// The SE-side S8a mutants (M1–M3, M5, M6, M8–M10, M12–M14) are DeepEngineers.SE's <c>S8aMutants</c>.
 /// </summary>
    internal static class S8aMutants
    {
        public const string NameM4 = "S8a.M4", NameM7 = "S8a.M7", NameM11 = "S8a.M11", NameM29 = "S8a.M29", NameM21 = "S8a.M21", NameM22 = "S8a.M22",
                            NameM24 = "S8a.M24", NameM25 = "S8a.M25", NameM26 = "S8a.M26";

        /// <summary>The ids this side owns (DevMutants.IsKnown accepts them).</summary>
        public static readonly string[] All = { NameM4, NameM7, NameM11, NameM29, NameM21, NameM22, NameM24, NameM25, NameM26 };

        public static bool M4 { get { return DevMutants.IsOn(NameM4); } }
        public static bool M7 { get { return DevMutants.IsOn(NameM7); } }
        public static bool M11 { get { return DevMutants.IsOn(NameM11); } }
        public static bool M29 { get { return DevMutants.IsOn(NameM29); } }
        public static bool M21 { get { return DevMutants.IsOn(NameM21); } }
        public static bool M22 { get { return DevMutants.IsOn(NameM22); } }
        public static bool M24 { get { return DevMutants.IsOn(NameM24); } }
        public static bool M25 { get { return DevMutants.IsOn(NameM25); } }
        public static bool M26 { get { return DevMutants.IsOn(NameM26); } }
    }
}
