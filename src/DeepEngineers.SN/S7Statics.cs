using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7 statics reporter (ROADMAP S7 "statics into SE", the second terrain path; CONTRACT §5.4 "C12 static primitives
 /// (S7; S7Statics)"): Subnautica's static colliders around the camera (Aurora, precursor structures, Lost River
 /// trees and skeletons, wreck pieces) streamed into C12 as boxes, capsules, spheres and mesh OBBs per 160 m batch.
 /// <list type="bullet">
 /// <item><b>Scan.</b> Every <c>MinRescanMs</c> (2 s), and soon after a batch root appears in
 /// <c>LargeWorldStreamer.batch2root</c>, every batch within <c>ScanRadiusM</c> (320 m) of the camera is queried with
 /// <c>Physics.OverlapBoxNonAlloc(centre, half, buffer, identity, 1 &lt;&lt; 0, QueryTriggerInteraction.Ignore)</c>; a
 /// query that fills the buffer is split into 8 octants (down to 10 m). Resumable, at most 1 ms per frame. A collider
 /// is evaluated once per Subnautica generation (instance id cache): statics do not move.</item>
 /// <item><b>Filter</b> (<see cref="S7StaticsFilter"/>): enabled, non-trigger, Default layer, under a
 /// <c>LargeWorldEntity</c>, a batch root or a lightmapped prefab scene root, no <c>Creature</c>, <c>Pickupable</c>, <c>Vehicle</c>,
 /// <c>FreezeRigidbodyWhenFar</c>, <c>WorldForces</c>, S6 <c>ProxyMarker</c>, none of our roots ("DE." / "DeepEngineers."),
 /// and no <c>Rigidbody</c> / <c>LiveMixin</c> unless the root's class id is on the census allow-list
 /// (<c>%LOCALAPPDATA%\DeepEngineers\census\allowlist.json</c>, written by <c>tools/sn-octrees/sn_prefab_colliders.py
 /// --census</c>). Seeded failures: S7.M7 (static-or-kinematic), S7.M3 (capsules dropped).</item>
 /// <item><b>Primitives.</b> Box (<c>center</c>, <c>size</c>/2 × <c>lossyScale</c>), Capsule (radius, half segment,
 /// axis), Sphere (radius × max scale), MeshCollider as the OBB of <c>sharedMesh.bounds</c> (MeshObb); centre and
 /// rotation in SN world (w ≥ 0); <c>classIdHash</c> = FNV-1a 32 of the root's class id; flags AllowListed,
 /// SkewApprox (&gt; 1 % shear). A primitive entirely above SN y = 0 is not sent. Assigned to the batch of its centre.</item>
 /// <item><b>Send.</b> Per batch a grow-only set (<see cref="S7StaticsBatchSet"/>: dedup by kind, centre to 1 cm and
 /// class hash; ≤ <c>MaxPerBatch</c>) resent as one <c>BatchStatics</c> with epoch + 1 when it gained primitives; a
 /// full C12 ring re-queues the batch (tried again next frame). <c>BatchClear</c> is never sent (sets only grow;
 /// HYPOTHESIS of the contract: never needed in practice).</item>
 /// <item><b>Census</b> (<c>de.statics {action: census, on}</c>, the census session): every message is also written to
 /// <c>%LOCALAPPDATA%\DeepEngineers\census\batches\&lt;bx&gt;.&lt;by&gt;.&lt;bz&gt;.c12</c> with <c>Census</c> set.</item>
 /// <item><b>C7</b> <c>capabilities</c> bit 2 <c>statics</c> while the reporter runs (<see cref="FillStatus"/>).</item>
 /// <item><b>S7.N5</b> <c>de.statics {action: rays, n}</c>: <c>Physics.Raycast</c> from the camera at reported
 /// primitives; the reply carries every ray (origin, direction, length, the SN hit distance) for SE's
 /// <c>de.statics {action: rays, rays}</c>.</item>
 /// </list>
 /// Rows: <c>s7.statics</c> {snFrame, batch, epoch, primitives, filtered{reason: n}, census, op} per batch sent, per
 /// pass and on <c>status</c>. Keyed by batch, never by "the player" (v3). Main thread only; never throws into the game.
 /// <para>S7 review fixes: F5 the scan, the filter and the N5 rays use one layer mask, every layer the
 /// player collides with minus TerrainCollider and Trigger (<c>Physics.GetIgnoreLayerCollision(LayerID.Player, l)</c>,
 /// the pattern of <c>LayerID.GetAllCollisionsMask</c>; logged with the layer names at start), so the wreck walls on
 /// DiveReel are scanned; F3 the census list <c>allowWorldForces</c> waives WorldForces on kinematic wrecks; F6 the N5
 /// rays aim at the colliders Subnautica has (decision None, Duplicate or MutantCapsule), not at what was sent, and
 /// <c>static</c> means "one of those"; F17 <c>creaturesReported</c> (primitives under a Creature: only S7.M7) and
 /// <c>insideProxy</c> (primitives whose centre lies in an S6 proxy section's box) in the rows; F22 a batch already
 /// sent is resent only after its set stayed unchanged for 2 passes (the first send is immediate).</para>
 /// <para>Members:
 /// <c>LargeWorldStreamer.main</c>, <c>IsReady</c>, <c>batch2root</c> (<c>Dictionary&lt;Int3, LargeWorldBatchRoot&gt;</c>),
 /// <c>GetBatchMins(Int3)</c>; <c>LargeWorldEntity</c>, <c>LargeWorldBatchRoot</c>, <c>UniqueIdentifier.ClassId</c>,
 /// <c>PrefabIdentifier</c>, <c>Creature</c>, <c>Pickupable</c>, <c>Vehicle</c>, <c>FreezeRigidbodyWhenFar</c>,
 /// <c>WorldForces</c>, <c>LiveMixin</c>, <c>MainCamera.camera</c>; Unity <c>Physics.OverlapBoxNonAlloc(Vector3,
 /// Vector3, Collider[], Quaternion, int, QueryTriggerInteraction)</c>, <c>Physics.Raycast(Vector3, Vector3, out
 /// RaycastHit, float, int, QueryTriggerInteraction)</c>, <c>BoxCollider.center/size</c>,
 /// <c>CapsuleCollider.center/radius/height/direction</c>, <c>SphereCollider.center/radius</c>,
 /// <c>MeshCollider.sharedMesh</c>, <c>Mesh.bounds</c>, <c>Collider.isTrigger/enabled/attachedRigidbody</c>,
 /// <c>Rigidbody.isKinematic</c>, <c>Transform.TransformPoint/rotation/lossyScale/localToWorldMatrix</c>.</para>
 /// </summary>
    internal sealed unsafe class S7Statics : IDisposable
    {
        public const string Cmd = "de.statics";
        /// <summary>Colliders per overlap query (a fuller result splits the cell).</summary>
        public const int OverlapBuffer = 2048;
        /// <summary>Smallest cell edge a full query is split down to.</summary>
        public const float MinCellM = 10f;
        public const int StartSplit = 4;   //: a batch starts as 4 x 4 x 4 cells of 40 m
 /// <summary>Frame budget of the scan and the sends (CONTRACT: statics scan ≤ 1 ms per frame).</summary>
        public const double FrameBudgetMs = 1.0;
        /// <summary>A new batch root starts a pass this soon after the last one started (else every MinRescanMs).</summary>
        public const ulong NewBatchPassDelayMs = 500;
        /// <summary>The instance-id cache is cleared beyond this many colliders (bounded memory).</summary>
        public const int SeenCap = 400000;

        private struct Cell
        {
            public Vector3 Centre;
            public float Half;
        }

        private readonly ByteRing _c12;
        private readonly byte* _base;
        private readonly uint _generation;
        private bool _seKnown;
        private uint _seGeneration, _seWorldEpoch, _seLinkEpoch;
        public long Resends;
        private readonly Collider[] _hits = new Collider[OverlapBuffer];
        private readonly Dictionary<long, S7StaticsBatchSet> _sets = new Dictionary<long, S7StaticsBatchSet>();
        private readonly Dictionary<int, byte> _seen = new Dictionary<int, byte>();
        private readonly Dictionary<int, RootInfo> _roots = new Dictionary<int, RootInfo>();
        private readonly Stack<Cell> _work = new Stack<Cell>();
        private readonly List<long> _sendQueue = new List<long>();
        private readonly HashSet<long> _queued = new HashSet<long>();
        private readonly long[] _filtered = new long[S7StaticsFilter.ReasonCount];
        private readonly long[] _passFiltered = new long[S7StaticsFilter.ReasonCount];
        private readonly byte[] _buf = new byte[S7StaticsCodec.MaxPayloadBytes];
        private readonly Stopwatch _sw = new Stopwatch();
        private HashSet<string> _allow = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Review F3: the census list <c>allowWorldForces</c> (kinematic wrecks with a WorldForces script).</summary>
        private HashSet<string> _allowWf = new HashSet<string>(StringComparer.Ordinal);
        private string _allowSource = "not loaded";
        /// <summary>Review F5: the layers the scan, the filter and the rays use (bit l = layer l).</summary>
        private readonly int _mask;
        private readonly string _maskNames;
        /// <summary>Review F6: the colliders Subnautica has (decision None, Duplicate or MutantCapsule) by instance id, for the N5 rays.</summary>
        private readonly Dictionary<int, Collider> _aimable = new Dictionary<int, Collider>();
        /// <summary>Review F22: per batch the primitive count at the last pass and the passes it stayed unchanged.</summary>
        private readonly Dictionary<long, int[]> _growth = new Dictionary<long, int[]>();
        public const int StablePassesBeforeResend = 2;
        /// <summary>Review F17: the S6 proxy sections (set by the bridge: null without S6).</summary>
        private readonly Func<List<ProxySection>> _proxySource;
        private readonly List<Bounds> _proxyBounds = new List<Bounds>();
        private readonly List<Collider> _proxyColliders = new List<Collider>();
        public long CreaturesReported, InsideProxy, ResendsHeld;

        /// hierarchy (the Aurora), dropped from their sets when the landmarks are hidden again (<see cref="S7Landmarks"/>).</summary>
        private readonly Dictionary<long, HashSet<ulong>> _landmarkKeys = new Dictionary<long, HashSet<ulong>>();
        private int _landmarkVersion = -1;
        public long LandmarkSkipped, LandmarkDropped;

        private int _hitCount, _hitIndex;
        private bool _passActive;
        private ulong _passStartMs, _nextPassMs;
        private int _lastRootCount = -1;
        private ulong _lastRootChangeMs;
        private bool _lastM3, _lastM7;
        private bool _disposed, _disabled, _running;
        private string _disabledWhy;
        private bool _census;
        private string _censusDir;
        private Vector3 _passCamera;

        // counters (status and rows)
        public long Passes, CollidersEvaluated, PrimitivesAdded, MessagesSent, MessagesFull, MessagesFailed, CensusFiles, CensusErrors,
                    OverflowCells, Errors;
        private double _lastPassMs, _maxFrameMs;
        private int _lastPassBatches;

        /// <summary>What one entity root contributes (cached per root and generation): world membership, our roots, the class id.</summary>
        private struct RootInfo
        {
            public bool World, Bridge, AllowListed, AllowListedWorldForces;
            public uint ClassHash;
            public string ClassId;
        }

        /// <param name="sectionBase">the mapped bridge section (the C12 producer continues from the head it finds)</param>
 /// <param name="generation">this Subnautica's generation (epochs restart with it; SE closes the old hosts)</param>
 /// <param name="proxySource">review F17: the S6 proxy sections (null: none counted)</param>
        public S7Statics(byte* sectionBase, uint generation, Func<List<ProxySection>> proxySource = null)
        {
            if (sectionBase == null) throw new ArgumentNullException("sectionBase");
            _c12 = SectionLink.C12Producer(sectionBase);
            _base = sectionBase;
            _generation = generation;
            _proxySource = proxySource;
            _lastM3 = S7Mutants.M3;
            _lastM7 = S7Mutants.M7;
            _mask = BuildMask(out _maskNames);
            LoadAllowList();
            DeLog.Info("DE.S7 statics reporter ready: generation " + generation + ", scan " + S7Conventions.StaticsLimits.ScanRadiusM +
                       " m every " + S7Conventions.StaticsLimits.MinRescanMs + " ms, layer mask 0x" + _mask.ToString("x8", CultureInfo.InvariantCulture) +
                       " (" + _maskNames + "), allow-list " + _allowSource);
        }

        /// <summary>
 /// Review F5: every layer the player collides with (<c>!Physics.GetIgnoreLayerCollision(LayerID.Player, l)</c>), minus
 /// <c>LayerID.TerrainCollider</c> and <c>LayerID.Trigger</c> (VERIFIED: static
 /// readonly ints by <c>LayerMask.NameToLayer</c>); the names for the log line. Falls back to the Default layer alone
 /// when Unity answers nothing.
 /// </summary>
        private static int BuildMask(out string names)
        {
            int player = LayerID.Player;
            int mask = S7StaticsFilter.PlayerSolidMask(l => player >= 0 && !Physics.GetIgnoreLayerCollision(player, l), LayerID.TerrainCollider, LayerID.Trigger);
            var sb = new StringBuilder();
            for (int l = 0; l < 32; l++)
            {
                if ((mask & (1 << l)) == 0) continue;
                if (sb.Length > 0) sb.Append(',');
                string n = LayerMask.LayerToName(l);
                sb.Append(string.IsNullOrEmpty(n) ? l.ToString(CultureInfo.InvariantCulture) : n);
            }
            names = sb.ToString();
            return mask;
        }

        /// <summary>True while the reporter runs (C7 capabilities bit 2).</summary>
        public bool Running { get { return _running && !_disabled && !_disposed; } }

        public bool CensusOn { get { return _census; } }

        // ============================================================================================= per frame

 /// <summary>Per frame (main thread), from the S7 host. Never throws.</summary>
        public void Update(ulong nowMs)
        {
            if (_disposed || _disabled) return;
            try { UpdateCore(nowMs); }
            catch (Exception e) when (e is MissingMemberException || e is TypeLoadException)
            {
                // a game or Unity member this build does not have: only the statics feature goes off (CONTRACT §5.4)
                _disabled = true;
                _running = false;
                _disabledWhy = e.GetType().Name + ": " + e.Message;
                DeLog.Warn("DE.S7 statics OFF missing " + e.Message + " (the reporter stops; everything else keeps running)");
            }
            catch (Exception e)
            {
                Errors++;
                DeLog.WarnOnce("s7.statics.update." + e.GetType().Name, "DE.S7 statics update failed (" + e.GetType().Name + ": " + e.Message + "); the reporter keeps running " + e.StackTrace);
            }
        }

        private void UpdateCore(ulong nowMs)
        {
            LargeWorldStreamer lws = LargeWorldStreamer.main;
            if (lws == null || !lws.IsReady()) { _running = false; return; }
            _running = true;
            _sw.Reset();
            _sw.Start();

            bool m3 = S7Mutants.M3, m7 = S7Mutants.M7;
            if (m3 != _lastM3 || m7 != _lastM7)
            {
                // a seeded failure toggled: every collider is evaluated again under the new rule
                _lastM3 = m3; _lastM7 = m7;
                _seen.Clear();
                _roots.Clear();
                _aimable.Clear();
                DeLog.Warn("DE.S7 statics mutants now M3=" + m3 + " M7=" + m7 + ": collider cache cleared");
            }
            if (S7Landmarks.Version != _landmarkVersion)
            {
                // S7-1: the landmarks were hidden or shown: what was sent of them leaves SE, every collider is evaluated again
                _landmarkVersion = S7Landmarks.Version;
                if (S7Landmarks.Hidden) DropLandmarkPrimitives();
                _seen.Clear();
                _roots.Clear();
                _aimable.Clear();
            }

            if (DeLog.Frame % 30 == 0) WatchSe();
            PumpSends(nowMs);

            int roots = lws.batch2root != null ? lws.batch2root.Count : 0;
            if (roots != _lastRootCount) { _lastRootCount = roots; _lastRootChangeMs = nowMs; }
            if (!_passActive)
            {
                bool due = nowMs >= _nextPassMs || (_lastRootChangeMs > _passStartMs && nowMs >= _passStartMs + NewBatchPassDelayMs);
                if (due) StartPass(nowMs);
            }
            if (_passActive) StepPass(m3, m7);
            if (_passActive && _work.Count == 0 && _hitIndex >= _hitCount) EndPass(nowMs);
            double ms = _sw.Elapsed.TotalMilliseconds;
            if (ms > _maxFrameMs) _maxFrameMs = ms;
        }

        private bool OverBudget { get { return _sw.Elapsed.TotalMilliseconds >= FrameBudgetMs; } }

        private void StartPass(ulong nowMs)
        {
            Camera cam = MainCamera.camera;
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            Vector3 p = cam.transform.position;
            _passCamera = p;
            _passActive = true;
            CollectProxies();
            _passStartMs = nowMs;
            _nextPassMs = nowMs + (ulong)S7Conventions.StaticsLimits.MinRescanMs;
            Array.Clear(_passFiltered, 0, _passFiltered.Length);
            _work.Clear();
            _hitCount = _hitIndex = 0;
            var pv = new Vec3(p.x, p.y, p.z);
            double r = S7Conventions.StaticsLimits.ScanRadiusM;
            int bx0, by0, bz0, bx1, by1, bz1;
            S7StaticsMath.BatchOfSn(new Vec3(pv.X - r, pv.Y - r, pv.Z - r), out bx0, out by0, out bz0);
            S7StaticsMath.BatchOfSn(new Vec3(pv.X + r, pv.Y + r, pv.Z + r), out bx1, out by1, out bz1);
            int batches = 0;
            float half = 0.5f * S7Conventions.StaticsLimits.BatchM;
            for (int bx = bx0; bx <= bx1; bx++)
                for (int by = by0; by <= by1; by++)
                    for (int bz = bz0; bz <= bz1; bz++)
                    {
                        if (!S7StaticsMath.InLattice(bx, by, bz)) continue;
                        if (S7StaticsMath.DistanceToBatchSn(pv, bx, by, bz) > r) continue;
                        Vec3 c = S7StaticsMath.BatchCentreSn(bx, by, bz);
                        if (c.Y - half > 0.0) continue; // the batch lies wholly above the sea: nothing would be sent
 // 'DE.S10 slow call: s7 statics took 40-82 ms', ~36 times a session): one overlap
 // query over a whole 160 m batch could cost that much by itself (the 1 ms budget is checked between queries);
 // the batch is queued as StartSplit^3 cells (40 m) so each query stays small. Same coverage, the seen set
 // de-duplicates a collider met by several cells.
                        float sh = half / StartSplit;
                        for (int ix = 0; ix < StartSplit; ix++)
                            for (int iy = 0; iy < StartSplit; iy++)
                                for (int iz = 0; iz < StartSplit; iz++)
                                    _work.Push(new Cell
                                    {
                                        Centre = new Vector3((float)c.X - half + sh * (2 * ix + 1), (float)c.Y - half + sh * (2 * iy + 1), (float)c.Z - half + sh * (2 * iz + 1)),
                                        Half = sh,
                                    });
                        batches++;
                    }
            _lastPassBatches = batches;
        }

        private void StepPass(bool m3, bool m7)
        {
            while (!OverBudget)
            {
                if (_hitIndex < _hitCount)
                {
                    Collider col = _hits[_hitIndex];
                    _hits[_hitIndex] = null;
                    _hitIndex++;
                    if (col == null) continue; // destroyed since the query (Unity null)
                    Evaluate(col, m3, m7);
                    continue;
                }
                if (_work.Count == 0) return;
                Cell cell = _work.Pop();
                int n = Physics.OverlapBoxNonAlloc(cell.Centre, new Vector3(cell.Half, cell.Half, cell.Half), _hits, Quaternion.identity, _mask, QueryTriggerInteraction.Ignore);
                if (n >= _hits.Length && cell.Half > 0.5f * MinCellM)
                {
                    float h = 0.5f * cell.Half;
                    for (int i = 0; i < 8; i++)
                        _work.Push(new Cell { Centre = cell.Centre + new Vector3((i & 1) != 0 ? h : -h, (i & 2) != 0 ? h : -h, (i & 4) != 0 ? h : -h), Half = h });
                    for (int i = 0; i < n; i++) _hits[i] = null;
                    continue;
                }
                if (n >= _hits.Length)
                {
                    OverflowCells++;
                    DeLog.WarnOnce("s7.statics.overflow", "DE.S7 statics: a " + (2 * cell.Half).ToString("0", CultureInfo.InvariantCulture) + " m cell at " + cell.Centre +
                                   " still returns " + n + " colliders (buffer full); the rest of that cell is missed this pass (counted in overflowCells)");
                }
                _hitCount = n;
                _hitIndex = 0;
            }
        }

        private void EndPass(ulong nowMs)
        {
            _passActive = false;
            Passes++;
            _lastPassMs = nowMs - _passStartMs;
            foreach (KeyValuePair<long, S7StaticsBatchSet> kv in _sets)
            {
                S7StaticsBatchSet set = kv.Value;
                int[] g;
                if (!_growth.TryGetValue(kv.Key, out g)) { g = new int[2]; _growth[kv.Key] = g; }
                if (set.Count == g[0]) g[1]++;
                else { g[0] = set.Count; g[1] = 0; }
                if (!set.Dirty || _queued.Contains(kv.Key)) continue;
                // review F22: the first send at once; a batch already sent waits until its set stayed unchanged for 2 passes
 // (a re-send rebuilds SE's whole compound: one rebuild for the approach instead of one per pass)
                if (set.Epoch > 0 && g[1] < StablePassesBeforeResend) { ResendsHeld++; continue; }
                _queued.Add(kv.Key);
                _sendQueue.Add(kv.Key);
            }
            if (_seen.Count > SeenCap) { _seen.Clear(); _roots.Clear(); _aimable.Clear(); }
            if (Passes <= 3 || Passes % 30 == 0) PassRow();
        }

        // ============================================================================================= one collider

        private void Evaluate(Collider col, bool m3, bool m7)
        {
            int id = col.GetInstanceID();
            if (_seen.ContainsKey(id)) return;
            if (S7Landmarks.HiddenUnder(col.transform))
            {
                // S7-1: a collider of a hidden landmark (the Aurora) that is still enabled: never reported; not cached, so it
 // is reported once the landmarks are shown
                LandmarkSkipped++;
                _filtered[(int)S7StaticsReject.Disabled]++;
                _passFiltered[(int)S7StaticsReject.Disabled]++;
                return;
            }
            CollidersEvaluated++;
            S7StaticsReject reject = S7StaticsReject.None;
            var cand = new S7StaticsCandidate { Enabled = col.enabled, Trigger = col.isTrigger, Layer = col.gameObject.layer };
            RootInfo root = default(RootInfo);
            if (cand.Enabled && !cand.Trigger && cand.Layer >= 0 && cand.Layer < 32 && (_mask & (1 << cand.Layer)) != 0)
            {
                root = RootOf(col);
                cand.UnderWorld = root.World;
                cand.Bridge = root.Bridge;
                cand.AllowListed = root.AllowListed;
                cand.AllowListedWorldForces = root.AllowListedWorldForces;
                // the entity types on the collider's own chain of parents (CONTRACT: "its hierarchy passes the filter")
                cand.Proxy = col.GetComponentInParent<ProxyMarker>() != null;
                cand.Creature = col.GetComponentInParent<Creature>() != null;
                cand.Pickupable = col.GetComponentInParent<Pickupable>() != null;
                cand.Vehicle = col.GetComponentInParent<Vehicle>() != null;
                cand.Freeze = col.GetComponentInParent<FreezeRigidbodyWhenFar>() != null;
                cand.WorldForces = col.GetComponentInParent<WorldForces>() != null;
                cand.LiveMixin = col.GetComponentInParent<LiveMixin>() != null;
                Rigidbody rb = col.attachedRigidbody;
                cand.Rigidbody = rb != null;
                cand.RigidbodyKinematic = rb != null && rb.isKinematic;
            }
            reject = S7StaticsFilter.Decide(ref cand, m7, _mask);
            if (reject == S7StaticsReject.None)
            {
                StaticPrimitive p;
                reject = MakePrimitive(col, root, m3, out p);
                if (reject == S7StaticsReject.None)
                {
                    int bx, by, bz;
                    S7StaticsMath.BatchOfSn(new Vec3(p.centreSn.x, p.centreSn.y, p.centreSn.z), out bx, out by, out bz);
                    long key = S7StaticsMath.BatchKey(bx, by, bz);
                    S7StaticsBatchSet set;
                    if (!_sets.TryGetValue(key, out set)) { set = new S7StaticsBatchSet(bx, by, bz); _sets[key] = set; }
                    S7StaticsAdd r = set.Add(ref p);
                    if (r == S7StaticsAdd.Added)
                    {
                        PrimitivesAdded++;
                        if (S7Landmarks.Covers(col.transform))
                        {
                            HashSet<ulong> lk;
                            if (!_landmarkKeys.TryGetValue(key, out lk)) { lk = new HashSet<ulong>(); _landmarkKeys[key] = lk; }
                            lk.Add(S7StaticsMath.DedupKey(ref p));
                        }
                        // review F17: what S7.N9 must never see (a creature only under S7.M7; a static inside a proxy box)
                        if (cand.Creature) CreaturesReported++;
                        if (InProxy(new Vector3(p.centreSn.x, p.centreSn.y, p.centreSn.z))) InsideProxy++;
                    }
                    else if (r == S7StaticsAdd.Duplicate) reject = S7StaticsReject.Duplicate;
                    else
                    {
                        reject = S7StaticsReject.Full;
                        DeLog.WarnOnce("s7.statics.full." + key, "DE.S7 statics batch " + bx + "." + by + "." + bz + " holds " + S7Conventions.StaticsLimits.MaxPerBatch +
                                       " primitives (MaxPerBatch); further primitives of it are not sent (counted as full)");
                    }
                }
            }
            if (reject != S7StaticsReject.None) { _filtered[(int)reject]++; _passFiltered[(int)reject]++; }
            if (reject != S7StaticsReject.Disabled) _seen[id] = (byte)reject;
            // review F6: the N5 rays aim at the colliders Subnautica has (sent, deduplicated, or dropped by S7.M3)
            if (reject == S7StaticsReject.None || reject == S7StaticsReject.Duplicate || reject == S7StaticsReject.MutantCapsule) _aimable[id] = col;
        }

        /// <summary>Review F6: the decisions that make a collider one of Subnautica's statics for S7.N5.</summary>
        private static bool IsSubnauticaStatic(byte decision)
        {
            return decision == (byte)S7StaticsReject.None || decision == (byte)S7StaticsReject.Duplicate || decision == (byte)S7StaticsReject.MutantCapsule;
        }

        /// <summary>Review F17: the S6 proxy sections' box colliders, once per pass (none without S6).</summary>
        private void CollectProxies()
        {
            _proxyBounds.Clear();
            _proxyColliders.Clear();
            if (_proxySource == null) return;
            List<ProxySection> list;
            try { list = _proxySource(); } catch (Exception) { return; }
            if (list == null) return;
            foreach (ProxySection s in list)
            {
                if (s == null || s.Root == null || !s.Root.activeInHierarchy) continue;
                foreach (BoxSlot slot in s.Slots)
                {
                    if (slot == null || slot.Col == null || !slot.Col.enabled) continue;
                    _proxyBounds.Add(slot.Col.bounds);
                    _proxyColliders.Add(slot.Col);
                }
            }
        }

        /// <summary>Review F17: a point inside one of the proxy section boxes (bounds first, then the exact box).</summary>
        private bool InProxy(Vector3 p)
        {
            for (int i = 0; i < _proxyBounds.Count; i++)
            {
                if (!_proxyBounds[i].Contains(p)) continue;
                Collider c = _proxyColliders[i];
                if (c == null) continue;
                if ((c.ClosestPoint(p) - p).sqrMagnitude < 1e-6f) return true;
            }
            return false;
        }

        /// <summary>
 /// The entity root of a collider: its <c>LargeWorldEntity</c>, else its nearest <c>PrefabIdentifier</c> (an object
 /// placed under a batch root), else the collider itself; cached per root object for this generation.
 /// </summary>
        private RootInfo RootOf(Collider col)
        {
            LargeWorldEntity lwe = col.GetComponentInParent<LargeWorldEntity>();
            UniqueIdentifier uid = lwe != null ? lwe.GetComponent<PrefabIdentifier>() : null;
            if (uid == null) uid = col.GetComponentInParent<PrefabIdentifier>();
            Transform keyT = lwe != null ? lwe.transform : uid != null ? uid.transform : col.transform.root;
            int key = keyT.GetInstanceID();
            RootInfo info;
            if (_roots.TryGetValue(key, out info)) return info;
            info.World = lwe != null || col.GetComponentInParent<LargeWorldBatchRoot>() != null;
            string topName = col.transform.root.name ?? "";
            info.Bridge = topName.StartsWith("DE.", StringComparison.Ordinal) || topName.StartsWith("DeepEngineers.", StringComparison.Ordinal);
            info.ClassId = uid != null ? uid.ClassId : null;
            if (!info.World && lwe == null)
            {
                // a lightmapped prefab scene's main object (the Aurora): world, class id "scene:<name>"
                string sceneId = S7StaticsMath.LightmappedSceneClassId(topName);
                if (sceneId != null)
                {
                    info.World = true;
                    if (info.ClassId == null) info.ClassId = sceneId;
                }
            }
            info.ClassHash = S7StaticsMath.ClassIdHash(info.ClassId);
            info.AllowListed = info.ClassId != null && _allow.Contains(info.ClassId);
            info.AllowListedWorldForces = info.ClassId != null && _allowWf.Contains(info.ClassId);
            _roots[key] = info;
            return info;
        }

        private static S7StaticsReject MakePrimitive(Collider col, RootInfo root, bool m3, out StaticPrimitive p)
        {
            p = default(StaticPrimitive);
            Transform t = col.transform;
            Vector3 ls = t.lossyScale;
            var scale = new Vec3(ls.x, ls.y, ls.z);
            Vector3 centre;
            Vec3 size;
            byte kind;
            var box = col as BoxCollider;
            var cap = col as CapsuleCollider;
            var sph = col as SphereCollider;
            var mesh = col as MeshCollider;
            if (box != null)
            {
                kind = StaticKind.Box;
                centre = t.TransformPoint(box.center);
                Vector3 s = box.size;
                size = S7StaticsMath.BoxHalf(new Vec3(s.x, s.y, s.z), scale);
            }
            else if (cap != null)
            {
                kind = StaticKind.Capsule;
                S7StaticsReject k = S7StaticsFilter.KindAllowed(kind, m3);
                if (k != S7StaticsReject.None) return k;
                centre = t.TransformPoint(cap.center);
                size = S7StaticsMath.CapsuleSize(cap.radius, cap.height, cap.direction, scale);
            }
            else if (sph != null)
            {
                kind = StaticKind.Sphere;
                centre = t.TransformPoint(sph.center);
                size = new Vec3(S7StaticsMath.SphereRadius(sph.radius, scale), 0, 0);
            }
            else if (mesh != null)
            {
                Mesh m = mesh.sharedMesh;
                if (m == null) return S7StaticsReject.Degenerate;
                kind = StaticKind.MeshObb;
                Bounds b = m.bounds;
                centre = t.TransformPoint(b.center);
                size = S7StaticsMath.MeshHalf(new Vec3(b.extents.x, b.extents.y, b.extents.z), scale);
            }
            else return S7StaticsReject.Degenerate; // terrain or wheel colliders

            Quaternion q = t.rotation;
            Quat wq = S7StaticsMath.WireRotation(new Quat(q.x, q.y, q.z, q.w));
            byte flags = 0;
            if (root.AllowListed) flags |= S7Conventions.StaticFlags.AllowListed;
            Matrix4x4 m4 = t.localToWorldMatrix;
            Vector4 c0 = m4.GetColumn(0), c1 = m4.GetColumn(1), c2 = m4.GetColumn(2);
            if (S7StaticsMath.IsSkewed(new Vec3(c0.x, c0.y, c0.z), new Vec3(c1.x, c1.y, c1.z), new Vec3(c2.x, c2.y, c2.z))) flags |= S7Conventions.StaticFlags.SkewApprox;
            p.kind = kind;
            p.flags = flags;
            p.centreSn = new F32x3(centre.x, centre.y, centre.z);
            p.qSn = new F32x4((float)wq.X, (float)wq.Y, (float)wq.Z, (float)wq.W);
            p.size = new F32x3((float)size.X, (float)size.Y, (float)size.Z);
            p.classIdHash = root.ClassHash;
            if (S7StaticsCodec.Check(ref p) != S7StaticsStatus.Ok) return S7StaticsReject.Degenerate;
            if (S7StaticsMath.EntirelyAboveSea(ref p)) return S7StaticsReject.AboveSea;
            return S7StaticsReject.None;
        }

        // ============================================================================================= SE's state

 /// <summary>
 /// A new SE generation (SE restarted), world epoch (another world) or link epoch (a stall re-synced SE's C12 tail:
 /// messages in flight are gone) means SE holds none of the batches any more: every non-empty set is sent again
 /// (epoch + 1; SE's newest-epoch rule accepts it). Read from the header and C5 every 30 frames.
 /// </summary>
 /// <summary>
 /// S7-1 landmark toggle: every batch that holds primitives of a landmark hierarchy is rebuilt without them and sent
 /// at once. The grow-only set has no removal, so a new set continues the old one's epoch (<c>NextEpoch</c> up to it,
 /// Wire <c>S7StaticsBatchSet</c>) and is re-queued: its next message carries epoch + 1, which SE's newest-epoch rule
 /// accepts (<c>S7StaticsMath.Supersedes</c>); an emptied batch goes out empty (SE's host for it then holds nothing).
 /// </summary>
        private void DropLandmarkPrimitives()
        {
            if (_landmarkKeys.Count == 0) return;
            int batches = 0, dropped = 0;
            foreach (KeyValuePair<long, HashSet<ulong>> kv in _landmarkKeys)
            {
                S7StaticsBatchSet old;
                if (!_sets.TryGetValue(kv.Key, out old)) continue;
                var ns = new S7StaticsBatchSet(old.Bx, old.By, old.Bz);
                for (uint e = 0; e < old.Epoch; e++) ns.NextEpoch();
                StaticPrimitive[] items = old.Items;
                for (int i = 0; i < old.Count; i++)
                {
                    if (kv.Value.Contains(S7StaticsMath.DedupKey(ref items[i]))) { dropped++; continue; }
                    ns.Add(ref items[i]);
                }
                ns.Requeue();
                _sets[kv.Key] = ns;
                _growth.Remove(kv.Key);
                if (_queued.Add(kv.Key)) _sendQueue.Add(kv.Key);
                batches++;
            }
            _landmarkKeys.Clear();
            LandmarkDropped += dropped;
            DeLog.Info("DE.S7 statics: landmarks hidden: " + dropped + " primitive(s) of them dropped from " + batches + " batch(es), sent again");
        }

        private void WatchSe()
        {
            var hv = new HeaderView(_base);
            uint gen = hv.SeGeneration;
            SeState st = default(SeState);
            uint seq;
            if (Seqlock.TryReadBytes(Layout.RegionPtr(_base, RegionId.C5SeState), (byte*)&st, sizeof(SeState), Seqlock.MaxTries, out seq) != SeqlockReadStatus.Fresh) return;
            if (_seKnown && gen == _seGeneration && st.worldEpoch == _seWorldEpoch && st.linkEpoch == _seLinkEpoch) return;
            bool first = !_seKnown;
            string why = first ? null : "SE generation " + _seGeneration + "->" + gen + ", world epoch " + _seWorldEpoch + "->" + st.worldEpoch + ", link epoch " + _seLinkEpoch + "->" + st.linkEpoch;
            _seKnown = true;
            _seGeneration = gen; _seWorldEpoch = st.worldEpoch; _seLinkEpoch = st.linkEpoch;
            if (first) return;
            int n = 0;
            foreach (KeyValuePair<long, S7StaticsBatchSet> kv in _sets)
            {
                if (kv.Value.Count == 0) continue;
                kv.Value.Requeue();
                if (_queued.Add(kv.Key)) _sendQueue.Add(kv.Key);
                n++;
            }
            Resends++;
            DeLog.Info("DE.S7 statics: " + why + ": " + n + " batch(es) sent again");
        }

        // ============================================================================================= sends

        private void PumpSends(ulong nowMs)
        {
            int i = 0;
            while (i < _sendQueue.Count && !OverBudget)
            {
                long key = _sendQueue[i];
                S7StaticsBatchSet set;
                if (!_sets.TryGetValue(key, out set) || !set.Dirty) { _queued.Remove(key); _sendQueue.RemoveAt(i); continue; }
                set.NextEpoch();
                int n = set.Encode(_buf, false);
                RingWriteStatus st = _c12.TryWrite(C12Type.BatchStatics, _buf, 0, n);
                if (st == RingWriteStatus.Full)
                {
                    set.Requeue(); // tried once; the batch stays queued (CONTRACT: C12 full → re-queue)
                    MessagesFull++;
                    i++;
                    continue;
                }
                _queued.Remove(key);
                _sendQueue.RemoveAt(i);
                if (st != RingWriteStatus.Ok)
                {
                    MessagesFailed++;
                    DeLog.WarnOnce("s7.statics.write." + st, "DE.S7 statics C12 write " + st + " for batch " + set.Bx + "." + set.By + "." + set.Bz + " (" + n + " B)");
                    continue;
                }
                MessagesSent++;
                if (_census) WriteCensus(set);
                BatchRow("sent", set, n);
            }
        }

        private void WriteCensus(S7StaticsBatchSet set)
        {
            try
            {
                if (_censusDir == null) _censusDir = S7StaticsMath.CensusBatchesFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                Directory.CreateDirectory(_censusDir);
                int n = set.Encode(_buf, true);
                string path = Path.Combine(_censusDir, S7StaticsMath.CensusFileName(set.Bx, set.By, set.Bz));
                string tmp = path + ".tmp";
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) f.Write(_buf, 0, n);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                CensusFiles++;
            }
            catch (Exception e)
            {
                CensusErrors++;
                DeLog.WarnOnce("s7.statics.census." + e.GetType().Name, "DE.S7 statics census write failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ============================================================================================= allow-list

 /// <summary>
 /// Reads <c>%LOCALAPPDATA%\DeepEngineers\census\allowlist.json</c> ({"allow": [classId, …], "allowWorldForces":
 /// [classId, …]}); absent → empty.
 /// </summary>
        public void LoadAllowList()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var wf = new HashSet<string>(StringComparer.Ordinal);
            _allowWf = wf;
            string path = Path.Combine(S7StaticsMath.CensusFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), S7StaticsMath.AllowListFileName);
            try
            {
                if (!File.Exists(path))
                {
                    _allow = set;
                    _allowSource = "absent (" + path + "): Rigidbody/LiveMixin objects are not reported until the census tool ran";
                    DeLog.Warn("DE.S7 statics allow-list " + _allowSource);
                    return;
                }
                var root = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                object v;
                var list = root != null && root.TryGetValue("allow", out v) ? v as List<object> : null;
                if (list == null) throw new FormatException("no \"allow\" array");
                foreach (object o in list) { var s = o as string; if (!string.IsNullOrEmpty(s)) set.Add(s); }
                var wfl = root.TryGetValue("allowWorldForces", out v) ? v as List<object> : null;
                if (wfl != null) foreach (object o in wfl) { var s = o as string; if (!string.IsNullOrEmpty(s)) wf.Add(s); }
                _allow = set;
                _allowWf = wf;
                _allowSource = set.Count + " class id(s) (" + wf.Count + " with the WorldForces waiver) from " + path;
                _roots.Clear();
                _seen.Clear();
                _aimable.Clear();
            }
            catch (Exception e)
            {
                _allow = set;
                _allowSource = "unreadable (" + e.GetType().Name + ": " + e.Message + ")";
                DeLog.Warn("DE.S7 statics allow-list " + _allowSource);
            }
        }

        // ============================================================================================= C7, commands, rows

 /// <summary>C7: <c>capabilities</c> bit 2 <c>statics</c> while the reporter runs.</summary>
        public void FillStatus(ref SnStatus s)
        {
            if (Running) s.capabilities |= Capabilities.Statics;
        }

        /// <summary>C11 <c>de.statics</c> (dev builds): status | census on/off | rays n | allowlist (reload).</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Cmd) return false;
            reply.Str("cmd", req.Cmd);
            try
            {
                Dictionary<string, object> a = S3Args.ToDict(req.Args);
                string action = S3Args.Str(a, "action", "status").Trim().ToLowerInvariant();
                if (action == "census on" || action == "census off" || action == "census")
                {
                    bool on = action == "census on";
                    if (action == "census")
                    {
                        object v;
                        bool b;
                        if ((a.TryGetValue("on", out v) || a.TryGetValue("state", out v)) && S3Args.TryBool(v, out b)) on = b;
                        else { reply.Bool("ok", false).Str("error", "de.statics census: {on: true|false}"); return true; }
                    }
                    SetCensus(on);
                    Status(reply.Bool("ok", true));
                    return true;
                }
                switch (action)
                {
                    case "status":
                        Status(reply.Bool("ok", true));
                        PassRow();
                        return true;
                    case "allowlist":
                        LoadAllowList();
                        Status(reply.Bool("ok", true));
                        return true;
                    case "prims":
                    {
                        // the reported primitives near the camera (S7.N9: no reaper class, none inside a proxy box)
                        double range = Math.Max(1, Math.Min(1000, S3Args.Num(a, "range", 120)));
                        int max = (int)Math.Max(1, Math.Min(5000, S3Args.Num(a, "max", 2000)));
                        reply.Bool("ok", true).Raw("prims", PrimsJson(range, max));
                        Status(reply);
                        return true;
                    }
                    case "rays":
                    {
                        int n = (int)Math.Max(1, Math.Min(1000, S3Args.Num(a, "n", 200)));
                        double range = Math.Max(5, Math.Min(300, S3Args.Num(a, "range", 60)));
                        int seed = (int)S3Args.Num(a, "seed", 1);
                        string error;
                        string rays = Rays(n, range, seed, out error);
                        if (rays == null) { reply.Bool("ok", false).Str("error", error); return true; }
                        reply.Bool("ok", true).Raw("rays", rays);
                        Status(reply);
                        return true;
                    }
                    default:
                        reply.Bool("ok", false).Str("error", "de.statics: action status | census on | census off | rays {n, range, seed} | prims {range, max} | allowlist");
                        return true;
                }
            }
            catch (Exception e)
            {
                reply.Bool("ok", false).Str("error", "de.statics failed: " + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        private void SetCensus(bool on)
        {
            if (on == _census) return;
            _census = on;
            DeLog.Warn("DE.S7 statics census " + (on ? "ON: every batch message is also written to " + S7StaticsMath.CensusBatchesFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) : "OFF"));
            if (on)
            {
                // the batches already sent are written too (their sets are complete as far as streamed)
                foreach (S7StaticsBatchSet s in _sets.Values) if (s.Epoch > 0 && !s.Dirty) WriteCensus(s);
            }
        }

        private void Status(JsonObjectWriter w)
        {
            int prims = 0, sentBatches = 0;
            foreach (S7StaticsBatchSet s in _sets.Values) { prims += s.Count; if (s.Epoch > 0) sentBatches++; }
            w.Bool("running", Running).Num("generation", (long)_generation).Num("batches", (long)_sets.Count).Num("sentBatches", (long)sentBatches)
             .Num("primitives", (long)prims).Num("passes", Passes).Num("evaluated", CollidersEvaluated).Num("sent", MessagesSent)
             .Num("full", MessagesFull).Num("failed", MessagesFailed).Num("resends", Resends).Num("queued", (long)_sendQueue.Count).Bool("census", _census)
             .Num("censusFiles", CensusFiles).Num("censusErrors", CensusErrors).Num("overflowCells", OverflowCells).Num("errors", Errors)
             .Num("lastPassMs", _lastPassMs).Num("lastPassBatches", (long)_lastPassBatches).Num("maxFrameMs", _maxFrameMs)
             .Str("allowList", _allowSource).Bool("mutantM3", S7Mutants.M3).Bool("mutantM7", S7Mutants.M7)
             .Num("layerMask", (long)_mask).Str("layers", _maskNames).Num("creaturesReported", CreaturesReported).Num("insideProxy", InsideProxy)
             .Num("proxyBoxes", (long)_proxyBounds.Count).Num("resendsHeld", ResendsHeld)
             .Bool("landmarksHidden", S7Landmarks.Hidden).Num("landmarkSkipped", LandmarkSkipped).Num("landmarkDropped", LandmarkDropped)
             .Raw("filtered", FilteredJson(_filtered));
            if (_disabled) w.Str("disabled", _disabledWhy);
        }

        private static string FilteredJson(long[] counts)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            bool first = true;
            for (int i = 1; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                Json.AppendString(sb, S7StaticsFilter.ReasonName((S7StaticsReject)i));
                sb.Append(':').Append(counts[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.Append('}').ToString();
        }

        private void BatchRow(string op, S7StaticsBatchSet set, int bytes)
        {
            JsonOut j = S5Rows.Begin("s7.statics");
            j.Str("op", op).Num("snFrame", DeLog.Frame).Arr("batch").Num(null, (long)set.Bx).Num(null, (long)set.By).Num(null, (long)set.Bz).EndArr()
             .Num("epoch", (ulong)set.Epoch).Num("primitives", (long)set.Count).Num("bytes", (long)bytes).Bool("census", _census)
             .Num("generation", (ulong)_generation).Num("overflow", set.Overflow)
             .Num("creaturesReported", CreaturesReported).Num("insideProxy", InsideProxy);
            int boxes = 0, caps = 0, sph = 0, mesh = 0, allow = 0, skew = 0;
            StaticPrimitive[] it = set.Items;
            for (int i = 0; i < set.Count; i++)
            {
                switch (it[i].kind)
                {
                    case StaticKind.Box: boxes++; break;
                    case StaticKind.Capsule: caps++; break;
                    case StaticKind.Sphere: sph++; break;
                    default: mesh++; break;
                }
                if ((it[i].flags & S7Conventions.StaticFlags.AllowListed) != 0) allow++;
                if ((it[i].flags & S7Conventions.StaticFlags.SkewApprox) != 0) skew++;
            }
            j.Obj("kinds").Num("box", (long)boxes).Num("capsule", (long)caps).Num("sphere", (long)sph).Num("meshObb", (long)mesh).EndObj()
             .Num("allowListed", (long)allow).Num("skewApprox", (long)skew);
            // the class hashes in the batch (S7.N9: the reaper's must never appear), at most 64 listed
            var cls = new Dictionary<uint, int>();
            for (int i = 0; i < set.Count; i++) { int c; cls.TryGetValue(it[i].classIdHash, out c); cls[it[i].classIdHash] = c + 1; }
            j.Num("classCount", (long)cls.Count).Obj("classes");
            int listed = 0;
            foreach (KeyValuePair<uint, int> kv in cls)
            {
                if (listed++ >= 64) break;
                j.Num(kv.Key.ToString(CultureInfo.InvariantCulture), (long)kv.Value);
            }
            j.EndObj();
            S5Rows.End(j);
        }

        private void PassRow()
        {
            JsonOut j = S5Rows.Begin("s7.statics");
            int prims = 0;
            foreach (S7StaticsBatchSet s in _sets.Values) prims += s.Count;
            j.Str("op", "pass").Num("snFrame", DeLog.Frame).Num("passes", Passes).Num("batchesScanned", (long)_lastPassBatches)
             .Num("batches", (long)_sets.Count).Num("primitives", (long)prims).Num("evaluated", CollidersEvaluated).Num("lastPassMs", _lastPassMs)
             .Num("maxFrameMs", _maxFrameMs).Bool("census", _census).Num("generation", (ulong)_generation)
             .Vec("cameraSn", _passCamera.x, _passCamera.y, _passCamera.z)
             .Num("creaturesReported", CreaturesReported).Num("insideProxy", InsideProxy).Num("proxyBoxes", (long)_proxyBounds.Count)
             .Num("layerMask", (long)_mask).Num("resendsHeld", ResendsHeld)
             .Bool("landmarksHidden", S7Landmarks.Hidden).Num("landmarkSkipped", LandmarkSkipped).Num("landmarkDropped", LandmarkDropped);
            j.Obj("filtered");
            for (int i = 1; i < _filtered.Length; i++) if (_filtered[i] != 0) j.Num(S7StaticsFilter.ReasonName((S7StaticsReject)i), _filtered[i]);
            j.EndObj();
            j.Obj("filteredLastPass");
            for (int i = 1; i < _passFiltered.Length; i++) if (_passFiltered[i] != 0) j.Num(S7StaticsFilter.ReasonName((S7StaticsReject)i), _passFiltered[i]);
            j.EndObj();
            S5Rows.End(j);
        }

        // ============================================================================================= S7.N5 rays

 /// <summary>
 /// n rays from the camera at Subnautica's static colliders within <paramref name="range"/> m (nearest first, cycled):
 /// review F6, the colliders whose decision is None, Duplicate or MutantCapsule (what Subnautica has, not what was
 /// sent: under S7.M3 the capsules are aimed at and SE misses them), each aimed at a deterministic point inside the
 /// collider's primitive (seeded), cast with <c>Physics.Raycast</c> on the reporter's layer mask (triggers ignored),
 /// length = distance to the aim point + 20 m. <c>static</c> = the first hit is one of those colliders. Returns the
 /// JSON array for the reply: {o, d, len, hit, dSn, static, decision, cls, kind, aim}; o, d in SN world.
 /// </summary>
        private string Rays(int n, double range, int seed, out string error)
        {
            error = null;
            Camera cam = MainCamera.camera;
            if (cam == null) cam = Camera.main;
            if (cam == null) { error = "no camera"; return null; }
            Vector3 o = cam.transform.position;
            var near = new List<KeyValuePair<double, StaticPrimitive>>();
            var nearDecision = new Dictionary<int, byte>();
            foreach (KeyValuePair<int, Collider> kv in _aimable)
            {
                Collider col = kv.Value;
                if (col == null || !col.enabled) continue;
                Bounds cb = col.bounds;
                if ((cb.center - o).magnitude - cb.extents.magnitude > range) continue; // cheap: far colliders skip the primitive
                byte decision;
                if (!_seen.TryGetValue(kv.Key, out decision) || !IsSubnauticaStatic(decision)) continue;
                StaticPrimitive p;
                if (MakePrimitive(col, RootOf(col), false, out p) != S7StaticsReject.None) continue; // the geometry, capsules included
                var c = new Vector3(p.centreSn.x, p.centreSn.y, p.centreSn.z);
                double d = (c - o).magnitude;
                if (d <= range && d > 0.5)
                {
                    p.flags = decision; // the decision rides in the flags slot of this local copy (never sent)
                    near.Add(new KeyValuePair<double, StaticPrimitive>(d, p));
                }
            }
            if (near.Count == 0) { error = "no Subnautica static collider within " + range + " m of the camera (wait for a pass, or move closer)"; return null; }
            near.Sort((x, y) => x.Key.CompareTo(y.Key));
            var rnd = new System.Random(seed);
            var sb = new StringBuilder(n * 160);
            sb.Append('[');
            int hits = 0, statics = 0;
            for (int i = 0; i < n; i++)
            {
                StaticPrimitive p = near[i % near.Count].Value;
                var q = new Quaternion(p.qSn.x, p.qSn.y, p.qSn.z, p.qSn.w);
                Vector3 local;
                if (p.kind == StaticKind.Box || p.kind == StaticKind.MeshObb)
                    local = new Vector3((float)(rnd.NextDouble() - 0.5) * p.size.x, (float)(rnd.NextDouble() - 0.5) * p.size.y, (float)(rnd.NextDouble() - 0.5) * p.size.z);
                else if (p.kind == StaticKind.Capsule)
                {
                    Vector3 ax = p.size.z == 0f ? Vector3.right : p.size.z == 1f ? Vector3.up : Vector3.forward;
                    local = ax * ((float)(rnd.NextDouble() * 2 - 1) * p.size.y);
                }
                else local = Vector3.zero;
                Vector3 aim = new Vector3(p.centreSn.x, p.centreSn.y, p.centreSn.z) + q * local;
                Vector3 dir = aim - o;
                float dist = dir.magnitude;
                if (dist < 1e-3f) continue;
                dir /= dist;
                float len = dist + 20f;
                RaycastHit hit;
                bool h = Physics.Raycast(o, dir, out hit, len, _mask, QueryTriggerInteraction.Ignore);
                bool isStatic = false;
                if (h && hit.collider != null)
                {
                    byte decision;
                    isStatic = _seen.TryGetValue(hit.collider.GetInstanceID(), out decision) && IsSubnauticaStatic(decision);
                    hits++;
                    if (isStatic) statics++;
                }
                if (i > 0) sb.Append(',');
                sb.Append("{\"i\":").Append(i.ToString(CultureInfo.InvariantCulture));
                AppendVec(sb, "o", o); AppendVec(sb, "d", dir); AppendVec(sb, "aim", aim);
                sb.Append(",\"len\":"); Json.AppendNumber(sb, len);
                sb.Append(",\"hit\":").Append(h ? "true" : "false");
                sb.Append(",\"dSn\":"); if (h) Json.AppendNumber(sb, hit.distance); else sb.Append("null");
                sb.Append(",\"static\":").Append(isStatic ? "true" : "false");
                sb.Append(",\"decision\":"); Json.AppendString(sb, S7StaticsFilter.ReasonName((S7StaticsReject)p.flags));
                sb.Append(",\"kind\":").Append(((int)p.kind).ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"cls\":").Append(p.classIdHash.ToString(CultureInfo.InvariantCulture));
                if (h && hit.collider != null) { sb.Append(",\"hitName\":"); Json.AppendString(sb, hit.collider.name); }
                sb.Append('}');
            }
            sb.Append(']');
            JsonOut j = S5Rows.Begin("s7.statics");
            j.Str("op", "rays").Num("snFrame", DeLog.Frame).Num("n", (long)n).Num("candidates", (long)near.Count).Num("hits", (long)hits)
             .Num("staticHits", (long)statics).Num("range", range).Vec("cameraSn", o.x, o.y, o.z);
            S5Rows.End(j);
            return sb.ToString();
        }

        /// <summary>{batch, kind, flags, c, q, size, cls} of every reported primitive within <paramref name="range"/> m of the camera (nearest first).</summary>
        private string PrimsJson(double range, int max)
        {
            Camera cam = MainCamera.camera;
            if (cam == null) cam = Camera.main;
            Vector3 o = cam != null ? cam.transform.position : Vector3.zero;
            var near = new List<KeyValuePair<double, KeyValuePair<S7StaticsBatchSet, int>>>();
            foreach (S7StaticsBatchSet s in _sets.Values)
                for (int i = 0; i < s.Count; i++)
                {
                    StaticPrimitive p = s.Items[i];
                    double d = (new Vector3(p.centreSn.x, p.centreSn.y, p.centreSn.z) - o).magnitude;
                    if (d <= range) near.Add(new KeyValuePair<double, KeyValuePair<S7StaticsBatchSet, int>>(d, new KeyValuePair<S7StaticsBatchSet, int>(s, i)));
                }
            near.Sort((x, y) => x.Key.CompareTo(y.Key));
            var sb = new StringBuilder(Math.Min(near.Count, max) * 160 + 2);
            sb.Append('[');
            for (int k = 0; k < near.Count && k < max; k++)
            {
                S7StaticsBatchSet s = near[k].Value.Key;
                StaticPrimitive p = s.Items[near[k].Value.Value];
                if (k > 0) sb.Append(',');
                sb.Append("{\"batch\":[").Append(s.Bx.ToString(CultureInfo.InvariantCulture)).Append(',').Append(s.By.ToString(CultureInfo.InvariantCulture))
                  .Append(',').Append(s.Bz.ToString(CultureInfo.InvariantCulture)).Append("],\"kind\":").Append(((int)p.kind).ToString(CultureInfo.InvariantCulture))
                  .Append(",\"flags\":").Append(((int)p.flags).ToString(CultureInfo.InvariantCulture));
                AppendVec(sb, "c", new Vector3(p.centreSn.x, p.centreSn.y, p.centreSn.z));
                sb.Append(",\"q\":["); Json.AppendNumber(sb, p.qSn.x); sb.Append(','); Json.AppendNumber(sb, p.qSn.y); sb.Append(',');
                Json.AppendNumber(sb, p.qSn.z); sb.Append(','); Json.AppendNumber(sb, p.qSn.w); sb.Append(']');
                AppendVec(sb, "size", new Vector3(p.size.x, p.size.y, p.size.z));
                sb.Append(",\"cls\":").Append(p.classIdHash.ToString(CultureInfo.InvariantCulture)).Append(",\"d\":");
                Json.AppendNumber(sb, near[k].Key);
                sb.Append('}');
            }
            return sb.Append(']').ToString();
        }

        private static void AppendVec(StringBuilder sb, string name, Vector3 v)
        {
            sb.Append(",\"").Append(name).Append("\":[");
            Json.AppendNumber(sb, v.x); sb.Append(','); Json.AppendNumber(sb, v.y); sb.Append(','); Json.AppendNumber(sb, v.z);
            sb.Append(']');
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
            _sendQueue.Clear();
            _queued.Clear();
            DeLog.Info("DE.S7 statics reporter stopped: " + MessagesSent + " message(s) sent, " + PrimitivesAdded + " primitive(s), " + Passes + " pass(es)");
        }
    }
}
