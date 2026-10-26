using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// docs/S8A-PLANET-V2.md R8): the SE scanner's records
 /// (Wire <see cref="S8aOutcrops"/>, <c>ore-outcrops.tsv</c> in the store folder, SN coordinates of the chart its header names)
 /// become Subnautica's own large resource deposits: per record within <see cref="S8aOutcrops.SpawnRadiusM"/> of the camera one
 /// Drillable of the ore's TechType (<c>CraftData.GetPrefabForTechTypeAsync</c>, as S8aSpawns), set <see cref="S8aOutcrops.SinkM"/>
 /// into the seabed along the record's normal, a yaw from the key, scaled by the record's scale; updated when the record changes
 /// (it shrinks as SE's drill mines the vein), gone beyond <see cref="S8aOutcrops.DespawnM"/> or when the record goes. Transient
 /// (<c>LargeWorldStreamer.MakeEntityTransient</c>: never in the save), marked (<see cref="IsOurs"/>): never reported as
 /// Subnautica's own deposit (S8aCraterReports), never a flora candidate (S8aFlora), never damaged by its drills (the
 /// <c>Drillable.OnDrill</c> prefix in S8aPatches). Rows: <c>s8a.deposits</c> at 1 Hz with the live deposits within 100 m.
 /// <c>de.s8a deposits {status | immune | clear | on}</c>; immune drives Subnautica's drill path (<c>Drillable.OnDrill</c>, 60 hits)
 /// on the nearest deposit and reports its health before and after (S8a.NO.immune). Mutants: S8a.M21 every third record skipped
 /// (NO.cover red), S8a.M22 a phantom deposit 40 m from the first record (NO.vein red), S8a.M24 records' updates ignored
 /// (NO.shrink red), S8a.M25 the drill gets through (NO.immune red).
 /// </summary>
    internal sealed class S8aOreDeposits : IDisposable
    {
        public const int MaxSpawnsPerFrame = 3;
        public const ulong PollMs = 500;
        public const ulong PhantomKey = 0xDEADDEADDEADDEADUL;

        private sealed class Live
        {
            public ulong Key;
            public GameObject Go;
            public string TechType;
            public Vector3 Pos;
            public double Scale;
            public Vector3 BaseScale;
        }

        private static readonly HashSet<int> Ours = new HashSet<int>();
        private readonly Dictionary<ulong, Live> _live = new Dictionary<ulong, Live>();
        private readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly HashSet<string> _loading = new HashSet<string>(StringComparer.Ordinal), _missing = new HashSet<string>(StringComparer.Ordinal);
        private List<S8aOutcrop> _records = new List<S8aOutcrop>();
        private ulong _fileChartKey;
        private long _fileSeq, _fileLen = -1;
        private DateTime _fileTime;
        private ulong _nextPollMs, _nextRowMs;
        private MonoBehaviour _host;
        /// <summary>
 /// docs/S8A-PLANET-V2.md section 13): the ore voxels are the
 /// cue now, so the props are off unless DEEPENGINEERS_S8A_ORE_PROPS=on (the launcher's part `no` sets it for its rows).
 /// </summary>
        public bool On = string.Equals(Environment.GetEnvironmentVariable("DEEPENGINEERS_S8A_ORE_PROPS"), "on", StringComparison.OrdinalIgnoreCase);
        public long Spawned, Despawned, Updated, Polls, ParseFailures, PrefabFailures;

        public void Attach(MonoBehaviour host) { _host = host; }

        public int LiveCount { get { return _live.Count; } }

        /// <summary>A Subnautica object this mod placed as a deposit (any child of it).</summary>
        public static bool IsOurs(GameObject go)
        {
            if (go == null) return false;
            lock (Ours)
            {
                if (Ours.Count == 0) return false;
                Transform t = go.transform;
                while (t != null)
                {
                    if (Ours.Contains(t.gameObject.GetInstanceID())) return true;
                    t = t.parent;
                }
            }
            return false;
        }

        /// <summary>Per frame (main thread): poll the file, keep the deposits within reach, one row a second.</summary>
        public void Update(ulong nowMs, Vector3 cam, ulong storeKey, ulong chartKey)
        {
            if (!On || storeKey == 0) { if (!On && _live.Count > 0) Clear(); return; }
            if (nowMs >= _nextPollMs)
            {
                _nextPollMs = nowMs + PollMs;
                Poll(storeKey);
            }
            bool chartOk = _fileChartKey == chartKey && chartKey != 0;
            var want = new Dictionary<ulong, S8aOutcrop>();
            if (chartOk)
            {
                for (int i = 0; i < _records.Count; i++)
                {
                    if (S8aMutants.M21 && i % 3 == 2) continue;           // S8a.M21: every third record skipped
                    S8aOutcrop r = _records[i];
                    if (Dist(r, cam) <= S8aOutcrops.SpawnRadiusM) want[r.Key] = r;
                }
                if (S8aMutants.M22 && _records.Count > 0)
                {
                    S8aOutcrop ph = _records[0];                          // S8a.M22: a phantom deposit 40 m from any record
                    ph.Key = PhantomKey;
                    ph.X += 40.0;
                    if (Dist(ph, cam) <= S8aOutcrops.SpawnRadiusM) want[ph.Key] = ph;
                }
            }
            // gone: beyond reach, its record gone (M24 keeps them), or the chart left
            var gone = new List<ulong>();
            foreach (var kv in _live)
            {
                S8aOutcrop r;
                bool has = want.TryGetValue(kv.Key, out r);
                float d = Vector3.Distance(kv.Value.Pos, cam);
                if (kv.Value.Go == null || !chartOk || d > S8aOutcrops.DespawnM || (!has && !S8aMutants.M24)) gone.Add(kv.Key);
            }
            foreach (ulong k in gone) Despawn(k);
            int spawned = 0;
            foreach (var kv in want)
            {
                Live l;
                if (_live.TryGetValue(kv.Key, out l))
                {
                    if (!S8aMutants.M24 && Math.Abs(l.Scale - kv.Value.Scale) > 0.01) Apply(l, kv.Value);
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

        private static double Dist(S8aOutcrop r, Vector3 cam)
        {
            double dx = r.X - cam.x, dy = r.Y - cam.y, dz = r.Z - cam.z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private void Poll(ulong storeKey)
        {
            Polls++;
            string path = S8aOutcrops.PathOf(S8aSnPaths.Root(), storeKey);
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { if (_records.Count > 0) { _records = new List<S8aOutcrop>(); _fileLen = -1; } return; }
                if (fi.Length == _fileLen && fi.LastWriteTimeUtc == _fileTime) return;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                long seq;
                uint cs;
                ulong ck;
                List<S8aOutcrop> recs;
                if (!S8aOutcrops.TryParse(text, out seq, out cs, out ck, out recs)) { ParseFailures++; return; }
                _records = recs;
                _fileSeq = seq;
                _fileChartKey = ck;
                _fileLen = fi.Length;
                _fileTime = fi.LastWriteTimeUtc;
            }
            catch (Exception) { ParseFailures++; }
        }

        private bool TrySpawn(S8aOutcrop r)
        {
            string tech = r.TechType;
            if (tech == null || _host == null) return false;
            GameObject prefab;
            if (!_prefabs.TryGetValue(tech, out prefab))
            {
                if (!_missing.Contains(tech) && _loading.Add(tech)) { try { _host.StartCoroutine(LoadPrefab(tech)); } catch (Exception) { _loading.Remove(tech); _missing.Add(tech); } }
                return false;
            }
            try
            {
                GameObject go = UWE.Utils.InstantiateDeactivated(prefab, Vector3.zero, Quaternion.identity);
                if (go == null) { PrefabFailures++; return false; }
                lock (Ours) Ours.Add(go.GetInstanceID());                   // marked before any Start runs
                go.name = "DE.S8a.Deposit." + tech;
                var l = new Live { Key = r.Key, Go = go, TechType = tech, BaseScale = go.transform.localScale };
                Apply(l, r);
                go.SetActive(true);
                try { LargeWorldStreamer lws = LargeWorldStreamer.main; if (lws != null) lws.MakeEntityTransient(go); } catch (Exception) { }
                _live[r.Key] = l;
                Spawned++;
                return true;
            }
            catch (Exception e)
            {
                PrefabFailures++;
                DeLog.WarnOnce("s8a.deposit." + tech, "DE.S8a deposit " + tech + " failed: " + e.GetType().Name + ": " + e.Message);
                _missing.Add(tech);
                return false;
            }
        }

        private void Apply(Live l, S8aOutcrop r)
        {
            var n = new Vector3((float)r.NX, (float)r.NY, (float)r.NZ);
            if (n.sqrMagnitude < 1e-6f) n = Vector3.up;
            n.Normalize();
            var anchor = new Vector3((float)r.X, (float)r.Y, (float)r.Z);
            l.Pos = anchor - n * (float)S8aOutcrops.SinkM;
            float yaw = (float)((r.Key % 3600UL) / 10.0);
            Transform t = l.Go.transform;
            t.position = l.Pos;
            t.rotation = Quaternion.FromToRotation(Vector3.up, n) * Quaternion.Euler(0f, yaw, 0f);
            t.localScale = l.BaseScale * (float)Math.Max(0.05, r.Scale);
            if (l.Scale != 0 && Math.Abs(l.Scale - r.Scale) > 0.01) Updated++;
            l.Scale = r.Scale;
        }

        private IEnumerator LoadPrefab(string techType)
        {
            TechType tt;
            try { tt = (TechType)Enum.Parse(typeof(TechType), techType, true); }
            catch (Exception) { _missing.Add(techType); _loading.Remove(techType); DeLog.Warn("DE.S8a deposits: unknown TechType " + techType); yield break; }
            CoroutineTask<GameObject> task = CraftData.GetPrefabForTechTypeAsync(tt, false);
            yield return task;
            GameObject prefab = null;
            try { prefab = task.GetResult(); } catch (Exception) { }
            _loading.Remove(techType);
            if (prefab == null) { _missing.Add(techType); DeLog.Warn("DE.S8a deposits: no prefab for " + techType); yield break; }
            _prefabs[techType] = prefab;
        }

        private void Despawn(ulong key)
        {
            Live l;
            if (!_live.TryGetValue(key, out l)) return;
            _live.Remove(key);
            try
            {
                if (l.Go != null)
                {
                    lock (Ours) Ours.Remove(l.Go.GetInstanceID());
                    UnityEngine.Object.Destroy(l.Go);
                }
            }
            catch (Exception) { }
            Despawned++;
        }

        /// <summary>The live deposits within the spawn radius: "key:techType:x:y:z:scale;..." (the rows and the command's reply).</summary>
        public string NearList(Vector3 cam, out int near)
        {
            var sb = new StringBuilder();
            near = 0;
            foreach (Live l in _live.Values)
            {
                if (l.Go == null || Vector3.Distance(l.Pos, cam) > S8aOutcrops.SpawnRadiusM) continue;
                near++;
                sb.Append(sb.Length > 0 ? ";" : "").Append(l.Key.ToString("x16", CultureInfo.InvariantCulture)).Append(':').Append(l.TechType).Append(':')
                  .Append(l.Pos.x.ToString("0.0", CultureInfo.InvariantCulture)).Append(':').Append(l.Pos.y.ToString("0.0", CultureInfo.InvariantCulture)).Append(':')
                  .Append(l.Pos.z.ToString("0.0", CultureInfo.InvariantCulture)).Append(':').Append(l.Scale.ToString("0.00", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private void Row(Vector3 cam, bool chartOk)
        {
            int near;
            string list = NearList(cam, out near);
            var sb = new StringBuilder(list);
            int recordsNear = 0;
            if (chartOk) foreach (S8aOutcrop r in _records) if (Dist(r, cam) <= S8aOutcrops.SpawnRadiusM) recordsNear++;
            S8aTerrain.Row(new JsonOut().Str("t", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Num("tickMs", Win32.TickCount64())
                .Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.deposits").Str("ev", "s8a.deposits").Obj("data").Num("snFrame", DeLog.Frame)
                .Num("camX", cam.x).Num("camY", cam.y).Num("camZ", cam.z).Num("live", (long)_live.Count).Num("liveNear", (long)near).Num("records", (long)_records.Count)
                .Num("recordsNear", (long)recordsNear).Bool("chartOk", chartOk).Num("fileSeq", _fileSeq).Num("spawned", Spawned).Num("despawned", Despawned)
                .Num("updated", Updated).Num("prefabsMissing", (long)_missing.Count).Str("near", sb.ToString())
                .Bool("m21", S8aMutants.M21).Bool("m22", S8aMutants.M22).Bool("m24", S8aMutants.M24).Bool("m25", S8aMutants.M25).EndObj().ToString());
        }

        /// <summary>S8a.NO.immune: Subnautica's drill path on the nearest live deposit, n hits.</summary>
        public Dictionary<string, object> Immune(Vector3 cam, int hits)
        {
            Live best = null;
            float bd = float.MaxValue;
            foreach (Live l in _live.Values)
            {
                if (l.Go == null) continue;
                float d = Vector3.Distance(l.Pos, cam);
                if (d < bd) { bd = d; best = l; }
            }
            var r = new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true } };
            if (best == null) { r["ok"] = false; r["error"] = "no live deposit"; return r; }
            Drillable d0 = best.Go.GetComponent<Drillable>();
            if (d0 == null || d0.health == null) { r["ok"] = false; r["error"] = "no Drillable (or not started)"; return r; }
            float before = 0f;
            foreach (float h in d0.health) before += h;
            int calls = 0;
            for (int i = 0; i < hits; i++)
            {
                GameObject hit;
                try { d0.OnDrill(best.Go.transform.position, null, out hit); calls++; }
                catch (Exception) { }
            }
            float after = 0f;
            foreach (float h in d0.health) after += h;
            r["key"] = best.Key.ToString("x16", CultureInfo.InvariantCulture);
            r["techType"] = best.TechType;
            r["hits"] = hits;
            r["calls"] = calls;
            r["healthBefore"] = before;
            r["healthAfter"] = after;
            r["distance"] = bd;
            r["m25"] = S8aMutants.M25;
            S8aTerrain.Row(new JsonOut().Str("t", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Num("tickMs", Win32.TickCount64())
                .Num("frame", DeLog.Frame).Str("level", "info").Str("event", "s8a.deposit.immune").Str("ev", "s8a.deposit.immune").Obj("data").Num("snFrame", DeLog.Frame)
                .Str("key", (string)r["key"]).Str("techType", best.TechType).Num("hits", (long)hits).Num("calls", (long)calls).Num("healthBefore", before).Num("healthAfter", after)
                .Bool("m25", S8aMutants.M25).EndObj().ToString());
            return r;
        }

        public Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "on", On }, { "depositsLive", _live.Count }, { "records", _records.Count }, { "fileSeq", _fileSeq }, { "spawned", Spawned }, { "despawned", Despawned },
                { "updated", Updated }, { "polls", Polls }, { "parseFailures", ParseFailures }, { "prefabFailures", PrefabFailures }, { "prefabsMissing", _missing.Count },
            };
        }

        public void Clear()
        {
            foreach (ulong k in new List<ulong>(_live.Keys)) Despawn(k);
        }

        public void Dispose() { Clear(); }
    }
}
