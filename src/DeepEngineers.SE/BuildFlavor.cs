using System;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Dev or release build. The flavour is chosen at build time: <c>build.ps1 -Flavor release</c> (or
 /// <c>dotnet build -p:DeepEngFlavor=release</c>) defines <c>DEEPENG_RELEASE</c> in both plugins and writes them to
 /// <c>bin\ReleaseFlavor\</c>
 /// <list type="bullet">
 /// <item>Dev: the harness rings C11a/C11b, every <c>/de</c> chat command and relay, the run-time mutant switches
 /// (<c>DEEPENGINEERS_MUTANTS</c>, <c>/de mutant</c>), the S0.N12 version hook (<c>DEEPENGINEERS_TEST_GAMEVERSION</c>)
 /// and the measure-only baseline (<c>DEEPENGINEERS_SE_MODE=measure</c>).</item>
 /// <item>Release: none of those; the harness ring is never read; <c>/de</c> keeps only the user tuning commands of
 /// <see cref="DeepEngineers.Wire.UserCommandRules"/> (their relay to Subnautica included). The hello line says
 /// <c>build=release flavor=release</c>.</item>
 /// </list>
 /// </summary>
    internal static class BuildFlavor
    {
        // static readonly rather than const: the release branches stay compiled (no unreachable-code warnings)
#if DEEPENG_RELEASE
        public static readonly bool IsDev = false;
#else
        public static readonly bool IsDev = true;
#endif
        public static string Name { get { return IsDev ? "dev" : "release"; } }
    }

    /// <summary>
 /// Seeded failures of the SE plugin (ROADMAP §3 rule 1, S0 mutants): S0.M8 can only be shown red in the real game;
 /// S0.M5a-c (the peer detector of <see cref="Link"/>) are shown red by tests/DeepEngineers.SE.Tests and can also be
 /// switched on in a real run. Dev builds only. Switched by the environment variable <c>DEEPENGINEERS_MUTANTS</c>
 /// (comma-separated ids, read at Init) or by the C11 command <c>de.mutant</c>.
 /// </summary>
    internal static class Mutants
    {
        public const string EnvVar = "DEEPENGINEERS_MUTANTS";

        /// <summary>
 /// S0.M8: the version check reads the compile-time <c>SpaceEngineersGame.SE_VERSION</c> const (inlined by the
 /// compiler) instead of <c>MyPerGameSettings.BasicGameInfo.GameVersion</c>; the S0.N12 hook then cannot produce
 /// REFUSE, so S0.N12 must go red.
 /// </summary>
        public static volatile bool VersionFromCompiledConst;

        /// <summary>S0.M5a: the peer detector ignores process exit (no WaitForSingleObject poll on snPid).</summary>
        public static volatile bool IgnoreProcessExit;

        /// <summary>S0.M5b: the in-play stall timeout is 30 s instead of 3 s.</summary>
        public static volatile bool StallTimeout30s;

        /// <summary>
 /// S0.M5c: liveness judged on "heartbeat value changed" and peer identity by PID only (the generation is
 /// ignored, no process poll), so a restart that reuses the PID is not seen.
 /// </summary>
        public static volatile bool HeartbeatLiveness;

        public static string Active
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>(4);
                if (IgnoreProcessExit) parts.Add("S0.M5a");
                if (StallTimeout30s) parts.Add("S0.M5b");
                if (HeartbeatLiveness) parts.Add("S0.M5c");
                if (VersionFromCompiledConst) parts.Add("S0.M8");
                string s1 = S1Mutants.Active; // S1 seeded failures (S1Mutants.cs)
                if (s1.Length > 0) parts.Add(s1);
                string s2 = S2Mutants.Active; // S2 seeded failures (S2Mutants.cs)
                if (s2.Length > 0) parts.Add(s2);
                string s2b = S2bMutants.Active; // S2b seeded failures (S2bMutants.cs)
                if (s2b.Length > 0) parts.Add(s2b);
                string s3 = S3Mutants.Active; // S3 seeded failures of the SE producer (S3Mutants.cs)
                if (s3.Length > 0) parts.Add(s3);
                string s5a = S5aMutants.Active; // S5a seeded failures (S5aMutants.cs)
                if (s5a.Length > 0) parts.Add(s5a);
                string s4 = Terrain.S4Mutants.Active; // S4 seeded failures of the terrain
                if (s4.Length > 0) parts.Add(s4);
                string s5 = S5Mutants.Active; // S5 seeded failures of the SE producer (S5Mutants.cs)
                if (s5.Length > 0) parts.Add(s5);
                string s6 = Combat.S6Mutants.Active; // S6 seeded failures of the damage consumer
                if (s6.Length > 0) parts.Add(s6);
                string s7aPolicy = S7aPolicyMutants.Active; // S7a.M1, S7a.M3 of the picture policy (PicturePolicy.cs)
                if (s7aPolicy.Length > 0) parts.Add(s7aPolicy);
                string s7aGhost = S7aGhostMutants.Active; // S7a.M4 of the placement ghost (PlacementGhostRules.cs)
                if (s7aGhost.Length > 0) parts.Add(s7aGhost);
                string s7 = S7Mutants.Active; // S7 seeded failures of the SE side (S7Mutants.cs)
                if (s7.Length > 0) parts.Add(s7);
                string s11 = S11SeMutants.Active; // S11 hook: S11.M4, S11.M5 of the SE side
                if (s11.Length > 0) parts.Add(s11);
                string s16 = S16SeMutants.Active; // S16 hook: S16.M1-M6 of the SE side
                if (s16.Length > 0) parts.Add(s16);
                string s13 = S13SeMutants.Active; // S13 hook: S13.M3, S13.M5 of the SE side
                if (s13.Length > 0) parts.Add(s13);
                string s14 = S14SeMutants.Active; // S14 hook: S14.M1-M3, M7-M9 of the SE side
                if (s14.Length > 0) parts.Add(s14);
                string s15 = S15SeMutants.Active; // S15 hook: S15.M1, M2, M4, M8, M9 of the SE side
                if (s15.Length > 0) parts.Add(s15);
                string s12 = S12SeMutants.Active; // S12 hook: S12.M1-M5 of the SE side
                if (s12.Length > 0) parts.Add(s12);
                return string.Join(",", parts.ToArray());
            }
        }

        /// <summary>Switches every mutant off (tests).</summary>
        public static void ClearAll()
        {
            VersionFromCompiledConst = false;
            IgnoreProcessExit = false;
            StallTimeout30s = false;
            HeartbeatLiveness = false;
            S1Mutants.ClearAll();
            S2Mutants.ClearAll();
            S2bMutants.ClearAll();
            S3Mutants.ClearAll();
            S5aMutants.ClearAll();
            Terrain.S4Mutants.ClearAll();
            S5Mutants.ClearAll();
            Combat.S6Mutants.ClearAll();
            S7aPolicyMutants.ClearAll();
            S7aGhostMutants.ClearAll();
            S7Mutants.ClearAll();
            S11SeMutants.ClearAll(); // S11 hook
            S16SeMutants.ClearAll(); // S16 hook
            S13SeMutants.ClearAll(); // S13 hook
            S14SeMutants.ClearAll(); // S14 hook
            S15SeMutants.ClearAll(); // S15 hook
            S12SeMutants.ClearAll(); // S12 hook
        }

        public static void LoadFromEnvironment()
        {
            if (!BuildFlavor.IsDev) return;
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            if (string.IsNullOrEmpty(v)) return;
            foreach (string part in v.Split(',', ';', ' '))
            {
                string id = part.Trim();
                if (id.Length == 0) continue;
                string result = Set(id, true);
                DeLog.Info("DE.Mutant " + result + " (from " + EnvVar + ")");
            }
        }

        /// <summary>Switches a mutant; returns a log text naming the outcome.</summary>
        public static string Set(string id, bool on)
        {
            if (!BuildFlavor.IsDev) return "REFUSE " + id + ": release build";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S0.M8":
                    VersionFromCompiledConst = on;
                    return "S0.M8 " + (on ? "ON" : "OFF") + " (version check against the compiled SE_VERSION const)";
                case "S0.M5A":
                    IgnoreProcessExit = on;
                    return "S0.M5a " + (on ? "ON" : "OFF") + " (peer detector ignores process exit)";
                case "S0.M5B":
                    StallTimeout30s = on;
                    return "S0.M5b " + (on ? "ON" : "OFF") + " (stall timeout 30 s instead of 3 s)";
                case "S0.M5C":
                    HeartbeatLiveness = on;
                    return "S0.M5c " + (on ? "ON" : "OFF") + " (liveness by heartbeat change, identity by PID only)";
                default:
                    return S1Mutants.Set(id, on) ?? S2Mutants.Set(id, on) ?? S2bMutants.Set(id, on) ?? S3Mutants.Set(id, on) ?? S5aMutants.Set(id, on) ??
                           Terrain.S4Mutants.Set(id, on) ?? S5Mutants.Set(id, on) ?? Combat.S6Mutants.Set(id, on) ?? S7aPolicyMutants.Set(id, on) ??
                           S7aGhostMutants.Set(id, on) ?? S7Mutants.Set(id, on) ?? S8aMutants.Set(id, on) /* S8a (B2 hook) */ ?? S11SeMutants.Set(id, on) /* S11 hook */ ?? S16SeMutants.Set(id, on) /* S16 hook */ ?? S13SeMutants.Set(id, on) /* S13 hook */ ?? S14SeMutants.Set(id, on) /* S14 hook */ ?? S15SeMutants.Set(id, on) /* S15 hook */ ?? S12SeMutants.Set(id, on) /* S12 hook */ ??
                           "UNKNOWN " + id + " (SE-side mutants: S0.M5a, S0.M5b, S0.M5c, S0.M8, S1.M1, S1.M2, S1.M5, S1.M9, " +
                           "S2.M1, S2.M4, S2.M7, S2.M8, S2.M10, S2.M11, S2.M13, S2b.M1, S2b.M2, S2b.M3, S2b.M4, S3.M4, S5a.M1, S5a.M2, S5a.M3, " +
                           "S4.M2, S4.M3, S4.M4, S4.M5, S4.M6, S4.M7, S4.M8, S4.M8b, S4.M9.<path>, S4.M10, S5.M3, S6.M3, S6.M7, S7a.M1, S7a.M3, S7a.M4, " +
                           string.Join(", ", S7Mutants.All) + ", " + string.Join(", ", S8aMutants.All) + ")";
            }
        }
    }
}
