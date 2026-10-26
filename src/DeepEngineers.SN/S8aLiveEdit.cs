using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using DeepEngineers.Wire;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UWE;
using WorldStreaming;

namespace DeepEngineers.SN
{
    /// <summary>
 /// docs/S8A-BRIEF.md "Stage D"): a new version of a
 /// served batch that Subnautica already holds is applied in memory, never by unloading the batch and hiding its cells.
 /// <list type="number">
 /// <item><b>Diff</b> (a worker): the batch file the streamer last loaded and the new version, parsed (<see cref="S8aBatchFile"/>);
 /// the octrees whose nodes changed (<see cref="S8aLivePatch.ChangedOctrees"/>).</item>
 /// <item><b>Patch</b>
 /// for the batch the streamer holds
 /// (<c>GetBatch</c> on the streaming thread, then on the I/O thread still that id and <c>IsLoaded</c>), each changed octree in
 /// the loader's set (window ids all 125, crater ids inside <c>octreeBounds</c>) gets <c>Octree.Read(PooledBinaryReader,
 /// Int3, SplitNativeArrayPool&lt;byte&gt)</c> over its record (u16 count + nodes) and <c>UnloadChildren(minLod, allocator)</c>:
 /// the loader's own two calls on the new file's bytes, so the memory equals a reload (S8a.ND.roundtrip).</item>
 /// <item><b>Swap</b> (Stage D (1)): every loaded cell
 /// over the edit box is rebuilt beside its old mesh: <c>MeshBuilder.Reset</c>/<c>DoThreadablePart</c> on the
 /// meshing threads, <c>DoFinalizePart</c> on the build-layers pump
 /// (ClipmapCell.BeginBuildLayersAsync), the collider baked, then in
 /// one main-thread step the cell's <c>chunk</c> becomes the new one (shown at once when the cell is Visible) and the old one is
 /// hidden and returned to the pool as <c>BeginDestroyChunk</c> does (without hiding the cell's entities). The cell
 /// never leaves its state: no frame without a mesh. A cell that changed state meanwhile keeps its own mesh (the new one is
 /// returned); a cell that was building is rebuilt once it is loaded.</item>
 /// <item><b>Levels</b> (Stage D (2)): level 0 at once; levels 1–3 at most once per <see cref="S8aLivePatch.HigherLevelsEveryMs"/> per
 /// batch while edits keep coming (the last box rebuilt when due).</item>
 /// </list>
 /// The file stays the source of truth (SE wrote it; the GetPath postfix serves it to every later load). Anything this path
 /// cannot do (no previous load, a parse error, a member missing) falls back to the batch reload. Env
 /// <c>DEEPENGINEERS_S8A_LIVE=off</c> turns it off.
 /// </summary>
    internal sealed class S8aLiveEdit
    {
        public const string EnvLive = "DEEPENGINEERS_S8A_LIVE";
        public const ulong DeferredRetryMs = 100, DeferredGiveUpMs = 4000, JobGiveUpMs = 10000;

        private static readonly FieldInfo CellStateField = AccessTools.Field(typeof(ClipmapCell), "state");
        private static readonly AccessTools.FieldRef<ClipmapCell, ClipmapChunk> CellChunk = SafeRef<ClipmapCell, ClipmapChunk>("chunk");
        private static readonly AccessTools.FieldRef<BatchOctrees, SplitNativeArrayPool<byte>> BatchAllocator = SafeRef<BatchOctrees, SplitNativeArrayPool<byte>>("allocator");
        private static readonly AccessTools.FieldRef<Octree, NativeArray<byte>> OctreeData = SafeRef<Octree, NativeArray<byte>>("data");

        private static AccessTools.FieldRef<T, F> SafeRef<T, F>(string name)
        {
            try { return AccessTools.FieldRefAccess<T, F>(name); }
            catch (Exception e) { DeLog.Warn("DE.S8a MISSING " + typeof(T).Name + "." + name + ": " + e.GetType().Name + " (live edits off: batch reloads)"); return null; }
        }

        /// <summary>Every private member the path needs resolved.</summary>
        public static bool Ready { get { return CellStateField != null && CellChunk != null && BatchAllocator != null && OctreeData != null; } }

        public static bool Enabled { get { return Ready && EnvOn(EnvLive, true); } }

