using System;
using System.Collections.Generic;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S10 "Performance across specs", part 1: the measurement rig (ROADMAP §14 "### S10"; docs/S10-BRIEF.md). The pure
 // helpers of the per-second s10.* rows (s10.perf on Subnautica, s10.se and s10.paste on SE) and the layout of the
 // scripted paste's 10,000-block ship. Nothing here is pinned yet: the presets and budgets of parts 2-6 get their own
 // CONTRACT §5.4 block when they land. Python twin of the percentile rule and the layout counts:
 // tests/s10/analyze_s10.py (percentile, blueprint_counts; vectors checked by S10PerfRulesTests and --selftest).
 // =================================================================================================================

 /// <summary>Row names, stage slots and the percentile rule of the S10 rows.</summary>
    public static class S10PerfRules
    {
        public const string RowSn = "s10.perf", RowSe = "s10.se", RowPaste = "s10.paste";

        /// <summary>Frame samples kept per second (more are counted, not stored: the max and the mean stay exact).</summary>
        public const int WindowCapacity = 1024;

        /// <summary>Subnautica main-thread stage slots (s10.perf <c>stages</c>): the mirror's kit build (C3 drain, worker
 /// results, prepared models), its part placement (cell builds, views, uploads, materials, S5 hierarchy), the whole
 /// mirror tick, the S6 proxies (colliders), the S7 interiors and light pool, the S8a window (notices, reloads).</summary>
        public const int StageKit = 0, StagePlace = 1, StageMirror = 2, StageColliders = 3, StageLights = 4, StageWindow = 5, StagePlugin = 6, StageFx = 7,
                         StageView = 8, StageHosts = 9, StageCount = 10;

        public static readonly string[] StageNames = { "kit", "place", "mirror", "colliders", "lights", "window", "plugin", "fx", "view", "hosts" };
        // round 3 E: "view" = S1 + S2 + the S8 restore check (the camera, the recipes, the window role), "hosts" = the hosts
 // ticked after the S8a window (S7p, S11, S7t, the look match, S16, S15, S12): the bridge tick's rest, named
 // round 2 A: "plugin" = the whole bridge tick (every host of ours), "fx" = the S7 effects player; a frame whose time is
 // not in "plugin" is Subnautica's own (its streaming, physics, rendering) or the collector's

 /// <summary>SE update-thread stage slots (s10.se <c>stages</c>): the export reads of the mirrored grids, the S6 collision
 /// skins, the S7/S12 airtight rooms (those three inside the producer), the whole mirror producer tick, the gameplay
 /// hooks after it (S6 damage, S11, S16, S15, S12), the S7 statics and S8a hosts.</summary>
        public const int SeStageExport = 0, SeStageSkin = 1, SeStageRooms = 2, SeStageProducer = 3, SeStageHooks = 4, SeStageS8a = 5, SeStageCount = 6;

        public static readonly string[] SeStageNames = { "export", "skin", "rooms", "producer", "hooks", "s8a" };

        /// <summary>Nearest-rank percentile of <paramref name="sorted"/>[0, n): index ceil(q·n) − 1, clamped; NaN when empty.</summary>
        public static double Percentile(double[] sorted, int n, double q)
        {
            if (sorted == null || n <= 0) return double.NaN;
            if (n > sorted.Length) n = sorted.Length;
            int i = (int)Math.Ceiling(q * n) - 1;
            if (i < 0) i = 0;
            if (i > n - 1) i = n - 1;
            return sorted[i];
        }
    }

    /// <summary>One second of frame statistics (ms).</summary>
    public struct S10FrameStats
    {
        public int Frames;
        public double P50, P95, P99, Max, Mean, SumMs;
    }

    /// <summary>The frame-time samples of one window (fixed capacity, no allocation after construction; one thread).</summary>
    public sealed class S10FrameWindow
    {
        private readonly double[] _v, _sort;
        private int _n, _extra;
        private double _sum, _max;

        public S10FrameWindow(int capacity = S10PerfRules.WindowCapacity)
        {
            _v = new double[Math.Max(1, capacity)];
            _sort = new double[_v.Length];
        }

        public int Count { get { return _n + _extra; } }

        public void Add(double ms)
        {
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0) return;
            _sum += ms;
            if (ms > _max) _max = ms;
            if (_n < _v.Length) _v[_n++] = ms;
            else _extra++;
        }

        /// <summary>The statistics of the samples since the last take; the window starts empty again.</summary>
        public S10FrameStats Take()
        {
            var s = new S10FrameStats { Frames = _n + _extra, SumMs = _sum, Max = _n + _extra > 0 ? _max : double.NaN };
            s.Mean = s.Frames > 0 ? _sum / s.Frames : double.NaN;
            Array.Copy(_v, _sort, _n);
            Array.Sort(_sort, 0, _n);
            s.P50 = S10PerfRules.Percentile(_sort, _n, 0.50);
            s.P95 = S10PerfRules.Percentile(_sort, _n, 0.95);
            s.P99 = S10PerfRules.Percentile(_sort, _n, 0.99);
            _n = 0;
            _extra = 0;
            _sum = 0;
            _max = 0;
            return s;
        }
    }

    /// <summary>Per-stage main-thread time of one window, in Stopwatch ticks (one thread: the Subnautica main thread).</summary>
    public sealed class S10StageClock
    {
        private readonly long[] _sum, _max, _frame;
        private readonly int[] _calls;
        private readonly int _n;

        public S10StageClock(int stages = S10PerfRules.StageCount)
        {
            _n = Math.Max(1, stages);
            _sum = new long[_n];
            _max = new long[_n];
            _frame = new long[_n];
            _calls = new int[_n];
        }

        public int Stages { get { return _n; } }

        public void Add(int stage, long ticks)
        {
            if (stage < 0 || stage >= _n || ticks < 0) return;
            _sum[stage] += ticks;
            _frame[stage] += ticks;
            _calls[stage]++;
        }

        /// <summary>Closes one frame: each stage's frame total feeds its worst-frame value.</summary>
        public void EndFrame()
        {
            for (int i = 0; i < _n; i++)
            {
                if (_frame[i] > _max[i]) _max[i] = _frame[i];
                _frame[i] = 0;
            }
        }

        /// <summary>Copies the window into ms arrays (sum over the window, worst single frame, calls) and resets it.</summary>
        public void Take(double msPerTick, double[] sumMs, double[] maxFrameMs, int[] calls)
        {
            for (int i = 0; i < _n; i++)
            {
                if (sumMs != null && i < sumMs.Length) sumMs[i] = _sum[i] * msPerTick;
                if (maxFrameMs != null && i < maxFrameMs.Length) maxFrameMs[i] = _max[i] * msPerTick;
                if (calls != null && i < calls.Length) calls[i] = _calls[i];
                _sum[i] = 0;
                _max[i] = 0;
                _calls[i] = 0;
                _frame[i] = 0;
            }
        }
    }

    // =================================================================================================================
 // S10 round 3 milestone E (the stutters, the allocation cuts; docs/S10-BRIEF.md "## Round 3"). The allocation meter of
 // the stage clock, the cuts' switch and a bounded byte-array pool. Pure (S10PerfRulesTests); nothing pinned (no wire
 // layout, no convention: telemetry fields and scratch reuse that leave every result byte-identical).
 // =================================================================================================================

 /// <summary>
 /// The allocation cuts of round 3 E (env DEEPENGINEERS_S10_ALLOCCUTS off restores every old path; de.s10 {allocCuts}
 /// flips it at run time on either side, so one session can measure before and after). Each cut reuses scratch or skips
 /// a copy and leaves every result byte-identical (the mesh, texture and row tests pin it); the row caps change only
 /// telemetry. Read on any thread (volatile).
 /// </summary>
    public static class S10AllocCuts
    {
        public const string Env = "DEEPENGINEERS_S10_ALLOCCUTS";
        /// <summary>Per-second budget of the capped high-volume rows (s6.proxy per-section ops, s12.flood level rows): the
 /// s7.fx.thruster budget of round 2 D.</summary>
        public const int RowsPerSecond = 24;
        /// <summary>Bytes the readback pool may keep (three 1024² RGBA readbacks).</summary>
        public const long PoolBytes = 3L * 1024 * 1024 * 4;
        private static volatile bool _on = Parse(Environment.GetEnvironmentVariable(Env), true);

        public static bool On { get { return _on; } set { _on = value; } }

        public static bool Parse(string s, bool dflt)
        {
            if (string.IsNullOrEmpty(s)) return dflt;
            s = s.Trim().ToLowerInvariant();
            if (s == "on" || s == "1" || s == "true") return true;
            if (s == "off" || s == "0" || s == "false") return false;
            return dflt;
        }
    }

    /// <summary>
 /// Bytes allocated per stage of one window (one thread), beside <see cref="S10StageClock"/>. The caller reads an
 /// allocation counter at each mark: SE the update thread's exact <c>GC.GetAllocatedBytesForCurrentThread</c>,
 /// Subnautica the Mono heap's used size (<c>GC.GetTotalMemory(false)</c>: every thread, and only growth: a collection in
 /// the stage counts nothing, so it is a lower bound). Nested stages work: a mark is found again by its Stopwatch stamp in
 /// a small ring (a stamp not found any more, or a counter that went down, adds nothing). No allocation after construction.
 /// </summary>
    public sealed class S10AllocMeter
    {
        public const int Ring = 32;
        private readonly long[] _bytes, _markTs = new long[Ring], _markCounter = new long[Ring];
        private readonly int _n;
        private int _head;
        private long _lapCounter = -1;

        public S10AllocMeter(int stages = S10PerfRules.StageCount)
        {
            _n = Math.Max(1, stages);
            _bytes = new long[_n];
        }

        /// <summary>A stage starts: its Stopwatch stamp and the counter now.</summary>
        public void Mark(long ts, long counter)
        {
            _markTs[_head] = ts;
            _markCounter[_head] = counter;
            _head = (_head + 1) % Ring;
        }

        /// <summary>The stage that started at stamp <paramref name="ts0"/> ends with the counter at <paramref name="counter"/>.</summary>
        public void Add(int stage, long ts0, long counter)
        {
            if (stage < 0 || stage >= _n) return;
            for (int k = 1; k <= Ring; k++)
            {
                int i = (_head - k + Ring) % Ring;
                if (_markTs[i] != ts0) continue;
                long d = counter - _markCounter[i];
                if (d > 0) _bytes[stage] += d;
                _markTs[i] = 0; // used: a later stage with the same stamp finds its own mark
                return;
            }
        }

        /// <summary>Lap marks (SE's producer / hooks / S8a sequence): the counter since the previous lap goes to the stage.</summary>
        public void LapStart(long counter) { _lapCounter = counter; }

        public void Lap(int stage, long counter)
        {
            if (_lapCounter >= 0 && stage >= 0 && stage < _n && counter > _lapCounter) _bytes[stage] += counter - _lapCounter;
            _lapCounter = counter;
        }

        /// <summary>Copies the window's bytes per stage (KB) and resets it.</summary>
        public void Take(double[] kb)
        {
            for (int i = 0; i < _n; i++)
            {
                if (kb != null && i < kb.Length) kb[i] = _bytes[i] / 1024.0;
                _bytes[i] = 0;
            }
        }
    }

    /// <summary>
 /// Byte arrays of exactly one length handed back for reuse (round 3 E: the texture conversion's GPU readbacks, three
 /// 4 MB arrays per texture set, which Unity's NativeArray.CopyTo needs at the exact length). At most
 /// <see cref="MaxBytes"/> are kept; a returned array beyond that is left to the collector. Thread-safe (the readback
 /// callbacks rent on the main thread, the repack worker returns). An array is never handed out twice.
 /// </summary>
    public sealed class S10BufferPool
    {
        private readonly Dictionary<int, Stack<byte[]>> _free = new Dictionary<int, Stack<byte[]>>();
        private readonly object _lock = new object();
        public readonly long MaxBytes;
        private long _kept;
        public long Rented, Reused, Returned, Dropped;

        public S10BufferPool(long maxBytes) { MaxBytes = Math.Max(0, maxBytes); }

        public long KeptBytes { get { lock (_lock) return _kept; } }

        /// <summary>An array of exactly <paramref name="length"/> bytes (contents unspecified: the caller overwrites all of it).</summary>
        public byte[] Rent(int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), length, "S10BufferPool: no negative sizes");
            lock (_lock)
            {
                Rented++;
                Stack<byte[]> s;
                if (_free.TryGetValue(length, out s) && s.Count > 0)
                {
                    Reused++;
                    _kept -= length;
                    return s.Pop();
                }
            }
            return new byte[length];
        }

        /// <summary>The caller no longer reads or writes <paramref name="a"/>. Null and duplicates are ignored.</summary>
        public void Return(byte[] a)
        {
            if (a == null) return;
            lock (_lock)
            {
                Stack<byte[]> s;
                if (!_free.TryGetValue(a.Length, out s)) _free[a.Length] = s = new Stack<byte[]>();
                foreach (byte[] x in s) if (ReferenceEquals(x, a)) return;
                if (_kept + a.Length > MaxBytes) { Dropped++; return; }
                s.Push(a);
                _kept += a.Length;
                Returned++;
            }
        }

        /// <summary>Everything kept is let go (world unload, the cuts switched off).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _free.Clear();
                _kept = 0;
            }
        }
    }

    /// <summary>
 /// S10 round 3 milestone F (the terrain hosts' worst frames): the step names of the two S8a hosts' lap clocks (s10.perf
 /// <c>terrain</c>, s10.se <c>s8aSteps</c>: which step owns a worst frame) and the time slicing of Subnautica's host
 /// (env DEEPENGINEERS_S10_TERRAINSLICE on; default OFF until a scripted terrain build proves the meshes byte-identical in
 /// game). Slicing applies only to the host's two unbounded FIFO queues (the manifests read on a worker, the validated
 /// files): every item is still taken, in the same order, the first of each frame always; after it, more only while the
 /// host has spent less than <see cref="BudgetMs"/> this frame. An item taken a frame later is the same as an item that
 /// arrived a frame later (their arrival is a worker's timing already). The notice ring is never sliced (its overflow
 /// raises a resync). Pure (S10PerfRulesTests).
 /// </summary>
    public static class S10TerrainSlice
    {
        public const string Env = "DEEPENGINEERS_S10_TERRAINSLICE", BudgetEnv = "DEEPENGINEERS_S10_TERRAINBUDGETMS";
        public const double DefaultBudgetMs = 4.0;
        private static volatile bool _on = S10AllocCuts.Parse(Environment.GetEnvironmentVariable(Env), false);

        public static bool On { get { return _on; } set { _on = value; } }
        public static double BudgetMs = ParseMs(Environment.GetEnvironmentVariable(BudgetEnv), DefaultBudgetMs);

        public static double ParseMs(string s, double dflt)
        {
            double v;
            return !string.IsNullOrEmpty(s) && double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0 ? v : dflt;
        }

        /// <summary>Subnautica's S8aTerrain.UpdateCore steps, in call order.</summary>
        public static readonly string[] SnSteps = { "header", "manifests", "notices", "validated", "jobs", "cells", "flora", "spawns", "deposits", "sample", "rows" };
        public const int SnHeader = 0, SnManifests = 1, SnNotices = 2, SnValidated = 3, SnJobs = 4, SnCells = 5, SnFlora = 6, SnSpawns = 7, SnDeposits = 8, SnSample = 9, SnRows = 10;

        /// <summary>SE's S8aHost.Tick steps, in call order.</summary>
        public static readonly string[] SeSteps = { "stream", "overlay", "oreOverlay", "mirrors", "feed", "charts", "window", "rows", "crater", "outcrops", "commands", "sample" };
        public const int SeStream = 0, SeOverlay = 1, SeOreOverlay = 2, SeMirrors = 3, SeFeed = 4, SeCharts = 5, SeWindow = 6, SeRows = 7, SeCrater = 8, SeOutcrops = 9, SeCommands = 10, SeSample = 11;

        /// <summary>One more queued item this frame? Always without slicing; with it, the frame's first item, then only under
 /// the budget.</summary>
        public static bool Take(bool on, int takenThisFrame, double spentMs, double budgetMs)
        {
            return !on || takenThisFrame <= 0 || spentMs < budgetMs;
        }
    }

    // =================================================================================================================
 // S10 part 3 (large grids), pass 1: rules the plugins apply on top of the pinned S5/S7/S7p ones, each behind its own
 // switch (the presets of part 6 set the numbers). Pure, Unity- and SE-free (S10PerfRulesTests).
 // =================================================================================================================

 /// <summary>
 /// The clipboard's paste preview (S7p P7 mirrors a preview grid as a hologram up to S7pScene.MaxProjectedBlocks = 20,000
 /// blocks per grid): S10 admits it only while the whole clipboard holds at most <c>cap</c> blocks. cap &lt; 0: no S10 limit
 /// (S7p's own rules alone); cap 0: no paste preview mirrored at all.
 /// </summary>
    public static class S10PastePreview
    {
        public const int DefaultCap = 1000;
        public const string Env = "DEEPENGINEERS_S10_PREVIEWCAP";

        public static bool Admit(int clipboardBlocks, int cap)
        {
            if (cap < 0) return true;
            if (cap == 0) return false;
            return clipboardBlocks <= cap;
        }
    }

    /// <summary>
 /// Subnautica's per-cell level of detail (part 3): a cell is built coarse (the S7t impostor path: large parts only, one
 /// flat material per colour, no shadows) beyond <c>lodM</c> of the camera, and, for a grid of at least <c>hullParts</c>
 /// mirrored parts (the merged-hull mode of a 30,000-block grid), beyond <c>hullNearM</c>. Hysteresis <c>hystM</c> both
 /// ways so a camera on the edge does not rebuild a cell every frame. A distance that is not known keeps the state.
 /// </summary>
    public static class S10Lod
    {
        public const double DefaultLodM = 150.0, DefaultHystM = 15.0, DefaultHullNearM = 40.0;
        public const int DefaultHullParts = 40000;

        public static bool Coarse(double distM, bool wasCoarse, int gridParts, double lodM, double hystM, int hullParts, double hullNearM)
        {
            if (double.IsNaN(distM) || distM < 0) return wasCoarse;
            double edge = hullParts > 0 && gridParts >= hullParts ? Math.Min(lodM, hullNearM) : lodM;
            if (edge <= 0) return false;
            return wasCoarse ? distM > edge - hystM : distM > edge + hystM;
        }
    }

    /// <summary>
 /// The cell build order (part 3): within <c>nearM</c> of the camera the S5 rule (the newest dirty cell first, then the
 /// nearest: an edit beside the player shows at once), every near cell before any far one, far cells nearest first (a
 /// pasted ship fills in from the camera outward). Negative: a first.
 /// </summary>
    public static class S10BuildOrder
    {
        public const double DefaultNearM = 50.0;

        public static int Compare(long aDirtyFrame, double aDist, long bDirtyFrame, double bDist, double nearM)
        {
            bool an = aDist <= nearM, bn = bDist <= nearM;
            if (an != bn) return an ? -1 : 1;
            if (an)
            {
                int c = bDirtyFrame.CompareTo(aDirtyFrame);
                return c != 0 ? c : aDist.CompareTo(bDist);
            }
            int d = aDist.CompareTo(bDist);
            return d != 0 ? d : bDirtyFrame.CompareTo(aDirtyFrame);
        }
    }

    /// <summary>
 /// The per-grid light cap (part 3): SE's light table keeps the nearest 64 lights within 60 m (pinned S7 rule); S10 lets

 /// floods Subnautica's renderer nor takes every slot from the grids around it. <paramref name="sortedOrder"/> lists the
 /// candidates nearest first; the kept candidates go to <paramref name="kept"/> in the same order. perOwner &lt;= 0: no cap.
 /// </summary>
    public static class S10LightCap
    {
        public const int DefaultPerGrid = 12;

        public static int Keep(int[] sortedOrder, int n, long[] ownerOf, int perOwner, int max, int[] kept, Dictionary<long, int> scratch)
        {
            if (sortedOrder == null) throw new ArgumentNullException("sortedOrder");
            if (ownerOf == null) throw new ArgumentNullException("ownerOf");
            if (kept == null) throw new ArgumentNullException("kept");
            Dictionary<long, int> used = scratch ?? new Dictionary<long, int>();
            used.Clear();
            int k = 0, limit = Math.Min(max, kept.Length), count = Math.Min(n, sortedOrder.Length);
            for (int idx = 0; idx < count && k < limit; idx++)
            {
                int c = sortedOrder[idx];
                long owner = ownerOf[c];
                int u;
                used.TryGetValue(owner, out u);
                if (perOwner > 0 && u >= perOwner) continue;
                used[owner] = u + 1;
                kept[k++] = c;
            }
            return k;
        }
    }

    /// <summary>
 /// Round 2 milestone B, the curtain (addendum 2, a hard requirement: on 4546B, under the switch altitude, the player sees
 /// Subnautica's picture or a loading curtain, never SE's picture). Pure rules; SE draws the curtain (S10Se).
 /// <list type="bullet">
 /// <item><see cref="InZone"/>: where Subnautica's picture belongs: the view machine stepping (in world, site bound), and
 /// under the Site policy below the Approach band's enter height (ViewThresholds.ApproachEnterY), under the Waterline
 /// policy while the view is Underwater or Surfacing.</item>
 /// <item><see cref="SnShown"/>: what the player sees is Subnautica's: its picture requested (ViewOutputs.Picture), SE out of
 /// the way (design F's margins on, or A′'s lowered window), the renderer alive (PeerState.Alive) and Active, and after a
 /// jump, a load, a new renderer generation or a link recovery, its streaming confirmed again (no AreaBuilt/Settle block:
 /// <paramref name="awaitingSettle"/>).</item>
 /// <item><see cref="Alpha"/>: 1 while wanted; after Subnautica shows, eased out over <c>fadeMs</c>.</item>
 /// </list>
 /// </summary>
    public static class S10Curtain
    {
        /// <summary>

 /// curtain only beyond the view's own resettle distance (ViewStateDriver.ResettleJumpM, 150 m: a real teleport, which the
 /// view also reports as the Teleport event). SE's free spectator lands where it last was, tens of metres away, and the
 /// third-person camera pops out to its far point in one frame: both stay inside the resident area, which is built.
 /// </summary>
        public const double DefaultFadeMs = 250.0, DefaultJumpM = 150.0, DefaultGiveUpS = 60.0;
        /// <summary>
 /// What holds the curtain after a jump: the area around the camera not built yet.
 /// respawn read as a loading screen; episodes p50 2.5 s, max 7.3 s): the streaming queue's tail (ViewBlock.Settle, the far
 /// cells still reloading) no longer holds it; Subnautica draws what is built and the far cells pop in.
 /// </summary>
        public const uint SettleBlocks = ViewBlock.AreaBuilt;
        public const uint EventsNeedingSettle = ViewEvents.Teleport | ViewEvents.NewGeneration | ViewEvents.LinkRecovery | ViewEvents.WorldLoad;

        public static bool InZone(bool machineActive, uint picturePolicy, double camY, double bandEnterM, uint viewState)
        {
            if (!machineActive || double.IsNaN(camY)) return false;
            if (picturePolicy == ViewPicture.Waterline) return viewState == ViewState.Underwater || viewState == ViewState.Surfacing;
            return camY < bandEnterM;
        }

        public static bool SnShown(bool picture, bool marginsOn, bool seLowered, uint peer, uint snPhase, uint blocked, bool awaitingSettle)
        {
            if (!picture || !(marginsOn || seLowered)) return false;
            if (peer != PeerState.Alive || snPhase != SnPhase.Active) return false;
            return !awaitingSettle || (blocked & SettleBlocks) == 0;
        }

        /// <summary>The curtain's opacity: 1 while wanted, then 1 − t/fadeMs after Subnautica showed (0 once faded).</summary>
        public static double Alpha(bool wanted, double msSinceShown, double fadeMs)
        {
            if (wanted) return 1.0;
            if (fadeMs <= 0 || double.IsNaN(msSinceShown) || msSinceShown >= fadeMs) return 0.0;
            return msSinceShown <= 0 ? 1.0 : 1.0 - msSinceShown / fadeMs;
        }

        /// <summary>Why the curtain is up (row and on-screen line): the renderer gone or stalled, starting, streaming, off.</summary>
        public static string Cause(bool picture, bool marginsOn, bool seLowered, uint peer, uint snPhase, bool awaitingSettle)
        {
            if (peer == PeerState.Exited) return "exited";
            if (peer == PeerState.Stalled) return "stalled";
            if (peer != PeerState.Alive) return "starting";
            if (snPhase != SnPhase.Active) return "warming";
            if (awaitingSettle) return "streaming";
            if (!picture) return "picture";
            if (!(marginsOn || seLowered)) return "margins";
            return "none";
        }
    }

    /// <summary>One preset of <see cref="S10Presets"/>.</summary>
    public struct S10Preset
    {
        public string Name;
        public int FpsCap, LightsPerGrid, PreviewCap;
        public double ColliderNearM, LodM, Life;
        public bool Lod;
    }

    /// <summary>
 /// Round 2 milestone D: the performance presets of both plugins (ROADMAP §14 "### S10": low/mid/high with an auto-pick
 /// at install, the 30 fps cap on shared GPUs). Pinned by the canonical text below (CONTRACT §5.4 "S10 presets"; a changed
 /// number changes <see cref="ConventionsHash"/> and turns S10PerfRulesTests red; the Python twin in
 /// tests/s10/analyze_s10.py hashes the same text). <c>mid</c> is every switch's built-in default (no preset = mid).
 /// <c>life</c> is the spawn-volume factor of R7 for the S8a spawns (exported to the environment as
 /// DEEPENGINEERS_S10_LIFE; the S8a side reads it when it lands).
 /// </summary>
    public static class S10Presets
    {
        public const string Env = "DEEPENGINEERS_S10_PRESET";
        public const ulong ConventionsHash = 0x42e90bc51d397ecfUL;

        public static readonly S10Preset Low = new S10Preset { Name = "low", FpsCap = 30, LightsPerGrid = 6, PreviewCap = 500, ColliderNearM = 100, Lod = true, LodM = 100, Life = 0.5 };
        public static readonly S10Preset Mid = new S10Preset { Name = "mid", FpsCap = 0, LightsPerGrid = 12, PreviewCap = 1000, ColliderNearM = 150, Lod = false, LodM = 150, Life = 1.0 };
        public static readonly S10Preset High = new S10Preset { Name = "high", FpsCap = 0, LightsPerGrid = 24, PreviewCap = 3000, ColliderNearM = 250, Lod = false, LodM = 150, Life = 1.5 };

        public static string CanonicalText()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("ext s10 presets 1\n");
            foreach (S10Preset p in new[] { Low, Mid, High }) sb.Append(Line(p)).Append('\n');
            sb.Append("auto sharedGpu or ramGB<20 -> low; ramGB>=30 and dedicatedGpu -> high; else mid\n");
            return sb.ToString();
        }

        private static string Line(S10Preset p)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return "preset " + p.Name + " fpsCap=" + p.FpsCap.ToString(ci) + " lightsPerGrid=" + p.LightsPerGrid.ToString(ci) + " previewCap=" + p.PreviewCap.ToString(ci) +
                   " colliderNearM=" + p.ColliderNearM.ToString("0", ci) + " lod=" + (p.Lod ? "on" : "off") + " lodM=" + p.LodM.ToString("0", ci) + " life=" + p.Life.ToString("0.0", ci);
        }

        /// <summary>The preset of a name (low | mid | high, any case); mid for anything else.</summary>
        public static S10Preset Of(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            return n == "low" ? Low : n == "high" ? High : Mid;
        }

        /// <summary>The auto-pick (the launcher's, at the first play start): a shared GPU or under 20 GB of RAM → low; 30 GB or
 /// more with a dedicated GPU → high; otherwise mid.</summary>
        public static string Auto(bool sharedGpu, double ramGB)
        {
            if (sharedGpu || ramGB < 20) return "low";
            if (ramGB >= 30) return "high";
            return "mid";
        }

        /// <summary>The preset's cap on Subnautica's frame-rate decision: vSync off and the cap when it would draw faster.</summary>
        public static void CapFrameRate(int cap, ref int vSyncCount, ref int targetFrameRate)
        {
            if (cap <= 0) return;
            if (vSyncCount > 0) { vSyncCount = 0; targetFrameRate = cap; return; }
            if (targetFrameRate <= 0 || targetFrameRate > cap) targetFrameRate = cap;
        }
    }

    /// <summary>One block of the scripted paste's ship (grid cell, kind index into <see cref="S10Blueprint.Subtypes"/>).</summary>
    public struct S10Block
    {
        public int X, Y, Z;
        public byte Kind;
        public S10Block(int cellX, int cellY, int cellZ, byte kind) { X = cellX; Y = cellY; Z = cellZ; Kind = kind; }
    }

    /// <summary>
 /// The scripted paste's ship (perf scene "paste"): a large-grid hull of <see cref="Width"/> × <see cref="Height"/> blocks
 /// in cross-section (50 × 30 m) and as long as the block count needs (10,000 blocks: 101 slices, about 250 m, 204
 /// interior lights), the shape of a
 /// Workshop-class build and all vanilla 1×1×1 blocks (no third-party asset): a light-armour shell with a heavy-armour
 /// keel, two interior decks, a conveyor spine and an interior light every 6 m on each deck. Slices are filled from the
 /// bow; the last slice is cut at exactly <c>blocks</c>. The grid is centred on cell (0, 0, 0) along every axis so the
 /// clipboard's aim point is the ship's middle. Deterministic (the Python twin counts the same kinds).
 /// </summary>
    public static class S10Blueprint
    {
        public const byte Armor = 0, HeavyArmor = 1, Light = 2, Conveyor = 3;
        public const int Width = 20, Height = 12, MaxBlocks = 60000;

        /// <summary>SE definition ids "TypeId/SubtypeId".</summary>
        public static readonly string[] Subtypes =
        {
            "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", "MyObjectBuilder_CubeBlock/LargeHeavyBlockArmorBlock",
            "MyObjectBuilder_InteriorLight/SmallLight", "MyObjectBuilder_Conveyor/LargeBlockConveyor",
        };

        private static readonly int[] Decks = { 4, 8 };

        /// <summary>The kind of cell (x, y, z) of an open-ended hull, or -1 when the cell stays empty.</summary>
        public static int KindAt(int x, int y, int z)
        {
            bool shell = x == 0 || x == Width - 1 || y == 0 || y == Height - 1;
            if (shell) return y == 0 ? HeavyArmor : Armor;
            foreach (int d in Decks)
            {
                if (y == d) return Armor;
                if (y == d + 1 && x % 6 == 3 && z % 3 == 0) return Light;
            }
            if (y == 2 && x == Width / 2) return Conveyor;
            return -1;
        }

        /// <summary>Exactly <paramref name="blocks"/> blocks (1 … <see cref="MaxBlocks"/>), bow first, centred on (0, 0, 0).</summary>
        public static List<S10Block> Layout(int blocks)
        {
            int n = Math.Max(1, Math.Min(MaxBlocks, blocks));
            var raw = new List<S10Block>(n);
            for (int z = 0; raw.Count < n; z++)
            {
                for (int y = 0; y < Height && raw.Count < n; y++)
                    for (int x = 0; x < Width && raw.Count < n; x++)
                    {
                        int k = KindAt(x, y, z);
                        if (k >= 0) raw.Add(new S10Block(x, y, z, (byte)k));
                    }
            }
            int zMax = raw[raw.Count - 1].Z;
            int ox = Width / 2, oy = Height / 2, oz = zMax / 2;
            for (int i = 0; i < raw.Count; i++)
            {
                S10Block b = raw[i];
                raw[i] = new S10Block(b.X - ox, b.Y - oy, b.Z - oz, b.Kind);
            }
            return raw;
        }

        /// <summary>Blocks per kind of a layout (the twin's vector).</summary>
        public static int[] Counts(List<S10Block> layout)
        {
            var c = new int[Subtypes.Length];
            foreach (S10Block b in layout) c[b.Kind]++;
            return c;
        }
    }
}
