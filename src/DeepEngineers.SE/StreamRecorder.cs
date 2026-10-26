using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The dev-build stream recorder (<c>de.s5 record {name, c1, c2}</c> / <c>de.s5 record stop</c>; CONTRACT §5.4 "Stream
 /// recording (.derec, tests only)"): the producer hands every message after a successful ring write (and, when asked,
 /// every C1 record and C2 snapshot) to <see cref="RecordMessage"/> / <see cref="RecordRaw"/>, which copy it into a bounded
 /// queue (drops counted, never a wait); one background thread "DE.Recorder" writes
 /// <c>%LOCALAPPDATA%\DeepEngineers\recordings\&lt;name&gt;.derec</c> with Wire's <see cref="DerecWriter"/> (the format is
 /// Wire's <see cref="Derec"/>: 64 B header with the layout and conventions hashes, 24 B record headers, 8-byte padding,
 /// the end record) and on stop the <c>&lt;name&gt;.json</c> sidecar (name, formatVersion, created, the hashes, seVersion,
 /// first and last tick, counts per kind, bytes, models, grids, parts, dropped, sha256 of the file). Derived from the
 /// player's own game, never in the repository or a game folder. Game-independent (tests write into a temp folder).
 /// </summary>
    internal sealed class StreamRecorder
    {
        public const long MaxQueuedBytes = 64L << 20;
        public const int MaxQueuedRecords = 200000;

        private sealed class Item
        {
            public ushort Kind;
            public ulong Tick;
            public long Qpc;
            public byte[] Data;
            public int Count;
            public bool Stop;
            public string StopReason;
        }

        private readonly ConcurrentQueue<Item> _queue = new ConcurrentQueue<Item>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _lock = new object();
        private Thread _thread;
        private long _queuedBytes;
        private int _queuedRecords;
        private volatile bool _active;

        // session (set by the producer thread before the writer starts)
        private string _name, _path, _sidecar;
        private DerecHeader _header;

        // counters (writer thread; read racy by status)
        private long _records, _bytes, _dropped, _models, _materials, _grids, _parts, _partRemoves, _characters;
        private readonly long[] _kindCounts = new long[6];
        private ulong _firstTick, _lastTick;
        private bool _hasTick;
        private string _lastError, _lastSidecar, _lastSha;

        public bool Active { get { return _active; } }
        public bool WantC1 { get; private set; }
        public bool WantC2 { get; private set; }
        public string Name { get { return _name; } }
        public string FilePath { get { return _path; } }
        public string LastSidecar { get { return _lastSidecar; } }
        public string LastSha256 { get { return _lastSha; } }
        public string LastError { get { return _lastError; } }
        public long Dropped { get { return Interlocked.Read(ref _dropped); } }
        public long RecordsWritten { get { return Interlocked.Read(ref _records); } }

        public static string DefaultDirectory { get { return Derec.DefaultDirectory(); } }

        /// <summary>Starts a recording (producer thread); false with <paramref name="why"/> (bad name, already recording, I/O).</summary>
        public bool Start(string directory, string name, bool c1, bool c2, uint seVersion, uint worldEpoch, bool synthetic, ulong nowUnixMs, out string why)
        {
            why = null;
            lock (_lock)
            {
                if (_active) { why = "already recording " + _name + " (de.s5 record stop first)"; return false; }
                if (_thread != null && _thread.IsAlive) { why = "the previous recording " + _name + " is still being written; try again in a moment"; return false; }
                if (!Derec.IsValidName(name)) { why = "record name must be 1-64 characters of A-Z a-z 0-9 _ . - (not starting with a dot)"; return false; }
                string path, sidecar;
                try
                {
                    Directory.CreateDirectory(directory);
                    path = Derec.RecordingPath(directory, name);
                    sidecar = Derec.SidecarPath(directory, name);
                    // created here so a bad path fails the command, not the thread
                    using (new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { }
                }
                catch (Exception e) { why = "cannot create the recording in " + directory + ": " + e.GetType().Name + ": " + e.Message; return false; }
                _name = name;
                _path = path;
                _sidecar = sidecar;
                WantC1 = c1;
                WantC2 = c2;
                uint flags = (c1 ? Derec.FlagHasC1 : 0) | (c2 ? Derec.FlagHasC2 : 0) | (synthetic ? Derec.FlagSynthetic : 0);
                _header = Derec.CreateHeader(seVersion, nowUnixMs, flags, worldEpoch);
                _records = _bytes = _dropped = _models = _materials = _grids = _parts = _partRemoves = _characters = 0;
                Array.Clear(_kindCounts, 0, _kindCounts.Length);
                _hasTick = false;
                _firstTick = _lastTick = 0;
                _lastError = null;
                _lastSha = null;
                Interlocked.Exchange(ref _queuedBytes, 0);
                Interlocked.Exchange(ref _queuedRecords, 0);
                Item dummy;
                while (_queue.TryDequeue(out dummy)) { }
                _active = true;
                _thread = new Thread(Run) { Name = "DE.Recorder", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                _thread.Start();
                return true;
            }
        }

        /// <summary>Stops: the writer thread finishes the queue, writes the end record and the sidecar. Producer thread.</summary>
        public void Stop(string reason)
        {
            lock (_lock)
            {
                if (!_active) return;
                _active = false;
                _queue.Enqueue(new Item { Stop = true, StopReason = reason ?? "stop" });
                _wake.Set();
            }
        }

        /// <summary>Waits for the writer to finish (tests; never on a game thread).</summary>
        public bool Join(int timeoutMs)
        {
            Thread t = _thread;
            return t == null || t.Join(timeoutMs);
        }

        /// <summary>A ring message (C3 or C4) as written: {u32 type, u32 payloadBytes} + payload. Never blocks; drops when the queue is full.</summary>
        public void RecordMessage(ushort kind, uint type, byte[] payload, int count, ulong tick, long qpc)
        {
            if (!_active) return;
            var data = new byte[8 + count];
            PutU32(data, 0, type);
            PutU32(data, 4, (uint)count);
            if (count > 0) Buffer.BlockCopy(payload, 0, data, 8, count);
            Enqueue(kind, data, data.Length, tick, qpc);
        }

        /// <summary>A raw record (C1, C2, marker): the bytes are copied.</summary>
        public void RecordRaw(ushort kind, byte[] data, int count, ulong tick, long qpc)
        {
            if (!_active) return;
            var copy = new byte[count];
            Buffer.BlockCopy(data, 0, copy, 0, count);
            Enqueue(kind, copy, count, tick, qpc);
        }

        public void Marker(string json, ulong tick, long qpc)
        {
            if (!_active || json == null) return;
            byte[] b = Encoding.UTF8.GetBytes(json);
            Enqueue(Derec.KindMarker, b, b.Length, tick, qpc);
        }

        private void Enqueue(ushort kind, byte[] data, int count, ulong tick, long qpc)
        {
            if (count > Derec.MaxRecordBytes || Interlocked.Read(ref _queuedBytes) + count > MaxQueuedBytes || Volatile.Read(ref _queuedRecords) >= MaxQueuedRecords)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            Interlocked.Add(ref _queuedBytes, count);
            Interlocked.Increment(ref _queuedRecords);
            _queue.Enqueue(new Item { Kind = kind, Data = data, Count = count, Tick = tick, Qpc = qpc });
            _wake.Set();
        }

        private void Run()
        {
            string stopReason = "thread ended";
            DerecWriter w = null;
            try
            {
                var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                w = new DerecWriter(fs, _header);
                while (true)
                {
                    Item it;
                    if (!_queue.TryDequeue(out it)) { _wake.WaitOne(200); continue; }
                    if (it.Stop) { stopReason = it.StopReason; break; }
                    Interlocked.Add(ref _queuedBytes, -it.Count);
                    Interlocked.Decrement(ref _queuedRecords);
                    w.Write(it.Kind, it.Tick, it.Qpc, it.Data, 0, it.Count);
                    _bytes = w.Bytes;
                    Count(it);
                }
                w.WriteEnd(_lastTick, 0);
                _bytes = w.Bytes;
                w.Dispose();
                w = null;
                _lastSha = Sha256Of(_path);
                WriteSidecar(stopReason, _lastSha);
            }
            catch (Exception e)
            {
                _lastError = e.GetType().Name + ": " + e.Message;
                _active = false;
                try { if (w != null) w.Dispose(); } catch (Exception) { }
                try { WriteSidecar("error: " + _lastError, null); } catch (Exception) { }
            }
        }

        private static string Sha256Of(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(f);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private void Count(Item it)
        {
            Interlocked.Increment(ref _records);
            if (it.Kind < _kindCounts.Length) _kindCounts[it.Kind]++;
            if (!_hasTick) { _firstTick = it.Tick; _hasTick = true; }
            if (it.Tick > _lastTick) _lastTick = it.Tick;
            if (it.Count < 8) return;
            uint type = (uint)(it.Data[0] | (it.Data[1] << 8) | (it.Data[2] << 16) | (it.Data[3] << 24));
            if (it.Kind == Derec.KindC3)
            {
                if (type == C3Type.ModelBegin) _models++;
                else if (type == C3Type.MaterialDef) _materials++;
            }
            else if (it.Kind == Derec.KindC4)
            {
                if (type == C4Type.GridAdd) _grids++;
                else if (type == C4Type.PartAdd) _parts++;
                else if (type == C4Type.PartRemove) _partRemoves++;
                else if (type == C4Type.CharacterAdd) _characters++;
            }
        }

        private void WriteSidecar(string stopReason, string sha256)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", _name },
                { "formatVersion", (long)_header.formatVersion },
                { "created", DateTimeOffset.FromUnixTimeMilliseconds((long)_header.createdUnixMs).UtcDateTime.ToString("o", CultureInfo.InvariantCulture) },
                { "createdUnixMs", (long)_header.createdUnixMs },
                { "layoutHash", "0x" + _header.layoutHash.ToString("x16", CultureInfo.InvariantCulture) },
                { "conventionsHash", "0x" + _header.conventionsHash.ToString("x16", CultureInfo.InvariantCulture) },
                { "wireVersion", (long)_header.wireVersion },
                { "seVersion", (long)_header.seVersion },
                { "flags", (long)_header.flags },
                { "synthetic", (_header.flags & Derec.FlagSynthetic) != 0 },
                { "worldEpoch", (long)_header.worldEpoch },
                { "firstTick", (long)_firstTick },
                { "lastTick", (long)_lastTick },
                { "counts", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "c3", _kindCounts[Derec.KindC3] }, { "c4", _kindCounts[Derec.KindC4] }, { "c2", _kindCounts[Derec.KindC2] },
                        { "c1", _kindCounts[Derec.KindC1] }, { "marker", _kindCounts[Derec.KindMarker] },
                    } },
                { "records", Interlocked.Read(ref _records) },
                { "bytes", _bytes },
                { "models", _models },
                { "materials", _materials },
                { "grids", _grids },
                { "parts", _parts },
                { "partRemoves", _partRemoves },
                { "characters", _characters },
                { "dropped", Interlocked.Read(ref _dropped) },
                { "sha256", sha256 },
                { "stopReason", stopReason },
                { "file", _path },
            };
            File.WriteAllText(_sidecar, JsonText.Serialize(d) + "\n", new UTF8Encoding(false));
            _lastSidecar = _sidecar;
        }

        private static void PutU32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }

        public Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "active", _active }, { "name", _name }, { "file", _path }, { "c1", WantC1 }, { "c2", WantC2 },
                { "records", Interlocked.Read(ref _records) }, { "bytes", _bytes }, { "dropped", Interlocked.Read(ref _dropped) },
                { "queuedBytes", Interlocked.Read(ref _queuedBytes) }, { "lastSidecar", _lastSidecar }, { "sha256", _lastSha }, { "lastError", _lastError },
            };
        }
    }
}
