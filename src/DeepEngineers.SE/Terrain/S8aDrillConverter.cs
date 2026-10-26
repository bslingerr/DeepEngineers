using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using VRage.Game;
using VRage.Voxels;
using VRageMath;
using NoticeFlags = DeepEngineers.Wire.S8aConventions.NoticeFlags;
using NoticeLayer = DeepEngineers.Wire.S8aConventions.NoticeLayer;
using StoreC = DeepEngineers.Wire.S8aConventions.Store;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// S8a drill converter (CONTRACT §3.3 "Conversion", "The store", budgets; §5.4 formats convert.sample,
 /// convert.copyonwrite, store.path, store.manifest; ARCHITECTURE §5.17): one worker thread (below normal, Windows
 /// background mode as S4's prefetcher) that turns the edited SE ranges of the tiles into new versions of Subnautica
 /// batch files in the store's drill layer and queues a notice per file. The conversion itself is Wire's
 /// <see cref="S8aConvertCore"/>; this class wraps the
 /// tiles' storages in an <see cref="IS8aTileSource"/>
 /// <list type="bullet">
 /// <item>Coalescing: a batch is rewritten <c>CoalesceMs</c> + <c>CoalesceMsPerMB</c> × its base file's size after its last
 /// edit, at the latest <c>S8aWindow.CoalesceMaxMs</c> after its first; the edits of the wait are one job.</item>
 /// <item>Base: the batch's newest store version (the manifest), else the install's file (read in place, read-only),
 /// else an empty batch. The notice box is the min/max of the voxels that actually changed.</item>
 /// <item>Files: <c>&lt;root&gt;\&lt;storeKey x16&gt;\drill\compiled-batch-&lt;bx&gt;-&lt;by&gt;-&lt;bz&gt;.v&lt;version&gt;.optoctrees</c>,
 /// a version never overwritten (the versions of the folder are scanned at start); <c>manifest.v1.txt</c> rewritten at
 /// most once per second and at unload (temp file then replace); a version superseded <c>GraceMs</c> ago whose
 /// successor's notice Subnautica has applied without a new failure is deleted.</item>
 /// <item>The manifest's hash: the first live (non-restore) write after a restore or save sets it to
 /// the dirty sentinel 0; only the save postfix sets it to the saved overlay's hash, applied once every pending job of
 /// that save has been converted. At the next load a manifest whose hash is 0 or differs from the restored overlay's is
 /// dropped and the layer rebuilt, so an unsaved session never leaves phantom holes in the store.</item>
 /// <item>Fail-safe (CONTRACT §3.3 (1)): a root inside either install, SNAppData or the world folder (S8a.N6), or one that
 /// cannot be written, logs <c>DE.S8a STORE REFUSE &lt;reason&gt;</c> once and clears the header's DrillOn flag; SE's own
 /// overlay keeps working and saving. S8a.M1 suppresses the notices (files still written); S8a.M2 samples at the voxel
 /// corner; S8a.M5 puts the root under the SE world folder (throwaway copies only).</item>
 /// </list>
 /// Rows: <c>s8a.write</c> {tick, layer, bx, by, bz, version, bytes, fnv, ms, path}; the notices' rows are
 /// <see cref="S8aFeed"/>'s.
 /// </summary>
    internal static class S8aDrillConverter
    {
        private struct MirrorJob
        {
            public int Src, Dst;
            public Vector3I SrcMin, SrcMax, DstMin, DstMax;
        }

        private sealed class BatchJob
        {
            public Int3Key Id;
            public readonly List<S8aEditBox> Edits = new List<S8aEditBox>(8);
            public long FirstMs, LastMs;
            public bool Restore, Live;
            public double BaseMb;
        }

        private sealed class Superseded
        {
            public string Path;
            public uint Seq;
            public long SinceMs;
            public uint FailuresAtAdd;
        }

        /// <summary>The tiles' storages as the core's tile source (worker thread; one MyStorageData per worker).</summary>
        internal sealed class TileSource : IS8aTileSource
        {
            private readonly MyStorageData _data;

            public TileSource(MyStorageData data) { _data = data; }

            public int Count { get { return TerrainTiles.Tiles.Count; } }
            public int TileSize { get { return SiteV1.Tile; } }

            public bool TryGetTile(int index, out Vec3 placeMin)
            {
                IReadOnlyList<TerrainTiles.TileInfo> tiles = TerrainTiles.Tiles;
                placeMin = Vec3.Zero;
                if (index < 0 || index >= tiles.Count) return false;
                TerrainTiles.TileInfo t = tiles[index];
                if (t.Map == null || t.Map.Closed || !(t.Map.Storage is MyStorageBase)) return false;
                placeMin = t.PlaceMin;
                return true;
            }

            public int TileAt(Vec3 se)
            {
                TerrainTiles.TileInfo t = TerrainTiles.TileAt(se);
                if (t == null) return -1;
                IReadOnlyList<TerrainTiles.TileInfo> tiles = TerrainTiles.Tiles;
                for (int i = 0; i < tiles.Count; i++) if (ReferenceEquals(tiles[i], t)) return i;
                return -1;
            }

            public void ReadRange(int index, int minX, int minY, int minZ, int maxX, int maxY, int maxZ, byte[] content, byte[] material)
            {
                var st = TerrainTiles.Tiles[index].Map.Storage as MyStorageBase;
                var rmin = new Vector3I(minX, minY, minZ);
                var rmax = new Vector3I(maxX, maxY, maxZ);
                _data.Resize(rmin, rmax);
                st.ReadRange(_data, MyStorageDataTypeFlags.ContentAndMaterial, 0, rmin, rmax);
                int n = (maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1);
                Buffer.BlockCopy(_data[MyStorageDataTypeEnum.Content], 0, content, 0, n);
                Buffer.BlockCopy(_data[MyStorageDataTypeEnum.Material], 0, material, 0, n);
            }
        }

        private static readonly object Lock = new object();
        private static readonly Dictionary<Int3Key, BatchJob> _pending = new Dictionary<Int3Key, BatchJob>();
        private static readonly List<MirrorJob> _mirrors = new List<MirrorJob>(64);
        private static readonly Dictionary<Int3Key, uint> _versions = new Dictionary<Int3Key, uint>();
        private static readonly List<Superseded> _superseded = new List<Superseded>();
        private static S8aManifest _manifest;
        private static bool _manifestDirty;
        private static long _manifestWrittenMs;
        private static string _root, _drillFolder, _worldPath, _installDir;
        private static ulong _storeKey, _overlayHash, _savedHash;
        private static bool _savedHashValid, _dirtySinceSave;
        private static Thread _thread;
        private static volatile bool _stop, _running;
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static byte[] _materialTable;
        private static bool _refused;
        private static long _written, _notices, _mirrorsDone, _converted, _octreesRewritten, _cellsChanged, _errors, _deleted, _kept, _writeMsMax, _writeMsSum;
        private static uint _drillGen;
        private static double _lastMs;

        public static bool Running { get { return _running && !_refused; } }
        /// <summary>Drill files written this stream (the C2 header's drillGen).</summary>
        public static uint DrillGen { get { return _drillGen; } }
        public static string Root { get { return _root; } }
        public static string DrillFolder { get { return _drillFolder; } }
        public static int Pending { get { lock (Lock) return _pending.Count; } }
        /// <summary>The manifest's hash as written (0 = dirty: live edits since the last save).</summary>
        public static ulong ManifestHash { get { lock (Lock) return _manifest != null ? _manifest.Hash : 0; } }

        // ================================================================================================= start / stop

 /// <summary>
 /// BeforeStart (game thread), after the overlay restore: the root check (S8a.N6), the folder, the manifest (kept when
 /// its key and hash equal this world's and the restored overlay's and it is not dirty; else a fresh one: the drill
 /// layer is rebuilt by the restore's ranges), the material table, the worker. Returns whether the manifest matched.
 /// </summary>
        public static bool Start(string root, ulong storeKey, ulong overlayHash, string worldPath, bool skipRootCheck)
        {
            Stop();
            _refused = false;
            _root = root;
            _storeKey = storeKey;
            _overlayHash = overlayHash;
            _savedHash = overlayHash;
            _savedHashValid = true;
            _dirtySinceSave = false;
            _worldPath = worldPath;
            _drillGen = 0;
            _written = _notices = _mirrorsDone = _converted = _octreesRewritten = _cellsChanged = _errors = _deleted = _kept = _writeMsMax = _writeMsSum = 0;
            lock (Lock) { _pending.Clear(); _mirrors.Clear(); _versions.Clear(); _superseded.Clear(); }
            S8aOreOverlaySe.Reset();
            try
            {
                SnSourceManifest m = TerrainGate.Manifest;
                _installDir = m != null && !string.IsNullOrEmpty(m.OctreeDir) ? m.OctreeDir : SnSourceManifest.OctreeDirOf(TerrainGate.SubnauticaDir);
            }
            catch (Exception) { _installDir = null; }
            if (!skipRootCheck)
            {
                string why = S8aStore.CheckRoot(root, ForbiddenRoots(worldPath));
                if (why != null) { Refuse(why); return false; }
            }
            _drillFolder = S8aStore.DrillFolder(root, storeKey);
            try
            {
                Directory.CreateDirectory(_drillFolder);
                string probe = Path.Combine(_drillFolder, ".write-test");
                File.WriteAllBytes(probe, new byte[] { 1 });
                File.Delete(probe);
            }
            catch (Exception e) { Refuse("root not writable: " + e.GetType().Name + ": " + e.Message); return false; }
            bool matched = false;
            try
            {
                string err;
                S8aManifest existing = S8aManifest.ReadFrom(_drillFolder, out err);
                matched = existing != null && existing.Layer == S8aStore.DrillLayer && existing.Key == storeKey && existing.Hash == overlayHash && existing.Hash != 0;
                if (matched)
                {
                    _manifest = existing;
                    // every listed file must exist; else the layer is rebuilt
                    foreach (S8aManifest.Entry e in existing.Entries)
                        if (!File.Exists(Path.Combine(root, e.RelativePath))) { matched = false; break; }
                }
                if (!matched)
                {
                    _manifest = new S8aManifest { Layer = S8aStore.DrillLayer, Key = storeKey, Hash = overlayHash };
                    if (existing != null)
                        DeLog.Info("DE.S8a drill manifest dropped: " + (existing.Key != storeKey ? "another store key" : existing.Hash == 0 ? "dirty (an earlier session's unsaved edits)" :
                                   existing.Hash != overlayHash ? "overlayHash " + S8aStore.Hex16(existing.Hash) + " != restored " + S8aStore.Hex16(overlayHash) : "a listed file is missing") +
                                   " (" + existing.Count + " entries; the drill layer is rebuilt from the restored overlay)");
                }
                ScanVersions();
                _manifestDirty = true;
                FlushManifest(true);
            }
            catch (Exception e) { Refuse("manifest: " + e.GetType().Name + ": " + e.Message); return false; }
            _materialTable = BuildMaterialTable();
            int swept = SweepUnreferenced();
            _stop = false;
            int token = Interlocked.Increment(ref _runToken);     // a stopped run never acts on the next run's state
            _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "DE.S8a drill", Priority = ThreadPriority.BelowNormal };
            _running = true;
            _thread.Start();
            if (swept > 0) DeLog.Info("DE.S8a drill folder swept: " + swept + " unreferenced file(s) of earlier sessions deleted");
            DeLog.Info("DE.S8a drill converter started: folder=" + _drillFolder + " manifest=" + _manifest.Count + " entries (" + (matched ? "kept" : "new") + ") versions=" + _versions.Count +
                       " install=" + _installDir + " coalesce=" + StoreC.CoalesceMs + "ms+" + StoreC.CoalesceMsPerMB + "ms/MB");
            return matched;
        }

        private static IEnumerable<string> ForbiddenRoots(string worldPath)
        {
            var l = new List<string>();
            // the SE install: Bin64's parent, from the game's own assembly. Not the running exe's folder: under the installer SE
 // runs through Pulsar from %LOCALAPPDATA%\DeepEngineers\Pulsar, whose parent holds the store itself
            try { l.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Sandbox.Game.World.MySession).Assembly.Location), ".."))); } catch (Exception) { }
            try
            {
                string sn = TerrainGate.SubnauticaDir;
                if (!string.IsNullOrEmpty(sn)) { l.Add(sn); l.Add(Path.Combine(sn, "SNAppData")); }
            }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(worldPath)) l.Add(worldPath);
            return l;
        }

        private static void Refuse(string reason)
        {
            _refused = true;
            _running = false;
            S8aOverlay.StoreRefused = true;
            DeLog.Info("DE.S8a STORE REFUSE " + reason + " (the drill layer is off: Subnautica keeps what it serves; SE's own overlay keeps working and saving)");
            Jsonl.WriteLine(S4Rows.Row("s8a.store", "tick", (long)GameState.GameplayTick, "refused", true, "reason", reason, "root", _root));
        }

        /// <summary>Unload (game thread): the worker stops, the manifest is flushed.</summary>
        public static void Stop()
        {
            Thread t = _thread;
            _thread = null;
            _stop = true;
            Interlocked.Increment(ref _runToken);
            _running = false;
            Wake.Set();
            if (t != null && t.IsAlive)
            {
                try { if (!t.Join(3000)) DeLog.Info("DE.S8a drill worker did not stop in 3 s (left as a background thread)"); } catch (Exception) { }
            }
            try { if (_manifest != null && !_refused) FlushManifest(true); } catch (Exception) { }
            lock (Lock) { _pending.Clear(); _mirrors.Clear(); }
        }

        /// <summary>
 /// The save postfix: the manifest's overlayHash follows the overlay just saved, applied at once when nothing is pending,
 /// else by the worker when every job queued before the save has been converted (the saved overlay already holds
 /// those edits). A live edit after the save dirties the hash again.
 /// </summary>
        public static void SetOverlayHash(ulong hash)
        {
            _overlayHash = hash;
            lock (Lock)
            {
                _savedHash = hash;
                _savedHashValid = true;
                _dirtySinceSave = false;
            }
            ApplySavedHashIfClean();
            Wake.Set();
        }

        /// <summary>Worker and game thread: the manifest takes the saved hash once no job is pending and no live edit came after the save.</summary>
        private static void ApplySavedHashIfClean()
        {
            lock (Lock)
            {
                if (_manifest == null || !_savedHashValid || _dirtySinceSave || _pending.Count > 0) return;
                if (_manifest.Hash == _savedHash) return;
                _manifest.Hash = _savedHash;
                _manifestDirty = true;
            }
        }

        /// <summary>Review low "old drill versions never swept across sessions": at start every batch file the manifest does not name goes (a dropped manifest: every file; Subnautica serves only what the manifest or a notice names).</summary>
        private static int SweepUnreferenced()
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (S8aManifest.Entry e in _manifest.Entries) keep.Add(Path.GetFullPath(Path.Combine(_root, e.RelativePath)));
            int n = 0;
            try
            {
                foreach (string f in Directory.GetFiles(_drillFolder, "compiled-batch-*.optoctrees"))
                {
                    if (keep.Contains(Path.GetFullPath(f))) continue;
                    try { File.Delete(f); n++; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return n;
        }

        /// <summary>The manifest flushed now.</summary>
        public static void FlushManifestNow()
        {
            try { FlushManifest(true); } catch (Exception) { }
        }

        private static int _runToken;

        private static void ScanVersions()
        {
            foreach (string f in Directory.GetFiles(_drillFolder, "compiled-batch-*.optoctrees"))
            {
                int bx, by, bz;
                uint v;
                if (!S8aStore.TryParseBatchFileName(Path.GetFileName(f), out bx, out by, out bz, out v)) continue;
                var id = new Int3Key(bx, by, bz);
                uint cur;
                if (!_versions.TryGetValue(id, out cur) || v > cur) _versions[id] = v;
            }
            foreach (S8aManifest.Entry e in _manifest.Entries)
            {
                uint cur;
                if (!_versions.TryGetValue(e.Id, out cur) || e.Version > cur) _versions[e.Id] = e.Version;
            }
        }

        /// <summary>SE material index → Subnautica block type, from this session's definitions.</summary>
        private static byte[] BuildMaterialTable()
        {
            if (S8aMutants.M10) return S8aMaterialMap.UniformTable(1);
            MyDefinitionManager dm = MyDefinitionManager.Static;
            return S8aMaterialMap.BuildTable(i =>
            {
                MyVoxelMaterialDefinition def = dm != null ? dm.GetVoxelMaterialDefinition((byte)i) : null;
                return def != null ? def.Id.SubtypeName : null;
            });
        }

        // ================================================================================================= queues (game thread)

        public static void EnqueueMirror(int src, Vector3I srcMin, Vector3I srcMax, int dst, Vector3I dstMin, Vector3I dstMax)
        {
            if (!_running) return;
            lock (Lock)
            {
                if (_mirrors.Count >= 4096) return;
                _mirrors.Add(new MirrorJob { Src = src, SrcMin = srcMin, SrcMax = srcMax, Dst = dst, DstMin = dstMin, DstMax = dstMax });
            }
            Wake.Set();
        }

        /// <summary>An edited range of a tile (tile voxel coordinates, inclusive): the batches its F image meets get a job; a live (non-restore) edit dirties the manifest.</summary>
        public static void EnqueueEdit(int tile, Vector3I min, Vector3I max, bool restore)
        {
            if (!_running) return;
            IReadOnlyList<TerrainTiles.TileInfo> tiles = TerrainTiles.Tiles;
            SiteMap map = TerrainTiles.Map;
            if (tile < 0 || tile >= tiles.Count || map == null) return;
            TerrainTiles.TileInfo t = tiles[tile];
            int vx0, vy0, vz0, vx1, vy1, vz1;
            S8aConvertCore.SnBoxOf(map, t.PlaceMin, min.X, min.Y, min.Z, max.X, max.Y, max.Z, out vx0, out vy0, out vz0, out vx1, out vy1, out vz1);
            int b0x = S8aConvert.BatchOfVoxel(vx0), b1x = S8aConvert.BatchOfVoxel(vx1);
            int b0y = S8aConvert.BatchOfVoxel(vy0), b1y = S8aConvert.BatchOfVoxel(vy1);
            int b0z = S8aConvert.BatchOfVoxel(vz0), b1z = S8aConvert.BatchOfVoxel(vz1);
            long now = (long)Win32.TickCount64();
            lock (Lock)
            {
                if (!restore) _dirtySinceSave = true;
                for (int bx = b0x; bx <= b1x; bx++)
                    for (int by = b0y; by <= b1y; by++)
                        for (int bz = b0z; bz <= b1z; bz++)
                        {
                            if (!S8aBatchFile.IsCraterId(bx, by, bz) || by < 0 || by >= S8aConventions.SnWorld.BatchesY) continue;   // S8a-1: the crater's drill layer only
                            var id = new Int3Key(bx, by, bz);
                            BatchJob j;
                            if (!_pending.TryGetValue(id, out j))
                            {
                                j = new BatchJob { Id = id, FirstMs = now, BaseMb = -1.0 };   // the base's size is read by the worker, never under this lock
                                _pending[id] = j;
                            }
                            j.LastMs = now;
                            j.Restore |= restore;
                            j.Live |= !restore;
                            if (j.Edits.Count < 4096) j.Edits.Add(new S8aEditBox(tile, min.X, min.Y, min.Z, max.X, max.Y, max.Z));
                        }
            }
            Wake.Set();
        }

        private static double BaseMb(Int3Key id)
        {
            try
            {
                S8aManifest.Entry e;
                if (_manifest != null && _manifest.TryGet(id.X, id.Y, id.Z, out e)) return e.Bytes / 1048576.0;
                if (_installDir != null)
                {
                    var fi = new FileInfo(Path.Combine(_installDir, SnOctreeFormat.BatchFileName(id.X, id.Y, id.Z)));
                    if (fi.Exists) return fi.Length / 1048576.0;
                }
            }
            catch (Exception) { }
            return 0.0;
        }

        /// <summary>Game thread, every update: nothing heavy (the worker owns the files); kept for symmetry with the other hosts.</summary>
        public static void Tick(ulong nowMs) { }

        // ================================================================================================= the worker

        private static void Run(int token)
        {
            bool bg = Background.Enter();
            var data = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
            var core = new S8aConvertCore();
            var source = new TileSource(data);
            var due = new List<BatchJob>(16);
            var unsized = new List<BatchJob>(16);
            try
            {
                while (!_stop && token == _runToken)
                {
                    Wake.WaitOne(50);
                    if (_stop || token != _runToken) break;
                    long now = (long)Win32.TickCount64();
                    lock (Lock)
                    {
                        unsized.Clear();
                        foreach (BatchJob j in _pending.Values) if (j.BaseMb < 0) unsized.Add(j);
                    }
                    foreach (BatchJob j in unsized) j.BaseMb = BaseMb(j.Id);     // file sizes off the lock
                    lock (Lock)
                    {

                        // RangeChanged -> MyVoxelPhysicsBody.InvalidateRange -> Havok crashed with an AccessViolation
 // (Havok shapes are game-thread only). The mirrors now run from S8aHost.Tick (TickMirrors).
                        due.Clear();
                        foreach (BatchJob j in _pending.Values)
                        {
                            if (j.BaseMb < 0) continue;
                            long wait = StoreC.CoalesceMs + (long)(StoreC.CoalesceMsPerMB * j.BaseMb);
                            // night queue (nd, session 20261023-194214: one write per pass): a drill cutting every 250 ms
 // kept re-arming the wait, so nothing was written until it stopped; like the window's (CONTRACT §3.3
 // "Window": at most 1.5 s after the first) a batch is now also due S8aWindow.CoalesceMaxMs after its first edit
                            if (now - j.LastMs >= wait || now - j.FirstMs >= S8aWindow.CoalesceMaxMs) due.Add(j);
                        }
                        foreach (BatchJob j in due) _pending.Remove(j.Id);
                    }
                    foreach (BatchJob j in due)
                    {
                        if (_stop) break;
                        try { Convert(j, core, source); }
                        catch (Exception e) { _errors++; Log("convert " + j.Id + " " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e)); }
                    }

                    if (!_stop && token == _runToken)
                    {
                        try { S8aOreOverlaySe.WorkerStep(now, IsPending); }
                        catch (Exception e) { _errors++; Log("ore overlay " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e)); }
                    }
                    Housekeeping(now);
                }
            }
            catch (Exception e) { _errors++; Log("worker " + e.GetType().Name + ": " + e.Message); }
            finally { if (bg) Background.Leave(); }
        }

        private static readonly MyStorageData _gameData = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
        private static readonly List<MirrorJob> _gameMirrors = new List<MirrorJob>(8);
        /// <summary>Game thread (S8aHost.Tick): up to <paramref name="max"/> mirror writes into the overlapping tiles. WriteRange with
 /// notifyRangeChanged raises RangeChanged, which invalidates the Havok voxel shape: game thread only.</summary>
        public static void TickMirrors(int max)
        {
            _gameMirrors.Clear();
            lock (Lock)
            {
                int n = Math.Min(max, _mirrors.Count);
                for (int i = 0; i < n; i++) _gameMirrors.Add(_mirrors[i]);
                if (n > 0) _mirrors.RemoveRange(0, n);
            }
            foreach (MirrorJob m in _gameMirrors)
            {
                try { DoMirror(m, _gameData); }
                catch (Exception e) { _errors++; Log("mirror " + e.GetType().Name + ": " + e.Message); }
            }
            _gameMirrors.Clear();
        }

        private static void DoMirror(MirrorJob m, MyStorageData data)
        {
            IReadOnlyList<TerrainTiles.TileInfo> tiles = TerrainTiles.Tiles;
            if (m.Src >= tiles.Count || m.Dst >= tiles.Count) return;
            TerrainTiles.TileInfo a = tiles[m.Src], b = tiles[m.Dst];
            var sa = a.Map != null && !a.Map.Closed ? a.Map.Storage as MyStorageBase : null;
            var sb = b.Map != null && !b.Map.Closed ? b.Map.Storage as MyStorageBase : null;
            if (sa == null || sb == null) return;
            Vector3I size = m.SrcMax - m.SrcMin + 1;
            if (size != m.DstMax - m.DstMin + 1) return;
            data.Resize(size);
            sa.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, 0, m.SrcMin, m.SrcMax);
            S8aOverlay.MirrorWrite(sb, data, m.DstMin, m.DstMax);
            _mirrorsDone++;
            Jsonl.WriteLine(S4Rows.Row("s8a.overlay", "tick", (long)GameState.GameplayTick, "cells", (long)size.X * size.Y * size.Z, "hash", null, "why", "mirror",
                                       "from", a.I + "," + a.J + "," + a.K, "to", b.I + "," + b.J + "," + b.K,
                                       "min", new object[] { m.DstMin.X, m.DstMin.Y, m.DstMin.Z }, "max", new object[] { m.DstMax.X, m.DstMax.Y, m.DstMax.Z }));
        }

        private static void Convert(BatchJob j, S8aConvertCore core, TileSource source)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SiteMap map = TerrainTiles.Map;
            if (map == null || TerrainTiles.Tiles.Count == 0) return;
            Int3Key id = j.Id;
            // the base: the newest store version, else the install's file, else empty
            S8aBatchFile batch;
            string baseFrom;
            if (!LoadBase(id, out batch, out baseFrom)) return;
            core.MaterialTable = _materialTable ?? S8aMaterialMap.UniformTable(S8aMaterialMap.Fallback);
            core.SnOffset = S8aMutants.M2 ? 0.0 : S8aConvert.SampleOffset;
            S8aConvertResult r = core.ConvertBatch(map, source, batch, j.Edits);
            if (r.Skipped > 0 && r.Note != null) Log(id + ": " + r.Skipped + " octree(s) skipped: " + r.Note);
            _converted++;
            // the ore overlay of the edited octrees (a vein drilled out loses its paint, a wall cut into one takes it): the edits' SN box
 // grown by the overlay's reach
            int ex0, ey0, ez0, ex1, ey1, ez1;
            EditsSnBox(map, j.Edits, out ex0, out ey0, out ez0, out ex1, out ey1, out ez1);
            S8aOreOverlayResult ov = ex1 >= ex0 ? S8aOreOverlaySe.ApplyInline(batch, ex0, ey0, ez0, ex1, ey1, ez1) : null;
            bool oreChanged = ov != null && ov.Changed;
            if (r.OctreesRewritten == 0 && !oreChanged) return;   // the edit changed no Subnautica voxel (an edit inside solid rock or open water): no new version
            int mnx = r.MinX, mny = r.MinY, mnz = r.MinZ, mxx = r.MaxX, mxy = r.MaxY, mxz = r.MaxZ;
            if (oreChanged)
            {
                mnx = Math.Min(mnx, ov.MinX); mny = Math.Min(mny, ov.MinY); mnz = Math.Min(mnz, ov.MinZ);
                mxx = Math.Max(mxx, ov.MaxX); mxy = Math.Max(mxy, ov.MaxY); mxz = Math.Max(mxz, ov.MaxZ);
            }
            if (!WriteVersion(id, batch, j.Restore, j.Live, mnx, mny, mnz, mxx, mxy, mxz, r.OctreesRewritten + (oreChanged ? ov.OctreesRewritten : 0), r.Cells,
                              baseFrom, "drill", j.Edits.Count, j.LastMs - j.FirstMs, t0, ov)) return;
            _octreesRewritten += r.OctreesRewritten;
            _cellsChanged += r.Cells;
            // the batches within the overlay's reach of the edits paint again against this version
            if (ex1 >= ex0) S8aOreOverlaySe.InvalidateAround(ex0, ey0, ez0, ex1, ey1, ez1, id);
        }

        /// <summary>The SN world voxel box (inclusive) of the edits' images through F (empty: ex1 &lt; ex0).</summary>
        private static void EditsSnBox(SiteMap map, IList<S8aEditBox> edits, out int ex0, out int ey0, out int ez0, out int ex1, out int ey1, out int ez1)
        {
            ex0 = ey0 = ez0 = int.MaxValue;
            ex1 = ey1 = ez1 = int.MinValue;
            IReadOnlyList<TerrainTiles.TileInfo> tiles = TerrainTiles.Tiles;
            foreach (S8aEditBox e in edits)
            {
                if (e.Tile < 0 || e.Tile >= tiles.Count) continue;
                int vx0, vy0, vz0, vx1, vy1, vz1;
                S8aConvertCore.SnBoxOf(map, tiles[e.Tile].PlaceMin, e.MinX, e.MinY, e.MinZ, e.MaxX, e.MaxY, e.MaxZ, out vx0, out vy0, out vz0, out vx1, out vy1, out vz1);
                ex0 = Math.Min(ex0, vx0); ey0 = Math.Min(ey0, vy0); ez0 = Math.Min(ez0, vz0);
                ex1 = Math.Max(ex1, vx1); ey1 = Math.Max(ey1, vy1); ez1 = Math.Max(ez1, vz1);
            }
        }

        /// <summary>A drill job pending for the batch (the ore pass leaves it to the job).</summary>
        private static bool IsPending(Int3Key id) { lock (Lock) return _pending.ContainsKey(id); }

        /// <summary>
 /// A new version of batch <paramref name="id"/> into the drill folder: the file (never overwritten), the manifest entry (a live
 /// write dirties the layer until the next save; the ore overlay's own passes are not live: a function of the saved edits), the
 /// row <c>s8a.write</c>, the notice and the superseded version's bookkeeping. False: the write failed (the store refused).
 /// </summary>
        private static bool WriteVersion(Int3Key id, S8aBatchFile batch, bool restore, bool live, int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
                                         int octrees, uint cells, string baseFrom, string kind, int edits, long coalesceMs, long t0, S8aOreOverlayResult ore)
        {
            uint version;
            lock (Lock)
            {
                uint cur;
                version = (_versions.TryGetValue(id, out cur) ? cur : 0) + 1;
                _versions[id] = version;
            }
            string name = S8aStore.BatchFileName(id.X, id.Y, id.Z, version);
            string path = Path.Combine(_drillFolder, name);
            long tw = System.Diagnostics.Stopwatch.GetTimestamp();
            long bytes;
            ulong fnv;
            try
            {
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16))
                {
                    batch.WriteTo(fs);
                    bytes = fs.Length;
                }
                fnv = batch.Fnv();
            }
            catch (Exception e) { Refuse("write " + name + ": " + e.GetType().Name + ": " + e.Message); return false; }
            double writeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - tw) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            ushort flags = restore ? NoticeFlags.Restore : (ushort)0;
            if (batch.AllSolid) flags |= NoticeFlags.AllSolid;
            if (batch.AllEmpty) flags |= NoticeFlags.AllEmpty;
            string previous = null;
            lock (Lock)
            {
                S8aManifest.Entry old;
                if (_manifest.TryGet(id.X, id.Y, id.Z, out old)) previous = old.RelativePath;
                _manifest.Set(new S8aManifest.Entry { Bx = id.X, By = id.Y, Bz = id.Z, Version = version, Bytes = bytes, Fnv = fnv, Flags = flags, RelativePath = S8aStore.RelativeDrillPath(_storeKey, id.X, id.Y, id.Z, version) });
                if (live && _manifest.Hash != 0) _manifest.Hash = 0;     // BLOCKER 1: live edits dirty the layer until the next save
                _manifestDirty = true;
            }
            _written++;
            _drillGen++;
            _writeMsSum += (long)writeMs;
            if ((long)writeMs > _writeMsMax) _writeMsMax = (long)writeMs;
            _lastMs = totalMs;
            Jsonl.WriteLine(S4Rows.Row("s8a.write", "tick", (long)GameState.GameplayTick, "layer", "drill", "kind", kind, "bx", id.X, "by", id.Y, "bz", id.Z, "version", (long)version,
                                       "bytes", bytes, "fnv", S8aStore.Hex16(fnv), "ms", totalMs, "writeMs", writeMs, "path", path, "base", baseFrom,
                                       "octrees", octrees, "cells", (long)cells, "edits", edits, "restore", restore, "live", live,
                                       "box", new object[] { minX, minY, minZ, maxX, maxY, maxZ }, "coalesceMs", coalesceMs, "manifestHash", S8aStore.Hex16(ManifestHash),
                                       "orePainted", ore != null ? ore.Painted : 0L, "oreRestored", ore != null ? ore.Restored : 0L, "oreSurface", ore != null ? ore.OreSurface : 0L));
            DeLog.Info("DE.S8a drill write " + name + " v" + version + " (" + kind + ") " + bytes + " B fnv=" + S8aStore.Hex16(fnv) + " octrees=" + octrees + " cells=" + cells +
                       " box=" + minX + "," + minY + "," + minZ + "-" + maxX + "," + maxY + "," + maxZ +
                       (ore != null && ore.Changed ? " ore painted=" + ore.Painted + " restored=" + ore.Restored : "") +
                       " base=" + baseFrom + " in " + totalMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms (write " + writeMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms)" +
                       (S8aMutants.M1 ? " (S8a.M1 ON: notice suppressed)" : ""));
            uint seq = 0;
            if (!S8aMutants.M1)
            {
                var notice = S8aNotice.BatchWritten(NoticeLayer.Drill, id.X, id.Y, id.Z, version, flags, minX, minY, minZ, maxX, maxY, maxZ, octrees, cells);
                seq = S8aFeed.Enqueue(notice);
                _notices++;
            }
            if (previous != null)
                lock (Lock) _superseded.Add(new Superseded { Path = Path.Combine(_root, previous), Seq = seq, SinceMs = (long)Win32.TickCount64(), FailuresAtAdd = S8aFeed.LastC7.reloadFailures });
            Wake.Set();
            return true;
        }

        /// <summary>The ore overlay's write (worker): a pass that changed the batch, not live (a function of the saved edits and the rule).</summary>
        internal static bool WriteOreVersion(Int3Key id, S8aBatchFile batch, S8aOreOverlayResult ore, string baseFrom, long t0)
        {
            return WriteVersion(id, batch, false, false, ore.MinX, ore.MinY, ore.MinZ, ore.MaxX, ore.MaxY, ore.MaxZ, ore.OctreesRewritten, (uint)(ore.Painted + ore.Restored),
                                baseFrom, "ore", 0, 0, t0, ore);
        }

        /// <summary>The batch's base (the newest store version, else the install's file, else empty) for the ore overlay (worker or game thread).</summary>
        internal static bool LoadBaseFor(Int3Key id, out S8aBatchFile batch, out string from) { return LoadBase(id, out batch, out from); }

        /// <summary>The install's own file of the batch (parsed, read-only), or null.</summary>
        internal static S8aBatchFile LoadInstall(Int3Key id)
        {
            if (_installDir == null) return null;
            string path = Path.Combine(_installDir, SnOctreeFormat.BatchFileName(id.X, id.Y, id.Z));
            if (!File.Exists(path)) return null;
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[fs.Length];
                int off = 0;
                while (off < bytes.Length) { int r = fs.Read(bytes, off, bytes.Length - off); if (r <= 0) break; off += r; }
            }
            S8aBatchFile b;
            string err;
            return S8aBatchFile.TryParse(bytes, bytes.Length, id.X, id.Y, id.Z, out b, out err) ? b : null;
        }

        private static bool LoadBase(Int3Key id, out S8aBatchFile batch, out string from)
        {
            batch = null;
            from = null;
            string path = null;
            S8aManifest.Entry e;
            lock (Lock) { if (_manifest != null && _manifest.TryGet(id.X, id.Y, id.Z, out e)) path = Path.Combine(_root, e.RelativePath); }
            if (path != null && File.Exists(path)) from = "store v" + Path.GetFileName(path);
            else if (_installDir != null)
            {
                path = Path.Combine(_installDir, SnOctreeFormat.BatchFileName(id.X, id.Y, id.Z));
                from = File.Exists(path) ? "install" : null;
            }
            if (from == null)
            {
                batch = S8aBatchFile.CreateEmpty(id.X, id.Y, id.Z);
                from = "empty";
                return true;
            }
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[fs.Length];
                int off = 0;
                while (off < bytes.Length) { int r = fs.Read(bytes, off, bytes.Length - off); if (r <= 0) break; off += r; }
            }
            string err;
            if (!S8aBatchFile.TryParse(bytes, bytes.Length, id.X, id.Y, id.Z, out batch, out err))
            {
                Log("base " + path + " unreadable: " + err + " (an empty batch is used)");
                batch = S8aBatchFile.CreateEmpty(id.X, id.Y, id.Z);
                from = "empty(bad " + from + ")";
            }
            return true;
        }

        // ================================================================================================= housekeeping

        private static void Housekeeping(long now)
        {
            ApplySavedHashIfClean();
            bool dirty;
            lock (Lock) dirty = _manifestDirty;
            if (dirty && now - _manifestWrittenMs >= 1000) FlushManifest(false);
            // superseded versions: deleted GraceMs after Subnautica's appliedSeq covers the successor's notice and no reload
 // failed meanwhile; a version whose successor's notice was never sent (seq 0: S8a.M1, the feed down) is kept
            uint applied = S8aFeed.AppliedSeq;
            uint failures = S8aFeed.LastC7.reloadFailures;
            List<Superseded> gone = null;
            lock (Lock)
            {
                for (int i = _superseded.Count - 1; i >= 0; i--)
                {
                    Superseded s = _superseded[i];
                    if (now - s.SinceMs < StoreC.GraceMs) continue;
                    if (s.Seq == 0 || failures != s.FailuresAtAdd)
                    {
                        _superseded.RemoveAt(i);     // kept on disk: Subnautica may still serve it
                        _kept++;
                        continue;
                    }
                    if ((int)(applied - s.Seq) < 0) continue;
                    if (gone == null) gone = new List<Superseded>();
                    gone.Add(s);
                    _superseded.RemoveAt(i);
                }
            }
            if (gone == null) return;
            foreach (Superseded s in gone)
            {
                try { if (File.Exists(s.Path)) { File.Delete(s.Path); _deleted++; } }
                catch (Exception) { lock (Lock) _superseded.Add(new Superseded { Path = s.Path, Seq = s.Seq, SinceMs = now, FailuresAtAdd = s.FailuresAtAdd }); }   // Subnautica may still hold it: later
            }
        }

        private static void FlushManifest(bool force)
        {
            S8aManifest m;
            lock (Lock)
            {
                if (_manifest == null || (!_manifestDirty && !force)) return;
                m = _manifest;
                _manifestDirty = false;
            }
            try
            {
                lock (m) m.WriteAtomic(_drillFolder);
                _manifestWrittenMs = (long)Win32.TickCount64();
            }
            catch (Exception e) { _errors++; Log("manifest write " + e.GetType().Name + ": " + e.Message); lock (Lock) _manifestDirty = true; }
        }

        private static void Log(string what) { DeLog.Info("DE.S8a drill " + what); }

        private static string FirstFrame(Exception e)
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(e, true);
                System.Diagnostics.StackFrame f = st.FrameCount > 0 ? st.GetFrame(0) : null;
                return f != null && f.GetMethod() != null ? "at " + f.GetMethod().Name + ":" + f.GetFileLineNumber() : "";
            }
            catch (Exception) { return ""; }
        }

        public static Dictionary<string, object> Status()
        {
            int pending, mirrors, versions, superseded, entries;
            ulong mh;
            bool dirty;
            lock (Lock)
            {
                pending = _pending.Count; mirrors = _mirrors.Count; versions = _versions.Count; superseded = _superseded.Count;
                entries = _manifest != null ? _manifest.Count : 0;
                mh = _manifest != null ? _manifest.Hash : 0;
                dirty = _dirtySinceSave;
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "running", Running }, { "refused", _refused }, { "root", _root }, { "folder", _drillFolder }, { "install", _installDir },
                { "storeKey", S8aStore.Hex16(_storeKey) }, { "overlayHash", S8aStore.Hex16(_overlayHash) }, { "manifestHash", S8aStore.Hex16(mh) }, { "dirtySinceSave", dirty },
                { "manifestEntries", entries }, { "pending", pending }, { "mirrorsQueued", mirrors }, { "mirrorsDone", _mirrorsDone }, { "converted", _converted }, { "written", _written },
                { "drillGen", (long)_drillGen }, { "notices", _notices }, { "octreesRewritten", _octreesRewritten }, { "cellsChanged", _cellsChanged },
                { "versions", versions }, { "superseded", superseded }, { "deleted", _deleted }, { "keptSuperseded", _kept }, { "errors", _errors },
                { "writeMsMax", _writeMsMax }, { "writeMsAvg", _written > 0 ? (double)_writeMsSum / _written : 0.0 }, { "lastMs", _lastMs },
                { "ore", S8aOreOverlaySe.Status() },
            };
        }

        /// <summary>Windows thread background mode (<c>SetThreadPriority(GetCurrentThread, THREAD_MODE_BACKGROUND_BEGIN)</c>), as S4's prefetcher.</summary>
        private static class Background
        {
            private const int ModeBegin = 0x00010000, ModeEnd = 0x00020000;

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetCurrentThread();

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetThreadPriority(IntPtr thread, int priority);

            public static bool Enter()
            {
                try { return SetThreadPriority(GetCurrentThread(), ModeBegin); }
                catch (Exception) { return false; }
            }

            public static void Leave()
            {
                try { SetThreadPriority(GetCurrentThread(), ModeEnd); }
                catch (Exception) { }
            }
        }
    }
}
