using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeepEngineers.Wire.Native
{
    /// <summary>Result of a non-blocking process query.</summary>
    public enum ProcessState
    {
        /// <summary>No process with that id (OpenProcess: ERROR_INVALID_PARAMETER) or id 0.</summary>
        NotFound = 0,
        Alive = 1,
        /// <summary>The process object still exists (someone holds a handle) but has exited.</summary>
        Exited = 2,
        /// <summary>A process exists but cannot be opened (other user or higher integrity).</summary>
        AccessDenied = 3,
        Error = 4,
    }

    /// <summary>Presence of a named kernel object of any type in the session namespace.</summary>
    public enum NamedObjectState
    {
        Absent = 0,
        Present = 1,
        Error = 2,
    }

    /// <summary>Outcome of NamedMutex.TryAcquire.</summary>
    public enum MutexAcquire
    {
        Acquired = 0,
        /// </summary>
        AcquiredAbandoned = 1,
        /// <summary>Another instance holds it: log `DE.Link REFUSE second instance` and stay dormant.</summary>
        HeldElsewhere = 2,
        Error = 3,
    }

    /// <summary>Outcome of the power-throttling opt-out (logged by both plugins).</summary>
    public struct PowerThrottlingResult
    {
        public bool Ok;
        /// <summary>The ControlMask that was accepted (EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION, or EXECUTION_SPEED alone).</summary>
        public uint ControlMask;
        public int Win32Error;
        public string Detail;

        public override string ToString()
        {
            return Ok ? "ok controlMask=0x" + ControlMask.ToString("x") : "failed error=" + Win32Error + (Detail != null ? " " + Detail : "");
        }
    }

    /// <summary>
 /// Public, non-blocking wrappers over the native calls the bridge needs (CONTRACT §5-§7, ROADMAP S0 Wire work
 /// items). None of them throws on an ordinary failure; each reports the Win32 error instead.
 /// </summary>
    public static class Win32
    {
        private static long _qpcFrequency;

        public static uint CurrentProcessId() { return Kernel32.GetCurrentProcessId(); }

        public static uint CurrentThreadId() { return Kernel32.GetCurrentThreadId(); }

        /// <summary>GetTickCount64: the heartbeat clock (ms).</summary>
        public static ulong TickCount64() { return Kernel32.GetTickCount64(); }

        public static long QpcNow()
        {
            long v;
            Kernel32.QueryPerformanceCounter(out v);
            return v;
        }

        /// <summary>QueryPerformanceFrequency (constant per boot; cached).</summary>
        public static long QpcFrequency()
        {
            long f = _qpcFrequency;
            if (f == 0)
            {
                Kernel32.QueryPerformanceFrequency(out f);
                _qpcFrequency = f;
            }
            return f;
        }

        /// <summary>OpenProcess(SYNCHRONIZE) + WaitForSingleObject(h, 0). Never blocks.</summary>
        public static ProcessState QueryProcess(uint pid)
        {
            if (pid == 0) return ProcessState.NotFound;
            IntPtr h = Kernel32.OpenProcess(Kernel32.SYNCHRONIZE, false, pid);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == Kernel32.ERROR_INVALID_PARAMETER) return ProcessState.NotFound;
                if (err == Kernel32.ERROR_ACCESS_DENIED) return ProcessState.AccessDenied;
                return ProcessState.Error;
            }
            try
            {
                uint w = Kernel32.WaitForSingleObject(h, 0);
                if (w == Kernel32.WAIT_TIMEOUT) return ProcessState.Alive;
                if (w == Kernel32.WAIT_OBJECT_0) return ProcessState.Exited;
                return ProcessState.Error;
            }
            finally { Kernel32.CloseHandle(h); }
        }

        /// <summary>
 /// Conservative liveness for the creator rule (CONTRACT §6 rule 2): Alive, AccessDenied and Error count as
 /// alive (never take over a section a live process may hold); NotFound and Exited count as dead.
 /// </summary>
        public static bool IsProcessAlive(uint pid)
        {
            ProcessState s = QueryProcess(pid);
            return s == ProcessState.Alive || s == ProcessState.AccessDenied || s == ProcessState.Error;
        }

        /// <summary>The current user's SID string (S-1-5-21-...), read from the process token.</summary>
        public static string CurrentUserSid()
        {
            IntPtr token;
            if (!Advapi32.OpenProcessToken(Kernel32.CurrentProcess, Advapi32.TOKEN_QUERY, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
            IntPtr buffer = IntPtr.Zero;
            try
            {
                uint length;
                Advapi32.GetTokenInformation(token, Advapi32.TokenUser, IntPtr.Zero, 0, out length);
                if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(TokenUser) size");
                buffer = Marshal.AllocHGlobal((int)length);
                if (!Advapi32.GetTokenInformation(token, Advapi32.TokenUser, buffer, length, out length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(TokenUser)");
                IntPtr sid = Marshal.ReadIntPtr(buffer); // TOKEN_USER.User.Sid
                IntPtr str;
                if (!Advapi32.ConvertSidToStringSidW(sid, out str))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertSidToStringSidW");
                try { return Marshal.PtrToStringUni(str); }
                finally { Kernel32.LocalFree(str); }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                Kernel32.CloseHandle(token);
            }
        }

        /// <summary>
 /// ConvertStringSecurityDescriptorToSecurityDescriptorW; the caller frees the result with FreeSecurityDescriptor.
 /// Throws Win32Exception on a malformed SDDL.
 /// </summary>
        public static IntPtr SecurityDescriptorFromSddl(string sddl)
        {
            if (string.IsNullOrEmpty(sddl)) throw new ArgumentException("sddl");
            IntPtr sd;
            if (!Advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, Advapi32.SDDL_REVISION_1, out sd, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSecurityDescriptorToSecurityDescriptorW(" + sddl + ")");
            return sd;
        }

        public static void FreeSecurityDescriptor(IntPtr sd)
        {
            if (sd != IntPtr.Zero) Kernel32.LocalFree(sd);
        }

        /// <summary>AllowSetForegroundWindow(pid); pass 0xFFFFFFFF (ASFW_ANY) for any process.</summary>
        public static bool AllowSetForegroundWindow(uint pid, out int win32Error)
        {
            bool ok = User32.AllowSetForegroundWindow(pid);
            win32Error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        public const uint AsfwAny = User32.ASFW_ANY;

        /// <summary>DisableProcessWindowsGhosting (Subnautica, at Awake). Returns false with the exception text if unavailable.</summary>
        public static bool DisableProcessWindowsGhosting(out string error)
        {
            try { User32.DisableProcessWindowsGhosting(); error = null; return true; }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return false; }
        }

        /// <summary>
 /// Opts the current process out of power throttling: SetProcessInformation(ProcessPowerThrottling) with
 /// PROCESS_POWER_THROTTLING_STATE {Version 1, ControlMask EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION, StateMask 0}.
 /// If Windows rejects IGNORE_TIMER_RESOLUTION (before Windows 11), retries with EXECUTION_SPEED alone.
 /// </summary>
        public static PowerThrottlingResult OptOutOfPowerThrottling()
        {
            var result = new PowerThrottlingResult();
            try
            {
                uint mask = Kernel32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED | Kernel32.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
                if (TrySetThrottling(mask, out result.Win32Error)) { result.Ok = true; result.ControlMask = mask; return result; }
                int first = result.Win32Error;
                mask = Kernel32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                if (TrySetThrottling(mask, out result.Win32Error))
                {
                    result.Ok = true; result.ControlMask = mask;
                    result.Detail = "IGNORE_TIMER_RESOLUTION rejected (error " + first + ")";
                    return result;
                }
                return result;
            }
            catch (Exception e)
            {
                result.Ok = false; result.Detail = e.GetType().Name + ": " + e.Message;
                return result;
            }
        }

        private static bool TrySetThrottling(uint controlMask, out int win32Error)
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = Kernel32.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = controlMask,
                StateMask = 0,
            };
            bool ok = Kernel32.SetProcessInformation(Kernel32.CurrentProcess, Kernel32.ProcessPowerThrottling, ref state,
                (uint)Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
            win32Error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        /// <summary>
 /// Whether a named kernel object of any type exists: OpenMutexW succeeds for a mutex; ERROR_INVALID_HANDLE means the name exists as another object type;
 /// ERROR_FILE_NOT_FOUND means absent. Never creates anything.
 /// </summary>
        public static NamedObjectState QueryNamedObject(string name, out int win32Error)
        {
            IntPtr h = Kernel32.OpenMutexW(Kernel32.SYNCHRONIZE, false, name);
            if (h != IntPtr.Zero) { Kernel32.CloseHandle(h); win32Error = 0; return NamedObjectState.Present; }
            win32Error = Marshal.GetLastWin32Error();
            if (win32Error == Kernel32.ERROR_INVALID_HANDLE || win32Error == Kernel32.ERROR_ACCESS_DENIED) return NamedObjectState.Present;
            if (win32Error == Kernel32.ERROR_FILE_NOT_FOUND) return NamedObjectState.Absent;
            return NamedObjectState.Error;
        }
    }

    /// <summary>
 /// A named mutex held for the lifetime of a bridge role (`Local\DeepEngineers_v1_sn`, `Local\DeepEngineers_v1_se`).
 /// Mutex ownership is per thread: acquire and dispose on the same thread (the game's main/update thread).
 /// If the process dies the OS abandons the mutex and the next instance acquires it.
 /// </summary>
    public sealed class NamedMutex : IDisposable
    {
        private IntPtr _handle;

        public string Name { get; }
        public bool WasAbandoned { get; }

        private NamedMutex(string name, IntPtr handle, bool abandoned)
        {
            Name = name; _handle = handle; WasAbandoned = abandoned;
        }

        /// <summary>Non-blocking acquire. HeldElsewhere: another instance runs; stay dormant.</summary>
        public static MutexAcquire TryAcquire(string name, out NamedMutex mutex, out int win32Error)
        {
            mutex = null;
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name");
            IntPtr h = Kernel32.CreateMutexW(IntPtr.Zero, true, name);
            win32Error = Marshal.GetLastWin32Error();
            if (h == IntPtr.Zero) return MutexAcquire.Error;
            if (win32Error != Kernel32.ERROR_ALREADY_EXISTS)
            {
                win32Error = 0;
                mutex = new NamedMutex(name, h, false);
                return MutexAcquire.Acquired;
            }
            uint w = Kernel32.WaitForSingleObject(h, 0);
            if (w == Kernel32.WAIT_OBJECT_0 || w == Kernel32.WAIT_ABANDONED)
            {
                win32Error = 0;
                mutex = new NamedMutex(name, h, w == Kernel32.WAIT_ABANDONED);
                return w == Kernel32.WAIT_ABANDONED ? MutexAcquire.AcquiredAbandoned : MutexAcquire.Acquired;
            }
            win32Error = w == Kernel32.WAIT_FAILED ? Marshal.GetLastWin32Error() : 0;
            Kernel32.CloseHandle(h);
            return w == Kernel32.WAIT_TIMEOUT ? MutexAcquire.HeldElsewhere : MutexAcquire.Error;
        }

        public bool IsHeld { get { return _handle != IntPtr.Zero; } }

        /// <summary>Releases (on the owning thread) and closes the handle.</summary>
        public void Dispose()
        {
            IntPtr h = _handle;
            if (h == IntPtr.Zero) return;
            _handle = IntPtr.Zero;
            Kernel32.ReleaseMutex(h);
            Kernel32.CloseHandle(h);
        }
    }

    /// <summary>
 /// A SYNCHRONIZE handle on the peer process, polled with WaitForSingleObject(h, 0) (CONTRACT §7 PeerExited:
 /// "process exit polled"). Open it when the peer's PID is first seen; the handle keeps the PID from being reused
 /// while it is open.
 /// </summary>
    public sealed class ProcessWatch : IDisposable
    {
        private IntPtr _handle;

        public uint Pid { get; }

        private ProcessWatch(uint pid, IntPtr handle) { Pid = pid; _handle = handle; }

        /// <summary>Null when the process does not exist or cannot be opened (win32Error says which).</summary>
        public static ProcessWatch Open(uint pid, out int win32Error)
        {
            win32Error = 0;
            if (pid == 0) { win32Error = Kernel32.ERROR_INVALID_PARAMETER; return null; }
            IntPtr h = Kernel32.OpenProcess(Kernel32.SYNCHRONIZE, false, pid);
            if (h == IntPtr.Zero) { win32Error = Marshal.GetLastWin32Error(); return null; }
            return new ProcessWatch(pid, h);
        }

        /// <summary>True once the process has exited (non-blocking). A disposed watch reports exited.</summary>
        public bool HasExited()
        {
            IntPtr h = _handle;
            if (h == IntPtr.Zero) return true;
            return Kernel32.WaitForSingleObject(h, 0) == Kernel32.WAIT_OBJECT_0;
        }

        public void Dispose()
        {
            IntPtr h = _handle;
            if (h == IntPtr.Zero) return;
            _handle = IntPtr.Zero;
            Kernel32.CloseHandle(h);
        }
    }
}
