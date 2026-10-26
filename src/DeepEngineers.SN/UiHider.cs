using System;
using System.Runtime.CompilerServices;
using UWE;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Subnautica's own UI and screen effects kept off (ROADMAP S1 SN plugin; ARCHITECTURE §5.11; CONTRACT §1.1 "HUD hide
 /// phase, PDA voice, radiation FX" runtime only), and "Run in background" (ARCHITECTURE §5.2; CONTRACT §1.1
 /// `MiscSettings.runInBackground` forced true in memory, never persisted):
 /// <list type="bullet">
 /// <item><c>GUIController.SetHidePhase(GUIController.HidePhase.All)</c> (static; VERIFIED: hides every
 /// HideForScreenshots object of type Mask, HUD or ViewModel) after the world load, again after every later scene load
 /// and every 10 s (new HUD objects), and at once when <c>GUIController.main</c>'s own hide phase changes
 /// PDA voice, subtitles, ErrorMessage lines, voice and PDA notifications and popups
 /// are swallowed by <see cref="S1Patches"/>. Seeded failure S1.M7 never sets the hide phase.</item>
 /// <item><c>Player.main.SetRadiationAmount(0)</c> every frame (RadiatePlayerInRange writes it every 0.2 s), and the
 /// main camera's <c>RadiationsScreenFXController</c> and <c>RadiationsScreenFX</c> disabled (the controller re-enables
 /// the effect whenever its noise factor is above 0).</item>
 /// <item>Run in background: the options value is logged once at the main menu (read only; StartScreen loads the
 /// options before the menu). From the world set-up on (before ReadyIdle, hence before the launcher starts SE and SE
 /// takes the focus), when <c>MiscSettings.runInBackground</c> is false it is set true and
 /// <c>ApplicationFocus.OnRunInBackgroundChanged</c> is called; checked every second, together with whether FreezeTime
 /// holds an ApplicationFocus entry (S1.N11). Not forced at the menu: the menu's NewsfeedPopup may call
 /// <c>GameSettings.SaveAsync</c>, which would persist a forced value. Test hook (dev):
 /// <c>DEEPENGINEERS_SN_TEST_RUNINBACKGROUND_OFF=1</c> sets it false in memory first. Seeded failure S1.M8 skips the
 /// enforcement.</item>
 /// </list>
 /// </summary>
    internal sealed class UiHider
    {
        public const ulong HideReassertMs = 10000;

        private bool _rbOriginalLogged, _rbTestApplied;
        private bool _hideApplied;
        private ulong _nextHideMs;
        private bool _sceneDirty;
        private bool _fxDisabled;
        private bool _focusFreezerLogged;
        private bool _phaseSeen;
        private GUIController.HidePhase _lastPhase;
        public long HidePhaseChanges;

        public bool RunInBackgroundOriginal { get; private set; }
        public bool RunInBackgroundForced { get; private set; }
        public long RunInBackgroundEnforced, HideApplications, RadiationZeroed;
        public bool UiHidden { get { return _hideApplied && S1Patches.UiSinksInstalled > 0; } }

        /// <summary>A scene was loaded after the world: the hide phase is applied again on the next frame.</summary>
        public void MarkSceneLoaded() { _sceneDirty = true; }

        // -------------------------------------------------------------------------- run in background (Menu onwards)

 /// <summary>Logs the options value once (read only).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void NoteRunInBackground()
        {
            if (_rbOriginalLogged) return;
            _rbOriginalLogged = true;
            RunInBackgroundOriginal = MiscSettings.runInBackground;
            DeLog.Info("DE.RunInBackground original MiscSettings.runInBackground=" + RunInBackgroundOriginal +
                       " Application.runInBackground=" + Application.runInBackground + " (options value; never saved by the bridge)");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EnforceRunInBackground()
        {
            NoteRunInBackground();
            if (S1Switches.TestRunInBackgroundOff && !_rbTestApplied)
            {
                _rbTestApplied = true;
                MiscSettings.runInBackground = false;
                ApplicationFocus.OnRunInBackgroundChanged();
                DeLog.Warn("DE.RunInBackground TEST-HOOK " + S1Switches.TestRunInBackgroundOffVar + ": set false in memory (S1.N11)");
            }
            if (DevMutants.IsOn(DevMutants.S1NoRunInBackground))
            {
                RunInBackgroundForced = false;
                return;
            }
            if (!MiscSettings.runInBackground)
            {
                MiscSettings.runInBackground = true;
                ApplicationFocus.OnRunInBackgroundChanged();
                RunInBackgroundEnforced++;
                DeLog.Info("DE.RunInBackground forced true in memory (ApplicationFocus.OnRunInBackgroundChanged) #" + RunInBackgroundEnforced);
            }
            RunInBackgroundForced = MiscSettings.runInBackground;
        }

        /// <summary>FreezeTime holds an ApplicationFocus entry (S1.N11 must stay false); logged on each change.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool FocusFreezerPresent()
        {
            bool present = FreezeTime.Contains(FreezeTime.Id.ApplicationFocus);
            if (present != _focusFreezerLogged)
            {
                _focusFreezerLogged = present;
                string line = "DE.RunInBackground FreezeTime ApplicationFocus entry " + (present ? "PRESENT (game time frozen by focus loss)" : "gone") +
                              " runInBackground=" + MiscSettings.runInBackground + " focused=" + Application.isFocused;
                if (present) DeLog.Warn(line);
                else DeLog.Info(line);
            }
            return present;
        }

        // -------------------------------------------------------------------------- in world

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyHide(ulong now, string why)
        {
            _nextHideMs = now + HideReassertMs;
            _sceneDirty = false;
            if (DevMutants.IsOn(DevMutants.S1HidePhaseNone))
            {
                if (HideApplications == 0) DeLog.Warn("DE.Ui hide phase left at None (MUTANT S1.M7)");
                HideApplications++;
                _hideApplied = false;
                return;
            }
            // MaskHUD, not All: in the decomp All = HideForScreenshots.Hide(Mask | HUD | ViewModel), and the ViewModel flag
 // disables the first-person view model's renderers and PlayerBreathBubbles (HideForScreenshots): harmless for the

 // 'enabled=0/25 reEnabled=25' every 600 ticks). MaskHUD hides the HUD and the scuba-mask overlay only.
            GUIController.SetHidePhase(GUIController.HidePhase.MaskHUD);
            HideApplications++;
            if (!_hideApplied) DeLog.Info("DE.Ui GUIController.SetHidePhase(MaskHUD) (" + why + "); UI sinks " + S1Patches.UiSinksInstalled + "/7");
            _hideApplied = true;
            DisableRadiationFx();
        }

        /// <summary>Per frame in world: radiation zeroed; the hide phase re-applied after scene loads and every 10 s.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void PerFrame(ulong now)
        {
            Player p = Player.main;
            if (p != null && p.radiationAmount != 0f)
            {
                S11Hooks.RadiationSeen(p.radiationAmount); // S11 hook: the level RadiatePlayerInRange set, for S11's radiation damage (the FX stays hidden)
                p.SetRadiationAmount(0f);
                RadiationZeroed++;
            }
            if (_sceneDirty) ApplyHide(now, "scene loaded");
            else if (now >= _nextHideMs) ApplyHide(now, "10 s re-assertion");
            else if (_hideApplied) CheckGuiControllerPhase(now);
        }

        /// <summary>
 /// GUIController.Update cycles its own hide phase on F6 (when Subnautica has the focus) and applies it through the
 /// static SetHidePhase: any change of that instance field re-applies All at once.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CheckGuiControllerPhase(ulong now)
        {
            GUIController g = GUIController.main;
            if (g == null) return;
            GUIController.HidePhase phase = g.GetHidePhase();
            if (!_phaseSeen)
            {
                _phaseSeen = true;
                _lastPhase = phase;
                return;
            }
            if (phase == _lastPhase) return;
            _lastPhase = phase;
            HidePhaseChanges++;
            DeLog.Warn("DE.Ui GUIController hide phase changed to " + phase + " (F6 with Subnautica focused): SetHidePhase(MaskHUD) again (#" + HidePhaseChanges + ")");
            ApplyHide(now, "GUIController hide phase changed");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DisableRadiationFx()
        {
            Camera cam = MainCamera.camera;
            if (cam == null) return;
            RadiationsScreenFXController ctl = cam.GetComponent<RadiationsScreenFXController>();
            RadiationsScreenFX fx = cam.GetComponent<RadiationsScreenFX>();
            if (ctl != null && ctl.enabled) ctl.enabled = false;
            if (fx != null && fx.enabled) fx.enabled = false;
            if (!_fxDisabled)
            {
                _fxDisabled = true;
                DeLog.Info("DE.Ui radiation: RadiationsScreenFXController " + (ctl != null ? "disabled" : "absent") + ", RadiationsScreenFX " +
                           (fx != null ? "disabled" : "absent") + ", radiationAmount zeroed every frame");
            }
        }

        public string Describe()
        {
            return "uiHidden=" + UiHidden + " hideApplications=" + HideApplications + " runInBackground=" + RunInBackgroundForced +
                   "(original " + RunInBackgroundOriginal + ", enforced " + RunInBackgroundEnforced + ") radiationZeroed=" + RadiationZeroed +
                   " " + S1Patches.DescribeUi();
        }
    }
}
