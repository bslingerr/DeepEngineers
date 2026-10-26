using System;
using System.Runtime.InteropServices;
using System.Text;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The Subnautica window role, S0 part only (ROADMAP S0 SN plugin; CONTRACT §5.4 C0 snFlags/snHwnd, §1.1):
 /// DisableProcessWindowsGhosting, the power-throttling opt-out (return value logged) and finding our own HWND to
 /// publish in `snHwnd`. No style, size, z-order or foreground change happens in S0 (those are S2: stylesApplied
 /// stays 0). Called only in bridge mode: a dormant session changes nothing.
 /// </summary>
    internal static class WindowRole
    {
        /// <summary>Unity's standalone player window class.</summary>
        public const string UnityWindowClass = "UnityWndClass";

        public static bool DisableGhosting()
        {
            string error;
            bool ok = Win32.DisableProcessWindowsGhosting(out error);
            if (ok) DeLog.Info("DE.Window ghosting disabled (DisableProcessWindowsGhosting)");
            else DeLog.Warn("DE.Window DisableProcessWindowsGhosting failed: " + error);
            return ok;
        }

        public static void OptOutOfPowerThrottling()
        {
            PowerThrottlingResult r = Win32.OptOutOfPowerThrottling();
            string line = "DE.Perf powerThrottling optOut " + r;
            if (r.Ok) DeLog.Info(line + (r.Detail != null ? " (" + r.Detail + ")" : ""));
            else DeLog.Warn(line);
        }

        // ------------------------------------------------------------------------------------------ HWND lookup

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private const uint GW_OWNER = 4;

        // Search state (main thread only); the delegate is kept in a static field so it is never collected mid-call.
        private static readonly EnumWindowsProc Callback = OnWindow;
        private static readonly StringBuilder ClassBuffer = new StringBuilder(256);
        private static uint _searchPid;
        private static IntPtr _bestUnity, _bestUnityHidden, _bestOther;
        private static string _bestOtherClass;

        /// <summary>
 /// Our process's top-level, unowned `UnityWndClass` window (a visible one first, else a hidden one).
 /// IntPtr.Zero when none exists yet (Unity may not have created or shown it at plugin Awake; the bridge retries
 /// once per second).
 /// </summary>
        public static IntPtr FindMainWindow(out string description)
        {
            _searchPid = Win32.CurrentProcessId();
            _bestUnity = IntPtr.Zero; _bestUnityHidden = IntPtr.Zero; _bestOther = IntPtr.Zero; _bestOtherClass = null;
            try
            {
                EnumWindows(Callback, IntPtr.Zero);
            }
            catch (Exception e)
            {
                description = "EnumWindows failed: " + e.GetType().Name + ": " + e.Message;
                return IntPtr.Zero;
            }
            if (_bestUnity != IntPtr.Zero) { description = UnityWindowClass + " visible"; return _bestUnity; }
            if (_bestUnityHidden != IntPtr.Zero) { description = UnityWindowClass + " hidden"; return _bestUnityHidden; }
            // Another class (e.g. a console window attributed to this process) is never published as snHwnd.
            description = _bestOther != IntPtr.Zero
                ? "no " + UnityWindowClass + " yet (other visible window of class " + _bestOtherClass + " ignored)"
                : "no " + UnityWindowClass + " yet";
            return IntPtr.Zero;
        }

        private static bool OnWindow(IntPtr hWnd, IntPtr lParam)
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != _searchPid) return true;
                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;
                ClassBuffer.Length = 0;
                int n = GetClassNameW(hWnd, ClassBuffer, ClassBuffer.Capacity);
                string cls = n > 0 ? ClassBuffer.ToString() : "";
                bool visible = IsWindowVisible(hWnd);
                if (cls == UnityWindowClass)
                {
                    if (visible) { if (_bestUnity == IntPtr.Zero) _bestUnity = hWnd; }
                    else if (_bestUnityHidden == IntPtr.Zero) _bestUnityHidden = hWnd;
                }
                else if (visible && _bestOther == IntPtr.Zero)
                {
                    _bestOther = hWnd;
                    _bestOtherClass = cls;
                }
            }
            catch (Exception)
            {
                // never let an exception cross the native callback
            }
            return true;
        }
    }
}
