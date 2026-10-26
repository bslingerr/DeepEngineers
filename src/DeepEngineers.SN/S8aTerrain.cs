using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;
using UWE;
using WorldStreaming;
using C7S8a = DeepEngineers.Wire.S8aConventions.C7S8a;
using C7S8aFlags = DeepEngineers.Wire.S8aConventions.C7S8aFlags;
using NoticeKind = DeepEngineers.Wire.S8aConventions.NoticeKind;
using NoticeLayer = DeepEngineers.Wire.S8aConventions.NoticeLayer;
using QueueC = DeepEngineers.Wire.S8aConventions.Queue;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S8a on the Subnautica side (ROADMAP "### S8a" SN plugin; CONTRACT §3.3, §5.4 "S8a conventions"; ARCHITECTURE §5.17):
 /// the store view, the notice reader, the reload scheduler on the streaming thread, the clipmap cell reloads, the C7
 /// S8a block, the rows and <c>de.s8a</c>. Stage A: the drill layer (S8a-1).
 /// <list type="bullet">
 /// <item><b>Feed.</b> <see cref="S8aFeedReader"/> registered as a C2 extension reader (<c>EntityTableReader.Extensions</c>, the
 /// B4 hook of MirrorWire, copied inside the C2 seqlock window). The header names the store (<c>storeKey</c>): the
 /// drill manifest is read on a worker and becomes the <see cref="S8aStoreView"/>; a Resync (full SE queue), a new
 /// stream or a <c>StoreReset</c> re-reads it.</item>
 /// <item><b>A notice</b> (BatchWritten, drill layer) names a batch, a version and the changed box: the file is validated on a
 /// worker (<see cref="S8aNoticeRule"/>: exists, parses, size and FNV when the manifest lists it), then served by the
 /// <c>GetPath</c> postfix (<see cref="S8aPatches"/>) and reloaded: <c>BatchOctreesStreamer.GetBatch(Int3 id)</c>
 /// →
 /// <c>BatchOctrees.Reload(Int3 newId)</c> ( BatchOctrees.cs; with the batch's own id: unload, then load again through
 /// GetPath) on the streaming thread (<c>BatchOctreesStreamer.streamingThread</c>, <c>UWE.IThread.Enqueue(Task.Function
 /// Task.
 /// machines are not thread-safe); once <c>BatchOctrees.IsLoaded</c> is true again, <c>ClipmapCell.Reload(Int3
 /// newId)</c> for every level 0–3 cell of <c>ClipmapStreamer.GetLevel(int)</c> →
 /// <c>ClipmapLevel.GetCell(Int3)</c> whose range (<c>ClipmapLevel.GetCellRange</c>, <see cref="S8aCellRange"/>)
 /// meets the notice box (a loaded cell does not rebuild on OnRangeLoaded: only WaitingForOctrees cells react, hence
 /// the explicit reload; capped at <c>Queue.MaxCellReloadsPerNotice</c>); applied = <c>ClipmapStreamer.IsRangeActiveAndBuilt(Int3.Bounds)</c>
 /// over the box. A batch the ring does not hold needs no reload (its next load takes the new path).</item>
 /// <item><b>C7</b> (<see cref="FillStatus"/>): version 1, flags (StoreOpen, GetPathPatched, Refused), reloadsPending, ackSeq
 /// (received and scheduled), appliedSeq (loaded and built), stream, resyncEpoch, chartSeqApplied (the header's chartSeq
 /// echoed: no charts before S8a-2), batchesServed, reloadFailures, lastLatencyMs, nearPendingSeen (0), storeKey.</item>
 /// <item><b>Fail-safe</b> (CONTRACT §3.3 (2), (3)): a file that fails the check is never served (the previous version or the
 /// install stands); a reload not done in 5 s is retried once, at 10 s it counts as applied with a failure
 /// (<see cref="S8aReloadTracker"/>). Everything runs in one feature guard: the first exception disables S8a only.</item>
 /// <item><c>de.s8a {action: status | probe {sn} | verify {bx, by, bz} | reload {all | bx, by, bz} | rays {columns, label} |
 /// spawns}</c> (C11, dev builds); <c>rays</c> forwards to the S4 raycast service (<c>de.s4 probe</c> with the label as the
 /// batch id, so the <c>s4.probe</c> rows carry it).</item>
 /// </list>
 /// Rows: <c>s8a.notice</c> {snFrame, seq, kind, bx, by, bz, version, served, scheduledMs, appliedMs, cellsReloaded},
 /// <c>s8a.load</c> {snFrame, bx, by, bz, path, bytes, fnv, ok}, <c>s8a.status</c> (1 Hz), <c>s8a.rays</c> {label, columns}.
 /// Main-thread budget ≤ 0.5 ms per frame: ≤ 16 notices dequeued, the manifests and the file checks off the main thread.
 /// </summary>
    internal sealed unsafe class S8aTerrain : IDisposable
    {
        public const string Cmd = "de.s8a";
        public const int MaxNoticesPerFrame = 16;
        /// <summary>A reloaded batch whose unload was never seen: after this long a loaded batch counts as reloaded (the unload and the load fell between two frames).</summary>
        public const ulong ReloadUnseenMs = 1500;

        private static volatile S8aTerrain _active;

        private readonly byte* _base;
        private readonly uint _generation;
        private readonly S8aFeedReader _reader = new S8aFeedReader();
        private readonly S8aStoreView _view = new S8aStoreView();
        private readonly S8aReloadTracker _tracker = new S8aReloadTracker();
        private readonly ConcurrentQueue<Validation> _validated = new ConcurrentQueue<Validation>();
        private readonly ConcurrentQueue<ManifestRead> _manifests = new ConcurrentQueue<ManifestRead>();
        private readonly List<ReloadJob> _jobs = new List<ReloadJob>(16);
        // Stage D: the live edits in flight, the per-batch time of the last levels 1-3 rebuild, the boxes waiting for it
        private readonly List<S8aLiveEdit.Job> _live = new List<S8aLiveEdit.Job>(16);
        private readonly Dictionary<Int3Key, ulong> _lastHigher = new Dictionary<Int3Key, ulong>();
        private readonly Dictionary<Int3Key, int[][]> _pendingHigher = new Dictionary<Int3Key, int[][]>();
        private long _liveApplied, _liveFallbacks, _liveNotLoaded, _liveMsMax;
        private long _chartViewMoved, _jobGiveUps;
        /// <summary>A reload job's own bound (its start to its cells loaded); the tracker's per-notice give-up grows with the queue, a
 /// </summary>
        public const ulong JobGiveUpMs = 10000;
        // Stage D oracle: per-frame cell visibility over the latest edited box while switched on (de.s8a cellvis {seconds})
        private int[] _visMins, _visMaxs;
        private ulong _visUntilMs;
        private long _visRows, _visUncoveredFrames, _visRun, _visRunMax;
        private readonly List<S8aReloadTracker.Item> _waiting = new List<S8aReloadTracker.Item>(16);
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private EntityTableReader _c2;
        private Func<ControlRequest, JsonObjectWriter, ulong, bool> _s4Forward;
        private bool _registered, _disposed;
        private ulong _storeKey;
        private string _drillFolder;
        private volatile S8aManifest _manifest;
        private bool _storeOpen, _refused, _storeLogged;
        private int _manifestReads;
        private uint _chartSeqApplied;
        private ulong _nextStatusMs;
        private long _notices, _validations, _validationFailures, _reloadsScheduled, _reloadsDone, _cellsReloaded, _frames;
        private int _validating;
        private double _updateMsMax, _updateMsSum;
        private bool _nativeLast, _gateLast;
        private long _fallbacks;
        private readonly ConcurrentDictionary<Int3Key, LoadRecord> _loads = new ConcurrentDictionary<Int3Key, LoadRecord>();
        private readonly Task.Function _runJob;
        // S8a-2: the window layer, the charts, the spawns
        private readonly S8aSpawns _spawns = new S8aSpawns();
        private readonly S8aOreDeposits _deposits = new S8aOreDeposits();
        private readonly S8aSampleSpawns _sample = new S8aSampleSpawns();     // planet v2 route B: the sample chunk's entities
        private MonoBehaviour _host;
        private Vector3 _camLast;
        private bool _haveCam;
        private int _nearPendingSeen;
        private long _windowNotices, _clearedNotices, _chartSteps;
        private byte _biomeLast = 255;
        private string _snBiomeLast;
        private uSky.uSkyLight _uSkyLight;
        private static readonly int LightScalarId = Shader.PropertyToID("_UweLightScalar"), LocalLightScalarId = Shader.PropertyToID("_UweLocalLightScalar");
        /// <summary>Frames whose pose bracket was snapped to the newer tick across a chart change (CONTRACT "chart.apply"; S8a.N8.snap).</summary>
        public static long SnapFrames;

        /// <summary>The last <c>BatchOctrees.LoadOctrees</c> of a batch id: the path it loaded and when.</summary>
        private sealed class LoadRecord
        {
            public string Path;
            public ulong Ms;
            public bool Ok;
        }

        /// <summary>The host (CONTRACT: <c>new S8aTerrain(byte* sectionBase, uint generation)</c>); <see cref="Attach"/> wires the C2 reader and the S4 forwarder.</summary>
        public S8aTerrain(byte* sectionBase, uint generation)
        {
            _base = sectionBase;
            _generation = generation;
            _runJob = RunJob;
            S8aPatches.Install();
            _active = this;
            DeLog.Info("DE.S8a host start: conventions " + S8aConventions.Header + " " + DeLog.Hex(S8aConventions.Hash) +
                       (S8aConventions.Hash == S8aConventions.PinnedHash ? " (pinned)" : " (DIFFERS from the pin)") + " generation=" + generation +
                       " store=" + S8aSnPaths.Root() + " getPath=" + (S8aPatches.GetPathPatched ? "patched" : "MISSING") +
                       " feed=C2+0x" + S8aConventions.C2S8a.Offset.ToString("x") + " ack=C7+0x" + C7S8a.Offset.ToString("x") + " (" + Cmd + ")");
        }

        /// <summary>Bridge, right after construction: the C2 table reader (the notice reader is one of its extensions) and the S4 raycast forwarder (<c>de.s8a rays</c>).</summary>
        public void Attach(EntityTableReader c2, Func<ControlRequest, JsonObjectWriter, ulong, bool> s4Forward)
        {
            _c2 = c2;
            _s4Forward = s4Forward;
            if (c2 != null && !c2.Extensions.Contains(_reader)) { c2.Extensions.Add(_reader); _registered = true; }
            if (c2 == null) DeLog.Warn("DE.S8a no C2 reader (S3 not started): the notices are never read; the GetPath postfix stays inert");
        }

        /// <summary>Bridge: the MonoBehaviour that runs the spawns' prefab coroutines (S8a-2).</summary>
        public void AttachHost(MonoBehaviour host)
        {
            _spawns.Attach(host, (bx, by, bz) => { S8aServed s; return _view.TryGet(bx, by, bz, out s) ? s.Path : null; });
            _deposits.Attach(host);
            _sample.Attach(host);
            _host = host;
        }

        public S8aStoreView View { get { return _view; } }
        public uint AckSeq { get { return _tracker.AckSeq; } }
        public uint AppliedSeq { get { return _tracker.AppliedSeq; } }

        /// <summary>The window is served (the SE header's WindowOn flag and this side's loader prefix).</summary>
        public bool WindowOn { get { return S8aPatches.LoaderPatched && _reader.LastHeaderValid && _reader.LastHeader.Has(S8aConventions.C2S8aFlags.WindowOn); } }

        /// <summary>CONTRACT "chart.apply" (the S5 pose sites): a chart change lies inside the camera's bracket. Any thread.</summary>
        public static bool ChartChangedBetween(ulong olderTick, ulong newerTick)
        {
            S8aTerrain t = _active;
            return t != null && !t._disposed && t._reader.ChartChangedBetween(olderTick, newerTick);
        }

        /// <summary>CONTRACT "biome.rule" (S2's GetBiome prefix, outside the tile): the window's cameraBiome within BiomeRadiusM of the camera, "void" beyond; null = not the window's business (S2's own answer stands).</summary>
        public static string BiomeOutsideTile(Vector3 p)
        {
            S8aTerrain t = _active;
            if (t == null || t._disposed || !t._haveCam || !t.WindowOn) return null;
            if (!S8aBiomes.IsWindowColumn(S8aBiomes.BatchX(p.x), S8aBiomes.BatchZ(p.z))) return null;
            Vector3 c = t._camLast;
            return S8aBiomes.BiomeAt(p.x, p.y, p.z, c.x, c.y, c.z, t._reader.LastHeader.cameraBiome);
        }

        // ------------------------------------------------------------------------------------------ the GetPath answer (I/O thread)

 /// <summary>
 /// The store's validated path of a batch, else null (the original path stands). Any thread. A served file that is
 /// gone meanwhile falls back to the newest version still on disk,
 /// else to the install (the view entry dropped), so a crater batch never loads as water.
 /// </summary>
        public static string ServedPath(int bx, int by, int bz)
        {
            S8aTerrain t = _active;
            if (t == null || t._disposed) return null;
            string p = t._view.PathOf(bx, by, bz);
            if (p == null) return null;
            bool exists;
            try { exists = File.Exists(p); } catch (Exception) { exists = false; }
            if (exists) return p;
            Interlocked.Increment(ref t._fallbacks);
            var id = new Int3Key(bx, by, bz);
            t._view.Remove(id);
            uint version;
            string alt = S8aSnPaths.NewestExisting(t._storeKey, bx, by, bz, out version);
            // review Stage A minor: the newest version on disk is served only once validated (parse) on a worker; the install stands meanwhile
            if (alt != null && t._fallbackPending.TryAdd(id, 0)) t.StartFallbackValidation(id, alt, version);
            return null;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<Int3Key, byte> _fallbackPending = new System.Collections.Concurrent.ConcurrentDictionary<Int3Key, byte>();

        private void StartFallbackValidation(Int3Key id, string path, uint version)
        {
            Interlocked.Increment(ref _validating);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var v = new Validation { Fallback = true, Bx = id.X, By = id.Y, Bz = id.Z, Path = path };
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    byte[] file = File.Exists(path) ? File.ReadAllBytes(path) : null;
                    ulong fnv;
                    string err;
                    if (file == null) v.Error = "missing " + path;
                    else if (S8aNoticeRule.Validate(file, id.X, id.Y, id.Z, null, out fnv, out err))
                    {
                        v.Ok = true;
                        v.Served = new S8aServed { Bx = id.X, By = id.Y, Bz = id.Z, Version = version, Path = path, Bytes = file.Length, Fnv = fnv, FromNotice = false, ValidatedUtcMs = UtcMs() };
                    }
                    else v.Error = err;
                }
                catch (Exception e) { v.Error = e.GetType().Name + ": " + e.Message; }
                v.Took = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                Interlocked.Decrement(ref _validating);
                _validated.Enqueue(v);
            });
        }

        private static long UtcMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        /// <summary>Review HIGH 5: the C1 camera frames of the bracket the mirror snapped across a chart change: <c>s8a.chartsnap</c> {snFrame, preCull, older, newer, prevTick, camOlder*, camNewer*, camPrev*, chartSeq (header), chartSeqApplied}.</summary>
        public static void ChartSnapRow(CamSample s, CamBracket br, bool preCull)
        {
            S8aTerrain t = _active;
            if (t == null || t._disposed) return;
            long seq = t._reader.LastHeaderValid ? (long)t._reader.LastHeader.chartSeq : 0L;
            Row(new JsonOut().Str("t", Utc()).Num("tickMs", Win32.TickCount64()).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.chartsnap").Str("ev", "s8a.chartsnap")
                .Obj("data").Num("snFrame", DeLog.Frame).Bool("preCull", preCull).Num("older", (long)br.Older).Num("newer", (long)br.Newer).Num("prevTick", (long)s.PrevTick).Num("alpha", br.Alpha)
                .Num("camOlderX", s.OlderPx).Num("camOlderY", s.OlderPy).Num("camOlderZ", s.OlderPz).Num("camNewerX", s.NewerPx).Num("camNewerY", s.NewerPy).Num("camNewerZ", s.NewerPz)
                .Num("camPrevX", s.PrevPx).Num("camPrevY", s.PrevPy).Num("camPrevZ", s.PrevPz).Num("chartSeq", seq).Num("chartSeqApplied", (long)t._chartSeqApplied).EndObj().ToString());
        }

        /// <summary>A mirrored grid's drawn frame position and the tick it was posed at.</summary>
        public struct MirrorPose
        {
            public bool Ok;
            public float X, Y, Z;
            public ulong Tick;
        }

        public Func<ulong, MirrorPose> MirrorPoseOf;

        /// <summary>The loader postfix (I/O thread): batch <paramref name="bx"/>-<paramref name="by"/>-<paramref name="bz"/> loaded the path the GetPath postfix last answered for it.</summary>
        public static void OnBatchLoaded(int bx, int by, int bz, bool ok)
        {
            S8aTerrain t = _active;
            if (t == null || t._disposed) return;
            var id = new Int3Key(bx, by, bz);
            t._loads[id] = new LoadRecord { Path = S8aPatches.LastPathOf(id), Ms = Win32.TickCount64(), Ok = ok };
        }

        // ------------------------------------------------------------------------------------------ per frame (main thread)

        public void Update(ulong nowMs)
        {
            if (_disposed || _disabled.Contains("host")) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { UpdateCore(nowMs); }
            catch (Exception e)
            {
                _disabled.Add("host");
                DeLog.Error("DE.S8a FEATURE host DISABLED after " + e.GetType().Name + ": " + e.Message + " (the store keeps what it serves; the rest of the bridge keeps running)\n" + e);
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _frames++;
            _updateMsSum += ms;
            if (ms > _updateMsMax) _updateMsMax = ms;
        }

        private void UpdateCore(ulong nowMs)
        {
            long s10 = S10Sn.TerrainStart(); // S10 hook (round 3 F): the steps below on a lap clock (s10.perf terrain); the two FIFO queues time-sliced when DEEPENGINEERS_S10_TERRAINSLICE is on
            if (!_registered && _c2 != null) { if (!_c2.Extensions.Contains(_reader)) _c2.Extensions.Add(_reader); _registered = true; }
            // the header: the store, the stream, the chart
            if (_reader.LastHeaderValid)
            {
                S8aHeader h = _reader.LastHeader;
                if (h.storeKey != _storeKey && h.storeKey != 0) OpenStore(h.storeKey);
                _chartSeqApplied = h.chartSeq;
            }
            uint stream = _reader.CurrentStream;
            if (_reader.CurrentStream != 0 && (!_tracker.HasStream || _tracker.Stream != stream))
            {
                _tracker.Reset(stream);
                _jobs.Clear();
                foreach (S8aLiveEdit.Job lj in _live) lj.Done = true;
                _live.Clear();
                _waiting.Clear();
                DeLog.Info("DE.S8a feed stream " + stream + " (counters restart; the view follows the manifest again)");
                if (_storeKey != 0) ReadManifest("stream " + stream, true);
            }
            if (_reader.TakeResyncNeeded() && _storeKey != 0) ReadManifest("resync", true);
            S10Sn.TerrainLap(S10TerrainSlice.SnHeader); // S10 hook (round 3 F): lap mark
 // manifests read on a worker
            ManifestRead mr;
            int s10Taken = 0; // S10 hook (round 3 F): FIFO kept, the frame's first always, more under the budget when slicing is on (off: every one, as before)
            while (_manifests.TryPeek(out mr) && S10Sn.TerrainTake(s10Taken, s10) && _manifests.TryDequeue(out mr)) { s10Taken++; ApplyManifest(mr, nowMs); }
            S10Sn.TerrainLap(S10TerrainSlice.SnManifests); // S10 hook (round 3 F): lap mark
 // notices
            S8aNotice n;
            int taken = 0;
            while (taken < MaxNoticesPerFrame && _reader.TryDequeue(out n))
            {
                taken++;
                OnNotice(n, nowMs);
            }
            S10Sn.TerrainLap(S10TerrainSlice.SnNotices); // S10 hook (round 3 F): lap mark (the notice ring is never sliced)
 // validated files
            Validation v;
            s10Taken = 0; // S10 hook (round 3 F): as the manifests
            while (_validated.TryPeek(out v) && S10Sn.TerrainTake(s10Taken, s10) && _validated.TryDequeue(out v)) { s10Taken++; OnValidated(v, nowMs); }
            S10Sn.TerrainLap(S10TerrainSlice.SnValidated); // S10 hook (round 3 F): lap mark
 // reloads in flight
            PollJobs(nowMs);
            PollLive(nowMs);
            DrainWaiting(nowMs);
            List<S8aReloadTracker.Item> retry = _tracker.Tick(nowMs, _waiting.Count + _jobs.Count);
            S10Sn.TerrainLap(S10TerrainSlice.SnJobs); // S10 hook (round 3 F): lap mark
            WatchCells(nowMs);
            S8aCraterReports.Flush(_storeKey);   // Stage E: the crater's large deposits for SE's ore pockets
            if (nowMs < _visUntilMs && _visMins != null) CellVisRow(nowMs);
            S8aOffSite.Update(nowMs, _reader.LastHeaderValid && (_reader.LastHeader.flags & S8aConventions.C2S8aFlags.OffSite) != 0);
            S10Sn.TerrainLap(S10TerrainSlice.SnCells); // S10 hook (round 3 F): lap mark
            S8aFlora.SetContext(_storeKey, _reader.LastHeaderValid ? _reader.LastHeader.chartKey : 0);
            S8aFlora.Update(nowMs);
            S10Sn.TerrainLap(S10TerrainSlice.SnFlora); // S10 hook (round 3 F): lap mark
            if (!_paletteLogged) LogPalette(nowMs);
            if (retry != null) foreach (S8aReloadTracker.Item it in retry) Reschedule(it, nowMs);
            // S8a-2: window notices near the camera not yet applied ("window.ready"), the spawns
            _nearPendingSeen = CountNearPending();
            S4Rules.WindowUniverse = WindowOn;      // Stage C fix: S4's ray probe reaches the window universe while the window is served
            _spawns.M11 = S8aMutants.M11;
            if (_haveCam) _spawns.Update(nowMs, _camLast, WindowOn && !S8aMutants.M29, _reader.LastHeaderValid ? _reader.LastHeader.chartKey : 0);
            S10Sn.TerrainLap(S10TerrainSlice.SnSpawns); // S10 hook (round 3 F): lap mark
            if (_haveCam) _deposits.Update(nowMs, _camLast, _storeKey, _reader.LastHeaderValid ? _reader.LastHeader.chartKey : 0);   // planet v2 R8
            S10Sn.TerrainLap(S10TerrainSlice.SnDeposits); // S10 hook (round 3 F): lap mark
            if (_haveCam) _sample.Update(nowMs, _camLast, _storeKey, _reader.LastHeaderValid ? _reader.LastHeader.chartKey : 0);     // planet v2 route B (the sample)
            S10Sn.TerrainLap(S10TerrainSlice.SnSample); // S10 hook (round 3 F): lap mark
            if (nowMs >= _nextStatusMs)
            {
                _nextStatusMs = nowMs + 1000;
                StatusRow();
                BiomeRow();
            }
            S10Sn.TerrainLap(S10TerrainSlice.SnRows); // S10 hook (round 3 F): lap mark
        }

        /// <summary>Window notices whose box meets the camera's ±NearBoxM box and are not applied yet (C7 nearPendingSeen).</summary>
        private int CountNearPending()
        {
            if (!_haveCam || _tracker.Pending == 0) return 0;
            int cx = (int)System.Math.Floor(_camLast.x - S8aConventions.SnWorld.OriginX), cy = (int)System.Math.Floor(_camLast.y - S8aConventions.SnWorld.OriginY), cz = (int)System.Math.Floor(_camLast.z - S8aConventions.SnWorld.OriginZ);
            int r = S8aConventions.Window.NearBoxM;
            int n = 0;
            foreach (S8aReloadTracker.Item it in _tracker.Items)
            {
                if (it.Notice.layer != NoticeLayer.Window || it.Stage == S8aReloadTracker.Stage.Applied || it.Stage == S8aReloadTracker.Stage.Failed) continue;
                if (it.Maxs[0] < cx - r || it.Mins[0] > cx + r || it.Maxs[1] < cy - r || it.Mins[1] > cy + r || it.Maxs[2] < cz - r || it.Mins[2] > cz + r) continue;
                n++;
            }
            return n;
        }

        private void BiomeRow()
        {
            if (!_reader.LastHeaderValid || !_haveCam) return;
            byte cb = _reader.LastHeader.cameraBiome;
            string biome = null;
            try { LargeWorld lw = LargeWorld.main; if (lw != null) biome = lw.GetBiome(_camLast); } catch (Exception) { }

            // with the water settings it applies at the camera
            if (cb == _biomeLast && string.Equals(biome, _snBiomeLast, StringComparison.Ordinal) && _frames % 600 != 0) return;
            _biomeLast = cb;
            _snBiomeLast = biome;
            var j = new JsonOut().Str("t", Utc()).Num("tickMs", Win32.TickCount64()).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.biome").Str("ev", "s8a.biome")
                .Obj("data").Num("snFrame", DeLog.Frame).Num("cameraBiome", (long)cb).Str("cameraBiomeName", S8aBiomes.Name(cb)).Str("biome", biome).Bool("windowOn", WindowOn)
                .Num("camX", _camLast.x).Num("camY", _camLast.y).Num("camZ", _camLast.z);
            try
            {
                WaterscapeVolume.Settings ws;
                WaterBiomeManager wbm = WaterBiomeManager.main;
                if (wbm != null && wbm.GetSettings(_camLast, true, out ws) && ws != null)
                    j.Num("emissiveR", ws.emissive.r).Num("emissiveG", ws.emissive.g).Num("emissiveB", ws.emissive.b).Num("emissiveScale", ws.emissiveScale)
                     .Num("ambientScale", ws.ambientScale).Num("sunlightScale", ws.sunlightScale).Num("murkiness", ws.murkiness).Num("startDistance", ws.startDistance);
                // review (night light): the two light scalars DayNightCycle.UpdateAtmosphere sets
 // the
 // biome sky applied by SkyApplier (WaterBiomeManager.GetBiomeEnvironment), the atmosphere volume's override biome
 // (GetBiome(pos, true) answers it before the map) and uSkyLight.ambientLight
                if (wbm != null)
                {
                    mset.Sky sky = wbm.GetBiomeEnvironment(_camLast);
                    j.Str("biomeSky", sky != null ? sky.name : null).Str("atmosphereBiome", wbm.GetBiome(_camLast, true));
                }
                j.Num("lightScalar", Shader.GetGlobalFloat(LightScalarId)).Num("localLightScalar", Shader.GetGlobalFloat(LocalLightScalarId));
                if (_uSkyLight == null) _uSkyLight = UnityEngine.Object.FindObjectOfType<uSky.uSkyLight>();
                if (_uSkyLight != null) j.Num("uSkyAmbient", _uSkyLight.ambientLight);
            }
            catch (Exception) { }
            Row(j.EndObj().ToString());
        }

        // ------------------------------------------------------------------------------------------ the store

        private void OpenStore(ulong storeKey)
        {
            _storeKey = storeKey;
            _drillFolder = S8aSnPaths.DrillFolder(storeKey);
            _view.Clear();
            _manifest = null;
            _storeOpen = Directory.Exists(_drillFolder);
            _refused = false;
            DeLog.Info("DE.S8a store " + S8aSnPaths.Hex(storeKey) + ": " + _drillFolder + (_storeOpen ? "" : " (not there yet: the install is served until SE writes)"));
            ReadManifest("store opened", true);
        }

        private sealed class ManifestRead
        {
            public ulong StoreKey;
            public S8aManifest Manifest;
            public string Error, Why;
            public long Took;
            /// <summary>The view is replaced by this manifest (a reset, a new stream, a resync), not merged into.</summary>
            public bool Replace;
            /// <summary>Per listed file: it exists, parses as that batch and has the manifest's size and FNV.</summary>
            public Dictionary<string, bool> Valid;
            public int Invalid;
            /// <summary>Unix ms of the manifest file's last write (0 when unknown): notice entries validated after it survive a replace.</summary>
            public long WrittenUtcMs;
        }

        /// <summary>The drill manifest, parsed off the main thread (CONTRACT budgets: manifests are parsed off the main thread); every listed file validated there too (size, parse, FNV).</summary>
        private void ReadManifest(string why, bool replace)
        {
            ulong key = _storeKey;
            string folder = _drillFolder;
            string root = S8aSnPaths.Root();
            _manifestReads++;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var r = new ManifestRead { StoreKey = key, Why = why, Replace = replace };
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    string err;
                    r.Manifest = S8aManifest.ReadFrom(folder, out err);
                    r.Error = err;
                    if (r.Manifest != null)
                    {
                        try { r.WrittenUtcMs = (long)(File.GetLastWriteTimeUtc(Path.Combine(folder, S8aStore.ManifestName)) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
                        catch (Exception) { r.WrittenUtcMs = 0; }
                        r.Valid = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        foreach (S8aManifest.Entry e in r.Manifest.Entries)
                        {
                            bool ok = false;
                            try
                            {
                                string path = Path.Combine(root, e.RelativePath);
                                byte[] file = File.Exists(path) ? File.ReadAllBytes(path) : null;
                                ulong fnv;
                                string verr;
                                ok = file != null && S8aNoticeRule.Validate(file, e.Bx, e.By, e.Bz, e, out fnv, out verr);
                            }
                            catch (Exception) { ok = false; }
                            r.Valid[e.RelativePath] = ok;
                            if (!ok) r.Invalid++;
                        }
                    }
                }
                catch (Exception e) { r.Error = e.GetType().Name + ": " + e.Message; }
                r.Took = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                _manifests.Enqueue(r);
            });
        }

        /// <summary>
 /// The manifest becomes the view; every batch whose served version changed, appeared or disappeared is reloaded (CONTRACT
 /// "feed.resync"), so a loaded batch never keeps octrees of a version the store no longer names.
 /// </summary>
        private void ApplyManifest(ManifestRead mr, ulong nowMs)
        {
            if (mr.StoreKey != _storeKey) return;
            var old = new Dictionary<Int3Key, uint>();
            foreach (S8aServed s in _view.Snapshot()) old[s.Id] = s.Version;
            if (mr.Manifest == null)
            {
                if (!_storeLogged) { _storeLogged = true; DeLog.Info("DE.S8a drill manifest not read (" + mr.Why + "): " + mr.Error + " (the install is served until SE writes one)"); }
                if (mr.Replace && old.Count > 0)
                {
                    _view.Clear();
                    _manifest = null;
                    DeLog.Info("DE.S8a view emptied (" + mr.Why + ", no manifest): " + old.Count + " batches back to the install");
                    foreach (Int3Key id in old.Keys) ScheduleReload(null, id, new[] { id.X * 160, id.Y * 160, id.Z * 160 }, new[] { id.X * 160 + 159, id.Y * 160 + 159, id.Z * 160 + 159 }, nowMs, mr.Why, null);
                }
                return;
            }
            if (mr.Manifest.Key != _storeKey) { DeLog.Warn("DE.S8a drill manifest names store " + S8aSnPaths.Hex(mr.Manifest.Key) + ", not " + S8aSnPaths.Hex(_storeKey) + ": ignored"); return; }
            _manifest = mr.Manifest;
            _storeOpen = true;
            var kept = new HashSet<Int3Key>();
            if (mr.Replace)
            {
                // review Stage A minor: entries validated from notices after the manifest was written are newer than it: they survive the replace
                var keep = new List<S8aServed>();
                if (mr.WrittenUtcMs > 0) foreach (S8aServed s in _view.Snapshot()) if (s.FromNotice && s.ValidatedUtcMs > mr.WrittenUtcMs) keep.Add(s);
                _view.Clear();
                foreach (S8aServed s in keep) { _view.Set(s, true); kept.Add(s.Id); old.Remove(s.Id); }
            }
            string root = S8aSnPaths.Root();
            var changed = new List<Int3Key>();
            int taken = 0;
            foreach (S8aManifest.Entry e in mr.Manifest.Entries)
            {
                bool valid;
                if (mr.Valid == null || !mr.Valid.TryGetValue(e.RelativePath, out valid) || !valid) continue;   // checked on the worker: size, parse, FNV
                string path = Path.Combine(root, e.RelativePath);
                if (_view.Set(new S8aServed { Bx = e.Bx, By = e.By, Bz = e.Bz, Version = e.Version, Path = path, Bytes = e.Bytes, Fnv = e.Fnv, FromNotice = false }, mr.Replace && !kept.Contains(e.Id)))
                {
                    taken++;
                    uint was;
                    if (!old.TryGetValue(e.Id, out was) || was != e.Version) changed.Add(e.Id);
                    old.Remove(e.Id);
                }
                else old.Remove(e.Id);
            }
            if (mr.Replace) foreach (Int3Key id in old.Keys) changed.Add(id);   // served before, not in the manifest (or invalid now): back to the install
            DeLog.Info("DE.S8a drill manifest (" + mr.Why + (mr.Replace ? ", replace" : "") + "): " + mr.Manifest.Count + " entries, " + taken + " served, " + mr.Invalid + " invalid, " + kept.Count + " newer notice entries kept, " +
                       changed.Count + " to reload, overlayHash=" + S8aSnPaths.Hex(mr.Manifest.Hash) + " in " + mr.Took + " ms");
            foreach (Int3Key id in changed)
            {
                S8aServed s;
                string served = _view.TryGet(id.X, id.Y, id.Z, out s) ? s.Path : null;
                ScheduleReload(null, id, new[] { id.X * 160, id.Y * 160, id.Z * 160 }, new[] { id.X * 160 + 159, id.Y * 160 + 159, id.Z * 160 + 159 }, nowMs, mr.Why, served);
            }
        }

        // ------------------------------------------------------------------------------------------ notices

        private sealed class Validation
        {
            public uint Seq;
            public S8aServed Served;
            public bool Ok;
            public string Error;
            public long Took;
            /// <summary>A ServedPath fallback (no notice): the id and the candidate file.</summary>
            public bool Fallback;
            public int Bx, By, Bz;
            public string Path;
        }

        private void OnNotice(in S8aNotice n, ulong nowMs)
        {
            _notices++;
            if (_tracker.HasStream && n.seq != _tracker.AckSeq + 1 && _tracker.Pending == 0 && (int)(n.seq - 1 - _tracker.AckSeq) > 0) _tracker.SkipTo(n.seq);
            S8aReloadTracker.Item it = _tracker.Add(n, nowMs);

            if (IsCarve(n)) S8aFlora.OnCarveNotice(it.Mins, it.Maxs, n.seq, nowMs);
            S8aHeader hdr = _reader.LastHeader;
            switch (n.kind)
            {
                case NoticeKind.BatchWritten:
                    if (nowMs < _visUntilMs) { _visMins = it.Mins; _visMaxs = it.Maxs; }   // Stage D oracle: the latest edited box
                    if (n.layer == NoticeLayer.Window)
                    {
                        // S8a-2: a window batch of the current chart's folder (the header's chartKey); a file this side cannot load (the
 // loader prefix missing) is acknowledged and applied at once so the feed never stalls
                        _windowNotices++;
                        if (!S8aPatches.LoaderPatched)
                        {
                            _tracker.MarkScheduled(it, nowMs);
                            _tracker.MarkApplied(it, nowMs, true, "window layer: loader prefix missing");
                            NoticeRow(it, "window-unloadable");
                            return;
                        }
                        StartValidation(it, n, S8aSnPaths.WindowFile(_storeKey, hdr.chartKey, n.bx, n.by, n.bz, n.version));
                        return;
                    }
                    StartValidation(it, n, S8aSnPaths.DrillFile(_storeKey, n.bx, n.by, n.bz, n.version));
                    return;
                case NoticeKind.BatchCleared:
                {
                    // water: nothing served; a loaded batch reloads to empty octrees (the loader answers false without a file)
                    _clearedNotices++;
                    var id = new Int3Key(n.bx, n.by, n.bz);
                    bool had = _view.Remove(id);
                    if (had || n.layer == NoticeLayer.Window) ScheduleReload(it, id, it.Mins, it.Maxs, nowMs, "cleared " + n.seq, null);
                    if (!_waiting.Contains(it)) _tracker.MarkScheduled(it, nowMs);
                    if (!had && n.layer != NoticeLayer.Window) _tracker.MarkApplied(it, nowMs, false, null);
                    NoticeRow(it, "cleared");
                    return;
                }
                case NoticeKind.ChartStep:
                {
                    // CONTRACT "chart.apply": the window's files now belong to the new chart's folder (Reuse notices repopulate the
 // view); the spawns move by −T; the drill layer (crater ids) is untouched
                    _chartSteps++;
                    _chartSeqApplied = n.version;
                    // terrain 4 "chart.
 // files are served at once under the shifted ids (new id = old id - T: the camera's SN position moved by -T), so the
 // batches the streamer loads around the camera's new position hold the terrain the camera was over; the new chart's
 // notices (Reuse copies, then regenerations) replace them, live (a copy changes no octree)
                    var moved = new List<S8aServed>();
                    foreach (S8aServed s in _view.Snapshot())
                    {
                        if (!S8aWindowPlan.IsWindowId(s.Bx, s.By, s.Bz)) continue;
                        _view.Remove(s.Id);
                        int nbx = s.Bx - n.bx, nbz = s.Bz - n.bz;
                        if (!S8aWindowPlan.IsWindowId(nbx, s.By, nbz)) continue;
                        moved.Add(new S8aServed { Bx = nbx, By = s.By, Bz = nbz, Version = s.Version, Path = s.Path, Bytes = s.Bytes, Fnv = s.Fnv, FromNotice = true, ValidatedUtcMs = s.ValidatedUtcMs });
                    }
                    foreach (S8aServed s in moved) _view.Set(s, true);
                    _chartViewMoved += moved.Count;
                    _spawns.OnChartStep(hdr.chartKey, n.bx * (double)S8aConventions.SnWorld.BatchVoxels, n.bz * (double)S8aConventions.SnWorld.BatchVoxels);
                    _tracker.MarkScheduled(it, nowMs);
                    _tracker.MarkApplied(it, nowMs, false, null);
                    NoticeRow(it, "chart");
                    DeLog.Info("DE.S8a chart " + n.version + " applied: T=(" + n.bx + ", " + n.bz + ") batches, window view cleared, spawns moved");
                    return;
                }
                case NoticeKind.StoreReset:
                    if (n.layer == NoticeLayer.Drill && _storeKey != 0) ReadManifest("StoreReset gen " + n.version, true);   // the view is replaced when the manifest arrives (no gap served from the install)
                    _tracker.MarkScheduled(it, nowMs);
                    _tracker.MarkApplied(it, nowMs, false, null);
                    NoticeRow(it, "reset");
                    return;
                default:
                    _tracker.MarkScheduled(it, nowMs);
                    _tracker.MarkApplied(it, nowMs, true, "unknown kind " + n.kind);
                    return;
            }
        }

        private void StartValidation(S8aReloadTracker.Item it, in S8aNotice n, string path)
        {
            uint seq = n.seq;
            int bx = n.bx, by = n.by, bz = n.bz;
            uint version = n.version;
            S8aManifest m = n.layer == NoticeLayer.Drill ? _manifest : null;
            S8aManifest.Entry me = null;
            if (m != null) { S8aManifest.Entry e; if (m.TryGet(bx, by, bz, out e) && e.Version == version) me = e; }
            Interlocked.Increment(ref _validating);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var v = new Validation { Seq = seq };
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    byte[] file = File.Exists(path) ? File.ReadAllBytes(path) : null;
                    ulong fnv;
                    string err;
                    if (file == null) { v.Error = "missing " + path; }
                    else if (S8aNoticeRule.Validate(file, bx, by, bz, me, out fnv, out err))
                    {
                        v.Ok = true;
                        v.Served = new S8aServed { Bx = bx, By = by, Bz = bz, Version = version, Path = path, Bytes = file.Length, Fnv = fnv, FromNotice = true, ValidatedUtcMs = UtcMs() };
                    }
                    else v.Error = err;
                }
                catch (Exception e) { v.Error = e.GetType().Name + ": " + e.Message; }
                v.Took = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                Interlocked.Decrement(ref _validating);
                _validated.Enqueue(v);
            });
        }

        private void OnFallbackValidated(Validation v, ulong nowMs)
        {
            var id = new Int3Key(v.Bx, v.By, v.Bz);
            byte dummy;
            _fallbackPending.TryRemove(id, out dummy);
            if (!v.Ok)
            {
                DeLog.Warn("DE.S8a fallback " + v.Bx + "-" + v.By + "-" + v.Bz + " " + v.Path + " refused: " + v.Error + " (the install stands)");
                return;
            }
            _view.Set(v.Served, true);
            DeLog.Info("DE.S8a fallback " + v.Bx + "-" + v.By + "-" + v.Bz + " v" + v.Served.Version + " served after validation (" + v.Took + " ms)");
            ScheduleReload(null, id, new[] { id.X * 160, id.Y * 160, id.Z * 160 }, new[] { id.X * 160 + 159, id.Y * 160 + 159, id.Z * 160 + 159 }, nowMs, "fallback", v.Served.Path);
        }

        private void OnValidated(Validation v, ulong nowMs)
        {
            _validations++;
            if (v.Fallback) { OnFallbackValidated(v, nowMs); return; }
            S8aReloadTracker.Item it = _tracker.Get(v.Seq);
            if (it == null) return;   // the stream changed meanwhile
            S8aNotice n = it.Notice;
            if (!v.Ok)
            {
                _validationFailures++;
                _tracker.MarkScheduled(it, nowMs);
                _tracker.MarkApplied(it, nowMs, true, v.Error);
                DeLog.Warn("DE.S8a REFUSE batch " + n.bx + "-" + n.by + "-" + n.bz + " v" + n.version + ": " + v.Error + " (not served; the previous version or the install stands)");
                Row(new JsonOut().Str("t", Utc()).Num("tickMs", Win32.TickCount64()).Num("frame", DeLog.Frame).Str("level", "warn").Str("event", "s8a.load").Str("ev", "s8a.load")
                    .Obj("data").Num("snFrame", DeLog.Frame).Num("seq", (long)n.seq).Num("bx", (long)n.bx).Num("by", (long)n.by).Num("bz", (long)n.bz).Num("version", (long)n.version)
                    .Num("layer", (long)n.layer).Bool("ok", false).Str("error", v.Error).Num("ms", v.Took).EndObj().ToString());
                NoticeRow(it, "refused");
                return;
            }
            it.Served = _view.Set(v.Served);
            it.ServedPath = it.Served ? v.Served.Path : null;
            Row(new JsonOut().Str("t", Utc()).Num("tickMs", Win32.TickCount64()).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.load").Str("ev", "s8a.load")
                .Obj("data").Num("snFrame", DeLog.Frame).Num("seq", (long)n.seq).Num("bx", (long)n.bx).Num("by", (long)n.by).Num("bz", (long)n.bz).Num("version", (long)n.version)
                .Str("path", v.Served.Path).Num("bytes", v.Served.Bytes).Str("fnv", S8aSnPaths.Hex(v.Served.Fnv)).Bool("ok", true).Bool("served", it.Served).Num("ms", v.Took).EndObj().ToString());
            ScheduleReload(it, new Int3Key(n.bx, n.by, n.bz), it.Mins, it.Maxs, nowMs, "notice " + n.seq, it.ServedPath);
            if (!_waiting.Contains(it)) _tracker.MarkScheduled(it, nowMs);
        }

        /// <summary>Notices that waited for a reload slot take it in seq order.</summary>
        private void DrainWaiting(ulong nowMs)
        {
            // a notice whose
 // batch has a reload in flight waits for it (supersede after apply: never two reloads of one batch at once)
            if (_waiting.Count > 1 && _haveCam) _waiting.Sort((a, b) => CamDist2(a).CompareTo(CamDist2(b)));
            for (int k = 0; k < _waiting.Count && _tracker.InFlight < QueueC.MaxReloadsInFlight; )
            {
                S8aReloadTracker.Item it = _waiting[k];
                if (_tracker.Get(it.Notice.seq) != it) { _waiting.RemoveAt(k); continue; }   // the stream changed
                S8aNotice n = it.Notice;
                var key = new Int3Key(n.bx, n.by, n.bz);
                if (BatchInFlight(key)) { k++; continue; }
                _waiting.RemoveAt(k);
                ScheduleReload(it, key, it.Mins, it.Maxs, nowMs, "notice " + n.seq + " (waited)", it.ServedPath);
                if (!_waiting.Contains(it)) _tracker.MarkScheduled(it, nowMs);
                else break;
            }
        }

        // ------------------------------------------------------------------------------------------ reloads (streaming thread)

 /// <summary>
 /// One batch reload: phase 0 = Reload called, 1 = waiting for the batch to load the served path again, 2 = the cells reloaded (the main thread then polls the ring's level-0 cells).
 /// </summary>

 /// <summary>
 /// A finished reload's box, checked later on the main thread: every ring cell of its level-0..3 range that is neither loaded
 /// nor building (<c>IsProcessing</c>) is reloaded again (<c>Reload(Int3)</c>) on the
 /// streaming thread. A reload that gave up or was superseded can leave a cell hidden
 /// the watch kicks it until it loads.
 /// </summary>
        private sealed class CellWatch
        {
            public ClipmapStreamer Clipmap;
            public BatchOctreesStreamer Streamer;
            public int[] Mins, Maxs;
            public ulong DueMs;
            public int Tries;
            public Int3Key Id;
        }

        public const ulong WatchFirstMs = 3000, WatchEveryMs = 4000;
        public const int WatchTries = 6, MaxWatches = 1024;
        private readonly List<CellWatch> _watches = new List<CellWatch>(64);
        private long _cellKicks, _retriesKept;
        private static Task.Function _kickCells;

        private bool _paletteLogged;

        /// <summary>
 /// Once per session: Subnautica's block type palette by index
 /// row s8a.palette: the names behind the converter table's types
 /// (convert.
 /// </summary>
        private void LogPalette(ulong nowMs)
        {
            try
            {
                LargeWorldStreamer lws = LargeWorldStreamer.main;
                WorldStreamer ws = lws != null ? lws.streamerV2 : null;
                VoxelandBlockType[] types = ws != null ? ws.blockTypes : null;
                if (types == null) return;
                _paletteLogged = true;
                var j = new JsonOut().Str("t", Utc()).Num("tickMs", nowMs).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.palette").Str("ev", "s8a.palette")
                    .Obj("data").Num("count", (long)types.Length).Obj("types");
                for (int i = 0; i < types.Length; i++)
                {
                    VoxelandBlockType bt = types[i];
                    j.Str(i.ToString(CultureInfo.InvariantCulture), bt == null ? null : bt.name + " | " + (bt.material != null ? bt.material.name : "-") + (bt.filled ? "" : " | not filled"));
                }
                Row(j.EndObj().EndObj().ToString());
                DeLog.Info("DE.S8a palette: " + types.Length + " block types logged (row s8a.palette)");
            }
            catch (Exception e) { _paletteLogged = true; DeLog.Info("DE.S8a palette not logged: " + e.GetType().Name + ": " + e.Message); }
        }

        private float CamDist2(S8aReloadTracker.Item it)
        {
            float dx = it.Notice.bx * 160f + 80f + S8aConventions.SnWorld.OriginX - _camLast.x;
            float dy = it.Notice.by * 160f + 80f + S8aConventions.SnWorld.OriginY - _camLast.y;
            float dz = it.Notice.bz * 160f + 80f + S8aConventions.SnWorld.OriginZ - _camLast.z;
            return dx * dx + dy * dy + dz * dz;
        }

        private bool BatchInFlight(Int3Key id)
        {
            foreach (ReloadJob j in _jobs) if (!j.Done && j.Item != null && j.Id.Equals(id)) return true;
            // nd rerun: a live job holds its batch until its octrees are patched and its level-0 cells swapped; its level 1-3
 // rebuilds wait for the meshing threads behind every level 0 (S8aLiveEdit.PumpHigher) and read the newest octrees when they
 // run, so the next version's live edit starts without waiting for them
            foreach (S8aLiveEdit.Job j in _live) if (!j.Done && j.Id.Equals(id) && (j.Level0Ms == 0 || j.Stage < 4)) return true;
            return false;
        }

        /// <summary>Main thread: the due watches; hidden, idle cells are reloaded on the streaming thread (≤ MaxCellReloadsPerNotice per watch).</summary>
        private void WatchCells(ulong nowMs)
        {
            if (_watches.Count == 0) return;
            if (_kickCells == null) _kickCells = KickCells;
            for (int i = _watches.Count - 1; i >= 0; i--)
            {
                CellWatch w = _watches[i];
                if (nowMs < w.DueMs) continue;
                List<ClipmapCell> stuck = null;
                try
                {
                    var sizes = new int[4];
                    int levels = 0;
                    for (int l = 0; l < 4; l++) { ClipmapLevel lv = w.Clipmap.GetLevel(l); if (lv == null) break; sizes[l] = lv.cellSize; levels = l + 1; }
                    foreach (S8aCellRange.Cell c in S8aCellRange.Plan(levels, sizes, w.Mins, w.Maxs, QueueC.MaxCellReloadsPerNotice))
                    {
                        ClipmapLevel lv = w.Clipmap.GetLevel(c.Level);
                        ClipmapCell cell = lv != null ? lv.GetCell(new Int3(c.X, c.Y, c.Z)) : null;
                        if (cell == null || cell.IsLoaded() || cell.IsProcessing()) continue;
                        if (stuck == null) stuck = new List<ClipmapCell>();
                        stuck.Add(cell);
                    }
                }
                catch (Exception) { stuck = null; }
                w.Tries++;
                if (stuck != null && w.Streamer != null && w.Streamer.streamingThread != null)
                {
                    _cellKicks += stuck.Count;
                    w.Streamer.streamingThread.Enqueue(_kickCells, this, stuck);
                    if (w.Tries == 1 || w.Tries == WatchTries) DeLog.Info("DE.S8a cell watch " + w.Id + ": " + stuck.Count + " hidden cell(s) reloaded again (try " + w.Tries + ")");
                }
                if (stuck == null || w.Tries >= WatchTries) _watches.RemoveAt(i);
                else w.DueMs = nowMs + WatchEveryMs;
            }
        }

        /// <summary>Streaming thread: <c>ClipmapCell.Reload(cell.id)</c> for the watch's stuck cells.</summary>
        private static void KickCells(object owner, object state)
        {
            var cells = (List<ClipmapCell>)state;
            foreach (ClipmapCell cell in cells)
            {
                try { if (!cell.IsLoaded() && !cell.IsProcessing()) cell.Reload(cell.id); } catch (Exception) { }
            }
        }

        private sealed class ReloadJob
        {
            public S8aReloadTracker.Item Item;
            public Int3Key Id;
            public int[] Mins, Maxs;
            public string Why, ServedPath;
            public ulong StartMs, ReloadedMs;
            public long ReloadIssuedMs;
            public volatile int Phase;
            public volatile bool SawUnloaded, NotInRing, Failed, Reissued;
            public volatile int Cells;
            public string Error;
            public BatchOctrees Batch;
            public BatchOctreesStreamer Streamer;
            public ClipmapStreamer Clipmap;
            public bool Done;
        }

        private void ScheduleReload(S8aReloadTracker.Item it, Int3Key id, int[] mins, int[] maxs, ulong nowMs, string why, string servedPath)
        {
            LargeWorldStreamer lws = LargeWorldStreamer.main;
            WorldStreamer ws = lws != null ? lws.streamerV2 : null;
            if (ws == null || ws.octreesStreamer == null || ws.clipmapStreamer == null || ws.octreesStreamer.streamingThread == null)
            {
                // no streamer: nothing to reload (the next load takes the new path)
                if (it != null) { it.NeedsReload = false; _tracker.MarkScheduled(it, nowMs); _tracker.MarkApplied(it, nowMs, false, "no streamer"); NoticeRow(it, "no-streamer"); }
                return;
            }
            if (it != null && BatchInFlight(id))
            {
                // supersede after apply: the in-flight reload of this batch finishes first (it loads the newest served version anyway)
                if (!_waiting.Contains(it)) _waiting.Add(it);
                return;
            }
            if (_tracker.InFlight >= QueueC.MaxReloadsInFlight && it != null)
            {
                // the queue rule (CONTRACT budgets: ≤ 8 batch reloads in flight): the notice waits for a slot (its ack waits too)
                if (!_waiting.Contains(it)) _waiting.Add(it);
                return;
            }
            if (it == null)
            {
                foreach (ReloadJob j in _jobs) if (j.Item == null && !j.Done && j.Id.Equals(id)) return;   // a manifest reload of the same batch is already in flight
            }

            // memory and its cells rebuilt beside their meshes (S8aLiveEdit); the batch reload below stays the fallback
            LoadRecord lr;
            if (it != null && servedPath != null && S8aLiveEdit.Enabled && _loads.TryGetValue(id, out lr) && lr.Ok && lr.Path != null &&
                !string.Equals(lr.Path, servedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(lr.Path))
            {
                ulong last;
                _lastHigher.TryGetValue(id, out last);
                bool higher = S8aLivePatch.HigherLevelsDue(nowMs, last);
                if (higher) { _lastHigher[id] = nowMs; _pendingHigher.Remove(id); }
                else
                {
                    int[][] box;
                    if (_pendingHigher.TryGetValue(id, out box))
                        for (int k = 0; k < 3; k++) { box[0][k] = System.Math.Min(box[0][k], mins[k]); box[1][k] = System.Math.Max(box[1][k], maxs[k]); }
                    else _pendingHigher[id] = new[] { (int[])mins.Clone(), (int[])maxs.Clone() };
                }
                var lj = new S8aLiveEdit.Job { Id = id, NewPath = servedPath, OldPath = lr.Path, Why = why, Mins = mins, Maxs = maxs, Item = it, StartMs = nowMs, HigherLevels = higher, Ws = ws };
                _live.Add(lj);
                _reloadsScheduled++;
                S8aLiveEdit.Start(lj, id.X, id.Y, id.Z);
                return;
            }
            var job = new ReloadJob { Item = it, Id = id, Mins = mins, Maxs = maxs, Why = why, StartMs = nowMs, Streamer = ws.octreesStreamer, Clipmap = ws.clipmapStreamer,
                                      ServedPath = servedPath, ReloadIssuedMs = (long)nowMs };
            _jobs.Add(job);
            _reloadsScheduled++;
            ws.octreesStreamer.streamingThread.Enqueue(_runJob, this, job);
        }

        private void Reschedule(S8aReloadTracker.Item it, ulong nowMs)
        {
            foreach (S8aLiveEdit.Job lj in _live) if (lj.Item == it && !lj.Done) { _retriesKept++; return; }   // Stage D: a live edit in flight keeps running
            ReloadJob job = null;
            foreach (ReloadJob j in _jobs) if (j.Item == it) { job = j; break; }
            if (job != null && !job.Done)
            {
                // it keeps running (the give-up and the
 // cell watch cover a stuck one)
                _retriesKept++;
                return;
            }
            if (_waiting.Contains(it)) return;
            DeLog.Warn("DE.S8a reload of " + it.Notice.bx + "-" + it.Notice.by + "-" + it.Notice.bz + " v" + it.Notice.version + " not applied in " + S8aReloadTracker.RetryMs + " ms: retried once");
            ScheduleReload(it, new Int3Key(it.Notice.bx, it.Notice.by, it.Notice.bz), it.Mins, it.Maxs, nowMs, "retry " + it.Notice.seq, it.ServedPath);
        }

        /// <summary>Streaming thread (UWE.Task.Function): the batch and clipmap state machines.</summary>
        private static void RunJob(object owner, object state)
        {
            var job = (ReloadJob)state;
            try
            {
                if (job.Phase == 0)
                {
                    var b = job.Streamer.GetBatch(new Int3(job.Id.X, job.Id.Y, job.Id.Z));
                    if (b == null) { job.NotInRing = true; job.Phase = 2; return; }
                    job.Batch = b;
                    job.ReloadIssuedMs = (long)Win32.TickCount64();
                    b.Reload(b.id);
                    job.Phase = 1;
                    return;
                }
                if (job.Phase == 1)
                {
                    // the batch is loaded again: the collision and render cells over the box
                    int n = 0;
                    var mins = new Int3(job.Mins[0], job.Mins[1], job.Mins[2]);
                    var maxs = new Int3(job.Maxs[0], job.Maxs[1], job.Maxs[2]);
                    var sizes = new int[4];
                    int levels = 0;
                    for (int l = 0; l < 4; l++)
                    {
                        ClipmapLevel lv = job.Clipmap.GetLevel(l);
                        if (lv == null) break;
                        sizes[l] = lv.cellSize;
                        levels = l + 1;
                    }
                    List<S8aCellRange.Cell> plan = S8aCellRange.Plan(levels, sizes, job.Mins, job.Maxs, QueueC.MaxCellReloadsPerNotice);
                    foreach (S8aCellRange.Cell c in plan)
                    {
                        ClipmapLevel lv = job.Clipmap.GetLevel(c.Level);
                        if (lv == null) continue;
                        ClipmapCell cell = lv.GetCell(new Int3(c.X, c.Y, c.Z));
                        if (cell == null) continue;
                        cell.Reload(cell.id);
                        n++;
                    }
                    job.Cells = n;
                    job.Phase = 2;
                }
            }
            catch (Exception e)
            {
                job.Failed = true;
                job.Error = e.GetType().Name + ": " + e.Message;
                job.Phase = 2;
            }
        }

        /// <summary>
 /// Main thread (Stage D): the live edits' progress: diff done -> patch queued (or the batch reload as fallback, or applied
 /// when nothing changed); patched -> the cells planned; the deferred (building) cells planned again every
 /// <see cref="S8aLiveEdit.DeferredRetryMs"/>; done when every rebuild swapped or was discarded. The levels 1-3 boxes that
 /// waited for their slot get a rebuild-only job when due.
 /// </summary>
        private void PollLive(ulong nowMs)
        {
            S8aLiveEdit.PumpFinalize();
            S8aLiveEdit.PumpHigher(8);
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                S8aLiveEdit.Job j = _live[i];
                if (j.Stage == 1)
                {
                    if (j.Error != null) { LiveFallback(j, nowMs, j.Error); _live.RemoveAt(i); continue; }
                    if (j.Changed == 0) { LiveFinish(j, nowMs, "no octree changed"); _live.RemoveAt(i); continue; }
                    S8aLiveEdit.QueuePatch(j);
                    continue;
                }
                if (j.Stage == 3)
                {
                    if (j.Error != null && j.Patched == 0) { LiveFallback(j, nowMs, j.Error); _live.RemoveAt(i); continue; }
                    if (j.Patched == 0 && !j.SkipPatch)
                    {
                        // the ring holds the batch no more: its next load takes the served file
                        _liveNotLoaded++;
                        if (j.Item != null) j.Item.NeedsReload = false;
                        LiveFinish(j, nowMs, "not loaded");
                        _live.RemoveAt(i);
                        continue;
                    }
                    if (!j.SkipPatch) _loads[j.Id] = new LoadRecord { Path = j.NewPath, Ms = nowMs, Ok = true };
                    S8aLiveEdit.QueuePlan(j);
                    continue;
                }
                if (j.Stage != 4)
                {
                    if (nowMs - j.StartMs >= S8aLiveEdit.JobGiveUpMs) { LiveFallback(j, nowMs, "stage " + j.Stage + " not left in " + S8aLiveEdit.JobGiveUpMs + " ms"); _live.RemoveAt(i); }
                    continue;
                }
                // S8a.ND.live's measure: notice seen -> the last level-0 rebuild swapped (levels 1-3 follow; the whole job is "ms")
                if (j.Level0Ms == 0 && j.Done0 >= j.Rebuilds0) j.Level0Ms = System.Math.Max(1L, (long)(nowMs - j.StartMs));
                List<ClipmapCell> again = null;
                bool deferred;
                lock (j.Deferred)
                {
                    if (j.Deferred.Count > 0)
                    {
                        if (j.DeferredSinceMs == 0) j.DeferredSinceMs = nowMs;
                        if (nowMs - j.DeferredSinceMs >= S8aLiveEdit.DeferredGiveUpMs) j.Deferred.Clear();
                        else if (nowMs >= j.DeferredNextMs) { again = new List<ClipmapCell>(j.Deferred); j.Deferred.Clear(); j.DeferredNextMs = nowMs + S8aLiveEdit.DeferredRetryMs; }
                    }
                    deferred = j.Deferred.Count > 0;
                }
                if (again != null) { S8aLiveEdit.QueueDeferred(j, again); continue; }
                if (!deferred && j.Replans == 0 && j.RebuildsDone >= j.Rebuilds)
                {
                    LiveFinish(j, nowMs, null);
                    _live.RemoveAt(i);
                }
                else if (nowMs - j.StartMs >= S8aLiveEdit.JobGiveUpMs) { LiveFinish(j, nowMs, "rebuilds not done in " + S8aLiveEdit.JobGiveUpMs + " ms"); _live.RemoveAt(i); }
            }
            // levels 1-3 that waited for their once-per-second slot
            if (_pendingHigher.Count > 0)
            {
                LargeWorldStreamer lws = LargeWorldStreamer.main;
                WorldStreamer ws = lws != null ? lws.streamerV2 : null;
                List<Int3Key> due = null;
                foreach (KeyValuePair<Int3Key, int[][]> kv in _pendingHigher)
                {
                    ulong last;
                    _lastHigher.TryGetValue(kv.Key, out last);
                    if (S8aLivePatch.HigherLevelsDue(nowMs, last) && !BatchInFlight(kv.Key))
                    {
                        if (due == null) due = new List<Int3Key>();
                        due.Add(kv.Key);
                    }
                }
                if (due != null)
                    foreach (Int3Key id in due)
                    {
                        int[][] box = _pendingHigher[id];
                        _pendingHigher.Remove(id);
                        _lastHigher[id] = nowMs;
                        if (ws == null || ws.clipmapStreamer == null) continue;
                        var hj = new S8aLiveEdit.Job { Id = id, Why = "levels 1-3", Mins = box[0], Maxs = box[1], StartMs = nowMs, HigherLevels = true, LevelZero = false, SkipPatch = true, Ws = ws };
                        _live.Add(hj);
                        S8aLiveEdit.Start(hj, id.X, id.Y, id.Z);
                    }
            }
        }

        /// <summary>
 /// Stage D oracle (S8a.ND.hidden, S8a.ND.wall): one row per frame while switched on: the level-0 cells the ring holds over
 /// the latest edited box, how many are covered (some level shows their area: <see cref="S8aLivePatch.Covered"/>), whether
 /// the cell under the box's centre is, and the run of consecutive frames with an uncovered cell.
 /// </summary>
        private void CellVisRow(ulong nowMs)
        {
            LargeWorldStreamer lws = LargeWorldStreamer.main;
            WorldStreamer ws = lws != null ? lws.streamerV2 : null;
            ClipmapLevel lv = ws != null && ws.clipmapStreamer != null ? ws.clipmapStreamer.GetLevel(0) : null;
            if (lv == null) return;
            int[] cmin, cmax;
            S8aCellRange.Range(0, lv.cellSize, _visMins, _visMaxs, out cmin, out cmax);
            int cells = 0, covered = 0;
            var states = new int[13];
            for (int x = cmin[0]; x <= cmax[0]; x++)
                for (int y = cmin[1]; y <= cmax[1]; y++)
                    for (int z = cmin[2]; z <= cmax[2]; z++)
                    {
                        ClipmapCell cell = lv.GetCell(new Int3(x, y, z));
                        if (cell == null) continue;
                        cells++;
                        int st = S8aLiveEdit.CellState(cell);
                        if (st >= 0 && st < states.Length) states[st]++;
                        if (S8aLivePatch.Covered(st)) covered++;
                    }
            int cx = S8aCellRange.FloorDiv((_visMins[0] + _visMaxs[0]) / 2, lv.cellSize), cy = S8aCellRange.FloorDiv((_visMins[1] + _visMaxs[1]) / 2, lv.cellSize),
                cz = S8aCellRange.FloorDiv((_visMins[2] + _visMaxs[2]) / 2, lv.cellSize);
            ClipmapCell centre = lv.GetCell(new Int3(cx, cy, cz));
            int cst = centre != null ? S8aLiveEdit.CellState(centre) : -1;
            bool centreCovered = centre == null || S8aLivePatch.Covered(cst);
            if (covered < cells || !centreCovered) { _visRun++; _visUncoveredFrames++; if (_visRun > _visRunMax) _visRunMax = _visRun; }
            else _visRun = 0;
            _visRows++;
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < states.Length; k++) if (states[k] > 0) sb.Append(sb.Length > 0 ? "," : "").Append(k).Append(':').Append(states[k]);
            Row(new JsonOut().Str("t", Utc()).Num("tickMs", nowMs).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.cellvis").Str("ev", "s8a.cellvis")
                .Obj("data").Num("snFrame", DeLog.Frame).Num("cells", (long)cells).Num("covered", (long)covered).Bool("centreCovered", centreCovered).Num("centreState", (long)cst)
                .Str("states", sb.ToString()).Num("run", _visRun).Num("live", (long)_live.Count).Num("jobs", (long)_jobs.Count)
                .Num("mins0", (long)_visMins[0]).Num("mins1", (long)_visMins[1]).Num("mins2", (long)_visMins[2])
                // nd rerun: the camera (the oracle judges only frames where it stayed at the pass's spot)
                .Num("camX", _haveCam ? _camLast.x : double.NaN).Num("camY", _haveCam ? _camLast.y : double.NaN).Num("camZ", _haveCam ? _camLast.z : double.NaN)
                .Num("l0Pending", (long)S8aLiveEdit.Level0Pending).Num("higherWaiting", (long)S8aLiveEdit.HigherWaiting).Num("finalWaiting", (long)S8aLiveEdit.FinalWaiting).EndObj().ToString());
        }

        /// <summary>

 /// box is part of its batch and no Reuse copy: a planet edit) has the entities over its box tested for ground (S8aFlora).
 /// </summary>
        private static void FloraAfter(S8aReloadTracker.Item it, ulong nowMs)
        {
            if (IsCarve(it.Notice)) S8aFlora.OnCarveApplied(it.Mins, it.Maxs, it.Notice.seq, nowMs);
        }

        /// <summary>A carve: a drill-layer batch, or a window batch whose box is part of its batch and no Reuse copy (a planet edit).</summary>
        private static bool IsCarve(in S8aNotice n)
        {
            if (n.kind != NoticeKind.BatchWritten) return false;
            return n.layer == NoticeLayer.Drill ||
                   (n.layer == NoticeLayer.Window && (n.flags & S8aConventions.NoticeFlags.Reuse) == 0 && (n.maxX - n.minX < 159 || n.maxY - n.minY < 159 || n.maxZ - n.minZ < 159));
        }

        private void LiveFinish(S8aLiveEdit.Job j, ulong nowMs, string note)
        {
            j.Done = true;
            long ms = (long)(nowMs - j.StartMs);
            S8aLiveEdit.LastJobMs = ms;
            if (ms > _liveMsMax) _liveMsMax = ms;
            if (j.Item != null)
            {
                _liveApplied++;
                _reloadsDone++;
                j.Item.CellsReloaded = j.Swapped;
                _tracker.MarkApplied(j.Item, nowMs, false, note);
                NoticeRow(j.Item, "applied-live");
                FloraAfter(j.Item, nowMs);
            }
            Row(new JsonOut().Str("t", Utc()).Num("tickMs", nowMs).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.live").Str("ev", "s8a.live")
                .Obj("data").Num("snFrame", DeLog.Frame).Num("seq", j.Item != null ? (long)j.Item.Notice.seq : -1L).Num("bx", (long)j.Id.X).Num("by", (long)j.Id.Y).Num("bz", (long)j.Id.Z)
                .Str("why", j.Why).Bool("levelZero", j.LevelZero).Bool("higher", j.HigherLevels).Num("changed", (long)j.Changed).Num("patched", (long)j.Patched)
                .Num("rebuilds", (long)j.Rebuilds).Num("swapped", (long)j.Swapped).Num("discarded", (long)j.Discarded).Num("noMesh", (long)j.NoMesh)
                .Num("diffMs", j.DiffMs).Num("patchMs", j.PatchMs).Num("ms", ms).Num("level0Ms", j.Level0Ms > 0 ? j.Level0Ms : ms).Num("rebuilds0", (long)j.Rebuilds0)
                // a test run: per level-0 cell (means, ms): waiting for a meshing thread, meshing, waiting for the finishing step, finishing;
 // plan to the first level-0 swap; the finishing pump's budget (0: Subnautica's BuildLayers queue)
                .Num("l0QueueMs", L0Avg(j.L0QueueTicks, j.Done0)).Num("l0MeshMs", L0Avg(j.L0MeshTicks, j.Done0)).Num("l0WaitMs", L0Avg(j.L0WaitTicks, j.Done0))
                .Num("l0FinalMs", L0Avg(j.L0FinalTicks, j.Done0)).Num("firstL0Ms", System.Math.Round(S8aLiveEdit.TicksToMs(j.FirstDoneTicks), 1)).Num("finalizeBudgetMs", S8aLiveEdit.FinalizeBudgetMs)
                .Str("note", note).Str("error", j.Error).EndObj().ToString());
        }

        private static double L0Avg(long ticks, int n) { return n > 0 ? System.Math.Round(S8aLiveEdit.TicksToMs(ticks) / n, 2) : 0.0; }

        /// <summary>A live edit that cannot run (a file, a member, a stage): the batch reload takes over (CONTRACT §3.3 fail-safe).</summary>
        private void LiveFallback(S8aLiveEdit.Job j, ulong nowMs, string why)
        {
            j.Done = true;
            _liveFallbacks++;
            Interlocked.Increment(ref S8aLiveEdit.Fallbacks);
            DeLog.Info("DE.S8a live edit of " + j.Id + " falls back to the batch reload: " + why);
            if (j.Item == null) return;
            LoadRecord none;
            _loads.TryRemove(j.Id, out none);    // the reload path below, not the live one again
            ScheduleReload(j.Item, j.Id, j.Mins, j.Maxs, nowMs, j.Why + " (live fallback: " + why + ")", j.NewPath);
        }

        /// <summary>
 /// Main thread: the jobs' progress. Phase 1 starts the cell reloads once the loader postfix recorded a load of the
 /// served path after the Reload was issued ( <c>IsLoaded</c> is also true for QueuedForUnloading, so
 /// it is not the signal; a batch that is loaded again without such a load within <see cref="ReloadUnseenMs"/> gets
 /// its Reload re-issued once, e.g. after <c>UpdateCenter</c> cancelled it). Without the postfix the old IsLoaded
 /// edge detection stands. Applied = every level-0 cell of the box that exists in the ring is loaded;
 /// every job gives up at <see cref="S8aReloadTracker.GiveUpMs"/>.
 /// </summary>
        private void PollJobs(ulong nowMs)
        {
            for (int i = _jobs.Count - 1; i >= 0; i--)
            {
                ReloadJob job = _jobs[i];
                if (job.Phase == 1 && job.Batch != null && job.ReloadedMs == 0)
                {
                    bool go = false;
                    if (S8aPatches.LoaderPostfixPatched)
                    {
                        LoadRecord lr;
                        // fix check: a newer notice for the same batch may have moved the
 // view on while this job's Reload was in flight; the load then records the newer version, which
 // covers this job too. Equal path, or the same batch at a version >= the job's, counts as done.
                        if (_loads.TryGetValue(job.Id, out lr) && (long)lr.Ms >= job.ReloadIssuedMs && lr.Ok &&
                            (job.ServedPath == null || string.Equals(lr.Path, job.ServedPath, StringComparison.OrdinalIgnoreCase)
                             || S8aSnPaths.VersionOfPath(lr.Path) >= S8aSnPaths.VersionOfPath(job.ServedPath) && S8aSnPaths.VersionOfPath(job.ServedPath) > 0))
                            go = true;
                        else if (nowMs - (ulong)job.ReloadIssuedMs >= ReloadUnseenMs && !job.Reissued)
                        {
                            bool loaded;
                            try { loaded = job.Batch.IsLoaded(); } catch (Exception) { loaded = true; }
                            if (loaded)
                            {
                                job.Reissued = true;
                                job.Phase = 0;
                                job.Streamer.streamingThread.Enqueue(_runJob, this, job);   // Reload again (the first was cancelled or served the old file)
                                DeLog.Info("DE.S8a reload of " + job.Id + " re-issued: no load of the served file seen in " + ReloadUnseenMs + " ms (" + job.Why + ")");
                            }
                        }
                    }
                    else
                    {
                        bool loaded;
                        try { loaded = job.Batch.IsLoaded(); } catch (Exception) { loaded = true; }
                        if (!loaded) job.SawUnloaded = true;
                        else if (job.SawUnloaded || nowMs - job.StartMs >= ReloadUnseenMs) go = true;
                    }
                    if (go)
                    {
                        job.ReloadedMs = nowMs;
                        job.Streamer.streamingThread.Enqueue(_runJob, this, job);   // phase 1 body: the cell reloads
                    }
                    else if (nowMs - job.StartMs >= JobGiveUpMs)
                    {
                        _jobGiveUps++;
                        Finish(job, nowMs, true, "the batch did not load the served file in " + JobGiveUpMs + " ms");
                        _jobs.RemoveAt(i);
                    }
                    continue;
                }
                if (job.Phase != 2)
                {
                    if (nowMs - job.StartMs >= JobGiveUpMs) { _jobGiveUps++; Finish(job, nowMs, true, "the reload did not run in time"); _jobs.RemoveAt(i); }
                    continue;
                }
                if (job.Failed)
                {
                    Finish(job, nowMs, true, job.Error);
                    _jobs.RemoveAt(i);
                    continue;
                }
                if (job.NotInRing)
                {
                    if (job.Item != null) job.Item.NeedsReload = false;
                    Finish(job, nowMs, false, "not in the ring");
                    _jobs.RemoveAt(i);
                    continue;
                }
                if (job.Cells >= 0 && !job.Done)
                {
                    bool built;
                    try { built = RingRangeLoaded(job.Clipmap, job.Mins, job.Maxs); }
                    catch (Exception) { built = true; }
                    if (!built)
                    {
                        if (nowMs - job.StartMs < JobGiveUpMs) continue;
                        _jobGiveUps++;
                        Finish(job, nowMs, true, "the level-0 cells over the box did not load in time");
                        _jobs.RemoveAt(i);
                        continue;
                    }
                    Finish(job, nowMs, false, null);
                    _jobs.RemoveAt(i);
                }
            }
        }

        /// <summary>
 /// CONTRACT "applied" as the ring allows: every level-0 cell of the box's cell range that the ring
 /// holds is loaded (<c>ClipmapCell.IsLoaded
 /// (ClipmapCell)</c>, static); cells outside the ring never load and are not waited for. No level 0: true.
 /// </summary>
        private static bool RingRangeLoaded(ClipmapStreamer clipmap, int[] mins, int[] maxs)
        {
            ClipmapLevel lv = clipmap.GetLevel(0);
            if (lv == null) return true;
            int[] cmin, cmax;
            S8aCellRange.Range(0, lv.cellSize, mins, maxs, out cmin, out cmax);
            for (int x = cmin[0]; x <= cmax[0]; x++)
                for (int y = cmin[1]; y <= cmax[1]; y++)
                    for (int z = cmin[2]; z <= cmax[2]; z++)
                    {
                        ClipmapCell cell = lv.GetCell(new Int3(x, y, z));
                        if (cell == null) continue;
                        if (!ClipmapCell.IsLoaded(cell)) return false;
                    }
            return true;
        }

        private void Finish(ReloadJob job, ulong nowMs, bool failed, string error)
        {
            job.Done = true;
            if (job.Clipmap != null && job.Mins != null && job.Maxs != null && _watches.Count < MaxWatches)
                _watches.Add(new CellWatch { Clipmap = job.Clipmap, Streamer = job.Streamer, Mins = job.Mins, Maxs = job.Maxs, DueMs = nowMs + WatchFirstMs, Id = job.Id });
            _reloadsDone++;
            _cellsReloaded += job.Cells;
            if (job.Item != null)
            {
                job.Item.CellsReloaded = job.Cells;
                _tracker.MarkApplied(job.Item, nowMs, failed, error);
                if (!failed) FloraAfter(job.Item, nowMs);
                NoticeRow(job.Item, failed ? "failed" : "applied");
            }
            else
            {
                Row(new JsonOut().Str("t", Utc()).Num("tickMs", nowMs).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.reload").Str("ev", "s8a.reload")
                    .Obj("data").Num("snFrame", DeLog.Frame).Num("bx", (long)job.Id.X).Num("by", (long)job.Id.Y).Num("bz", (long)job.Id.Z).Str("why", job.Why)
                    .Bool("inRing", !job.NotInRing).Num("cellsReloaded", (long)job.Cells).Num("ms", (long)(nowMs - job.StartMs)).Bool("failed", failed).Str("error", error).EndObj().ToString());
            }
        }

        // ------------------------------------------------------------------------------------------ C7, the gate, the biome

 /// <summary>C7 (every Subnautica frame): the S8a block at 0x8C.</summary>
        public void FillStatus(ref SnStatus s)
        {
            byte flags = 0;
            if (_storeOpen) flags |= C7S8aFlags.StoreOpen;
            if (S8aPatches.GetPathPatched) flags |= C7S8aFlags.GetPathPatched;
            if (S8aPatches.LoaderPatched) flags |= C7S8aFlags.LoaderPatched;
            if (S8aPatches.CenterWidened) flags |= C7S8aFlags.CenterWidened;
            if (_refused || _disabled.Count > 0) flags |= C7S8aFlags.Refused;
            var c = new S8aC7
            {
                version = (byte)C7S8a.Version, flags = flags, reloadsPending = (ushort)System.Math.Min(65535, _tracker.Pending), ackSeq = _tracker.AckSeq, appliedSeq = _tracker.AppliedSeq,
                stream = _tracker.Stream, resyncEpoch = _reader.ResyncEpochSeen, chartSeqApplied = _chartSeqApplied, batchesServed = (uint)System.Math.Min(uint.MaxValue, (ulong)_view.Answers),
                reloadFailures = (uint)System.Math.Min(uint.MaxValue, (ulong)(_tracker.Failures + _validationFailures)), lastLatencyMs = _tracker.LastLatencyMs,
                nearPendingSeen = (ushort)System.Math.Min(65535, _nearPendingSeen), storeKey = _storeKey,
            };
            c.WriteInto(ref s);
        }

        /// <summary>
 /// CONTRACT "window.ready" (the S1 telemetry's hook): Subnautica's own areaBuilt and not NearPending and every window
 /// notice meeting the near box applied; NearEmpty (every near window id empty or absent) lets areaBuilt ignore
 /// unbuilt empty clipmap cells. Also the camera the biome rule and the spawns use.
 /// </summary>
        public bool AreaBuiltGate(bool native, Vector3 cam)
        {
            _camLast = cam;
            _haveCam = true;
            _nativeLast = native;
            bool gate = native;
            if (_reader.LastHeaderValid)
            {
                S8aHeader h = _reader.LastHeader;
                if (h.Has(S8aConventions.C2S8aFlags.NearPending)) gate = false;
                else if (_nearPendingSeen > 0 && h.Has(S8aConventions.C2S8aFlags.WindowOn)) gate = false;
                else if (!native && h.Has(S8aConventions.C2S8aFlags.NearEmpty) && h.Has(S8aConventions.C2S8aFlags.WindowOn) && S8aPatches.LoaderPatched) gate = true;
            }
            _gateLast = gate;
            return gate;
        }

        /// <summary>CONTRACT "biome.rule" beyond the crater box: the instance form of <see cref="BiomeOutsideTile"/>.</summary>
        public string BiomeAt(Vector3 p) { return BiomeOutsideTile(p); }

        // ------------------------------------------------------------------------------------------ rows

        private void NoticeRow(S8aReloadTracker.Item it, string state)
        {
            S8aNotice n = it.Notice;
            Row(new JsonOut().Str("t", Utc()).Num("tickMs", Win32.TickCount64()).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.notice").Str("ev", "s8a.notice")
                .Obj("data").Num("snFrame", DeLog.Frame).Num("seq", (long)n.seq).Num("kind", (long)n.kind).Num("layer", (long)n.layer).Num("flags", (long)n.flags)
                .Num("bx", (long)n.bx).Num("by", (long)n.by).Num("bz", (long)n.bz).Num("version", (long)n.version).Bool("served", it.Served).Str("state", state)
                .Num("scheduledMs", it.ScheduledMs > 0 ? (long)(it.ScheduledMs - it.SeenMs) : -1L).Num("appliedMs", it.AppliedMs > 0 ? (long)(it.AppliedMs - it.SeenMs) : -1L)
                .Num("cellsReloaded", (long)it.CellsReloaded).Bool("needsReload", it.NeedsReload).Str("error", it.Error)
                .Num("ackSeq", (long)_tracker.AckSeq).Num("appliedSeq", (long)_tracker.AppliedSeq).EndObj().ToString());
        }

        private void StatusRow()
        {
            S8aHeader h = _reader.LastHeader;
            Row(new JsonOut().Str("t", Utc()).Num("tickMs", Win32.TickCount64()).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.status").Str("ev", "s8a.status")
                .Obj("data").Num("snFrame", DeLog.Frame).Num("ackSeq", (long)_tracker.AckSeq).Num("appliedSeq", (long)_tracker.AppliedSeq).Num("chartSeqApplied", (long)_chartSeqApplied)
                .Num("served", _view.Answers).Num("viewEntries", (long)_view.Count).Num("failures", _tracker.Failures + _validationFailures).Num("nearPendingSeen", 0L)
                .Bool("areaBuiltNative", _nativeLast).Bool("areaBuilt", _gateLast).Num("stream", (long)_tracker.Stream).Num("pending", (long)_tracker.Pending)
                .Num("inFlight", (long)_tracker.InFlight).Num("jobs", (long)_jobs.Count).Num("notices", _notices).Num("reloads", _reloadsDone).Num("cellsReloaded", _cellsReloaded).Num("cellKicks", _cellKicks).Num("watches", (long)_watches.Count).Num("retriesKept", _retriesKept)
                .Num("floraTests", S8aFlora.Tests).Num("floraHidden", S8aFlora.Hidden).Num("floraLate", S8aFlora.LateTested).Num("floraLateHidden", S8aFlora.LateHidden).Num("floraNotLoaded", S8aFlora.NotLoaded).Num("floraCarvesLoaded", S8aFlora.CarvesLoaded)
                .Num("floraRegistered", S8aFlora.Registered).Num("floraRegistryFound", S8aFlora.RegistryFound).Num("floraWoken", S8aFlora.Woken)
                .Num("floraViaStart", S8aFlora.RegisteredStart).Num("floraViaCell", S8aFlora.RegisteredCell).Num("floraViaDev", S8aFlora.RegisteredDev)
                .Bool("cellRegisterPatched", S8aPatches.CellRegisterPatched)
                .Str("floraFamilies", S8aFlora.FamiliesText()).Num("chartViewMoved", _chartViewMoved).Num("jobGiveUps", _jobGiveUps).Bool("offSite", S8aOffSite.Active).Num("offSiteChunks", S8aOffSite.ChunksHidden)
                .Num("liveJobs", S8aLiveEdit.Jobs).Num("liveApplied", _liveApplied).Num("liveFallbacks", _liveFallbacks).Num("liveNotLoaded", _liveNotLoaded)
                .Num("octreesPatched", S8aLiveEdit.OctreesPatched).Num("cellsSwapped", S8aLiveEdit.CellsSwapped).Num("cellsDiscarded", S8aLiveEdit.CellsDiscarded)
                .Num("cellsDeferred", S8aLiveEdit.CellsDeferred).Num("liveErrors", S8aLiveEdit.Errors).Num("liveMsMax", _liveMsMax).Bool("liveOn", S8aLiveEdit.Enabled)
                .Num("finalized", S8aLiveEdit.Finalized).Num("finalizeMsAvg", S8aLiveEdit.Finalized > 0 ? System.Math.Round(S8aLiveEdit.TicksToMs(S8aLiveEdit.FinalizeTicks) / S8aLiveEdit.Finalized, 3) : 0.0)
                .Num("finalizeMsMax", System.Math.Round(S8aLiveEdit.TicksToMs(S8aLiveEdit.FinalizeMaxTicks), 2)).Num("finalizeMaxPerFrame", (long)S8aLiveEdit.FinalizeMaxPerFrame)
                .Num("craterDeposits", S8aCraterReports.Reported).Num("craterDepositsWritten", S8aCraterReports.Written).Bool("drillablePatched", S8aPatches.DrillablePatched)
                .Bool("headerValid", _reader.LastHeaderValid).Num("headerFlags", (long)h.flags).Str("storeKey", S8aSnPaths.Hex(_storeKey)).Bool("storeOpen", _storeOpen)
                .Num("drillGen", (long)h.drillGen).Num("overlayGen", (long)h.overlayGen).Num("getPathServed", S8aPatches.Served).Num("getPathPass", S8aPatches.PassThrough)
                .Num("m4Withheld", S8aPatches.M4Withheld).Num("readerMissed", _reader.Missed).Num("readerStale", _reader.Stale).Num("updateMsMax", _updateMsMax)
                .Num("updateMsAvg", _frames > 0 ? _updateMsSum / _frames : 0.0).Num("lastLatencyMs", (long)_tracker.LastLatencyMs)
                .Bool("windowOn", WindowOn).Num("nearPendingSeenCount", (long)_nearPendingSeen).Bool("headerNearPending", _reader.LastHeaderValid && h.Has(S8aConventions.C2S8aFlags.NearPending))
                .Bool("headerNearEmpty", _reader.LastHeaderValid && h.Has(S8aConventions.C2S8aFlags.NearEmpty)).Num("cameraBiome", (long)h.cameraBiome).Num("snapFrames", SnapFrames)
                .Num("chartChanges", _reader.ChartChanges).Num("spawnsLive", (long)_spawns.LiveCount).Num("windowLoads", S8aPatches.WindowLoads).EndObj().ToString());
            _updateMsMax = 0;
        }

        internal static void Row(string json)
        {
            var sb = new System.Text.StringBuilder(json.Length + 1);
            sb.Append(json).Append('\n');
            DeLog.AppendJsonlBatch(sb);
        }

        private static string Utc() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------------------------------------ C11

        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Cmd) return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            string action = S3Args.Str(a, "action", null);
            if (action == null)
            {
                if (a.ContainsKey("probe")) action = "probe";
                else if (a.ContainsKey("reload") || a.ContainsKey("all")) action = "reload";
                else if (a.ContainsKey("columns")) action = "rays";
                else if (a.ContainsKey("bx")) action = "verify";
                else action = "status";
            }
            try
            {
                switch (action)
                {
                    case "status": Status(reply); return true;
                    case "probe": Probe(a, reply); return true;
                    case "pose": Pose(a, reply); return true;
                    case "verify": Verify(a, reply); return true;
                    case "cellvis":
                    {
                        // Stage D oracle: per-frame s8a.cellvis rows over the latest edited box for {seconds} (0 stops)
                        double secs = S3Args.Num(a, "seconds", 30.0);
                        _visUntilMs = secs > 0 ? Win32.TickCount64() + (ulong)(secs * 1000.0) : 0;
                        if (secs > 0) { _visRun = 0; _visRunMax = 0; _visUncoveredFrames = 0; _visRows = 0; }
                        reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "cellvis").Num("seconds", secs).Num("rows", _visRows).Num("uncoveredFrames", _visUncoveredFrames)
                             .Num("runMax", _visRunMax).Bool("liveOn", S8aLiveEdit.Enabled).Num("liveJobs", S8aLiveEdit.Jobs).Num("cellsSwapped", S8aLiveEdit.CellsSwapped)
                             .Num("liveFallbacks", _liveFallbacks);
                        return true;
                    }
                    case "reload": Reload(a, reply, now); return true;
                    case "rays": Rays(a, req, reply, now); return true;
                    case "deposits":
                    {
                        // planet v2 R8: {on, clear, immune: n hits}: the deposits' switch, a clear, Subnautica's drill on the nearest (NO.immune)
                        object v;
                        if (a.TryGetValue("on", out v) && v is bool) _deposits.On = (bool)v;
                        if (S3Args.Flag(a, "clear", false)) _deposits.Clear();
                        reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "deposits");
                        double hits = S3Args.Num(a, "immune", 0.0);
                        if (hits > 0 && _haveCam)
                            foreach (var kv in _deposits.Immune(_camLast, (int)hits)) reply.Scalar("immune." + kv.Key, kv.Value);
                        foreach (var kv in _deposits.Status()) reply.Scalar(kv.Key, kv.Value);
                        if (_haveCam)
                        {
                            int nearN;
                            reply.Str("near", _deposits.NearList(_camLast, out nearN)).Num("liveNear", (long)nearN).Num("camX", _camLast.x).Num("camY", _camLast.y).Num("camZ", _camLast.z);
                        }
                        reply.Num("drillsRefused", S8aPatches.DrillsRefused).Bool("drillImmunePatched", S8aPatches.DrillImmunePatched);
                        return true;
                    }
                    case "oresurface": OreSurface(a, reply); return true;
                    case "orelook":
                    {
                        // planet v2, ores as voxels (S8a.NO.palette, NO.look): {as: "ours" | type}: the ore materials show our texture or
 // another palette type's material (the paint test); the slots, the template and the texture properties
                        string asWhat = S3Args.Str(a, "as", null);
                        object asv;
                        if (asWhat == null && a.TryGetValue("as", out asv) && asv != null) asWhat = Convert.ToString(asv, CultureInfo.InvariantCulture);
                        string res = asWhat != null ? S8aOrePalette.LookAs(asWhat) : null;
                        reply.Bool("ok", res == null || !res.StartsWith("error", StringComparison.Ordinal)).Str("cmd", Cmd).Str("action", "orelook");
                        if (res != null) reply.Str("result", res);
                        foreach (var kv in S8aOrePalette.Status()) reply.Scalar(kv.Key, kv.Value);
                        return true;
                    }
                    case "sample":
                    {
                        // planet v2 route B (S8a.NS.entities): {radius: 60, on, clear}: per family the records within the radius and the live objects
                        object v;
                        if (a.TryGetValue("on", out v) && v is bool) _sample.On = (bool)v;
                        if (S3Args.Flag(a, "clear", false)) _sample.Clear();
                        double rad = S3Args.Num(a, "radius", 60.0);
                        reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "sample").Bool("haveCam", _haveCam).Num("camX", _camLast.x).Num("camY", _camLast.y).Num("camZ", _camLast.z);
                        foreach (var kv in _sample.Census(_camLast, rad)) reply.Scalar(kv.Key, kv.Value);
                        return true;
                    }
                    case "life":
                    {
                        // a test run (S8a.NL.look): the started entities round the camera by kind (de.s8a life {radius: 40})
                        double rad = S3Args.Num(a, "radius", 40.0);
                        int cr, fl, ot, ou;
                        S8aFlora.Life(_camLast, (float)rad, out cr, out fl, out ot, out ou);
                        reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "life").Num("radius", rad).Num("creatures", (long)cr).Num("flora", (long)fl)
                             .Num("other", (long)ot).Num("ours", (long)ou).Num("life", (long)(cr + fl)).Bool("haveCam", _haveCam)
                             .Num("camX", _camLast.x).Num("camY", _camLast.y).Num("camZ", _camLast.z);
                        return true;
                    }
                    case "props":
                    {
                        // planet v2 R9 (S8a.ND.props): {at: [x, y, z]} spawns one test prop per family round the point; {status} reports them
                        S8aFloraProps.Command(a, reply, _host);
                        return true;
                    }
                    case "spawns":
                    {
                        // {on, clear}: the spawns' switch and a clear (S8a.N10, the teardown)
                        object v;
                        if (a.TryGetValue("on", out v) && v is bool) _spawns.On = (bool)v;
                        if (S3Args.Flag(a, "clear", false)) _spawns.Clear();
                        reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "spawns");
                        foreach (var kv in _spawns.Status()) reply.Scalar(kv.Key, kv.Value);
                        return true;
                    }
                    default: reply.Bool("ok", false).Str("cmd", Cmd).Str("error", "unknown action '" + action + "' (status | probe {sn} | verify {bx, by, bz} | reload {all | bx, by, bz} | rays {columns, label} | spawns)"); return true;
                }
            }
            catch (Exception e)
            {
                reply.Bool("ok", false).Str("cmd", Cmd).Str("error", action + " failed: " + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        private void Status(JsonObjectWriter reply)
        {
            S8aHeader h = _reader.LastHeader;
            reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "status").Str("conventions", S8aConventions.Header).Str("hash", S8aSnPaths.Hex(S8aConventions.Hash))
                 .Bool("getPathPatched", S8aPatches.GetPathPatched).Bool("loaderPatched", S8aPatches.LoaderPatched).Bool("centerWidened", S8aPatches.CenterWidened)
                 .Str("storeKey", S8aSnPaths.Hex(_storeKey)).Str("drillFolder", _drillFolder).Bool("storeOpen", _storeOpen).Num("viewEntries", (long)_view.Count)
                 .Num("served", _view.Answers).Num("misses", _view.Misses).Num("stream", (long)_tracker.Stream).Num("ackSeq", (long)_tracker.AckSeq).Num("appliedSeq", (long)_tracker.AppliedSeq)
                 .Num("pending", (long)_tracker.Pending).Num("inFlight", (long)_tracker.InFlight).Num("jobs", (long)_jobs.Count).Num("failures", _tracker.Failures + _validationFailures).Num("retries", _tracker.Retries)
                 .Num("timeouts", _tracker.Timeouts).Num("notices", _notices).Num("validations", _validations).Num("validationFailures", _validationFailures)
                 .Num("reloadsScheduled", _reloadsScheduled).Num("reloadsDone", _reloadsDone).Num("cellsReloaded", _cellsReloaded).Num("lastLatencyMs", (long)_tracker.LastLatencyMs)
                 .Bool("headerValid", _reader.LastHeaderValid).Num("headerFlags", (long)h.flags).Num("headerChartSeq", (long)h.chartSeq).Num("headerDrillGen", (long)h.drillGen)
                 .Num("headerOverlayGen", (long)h.overlayGen).Str("headerStoreKey", S8aSnPaths.Hex(h.storeKey)).Num("readerQueued", _reader.Queued).Num("readerMissed", _reader.Missed)
                 .Num("readerStale", _reader.Stale).Num("readerNoBlock", _reader.NoBlock).Num("readerCommits", _reader.Commits).Bool("registered", _registered)
                 .Num("updateMsMax", _updateMsMax).Num("m4Withheld", S8aPatches.M4Withheld).Bool("m4", S8aMutants.M4).Num("fallbacks", Interlocked.Read(ref _fallbacks))
                 .Bool("loaderPostfix", S8aPatches.LoaderPostfixPatched).Num("loadsSeen", (long)_loads.Count)
                 .Bool("windowOn", WindowOn).Bool("windowEnabled", S8aPatches.WindowEnabled).Num("windowLoads", S8aPatches.WindowLoads).Num("windowLoadFailures", S8aPatches.WindowLoadFailures)
                 .Num("windowNotices", _windowNotices).Num("clearedNotices", _clearedNotices).Num("chartSteps", _chartSteps).Num("chartSeqApplied", (long)_chartSeqApplied)
                 .Num("nearPendingSeen", (long)_nearPendingSeen).Num("snapFrames", SnapFrames).Bool("m7", S8aMutants.M7).Num("cameraBiome", _reader.LastHeaderValid ? (long)_reader.LastHeader.cameraBiome : 255L)
                 .Num("spawnsLive", (long)_spawns.LiveCount).Num("spawned", _spawns.Spawned).Num("despawned", _spawns.Despawned).Bool("spawnsOn", _spawns.On).Bool("m11", S8aMutants.M11)
                .Num("depositsLive", (long)_deposits.LiveCount).Num("sampleLive", (long)_sample.LiveCount)
                 .Str("featuresDisabled", _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()));
        }

        /// <summary><c>probe {sn: [x, y, z]}</c>: type and density of the voxel from the file the streamer would serve (the store's version through the postfix, else the install).</summary>
 /// <summary>Review HIGH 4 (S8a.N8.jump): <c>de.s8a pose {entity}</c>: the Subnautica-side camera position and a mirrored grid's drawn frame position with the tick it was posed at.</summary>
        private void Pose(Dictionary<string, object> a, JsonObjectWriter reply)
        {
            reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "pose").Bool("haveCam", _haveCam).Num("snFrame", DeLog.Frame).Num("chartSeqApplied", (long)_chartSeqApplied)
                 .Num("chartSeq", (long)(_reader.LastHeaderValid ? _reader.LastHeader.chartSeq : 0u));
            if (_haveCam) reply.Num("camX", (double)_camLast.x).Num("camY", (double)_camLast.y).Num("camZ", (double)_camLast.z);
            object v;
            if (a.TryGetValue("entity", out v) && v != null)
            {
                ulong id = 0;
                string text = v.ToString().Trim();
                bool parsed = v is double ? (id = (ulong)(double)v) >= 0 : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? ulong.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
                    : ulong.TryParse(text, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out id);
                reply.Str("entity", parsed ? id.ToString(CultureInfo.InvariantCulture) : text);
                MirrorPose p = parsed && MirrorPoseOf != null ? MirrorPoseOf(id) : default(MirrorPose);
                reply.Bool("entityFound", p.Ok);
                if (p.Ok) reply.Num("entityX", (double)p.X).Num("entityY", (double)p.Y).Num("entityZ", (double)p.Z).Num("entityTick", (long)p.Tick);
            }
        }

        private void Probe(Dictionary<string, object> a, JsonObjectWriter reply)
        {
            double x, y, z;
            object sv;
            if (!a.TryGetValue("sn", out sv) && !a.TryGetValue("probe", out sv) || !S3Args.TryVec(sv, out x, out y, out z))
            { reply.Bool("ok", false).Str("cmd", Cmd).Str("error", "probe: sn: [x, y, z] (SN metres)"); return; }
            int vx, vy, vz;
            S8aConvert.VoxelOf(new Vec3(x, y, z), out vx, out vy, out vz);
            int bx = S8aConvert.BatchOfVoxel(vx), by = S8aConvert.BatchOfVoxel(vy), bz = S8aConvert.BatchOfVoxel(vz);
            string path = null, source = "install";
            S8aServed s;
            if (_view.TryGet(bx, by, bz, out s) && !S8aMutants.M4) { path = s.Path; source = "store v" + s.Version; }
            else
            {
                LargeWorldStreamer lws = LargeWorldStreamer.main;
                if (lws != null && lws.streamerV2 != null && lws.streamerV2.octreesStreamer != null) path = lws.streamerV2.octreesStreamer.GetPath(new Int3(bx, by, bz));
            }
            if (path == null || !File.Exists(path))
            {
                // Stage C fix: a window id without a file is an answer, not a failure: the window never generated it (a layer away
 // from the camera's ring) or it is all water (BatchCleared); Subnautica serves empty water there. A crater id
 // without its install file stays an error.
                if (!S8aWindowPlan.InCraterBox(bx, bz))
                {
                    reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "probe").Bool("file", false).Num("bx", (long)bx).Num("by", (long)by).Num("bz", (long)bz)
                         .Num("type", 0L).Num("density", 0L).Bool("solid", false).Str("source", "none (window id not generated, or all water)");
                    return;
                }
                reply.Bool("ok", false).Str("cmd", Cmd).Str("error", "probe: no file for batch " + bx + "-" + by + "-" + bz + " (" + (path ?? "no streamer") + ")");
                return;
            }
            byte[] file = File.ReadAllBytes(path);
            S8aBatchFile b;
            string err;
            if (!S8aBatchFile.TryParse(file, file.Length, bx, by, bz, out b, out err)) { reply.Bool("ok", false).Str("cmd", Cmd).Str("error", "probe: " + path + ": " + err); return; }
            int ox = (vx - bx * 160) >> 5, oy = (vy - by * 160) >> 5, oz = (vz - bz * 160) >> 5;
            int oi = S8aBatchFile.OctreeIndex(ox, oy, oz);
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            b.DecodeOctree(oi, t, d);
            int li = S8aOctree.Index((vx - bx * 160) & 31, (vy - by * 160) & 31, (vz - bz * 160) & 31);
            reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "probe").Num("bx", (long)bx).Num("by", (long)by).Num("bz", (long)bz).Num("octree", (long)oi)
                 .Num("vx", (long)vx).Num("vy", (long)vy).Num("vz", (long)vz).Num("type", (long)t[li]).Num("density", (long)d[li]).Bool("solid", S8aOctree.IsBelowSurface(t[li], d[li]))
                 .Str("source", source).Str("path", path).Num("bytes", (long)file.Length).Str("fnv", S8aSnPaths.Hex(LayoutHash.Fnv1a64(file))).Num("nodes", (long)b.NodeCount(oi));
        }

        /// <summary>
 /// <c>oresurface {points: [[x, y, z, ore?],...], radius, voxels: [[x, y, z],...]}</c> (the crater's ore overlay, S8a.NV.detector and
 /// NV.mined): per point the served surface voxels (solid, density != 0; the store's newest version, else the install's file) within
 /// radius metres (voxel centres), how many carry the ore's Subnautica type (ore: an SE subtype; none: any ore slot), the nearest of
 /// them (and, with list n, the first n of them: typedList); per listed voxel its served type, density, solidity and whether the
 /// type is an ore slot.
 /// </summary>
        private void OreSurface(Dictionary<string, object> a, JsonObjectWriter reply)
        {
            double radius = System.Math.Max(1.0, System.Math.Min(12.0, S3Args.Num(a, "radius", 5.0)));
            int listMax = (int)System.Math.Max(0, System.Math.Min(512, S3Args.Num(a, "list", 0)));
            var oreSlot = new bool[256];
            for (int i = S8aMaterialMap.SurfaceMaterials; i < S8aMaterialMap.SnTypes.Length; i++) oreSlot[S8aMaterialMap.SnTypes[i]] = true;
            var files = new Dictionary<Int3Key, S8aBatchFile>();
            var srcOf = new Dictionary<Int3Key, string>();
            var dec = new Dictionary<long, byte[]>();
            int bv = S8aConventions.SnWorld.BatchVoxels;
            // the served voxel: type | density << 8, -1 = no data (no file)
            Func<int, int, int, int> voxel = (wx, wy, wz) =>
            {
                int bx = S8aConvert.BatchOfVoxel(wx), by = S8aConvert.BatchOfVoxel(wy), bz = S8aConvert.BatchOfVoxel(wz);
                var id = new Int3Key(bx, by, bz);
                S8aBatchFile f;
                if (!files.TryGetValue(id, out f))
                {
                    string path = null, source = "install";
                    S8aServed sv;
                    if (_view.TryGet(bx, by, bz, out sv) && !S8aMutants.M4) { path = sv.Path; source = "store v" + sv.Version; }
                    else
                    {
                        LargeWorldStreamer lws = LargeWorldStreamer.main;
                        if (lws != null && lws.streamerV2 != null && lws.streamerV2.octreesStreamer != null) path = lws.streamerV2.octreesStreamer.GetPath(new Int3(bx, by, bz));
                    }
                    f = null;
                    if (path != null && File.Exists(path))
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        string err;
                        if (!S8aBatchFile.TryParse(bytes, bytes.Length, bx, by, bz, out f, out err)) f = null;
                    }
                    files[id] = f;
                    srcOf[id] = f != null ? source : "none";
                }
                if (f == null) return -1;
                int lx = wx - bx * bv, ly = wy - by * bv, lz = wz - bz * bv;
                int oi = S8aBatchFile.OctreeIndex(lx >> 5, ly >> 5, lz >> 5);
                if (!f.Has(oi)) return 0;
                long key = ((((long)(bx + 64) * 128 + (by + 64)) * 128 + (bz + 64)) << 8) | (long)oi;
                byte[] r;
                if (!dec.TryGetValue(key, out r))
                {
                    var t = new byte[S8aOctree.Voxels];
                    var d = new byte[S8aOctree.Voxels];
                    f.DecodeOctree(oi, t, d);
                    r = new byte[2 * S8aOctree.Voxels];
                    Buffer.BlockCopy(t, 0, r, 0, S8aOctree.Voxels);
                    Buffer.BlockCopy(d, 0, r, S8aOctree.Voxels, S8aOctree.Voxels);
                    dec[key] = r;
                }
                int li = S8aOctree.Index(lx & 31, ly & 31, lz & 31);
                return r[li] | (r[S8aOctree.Voxels + li] << 8);
            };
            var rows = new System.Text.StringBuilder("[");
            object pv;
            var pts = a.TryGetValue("points", out pv) ? pv as IList<object> : null;
            int nPts = 0, nJudged = 0, nTyped = 0;
            int reach = (int)System.Math.Ceiling(radius);
            if (pts != null)
                foreach (object po in pts)
                {
                    double x, y, z;
                    if (!S3Args.TryVec(po, out x, out y, out z)) continue;
                    var pl = po as IList<object>;
                    string ore = pl != null && pl.Count >= 4 && pl[3] != null ? Convert.ToString(pl[3], CultureInfo.InvariantCulture) : null;
                    byte want = ore != null ? S8aMaterialMap.SnType(ore) : (byte)0;
                    if (want != 0 && !oreSlot[want]) want = 0;
                    int v0x, v0y, v0z;
                    S8aConvert.VoxelOf(new Vec3(x, y, z), out v0x, out v0y, out v0z);
                    long surface = 0, typed = 0, anyOre = 0;
                    double nearS = double.NaN, nearT = double.NaN;
                    double tx = double.NaN, ty = double.NaN, tz = double.NaN;
                    long typeAt = 0;
                    var listed = new System.Text.StringBuilder("[");
                    int nListed = 0;
                    for (int dz = -reach; dz <= reach; dz++)
                        for (int dy = -reach; dy <= reach; dy++)
                            for (int dx = -reach; dx <= reach; dx++)
                            {
                                int wx = v0x + dx, wy = v0y + dy, wz = v0z + dz;
                                double cx = S8aConventions.SnWorld.OriginX + wx + 0.5 - x, cy = S8aConventions.SnWorld.OriginY + wy + 0.5 - y, cz = S8aConventions.SnWorld.OriginZ + wz + 0.5 - z;
                                double dist = System.Math.Sqrt(cx * cx + cy * cy + cz * cz);
                                if (dist > radius) continue;
                                int vv = voxel(wx, wy, wz);
                                if (vv < 0) continue;
                                byte t = (byte)(vv & 255), dn = (byte)(vv >> 8);
                                if (dn == 0 || !S8aOctree.IsBelowSurface(t, dn)) continue;
                                surface++;
                                if (double.IsNaN(nearS) || dist < nearS) nearS = dist;
                                if (oreSlot[t]) anyOre++;
                                if (want != 0 ? t == want : oreSlot[t])
                                {
                                    typed++;
                                    if (nListed < listMax)
                                    {
                                        if (nListed++ > 0) listed.Append(',');
                                        listed.Append('[');
                                        Json.AppendNumber(listed, x + cx); listed.Append(',');
                                        Json.AppendNumber(listed, y + cy); listed.Append(',');
                                        Json.AppendNumber(listed, z + cz); listed.Append(']');
                                    }
                                    if (double.IsNaN(nearT) || dist < nearT)
                                    {
                                        nearT = dist;
                                        tx = x + cx; ty = y + cy; tz = z + cz;
                                        typeAt = t;
                                    }
                                }
                            }
                    nPts++;
                    if (surface > 0) nJudged++;
                    if (typed > 0) nTyped++;
                    if (rows.Length > 1) rows.Append(',');
                    var w = new JsonObjectWriter();
                    w.Num("surface", surface).Num("nearestSurfaceM", nearS).Str("ore", ore).Num("expected", (long)want).Num("typed", typed).Num("nearestTypedM", nearT)
                     .Num("typeAt", typeAt).Num("anyOre", anyOre);
                    if (listMax > 0) w.Raw("typedList", listed.Append(']').ToString());
                    if (!double.IsNaN(tx))
                    {
                        var ta = new System.Text.StringBuilder("[");
                        Json.AppendNumber(ta, tx); ta.Append(','); Json.AppendNumber(ta, ty); ta.Append(','); Json.AppendNumber(ta, tz); ta.Append(']');
                        w.Raw("typedAt", ta.ToString());
                    }
                    rows.Append(w.ToString());
                }
            rows.Append(']');
            var vox = new System.Text.StringBuilder("[");
            var vl = a.TryGetValue("voxels", out pv) ? pv as IList<object> : null;
            if (vl != null)
                foreach (object vo in vl)
                {
                    double x, y, z;
                    if (vox.Length > 1) vox.Append(',');
                    if (!S3Args.TryVec(vo, out x, out y, out z)) { vox.Append("null"); continue; }
                    int wx, wy, wz;
                    S8aConvert.VoxelOf(new Vec3(x, y, z), out wx, out wy, out wz);
                    int vv = voxel(wx, wy, wz);
                    var w = new JsonObjectWriter();
                    if (vv < 0) w.Bool("data", false);
                    else
                    {
                        byte t = (byte)(vv & 255), dn = (byte)(vv >> 8);
                        w.Bool("data", true).Num("type", (long)t).Num("density", (long)dn).Bool("solid", S8aOctree.IsBelowSurface(t, dn)).Bool("ore", oreSlot[t]);
                    }
                    vox.Append(w.ToString());
                }
            vox.Append(']');
            var srcs = new System.Text.StringBuilder("{");
            foreach (KeyValuePair<Int3Key, string> kv in srcOf)
            {
                if (srcs.Length > 1) srcs.Append(',');
                Json.AppendString(srcs, kv.Key.X + "-" + kv.Key.Y + "-" + kv.Key.Z);
                srcs.Append(':');
                Json.AppendString(srcs, kv.Value);
            }
            srcs.Append('}');
            reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "oresurface").Num("radius", radius).Num("points", (long)nPts).Num("withSurface", (long)nJudged).Num("withTyped", (long)nTyped)
                 .Raw("rows", rows.ToString()).Raw("voxels", vox.ToString()).Raw("sources", srcs.ToString());
        }

        /// <summary><c>verify {bx, by, bz}</c>: the served file's size and FNV read now, against the manifest's entry.</summary>
        private void Verify(Dictionary<string, object> a, JsonObjectWriter reply)
        {
            int bx = (int)S3Args.Num(a, "bx", int.MinValue), by = (int)S3Args.Num(a, "by", int.MinValue), bz = (int)S3Args.Num(a, "bz", int.MinValue);
            if (bx == int.MinValue || by == int.MinValue || bz == int.MinValue) { reply.Bool("ok", false).Str("cmd", Cmd).Str("error", "verify: bx, by, bz"); return; }
            S8aServed s;
            bool served = _view.TryGet(bx, by, bz, out s);
            reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "verify").Num("bx", (long)bx).Num("by", (long)by).Num("bz", (long)bz).Bool("served", served);
            if (!served) { reply.Str("note", "the store names no version of this batch: the install is served"); return; }
            long bytes = -1;
            ulong fnv = 0;
            bool exists = File.Exists(s.Path);
            if (exists)
            {
                byte[] file = File.ReadAllBytes(s.Path);
                bytes = file.Length;
                fnv = LayoutHash.Fnv1a64(file);
            }
            S8aManifest m = _manifest;
            S8aManifest.Entry me = null;
            if (m != null) { S8aManifest.Entry e; if (m.TryGet(bx, by, bz, out e)) me = e; }
            if (S3Args.Flag(a, "octrees", false))
            {
                // Stage D round trip (S8a.ND.roundtrip): the octrees the streamers hold = the served file loaded the loader's way
                LargeWorldStreamer lws = LargeWorldStreamer.main;
                WorldStreamer ws = lws != null ? lws.streamerV2 : null;
                int compared = 0, differ = 0, streamers = 0;
                string note = ws == null ? "no streamer" : null;
                if (ws != null) S8aLiveEdit.Compare(ws, new Int3Key(bx, by, bz), s.Path, out compared, out differ, out streamers, out note);
                LoadRecord lr;
                bool loadedLive = _loads.TryGetValue(new Int3Key(bx, by, bz), out lr) && lr.Path != null && string.Equals(lr.Path, s.Path, StringComparison.OrdinalIgnoreCase);
                reply.Num("octreesCompared", (long)compared).Num("octreesDiffer", (long)differ).Num("streamers", (long)streamers).Str("octreesNote", note).Bool("loadedPathIsServed", loadedLive);
            }
            reply.Num("version", (long)s.Version).Str("path", s.Path).Bool("exists", exists).Num("bytes", bytes).Str("fnv", S8aSnPaths.Hex(fnv))
                 .Num("noticeBytes", s.Bytes).Str("noticeFnv", S8aSnPaths.Hex(s.Fnv)).Bool("fromNotice", s.FromNotice)
                 .Bool("manifestHas", me != null).Num("manifestVersion", me != null ? (long)me.Version : -1L).Str("manifestFnv", me != null ? S8aSnPaths.Hex(me.Fnv) : null)
                 .Bool("match", exists && bytes == s.Bytes && fnv == s.Fnv && (me == null || (me.Version == s.Version && me.Fnv == fnv)));
        }

        /// <summary><c>reload {all: true}</c> or <c>{bx, by, bz}</c>: every served batch (or that one) reloaded with its clipmap cells (S8a.N5).</summary>
        private void Reload(Dictionary<string, object> a, JsonObjectWriter reply, ulong now)
        {
            bool all = S3Args.Flag(a, "all", false) || S3Args.Flag(a, "reload", false);
            var ids = new List<Int3Key>();
            if (all) foreach (S8aServed s in _view.Snapshot()) ids.Add(s.Id);
            else
            {
                int bx = (int)S3Args.Num(a, "bx", int.MinValue), by = (int)S3Args.Num(a, "by", int.MinValue), bz = (int)S3Args.Num(a, "bz", int.MinValue);
                if (bx == int.MinValue || by == int.MinValue || bz == int.MinValue) { reply.Bool("ok", false).Str("cmd", Cmd).Str("error", "reload: all: true, or bx, by, bz"); return; }
                ids.Add(new Int3Key(bx, by, bz));
            }
            int scheduled = 0;
            foreach (Int3Key id in ids)
            {
                S8aServed s;
                string served = _view.TryGet(id.X, id.Y, id.Z, out s) ? s.Path : null;
                ScheduleReload(null, id, new[] { id.X * 160, id.Y * 160, id.Z * 160 }, new[] { id.X * 160 + 159, id.Y * 160 + 159, id.Z * 160 + 159 }, now, all ? "de.s8a reload all" : "de.s8a reload", served);
                scheduled++;
            }
            DeLog.Info("DE.S8a reload " + (all ? "all" : ids[0].ToString()) + ": " + scheduled + " batch reloads scheduled (served entries " + _view.Count + ")" + (S8aMutants.M4 ? " (S8a.M4 ON: GetPath withholds the store)" : ""));
            reply.Bool("ok", true).Str("cmd", Cmd).Str("action", "reload").Bool("all", all).Num("scheduled", (long)scheduled).Num("viewEntries", (long)_view.Count).Bool("m4", S8aMutants.M4);
        }

        /// <summary><c>rays {columns: [[x, z], …], label}</c>: the S4 raycast service (layer-30 rays after Physics.SyncTransforms, S4's camera hold) with the label as its batch id.</summary>
        private void Rays(Dictionary<string, object> a, ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (_s4Forward == null) { reply.Bool("ok", false).Str("cmd", Cmd).Str("error", "rays: the S4 raycast service is not started"); return; }
            object cols;
            if (!a.TryGetValue("columns", out cols)) { reply.Bool("ok", false).Str("cmd", Cmd).Str("error", "rays: columns: [[x, z], ...]"); return; }
            string label = S3Args.Str(a, "label", "s8a.rays");
            var args = new Dictionary<string, object>(StringComparer.Ordinal) { { "probe", cols }, { "id", label }, { "hold", true }, { "mesh", false } };
            object v;
            if (a.TryGetValue("fromY", out v)) args["fromY"] = v;
            if (a.TryGetValue("timeoutMs", out v)) args["timeoutMs"] = v;
            int columns = cols is IList<object> ? ((IList<object>)cols).Count : -1;
            Row(new JsonOut().Str("t", Utc()).Num("tickMs", now).Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.rays").Str("ev", "s8a.rays")
                .Obj("data").Num("snFrame", DeLog.Frame).Str("label", label).Num("columns", (long)columns).Str("via", "de.s4 probe").EndObj().ToString());
            var fwd = new ControlRequest { Id = req.Id, Cmd = S4Rules.Cmd, Args = args };
            if (!_s4Forward(fwd, reply, now)) reply.Bool("ok", false).Str("cmd", Cmd).Str("error", "rays: de.s4 did not take the request");
        }

        // ------------------------------------------------------------------------------------------ lifecycle

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(_active, this)) _active = null;
            try { if (_c2 != null) _c2.Extensions.Remove(_reader); } catch (Exception) { }
            try { _spawns.Dispose(); } catch (Exception) { }
            try { _deposits.Dispose(); } catch (Exception) { }
            try { _sample.Dispose(); } catch (Exception) { }
            try { S8aLiveEdit.DropFinalQueue(); } catch (Exception) { }
            _view.Clear();
            _jobs.Clear();
            DeLog.Info("DE.S8a host disposed: notices " + _notices + " reloads " + _reloadsDone + " served " + _view.Answers + " failures " + (_tracker.Failures + _validationFailures));
        }
    }
}
