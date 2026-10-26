using System.Collections.Generic;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Seeded failures of slice S1 that live in the SE plugin (ROADMAP S1 "Seeded failures", §3 rule 1). Dev builds
 /// only; switched through <see cref="Mutants.Set"/> (environment variable <c>DEEPENGINEERS_MUTANTS</c> at Init, C11
 /// <c>de.mutant</c>, or <c>/de mutant</c>). Game-independent, so tests/DeepEngineers.SE.Tests can turn each one on.
 /// <list type="bullet">
 /// <item>S1.M1 naive tangent-plane map: C1 points are (P − C)·(E0, U0, N0) with y − Rs, directions use the anchor
 /// frame (no curvature). S1.WM-2 misses by about 19.2 m; S1.WM-3 drifts to about −133.3 m at 2 km.</item>
 /// <item>S1.M2 camera published from <c>IPlugin.Update</c> instead of the <c>MySandboxGame.PrepareForDraw</c> postfix:
 /// the camera of the previous tick, stamped with the QPC of the publication
 /// S1.N2 shifts by about one SE tick; S1.N1's
 /// transport (qpcCapture → qpcUsed) does not, by construction.</item>
 /// <item>S1.M5 Rs read plus one ulp: <c>DE.Site REFUSE Rs</c>.</item>
 /// <item>S1.M9 the 50271 handler registered late instead of in <c>LoadData</c>: at the first
 /// <c>UpdateBeforeSimulation</c>, after every session component's <c>BeforeStart</c>, so the Water Mod's single
 /// dictionary message (sent from its BeforeStart, VERIFIED mod source) is always missed: <c>DE.Site REFUSE API not
 /// received</c>. Registering in our own BeforeStart (ROADMAP's wording) would miss it only if the Water Mod's
 /// BeforeStart ran first, which depends on Dictionary enumeration order. Must be on before the
 /// world loads.</item>
 /// </list>
 /// </summary>
    internal static class S1Mutants
    {
        /// <summary>S1.M1: naive tangent-plane map for C1 points and the camera rotation.</summary>
        public static volatile bool NaiveTangentMap;

        /// <summary>S1.M2: C1 written from the plugin's Update (previous tick's camera).</summary>
        public static volatile bool CameraFromPluginUpdate;

        /// <summary>S1.M5: the sea radius read from the Water Mod is bumped by one float32 ulp.</summary>
        public static volatile bool RsPlusOneUlp;

        /// <summary>S1.M9: the Water Mod message handler is registered at the first UpdateBeforeSimulation (too late).</summary>
        public static volatile bool HandlerLate;

        public static string Active
        {
            get
            {
                var parts = new List<string>(4);
                if (NaiveTangentMap) parts.Add("S1.M1");
                if (CameraFromPluginUpdate) parts.Add("S1.M2");
                if (RsPlusOneUlp) parts.Add("S1.M5");
                if (HandlerLate) parts.Add("S1.M9");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            NaiveTangentMap = false;
            CameraFromPluginUpdate = false;
            RsPlusOneUlp = false;
            HandlerLate = false;
        }

        /// <summary>Switches an S1 mutant; returns the log text, or null when <paramref name="id"/> is not an S1 id.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S1.M1":
                    NaiveTangentMap = on;
                    return "S1.M1 " + state + " (naive tangent-plane map for C1 points and camera rotation)";
                case "S1.M2":
                    CameraFromPluginUpdate = on;
                    return "S1.M2 " + state + " (C1 published from IPlugin.Update: previous tick's camera, stamped at publication)";
                case "S1.M5":
                    RsPlusOneUlp = on;
                    return "S1.M5 " + state + " (Water Mod sea radius read plus one float32 ulp)";
                case "S1.M9":
                    HandlerLate = on;
                    return "S1.M9 " + state + " (50271 handler registered at the first UpdateBeforeSimulation instead of LoadData; takes effect at the next world load)";
                default:
                    return null;
            }
        }
    }
}
