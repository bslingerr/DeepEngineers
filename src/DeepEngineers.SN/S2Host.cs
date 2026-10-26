using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>What the S2 host needs from the bridge behaviour (implemented by <see cref="BridgeBehaviour"/>).</summary>
    internal interface IS2Bridge
    {
        uint Phase { get; }
        ulong SnFrame { get; }
        bool Quitting { get; }
        void SetPhase(uint phase, string why);
        SePeerState PeerState { get; }
        /// <summary>Our window handle as published in C0 snHwnd (S0's lookup).</summary>
        IntPtr SnHwnd { get; }
        /// <summary>C0 seHwnd (SE's window, the z-order reference after a mid-session restyle).</summary>
        ulong SeHwnd { get; }
        /// <summary>A re-found window handle: C0 snHwnd and C7 snHwnd.</summary>
        void PublishHwnd(IntPtr hwnd, string how);
        /// <summary>C0 snFlags bit 2 stylesApplied.</summary>
        void SetStylesApplied(bool on);
    }

    /// <summary>
 /// The S2 "display + input + audio" part of the Subnautica side (ROADMAP S2 SN plugin), driven by the S0 bridge after
 /// S1 (<see cref="S1Host"/>) each frame:
 /// <list type="number">
 /// <item>Bridge start: the S2 Harmony patches (<see cref="S2Patches"/>: GetBiome "void" outside the tile, the S2.M2
 /// ClearInput switch), the DisplayManager hook.</item>
 /// <item>Just before S1 publishes ReadyIdle (<see cref="BeforeReadyIdle"/>): the user's frame-rate pair and priority
 /// saved, the Idle recipe applied (no SE yet: frozen, cameras off, (0, 10), BelowNormal), muted, the window restyled
 /// and lowered with AllowSetForegroundWindow(ASFW_ANY) (<see cref="SnWindow"/>); then ReadyIdle (CONTRACT §7 "Start").</item>
 /// <item>Every frame in world: recipe from C5 (<see cref="PerfRecipeRules"/>, <see cref="PerfApplier"/>), the freeze

 /// (<see cref="AudioMix"/>, re-applied after every FreezeTime transition), the window checks after display changes,
 /// input measurement (<see cref="InputWatch"/>), and the phase (<see cref="PhaseRules"/>, CONTRACT §5.4: ReadyIdle for
 /// Space; WarmingUp for Approach/Track; WarmingUp → Active for Approach/Prime, Underwater and Surfacing).</item>
 /// <item>Every second: window styles, rectangles and rectOk, priority, the measured frame rate, `DE.S2 sec` and
 /// `DE.Input sec`.</item>
 /// <item>C7 every frame: snHwnd, snRect, rectOk, perfModeApplied, vSyncCountApplied, targetFrameRateApplied, frozen
 /// (S1 keeps areaBuilt, presentedSeTick, uiHidden …).</item>
 /// <item>Quit: muted and frozen at once; SE exited → minimised without activation; the user's values restored at
 /// application quit; everything restored if the bridge is disposed.</item>
 /// <item>C11 (dev builds): <c>de.calib</c> (<see cref="CalibrationPattern"/>), <c>de.s2</c> (status) and the test hook
 /// <c>de.s2c5</c> (<see cref="S2TestC5"/>: a hand-written C5 used instead of SE's, for Subnautica-alone runs).</item>
 /// </list>
 /// Each feature has its own guard: the first exception logs `DE.S2 FEATURE … DISABLED` once and stops that feature only.
 /// </summary>
    internal sealed unsafe class S2Host
    {
        private readonly IS2Bridge _bridge;
        private readonly S1Host _s1;
        private readonly Transform _root;

        public readonly SnWindow Window;
        public readonly PerfApplier Perf = new PerfApplier();
        public readonly FreezeOwnerCore Freeze = new FreezeOwnerCore(new UweFreezeApi());
        public readonly AudioMix Audio = new AudioMix();
        public readonly InputWatch Input = new InputWatch();
        public readonly CalibrationPattern Calib = new CalibrationPattern();
        public readonly FramePacing Pacing = new FramePacing();

        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _onceKeys = new HashSet<string>(StringComparer.Ordinal);

        // C5 from the current SE generation
        private SeState _c5;
        private bool _c5Valid;
        private ulong _c5Ms;
        public long C5Seen;

        // dev test hook de.s2c5: a hand-written C5 used instead of SE's (as if a live SE published it)
        private bool _override;
        private SeState _ovr;
        private string _ovrDescribe = "";

        // the C5 this frame works on: the override, else SE's (valid = from the current SE generation, alive = peer Alive)
        private SeState _e;
        private bool _eValid, _eAlive;

        private bool _readyIdleDone, _quitting, _recipesOn;
        private ulong _now, _nextSecondMs;
        private RecipeDecision _decision;
        private bool _fullWasApplied;
        private long _fullFramesBase;
        private uint _freezeSig;
        private bool _frozenNow, _idleCause, _pausedCause, _stalledCause;
        private int _vsNow, _fpsNow;
        private uint _lastViewState = uint.MaxValue, _lastApproach = uint.MaxValue;
        private uint _mmView = uint.MaxValue, _mmApproach = uint.MaxValue, _mmPerf = uint.MaxValue;
        public long PhaseChanges;

        private readonly Action _stepPerf, _stepFreeze, _stepAudio, _stepWindowFrame, _stepInput, _stepPictureRow;

        public S2Host(IS2Bridge bridge, S1Host s1, Transform root)
        {
            // S7a: by perfMode (parked only
 // while SE asks for less than Full), S2's view rule under the seeded failure S7a.M2
            CameraDriver.ParkSource = ParkInputs;
            _bridge = bridge;
            _s1 = s1;
            _root = root;
            Window = new SnWindow(bridge);
            _stepPerf = StepPerf;
            _stepFreeze = StepFreeze;
            _stepAudio = StepAudio;
            _stepWindowFrame = StepWindowFrame;
            _stepInput = StepInput;
            _stepPictureRow = PictureRow;
        }

        public bool Ready { get { return _readyIdleDone; } }

        /// <summary>
 /// S7a: C5 flag bit 9 pictureSite of the C5 in force (the de.s2c5 override included): SE's view machine runs the
 /// Site picture policy (Subnautica's picture inside the site below the Approach band). Read by the S5 chunk culling
 /// (<see cref="MirrorCullRules"/>); false without a valid C5.
 /// </summary>
        public static bool PictureSite { get; private set; }

        /// <summary>S7a: the camera parking inputs of this frame (<see cref="CameraParkRules.Decide"/>).</summary>
        private PerfInputsForPark ParkInputs()
        {
            return new PerfInputsForPark
            {
                C5Valid = _eValid,
                View = _eValid ? _e.viewState : ViewState.Space,
                Approach = _eValid ? _e.approachMode : ApproachMode.None,
                PerfMode = _eValid ? _e.perfMode : PerfMode.Idle,
                ParkByView = DevMutants.IsOn(CameraParkRules.DevMutantS7aM2),
            };
        }

        /// <summary>S5a: the C5 view state (Wire.ViewState) this frame works on (the de.s2c5 override included); Space when none is valid.</summary>
        public uint C5ViewState { get { return _eValid ? _e.viewState : ViewState.Space; } }
        public uint C5ApproachMode { get { return _eValid ? _e.approachMode : ApproachMode.None; } }
        /// <summary>S5a: a C5 (or the test override) is in force.</summary>
        public bool C5ViewValid { get { return _eValid; } }

        public void Start()
        {
            _recipesOn = S2Switches.Recipes;
            Window.Enabled = S2Switches.Window;
            DeLog.Info("DE.S2 start: " + S2Switches.Describe() + " mutants=" + DevMutants.Describe() +
                       " freezeOwner=FreezeTime(" + UweFreezeApi.OwnerId + ") master bus=" + AudioMix.MasterBus);
            Guard("patches", S2Patches.Install);
            if (Window.Enabled) Guard("window", Window.Hook);
        }

        // ------------------------------------------------------------------------------------------ ReadyIdle

 /// <summary>Called by the bridge just before S1's first ReadyIdle is published: Idle, muted, restyled.</summary>
        public void BeforeReadyIdle(ulong now)
        {
            if (_readyIdleDone) return;
            _readyIdleDone = true;
            _now = now;
            _nextSecondMs = now + 1000;
            Guard("perf", Perf.SaveUser);
            Step(now);
            Guard("window", () => Window.ApplyAtReadyIdle(now));
            DeLog.Info("DE.S2 ReadyIdle set-up: recipe " + PerfRecipeRules.Name(_decision.Recipe) + " " + _decision.Pair + " frozen=" + _frozenNow +
                       " audio " + (Audio.Applied ? (Audio.Muted ? "muted" : "unmuted") : "pending (FMOD)") + " window " + (Window.StylesOk ? "restyled" : "NOT restyled") +
                       " (CONTRACT §7 Start: Idle, muted, restyled, lowered, then ReadyIdle)");
        }

        // ------------------------------------------------------------------------------------------ per frame

        public void Tick(ulong now)
        {
            _now = now;
            S2Patches.BiomeMutant = DevMutants.IsOn(DevMutants.S2BiomePatchOff);
            S2Patches.SilencerMutant = DevMutants.IsOn(DevMutants.S2InputSilencerOff);
            if (!_readyIdleDone) return;
            Pacing.Frame(now);
            Step(now);
            if (now >= _nextSecondMs)
            {
                _nextSecondMs = now + 1000;
                Second(now);
            }
        }

        private void Effective()
        {
            if (_override)
            {
                _e = _ovr;
                _eValid = true;
                _eAlive = true;
                return;
            }
            _e = _c5;
            _eValid = _c5Valid;
            _eAlive = _bridge.PeerState == SePeerState.Alive;
        }

        /// <summary>S7a: the C5 in force runs the Site picture policy (C5 flag bit 9).</summary>
        private bool SitePolicy { get { return _eValid && (_e.flags & S7aConventions.C5FlagsExt.PictureSite) != 0; } }

        private void Step(ulong now)
        {
            Effective();
            // S7a: the picture policy for the S5 culling and the areaBuilt camera box (S1 telemetry, next measurement)
            PictureSite = SitePolicy;
            if (_s1 != null) _s1.Tel.AreaBoxSite = PictureSite;
            if (_quitting)
            {
                // muted and frozen since OnQuitting; keep the mix muted against FreezeTime transitions of the quit path
                if (!Disabled("audio")) Guard("audio", _stepAudio);
                return;
            }
            RecipeInputs ri = Inputs();
            _decision = PerfRecipeRules.Select(ref ri);
            // S5.M4 / S5.M5 (ROADMAP S5 "Seeded failures"): Full instead of Idle in Space, cameras left on in Track
            S5PerfMutants.Apply(ref _decision, ref ri, DevMutants.IsOn(DevMutants.S5FullInSpace), DevMutants.IsOn(DevMutants.S5CamerasOnInTrack));
            S10Sn.CapRecipe(ref _decision); // S10 hook (round 2 D): the preset's frame-rate cap (low: 30 on shared GPUs); none by default
            NoteViewState();
            if (_recipesOn && !Disabled("perf")) Guard("perf", _stepPerf);
            ReadPair();
            bool fullApplied = _recipesOn ? (Perf.HasApplied && Perf.Applied.Recipe == PerfMode.Full && Perf.CamerasOnNow) : true;
            if (fullApplied && !_fullWasApplied) _fullFramesBase = FramesPresented();
            _fullWasApplied = fullApplied;

            if (_recipesOn && !Disabled("freeze")) Guard("freeze", _stepFreeze);
            if (!Disabled("audio")) Guard("audio", _stepAudio);
            // review fix (finding 3): no hide/show restyle while SE shows Subnautica
 // (the last C5 of the current SE generation counts even while SE stalls: its window may still show ours)
            Window.HideAllowed = !WindowStyleRules.SeShowsSubnautica(_eValid, _eValid ? _e.viewState : ViewState.Space, _eValid ? _e.flags : 0u);
            if (!Disabled("window")) Guard("window", _stepWindowFrame);
            if (!Disabled("input") && _s1 != null && _s1.InWorld) Guard("input", _stepInput);

            UpdatePhase(fullApplied);
            if (!Disabled("s7a.picture") && _s1 != null && _s1.InWorld) Guard("s7a.picture", _stepPictureRow);
        }

        // ------------------------------------------------------------------------------------------ S7a s7a.picture row

        private uint _prKey = uint.MaxValue;
        private ulong _prNextMs;
        public long PictureRows;

        /// <summary>One <c>s7a.picture</c> row at once.</summary>
        public void PictureRowNow()
        {
            if (Disabled("s7a.picture") || _s1 == null || !_s1.InWorld) return; // as the per-frame call
            _prKey = uint.MaxValue;
            _prNextMs = 0;
            Guard("s7a.picture", _stepPictureRow);
        }

        /// <summary>
 /// S7a: one <c>s7a.picture</c> row {snFrame, presentedSeTick, view, approach, perfMode, perfApplied, seeThrough,
 /// marginsOn, pictureSite, park, phase, hideAllowed, camY, frameMsP99} on every change of the picture-relevant
 /// fields and once per second (ROADMAP S7a JSONL rows; S7a.N1 reads it).
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PictureRow()
        {
            uint flags = _eValid ? _e.flags : 0u;
            bool see = (flags & SeStateFlags.SeeThrough) != 0, margins = (flags & SeStateFlags.MarginsOn) != 0;
            bool park = CameraParkRules.Decide(ParkInputs());
            uint applied = _recipesOn ? (Perf.HasApplied ? Perf.Applied.Recipe : PerfMode.Idle) : PerfMode.Full;
            uint view = _eValid ? _e.viewState : 7u, approach = _eValid ? _e.approachMode : 0u, perf = _eValid ? _e.perfMode : 7u;
            uint key = (view & 7u) | ((approach & 3u) << 3) | ((perf & 7u) << 5) | ((applied & 7u) << 8) | (see ? 1u << 11 : 0u) |
                       (margins ? 1u << 12 : 0u) | (PictureSite ? 1u << 13 : 0u) | (park ? 1u << 14 : 0u) | ((_bridge.Phase & 15u) << 15) |
                       (Window.HideAllowed ? 1u << 19 : 0u);
            if (key == _prKey && _now < _prNextMs) return;
            _prKey = key;
            _prNextMs = _now + 1000;
            PictureRows++;
            Camera cam = MainCamera.camera;
            JsonOut j = S5Rows.Begin("s7a.picture");
            j.Num("snFrame", _bridge.SnFrame).Num("presentedSeTick", _s1 != null ? _s1.Cam.PresentedSeTick : 0UL)
             .Str("view", _eValid ? PerfRecipeRules.ViewName(_e.viewState, _e.approachMode) : "none")
             .Num("approach", (long)approach).Num("perfMode", _eValid ? (long)_e.perfMode : -1L).Num("perfApplied", (long)applied)
             .Bool("seeThrough", see).Bool("marginsOn", margins).Bool("pictureSite", PictureSite).Bool("park", park)
             .Str("phase", SnPhase.Name(_bridge.Phase)).Bool("hideAllowed", Window.HideAllowed)
             .Num("camY", cam != null ? (double)cam.transform.position.y : double.NaN).Num("frameMsP99", (long)Pacing.LastP99Ms)
             .Bool("override", _override);
            S5Rows.End(j);
        }

        private RecipeInputs Inputs()
        {
            bool valid = _eValid;
            uint flags = valid ? _e.flags : 0u;
            uint overlays = valid ? _e.overlays : 0u;
            return new RecipeInputs
            {
                C5Valid = valid,
                PeerAlive = _eAlive,
                PerfMode = valid ? _e.perfMode : PerfMode.Idle,
                TargetFps = valid ? _e.targetFps : 0u,
                ViewState = valid ? _e.viewState : ViewState.Space,
                ApproachMode = valid ? _e.approachMode : ApproachMode.None,
                SeForeground = valid && (flags & SeStateFlags.SeForeground) != 0 && (overlays & Overlays.Unfocused) == 0,
                SeeThrough = valid && (flags & SeStateFlags.SeeThrough) != 0,   // S7a: the unfocused (0, 30) rule
                IdleRecipeOff = DevMutants.IsOn(DevMutants.S2IdleRecipeOff),
                VSyncLeftInIdle = DevMutants.IsOn(DevMutants.S2VSyncLeftInIdle),
                UserVSync = Perf.UserVSync,
                UserTargetFps = Perf.UserTargetFps,
            };
        }

        private bool Valid { get { return _eValid && _eAlive; } }

        private void StepPerf()
        {
            Perf.Apply(ref _decision, _now, WaitScreenUp());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool WaitScreenUp() { return WaitScreen.IsWaiting; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReadPair()
        {
            _vsNow = QualitySettings.vSyncCount;
            _fpsNow = Application.targetFrameRate;
        }

        private void StepFreeze()
        {
            bool valid = Valid;
            uint flags = valid ? _e.flags : 0u;
            uint overlays = valid ? _e.overlays : 0u;
            SePeerState peer = _bridge.PeerState;
            _idleCause = _decision.IdleCause;
            _pausedCause = valid && ((flags & SeStateFlags.SePaused) != 0 || (overlays & Overlays.Paused) != 0);
            _stalledCause = !_override && (peer == SePeerState.Stalled || peer == SePeerState.Exited);
            bool wasFrozen = _frozenNow;
            Freeze.PerCauseMutant = DevMutants.IsOn(DevMutants.S2FreezePerCause);
            bool called = Freeze.Update(_idleCause, _pausedCause, _stalledCause);
            _frozenNow = Freeze.Frozen;
            if (called)
            {
                Audio.MarkDirty();
                DeLog.Info("DE.Freeze " + Freeze.LastReason + " -> frozen=" + _frozenNow + " causes=" + FreezeOwnerCore.Causes(_idleCause, _pausedCause, _stalledCause) +
                           " timeScale=" + Time.timeScale.ToString("0.##", CultureInfo.InvariantCulture) + " top=" + FreezeSignature.Top() +
                           (Freeze.PerCauseMutant ? " (MUTANT S2.M12: one pair per cause)" : ""));
            }
            else if (wasFrozen != _frozenNow)
            {
                DeLog.Warn("DE.Freeze FeedbackPanel entry " + (_frozenNow ? "appeared" : "vanished") + " outside the owner (wanted=" + Freeze.Wanted + ")");
            }
        }

        private void StepAudio()
        {
            uint sig = FreezeSignature.Read();
            if (sig != _freezeSig)
            {
                _freezeSig = sig;
                Audio.MarkDirty();
            }
            bool valid = Valid;
            uint flags = valid ? _e.flags : 0u;
            uint overlays = valid ? _e.overlays : 0u;
            bool fg = valid && (flags & SeStateFlags.SeForeground) != 0 && (overlays & Overlays.Unfocused) == 0;
            Audio.Tick(_now, valid, fg, _quitting, valid ? _e.snBusTarget : 0f, valid ? _e.crossfadeMs : 0f,
                       DevMutants.IsOn(DevMutants.S2AboveWaterMuteSkipped));
        }

        private void StepWindowFrame() { Window.Frame(_now); }

        private void StepInput() { Input.Frame(S2Patches.SilencerMutant); }

        private long FramesPresented() { return _s1 != null ? _s1.Cam.FramesPresented : 0; }

        private void UpdatePhase(bool fullApplied)
        {
            uint cur = _bridge.Phase;
            bool worldReady = _s1 != null && _s1.InWorld;
            bool warm = PhaseRules.WarmWanted(_eValid, _eAlive, _eValid ? _e.viewState : ViewState.Space,
                                              _eValid ? _e.approachMode : ApproachMode.None);
            long since = FramesPresented() - _fullFramesBase;
            uint target = PhaseRules.Target(cur, worldReady, _bridge.Quitting || _quitting, warm, fullApplied, since);
            if (target == cur) return;
            PhaseChanges++;
            string view = _eValid ? PerfRecipeRules.ViewName(_e.viewState, _e.approachMode) + (_override ? " (de.s2c5 override)" : "") : "no C5";
            string why = target == SnPhase.ReadyIdle ? "C5 " + view + " (S1 ReadyIdle)" :
                         target == SnPhase.WarmingUp ? "C5 " + view + "; Full " + (fullApplied ? "applied, " + since + " frame(s) presented" : "not applied yet") :
                         "C5 " + view + "; Full applied, " + since + " frame(s) presented by the main camera";
            _bridge.SetPhase(target, why);
        }

        private void NoteViewState()
        {
            if (!_eValid) return;
            uint v = _e.viewState, a = _e.approachMode;
            if (v != _lastViewState || a != _lastApproach)
            {
                _lastViewState = v;
                _lastApproach = a;
                DeLog.Info("DE.S2 C5 view " + PerfRecipeRules.ViewName(v, a) + " perfMode=" + PerfRecipeRules.Name(_e.perfMode) + " targetFps=" + _e.targetFps +
                           " zone=" + _e.zone + " busTarget=" + F(_e.snBusTarget) + " crossingTick=" + _e.crossingTick + " flags=" + DeLog.Hex(_e.flags) +
                           " overlays=" + DeLog.Hex(_e.overlays) + (_override ? " (de.s2c5 override)" : ""));
            }
            uint expected = PerfRecipeRules.ExpectedPerfMode(v, a, SitePolicy);   // S7a: Full in Track under the Site picture policy
 // review fix (finding 19): the once-key string is built only when the triple changes, not every frame
            if (expected != _e.perfMode && (v != _mmView || a != _mmApproach || _e.perfMode != _mmPerf))
            {
                _mmView = v; _mmApproach = a; _mmPerf = _e.perfMode;
                string key = "c5mismatch:" + v + ":" + a + ":" + _e.perfMode;
                if (_onceKeys.Add(key))
                    DeLog.Info("DE.S2 C5 perfMode " + PerfRecipeRules.Name(_e.perfMode) + " while the view state " + PerfRecipeRules.ViewName(v, a) +
                               " implies " + PerfRecipeRules.Name(expected) + ": the recipe follows perfMode, the phase follows the view state" +
                               " (an S1 SE publishes Full in the dive zone with view state Space)");
            }
        }

        // ------------------------------------------------------------------------------------------ per second

        private void Second(ulong now)
        {
            Pacing.Roll(now);
            Effective();
            if (!Disabled("window")) Guard("window", () => Window.Second(now, Valid, Valid ? RectI.From(_e.seRect) : default(RectI)));
            if (_recipesOn && !Disabled("perf")) Guard("perf", () => Perf.Second(now, Pacing));
            if (!Disabled("calib")) Guard("calib", Calib.Second);
            if (!Disabled("telemetry")) Guard("telemetry", () => DeLog.Info(SecondLine()));
            if (!Disabled("input") && _s1 != null && _s1.InWorld) Guard("input", () => DeLog.Info(Input.Second()));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string SecondLine()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(640);
            sb.Append("DE.S2 sec phase=").Append(SnPhase.Name(_bridge.Phase));
            sb.Append(" c5=").Append(_eValid ? PerfRecipeRules.ViewName(_e.viewState, _e.approachMode) + "/" + PerfRecipeRules.Name(_e.perfMode) +
                                                 "/" + _e.targetFps.ToString(ci) + (_override ? "(OVERRIDE de.s2c5)" : "") : "none");
            sb.Append(" se=").Append(SePeerMonitor.Name(_bridge.PeerState));
            sb.Append(" recipe=").Append(_recipesOn ? PerfRecipeRules.Name(_decision.Recipe) + _decision.Pair : "OFF");
            sb.Append(" applied=(").Append(_vsNow.ToString(ci)).Append(", ").Append(_fpsNow.ToString(ci)).Append(')');
            sb.Append(" fps=").Append(Pacing.LastFps.ToString("0.0", ci)).Append(" maxGapMs=").Append(Pacing.LastMaxGapMs.ToString(ci));
            sb.Append(" gaps>100ms=").Append(Pacing.GapsOver100Ms.ToString(ci)).Append(" gaps>1s=").Append(Pacing.GapsOver1s.ToString(ci));
            sb.Append(" cameras=").Append(Perf.CamerasOnNow ? "on" : "off").Append(" priority=").Append(SnNative.PriorityName(Perf.PriorityNow));
            sb.Append(" frozen=").Append(_frozenNow ? "true" : "false").Append(" causes=").Append(FreezeOwnerCore.Causes(_idleCause, _pausedCause, _stalledCause));
            sb.Append(" timeScale=").Append(Time.timeScale.ToString("0.##", ci)).Append(" freezeTop=").Append(FreezeSignature.Top());
            sb.Append(" freezeCalls=").Append(Freeze.Begins.ToString(ci)).Append('/').Append(Freeze.Ends.ToString(ci)).Append('/').Append(Freeze.Reasserts.ToString(ci));
            sb.Append(' ').Append(Audio.Second());
            sb.Append(' ').Append(Window.Describe()).Append(" rectOk=").Append(Window.RectOk ? "true" : "false");
            sb.Append(" biome{void=").Append(S2Patches.BiomeVoidAnswers.ToString(ci)).Append(" passThrough=").Append(S2Patches.BiomePassThrough.ToString(ci)).Append('}');
            sb.Append(" presentedSeTick=").Append(_s1 != null ? _s1.Cam.PresentedSeTick.ToString(ci) : "n/a");
            sb.Append(" framesPresented=").Append(FramesPresented().ToString(ci));
            sb.Append(' ').Append(Calib.Describe());
            sb.Append(' ').Append(S8Host.SecondText()); // S8-memory (ROADMAP S8.N3, S8.N4): s8{audits fails peerRows} mem{ws priv mono gc load commit}
            if (_disabled.Count > 0) sb.Append(" featuresDisabled=").Append(string.Join(",", new List<string>(_disabled).ToArray()));
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------ bridge events

 /// <summary>A fresh C5 of the current SE generation.</summary>
        public void OnSeState(ref SeState st)
        {
            _c5 = st;
            _c5Valid = true;
            _c5Ms = Win32.TickCount64();
            C5Seen++;
        }

        /// <summary>A new SE generation: its C5 is not used until it changes (CONTRACT §6); the mix restarts from silence.</summary>
        public void OnNewSePeer()
        {
            _c5Valid = false;
            _lastViewState = uint.MaxValue;
            _lastApproach = uint.MaxValue;
            Audio.Fade.Set(0f, Win32.TickCount64());
            Audio.MarkDirty();
        }

        /// <summary>The bridge is quitting (CONTRACT §7, ARCHITECTURE §5.14): muted and frozen at once; minimised when SE exited.</summary>
        public void OnQuitting(string why, bool seExited)
        {
            if (_quitting) return;
            _quitting = true;
            if (!_readyIdleDone) return;
            ulong now = Win32.TickCount64();
            _now = now;
            Effective();
            Guard("audio", () => { Audio.MarkDirty(); StepAudio(); });
            if (!Disabled("freeze"))
                Guard("freeze", () =>
                {
                    Freeze.PerCauseMutant = false;
                    if (Freeze.Update(true, false, false)) DeLog.Info("DE.Freeze " + Freeze.LastReason + " (quitting: " + why + ")");
                    _frozenNow = Freeze.Frozen;
                });
            if (seExited) Guard("window", () => Window.MinimiseNoActivate(why));
            DeLog.Info("DE.S2 quitting (" + why + "): muted=" + Audio.Muted + " frozen=" + _frozenNow);
        }

        /// <summary>Application quit: the user's frame-rate pair and priority back, the Input System setting back (in memory).</summary>
        public void OnApplicationQuit()
        {
            Guard("perf", () => Perf.RestoreUser("application quit", false));
            Guard("input", () => Input.RestoreBackground("application quit"));
        }

        /// <summary>The bridge is going away without a quit (failure, withdrawal): Subnautica is left vanilla-like.</summary>
        public void Dispose()
        {
            try { Window.Unhook(); } catch (Exception) { }
            try { Calib.Dispose(); } catch (Exception) { }
            try { Input.RestoreBackground("bridge disposal"); } catch (Exception) { }
            if (_appQuitting) return;
            try { if (Freeze.Release()) DeLog.Info("DE.Freeze released (bridge disposal)"); } catch (Exception) { }
            try { if (_readyIdleDone) FmodUnmute(); } catch (Exception) { }
            try { if (_readyIdleDone) Perf.RestoreUser("bridge disposal", true); } catch (Exception) { }
        }

        private bool _appQuitting;
        public void MarkAppQuitting() { _appQuitting = true; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FmodUnmute()
        {
            if (FMODUnity.RuntimeManager.IsInitialized) FMODUnity.RuntimeManager.MuteAllEvents(false);
        }

        // ------------------------------------------------------------------------------------------ C7

 /// <summary>C7 fields owned by S2 (from ReadyIdle on; before it S0/S1 values stand).</summary>
        public void FillStatus(ref SnStatus s)
        {
            if (!_readyIdleDone) return;
            s.perfModeApplied = _recipesOn ? (Perf.HasApplied ? Perf.Applied.Recipe : PerfMode.Idle) : PerfMode.Full;
            s.vSyncCountApplied = (uint)Math.Max(0, _vsNow);
            s.targetFrameRateApplied = _fpsNow;
            if (Window.RectOk) s.flags |= SnStatusFlags.RectOk;
            if (_frozenNow) s.flags |= SnStatusFlags.Frozen;
            IntPtr h = Window.Hwnd;
            if (h != IntPtr.Zero) s.snHwnd = (ulong)h.ToInt64();
            s.snRect = Window.SnRect.ToI32x4();
        }

        // ------------------------------------------------------------------------------------------ C11

 /// <summary>The S2 commands; false when the command is not one of them.</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            switch (req.Cmd)
            {
                case "de.calib":
                {
                    bool off;
                    byte r, g, b;
                    string error;
                    if (!CalibRules.TryParse(req.Args, out off, out r, out g, out b, out error))
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error);
                        return true;
                    }
                    if (!off && !(_s1 != null && _s1.InWorld))
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "not in the host world (snPhase " + SnPhase.Name(_bridge.Phase) + ")");
                        return true;
                    }
                    bool ok = true;
                    if (off) Guard("calib", () => Calib.Hide("de.calib off"));
                    else
                    {
                        ok = !Disabled("calib");
                        Guard("calib", () => Calib.Show(_root, r, g, b));
                        ok &= !Disabled("calib");
                    }
                    reply.Bool("ok", ok).Str("cmd", req.Cmd).Str("calib", Calib.On ? "on" : "off");
                    if (Calib.On) reply.Str("colour", CalibRules.Hex(r, g, b)).Num("r", (long)r).Num("g", (long)g).Num("b", (long)b);
                    if (!ok) reply.Str("error", "calibration pattern feature disabled (see DE.S2 FEATURE in the log)");
                    return true;
                }

                case "de.s2":
                    Status(reply.Bool("ok", true).Str("cmd", req.Cmd), now);
                    return true;

                case "de.biome":
                {
                    // review fix (finding 8): S2.N6 asks GetBiome directly (main thread, through the patch)
                    double x, y, z;
                    string error;
                    if (!BiomeRules.TryParseQuery(req.Args, out x, out y, out z, out error))
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error);
                        return true;
                    }
                    bool hasWorld = false;
                    string biome = null;
                    string fail = null;
                    long voidBefore = S2Patches.BiomeVoidAnswers;
                    try { biome = S2Patches.QueryBiome(new Vector3((float)x, (float)y, (float)z), out hasWorld); }
                    catch (Exception e) { fail = e.GetType().Name + ": " + e.Message; }
                    bool outside = BiomeRules.OutsideTile(x, z);
                    reply.Bool("ok", fail == null && hasWorld).Str("cmd", req.Cmd).Num("x", x).Num("y", y).Num("z", z)
                         .Str("biome", biome ?? "(null)").Bool("biomeIsNull", biome == null).Bool("outsideTile", outside)
                         .Bool("patched", S2Patches.BiomePatched).Bool("answeredByPatch", S2Patches.BiomeVoidAnswers > voidBefore)
                         .Bool("mutantS2M5", S2Patches.BiomeMutant).Num("voidSpawnsSkipped", S2Patches.VoidSpawnsSkipped);
                    if (!hasWorld && fail == null) reply.Str("error", "LargeWorld.main is null (not in the host world)");
                    if (fail != null) reply.Str("error", fail);
                    DeLog.Info("DE.Biome query (" + x.ToString("0.#", CultureInfo.InvariantCulture) + ", " + y.ToString("0.#", CultureInfo.InvariantCulture) + ", " +
                               z.ToString("0.#", CultureInfo.InvariantCulture) + "): \"" + (biome ?? "(null)") + "\" outsideTile=" + outside +
                               " patch=" + (S2Patches.BiomePatched ? (S2Patches.BiomeMutant ? "OFF (MUTANT S2.M5)" : "on") : "MISSING"));
                    return true;
                }

                case "de.s2c5":
                {
                    bool off;
                    SeState st;
                    string describe, error;
                    if (!S2TestC5.TryParse(req.Args, out off, out st, out describe, out error))
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error);
                        return true;
                    }
                    if (off)
                    {
                        bool was = _override;
                        _override = false;
                        _lastViewState = uint.MaxValue;
                        if (was) DeLog.Warn("DE.S2 TEST C5 override OFF (de.s2c5): SE's C5 is used again" + (_c5Valid ? "" : " (none from a live SE yet: Idle)"));
                        reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("override", false);
                        return true;
                    }
                    _override = true;
                    _ovr = st;
                    _ovrDescribe = describe;
                    DeLog.Warn("DE.S2 TEST C5 override ON (de.s2c5, dev test hook; SE's C5 ignored until off): " + describe);
                    reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("override", true).Str("c5", describe)
                         .Str("note", "the recipe, freeze owner, mix, rect check and phase follow it from the next frame");
                    return true;
                }
            }
            return false;
        }

        private void Status(JsonObjectWriter w, ulong now)
        {
            Effective();
            w.Str("snPhase", SnPhase.Name(_bridge.Phase)).Bool("readyIdleSetUp", _readyIdleDone).Bool("recipes", _recipesOn)
             .Bool("c5Valid", _c5Valid).Num("c5Seen", C5Seen).Num("c5AgeMs", _c5Valid ? (long)(now - _c5Ms) : -1L)
             .Bool("c5Override", _override).Str("c5OverrideValue", _override ? _ovrDescribe : "")
             .Str("view", _eValid ? PerfRecipeRules.ViewName(_e.viewState, _e.approachMode) : "none")
             .Num("c5PerfMode", _eValid ? (long)_e.perfMode : -1L).Num("c5TargetFps", _eValid ? (long)_e.targetFps : -1L)
             .Num("c5BusTarget", _eValid ? (double)_e.snBusTarget : -1.0).Num("c5CrossingTick", _eValid ? _e.crossingTick : 0UL)
             .Str("recipe", PerfRecipeRules.Name(_decision.Recipe)).Str("recipeWhy", _decision.Why ?? "")
             .Num("vSyncCount", (long)_vsNow).Num("targetFrameRate", (long)_fpsNow).Bool("camerasOn", Perf.CamerasOnNow)
             .Str("priority", SnNative.PriorityName(Perf.PriorityNow)).Num("fps", (double)Pacing.LastFps).Num("maxGapMs", Pacing.LastMaxGapMs)
             .Bool("frozen", _frozenNow).Str("freezeCauses", FreezeOwnerCore.Causes(_idleCause, _pausedCause, _stalledCause))
             .Num("timeScale", (double)Time.timeScale).Str("freezeTop", FreezeSignature.Top())
             .Bool("muted", Audio.Muted).Str("muteWhy", Audio.MuteWhy).Num("busFade", (double)Audio.Fade.Value).Num("busTarget", (double)Audio.EffectiveTarget)
             .Str("hwnd", SnNative.Hwnd(Window.Hwnd)).Bool("stylesOk", Window.StylesOk).Str("exStyle", WindowStyleRules.Describe(Window.ExStyle))
             .Str("snRect", Window.SnRect.ToString()).Str("seRect", Window.SeRect.ToString()).Bool("rectOk", Window.RectOk)
             .Str("rectVerdict", RectRules.Describe(Window.Verdict)).Num("rectCorrections", (long)Window.CorrectionAttempts)
             .Num("biomeVoid", S2Patches.BiomeVoidAnswers).Bool("biomePatch", S2Patches.BiomePatched)
             .Bool("voidSpawnerPatch", S2Patches.VoidSpawnerPatched).Num("voidSpawnsSkipped", S2Patches.VoidSpawnsSkipped)
             .Bool("hideAllowed", Window.HideAllowed).Num("hidesSkipped", Window.HidesSkipped)
             .Bool("pictureSite", SitePolicy).Bool("seeThrough", _eValid && (_e.flags & SeStateFlags.SeeThrough) != 0)
             .Bool("parked", CameraParkRules.Decide(ParkInputs())).Bool("parkByViewMutantS7aM2", DevMutants.IsOn(CameraParkRules.DevMutantS7aM2))
             .Num("pictureRows", PictureRows).Num("frameMsP99", (long)Pacing.LastP99Ms)
             .Str("calib", Calib.On ? CalibRules.Hex(Calib.R, Calib.G, Calib.B) : "off")
             .Str("switches", S2Switches.Describe()).Str("mutants", DevMutants.Describe())
             .Str("featuresDisabled", _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()))
             .Num("tickMs", now);
        }

        // ------------------------------------------------------------------------------------------ feature guard

        private bool Disabled(string feature) { return _disabled.Contains(feature); }

        /// <summary>Runs one feature step; its first exception disables that feature only (logged once).</summary>
        private void Guard(string feature, Action a)
        {
            if (_disabled.Contains(feature)) return;
            try
            {
                a();
            }
            catch (Exception e)
            {
                _disabled.Add(feature);
                DeLog.Error("DE.S2 FEATURE " + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message +
                            " (the rest of the bridge keeps running)\n" + e);
            }
        }

        private static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
