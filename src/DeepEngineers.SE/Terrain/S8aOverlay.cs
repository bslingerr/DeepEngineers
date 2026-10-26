using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Voxels;
using VRageMath;
using OverlayC = DeepEngineers.Wire.S8aConventions.Overlay;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// S8a drill overlay (CONTRACT §3.3 "Drill overlay (S8a-1)", §5.4 formats guard.lift, overlay.file, overlay.persist;
 /// ARCHITECTURE §5.17 "Data flow, drilling"). While <see cref="DrillOn"/>, S4's edit guards let every edit path through
 /// on a tile (<see cref="AllowsEdit"/>, the one-line hook in <c>TerrainGuards.Block</c>): SE's <c>MyOctreeStorage</c>
 /// writes its own leaves over the provider's (copy-on-write; the provider has no write member, so it is never written;
 /// mining yields, physics and ore stay SE's). Each <c>MyStorageBase.RangeChanged</c> on a tile
 /// is only
 /// enqueued (any thread, a lock and a list add); the game thread drains the queue in <see cref="Tick"/>: the 16³
 /// LOD-0 overlay cells the range touches are marked dirty (keyed by tile and cell, never by player: v3-safe), the range
 /// is mirrored into every other tile whose box holds it (the 4-voxel overlaps at pitch 1,020 m; the mirror
 /// <c>WriteRange</c> runs on the converter's worker under the thread-static mirror flag, which this class lets
 /// through the guard), and the drill converter is asked to rewrite the Subnautica batches it meets.
 /// <para>Persistence ("overlay.persist"): a Harmony <b>postfix</b> on <c>MySession.Save(out MySessionSnapshot, string,
 /// Action&lt;SaveProgress&gt)</c>
 /// reads the dirty cells back from the
 /// tiles (<c>MyStorageBase.ReadRange(MyStorageData, MyStorageDataTypeFlags, int, Vector3I, Vector3I)</c>, LOD 0,
 /// game thread) and stores <c>snapshot.CompressedVoxelSnapshots["DE_S8a_overlay"]</c>
 /// In <c>BeforeStart</c>, right after the 34 tiles
 /// (<see cref="OnTilesCreated"/>, the hook in <c>TerrainTiles</c>), the file is written back into the tiles
 /// (<c>WriteRange(MyStorageData, MyStorageDataTypeFlags, Vector3I, Vector3I, bool notifyRangeChanged = true, bool skipCache
 /// = false)</c> under the restore flag, notify true) and the store's drill layer is rebuilt when its manifest's
 /// <c>overlayHash</c> differs from the overlay just restored.</para>
 /// <para>Fail-safe (CONTRACT §3.3 (4)): a file that does not decode, or carries another site version, logs
 /// <c>DE.S8a OVERLAY REFUSE &lt;reason&gt;</c> once; the tiles stay the provider's, the next saves carry the original bytes
 /// unchanged (so SE's cleanup does not drop it) and this session's edits are not persisted (logged).
 /// <c>DrillOn</c> off (env <c>DEEPENGINEERS_S8A_DRILL=off</c>, or S8a off) restores S4's behaviour exactly: the
 /// guards block, nothing is subscribed, nothing is saved or restored.</para>
 /// Budgets: the guard decision and the enqueue ≤ 20 µs; the save postfix ≤ 50 ms per 1,000 cells; the restore ≤ 2 s per
 /// 10,000 cells. Rows: <c>s8a.edit</c> (each range let through, rate-limited per path to 10/s with a count),
 /// <c>s8a.overlay</c> {cells, hash, why save|restore|mirror}.
 /// </summary>
    internal static class S8aOverlay
    {
        public const string EnvS8a = "DEEPENGINEERS_S8A", EnvDrill = "DEEPENGINEERS_S8A_DRILL";
        public const int MaxQueue = 65536;
        public const int EditRowsPerSecondPerPath = 10;

        /// <summary><c>DEEPENGINEERS_S8A=on</c>: the slice is on (read once at Init by <see cref="ReadEnv"/>).</summary>
        public static bool Enabled { get; private set; }
        /// <summary><c>DEEPENGINEERS_S8A_DRILL</c> (default on with S8a on).</summary>
        public static bool DrillSetting { get; private set; }
        /// <summary>The store refused the drill layer (root not writable, disk full): the header's DrillOn flag clears, SE's own overlay keeps working and saving.</summary>
        public static bool StoreRefused { get; internal set; }

        /// <summary>"guard.lift" in force: the slice is on, drilling is on, the tiles are hooked.</summary>
        public static bool DrillOn { get { return Enabled && DrillSetting && _hooked; } }

        /// <summary>The drill layer of the store writes files (DrillOn and the store not refused): the C2 header's DrillOn flag.</summary>
        public static bool DrillLayerOn { get { return DrillOn && !StoreRefused; } }

        [ThreadStatic] private static bool _mirrorWrite;
        [ThreadStatic] private static bool _restoreWrite;

        private struct Pending
        {
            public int Tile;
            public Vector3I Min, Max;
            public bool Mirror, Restore;
            public MyStorageDataTypeFlags Flags;
        }

        private static readonly object QLock = new object();
        private static List<Pending> _queue = new List<Pending>(256);
        private static List<Pending> _drain = new List<Pending>(256);
        private static long _enqueued, _queueDropped;

        private static readonly HashSet<ulong> _cells = new HashSet<ulong>();
        private static uint _overlayGen;
        private static ulong _hash;
        private static bool _hooked;
        private static MyStorageBase[] _storages;
        private static Action<Vector3I, Vector3I, MyStorageDataTypeFlags>[] _handlers;
        private static int[] _lastPath;
        private static long[] _lastPathMs;
        private static readonly Dictionary<long, int> _tileIndexByKey = new Dictionary<long, int>();
        private static string _worldPath;
        private static ulong _storeKey;
        private static ulong _restoredHash;
        private static int _restoredCells;
        private static bool _restoreRefused, _refuseLogged, _capLogged;
        private static byte[] _carried;
        private static bool _manifestMatched;
        private static long _editRows, _editSuppressed, _mirrorJobs, _convertJobs, _saves, _lastSaveMs, _lastSaveCells;
        private static readonly long[] _pathWindowStart = new long[TerrainPaths.Names.Length + 1];
        private static readonly int[] _pathWindowCount = new int[TerrainPaths.Names.Length + 1];
        public static bool SavePostfixInstalled { get; private set; }

        public static int Cells { get { lock (_cells) return _cells.Count; } }
        public static uint OverlayGen { get { return _overlayGen; } }
        /// <summary>The overlay hash of the last save or restore (the manifest's overlayHash).</summary>
        public static ulong Hash { get { return _hash; } }
        public static ulong StoreKey { get { return _storeKey; } }
        public static string WorldPath { get { return _worldPath; } }
        public static bool ManifestMatched { get { return _manifestMatched; } }
        public static bool RestoreRefused { get { return _restoreRefused; } }

        // ================================================================================================= settings

 /// <summary>Plugin Init: <c>DEEPENGINEERS_S8A</c> (off unless "on"/"1"/"true"), <c>DEEPENGINEERS_S8A_DRILL</c> (on unless "off"/"0"/"false").</summary>
        public static void ReadEnv()
        {
            Enabled = EnvOn(EnvS8a, false);
            DrillSetting = EnvOn(EnvDrill, true);
        }

        internal static bool EnvOn(string name, bool def)
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(name); } catch (Exception) { }
            if (string.IsNullOrEmpty(v)) return def;
            v = v.Trim().ToLowerInvariant();
            if (v == "on" || v == "1" || v == "true" || v == "yes") return true;
            if (v == "off" || v == "0" || v == "false" || v == "no") return false;
            return def;
        }

        /// <summary>Tests and <c>de.s8a</c>: the switches without the environment.</summary>
        internal static void Configure(bool enabled, bool drill)
        {
            Enabled = enabled;
            DrillSetting = drill;
        }

        // ================================================================================================= the guard hook

 /// <summary>
 /// The S4 guard's question for an edit on one of our tiles (<c>TerrainGuards.Block</c>, any thread, ≤ 20 µs): true =
 /// let the edit through. Our own mirror and restore writes always pass (thread-static flags); every other path passes
 /// while <see cref="DrillOn"/> (S8a.M12 keeps the guards: nothing passes). The path is remembered per tile so the
 /// <c>s8a.edit</c> row of the range that follows can name it.
 /// </summary>
        public static bool AllowsEdit(int path, MyVoxelBase v, MyStorageBase s)
        {
            if (_mirrorWrite || _restoreWrite) return true;
            if (!DrillOn || S8aMutants.M12) return false;
            try
            {
                MyStorageBase st = s ?? (v != null ? v.Storage as MyStorageBase : null);
                var prov = st != null ? st.DataProvider as SnTerrainProvider : null;
                if (prov != null)
                {
                    int idx;
                    if (_tileIndexByKey.TryGetValue(TileKey(prov.I, prov.J, prov.K), out idx))
                    {
                        _lastPath[idx] = path;
                        _lastPathMs[idx] = (long)Win32.TickCount64();
                    }
                }
            }
            catch (Exception) { }
            return true;
        }

        private static long TileKey(int i, int j, int k) { return ((long)i << 16) | ((long)j << 8) | (long)k; }

        // ================================================================================================= session

 /// <summary>The marker of a throwaway world copy (tools/lab/make_terrain_copy.py): the <c>DE_THROWAWAY</c> subfolder or the older top-level <c>DE_THROWAWAY.txt</c>.</summary>
        public static bool ThrowawayMarked(string worldPath)
        {
            if (string.IsNullOrEmpty(worldPath)) return false;
            try { return Directory.Exists(Path.Combine(worldPath, "DE_THROWAWAY")) || File.Exists(Path.Combine(worldPath, "DE_THROWAWAY.txt")); }
            catch (Exception) { return false; }
        }

        /// <summary>
 /// <c>TerrainTiles</c> right after the 34 tiles exist (game thread, BeforeStart): the store key of this world
 /// the RangeChanged subscriptions, the overlay
 /// restore, the drill converter's start (the manifest's hash against the restored overlay's). The world's existing
 /// overlay file is read first in every case (S8a off, drilling off, tiles refused): the save postfix carries its
 /// bytes unchanged whenever this session does not write its own.
 /// </summary>
        public static void OnTilesCreated()
        {
            string path = null;
            try { path = MySession.Static != null ? MySession.Static.CurrentPath : null; } catch (Exception) { }
            EnsureCarried(path);
            if (!Enabled) return;
            try { OnTilesCreatedCore(path); }
            catch (Exception e)
            {
                DeLog.Info("DE.S8a OVERLAY REFUSE BeforeStart " + e.GetType().Name + ": " + e.Message + " (drilling stays S4's: the guards block)");
                try { OnUnload(); } catch (Exception) { }
            }
        }

        private static string _carriedPath;

        /// <summary>The existing <c>DE_S8a_overlay.vx2</c> of the world folder, kept to be carried unchanged by saves this session does not write itself.</summary>
 /// <param name="fromSave">true from the save postfix: a Save As changes CurrentPath to a folder without the file yet, so the bytes read from the previous path are kept; false at world load: the new world's file only.</param>
        private static void EnsureCarried(string path, bool fromSave = false)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_carried != null && string.Equals(_carriedPath, path, StringComparison.OrdinalIgnoreCase)) return;
            byte[] fresh = null;
            try
            {
                string file = Path.Combine(path, S8aOverlayFile.FileName);
                if (File.Exists(file)) fresh = File.ReadAllBytes(file);
            }
            catch (Exception e) { DeLog.Info("DE.S8a overlay file not read for carrying: " + e.GetType().Name + ": " + e.Message); }
            if (fresh != null || !fromSave) _carried = fresh;
            _carriedPath = path;
        }

        private static void OnTilesCreatedCore(string path)
        {
            OnUnload();
            if (!DrillSetting)
            {
                DeLog.Info("DE.S8a drill OFF (" + EnvDrill + "): S4's guards block every edit of the tiles; no overlay, no store" + (_carried != null ? "; the world's overlay file is carried through saves" : ""));
                return;
            }
            IReadOnlyList<TerrainTiles.TileInfo> tiles = TerrainTiles.Tiles;
            if (tiles == null || tiles.Count == 0) { DeLog.Info("DE.S8a drill: no tiles (terrain refused): nothing to overlay"); return; }
            if (string.IsNullOrEmpty(path)) { DeLog.Info("DE.S8a OVERLAY REFUSE no session save folder (CurrentPath): no store key"); return; }
            _worldPath = path;
            _storeKey = S8aStore.StoreKey(path, SiteV1.SiteVersion);
            int n = tiles.Count;
            _storages = new MyStorageBase[n];
            _handlers = new Action<Vector3I, Vector3I, MyStorageDataTypeFlags>[n];
            _lastPath = new int[n];
            _lastPathMs = new long[n];
            _tileIndexByKey.Clear();
            for (int i = 0; i < n; i++)
            {
                TerrainTiles.TileInfo t = tiles[i];
                _tileIndexByKey[TileKey(t.I, t.J, t.K)] = i;
                _lastPath[i] = -1;
                var st = t.Map != null ? t.Map.Storage as MyStorageBase : null;
                if (st == null) continue;
                int ti = i;
                Action<Vector3I, Vector3I, MyStorageDataTypeFlags> h = (min, max, flags) => OnRangeChanged(ti, min, max, flags);
                st.RangeChanged += h;
                _storages[i] = st;
                _handlers[i] = h;
            }
            lock (_cells) _cells.Clear();
            _overlayGen = 0;
            _restoreRefused = false;
            _refuseLogged = false;
            _restoredCells = 0;
            _hooked = true;
            // the overlay file of this save (none: the hash of an empty overlay)
            _restoredHash = S8aOverlayFile.Hash(SiteV1.SiteVersion, new List<S8aOverlayCell>());
            Restore(tiles);
            _hash = _restoredHash;
            // S8a.M5 (the store under the SE world folder) only in a throwaway copy with autosave off, as S4.M10
            bool m5 = S8aMutants.M5;
            if (m5)
            {
                string refuse = ThrowawayMarked(path) ? TerrainLattice.ThrowawayRefusal("S8a.M5", TerrainTiles.AutosaveMinutes)
                    : "REFUSE S8a.M5: no DE_THROWAWAY marker in " + path + "; only on a throwaway world copy (tools/lab/make_terrain_copy.py); the store stays at its root";
                if (refuse != null) { DeLog.Info("DE.Mutant " + refuse); m5 = false; }
                else DeLog.Info("DE.Mutant S8a.M5 ACTIVE: the drill store is written under the SE world folder (throwaway copy, autosave off)");
            }
            string root = m5 ? Path.Combine(path, "DE_S8a_store") : S8aStore.DefaultRoot();
            _manifestMatched = S8aDrillConverter.Start(root, _storeKey, _restoredHash, path, m5);
            DeLog.Info("DE.S8a drill ON storeKey=" + S8aStore.Hex16(_storeKey) + " world=" + path + " tiles=" + n + " overlayCells=" + _restoredCells +
                       " overlayHash=" + S8aStore.Hex16(_restoredHash) + " manifestMatched=" + _manifestMatched + " store=" + root +
                       (StoreRefused ? " STORE REFUSED" : "") + (S8aMutants.Active.Length > 0 ? " MUTANTS=" + S8aMutants.Active : ""));
            Jsonl.WriteLine(S4Rows.Row("s8a.overlay", "tick", (long)GameState.GameplayTick, "cells", _restoredCells, "hash", S8aStore.Hex16(_restoredHash), "why", "restore",
                                       "refused", _restoreRefused, "manifestMatched", _manifestMatched, "storeKey", S8aStore.Hex16(_storeKey), "store", root));
        }

        /// <summary><c>TerrainTiles.OnUnloadData</c>: subscriptions dropped, the converter stopped (manifest flushed), the cells forgotten.</summary>
        public static void OnUnload()
        {
            if (_storages != null)
            {
                for (int i = 0; i < _storages.Length; i++)
                {
                    try { if (_storages[i] != null && _handlers[i] != null) _storages[i].RangeChanged -= _handlers[i]; } catch (Exception) { }
                }
            }
            bool was = _hooked;
            _hooked = false;
            _storages = null;
            _handlers = null;
            try { S8aDrillConverter.Stop(); } catch (Exception) { }
            lock (QLock) { _queue.Clear(); }
            lock (_cells) _cells.Clear();
            _tileIndexByKey.Clear();
            if (was) DeLog.Info("DE.S8a drill unload: overlay cells forgotten, converter stopped");
        }

        // ================================================================================================= RangeChanged (any thread)

        private static void OnRangeChanged(int tile, Vector3I min, Vector3I max, MyStorageDataTypeFlags flags)
        {
            var p = new Pending { Tile = tile, Min = min, Max = max, Mirror = _mirrorWrite, Restore = _restoreWrite, Flags = flags };
            lock (QLock)
            {
                if (_queue.Count >= MaxQueue) { _queueDropped++; return; }
                _queue.Add(p);
                _enqueued++;
            }
        }

        // ================================================================================================= Tick (game thread)

 /// <summary>Game thread, every update: the queued ranges become dirty cells, mirror jobs and conversion requests.</summary>
        public static void Tick(ulong nowMs)
        {
            if (!_hooked) return;
            List<Pending> d;
            lock (QLock)
            {
                if (_queue.Count == 0) return;
                d = _queue;
                _queue = _drain;
                _drain = d;
                _queue.Clear();
            }
            IReadOnlyList<TerrainTiles.TileInfo> tiles = TerrainTiles.Tiles;
            int newCells = 0;
            for (int q = 0; q < d.Count; q++)
            {
                Pending p = d[q];
                if (p.Tile < 0 || p.Tile >= tiles.Count) continue;
                TerrainTiles.TileInfo t = tiles[p.Tile];
                Vector3I min = Vector3I.Max(p.Min, Vector3I.Zero), max = Vector3I.Min(p.Max, new Vector3I(SiteV1.Tile - 1));
                if (max.X < min.X || max.Y < min.Y || max.Z < min.Z) continue;
                int c0x = min.X >> 4, c0y = min.Y >> 4, c0z = min.Z >> 4, c1x = max.X >> 4, c1y = max.Y >> 4, c1z = max.Z >> 4;
                int cells = (c1x - c0x + 1) * (c1y - c0y + 1) * (c1z - c0z + 1);
                int added = 0;
                bool overflow = false;
                lock (_cells)
                {
                    if (_cells.Count + cells <= OverlayC.MaxCells)
                    {
                        for (int cz = c0z; cz <= c1z; cz++)
                            for (int cy = c0y; cy <= c1y; cy++)
                                for (int cx = c0x; cx <= c1x; cx++)
                                    if (_cells.Add(S8aOverlayCell.KeyOf(t.I, t.J, t.K, cx, cy, cz))) added++;
                    }
                    else overflow = true;
                }
                if (overflow && !_capLogged)
                {
                    _capLogged = true;   // review low: the cap is said once
                    DeLog.Info("DE.S8a overlay cell cap " + OverlayC.MaxCells + " reached (" + Cells + " cells): this and later edits are converted but not persisted by the save (CONTRACT Overlay.MaxCells)");
                }
                newCells += added;
                string path = p.Restore ? "restore" : p.Mirror ? "mirror" : PathName(p.Tile, nowMs);
                EditRow(path, t, min, max, cells, p.Flags, nowMs);
                if (!p.Mirror && !p.Restore && !S8aMutants.M14) Mirror(tiles, p.Tile, min, max);
                if (p.Restore && _manifestMatched) continue;     // the store already holds this overlay's conversion
                if (!S8aDrillConverter.Running) continue;
                S8aDrillConverter.EnqueueEdit(p.Tile, min, max, p.Restore);
                _convertJobs++;
            }
            d.Clear();
            if (newCells > 0) _overlayGen++;
        }

        private static string PathName(int tile, ulong nowMs)
        {
            int p = _lastPath != null && tile < _lastPath.Length ? _lastPath[tile] : -1;
            if (p < 0 || p >= TerrainPaths.Names.Length) return "unknown";
            return (long)nowMs - _lastPathMs[tile] <= 3000 ? TerrainPaths.Names[p] : "unknown";
        }

        private static void EditRow(string path, TerrainTiles.TileInfo t, Vector3I min, Vector3I max, int cells, MyStorageDataTypeFlags flags, ulong nowMs)
        {
            int slot = TerrainPaths.IndexOf(path);
            if (slot < 0) slot = TerrainPaths.Names.Length;
            long now = (long)nowMs;
            if (now - _pathWindowStart[slot] >= 1000) { _pathWindowStart[slot] = now; _pathWindowCount[slot] = 0; }
            _pathWindowCount[slot]++;
            if (_pathWindowCount[slot] > EditRowsPerSecondPerPath) { _editSuppressed++; return; }
            _editRows++;
            Jsonl.WriteLine(S4Rows.Row("s8a.edit", "tick", (long)GameState.GameplayTick, "path", path, "tile", t.I + "," + t.J + "," + t.K,
                                       "min", new object[] { min.X, min.Y, min.Z }, "max", new object[] { max.X, max.Y, max.Z }, "cells", cells,
                                       "data", flags.ToString(), "count", _editRows, "suppressed", _editSuppressed, "overlayCells", Cells));
        }

        /// <summary>Every other tile whose box holds a part of the range gets the same content (the worker performs the WriteRange).</summary>
        private static void Mirror(IReadOnlyList<TerrainTiles.TileInfo> tiles, int src, Vector3I min, Vector3I max)
        {
            TerrainTiles.TileInfo a = tiles[src];
            for (int j = 0; j < tiles.Count; j++)
            {
                if (j == src) continue;
                TerrainTiles.TileInfo b = tiles[j];
                if (b.Map == null || b.Map.Closed) continue;
                // integer offset between the two voxel grids (the lattice pitch is a whole number of metres)
                Vec3 dv = a.PlaceMin - b.PlaceMin;
                var off = new Vector3I((int)Math.Round(dv.X), (int)Math.Round(dv.Y), (int)Math.Round(dv.Z));
                Vector3I bmin = min + off, bmax = max + off;
                if (bmax.X < 0 || bmax.Y < 0 || bmax.Z < 0 || bmin.X >= SiteV1.Tile || bmin.Y >= SiteV1.Tile || bmin.Z >= SiteV1.Tile) continue;
                Vector3I cmin = Vector3I.Max(bmin, Vector3I.Zero), cmax = Vector3I.Min(bmax, new Vector3I(SiteV1.Tile - 1));
                // the source range of the clipped part
                S8aDrillConverter.EnqueueMirror(src, cmin - off, cmax - off, j, cmin, cmax);
                _mirrorJobs++;
            }
        }

        /// <summary>The worker's mirror write: the same content and material into the other tile, under the mirror flag.</summary>
        internal static void MirrorWrite(MyStorageBase dst, MyStorageData data, Vector3I dstMin, Vector3I dstMax)
        {
            _mirrorWrite = true;
            try { dst.WriteRange(data, MyStorageDataTypeFlags.ContentAndMaterial, dstMin, dstMax, true, false); }
            finally { _mirrorWrite = false; }
        }

        // ================================================================================================= restore

        private static void Restore(IReadOnlyList<TerrainTiles.TileInfo> tiles)
        {
            string file = Path.Combine(_worldPath, S8aOverlayFile.FileName);
            if (!File.Exists(file)) return;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (Exception e) { Refuse("read " + e.GetType().Name + ": " + e.Message, null); return; }
            int siteVersion;
            List<S8aOverlayCell> cells;
            ulong hash;
            string err;
            if (!S8aOverlayFile.TryDecode(bytes, out siteVersion, out cells, out hash, out err)) { Refuse("decode: " + err, bytes); return; }
            if (siteVersion != SiteV1.SiteVersion) { Refuse("site version " + siteVersion + " (this build: " + SiteV1.SiteVersion + ")", bytes); return; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int written = 0, skipped = 0;
            _restoreWrite = true;
            try
            {
                foreach (S8aOverlayCell c in cells)
                {
                    int idx;
                    if (!_tileIndexByKey.TryGetValue(TileKey(c.I, c.J, c.K), out idx) || _storages[idx] == null) { skipped++; continue; }
                    var min = new Vector3I(c.Cx * OverlayC.CellVoxels, c.Cy * OverlayC.CellVoxels, c.Cz * OverlayC.CellVoxels);
                    if (min.X + OverlayC.CellVoxels > SiteV1.Tile || min.Y + OverlayC.CellVoxels > SiteV1.Tile || min.Z + OverlayC.CellVoxels > SiteV1.Tile) { skipped++; continue; }
                    var max = min + new Vector3I(OverlayC.CellVoxels - 1);
                    var data = new MyStorageData(new Vector3I(OverlayC.CellVoxels), c.Content, c.Material);
                    _storages[idx].WriteRange(data, MyStorageDataTypeFlags.ContentAndMaterial, min, max, true, false);
                    written++;
                }
            }
            finally { _restoreWrite = false; }
            _restoredHash = hash;
            _restoredCells = written;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            DeLog.Info("DE.S8a overlay restored: " + written + " cells (" + skipped + " skipped) hash=" + S8aStore.Hex16(hash) + " in " +
                       ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms from " + file);
        }

        private static void Refuse(string reason, byte[] carried)
        {
            _restoreRefused = true;
            _carried = carried;
            if (_refuseLogged) return;
            _refuseLogged = true;
            DeLog.Info("DE.S8a OVERLAY REFUSE " + reason + " (the tiles stay the provider's; the next saves carry the file's bytes unchanged; this session's edits are not persisted)");
            Jsonl.WriteLine(S4Rows.Row("s8a.overlay", "tick", (long)GameState.GameplayTick, "cells", 0, "hash", null, "why", "refuse", "reason", reason));
        }

        // ================================================================================================= save

        internal static void InstallSavePostfix(Harmony h)
        {
            const string name = "MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>)";
            MethodInfo save = AccessTools.Method(typeof(MySession), "Save",
                new[] { typeof(MySessionSnapshot).MakeByRefType(), typeof(string), typeof(Action<SaveProgress>) });
            if (save == null || save.IsStatic || save.ReturnType != typeof(bool))
            {
                DeLog.Info("DE.S8a MISSING " + name + (save == null ? " not found" : " has another shape") + " (the overlay is not saved; drilling still works in this session)");
                return;
            }
            try
            {
                h.Patch(save, postfix: new HarmonyMethod(typeof(S8aOverlay).GetMethod(nameof(SavePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                SavePostfixInstalled = true;
                DeLog.Info("DE.S8a PATCH overlay.persist ok " + name + " postfix (snapshot.CompressedVoxelSnapshots[" + S8aOverlayFile.SnapshotName + "] while S8a is on)");
            }
            catch (Exception e)
            {
                DeLog.Info("DE.S8a MISSING " + name + " patch failed " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
 /// Positional names: __0 = the out snapshot; __result = Save's answer (false: the fail-safe's prefix blocked it, or the
 /// snapshot failed). With the overlay hooked the dirty cells are read back and stored; otherwise (S8a off, drilling
 /// off, tiles refused, the restore refused) the world's existing file is carried unchanged, so SE's cleanup never
 /// drops saved holes.
 /// </summary>
        private static void SavePostfix(ref MySessionSnapshot __0, bool __result)
        {
            if (!__result || __0 == null) return;
            try
            {
                bool own = Enabled && _hooked && !_restoreRefused;
                if (!own)
                {
                    string path = null;
                    try { path = MySession.Static != null ? MySession.Static.CurrentPath : null; } catch (Exception) { }
                    EnsureCarried(path, true);
                    if (_carried == null) return;
                }
                AddToSnapshot(__0, own);
            }
            catch (Exception e) { DeLog.Info("DE.S8a overlay save failed " + e.GetType().Name + ": " + e.Message + " (the save goes on without the overlay)"); }
        }

        private static void AddToSnapshot(MySessionSnapshot snapshot, bool own)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            byte[] bytes;
            ulong hash;
            int count;
            string why;
            if (!own)
            {
                bytes = _carried;
                hash = _hash;
                count = -1;
                why = "carried";
            }
            else
            {
                List<S8aOverlayCell> cells = ReadCells();
                bytes = S8aOverlayFile.EncodeFast(SiteV1.SiteVersion, cells, out hash);   // the game thread: the fastest deflate
                count = cells.Count;
                why = "save";
            }
            if (!S8aMutants.M3 || !own)
            {
                if (snapshot.CompressedVoxelSnapshots == null) snapshot.CompressedVoxelSnapshots = new Dictionary<string, byte[]>();
                snapshot.CompressedVoxelSnapshots[S8aOverlayFile.SnapshotName] = bytes;
            }
            if (why == "save")
            {
                _hash = hash;
                _carried = bytes;      // the file this save writes: carried by a later save that does not write its own
                _carriedPath = snapshot.TargetDir ?? _carriedPath;
                S8aDrillConverter.SetOverlayHash(hash);
            }
            _saves++;
            _lastSaveMs = (long)Win32.TickCount64();
            _lastSaveCells = count;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            DeLog.Info("DE.S8a overlay " + why + ": " + count + " cells hash=" + S8aStore.Hex16(hash) + " bytes=" + bytes.Length + " in " + ms.ToString("0.0", CultureInfo.InvariantCulture) +
                       " ms -> " + snapshot.TargetDir + "\\" + S8aOverlayFile.FileName + (S8aMutants.M3 ? " (S8a.M3 ON: NOT added to the snapshot)" : ""));
            Jsonl.WriteLine(S4Rows.Row("s8a.overlay", "tick", (long)GameState.GameplayTick, "cells", count, "hash", S8aStore.Hex16(hash), "why", why, "bytes", bytes.Length,
                                       "ms", ms, "target", snapshot.TargetDir, "m3", S8aMutants.M3));
        }

        /// <summary>The dirty cells read back from the tiles (game thread; LOD 0, content and material).</summary>
        private static List<S8aOverlayCell> ReadCells()
        {
            ulong[] keys;
            lock (_cells) { keys = new ulong[_cells.Count]; _cells.CopyTo(keys); }
            Array.Sort(keys);
            var list = new List<S8aOverlayCell>(keys.Length);
            var data = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
            data.Resize(new Vector3I(OverlayC.CellVoxels));
            foreach (ulong key in keys)
            {
                int i = (int)(key >> 56) & 0xFF, j = (int)(key >> 48) & 0xFF, k = (int)(key >> 40) & 0xFF;
                int cx = (int)(key >> 24) & 0xFF, cy = (int)(key >> 16) & 0xFF, cz = (int)(key >> 8) & 0xFF;
                int idx;
                if (!_tileIndexByKey.TryGetValue(TileKey(i, j, k), out idx) || _storages == null || _storages[idx] == null) continue;
                var min = new Vector3I(cx * OverlayC.CellVoxels, cy * OverlayC.CellVoxels, cz * OverlayC.CellVoxels);
                var max = min + new Vector3I(OverlayC.CellVoxels - 1);
                _storages[idx].ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, 0, min, max);
                var c = new S8aOverlayCell { I = (byte)i, J = (byte)j, K = (byte)k, Cx = (byte)cx, Cy = (byte)cy, Cz = (byte)cz };
                Buffer.BlockCopy(data[MyStorageDataTypeEnum.Content], 0, c.Content, 0, S8aOverlayCell.Voxels);
                Buffer.BlockCopy(data[MyStorageDataTypeEnum.Material], 0, c.Material, 0, S8aOverlayCell.Voxels);
                list.Add(c);
            }
            return list;
        }

        /// <summary><c>de.s8a overlay {cells: true}</c>: the current cells read back and hashed (game thread; no file).</summary>
        public static ulong HashNow(out int count)
        {
            List<S8aOverlayCell> cells = ReadCells();
            count = cells.Count;
            return S8aOverlayFile.Hash(SiteV1.SiteVersion, cells);
        }

        public static List<string> CellKeys()
        {
            ulong[] keys;
            lock (_cells) { keys = new ulong[_cells.Count]; _cells.CopyTo(keys); }
            Array.Sort(keys);
            var l = new List<string>(keys.Length);
            foreach (ulong key in keys)
                l.Add(((key >> 56) & 0xFF) + "," + ((key >> 48) & 0xFF) + "," + ((key >> 40) & 0xFF) + ":" + ((key >> 24) & 0xFF) + "," + ((key >> 16) & 0xFF) + "," + ((key >> 8) & 0xFF));
            return l;
        }

        public static Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "enabled", Enabled }, { "drillSetting", DrillSetting }, { "drillOn", DrillOn }, { "drillLayerOn", DrillLayerOn }, { "storeRefused", StoreRefused },
                { "hooked", _hooked }, { "cells", Cells }, { "overlayGen", (long)_overlayGen }, { "hash", S8aStore.Hex16(_hash) }, { "storeKey", S8aStore.Hex16(_storeKey) },
                { "world", _worldPath }, { "restoredCells", _restoredCells }, { "restoreRefused", _restoreRefused }, { "manifestMatched", _manifestMatched },
                { "queue", _enqueued }, { "queueDropped", _queueDropped }, { "editRows", _editRows }, { "editSuppressed", _editSuppressed },
                { "mirrorJobs", _mirrorJobs }, { "convertJobs", _convertJobs }, { "saves", _saves }, { "lastSaveCells", _lastSaveCells },
                { "savePostfix", SavePostfixInstalled }, { "carriedBytes", _carried != null ? _carried.Length : 0 }, { "mutants", S8aMutants.Active },
            };
        }
    }
}
