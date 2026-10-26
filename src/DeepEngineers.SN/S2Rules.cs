using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S2 "display + input + audio", Subnautica side: the Unity-free decisions (ROADMAP S2 SN plugin; ARCHITECTURE §4,
 // §5.1-§5.3, §5.13, §5.14; CONTRACT §1, §5.4 C5/C7, §7). Everything here is pure C# so that
 // tests/DeepEngineers.SN.
 // FreezeTime pair; S2.M12 = one pair per cause), the audio mute and crossfade, the rectangle comparison, the window
 // style bits, the biome tile test, the calibration colour parser and the S2 switches. The Unity and Win32 sides live
 // in SnWindow.cs, PerfRecipes.cs, FreezeOwner.cs, AudioMix.cs, S2Patches.cs, InputWatch.cs, CalibrationPattern.cs
 // and S2Host.cs.
 // =================================================================================================================

 /// <summary>What the recipe selection reads (C5 from the current SE generation, the peer watchdog, the mutants).</summary>
    internal struct RecipeInputs
    {
        /// <summary>A C5 written by the current SE generation has been read (CONTRACT §6: never one left by an earlier SE).</summary>
        public bool C5Valid;
        /// <summary>SePeerMonitor says Alive (not Stalled, Exited, Detached, Stale or None).</summary>
        public bool PeerAlive;
        public uint PerfMode;
        public uint TargetFps;
        public uint ViewState;
        public uint ApproachMode;
        /// <summary>C5 flags bit 0 seForeground set and overlay Unfocused clear.</summary>
        public bool SeForeground;
        /// <summary>
 /// S7a: C5 flags bit 3 seeThrough (Subnautica's picture shown: F scene skipped or A′ lowered). The unfocused
 /// (0, 30) rule fires on it (S2: on viewState = Underwater, the same set under the Waterline policy).
 /// </summary>
        public bool SeeThrough;
        /// <summary>Seeded failure S2.M6: the Idle recipe is not applied (no freeze for Idle, cameras on, user pair, Normal).</summary>
        public bool IdleRecipeOff;
        /// <summary>Seeded failure S2.M14: vSyncCount left at 1 in Idle (Unity then ignores targetFrameRate).</summary>
        public bool VSyncLeftInIdle;
        /// <summary>The user's values saved at load (restored on quit; used by S2.M6).</summary>
        public int UserVSync, UserTargetFps;
    }

    /// <summary>The recipe Subnautica applies (C7 perfModeApplied, vSyncCountApplied, targetFrameRateApplied).</summary>
    internal struct RecipeDecision
    {
        /// <summary>PerfMode.Idle, Track or Full (the C5/C7 enumeration).</summary>
        public uint Recipe;
        public int VSyncCount;
        public int TargetFrameRate;
        /// <summary>Main and GUI camera components enabled.</summary>
        public bool CamerasOn;
        /// <summary>Process priority class BelowNormal (else the original class, at most Normal).</summary>
        public bool BelowNormal;
        /// M6).</summary>
        public bool IdleCause;
        public string Why;

        public string Pair { get { return "(" + VSyncCount.ToString(CultureInfo.InvariantCulture) + ", " + TargetFrameRate.ToString(CultureInfo.InvariantCulture) + ")"; } }
    }

    /// <summary>
 /// Idle / Track / Full recipes (ARCHITECTURE §4.2, §5.13; ROADMAP S2 state table). The recipe follows C5
 /// <c>perfMode</c> (CONTRACT §5.4: "Subnautica mode = C5 perfMode"); without a live SE and a C5 of its generation
 /// Subnautica idles (CONTRACT §7 "Start": Idle until SE says otherwise; SE stalled → Idle).
 /// <list type="bullet">
 /// </item>
 /// <item>Track: unfrozen, cameras off, (0, 10), Normal; the ghost and the camera transform keep following C1, so
 /// streaming follows.</item>
 /// <item>Full: cameras on; C5 targetFps 0 → (1, −1) (vsync at the monitor rate, "start with (1, −1)"); targetFps
 /// &gt; 0 → (0, targetFps) (the CPU-bound variant, e.g. 120); Subnautica's picture shown (C5 seeThrough) with SE not
 /// foreground → (0, 30) whatever targetFps says (ARCHITECTURE §4.3 Unfocused overlay, CONTRACT §7 Alt+Tab; S2: "if
 /// Underwater", which is the seeThrough set under the Waterline policy; S7a Site: Approach under the picture latch
 /// too; the SE machine also publishes targetFps 30 then).</item>
 /// </list>
 /// S7a: under the Site picture policy SE never asks for Track (Full in every non-Space state); the Track recipe stays
 /// for the Waterline policy (the fallback).
 /// </summary>
    internal static class PerfRecipeRules
    {
        public const int IdleFps = 10;
        public const int TrackFps = 10;
        public const int UnfocusedFullFps = 30;
        public const int MinCappedFps = 10, MaxCappedFps = 500;

        public static RecipeDecision Select(ref RecipeInputs i)
        {
            if (!i.C5Valid || !i.PeerAlive)
                return Idle(ref i, !i.C5Valid ? "no C5 from a live SE yet" : "SE not alive");
            switch (i.PerfMode)
            {
                case Wire.PerfMode.Idle: return Idle(ref i, "C5 perfMode Idle");
                case Wire.PerfMode.Track:
                    return new RecipeDecision
                    {
                        Recipe = Wire.PerfMode.Track, VSyncCount = 0, TargetFrameRate = TrackFps, CamerasOn = false,
                        BelowNormal = false, IdleCause = false, Why = "C5 perfMode Track",
                    };
                case Wire.PerfMode.Full:
                {
                    var d = new RecipeDecision { Recipe = Wire.PerfMode.Full, CamerasOn = true, BelowNormal = false, IdleCause = false };
                    if (!i.SeForeground && i.SeeThrough)
                    {
                        d.VSyncCount = 0; d.TargetFrameRate = UnfocusedFullFps; d.Why = "C5 perfMode Full, Subnautica's picture shown (seeThrough) and SE not foreground (Unfocused overlay)";
                    }
                    else if (i.TargetFps == 0)
                    {
                        d.VSyncCount = 1; d.TargetFrameRate = -1; d.Why = "C5 perfMode Full, targetFps 0 (vsync at the monitor rate)";
                    }
                    else
                    {
                        int fps = (int)Math.Min(MaxCappedFps, Math.Max(MinCappedFps, i.TargetFps));
                        d.VSyncCount = 0; d.TargetFrameRate = fps; d.Why = "C5 perfMode Full, targetFps " + i.TargetFps + " (capped, no vsync)";
                    }
                    return d;
                }
                default:
                    return Idle(ref i, "unknown C5 perfMode " + i.PerfMode);
            }
        }

        private static RecipeDecision Idle(ref RecipeInputs i, string why)
        {
            if (i.IdleRecipeOff)
                return new RecipeDecision
                {
                    Recipe = Wire.PerfMode.Idle, VSyncCount = i.UserVSync, TargetFrameRate = i.UserTargetFps, CamerasOn = true,
                    BelowNormal = false, IdleCause = false, Why = why + "; MUTANT S2.M6 idle recipe off",
                };
            return new RecipeDecision
            {
                Recipe = Wire.PerfMode.Idle, VSyncCount = i.VSyncLeftInIdle ? 1 : 0, TargetFrameRate = IdleFps, CamerasOn = false,
                BelowNormal = true, IdleCause = true, Why = why + (i.VSyncLeftInIdle ? "; MUTANT S2.M14 vSyncCount left at 1" : ""),
            };
        }

        /// <summary>
 /// The perfMode the view state implies (ARCHITECTURE §4.2), for the consistency log and the de.s2c5 defaults:
 /// Idle in Space; under the S7a Site policy (C5 flag bit 9 <paramref name="pictureSite"/>) Full in every other
 /// state; under Waterline (S2) Track in Approach/Track and Full in Prime, Underwater and Surfacing.
 /// </summary>
        public static uint ExpectedPerfMode(uint viewState, uint approachMode, bool pictureSite)
        {
            switch (viewState)
            {
                case Wire.ViewState.Space: return Wire.PerfMode.Idle;
                case Wire.ViewState.Approach:
                    if (pictureSite) return Wire.PerfMode.Full;
                    return approachMode == Wire.ApproachMode.Prime ? Wire.PerfMode.Full : Wire.PerfMode.Track;
                case Wire.ViewState.Underwater:
                case Wire.ViewState.Surfacing: return Wire.PerfMode.Full;
                default: return Wire.PerfMode.Idle;
            }
        }

        public static string Name(uint perfMode)
        {
            switch (perfMode)
            {
                case Wire.PerfMode.Idle: return "Idle";
                case Wire.PerfMode.Track: return "Track";
                case Wire.PerfMode.Full: return "Full";
                default: return "Unknown(" + perfMode + ")";
            }
        }

        public static string ViewName(uint viewState, uint approachMode)
        {
            switch (viewState)
            {
                case Wire.ViewState.Space: return "Space";
                case Wire.ViewState.Approach:
                    return approachMode == Wire.ApproachMode.Prime ? "Approach/Prime" : approachMode == Wire.ApproachMode.Track ? "Approach/Track" : "Approach/None";
                case Wire.ViewState.Underwater: return "Underwater";
                case Wire.ViewState.Surfacing: return "Surfacing";
                default: return "Unknown(" + viewState + ")";
            }
        }
    }

    /// <summary>
 /// snPhase mapping of S2 (CONTRACT §5.4 "C5/C7 semantics for S2" table: Space → ReadyIdle, Approach/Track →
 /// WarmingUp, Approach/Prime → WarmingUp until the Full recipe has rendered, then Active; Underwater and Surfacing →
 /// Active; SE stalled → Subnautica applies Idle itself and shows ReadyIdle). S7a: unchanged; under the Site policy
 /// Approach/Track asks for Full, so Track too goes WarmingUp → Active once Full has rendered:
 /// <list type="bullet">
 /// <item>ReadyIdle: in the host world, and C5 says Space (or there is no live SE): S1's ReadyIdle, camera still
 /// driven by C1, whatever recipe C5 perfMode selects.</item>
 /// <item>WarmingUp: C5 says Approach (Track or Prime), Underwater or Surfacing (from a live SE), but the Full recipe
 /// is not applied (Track: never), or fewer than <see cref="WarmFrames"/> frames were presented by the main camera
 /// since it was.</item>
 /// <item>Active: Full applied with the cameras on and at least <see cref="WarmFrames"/> presented frames since; kept
 /// while the view state wants it and Full stays applied (no flapping on a held frame: freshness is SE's check through
 /// C7 presentedSeTick).</item>
 /// </list>
 /// Integration change: sn-s2 kept Approach/Track at ReadyIdle; CONTRACT §5.4 (written in the same pass)
 /// maps Track to WarmingUp, and CONTRACT decides. Phases below ReadyIdle (Booting, Menu, LoadingSave) belong to S0/S1,
 /// Quitting to the lifecycle: never changed here.
 /// </summary>
    internal static class PhaseRules
    {
        public const long WarmFrames = 2;

        /// <summary>SE asks for more than Idle: Approach (Track or Prime), Underwater or Surfacing from a live SE.</summary>
        public static bool WarmWanted(bool c5Valid, bool peerAlive, uint viewState, uint approachMode)
        {
            if (!c5Valid || !peerAlive) return false;
            return viewState == Wire.ViewState.Underwater || viewState == Wire.ViewState.Surfacing ||
                   viewState == Wire.ViewState.Approach;
        }

        public static uint Target(uint current, bool worldReady, bool quitting, bool warmWanted, bool fullApplied, long framesSinceFull)
        {
            if (quitting || !worldReady) return current;
            if (current < SnPhase.ReadyIdle || current > SnPhase.Active) return current;
            if (!warmWanted) return SnPhase.ReadyIdle;
            if (!fullApplied) return SnPhase.WarmingUp;
            if (current == SnPhase.Active) return SnPhase.Active;
            return framesSinceFull >= WarmFrames ? SnPhase.Active : SnPhase.WarmingUp;
        }
    }

    /// <summary>
 /// C7 <c>streamingSettled</c> (CONTRACT §5.4 C7 bit 1): <c>LargeWorldStreamer.IsWorldSettled</c> seen on a busy → idle
 /// edge (or idle for 1 s) since the last jump. <c>S1Telemetry</c> runs a probe after every camera jump (&gt; 20 m in
 /// one frame, a C1 snap with a move, de.warpcam) that records exactly that edge; while the
 /// probe has not seen it, the raw idle of the first frames after the jump (the streamer has not started yet) is not
 /// reported. Without a running probe the raw value stands. SE's state machine additionally needs C7
 /// <c>areaBuiltSeTick</c> ≥ the event tick (Wire.ViewStateMachine).
 /// </summary>
    internal static class SettleRules
    {
        public static bool Report(bool isWorldSettled, bool probeRunning, bool probeSawSettle)
        {
            return isWorldSettled && (!probeRunning || probeSawSettle);
        }
    }

    /// <summary>One FreezeTime id, as UWE.FreezeTime treats it.</summary>
    internal interface IFreezeApi
    {
        bool Held { get; }
        void Begin();
        void End();
    }

    /// <summary>
 /// 3, CONTRACT §1 "Pause"): <c>freeze = Idle ∨ Paused ∨ SE stalled</c>
 /// computed each frame; <c>FreezeTime.Begin(FeedbackPanel)</c> is called only when that boolean becomes true and
 /// <c>End(FeedbackPanel)</c> only when it becomes false.
 /// (someone else ended the id), it is begun again and counted. Seeded failure S2.M12: one Begin/End pair per cause on
 /// the same id; because FreezeTime keys freezers by id, the first cause that ends unfreezes Subnautica although
 /// another cause still holds (ESC and resume in Space → unfrozen in Space).
 /// </summary>
    internal sealed class FreezeOwnerCore
    {
        private readonly IFreezeApi _api;
        private bool _owned;
        private bool _idle, _paused, _stalled;

        public bool PerCauseMutant;
        public long Begins, Ends, Reasserts, Calls;
        public string LastReason = "";

        public FreezeOwnerCore(IFreezeApi api) { _api = api; }

        public bool Wanted { get; private set; }
        public bool Frozen { get { return _api.Held; } }
        public bool Owned { get { return _owned; } }

        public static bool Want(bool idle, bool paused, bool stalled) { return idle || paused || stalled; }

        public static string Causes(bool idle, bool paused, bool stalled)
        {
            if (!idle && !paused && !stalled) return "none";
            var parts = new List<string>(3);
            if (idle) parts.Add("Idle");
            if (paused) parts.Add("Paused");
            if (stalled) parts.Add("SEStalled");
            return string.Join("+", parts.ToArray());
        }

        /// <summary>One frame. True when a FreezeTime call was made (the audio mix is re-applied after it).</summary>
        public bool Update(bool idle, bool paused, bool stalled)
        {
            bool want = Want(idle, paused, stalled);
            Wanted = want;
            bool called = false;
            if (!PerCauseMutant)
            {
                if (want && !_owned)
                {
                    _api.Begin(); _owned = true; Begins++; called = true;
                    LastReason = "Begin (" + Causes(idle, paused, stalled) + ")";
                }
                else if (!want && _owned)
                {
                    _api.End(); _owned = false; Ends++; called = true;
                    LastReason = "End (no cause left)";
                }
                else if (want && _owned && !_api.Held)
                {
                    _api.Begin(); Reasserts++; called = true;
                    LastReason = "Begin again (the entry was gone; " + Causes(idle, paused, stalled) + ")";
                }
            }
            else
            {
                called |= Edge(_idle, idle, "Idle");
                called |= Edge(_paused, paused, "Paused");
                called |= Edge(_stalled, stalled, "SEStalled");
                _owned = _api.Held;
            }
            _idle = idle; _paused = paused; _stalled = stalled;
            if (called) Calls++;
            return called;
        }

        private bool Edge(bool was, bool now, string cause)
        {
            if (now == was) return false;
            if (now) { _api.Begin(); Begins++; LastReason = "MUTANT S2.M12 Begin for " + cause; }
            else { _api.End(); Ends++; LastReason = "MUTANT S2.M12 End for " + cause; }
            return true;
        }

        /// True when End was called.</summary>
        public bool Release()
        {
            if (!_owned && !(PerCauseMutant && _api.Held)) return false;
            _api.End();
            _owned = false;
            Ends++;
            Calls++;
            LastReason = "End (released)";
            return true;
        }
    }

    /// <summary>
 /// Subnautica's audio mix (ARCHITECTURE §5.3; CONTRACT §1 "Audio mix state: SE decides (C5), each side applies it"):
 /// the master bus <c>bus:/</c> volume follows C5 <c>snBusTarget</c> with a linear crossfade of C5
 /// <c>crossfadeMs</c> (300) on the unscaled clock; the bus is muted (RuntimeManager.MuteAllEvents, a mute FreezeTime
 /// never touches) whenever there is no live SE, SE is not foreground, the bridge is quitting, or the bus is faded
 /// out to a target of 0 (above water). Seeded failure S2.M3: the above-water mute is skipped (target 0 is taken as 1).
 /// </summary>
    internal static class MixRules
    {
        public const float DefaultCrossfadeMs = 300f;
        public const float MaxCrossfadeMs = 10000f;

        public static float CrossfadeMs(float c5Value)
        {
            if (float.IsNaN(c5Value) || c5Value <= 0f || c5Value > MaxCrossfadeMs) return DefaultCrossfadeMs;
            return c5Value;
        }

        public static float ClampTarget(float t)
        {
            if (float.IsNaN(t) || t <= 0f) return 0f;
            return t >= 1f ? 1f : t;
        }

        /// <summary>The bus target actually used: C5 snBusTarget, or 1 under S2.M3 when C5 asks for 0 (above water).</summary>
        public static float EffectiveTarget(bool valid, float c5Target, bool aboveWaterMuteSkipped)
        {
            if (!valid) return 0f;
            float t = ClampTarget(c5Target);
            if (t <= 0f && aboveWaterMuteSkipped) return 1f;
            return t;
        }

        public static bool ShouldMute(bool valid, bool seForeground, bool quitting, float value, float effectiveTarget, out string why)
        {
            if (quitting) { why = "quitting"; return true; }
            if (!valid) { why = "no live SE"; return true; }
            if (!seForeground) { why = "SE not foreground"; return true; }
            if (value <= 0f && effectiveTarget <= 0f) { why = "bus target 0 (above water)"; return true; }
            why = "audible";
            return false;
        }
    }

    /// <summary>Linear crossfade at a constant rate (a full 0 ↔ 1 swing takes the crossfade time; an interrupted fade turns around in place).</summary>
    internal sealed class Crossfade
    {
        private bool _started;
        private ulong _lastMs;

        public float Value { get; private set; }
        public float Target { get; private set; }
        public float DurationMs { get; private set; }
        public long Fades;

        public Crossfade(float initial)
        {
            Value = initial;
            Target = initial;
            DurationMs = MixRules.DefaultCrossfadeMs;
        }

        public bool Fading { get { return Value != Target; } }

        /// <summary>Advances to <paramref name="nowMs"/> toward <paramref name="target"/>; returns the new value.</summary>
        public float Step(float target, float durationMs, ulong nowMs)
        {
            if (target != Target) { Target = target; Fades++; }
            DurationMs = durationMs > 0f ? durationMs : MixRules.DefaultCrossfadeMs;
            if (!_started) { _started = true; _lastMs = nowMs; }
            ulong dt = nowMs > _lastMs ? nowMs - _lastMs : 0;
            _lastMs = nowMs;
            if (Value == Target) return Value;
            float step = dt / DurationMs;
            if (Value < Target) Value = Math.Min(Target, Value + step);
            else Value = Math.Max(Target, Value - step);
            return Value;
        }

        /// <summary>Jumps to a value (mute at quit, a new SE generation).</summary>
        public void Set(float value, ulong nowMs)
        {
            Value = value;
            Target = value;
            _lastMs = nowMs;
            _started = true;
        }
    }

    /// <summary>A Win32 RECT in screen pixels: left, top, right, bottom (right and bottom exclusive).</summary>
    internal struct RectI : IEquatable<RectI>
    {
        public int L, T, R, B;

        public RectI(int l, int t, int r, int b) { L = l; T = t; R = r; B = b; }

        public int W { get { return R - L; } }
        public int H { get { return B - T; } }
        public bool Empty { get { return R <= L || B <= T; } }

        public bool Equals(RectI o) { return L == o.L && T == o.T && R == o.R && B == o.B; }
        public override bool Equals(object obj) { return obj is RectI && Equals((RectI)obj); }
        public override int GetHashCode() { return ((L * 397 ^ T) * 397 ^ R) * 397 ^ B; }

        public bool Contains(int x, int y) { return x >= L && x < R && y >= T && y < B; }

        /// <summary>C5 seRect / C7 snRect: i32[4] = (left, top, right, bottom). CONTRACT names no order; this is our reading.</summary>
        public static RectI From(I32x4 v) { return new RectI(v.x, v.y, v.z, v.w); }
        public I32x4 ToI32x4() { return new I32x4(L, T, R, B); }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return "(" + L.ToString(ci) + "," + T.ToString(ci) + "," + R.ToString(ci) + "," + B.ToString(ci) + " " + W.ToString(ci) + "x" + H.ToString(ci) + ")";
        }
    }

    internal enum RectVerdict
    {
        /// <summary>snRect equals seRect: C7 rectOk.</summary>
        Ok = 0,
        /// <summary>No seRect yet (no C5 from a live SE, or all zeros).</summary>
        NoSeRect = 1,
        /// <summary>Our window has no client rectangle (no HWND, minimised).</summary>
        NoSnRect = 2,
        /// <summary>Same monitor, SE covers that monitor, our size differs: Screen.SetResolution(w, h, FullScreenWindow).</summary>
        CorrectSize = 3,
        /// <summary>Our window is on another monitor than SE's: Unity 2019.</summary>
        OtherMonitor = 4,
        /// <summary>SE's rectangle is not a whole monitor (SE windowed): a fullscreen-window Subnautica cannot match it.</summary>
        SeNotMonitorSized = 5,
    }

    /// <summary>The once-per-second rectangle check (ARCHITECTURE §5.1 "Monitor and rectangle"; C7 rectOk).</summary>
    internal static class RectRules
    {
        public static RectVerdict Compare(RectI sn, RectI se, RectI snMonitor, RectI seMonitor)
        {
            if (se.Empty) return RectVerdict.NoSeRect;
            if (sn.Empty) return RectVerdict.NoSnRect;
            if (sn.Equals(se)) return RectVerdict.Ok;
            if (!snMonitor.Equals(seMonitor)) return RectVerdict.OtherMonitor;
            if (!se.Equals(seMonitor)) return RectVerdict.SeNotMonitorSized;
            return RectVerdict.CorrectSize;
        }

        public static string Describe(RectVerdict v)
        {
            switch (v)
            {
                case RectVerdict.Ok: return "ok";
                case RectVerdict.NoSeRect: return "no seRect";
                case RectVerdict.NoSnRect: return "no snRect";
                case RectVerdict.CorrectSize: return "size differs (correctable)";
                case RectVerdict.OtherMonitor: return "other monitor (not correctable at run time)";
                case RectVerdict.SeNotMonitorSized: return "SE not monitor-sized (not correctable)";
                default: return v.ToString();
            }
        }
    }

    /// <summary>Spacing and budget of the run-time size corrections (never a loop of SetResolution calls).</summary>
    internal sealed class RectCorrector
    {
        public const int MaxAttempts = 3;
        public const ulong MinSpacingMs = 5000;

        private ulong _lastMs;
        public int Attempts { get; private set; }
        public bool Exhausted { get { return Attempts >= MaxAttempts; } }

        public bool ShouldAttempt(RectVerdict v, bool enabled, ulong nowMs)
        {
            if (!enabled || v != RectVerdict.CorrectSize || Attempts >= MaxAttempts) return false;
            if (Attempts > 0 && nowMs - _lastMs < MinSpacingMs) return false;
            return true;
        }

        public void Note(ulong nowMs)
        {
            Attempts++;
            _lastMs = nowMs;
        }
    }

    /// <summary>
 /// The window styles of the Subnautica window (ARCHITECTURE §5.1, §5.2): WS_EX_NOACTIVATE (never activated by a
 /// click) and WS_EX_TOOLWINDOW (no taskbar button, no Alt+Tab entry). WS_EX_APPWINDOW is cleared as well: the shell
 /// gives an unowned window with WS_EX_APPWINDOW a taskbar button whatever its other styles (Microsoft "Managing
 /// Taskbar Buttons"); not named by ROADMAP (our addition).
 /// </summary>
    internal static class WindowStyleRules
    {
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const long WS_EX_APPWINDOW = 0x00040000;
        public const long WS_EX_NOACTIVATE = 0x08000000;
        public const long WS_EX_TOPMOST = 0x00000008;
        public const long Required = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

        public static bool HasRequired(long exStyle) { return (exStyle & Required) == Required && (exStyle & WS_EX_APPWINDOW) == 0; }

        public static long WithRequired(long exStyle) { return (exStyle | Required) & ~WS_EX_APPWINDOW; }

        /// <summary>
 /// Review fix (finding 3): SE shows Subnautica's window (C5 of a live SE: margins on, see-through
 /// (F skip or A′ lowered), or any view state but Space), so the restyle must not hide it even for a moment.
 /// S7a: unchanged and already right for the Site picture policy.
 /// </summary>
        public static bool SeShowsSubnautica(bool c5Valid, uint viewState, uint c5Flags)
        {
            if (!c5Valid) return false;
            if ((c5Flags & (SeStateFlags.MarginsOn | SeStateFlags.SeeThrough)) != 0) return true;
            return viewState != ViewState.Space;
        }

        public static string Describe(long exStyle)
        {
            return "0x" + exStyle.ToString("x8", CultureInfo.InvariantCulture) + "[" + ((exStyle & WS_EX_NOACTIVATE) != 0 ? "NOACTIVATE " : "") +
                   ((exStyle & WS_EX_TOOLWINDOW) != 0 ? "TOOLWINDOW " : "") + ((exStyle & WS_EX_APPWINDOW) != 0 ? "APPWINDOW " : "") +
                   ((exStyle & WS_EX_TOPMOST) != 0 ? "TOPMOST" : "") + "]";
        }
    }

    /// <summary>
 /// The biome outside the Subnautica tile (ROADMAP S2 SN item; probe-world-mapping §1.3; ARCHITECTURE §4.2 OUTER VOID):
 /// <c>LargeWorld.GetBiome(Vector3)</c> wraps east of the tile and returns null north and south of it (VERIFIED code
 /// reading); the S2 patch answers "void" for any position outside the tile. The tile is the 4,096 m biome map centred on
 /// SN (0, 0): x and z in [−2,048, +2,048) (SiteV1.SnTileHalf; the land origin is logged once by the patch so the real
 /// run can confirm it).
 /// </summary>
    internal static class BiomeRules
    {
        public const string VoidBiome = "void";

        public static bool OutsideTile(double x, double z)
        {
            double h = SiteV1.SnTileHalf;
            if (double.IsNaN(x) || double.IsNaN(z)) return false;
            return x < -h || x >= h || z < -h || z >= h;
        }

        /// <summary>
 /// Review fix (finding 9): the "void" answer also turns on Subnautica's <c>VoidGhostLeviathansSpawner</c>
 /// (it spawns ghost leviathans around a player whose biome string is "void"), while ARCHITECTURE §4.2 says OUTER
 /// VOID is empty water. Default: the spawner's update is skipped while the player is outside the tile (switch
 /// <c>DEEPENGINEERS_SN_VOID_LEVIATHANS=on</c> keeps the vanilla behaviour; Inside the tile the
 /// vanilla spawner runs unchanged.
 /// </summary>
        public static bool SuppressVoidSpawner(bool switchOn, double playerX, double playerZ)
        {
            return !switchOn && OutsideTile(playerX, playerZ);
        }

        /// <summary>
 /// Arguments of <c>de.biome</c>: <c>{"x".., "y"..,
 /// "z"..}</c> in SN world metres, or from the SE chat relay <c>{"argv": ["2500", "-50", "0"]}</c>.
 /// </summary>
        public static bool TryParseQuery(object args, out double x, out double y, out double z, out string error)
        {
            x = y = z = 0; error = null;
            object a = S1Args.Unwrap(args);
            if (!S1Args.TryDouble(S1Args.Get(a, "x", 0), out x) || !S1Args.TryDouble(S1Args.Get(a, "y", 1), out y) ||
                !S1Args.TryDouble(S1Args.Get(a, "z", 2), out z) || double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z))
            {
                error = "args {\"x\": m, \"y\": m, \"z\": m} (SN world metres)";
                return false;
            }
            return true;
        }
    }

    /// <summary>
 /// Arguments of <c>de.calib</c> (S2.N0 calibration pattern): <c>{"colour": "#rrggbb"|"rrggbb"|"r,g,b"|[r, g, b]|name|"off"}</c>
 /// (<c>"color"</c> accepted too), or from the SE chat relay <c>{"argv": ["ff00ff"]}</c> / <c>["255", "0", "255"]</c> /
 /// <c>["off"]</c>. Names: red, green, blue, magenta, cyan, yellow, white, black.
 /// </summary>
    internal static class CalibRules
    {
        public static bool TryParse(object args, out bool off, out byte r, out byte g, out byte b, out string error)
        {
            off = false; r = g = b = 0; error = null;
            object a = S1Args.Unwrap(args);
            object v = null;
            var dict = a as Dictionary<string, object>;
            var list = a as List<object>;
            if (dict != null)
            {
                if (!dict.TryGetValue("colour", out v)) dict.TryGetValue("color", out v);
            }
            else if (list != null)
            {
                if (list.Count == 3) v = list;
                else if (list.Count == 1) v = list[0];
            }
            else if (a is string) v = a;

            var vl = v as List<object>;
            if (vl != null)
            {
                if (vl.Count != 3) { error = "a colour list needs 3 values"; return false; }
                int[] c = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    double d;
                    if (!S1Args.TryDouble(vl[k], out d) || d < 0 || d > 255 || d != Math.Floor(d)) { error = "colour components are integers 0-255"; return false; }
                    c[k] = (int)d;
                }
                r = (byte)c[0]; g = (byte)c[1]; b = (byte)c[2];
                return true;
            }
            var s = v as string;
            if (s == null) { error = "args {\"colour\": \"#rrggbb\"|\"r,g,b\"|[r,g,b]|name|\"off\"}"; return false; }
            s = s.Trim();
            if (string.Equals(s, "off", StringComparison.OrdinalIgnoreCase)) { off = true; return true; }
            switch (s.ToLowerInvariant())
            {
                case "red": r = 255; return true;
                case "green": g = 255; return true;
                case "blue": b = 255; return true;
                case "magenta": r = 255; b = 255; return true;
                case "cyan": g = 255; b = 255; return true;
                case "yellow": r = 255; g = 255; return true;
                case "white": r = g = b = 255; return true;
                case "black": return true;
            }
            if (s.IndexOf(',') >= 0)
            {
                string[] parts = s.Split(',');
                var tmp = new List<object>(3);
                foreach (string p in parts) tmp.Add(p.Trim());
                var wrapped = new Dictionary<string, object>(StringComparer.Ordinal) { { "colour", tmp } };
                return TryParse(wrapped, out off, out r, out g, out b, out error);
            }
            string hex = s.StartsWith("#", StringComparison.Ordinal) ? s.Substring(1) : s;
            int rgb;
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb))
            {
                error = "unknown colour '" + s + "' (use #rrggbb, r,g,b, a name or off)";
                return false;
            }
            r = (byte)((rgb >> 16) & 0xFF); g = (byte)((rgb >> 8) & 0xFF); b = (byte)(rgb & 0xFF);
            return true;
        }

        public static string Hex(byte r, byte g, byte b)
        {
            return "#" + r.ToString("x2", CultureInfo.InvariantCulture) + g.ToString("x2", CultureInfo.InvariantCulture) + b.ToString("x2", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
 /// S2 switches (environment, read once at bridge start; tests replace <see cref="S1Switches.Reader"/>). Each one isolates
 /// a HYPOTHESIS mechanism so the real run can settle it:
 /// <list type="bullet">
 /// <item><c>DEEPENGINEERS_SN_WINDOW</c> = <c>on</c> (default) | <c>off</c>: the window role of S2 (restyle, lower, rect
 /// correction). Off leaves the window exactly as S1 did (diagnostics; the SE side then never reads rectOk).</item>
 /// <item><c>DEEPENGINEERS_SN_TASKBAR</c> = <c>styles</c> (default: ITaskbarList:DeleteTab only when the restyle does
 /// not verify) | <c>deletetab</c> (DeleteTab after every restyle too).</item>
 /// <item><c>DEEPENGINEERS_SN_RECT_CORRECT</c> = <c>on</c> (default) | <c>off</c>: the Screen.SetResolution
 /// correction (it writes Unity's Screenmanager registry keys, which the launcher restores, CONTRACT §1.1).</item>
 /// <item><c>DEEPENGINEERS_SN_RECIPES</c> = <c>on</c> (default) | <c>off</c>: Idle/Track/Full recipes and the freeze
 /// off keeps S1's behaviour (cameras on, time running, the user's frame rate) for a comparison run.</item>
 /// </list>
 /// </summary>
    internal static class S2Switches
    {
        public const string WindowVar = "DEEPENGINEERS_SN_WINDOW";
        public const string TaskbarVar = "DEEPENGINEERS_SN_TASKBAR";
        public const string RectCorrectVar = "DEEPENGINEERS_SN_RECT_CORRECT";
        public const string RecipesVar = "DEEPENGINEERS_SN_RECIPES";
        /// <summary>Review fix (finding 9): "on" lets the vanilla void ghost leviathans spawn in OUTER VOID.</summary>
        public const string VoidLeviathansVar = "DEEPENGINEERS_SN_VOID_LEVIATHANS";

        public static bool Window { get { return !S1Switches.IsOff(S1Switches.Reader(WindowVar)); } }
        public static bool VoidLeviathans { get { return S1Switches.IsOn(S1Switches.Reader(VoidLeviathansVar)); } }
        public static bool RectCorrect { get { return !S1Switches.IsOff(S1Switches.Reader(RectCorrectVar)); } }
        public static bool Recipes { get { return !S1Switches.IsOff(S1Switches.Reader(RecipesVar)); } }

        public static bool AlwaysDeleteTab
        {
            get
            {
                string v = S1Switches.Reader(TaskbarVar);
                return v != null && string.Equals(v.Trim(), "deletetab", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string Describe()
        {
            return "window=" + (Window ? "on" : "OFF") + " taskbar=" + (AlwaysDeleteTab ? "deletetab" : "styles") +
                   " rectCorrect=" + (RectCorrect ? "on" : "OFF") + " recipes=" + (Recipes ? "on" : "OFF") +
                   " voidLeviathans=" + (VoidLeviathans ? "on (vanilla)" : "off outside the tile");
        }
    }

    /// <summary>
 /// Dev test hook <c>de.s2c5</c>: a C5 written by hand, used by the S2 part instead of SE's (as if a live SE published
 /// it), so that Subnautica alone (launcher or <c>tools/lab/se_cmd.py --to sn</c>) can be driven through every S2 state:
 /// <c>{"view": "Space"|"Track"|"Prime"|"Underwater"|"Surfacing", "perfMode": 0-2, "targetFps": n, "busTarget": 0-1,
 /// "foreground": bool, "paused": bool, "crossfadeMs": ms, "seRect": [l, t, r, b]}</c> (only <c>view</c> is required;
 /// the others default as the S2 SE machine publishes them: perfMode from the view, targetFps 10/10/0 and 30 underwater
 /// unfocused, busTarget 1 underwater and foreground else 0, crossfade 300 ms), or <c>"off"</c>. From the SE chat relay:
 /// <c>{"argv": ["Underwater"]}</c>. Dev builds only; never on by itself.
 /// S7a: optional <c>"picture": "site"|"waterline"</c> (default site, as SE's machine since S7a): under site perfMode is
 /// Full in every non-Space view, C5 bit 9 pictureSite is set and the steady state of the picture latch is written
 /// (marginsOn and seeThrough in every non-Space view, 30 fps unfocused there); under waterline S2's defaults (Track
 /// recipe in Track, marginsOn in Prime/Underwater/Surfacing, seeThrough Underwater).
 /// </summary>
    internal static class S2TestC5
    {
        public static bool TryParse(object args, out bool off, out SeState st, out string describe, out string error)
        {
            off = false; st = default(SeState); describe = null; error = null;
            object a = S1Args.Unwrap(args);
            object viewArg = S1Args.Get(a, "view", 0);
            if (viewArg == null && a is Dictionary<string, object>)
            {
                bool o;
                if (S1Args.TryBool(S1Args.Get(a, "off", -1), out o) && o) { off = true; return true; }
            }
            var v = viewArg as string;
            if (v == null) { error = "args {\"view\": \"Space|Track|Prime|Underwater|Surfacing\" [, perfMode, targetFps, busTarget, foreground, paused, crossfadeMs, seRect]} or \"off\""; return false; }
            uint view, approach;
            switch (v.Trim().ToLowerInvariant())
            {
                case "off": off = true; return true;
                case "space": view = ViewState.Space; approach = ApproachMode.None; break;
                case "track": view = ViewState.Approach; approach = ApproachMode.Track; break;
                case "prime": view = ViewState.Approach; approach = ApproachMode.Prime; break;
                // CONTRACT §5.4: approachMode is 0 outside Approach (as the integrated SE writes C5)
                case "underwater": view = ViewState.Underwater; approach = ApproachMode.None; break;
                case "surfacing": view = ViewState.Surfacing; approach = ApproachMode.None; break;
                default: error = "unknown view '" + v + "' (Space, Track, Prime, Underwater, Surfacing or off)"; return false;
            }
            bool fg = true, paused = false;
            object x = S1Args.Get(a, "foreground", 1);
            if (x != null && !S1Args.TryBool(x, out fg)) { error = "foreground must be true|false"; return false; }
            x = S1Args.Get(a, "paused", 2);
            if (x != null && !S1Args.TryBool(x, out paused)) { error = "paused must be true|false"; return false; }
            bool site = true;
            x = S1Args.Get(a, "picture", -1);
            if (x != null)
            {
                string ps = (x as string ?? "").Trim().ToLowerInvariant();
                if (ps == "waterline") site = false;
                else if (ps != "site") { error = "picture is \"site\" or \"waterline\""; return false; }
            }
            // the steady state of SE's machine (S7a: the picture latch on in every non-Space view under site)
            bool marginsOn = site ? view != ViewState.Space : ViewStateMachine.IsPrimeLike(view, view == ViewState.Approach ? approach : ApproachMode.Prime);
            bool seeThrough = site ? view != ViewState.Space : view == ViewState.Underwater;

            uint perf = PerfRecipeRules.ExpectedPerfMode(view, approach, site);
            double d;
            x = S1Args.Get(a, "perfMode", -1);
            if (x != null)
            {
                if (!S1Args.TryDouble(x, out d) || d < 0 || d > 2 || d != Math.Floor(d)) { error = "perfMode is 0 (Idle), 1 (Track) or 2 (Full)"; return false; }
                perf = (uint)d;
            }
            uint fps = perf == PerfMode.Full ? 0u : (uint)PerfRecipeRules.IdleFps;
            if (seeThrough && !fg) fps = (uint)PerfRecipeRules.UnfocusedFullFps;
            x = S1Args.Get(a, "targetFps", -1);
            if (x != null)
            {
                if (!S1Args.TryDouble(x, out d) || d < 0 || d > 1000 || d != Math.Floor(d)) { error = "targetFps is an integer 0-1000"; return false; }
                fps = (uint)d;
            }
            float bus = view == ViewState.Underwater && fg ? 1f : 0f;
            x = S1Args.Get(a, "busTarget", -1);
            if (x != null)
            {
                if (!S1Args.TryDouble(x, out d) || d < 0 || d > 1) { error = "busTarget is 0-1"; return false; }
                bus = (float)d;
            }
            float fade = MixRules.DefaultCrossfadeMs;
            x = S1Args.Get(a, "crossfadeMs", -1);
            if (x != null)
            {
                if (!S1Args.TryDouble(x, out d) || d <= 0 || d > MixRules.MaxCrossfadeMs) { error = "crossfadeMs is (0, 10000]"; return false; }
                fade = (float)d;
            }
            I32x4 rect = default(I32x4);
            x = S1Args.Get(a, "seRect", -1);
            if (x != null)
            {
                var l = x as List<object>;
                double r0, r1, r2, r3;
                if (l == null || l.Count != 4 || !S1Args.TryDouble(l[0], out r0) || !S1Args.TryDouble(l[1], out r1) ||
                    !S1Args.TryDouble(l[2], out r2) || !S1Args.TryDouble(l[3], out r3))
                {
                    error = "seRect is [left, top, right, bottom]";
                    return false;
                }
                rect = new I32x4((int)r0, (int)r1, (int)r2, (int)r3);
            }

            uint flags = 0, overlays = 0;
            if (fg) flags |= SeStateFlags.SeForeground; else overlays |= Overlays.Unfocused;
            if (paused) { flags |= SeStateFlags.SePaused; overlays |= Overlays.Paused; }
            if (marginsOn) flags |= SeStateFlags.MarginsOn;
            if (seeThrough) flags |= SeStateFlags.SeeThrough;
            if (site) flags |= S7aConventions.C5FlagsExt.PictureSite;
            st.flags = flags;
            st.overlays = overlays;
            st.viewState = view;
            st.approachMode = approach;
            st.perfMode = perf;
            st.targetFps = fps;
            st.snBusTarget = bus;
            st.crossfadeMs = fade;
            st.seRect = rect;
            st.zone = Zone.Core;
            describe = PerfRecipeRules.ViewName(view, approach) + " picture=" + (site ? "site" : "waterline") + " perfMode=" + PerfRecipeRules.Name(perf) + " targetFps=" + fps +
                       " busTarget=" + bus.ToString("0.###", CultureInfo.InvariantCulture) + " foreground=" + (fg ? "true" : "false") +
                       " paused=" + (paused ? "true" : "false") + " crossfadeMs=" + fade.ToString("0.#", CultureInfo.InvariantCulture) +
                       " seRect=" + RectI.From(rect);
            return true;
        }
    }

    /// <summary>Frame pacing of Subnautica's main thread (S2.N8 "snFrame advances at ≥ 25 fps, no gap > 1 s"; S2.N3 "≤ 11 fps").</summary>
    internal sealed class FramePacing
    {
        private ulong _lastMs, _windowStartMs;
        private bool _started;
        private int _frames;
        private ulong _maxGapMs;
        // S7a (s7a.picture frameMsP99): the frame gaps of the current window (no allocation; extra frames beyond the
 // buffer are counted in max/fps only)
        private readonly ulong[] _gaps = new ulong[1024];
        private int _gapCount;

        public float LastFps { get; private set; }
        public ulong LastMaxGapMs { get; private set; }
        /// <summary>S7a: the 99th percentile frame gap of the last closed window (ms; 0 without frames).</summary>
        public ulong LastP99Ms { get; private set; }
        public long GapsOver100Ms, GapsOver1s;
        public ulong WorstGapMs;

        public void Frame(ulong nowMs)
        {
            if (!_started) { _started = true; _lastMs = nowMs; _windowStartMs = nowMs; return; }
            ulong gap = nowMs > _lastMs ? nowMs - _lastMs : 0;
            _lastMs = nowMs;
            _frames++;
            if (_gapCount < _gaps.Length) _gaps[_gapCount++] = gap;
            if (gap > _maxGapMs) _maxGapMs = gap;
            if (gap > WorstGapMs) WorstGapMs = gap;
            if (gap > 100) GapsOver100Ms++;
            if (gap > 1000) GapsOver1s++;
        }

        /// <summary>Closes the current window (called once per second): frames per second and the largest frame gap.</summary>
        public void Roll(ulong nowMs)
        {
            ulong span = nowMs > _windowStartMs ? nowMs - _windowStartMs : 0;
            LastFps = span > 0 ? _frames * 1000f / span : 0f;
            LastMaxGapMs = _maxGapMs;
            if (_gapCount > 0)
            {
                Array.Sort(_gaps, 0, _gapCount);
                int k = (int)Math.Ceiling(0.99 * _gapCount) - 1;
                LastP99Ms = _gaps[Math.Max(0, Math.Min(_gapCount - 1, k))];
            }
            else LastP99Ms = 0;
            _gapCount = 0;
            _frames = 0;
            _maxGapMs = 0;
            _windowStartMs = nowMs;
        }
    }
    /// <summary>

 /// during the 2.7 s between Prime and the water). While Subnautica's picture cannot be shown, the driven camera is
 /// clamped to <see cref="ParkY"/> above the sea under the SE camera's (x, z): the streamer keeps that column resident
 /// before the dive. S7a (CONTRACT §5.4 "C5/C7 semantics for S7a"): parked while C5 is valid and <c>perfMode ≠ Full</c>
 /// (<see cref="Park(bool, uint)"/>): under the Waterline policy exactly S2's Space and Track, under Site Space only
 /// (never while Subnautica's picture can be shown: Track renders it at the band). S2's view rule is kept as
 /// <see cref="ParkByView"/> for the seeded failure S7a.M2 (<see cref="DevMutantS7aM2"/>).
 /// </summary>
    internal static class CameraParkRules
    {
        public const float ParkY = 60f;
        /// <summary>The SN mutant id of the S2 park rule (registered in <see cref="DevMutants"/>).</summary>
        public const string DevMutantS7aM2 = "S7a.M2";

        /// <summary>S7a: parked while a valid C5 asks for less than Full (Idle, or the Waterline policy's Track).</summary>
        public static bool Park(bool c5Valid, uint perfMode)
        {
            return c5Valid && perfMode != Wire.PerfMode.Full;
        }

        /// <summary>S2's rule: Space, or Approach in Track. Seeded failure S7a.M2 (parked while the picture shows in Track).</summary>
        public static bool ParkByView(bool c5Valid, uint view, uint approach)
        {
            if (!c5Valid) return false;
            return view == Wire.ViewState.Space || (view == Wire.ViewState.Approach && approach == Wire.ApproachMode.Track);
        }

        /// <summary>The camera driver's decision for this frame (<see cref="PerfInputsForPark.ParkByView"/> = S7a.M2).</summary>
        public static bool Decide(PerfInputsForPark pi)
        {
            return pi.ParkByView ? ParkByView(pi.C5Valid, pi.View, pi.Approach) : Park(pi.C5Valid, pi.PerfMode);
        }

        /// <summary>The y to drive: <paramref name="y"/> when not parked or already lower, else <see cref="ParkY"/>.</summary>
        public static float DriveY(bool park, float y) { return park && y > ParkY ? ParkY : y; }
    }

    /// <summary>
 /// S7a: the distance beyond which mirrored chunks are not drawn (S5 hook in <c>S5Mirror</c>'s culling; CONTRACT §5.4
 /// "C5/C7 semantics for S7a", entity set): under the Site picture policy with the camera above the sea, the entity
 /// set's leave radius above water (1,050 m: everything SE still mirrors is drawn, Subnautica's above-water view reaches
 /// kilometres); otherwise the under-water distance (S5: the water visibility fallback, 200 m).
 /// </summary>
    internal static class MirrorCullRules
    {
        public static double DistanceM(bool pictureSite, double camY, double underwaterM)
        {
            return pictureSite && camY > 0.0 ? S7aConventions.Limits.MirrorLeaveAboveWaterM : underwaterM;
        }
    }

}
