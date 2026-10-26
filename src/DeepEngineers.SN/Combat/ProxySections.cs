using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DeepEngineers.SN
{
    /// <summary>One entity's proxy (a grid, a subgrid, or the local synthetic B-T1 hull) and its sampled target pose.</summary>
    internal sealed class ProxyEntity
    {
        public ulong EntityId, RootId;
        public bool IsSubgrid, Local, Large;
        public uint TargetClass;
        public uint Version;
        public readonly Dictionary<uint, ProxySection> Sections = new Dictionary<uint, ProxySection>();
        public bool HasTarget;
        public Vec3 O;
        public Quat Q = Quat.Identity;
        public double S = 1.0;
        public SeMatrix Rel = InstanceMath.Identity;
        public ulong Tick;
        /// <summary>Local synthetic proxy: its loop (centre, radius, speed) and start time.</summary>
        public Vec3 LoopCentre;
        public double LoopRadius, LoopSpeed, LoopStart;
    }

    /// <summary>One collider slot of a section: a BoxCollider on the root (level boxes) or on a unit-scale child (rotated boxes).</summary>
    internal sealed class BoxSlot
    {
        public BoxCollider Col;
        public GameObject Child;
        public bool Rotated;
    }

    /// <summary>One proxy section: a unit-scale root GameObject (never a child of the scaled Frame) with its colliders.</summary>
    internal sealed class ProxySection
    {
        public ProxyEntity Owner;
        public SkinSectionData Data;
        public double RhoMax;
        public GameObject Root;
        public Transform T;
        public Rigidbody Body;
        public LiveMixin Live;
        public ProxyMarker Marker;
        public readonly List<BoxSlot> Slots = new List<BoxSlot>();
        public int WantedSlots;
        public Quat PassQ = Quat.Identity;
        public double PassS = 1.0;
        public bool PassIsCreate;
        public bool Applied;
        public double SApplied = 1.0;
        public Vec3 UApplied = Vec3.UnitY;
        public bool HasTarget, HasPrev, Active;
        public Vec3 TargetPos, PrevPos;
        public Quat TargetRot = Quat.Identity, PrevRot = Quat.Identity;
        public double LastMovedSec = double.NegativeInfinity;
        public long Refreshes, Teleports;
    }

    /// <summary>
 /// The grid proxies of CONTRACT §2.6 (S6 SN plugin): one root per SkinSet section (kinematic Rigidbody, layer
 /// Default, speculative CCD, DontDestroyOnLoad, named <c>DE.Proxy &lt;entity x16&gt;/&lt;section x8&gt;</c>) with
 /// LiveMixin + LiveMixinData (invincibleInCreative false, shielded), the zero DamageModifier, an EcoTarget Shark and a
 /// <see cref="ProxyMarker"/>. Skins are diffed by section id and content hash; colliders are created and refreshed
 /// through the 256-shapes-per-step round robin (<see cref="ShapeQueue{T}"/>) with K pre-scaling (Wire
 /// <see cref="WireMath.PrescaleBox"/>) under the 1 cm rule (<see cref="CollisionSkinMath.NeedsRefresh"/>). Targets come
 /// from the mirror's applied pose of the latest camera sample (<see cref="Sample"/>); bodies move in the physics step
 /// (<see cref="Step"/>) with MovePosition/MoveRotation, or teleport beyond 2 m / 30°. Main thread only.
 /// </summary>
    internal sealed class ProxySections
    {
        public const string RowProxy = "s6.proxy";
        public const int MaxRowsPerSecond = 200;

        private readonly Dictionary<ulong, ProxyEntity> _entities = new Dictionary<ulong, ProxyEntity>();
        private readonly Dictionary<int, ProxyMarker> _byLive = new Dictionary<int, ProxyMarker>();
        private readonly Dictionary<int, ProxyMarker> _byRoot = new Dictionary<int, ProxyMarker>();
        private readonly List<ProxySection> _list = new List<ProxySection>();
        public readonly ShapeQueue<ProxySection> Budget = new ShapeQueue<ProxySection>();
        private static LiveMixinData _liveData;
        private Action<ProxySection, int, int> _applySlice;
        private Action<ProxySection> _passDone;
        private int _rowsThisSecond;
        private double _rowSecond;

        // counters (totals, and per-second window for s6.proxy refresh aggregation and s6.perf)
        public long Created, Updated, Removed, Teleports, Refreshes, ShapesCreated, ShapesRefreshed, SkinsApplied;
        public long WindowRefreshes, WindowShapes, WindowCreated;
        /// <summary>The largest |target − body| at a camera sample this second: the collider's lag behind the drawn hull (≈ |v|·Δt).</summary>
        public double WindowLagM;
        public int SectionCount, ShapeCount;

        public ProxySections()
        {
            _applySlice = ApplySlice;
            _passDone = PassDone;
        }

        public IEnumerable<ProxyEntity> Entities { get { return _entities.Values; } }
        public int EntityCount { get { return _entities.Count; } }
        public bool Triggers { get { return S6Mutants.IsOn(S6Mutants.Triggers); } }

        // ------------------------------------------------------------------------------------------ lookups (O(1))

        public void Register(ProxyMarker m, LiveMixin lm, GameObject root)
        {
            if (m == null) return;
            if (lm != null) _byLive[lm.GetInstanceID()] = m;
            if (root != null) _byRoot[root.GetInstanceID()] = m;
        }

        public void Unregister(LiveMixin lm, GameObject root)
        {
            if (lm != null) _byLive.Remove(lm.GetInstanceID());
            if (root != null) _byRoot.Remove(root.GetInstanceID());
        }

        /// <summary>The TakeDamage postfix's lookup (CONTRACT §4.2: an O(1) lookup, not GetComponent).</summary>
        public bool TryGet(LiveMixin lm, out ProxyMarker m)
        {
            m = null;
            if (lm == null) return false;
            return _byLive.TryGetValue(lm.GetInstanceID(), out m) && m != null;
        }

        /// <summary>A section root (or the character root) by its GameObject.</summary>
        public bool TryGetRoot(GameObject go, out ProxyMarker m)
        {
            m = null;
            if (go == null) return false;
            return _byRoot.TryGetValue(go.GetInstanceID(), out m) && m != null;
        }

        // ------------------------------------------------------------------------------------------ skins

 /// <summary>A new complete skin for an entity: sections diffed by id and content hash (CONTRACT §5.4 "Resend rule").</summary>
        public void ApplySkin(ulong entityId, uint version, IList<SkinSectionData> sections, GridRecord grid, bool local)
        {
            ProxyEntity e;
            if (!_entities.TryGetValue(entityId, out e))
            {
                e = new ProxyEntity { EntityId = entityId, RootId = entityId, Local = local };
                _entities[entityId] = e;
            }
            e.Version = version;
            if (grid != null)
            {
                e.TargetClass = grid.TargetClass;
                e.Large = grid.TargetClass == GridClass.LargeGrid;
                e.IsSubgrid = grid.ParentId != 0;
                e.RootId = grid.ParentId != 0 ? grid.ParentId : entityId;
            }
            else if (local) { e.TargetClass = GridClass.LargeGrid; e.Large = true; e.IsSubgrid = false; e.RootId = entityId; }
            var keep = new HashSet<uint>();
            int created = 0, updated = 0, removed = 0;
            foreach (SkinSectionData s in sections)
            {
                keep.Add(s.SectionId);
                ProxySection p;
                if (e.Sections.TryGetValue(s.SectionId, out p))
                {
                    if (p.Data != null && p.Data.ContentHash == s.ContentHash && p.Data.Boxes.Count == s.Boxes.Count) continue;
                    p.Data = s;
                    p.RhoMax = CollisionSkinMath.RhoMax(s);
                    UpdateMarker(p);
                    StartPass(p, false);
                    updated++;
                    Updated++;
                    Row("update", p, null);
                }
                else
                {
                    p = CreateSection(e, s);
                    e.Sections[s.SectionId] = p;
                    StartPass(p, true);
                    created++;
                    Created++;
                    WindowCreated++;
                    Row("create", p, null);
                }
            }
            var gone = new List<uint>();
            foreach (uint id in e.Sections.Keys) if (!keep.Contains(id)) gone.Add(id);
            foreach (uint id in gone) { DestroySection(e.Sections[id], "skin v" + version); e.Sections.Remove(id); removed++; }
            SkinsApplied++;
            Recount();
            DeLog.Info("DE.S6 skin " + DeLog.Hex(entityId) + " v" + version + (local ? " (local synthetic)" : "") + " sections=" + sections.Count +
                       " created=" + created + " updated=" + updated + " removed=" + removed + " subgrid=" + e.IsSubgrid + " class=" + e.TargetClass);
        }

        /// <summary>Re-reads an entity's GridRecord (re-parent, class) without a new skin.</summary>
        public void Reclassify(GridRecord grid)
        {
            ProxyEntity e;
            if (grid == null || !_entities.TryGetValue(grid.Id, out e)) return;
            e.TargetClass = grid.TargetClass;
            e.Large = grid.TargetClass == GridClass.LargeGrid;
            e.IsSubgrid = grid.ParentId != 0;
            e.RootId = grid.ParentId != 0 ? grid.ParentId : grid.Id;
            foreach (ProxySection p in e.Sections.Values) UpdateMarker(p);
        }

        public void Remove(ulong entityId, string why)
        {
            ProxyEntity e;
            if (!_entities.TryGetValue(entityId, out e)) return;
            foreach (ProxySection p in e.Sections.Values) DestroySection(p, why);
            _entities.Remove(entityId);
            Recount();
        }

        public void Clear(string why)
        {
            int n = _entities.Count;
            foreach (ProxyEntity e in _entities.Values) foreach (ProxySection p in e.Sections.Values) DestroySection(p, why);
            _entities.Clear();
            Budget.Clear();
            Recount();
            if (n > 0) DeLog.Info("DE.S6 proxies cleared (" + why + "): " + n + " entities");
        }

        /// <summary>Removes every entity that came from the wire (the local synthetic proxy stays).</summary>
        public int ClearWire(string why)
        {
            var ids = new List<ulong>();
            foreach (ProxyEntity e in _entities.Values) if (!e.Local) ids.Add(e.EntityId);
            foreach (ulong id in ids) Remove(id, why);
            if (ids.Count > 0) DeLog.Info("DE.S6 wire proxies cleared (" + why + "): " + ids.Count + " entities");
            return ids.Count;
        }

        /// <summary>Removes only the local synthetic entities.</summary>
        public int ClearLocal(string why)
        {
            var ids = new List<ulong>();
            foreach (ProxyEntity e in _entities.Values) if (e.Local) ids.Add(e.EntityId);
            foreach (ulong id in ids) Remove(id, why);
            return ids.Count;
        }

        public ProxyEntity Get(ulong id) { ProxyEntity e; return _entities.TryGetValue(id, out e) ? e : null; }

        private static LiveMixinData LiveData()
        {
            if (_liveData != null) return _liveData;
            // CONTRACT §4.2 (VERIFIED LiveMixinData.cs): maxHealth 100, every death/repair path off, every effect null
            LiveMixinData d = ScriptableObject.CreateInstance<LiveMixinData>();
            d.name = "DE.Proxy.LiveMixinData";
            d.hideFlags = HideFlags.DontUnloadUnusedAsset;
            d.maxHealth = 100f;
            d.invincibleInCreative = false;
            d.knifeable = false;
            d.weldable = false;
            d.destroyOnDeath = false;
            d.broadcastKillOnDeath = false;
            d.passDamageDataOnDeath = false;
            d.canResurrect = false;
            d.damageEffect = null;
            d.deathEffect = null;
            d.electricalDamageEffect = null;
            d.loopingDamageEffect = null;
            _liveData = d;
            return d;
        }

        /// <summary>
 /// The components of a section (and of the character and self-check sections): LiveMixin with its data assigned
 /// right after AddComponent (before Start), health = max, invincible false, shielded true; the zero modifier; S6.M12's
 /// forwarder when that mutant is on (added before Start, so it is cached).
 /// </summary>
        public static LiveMixin AddLive(GameObject go)
        {
            LiveMixin lm = go.AddComponent<LiveMixin>();
            lm.data = LiveData();
            lm.health = lm.data.maxHealth;
            lm.invincible = false;
            lm.shielded = true;
            go.AddComponent<ZeroDamageModifier>();
            if (S6Mutants.IsOn(S6Mutants.LateForwarder))
            {
                LateDamageForwarder f = go.AddComponent<LateDamageForwarder>();
                f.Live = lm;
            }
            return lm;
        }

        public static Rigidbody AddBody(GameObject go)
        {
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; // HYPOTHESIS that it helps (S6.B-T1 (1))
            return rb;
        }

        public static void AddEco(GameObject go)
        {
            EcoTarget eco = go.AddComponent<EcoTarget>();
            eco.SetTargetType(EcoTargetType.Shark); // 2030, VERIFIED EcoTargetType.cs
        }

        private ProxySection CreateSection(ProxyEntity e, SkinSectionData s)
        {
            var go = new GameObject("DE.Proxy " + e.EntityId.ToString("x16", CultureInfo.InvariantCulture) + "/" + s.SectionId.ToString("x8", CultureInfo.InvariantCulture));
            go.SetActive(false); // posed and activated at its first target (never collides at the origin)
            go.layer = 0;
            Object.DontDestroyOnLoad(go);
            var p = new ProxySection { Owner = e, Data = s, RhoMax = CollisionSkinMath.RhoMax(s), Root = go, T = go.transform };
            p.Body = AddBody(go);
            p.Live = AddLive(go);
            AddEco(go);
            p.Marker = go.AddComponent<ProxyMarker>();
            p.Marker.Live = p.Live;
            UpdateMarker(p);
            Register(p.Marker, p.Live, go);
            return p;
        }

        private static void UpdateMarker(ProxySection p)
        {
            ProxyMarker m = p.Marker;
            if (m == null) return;
            ProxyEntity e = p.Owner;
            m.EntityId = e.EntityId;
            m.RootEntityId = e.RootId;
            m.SectionId = p.Data.SectionId;
            m.Kind = DamageTargetKind.GridSection;
            m.TargetClass = e.TargetClass;
            m.IsSubgrid = e.IsSubgrid;
            m.Large = e.Large;
            m.Local = e.Local;
            m.GridSize = e.Large ? 2.5 : 0.5;
        }

        private void DestroySection(ProxySection p, string why)
        {
            Budget.Cancel(p);
            Unregister(p.Live, p.Root);
            Removed++;
            Row("remove", p, why);
            if (p.Root != null) Object.Destroy(p.Root);
            p.Root = null;
            p.Slots.Clear();
        }

        private void Recount()
        {
            int sections = 0, shapes = 0;
            _list.Clear();
            foreach (ProxyEntity e in _entities.Values)
                foreach (ProxySection p in e.Sections.Values) { sections++; shapes += p.Slots.Count; if (p.Root != null) _list.Add(p); }
            SectionCount = sections;
            ShapeCount = shapes;
        }

        // ------------------------------------------------------------------------------------------ pose sampling

 /// <summary>
 /// The targets of every section from the mirror's applied pose at this camera sample (CONTRACT §2.6): a root grid
 /// from its view's (o, q, s); a subgrid from its root's (o, q, s) and its own applied C2 row (rel). Called after
 /// MirrorConsumer.ApplyPoses (OnCameraSample hook) or, without a camera sample this frame, from the host's Tick.
 /// </summary>
        public void Sample(MirrorConsumer m, double nowSec)
        {
            Dictionary<ulong, MirrorGridView> views = m != null ? m.ViewMap : null;
            foreach (ProxyEntity e in _entities.Values)
            {
                if (e.Local) SampleLocal(e, nowSec);
                else SampleView(e, views);
                foreach (ProxySection p in e.Sections.Values)
                {
                    if (!e.HasTarget) { p.HasTarget = false; continue; }
                    Vec3 pos = CollisionSkinMath.SectionRootPosition(e.O, e.Q, e.S, e.Rel, p.Data.Centre);
                    Quat rot = CollisionSkinMath.SectionRootRotation(e.Q, e.Rel);
                    if (!pos.IsFinite) { p.HasTarget = false; continue; }
                    // review F5: the anchor pose moves only when the section moved > 1 mm / 0.05° since the anchor (CONTRACT §2.6:
 // "in the last 0.25 s", the rate holds 2 s), so slow motion registers; a per-sample comparison missed anything under 6 cm/s at 60 fps
                    if (p.HasPrev && MotionRule.Moved(p.PrevPos, p.PrevRot, pos, rot)) { p.LastMovedSec = nowSec; p.PrevPos = pos; p.PrevRot = rot; }
                    if (p.Active && p.Body != null)
                    {
                        double lag = Vec3.Distance(pos, D(p.Body.position));
                        if (lag > WindowLagM) WindowLagM = lag;
                    }
                    if (!p.HasPrev) { p.PrevPos = pos; p.PrevRot = rot; p.HasPrev = true; }
                    p.TargetPos = pos; p.TargetRot = rot; p.HasTarget = true;
                }
            }
        }

        private static void SampleView(ProxyEntity e, Dictionary<ulong, MirrorGridView> views)
        {
            e.HasTarget = false;
            if (views == null) return;
            MirrorGridView v;
            if (!views.TryGetValue(e.EntityId, out v) || v == null || !v.Alive || !v.HasPose) return;
            if (!v.IsSubgrid)
            {
                if (!v.LastPose.Finite) return;
                e.O = v.LastPose.O; e.Q = v.LastPose.Q; e.S = v.LastPose.S; e.Rel = InstanceMath.Identity; e.Tick = v.LastTick;
                e.IsSubgrid = false; e.RootId = e.EntityId;
                e.HasTarget = true;
                return;
            }
            MirrorGridView root;
            if (!views.TryGetValue(v.ParentId, out root) || root == null || !root.Alive || !root.HasPose || !root.LastPose.Finite) return;
            SeMatrix rel = SeMatrix.FromRows(v.LastRow, 0);
            if (!(Math.Abs(rel.Determinant - 1.0) < 1e-3)) return;
            e.O = root.LastPose.O; e.Q = root.LastPose.Q; e.S = root.LastPose.S; e.Rel = rel; e.Tick = v.LastTick;
            e.IsSubgrid = true; e.RootId = v.ParentId;
            e.HasTarget = true;
        }

        private static void SampleLocal(ProxyEntity e, double nowSec)
        {
            Vec3 o;
            Quat q;
            LocalLoop.Pose(e.LoopCentre, e.LoopRadius, e.LoopSpeed, nowSec - e.LoopStart, out o, out q);
            e.O = o; e.Q = q; e.S = 1.0; e.Rel = InstanceMath.Identity; e.Tick = (ulong)Math.Max(0, (nowSec - e.LoopStart) * 60.0);
            e.HasTarget = true;
        }

        public bool AnyMovingNear(Vector3 cam, float radius, double nowSec)
        {
            double r2 = (double)radius * radius;
            foreach (ProxyEntity e in _entities.Values)
                foreach (ProxySection p in e.Sections.Values)
                {
                    if (!p.HasTarget || nowSec - p.LastMovedSec > MotionRule.WindowSeconds) continue;
                    double dx = p.TargetPos.X - cam.x, dy = p.TargetPos.Y - cam.y, dz = p.TargetPos.Z - cam.z;
                    if (dx * dx + dy * dy + dz * dz <= r2) return true;
                }
            return false;
        }

        // ------------------------------------------------------------------------------------------ physics step

 /// <summary>
 /// One physics step (the stepper's FixedUpdate, before the game's scripts): bodies to their targets, the step pose
 /// stored on the markers, refresh passes queued under the 1 cm rule, then at most 256 shapes created or refreshed.
 /// </summary>
        public void Step(int shapeBudget)
        {
            foreach (ProxyEntity e in _entities.Values)
                foreach (ProxySection p in e.Sections.Values)
                {
                    if (p.Root == null) continue;
                    if (!p.HasTarget)
                    {
                        if (p.Active) { p.Root.SetActive(false); p.Active = false; }
                        continue;
                    }
                    Vector3 tp = V(p.TargetPos);
                    Quaternion tq = Q(p.TargetRot);
                    if (!p.Active)
                    {
                        p.T.SetPositionAndRotation(tp, tq);
                        p.Body.position = tp;
                        p.Body.rotation = tq;
                        p.Root.SetActive(true);
                        p.Active = true;
                    }
                    else
                    {
                        Vector3 bp = p.Body.position;
                        Quaternion bq = p.Body.rotation;
                        if (MotionRule.Teleport(new Vec3(bp.x, bp.y, bp.z), new Quat(bq.x, bq.y, bq.z, bq.w), p.TargetPos, p.TargetRot))
                        {
                            p.Body.position = tp;
                            p.Body.rotation = tq;
                            p.Teleports++;
                            Teleports++;
                            Row("teleport", p, null);
                        }
                        else
                        {
                            p.Body.MovePosition(tp);
                            p.Body.MoveRotation(tq);
                        }
                    }
                    ProxyMarker m = p.Marker;
                    m.StepO = e.O; m.StepQ = e.Q; m.StepS = e.S; m.StepRel = e.Rel; m.StepTick = e.Tick; m.StepValid = true;
                    m.IsSubgrid = e.IsSubgrid; m.RootEntityId = e.RootId;
                    if (p.Applied && !Budget.Contains(p))
                    {
                        Vec3 u = CollisionSkinMath.UpInSection(p.TargetRot);
                        if (CollisionSkinMath.NeedsRefresh(p.SApplied, p.UApplied, e.S, u, p.RhoMax)) StartPass(p, false);
                    }
                }
            if (Budget.Pending > 0) Budget.Step(shapeBudget, _applySlice, _passDone);
        }

        /// <summary>Starts (or restarts) a create/refresh pass of every box of the section with its current target pose.</summary>
        private void StartPass(ProxySection p, bool create)
        {
            p.PassQ = p.HasTarget ? p.TargetRot : p.Owner.Q;
            p.PassS = p.Owner.HasTarget ? p.Owner.S : 1.0;
            p.PassIsCreate = create || !p.Applied;
            p.WantedSlots = p.Data.Boxes.Count;
            Budget.Enqueue(p, Math.Max(1, p.Data.Boxes.Count));
            if (p.Applied) { p.Refreshes++; Refreshes++; WindowRefreshes++; }
        }

        private void ApplySlice(ProxySection p, int first, int count)
        {
            if (p.Root == null) return;
            bool trig = Triggers;
            int n = p.Data.Boxes.Count;
            for (int i = first; i < first + count && i < n; i++) ApplyBox(p, i, trig);
            WindowShapes += count;
        }

        private void ApplyBox(ProxySection p, int i, bool trigger)
        {
            SkinBoxData b = p.Data.Boxes[i];
            ProxyBox pb = WireMath.PrescaleBox(p.PassQ, p.PassS, p.Data.Centre, b.Centre, b.Half, b.E0, b.E1, b.E2);
            bool rotated = (b.Flags & CombatConventions.SkinBoxFlags.Rotated) != 0;
            while (p.Slots.Count <= i) p.Slots.Add(new BoxSlot());
            BoxSlot slot = p.Slots[i];
            bool fresh = false;
            if (slot.Col == null || slot.Rotated != rotated)
            {
                DestroySlot(slot);
                if (!rotated) slot.Col = p.Root.AddComponent<BoxCollider>();
                else
                {
                    var child = new GameObject("box" + i.ToString(CultureInfo.InvariantCulture));
                    child.layer = 0;
                    child.transform.SetParent(p.T, false);
                    slot.Child = child;
                    slot.Col = child.AddComponent<BoxCollider>();
                }
                slot.Rotated = rotated;
                fresh = true;
            }
            Vector3 size = new Vector3((float)(2 * pb.HalfExtents.X), (float)(2 * pb.HalfExtents.Y), (float)(2 * pb.HalfExtents.Z));
            if (!rotated)
            {
                slot.Col.center = V(pb.Centre);
                slot.Col.size = size;
            }
            else
            {
                Transform ct = slot.Child.transform;
                ct.localPosition = V(pb.Centre);
                ct.localRotation = Q(pb.Rotation);
                ct.localScale = Vector3.one;
                slot.Col.center = Vector3.zero;
                slot.Col.size = size;
            }
            if (slot.Col.isTrigger != trigger) slot.Col.isTrigger = trigger; // S6.M1 (CONTRACT §2.6: non-trigger)
            if (fresh) ShapesCreated++; else ShapesRefreshed++;
        }

        private static void DestroySlot(BoxSlot s)
        {
            if (s.Child != null) Object.Destroy(s.Child);
            else if (s.Col != null) Object.Destroy(s.Col);
            s.Child = null;
            s.Col = null;
        }

        private void PassDone(ProxySection p)
        {
            if (p.Root == null) return;
            for (int i = p.Slots.Count - 1; i >= p.WantedSlots; i--) { DestroySlot(p.Slots[i]); p.Slots.RemoveAt(i); }
            p.SApplied = p.PassS;
            p.UApplied = CollisionSkinMath.UpInSection(p.PassQ);
            p.Applied = true;
            ProxyMarker m = p.Marker;
            m.Colliders.Clear();
            foreach (BoxSlot s in p.Slots) if (s.Col != null) m.Colliders.Add(s.Col);
            Recount();
        }

        /// <summary>S6.M1 switched: every section refreshed (the trigger flag is applied with the boxes).</summary>
        public void RefreshAll()
        {
            foreach (ProxyEntity e in _entities.Values) foreach (ProxySection p in e.Sections.Values) if (p.Root != null) StartPass(p, false);
        }

        /// <summary>Every section (rebuilt on each skin, removal and finished pass; callers skip destroyed roots): no allocation per call.</summary>
        public List<ProxySection> AllSections() { return _list; }

        // ------------------------------------------------------------------------------------------ rows

        private void Row(string op, ProxySection p, string why)
        {
            if (op == "teleport" && !S10Sn.TakeRow(S10Sn.RowProxy)) return; // S10 hook (round 3 E): the teleport rows (97 % of s6.proxy) under the per-second budget; create/update/remove always; telemetry only
            double now = Time.realtimeSinceStartup;
            if (now - _rowSecond >= 1.0) { _rowSecond = now; _rowsThisSecond = 0; }
            if (++_rowsThisSecond > MaxRowsPerSecond) return;
            JsonOut j = S6Rows.Begin(RowProxy);
            j.Str("op", op).Str("entity", S6Rows.Hex(p.Owner.EntityId)).Str("section", S6Rows.Sec(p.Data.SectionId)).Num("kind", (long)DamageTargetKind.GridSection)
             .Num("boxes", (long)p.Data.Boxes.Count).Num("rhoMax", p.RhoMax).Num("s", p.Owner.S).Vec("u", CollisionSkinMath.UpInSection(p.TargetRot))
             .Vec("root", p.TargetPos).Num("tick", p.Owner.Tick).Bool("subgrid", p.Owner.IsSubgrid).Bool("local", p.Owner.Local);
            if (why != null) j.Str("why", why);
            S6Rows.End(j);
        }

        /// <summary>The aggregated per-second row (op "refresh"): refresh passes, creations, shapes handled, the largest lag.</summary>
        public void RefreshRow()
        {
            if (WindowRefreshes == 0 && WindowCreated == 0 && SectionCount == 0) { WindowShapes = 0; WindowLagM = 0; return; }
            JsonOut j = S6Rows.Begin(RowProxy);
            j.Str("op", "refresh").Num("refreshes", WindowRefreshes).Num("created", WindowCreated).Num("shapes", WindowShapes).Num("sections", (long)SectionCount)
             .Num("pendingPasses", (long)Budget.Pending).Num("lagM", WindowLagM).Num("teleports", Teleports);
            S6Rows.End(j);
            WindowRefreshes = 0; WindowCreated = 0; WindowShapes = 0; WindowLagM = 0;
        }

        public static Vector3 V(Vec3 v) { return new Vector3((float)v.X, (float)v.Y, (float)v.Z); }
        public static Quaternion Q(Quat q) { return new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W); }
        public static Vec3 D(Vector3 v) { return new Vec3(v.x, v.y, v.z); }
    }
}
