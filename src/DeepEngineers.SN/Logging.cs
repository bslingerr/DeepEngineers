using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Logging;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Logging helpers (ROADMAP §3 rule 6): every line starts with a `DE.` tag (DE.Hello, DE.Bridge, DE.Link,
 /// DE.Phase, DE.WireSelfTest, DE.Control, DE.Lifecycle,...) and goes to the BepInEx log (LogOutput.log). In bridge
 /// mode the same lines also go to a structured JSONL file in %LOCALAPPDATA%\DeepEngineers\logs\sn-&lt;timestamp&gt;.jsonl.
 /// A dormant session writes no file (vanilla Subnautica). Never throws.
 /// </summary>
    internal static class DeLog
    {
        private const int EarlyLinesMax = 64;

        private static ManualLogSource _log;
        /// <summary>Guards the JSONL writer and the record build pass: the camera trace writer appends from its own thread.</summary>
        private static readonly object JsonlLock = new object();
        private static StreamWriter _jsonl;
        private static bool _jsonlDisabled;
        private static readonly List<string> Early = new List<string>();
        private static readonly HashSet<string> OnceKeys = new HashSet<string>();
        private static readonly StringBuilder Sb = new StringBuilder(256);

        /// <summary>Frame counter stamped into JSONL records (set by the bridge each frame).</summary>
        public static ulong Frame;

        public static string JsonlPath { get; private set; }

        public static void Init(ManualLogSource log)
        {
            _log = log;
        }

        public static void Info(string line) { Write(LogLevel.Info, line); }
        public static void Warn(string line) { Write(LogLevel.Warning, line); }
        public static void Error(string line) { Write(LogLevel.Error, line); }

        /// <summary>Logs a line only the first time <paramref name="key"/> is seen.</summary>
        public static void WarnOnce(string key, string line)
        {
            if (OnceKeys.Add(key)) Write(LogLevel.Warning, line);
        }

        private static void Write(LogLevel level, string line)
        {
            if (line == null) line = "";
            try
            {
                if (_log != null) _log.Log(level, line);
                else UnityEngine.Debug.Log("[DeepEngineers.SN] " + line);
            }
            catch (Exception)
            {
                // logging must never take the game down
            }
            WriteJsonl(level, line);
        }

        /// <summary>Starts the JSONL file (bridge mode only) and flushes the lines logged before it.</summary>
        public static void EnableJsonl()
        {
            lock (JsonlLock) EnableJsonlLocked();
        }

        private static void EnableJsonlLocked()
        {
            if (_jsonl != null || _jsonlDisabled) return;
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("LocalApplicationData is empty");
                string dir = Path.Combine(Path.Combine(root, "DeepEngineers"), "logs");
                Directory.CreateDirectory(dir);
                string name = "sn-" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "-" +
                              Win32.CurrentProcessId().ToString(CultureInfo.InvariantCulture) + ".jsonl";
                JsonlPath = Path.Combine(dir, name);
                var fs = new FileStream(JsonlPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _jsonl = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                foreach (string early in Early) _jsonl.WriteLine(early);
                Early.Clear();
            }
            catch (Exception e)
            {
                _jsonlDisabled = true;
                _jsonl = null;
                Early.Clear();
                try { if (_log != null) _log.Log(LogLevel.Warning, "DE.Log JSONL disabled: " + e.GetType().Name + ": " + e.Message); } catch (Exception) { }
            }
        }

        /// <summary>Dormant session: drop the buffered lines and never write a file.</summary>
        public static void DisableJsonl()
        {
            lock (JsonlLock)
            {
                _jsonlDisabled = true;
                Early.Clear();
            }
        }

        public static void CloseJsonl()
        {
            lock (JsonlLock)
            {
                StreamWriter w = _jsonl;
                _jsonl = null;
                _jsonlDisabled = true;
                if (w == null) return;
                try { w.Dispose(); } catch (Exception) { }
            }
        }

        private static void WriteJsonl(LogLevel level, string line)
        {
            lock (JsonlLock)
            {
                if (_jsonlDisabled) return;
                try
                {
                    string record = Record(level, line);
                    if (_jsonl != null) _jsonl.WriteLine(record);
                    else if (Early.Count < EarlyLinesMax) Early.Add(record);
                }
                catch (Exception)
                {
                    _jsonlDisabled = true;
                    _jsonl = null;
                }
            }
        }

        /// <summary>
 /// Appends complete JSONL lines (each ending in a line feed) in one write, to the JSONL only (not the BepInEx
 /// log): the batched camera trace. Any thread. Dropped when the JSONL is not open.
 /// </summary>
        public static void AppendJsonlBatch(StringBuilder lines)
        {
            if (lines == null || lines.Length == 0) return;
            lock (JsonlLock)
            {
                if (_jsonlDisabled || _jsonl == null) return;
                try
                {
                    int n = lines.Length;
                    if (_batchChars == null || _batchChars.Length < n) _batchChars = new char[Math.Max(n, 64 * 1024)];
                    lines.CopyTo(0, _batchChars, 0, n);
                    _jsonl.Write(_batchChars, 0, n);
                    _jsonl.Flush();
                }
                catch (Exception)
                {
                    _jsonlDisabled = true;
                    _jsonl = null;
                }
            }
        }

        private static char[] _batchChars;

        private static string Record(LogLevel level, string line)
        {
            Sb.Length = 0;
            Sb.Append("{\"t\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            Sb.Append("\",\"tickMs\":").Append(Win32.TickCount64().ToString(CultureInfo.InvariantCulture));
            Sb.Append(",\"frame\":").Append(Frame.ToString(CultureInfo.InvariantCulture));
            Sb.Append(",\"level\":\"").Append(level == LogLevel.Error ? "error" : level == LogLevel.Warning ? "warn" : "info");
            Sb.Append("\",\"event\":");
            int sp = line.IndexOf(' ');
            Json.AppendString(Sb, sp > 0 ? line.Substring(0, sp) : line);
            Sb.Append(",\"text\":");
            Json.AppendString(Sb, line);
            Sb.Append('}');
            return Sb.ToString();
        }

        /// <summary>Invariant formatting helpers for log lines.</summary>
        public static string F(double v, string format = "0.0e+0")
        {
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        public static string D(long v) { return v.ToString(CultureInfo.InvariantCulture); }
        public static string D(ulong v) { return v.ToString(CultureInfo.InvariantCulture); }
        public static string Hex(ulong v) { return "0x" + v.ToString("x", CultureInfo.InvariantCulture); }
    }
}
