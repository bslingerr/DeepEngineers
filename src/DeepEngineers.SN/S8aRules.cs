using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeepEngineers.Wire;
using QueueC = DeepEngineers.Wire.S8aConventions.Queue;

namespace DeepEngineers.SN
{
    /// <summary>One batch the store serves (the GetPath postfix's answer).</summary>
    internal sealed class S8aServed
    {
        public int Bx, By, Bz;
        public uint Version;
        /// <summary>The full path of the validated file.</summary>
        public string Path;
        public long Bytes;
        public ulong Fnv;
        /// <summary>From a notice (true) or the manifest (false).</summary>
        public bool FromNotice;
        /// <summary>Unix ms when the file was validated (0: from a manifest read); a resync replace keeps notice entries newer than the manifest's write.</summary>
        public long ValidatedUtcMs;

        public Int3Key Id { get { return new Int3Key(Bx, By, Bz); } }
    }

    /// <summary>
 /// The store view of the Subnautica side (CONTRACT §3.3 "The store": one reader, only versions it has validated):
 /// batch id → the served file. Read on the streamer's I/O thread by the GetPath postfix, written on the main thread
 /// (notices) and from the manifest reader: one lock, no allocation on the read path.
 /// </summary>
    internal sealed class S8aStoreView
    {
        private readonly object _lock = new object();
        private readonly Dictionary<Int3Key, S8aServed> _served = new Dictionary<Int3Key, S8aServed>();
        private long _answers, _misses;

        public int Count { get { lock (_lock) return _served.Count; } }
        /// <summary>GetPath answers taken from the store since start (C7 <c>batchesServed</c>).</summary>
        public long Answers { get { lock (_lock) return _answers; } }
        public long Misses { get { lock (_lock) return _misses; } }

        /// <summary>The served path of a batch id, counted; null when the store names none (the original path stands).</summary>
        public string PathOf(int bx, int by, int bz)
        {
            lock (_lock)
            {
                S8aServed s;
                if (_served.TryGetValue(new Int3Key(bx, by, bz), out s)) { _answers++; return s.Path; }
                _misses++;
                return null;
            }
        }

        public bool TryGet(int bx, int by, int bz, out S8aServed s) { lock (_lock) return _served.TryGetValue(new Int3Key(bx, by, bz), out s); }

        /// <summary>A newer or equal version replaces the entry; an older one is ignored (false) unless <paramref name="force"/> (the manifest replacing the view, a fallback to an older file still on disk).</summary>
        public bool Set(S8aServed s, bool force = false)
        {
            lock (_lock)
            {
                S8aServed cur;
                if (!force && _served.TryGetValue(s.Id, out cur) && (int)(cur.Version - s.Version) > 0) return false;
                _served[s.Id] = s;
                return true;
            }
        }

        public bool Remove(Int3Key id) { lock (_lock) return _served.Remove(id); }

        public void Clear() { lock (_lock) _served.Clear(); }

        public List<S8aServed> Snapshot()
        {
            lock (_lock) return new List<S8aServed>(_served.Values);
        }

        /// <summary>
 /// The manifest's entries become the view (a resync, a new stream, a reset): every entry whose file
 /// <paramref name="valid"/> accepts; with <paramref name="replace"/> the previous entries go first. Returns the entries taken.
 /// </summary>
        public int LoadManifest(S8aManifest m, string root, Func<string, S8aManifest.Entry, bool> valid, bool replace = false)
        {
            if (m == null) return 0;
            if (replace) Clear();
            int n = 0;
            foreach (S8aManifest.Entry e in m.Entries)
            {
                string path = System.IO.Path.Combine(root, e.RelativePath);
                if (valid == null || !valid(path, e)) continue;
                if (Set(new S8aServed { Bx = e.Bx, By = e.By, Bz = e.Bz, Version = e.Version, Path = path, Bytes = e.Bytes, Fnv = e.Fnv, FromNotice = false }, replace)) n++;
            }
            return n;
        }
    }

    /// <summary>
 /// The notice's file check (CONTRACT §3.3 fail-safe (2); a worker thread): the file exists, parses as a batch of that
 /// id (version ≥ 4, every count consumed), and, when the manifest lists the version, has its size and FNV-1a 64. A
 /// file that fails is never served.
 /// </summary>
    internal static class S8aNoticeRule
    {
        public static bool Validate(byte[] file, int bx, int by, int bz, S8aManifest.Entry manifestEntry, out ulong fnv, out string error)
        {
            fnv = 0;
            error = null;
            if (file == null || file.Length < 4) { error = "short file"; return false; }
            S8aBatchFile b;
            if (!S8aBatchFile.TryParse(file, file.Length, bx, by, bz, out b, out error)) return false;
            fnv = LayoutHash.Fnv1a64(file);
            if (manifestEntry != null)
            {
                if (manifestEntry.Bytes != file.Length) { error = "size " + file.Length + " != manifest " + manifestEntry.Bytes; return false; }
                if (manifestEntry.Fnv != fnv) { error = "fnv " + S8aStore.Hex16(fnv) + " != manifest " + S8aStore.Hex16(manifestEntry.Fnv); return false; }
            }
            return true;
        }

