using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;
using SpawnC = DeepEngineers.Wire.S8aConventions.Spawn;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S8a-2 spawns (ROADMAP B4 "S8aSpawns: the spawn table from S8aBiomes, transient prefabs, caps, DespawnM, never in the
 /// host save"; CONTRACT "spawn.place", "spawn.table"). Window columns only: per batch column (bx, bz) near the camera
 /// the seabed of the served window files is scanned on a worker (<see cref="S8aSeabedColumns"/>: the top solid voxel
 /// per 1 m column, its type and SN y); every 1 m column draws each row of its depth band with the key
 /// fnv1a64(chartKey, bx, bz, column, row) (<see cref="S8aSpawnTable.Spawns"/>: p = perHectare / 10,000, S8a.M11 × 10);
 /// a drawn point within <c>RadiusM</c> of the camera is instantiated from the game's own prefab
 /// (<c>CraftData.GetPrefabForTechTypeAsync(TechType, bool)</c> → <c>CoroutineTask&lt;GameObject&gt;</c>, VERIFIED, as S7's
 /// EffectsLibrary) and made transient (<c>LargeWorldStreamer.MakeEntityTransient(GameObject)</c>, VERIFIED: never saved);
 /// gone beyond <c>DespawnM</c>; moved by −T on a chart step. Caps: ≤ <see cref="MaxPerFrame"/> instantiations per frame,
 /// ≤ <see cref="MaxLive"/> live objects. <c>de.s8a spawns {on, clear}</c>. Rows: <c>s8a.spawn</c> {snFrame, techType,
 /// band, count, despawned} (1 Hz, per tech type with activity).
 /// </summary>
    internal sealed class S8aSpawns : IDisposable
    {
        public const int MaxPerFrame = 4, MaxLive = 240, MaxPointsPerColumn = 400;
        public const double RescanM = 80.0;

        private sealed class Column
        {
            public int Bx, Bz;
            public ulong ChartKey;
            public S8aSeabedColumns Seabed;
            public readonly List<S8aSpawnPoint> Points = new List<S8aSpawnPoint>();
            public volatile bool Ready, Failed;
            public string Error;
            public bool Logged;
        }

        private sealed class Live
        {
            public ulong Key;
            public GameObject Go;
            public string TechType;
            public int Band;
            public Vector3 Seabed;
        }

        private readonly Dictionary<Int3Key, Column> _columns = new Dictionary<Int3Key, Column>();
        private readonly Dictionary<ulong, Live> _live = new Dictionary<ulong, Live>();
        private readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly HashSet<string> _loading = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _missing = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long[]> _rowStats = new Dictionary<string, long[]>(StringComparer.Ordinal);
        private MonoBehaviour _host;
        private Func<int, int, int, string> _servedPath;
        // S17 hook: a fish S17 caught leaves a tombstone on its spawn point; the point respawns only once it is
 // beyond DespawnM (else Update would see Go == null, despawn the key and TrySpawn a new fish there within a frame)
        private static S8aSpawns _current;
        private readonly Dictionary<ulong, Vector3> _tombs = new Dictionary<ulong, Vector3>();
        public long Tombs;
        private ulong _chartKey;
        private ulong _nextRowMs;
        public bool On = true;
        public long Spawned, Despawned, Scans, ScanFailures, PrefabFailures;
        public volatile bool M11;

        public void Attach(MonoBehaviour host, Func<int, int, int, string> servedPath)
        {
            _host = host;
            _servedPath = servedPath;
            _current = this;   // S17 hook
        }

        /// <summary>S17 hook: <paramref name="go"/> is about to be destroyed by a catch; true when it was one of these spawns (its point gets a tombstone).</summary>
        public static bool OnCaught(GameObject go)
        {
            S8aSpawns s = _current;
            if (s == null || go == null) return false;
            foreach (Live l in s._live.Values)
                if (l.Go == go) { s._tombs[l.Key] = l.Seabed; s.Tombs++; return true; }
            return false;
        }

        public int LiveCount { get { return _live.Count; } }
        public int ColumnsScanned { get { return _columns.Count; } }

        /// <summary>A chart step moved the camera's SN position by −T: the live objects follow, the columns rescan under the new key.</summary>
        public void OnChartStep(ulong chartKey, double txM, double tzM)
        {
            _chartKey = chartKey;
            var d = new Vector3((float)-txM, 0f, (float)-tzM);
            foreach (Live l in _live.Values)
            {
                try { if (l.Go != null) l.Go.transform.position += d; } catch (Exception) { }
                l.Seabed += d;
            }
            if (_tombs.Count > 0) foreach (ulong k in new List<ulong>(_tombs.Keys)) _tombs[k] += d;   // S17 hook
            _columns.Clear();
        }

        /// <summary>Per frame (main thread, ≤ 0.5 ms): the columns around the camera, their scans, the spawns within RadiusM, the despawns beyond DespawnM.</summary>
        public void Update(ulong nowMs, Vector3 cam, bool windowOn, ulong chartKey)
        {
            if (!On || !windowOn || _servedPath == null || _host == null) { if (_live.Count > 0 && !On) Clear(); return; }
            if (chartKey != _chartKey) { _chartKey = chartKey; _columns.Clear(); }
            int cbx = S8aBiomes.BatchX(cam.x), cbz = S8aBiomes.BatchZ(cam.z);
            int spawnedNow = 0;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    int bx = cbx + dx, bz = cbz + dz;
                    if (!S8aBiomes.IsWindowColumn(bx, bz)) continue;
                    var key = new Int3Key(bx, 0, bz);
                    Column col;
                    if (!_columns.TryGetValue(key, out col)) { col = StartScan(bx, bz, chartKey); _columns[key] = col; }
                    if (!col.Ready) continue;
                    if (!col.Logged) { col.Logged = true; ScanRow(col); }
                    foreach (S8aSpawnPoint p in col.Points)
                    {
                        if (spawnedNow >= MaxPerFrame || _live.Count >= MaxLive) break;
                        if (_live.ContainsKey(p.Key) || _tombs.ContainsKey(p.Key)) continue;   // S17 hook: a caught fish's point stays empty
                        float dxm = (float)p.X - cam.x, dzm = (float)p.Z - cam.z, dym = (float)p.SeabedY - cam.y;
                        if (dxm * dxm + dzm * dzm + dym * dym > SpawnC.RadiusM * SpawnC.RadiusM) continue;
                        if (TrySpawn(p)) spawnedNow++;
                    }
                }
            // despawn beyond DespawnM (distance from the seabed point)
            List<ulong> gone = null;
            foreach (var kv in _live)
            {
                Vector3 s = kv.Value.Seabed;
                float dx2 = s.x - cam.x, dy2 = s.y - cam.y, dz2 = s.z - cam.z;
                if (dx2 * dx2 + dy2 * dy2 + dz2 * dz2 > SpawnC.DespawnM * SpawnC.DespawnM || kv.Value.Go == null)
                {
                    if (gone == null) gone = new List<ulong>();
                    gone.Add(kv.Key);
                }
            }
            if (gone != null) foreach (ulong k in gone) Despawn(k);
            if (_tombs.Count > 0)   // S17 hook: a tombstone lifts once its point is beyond DespawnM
            {
                List<ulong> lifted = null;
                foreach (var kv in _tombs)
                {
                    Vector3 s = kv.Value;
                    float dx3 = s.x - cam.x, dy3 = s.y - cam.y, dz3 = s.z - cam.z;
                    if (dx3 * dx3 + dy3 * dy3 + dz3 * dz3 > SpawnC.DespawnM * SpawnC.DespawnM) { if (lifted == null) lifted = new List<ulong>(); lifted.Add(kv.Key); }
                }
                if (lifted != null) foreach (ulong k in lifted) _tombs.Remove(k);
            }
            // columns far from the camera are forgotten (their points come back from the files when revisited)
            if (_columns.Count > 12)
            {
                var far = new List<Int3Key>();
                foreach (var kv in _columns) if (Math.Abs(kv.Key.X - cbx) > 2 || Math.Abs(kv.Key.Z - cbz) > 2) far.Add(kv.Key);
                foreach (Int3Key k in far) _columns.Remove(k);
            }
            if (nowMs >= _nextRowMs) { _nextRowMs = nowMs + 1000; Rows(); }
        }

        /// <summary>
 /// Review MEDIUM 8 (S8a.N10.spawn): the deterministic draw of one scanned batch column, independent of the live caps and
 /// the spawn radius: <c>s8a.scan</c> {snFrame, bx, bz, chartKey, columns (1 m^2 each with a seabed), c0..c3 (per depth
 /// band), p0..p3 (points drawn per band), failed, m11}. Density per band = p / (c / 10,000) per hectare.
 /// </summary>
        private void ScanRow(Column col)
        {
            var cb = new long[4];
            var pb = new long[4];
            long columns = 0;
            if (col.Seabed != null)
            {
                for (int ci = 0; ci < col.Seabed.TopY.Length; ci++)
                {
                    float top = col.Seabed.TopY[ci];
                    if (float.IsNaN(top)) continue;
                    columns++;
                    int b = S8aSpawnTable.BandOf(-top);
                    if (b >= 0 && b < 4) cb[b]++;
                }
            }
            foreach (S8aSpawnPoint p in col.Points) if (p.Band >= 0 && p.Band < 4) pb[p.Band]++;
            S8aTerrain.Row(new JsonOut().Str("t", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Num("tickMs", Win32.TickCount64()).Num("frame", DeLog.Frame)
                .Str("level", "info").Str("event", "s8a.scan").Str("ev", "s8a.scan").Obj("data").Num("snFrame", DeLog.Frame).Num("bx", (long)col.Bx).Num("bz", (long)col.Bz)
                .Str("chartKey", S8aSnPaths.Hex(col.ChartKey)).Num("columns", columns).Num("c0", cb[0]).Num("c1", cb[1]).Num("c2", cb[2]).Num("c3", cb[3])
                .Num("p0", pb[0]).Num("p1", pb[1]).Num("p2", pb[2]).Num("p3", pb[3]).Num("points", (long)col.Points.Count).Bool("failed", col.Failed).Str("error", col.Error).Bool("m11", M11).EndObj().ToString());
        }

        private Column StartScan(int bx, int bz, ulong chartKey)
        {
            var col = new Column { Bx = bx, Bz = bz, ChartKey = chartKey };
            Func<int, int, int, string> served = _servedPath;
            bool m11 = M11;
            Scans++;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    S8aSeabedColumns sc = S8aSeabedColumns.Scan(bx, bz, by =>
                    {
                        string p = served(bx, by, bz);
                        if (p == null) return null;
                        try { return File.Exists(p) ? File.ReadAllBytes(p) : null; } catch (Exception) { return null; }
                    });
                    col.Seabed = sc;
                    double scale = m11 ? 10.0 : 1.0;
                    for (int lz = 0; lz < S8aSeabedColumns.N && col.Points.Count < MaxPointsPerColumn; lz++)
                        for (int lx = 0; lx < S8aSeabedColumns.N && col.Points.Count < MaxPointsPerColumn; lx++)
                        {
                            int ci = S8aSeabedColumns.ColumnIndex(lx, lz);
                            float top = sc.TopY[ci];
                            if (float.IsNaN(top)) continue;
                            int band = S8aSpawnTable.BandOf(-top);
                            if (band < 0) continue;
                            foreach (S8aSpawnRow row in S8aSpawnTable.RowsOfBand(band))
                            {
                                ulong key = S8aSpawnTable.Key(chartKey, bx, bz, ci, row.Index);
                                if (!S8aSpawnTable.Spawns(key, row.PerHectare, scale)) continue;
                                double jx, jz, yaw, hover;
                                S8aSpawnTable.Placement(key, out jx, out jz, out yaw, out hover);
                                double x = S8aConventions.SnWorld.OriginX + bx * 160 + lx + jx, z = S8aConventions.SnWorld.OriginZ + bz * 160 + lz + jz;
                                col.Points.Add(new S8aSpawnPoint { Key = key, Row = row.Index, Column = ci, Band = band, Bx = bx, Bz = bz, X = x, Z = z, SeabedY = top,
                                                                   Y = row.Flora ? top - S8aSpawnTable.FloraSinkM : top + hover, YawDeg = yaw });
                            }
                        }
                }
                catch (Exception e) { col.Failed = true; col.Error = e.GetType().Name + ": " + e.Message; ScanFailures++; }
                col.Ready = true;
            });
            return col;
        }

        private bool TrySpawn(S8aSpawnPoint p)
        {
            S8aSpawnRow row = S8aSpawnTable.Rows[p.Row];
            GameObject prefab;
            if (!_prefabs.TryGetValue(row.TechType, out prefab))
            {
                if (!_missing.Contains(row.TechType) && _loading.Add(row.TechType)) { try { _host.StartCoroutine(LoadPrefab(row.TechType)); } catch (Exception) { _loading.Remove(row.TechType); _missing.Add(row.TechType); } }
                return false;
            }
            try
            {
                var pos = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
                GameObject go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, (float)p.YawDeg, 0f));
                go.SetActive(true);
                try { LargeWorldStreamer lws = LargeWorldStreamer.main; if (lws != null) lws.MakeEntityTransient(go); } catch (Exception) { }
                _live[p.Key] = new Live { Key = p.Key, Go = go, TechType = row.TechType, Band = p.Band, Seabed = new Vector3((float)p.X, (float)p.SeabedY, (float)p.Z) };
                Spawned++;
                Stat(row.TechType, p.Band)[0]++;
                return true;
            }
            catch (Exception e)
            {
                PrefabFailures++;
                DeLog.WarnOnce("s8a.spawn." + row.TechType, "DE.S8a spawn " + row.TechType + " failed: " + e.GetType().Name + ": " + e.Message);
                _missing.Add(row.TechType);
                return false;
            }
        }

        private IEnumerator LoadPrefab(string techType)
        {
            TechType tt;
            try { tt = (TechType)Enum.Parse(typeof(TechType), techType, true); }
            catch (Exception) { _missing.Add(techType); _loading.Remove(techType); DeLog.Warn("DE.S8a spawn: unknown TechType " + techType); yield break; }
            CoroutineTask<GameObject> task = CraftData.GetPrefabForTechTypeAsync(tt, false);
            yield return task;
            GameObject prefab = null;
            try { prefab = task.GetResult(); } catch (Exception) { }
            _loading.Remove(techType);
            if (prefab == null) { _missing.Add(techType); DeLog.Warn("DE.S8a spawn: no prefab for " + techType); yield break; }
            _prefabs[techType] = prefab;
        }

        private void Despawn(ulong key)
        {
            Live l;
            if (!_live.TryGetValue(key, out l)) return;
            _live.Remove(key);
            try { if (l.Go != null) UnityEngine.Object.Destroy(l.Go); } catch (Exception) { }
            Despawned++;
            Stat(l.TechType, l.Band)[1]++;
        }

        private long[] Stat(string techType, int band)
        {
            long[] s;
            string k = techType + "|" + band;
            if (!_rowStats.TryGetValue(k, out s)) { s = new long[2]; _rowStats[k] = s; }
            return s;
        }

        private void Rows()
        {
            foreach (var kv in _rowStats)
            {
                if (kv.Value[0] == 0 && kv.Value[1] == 0) continue;
                string[] p = kv.Key.Split('|');
                S8aTerrain.Row(new JsonOut().Str("t", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Num("tickMs", Win32.TickCount64()).Num("frame", DeLog.Frame)
                    .Str("level", "info").Str("event", "s8a.spawn").Str("ev", "s8a.spawn").Obj("data").Num("snFrame", DeLog.Frame).Str("techType", p[0]).Num("band", long.Parse(p[1]))
                    .Num("count", kv.Value[0]).Num("despawned", kv.Value[1]).Num("live", (long)_live.Count).Bool("m11", M11).EndObj().ToString());
                kv.Value[0] = 0;
                kv.Value[1] = 0;
            }
        }

        public void Clear()
        {
            var keys = new List<ulong>(_live.Keys);
            foreach (ulong k in keys) Despawn(k);
            _columns.Clear();
            _tombs.Clear();   // S17 hook
        }

        public Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "on", On }, { "live", _live.Count }, { "columns", _columns.Count }, { "spawned", Spawned }, { "despawned", Despawned }, { "scans", Scans },
                { "scanFailures", ScanFailures }, { "prefabs", _prefabs.Count }, { "missingPrefabs", _missing.Count }, { "prefabFailures", PrefabFailures }, { "m11", M11 },
            };
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
