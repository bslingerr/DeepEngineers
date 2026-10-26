using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// Assembly set-up: BepInEx.dll and UnityEngine.CoreModule.dll are resolved from the install folders recorded at
 /// build time (AssemblyMetadata "DeepEngineers.ResolveDir"), never copied into the repository or the output. The
 /// SN log goes to a BepInEx ManualLogSource captured in <see cref="SnRig.Logged"/>; no JSONL file is written.
 /// </summary>
    [TestClass]
    public static class AssemblySetup
    {
        private static readonly List<string> Dirs = new List<string>();

        [AssemblyInitialize]
        public static void Init(TestContext ctx)
        {
            foreach (AssemblyMetadataAttribute a in typeof(AssemblySetup).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                if (a.Key == "DeepEngineers.ResolveDir" && !string.IsNullOrEmpty(a.Value)) Dirs.Add(a.Value);
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            InitLog(); // separate method: its JIT needs BepInEx, which only resolves once the handler is registered
        }

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string simple = new AssemblyName(e.Name).Name;
            foreach (string d in Dirs)
            {
                string p = Path.Combine(d, simple + ".dll");
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InitLog()
        {
            var src = new BepInEx.Logging.ManualLogSource("DeepEngineers.SN");
            src.LogEvent += (s, e) => { lock (SnRig.Logged) SnRig.Logged.Add(e.Data.ToString()); };
            DeLog.Init(src);
            DeLog.DisableJsonl(); // never write into %LOCALAPPDATA% from the tests
        }
    }

    internal static class SnRig
    {
        public static readonly List<string> Logged = new List<string>();

        public static bool Has(string contains)
        {
            lock (Logged) foreach (string s in Logged) if (s.Contains(contains)) return true;
            return false;
        }

        /// <summary>A private section name (never the CONTRACT one).</summary>
        public static string Name(string tag)
        {
            return @"Local\DeepEngineers_sntest_" + Win32.CurrentProcessId() + "_" + tag + "_" + Guid.NewGuid().ToString("N");
        }

        /// <summary>The PID of a process that has just exited (nobody holds it any more).</summary>
        public static uint DeadPid()
        {
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false }))
            {
                p.WaitForExit();
                return (uint)p.Id;
            }
        }

        public static Process StartLiveChild(int seconds = 60)
        {
            return Process.Start(new ProcessStartInfo("ping.exe", "-n " + seconds + " 127.0.0.1")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            });
        }

        public static void Kill(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
            try { p.WaitForExit(10000); } catch (Exception) { }
        }
    }
}