        /// <summary>S8a.M4 ("GetPath serves the install from the n5 reload on"): the store's answer is withheld while the mutant is on.</summary>
        public static string Serve(string storePath, bool m4) { return m4 ? null : storePath; }
    }

    /// <summary>
 /// The clipmap cell ranges of a notice box (CONTRACT §3.3: <c>ClipmapLevel.GetCellRange</c>, private, VERIFIED
 /// <c>Int3.MinMax(FloorDiv(mins − (3 &lt;&lt; id), cellSize),
 /// CeilDiv(maxs + 2·(3 &lt;&lt; id), cellSize))</c>) and the per-notice cap (<c>Queue.MaxCellReloadsPerNotice</c>: beyond it,
 /// levels 0–1 keep every cell of the box and levels 2–3 only the cell under the box's centre).
 /// </summary>
    internal static class S8aCellRange
    {
        public struct Cell
        {
            public int Level, X, Y, Z;
            public override string ToString() { return "L" + Level + " " + X + "," + Y + "," + Z; }
        }

        public static int FloorDiv(int a, int b) { int q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }
        public static int CeilDiv(int a, int b) { return -FloorDiv(-a, b); }

        /// <summary>The cell range of level <paramref name="level"/> over the block box [mins, maxs] (inclusive cell ids).</summary>
        public static void Range(int level, int cellSize, int[] mins, int[] maxs, out int[] cmin, out int[] cmax)
        {
            int pad = 3 << level;
            cmin = new int[3];
            cmax = new int[3];
            for (int a = 0; a < 3; a++)
            {
                cmin[a] = FloorDiv(mins[a] - pad, cellSize);
                cmax[a] = CeilDiv(maxs[a] + 2 * pad, cellSize);
            }
        }

        /// <summary>
 /// The cells to reload for a notice box over levels 0..<paramref name="levels"/>−1 (cell sizes per level), capped
 /// (<paramref name="cap"/> ≤ 0: no cap).
 /// </summary>
        public static List<Cell> Plan(int levels, int[] cellSizes, int[] mins, int[] maxs, int cap)
        {
            var all = new List<Cell>();
            int total = 0;
            var ranges = new int[levels][];
            for (int l = 0; l < levels; l++)
            {
                int[] cmin, cmax;
                Range(l, cellSizes[l], mins, maxs, out cmin, out cmax);
                ranges[l] = new[] { cmin[0], cmin[1], cmin[2], cmax[0], cmax[1], cmax[2] };
                total += (cmax[0] - cmin[0] + 1) * (cmax[1] - cmin[1] + 1) * (cmax[2] - cmin[2] + 1);
            }
            bool capped = cap > 0 && total > cap;
            for (int l = 0; l < levels; l++)
            {
                int[] r = ranges[l];
                if (capped && l >= 2)
                {
                    var c = new int[3];
                    for (int a = 0; a < 3; a++) c[a] = FloorDiv((mins[a] + maxs[a]) / 2, cellSizes[l]);
                    all.Add(new Cell { Level = l, X = c[0], Y = c[1], Z = c[2] });
                    continue;
                }
                for (int x = r[0]; x <= r[3]; x++)
                    for (int y = r[1]; y <= r[4]; y++)
                        for (int z = r[2]; z <= r[5]; z++)
                            all.Add(new Cell { Level = l, X = x, Y = y, Z = z });
            }
            return all;
        }
    }

    /// <summary>
 /// The reload bookkeeping of the notices (CONTRACT "feed.ack": ackSeq = the highest contiguous seq received and
 /// scheduled; appliedSeq = the highest contiguous seq whose batch is loaded and whose box is built at the collision
 /// level; fail-safe (3): a reload that does not complete in 5 s is retried once, at 10 s it counts as applied with a
 /// failure, so the feed never stalls). Game-free: the host drives it with its own clock and answers.
 /// </summary>
    internal sealed class S8aReloadTracker
    {
        public const ulong RetryMs = 5000, GiveUpMs = 10000;
        /// <summary>

 /// flight, so the retry and the give-up grow with the queue: + PerQueuedMs per notice waiting or in flight, at most MaxGiveUpMs.
 /// </summary>
        public const ulong PerQueuedMs = 250, MaxGiveUpMs = 120000;

