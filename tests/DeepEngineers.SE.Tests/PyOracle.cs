using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// Runs the independent Python oracles (python -I, stdlib only): the repository root is found upwards from the test
 /// assembly (the folder holding DeepEngineers.sln); Python is DEEPENG_PYTHON, else
 /// %LOCALAPPDATA%\Programs\Python\Python310\python.exe, else "python" on PATH (as tests/DeepEngineers.WireMath.Tests).
 /// </summary>
    internal static class PyOracle
    {
        private static string _root;

        public static string Root
        {
            get
            {
                if (_root != null) return _root;
                string dir = Path.GetDirectoryName(typeof(PyOracle).Assembly.Location);
                while (dir != null && !File.Exists(Path.Combine(dir, "DeepEngineers.sln"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) throw new InvalidOperationException("DeepEngineers.sln not found above " + typeof(PyOracle).Assembly.Location);
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

        /// <summary><c>python -I &lt;args&gt;</c> in the repository root; stdout, or an exception with stderr on a non-zero exit.</summary>
        public static string Run(params string[] args)
        {
            var sb = new StringBuilder("-I");
            foreach (string a in args) sb.Append(' ').Append(a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a);
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
                var err = p.StandardError.ReadToEndAsync();
                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("python " + sb); }
                if (p.ExitCode != 0) throw new InvalidOperationException("python " + sb + " exited " + p.ExitCode + "\n" + stdout + "\n" + err.Result);
                return stdout;
            }
        }
    }
}
