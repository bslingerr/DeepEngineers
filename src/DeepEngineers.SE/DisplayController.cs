using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The update-thread half of the S2 display (ROADMAP S2 "SE plugin"; ARCHITECTURE §5.1), game-independent: turns the
 /// view state and the dev overrides into a <see cref="DisplayRequest"/> for <see cref="RenderDisplay"/> (render
 /// thread), and raises its one-shot actions:
 /// <list type="bullet">
 /// <item>foreground: <c>SetForegroundWindow(seHwnd)</c> when the link first comes up (CONTRACT §7 Start: "takes the
 /// foreground itself, then places Subnautica directly under SE"; S2.M8 skips it), a HUD hint if it fails;</item>
 /// <item>z-order re-assert: at link-up and when <c>snHwnd</c> becomes known, on Prime entry (the Prime → Underwater
 /// guard needs <c>zOrderOk</c>), on Underwater entry, after every SE re-activation, on a see-through toggle and on
 /// <c>de.zorder</c>;</item>
 /// <item>design F: the machine's request (S7a <c>Site</c> policy: margins in every non-Space state, the scene skipped
 /// while the picture latch is on; S2 <c>Waterline</c>: margins in Prime (not under S2.M7), Underwater and Surfacing,
 /// the scene skipped Underwater); A′: lowered while the machine asks for it unless a menu is open
 /// (<see cref="FallbackAPrime"/>);</item>
 /// <item>the picture-swap fade (<see cref="CrossingHook"/> → <see cref="CrossingFlash"/>) on the machine's picture edge:
 /// at the Approach band under Site, at the waterline under Waterline;</item>
 /// <item>dev overrides: the see-through toggle (hotkey <c>Apps</c> = the keyboard's Menu key, <c>de.display toggle</c>:
 /// S2.T-D1), forced margins/skip, the alpha fill switch, the composed-pixel probe and the alpha readback. S5a remapped
 /// the hotkey from F8: SE binds F8 to its free spectator
 /// and F9 to the static spectator; <c>MyKeys.Apps</c> has no default SE control
 /// <c>config.json "seeThroughKey"</c> still overrides it.</item>
 /// <item>S7t T1, the crossfade (<see cref="CrossfadeRules.Decide"/>; CONTRACT §5.4 "S7t crossfade"): under Site the
 /// request carries the ramp's target (Subnautica while the latch is on and the leave dwell has not started), its
 /// duration (<see cref="S7tSettings.FadeMs"/>, <c>de.s7t fade</c>, env <c>DEEPENGINEERS_FADE_MS</c>, default 700 ms)
 /// and a snap sequence; the F skip and the A′ lowering wait for the render thread's ramp; <c>s7t.fade</c> rows
 /// {tick, dir, ms, path} at each start and end. The machine's leave dwell and tick come through
 /// <see cref="SetMachineFacts"/> before each <see cref="Post"/> after a step.</item>
 /// </list>
 /// </summary>
    internal sealed class DisplayController
    {
        public readonly RenderDisplay Render;
        public Action<string> Log = s => { };
        public Action<string> HudHint = s => { };
        public Func<IntPtr> ForegroundWindow = () => NativeWin.GetForegroundWindow();

        private int _zSeq, _fgSeq, _rbSeq, _probeSeq;
        private string _zWhy;
        private ProbeSpec _probe;
        private long _skipRequestQpc;
        private bool _lastSkip;
        private bool _linkedPrev;
        private IntPtr _snPrev;
        private bool _foregroundPrev;
        private bool _hintShown;
        private int _probeSeen;
        private int _notHonouredInARow;
        private string _label;
        private S2State _lastState;
        private DisplayRequest _lastRequest;
        // S7t T1: the crossfade decision and its rows
        private CrossfadeRules.Memory _fadeMem;
        private bool _leaveArmed;
        private ulong _tick;
        private bool _fadeRunning, _fadeRunToSn, _fadeRunOn;
        private long _fadeStartQpc;
        private string _fadePath, _fadeReason;
        private long _fadeRowSecond, _fadeRowsInSecond;
        /// <summary>S7t T1: the <c>s7t.fade</c> row sink (the session JSONL; tests replace it).</summary>
        public Action<string> RowSink = Jsonl.WriteLineBuffered;
        /// <summary>S7t T1 counters: ramps started, ended, snaps, rows dropped by the cap.</summary>
        public long FadeStarts { get; private set; }
        public long FadeEnds { get; private set; }
        public long FadeSnaps { get; private set; }
        public long FadeRowsDropped { get; private set; }
        /// <summary>S7t T1: the last decision (de.s7t status).</summary>
        public CrossfadeRules.Decision LastFade { get; private set; }
        /// <summary>S7t T1: at most this many s7t.fade rows per second (the rest counted in the next row's <c>dropped</c>).</summary>
        public const int MaxFadeRowsPerSecond = 20;

        /// <summary>
 /// Composed-pixel probes with the calibration colour found missing in a row before <see cref="AutoFallback"/>
 /// switches to A′.
 /// </summary>
        public const int AutoFallbackAfterProbes = 2;

        public DisplayDesign Mode { get; private set; }
        public S2Config Config { get; private set; }
        /// <summary>config <c>autoFallback</c>, switchable at run time (<c>de.display autoFallback=on|off</c>; the scripted
 /// sessions switch it off and decide from the probe row).</summary>
        public bool AutoFallback { get; set; }
        public int NotHonouredInARow { get { return _notHonouredInARow; } }
        public bool AlphaFill { get; set; }
        public bool ReadbackPeriodic { get; set; }
        /// <summary>The S2.T-D1 see-through toggle (hotkey Apps since S5a, was F8 / de.display toggle): forced see-through in F, lowered in A′.</summary>
        public bool ForcedSeeThrough { get; private set; }
        /// <summary>
 /// Called on every picture edge of the view machine (S7a; <c>ViewOutputs.PictureEdge</c>): (toSubnautica, band,
 /// why) with band = the Site policy's swap at the Approach band (false: the Waterline policy's waterline). Plugin
 /// wires the fade (<see cref="CrossingFlash.
 /// </summary>
        public static Action<bool, bool, string> CrossingHook;
        /// <summary>S7a: picture edges seen by <see cref="Post"/>, the last one (+1 / −1) and the policy of the last request.</summary>
        public long PictureEdges { get; private set; }
        public int LastPictureEdge { get; private set; }
        public bool PictureSite { get; private set; }
        public bool? ForcedMargins { get; set; }
        public bool? ForcedSkip { get; set; }
        public long Toggles { get; private set; }
        /// <summary>S8: requests whose skip or lowering the peer-loss guard took back (S8Rules.PeerLost).</summary>
        public long PeerLostGuards { get; private set; }
        /// <summary>S8: the last request was posted under the peer-loss guard.</summary>
        public bool PeerLostNow { get; private set; }
        public long ZRequests { get { return _zSeq; } }
        public DisplayRequest LastRequest { get { return _lastRequest; } }

        public DisplayController(RenderDisplay render, S2Config config)
        {
            Render = render;
            Config = config ?? new S2Config();
            Mode = Config.Display;
            AutoFallback = Config.AutoFallback;
            AlphaFill = Config.AlphaFill;
            ReadbackPeriodic = Config.AlphaReadbackPeriodic;
        }

        // ================================================================================================= triggers

        public void RequestZOrder(string why)
        {
            _zSeq++;
            _zWhy = why;
        }

        public void RequestForeground(string why)
        {
            _fgSeq++;
            Log("DE.Display foreground request (" + why + ")");
        }

        public void RequestReadback() { _rbSeq++; }

        public void RequestProbe(ProbeSpec spec)
        {
            _probe = spec;
            _probeSeq++;
        }

        public void SetMode(DisplayDesign mode, string why)
        {
            if (mode == Mode) return;
            Log("DE.Display mode " + S2Config.Name(Mode) + " -> " + S2Config.Name(mode) + " (" + why + ")");
            Mode = mode;
            ForcedMargins = null;
            ForcedSkip = null;
        }

        /// <summary>The see-through toggle; returns the update-thread cost in ms (S2.T-D1: &lt; 1 ms per toggle).</summary>
        public double ToggleSeeThrough(string via)
        {
            long t0 = Stopwatch.GetTimestamp();
            ForcedSeeThrough = !ForcedSeeThrough;
            Toggles++;
            _label = "toggle #" + Toggles;
            // S2.T-D1: "the walker finds snHwnd directly under seHwnd at every toggle": a verified re-assert per toggle
            RequestZOrder("see-through toggle #" + Toggles + (ForcedSeeThrough ? " on" : " off"));
            IntPtr fg = ForegroundWindow();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Log("DE.Display toggle #" + Toggles + " see-through " + (ForcedSeeThrough ? "ON" : "OFF") + " via " + via + " mode=" + S2Config.Name(Mode) +
                " foregroundIsSe=" + (fg == _lastRequest.Se && fg != IntPtr.Zero) + " updateMs=" + ms.ToString("0.000", CultureInfo.InvariantCulture));
            return ms;
        }

        public void SetSeeThrough(bool on, string via)
        {
            if (ForcedSeeThrough != on) ToggleSeeThrough(via);
        }

        /// <summary>
 /// S7t T1: the view machine's facts the crossfade needs, set on the update thread right before the
 /// <see cref="Post"/> that follows a step: the F15 leave dwell (<c>ViewMachineState.LeftArmed</c>: the altitude exit
 /// condition holds; the fade back starts there) and the SE tick (the <c>s7t.fade</c> rows).
 /// </summary>
        public void SetMachineFacts(bool leaveArmed, ulong tick)
        {
            _leaveArmed = leaveArmed;
            _tick = tick;
        }

        // ================================================================================================= per tick

 /// <summary>
 /// Update thread (plugin Update and the PrepareForDraw postfix): the one-shot triggers of this tick, then the
 /// request for the render thread. <paramref name="machineRequest"/> is the view state machine's request
 /// (Wire.ViewRequest bits: margins, scene skip, alpha fill, A′ lowered; 0 when it did not step) and
 /// <paramref name="machineZPulse"/> its z-order re-assert pulse (margins becoming wanted, re-activation, zOrderOk
 /// lost). S7a: <paramref name="pictureEdge"/> is the machine's picture edge of this step (+1 SE → Subnautica, −1
 /// back, 0; pass it only on the call that follows the step) and <paramref name="pictureSite"/> its effective policy
 /// (Site, else Waterline).
 /// </summary>
        public void Post(ref LinkFacts link, IntPtr seHwnd, S2State state, uint machineRequest, bool machineZPulse, bool inWorld, bool menuOpen,
                         bool foreground, ref DisplayFeedback fb, int pictureEdge = 0, bool pictureSite = false)
        {
            PictureSite = pictureSite;
            if (machineZPulse) RequestZOrder("view state machine pulse (" + ViewStateAdapter.Name(state) + ")");
            // S7t T1: the crossfade decision of this tick (before the edge's hook: the flash asks whether the ramp covers the swap)
            bool f = Mode == DisplayDesign.F;
            bool forced = ForcedSeeThrough && inWorld;
            bool mMargins = (machineRequest & Wire.ViewRequest.Margins) != 0;
            bool mSkip = (machineRequest & Wire.ViewRequest.SceneSkip) != 0;
            bool mLowered = (machineRequest & Wire.ViewRequest.Lowered) != 0;
            // S8 (S8Rules.PeerLost): a linked Subnautica that is not alive (stalled, or a new generation not judged yet) never
 // gets SE's scene skipped or SE lowered under it, whatever the last step or a dev override asked: SE's own picture
 // on this frame, the crossfade snaps (its latch drops without the dwell), margins off after a drawn present
            bool peerLost = S8Rules.PeerLost(link.Linked, link.PeerAlive);
            PeerLostNow = peerLost;
            if (peerLost) { mSkip = false; mLowered = false; }
            var fi = new CrossfadeRules.Inputs
            {
                Enabled = S7tSettings.FadeMs > 0.0, Ms = S7tSettings.FadeMs, PictureSite = pictureSite, InWorld = inWorld,
                DesignF = f, DesignAPrime = Mode == DisplayDesign.APrime, APrimeRamp = S7tSettings.APrimeRamp,
                Forced = forced || ForcedSkip.HasValue || ForcedMargins.HasValue, MenuOpen = menuOpen,
                Latch = f ? mSkip : mLowered, LeaveArmed = _leaveArmed,
            };
            CrossfadeRules.Decision fd = CrossfadeRules.Decide(ref fi, ref _fadeMem, mSkip, mLowered);
            LastFade = fd;
            S7tSettings.CrossfadeCovers = fd.On;
            if (fd.Started && (fd.On || pictureSite)) FadeStarted(fd, state); // rows for the band swap (Site); Waterline keeps S2's cut and flash
            if (pictureEdge != 0)
            {
                PictureEdges++;
                LastPictureEdge = pictureEdge;
                string why = (pictureSite ? "band" : "waterline") + " " + ViewStateAdapter.Name(_lastState) + " -> " + ViewStateAdapter.Name(state);
                // S7a: the
 // swap is at the Approach band under the Site policy, at the waterline under Waterline
                if (inWorld && CrossingHook != null) CrossingHook(pictureEdge > 0, pictureSite, why);
                // the dev toggle (Apps) is a look-at-the-other-picture aid: it clears itself when Subnautica's picture goes
 // (S7a Site: the latch's off edge
                if (pictureSite && pictureEdge < 0 && ForcedSeeThrough) ToggleSeeThrough("auto: picture back to SE (" + why + ")");
            }
            // review fix (finding 17): the window set-up (foreground, HWND_NOTOPMOST, z-order) needs
 // Subnautica's window too (CONTRACT §7 Start: "then places Subnautica directly under SE"); a peer without
 // one (the SE-alone baseline's passive host) leaves SE's window exactly as SE set it
            bool linked = link.Linked && seHwnd != IntPtr.Zero && link.SnHwnd != IntPtr.Zero;
            if (linked && !_linkedPrev)
            {
                if (S2Mutants.NoForegroundAtStart) Log("DE.Display link up: MUTANT S2.M8, SE does NOT take the foreground");
                else RequestForeground("link up: SE takes the foreground itself (CONTRACT §7 Start)");
                RequestZOrder("link up: Subnautica directly under SE");
                _hintShown = false;
            }
            if (linked && link.SnHwnd != IntPtr.Zero && link.SnHwnd != _snPrev && _linkedPrev)
                RequestZOrder("snHwnd " + NativeWin.Hex(link.SnHwnd) + " known");
            if (linked && foreground && !_foregroundPrev && _linkedPrev)
                RequestZOrder("SE re-activated (foreground change)");
            if (state != _lastState)
            {
                if (state == S2State.Prime) RequestZOrder("Prime entry");
                else if (state == S2State.Underwater) RequestZOrder("Underwater entry");

                // the session (Surfacing cannot complete while the scene is skipped); the toggle is a look-at-SE's-picture aid
 // under water, so under the Waterline policy it clears itself when the view leaves Underwater (Site: above)
                if (!pictureSite && ForcedSeeThrough && _lastState == S2State.Underwater && state != S2State.Underwater) ToggleSeeThrough("auto: view left Underwater (" + ViewStateAdapter.Name(state) + ")");
                // review F18: under Site the toggle in Space would show Subnautica's frozen, parked window
                if (pictureSite && ForcedSeeThrough && state == S2State.Space) ToggleSeeThrough("auto: Space under the Site policy (review F18)");
                if (!ForcedSeeThrough) _label = ViewStateAdapter.Name(state);
                _lastState = state;
            }
            _linkedPrev = linked;
            _snPrev = link.SnHwnd;
            _foregroundPrev = foreground;

            if (fb.ForegroundFailed && !_hintShown && linked)
            {
                _hintShown = true;
                HudHint("DeepEngineers: Space Engineers could not take the keyboard focus - click its window once");
            }
            if (fb.ProbeDone != _probeSeen)
            {
                _probeSeen = fb.ProbeDone;
                // review fix (finding 1): only NOT HONOURED verdicts with an expected colour count (each is
 // already two agreeing reads), and AutoFallbackAfterProbes of them in a row; anything else resets the run
                if (fb.ProbeVerdict == 0 && fb.ProbeHadExpected) _notHonouredInARow++;
                else _notHonouredInARow = 0;
                if (_notHonouredInARow >= AutoFallbackAfterProbes && Mode == DisplayDesign.F && AutoFallback)
                    SetMode(DisplayDesign.APrime, "composed-pixel probe: DWM alpha not honoured in " + _notHonouredInARow +
                                                  " probes in a row (ARCHITECTURE §5.1: not honoured -> A')");
                else if (fb.ProbeVerdict == 0 && fb.ProbeHadExpected)
                    Log("DE.Display autoFallback: NOT HONOURED probe #" + _notHonouredInARow + " in a row, design " + S2Config.Name(Mode) + " kept (autoFallback " +
                        (AutoFallback ? "switches to A' after " + AutoFallbackAfterProbes + " in a row" : "off") + ")");
            }

            var r = new DisplayRequest
            {
                Linked = linked,
                Se = seHwnd,
                Sn = link.SnHwnd,
                SnPid = link.SnPid,
                SnFrame = link.HaveC7 ? link.C7.snFrame : 0UL,
                Mode = Mode,
                AlphaFill = AlphaFill,
                ZSeq = _zSeq,
                ZWhy = _zWhy,
                ForegroundSeq = _fgSeq,
                ReadbackSeq = _rbSeq,
                ReadbackPeriodic = ReadbackPeriodic,
                ProbeSeq = _probeSeq,
                Probe = _probe,
                Label = _label,
                // S7t T1: the ramp (CrossfadeRules; the render thread steps it and holds the skip / lowering until it ends)
                FadeOn = fd.On,
                FadeToSn = fd.ToSn,
                FadeMs = fd.Ms,
                FadeSnapSeq = fd.SnapSeq,
                FadeStraight = S7tSettings.Straight,
            };
            bool prime = state == S2State.Prime, under = state == S2State.Underwater, surf = state == S2State.Surfacing;
            if (prime && S2Mutants.MarginsOffInPrime) mMargins = false; // S2.M7: Subnautica fully covered during Prime
            if (f)
            {
                r.Margins = inWorld && (mMargins || forced);
                if (ForcedMargins.HasValue) r.Margins = ForcedMargins.Value;
                r.Skip = inWorld && (fd.Skip || forced); // S7t T1: fd.Skip = the machine's skip, off from the leave dwell while the ramp runs
                if (ForcedSkip.HasValue) r.Skip = ForcedSkip.Value;
            }
            else
            {
                // A': Lowered = SE below Subnautica; Skip while lowered = DrawEnabled false (the machine keeps Lowered
 // through Surfacing with Skip off, so SE draws a fresh frame before it is raised)
                bool forcedLower = FallbackAPrime.WantLowered(S2State.Space, forced, menuOpen);
                r.Lowered = inWorld && (fd.Lowered || forcedLower); // S7t T1: fd.Lowered = the machine's, off from the leave dwell with the A′ ramp
                r.Skip = r.Lowered && (mSkip || forcedLower);
            }
            if (peerLost && (r.Skip || r.Lowered))
            {
                PeerLostGuards++;
                r.Skip = false;
                r.Lowered = false;
            }
            FadeProgress(ref fb);
            r.WalkEveryFrame = prime || under || surf || forced || r.Margins || r.Lowered;
            // the z-order auto re-assert runs while the margins are requested with SE's scene drawn (S2: Prime only; S7a
 // Site: also Track and the latch-off Approach while Subnautica is composed under SE's opaque scene)
            r.AutoReassert = mMargins && !mSkip && !forced;
            if (r.Skip && !_lastSkip) _skipRequestQpc = Stopwatch.GetTimestamp();
            _lastSkip = r.Skip;
            r.SkipRequestQpc = _skipRequestQpc;
            _lastRequest = r;
            Render.SetRequest(ref r);
        }

        // ============================================================================================ S7t crossfade

 /// <summary>S7t T1: a new target (a ramp, a snap, or the instant swap while the ramp is off): log line and row.</summary>
        private void FadeStarted(CrossfadeRules.Decision fd, S2State state)
        {
            long now = Stopwatch.GetTimestamp();
            DisplayFeedback fb = Render.Feedback;
            if (_fadeRunning) FadeRow("superseded", _fadeRunToSn, _fadePath, _fadeReason, state, (now - _fadeStartQpc) * 1000.0 / Stopwatch.Frequency, ref fb);
            string path = !fd.On ? "cut" : fd.Snap ? "snap" : Mode == DisplayDesign.F ? "F" : "A'";
            string reason = CrossfadeRules.Name(fd.Why);
            FadeStarts++;
            if (fd.Snap) FadeSnaps++;
            _fadeRunning = fd.On && !fd.Snap;
            _fadeRunOn = fd.On;
            _fadeRunToSn = fd.ToSn;
            _fadeStartQpc = now;
            _fadePath = path;
            _fadeReason = reason;
            Log("DE.Display crossfade " + (fd.ToSn ? "to Subnautica" : "to SE") + " path=" + path + " ms=" + fd.Ms.ToString("0", CultureInfo.InvariantCulture) +
                " reason=" + reason + " view=" + ViewStateAdapter.Name(state) + " p=" + fb.FadeP.ToString("0.###", CultureInfo.InvariantCulture));
            FadeRow("start", fd.ToSn, path, reason, state, double.NaN, ref fb);
        }

        /// <summary>S7t T1: the end of a running ramp, read from the render thread's feedback (its progress reached the target).</summary>
        private void FadeProgress(ref DisplayFeedback fb)
        {
            if (!_fadeRunning) return;
            bool done = _fadeRunToSn ? fb.FadeP >= 1.0 : fb.FadeP <= 0.0;
            if (!done) return;
            _fadeRunning = false;
            FadeEnds++;
            double ms = (Stopwatch.GetTimestamp() - _fadeStartQpc) * 1000.0 / Stopwatch.Frequency;
            Log("DE.Display crossfade done " + (_fadeRunToSn ? "to Subnautica" : "to SE") + " in " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms (passes " + fb.FadePasses +
                ", held skips " + fb.FadeHeldSkips + ", margin holds " + fb.FadeMarginHolds + (fb.FadeErrors > 0 ? ", pass errors " + fb.FadeErrors + " " + fb.FadeError : "") + ")");
            FadeRow("end", _fadeRunToSn, _fadePath, _fadeReason, _lastState, ms, ref fb);
        }

        /// <summary>One <c>s7t.fade</c> row (CONTRACT §5.4 "S7t crossfade"): at most <see cref="MaxFadeRowsPerSecond"/> a second.</summary>
        private void FadeRow(string phase, bool toSn, string path, string reason, S2State state, double actualMs, ref DisplayFeedback fb)
        {
            long sec = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            if (sec != _fadeRowSecond) { _fadeRowSecond = sec; _fadeRowsInSecond = 0; }
            if (++_fadeRowsInSecond > MaxFadeRowsPerSecond) { FadeRowsDropped++; return; }
            try
            {
                var ci = CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder(256);
                sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", ci));
                sb.Append(",\"ev\":\"s7t.fade\",\"tick\":").Append(_tick.ToString(ci));
                sb.Append(",\"phase\":\"").Append(phase).Append('"');
                sb.Append(",\"dir\":\"").Append(toSn ? "toSn" : "toSe").Append('"');
                sb.Append(",\"ms\":").Append(S7tSettings.FadeMs.ToString("0.#", ci));
                sb.Append(",\"path\":"); JsonText.AppendString(sb, path ?? "-");
                sb.Append(",\"reason\":\"").Append(reason ?? "none").Append('"');
                sb.Append(",\"view\":\"").Append(ViewStateAdapter.Name(state)).Append('"');
                sb.Append(",\"design\":\"").Append(S2Config.Name(Mode)).Append('"');
                sb.Append(",\"p\":").Append(fb.FadeP.ToString("0.####", ci));
                if (!double.IsNaN(actualMs)) sb.Append(",\"actualMs\":").Append(actualMs.ToString("0.#", ci));
                sb.Append(",\"passes\":").Append(fb.FadePasses.ToString(ci));
                sb.Append(",\"heldSkips\":").Append(fb.FadeHeldSkips.ToString(ci));
                sb.Append(",\"marginHolds\":").Append(fb.FadeMarginHolds.ToString(ci));
                sb.Append(",\"passErrors\":").Append(fb.FadeErrors.ToString(ci));
                sb.Append(",\"layered\":").Append(fb.Layered ? "true" : "false");
                sb.Append(",\"flash\":").Append(S7tSettings.FlashWithFade ? "true" : "false");
                sb.Append(",\"dropped\":").Append(FadeRowsDropped.ToString(ci)).Append('}');
                RowSink(sb.ToString());
            }
            catch (Exception) { }
        }

        /// <summary>S7t T1: the crossfade part of <c>de.s7t status</c> / <c>de.display state</c>.</summary>
        public Dictionary<string, object> DescribeFade()
        {
            DisplayFeedback fb = Render.Feedback;
            CrossfadeRules.Decision d = LastFade;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "fadeMs", S7tSettings.FadeMs }, { "fadeSource", S7tSettings.FadeSource }, { "on", d.On }, { "toSn", d.ToSn },
                { "reason", CrossfadeRules.Name(d.Why) }, { "p", fb.FadeP }, { "running", _fadeRunning }, { "leaveArmed", _leaveArmed },
                { "starts", FadeStarts }, { "ends", FadeEnds }, { "snaps", FadeSnaps }, { "rowsDropped", FadeRowsDropped },
                { "passes", fb.FadePasses }, { "heldSkips", fb.FadeHeldSkips }, { "marginHolds", fb.FadeMarginHolds },
                { "passErrors", fb.FadeErrors }, { "passError", fb.FadeError }, { "layered", fb.Layered }, { "layeredCalls", fb.LayeredCalls },
                { "aprimeRamp", S7tSettings.APrimeRamp }, { "straight", S7tSettings.Straight }, { "flashWithFade", S7tSettings.FlashWithFade },
                { "spritesHook", S7tSettings.SpritesHookOk }, { "hookDead", fb.FadeHookDead },
            };
        }

        /// <summary>The <c>de.display state</c> dump (update thread).</summary>
        public Dictionary<string, object> Describe()
        {
            DisplayFeedback fb = Render.Feedback;
            DisplayRequest r = _lastRequest;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "mode", S2Config.Name(Mode) },
                { "picture", PicturePolicy.Describe() },
                { "pictureEffective", PictureSite ? "site" : "waterline" },
                { "pictureEdges", PictureEdges },
                { "lastPictureEdge", LastPictureEdge },
                { "config", Config.Describe() },
                { "linked", r.Linked },
                { "seHwnd", NativeWin.Hex(r.Se) },
                { "snHwnd", NativeWin.Hex(r.Sn) },
                { "snPid", r.SnPid },
                { "wantMargins", r.Margins },
                { "wantSkip", r.Skip },
                { "wantLowered", r.Lowered },
                { "alphaFill", AlphaFill },
                { "forcedSeeThrough", ForcedSeeThrough },
                { "forcedMargins", ForcedMargins.HasValue ? (object)ForcedMargins.Value : "auto" },
                { "forcedSkip", ForcedSkip.HasValue ? (object)ForcedSkip.Value : "auto" },
                { "toggles", Toggles },
                { "hooksSeen", fb.HooksSeen },
                { "presents", fb.Presents },
                { "fullDraws", fb.FullDraws },
                { "sceneDrawnPresents", fb.SceneDrawnPresents },
                { "skippedPresents", fb.SkippedPresents },
                { "marginsOn", fb.MarginsOn },
                { "marginsPresents", fb.MarginsPresents },
                { "lastDwmHr", NativeWin.Hr(fb.LastDwmHr) },
                { "zOrderOk", fb.ZOrderOk },
                { "zPending", fb.ZPending },
                { "zAttempts", fb.ZAttempts },
                { "zRequests", _zSeq },
                { "walks", fb.Walks },
                { "walker", fb.LastWalk ?? "not run" },
                { "foreground", fb.Foreground },
                { "foregroundFailed", fb.ForegroundFailed },
                { "snWindowLost", fb.SnWindowLost },
                { "snWindowGone", fb.SnWindowGone },
                { "snHiddenPresents", fb.SnHiddenPresents },
                { "snWindowRecoveries", fb.SnWindowRecoveries },
                { "lowered", fb.Lowered },
                { "zInverted", fb.ZInverted },
                { "seRect", fb.SeRectValid ? fb.SeRect.x + "," + fb.SeRect.y + "," + fb.SeRect.z + "," + fb.SeRect.w : "unknown" },
                { "fills", fb.Fills },
                { "fillError", fb.FillError },
                { "probe", fb.ProbeResult ?? "not run" },
                { "probeVerdict", fb.ProbeVerdict == 1 ? "HONOURED" : fb.ProbeVerdict == 0 ? "NOT HONOURED" : "undecided" },
                { "probesDone", fb.ProbeDone },
                { "probeActive", fb.ProbeActive },
                { "autoFallback", AutoFallback },
                { "notHonouredInARow", _notHonouredInARow },
                { "renderErrors", Render.Errors },
                { "crossfade", DescribeFade() }, // S7t T1
            };
        }
    }
}
