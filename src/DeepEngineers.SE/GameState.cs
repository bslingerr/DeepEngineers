using System;
using System.Diagnostics;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using VRage;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Read-only facts from Space Engineers 1.210.014, each member read:
 /// `MySession.Static` (static property), `MySession.GameplayFrameCounter` (int property),
 /// `MyGuiScreenGamePlay.Static` (public static field) and `.LoadingDone` (public bool property, set when the
 /// loading screen finishes), `MySandboxGame.IsPaused` (static property), `MyVRage.Platform.Windows.Window.IsActive`
 /// (read by SE itself on the update thread in `MySandboxGame.UpdateSound`), `MyFpsManager.FrameTimeAvg` (the
 /// "FrameTime" column of SE's STATISTICS log line). Update thread only. Never throws.
 /// </summary>
    internal static class GameState
    {
        public static bool SessionExists
        {
            get { try { return MySession.Static != null; } catch (Exception) { return false; } }
        }

        public static bool GameplayScreen
        {
            get { try { return MyGuiScreenGamePlay.Static != null; } catch (Exception) { return false; } }
        }

        public static bool GameplayLoadingDone
        {
            get
            {
                try
                {
                    MyGuiScreenGamePlay g = MyGuiScreenGamePlay.Static;
                    return g != null && g.LoadingDone;
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>`MySession.Static.GameplayFrameCounter` (C1/C5 `tick`), 0 outside a world.</summary>
        public static ulong GameplayTick
        {
            get
            {
                try
                {
                    MySession s = MySession.Static;
                    return s != null ? (ulong)(uint)s.GameplayFrameCounter : 0UL;
                }
                catch (Exception) { return 0; }
            }
        }

        public static bool Paused
        {
            get { try { return MySandboxGame.IsPaused; } catch (Exception) { return false; } }
        }

        /// <summary>SE's window is the active window (ARCHITECTURE §4.1 `foreground` = `MyGameWindow.IsActive`).</summary>
        public static bool Foreground
        {
            get
            {
                try
                {
                    IVRagePlatform p = MyVRage.Platform;
                    if (p == null || p.Windows == null) return false;
                    IVRageWindow w = p.Windows.Window;
                    return w != null && w.IsActive;
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>The phase a save finalizer returns to (no loading-flag bookkeeping; any thread).</summary>
        public static uint PhaseOutsideSave()
        {
            if (Phases.Exiting) return Wire.SePhase.Exiting;
            if (Phases.Loading) return Wire.SePhase.LoadingWorld;
            return SessionExists && GameplayScreen ? Wire.SePhase.InWorld : Wire.SePhase.Menu;
        }

        // --------------------------------------------------------------------------------------------- frame stats

        public static float FrameTimeAvgMs { get { try { return MyFpsManager.FrameTimeAvg; } catch (Exception) { return float.NaN; } } }
        public static float FrameTimeMs { get { try { return MyFpsManager.FrameTime; } catch (Exception) { return float.NaN; } } }
        public static float FrameTimeMinMs { get { try { return MyFpsManager.FrameTimeMin; } catch (Exception) { return float.NaN; } } }
        public static float FrameTimeMaxMs { get { try { return MyFpsManager.FrameTimeMax; } catch (Exception) { return float.NaN; } } }
        public static int Fps { get { try { return MyFpsManager.GetFps(); } catch (Exception) { return -1; } } }

        public static float CpuLoadSmooth
        {
            get
            {
                try
                {
                    MySandboxGame g = MySandboxGame.Static;
                    return g != null ? g.CPULoadSmooth : float.NaN;
                }
                catch (Exception) { return float.NaN; }
            }
        }

        // ----------------------------------------------------------------------------------------- window handle

        private static Process _self;

        /// <summary>
 /// The process's main window (Process.MainWindowHandle: the first visible unowned top-level window, i.e. SE's
 /// game window once the splash is gone). Used for C0 `seHwnd`; HYPOTHESIS until S2 checks it against
 /// `MyGameWindow` (reading the WinForms handle from the update thread is a cross-thread call).
 /// </summary>
        public static ulong MainWindowHandle()
        {
            try
            {
                if (_self == null) _self = Process.GetCurrentProcess();
                _self.Refresh();
                return (ulong)_self.MainWindowHandle.ToInt64();
            }
            catch (Exception) { return 0; }
        }
    }
}
