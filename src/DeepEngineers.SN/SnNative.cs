using System;
using System.Runtime.InteropServices;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The Win32 calls of the S2 window role and process recipes (ARCHITECTURE §5.1, §5.13, §5.14). Every call acts on
 /// Subnautica's own window or process; none blocks on another process (SetWindowPos on our own window, the SE window
 /// only as the insert-after reference). Constants from the Windows SDK headers (winuser.h, winbase.h, shobjidl.h).
 /// Wrappers never throw: a failure returns false / zero and the Win32 error where it matters.
 /// </summary>
    internal static class SnNative
    {
        public const int GWL_EXSTYLE = -20;
        public const int SW_HIDE = 0, SW_SHOWMINNOACTIVE = 7, SW_SHOWNOACTIVATE = 4;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
                          SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
        public const uint MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;
        public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000, NORMAL_PRIORITY_CLASS = 0x00000020,
                          IDLE_PRIORITY_CLASS = 0x00000040, ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000,
                          HIGH_PRIORITY_CLASS = 0x00000080, REALTIME_PRIORITY_CLASS = 0x00000100;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

        [DllImport("kernel32.dll")]
        private static extern void SetLastError(uint code);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        private static extern uint GetDpiForWindowNative(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

        // ------------------------------------------------------------------------------------------ wrappers

        public static long GetExStyle(IntPtr hWnd)
        {
            try
            {
                return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hWnd, GWL_EXSTYLE);
            }
            catch (Exception) { return 0; }
        }

        /// <summary>SetWindowLongPtr(GWL_EXSTYLE); false with the Win32 error when it failed.</summary>
        public static bool SetExStyle(IntPtr hWnd, long exStyle, out int win32Error)
        {
            win32Error = 0;
            try
            {
                SetLastError(0);
                long previous = IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(exStyle)).ToInt64()
                                                  : SetWindowLong32(hWnd, GWL_EXSTYLE, unchecked((int)exStyle));
                if (previous == 0)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return win32Error == 0;
                }
                return true;
            }
            catch (Exception) { win32Error = -1; return false; }
        }

        public static bool TryClientRectOnScreen(IntPtr hWnd, out RectI rect)
        {
            rect = default(RectI);
            try
            {
                RECT r;
                if (!GetClientRect(hWnd, out r)) return false;
                var p = new POINT { X = 0, Y = 0 };
                if (!ClientToScreen(hWnd, ref p)) return false;
                rect = new RectI(p.X, p.Y, p.X + (r.Right - r.Left), p.Y + (r.Bottom - r.Top));
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool TryWindowRect(IntPtr hWnd, out RectI rect)
        {
            rect = default(RectI);
            try
            {
                RECT r;
                if (!GetWindowRect(hWnd, out r)) return false;
                rect = new RectI(r.Left, r.Top, r.Right, r.Bottom);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The monitor nearest to a window: its rectangle and whether it is the primary monitor.</summary>
        public static bool TryMonitorOfWindow(IntPtr hWnd, out RectI monitor, out bool primary)
        {
            monitor = default(RectI);
            primary = false;
            try
            {
                IntPtr m = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
                return TryMonitorInfo(m, out monitor, out primary);
            }
            catch (Exception) { return false; }
        }

        /// <summary>The monitor nearest to a screen rectangle (SE's C5 seRect).</summary>
        public static bool TryMonitorOfRect(RectI rect, out RectI monitor, out bool primary)
        {
            monitor = default(RectI);
            primary = false;
            try
            {
                var r = new RECT { Left = rect.L, Top = rect.T, Right = rect.R, Bottom = rect.B };
                IntPtr m = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
                return TryMonitorInfo(m, out monitor, out primary);
            }
            catch (Exception) { return false; }
        }

        private static bool TryMonitorInfo(IntPtr m, out RectI monitor, out bool primary)
        {
            monitor = default(RectI);
            primary = false;
            if (m == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(m, ref mi)) return false;
            monitor = new RectI(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
            primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
            return true;
        }

        /// <summary>GetDpiForWindow (Windows 10 1607+); 0 when unavailable.</summary>
        public static uint DpiForWindow(IntPtr hWnd)
        {
            try { return GetDpiForWindowNative(hWnd); }
            catch (Exception) { return 0; }
        }

        public static uint ForegroundPid(out IntPtr hwnd)
        {
            hwnd = IntPtr.Zero;
            try
            {
                hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return 0;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                return pid;
            }
            catch (Exception) { return 0; }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadTimes(IntPtr hThread, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr hProcess, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

        /// <summary>
 /// Kernel + user CPU time of the calling thread in 100-ns units (GetThreadTimes on the GetCurrentThread
 /// pseudo-handle, so it must be called from the thread measured; time spent blocked, e.g. in a vsync or frame-cap
 /// wait, is not counted). The accounting advances in scheduler ticks (about 15.6 ms): use it over hundreds of
 /// frames. −1 on failure.
 /// </summary>
        public static long CurrentThreadCpu100ns()
        {
            try
            {
                long c, e, k, u;
                return GetThreadTimes(GetCurrentThread(), out c, out e, out k, out u) ? k + u : -1;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>Kernel + user CPU time of the whole process in 100-ns units (every thread, the render thread included); −1 on failure.</summary>
        public static long ProcessCpu100ns()
        {
            try
            {
                long c, e, k, u;
                return GetProcessTimes(GetCurrentProcess(), out c, out e, out k, out u) ? k + u : -1;
            }
            catch (Exception) { return -1; }
        }

        public static uint ProcessPriorityClass()
        {
            try { return GetPriorityClass(GetCurrentProcess()); }
            catch (Exception) { return 0; }
        }

        public static bool SetProcessPriorityClass(uint priorityClass, out int win32Error)
        {
            win32Error = 0;
            try
            {
                if (SetPriorityClass(GetCurrentProcess(), priorityClass)) return true;
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }
            catch (Exception) { win32Error = -1; return false; }
        }

        public static string PriorityName(uint c)
        {
            switch (c)
            {
                case IDLE_PRIORITY_CLASS: return "Idle";
                case BELOW_NORMAL_PRIORITY_CLASS: return "BelowNormal";
                case NORMAL_PRIORITY_CLASS: return "Normal";
                case ABOVE_NORMAL_PRIORITY_CLASS: return "AboveNormal";
                case HIGH_PRIORITY_CLASS: return "High";
                case REALTIME_PRIORITY_CLASS: return "Realtime";
                case 0: return "unknown";
                default: return "0x" + c.ToString("x");
            }
        }

        public static string Hwnd(IntPtr h) { return "0x" + h.ToInt64().ToString("x"); }
    }

    /// <summary>
 /// ITaskbarList:DeleteTab, the fallback of ARCHITECTURE §5.1 when the restyle leaves a taskbar button. Called
 /// through the COM vtable with CoCreateInstance and function pointers (no runtime COM interop, which Unity's Mono
 /// may lack): IUnknown 0-2, ITaskbarList HrInit 3, AddTab 4, DeleteTab 5. Main thread only; COM is initialised on the
 /// thread as apartment-threaded when it is not yet (S_FALSE and RPC_E_CHANGED_MODE are both usable).
 /// </summary>
    internal static class TaskbarTab
    {
        private static readonly Guid ClsidTaskbarList = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
        private static readonly Guid IidTaskbarList = new Guid("56FDF342-FD6D-11d0-958A-006097C9A090");
        private const uint CLSCTX_INPROC_SERVER = 1;
        private const uint COINIT_APARTMENTTHREADED = 2;

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int HrInitFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int TabFn(IntPtr self, IntPtr hwnd);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseFn(IntPtr self);

        /// <summary>HrInit then DeleteTab(hwnd). Returns a short description of each HRESULT.</summary>
        public static bool DeleteTab(IntPtr hwnd, out string result)
        {
            IntPtr p = IntPtr.Zero;
            try
            {
                int init = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
                Guid clsid = ClsidTaskbarList, iid = IidTaskbarList;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out p);
                if (hr < 0 || p == IntPtr.Zero)
                {
                    result = "CoInitializeEx=0x" + init.ToString("x8") + " CoCreateInstance(TaskbarList)=0x" + hr.ToString("x8");
                    return false;
                }
                IntPtr vtbl = Marshal.ReadIntPtr(p);
                var hrInit = (HrInitFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size), typeof(HrInitFn));
                var deleteTab = (TabFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, 5 * IntPtr.Size), typeof(TabFn));
                int h1 = hrInit(p);
                int h2 = h1 >= 0 ? deleteTab(p, hwnd) : h1;
                result = "CoInitializeEx=0x" + init.ToString("x8") + " HrInit=0x" + h1.ToString("x8") + " DeleteTab=0x" + h2.ToString("x8");
                return h1 >= 0 && h2 >= 0;
            }
            catch (Exception e)
            {
                result = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (p != IntPtr.Zero)
                    {
                        IntPtr vtbl = Marshal.ReadIntPtr(p);
                        var release = (ReleaseFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size), typeof(ReleaseFn));
                        release(p);
                    }
                }
                catch (Exception) { }
            }
        }
    }
}
