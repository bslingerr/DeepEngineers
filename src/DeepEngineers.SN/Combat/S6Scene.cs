using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeepEngineers.Wire;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S6 test scenes behind <c>de.s6 {action: …}</c> (ROADMAP S6 oracle; dev builds):
 /// <list type="bullet">
 /// <item><c>bt1</c> {seconds 180, spawn {Peeper: 20, BoneShark: 1, ReaperLeviathan: 1}, dist 15, baselineSeconds 30,
 /// proxy "wire"|"local", boxes 50|1000}: S6.B-T1. A baseline phase (proxies moving, nothing spawned) then the spawns
 /// (de.spawn path) and the run with the penetration sampler; an s6.bt1 summary row at the end. The proxy is the
 /// stand-in producer's synthetic skin (wire) or a local synthetic hull looping at 5 m/s (local).</item>
 /// <item><c>local</c> {boxes 50|1000, centre [x,y,z], radius 15, speed 5}: the local synthetic hull only.</item>
 /// <item><c>penetration</c> {seconds}: <c>Physics.ComputePenetration</c> between every creature collider near a section and
 /// the section's colliders after each physics step (s6.penetration rows deeper than 0.05 m, per-second summaries;
 /// B-T1 (1) fails on a run of more than 3 steps deeper than 0.1 m).</item>
 /// <item><c>paths</c> {techType "Peeper", seconds 60, hz 2}: S6.N3, s6.path rows with the section a creature is inside.</item>
 /// <item><c>probe</c> {wm1 "&lt;vectors.jsonl&gt;" (else the embedded WM-1 subset), count 200, repose30 true}: S6.G1-E3, WM-1 box
 /// records as real colliders, ClosestPoint probes after SyncTransforms (S6.M5: under a scaled Frame, Entity +30°; S6.M9:
 /// no SyncTransforms).</item>
 /// <item><c>teardown</c>: stops every session, removes the local proxies and the creatures spawned through de.spawn.</item>
 /// </list>
 /// Combat scenes (bt1, penetration, paths) refuse to start unless the Freedom gate passed (S6.N2).
 /// </summary>
    internal sealed class S6Scene
    {
        public const ulong LocalEntityId = 0x0DE5600000000001UL;
        public const string RowBt1 = "s6.bt1", RowPenetration = "s6.penetration", RowPath = "s6.path", RowProbe = "s6.probe";
        public const int MaxPenetrationRowsPerSecond = 200;

        private readonly S6Host _h;
        public string Phase = "idle";

        // bt1
        private bool _bt1;
        private double _bt1Start, _bt1RunAt, _bt1End;
        private bool _bt1Spawned;
        private readonly List<KeyValuePair<string, int>> _bt1SpawnList = new List<KeyValuePair<string, int>>();
        private int _bt1Dist;
        private readonly Dictionary<string, long> _bites = new Dictionary<string, long>(StringComparer.Ordinal);
        private long _runHits, _runRecords;

        // penetration (B-T1 (1))
        private bool _pen;
        private double _penUntil, _nextScan;
        public readonly PenetrationStreaks Streaks = new PenetrationStreaks();
        private readonly List<Collider> _creatureCols = new List<Collider>();
        private readonly Dictionary<int, string> _colTech = new Dictionary<int, string>();
        private readonly Collider[] _overlap = new Collider[256];
        private int _penRows;

        // paths (S6.N3)
        private bool _paths;
        private double _pathsUntil, _nextPath, _pathsHz = 2;
        private TechType _pathsTech = TechType.Peeper;
        private readonly List<GameObject> _pathScan = new List<GameObject>();
        private double _nextPathScan;
        public long PathRows, PathInside;

        // spawned objects (de.spawn), removed by teardown
        private readonly List<GameObject> _spawned = new List<GameObject>();
        public long OverlapFull;   // review F17

        public S6Scene(S6Host h) { _h = h; }

        public bool Running { get { return _bt1 || _pen || _paths; } }

        // ------------------------------------------------------------------------------------------ commands

        public bool Handle(string action, Dictionary<string, object> a, out string err, out JsonOut detail)
        {
            err = null;
            detail = null;
            switch (action)
            {
                case "bt1": detail = StartBt1(a, out err); return true;
                case "local": detail = StartLocal(a, out err); return true;
                case "penetration":
                    if (!CombatAllowed(out err)) return true;
                    StartPenetration(S3Args.Num(a, "seconds", 60));
                    detail = new JsonOut().Num("seconds", S3Args.Num(a, "seconds", 60));
                    return true;
                case "paths": detail = StartPaths(a, out err); return true;
                case "probe": detail = Probe(a, out err); return true;
                case "teardown": detail = Teardown("de.s6 teardown"); return true;
                default: return false;
            }
        }

        private bool CombatAllowed(out string err)
        {
            err = null;
            if (!_h.WorldUp) err = "no world";
            else if (!_h.GatePass) err = "freedom gate REFUSED: " + _h.GateLine;
            return err == null;
        }

        // ------------------------------------------------------------------------------------------ local proxy

        private JsonOut StartLocal(Dictionary<string, object> a, out string err)
        {
            err = null;
            if (!_h.WorldUp) { err = "no world"; return null; }
            Camera cam = MainCamera.camera;
            if (cam == null) { err = "no main camera"; return null; }
            int boxes = (int)S3Args.Num(a, "boxes", 50);
            List<SkinSectionData> skin = boxes > 50 ? SyntheticSkins.Perf(boxes) : SyntheticSkins.Bt1();
            Vector3 right = cam.transform.right;
            right.y = 0;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
            Vector3 c = cam.transform.position + right * 30f;
            object cv;
            double x, y, z;
            if (a.TryGetValue("centre", out cv) && S3Args.TryVec(cv, out x, out y, out z) && !double.IsNaN(y)) c = new Vector3((float)x, (float)y, (float)z);
            double radius = S3Args.Num(a, "radius", 15), speed = S3Args.Num(a, "speed", 5);
            _h.Proxies.ClearLocal("new local proxy");
            _h.Proxies.ApplySkin(LocalEntityId, 1, skin, null, true);
            ProxyEntity e = _h.Proxies.Get(LocalEntityId);
            e.LoopCentre = ProxySections.D(c);
            e.LoopRadius = radius;
            e.LoopSpeed = speed;
            e.LoopStart = _h.Now;
            DeLog.Info("DE.S6 local synthetic proxy " + DeLog.Hex(LocalEntityId) + ": " + SyntheticSkins.CountBoxes(skin) + " boxes in " + skin.Count + " sections, loop centre " +
                       ProxySections.D(c) + " radius " + radius + " m at " + speed + " m/s");
            var j = new JsonOut();
            j.Str("entity", S6Rows.Hex(LocalEntityId)).Num("boxes", (long)SyntheticSkins.CountBoxes(skin)).Num("sections", (long)skin.Count).Vec("centre", ProxySections.D(c))
             .Num("radius", radius).Num("speed", speed);
            return j;
        }

        // ------------------------------------------------------------------------------------------ B-T1

        private JsonOut StartBt1(Dictionary<string, object> a, out string err)
        {
            if (!CombatAllowed(out err)) return null;
            string proxy = S3Args.Str(a, "proxy", null);
            int wire = 0;
            foreach (ProxyEntity e in _h.Proxies.Entities) if (!e.Local) wire++;
            if (proxy == null) proxy = wire > 0 ? "wire" : "local";
            JsonOut local = null;
            if (proxy == "local") { local = StartLocal(a, out err); if (err != null) return null; }
            else if (proxy != "wire") { err = "proxy: wire|local"; return null; }
            else if (wire == 0) { err = "proxy=wire but no SkinSet received (start the stand-in producer with --s6 --skin synth:bt1, or use proxy=local)"; return null; }
            _bt1SpawnList.Clear();
            object sv;
            var sd = a.TryGetValue("spawn", out sv) ? sv as Dictionary<string, object> : null;
            if (sd == null)
            {
                _bt1SpawnList.Add(new KeyValuePair<string, int>("Peeper", 20));
                _bt1SpawnList.Add(new KeyValuePair<string, int>("BoneShark", 1));
                _bt1SpawnList.Add(new KeyValuePair<string, int>("ReaperLeviathan", 1));
            }
            else foreach (KeyValuePair<string, object> kv in sd) { double n; if (S1Args.TryDouble(kv.Value, out n)) _bt1SpawnList.Add(new KeyValuePair<string, int>(kv.Key, (int)n)); }
            double seconds = S3Args.Num(a, "seconds", 180), baseline = S3Args.Num(a, "baselineSeconds", 30);
            _bt1Dist = (int)S3Args.Num(a, "dist", 15);
            double now = _h.Now;
            _bt1 = true;
            _bt1Spawned = false;
            _bt1Start = now;
            _bt1RunAt = now + Math.Max(0, baseline);
            _bt1End = _bt1RunAt + Math.Max(1, seconds);
            _bites.Clear();
            _runHits = 0;
            _runRecords = 0;
            Streaks.Reset();
            Phase = "bt1-baseline";
            DeLog.Info("DE.S6 bt1 start: proxy=" + proxy + " baseline " + baseline + " s, run " + seconds + " s, spawn " + Describe(_bt1SpawnList) + " dist " + _bt1Dist);
            var j = new JsonOut();
            j.Str("proxy", proxy).Num("baselineSeconds", baseline).Num("seconds", seconds).Str("spawn", Describe(_bt1SpawnList)).Num("dist", (long)_bt1Dist);
            if (local != null) j.Str("local", local.ToString());
            return j;
        }

        private static string Describe(List<KeyValuePair<string, int>> l)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in l) parts.Add(kv.Key + ":" + kv.Value);
            return string.Join(",", parts.ToArray());
        }

        private void Bt1Tick(double now)
        {
            if (!_bt1Spawned && now >= _bt1RunAt)
            {
                _bt1Spawned = true;
                Phase = "bt1-run";
                foreach (KeyValuePair<string, int> kv in _bt1SpawnList)
                {
                    int left = kv.Value;
                    while (left > 0)
                    {
                        int n = Math.Min(left, SpawnArgs.MaxCount);
                        left -= n;
                        var d = new Dictionary<string, object>(StringComparer.Ordinal) { { "techType", kv.Key }, { "count", (double)n }, { "dist", (double)_bt1Dist } };
                        string err;
                        _h.Spawn(d, out err);
                        if (err != null) DeLog.Warn("DE.S6 bt1 spawn " + kv.Key + " x" + n + " FAILED: " + err);
                    }
                }
                StartPenetration(_bt1End - now);
            }
            if (now >= _bt1End) FinishBt1("completed");
        }

        private void FinishBt1(string why)
        {
            if (!_bt1) return;
            _bt1 = false;
            float minHealth = float.PositiveInfinity;
            int sections = 0;
            foreach (ProxySection p in _h.Proxies.AllSections())
            {
                sections++;
                if (p.Live != null && p.Live.health < minHealth) minHealth = p.Live.health;
            }
            bool healthOk = sections > 0 && minHealth >= 100f;
            JsonOut j = S6Rows.Begin(RowBt1);
            j.Str("why", why).Num("seconds", _bt1End - _bt1RunAt).Bool("penetrationPass", Streaks.Pass).Num("maxStreak", (long)Streaks.MaxStreakSeen)
             .Num("maxDepthM", Streaks.MaxDepthM).Num("violations", Streaks.Violations).Num("samples", Streaks.Samples).Num("deepSamples", Streaks.Deep)
             .Num("hits", _runHits).Num("records", _runRecords).Num("sections", (long)sections).Num("sectionHealthMin", sections > 0 ? (double)minHealth : double.NaN)
             .Bool("healthUnchanged", healthOk).Num("spawned", (long)_spawned.Count).Str("mutants", S6Mutants.Describe());
            j.Obj("bites");
            foreach (KeyValuePair<string, long> kv in _bites) j.Num(kv.Key, kv.Value);
            j.EndObj();
            S6Rows.End(j);
            DeLog.Info("DE.S6 bt1 " + why + ": penetration " + (Streaks.Pass ? "PASS" : "FAIL") + " (max streak " + Streaks.MaxStreakSeen + ", max depth " +
                       Streaks.MaxDepthM.ToString("0.###", CultureInfo.InvariantCulture) + " m) hits " + _runHits + " records " + _runRecords + " healthUnchanged=" + healthOk);
            _pen = false;
            Phase = _paths ? "paths" : "idle";
        }

        /// <summary>Every forwarded record during a scene (bites by amount: B-T1 (2) wants 30 and 80).</summary>
        public void NoteHit(DamageRecord r, HitVia via, ProxyMarker m, bool isPlayer)
        {
            if (!_bt1 && !_pen) return;
            _runHits++;
            if (r.dedupSeq != 0) _runRecords++;
            string k = "t" + r.snDamageType.ToString(CultureInfo.InvariantCulture) + "_" + r.amount.ToString("0.###", CultureInfo.InvariantCulture);
            long n;
            _bites.TryGetValue(k, out n);
            _bites[k] = n + 1;
        }

        // ------------------------------------------------------------------------------------------ penetration

        private void StartPenetration(double seconds)
        {
            _pen = true;
            _penUntil = _h.Now + Math.Max(1, seconds);
            _nextScan = 0;
            if (!_bt1) { Streaks.Reset(); Phase = "penetration"; }
            DeLog.Info("DE.S6 penetration sampler on for " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
        }

        private void ScanCreatures()
        {
            _creatureCols.Clear();
            var seen = new HashSet<int>();
            foreach (ProxySection p in _h.Proxies.AllSections())
            {
                if (p.Root == null || !p.Active) continue;
                int n = Physics.OverlapSphereNonAlloc(ProxySections.V(p.TargetPos), (float)p.RhoMax + 10f, _overlap, ~(1 << 30), QueryTriggerInteraction.Ignore);   // review F17: not the terrain layer
                if (n >= _overlap.Length && OverlapFull++ == 0) DeLog.Info("DE.S6 penetration scan: the overlap buffer (" + _overlap.Length + ") filled near a section; creatures may be missed");
                for (int i = 0; i < n; i++)
                {
                    Collider c = _overlap[i];
                    if (c == null || c.isTrigger || !seen.Add(c.GetInstanceID())) continue;
                    ProxyMarker pm;
                    GameObject owner = c.attachedRigidbody != null ? c.attachedRigidbody.gameObject : c.gameObject;
                    if (_h.Proxies.TryGetRoot(owner, out pm)) continue;
                    Creature cr = c.GetComponentInParent<Creature>();
                    if (cr == null) continue;
                    _creatureCols.Add(c);
                    if (!_colTech.ContainsKey(c.GetInstanceID()))
                    {
                        string tech;
                        try { tech = CraftData.GetTechType(cr.gameObject).ToString(); } catch (Exception) { tech = "?"; }
                        _colTech[c.GetInstanceID()] = tech;
                    }
                }
            }
        }

        public void BeforeStep() { }

        /// <summary>After each physics step (B-T1 (1)): every creature collider near a section against the section's colliders.</summary>
        public void AfterStep()
        {
            if (!_pen) return;
            double now = _h.Now;
            if (now >= _nextScan) { _nextScan = now + 1.0; ScanCreatures(); }
            Streaks.BeginStep();
            foreach (Collider cc in _creatureCols)
            {
                if (cc == null || !cc.enabled || !cc.gameObject.activeInHierarchy) continue;
                Bounds cb = cc.bounds;
                float cr = cb.extents.magnitude;
                foreach (ProxySection p in _h.Proxies.AllSections())
                {
                    if (p.Root == null || !p.Active) continue;
                    float reach = (float)p.RhoMax + cr;
                    if ((ProxySections.V(p.TargetPos) - cb.center).sqrMagnitude > reach * reach) continue;
                    float depth = 0f;
                    Vector3 dir = Vector3.zero;
                    foreach (Collider pc in p.Marker.Colliders)
                    {
                        if (pc == null || !pc.enabled || !pc.bounds.Intersects(cb)) continue;
                        Vector3 d;
                        float dist;
                        if (Physics.ComputePenetration(cc, cc.transform.position, cc.transform.rotation, pc, pc.transform.position, pc.transform.rotation, out d, out dist) && dist > depth)
                        {
                            depth = dist;
                            dir = d;
                        }
                    }
                    if (depth <= 0f) continue;
                    long key = PenetrationStreaks.Key(cc.GetInstanceID(), p.Data.SectionId ^ (uint)p.Owner.EntityId);
                    int streak = Streaks.Note(key, depth);
                    if (depth > PenetrationStreaks.RowM && _penRows < MaxPenetrationRowsPerSecond)
                    {
                        _penRows++;
                        Streaks.Rows++;
                        string tech;
                        _colTech.TryGetValue(cc.GetInstanceID(), out tech);
                        JsonOut j = S6Rows.Begin(RowPenetration);
                        j.Num("t", now - _bt1RunAt).Str("creature", tech ?? "?").Num("id", (long)cc.GetInstanceID()).Str("entity", S6Rows.Hex(p.Owner.EntityId))
                         .Str("section", S6Rows.Sec(p.Data.SectionId)).Num("depthM", (double)depth).Vec("dir", dir.x, dir.y, dir.z).Num("streak", (long)streak)
                         .Bool("trigger", S6Mutants.IsOn(S6Mutants.Triggers));
                        S6Rows.End(j);
                    }
                }
            }
            Streaks.EndStep();
        }

        // ------------------------------------------------------------------------------------------ paths (S6.N3)

        private JsonOut StartPaths(Dictionary<string, object> a, out string err)
        {
            if (!CombatAllowed(out err)) return null;
            string t = S3Args.Str(a, "techType", "Peeper");
            TechType tt;
            try { tt = (TechType)Enum.Parse(typeof(TechType), t, true); }
            catch (Exception) { err = "unknown TechType '" + t + "'"; return null; }
            _pathsTech = tt;
            _pathsHz = Math.Max(0.1, Math.Min(20, S3Args.Num(a, "hz", 2)));
            _pathsUntil = _h.Now + Math.Max(1, S3Args.Num(a, "seconds", 60));
            _nextPath = 0;
            _nextPathScan = 0;
            _paths = true;
            if (!_bt1 && !_pen) Phase = "paths";
            return new JsonOut().Str("techType", tt.ToString()).Num("hz", _pathsHz).Num("seconds", S3Args.Num(a, "seconds", 60));
        }

        private void PathsTick(double now)
        {
            if (now >= _pathsUntil) { _paths = false; if (Phase == "paths") Phase = "idle"; DeLog.Info("DE.S6 paths done: " + PathRows + " rows, inside " + PathInside); return; }
            if (now < _nextPath) return;
            _nextPath = now + 1.0 / _pathsHz;
            if (now >= _nextPathScan)
            {
                _nextPathScan = now + 2.0;
                _pathScan.Clear();
                foreach (Creature c in Object.FindObjectsOfType<Creature>())
                {
                    if (c == null) continue;
                    TechType t;
                    try { t = CraftData.GetTechType(c.gameObject); } catch (Exception) { continue; }
                    if (t == _pathsTech) _pathScan.Add(c.gameObject);
                    if (_pathScan.Count >= 200) break;
                }
            }
            if (!S6Mutants.IsOn(S6Mutants.NoSync)) Physics.SyncTransforms();
            foreach (GameObject go in _pathScan)
            {
                if (go == null) continue;
                Vector3 pos = go.transform.position;
                string inside = null;
                foreach (ProxySection p in _h.Proxies.AllSections())
                {
                    if (p.Root == null || !p.Active) continue;
                    if ((ProxySections.V(p.TargetPos) - pos).sqrMagnitude > (p.RhoMax + 1) * (p.RhoMax + 1)) continue;
                    foreach (Collider c in p.Marker.Colliders)
                    {
                        if (c == null || !c.enabled) continue;
                        if ((c.ClosestPoint(pos) - pos).sqrMagnitude < 1e-8f) { inside = S6Rows.Sec(p.Data.SectionId); break; }
                    }
                    if (inside != null) break;
                }
                PathRows++;
                if (inside != null) PathInside++;
                JsonOut j = S6Rows.Begin(RowPath);
                j.Num("id", (long)go.GetInstanceID()).Str("techType", _pathsTech.ToString()).Vec("pos", ProxySections.D(pos));
                if (inside != null) j.Str("inside", inside); else j.Str("inside", null);
                S6Rows.End(j);
            }
        }

        // ------------------------------------------------------------------------------------------ probe (S6.G1-E3)

        private struct ProbeRec
        {
            public Wm1BoxRecord R;
            public GameObject Root, Frame;
            public BoxCollider Col;
            public Vec3 RootPos;
            public Quat Q;
        }

        private JsonOut Probe(Dictionary<string, object> a, out string err)
        {
            err = null;
            string path = S3Args.Str(a, "wm1", null);
            int count = (int)S3Args.Num(a, "count", 200);
            bool repose = S3Args.Flag(a, "repose30", true);
            bool m5 = S6Mutants.IsOn(S6Mutants.ProbeUnderFrame), m9 = S6Mutants.IsOn(S6Mutants.NoSync);
            var recs = new List<Wm1BoxRecord>();
            if (string.IsNullOrEmpty(path)) for (int i = 0; i < Wm1Subset.SnBoxCount && recs.Count < count; i++) recs.Add(Wm1Subset.SnBox(i));
            else if (!LoadWm1(path, count, recs, out err)) return null;
            if (recs.Count == 0) { err = "no WM-1 box records"; return null; }
            Quat r30 = Repose30();
            var probes = new List<ProbeRec>();
            try
            {
                foreach (Wm1BoxRecord r in recs) probes.Add(Build(r, m5, r30));
                if (!m9) Physics.SyncTransforms();
                int corners = 0, fails = 0;
                double worstDev = 0, worstPre = 0, worstRepose = 0;
                for (int k = 0; k < probes.Count; k++) Measure(probes[k], k, "pose", m5, Quat.Identity, ref corners, ref fails, ref worstDev, ref worstPre);
                if (repose && !m5)
                {
                    foreach (ProbeRec p in probes)
                        p.Root.transform.rotation = ProxySections.Q(r30 * p.Q);   // rigid 30° about the root's own position
                    if (!m9) Physics.SyncTransforms();
                    double dummy = 0;
                    for (int k = 0; k < probes.Count; k++) Measure(probes[k], k, "repose30", false, r30, ref corners, ref fails, ref dummy, ref worstRepose);
                }
                bool pass = fails == 0;
                JsonOut s = S6Rows.Begin(RowProbe);
                s.Bool("summary", true).Num("records", (long)probes.Count).Num("corners", (long)corners).Num("fails", (long)fails).Num("worstDevM", worstDev)
                 .Num("worstPrescaledM", worstPre).Num("worstReposeM", worstRepose).Bool("pass", pass).Bool("m5", m5).Bool("m9", m9).Str("source", path ?? "embedded WM-1 subset");
                S6Rows.End(s);
                DeLog.Info("DE.S6 probe " + (pass ? "PASS" : "FAIL") + " records=" + probes.Count + " corners=" + corners + " fails=" + fails + " worstDev=" + DeLog.F(worstDev) +
                           " worstPrescaled=" + DeLog.F(worstPre) + " worstRepose=" + DeLog.F(worstRepose) + " mutants=" + S6Mutants.Describe());
                return new JsonOut().Bool("pass", pass).Num("records", (long)probes.Count).Num("corners", (long)corners).Num("fails", (long)fails)
                    .Num("worstDevM", worstDev).Num("worstPrescaledM", worstPre).Num("worstReposeM", worstRepose);
            }
            finally
            {
                foreach (ProbeRec p in probes)
                {
                    GameObject top = p.Frame != null ? p.Frame : p.Root;
                    if (top != null) { top.SetActive(false); Object.Destroy(top); }
                }
            }
        }

        /// <summary>30° about the unit axis (0.6, 0, 0.8) (the S0 re-pose, UnitySelfTest).</summary>
        private static Quat Repose30()
        {
            double half = 30.0 * Math.PI / 360.0, sn = Math.Sin(half);
            return new Quat(0.6 * sn, 0, 0.8 * sn, Math.Cos(half));
        }

        private static ProbeRec Build(Wm1BoxRecord r, bool m5, Quat r30)
        {
            var p = new ProbeRec { R = r };
            WirePose pose = r.Pose;
            if (!m5)
            {
                p.Q = pose.Q;
                p.RootPos = WireMath.Apply(pose, r.SectionCentreSe);
                var go = new GameObject("DE.S6.probe");
                go.layer = 0;
                go.transform.SetPositionAndRotation(ProxySections.V(p.RootPos), ProxySections.Q(pose.Q));
                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
                ProxyBox pb = WireMath.PrescaleBox(pose.Q, pose.S, r.SectionCentreSe, r.BoxCentreSe, r.HalfExtentsSe, r.AxisSe0, r.AxisSe1, r.AxisSe2);
                Vector3 size = new Vector3((float)(2 * pb.HalfExtents.X), (float)(2 * pb.HalfExtents.Y), (float)(2 * pb.HalfExtents.Z));
                if (!r.Rotated)
                {
                    p.Col = go.AddComponent<BoxCollider>();
                    p.Col.center = ProxySections.V(pb.Centre);
                    p.Col.size = size;
                }
                else
                {
                    var child = new GameObject("box");
                    child.layer = 0;
                    child.transform.SetParent(go.transform, false);
                    child.transform.localPosition = ProxySections.V(pb.Centre);
                    child.transform.localRotation = ProxySections.Q(pb.Rotation);
                    p.Col = child.AddComponent<BoxCollider>();
                    p.Col.size = size;
                }
                p.Root = go;
                return p;
            }
            // S6.M5: Frame (o, scale (s, 1, s)) → Entity (rotation q·30°) → an unscaled box at Mirror(b) (no K: Unity cannot shear)
            Quat q = r30 * pose.Q;
            p.Q = q;
            p.RootPos = WireMath.Apply(pose.O, q, pose.S, r.SectionCentreSe);
            var frame = new GameObject("DE.S6.probe.frame");
            frame.transform.position = ProxySections.V(pose.O);
            frame.transform.localScale = new Vector3((float)pose.S, 1f, (float)pose.S);
            var entity = new GameObject("entity");
            entity.transform.SetParent(frame.transform, false);
            entity.transform.localRotation = ProxySections.Q(q);
            Rigidbody erb = entity.AddComponent<Rigidbody>();
            erb.isKinematic = true;
            erb.useGravity = false;
            var box = new GameObject("box");
            box.transform.SetParent(entity.transform, false);
            box.transform.localPosition = ProxySections.V(WireMath.Mirror(r.BoxCentreSe));
            Vec3 a0 = WireMath.Mirror(r.AxisSe0), a1 = WireMath.Mirror(r.AxisSe1), a2 = WireMath.Mirror(r.AxisSe2);
            box.transform.localRotation = ProxySections.Q(Mat3.FromColumns(a0, a1, -a2).ToQuat());
            p.Col = box.AddComponent<BoxCollider>();
            p.Col.size = new Vector3((float)(2 * r.HalfExtentsSe.X), (float)(2 * r.HalfExtentsSe.Y), (float)(2 * r.HalfExtentsSe.Z));
            p.Frame = frame;
            p.Root = entity;
            return p;
        }

        /// <summary>
 /// The 8 corners of one record: devM = ClosestPoint at 0.5 m outside the exact affine corner vs that corner (bound 1 mm
 /// for an exact case, else 1.02 × the record's predicted pre-scale error (Wire PrescaleCornerError) + 1 mm here; the analyzer re-judges with wire_ref.py s6bound for the
 /// depth and tilt in the row); devPrescaledM = the same against the oracle's pre-scaled box (always 1 mm). After the
 /// re-pose only the pre-scaled box (rigidly rotated) is checked.
 /// </summary>
        private static void Measure(ProbeRec p, int index, string phase, bool m5, Quat rep, ref int corners, ref int fails, ref double worstDev, ref double worstPre)
        {
            Wm1BoxRecord r = p.R;
            WirePose pose = r.Pose;
            Quat q = p.Q;
            double s = pose.S;
            Vec3 boxC = WireMath.Apply(pose.O, q, s, r.BoxCentreSe);
            var oracle = new ProxyBox(r.ExpectedCentreUnity, r.ExpectedHalfExtentsUnity, r.ExpectedRotationUnity, Vec3.Zero, Vec3.Zero, Vec3.Zero);
            Vec3 preC = p.RootPos + q.Rotate(r.ExpectedCentreUnity);
            bool exact = Exact(q, s, r);
            double tilt = CollisionSkinMath.TiltDegrees(q);
            // the record's own predicted pre-scale corner error (Wire, the S6.G1-E3 oracle) + 1 mm of float32
            double predicted = CollisionSkinMath.PrescaleCornerError(q, s, r.HalfExtentsSe, r.AxisSe0, r.AxisSe1, r.AxisSe2);
            double bound = exact ? 0.001 : Math.Max(0.001, 1.02 * predicted + 0.001);
            for (int c = 0; c < 8; c++)
            {
                int sx = (c & 1) != 0 ? 1 : -1, sy = (c & 2) != 0 ? 1 : -1, sz = (c & 4) != 0 ? 1 : -1;
                Vec3 pre = p.RootPos + q.Rotate(oracle.CornerSectionLocal(sx, sy, sz));
                Vec3 preCentre = preC;
                if (phase == "repose30")
                {
                    pre = p.RootPos + rep.Rotate(pre - p.RootPos);
                    preCentre = p.RootPos + rep.Rotate(preC - p.RootPos);
                }
                Vec3 outPre = (pre - preCentre).Normalized();
                Vec3 gotPre = ProxySections.D(p.Col.ClosestPoint(ProxySections.V(pre + outPre * 0.5)));
                double devPre = Vec3.Distance(gotPre, pre);
                double dev = double.NaN;
                Vec3 exactCorner = Vec3.Zero, gotExact = Vec3.Zero;
                if (phase == "pose")
                {
                    Vec3 cornerSe = r.BoxCentreSe + r.AxisSe0 * (sx * r.HalfExtentsSe.X) + r.AxisSe1 * (sy * r.HalfExtentsSe.Y) + r.AxisSe2 * (sz * r.HalfExtentsSe.Z);
                    exactCorner = WireMath.Apply(pose.O, q, s, cornerSe);
                    Vec3 outEx = (exactCorner - boxC).Normalized();
                    gotExact = ProxySections.D(p.Col.ClosestPoint(ProxySections.V(exactCorner + outEx * 0.5)));
                    dev = Vec3.Distance(gotExact, exactCorner);
                }
                bool pass = phase == "pose" ? dev <= bound && (m5 || devPre <= 0.001) : devPre <= 0.001;
                corners++;
                if (!pass) fails++;
                if (!double.IsNaN(dev) && dev > worstDev) worstDev = dev;
                if (devPre > worstPre) worstPre = devPre;
                JsonOut j = S6Rows.Begin(RowProbe);
                j.Num("record", (long)index).Num("corner", (long)c).Str("phase", phase).Vec("expected", phase == "pose" ? exactCorner : pre)
                 .Vec("got", phase == "pose" ? gotExact : gotPre).Num("devM", dev).Num("boundM", bound).Num("devPrescaledM", devPre).Num("predictedM", predicted).Bool("rotated", r.Rotated)
                 .Bool("tilted", !exact).Num("tiltDeg", tilt).Num("depthY", pose.O.Y).Num("s", s).Vec("h", r.HalfExtentsSe).Bool("pass", pass).Bool("m5", m5);
                S6Rows.End(j);
            }
        }

        /// <summary>Exact case: every Mirror(e_i) is an eigenvector of K(q, s) (level hull with level/vertical box axes).</summary>
        private static bool Exact(Quat q, double s, Wm1BoxRecord r)
        {
            Vec3[] axes = { r.AxisSe0, r.AxisSe1, r.AxisSe2 };
            foreach (Vec3 e in axes)
            {
                Vec3 a = WireMath.Mirror(e);
                Vec3 v = WireMath.K(q, s, a);
                Vec3 perp = v - a * Vec3.Dot(v, a);
                if (perp.Length > 1e-9) return false;
            }
            return true;
        }

        /// <summary>Reads WM-1 vectors (JSONL: "pose" records in order, "box" records pointing at them by index).</summary>
        private static bool LoadWm1(string path, int count, List<Wm1BoxRecord> recs, out string err)
        {
            err = null;
            if (!File.Exists(path)) { err = "wm1 file not found: " + path; return false; }
            var poses = new List<WirePose>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (recs.Count >= count) break;
                if (string.IsNullOrEmpty(line) || line[0] != '{') continue;
                var d = Json.Parse(line) as Dictionary<string, object>;
                if (d == null) continue;
                string kind = d.ContainsKey("kind") ? d["kind"] as string : null;
                if (kind == "pose")
                {
                    poses.Add(new WirePose(V3(d, "o"), Q4(d, "q"), N(d, "s")));
                    continue;
                }
                if (kind != "box") continue;
                int pi = (int)N(d, "pose");
                if (pi < 0 || pi >= poses.Count) { err = "box record before its pose " + pi; return false; }
                var axes = d["axes_se"] as List<object>;
                var r = new Wm1BoxRecord
                {
                    Pose = poses[pi],
                    SectionCentreSe = V3(d, "section_centre_se"),
                    ExpectedSectionRootSn = V3(d, "section_root_sn"),
                    BoxCentreSe = V3(d, "box_centre_se"),
                    HalfExtentsSe = V3(d, "half_extents_se"),
                    AxisSe0 = V3L(axes[0]), AxisSe1 = V3L(axes[1]), AxisSe2 = V3L(axes[2]),
                    ExpectedCentreUnity = V3(d, "centre_section_local_unity"),
                    ExpectedHalfExtentsUnity = V3(d, "half_extents_unity"),
                    ExpectedRotationUnity = Q4(d, "rotation_section_local_unity"),
                    Rotated = d.ContainsKey("rotated") && d["rotated"] is bool && (bool)d["rotated"],
                };
                recs.Add(r);
            }
            return true;
        }

        private static double N(Dictionary<string, object> d, string k) { double v; return d.ContainsKey(k) && S1Args.TryDouble(d[k], out v) ? v : double.NaN; }
        private static Vec3 V3(Dictionary<string, object> d, string k) { return d.ContainsKey(k) ? V3L(d[k]) : new Vec3(double.NaN, double.NaN, double.NaN); }

        private static Vec3 V3L(object o)
        {
            var l = o as List<object>;
            double x, y, z;
            if (l == null || l.Count < 3 || !S1Args.TryDouble(l[0], out x) || !S1Args.TryDouble(l[1], out y) || !S1Args.TryDouble(l[2], out z)) return new Vec3(double.NaN, double.NaN, double.NaN);
            return new Vec3(x, y, z);
        }

        private static Quat Q4(Dictionary<string, object> d, string k)
        {
            var l = d.ContainsKey(k) ? d[k] as List<object> : null;
            double x, y, z, w;
            if (l == null || l.Count < 4 || !S1Args.TryDouble(l[0], out x) || !S1Args.TryDouble(l[1], out y) || !S1Args.TryDouble(l[2], out z) || !S1Args.TryDouble(l[3], out w)) return Quat.Identity;
            return new Quat(x, y, z, w);
        }

        // ------------------------------------------------------------------------------------------ spawned objects and teardown

        public void TrackSpawn(GameObject go, TechType t)
        {
            if (go == null) return;
            if (_spawned.Count < 1000) _spawned.Add(go);
        }

        private JsonOut Teardown(string why)
        {
            Stop(why);
            int local = _h.Proxies.ClearLocal(why);
            int destroyed = 0;
            foreach (GameObject go in _spawned) if (go != null) { Object.Destroy(go); destroyed++; }
            _spawned.Clear();
            // review F1: spawns are tracked only through the optional craft postfix; the fallback destroys every Creature of a
 // TechType de.spawn asked for within 100 m of the camera
            int creatures = 0;
            try
            {
                Camera cam = MainCamera.camera;
                Vector3 at = cam != null ? cam.transform.position : Vector3.zero;
                foreach (Creature c in Object.FindObjectsOfType<Creature>())
                {
                    if (c == null) continue;
                    TechType tt = CraftData.GetTechType(c.gameObject);
                    if (!SpawnRelay.SpawnedTech.Contains(tt.ToString())) continue;
                    if (cam != null && Vector3.Distance(c.transform.position, at) > 100f) continue;
                    Object.Destroy(c.gameObject);
                    creatures++;
                }
            }
            catch (System.Exception e) { DeLog.Info("DE.S6 teardown creature sweep failed: " + e.GetType().Name + ": " + e.Message); }
            DeLog.Info("DE.S6 teardown (" + why + "): local proxies " + local + ", spawned objects destroyed " + destroyed + ", creatures swept " + creatures);
            return new JsonOut().Num("localProxies", (long)local).Num("spawnedDestroyed", (long)destroyed).Num("creaturesSwept", (long)creatures);
        }

        public void Stop(string why)
        {
            if (_bt1) FinishBt1("stopped: " + why);
            _pen = false;
            _paths = false;
            Phase = "idle";
        }

        // ------------------------------------------------------------------------------------------ clock

        public void Tick(double now)
        {
            if (_bt1) Bt1Tick(now);
            if (_pen && !_bt1 && now >= _penUntil) { _pen = false; if (Phase == "penetration") Phase = "idle"; DeLog.Info("DE.S6 penetration sampler off: " + (Streaks.Pass ? "PASS" : "FAIL") + " max streak " + Streaks.MaxStreakSeen); }
            if (_paths) PathsTick(now);
        }

        public void PerSecond(double now)
        {
            _penRows = 0;
            if (!_pen) return;
            int pairs, streak;
            double depth;
            Streaks.TakeWindow(out pairs, out depth, out streak);
            JsonOut j = S6Rows.Begin(RowPenetration);
            j.Bool("summary", true).Num("t", now - _bt1RunAt).Num("pairs", (long)pairs).Num("maxDepthM", depth).Num("maxStreak", (long)streak).Num("creatureColliders", (long)_creatureCols.Count)
             .Num("violations", Streaks.Violations).Str("phase", Phase);
            S6Rows.End(j);
        }
    }
}
