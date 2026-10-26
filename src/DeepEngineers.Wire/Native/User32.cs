using System;
using System.Runtime.InteropServices;

namespace DeepEngineers.Wire.Native
{
    /// <summary>Raw user32 P/Invoke used by the lifecycle (CONTRACT §7, ROADMAP S0 work items).</summary>
    internal static class User32
    {
        /// <summary>ASFW_ANY: any process may set the foreground window.</summary>
        public const uint ASFW_ANY = 0xFFFFFFFF;

        [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern void DisableProcessWindowsGhosting();
    }
}