        private static bool EnvOn(string name, bool def)
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(name); } catch (Exception) { }
            if (string.IsNullOrEmpty(v)) return def;
            v = v.Trim().ToLowerInvariant();
            return v == "on" || v == "1" || v == "true" || v == "yes" || (v != "off" && v != "0" && v != "false" && v != "no" && def);
        }

        /// <summary>The cell's private <c>chunk</c> (null when the member is missing or the cell has no mesh).</summary>
        public static ClipmapChunk ChunkOf(ClipmapCell cell)
        {
            try { return CellChunk != null && cell != null ? CellChunk(cell) : null; }
            catch (Exception) { return null; }
        }

        public static int CellState(ClipmapCell cell)
        {
            try { return Convert.ToInt32(CellStateField.GetValue(cell)); }
            catch (Exception) { return -1; }
        }

        // ------------------------------------------------------------------------------------------------- the job

        public sealed class Job
        {
            public Int3Key Id;
            public string NewPath, OldPath, Why;
            public int[] Mins, Maxs;
            public S8aReloadTracker.Item Item;
            public ulong StartMs;
            public bool HigherLevels, SkipPatch, LevelZero = true;
            public volatile int Stage;              // 0 diff queued, 1 diff done, 2 patching, 3 patched, 4 cells planned
            public volatile string Error;
            public byte[][] Records;               // per file index: the new record (null = unchanged)
            public int Changed;
            public int PatchesPending, Patched, NotLoaded;
            public int Rebuilds, RebuildsDone, Discarded, Swapped, NoMesh;
            /// <summary>The level-0 rebuilds planned and done; <see cref="Level0Ms"/> = start to the last of them (S8a.ND.live's measure), 0 until then.</summary>
            public int Rebuilds0, Done0;
            public long Level0Ms;
            /// <summary>
 /// a test run (S8a.ND.live: where a level-0 cell's time goes), Stopwatch ticks summed over the job's level-0 rebuilds: waiting
 /// for a meshing thread, meshing (MeshBuilder.Reset + DoThreadablePart), waiting for the finishing step, finishing
 /// (DoFinalizePart + BakeMesh + the swap). FirstDoneTicks: plan to the first level-0 swap.
 /// </summary>
            public long L0QueueTicks, L0MeshTicks, L0WaitTicks, L0FinalTicks, PlannedTicks, FirstDoneTicks;
            public readonly List<ClipmapCell> Deferred = new List<ClipmapCell>();
            public ulong DeferredSinceMs, DeferredNextMs;
            public int Replans;
            public bool Done;
            public WorldStreamer Ws;
            public long DiffMs;
            public long PatchMs;
            public ulong PatchedAtMs;
        }

        private sealed class Rebuild
        {
            public Job Job;
            public ClipmapCell Cell;
            public Int3 CellId;
            public ClipmapLevel Level;
            public bool L0;
            public WorldStreaming.MeshBuilder Builder;
            public long QueuedAt, MeshStart, MeshedAt;
        }

        private static readonly Task.Function PatchOnStreamingFn = PatchOnStreaming;
        private static readonly Task.Function PatchOnIoFn = PatchOnIo;
        private static readonly Task.Function PlanOnStreamingFn = PlanOnStreaming;
        private static readonly Task.Function BeginRebuildFn = BeginRebuild;
        private static readonly Task.Function EndRebuildFn = EndRebuild;

        public static long Jobs, Fallbacks, OctreesPatched, CellsSwapped, CellsDiscarded, CellsDeferred, Errors;
        public static long LastJobMs;

        // nd rerun (session 20261023-213453: level-0 swap p95 1,172 ms): the meshing threads are FIFO, so a job's level-0
 // cells waited behind the previous job's 53 level 1-3 rebuilds (the drill line crossing two batches, a job every 0.75 s). Level 0
 // goes first now: a level-0 rebuild is enqueued at once, a level 1-3 one waits here until no level-0 rebuild is pending
 // (PumpHigher, main thread); a cell already waiting is not queued twice (the waiting rebuild reads the newest octrees when it runs)
        private static int _l0Pending;
        // a test run (nd 20261024-003439, S8a.ND.live FAIL p95 1,672 ms): the finishing step went through Subnautica's BuildLayers queue,
 // pumped at max((int)(numBuildLayersPerSecond * deltaTime), 1) items per frame: one level-0
 // cell per frame in every job at 68-167 fps (180 cells = 183 frames), and behind the previous job's level 1-3 swaps in that
 // FIFO (the first level-0 swap 234-734 ms after the plan). It runs on our own main-thread pump now (PumpFinalize, called from
 // S8aTerrain's frame step like the BuildLayers coroutine: the same thread): level 0 first, then levels 1-3, at least one per
 // frame and up to the budget (env DEEPENGINEERS_S8A_LIVE_FINALIZE_MS, default 3 ms; 0 = Subnautica's queue, the old path)
        public const string EnvFinalizeMs = "DEEPENGINEERS_S8A_LIVE_FINALIZE_MS";
        public static readonly double FinalizeBudgetMs = ReadBudgetMs();
        private static readonly object FinalLock = new object();
        private static readonly Queue<Rebuild> _final0 = new Queue<Rebuild>(), _finalHigher = new Queue<Rebuild>();
        public static long Finalized, FinalizeTicks, FinalizeMaxTicks;
        public static int FinalizeMaxPerFrame;
        public static int FinalWaiting { get { lock (FinalLock) return _final0.Count + _finalHigher.Count; } }

        private static double ReadBudgetMs()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(EnvFinalizeMs);
                double ms;
                if (!string.IsNullOrEmpty(v) && double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ms))
                    return System.Math.Max(0.0, System.Math.Min(50.0, ms));
            }
            catch (Exception) { }
            return 3.0;
        }

        private static long Now() { return System.Diagnostics.Stopwatch.GetTimestamp(); }

        /// <summary>Stopwatch ticks to milliseconds.</summary>
        public static double TicksToMs(long ticks) { return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

        /// <summary>
 /// Main thread, per frame (before <see cref="PumpHigher"/>): the meshed rebuilds finished and swapped, level 0 first, at least
 /// one and then while the frame's budget lasts.
 /// </summary>
        public static void PumpFinalize()
        {
            if (FinalizeBudgetMs <= 0) return;
            long t0 = Now(), budget = (long)(FinalizeBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            int n = 0;
            while (true)
            {
                Rebuild r;
                lock (FinalLock)
                {
                    if (_final0.Count > 0) r = _final0.Dequeue();
                    else if (_finalHigher.Count > 0) r = _finalHigher.Dequeue();
                    else break;
                }
                long a = Now();
                EndRebuild(null, r);
                long took = Now() - a;
                FinalizeTicks += took;
                if (took > FinalizeMaxTicks) FinalizeMaxTicks = took;
                Finalized++;
                n++;
                if (Now() - t0 >= budget) break;
            }
            if (n > FinalizeMaxPerFrame) FinalizeMaxPerFrame = n;
        }

        /// <summary>The host is going: the meshed rebuilds not finished return their builders (their cells keep their meshes).</summary>
        public static void DropFinalQueue()
        {
            var left = new List<Rebuild>();
            lock (FinalLock)
            {
                left.AddRange(_final0);
                left.AddRange(_finalHigher);
                _final0.Clear();
                _finalHigher.Clear();
            }
            foreach (Rebuild r in left)
            {
                try { if (r.Builder != null) r.Level.streamer.meshBuilderPool.Return(r.Builder); } catch (Exception) { }
                r.Builder = null;
                if (r.L0) Interlocked.Decrement(ref _l0Pending);
            }
        }
        private static readonly object HigherLock = new object();
        private static readonly Queue<Rebuild> _higher = new Queue<Rebuild>();
        private static readonly HashSet<ClipmapCell> _higherCells = new HashSet<ClipmapCell>();
        public static long HigherMerged, HigherPumped;
        public static int HigherWaiting { get { lock (HigherLock) return _higher.Count; } }
        public static int Level0Pending { get { return Volatile.Read(ref _l0Pending); } }

        /// <summary>Main thread, per frame: up to <paramref name="max"/> waiting level 1-3 rebuilds into the meshing threads, only while no level-0 rebuild is pending.</summary>
        public static void PumpHigher(int max)
        {
            for (int k = 0; k < max && Volatile.Read(ref _l0Pending) == 0; k++)
            {
                Rebuild r;
                lock (HigherLock)
                {
                    if (_higher.Count == 0) return;
                    r = _higher.Dequeue();
                    _higherCells.Remove(r.Cell);
                }
                HigherPumped++;
                r.QueuedAt = Now();
                r.Cell.level.streamer.meshingThreads.Enqueue(BeginRebuildFn, null, r);
            }
        }

        /// <summary>A rebuild is over (swapped, discarded or failed): the level-0 count and the job's.</summary>
        private static void Finish(Rebuild r)
        {
            if (r.L0)
            {
                Interlocked.Increment(ref r.Job.Done0);
                Interlocked.Decrement(ref _l0Pending);
            }
            Interlocked.Increment(ref r.Job.RebuildsDone);
        }

        /// <summary>Main thread: starts the diff of a job (a worker reads both files).</summary>
        public static void Start(Job job, int bx, int by, int bz)
        {
            Interlocked.Increment(ref Jobs);
            if (job.SkipPatch) { job.Stage = 3; return; }
            string oldPath = job.OldPath, newPath = job.NewPath;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    byte[] a = File.ReadAllBytes(oldPath), b = File.ReadAllBytes(newPath);
                    S8aBatchFile fa, fb;
                    string err;
                    if (!S8aBatchFile.TryParse(a, a.Length, bx, by, bz, out fa, out err)) { job.Error = "old file: " + err; }
                    else if (!S8aBatchFile.TryParse(b, b.Length, bx, by, bz, out fb, out err)) { job.Error = "new file: " + err; }
                    else
                    {
                        List<int> changed = S8aLivePatch.ChangedOctrees(fa, fb);
                        var rec = new byte[S8aBatchFile.OctreesPerBatch][];
                        foreach (int i in changed) rec[i] = S8aLivePatch.ReadRecord(fb, i);
                        job.Records = rec;
                        job.Changed = changed.Count;
                    }
                }
                catch (Exception e) { job.Error = e.GetType().Name + ": " + e.Message; }
                job.DiffMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                job.Stage = 1;
            });
        }

        /// <summary>Main thread, after the diff: the patch is queued on the streaming thread (the batches looked up there).</summary>
        public static void QueuePatch(Job job)
        {
            job.Stage = 2;
            job.Ws.octreesStreamer.streamingThread.Enqueue(PatchOnStreamingFn, null, job);
        }

        /// <summary>Streaming thread: the batch each octree streamer holds for the id, its patch queued on that streamer's I/O thread.</summary>
        private static void PatchOnStreaming(object owner, object state)
        {
            var job = (Job)state;
            try
            {
                var id = new Int3(job.Id.X, job.Id.Y, job.Id.Z);
                var targets = new List<KeyValuePair<BatchOctreesStreamer, BatchOctrees>>(2);
                foreach (BatchOctreesStreamer s in new[] { job.Ws.octreesStreamer, job.Ws.lowDetailOctreesStreamer })
                {
                    if (s == null) continue;
                    BatchOctrees b = s.GetBatch(id);
                    if (b == null || !b.IsLoaded() || b.reloadId.HasValue) { job.NotLoaded++; continue; }
                    targets.Add(new KeyValuePair<BatchOctreesStreamer, BatchOctrees>(s, b));
                }
                job.PatchesPending = targets.Count;
                if (targets.Count == 0) { job.Stage = 3; return; }
                foreach (KeyValuePair<BatchOctreesStreamer, BatchOctrees> t in targets)
                    t.Key.ioThread.Enqueue(PatchOnIoFn, t.Key, new object[] { job, t.Value });
            }
            catch (Exception e) { job.Error = "streaming: " + e.GetType().Name + ": " + e.Message; job.Stage = 3; }
        }

        /// <summary>
 /// The streamer's I/O thread: the changed octrees of the loader's set read from their records with the loader's own calls
 /// (<c>Octree.Read</c> + <c>UnloadChildren(minLod)</c>); skipped when the batch moved to another id or is no longer loaded.
 /// </summary>
        private static void PatchOnIo(object owner, object state)
        {
            var s = (BatchOctreesStreamer)owner;
            var args = (object[])state;
            var job = (Job)args[0];
            var b = (BatchOctrees)args[1];
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                Int3 id = b.id;
                if (id.x != job.Id.X || id.y != job.Id.Y || id.z != job.Id.Z || !b.IsLoaded()) { Interlocked.Increment(ref job.NotLoaded); }
                else
                {
                    SplitNativeArrayPool<byte> allocator = BatchAllocator(b);
                    bool window = S8aWindowPlan.IsWindowId(id.x, id.y, id.z);
                    int n = 0;
                    for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++)
                    {
                        byte[] rec = job.Records[i];
                        if (rec == null) continue;
                        int ix, iy, iz;
                        S8aBatchFile.OctreeCoords(i, out ix, out iy, out iz);
                        var item = new Int3(ix, iy, iz);
                        if (!window && !s.octreeBounds.Contains(item + id * S8aConventions.SnWorld.OctreesPerBatch)) continue;
                        Octree octree = b.GetOctree(item);
                        using (var reader = new PooledBinaryReader(new MemoryStream(rec, false)))
                            octree.Read(reader, id, allocator);
                        octree.UnloadChildren(s.minLod, allocator);
                        n++;
                    }
                    Interlocked.Add(ref job.Patched, n);
                    Interlocked.Add(ref OctreesPatched, n);
                }
            }
            catch (Exception e) { job.Error = "io: " + e.GetType().Name + ": " + e.Message; Interlocked.Increment(ref Errors); }
            Interlocked.Add(ref job.PatchMs, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency);
            if (Interlocked.Decrement(ref job.PatchesPending) == 0) job.Stage = 3;
        }

        /// <summary>Main thread, once patched: the cells over the box are planned on the streaming thread (their state is read there).</summary>
        public static void QueuePlan(Job job)
        {
            job.Stage = 31;
            job.PatchedAtMs = DeepEngineers.Wire.Native.Win32.TickCount64();
            job.Ws.clipmapStreamer.streamingThread.Enqueue(PlanOnStreamingFn, null, job);
        }

        /// <summary>Streaming thread: per cell of the planned levels over the box, a rebuild beside its mesh, a deferral, or nothing.</summary>
        private static void PlanOnStreaming(object owner, object state)
        {
            var job = (Job)state;
            try
            {
                ClipmapStreamer clip = job.Ws.clipmapStreamer;
                int levels = 0;
                for (int l = 0; l < 4; l++) if (clip.GetLevel(l) != null) levels = l + 1;
                int from = job.LevelZero ? 0 : 1, to = job.HigherLevels ? levels - 1 : System.Math.Min(0, levels - 1);
                for (int l = from; l <= to; l++)
                {
                    ClipmapLevel lv = clip.GetLevel(l);
                    if (lv == null) continue;
                    int[] cmin, cmax;
                    S8aCellRange.Range(l, lv.cellSize, job.Mins, job.Maxs, out cmin, out cmax);
                    for (int x = cmin[0]; x <= cmax[0]; x++)
                        for (int y = cmin[1]; y <= cmax[1]; y++)
                            for (int z = cmin[2]; z <= cmax[2]; z++)
                            {
                                ClipmapCell cell = lv.GetCell(new Int3(x, y, z));
                                if (cell != null) PlanCell(job, cell);
                            }
                }
            }
            catch (Exception e) { job.Error = "plan: " + e.GetType().Name + ": " + e.Message; Interlocked.Increment(ref Errors); }
            job.Stage = 4;
        }

        /// <summary>Streaming thread: the deferred (building) cells of a job checked again.</summary>
        public static void QueueDeferred(Job job, List<ClipmapCell> cells)
        {
            Interlocked.Increment(ref job.Replans);
            job.Ws.clipmapStreamer.streamingThread.Enqueue((o, st) =>
            {
                try { foreach (ClipmapCell c in (List<ClipmapCell>)st) PlanCell(job, c); }
                catch (Exception e) { job.Error = "replan: " + e.GetType().Name + ": " + e.Message; }
                finally { Interlocked.Decrement(ref job.Replans); }
            }, null, cells);
        }

        private static void PlanCell(Job job, ClipmapCell cell)
        {
            int st = CellState(cell);
            if (S8aLivePatch.Rebuildable(st))
            {
                Interlocked.Increment(ref job.Rebuilds);
                bool l0 = cell.level.id == 0;
                var r = new Rebuild { Job = job, Cell = cell, CellId = cell.id, Level = cell.level, L0 = l0, QueuedAt = Now() };
                if (l0) Interlocked.CompareExchange(ref job.PlannedTicks, r.QueuedAt, 0);
                if (l0)
                {
                    Interlocked.Increment(ref job.Rebuilds0);
                    Interlocked.Increment(ref _l0Pending);
                    cell.level.streamer.meshingThreads.Enqueue(BeginRebuildFn, null, r);
                }
                else
                {
                    bool merged;
                    lock (HigherLock)
                    {
                        merged = !_higherCells.Add(cell);
                        if (!merged) _higher.Enqueue(r);
                    }
                    if (merged)
                    {
                        Interlocked.Increment(ref HigherMerged);
                        Interlocked.Increment(ref job.RebuildsDone);    // the waiting rebuild of this cell covers this job's change
                    }
                }
            }
            else if (S8aLivePatch.Building(st))
            {
                lock (job.Deferred) job.Deferred.Add(cell);
                Interlocked.Increment(ref CellsDeferred);
            }
            // waiting for octrees: it reads the patched ones; queued, unloaded or unloading: nothing shows from it to keep
        }

        /// <summary>Meshing thread: the mesh of the cell from the patched octrees (ClipmapCell.BeginBuildMesh's two calls).</summary>
        private static void BeginRebuild(object owner, object state)
        {
            var r = (Rebuild)state;
            try
            {
                ClipmapStreamer streamer = r.Level.streamer;
                r.MeshStart = Now();
                WorldStreaming.MeshBuilder mb = streamer.meshBuilderPool.Get();
                r.Builder = mb;
                mb.Reset(r.Level.id, r.CellId, r.Level.cellSize, r.Level.settings, streamer.host.blockTypes);
                mb.DoThreadablePart(streamer.host.GetOctreesStreamer(r.Level.id), streamer.settings.collision);
                r.MeshedAt = Now();
                if (FinalizeBudgetMs > 0) { lock (FinalLock) (r.L0 ? _final0 : _finalHigher).Enqueue(r); }
                else streamer.buildLayersThread.Enqueue(EndRebuildFn, null, r);
            }
            catch (Exception e)
            {
                r.Job.Error = "mesh: " + e.GetType().Name + ": " + e.Message;
                Interlocked.Increment(ref Errors);
                if (r.Builder != null) { try { r.Level.streamer.meshBuilderPool.Return(r.Builder); } catch (Exception) { } }
                Interlocked.Increment(ref r.Job.Discarded);
                Finish(r);       // a failed rebuild is done too (else the job waits for JobGiveUpMs)
            }
        }

        /// <summary>
 /// Main thread (the build-layers pump): the new chunk finalised, its collider baked, then swapped in for the cell's chunk in
 /// this one step when the cell is still the one planned and loaded; else returned to the pool.
 /// </summary>
        private static void EndRebuild(object owner, object state)
        {
            var r = (Rebuild)state;
            ClipmapStreamer streamer = r.Level.streamer;
            WorldStreamer host = streamer.host;
            ClipmapChunk fresh = null;
            long finalStart = Now();
            try
            {
                fresh = r.Builder.DoFinalizePart(host.chunkRoot, host.terrainPoolManager);
                streamer.meshBuilderPool.Return(r.Builder);
                r.Builder = null;
                if (fresh != null)
                {
                    if (fresh.collision != null && fresh.collision.sharedMesh != null) Physics.BakeMesh(fresh.collision.sharedMesh.GetInstanceID(), false);
                    fresh.gameObject.SetActive(true);
                    if (fresh.collision != null && fresh.collision.sharedMesh != null)
                    {
                        fresh.collision.gameObject.SetActive(true);
                        fresh.collision.sharedMesh.Clear();
                    }
                }
                int st = CellState(r.Cell);
                bool same = r.Cell.level == r.Level && r.Cell.id.Equals(r.CellId);
                if (!same || !S8aLivePatch.Rebuildable(st))
                {
                    if (fresh != null) ReturnChunk(host, fresh);
                    Interlocked.Increment(ref r.Job.Discarded);
                    Interlocked.Increment(ref CellsDiscarded);
                    return;
                }
                ClipmapChunk old = CellChunk(r.Cell);
                CellChunk(r.Cell) = fresh;
                if (fresh != null)
                {
                    if (st == 6) fresh.Show();                       // Visible: at once, no fade (the old mesh goes in the same frame)
                    else fresh.Hide(r.Level.settings.keepGrassVisible);   // Loaded or hidden by a parent/children: as the old one was
                }
                else r.Job.NoMesh++;
                if (old != null)
                {
                    old.HideTerrain();
                    old.HideGrass();
                    ReturnChunk(host, old);
                }
                Interlocked.Increment(ref r.Job.Swapped);
                Interlocked.Increment(ref CellsSwapped);
            }
            catch (Exception e)
            {
                r.Job.Error = "swap: " + e.GetType().Name + ": " + e.Message;
                Interlocked.Increment(ref Errors);
                if (r.Builder != null) { try { streamer.meshBuilderPool.Return(r.Builder); } catch (Exception) { } }
            }
            finally
            {
                if (r.L0 && r.MeshedAt != 0)
                {
                    long end = Now();
                    Interlocked.Add(ref r.Job.L0QueueTicks, r.MeshStart - r.QueuedAt);
                    Interlocked.Add(ref r.Job.L0MeshTicks, r.MeshedAt - r.MeshStart);
                    Interlocked.Add(ref r.Job.L0WaitTicks, finalStart - r.MeshedAt);
                    Interlocked.Add(ref r.Job.L0FinalTicks, end - finalStart);
                    if (r.Job.PlannedTicks != 0) Interlocked.CompareExchange(ref r.Job.FirstDoneTicks, end - r.Job.PlannedTicks, 0);
                }
                Finish(r);
            }
        }

        /// <summary>ClipmapCell.BeginDestroyChunk's pool return, without OnCellUnloaded (the cell stays loaded).</summary>
        private static void ReturnChunk(WorldStreamer host, ClipmapChunk chunk)
        {
            TerrainPoolManager pool = host.terrainPoolManager;
            if (!pool.meshPoolingEnabled) WorldStreaming.MeshBuilder.DestroyMeshes(chunk);
            foreach (TerrainChunkPiece piece in chunk.chunkPieces) pool.Return(piece);
            pool.Return(chunk);
        }

        // ------------------------------------------------------------------------------------------------- the round trip

 /// <summary>
 /// S8a.ND.roundtrip (main thread, dev command): the octrees each streamer holds for batch <paramref name="id"/> against the
 /// served file loaded the loader's way (<see cref="S8aLivePatch.LoadedBytes"/> with the streamer's minLod): octrees
 /// compared and those that differ.
 /// </summary>
        public static void Compare(WorldStreamer ws, Int3Key key, string path, out int compared, out int differ, out int streamers, out string note)
        {
            compared = differ = streamers = 0;
            note = null;
            if (!Ready) { note = "members missing"; return; }
            if (path == null || !File.Exists(path)) { note = "no served file"; return; }
            byte[] bytes = File.ReadAllBytes(path);
            S8aBatchFile f;
            string err;
            if (!S8aBatchFile.TryParse(bytes, bytes.Length, key.X, key.Y, key.Z, out f, out err)) { note = "parse: " + err; return; }
            var id = new Int3(key.X, key.Y, key.Z);
            bool window = S8aWindowPlan.IsWindowId(key.X, key.Y, key.Z);
            foreach (BatchOctreesStreamer s in new[] { ws.octreesStreamer, ws.lowDetailOctreesStreamer })
            {
                if (s == null) continue;
                BatchOctrees b = s.GetBatch(id);
                if (b == null || !b.IsLoaded()) continue;
                streamers++;
                for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++)
                {
                    int ix, iy, iz;
                    S8aBatchFile.OctreeCoords(i, out ix, out iy, out iz);
                    var item = new Int3(ix, iy, iz);
                    if (!window && !s.octreeBounds.Contains(item + id * S8aConventions.SnWorld.OctreesPerBatch)) continue;
                    if (!f.Has(i)) continue;
                    byte[] want = S8aLivePatch.LoadedBytes(f, i, s.minLod);
                    NativeArray<byte> data = OctreeData(b.GetOctree(item));
                    byte[] have = data.IsCreated && data.Length > 0 ? data.ToArray() : new byte[0];
                    compared++;
                    // night queue (S8a.ND.roundtrip 121/375 "differ"): the octree's array comes from the octree pool
 // (BatchOctreesAllocator.octreePool = SplitNativeArrayPool<byte>(1, 32, 4, 0, 0, 2048, 3, 0): 32 bytes or more
 // from the big pool, whose arrays are whole 2,048-byte buckets, up to 3 buckets larger, NativeArrayPool.Get /
 // ArrayPoolBase.SizeForBucket), so it is longer than the octree and its tail is stale: the octree is the first
 // want.Length bytes (Octree.Read copies exactly those; the traversal never reads past the nodes)
                    if (have.Length < want.Length) { differ++; continue; }
                    for (int k = 0; k < want.Length; k++) if (have[k] != want[k]) { differ++; break; }
                }
            }
        }
    }
}
