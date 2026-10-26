using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using UnityEngine;
using UnityEngine.Profiling;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S10 part 1 (docs/S10-BRIEF.md, the measurement rig): the per-second <c>s10.perf</c> row of Subnautica, in every flavour
 /// and every phase (a row costs one sort of at most 1,024 doubles and one StringBuilder per second; nothing per frame
 /// allocates). Fields: <c>frames</c>, <c>fps</c> (frames over the row's own span), <c>frameP50Ms/P95/P99/Max/Mean</c>
 /// from Unity's <c>Time.unscaledDeltaTime</c>
 /// the stage clock of the
 /// mirror and its neighbours (<see cref="S10PerfRules.StageNames"/>: ms summed over the second, the worst frame, calls),
 /// the process memory (K32GetProcessMemoryInfo: private and working set), the managed heap and the GC counts, and the
 /// applied frame-rate pair (QualitySettings.vSyncCount, Application.targetFrameRate). No GPU time: Subnautica's player
 /// docs in launcher_s10 --notes).
 /// The hooks are timestamps only (<see cref="T"/> / <see cref="Add"/>): Bridge.Tick around the S3 mirror, S6, S7
 /// interiors and the S8a window, MirrorConsumer.Tick around the kit and placement steps. Main thread only; never throws.
 /// </summary>
    internal static class S10Sn
    {
        private static readonly S10FrameWindow Window = new S10FrameWindow();
        private static readonly S10StageClock Clock = new S10StageClock();
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static readonly double[] SumMs = new double[S10PerfRules.StageCount], MaxMs = new double[S10PerfRules.StageCount];
        private static readonly int[] Calls = new int[S10PerfRules.StageCount];
        private static ulong _windowStartMs, _rows;
        private static long _lastHeap, _allocBytes;
        private static bool _failed;

        // ------------------------------------------------------------------------------------------ milestone A2: memory

 /// <summary>The s10.mem row's period (round 2 A2: the S10.MEM.* rows, the 16 GB acceptance).</summary>
        public const ulong MemEveryMs = 10000;
        private static ulong _nextMemMs, _lastFaults, _lastFaultsMs;
        /// <summary>The mirror (set by MirrorConsumer.Tick every frame) whose meshes and kit the memory row counts.</summary>
        internal static MirrorConsumer Mirror;

        /// <summary>Chunk meshes keep no CPU copy (round 2 A2; env DEEPENGINEERS_S10_MESHCPU keep restores readable meshes):
 /// Unity holds a CPU copy of every readable mesh beside the GPU one, and nothing of ours reads a chunk mesh back
 /// after its upload (placement probes and bounds travel in the build result). A rebuild of a non-readable chunk
 /// takes a new Mesh object (MirrorConsumer.ApplyCell).</summary>
        public static bool FreeMeshCpu = !string.Equals((Environment.GetEnvironmentVariable("DEEPENGINEERS_S10_MESHCPU") ?? "").Trim(), "keep", StringComparison.OrdinalIgnoreCase);
        public static long MeshesFreed;

        /// <summary>
 /// s10.mem every 10 s (round 2 A2): process private / working set / page faults per second (soft and hard together:
 /// K32GetProcessMemoryInfo's PageFaultCount), Unity's own totals
 /// Subnautica's loaded batch roots
 /// and the mirror's share: chunk meshes (vertex 48 B + index bytes; doubled while
 /// readable), the prepared kit models, the texture sets, the views. Main thread; never throws.
 /// </summary>
        private static void MemRow(ulong now)
        {
            long priv, ws, faults;
            ProcessMemory(out priv, out ws, out faults);
            double faultRate = _lastFaultsMs > 0 && now > _lastFaultsMs ? (faults - (long)_lastFaults) * 1000.0 / (now - _lastFaultsMs) : double.NaN;
            _lastFaults = (ulong)Math.Max(0, faults);
            _lastFaultsMs = now;
            long uAlloc = -1, uRes = -1, monoUsed = -1, monoHeap = -1;
            try { uAlloc = Profiler.GetTotalAllocatedMemoryLong(); uRes = Profiler.GetTotalReservedMemoryLong(); monoUsed = Profiler.GetMonoUsedSizeLong(); monoHeap = Profiler.GetMonoHeapSizeLong(); }
            catch (Exception) { }
            int batches = -1;
            try
            {
                LargeWorldStreamer lws = LargeWorldStreamer.main;
                var roots = lws != null ? lws.batch2root : null;
                batches = roots != null ? roots.Count : -1;
            }
            catch (Exception) { }
            MirrorConsumer.S10MemoryStats m = default(MirrorConsumer.S10MemoryStats);
            try { if (Mirror != null) m = Mirror.S10Memory(); } catch (Exception) { }
            JsonOut j = S5Rows.Begin("s10.mem");
            j.Num("privateMB", priv >= 0 ? priv / 1048576.0 : double.NaN).Num("workingSetMB", ws >= 0 ? ws / 1048576.0 : double.NaN)
             .Num("pageFaultsPerSec", faultRate).Num("unityAllocatedMB", uAlloc >= 0 ? uAlloc / 1048576.0 : double.NaN)
             .Num("unityReservedMB", uRes >= 0 ? uRes / 1048576.0 : double.NaN).Num("monoUsedMB", monoUsed >= 0 ? monoUsed / 1048576.0 : double.NaN)
             .Num("monoHeapMB", monoHeap >= 0 ? monoHeap / 1048576.0 : double.NaN).Num("snBatchRoots", (long)batches)
             .Num("views", (long)m.Views).Num("chunks", (long)m.Chunks).Num("chunksReadable", (long)m.ReadableChunks)
             .Num("mirrorMeshMB", m.MeshBytes / 1048576.0).Num("mirrorMeshCpuMB", m.MeshCpuBytes / 1048576.0)
             .Num("kitModels", (long)m.Models).Num("kitMB", m.KitBytes / 1048576.0).Num("textureSets", (long)m.TextureSets)
             .Num("rawTextures", (long)m.RawTextures).Bool("freeMeshCpu", FreeMeshCpu).Num("meshesFreed", MeshesFreed);
            S5Rows.End(j);
        }

        // ------------------------------------------------------------------------------------------ part 3 settings

 /// <summary>Per-cell LOD and the merged hull (<see cref="S10Lod"/>; env DEEPENGINEERS_S10_LOD on|off, de.s10 {lod}).</summary>
 /// <summary>Round 2 D: the preset (env DEEPENGINEERS_S10_PRESET; the launcher's auto-pick); a knob's own variable wins.</summary>
        public static readonly S10Preset Preset = S10Presets.Of(Environment.GetEnvironmentVariable(S10Presets.Env));
        public static bool LodOn = EnvOn("DEEPENGINEERS_S10_LOD", Preset.Lod); // the low preset turns the 150 m LOD on (mid/high: off, as part 3 left it)
 /// <summary>The preset's frame-rate cap (low: 30, for shared GPUs; env DEEPENGINEERS_S10_FPSCAP overrides, 0 = none).</summary>
        public static int FpsCap = (int)EnvNum("DEEPENGINEERS_S10_FPSCAP", Preset.FpsCap);
        public static long FpsCapped;

        /// <summary>S2Host (after the recipe choice; S10 hook): the preset's cap on the frame-rate pair S2 applies.</summary>
        internal static void CapRecipe(ref RecipeDecision d)
        {
            if (FpsCap <= 0) return;
            int vs = d.VSyncCount, fps = d.TargetFrameRate;
            S10Presets.CapFrameRate(FpsCap, ref vs, ref fps);
            if (vs == d.VSyncCount && fps == d.TargetFrameRate) return;
            d.VSyncCount = vs;
            d.TargetFrameRate = fps;
            d.Why = (d.Why ?? "") + " +S10 cap " + FpsCap;
            FpsCapped++;
        }

        /// <summary>Round 2 D (the collector): per-second budgets of the high-volume telemetry rows (s7.fx.thruster, s7.sky):
 /// a row not written allocates nothing. Counted in s10.perf (rowsSkipped).</summary>
        public const int RowThruster = 0, RowSky = 1, RowProxy = 2, RowSlots = 3, RowsPerSecond = S10AllocCuts.RowsPerSecond; // round 3 E: s6.proxy teleport rows
        private static readonly int[] _rowsUsed = new int[RowSlots];
        public static long RowsSkipped;

        public static bool TakeRow(int slot)
        {
            if (!EditsOn || slot < 0 || slot >= RowSlots) return true;
            Roll();
            if (_rowsUsed[slot] < RowsPerSecond) { _rowsUsed[slot]++; return true; }
            RowsSkipped++;
            return false;
        }
        /// <summary>The distance-first build order (<see cref="S10BuildOrder"/>; env DEEPENGINEERS_S10_ORDER on|off, de.s10 {order}).</summary>
        public static bool OrderOn = EnvOn("DEEPENGINEERS_S10_ORDER", true);
        public static double LodM = EnvNum("DEEPENGINEERS_S10_LODM", Preset.LodM), HystM = S10Lod.DefaultHystM;
        public static double HullNearM = EnvNum("DEEPENGINEERS_S10_HULLNEARM", S10Lod.DefaultHullNearM), NearM = S10BuildOrder.DefaultNearM;
        public static int HullParts = (int)EnvNum("DEEPENGINEERS_S10_HULLPARTS", S10Lod.DefaultHullParts);
        /// <summary>Frames between two LOD sweeps of a grid's cells.</summary>
        public const int LodSweepFrames = 15;
        public static long LodSwapsToCoarse, LodSwapsToFull, CoarseCellsPosted;
        public static int CoarseCells;

        /// <summary>
 /// Round 2 milestone A (the edit path): the mirror's per-part and per-chunk log output under a per-second budget
 /// (env DEEPENGINEERS_S10_EDITS off restores every line).
 /// 2,180 s5.edit rows and 2,098 "DE.Mirror material" lines in one second: strings and JSON on the main thread, and
 /// garbage for Subnautica's collector (a collection every 3.4 s). The first lines of every second are kept (the S3/S5
 /// oracles read them); the rest are counted in s10.perf (editRowsSkipped, mirrorLinesSkipped).
 /// </summary>
        public static bool EditsOn = EnvOn("DEEPENGINEERS_S10_EDITS", true);   // on (21:5x, finished): the 15:15:27 burst wrote 4,300 rows/lines in a second; scripted S3/S5 synth runs above 64 edits/s set it off
        public const int EditRowsPerSecond = 64, MirrorLinesPerSecond = 32;
        public static long EditRowsSkipped, MirrorLinesSkipped;
        private static int _editRows, _mirrorLines;
        private static float _budgetT = -10f;

        private static void Roll()
        {
            float t = Time.unscaledTime;
            if (t - _budgetT < 1f) return;
            _budgetT = t;
            _editRows = 0;
            _mirrorLines = 0;
            Array.Clear(_rowsUsed, 0, _rowsUsed.Length);
        }

        /// <summary>S5Mirror (s5.edit rows): true while this second's budget allows one more row.</summary>
        public static bool TakeEditRow()
        {
            if (!EditsOn) return true;
            Roll();
            if (_editRows < EditRowsPerSecond) { _editRows++; return true; }
            EditRowsSkipped++;
            return false;
        }

        /// <summary>MirrorConsumer (the per-chunk material lines, the per-cell built and placed lines, the per-model line):
 /// true while this second's budget allows one more line. Warnings never go through it.</summary>
        public static bool TakeMirrorLine()
        {
            if (!EditsOn) return true;
            Roll();
            if (_mirrorLines < MirrorLinesPerSecond) { _mirrorLines++; return true; }
            MirrorLinesSkipped++;
            return false;
        }

        /// <summary>The glass tint (env DEEPENGINEERS_S10_GLASS off restores the plain Seamoth clone): Subnautica's base windows read
 /// under water by a faint blue-green body and a raised opacity; the clone's <c>_Color</c> is lerped toward
 /// <see cref="GlassTint"/> and its alpha raised to at least <see cref="GlassMinAlpha"/>. Only properties the shader has are set.</summary>
        public static bool GlassOn = EnvOn("DEEPENGINEERS_S10_GLASS", true);
        public static readonly Color GlassTint = new Color(0.55f, 0.80f, 0.85f, 1f);
        public const float GlassMinAlpha = 0.35f, GlassTintWeight = 0.6f;
        public static long GlassTinted;

        public static void TintGlass(Material g)
        {
            if (!GlassOn || g == null) return;
            try
            {
                if (g.HasProperty("_Color"))
                {
                    Color c = Color.Lerp(g.GetColor("_Color"), GlassTint, GlassTintWeight);
                    c.a = Mathf.Max(g.GetColor("_Color").a, GlassMinAlpha);
                    g.SetColor("_Color", c);
                }
                if (g.HasProperty("_SpecColor")) g.SetColor("_SpecColor", Color.Lerp(g.GetColor("_SpecColor"), Color.white, 0.5f));
                GlassTinted++;
            }
            catch (Exception e) { DeLog.WarnOnce("s10.glass", "DE.S10 glass tint not applied: " + e.GetType().Name + ": " + e.Message); }
        }

        private static bool EnvOn(string name, bool dflt)
        {
            string s = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(s)) return dflt;
            s = s.Trim().ToLowerInvariant();
            return s == "on" || s == "1" || s == "true" || (s != "off" && s != "0" && s != "false" && dflt);
        }

        private static double EnvNum(string name, double dflt)
        {
            double v;
            string s = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrEmpty(s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : dflt;
        }

        /// <summary>de.s10 on Subnautica (dev builds): {lod: on|off, order: on|off, lodM, hullParts, hullNearM, nearM}; the reply carries
 /// the settings and the LOD counters. A changed LOD setting takes effect at the next sweep (cells flip and rebuild).</summary>
        public static bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != "de.s10") return false;
            try
            {
                if (!BuildFlavor.Dev) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", UserCommandRules.RefusalText); return true; }
                Dictionary<string, object> a = S3Args.ToDict(req.Args);
                object v;
                if (a.TryGetValue("lod", out v)) LodOn = OnOff(v, LodOn);
                if (a.TryGetValue("order", out v)) OrderOn = OnOff(v, OrderOn);
                if (a.TryGetValue("lodM", out v)) LodM = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (a.TryGetValue("hullParts", out v)) HullParts = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                if (a.TryGetValue("hullNearM", out v)) HullNearM = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (a.TryGetValue("nearM", out v)) NearM = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (a.TryGetValue("allocCuts", out v)) { S10AllocCuts.On = OnOff(v, S10AllocCuts.On); if (!S10AllocCuts.On) TextureImport.S10Readbacks.Clear(); } // round 3 E: before/after in one session
                if (a.TryGetValue("allocStages", out v)) AllocStages = OnOff(v, AllocStages);
                if (a.TryGetValue("terrainSlice", out v)) S10TerrainSlice.On = OnOff(v, S10TerrainSlice.On); // round 3 F
                if (a.TryGetValue("terrainBudgetMs", out v)) S10TerrainSlice.BudgetMs = Math.Max(0.1, Convert.ToDouble(v, CultureInfo.InvariantCulture));
                reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("lod", LodOn).Bool("order", OrderOn).Num("lodM", LodM).Num("hullParts", (long)HullParts)
                     .Num("hullNearM", HullNearM).Num("nearM", NearM).Num("coarseCells", (long)CoarseCells).Num("toCoarse", LodSwapsToCoarse)
                     .Num("toFull", LodSwapsToFull).Num("coarsePosted", CoarseCellsPosted).Num("rows", (long)_rows)
                     .Bool("allocCuts", S10AllocCuts.On).Bool("allocStages", AllocStages)
                     .Bool("terrainSlice", S10TerrainSlice.On).Num("terrainBudgetMs", S10TerrainSlice.BudgetMs).Num("terrainDeferred", TerrainDeferred);
            }
            catch (Exception e) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "de.s10: " + e.GetType().Name + ": " + e.Message); }
            return true;
        }

        private static bool OnOff(object v, bool dflt)
        {
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            return s == "on" || s == "true" || s == "1" ? true : s == "off" || s == "false" || s == "0" ? false : dflt;
        }

        /// <summary>A Stopwatch timestamp (the start of a measured step); round 3 E: the Mono heap's used size beside it.</summary>
        public static long T()
        {
            long ts = Stopwatch.GetTimestamp();
            if (AllocStages) Alloc.Mark(ts, GC.GetTotalMemory(false));
            return ts;
        }

        /// <summary>The step that started at <paramref name="t0"/> ends now (stage slot of <see cref="S10PerfRules"/>).</summary>
        public static void Add(int stage, long t0)
        {
            Clock.Add(stage, Stopwatch.GetTimestamp() - t0);
            if (AllocStages) Alloc.Add(stage, t0, GC.GetTotalMemory(false));
        }

        // ------------------------------------------------------------------------------------------ round 3 E: allocations

 /// <summary>
 /// Round 3 E: the heap growth of each stage (s10.perf <c>stages.*.allocKB</c>; env DEEPENGINEERS_S10_ALLOCSTAGES off:
 /// no heap read per stage). Mono's used size (GC.GetTotalMemory(false), the counter monoAllocMBps steps) is
 /// process-wide: a stage's number includes what worker threads allocated meanwhile, and a collection inside a stage
 /// counts nothing (a lower bound). The cuts themselves are <see cref="S10AllocCuts"/> (de.s10 {allocCuts}).
 /// </summary>
        public static bool AllocStages = EnvOn("DEEPENGINEERS_S10_ALLOCSTAGES", true);
        private static readonly S10AllocMeter Alloc = new S10AllocMeter(S10PerfRules.StageCount);
        private static readonly double[] AllocKb = new double[S10PerfRules.StageCount];

        // ------------------------------------------------------------------------------------------ round 3 F: the terrain host

 /// <summary>
 /// Round 3 F: the S8a terrain host's steps (S8aTerrain.UpdateCore, <see cref="S10TerrainSlice.SnSteps"/>) on a lap clock
 /// (s10.perf <c>terrain</c>: ms per second and the worst frame per step), and the time slicing of its two FIFO queues
 /// (<see cref="S10TerrainSlice"/>; env DEEPENGINEERS_S10_TERRAINSLICE on, de.s10 {terrainSlice}; default off).
 /// Main thread only.
 /// </summary>
        private static readonly S10StageClock TerrainClock = new S10StageClock(S10TerrainSlice.SnSteps.Length);
        private static readonly double[] TerrainSum = new double[S10TerrainSlice.SnSteps.Length], TerrainMax = new double[S10TerrainSlice.SnSteps.Length];
        private static readonly int[] TerrainCalls = new int[S10TerrainSlice.SnSteps.Length];
        private static long _terrainLapT;
        public static long TerrainDeferred;
        /// <summary>S8aTerrain.UpdateCore starts: the first lap mark and the frame's start stamp (for <see cref="TerrainTake"/>).</summary>
        public static long TerrainStart()
        {
            long t = Stopwatch.GetTimestamp();
            _terrainLapT = t;
            return t;
        }

        /// <summary>The time since the previous lap mark goes to step <paramref name="step"/>.</summary>
        public static void TerrainLap(int step)
        {
            long t = Stopwatch.GetTimestamp();
            if (_terrainLapT != 0) TerrainClock.Add(step, t - _terrainLapT);
            _terrainLapT = t;
        }

        /// <summary>One more item of a sliced queue this frame (an item is waiting): <see cref="S10TerrainSlice.Take"/> on the
 /// host's time since <paramref name="t0"/>; a refusal is counted (the item stays first in its queue).</summary>
        public static bool TerrainTake(int takenThisFrame, long t0)
        {
            if (S10TerrainSlice.Take(S10TerrainSlice.On, takenThisFrame, (Stopwatch.GetTimestamp() - t0) * MsPerTick, S10TerrainSlice.BudgetMs)) return true;
            TerrainDeferred++;
            return false;
        }

        /// <summary>Once per frame from Bridge.Tick (before the hosts tick): the frame time, the previous frame's stages
 /// closed, the row once a second.</summary>
        public static void Frame(ulong now)
        {
            if (_failed) return;
            try
            {
                Clock.EndFrame();
                TerrainClock.EndFrame(); // round 3 F
                Window.Add(Time.unscaledDeltaTime * 1000.0);
                long heap = GC.GetTotalMemory(false); // round 2 D: the managed allocation rate (positive steps of the heap between collections)
                if (_lastHeap > 0 && heap > _lastHeap) _allocBytes += heap - _lastHeap;
                _lastHeap = heap;
                if (_windowStartMs == 0) { _windowStartMs = now; return; }
                if (now - _windowStartMs < 1000) return;
                Row(now);
                if (now >= _nextMemMs) { _nextMemMs = now + MemEveryMs; MemRow(now); }
            }
            catch (Exception e)
            {
                _failed = true;
                DeLog.Warn("DE.S10 s10.perf off after an exception (measurement only; nothing else changes): " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Row(ulong now)
        {
            double spanS = (now - _windowStartMs) / 1000.0;
            _windowStartMs = now;
            S10FrameStats f = Window.Take();
            Clock.Take(MsPerTick, SumMs, MaxMs, Calls);
            Alloc.Take(AllocKb);
            TerrainClock.Take(MsPerTick, TerrainSum, TerrainMax, TerrainCalls); // round 3 F
            long priv = -1, ws = -1, faults;
            ProcessMemory(out priv, out ws, out faults);
            JsonOut j = S5Rows.Begin(S10PerfRules.RowSn);
            j.Num("seq", ++_rows).Num("spanS", spanS).Num("frames", (long)f.Frames).Num("fps", spanS > 0 ? f.Frames / spanS : double.NaN)
             .Num("frameP50Ms", f.P50).Num("frameP95Ms", f.P95).Num("frameP99Ms", f.P99).Num("frameMaxMs", f.Max).Num("frameMeanMs", f.Mean);
            j.Obj("stages");
            for (int i = 0; i < S10PerfRules.StageCount; i++)
            {
                j.Obj(S10PerfRules.StageNames[i]).Num("ms", SumMs[i]).Num("maxFrameMs", MaxMs[i]).Num("calls", (long)Calls[i]);
                if (AllocStages) j.Num("allocKB", AllocKb[i]); // round 3 E: heap growth during the stage (all threads, a lower bound)
                j.EndObj();
            }
            j.EndObj();
            j.Obj("terrain"); // round 3 F: the S8a terrain host's steps (ms over the second, the worst frame)
            for (int i = 0; i < S10TerrainSlice.SnSteps.Length; i++)
                if (TerrainCalls[i] > 0) j.Obj(S10TerrainSlice.SnSteps[i]).Num("ms", TerrainSum[i]).Num("maxFrameMs", TerrainMax[i]).EndObj();
            j.EndObj();
            j.Bool("terrainSlice", S10TerrainSlice.On).Num("terrainBudgetMs", S10TerrainSlice.BudgetMs).Num("terrainDeferred", TerrainDeferred);
            j.Num("privateMB", priv >= 0 ? priv / 1048576.0 : double.NaN).Num("workingSetMB", ws >= 0 ? ws / 1048576.0 : double.NaN)
             .Num("gcHeapMB", GC.GetTotalMemory(false) / 1048576.0).Num("gc0", (long)GC.CollectionCount(0)).Num("gc1", (long)GC.CollectionCount(1))
             .Num("gc2", (long)GC.CollectionCount(2)).Num("vSyncCount", (long)QualitySettings.vSyncCount).Num("targetFrameRate", (long)Application.targetFrameRate)
             .Num("gpuMs", double.NaN).Bool("lod", LodOn).Bool("order", OrderOn).Num("coarseCells", (long)CoarseCells)
             .Num("lodToCoarse", LodSwapsToCoarse).Num("lodToFull", LodSwapsToFull).Num("coarsePosted", CoarseCellsPosted).Num("glassTinted", GlassTinted)
             .Bool("edits", EditsOn).Num("editRowsSkipped", EditRowsSkipped).Num("mirrorLinesSkipped", MirrorLinesSkipped)
             .Str("preset", Preset.Name).Num("fpsCap", (long)FpsCap).Num("fpsCapped", FpsCapped).Num("rowsSkipped", RowsSkipped)
             .Num("monoAllocMBps", spanS > 0 ? _allocBytes / 1048576.0 / spanS : double.NaN)
             .Bool("allocCuts", S10AllocCuts.On).Bool("allocStages", AllocStages).Num("groupsBuilt", MeshBuilder.S10GroupsBuilt).Num("groupsReused", MeshBuilder.S10GroupsReused)
             .Num("readbacksRented", TextureImport.S10Readbacks.Rented).Num("readbacksReused", TextureImport.S10Readbacks.Reused); // round 3 E
            _allocBytes = 0;
            S5Rows.End(j);
        }

        // ------------------------------------------------------------------------------------------ process memory

        [StructLayout(LayoutKind.Sequential)]
        private struct Counters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [DllImport("kernel32.dll")]
        private static extern bool K32GetProcessMemoryInfo(IntPtr hProcess, out Counters memoryCounters, uint sizeBytes);

        private static readonly IntPtr Self = new IntPtr(-1); // the current-process pseudo handle (GetCurrentProcess's value; never closed)

        private static void ProcessMemory(out long priv, out long ws, out long faults)
        {
            priv = -1;
            ws = -1;
            faults = -1;
            try
            {
                Counters c;
                if (K32GetProcessMemoryInfo(Self, out c, (uint)Marshal.SizeOf(typeof(Counters))))
                {
                    priv = (long)c.PrivateUsage.ToUInt64();
                    ws = (long)c.WorkingSetSize.ToUInt64();
                    faults = c.PageFaultCount;
                }
            }
            catch (Exception) { }
        }
    }
}
