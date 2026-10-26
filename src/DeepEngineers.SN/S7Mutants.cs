using System;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Seeded failures of slice S7 on the Subnautica side (ROADMAP S7 "Seeded failures"; build pass contract: "SN
 /// <c>S7Mutants.cs</c> (M1–M5, M7, M11, M13, M18)"). Unity-free; the switch is <see cref="DevMutants"/> (dev builds,
 /// <c>DEEPENGINEERS_MUTANT</c> at load, C11 <c>de.mutant S7.Mx on|off</c>), these are the names and the read side the
 /// M4</c> …)
 /// <list type="bullet">
 /// <item>S7.M1 <c>IsInsideWalkable</c> postfix off: S7.N1.fog, S7.N2.readers red;</item>
 /// <item>S7.M2 time set directly instead of advanced (= S7a.M6: switching S7.M2 switches S7a.M6 with it, the time
 /// drive reads that one): S7.WM-5 monotonic red;</item>
 /// <item>S7.M3 the statics reporter drops capsules: S7.N5 red;</item>
 /// <item>S7.M4 bones mirrored component-wise instead of conjugated (<see cref="SkinnedAstronaut"/>: the SeBones pose
 /// and the extra rows through Wire <c>S7Bones.ComponentWiseLocal</c>): S7.N6.pos red;</item>
 /// <item>S7.M5 <c>SetSky(Skies.BaseInterior)</c> without the custom sky: S7.N8 red;</item>
 /// <item>S7.M7 statics chosen by "static or kinematic": S7.N9 red;</item>
 /// <item>S7.M11 thruster bubble emission constant: S7.N4.thruster.rate red;</item>
 /// <item>S7.M13 explosion scale fixed, radius ignored: S7.N4.explosion red;</item>
 /// <item>S7.M18 no shadow cap in the light pool: S7.N3.shadows red.</item>
 /// </list>
 /// </summary>
    internal static class S7Mutants
    {
        public const string NameM1 = "S7.M1", NameM2 = "S7.M2", NameM3 = "S7.M3", NameM4 = "S7.M4", NameM5 = "S7.M5", NameM7 = "S7.M7",
                            NameM11 = "S7.M11", NameM13 = "S7.M13", NameM18 = "S7.M18";

        /// <summary>The ids this side owns (DevMutants.IsKnown accepts them).</summary>
        public static readonly string[] All = { NameM1, NameM2, NameM3, NameM4, NameM5, NameM7, NameM11, NameM13, NameM18 };

        public static bool M1 { get { return DevMutants.IsOn(NameM1); } }
        public static bool M2 { get { return DevMutants.IsOn(NameM2) || DevMutants.IsOn(DevMutants.S7aTimeDirect); } }
        public static bool M3 { get { return DevMutants.IsOn(NameM3); } }
        public static bool M4 { get { return DevMutants.IsOn(NameM4); } }
        public static bool M5 { get { return DevMutants.IsOn(NameM5); } }
        public static bool M7 { get { return DevMutants.IsOn(NameM7); } }
        public static bool M11 { get { return DevMutants.IsOn(NameM11); } }
        public static bool M13 { get { return DevMutants.IsOn(NameM13); } }
        public static bool M18 { get { return DevMutants.IsOn(NameM18); } }

        /// <summary>
 /// DevMutants.Set's hook after a switch: S7.M2 is S7a.M6 under another id (ROADMAP S7 "S7.M2 … (= S7a.M6)"), so it
 /// switches that one too (the time drive, <c>DayNightApply</c>, reads S7a.M6 only).
 /// </summary>
        public static void OnSet(string name, bool on)
        {
            if (!string.Equals(name, NameM2, StringComparison.Ordinal)) return;
            string error;
            if (!DevMutants.Set(DevMutants.S7aTimeDirect, on, out error)) DeLog.Warn("DE.Mutant S7.M2 could not switch S7a.M6: " + error);
        }
    }
}
