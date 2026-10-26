using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S8a planet v2, route B's first slice (docs/S8A-PLANET-V2.md section 12): the sample chunk's entities, spawned in Subnautica from
 /// the records SE places (Wire S8aSampleEntities, <c>sample-entities.tsv</c> in the store folder, SN coordinates of the chart its
 /// header names). A baked record is the real map's own prefab by class id
 /// at the record's pose and scale; a slot record is filled by Subnautica's own loot table, as
 /// EntitySlotsPlaceholder.Spawn does: <c>LargeWorld.main.streamer.cellManager.GetPrefabForSlot(IEntitySlot)</c>
 /// with an <c>EntitySlotData</c> {biomeType, allowedTypes, density}, then
 /// <c>UWE.WorldEntityDatabase.TryGetInfo(string, out WorldEntityInfo)</c> for its scale and z-up flag and
 /// <c>SpawnRestrictionEnforcer.ShouldSpawn(string)</c>; count instances, the extra ones offset by a
 /// key hash (Subnautica: insideUnitSphere x 4 m). A slot is rolled once per session (the same fill after a despawn). Every object is
 /// transient, kept within its cell level's
 /// radius (S8aSampleEntities.RadiusOf), moved when its record moves (a chart step), gone beyond the radius +
 /// <see cref="S8aSampleEntities.DespawnExtraM"/>. Not marked as this mod's own: they are Subnautica's plants and creatures (S8a.NL.look
 /// counts them as life; S8aFlora hides them over a carve like any other).
 /// </summary>
    internal sealed class S8aSampleSpawns : IDisposable
    {
        public const int MaxSpawnsPerFrame = 12;
        public const ulong PollMs = 500;

        private sealed class Live
        {
            public ulong Key;
            public readonly List<GameObject> Gos = new List<GameObject>(1);
            public string ClassId, Family;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private sealed class SlotFill { public string ClassId; public int Count; public Vector3 Scale = Vector3.one; public bool ZUp; }

        private readonly Dictionary<ulong, Live> _live = new Dictionary<ulong, Live>();
        private readonly Dictionary<ulong, SlotFill> _fills = new Dictionary<ulong, SlotFill>();
        private readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly HashSet<string> _loading = new HashSet<string>(StringComparer.Ordinal), _missing = new HashSet<string>(StringComparer.Ordinal);
        private List<S8aSampleRecord> _records = new List<S8aSampleRecord>();
        private HashSet<ulong> _recordKeys = new HashSet<ulong>();
        private ulong _fileChartKey, _sampleId;
        private double[] _camNative = new double[3];
        private long _fileSeq, _fileLen = -1;
        private DateTime _fileTime;
        private ulong _nextPollMs, _nextRowMs;
        private MonoBehaviour _host;
        public bool On = true;
        public long Spawned, Despawned, Moved, Polls, ParseFailures, PrefabFailures, SlotsRolled, SlotsEmpty, SlotsRestricted, Restricted, NoLwe;

        public void Attach(MonoBehaviour host) { _host = host; }

        public int LiveCount { get { return _live.Count; } }

        /// <summary>Per frame (main thread): poll the file, keep the records' objects within reach, one row a second.</summary>
        public void Update(ulong nowMs, Vector3 cam, ulong storeKey, ulong chartKey)
        {
            if (!On || storeKey == 0) { if (!On && _live.Count > 0) Clear(); return; }
            if (nowMs >= _nextPollMs)
            {
                _nextPollMs = nowMs + PollMs;
                Poll(storeKey);
            }
            bool chartOk = _fileChartKey == chartKey && chartKey != 0;
            var want = new Dictionary<ulong, S8aSampleRecord>();
            if (chartOk)
                foreach (S8aSampleRecord r in _records)
                    if (Dist(r, cam) <= S8aSampleEntities.RadiusOf(r.Level)) want[r.Key] = r;
            var gone = new List<ulong>();
            foreach (var kv in _live)
            {
                S8aSampleRecord r;
                bool has = want.TryGetValue(kv.Key, out r);
                bool far = Vector3.Distance(kv.Value.Pos, cam) > S8aSampleEntities.RadiusOf(has ? r.Level : 0) + S8aSampleEntities.DespawnExtraM;
                if (!chartOk || far || (!has && !_recordKeys.Contains(kv.Key))) gone.Add(kv.Key);
            }
            foreach (ulong k in gone) Despawn(k);
            int spawned = 0;
            foreach (var kv in want)
            {
                Live l;
                if (_live.TryGetValue(kv.Key, out l))
                {
                    var p = new Vector3((float)kv.Value.X, (float)kv.Value.Y, (float)kv.Value.Z);
                    if ((p - l.Pos).sqrMagnitude > 0.25f) Move(l, kv.Value);
                    continue;
                }
                if (spawned >= MaxSpawnsPerFrame) continue;
                if (TrySpawn(kv.Value)) spawned++;
            }
            if (nowMs >= _nextRowMs)
            {
                _nextRowMs = nowMs + 1000;
                Row(cam, chartOk);
            }
        }

        private static double Dist(S8aSampleRecord r, Vector3 cam)
        {
            double dx = r.X - cam.x, dy = r.Y - cam.y, dz = r.Z - cam.z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private void Poll(ulong storeKey)
        {
            Polls++;
            string path = S8aSampleEntities.PathOf(S8aSnPaths.Root(), storeKey);
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { if (_records.Count > 0) { _records = new List<S8aSampleRecord>(); _recordKeys = new HashSet<ulong>(); _fileLen = -1; } return; }
                if (fi.Length == _fileLen && fi.LastWriteTimeUtc == _fileTime) return;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                long seq;
                uint cs;
                ulong ck, sid;
                double[] cn;
                List<S8aSampleRecord> recs;
                if (!S8aSampleEntities.TryParse(text, out seq, out cs, out ck, out sid, out cn, out recs)) { ParseFailures++; return; }
                _records = recs;
                var keys = new HashSet<ulong>();
                foreach (S8aSampleRecord r in recs) keys.Add(r.Key);
                _recordKeys = keys;
                _fileSeq = seq;
                _fileChartKey = ck;
                _sampleId = sid;
                _camNative = cn;
                _fileLen = fi.Length;
                _fileTime = fi.LastWriteTimeUtc;
            }
            catch (Exception) { ParseFailures++; }
        }

        private static Quaternion Q(S8aSampleRecord r) { return new Quaternion((float)r.Qx, (float)r.Qy, (float)r.Qz, (float)r.Qw); }

        private bool Prefab(string classId, out GameObject prefab)
        {
            prefab = null;
            if (string.IsNullOrEmpty(classId) || _missing.Contains(classId)) return false;
            if (_prefabs.TryGetValue(classId, out prefab)) return prefab != null;
            if (_host != null && _loading.Add(classId))
            {
                try { _host.StartCoroutine(LoadPrefab(classId)); }
                catch (Exception) { _loading.Remove(classId); _missing.Add(classId); }
            }
            return false;
        }

        private bool TrySpawn(S8aSampleRecord r)
        {
            string cid = r.ClassId;
            int count = 1;
            Vector3 scale = new Vector3((float)r.Sx, (float)r.Sy, (float)r.Sz);
            bool zUp = false;
            if (r.Slot)
            {
                SlotFill fill;
                if (!_fills.TryGetValue(r.Key, out fill))
                {
                    fill = Roll(r);
                    if (fill == null) return false;
                    _fills[r.Key] = fill;
                }
                if (string.IsNullOrEmpty(fill.ClassId) || fill.Count <= 0)
                {
                    // an empty roll: a live entry without objects, so the slot is judged as filled-empty, not retried
                    _live[r.Key] = new Live { Key = r.Key, ClassId = null, Family = "slot", Pos = new Vector3((float)r.X, (float)r.Y, (float)r.Z), Rot = Q(r) };
                    return false;
                }
                cid = fill.ClassId;
                count = fill.Count;
                scale = fill.Scale;
                zUp = fill.ZUp;
            }
            else
            {
                try { if (!SpawnRestrictionEnforcer.ShouldSpawn(cid)) { Restricted++; _missing.Add(cid); return false; } } catch (Exception) { }
            }
            GameObject prefab;
            if (!Prefab(cid, out prefab)) return false;
            var l = new Live { Key = r.Key, ClassId = cid, Family = r.Family, Pos = new Vector3((float)r.X, (float)r.Y, (float)r.Z), Rot = Q(r) };
            try
            {
                for (int j = 0; j < count; j++)
                {
                    Vector3 p = l.Pos;
                    if (j > 0)
                    {
                        double dx, dy, dz;
                        S8aSampleEntities.SlotJitter(r.Key, j, out dx, out dy, out dz);
                        p += new Vector3((float)dx, (float)dy, (float)dz);
                    }
                    Quaternion q = zUp ? l.Rot * Quaternion.Euler(-90f, 0f, 0f) : l.Rot;
                    GameObject go = UWE.Utils.InstantiateDeactivated(prefab, p, q);
                    if (go == null) { PrefabFailures++; continue; }
                    go.transform.localScale = scale;
                    // not "DE.S8a...": S8aFlora.Life counts such names as this mod's own (ns 20261024-113052: life 0 at every spot)
                    go.name = prefab.name + " [sample]";
                    go.SetActive(true);
                    try { LargeWorldStreamer lws = LargeWorldStreamer.main; if (lws != null) lws.MakeEntityTransient(go); } catch (Exception) { }
                    // ns 20261024-124136: life 0 at every spot with 450 of our plants live: the registry S8aFlora.Life reads from is fed
 // here too (the dev path), not only by LargeWorldEntity.Start
                    try { LargeWorldEntity lwe = go.GetComponent<LargeWorldEntity>(); if (lwe != null) S8aFlora.Register(lwe, "dev"); else NoLwe++; } catch (Exception) { }
                    l.Gos.Add(go);
                }
            }
            catch (Exception e)
            {
                PrefabFailures++;
                DeLog.WarnOnce("s8a.sample." + cid, "DE.S8a sample entity " + cid + " failed: " + e.GetType().Name + ": " + e.Message);
                _missing.Add(cid);
            }
            _live[r.Key] = l;
            Spawned++;
            return true;
        }

        /// <summary>Subnautica's own slot fill (its loot table by biome, allowed types and density), once per slot and session.</summary>
        private SlotFill Roll(S8aSampleRecord r)
        {
            try
            {
                LargeWorld lw = LargeWorld.main;
                if (lw == null || lw.streamer == null || lw.streamer.cellManager == null) return null;
                var data = new EntitySlotData { biomeType = (BiomeType)r.SlotBiome, allowedTypes = (EntitySlotData.EntitySlotType)r.SlotAllowed, density = (float)r.SlotDensity };
                EntitySlot.Filler f = lw.streamer.cellManager.GetPrefabForSlot(data);
                SlotsRolled++;
                var fill = new SlotFill { ClassId = f.classId, Count = f.count };
                if (string.IsNullOrEmpty(f.classId)) { SlotsEmpty++; return fill; }
                if (!SpawnRestrictionEnforcer.ShouldSpawn(f.classId)) { SlotsRestricted++; fill.ClassId = null; return fill; }
                UWE.WorldEntityInfo info;
                if (UWE.WorldEntityDatabase.TryGetInfo(f.classId, out info) && info != null)
                {
                    fill.Scale = info.localScale;
                    fill.ZUp = info.prefabZUp;
                }
                return fill;
            }
            catch (Exception e)
            {
                DeLog.WarnOnce("s8a.sample.slot", "DE.S8a sample slot roll failed: " + e.GetType().Name + ": " + e.Message);
                return new SlotFill();
            }
        }

        private void Move(Live l, S8aSampleRecord r)
        {
            var p = new Vector3((float)r.X, (float)r.Y, (float)r.Z);
            Quaternion q = Q(r);
            Vector3 d = p - l.Pos;
            Quaternion dq = q * Quaternion.Inverse(l.Rot);
            foreach (GameObject go in l.Gos)
            {
                if (go == null) continue;
                Vector3 off = go.transform.position - l.Pos;
                go.transform.position = p + dq * off;
                go.transform.rotation = dq * go.transform.rotation;
            }
            l.Pos = p;
            l.Rot = q;
            if (d.sqrMagnitude > 0) Moved++;
        }

        private IEnumerator LoadPrefab(string classId)
        {
            UWE.IPrefabRequest req = null;
            try { req = UWE.PrefabDatabase.GetPrefabAsync(classId); }
            catch (Exception) { req = null; }
            if (req == null) { _loading.Remove(classId); _missing.Add(classId); yield break; }
            yield return req;
            GameObject prefab = null;
            try { if (!req.TryGetPrefab(out prefab)) prefab = null; } catch (Exception) { prefab = null; }
            _loading.Remove(classId);
            if (prefab == null) { _missing.Add(classId); PrefabFailures++; DeLog.WarnOnce("s8a.sample.prefab." + classId, "DE.S8a sample: no prefab for class id " + classId); yield break; }
            _prefabs[classId] = prefab;
        }

        private void Despawn(ulong key)
        {
            Live l;
            if (!_live.TryGetValue(key, out l)) return;
            _live.Remove(key);
            foreach (GameObject go in l.Gos)
            {
                try { if (go != null) UnityEngine.Object.Destroy(go); } catch (Exception) { }
            }
            Despawned++;
        }

        /// <summary>
 /// Per family within <paramref name="radius"/> of the camera: the records in the file and the live objects (slots: rolled, filled,
 /// empty), the camera's point in the real map (the file's header): S8a.NS.entities.
 /// </summary>
        public Dictionary<string, object> Census(Vector3 cam, double radius)
        {
            var recs = new Dictionary<string, long>(StringComparer.Ordinal);
            var live = new Dictionary<string, long>(StringComparer.Ordinal);
            long slotRecs = 0, slotFilled = 0, slotEmpty = 0, slotPending = 0, objects = 0, deadObjects = 0, inactive = 0;
            var fams = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (S8aSampleRecord r in _records)
            {
                if (Dist(r, cam) > radius) continue;
                string fam = r.Family ?? "other";
                long n;
                recs.TryGetValue(fam, out n);
                recs[fam] = n + 1;
                if (!r.Slot) continue;
                slotRecs++;
                SlotFill fill;
                if (!_fills.TryGetValue(r.Key, out fill)) slotPending++;
                else if (string.IsNullOrEmpty(fill.ClassId)) slotEmpty++;
                else slotFilled++;
            }
            foreach (Live l in _live.Values)
            {
                if (Vector3.Distance(l.Pos, cam) > radius) continue;
                int alive = 0;
                foreach (GameObject go in l.Gos)
                {
                    if (go == null) { deadObjects++; continue; }
                    alive++;
                    if (!go.activeInHierarchy) inactive++;
                    string lf = S8aFlora.Family(go);
                    long q;
                    fams.TryGetValue(lf, out q);
                    fams[lf] = q + 1;
                }
                objects += alive;
                if (alive == 0) continue;
                string fam = l.Family ?? "other";
                long n;
                live.TryGetValue(fam, out n);
                live[fam] = n + 1;
            }
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "radius", radius }, { "records", recs.Count == 0 ? (object)"" : Join(recs) }, { "live", live.Count == 0 ? (object)"" : Join(live) },
                { "slotRecords", slotRecs }, { "slotFilled", slotFilled }, { "slotEmpty", slotEmpty }, { "slotPending", slotPending }, { "objects", objects },
                { "deadObjects", deadObjects }, { "inactive", inactive }, { "lifeFamilies", fams.Count == 0 ? (object)"" : Join(fams) }, { "noLwe", NoLwe },
                { "camNativeX", _camNative[0] }, { "camNativeY", _camNative[1] }, { "camNativeZ", _camNative[2] },
                { "fileSeq", _fileSeq }, { "fileRecords", (long)_records.Count }, { "sampleId", _sampleId.ToString("x16", CultureInfo.InvariantCulture) },
            };
            foreach (var kv in Status()) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
            return d;
        }

        private static string Join(Dictionary<string, long> d)
        {
            var parts = new List<string>();
            foreach (var kv in d) parts.Add(kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture));
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts.ToArray());
        }

        public Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "on", On }, { "liveRecords", (long)_live.Count }, { "spawned", Spawned }, { "despawned", Despawned }, { "moved", Moved }, { "polls", Polls },
                { "parseFailures", ParseFailures }, { "prefabFailures", PrefabFailures }, { "prefabsLoaded", (long)_prefabs.Count }, { "prefabsMissing", (long)_missing.Count },
                { "prefabsLoading", (long)_loading.Count }, { "slotsRolled", SlotsRolled }, { "slotsEmpty", SlotsEmpty }, { "slotsRestricted", SlotsRestricted },
                { "restricted", Restricted }, { "fileChartKey", _fileChartKey.ToString("x16", CultureInfo.InvariantCulture) },
            };
        }

        private void Row(Vector3 cam, bool chartOk)
        {
            if (_records.Count == 0 && _live.Count == 0) return;
            Dictionary<string, object> c = Census(cam, 60.0);
            JsonOut j = new JsonOut().Str("t", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Num("tickMs", Win32.TickCount64())
                .Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.sample").Str("ev", "s8a.sample").Obj("data").Num("snFrame", DeLog.Frame)
                .Num("camX", cam.x).Num("camY", cam.y).Num("camZ", cam.z).Bool("chartOk", chartOk);
            foreach (var kv in c)
            {
                object v = kv.Value;
                if (v is string) j.Str(kv.Key, (string)v);
                else if (v is bool) j.Bool(kv.Key, (bool)v);
                else if (v is double) j.Num(kv.Key, (double)v);
                else if (v is long) j.Num(kv.Key, (long)v);
                else j.Str(kv.Key, Convert.ToString(v, CultureInfo.InvariantCulture));
            }
            S8aTerrain.Row(j.EndObj().ToString());
        }

        public void Clear()
        {
            foreach (ulong k in new List<ulong>(_live.Keys)) Despawn(k);
            _live.Clear();
        }

        public void Dispose() { Clear(); }
    }
}
