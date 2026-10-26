using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Raw user32 / dwmapi / gdi32 calls of the S2 display (ARCHITECTURE §5.1). Game-independent. In the plugin every call
 /// the
 /// only cross-process window call is <c>SetWindowPos(snHwnd, seHwnd, …, SWP_ASYNCWINDOWPOS)</c>, which posts and never
 /// blocks. Constants from the Windows SDK headers (winuser.h, dwmapi.h).
 /// </summary>
    internal static class NativeWin
    {
        public const uint GW_HWNDNEXT = 2, GW_OWNER = 4;
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOPMOST = 0x00000008;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
            SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;
        /// <summary>DWMWA_CLOAKED (dwmapi.h DWMWINDOWATTRIBUTE 14): non-zero when the window is cloaked (other virtual
 /// desktop, UWP frame, shell-hidden).</summary>
        public const int DWMWA_CLOAKED = 14;
        public const uint CLR_INVALID = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("dwmapi.dll", ExactSpelling = true)]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [DllImport("dwmapi.dll", ExactSpelling = true)]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "GetClassNameW")]
        public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll", ExactSpelling = true, EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern uint GetPixel(IntPtr hdc, int x, int y);

        public static bool IsCloaked(IntPtr hwnd)
        {
            int v;
            try { return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out v, 4) == 0 && v != 0; }
            catch (Exception) { return false; }
        }

        public static string ClassName(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(128);
                int n = GetClassName(hwnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : "?";
            }
            catch (Exception) { return "?"; }
        }

        public static uint ProcessOf(IntPtr hwnd)
        {
            uint pid;
            try { GetWindowThreadProcessId(hwnd, out pid); return pid; }
            catch (Exception) { return 0; }
        }

        public static string Hex(IntPtr h) { return "0x" + h.ToInt64().ToString("x"); }

        public static string Hr(int hr) { return "0x" + hr.ToString("x8"); }
    }
}
