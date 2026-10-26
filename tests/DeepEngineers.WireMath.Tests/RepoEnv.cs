using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>Repository paths, the Python oracle runner and the on-the-fly WM-1 file generation.</summary>
    internal static class RepoEnv
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Generated = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string _root;

        /// <summary>The repository root (the folder holding DeepEngineers.sln), found upwards from the test assembly.</summary>
        public static string Root
        {
            get
            {
                if (_root != null) return _root;
                string dir = Path.GetDirectoryName(typeof(RepoEnv).Assembly.Location);
                while (dir != null && !File.Exists(Path.Combine(dir, "DeepEngineers.sln"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) throw new InvalidOperationException("DeepEngineers.sln not found above " + typeof(RepoEnv).Assembly.Location);
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

        /// <summary>Python 3.10 (stdlib only): DEEPENG_PYTHON, else %LOCALAPPDATA%\Programs\Python\Python310\python.exe, else "python" on PATH.</summary>
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
                if (!p.WaitForExit(600000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("python " + sb); }
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

        /// <summary>
 /// The WM-1 file for a site ("v1" or "example"): DEEPENG_WM1_DIR/wm1-&lt;site&gt;.jsonl when that variable is set,
 /// else generated once per test run with <c>python -I tools/worldmap/wire_ref.py vectors tests/out/wm1/wm1-&lt;site&gt;.jsonl --site &lt;site&gt;</c>.
 /// </summary>
        public static string Wm1File(string site)
        {
            lock (Gate)
            {
                string path;
                if (Generated.TryGetValue(site, out path)) return path;
                string pre = Environment.GetEnvironmentVariable("DEEPENG_WM1_DIR");
                if (!string.IsNullOrEmpty(pre))
                {
                    path = Path.Combine(pre, "wm1-" + site + ".jsonl");
                    if (!File.Exists(path)) throw new FileNotFoundException("DEEPENG_WM1_DIR set but file missing", path);
                    Console.WriteLine("WM-1 " + site + ": using pre-generated " + path);
                }
                else
                {
                    string dir = PathIn("tests", "out", "wm1");
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, "wm1-" + site + ".jsonl");
                    string tmp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
                    string output = RunPython(Path.Combine("tools", "worldmap", "wire_ref.py"), "vectors", tmp, "--site", site);
                    File.Copy(tmp, path, true);
                    File.Delete(tmp);
                    Console.WriteLine("WM-1 " + site + ": " + output.Trim());
                }
                Console.WriteLine("WM-1 " + site + ": sha256 " + Sha256File(path));
                Generated[site] = path;
                return path;
            }
        }

        public static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(fs);
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
