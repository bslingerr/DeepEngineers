using System;
using System.Collections.Generic;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S14 seeded failures of the SE side (ROADMAP §14 "### S14"; docs/S14-BRIEF.md). Pure (compiled into SE.Tests for
 /// BuildFlavor); each turns exactly its analyzer row red (tests/s14/analyze_s14.py MUTANT_ROWS):
 /// <list type="bullet">
 /// <item>S14.M1 the jetpack silence is off (SelectSound keeps SE's answer under water: the idle loop goes on, S14.N1);</item>
 /// <item>S14.M2 the Arcade route is off (no implicit-effect delegate: SE's sounds stay dry under water in an Arcade world, S14.N2);</item>
 /// <item>S14.M3 the plugin writes the Water Mod's weapon configs back to FunctionalUnderwater false in memory (S14.N3);</item>
 /// <item>S14.M7 SE's own thruster sound keeps SE's level under water: no envelope, no level rule, not faded under Subnautica's
 /// loop (S14.N4E, S14.N4V, S14.N4S);</item>
 /// <item>S14.M8 (round 4, the left ear) SE's own matrix kept for 3D voices with 2 channels or a wrong channel count (pan.stereo
 /// off: S14.N4P red when SE's matrix is the one-sided one);</item>
 /// <item>S14.M9 (round 4, too muffled) under water SelectCue answers the pair's Real* recording where one exists (the stacked
 /// chain the round-4 fix prevents: S14.N2C red).</item>
 /// <item>S14.
 /// waterline hysteresis, dwell and ramp) instead of the eye: S14.N2E red.</item>
 /// </list>
 /// S14.M4 (listener), S14.M5 (envelope) and S14.M6 (jets, and the kit's thruster puffs back) are Subnautica's.
 /// </summary>
    internal static class S14SeMutants
    {
        public static volatile bool JetpackLoud, ArcadeOff, WeaponsReverted, ThrusterLoud, PanRaw, RealisticRecording, MuffleByMix;
        public static readonly string[] All = { "S14.M1", "S14.M2", "S14.M3", "S14.M7", "S14.M8", "S14.M9", "S14.M10" };
        public static readonly string[] SnSide = { "S14.M4", "S14.M5", "S14.M6" };

        public static string Active
        {
            get
            {
                var parts = new List<string>(4);
                if (JetpackLoud) parts.Add("S14.M1");
                if (ArcadeOff) parts.Add("S14.M2");
                if (WeaponsReverted) parts.Add("S14.M3");
                if (ThrusterLoud) parts.Add("S14.M7");
                if (PanRaw) parts.Add("S14.M8");
                if (RealisticRecording) parts.Add("S14.M9");
                if (MuffleByMix) parts.Add("S14.M10");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll() { JetpackLoud = ArcadeOff = WeaponsReverted = ThrusterLoud = PanRaw = RealisticRecording = MuffleByMix = false; }

        /// <summary>Switches an S14 SE mutant; the log text, or null when the id is not an S14 id this side owns.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            string k = (id ?? "").Trim().ToUpperInvariant();
            switch (k)
            {
                case "S14.M1": JetpackLoud = on; return "S14.M1 " + state + " (the jetpack silence is off: SelectSound keeps SE's answer while swimming)";
                case "S14.M2": ArcadeOff = on; return "S14.M2 " + state + " (no Arcade route: SE's sounds stay dry under water in an Arcade world)";
                case "S14.M3": WeaponsReverted = on; return "S14.M3 " + state + " (the Water Mod's weapon configs written back to FunctionalUnderwater false in memory)";
                case "S14.M7": ThrusterLoud = on; return "S14.M7 " + state + " (SE's own thruster sound at SE's level under water: no envelope, no fade under Subnautica's loop)";
                case "S14.M8": PanRaw = on; return "S14.M8 " + state + " (SE's own 3D matrix kept for 2-channel voices: pan.stereo off)";
                case "S14.M9": RealisticRecording = on; return "S14.M9 " + state + " (under water the pair's Real* recording plays under the effect: two low-passes)";
                case "S14.M10": MuffleByMix = on; return "S14.M10 " + state + " (the muffle follows the view's mix again: its waterline hysteresis, dwell and ramp)";
                default:
                    if (Array.IndexOf(SnSide, k) >= 0) return "REFUSE " + id.Trim() + ": a Subnautica-side S14 mutant (send de.mutant to Subnautica: /de sn mutant ...)";
                    return null;
            }
        }
    }
}
