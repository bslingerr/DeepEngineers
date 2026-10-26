using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S5a "arms and breath" part of the Subnautica side (ROADMAP S5a; docs/S5A-STATUS.md; CONTRACT §5.4 "C1 swim
 /// extension", §2.10, §4.2), driven by the S0 bridge after S1-S3 each frame and by the S1 camera callbacks:
 /// <list type="number">
 /// <item><b>Input:</b> the C1 swim extension the camera driver's reader decodes with every fresh record
 /// (<see cref="FrameReader.Swim"/>; version 1 = valid, 0 = an SE build before S5a: everything below stays off and
 /// Subnautica keeps its vanilla ghost behaviour). Its tick is the join key of every contract line.</item>
 /// <item><b>Arms</b> (<see cref="StepArms"/>): the first-person view model
 /// is shown only while <see cref="S5aRules.ArmsVisible"/>
 /// holds (C1 inWater, the camera under the sea, SE first person, C5 Underwater/Surfacing, C1 driving), posed at the
 /// rig's position with the rig's yaw (what the disabled MainCameraControl would write: same local position, yaw
 /// only; the pitch is the animator's <c>view_pitch</c>), and animated through the Player animator's own parameters
 /// (<c>is_underwater</c>, <c>on_surface</c>, <c>move_speed*</c>, <c>view_pitch</c>, <c>view_turn</c>, the tool and
 /// vehicle bools false) from the C1 fields. <c>ArmsController</c> is disabled while S5a drives
 /// no Subnautica motor or input runs: the ghost stays LockedPiloting with
 /// PlayerController disabled and the rigidbody kinematic, and the view model is a child of the Player root that is
 /// moved by GhostPlayer only. Hidden again when any condition fails (exit, gate hold, override, resync, quit).</item>
 /// <item><b>Oxygen</b> (<see cref="StepO2"/>): every frame with a valid extension the ghost's <c>OxygenManager</c>
 /// is set to <c>o2/255 × capacity</c> through its own <c>AddOxygen</c>/<c>RemoveOxygen</c>; its Update
 /// is disabled while mirroring so nothing fights the
 /// mirror, and the ghost's own breaths are already off. The real systems then read it:
 /// <c>LowOxygenAlert.Update</c>, <c>uGUI_OxygenBar</c>, <c>FMODGameParams</c>, <c>Player.SuffocationUpdate</c>. The
 /// ghost never dies: <c>liveMixin.shielded</c> stays on (GhostPlayer), <see cref="S5aPatches"/> skips
 /// <c>Player.SuffocationDie</c>, and without that patch the mirror floors the units at 1 (no suffocation at all).</item>
 /// <item><b>Breath</b> (<see cref="StepBreath"/>): the game's breath and bubble triggers are fired from the C1 state
 /// on Subnautica's own cadence (<see cref="BreathScheduler"/>: <c>Player.tookBreathEvent</c> every breath period of
 /// the depth class, <c>PlayerBreathBubbles.MakeBubbles</c> every <c>delay</c>); the low-O2 alert thresholds are
 /// watched on the mirrored units (<see cref="LowO2Monitor"/>) and the suffocation state for the choke.</item>
 /// <item><b>PDA voice</b> stays suppressed by S1's entry-point prefixes (<see cref="S1Patches"/>: SoundQueue.Play,
 /// VoiceNotification.Play, PDANotification.Play, Subtitles, popups; on since the host save load) — a bus-level mute
 /// would also silence the breath, bubble and alert emitters that share Subnautica's SFX buses. DE.PDA reports the
 /// counters once a minute and at shutdown.</item>
 /// </list>
 /// Log lines exactly as docs/S5A-STATUS.md §6 (parsed by tests/s5a/analyze_s5a.py): DE.Arms, DE.O2, DE.Breath, DE.PDA,
 /// DE.SwimWire; free-text lines use DE.S5a. No DE.Ghost line is ever written here. Each feature has its own guard:
 /// the first exception logs `DE.S5a FEATURE … DISABLED` once and stops that feature only. Dev C11: <c>de.s5a</c>
 /// (status), <c>de.arms</c>, <c>de.o2</c>.
 /// </summary>
    internal sealed class S5aHost
    {
        private readonly IS2Bridge _bridge;
        private readonly S1Host _s1;
        private readonly S2Host _s2;

        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private readonly Action _stepArms, _stepO2, _stepBreath, _stepPose, _stepPda;

        // per-frame inputs (LateUpdateLast)
        private ulong _now;
        private CamSample _sample;
        private Vector3 _camPos;
        private bool _driven, _camUnder, _snShows, _firstPerson;
        private SwimWireState _ext;
        private bool _extValid;
        private ulong _tick;
        private float _dt;

        // S7a hook (CONTRACT §5.4 "C1 locomotion extension", Subnautica side; S7aAnimRules): the C1 locomotion extension of
 // the same record drives the animator out of the water in a foot state (Subnautica's walk/idle/run/jump clips for
 // CharacterMirror's Retarget and the view-model arms above water); everything else keeps the S5a behaviour
        private LocoWireState _loco;
        public static long ToolHeldFrames;
        /// <summary>

 /// under fpbody=se the diver arms are off, and with them Subnautica's dive-in animation and its sound. For this long after
 /// the camera crosses the surface while the character is in the water, the diver arms show so the dive plays.
 /// </summary>
        public const ulong SplashWindowMs = 1300;
        public static long SplashWindows;
        private bool _camUnderPrev;
        private ulong _splashUntil;
        /// <summary>SE holds a gun or tool this frame (C1 ToolHeld): CharacterMirror keeps SE's bones while swimming then.</summary>
        public static bool ToolHeldNow;
        private bool _locoValid, _locoOut, _armsOut;
        private float _pelvisY;
        private AnimParams _outParams;
        private readonly AirClock _air = new AirClock();
        private readonly S7aRowCadence _animRow = new S7aRowCadence(500);
        public long OutOfWaterFrames, ArmsOutFrames;

        // settings
        public ArmsGate Gate = ArmsGate.Full;
        public bool MaskOn = true;
        public bool MirrorOn = true;
        public double O2FloorUnits;
        public Vector3 ViewModelOffset = Vector3.zero;

        // arms
        private Transform _viewModel;
        private Renderer[] _armsRenderers = new Renderer[0];
        private readonly HashSet<Renderer> _exempt = new HashSet<Renderer>();
        private Animator _animator;
        private HashSet<string> _params;
        private ArmsController _arms;
        private bool _rigFound, _rigFailed, _driving, _visible, _maskActive, _armsControllerWasEnabled = true;
        private AnimatorCullingMode _cullingWas = AnimatorCullingMode.AlwaysAnimate;
        private readonly ArmsMotion _motion = new ArmsMotion();
        private readonly ArmsLogCadence _armsLog = new ArmsLogCadence();
        private string _state = "out";
        private string _hiddenWhy = "not started";
        private float _prevYaw;
        private bool _hasPrevYaw;
        private int _poses;
        public long ArmsShown, ArmsHidden, Poses, ParamFrames;

        // oxygen
        private readonly PeriodicDue _o2Log = new PeriodicDue(S5aRules.LogPeriodMs);
        private bool _managerDisabledByUs;
        private double _mirrored, _snOxygen, _capacity;
        private bool _o2Override, _shielded;
        public long O2Writes;

        // breath
        private PlayerBreathBubbles _bubbles;
        private MethodInfo _makeBubbles;
        private double _bubbleDelayS = S5aRules.DefaultBubbleDelayS;
        private LowOxygenAlert _alert;
        private LowO2Monitor _lowO2 = new LowO2Monitor(new float[0]);
        private string _alertsDescribe = "not read";
        private readonly BreathScheduler _breath = new BreathScheduler();
        private bool _wasSuffocating;
        // breathing loop: BreathingSound.UpdateSound starts the looping breath event only with mode Normal, an oxygen tank and
 // Player.isUnderwater, none of which the ghost has, and it stops the event every 0.25 s
 // otherwise
 //: "pas de bruit de respiration ni de bulles")
        private BreathingSound _breathing;
        private bool _breathingTaken, _loopOn;
        private ulong _lastSearchMs;
        public long LoopStarts, LoopStops;
        public long Breaths, Bursts, LowO2Events, Chokes, Refills, GameAlertPlays, GameBubblePlays;

        // pda
        private readonly PeriodicDue _pdaLog = new PeriodicDue(S5aRules.PdaPeriodMs);
        private long _pdaSuppressedBase, _pdaPassedBase;
        private bool _pdaFinalWritten;

        // wire
        private bool _wireLogged;
        private byte _wireVersionLogged;
        public long WireLines;

        private bool _started, _quitting, _disposed;

        public S5aHost(IS2Bridge bridge, S1Host s1, S2Host s2)
        {
            _bridge = bridge;
            _s1 = s1;
            _s2 = s2;
            _stepArms = StepArms;
            _stepO2 = StepO2;
            _stepBreath = StepBreath;
            _stepPose = StepPose;
            _stepPda = StepPda;
        }

        public bool Started { get { return _started; } }

        /// <summary>
 /// S7p-character (P8): the diver arms (view model, exempt from the ghost's renderer rule) are shown this frame. Under
 /// fpbody=se the S5 host keeps <see cref="Gate"/> ForceOff, so this stays false; the <c>s7p.char</c> row reports it.
 /// </summary>
        public bool ArmsVisibleNow { get { return _visible; } }

        public void Start()
        {
            if (_started) return;
            _started = true;
            MaskOn = S5aSwitches.Mask;
            Guard("patches", S5aPatches.Install);
            O2FloorUnits = S5aPatches.SuffocationDiePatched ? 0.0 : S5aRules.O2FloorUnpatched;
            _pdaSuppressedBase = PdaSuppressedTotal();
            _pdaPassedBase = S1Patches.PdaPassed;
            _s1.AfterGhostFrame = LateFrame;
            _s1.AfterPreCullFrame = PreCullFrame;
            S1Patches.EmitterPlayed = OnEmitterPlayed;
            DeLog.Info("DE.S5a start: " + S5aSwitches.Describe() + " gate=" + S5aRules.GateName(Gate) + " o2Floor=" + F(O2FloorUnits) + " units" +
                       " (SuffocationDie " + (S5aPatches.SuffocationDiePatched ? "patched" : "NOT patched: floor keeps the ghost from suffocating") + ")" +
                       " pdaSuppression=S1Patches entry-point prefixes (SoundQueue.Play, VoiceNotification.Play, PDANotification.Play; SuppressUi=" + S1Patches.SuppressUi + ")" +
                       " swimExtHash=" + DeLog.Hex(SwimWire.Hash));
        }

        // ------------------------------------------------------------------------------------------ per frame (Update)

 /// <summary>The bridge's Update: the clock and the minute line.</summary>
        public void Tick(ulong now)
        {
            _now = now;
            if (!_s1.InWorld || !_s1.Ghost.Ready) return;
            if (!Disabled("pda")) Guard("pda", _stepPda);
        }

        // ------------------------------------------------------------------------------------------ LateUpdateLast

        private void LateFrame(CamSample s, Vector3 camPos, bool driven)
        {
            if (_disposed) return;
            if (!_s1.InWorld || !_s1.Ghost.Ready)
            {
                // stage 3: the ghost rule stopped (world gone, ghost not ready) while the arms were shown or driven:
 // release everything so no exemption, mask or disabled ArmsController outlives the ghost rule
                if (_visible || _driving || _maskActive) Guard("arms", () => ReleaseArms("ghost not ready"));
                return;
            }
            _sample = s;
            _camPos = camPos;
            _driven = driven && !_s1.Cam.OverrideActive;
            _dt = Time.deltaTime;
            FrameReader r = _s1.Cam.Reader;
            _extValid = r.SwimValid;
            _ext = r.Swim;
            _locoValid = r.LocoValid;
            _loco = r.Loco;
            _pelvisY = r.PelvisSnY;
            _tick = r.SwimSeen ? r.SwimTick : s.NewestTick;
            _camUnder = camPos.y < OceanLevel() + (_camUnder ? S5aRules.CamUnderExitM : S5aRules.CamUnderEnterM);
            _snShows = S5aRules.SnShows(_s2 != null && _s2.C5ViewValid, _s2 != null ? _s2.C5ViewState : ViewState.Space);
            _firstPerson = s.CameraMode == CameraMode.FirstPerson;
            if (_camUnder != _camUnderPrev)
            {
                _camUnderPrev = _camUnder;
                // a surface crossing with the character in the water (not seated in a hull): the dive window
                if (_extValid && _ext.Has(SwimWireFlags.InWater) && _firstPerson) { _splashUntil = _now + SplashWindowMs; SplashWindows++; }
            }

            if (!Disabled("wire")) Guard("wire", StepWire);
            if (!Disabled("arms")) Guard("arms", _stepArms);
            if (!Disabled("arms")) Guard("arms", CheckArms);
            if (!Disabled("o2")) Guard("o2", _stepO2);
            if (!Disabled("breath")) Guard("breath", _stepBreath);
        }

        private void PreCullFrame(CamSample s)
        {
            if (!_disposed) S13Sn.PreCullArms(_armsRenderers, Gate == ArmsGate.ForceOff, s.CameraMode == CameraMode.FirstPerson); // S13 hook: the drawn state of the view model (A1)
            if (_disposed || !_visible || Disabled("arms")) return;
            Guard("arms", _stepPose);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float OceanLevel() { return Ocean.GetOceanLevel(); }

        // ------------------------------------------------------------------------------------------ DE.SwimWire

        private void StepWire()
        {
            FrameReader r = _s1.Cam.Reader;
            if (!r.SwimSeen) return;
            byte v = r.SwimVersion;
            if (_wireLogged && v == _wireVersionLogged) return;
            _wireLogged = true;
            _wireVersionLogged = v;
            WireLines++;
            SwimWireState raw = r.Swim;
            raw.Version = v;
            DeLog.Info(S5aRules.SwimWireLine(_tick, ref raw));
            if (v != SwimWire.Version)
                DeLog.Warn("DE.S5a C1 swim extension version " + v + " (expected " + SwimWire.Version + "): arms, oxygen mirror and breath stay off" +
                           (v == 0 ? " (an SE build before S5a, or the SE controller never wrote it)" : ""));
        }

        // ------------------------------------------------------------------------------------------ arms

        private void StepArms()
        {
            Player p = Player.main;
            if (p == null) return;
            if (!_rigFound && !_rigFailed) FindRig(p);
            bool ghostReady = _s1.Ghost.Ready;
            // S7a: out of the water in a foot state (the swim latch off); the arms above water need Subnautica's picture (C5
 // seeThrough, policy Site), first person, a driven camera above the sea and fpbody=diver (Gate not ForceOff)
            bool inWaterExt = _extValid && _ext.Has(SwimWireFlags.InWater);
            _locoOut = _locoValid && !inWaterExt && !_loco.Has(S7aConventions.C1LocoFlags.InWater) && S7aAnimRules.FootState(_loco.State);
            _armsOut = _locoOut && Gate == ArmsGate.Full && ghostReady &&
                       S7aAnimRules.ArmsOutOfWater(_firstPerson, _driven, !_camUnder, S7aSnState.SeeThrough, S7aSnState.PictureSite, _loco.State, Gate != ArmsGate.ForceOff);
            // review F9: swimming at the surface in first person under Site (the camera above the sea, Subnautica's
 // picture with SE's scene skipped) shows the diver arms too; S2's rule needed the camera under water
            bool surfaceArms = _extValid && _ext.Has(SwimWireFlags.InWater) && !_camUnder && Gate == ArmsGate.Full && ghostReady &&
                               _firstPerson && _driven && S7aSnState.SeeThrough && S7aSnState.PictureSite;
            // the diver arms hide then
            bool toolHeld = _locoValid && _loco.Has(S7aConventions.C1LocoFlags.ToolHeld);
            if (toolHeld) ToolHeldFrames++;
            ToolHeldNow = toolHeld;
            // S13 hook (point 1, "Subnautica's own hands appear under water and blend with the SE character"): the 1.3 s dive window
 // no longer shows the diver's view model under fpbody=se (SE's astronaut does the dive through the retarget; the dive and
 // surface sounds are WaterAmbience's, played by S13Sn.SwimFrame); S13.M1 brings the window back
            bool splashWindow = DevMutants.IsOn(S13SnMutants.SplashArms) && _now < _splashUntil && _firstPerson && _driven && Gate == ArmsGate.ForceOff;
            bool visible = _rigFound && !toolHeld && (S5aRules.ArmsVisible(Gate, ghostReady, _extValid, _ext.Flags, _camUnder, _snShows, _firstPerson, _driven) || _armsOut || surfaceArms || splashWindow) && !_quitting;
            _hiddenWhy = visible ? null : (_rigFound ? S5aRules.HiddenReason(Gate, ghostReady, _extValid, _ext.Flags, _camUnder, _snShows, _firstPerson, _driven) ?? "quitting" : "view model not found");
            S7aSnState.ArmsOutWhy = _locoOut && !_armsOut ? S7aAnimRules.ArmsOutHiddenReason(_firstPerson, _driven, !_camUnder, S7aSnState.SeeThrough, S7aSnState.PictureSite, _loco.State, Gate != ArmsGate.ForceOff) : null;
            if (_armsOut) ArmsOutFrames++;
            S13Sn.ArmsFrame(visible, Gate == ArmsGate.ForceOff, _firstPerson); // S13 hook (point 1): the A1 leak counter
            S13Sn.SwimFrame(_extValid, _ext.Flags, _locoValid, _loco.VUpCm, _extValid ? SwimWire.FromFraction(_ext.O2) : 1.0, _firstPerson); // S13 hook (point 1): the dive and surface sounds (WaterAmbience)
            _state = S5aRules.StateName(_extValid, _ext.Flags, _ext.Mode);

            // drive the animator while the extension is valid (so the pose is right the frame the arms appear)
            SetDriving(_rigFound && _extValid && !_quitting);
            if (_driving)
            {
                // S13 hook (point 5): the swim animation from the look-frame velocity (C1 S13 extension, Subnautica's own rule)
                double tx, ty, tz;
                bool swimAnim = !_locoOut && _extValid && _ext.Has(SwimWireFlags.InWater);
                if (swimAnim && S13Sn.AnimTarget(_s1.Cam.Reader, out tx, out ty, out tz)) _motion.UpdateTarget(_dt, tx, ty, tz);
                else _motion.Update(_dt, ref _ext);
                S13Sn.AnimFrame(_s1.Cam.Reader, _motion.Vx, _motion.Vz, swimAnim);
                ApplyAnimator(p);
            }

            if (visible != _visible) ApplyVisible(visible, p);
            if (_visible) StepPose();
            if (MaskOn) ApplyMask(p, _visible && _ext.Has(SwimWireFlags.Underwater));
            else if (_maskActive) ApplyMask(p, false); // stage 3: `de.arms mask=off` while the mask is on must drop it

            if (_extValid && _armsLog.Due(_now, _state, _visible))
                DeLog.Info(S5aRules.ArmsLine(_tick, _state, _visible, ref _ext, _visible || _driving ? CurrentClip() : null));
            AnimRow();
        }

        /// <summary>S7a: the animator snapshot for S7aHost and the s7a.anim row (every change of state/source/arms/clip, 2 Hz, de.s7 anim).</summary>
        private void AnimRow()
        {
            if (!_locoValid && !S7aSnState.AnimRowRequested) return;
            string clip = _driving ? CurrentClip() : null;
            S7aSnState.AnimSeTick = _tick;
            S7aSnState.AnimLocoValid = _locoValid;
            S7aSnState.AnimLocoState = _loco.State;
            S7aSnState.AnimInWater = _extValid && _ext.Has(SwimWireFlags.InWater);
            S7aSnState.AnimOut = _locoOut;
            S7aSnState.AnimArmsShown = _visible;
            S7aSnState.AnimDriving = _driving;
            S7aSnState.AnimLast = _outParams;
            S7aSnState.AnimClip = clip;
            string key = _loco.State + "|" + (_locoOut ? 1 : 0) + "|" + (_visible ? 1 : 0) + "|" + S7aSnState.CharSource + "|" + (clip ?? "-");
            bool asked = S7aSnState.AnimRowRequested;
            if (!_animRow.Due(key, _now) && !asked) return;
            S7aSnState.AnimRowRequested = false;
            AnimParams o = _outParams;
            JsonOut j = S5Rows.Begin("s7a.anim");
            j.Num("snFrame", (long)Time.frameCount).Num("seTick", _tick).Str("source", S7aSnState.CharSource)
             .Str("locoState", S7aConventions.C1LocoState.Name(_loco.State)).Bool("locoValid", _locoValid).Bool("inWater", S7aSnState.AnimInWater)
             .Bool("outOfWater", _locoOut).Bool("driving", _driving);
            j.Obj("params").Bool("is_underwater", _locoOut ? o.IsUnderwater : _extValid && _ext.Has(SwimWireFlags.InWater) && _ext.Has(SwimWireFlags.Underwater))
             .Bool("on_surface", _locoOut ? o.OnSurface : _extValid && _ext.Has(SwimWireFlags.InWater) && _ext.Has(SwimWireFlags.AtSurface))
             .Num("move_speed", _locoOut ? (double)o.MoveSpeed : _motion.Speed).Num("move_speed_x", _locoOut ? (double)o.X : _motion.Vx)
             .Num("move_speed_y", _locoOut ? (double)o.Y : _motion.Vy).Num("move_speed_z", _locoOut ? (double)o.Z : _motion.Vz)
             .Bool("jump", _locoOut && o.Jump).Bool("diving", _locoOut && o.Diving).EndObj();
            j.Str("clips", clip ?? "").Bool("armsShown", _visible).Bool("armsOut", _armsOut).Str("armsOutWhy", S7aSnState.ArmsOutWhy ?? "")
             .Str("fpbody", Gate == ArmsGate.ForceOff ? "se" : "diver").Str("walkSource", S7aAnimRules.WalkSourceName(S7aSnSwitches.WalkRetarget))
             .Num("vF", (long)_loco.VForwardCm).Num("vS", (long)_loco.VStrafeCm).Num("vU", (long)_loco.VUpCm).Num("pelvisY", (double)_pelvisY)
             .Bool("seeThrough", S7aSnState.SeeThrough).Bool("pictureSite", S7aSnState.PictureSite).Bool("firstPerson", _firstPerson)
             .Bool("cameraUnder", _camUnder).Num("footFrames", S7aSnState.CharFootFrames).Num("footRetarget", S7aSnState.CharFootRetarget);
            S5Rows.End(j);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void FindRig(Player p)
        {
            MainCameraControl mcc = MainCameraControl.main;
            Transform vm = mcc != null ? mcc.viewModel : null;
            if (vm == null && p.armsController != null) vm = p.armsController.transform;
            if (vm == null)
            {
                _rigFailed = true;
                DeLog.Warn("DE.S5a FEATURE arms DISABLED: no view model (MainCameraControl.main.viewModel and Player.armsController are null)");
                return;
            }
            _viewModel = vm;
            _arms = p.armsController != null ? p.armsController : vm.GetComponentInChildren<ArmsController>(true);
            _animator = _arms != null ? _arms.GetComponent<Animator>() : null;
            if (_animator == null) _animator = p.playerAnimator;
            if (_animator == null) _animator = vm.GetComponentInChildren<Animator>(true);
            _params = new HashSet<string>(StringComparer.Ordinal);
            int paramCount = 0;
            if (_animator != null)
            {
                // the arms' renderers are disabled most of the time: a culled Animator would freeze (and show a stale or
 // bind pose on the first visible frame), so it keeps evaluating; the original mode is restored on release
                _cullingWas = _animator.cullingMode;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                AnimatorControllerParameter[] ps = _animator.parameters;
                if (ps != null)
                {
                    paramCount = ps.Length;
                    for (int i = 0; i < ps.Length; i++) if (ps[i] != null && ps[i].name != null) _params.Add(ps[i].name);
                }
            }
            Renderer[] rs = vm.GetComponentsInChildren<Renderer>(true);
            var list = new List<Renderer>(rs.Length);
            for (int i = 0; i < rs.Length; i++) if (rs[i] != null) list.Add(rs[i]);
            _armsRenderers = list.ToArray();
            // the camera rig is unparented from the Player by the camera driver, so the components that live under it are not
 // the Player's children any more: scene-wide fallback
            _bubbles = p.GetComponentInChildren<PlayerBreathBubbles>(true) ?? FindAnywhere<PlayerBreathBubbles>();
            _breathing = p.GetComponentInChildren<BreathingSound>(true) ?? FindAnywhere<BreathingSound>();
            DeLog.Info("DE.S5a breathing: BreathingSound " + (_breathing != null ? "found on '" + _breathing.gameObject.name + "' emitter=" + (_breathing.loopingBreathingSound != null ? "ok" : "MISSING") : "MISSING") +
                       "; PlayerBreathBubbles " + (_bubbles != null ? "found on '" + _bubbles.gameObject.name + "'" : "MISSING (retried every 2 s)"));
            if (_bubbles != null)
            {
                _bubbleDelayS = _bubbles.delay > 0.05f ? _bubbles.delay : S5aRules.DefaultBubbleDelayS;
                try { _makeBubbles = typeof(PlayerBreathBubbles).GetMethod("MakeBubbles", BindingFlags.Instance | BindingFlags.NonPublic); } catch (Exception) { _makeBubbles = null; }
            }
            _alert = p.GetComponentInChildren<LowOxygenAlert>(true);
            if (_alert == null) _alert = UnityEngine.Object.FindObjectOfType<LowOxygenAlert>();
            ReadAlerts(p);
            _rigFound = true;
            var sb = new StringBuilder(512);
            sb.Append("DE.S5a view model '").Append(vm.name).Append("' parent='").Append(vm.parent != null ? vm.parent.name : "(none)")
              .Append("' localPos=").Append(CameraDriver.V(vm.localPosition)).Append(" localEuler=").Append(CameraDriver.V(vm.localEulerAngles))
              .Append(" renderers=").Append(_armsRenderers.Length);
            for (int i = 0; i < _armsRenderers.Length && i < 6; i++) sb.Append(i == 0 ? " [" : ",").Append(_armsRenderers[i].name);
            if (_armsRenderers.Length > 0) sb.Append(_armsRenderers.Length > 6 ? ",…]" : "]");
            sb.Append(" armsController=").Append(_arms != null ? "found" : "MISSING").Append(" animator=").Append(_animator != null ? "found(" + paramCount + " params)" : "MISSING")
              .Append(" rig=").Append(_s1.Cam.CameraUnderRig ? "MainCameraControl transform (camera under it)" : "camera transform (camera not under the rig)")
              .Append(" bubbles=").Append(_bubbles != null ? "found delay=" + F(_bubbleDelayS) + "s MakeBubbles=" + (_makeBubbles != null ? "ok" : "MISSING") : "MISSING")
              .Append(" lowOxygenAlert=").Append(_alertsDescribe);
            DeLog.Info(sb.ToString());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReadAlerts(Player p)
        {
            var thresholds = new List<float>();
            var sb = new StringBuilder(128);
            double capacity = p.oxygenMgr != null ? p.oxygenMgr.GetOxygenCapacity() : 0.0;
            if (_alert != null && _alert.alertList != null)
            {
                sb.Append(_alert.alertList.Count).Append(" alerts");
                for (int i = 0; i < _alert.alertList.Count; i++)
                {
                    LowOxygenAlert.Alert a = _alert.alertList[i];
                    if (a == null) continue;
                    thresholds.Add(a.oxygenTriggerSeconds);
                    sb.Append(i == 0 ? " [" : ",").Append("trigger=").Append(a.oxygenTriggerSeconds).Append("s minCap=").Append(F(a.minO2Capacity))
                      .Append(" minDepth=").Append(F(a.minDepth)).Append(" sfx=").Append(a.soundSFX != null ? "yes" : "no")
                      .Append(" eligible=").Append(capacity >= a.minO2Capacity ? "yes" : "no(capacity " + F(capacity) + ")");
                }
                if (_alert.alertList.Count > 0) sb.Append(']');
            }
            else sb.Append(_alert == null ? "component MISSING" : "alertList null");
            if (thresholds.Count == 0)
            {
                thresholds.Add(10f);
                sb.Append(" -> stand-in threshold 10 s (HYPOTHESIS: the prefab's list was not readable)");
            }
            _lowO2 = new LowO2Monitor(thresholds.ToArray());
            _alertsDescribe = sb.ToString();
        }

        private void SetDriving(bool on)
        {
            if (on == _driving) return;
            _driving = on;
            if (_arms != null)
            {
                if (on)
                {
                    _armsControllerWasEnabled = _arms.enabled;
                    if (_arms.enabled) _arms.enabled = false;
                }
                else if (_armsControllerWasEnabled && !_arms.enabled) _arms.enabled = true;
            }
            if (!on)
            {
                _motion.Reset();
                _hasPrevYaw = false;
                if (_animator != null && _animator.cullingMode != _cullingWas) _animator.cullingMode = _cullingWas;
            }
            else if (_animator != null && _animator.cullingMode != AnimatorCullingMode.AlwaysAnimate) _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            DeLog.Info("DE.S5a arms " + (on ? "DRIVEN by the C1 swim extension (ArmsController disabled; animator parameters set from C1; Animator AlwaysAnimate; no Subnautica motor or input)" :
                                          "released (ArmsController " + (_arms != null && _arms.enabled ? "enabled again" : "left as found") + ")"));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplyAnimator(Player p)
        {
            Animator a = _animator;
            if (a == null) return;
            ParamFrames++;
            bool inWater = _ext.Has(SwimWireFlags.InWater);
            bool under = inWater && _ext.Has(SwimWireFlags.Underwater);
            bool surface = inWater && _ext.Has(SwimWireFlags.AtSurface);
            float speed = (float)_motion.Speed, vx = (float)_motion.Vx, vy = (float)_motion.Vy, vz = (float)_motion.Vz;
            bool jump = false, diving = false;
            if (_locoOut)
            {
                // S7a: out of the water in a foot state: Subnautica's above-water parameters from the C1 locomotion extension
                double air = _air.Update(_loco.State, _now / 1000.0);
                _outParams = S7aAnimRules.OutOfWater(ref _loco, _pelvisY, air, S7aAnimRules.GroundScale);
                under = _outParams.IsUnderwater;
                surface = _outParams.OnSurface;
                speed = _outParams.MoveSpeed; vx = _outParams.X; vy = _outParams.Y; vz = _outParams.Z;
                jump = _outParams.Jump;
                diving = _outParams.Diving;
                OutOfWaterFrames++;
            }
            else
            {
                _air.Reset();
                _outParams = default(AnimParams);
            }
            SetBool(a, "is_underwater", under);
            SetBool(a, "on_surface", surface);
            if (_params.Contains("move_speed")) a.SetFloat(AnimatorHashID.move_speed, speed);
            if (_params.Contains("move_speed_x")) a.SetFloat(AnimatorHashID.move_speed_x, vx);
            if (_params.Contains("move_speed_y")) a.SetFloat(AnimatorHashID.move_speed_y, vy);
            if (_params.Contains("move_speed_z")) a.SetFloat(AnimatorHashID.move_speed_z, vz);
            // view_pitch: MainCameraControl.GetCameraPitch = -(local x angles), i.e. positive looking UP
            Quaternion q = new Quaternion(_sample.Qx, _sample.Qy, _sample.Qz, _sample.Qw);
            float pitchUp = (float)CamMath.PitchDeg(q.x, q.y, q.z, q.w);
            SetFloat(a, "view_pitch", pitchUp);
            float yaw = (float)CamMath.YawDeg(q.x, q.y, q.z, q.w);
            if (_hasPrevYaw && _dt > 0f && _params.Contains("view_turn"))
            {
                float rate = Mathf.DeltaAngle(_prevYaw, yaw) / _dt;
                float damp = _arms != null ? _arms.turnAnimationDampTime : 0f;
                a.SetFloat("view_turn", rate, damp, _dt);
            }
            _prevYaw = yaw;
            _hasPrevYaw = true;
            SetBool(a, "jump", jump);
            SetBool(a, "diving", diving);
            SetBool(a, "diving_land", false);
            SetBool(a, "holding_tool", false);
            SetBool(a, "using_tool", false);
            SetBool(a, "using_tool_alt", false);
            SetBool(a, "in_seamoth", false);
            SetBool(a, "in_exosuit", false);
            SetBool(a, "cyclops_steering", false);
            SetBool(a, "bleeder", false);
            SetBool(a, "grab", false);
            SetBool(a, "bash", false);
            SetBool(a, "using_pda", false);
            SetBool(a, "using_mechsuit", false);
            SetBool(a, "cinematics_enabled", !GameOptions.GetVrAnimationMode());
            SetFloat(a, "verticalOffset", 0f);
        }

        private void SetBool(Animator a, string name, bool v) { if (_params.Contains(name)) a.SetBool(name, v); }
        private void SetFloat(Animator a, string name, float v) { if (_params.Contains(name)) a.SetFloat(name, v); }

        // ------------------------------------------------------------------------------------------ arms check

 // then after a few seconds it disappears" while the plugin's own flag stayed visible=1 and no HIDDEN line was written.
 // So something outside ApplyVisible changes the drawn result. Every frame while visible: count the renderers that are
 // enabled / active in the hierarchy / drawn by a camera last frame (Renderer.isVisible), re-enable a disabled one,
 // and log a CHECK line whenever any of these change, the view model drifts from the camera, or every 5 s.
        private int _ckEnabled = -1, _ckActive = -1, _ckDrawn = -1;
        private float _ckDist = -1f;
        private ulong _ckLastMs;
        public long ArmsReEnabled, ArmsChecks;

        private void CheckArms()
        {
            if (!_visible || _armsRenderers == null || _armsRenderers.Length == 0) { _ckEnabled = _ckActive = _ckDrawn = -1; return; }
            int enabled = 0, active = 0, drawn = 0, total = 0, reEnabled = 0;
            string offLayer = null;
            for (int i = 0; i < _armsRenderers.Length; i++)
            {
                Renderer r = _armsRenderers[i];
                if (r == null) continue;
                total++;
                if (!r.enabled) { r.enabled = true; reEnabled++; ArmsReEnabled++; } else enabled++;
                if (r.gameObject.activeInHierarchy) active++;
                if (r.isVisible) drawn++;
                if (offLayer == null && r.gameObject.layer != 0) offLayer = r.gameObject.name + "@layer" + r.gameObject.layer;
            }
            Vector3 vm = _viewModel != null ? _viewModel.position : _camPos;
            float dist = Vector3.Distance(vm, _camPos);
            Player p = Player.main;
            Vector3 pp = p != null ? p.transform.position : Vector3.zero;
            bool changed = enabled != _ckEnabled || active != _ckActive || (drawn == 0) != (_ckDrawn == 0) || Math.Abs(dist - _ckDist) > 1f || reEnabled > 0;
            if (changed || _now - _ckLastMs > 5000)
            {
                ArmsChecks++;
                _ckLastMs = _now;
                DeLog.Info("DE.S5a arms CHECK tick=" + _tick + " enabled=" + enabled + "/" + total + " active=" + active + " drawn=" + drawn + " reEnabled=" + reEnabled +
                           " vmActive=" + (_viewModel != null && _viewModel.gameObject.activeInHierarchy ? 1 : 0) + " vmParent=" + (_viewModel != null && _viewModel.parent != null ? _viewModel.parent.name : "-") +
                           " dist=" + F(dist) + " vm=(" + F(vm.x) + "," + F(vm.y) + "," + F(vm.z) + ") cam=(" + F(_camPos.x) + "," + F(_camPos.y) + "," + F(_camPos.z) + ")" +
                           " player=(" + F(pp.x) + "," + F(pp.y) + "," + F(pp.z) + ") mask=" + (_maskActive ? 1 : 0) + (offLayer != null ? " layer=" + offLayer : "") +
                           " camMask=0x" + (MainCamera.camera != null ? MainCamera.camera.cullingMask.ToString("x") : "-") + (changed ? " CHANGED" : ""));
            }
            _ckEnabled = enabled; _ckActive = active; _ckDrawn = drawn; _ckDist = dist;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplyVisible(bool visible, Player p)
        {
            _visible = visible;
            if (visible)
            {
                _exempt.Clear();
                for (int i = 0; i < _armsRenderers.Length; i++) if (_armsRenderers[i] != null) _exempt.Add(_armsRenderers[i]);
                _s1.Ghost.Exempt = _exempt;
                for (int i = 0; i < _armsRenderers.Length; i++)
                {
                    Renderer r = _armsRenderers[i];
                    if (r != null && !r.enabled) r.enabled = true;
                }
                ArmsShown++;
                StepPose();
                DeLog.Info("DE.S5a arms SHOWN (#" + ArmsShown + ") tick=" + _tick + " state=" + _state + " renderers=" + _armsRenderers.Length +
                           " camY=" + F(_camPos.y) + " ocean=" + F(OceanLevel()) + " gate=" + S5aRules.GateName(Gate));
            }
            else
            {
                _s1.Ghost.Exempt = null;
                for (int i = 0; i < _armsRenderers.Length; i++)
                {
                    Renderer r = _armsRenderers[i];
                    if (r != null && r.enabled) r.enabled = false;
                }
                ArmsHidden++;
                DeLog.Info("DE.S5a arms HIDDEN (#" + ArmsHidden + ") tick=" + _tick + " state=" + _state + " why=" + (_hiddenWhy ?? "?"));
            }
        }

        /// <summary>
 /// The view model follows the camera rig: the rig's position
 /// and its yaw only (the arms' pitch is the animator's). Dev offset
 /// <see cref="ViewModelOffset"/> in the yaw frame (<c>de.arms offset</c>) for the real-run fit.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void StepPose()
        {
            if (_viewModel == null) return;
            Transform src = _s1.Cam.CameraUnderRig && _s1.Cam.Rig != null ? _s1.Cam.Rig : (_s1.Cam.Camera != null ? _s1.Cam.Camera.transform : null);
            if (src == null) return;
            Vector3 f = src.rotation * Vector3.forward;
            f.y = 0f;
            Quaternion yaw = f.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
            _viewModel.SetPositionAndRotation(src.position + yaw * ViewModelOffset, yaw);
            Poses++;
            if (++_poses == 1)
                DeLog.Info("DE.S5a view model posed at the rig: " + CameraDriver.V(src.position) + " yaw " + CameraDriver.Q(yaw) + " (camera " + CameraDriver.V(_camPos) + ")");
        }

        /// <summary>Arms hidden, mask off, ArmsController and the animator's culling mode restored (one place for every exit path).</summary>
        private void ReleaseArms(string why)
        {
            Player p = Player.main;
            if (_visible) ApplyVisible(false, p);
            if (_maskActive && p != null) ApplyMask(p, false);
            if (_driving) SetDriving(false);
            _hiddenWhy = why;
            StopBreathingLoop(why);
        }

        /// <summary>Scene objects of a type, active or not (never prefabs or assets).</summary>
        private static T FindAnywhere<T>() where T : UnityEngine.Component
        {
            T[] all = Resources.FindObjectsOfTypeAll<T>();
            T best = null;
            for (int i = 0; i < all.Length; i++)
            {
                T c = all[i];
                if (c == null || c.gameObject == null || !c.gameObject.scene.IsValid()) continue;
                if (best == null || c.gameObject.activeInHierarchy) best = c;
            }
            return best;
        }

        private void FindBreathComponents(Player p)
        {
            if (_bubbles == null)
            {
                _bubbles = (p != null ? p.GetComponentInChildren<PlayerBreathBubbles>(true) : null) ?? FindAnywhere<PlayerBreathBubbles>();
                if (_bubbles != null)
                {
                    _bubbleDelayS = _bubbles.delay > 0.05f ? _bubbles.delay : S5aRules.DefaultBubbleDelayS;
                    try { _makeBubbles = typeof(PlayerBreathBubbles).GetMethod("MakeBubbles", BindingFlags.Instance | BindingFlags.NonPublic); } catch (Exception) { _makeBubbles = null; }
                    DeLog.Info("DE.S5a breathing: PlayerBreathBubbles found late on '" + _bubbles.gameObject.name + "' delay=" + F(_bubbleDelayS) + "s MakeBubbles=" + (_makeBubbles != null ? "ok" : "MISSING"));
                }
            }
            if (_breathing == null)
            {
                _breathing = (p != null ? p.GetComponentInChildren<BreathingSound>(true) : null) ?? FindAnywhere<BreathingSound>();
                if (_breathing != null) DeLog.Info("DE.S5a breathing: BreathingSound found late on '" + _breathing.gameObject.name + "'");
            }
        }

        /// <summary>The game's looping breath event, driven from the C1 state (under = in the water with the eye under).</summary>
        private void StepBreathingLoop(bool under, double depth)
        {
            if (_breathing == null || _breathing.loopingBreathingSound == null) return;
            FMOD_StudioEventEmitter em = _breathing.loopingBreathingSound;
            if (under && !_quitting)
            {
                if (!_breathingTaken)
                {
                    _breathing.CancelInvoke("UpdateSound");
                    _breathingTaken = true;
                    DeLog.Info("DE.S5a breathing loop taken from BreathingSound.UpdateSound (its gates: mode Normal, a tank, Player.isUnderwater; the ghost has none)");
                }
                if (!em.GetIsStartingOrPlaying())
                {
                    em.StartEvent();
                    _loopOn = true;
                    LoopStarts++;
                    DeLog.Info("DE.S5a breathing loop ON tick=" + _tick + " depth=" + F(depth) + " (#" + LoopStarts + ")");
                }
                em.SetParameterValue("depth", _camPos.y);
                em.SetParameterValue("rebreather", 0f);
            }
            else if (_loopOn || em.GetIsPlaying())
            {
                StopBreathingLoop(under ? "quitting" : "out of the water");
            }
        }

        private void StopBreathingLoop(string why)
        {
            if (_breathing == null || _breathing.loopingBreathingSound == null) { _loopOn = false; return; }
            FMOD_StudioEventEmitter em = _breathing.loopingBreathingSound;
            bool was = _loopOn || em.GetIsPlaying();
            if (was) { em.Stop(true); LoopStops++; DeLog.Info("DE.S5a breathing loop OFF tick=" + _tick + " (" + why + "; #" + LoopStops + ")"); }
            _loopOn = false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplyMask(Player p, bool on)
        {
            if (on == _maskActive) return;
            _maskActive = on;
            if (p == null) return;
            p.SetScubaMaskActive(on);
            if (on && p.scubaMaskModelSpawn != null && p.scubaMaskModelSpawn.spawnedObj != null && _s1.Ghost.Exempt != null)
            {
                Renderer[] rs = p.scubaMaskModelSpawn.spawnedObj.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] == null) continue;
                    _exempt.Add(rs[i]);
                    if (!rs[i].enabled) rs[i].enabled = true;
                }
            }
            DeLog.Info("DE.S5a scuba mask " + (on ? "on" : "off") + " (Player.SetScubaMaskActive; the view-model overlay Subnautica shows under water)");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string CurrentClip()
        {
            Animator a = _animator;
            if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return null;
            try
            {
                AnimatorClipInfo[] info = a.GetCurrentAnimatorClipInfo(0);
                if (info == null || info.Length == 0 || info[0].clip == null) return null;
                return info[0].clip.name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------------------------------ oxygen

        private void StepO2()
        {
            Player p = Player.main;
            if (p == null) return;
            OxygenManager m = p.oxygenMgr;
            if (m == null) return;
            _shielded = p.liveMixin != null && p.liveMixin.shielded;
            if (!_extValid || !MirrorOn || _quitting)
            {
                if (_managerDisabledByUs) { m.enabled = true; _managerDisabledByUs = false; DeLog.Info("DE.S5a OxygenManager.Update enabled again (" + (!_extValid ? "no extension" : MirrorOn ? "quitting" : "de.o2 mirror=off") + ")"); }
                return;
            }
            float capacity = m.GetOxygenCapacity();
            _capacity = capacity;
            _mirrored = SwimWire.FromFraction(_ext.O2);
            _o2Override = _ext.Has(SwimWireFlags.O2Override);
            if (capacity > 0f)
            {
                if (m.enabled)
                {
                    m.enabled = false;
                    _managerDisabledByUs = true;
                    DeLog.Info("DE.S5a OxygenManager.Update disabled while mirroring (its 30 units/s surface refill would fight the mirror; Player's own breaths are off by FreezeStats) capacity=" + F(capacity));
                }
                double target = O2Mirror.TargetUnits(_ext.O2, capacity, O2FloorUnits);
                double cur = m.GetOxygenAvailable();
                double d = O2Mirror.Delta(cur, target);
                if (d > 0.0) { m.AddOxygen((float)d); O2Writes++; }
                else if (d < 0.0) { m.RemoveOxygen((float)-d); O2Writes++; }
                _snOxygen = m.GetOxygenAvailable();
            }
            else _snOxygen = 0.0;
            if (_o2Log.Due(_now)) DeLog.Info(S5aRules.O2Line(_tick, _mirrored, _snOxygen, _capacity, _o2Override, _shielded));
        }

        // ------------------------------------------------------------------------------------------ breath

        private void StepBreath()
        {
            Player p = Player.main;
            if (p == null || !_extValid || _quitting) { if (_quitting || !_extValid) _breath.Reset(); return; }
            bool under = _ext.Has(SwimWireFlags.InWater) && _ext.Has(SwimWireFlags.Underwater);
            double depth = Math.Max(0.0, OceanLevel() - _camPos.y);
            if ((_bubbles == null || _breathing == null) && _now - _lastSearchMs > 2000) { _lastSearchMs = _now; FindBreathComponents(p); }
            StepBreathingLoop(under, depth);
            bool breath, bubbles;
            _breath.Update(_now, under, _ext.DepthClass, _bubbleDelayS, out breath, out bubbles);
            if (breath)
            {
                Breaths++;
                TriggerBreath(p);
                DeLog.Info(S5aRules.BreathLine(_tick, "breath", _mirrored, depth));
            }
            if (bubbles)
            {
                Bursts++;
                MakeBubbles();
                DeLog.Info(S5aRules.BreathLine(_tick, "bubbles", _mirrored, depth));
            }
            if (MirrorOn && _capacity > 0.0)
            {
                float low;
                bool refill;
                _lowO2.Update((float)_snOxygen, out low, out refill);
                if (!float.IsNaN(low)) { LowO2Events++; DeLog.Info(S5aRules.BreathLine(_tick, "lowO2", _mirrored, depth) + " threshold=" + F(low)); }
                if (refill) { Refills++; DeLog.Info(S5aRules.BreathLine(_tick, "refill", _mirrored, depth)); }
            }
            bool suff = p.suffocationState == Player.SuffocationState.Suffocating;
            if (suff && !_wasSuffocating) { Chokes++; DeLog.Info(S5aRules.BreathLine(_tick, "choke", _mirrored, depth) + " suffocationTime=" + F(p.suffocationTime)); }
            _wasSuffocating = suff;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void TriggerBreath(Player p)
        {
            if (p.tookBreathEvent != null) p.tookBreathEvent.Trigger(p);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void MakeBubbles()
        {
            if (_bubbles == null || _makeBubbles == null || !_bubbles.enabled) return;
            _makeBubbles.Invoke(_bubbles, null);
        }

        /// <summary>S1Patches' FMOD_CustomEmitter.Play postfix: the game's own low-O2 alert and bubble emitters, for the record.</summary>
        private void OnEmitterPlayed(FMOD_CustomEmitter e)
        {
            try
            {
                if (e == null) return;
                if (_bubbles != null && e == _bubbles.bubbleSound) { GameBubblePlays++; return; }
                if (_alert != null && _alert.alertList != null)
                {
                    for (int i = 0; i < _alert.alertList.Count; i++)
                    {
                        LowOxygenAlert.Alert a = _alert.alertList[i];
                        if (a != null && a.soundSFX == e)
                        {
                            GameAlertPlays++;
                            DeLog.Info("DE.S5a LowOxygenAlert sound played by the game (trigger=" + a.oxygenTriggerSeconds + "s) snOxygen=" + F(_snOxygen) + " tick=" + _tick +
                                       " (the alert fires from the mirrored OxygenManager; its subtitle is suppressed by S1Patches)");
                            return;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // a patch hook never throws into the game
            }
        }

        // ------------------------------------------------------------------------------------------ PDA

        private void StepPda()
        {
            if (_pdaLog.Due(_now)) WritePdaLine();
        }

        private static long PdaSuppressedTotal() { return S1Patches.PdaVoice + S1Patches.VoiceNotifications + S1Patches.PdaNotifications; }

        private void WritePdaLine()
        {
            long sup = PdaSuppressedTotal(), passed = S1Patches.PdaPassed;
            DeLog.Info(S5aRules.PdaLine(_tick, sup - _pdaSuppressedBase, passed - _pdaPassedBase));
            _pdaSuppressedBase = sup;
            _pdaPassedBase = passed;
        }

        // ------------------------------------------------------------------------------------------ bridge events

 /// <summary>A new SE generation: the extension is unknown again, the arms hide, the cadences restart.</summary>
        public void OnNewSePeer()
        {
            _wireLogged = false;
            _armsLog.Reset();
            _o2Log.Reset();
            _breath.Reset();
            _lowO2.Reset();
            _extValid = false;
            _ext = default(SwimWireState);
            _locoValid = false;
            _locoOut = false;
            _loco = default(LocoWireState);
            _air.Reset();
            _animRow.Reset();
            Guard("arms", () => ReleaseArms("new SE generation"));
        }

        /// <summary>The bridge is quitting: arms hidden, the mirror released, the final DE.PDA line.</summary>
        public void OnQuitting(string why)
        {
            if (_quitting) return;
            _quitting = true;
            Player p = Player.main;
            if (p != null)
            {
                Guard("arms", () => ReleaseArms("quitting"));
                Guard("breath", () => StopBreathingLoop("quitting"));
                Guard("o2", () => { if (_managerDisabledByUs && p.oxygenMgr != null) { p.oxygenMgr.enabled = true; _managerDisabledByUs = false; } });
            }
            if (!_pdaFinalWritten && _s1.InWorld) { _pdaFinalWritten = true; Guard("pda", WritePdaLine); }
            DeLog.Info("DE.S5a quitting (" + why + "): arms hidden, OxygenManager released, deathsSkipped=" + S5aPatches.DeathsSkipped);
        }

        /// <summary>The bridge is going away: hooks dropped, Subnautica left as the ghost rule had it.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_s1 != null) { _s1.AfterGhostFrame = null; _s1.AfterPreCullFrame = null; } } catch (Exception) { }
            try { S1Patches.EmitterPlayed = null; } catch (Exception) { }
            try { if (!_quitting) OnQuitting("bridge disposal"); } catch (Exception) { }
        }

        // ------------------------------------------------------------------------------------------ C11

 /// <summary>The S5a commands; false when the command is not one of them.</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            object args = S1Args.Unwrap(req.Args);
            switch (req.Cmd)
            {
                case "de.s5a":
                    Status(reply.Bool("ok", true).Str("cmd", req.Cmd), now);
                    return true;

                case "de.arms":
                {
                    string error = null;
                    object v = S1Args.Get(args, "show", -1) ?? S1Args.Get(args, "gate", -1);
                    var list = args as List<object>;
                    if (v == null && list != null && list.Count >= 2 && ((list[0] as string) == "show" || (list[0] as string) == "gate")) v = list[1];
                    if (v != null)
                    {
                        ArmsGate g;
                        if (S5aRules.TryParseGate(v as string, out g)) { Gate = g; DeLog.Warn("DE.S5a arms gate=" + S5aRules.GateName(g) + " (de.arms)"); }
                        else error = "show/gate must be full|camera|on|off";
                    }
                    object mv = S1Args.Get(args, "mask", -1);
                    if (mv == null && list != null && list.Count >= 2 && (list[0] as string) == "mask") mv = list[1];
                    if (mv != null)
                    {
                        bool b;
                        if (S1Args.TryBool(mv, out b)) MaskOn = b; else error = "mask must be on|off";
                    }
                    object ov = S1Args.Get(args, "offset", -1);
                    var ol = ov as List<object>;
                    if (ol == null && list != null && list.Count >= 4 && (list[0] as string) == "offset") ol = list.GetRange(1, 3);
                    if (ol != null)
                    {
                        double x, y, z;
                        if (ol.Count == 3 && S1Args.TryDouble(ol[0], out x) && S1Args.TryDouble(ol[1], out y) && S1Args.TryDouble(ol[2], out z))
                            ViewModelOffset = new Vector3((float)x, (float)y, (float)z);
                        else error = "offset must be [x, y, z] metres in the yaw frame";
                    }
                    if (error != null) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error + "; args {show|gate: full|camera|on|off, mask: on|off, offset: [x,y,z]}"); return true; }
                    Status(reply.Bool("ok", true).Str("cmd", req.Cmd), now);
                    return true;
                }

                case "de.o2":
                {
                    string error = null;
                    var list = args as List<object>;
                    object mv = S1Args.Get(args, "mirror", -1);
                    if (mv == null && list != null && list.Count >= 2 && (list[0] as string) == "mirror") mv = list[1];
                    if (mv != null)
                    {
                        bool b;
                        if (S1Args.TryBool(mv, out b)) { MirrorOn = b; DeLog.Warn("DE.S5a o2 mirror=" + (b ? "on" : "off") + " (de.o2)"); } else error = "mirror must be on|off";
                    }
                    object fv = S1Args.Get(args, "floor", -1);
                    if (fv == null && list != null && list.Count >= 2 && (list[0] as string) == "floor") fv = list[1];
                    if (fv != null)
                    {
                        double d;
                        if (S1Args.TryDouble(fv, out d) && d >= 0 && d <= 45) { O2FloorUnits = d; DeLog.Warn("DE.S5a o2 floor=" + F(d) + " units (de.o2)"); } else error = "floor must be 0..45 units";
                    }
                    if (error != null) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error + "; args {mirror: on|off, floor: <units>}"); return true; }
                    Status(reply.Bool("ok", true).Str("cmd", req.Cmd), now);
                    return true;
                }
            }
            return false;
        }

        private void Status(JsonObjectWriter w, ulong now)
        {
            FrameReader r = _s1.Cam.Reader;
            SwimWireState s = r.Swim;
            w.Bool("started", _started).Bool("inWorld", _s1.InWorld).Bool("ghostReady", _s1.Ghost.Ready).Bool("quitting", _quitting)
             .Bool("extValid", r.SwimValid).Num("extVersion", (long)r.SwimVersion).Num("extTick", r.SwimTick).Num("extReads", r.SwimReads)
             .Str("ext", r.SwimSeen ? SwimWire.Describe(ref s) : "none").Str("extHash", DeLog.Hex(SwimWire.Hash))
             .Str("armsState", _state).Bool("armsVisible", _visible).Bool("armsDriving", _driving).Str("armsGate", S5aRules.GateName(Gate))
             .Str("armsHiddenWhy", _hiddenWhy ?? "").Bool("viewModel", _viewModel != null).Num("armsRenderers", (long)_armsRenderers.Length)
             .Bool("animator", _animator != null).Bool("armsController", _arms != null).Bool("armsControllerEnabled", _arms != null && _arms.enabled)
             .Str("anim", S5aRules.AnimToken(CurrentClipSafe())).Bool("mask", MaskOn).Bool("maskActive", _maskActive)
             .Str("viewModelOffset", CameraDriver.V(ViewModelOffset)).Num("armsShown", ArmsShown).Num("armsHidden", ArmsHidden).Num("poses", Poses)
             .Bool("cameraUnder", _camUnder).Bool("snShows", _snShows).Bool("firstPerson", _firstPerson).Bool("driven", _driven)
             .Num("motionVx", _motion.Vx).Num("motionVy", _motion.Vy).Num("motionVz", _motion.Vz)
             .Bool("o2Mirror", MirrorOn).Num("o2Mirrored", _mirrored).Num("snOxygen", _snOxygen).Num("capacity", _capacity).Bool("o2Override", _o2Override)
             .Bool("shielded", _shielded).Num("o2Floor", O2FloorUnits).Bool("oxygenManagerDisabled", _managerDisabledByUs).Num("o2Writes", O2Writes)
             .Bool("suffocationDiePatched", S5aPatches.SuffocationDiePatched).Num("deathsSkipped", S5aPatches.DeathsSkipped)
             .Num("breaths", Breaths).Num("bubbleBursts", Bursts).Num("bubbleDelayS", _bubbleDelayS).Num("lowO2Events", LowO2Events).Num("refills", Refills).Num("chokes", Chokes)
             .Num("gameAlertPlays", GameAlertPlays).Num("gameBubblePlays", GameBubblePlays).Str("alerts", _alertsDescribe)
             .Num("pdaSuppressedTotal", PdaSuppressedTotal()).Num("pdaPassedTotal", S1Patches.PdaPassed).Bool("suppressUi", S1Patches.SuppressUi)
             .Num("wireLines", WireLines).Str("switches", S5aSwitches.Describe())
             .Bool("locoValid", _locoValid).Str("loco", S7aLoco.Describe(ref _loco)).Bool("outOfWater", _locoOut).Bool("armsOut", _armsOut)
             .Num("outOfWaterFrames", OutOfWaterFrames).Num("armsOutFrames", ArmsOutFrames)
             .Str("featuresDisabled", _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray())).Num("tickMs", now);
        }

        private string CurrentClipSafe()
        {
            try { return CurrentClip(); } catch (Exception) { return null; }
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
                DeLog.Error("DE.S5a FEATURE " + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message +
                            " (the rest of the bridge keeps running)\n" + e);
                if (feature == "arms")
                {
                    // the ghost rule hides every renderer again; best effort on the rest (stage 3: the mask and the
 // disabled ArmsController must not outlive the feature)
                    try { _s1.Ghost.Exempt = null; } catch (Exception) { }
                    _visible = false;
                    try { Player p = Player.main; if (_maskActive && p != null) { _maskActive = false; p.SetScubaMaskActive(false); } } catch (Exception) { }
                    try { if (_driving) SetDriving(false); } catch (Exception) { }
                }
            }
        }

        private static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
