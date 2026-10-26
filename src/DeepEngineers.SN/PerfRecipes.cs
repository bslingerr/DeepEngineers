using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Applies the Idle / Track / Full recipe chosen by <see cref="PerfRecipeRules"/> (ARCHITECTURE §5.13; CONTRACT §1.1
 /// "vSyncCount, targetFrameRate: values at load, per perfMode, restored on quit, never persisted"):
 /// <list type="bullet">
 /// <item><c>QualitySettings.vSyncCount</c> and <c>Application.targetFrameRate</c> set directly (never through
 /// GraphicsUtil, whose SetFrameRate clamps to 30) and re-asserted every frame; an outside change (the options code,
 /// StartScreen's 144, the console) is counted and logged at most 5 times. A vSyncCount change fires
 /// DisplayManager.OnDisplayChanged, which makes <see cref="SnWindow"/> verify the styles.</item>
 /// <item>Main and GUI cameras: <c>SNCameraRoot.main.mainCam</c> and <c>guiCam</c> (<c>MainCamera.camera</c> when
 /// SNCameraRoot is missing) <c>enabled</c> per the recipe, every frame. The <c>MainCamera</c> script stays enabled, so
 /// <c>MainCamera.camera</c> stays valid and LargeWorldStreamer keeps following the transform. While
 /// a WaitScreen is up in Full the cameras are not forced on (WBOIT.SetLoadingScreenOptimizations turns the main camera
 /// off during loads).</item>
 /// <item>Process priority: BelowNormal in Idle, else the class found at start (at most Normal: Subnautica never runs
 /// above SE), by <c>SetPriorityClass</c> on our own process; checked every second.</item>
 /// <item>The user's pair and priority are saved before the first application and restored on quit or bridge
 /// disposal; <c>GameSettings.SaveAsync</c> is never called
 /// </item>
 /// <item>The measured frame rate (main-thread frames per unscaled second) is logged 2 s after every recipe change.</item>
 /// </list>
 /// </summary>
    internal sealed class PerfApplier
    {
        public const ulong MeasureAfterMs = 2000;
        private const int MaxReassertLogs = 5;

        private bool _hasApplied;
        private int _reassertLogs;
        private bool _measurePending;
        private ulong _measureAtMs;
        private string _measureLabel;

        public bool UserSaved { get; private set; }
        public int UserVSync, UserTargetFps;
        public uint UserPriority;
        public RecipeDecision Applied;
        public bool HasApplied { get { return _hasApplied; } }
        public ulong AppliedAtMs;
        public long Changes, PairReasserts, CameraWrites, PriorityWrites, PriorityReasserts;
        public int VSyncNow, TargetFpsNow;
        public bool CamerasOnNow;
        public uint PriorityNow;
        public string LastWhy = "";

        // ------------------------------------------------------------------------------------------ user values

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SaveUser()
        {
            if (UserSaved) return;
            UserSaved = true;
            UserVSync = QualitySettings.vSyncCount;
            UserTargetFps = Application.targetFrameRate;
            UserPriority = SnNative.ProcessPriorityClass();
            DeLog.Info("DE.Perf user values saved: vSyncCount=" + UserVSync + " targetFrameRate=" + UserTargetFps + " priority=" + SnNative.PriorityName(UserPriority) +
                       " (restored on quit; never persisted: no GameSettings.SaveAsync)");
        }

        /// <summary>The priority class used outside Idle: the original one, at most Normal.</summary>
        public uint NormalClass
        {
            get
            {
                uint c = UserPriority;
                if (c == SnNative.IDLE_PRIORITY_CLASS || c == SnNative.BELOW_NORMAL_PRIORITY_CLASS || c == SnNative.NORMAL_PRIORITY_CLASS) return c;
                return SnNative.NORMAL_PRIORITY_CLASS;
            }
        }

        /// <summary>
 /// S7 review F10: a test frame cap (frames per second; 0 = none) set by the dev command
 /// <c>de.fx {action: fps, cap}</c> for the S7.N4.loss burst and cleared by the launcher's teardown (and when the
 /// effects player is disposed). While set, vSyncCount 0 and targetFrameRate = the cap; never persisted.
 /// </summary>
        public static volatile int TestFpsCap;
        private int _lastTestFpsCap;

        // ------------------------------------------------------------------------------------------ per frame

 /// <summary>Applies the decision (every frame). <paramref name="waitScreen"/>: a WaitScreen is up (cameras not forced on).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Apply(ref RecipeDecision d, ulong nowMs, bool waitScreen)
        {
            bool changed = !_hasApplied || d.Recipe != Applied.Recipe || d.VSyncCount != Applied.VSyncCount ||
                           d.TargetFrameRate != Applied.TargetFrameRate || d.CamerasOn != Applied.CamerasOn || d.BelowNormal != Applied.BelowNormal;
            if (changed)
            {
                string from = _hasApplied ? PerfRecipeRules.Name(Applied.Recipe) + " " + Applied.Pair + " cameras " + (Applied.CamerasOn ? "on" : "off") : "(none)";
                Applied = d;
                _hasApplied = true;
                AppliedAtMs = nowMs;
                Changes++;
                LastWhy = d.Why;
                DeLog.Info("DE.Perf recipe " + from + " -> " + PerfRecipeRules.Name(d.Recipe) + " " + d.Pair + " cameras " + (d.CamerasOn ? "on" : "off") +
                           " priority " + (d.BelowNormal ? "BelowNormal" : SnNative.PriorityName(NormalClass)) + " (" + d.Why + ")");
                ApplyPriority(d.BelowNormal ? SnNative.BELOW_NORMAL_PRIORITY_CLASS : NormalClass, false);
                _measurePending = true;
                _measureAtMs = nowMs + MeasureAfterMs;
                _measureLabel = PerfRecipeRules.Name(d.Recipe) + " " + d.Pair;
            }

            // S7 review F10: the test cap of de.fx {action: fps, cap} (S7.N4.loss at 30 fps) wins over the recipe while set;
 // the recipe's own pair comes back on the next frame after the cap is cleared (no re-assert noise either way)
            int cap = TestFpsCap;
            int wantVs = cap > 0 ? 0 : d.VSyncCount, wantFps = cap > 0 ? cap : d.TargetFrameRate;
            bool capEdge = cap != _lastTestFpsCap;
            _lastTestFpsCap = cap;
            int vs = QualitySettings.vSyncCount;
            if (vs != wantVs)
            {
                QualitySettings.vSyncCount = wantVs;
                if (!changed && !capEdge && cap == 0) NoteReassert("vSyncCount " + vs + " -> " + wantVs);
            }
            int fps = Application.targetFrameRate;
            if (fps != wantFps)
            {
                Application.targetFrameRate = wantFps;
                if (!changed && !capEdge && cap == 0) NoteReassert("targetFrameRate " + fps + " -> " + wantFps);
            }
            VSyncNow = QualitySettings.vSyncCount;
            TargetFpsNow = Application.targetFrameRate;

            bool camOn = d.CamerasOn;
            if (!(camOn && waitScreen)) SetCameras(camOn);
            CamerasOnNow = CamerasOn();
        }

        private void NoteReassert(string what)
        {
            PairReasserts++;
            if (_reassertLogs >= MaxReassertLogs) return;
            _reassertLogs++;
            DeLog.Warn("DE.Perf frame-rate value changed outside the plugin: " + what + " re-asserted (#" + PairReasserts +
                       (_reassertLogs == MaxReassertLogs ? "; further ones only counted" : "") + ")");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SetCameras(bool on)
        {
            SNCameraRoot root = SNCameraRoot.main;
            if (root != null)
            {
                SetCamera(root.mainCam, on);
                SetCamera(root.guiCam, on);
                return;
            }
            SetCamera(MainCamera.camera, on);
        }

        private void SetCamera(Camera c, bool on)
        {
            if (c == null || c.enabled == on) return;
            c.enabled = on;
            CameraWrites++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CamerasOn()
        {
            SNCameraRoot root = SNCameraRoot.main;
            Camera c = root != null ? root.mainCam : MainCamera.camera;
            return c != null && c.enabled;
        }

        private void ApplyPriority(uint wanted, bool reassert)
        {
            uint now = SnNative.ProcessPriorityClass();
            PriorityNow = now;
            if (now == wanted) return;
            int err;
            bool ok = SnNative.SetProcessPriorityClass(wanted, out err);
            if (reassert) PriorityReasserts++;
            PriorityWrites++;
            PriorityNow = SnNative.ProcessPriorityClass();
            DeLog.Info("DE.Perf priority " + SnNative.PriorityName(now) + " -> " + SnNative.PriorityName(wanted) + (ok ? "" : " FAILED error " + err) +
                       (reassert ? " (re-asserted)" : "") + " (SetPriorityClass, our own process)");
        }

        // ------------------------------------------------------------------------------------------ per second

        public void Second(ulong nowMs, FramePacing pacing)
        {
            if (!_hasApplied) return;
            ApplyPriority(Applied.BelowNormal ? SnNative.BELOW_NORMAL_PRIORITY_CLASS : NormalClass, true);
            if (_measurePending && nowMs >= _measureAtMs)
            {
                _measurePending = false;
                DeLog.Info("DE.Perf measured " + pacing.LastFps.ToString("0.0", CultureInfo.InvariantCulture) + " fps (maxGap " + pacing.LastMaxGapMs +
                           " ms) " + MeasureAfterMs / 1000 + " s after " + _measureLabel + "; applied vSyncCount=" + VSyncNow + " targetFrameRate=" + TargetFpsNow +
                           " cameras=" + (CamerasOnNow ? "on" : "off") + " priority=" + SnNative.PriorityName(PriorityNow));
            }
        }

        // ------------------------------------------------------------------------------------------ restore

 /// <summary>The user's frame-rate pair and priority back (quit, bridge disposal); cameras on when <paramref name="cameras"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RestoreUser(string why, bool cameras)
        {
            if (!UserSaved) return;
            try
            {
                QualitySettings.vSyncCount = UserVSync;
                Application.targetFrameRate = UserTargetFps;
                int err;
                if (UserPriority != 0) SnNative.SetProcessPriorityClass(UserPriority, out err);
                if (cameras) SetCameras(true);
                DeLog.Info("DE.Perf user values restored (" + why + "): vSyncCount=" + UserVSync + " targetFrameRate=" + UserTargetFps +
                           " priority=" + SnNative.PriorityName(UserPriority) + (cameras ? " cameras on" : "") + " (in memory only)");
            }
            catch (Exception e)
            {
                DeLog.Warn("DE.Perf restore failed (" + why + "): " + e.GetType().Name + ": " + e.Message);
            }
        }

        public string Describe()
        {
            return "perf{" + (_hasApplied ? PerfRecipeRules.Name(Applied.Recipe) + " want" + Applied.Pair : "none") + " now(" + VSyncNow + ", " + TargetFpsNow + ")" +
                   " cameras=" + (CamerasOnNow ? "on" : "off") + " priority=" + SnNative.PriorityName(PriorityNow) + " changes=" + Changes +
                   " reasserts=" + PairReasserts + " user(" + UserVSync + ", " + UserTargetFps + ")}";
        }
    }
}
