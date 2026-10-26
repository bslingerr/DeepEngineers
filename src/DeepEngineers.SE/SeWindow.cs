using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using VRage;

namespace DeepEngineers.SE
{
    /// <summary>
 /// SE's game window (ARCHITECTURE §5.1 "Handle from MyWindowsWindows.WindowHandle"): VERIFIED
 /// <c>public IntPtr
 /// WindowHandle { get; private set; }</c> on the internal class <c>MyWindowsWindows</c> (the object behind
 /// <c>MyVRage.Platform.Windows</c>; the property is not on the <c>IVRageWindows</c> interface, so it is read by
 /// reflection), set once in <c>CreateWindow</c> from the form's handle; <c>GameWindow</c> (<c>MyGameWindow</c>, a
 /// WinForms Form) gives the form for its <c>TopMost</c> property. Reading the stored handle is not a call on the
 /// window, so the update thread may do it. Falls back to <c>Process.MainWindowHandle</c> (the S0 source, D35).
 /// </summary>
    internal static class SeWindow
    {
        private static bool _resolved;
        private static object _windows;
        private static PropertyInfo _handleProp, _gameWindowProp;
        private static PropertyInfo _topMostProp;
        private static string _source = "unresolved";
        private static bool _comparedLogged;

        public static string Source { get { return _source; } }

        /// <summary>SE's window handle (0 until the window exists).</summary>
        public static IntPtr Handle
        {
            get
            {
                try
                {
                    Resolve();
                    if (_handleProp != null && _windows != null)
                    {
                        IntPtr h = (IntPtr)_handleProp.GetValue(_windows, null);
                        if (h != IntPtr.Zero) { _source = "MyWindowsWindows.WindowHandle"; return h; }
                    }
                }
                catch (Exception) { }
                _source = "Process.MainWindowHandle (fallback)";
                return new IntPtr((long)GameState.MainWindowHandle());
            }
        }

        /// <summary>The <c>MyGameWindow</c> form, or null.</summary>
        public static object GameWindow
        {
            get
            {
                try { Resolve(); return _gameWindowProp != null && _windows != null ? _gameWindowProp.GetValue(_windows, null) : null; }
                catch (Exception) { return null; }
            }
        }

        private static void Resolve()
        {
            if (_resolved && _windows != null) return;
            _resolved = true;
            IVRagePlatform p = MyVRage.Platform;
            _windows = p != null ? p.Windows : null;
            if (_windows == null) { _resolved = false; return; }
            Type t = _windows.GetType();
            _handleProp = t.GetProperty("WindowHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _gameWindowProp = t.GetProperty("GameWindow", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_handleProp != null && _handleProp.PropertyType != typeof(IntPtr)) _handleProp = null;
        }

        /// <summary>
 /// Sets WinForms <c>Form.
 /// consistent with the window; false when the form is not reachable (the caller then uses SetWindowPos).
 /// </summary>
        public static bool TrySetFormTopMost(bool topMost)
        {
            try
            {
                object form = GameWindow;
                if (form == null) return false;
                if (_topMostProp == null) _topMostProp = form.GetType().GetProperty("TopMost", BindingFlags.Instance | BindingFlags.Public);
                if (_topMostProp == null) return false;
                _topMostProp.SetValue(form, topMost, null);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>One log line comparing the handle sources (S0 D35: Process.MainWindowHandle was HYPOTHESIS).</summary>
        public static string CompareOnce()
        {
            if (_comparedLogged) return null;
            IntPtr h = Handle;
            if (h == IntPtr.Zero) return null;
            _comparedLogged = true;
            ulong main = GameState.MainWindowHandle();
            return "DE.Display seHwnd=" + NativeWin.Hex(h) + " from " + _source + "; Process.MainWindowHandle=0x" + main.ToString("x") +
                   ((ulong)h.ToInt64() == main ? " (same: S0 D35 confirmed)" : " (DIFFERENT: C0 seHwnd now from MyWindowsWindows.WindowHandle)") +
                   " class=" + NativeWin.ClassName(h);
        }

        /// <summary>
 /// The per-app GPU preferences of this executable and the global DirectX settings (ARCHITECTURE §5.1 "Mode selection
 /// at start"; R2.4 flip-model upgrade), with the Windows build. Registry read only.
 /// </summary>
        public static string GpuPreferences()
        {
            string exe = "?", app = null, global = null, build = null;
            try { exe = Process.GetCurrentProcess().MainModule.FileName; } catch (Exception) { }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                {
                    if (k != null)
                    {
                        app = k.GetValue(exe) as string;
                        global = k.GetValue("DirectXUserGlobalSettings") as string;
                    }
                }
            }
            catch (Exception) { }
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (k != null) build = k.GetValue("CurrentBuildNumber") as string;
            }
            catch (Exception) { }
            int b;
            bool buildKnown = int.TryParse(build, out b);
            bool upgradeGlobal = global != null && global.Replace(" ", "").IndexOf("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase) >= 0;
            bool optOut = app != null && app.Replace(" ", "").IndexOf("SwapEffectUpgradeEnable=0", StringComparison.OrdinalIgnoreCase) >= 0;
            string risk = !buildKnown ? "build unknown"
                : b < 22557 ? "build " + b + " < 22557: no flip-model upgrade of windowed games (DWM alpha expected to work)"
                : optOut ? "build " + b + ": per-app opt-out present"
                : upgradeGlobal ? "build " + b + " with SwapEffectUpgradeEnable=1 and no per-app opt-out: SE's swap chain may be flip-upgraded (DWM alpha lost; A' expected)"
                : "build " + b + ": upgrade not enabled globally";
            return "DE.Display gpuPreferences exe=" + exe + " app=" + (app ?? "none") + " global=" + (global ?? "none") + " windowsBuild=" + (build ?? "?") + " -> " + risk;
        }
    }

    /// <summary>The real window calls with WinForms' TopMost for SE's own form (render thread).</summary>
    internal sealed class SeWindowOps : NativeWindowOps
    {
        public override bool SetTopmost(IntPtr h, bool topmost)
        {
            if (h != IntPtr.Zero && h == SeWindow.Handle && SeWindow.TrySetFormTopMost(topmost)) return true;
            return base.SetTopmost(h, topmost);
        }
    }
}
