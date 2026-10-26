using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Plugin logging. Every line goes to the SE game log (<c>SpaceEngineers_&lt;timestamp&gt;.log</c>, through
 /// <see cref="GameSink"/>, bound to <c>MyLog.Default</c> by the plugin) and, as a JSON event, to our structured log
 /// <c>%LOCALAPPDATA%\DeepEngineers\logs\se-&lt;timestamp&gt;.jsonl</c> (ROADMAP §3 rule 6, §5.6). Lines start with
 /// <c>DE.</c> so the checks can grep them. Never throws.
 /// </summary>
    internal static class DeLog
    {
        /// <summary>Writes one line into the game log. Null (tests): System.Diagnostics.Trace.</summary>
        public static Action<string> GameSink;

        public static void Info(string line)
        {
            try
            {
                Action<string> sink = GameSink;
                if (sink != null) sink(line);
                else Trace.WriteLine(line);
            }
            catch (Exception) { }
            Jsonl.Event("log", line);
        }
    }

    /// <summary>
 /// The structured JSONL log (one JSON object per line, UTF-8, LF). Opened once at Init; written from the update
 /// thread (1 sample per second plus events) and, rarely, from the exit prefix; flushed after every line because
 /// Pulsar kills the process on exit (no Dispose). Per-tick test records (<c>c1trace</c>, <c>wm3</c>) go through
 /// <see cref="WriteLineBuffered"/> instead: kept in the writer's buffer and flushed at most once per second by
 /// <see cref="FlushIfDue"/> (plugin Update), by the next ordinary line, and by the exit prefix.
 /// Disables itself on the first I/O error.
 /// </summary>
    internal static class Jsonl
    {
        public const ulong BufferedFlushMs = 1000;

        private static readonly object Lock = new object();
        private static StreamWriter _writer;
        private static string _error;
        private static bool _dirty;
        private static ulong _nextFlushMs;

        public static string Path { get; private set; }
        public static string Error { get { return _error; } }

        public static void Open(DateTime startLocal)
        {
            lock (Lock)
            {
                if (_writer != null) return;
                try
                {
                    string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string dir = System.IO.Path.Combine(root, "DeepEngineers", "logs");
                    Directory.CreateDirectory(dir);
                    string path = System.IO.Path.Combine(dir, "se-" + startLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
                    var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                    Path = path;
                }
                catch (Exception e)
                {
                    _error = e.GetType().Name + ": " + e.Message;
                    _writer = null;
                }
            }
        }

        /// <summary>Appends one complete JSON object (no line break inside) and flushes.</summary>
        public static void WriteLine(string json)
        {
            Write(json, true);
        }

        /// <summary>Appends one complete JSON object without flushing (per-tick test records; see the class remarks).</summary>
        public static void WriteLineBuffered(string json)
        {
            Write(json, false);
        }

        /// <summary>
 /// As <see cref="WriteLineBuffered(string)"/> from a caller-owned buffer.
 /// </summary>
        public static void WriteLineBuffered(char[] json, int count)
        {
            if (json == null || count <= 0) return;
            lock (Lock)
            {
                if (_writer == null) return;
                try
                {
                    _writer.Write(json, 0, Math.Min(count, json.Length));
                    _writer.Write('\n');
                    _dirty = true;
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
        }

        /// <summary>Plugin Update: flushes buffered lines at most once per <see cref="BufferedFlushMs"/>.</summary>
        public static void FlushIfDue(ulong nowMs)
        {
            if (!_dirty || nowMs < _nextFlushMs) return;
            Flush();
            _nextFlushMs = nowMs + BufferedFlushMs;
        }

        /// <summary>Flushes now (exit prefix, world unload).</summary>
        public static void Flush()
        {
            lock (Lock)
            {
                if (_writer == null || !_dirty) return;
                try
                {
                    _writer.Flush();
                    _dirty = false;
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
        }

        private static void Write(string json, bool flush)
        {
            lock (Lock)
            {
                if (_writer == null) return;
                try
                {
                    _writer.Write(json);
                    _writer.Write('\n');
                    if (flush)
                    {
                        _writer.Flush();
                        _dirty = false;
                    }
                    else _dirty = true;
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
        }

        private static void Fail(Exception e)
        {
            _error = e.GetType().Name + ": " + e.Message;
            try { _writer.Dispose(); } catch (Exception) { }
            _writer = null;
        }

        public static void Event(string kind, string message)
        {
            if (_writer == null) return;
            var sb = new StringBuilder(64 + (message == null ? 0 : message.Length));
            sb.Append("{\"t\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":");
            JsonText.AppendString(sb, kind);
            sb.Append(",\"msg\":");
            JsonText.AppendString(sb, message ?? "");
            sb.Append('}');
            WriteLine(sb.ToString());
        }
    }

    /// <summary>"At most once per interval" for repeated log lines (DE.Link WAIT: at most once per 10 s, S0.N7).</summary>
    internal struct RateLimit
    {
        private ulong _nextMs;
        private ulong _intervalMs;
        private bool _started;

        /// <summary>Calls refused since the last allowed one.</summary>
        public int Suppressed;

        public RateLimit(ulong intervalMs)
        {
            _intervalMs = intervalMs; _nextMs = 0; _started = false; Suppressed = 0;
        }

        public bool Allow(ulong nowMs)
        {
            if (!_started || nowMs >= _nextMs)
            {
                _started = true;
                _nextMs = nowMs + _intervalMs;
                return true;
            }
            Suppressed++;
            return false;
        }

        /// <summary>Returns and clears the suppressed count (for "(n suppressed)" suffixes).</summary>
        public int TakeSuppressed()
        {
            int n = Suppressed;
            Suppressed = 0;
            return n;
        }

        public void Reset() { _started = false; Suppressed = 0; }
    }
}