        /// <summary>The retry age for <paramref name="queued"/> notices waiting or in flight (RetryMs with an empty queue).</summary>
        public static ulong RetryFor(int queued) { return Math.Min(MaxGiveUpMs / 2, RetryMs + PerQueuedMs / 2 * (ulong)Math.Max(0, queued)); }

        /// <summary>The give-up age for <paramref name="queued"/> notices waiting or in flight (GiveUpMs with an empty queue).</summary>
        public static ulong GiveUpFor(int queued) { return Math.Min(MaxGiveUpMs, GiveUpMs + PerQueuedMs * (ulong)Math.Max(0, queued)); }

        public enum Stage { Validating, Scheduled, Reloading, Rebuilding, Applied, Failed }

        public sealed class Item
        {
            public S8aNotice Notice;
            public Stage Stage;
            public ulong SeenMs, ScheduledMs, AppliedMs;
            public bool Retried, Served;
            public int CellsReloaded;
            public string Error;
            /// <summary>The validated file the reload must load (null: the install).</summary>
            public string ServedPath;
            public int[] Mins = new int[3], Maxs = new int[3];     // world block box of the notice
            public bool NeedsReload = true;                        // false: the ring does not hold the batch (nothing to rebuild)
        }

        private readonly SortedDictionary<uint, Item> _items = new SortedDictionary<uint, Item>();
        private uint _stream;
        private bool _haveStream;
        private uint _ackSeq, _appliedSeq;
        public long Failures, Applied, Retries, Timeouts;
        public ushort LastLatencyMs;

        public uint Stream { get { return _stream; } }
        public uint AckSeq { get { return _ackSeq; } }
        public uint AppliedSeq { get { return _appliedSeq; } }
        public int Pending { get { return _items.Count; } }

        /// <summary>A new stream: the counters restart (seq restarts at 1 on SE).</summary>
        public void Reset(uint stream)
        {
            _items.Clear();
            _stream = stream;
            _haveStream = true;
            _ackSeq = 0;
            _appliedSeq = 0;
        }

        public bool HasStream { get { return _haveStream; } }

        public Item Add(in S8aNotice n, ulong nowMs)
        {
            var it = new Item { Notice = n, Stage = Stage.Validating, SeenMs = nowMs };
            int bx = n.bx * 160, by = n.by * 160, bz = n.bz * 160;
            it.Mins[0] = bx + n.minX; it.Mins[1] = by + n.minY; it.Mins[2] = bz + n.minZ;
            it.Maxs[0] = bx + n.maxX; it.Maxs[1] = by + n.maxY; it.Maxs[2] = bz + n.maxZ;
            _items[n.seq] = it;
            return it;
        }

        public Item Get(uint seq) { Item it; return _items.TryGetValue(seq, out it) ? it : null; }

        public IEnumerable<Item> Items { get { return _items.Values; } }

        /// <summary>Scheduled (or nothing to do): the ack may advance.</summary>
        public void MarkScheduled(Item it, ulong nowMs)
        {
            if (it.Stage == Stage.Validating) it.Stage = Stage.Scheduled;
            it.ScheduledMs = nowMs;
            Advance();
        }

        public void MarkApplied(Item it, ulong nowMs, bool failed, string error)
        {
            if (it.Stage == Stage.Applied || it.Stage == Stage.Failed) return;
            it.Stage = failed ? Stage.Failed : Stage.Applied;
            it.AppliedMs = nowMs;
            it.Error = error;
            if (failed) Failures++; else Applied++;
            ulong lat = nowMs - it.SeenMs;
            LastLatencyMs = (ushort)Math.Min(65535UL, lat);
            Advance();
        }

        /// <summary>The time rule: items past RetryMs are retried once (the caller re-schedules those returned), past GiveUpMs they count as applied with a failure.</summary>
        public List<Item> Tick(ulong nowMs, int queued = 0)
        {
            List<Item> retry = null, giveUp = null;
            ulong giveUpMs = GiveUpFor(queued), retryMs = RetryFor(queued);
            foreach (Item it in _items.Values)
            {
                if (it.Stage == Stage.Applied || it.Stage == Stage.Failed) continue;
                ulong age = nowMs - it.SeenMs;
                if (age >= giveUpMs)
                {
                    if (giveUp == null) giveUp = new List<Item>();
                    giveUp.Add(it);
                }
                else if (age >= retryMs && !it.Retried && it.Stage >= Stage.Scheduled)
                {
                    it.Retried = true;
                    Retries++;
                    if (retry == null) retry = new List<Item>();
                    retry.Add(it);
                }
            }
            if (giveUp != null)
                foreach (Item it in giveUp)
                {
                    Timeouts++;
                    MarkApplied(it, nowMs, true, "reload timed out after " + (nowMs - it.SeenMs) + " ms");
                }
            return retry;
        }

