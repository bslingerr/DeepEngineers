using System.Collections.Generic;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Seeded failures of slice S2 that live in the SE plugin (ROADMAP S2 "Seeded failures", §3 rule 1). Dev builds
 /// only; switched through <see cref="Mutants.Set"/> (environment variable <c>DEEPENGINEERS_MUTANTS</c> at Init, C11
 /// <c>de.mutant</c>, or <c>/de mutant</c>). Game-independent, so tests/DeepEngineers.SE.Tests can turn each one on.
 /// <list type="bullet">
 /// <item>S2.M1 (required) DWM call disabled: the render thread reports the margins as applied but never calls
 /// <c>DwmExtendFrameIntoClientArea</c>; T-D1 must report "opaque" and S2.N0 must miss the calibration colour.</item>
 /// <item>S2.M4 no hysteresis and no dwell: the view state machine switches at one waterline (−0.125 m) both ways
 /// with no minimum dwell; S2.N2 trace (a) must show switches inside the band. Passed to Wire's view state machine
 /// as the <c>Wire.ViewMutants.M4NoHysteresis</c> bit of <c>ViewInputs.Mutants</c> (<see cref="ViewStateAdapter.MutantBits"/>).</item>
 /// <item>S2.M7 margins off during Prime: <see cref="DisplayController"/> drops the machine's margins request while in
 /// Prime, so Subnautica is a fully covered window there; S2.N8 must show the covered stall. Under Wire's render
 /// acknowledgement the margins request is then never acknowledged, so Underwater entry is held as well.</item>
 /// <item>S2.M8 SE does not take the foreground at start (no <c>SetForegroundWindow(seHwnd)</c> at link-up); S2.N10
 /// must go red.</item>
 /// <item>S2.M10 <c>snReady</c> gated on <c>IsWorldSettled</c> (C7 streamingSettled required at all times, not only
 /// after a load, teleport, new generation or link recovery); S2.N7 (b) must be delayed beyond 0.5 s or never.</item>
 /// <item>S2.M11 SE volumes written every tick without the focus gate; S2.T-A1 must go red (SE audible while away).</item>
 /// <item>S2.M13 Water Mod volume through <c>/wvolume</c>: implemented as a logged refusal only. The chat command is
 /// never sent (it would persist <c>WaterClientSettings.xml</c>); the log line names what the mutant would have done.</item>
 /// </list>
 /// S2.M9 is a tooling mutant (a visible Notepad between the games while the walker runs); the Subnautica-side ids
 /// (M2, M3, M5, M6, M12, M14) belong to the SN plugin. Both are answered with a <c>NOT-SE</c> text.
 /// </summary>
    internal static class S2Mutants
    {
        /// <summary>S2.M1: DwmExtendFrameIntoClientArea is never called (the margins are reported as applied).</summary>
        public static volatile bool DwmCallDisabled;

        /// <summary>S2.M4: the view state machine has no waterline band and no minimum dwell.</summary>
        public static volatile bool NoHysteresis;

        /// <summary>S2.M7: no DWM margins while in Prime (only from Underwater on).</summary>
        public static volatile bool MarginsOffInPrime;

        /// <summary>S2.M8: SE does not take the foreground when the link comes up.</summary>
        public static volatile bool NoForegroundAtStart;

        /// <summary>S2.M10: snReady requires C7 streamingSettled at all times.</summary>
        public static volatile bool SnReadyNeedsSettled;

        /// <summary>S2.M11: SE volumes written every tick without the focus gate.</summary>
        public static volatile bool VolumesWithoutFocusGate;

        /// <summary>S2.M13: the Water Mod volume "through /wvolume" (logged refusal; never sent).</summary>
        public static volatile bool WaterVolumeByCommand;

        public static string Active
        {
            get
            {
                var parts = new List<string>(8);
                if (DwmCallDisabled) parts.Add("S2.M1");
                if (NoHysteresis) parts.Add("S2.M4");
                if (MarginsOffInPrime) parts.Add("S2.M7");
                if (NoForegroundAtStart) parts.Add("S2.M8");
                if (SnReadyNeedsSettled) parts.Add("S2.M10");
                if (VolumesWithoutFocusGate) parts.Add("S2.M11");
                if (WaterVolumeByCommand) parts.Add("S2.M13");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            DwmCallDisabled = false;
            NoHysteresis = false;
            MarginsOffInPrime = false;
            NoForegroundAtStart = false;
            SnReadyNeedsSettled = false;
            VolumesWithoutFocusGate = false;
            WaterVolumeByCommand = false;
        }

        /// <summary>
 /// Switches an S2 mutant; returns the log text, or null when <paramref name="id"/> is not an S2 id. Ids that are
 /// S2 but not SE-side return a text starting with <c>NOT-SE</c> (C11 answers ok=false; nothing is switched).
 /// </summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            string key = (id ?? "").Trim().ToUpperInvariant();
            switch (key)
            {
                case "S2.M1":
                    DwmCallDisabled = on;
                    return "S2.M1 " + state + " (DwmExtendFrameIntoClientArea never called; margins reported as applied)";
                case "S2.M4":
                    NoHysteresis = on;
                    return "S2.M4 " + state + " (view state machine: one waterline at -0.125 m both ways, no dwell)";
                case "S2.M7":
                    MarginsOffInPrime = on;
                    return "S2.M7 " + state + " (no DWM margins during Prime; on only from Underwater)";
                case "S2.M8":
                    NoForegroundAtStart = on;
                    return "S2.M8 " + state + " (SE does not take the foreground when the link comes up)";
                case "S2.M10":
                    SnReadyNeedsSettled = on;
                    return "S2.M10 " + state + " (snReady requires C7 streamingSettled at all times)";
                case "S2.M11":
                    VolumesWithoutFocusGate = on;
                    return "S2.M11 " + state + " (SE volumes written every tick without the focus gate)";
                case "S2.M13":
                    WaterVolumeByCommand = on;
                    return "S2.M13 " + state + " (Water Mod volume 'through /wvolume': a logged refusal, the command is never sent)";
                case "S2.M9":
                    return "NOT-SE S2.M9 is a tooling mutant (a visible Notepad placed between the games while the walker runs); nothing to switch in the SE plugin";
                case "S2.M2":
                case "S2.M3":
                case "S2.M5":
                case "S2.M6":
                case "S2.M12":
                case "S2.M14":
                    return "NOT-SE " + key + " is a Subnautica-side seeded failure (SN plugin); nothing switched here";
                default:
                    return null;
            }
        }
    }
}
