using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using VRage.Voxels;
using VRageMath;
using NoticeFlags = DeepEngineers.Wire.S8aConventions.NoticeFlags;
using NoticeLayer = DeepEngineers.Wire.S8aConventions.NoticeLayer;
using StoreC = DeepEngineers.Wire.S8aConventions.Store;
using WindowC = DeepEngineers.Wire.S8aConventions.Window;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S8a-2 the window: Subnautica batches beyond the crater generated from SE's planet around the camera (ROADMAP B3
 /// "S8aWindow: planner, worker, near box, regeneration on planet edits, cameraBiome"; CONTRACT §3.3 "Window", formats
 /// window.set, window.ready, biome.rule, store.path, store.manifest). One worker thread (below normal, Windows
 /// background I/O mode as S4's prefetcher) keeps the ring of <see cref="S8aWindowPlan.Plan"/> generated nearest first:
 /// per batch the LOD-3 classification of its preimage (<see cref="S8aPlanetSampler.Classify"/>: all water → a
 /// <c>BatchCleared</c> notice, no file; all rock → a file of 125 one-node solid octrees), else per octree the same
 /// classification and the converter core (<see cref="S8aConvertCore"/>) with LOD-0 reads only for the octrees that meet
 /// the surface; files <c>&lt;root&gt;\&lt;storeKey&gt;\window\&lt;chartKey&gt;\compiled-batch-…v&lt;n&gt;.optoctrees</c>,
 /// the window layer's manifest (key chartKey, hash sculptHash), <c>BatchWritten(Window)</c> notices. A planet
 /// <c>MyStorageBase.RangeChanged</c> regenerates the batches whose preimage it meets (version + 1). A chart change
 /// (<see cref="OnChartChanged"/>) opens the new chart's folder; the previous chart's files are copied under the shifted
 /// ids with the <c>Reuse</c> flag until regenerated. <c>NearPending</c>/<c>NearEmpty</c> follow "window.ready",
 /// <c>cameraBiome</c> "biome.rule" (the SE material at the camera's closest surface point and its depth). Eviction:
 /// beyond <c>Store.MaxMB</c> other charts' folders go first (the site chart's kept), then this chart's files farther
 /// than <c>KeepWindowM</c> and off the ring. Mutants: S8a.M6 (the unedited provider), M9 (NearPending never set),
 /// M10 (every type 1). <c>de.s8a window {throttleMs, on}</c>. Rows: <c>s8a.window</c> (1 Hz), <c>s8a.write</c> (layer window).
 /// </summary>
    internal static class S8aWindow
    {
        public const string EnvWindow = "DEEPENGINEERS_S8A_WINDOW";
        public const int PlanEveryMs = 250, RowEveryMs = 1000, EvictEveryMs = 10000;
        public const int MaxQueue = 4096;

        private enum Kind : byte { Water, Rock, Surface, Reuse }

        private sealed class Entry
        {
            public uint Version;
            public Kind Kind;
            public long Bytes;
            public string Path;
            public double Ms;
            public ushort Flags;
            /// <summary>The seq of the notice that announced this entry (0: none yet, or the feed was full); applied once C7 appliedSeq reaches it.</summary>
            public uint NoticeSeq;
        }

        /// <summary>A chart change handed to the worker.</summary>
        private sealed class ChartSwitch
        {
            public SiteMap Chart;
            public uint Seq;
            public ulong Key;
            public int Tx, Tz;
            public Dictionary<Int3Key, Entry> Previous;
        }

        private sealed class Job
        {
            public Int3Key Id;
            public string ReuseFrom;
            public bool Regen;
            /// the
 /// batch's other octrees are kept from the served version. Null: a full generation.</summary>
            public List<Vector3I[]> Edits;
            /// <summary>Coalescing: the job runs at DueMs (CoalesceMs + CoalesceMsPerMB of the served file after the last edit, at most
 /// CoalesceMaxMs after the first), so a drill held down yields one version per pause, not one per edit.</summary>
            public ulong FirstMs, DueMs;
            /// <summary>Taken by the worker: later edits of this batch wait for the next version (never cancel a write in flight).</summary>
            public bool Started;
        }

        /// <summary>The longest a regeneration waits for edits to pause.</summary>
        public const int CoalesceMaxMs = 1500;
        /// <summary>Edits that arrived while their batch's regeneration was running: the next version's.</summary>
        private static readonly Dictionary<Int3Key, List<Vector3I[]>> _followUp = new Dictionary<Int3Key, List<Vector3I[]>>();
        private static long _incremental, _octreesKept;

        private static readonly object Lock = new object();
        private static readonly Dictionary<Int3Key, Entry> _gen = new Dictionary<Int3Key, Entry>();
        private static readonly Dictionary<Int3Key, Job> _queued = new Dictionary<Int3Key, Job>();
        private static readonly Dictionary<Int3Key, uint> _versions = new Dictionary<Int3Key, uint>();
        private static readonly List<Int3Key> _ring = new List<Int3Key>(160), _near = new List<Int3Key>(32);
        private static readonly List<Vector3I[]> _ranges = new List<Vector3I[]>();
        private static readonly List<double> _surfaceMs = new List<double>(256);
        private static ChartSwitch _pendingChart;
        /// <summary>Window ids whose box came within Near100M of the camera.</summary>
        private static readonly HashSet<Int3Key> _near100Seen = new HashSet<Int3Key>();
        private const double Near100M = 100.0;
        private static int _gateTail;
        /// <summary>Gate rows after NearPending cleared (2 s): the analyzer reads the resume (Subnautica's areaBuilt back) from them.</summary>
        private const int GateTailTicks = 120;
        private static S8aManifest _manifest;
        private static bool _manifestDirty;
        private static long _manifestWrittenMs;
        private static string _root, _folder;
        private static ulong _storeKey, _chartKey, _siteChartKey, _sculptHash;
        private static uint _chartSeq;
        private static SiteMap _map;
        private static Thread _thread;
        private static int _runToken;
        private static volatile bool _running;
        private static bool _refused;
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static S8aPlanetSampler _sampler;
        private static MyStorageBase _planetStorage;
        private static Action<Vector3I, Vector3I, MyStorageDataTypeFlags> _rangeHandler;
        private static byte[] _materialTable;
        private static Vec3 _camSn, _camSe, _velSn, _surfacePointSe;
        private static bool _haveSurface;
        private static double _seabedBelow = double.NaN;
        private static ulong _lastPlanMs, _lastCamMs, _nextRowMs;
        private static long _written, _cleared, _reused, _regen, _notices, _errors, _evicted, _rangeEvents;
        private static double _lastMs, _msMax;
        private static long _storeBytes;
        private static volatile bool _busy;

        /// <summary><c>DEEPENGINEERS_S8A_WINDOW</c> (default on with S8a on).</summary>
        public static bool Setting { get; private set; }
        /// <summary><c>de.s8a window {on}</c>: the runtime switch.</summary>
        public static bool On { get; private set; }
        /// <summary><c>de.s8a window {throttleMs}</c>: the worker sleeps this long per batch (S8a.N9).</summary>
        public static int ThrottleMs { get; private set; }
        public static bool WindowOn { get { return S8aOverlay.Enabled && Setting && On && _running && !_refused; } }
        public static bool NearPending { get; private set; }
        public static bool NearEmpty { get; private set; }
        public static ushort NearPendingCount { get; private set; }
        public static byte CameraBiome { get; private set; }
        public static float SeabedBelowCamM { get { return (float)_seabedBelow; } }
        public static uint WindowGen { get; private set; }
        public static ulong ChartKey { get { return _chartKey; } }

        public static void ReadEnv()
        {
            Setting = S8aOverlay.EnvOn(EnvWindow, true);
            On = true;
            CameraBiome = S8aMaterialMap.BiomeNone;
        }

        public static void Configure(bool on, int throttleMs)
        {
            On = on;
            ThrottleMs = Math.Max(0, throttleMs);
            Wake.Set();
        }

        // ================================================================================================= start / stop

 /// <summary>Game thread, once the site is bound and the planet known: the store's window layer for the current chart, the planet's RangeChanged, the worker.</summary>
        public static void Start(string root, ulong storeKey, ulong sculptHash, MyPlanet planet, SiteMap chart, uint chartSeq, ulong chartKey)
        {
            Stop();
            string err;
            _sampler = S8aPlanetSampler.Open(planet, out err);
            if (_sampler == null) { Refuse("planet sampler: " + err); return; }
            _root = root;
            _storeKey = storeKey;
            _sculptHash = sculptHash;
            _refused = false;
            _written = _cleared = _reused = _regen = _notices = _errors = _evicted = _rangeEvents = _incremental = _octreesKept = 0;
            WindowGen = 0;
            _msMax = 0;
            lock (Lock) { _gen.Clear(); _queued.Clear(); _versions.Clear(); _ranges.Clear(); _surfaceMs.Clear(); _followUp.Clear(); _superseded.Clear(); }
            _siteChartKey = S8aChartManager.ChartIndex == 0 ? chartKey : _siteChartKey;
            _gateTail = 0;
            QueueChart(chart, chartSeq, chartKey, 0, 0, true);
            _materialTable = S8aMutants.M10 ? S8aMaterialMap.UniformTable(1) : BuildMaterialTable();
            _planetStorage = _sampler.Storage;
            _rangeHandler = OnPlanetRangeChanged;
            try { _planetStorage.RangeChanged += _rangeHandler; } catch (Exception e) { DeLog.Info("DE.S8a window: planet RangeChanged not subscribed: " + e.GetType().Name + ": " + e.Message); }
            int token = Interlocked.Increment(ref _runToken);
            _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "DE.S8a window", Priority = ThreadPriority.BelowNormal };
            _running = true;
            _thread.Start();
            DeLog.Info("DE.S8a window started: folder=" + S8aStore.WindowFolder(_root, _storeKey, chartKey) + " chart " + chartSeq + " key=" + S8aStore.Hex16(chartKey) + " sculpt=" + S8aStore.Hex16(sculptHash) +
                       " planet " + (planet.StorageName ?? "?") + " size=" + _sampler.Size + " setting=" + Setting + (S8aMutants.M6 ? " (S8a.M6: provider reads)" : "") +
                       (S8aMutants.M9 ? " (S8a.M9: NearPending never set)" : "") + (S8aMutants.M10 ? " (S8a.M10: every type 1)" : ""));
        }

        public static void Stop()
        {
            Interlocked.Increment(ref _runToken);
            Thread t = _thread;
            _thread = null;
            _running = false;
            Wake.Set();
            if (t != null && t.IsAlive) { try { if (!t.Join(3000)) DeLog.Info("DE.S8a window worker did not stop in 3 s"); } catch (Exception) { } }
            if (_planetStorage != null && _rangeHandler != null) { try { _planetStorage.RangeChanged -= _rangeHandler; } catch (Exception) { } }
            _planetStorage = null;
            _rangeHandler = null;
            try { FlushManifest(true); } catch (Exception) { }
            lock (Lock) { _pendingChart = null; _near100Seen.Clear(); }
            NearPending = NearEmpty = false;
            NearPendingCount = 0;
            _gateTail = 0;
            CameraBiome = S8aMaterialMap.BiomeNone;
            _seabedBelow = double.NaN;
        }

        private static void Refuse(string why)
        {
            _refused = true;
            _running = false;
            DeLog.Info("DE.S8a STORE REFUSE window: " + why + " (the window layer is off; the crater and the drill layer are unaffected)");
            Jsonl.WriteLine(S4Rows.Row("s8a.store", "tick", (long)GameState.GameplayTick, "refused", true, "layer", "window", "reason", why, "root", _root));
        }

        private static byte[] BuildMaterialTable()
        {
            Sandbox.Definitions.MyDefinitionManager dm = Sandbox.Definitions.MyDefinitionManager.Static;
            return S8aMaterialMap.BuildTable(i =>
            {
                var def = dm != null ? dm.GetVoxelMaterialDefinition((byte)i) : null;
                return def != null ? def.Id.SubtypeName : null;
            });
        }

        /// <summary>Game thread, no I/O: the chart switch for the worker. The generated set is dropped at once (the ring plan refills it under the new chart; NearPending gates the picture meanwhile).</summary>
        private static void QueueChart(SiteMap chart, uint chartSeq, ulong chartKey, int tx, int tz, bool first)
        {
            lock (Lock)
            {
                var previous = first ? null : new Dictionary<Int3Key, Entry>(_gen);
                _gen.Clear();
                _queued.Clear();
                _versions.Clear();
                _near100Seen.Clear();
                _map = chart;
                _chartSeq = chartSeq;
                _chartKey = chartKey;
                _pendingChart = new ChartSwitch { Chart = chart, Seq = chartSeq, Key = chartKey, Tx = tx, Tz = tz, Previous = previous };
            }
            Wake.Set();
        }

        /// <summary>
 /// Worker: the chart's folder and manifest (kept when key and sculpt hash match and every file exists: a returning chart
 /// finds its batches, each re-announced by a notice because Subnautica's window view was cleared by the ChartStep);
 /// reuse jobs for the previous chart's files under the shifted ids. Every file access runs outside the lock.
 /// </summary>
        private static void ApplyChart(ChartSwitch cs)
        {
            try { FlushManifest(true); } catch (Exception) { }     // the outgoing chart's manifest into its own folder (still _folder)
            string folder = S8aStore.WindowFolder(_root, _storeKey, cs.Key);
            try
            {
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, ".write-test");
                File.WriteAllBytes(probe, new byte[] { 1 });
                File.Delete(probe);
            }
            catch (Exception e) { Refuse("folder not writable " + folder + ": " + e.GetType().Name + ": " + e.Message); return; }
            string err;
            S8aManifest existing = null;
            try { existing = S8aManifest.ReadFrom(folder, out err); } catch (Exception) { }
            bool matched = existing != null && existing.Layer == S8aStore.WindowLayer && existing.Key == cs.Key && existing.Hash == _sculptHash;
            var kept = new Dictionary<Int3Key, Entry>();
            if (matched)
            {
                foreach (S8aManifest.Entry e in existing.Entries)
                {
                    string p = Path.Combine(_root, e.RelativePath);
                    bool cleared = (e.Flags & NoticeFlags.AllEmpty) != 0 && e.Bytes == 0;
                    bool exists = false;
                    try { exists = cleared || File.Exists(p); } catch (Exception) { }
                    if (!exists) continue;
                    kept[e.Id] = new Entry { Version = e.Version, Kind = cleared ? Kind.Water : (e.Flags & NoticeFlags.Reuse) != 0 ? Kind.Reuse : (e.Flags & NoticeFlags.AllSolid) != 0 ? Kind.Rock : Kind.Surface,
                                             Bytes = e.Bytes, Path = cleared ? null : p, Flags = e.Flags };
                }
            }
            var versions = new Dictionary<Int3Key, uint>();
            try
            {
                foreach (string f in Directory.GetFiles(folder, "compiled-batch-*.optoctrees"))
                {
                    int bx, by, bz;
                    uint v;
                    if (!S8aStore.TryParseBatchFileName(Path.GetFileName(f), out bx, out by, out bz, out v)) continue;
                    var id = new Int3Key(bx, by, bz);
                    uint cur;
                    if (!versions.TryGetValue(id, out cur) || v > cur) versions[id] = v;
                }
            }
            catch (Exception) { }
            foreach (var kv in kept) { uint cur; if (!versions.TryGetValue(kv.Key, out cur) || kv.Value.Version > cur) versions[kv.Key] = kv.Value.Version; }
            int reuse = 0;
            lock (Lock)
            {
                if (_pendingChart != cs) return;      // a newer switch is queued: this one is obsolete
                _folder = folder;
                _manifest = matched ? existing : new S8aManifest { Layer = S8aStore.WindowLayer, Key = cs.Key, Hash = _sculptHash };
                foreach (var kv in kept) _gen[kv.Key] = kv.Value;
                foreach (var kv in versions) _versions[kv.Key] = kv.Value;
                _manifestDirty = true;
                // the previous chart's files under the shifted ids (CONTRACT NoticeFlags.Reuse) until regenerated
                if (cs.Previous != null && (cs.Tx != 0 || cs.Tz != 0))
                {
                    foreach (var kv in cs.Previous)
                    {
                        if (kv.Value.Kind == Kind.Water || kv.Value.Path == null) continue;
                        var nid = new Int3Key(kv.Key.X - cs.Tx, kv.Key.Y, kv.Key.Z - cs.Tz);
                        if (!S8aWindowPlan.IsWindowId(nid.X, nid.Y, nid.Z) || _gen.ContainsKey(nid) || _queued.ContainsKey(nid)) continue;
                        _queued[nid] = new Job { Id = nid, ReuseFrom = kv.Value.Path };
                        reuse++;
                    }
                }
                _pendingChart = null;
            }
            // Subnautica's window view holds nothing of this chart yet: one notice per kept entry (the file's version; the box the whole batch),

            Vec3 camNew = cs.Chart != null ? cs.Chart.SeToSn(_camSe) : _camSn;
            var order = new List<KeyValuePair<Int3Key, Entry>>(kept);
            order.Sort((a, b) => S8aWindowPlan.CentreDist2(camNew, a.Key).CompareTo(S8aWindowPlan.CentreDist2(camNew, b.Key)));
            foreach (var kv in order)
            {
                Int3Key id = kv.Key;
                Entry e = kv.Value;
                S8aNotice n = e.Kind == Kind.Water ? S8aNotice.BatchCleared(NoticeLayer.Window, id.X, id.Y, id.Z, e.Version, e.Flags)
                    : S8aNotice.BatchWritten(NoticeLayer.Window, id.X, id.Y, id.Z, e.Version, e.Flags, 0, 0, 0, 159, 159, 159, 0, 0);
                uint seq = S8aFeed.Enqueue(n);
                if (seq == 0) continue;
                _notices++;
                lock (Lock) { Entry ge; if (_gen.TryGetValue(id, out ge) && ge == e) ge.NoticeSeq = seq; }
            }
            DeLog.Info("DE.S8a window chart " + cs.Seq + " folder=" + folder + " generated=" + kept.Count + (kept.Count > 0 ? " (re-announced)" : "") + " reuse=" + reuse);
            Wake.Set();
        }

        /// <summary>S8aChartManager (game thread): the chart stepped (T in batches) or returned; the window follows the new chart key (the worker opens it).</summary>
        public static void OnChartChanged(uint chartSeq, ulong chartKey, SiteMap chart, int tx, int tz)
        {
            if (!_running) return;
            if (S8aChartManager.ChartIndex == 0) _siteChartKey = chartKey;
            QueueChart(chart, chartSeq, chartKey, tx, tz, false);
        }

        // ================================================================================================= planet edits (any thread)

        private static void OnPlanetRangeChanged(Vector3I min, Vector3I max, MyStorageDataTypeFlags flags)
        {
            lock (Lock)
            {
                _rangeEvents++;
                if (_ranges.Count < 1024) _ranges.Add(new[] { min, max });
            }
        }

        // ================================================================================================= Tick (game thread)

 /// <summary>Per tick: the camera under the current chart, the ring plan at 4 Hz, the near box, the planet edits' regeneration, the 1 Hz row.</summary>
        public static void Tick(ulong nowMs, Vec3 camSe, bool haveCamera)
        {
            if (!_running || _refused || !haveCamera) return;
            GateRow();
            SiteMap map;
            lock (Lock) map = _map;
            if (map == null) return;
            Vec3 camSn = map.SeToSn(camSe);
            if (_lastCamMs != 0 && nowMs > _lastCamMs)
            {
                double dt = (nowMs - _lastCamMs) / 1000.0;
                Vec3 v = (camSn - _camSn) * (1.0 / dt);
                _velSn = dt < 0.5 ? v : Vec3.Zero;
            }
            _camSn = camSn;
            _camSe = camSe;
            _lastCamMs = nowMs;
            if (nowMs - _lastPlanMs < (ulong)PlanEveryMs) return;
            _lastPlanMs = nowMs;
            DrainRanges(map);
            if (!On) { NearPending = NearEmpty = false; NearPendingCount = 0; return; }
            // SE's surface under the camera (the closest surface point of the planet; "window.ready": NearSurfaceM)
            try
            {
                var p = new Vector3D(camSe.X, camSe.Y, camSe.Z);
                Vector3D s = _sampler.Planet.GetClosestSurfacePointGlobal(ref p);
                _surfacePointSe = new Vec3(s.X, s.Y, s.Z);
                _haveSurface = true;
                double d = (camSe - _surfacePointSe).Length;
                double rc = (camSe - SiteBinding.C).Length, rs = (_surfacePointSe - SiteBinding.C).Length;
                _seabedBelow = rc >= rs ? d : -d;
            }
            catch (Exception) { _haveSurface = false; _seabedBelow = double.NaN; }
            S8aWindowPlan.Plan(camSn, _velSn, _ring);
            S8aWindowPlan.NearBox(camSn, _near);
            Near100Rows(camSn);
            int pendingNear = 0;
            bool allEmpty = true;
            uint appliedSeq = S8aFeed.AppliedSeq;
            lock (Lock)
            {
                foreach (Int3Key id in _ring)
                    if (!_gen.ContainsKey(id) && !_queued.ContainsKey(id) && _queued.Count < MaxQueue) _queued[id] = new Job { Id = id };
                foreach (Int3Key id in _near)
                {
                    Entry e;
                    if (!_gen.TryGetValue(id, out e)) { pendingNear++; allEmpty = false; if (!_queued.ContainsKey(id)) _queued[id] = new Job { Id = id }; }
                    else
                    {
                        if (e.Kind != Kind.Water) allEmpty = false;

                        // the batch's notice (C7 appliedSeq) before its picture may show the floor here
                        if (e.NoticeSeq != 0 && (int)(e.NoticeSeq - appliedSeq) > 0) pendingNear++;
                    }
                }
            }
            Wake.Set();
            NearPendingCount = (ushort)Math.Min(65535, pendingNear);
            bool nearSurface = !double.IsNaN(_seabedBelow) && _seabedBelow <= WindowC.NearSurfaceM;
            NearPending = pendingNear > 0 && nearSurface && !S8aMutants.M9;
            NearEmpty = allEmpty && _near.Count > 0;
            if (nowMs >= _nextRowMs)
            {
                _nextRowMs = nowMs + RowEveryMs;
                Row();
            }
        }

        /// <summary>
 /// Review MEDIUM 9: while NearPending is set, and for GateTailTicks after it clears, a row per tick with Subnautica's C7
 /// areaBuilt and presented tick as SE read them this tick: <c>s8a.gate</c> {tick, nearPending, nearPendingCount, nearEmpty, snAreaBuilt, snPresentedSeTick}.
 /// </summary>
        private static void GateRow()
        {
            bool np = NearPending;
            if (np) _gateTail = GateTailTicks;
            else if (_gateTail > 0) _gateTail--;
            else return;
            Jsonl.WriteLine(S4Rows.Row("s8a.gate", "tick", (long)GameState.GameplayTick, "nearPending", np, "nearPendingCount", (long)NearPendingCount, "nearEmpty", NearEmpty,
                                       "snAreaBuilt", S8aFeed.SnAreaBuilt, "snPresentedSeTick", (long)S8aFeed.SnPresentedTick, "seabedBelowCamM", double.IsNaN(_seabedBelow) ? (object)null : _seabedBelow));
        }

        /// <summary>
 /// Review MEDIUM 7 (S8a.N7.coverage): the first time a window id's box comes within Near100M of the camera, one
 /// <c>s8a.near100</c> row {tick, bx, by, bz, distM, state, kind, version, noticeSeq, appliedSeq, applied}: applied when
 /// its notice seq is at or below the C7 appliedSeq SE read this tick (the batch was in Subnautica before the camera came near).
 /// </summary>
        private static void Near100Rows(Vec3 camSn)
        {
            List<object[]> rows = null;
            lock (Lock)
            {
                double far = (Near100M + 400.0) * (Near100M + 400.0);
                _near100Seen.RemoveWhere(k => S8aWindowPlan.BoxDist2(camSn, k) > far);
                for (int pass = 0; pass < 2; pass++)
                {
                    List<Int3Key> ids = pass == 0 ? _ring : _near;
                    foreach (Int3Key id in ids)
                    {
                        double d2 = S8aWindowPlan.BoxDist2(camSn, id);
                        if (d2 > Near100M * Near100M || !_near100Seen.Add(id)) continue;
                        Entry e;
                        string state = _gen.TryGetValue(id, out e) ? "generated" : _queued.ContainsKey(id) ? "queued" : "none";
                        if (rows == null) rows = new List<object[]>();
                        rows.Add(new object[] { id, Math.Sqrt(d2), state, e != null ? e.Kind.ToString() : null, e != null ? (long)e.Version : 0L, e != null ? (long)e.NoticeSeq : 0L });
                    }
                }
            }
            if (rows == null) return;
            uint applied = S8aFeed.AppliedSeq;
            foreach (object[] r in rows)
            {
                var id = (Int3Key)r[0];
                long noticeSeq = (long)r[5];
                Jsonl.WriteLine(S4Rows.Row("s8a.near100", "tick", (long)GameState.GameplayTick, "bx", id.X, "by", id.Y, "bz", id.Z, "distM", (double)r[1], "state", r[2], "kind", r[3],
                                           "version", r[4], "noticeSeq", noticeSeq, "appliedSeq", (long)applied, "applied", noticeSeq != 0 && applied >= noticeSeq, "chartSeq", (long)_chartSeq));
            }
        }

        private static void DrainRanges(SiteMap map)
        {
            List<Vector3I[]> ranges = null;
            lock (Lock)
            {
                if (_ranges.Count > 0) { ranges = new List<Vector3I[]>(_ranges); _ranges.Clear(); }
            }
            if (ranges == null || _sampler == null) return;
            Vec3 corner = _sampler.Corner;
            foreach (Vector3I[] r in ranges)
            {
                int vx0, vy0, vz0, vx1, vy1, vz1;
                S8aConvertCore.SnBoxOf(map, corner, r[0].X, r[0].Y, r[0].Z, r[1].X, r[1].Y, r[1].Z, out vx0, out vy0, out vz0, out vx1, out vy1, out vz1);
                int b0x = S8aConvert.BatchOfVoxel(vx0), b1x = S8aConvert.BatchOfVoxel(vx1), b0y = S8aConvert.BatchOfVoxel(vy0), b1y = S8aConvert.BatchOfVoxel(vy1), b0z = S8aConvert.BatchOfVoxel(vz0), b1z = S8aConvert.BatchOfVoxel(vz1);
                if ((long)(b1x - b0x + 1) * (b1y - b0y + 1) * (b1z - b0z + 1) > 4096) continue;   // a planet-wide change: the ring regenerates as it is revisited
                ulong now = Win32.TickCount64();
                lock (Lock)
                {
                    for (int bx = b0x; bx <= b1x; bx++)
                        for (int by = b0y; by <= b1y; by++)
                            for (int bz = b0z; bz <= b1z; bz++)
                            {
                                if (!S8aWindowPlan.IsWindowId(bx, by, bz)) continue;
                                var id = new Int3Key(bx, by, bz);
                                Entry prev;
                                if (!_gen.TryGetValue(id, out prev)) continue;          // never served: the ring generates it fresh (it reads the edit)

                                Job j;
                                if (_queued.TryGetValue(id, out j))
                                {
                                    if (j.Started)
                                    {
                                        List<Vector3I[]> f;
                                        if (!_followUp.TryGetValue(id, out f)) _followUp[id] = f = new List<Vector3I[]>();
                                        f.Add(r);
                                    }
                                    else if (j.Edits != null) AddEdit(j, r, now, prev.Bytes);
                                    continue;                                           // a full generation queued reads the edit anyway
                                }
                                var nj = new Job { Id = id, Regen = true, Edits = new List<Vector3I[]>(), FirstMs = now };
                                AddEdit(nj, r, now, prev.Bytes);
                                _queued[id] = nj;
                                _regen++;
                            }
                }
            }
            Wake.Set();
        }

        /// <summary>One more planet edit for a queued regeneration (under Lock): the due time moves on, at most CoalesceMaxMs after the first edit.</summary>
        private static void AddEdit(Job j, Vector3I[] r, ulong now, long servedBytes)
        {
            j.Edits.Add(r);
            ulong due = now + (ulong)S8aConventions.Store.CoalesceMs + (ulong)(S8aConventions.Store.CoalesceMsPerMB * Math.Max(0L, servedBytes) / 1048576L);
            j.DueMs = Math.Min(j.FirstMs + CoalesceMaxMs, due);
        }

        /// <summary>True when an octree's planet preimage (storage voxels, inclusive) meets one of the edited boxes (2 voxels of margin: the stencil and the sphere's bulge).</summary>
        private static bool MeetsEdits(List<Vector3I[]> edits, int x0, int y0, int z0, int x1, int y1, int z1)
        {
            foreach (Vector3I[] e in edits)
                if (x0 <= e[1].X + 2 && x1 >= e[0].X - 2 && y0 <= e[1].Y + 2 && y1 >= e[0].Y - 2 && z0 <= e[1].Z + 2 && z1 >= e[0].Z - 2) return true;
            return false;
        }

        private static void Row()
        {
            int ring, generated, pending;
            double p95;
            lock (Lock)
            {
                ring = _ring.Count; generated = _gen.Count; pending = _queued.Count;
                p95 = Pct(_surfaceMs, 0.95);
            }
            Jsonl.WriteLine(S4Rows.Row("s8a.window", "tick", (long)GameState.GameplayTick, "chartSeq", (long)_chartSeq, "ring", ring, "generated", generated, "pending", pending,
                                       "nearPending", NearPending, "nearPendingCount", (long)NearPendingCount, "nearEmpty", NearEmpty, "msP95", p95, "msMax", _msMax, "workerBusy", _busy,
                                       "storeMB", _storeBytes / 1048576.0, "cameraBiome", (long)CameraBiome, "seabedBelowCamM", double.IsNaN(_seabedBelow) ? (object)null : _seabedBelow,
                                       "written", _written, "cleared", _cleared, "reused", _reused, "regen", _regen, "throttleMs", ThrottleMs, "on", On, "windowGen", (long)WindowGen,
                                       "camSn", S4Rows.Arr(_camSn), "velSn", S4Rows.Arr(_velSn)));
        }

        private static double Pct(List<double> v, double q)
        {
            if (v.Count == 0) return 0;
            var a = v.ToArray();
            Array.Sort(a);
            int i = (int)Math.Ceiling(q * a.Length) - 1;
            return a[Math.Max(0, Math.Min(a.Length - 1, i))];
        }

        // ================================================================================================= the worker

        private static void Run(int token)
        {
            bool bg = Background.Enter();
            var core = new S8aConvertCore();
            var batchBox = new S8aEditBox[1];
            var surface = new bool[125];
            var waterNode = new byte[S8aOctree.MaxBytes];
            var rockNode = new byte[S8aOctree.MaxBytes];
            var enc = new S8aOctreeEncoder();
            var t32 = new byte[S8aOctree.Voxels];
            var d32 = new byte[S8aOctree.Voxels];
            int waterNodes = enc.Encode(t32, d32, waterNode, 0);
            ulong lastBiomeMs = 0, lastEvict = 0;
            try
            {
                while (token == _runToken && _running)
                {
                    ChartSwitch cs;
                    bool idle;
                    lock (Lock) { cs = _pendingChart; idle = cs == null && (_queued.Count == 0 || !On); }
                    if (idle) Wake.WaitOne(100);     //  wait only while there is nothing to do
                    if (token != _runToken || !_running) break;
                    if (cs != null)
                    {
                        try { ApplyChart(cs); }
                        catch (Exception e) { _errors++; Log("chart " + cs.Seq + " " + e.GetType().Name + ": " + e.Message); lock (Lock) if (_pendingChart == cs) _pendingChart = null; }
                        continue;
                    }
                    ulong now = Win32.TickCount64();
                    if (now - lastBiomeMs >= 1000) { lastBiomeMs = now; try { UpdateBiome(); } catch (Exception e) { _errors++; Log("biome " + e.GetType().Name + ": " + e.Message); } }
                    if (now - lastEvict >= (ulong)EvictEveryMs) { lastEvict = now; try { Evict(); } catch (Exception e) { _errors++; Log("evict " + e.GetType().Name + ": " + e.Message); } }
                    FlushManifestIfDue(now);
                    DeleteSuperseded((long)now);
                    if (!On) continue;
                    Job job = NextJob();
                    if (job == null) { Wake.WaitOne(20); continue; }    // only coalescing jobs left: wait for their due time
                    _busy = true;
                    try { Generate(job, core, batchBox, surface, enc, t32, d32, waterNode, waterNodes, rockNode); }
                    catch (Exception e) { _errors++; Log("generate " + job.Id + " " + e.GetType().Name + ": " + e.Message); }
                    finally
                    {
                        _busy = false;
                        lock (Lock)
                        {
                            _queued.Remove(job.Id);
                            List<Vector3I[]> f;
                            if (_followUp.TryGetValue(job.Id, out f))
                            {
                                // the edits that came during this write: the next version (supersede after the write, never during it)
                                _followUp.Remove(job.Id);
                                ulong n2 = Win32.TickCount64();
                                var nj = new Job { Id = job.Id, Regen = true, Edits = f, FirstMs = n2, DueMs = n2 + (ulong)S8aConventions.Store.CoalesceMs };
                                _queued[job.Id] = nj;
                                _regen++;
                            }
                        }
                    }
                    if (ThrottleMs > 0) Thread.Sleep(ThrottleMs);
                }
            }
            catch (Exception e) { _errors++; Log("worker " + e.GetType().Name + ": " + e.Message); }
            finally { if (bg) Background.Leave(); }
        }

        /// <summary>The queued id nearest the camera (reuse copies first: they are cheap and keep the picture).</summary>
        private static Job NextJob()
        {
            lock (Lock)
            {
                if (_queued.Count == 0 || _pendingChart != null) return null;
                Job best = null;
                double bestD = double.MaxValue;
                ulong now = Win32.TickCount64();
                foreach (Job j in _queued.Values)
                {
                    if (j.Started || (j.Edits != null && now < j.DueMs)) continue;     // still coalescing
                    double d = S8aWindowPlan.CentreDist2(_camSn, j.Id) - (j.ReuseFrom != null ? 1e12 : 0.0);
                    if (d < bestD) { bestD = d; best = j; }
                }
                if (best != null) best.Started = true;
                return best;
            }
        }

        /// <summary>The batch's octree <paramref name="oi"/> holds exactly these nodes.</summary>
        private static bool SameOctree(S8aBatchFile b, int oi, byte[] nodes, int count)
        {
            if (!b.Has(oi) || b.NodeCount(oi) != count) return false;
            byte[] arr;
            int off, n;
            b.GetOctree(oi, out arr, out off, out n);
            for (int k = 0; k < 4 * count; k++) if (arr[off + k] != nodes[k]) return false;
            return true;
        }

        private static void Generate(Job job, S8aConvertCore core, S8aEditBox[] batchBox, bool[] surface, S8aOctreeEncoder enc, byte[] t32, byte[] d32, byte[] waterNode, int waterNodes, byte[] rockNode)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SiteMap map;
            string folder;
            ulong chartKey;
            lock (Lock) { map = _map; folder = _folder; chartKey = _chartKey; }
            if (map == null || folder == null) return;
            Int3Key id = job.Id;
            _sampler.UseProvider = S8aMutants.M6;
            uint version;
            lock (Lock) { uint cur; version = (_versions.TryGetValue(id, out cur) ? cur : 0) + 1; _versions[id] = version; }
            string name = S8aStore.BatchFileName(id.X, id.Y, id.Z, version);
            string path = Path.Combine(folder, name);
            Kind kind;
            long bytes = 0;
            ulong fnv = 0;
            ushort flags = 0;
            int octrees = 0;
            uint cells = 0, meshCells = 0, densityCells = 0;
            int[] changedBox = null;
            int minX = 0, minY = 0, minZ = 0, maxX = 159, maxY = 159, maxZ = 159;
            bool incremental = false;
            S8aBatchFile baseBatch = null;
            if (job.Edits != null && job.Edits.Count > 0)
            {
                Entry prevEntry;
                lock (Lock) _gen.TryGetValue(id, out prevEntry);
                if (prevEntry != null && (prevEntry.Kind == Kind.Water || prevEntry.Path == null)) baseBatch = S8aBatchFile.CreateEmpty(id.X, id.Y, id.Z);
                else if (prevEntry != null && File.Exists(prevEntry.Path))
                {
                    try
                    {
                        byte[] pb = File.ReadAllBytes(prevEntry.Path);
                        string perr;
                        if (!S8aBatchFile.TryParse(pb, pb.Length, id.X, id.Y, id.Z, out baseBatch, out perr)) baseBatch = null;
                    }
                    catch (Exception) { baseBatch = null; }
                }
            }
            if (baseBatch != null)
            {

                // again: an edit can open a surface in a rock octree or empty one), the others are the served version's bytes
                incremental = true;
                int bx0 = id.X * 160, by0 = id.Y * 160, bz0 = id.Z * 160;
                int px0, py0, pz0, px1, py1, pz1, cmin, cmax;
                PreimageSe(map, bx0, by0, bz0, 160, out px0, out py0, out pz0, out px1, out py1, out pz1);
                byte rockType = (_materialTable ?? S8aMaterialMap.UniformTable(S8aMaterialMap.Fallback))[Math.Max(0, Math.Min(255, _sampler.MaterialAt(map.SnToSe(S8aConvert.SampleCentreSn(bx0 + 80, by0 + 80, bz0 + 80)))))];
                if (rockType == 0) rockType = S8aMaterialMap.Fallback;
                for (int i = 0; i < S8aOctree.Voxels; i++) { t32[i] = rockType; d32[i] = 0; }
                int rockNodes = enc.Encode(t32, d32, rockNode, 0);
                int edited = 0, nSurface = 0;
                int ax = int.MaxValue, ay = int.MaxValue, az = int.MaxValue, bxm = int.MinValue, bym = int.MinValue, bzm = int.MinValue;
                // a test run: the box of the changes the level-0 mesh can show (S8aFieldSampler.LastMeshChanged): the notice's box
                int mx0 = int.MaxValue, my0 = int.MaxValue, mz0 = int.MaxValue, mx1 = int.MinValue, my1 = int.MinValue, mz1 = int.MinValue;
                for (int oi = 0; oi < 125; oi++)
                {
                    surface[oi] = false;
                    int ix = oi / 25, iy = (oi / 5) % 5, iz = oi % 5;
                    int ox0, oy0, oz0, ox1, oy1, oz1;
                    PreimageSe(map, bx0 + ix * 32, by0 + iy * 32, bz0 + iz * 32, 32, out ox0, out oy0, out oz0, out ox1, out oy1, out oz1);
                    if (!MeetsEdits(job.Edits, ox0, oy0, oz0, ox1, oy1, oz1)) { _octreesKept++; continue; }
                    edited++;
                    S8aPlanetSampler.Class oc = _sampler.Classify(ox0, oy0, oz0, ox1, oy1, oz1, out cmin, out cmax);
                    if (oc == S8aPlanetSampler.Class.Surface) { surface[oi] = true; nSurface++; continue; }
                    byte[] nodes = oc == S8aPlanetSampler.Class.Rock ? rockNode : waterNode;
                    int count = oc == S8aPlanetSampler.Class.Rock ? rockNodes : waterNodes;
                    // a test run (S8a.ND.live): an octree the edit's preimage meets but that stays rock (or water) as it was changes nothing:
 // no box (the notice covered whole octrees before, 180 level-0 cells per planet edit)
                    if (SameOctree(baseBatch, oi, nodes, count)) { _octreesKept++; continue; }
                    baseBatch.SetOctree(oi, nodes, 0, count);
                    ax = Math.Min(ax, ix * 32); ay = Math.Min(ay, iy * 32); az = Math.Min(az, iz * 32);
                    bxm = Math.Max(bxm, ix * 32 + 31); bym = Math.Max(bym, iy * 32 + 31); bzm = Math.Max(bzm, iz * 32 + 31);
                    mx0 = Math.Min(mx0, ix * 32); my0 = Math.Min(my0, iy * 32); mz0 = Math.Min(mz0, iz * 32);
                    mx1 = Math.Max(mx1, ix * 32 + 31); my1 = Math.Max(my1, iy * 32 + 31); mz1 = Math.Max(mz1, iz * 32 + 31);
                }
                if (nSurface > 0)
                {
                    core.MaterialTable = _materialTable ?? S8aMaterialMap.UniformTable(S8aMaterialMap.Fallback);
                    core.NativeType = S8aSample.NativeTypeFunc();      // planet v2 route B: a placed chunk's own block types
                    core.SnOffset = S8aConvert.SampleOffset;
                    batchBox[0] = new S8aEditBox(0, px0, py0, pz0, px1, py1, pz1);
                    S8aConvertResult r = core.ConvertBatch(map, _sampler, baseBatch, batchBox, false, oi => surface[oi]);
                    cells = r.Cells;
                    meshCells = r.MeshCells;
                    densityCells = r.DensityCells;
                    if (r.OctreesRewritten > 0)
                    {
                        ax = Math.Min(ax, r.MinX); ay = Math.Min(ay, r.MinY); az = Math.Min(az, r.MinZ);
                        bxm = Math.Max(bxm, r.MaxX); bym = Math.Max(bym, r.MaxY); bzm = Math.Max(bzm, r.MaxZ);
                    }
                    if (r.MeshCells > 0)
                    {
                        mx0 = Math.Min(mx0, r.MeshMinX); my0 = Math.Min(my0, r.MeshMinY); mz0 = Math.Min(mz0, r.MeshMinZ);
                        mx1 = Math.Max(mx1, r.MeshMaxX); my1 = Math.Max(my1, r.MeshMaxY); mz1 = Math.Max(mz1, r.MeshMaxZ);
                    }
                }
                if (bxm >= ax) changedBox = new[] { ax, ay, az, bxm, bym, bzm };
                // the notice: the mesh's box; changes the mesh cannot show only (far-solid types inside the rock): a one-voxel box at the
 // changes' corner (the file is newer, the octrees are patched, one cell's rebuild; S8a.ND.roundtrip still compares all)
                if (mx1 >= mx0) { minX = mx0; minY = my0; minZ = mz0; maxX = mx1; maxY = my1; maxZ = mz1; }
                else if (bxm >= ax) { minX = maxX = ax; minY = maxY = ay; minZ = maxZ = az; }
                else { minX = minY = minZ = 0; maxX = maxY = maxZ = 0; }      // nothing changed: a one-voxel box (no cell to rebuild beyond it)
                octrees = edited;
                kind = Kind.Surface;
                if (baseBatch.AllSolid) flags |= NoticeFlags.AllSolid;
                if (baseBatch.AllEmpty) flags |= NoticeFlags.AllEmpty;
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16))
                {
                    baseBatch.WriteTo(fs);
                    bytes = fs.Length;
                }
                fnv = baseBatch.Fnv();
                _written++;
                _incremental++;
            }
            else if (job.ReuseFrom != null && File.Exists(job.ReuseFrom))
            {
                File.Copy(job.ReuseFrom, path, false);
                byte[] b = File.ReadAllBytes(path);
                bytes = b.LongLength;
                fnv = LayoutHash.Fnv1a64(b);
                kind = Kind.Reuse;
                flags = NoticeFlags.Reuse;
                _reused++;
                // the reused file stands until the batch is regenerated: queue it again (nearest first)
                lock (Lock) if (!_queued.ContainsKey(id) || _queued[id] == job) { }   // the caller removes this job; the ring plan re-adds the id
            }
            else
            {
                // the batch's SE preimage (the planet's voxel box) through the current chart
                int bx0 = id.X * 160, by0 = id.Y * 160, bz0 = id.Z * 160;
                int px0, py0, pz0, px1, py1, pz1;
                PreimageSe(map, bx0, by0, bz0, 160, out px0, out py0, out pz0, out px1, out py1, out pz1);
                int cmin, cmax;
                S8aPlanetSampler.Class c = _sampler.Classify(px0, py0, pz0, px1, py1, pz1, out cmin, out cmax);
                if (c == S8aPlanetSampler.Class.Water)
                {
                    kind = Kind.Water;
                    flags = NoticeFlags.AllEmpty;
                    _cleared++;
                }
                else
                {
                    S8aBatchFile batch = S8aBatchFile.CreateEmpty(id.X, id.Y, id.Z);
                    byte rockType = (_materialTable ?? S8aMaterialMap.UniformTable(S8aMaterialMap.Fallback))[Math.Max(0, Math.Min(255, _sampler.MaterialAt(map.SnToSe(S8aConvert.SampleCentreSn(bx0 + 80, by0 + 80, bz0 + 80)))))];
                    if (rockType == 0) rockType = S8aMaterialMap.Fallback;
                    for (int i = 0; i < S8aOctree.Voxels; i++) { t32[i] = rockType; d32[i] = 0; }
                    int rockNodes = enc.Encode(t32, d32, rockNode, 0);
                    if (c == S8aPlanetSampler.Class.Rock)
                    {
                        for (int oi = 0; oi < 125; oi++) batch.SetOctree(oi, rockNode, 0, rockNodes);
                        kind = Kind.Rock;
                        flags = NoticeFlags.AllSolid;
                        octrees = 125;
                    }
                    else
                    {
                        int nSurface = 0;
                        for (int oi = 0; oi < 125; oi++)
                        {
                            int ix = oi / 25, iy = (oi / 5) % 5, iz = oi % 5;
                            int ox0, oy0, oz0, ox1, oy1, oz1;
                            PreimageSe(map, bx0 + ix * 32, by0 + iy * 32, bz0 + iz * 32, 32, out ox0, out oy0, out oz0, out ox1, out oy1, out oz1);
                            S8aPlanetSampler.Class oc = _sampler.Classify(ox0, oy0, oz0, ox1, oy1, oz1, out cmin, out cmax);
                            surface[oi] = oc == S8aPlanetSampler.Class.Surface;
                            if (oc == S8aPlanetSampler.Class.Rock) batch.SetOctree(oi, rockNode, 0, enc.Encode(t32, d32, rockNode, 0));
                            if (surface[oi]) nSurface++;
                        }
                        core.MaterialTable = _materialTable ?? S8aMaterialMap.UniformTable(S8aMaterialMap.Fallback);
                        core.NativeType = S8aSample.NativeTypeFunc();  // planet v2 route B: a placed chunk's own block types
                        core.SnOffset = S8aConvert.SampleOffset;
                        batchBox[0] = new S8aEditBox(0, px0, py0, pz0, px1, py1, pz1);
                        S8aConvertResult r = core.ConvertBatch(map, _sampler, batch, batchBox, false, oi => surface[oi]);
                        octrees = nSurface;
                        cells = r.Cells;
                        if (r.OctreesRewritten > 0) { minX = r.MinX; minY = r.MinY; minZ = r.MinZ; maxX = r.MaxX; maxY = r.MaxY; maxZ = r.MaxZ; }
                        kind = Kind.Surface;
                        if (batch.AllSolid) flags |= NoticeFlags.AllSolid;
                        if (batch.AllEmpty) flags |= NoticeFlags.AllEmpty;
                    }
                    using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16))
                    {
                        batch.WriteTo(fs);
                        bytes = fs.Length;
                    }
                    fnv = batch.Fnv();
                    _written++;
                }
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string previous = null;
            lock (Lock)
            {
                S8aManifest.Entry old;
                if (_manifest.TryGet(id.X, id.Y, id.Z, out old)) previous = old.RelativePath;
                //  the manifest lists served files only; an all-water batch has no file and no line (it lives in
 // _gen and in its BatchCleared notice; a returning chart classifies it again, ~1 ms): S8aWindowPlan.RecordGenerated
                S8aWindowPlan.RecordGenerated(_manifest, id, kind == Kind.Water, version, bytes, fnv, flags,
                                              kind == Kind.Water ? null : S8aStore.RelativeWindowPath(_storeKey, chartKey, id.X, id.Y, id.Z, version));
                _manifestDirty = true;
                _gen[id] = new Entry { Version = version, Kind = kind, Bytes = bytes, Path = kind == Kind.Water ? null : path, Ms = ms, Flags = flags };
                if (kind == Kind.Surface) { _surfaceMs.Add(ms); if (_surfaceMs.Count > 256) _surfaceMs.RemoveAt(0); }
                _storeBytes += bytes;
            }
            WindowGen++;
            _lastMs = ms;
            if (ms > _msMax) _msMax = ms;
            Jsonl.WriteLine(S4Rows.Row("s8a.write", "tick", (long)GameState.GameplayTick, "layer", "window", "bx", id.X, "by", id.Y, "bz", id.Z, "version", (long)version,
                                       "bytes", bytes, "fnv", S8aStore.Hex16(fnv), "ms", ms, "path", kind == Kind.Water ? null : path, "kind", kind.ToString(), "octrees", octrees,
                                       "cells", (long)cells, "regen", job.Regen, "reuse", job.ReuseFrom != null, "chartSeq", (long)_chartSeq, "chartKey", S8aStore.Hex16(chartKey),
                                       "incremental", incremental, "edits", job.Edits != null ? job.Edits.Count : 0, "box", new object[] { minX, minY, minZ, maxX, maxY, maxZ },
                                       "meshCells", (long)meshCells, "densityCells", (long)densityCells, "changedBox", changedBox != null ? new object[] { changedBox[0], changedBox[1], changedBox[2], changedBox[3], changedBox[4], changedBox[5] } : null));
            S8aNotice n = kind == Kind.Water ? S8aNotice.BatchCleared(NoticeLayer.Window, id.X, id.Y, id.Z, version, flags)
                : S8aNotice.BatchWritten(NoticeLayer.Window, id.X, id.Y, id.Z, version, flags, minX, minY, minZ, maxX, maxY, maxZ, octrees, cells);
            uint seq = S8aFeed.Enqueue(n);
            if (seq != 0)
            {
                _notices++;
                lock (Lock) { Entry ge; if (_gen.TryGetValue(id, out ge) && ge.Version == version) ge.NoticeSeq = seq; }
            }
            // night queue (nd, session 20261023-194214): the previous version was deleted right here, before its successor's
 // notice was even sent, so Subnautica's live edit could not read the version it holds (FileNotFoundException, the batch
 // reload instead: hidden cells, S8a.ND.hidden/.wall); like the drill layer's (S8aDrillConverter.Housekeeping) it is now
 // deleted GraceMs after Subnautica's appliedSeq covers the successor's notice
            if (previous != null)
            {
                string pp = Path.Combine(_root, previous);
                if (!string.Equals(pp, path, StringComparison.OrdinalIgnoreCase))
                    lock (Lock) _superseded.Add(new Superseded { Path = pp, Seq = seq, SinceMs = (long)Win32.TickCount64(), FailuresAtAdd = S8aFeed.LastC7.reloadFailures });
            }
        }

        /// <summary>The planet voxel box (LOD-0, inclusive, with the stencil's and the sphere's margin) a 160 or 32 m SN box maps to under the chart.</summary>
        private static void PreimageSe(SiteMap map, int vx0, int vy0, int vz0, int n, out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
        {
            S8aFieldSampler.PreimageBox(map, vx0, vy0, vz0, n, _sampler.Corner, 2, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
        }

        /// <summary>"biome.rule": the planet's biome map at the camera's closest surface point (Stage C), else the SE material there and the seabed depth → cameraBiome (1 Hz, worker).</summary>
        private static void UpdateBiome()
        {
            if (!_haveSurface || _sampler == null) { CameraBiome = S8aMaterialMap.BiomeNone; return; }
            SiteMap map;
            lock (Lock) map = _map;
            if (map == null) return;
            Vec3 sp = _surfacePointSe;
            int mat = _sampler.MaterialAt(sp);
            Vec3 sn = map.SeToSn(sp);
            string subtype = null;
            if (mat >= 0)
            {
                try { var def = Sandbox.Definitions.MyDefinitionManager.Static.GetVoxelMaterialDefinition((byte)mat); subtype = def != null ? def.Id.SubtypeName : null; } catch (Exception) { }
            }
            // Stage C (ext s8a terrain 2): the planet's biome map names the biome where the designed planet has one
            CameraBiome = S8aBiomes.CameraBiome(subtype, -sn.Y, _sampler.BiomeMapAt(sp));
        }

        /// <summary>Eviction (CONTRACT "The store"): beyond Store.MaxMB, other charts' folders first (never the site chart's), then this chart's files farther than KeepWindowM off the ring.</summary>
        private sealed class Superseded { public string Path; public uint Seq; public long SinceMs; public uint FailuresAtAdd; }
        private static readonly List<Superseded> _superseded = new List<Superseded>();
        private static long _supersededDeleted, _supersededKept;

        /// <summary>
 /// Worker: a superseded version is deleted GraceMs after Subnautica's appliedSeq covers its successor's notice (the live edit
 /// diffs against the version Subnautica holds); one whose successor's notice was never sent (seq 0), or after a reload
 /// failure meanwhile, stays on disk (the eviction removes it with its folder or as far).
 /// </summary>
        private static void DeleteSuperseded(long now)
        {
            uint applied = S8aFeed.AppliedSeq;
            uint failures = S8aFeed.LastC7.reloadFailures;
            List<Superseded> gone = null;
            lock (Lock)
            {
                for (int i = _superseded.Count - 1; i >= 0; i--)
                {
                    Superseded s = _superseded[i];
                    if (now - s.SinceMs < StoreC.GraceMs) continue;
                    if (s.Seq == 0 || failures != s.FailuresAtAdd) { _superseded.RemoveAt(i); _supersededKept++; continue; }
                    if ((int)(applied - s.Seq) < 0) continue;
                    if (gone == null) gone = new List<Superseded>();
                    gone.Add(s);
                    _superseded.RemoveAt(i);
                }
            }
            if (gone == null) return;
            foreach (Superseded s in gone)
            {
                try { if (File.Exists(s.Path)) { File.Delete(s.Path); _supersededDeleted++; } }
                catch (Exception) { lock (Lock) _superseded.Add(new Superseded { Path = s.Path, Seq = s.Seq, SinceMs = now, FailuresAtAdd = s.FailuresAtAdd }); }
            }
        }

        private static void Evict()
        {
            string layerRoot = Path.Combine(_root, S8aStore.Hex16(_storeKey), S8aStore.WindowLayer);
            if (!Directory.Exists(layerRoot)) return;
            long total = 0;
            var folders = new List<string>();
            foreach (string d in Directory.GetDirectories(layerRoot))
            {
                folders.Add(d);
                foreach (string f in Directory.GetFiles(d, "*.optoctrees")) total += new FileInfo(f).Length;
            }
            _storeBytes = total;
            long limit = (long)StoreC.MaxMB * 1048576L;
            if (total <= limit) return;
            string current = _folder;
            string site = S8aStore.WindowFolder(_root, _storeKey, _siteChartKey);
            foreach (string d in folders)
            {
                if (total <= limit) break;
                if (string.Equals(d, current, StringComparison.OrdinalIgnoreCase) || string.Equals(d, site, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string f in Directory.GetFiles(d, "*"))
                {
                    try { long n = new FileInfo(f).Length; File.Delete(f); total -= n; _evicted++; } catch (Exception) { }
                }
                try { Directory.Delete(d); } catch (Exception) { }
            }
            if (total <= limit) { _storeBytes = total; return; }
            List<KeyValuePair<Int3Key, Entry>> far;
            lock (Lock)
            {
                far = new List<KeyValuePair<Int3Key, Entry>>();
                foreach (var kv in _gen)
                {
                    if (kv.Value.Path == null || _ring.Contains(kv.Key)) continue;
                    if (Math.Sqrt(S8aWindowPlan.CentreDist2(_camSn, kv.Key)) > StoreC.KeepWindowM) far.Add(kv);
                }
            }
            foreach (var kv in far)
            {
                if (total <= limit) break;
                try { File.Delete(kv.Value.Path); total -= kv.Value.Bytes; _evicted++; } catch (Exception) { continue; }
                lock (Lock) { _gen.Remove(kv.Key); _manifest.Remove(kv.Key.X, kv.Key.Y, kv.Key.Z); _manifestDirty = true; }
            }
            _storeBytes = total;
        }

        /// <summary>A resync: the manifest flushed now (Subnautica re-reads it).</summary>
        public static void FlushManifestNow()
        {
            try { FlushManifest(true); } catch (Exception) { }
        }

        private static void FlushManifestIfDue(ulong now)
        {
            bool dirty;
            lock (Lock) dirty = _manifestDirty;
            if (dirty && (long)now - _manifestWrittenMs >= 1000) FlushManifest(false);
        }

        private static void FlushManifest(bool force)
        {
            S8aManifest m;
            string folder;
            lock (Lock)
            {
                if (_manifest == null || _folder == null || (!_manifestDirty && !force)) return;
                m = _manifest;
                folder = _folder;
                _manifestDirty = false;
            }
            try { lock (m) m.WriteAtomic(folder); _manifestWrittenMs = (long)Win32.TickCount64(); }
            catch (Exception e) { _errors++; Log("manifest write " + e.GetType().Name + ": " + e.Message); lock (Lock) _manifestDirty = true; }
        }

        private static void Log(string what) { DeLog.Info("DE.S8a window " + what); }

        public static Dictionary<string, object> Status()
        {
            int ring, generated, pending, water, rock, surf, reuse;
            double p95;
            lock (Lock)
            {
                ring = _ring.Count; generated = _gen.Count; pending = _queued.Count;
                water = rock = surf = reuse = 0;
                foreach (Entry e in _gen.Values) { if (e.Kind == Kind.Water) water++; else if (e.Kind == Kind.Rock) rock++; else if (e.Kind == Kind.Surface) surf++; else reuse++; }
                p95 = Pct(_surfaceMs, 0.95);
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "setting", Setting }, { "on", On }, { "windowOn", WindowOn }, { "running", _running }, { "refused", _refused }, { "folder", _folder }, { "chartSeq", (long)_chartSeq },
                { "chartKey", S8aStore.Hex16(_chartKey) }, { "ring", ring }, { "generated", generated }, { "water", water }, { "rock", rock }, { "surface", surf }, { "reuse", reuse },
                { "pending", pending }, { "nearPending", NearPending }, { "nearPendingCount", (long)NearPendingCount }, { "nearEmpty", NearEmpty }, { "cameraBiome", (long)CameraBiome },
                { "cameraBiomeName", S8aBiomes.Name(CameraBiome) }, { "seabedBelowCamM", double.IsNaN(_seabedBelow) ? (object)null : _seabedBelow }, { "throttleMs", ThrottleMs },
                { "msP95", p95 }, { "msMax", _msMax }, { "lastMs", _lastMs }, { "workerBusy", _busy }, { "storeMB", _storeBytes / 1048576.0 }, { "written", _written }, { "cleared", _cleared },
                { "reused", _reused }, { "regen", _regen }, { "rangeEvents", _rangeEvents }, { "notices", _notices }, { "evicted", _evicted }, { "errors", _errors }, { "windowGen", (long)WindowGen },
                { "m6", S8aMutants.M6 }, { "m9", S8aMutants.M9 }, { "m10", S8aMutants.M10 },
                // Stage C timing fix: the shell material read (S8aPlanetSampler.ReadShell)
                { "incremental", _incremental }, { "octreesKept", _octreesKept }, { "followUps", (long)_followUp.Count },
                { "shell", S8aPlanetSampler.Shell }, { "shellMaterials", _sampler != null ? _sampler.ShellMaterials : 0L }, { "farFilled", _sampler != null ? _sampler.FarFilled : 0L },
                { "shellFallbacks", _sampler != null ? _sampler.ShellFallbacks : 0L }, { "ringSurfaces", _sampler != null ? _sampler.RingSurfaces : 0L }, { "supersededPending", _superseded.Count },
                { "supersededDeleted", _supersededDeleted }, { "supersededKept", _supersededKept }, { "readVoxels", _sampler != null ? _sampler.ReadVoxels : 0L },
            };
        }

        private static class Background
        {
            private const int ModeBegin = 0x00010000, ModeEnd = 0x00020000;
            [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
            [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetThreadPriority(IntPtr thread, int priority);
            public static bool Enter() { try { return SetThreadPriority(GetCurrentThread(), ModeBegin); } catch (Exception) { return false; } }
            public static void Leave() { try { SetThreadPriority(GetCurrentThread(), ModeEnd); } catch (Exception) { } }
        }
    }
}
