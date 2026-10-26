using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>The five states of ARCHITECTURE §4.2 as the SE side names them (Approach split into Track and Prime).</summary>
    internal enum S2State
    {
        Space = 0,
        Track = 1,
        Prime = 2,
        Underwater = 3,
        Surfacing = 4,
    }

    /// <summary>
 /// The SE plugin's single coupling point to the view state machine of <c>DeepEngineers.Wire</c> (wire-s2:
 /// <see cref="ViewStateMachine"/>, <see cref="RenderAck"/>, <see cref="AudioMix"/>, <see cref="ViewTrace"/>; ROADMAP
 /// S2 "State machine as a pure function in Wire"). Everything the SE side needs from the machine's API goes through
 /// here, so a later change of Wire's signatures is reconciled in this file (and <see cref="ViewStateDriver"/>, which
 /// fills <see cref="ViewInputs"/>). The S2 seeded failures that live inside the machine (S2.M4, S2.M10, S2.M11) are
 /// passed as <see cref="ViewInputs.Mutants"/> bits.
 /// </summary>
    internal static class ViewStateAdapter
    {
        public const string Implementation = "DeepEngineers.Wire.ViewStateMachine (wire-s2)";

        public static void Step(ref ViewMachineState state, ref ViewInputs input, out ViewOutputs output)
        {
            ViewStateMachine.Step(ref state, ref input, out output);
        }

        /// <summary>The SE name of a (viewState, approachMode) pair.</summary>
        public static S2State StateOf(uint viewState, uint approachMode)
        {
            switch (viewState)
            {
                case ViewState.Approach: return approachMode == ApproachMode.Prime ? S2State.Prime : S2State.Track;
                case ViewState.Underwater: return S2State.Underwater;
                case ViewState.Surfacing: return S2State.Surfacing;
                default: return S2State.Space;
            }
        }

        public static string Name(S2State s)
        {
            switch (s)
            {
                case S2State.Space: return "Space";
                case S2State.Track: return "Track";
                case S2State.Prime: return "Prime";
                case S2State.Underwater: return "Underwater";
                case S2State.Surfacing: return "Surfacing";
                default: return "?";
            }
        }

        /// <summary>C5 viewState and approachMode of a forced state (<c>de.view force</c>): Prime kept while Underwater/Surfacing, as the machine does.</summary>
        public static void ToC5(S2State s, out uint viewState, out uint approachMode)
        {
            switch (s)
            {
                case S2State.Track: viewState = ViewState.Approach; approachMode = ApproachMode.Track; return;
                case S2State.Prime: viewState = ViewState.Approach; approachMode = ApproachMode.Prime; return;
                case S2State.Underwater: viewState = ViewState.Underwater; approachMode = ApproachMode.Prime; return;
                case S2State.Surfacing: viewState = ViewState.Surfacing; approachMode = ApproachMode.Prime; return;
                default: viewState = ViewState.Space; approachMode = ApproachMode.None; return;
            }
        }

        /// <summary>perfMode and targetFps of a forced state (the machine's own table: Idle/Track (0, 10), Full no cap, (0, 30) unfocused underwater).</summary>
        public static void Perf(S2State s, bool foreground, out uint perfMode, out uint targetFps)
        {
            switch (s)
            {
                case S2State.Space: perfMode = PerfMode.Idle; targetFps = ViewThresholds.IdleTargetFps; return;
                case S2State.Track: perfMode = PerfMode.Track; targetFps = ViewThresholds.TrackTargetFps; return;
                default:
                    perfMode = PerfMode.Full;
                    targetFps = s == S2State.Underwater && !foreground ? ViewThresholds.UnfocusedUnderwaterFps : ViewThresholds.FullTargetFps;
                    return;
            }
        }

        /// <summary>
 /// The render request bits of a forced state (design F: margins Prime/Underwater/Surfacing, skip Underwater; A′:
 /// lowered Underwater unless a menu). <c>de.view force</c> is a dev override and keeps S2's tables (the Waterline
 /// picture policy) for this and <see cref="Perf"/>; the S7a Site policy has no forced-state equivalent (its picture
 /// depends on the latch).
 /// </summary>
        public static uint ForcedRequest(S2State s, DisplayDesign mode, bool menuOpen)
        {
            if (mode == DisplayDesign.F)
            {
                bool margins = s == S2State.Prime || s == S2State.Underwater || s == S2State.Surfacing;
                bool skip = s == S2State.Underwater;
                uint r = 0;
                if (margins) r |= ViewRequest.Margins;
                if (skip) r |= ViewRequest.SceneSkip;
                else if (margins) r |= ViewRequest.AlphaFill;
                return r;
            }
            bool lowered = s == S2State.Underwater && !menuOpen;
            return lowered ? ViewRequest.Lowered | ViewRequest.SceneSkip : 0u;
        }

        /// <summary>The machine's mutant bits from the SE switches (S2.M4, S2.M10, S2.M11).</summary>
        public static uint MutantBits()
        {
            uint m = 0;
            if (S2Mutants.NoHysteresis) m |= ViewMutants.M4NoHysteresis;
            if (S2Mutants.SnReadyNeedsSettled) m |= ViewMutants.M10SettledAlways;
            if (S2Mutants.VolumesWithoutFocusGate) m |= ViewMutants.M11NoAudioGate;
            return m;
        }

        /// <summary>Wire.PeerState of the SE link's <see cref="PeerStatus"/> (same numbering: Unknown 0, Alive 1, Stalled 2, Exited 3).</summary>
        public static uint PeerOf(PeerStatus p)
        {
            switch (p)
            {
                case PeerStatus.Alive: return PeerState.Alive;
                case PeerStatus.Stalled: return PeerState.Stalled;
                case PeerStatus.Exited: return PeerState.Exited;
                default: return PeerState.Unknown;
            }
        }

        public static uint DisplayModeOf(DisplayDesign d)
        {
            return d == DisplayDesign.APrime ? DisplayMode.APrime : DisplayMode.F;
        }

        /// <summary>Names of <see cref="ViewBlock"/> bits (logs; allocates).</summary>
        public static string BlockedNames(uint bits)
        {
            if (bits == 0) return "none";
            var sb = new StringBuilder(64);
            Add(sb, bits, ViewBlock.NotPrime, "notPrime");
            Add(sb, bits, ViewBlock.Y, "y");
            Add(sb, bits, ViewBlock.Rho, "rho");
            Add(sb, bits, ViewBlock.Peer, "peer");
            Add(sb, bits, ViewBlock.Phase, "snPhase");
            Add(sb, bits, ViewBlock.AreaBuilt, "areaBuilt");
            Add(sb, bits, ViewBlock.Settle, "settle");
            Add(sb, bits, ViewBlock.Fresh, "fresh");
            Add(sb, bits, ViewBlock.Rect, "rectOk");
            Add(sb, bits, ViewBlock.UiHidden, "uiHidden");
            Add(sb, bits, ViewBlock.ZOrder, "zOrder");
            Add(sb, bits, ViewBlock.Dwell, "dwell");
            Add(sb, bits, ViewBlock.Crossing, "crossing");
            Add(sb, bits, ViewBlock.Render, "render(margins not presented)");
            Add(sb, bits, ViewBlock.Mode, "mode");
            Add(sb, bits, ViewBlock.Site, "site");
            // S7a picture-latch guards
            Add(sb, bits, ViewBlock.Band, "band(no frame since the Approach entry)");
            Add(sb, bits, ViewBlock.NotApproach, "notApproach");
            Add(sb, bits, ViewBlock.High, "high(y >= +300 m)");
            return sb.ToString();
        }

        private static void Add(StringBuilder sb, uint bits, uint bit, string name)
        {
            if ((bits & bit) == 0) return;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(name);
        }
    }
}
