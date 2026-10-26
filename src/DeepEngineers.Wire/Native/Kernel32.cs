using System;
using System.Runtime.InteropServices;

namespace DeepEngineers.Wire.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    /// <summary>PROCESS_POWER_THROTTLING_STATE (processthreadsapi.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    /// <summary>
 /// Raw kernel32 P/Invoke. Used on both sides instead of System.IO.MemoryMappedFiles (CONTRACT §5: no.NET
 /// MemoryMappedFile; P/Invoke behaves the same on.NET Framework 4.8 and Unity's Mono). Nothing here blocks:
 /// WaitForSingleObject is only ever called with a 0 ms timeout.
 /// </summary>
    internal static class Kernel32
    {
        public const uint PAGE_READWRITE = 0x04;
        public const uint FILE_MAP_WRITE = 0x0002;
        public const uint FILE_MAP_READ = 0x0004;
        public const uint FILE_MAP_ALL_ACCESS = 0xF001F;
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint MUTEX_MODIFY_STATE = 0x0001;
        public const uint WAIT_OBJECT_0 = 0x00000000;
        public const uint WAIT_ABANDONED = 0x00000080;
        public const uint WAIT_TIMEOUT = 0x00000102;
        public const uint WAIT_FAILED = 0xFFFFFFFF;

        public const int ERROR_FILE_NOT_FOUND = 2;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_INVALID_HANDLE = 6;
        public const int ERROR_INVALID_PARAMETER = 87;
        public const int ERROR_ALREADY_EXISTS = 183;

        /// <summary>PROCESS_INFORMATION_CLASS.ProcessPowerThrottling (processthreadsapi.h).</summary>
        public const int ProcessPowerThrottling = 4;
        public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        public const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        /// <summary>GetCurrentProcess pseudo-handle.</summary>
        public static readonly IntPtr CurrentProcess = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern IntPtr CreateFileMappingW(IntPtr hFile, ref SECURITY_ATTRIBUTES lpAttributes, uint flProtect,
            uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
            uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern IntPtr CreateMutexW(IntPtr lpMutexAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern IntPtr OpenMutexW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseMutex(IntPtr hMutex);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern ulong GetTickCount64();

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryPerformanceFrequency(out long lpFrequency);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr LocalFree(IntPtr hMem);
    }
}