        /// <summary>ackSeq follows the contiguous prefix of scheduled (or finished) notices, appliedSeq the contiguous prefix of finished ones; finished items up to appliedSeq are dropped.</summary>
        private void Advance()
        {
            uint ack = _ackSeq;
            foreach (var kv in _items)
            {
                if (kv.Key != ack + 1) { if ((int)(kv.Key - (ack + 1)) > 0) break; continue; }
                if (kv.Value.Stage == Stage.Validating) break;
                ack = kv.Key;
            }
            _ackSeq = ack;
            uint app = _appliedSeq;
            var done = new List<uint>();
            foreach (var kv in _items)
            {
                if (kv.Key != app + 1) { if ((int)(kv.Key - (app + 1)) > 0) break; done.Add(kv.Key); continue; }
                if (kv.Value.Stage != Stage.Applied && kv.Value.Stage != Stage.Failed) break;
                app = kv.Key;
                done.Add(kv.Key);
            }
            _appliedSeq = app;
            foreach (uint s in done) _items.Remove(s);
        }

        /// <summary>A gap: the seqs below <paramref name="firstSeq"/> that were never seen count as acknowledged and applied (SE dropped them into a resync).</summary>
        public void SkipTo(uint firstSeq)
        {
            if ((int)(firstSeq - 1 - _ackSeq) > 0 && _items.Count == 0) { _ackSeq = firstSeq - 1; _appliedSeq = firstSeq - 1; }
        }

        public int InFlight
        {
            get
            {
                int n = 0;
                foreach (Item it in _items.Values) if (it.Stage != Stage.Applied && it.Stage != Stage.Failed && it.Stage != Stage.Validating) n++;
                return n;
            }
        }
    }

    /// <summary>The paths of the store as Subnautica reads them (CONTRACT "store.path").</summary>
    internal static class S8aSnPaths
    {
        /// <summary>The version of a store file name <c>compiled-batch-x-y-z.v&lt;n&gt;.optoctrees</c>; 0 for the install's
 /// plain name, null or anything else.</summary>
        internal static int VersionOfPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int dot = path.LastIndexOf(".optoctrees", StringComparison.OrdinalIgnoreCase);
            if (dot <= 0) return 0;
            int v = path.LastIndexOf(".v", dot - 1, StringComparison.OrdinalIgnoreCase);
            if (v < 0 || v + 2 >= dot) return 0;
            int n;
            return int.TryParse(path.Substring(v + 2, dot - v - 2), out n) && n > 0 ? n : 0;
        }

        public static string Root() { return S8aStore.DefaultRoot(); }
        public static string DrillFolder(ulong storeKey) { return S8aStore.DrillFolder(Root(), storeKey); }
        public static string DrillFile(ulong storeKey, int bx, int by, int bz, uint version) { return Path.Combine(DrillFolder(storeKey), S8aStore.BatchFileName(bx, by, bz, version)); }
        public static string WindowFolder(ulong storeKey, ulong chartKey) { return S8aStore.WindowFolder(Root(), storeKey, chartKey); }
        public static string WindowFile(ulong storeKey, ulong chartKey, int bx, int by, int bz, uint version) { return Path.Combine(WindowFolder(storeKey, chartKey), S8aStore.BatchFileName(bx, by, bz, version)); }
        public static string Hex(ulong v) { return v.ToString("x16", CultureInfo.InvariantCulture); }

        /// <summary>The newest version of a batch still on disk in the drill folder, or null.</summary>
        public static string NewestExisting(ulong storeKey, int bx, int by, int bz, out uint version)
        {
            return NewestExisting(DrillFolder(storeKey), bx, by, bz, out version);
        }

        public static string NewestExisting(string folder, int bx, int by, int bz, out uint version)
        {
            version = 0;
            string best = null;
            try
            {
                if (!Directory.Exists(folder)) return null;
                foreach (string f in Directory.GetFiles(folder, "compiled-batch-" + bx + "-" + by + "-" + bz + ".v*.optoctrees"))
                {
                    int fx, fy, fz;
                    uint v;
                    if (!S8aStore.TryParseBatchFileName(Path.GetFileName(f), out fx, out fy, out fz, out v) || fx != bx || fy != by || fz != bz) continue;
                    if (best == null || v > version) { best = f; version = v; }
                }
            }
            catch (Exception) { return null; }
            return best;
        }
    }
}
