using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S8 (ROADMAP S8.N3, the minimum: "counters in the existing per-second rows so a 60-minute run can be judged"): the
 /// process working set and private bytes (<c>K32GetProcessMemoryInfo</c>, kernel32, Windows 7+), the CLR heap
 /// (<c>GC.GetTotalMemory(false)</c>, the managed part of SE's STATISTICS <c>GCMemoryUsed</c>), the collection counts and
 /// the machine's memory load and commit charge (<c>GlobalMemoryStatusEx</c>). Appended to the 1-second <c>sample</c>
 /// row by <see cref="PerfLog"/>; one call per second, no allocation but the row's own. Never throws (a failed call
 /// leaves its column 0 / null).
 /// </summary>
    internal static class S8Memory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        /// <summary>Appends the memory columns (see <see cref="S8Rules.AppendMemory"/>) to a JSON object being built.</summary>
        public static void Append(StringBuilder sb)
        {
            long ws = 0, priv = 0, gc = 0;
            int g0 = 0, g1 = 0, g2 = 0;
            uint load = 0;
            double commit = double.NaN;
            try
            {
                ProcessMemoryCountersEx c;
                if (K32GetProcessMemoryInfo(GetCurrentProcess(), out c, (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx))))
                {
                    ws = (long)c.WorkingSetSize.ToUInt64();
                    priv = (long)c.PrivateUsage.ToUInt64();
                }
            }
            catch (Exception) { }
            try
            {
                gc = GC.GetTotalMemory(false);
                g0 = GC.CollectionCount(0);
                g1 = GC.CollectionCount(1);
                g2 = GC.CollectionCount(2);
            }
            catch (Exception) { }
            try
            {
                var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (GlobalMemoryStatusEx(ref m))
                {
                    load = m.dwMemoryLoad;
                    commit = S8Rules.CommitPct(m.ullTotalPageFile, m.ullAvailPageFile);
                }
            }
            catch (Exception) { }
            S8Rules.AppendMemory(sb, ws, priv, gc, g0, g1, g2, load, commit);
        }
    }
}
