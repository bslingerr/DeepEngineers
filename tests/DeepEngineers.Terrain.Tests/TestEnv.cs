using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>Repository paths, the user's octree folder (read-only), the Python oracle runner, the derived-data folder.</summary>
    internal static class TestEnv
    {
        private static readonly object Gate = new object();
        private static string _root;
        private static SnOctreeSource _install;

        public static string Root
        {
            get
            {
                if (_root != null) return _root;
                string dir = Path.GetDirectoryName(typeof(TestEnv).Assembly.Location);
                while (dir != null && !File.Exists(Path.Combine(dir, "DeepEngineers.sln"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) throw new InvalidOperationException("DeepEngineers.sln not found above " + typeof(TestEnv).Assembly.Location);
                _root = dir;
                return dir;
            }
        }

        public static string PathIn(params string[] parts)
        {
            string p = Root;
            foreach (string part in parts) p = Path.Combine(p, part);
            return p;
        }

        public static string SubnauticaDir { get { return SnSourceManifest.ResolveSubnauticaDir(null); } }

        public static string OctreeDir { get { return SnSourceManifest.OctreeDirOf(SubnauticaDir); } }

        public static bool InstallPresent
        {
            get { return Directory.Exists(OctreeDir) && File.Exists(Path.Combine(OctreeDir, SnOctreeFormat.BatchFileName(12, 18, 12))); }
        }

        /// <summary>Marks the test Inconclusive when the user's CompiledOctreesCache is not on this machine.</summary>
        public static void RequireInstall()
        {
            if (!InstallPresent)
                Assert.Inconclusive("Subnautica's CompiledOctreesCache not found at " + OctreeDir +
                                    " (set DEEPENG_SUBNAUTICA to the Subnautica folder); the offline S4 oracles need the user's own files.");
        }

        /// <summary>One shared source over the install for the whole run (released at assembly cleanup).</summary>
        public static SnOctreeSource Install
        {
            get
            {
                RequireInstall();
                lock (Gate)
                {
                    if (_install == null)
                    {
                        string err;
                        _install = SnOctreeSource.Open(OctreeDir, out err);
                        if (_install == null) Assert.Inconclusive("cannot open " + OctreeDir + ": " + err);
                    }
                    return _install;
                }
            }
        }

        public static void ReleaseInstall()
        {
            lock (Gate)
            {
                if (_install != null) _install.Release();
                _install = null;
            }
        }

        /// <summary>Derived A-T0 data (outside the repository): DEEPENG_AT0_DIR, else %LOCALAPPDATA%\DeepEngineers\s4\at0.</summary>
        public static string At0Dir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("DEEPENG_AT0_DIR");
                string dir = !string.IsNullOrEmpty(env)
                    ? env
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepEngineers", "s4", "at0");
                string full = Path.GetFullPath(dir);
                string repo = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(repo, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("DEEPENG_AT0_DIR must be outside the repository (derived game data): " + full);
                Directory.CreateDirectory(full);
                return full;
            }
        }

        /// <summary>Python 3.10: DEEPENG_PYTHON, else %LOCALAPPDATA%\Programs\Python\Python310\python.exe, else "python".</summary>
        public static string Python
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("DEEPENG_PYTHON");
                if (!string.IsNullOrEmpty(env)) return env;
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (!string.IsNullOrEmpty(local))
                {
                    string p = Path.Combine(local, "Programs", "Python", "Python310", "python.exe");
                    if (File.Exists(p)) return p;
                }
                return "python";
            }
        }

        /// <summary>Runs <c>python -I &lt;args&gt;</c> in the repo root; returns stdout; throws with stderr on a non-zero exit.</summary>
        public static string RunPython(params string[] args)
        {
            var sb = new StringBuilder("-I");
            foreach (string a in args) sb.Append(' ').Append(Quote(a));
            var psi = new ProcessStartInfo(Python, sb.ToString())
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            using (Process p = Process.Start(psi))
            {
                var stderrTask = p.StandardError.ReadToEndAsync();
                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(900000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("python " + sb); }
                string stderr = stderrTask.Result;
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("python " + sb + " exited " + p.ExitCode + "\n" + stdout + "\n" + stderr);
                return stdout;
            }
        }

        private static string Quote(string a)
        {
            return a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
        }

        /// <summary>A fresh temporary folder (outside the repository) for synthetic batch files.</summary>
        public static string NewTempDir(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(), "deepeng-s4-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        public static void DeleteDir(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestClass]
    public static class AssemblyHooks
    {
        [AssemblyInitialize]
        public static void Init(TestContext ctx)
        {
            TerrainLog.Sink = line => Console.WriteLine(line);
        }

        [AssemblyCleanup]
        public static void Cleanup()
        {
            TerrainMutants.ClearAll();
            TestEnv.ReleaseInstall();
        }
    }
}
