using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>One read of SE's block build pass (<see cref="GhostReader.TryRead"/>).</summary>
    internal struct GhostSample
    {
        /// <summary>The build pass holds a block: <c>BlockCreationIsActivated</c> ∧ a definition ∧ <c>SpaceDefault.Enabled</c>.</summary>
        public bool Active;
        public bool Enabled, Shown, Allowed;
        /// <summary><c>SpaceDefault.m_worldMatrixAdd</c>: the block frame (rotation of the block, translation its centre).</summary>
        public MatrixD World;
        /// <summary><c>SpaceDefault.m_blockDefinition ?? CurrentBlockDefinition</c>.</summary>
        public MyCubeBlockDefinition Def;
        /// <summary>grid (the gizmo targets a block), voxel, dynamic (<c>DynamicMode</c>); logs only.</summary>
        public string Mode;
    }

    /// <summary>
 /// Reads SE's block-placement gizmo
 /// <c>MyCubeBuilder.Static</c> (public static field), <c>BlockCreationIsActivated</c> (public bool),
 /// <c>CurrentBlockDefinition</c> (public override MyCubeBlockDefinition), <c>DynamicMode</c> (public bool), and the one
 /// reflected member, <c>protected MyCubeBuilderGizmo m_gizmo</c> (a cached <see cref="FieldInfo"/>); then the public
 /// <c>MyCubeBuilderGizmo.SpaceDefault</c> (<c>MyGizmoSpaceProperties</c>: <c>bool Enabled</c>, <c>MatrixD
 /// m_worldMatrixAdd</c>, <c>bool m_buildAllowed</c>, <c>bool m_showGizmoCube</c>, <c>MyCubeBlockDefinition
 /// m_blockDefinition</c>, <c>MySlimBlock m_removeBlock</c>). A missing <c>m_gizmo</c> logs
 /// <c>DE.S7a ghost OFF missing MyCubeBuilder.m_gizmo</c> once and disables the ghost alone. Update thread.
 /// </summary>
    internal static class GhostReader
    {
        private static FieldInfo _gizmo;
        private static bool _bound;

        /// <summary>The reflected member is missing (or of another type): the ghost stays off for this process.</summary>
        public static bool Missing { get; private set; }
        public static string MissingWhy { get; private set; }

        /// <summary>Caches <c>MyCubeBuilder.m_gizmo</c> once; false (logged once) when it is missing.</summary>
        public static bool Bind()
        {
            if (_bound) return !Missing;
            _bound = true;
            try
            {
                FieldInfo f = typeof(MyCubeBuilder).GetField("m_gizmo", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f == null) MissingWhy = "missing MyCubeBuilder.m_gizmo";
                else if (!typeof(MyCubeBuilderGizmo).IsAssignableFrom(f.FieldType)) MissingWhy = "MyCubeBuilder.m_gizmo is a " + f.FieldType.FullName + ", not MyCubeBuilderGizmo";
                else _gizmo = f;
            }
            catch (Exception e) { MissingWhy = "MyCubeBuilder.m_gizmo not reflected (" + e.GetType().Name + ": " + e.Message + ")"; }
            if (_gizmo == null)
            {
                Missing = true;
                DeLog.Info("DE.S7a ghost OFF " + MissingWhy + " (the placement ghost alone is disabled; everything else runs)");
                return false;
            }
            return true;
        }

        /// <summary>The cached gizmo of a build pass (null when unbound or missing).</summary>
        internal static MyCubeBuilderGizmo Gizmo(MyCubeBuilder b)
        {
            return _gizmo != null && b != null ? _gizmo.GetValue(b) as MyCubeBuilderGizmo : null;
        }

        public static bool TryRead(out GhostSample s)
        {
            s = default(GhostSample);
            if (!Bind()) return false;
            MyCubeBuilder b = MyCubeBuilder.Static;
            if (b == null) { s.Mode = "none"; return true; }
            MyCubeBuilderGizmo gz = _gizmo.GetValue(b) as MyCubeBuilderGizmo;
            MyCubeBuilderGizmo.MyGizmoSpaceProperties sp = gz != null ? gz.SpaceDefault : null;
            bool activated = b.BlockCreationIsActivated;
            MyCubeBlockDefinition def = sp != null && sp.m_blockDefinition != null ? sp.m_blockDefinition : b.CurrentBlockDefinition;
            s.Def = def;
            s.Enabled = sp != null && sp.Enabled;
            s.Active = activated && def != null && s.Enabled;
            if (sp != null)
            {
                s.World = sp.m_worldMatrixAdd;
                s.Shown = sp.m_showGizmoCube && GhostGizmoPatch.DrawnRecently();   // review F8: SE drew the add preview this frame
                s.Allowed = sp.m_buildAllowed;
            }
            s.Mode = b.DynamicMode ? "dynamic" : (sp != null && sp.m_removeBlock != null ? "grid" : "voxel");
            return true;
        }

        /// <summary>The gizmo's pose and flags only (the C2 write, PrepareForDraw postfix: the frame SE draws).</summary>
        public static bool TryReadPose(out MatrixD world, out bool shown, out bool allowed)
        {
            world = MatrixD.Identity;
            shown = allowed = false;
            if (_gizmo == null) return false;
            MyCubeBuilder b = MyCubeBuilder.Static;
            if (b == null || !b.BlockCreationIsActivated) return false;
            MyCubeBuilderGizmo gz = _gizmo.GetValue(b) as MyCubeBuilderGizmo;
            MyCubeBuilderGizmo.MyGizmoSpaceProperties sp = gz != null ? gz.SpaceDefault : null;
            if (sp == null || !sp.Enabled) return false;
            world = sp.m_worldMatrixAdd;
            shown = sp.m_showGizmoCube && GhostGizmoPatch.DrawnRecently();   // review F8
            allowed = sp.m_buildAllowed;
            return true;
        }
    }

    /// <summary>
 /// Review F8: <c>m_showGizmoCube</c> and <c>m_worldMatrixAdd</c> go stale when <c>MyCubeBuilder.UpdateGizmo</c>
 /// runs with <c>add = false</c>. A
 /// postfix on <c>protected virtual void UpdateGizmo(MyGizmoSpaceProperties gizmoSpace, bool add, bool remove, bool draw)</c>
 /// records <c>add &amp;&amp; draw</c> for <c>SpaceDefault</c> with a timestamp; the ghost counts as shown only when that
 /// was true within the last 50 ms (this frame or the previous). A missing member or a failed patch logs once and the
 /// readers keep SE's <c>m_showGizmoCube</c> alone.
 /// </summary>
    internal static class GhostGizmoPatch
    {
        private const string Id = "deepengineers.se.s7a.ghost";
        private static bool _patched;
        private static bool _addDraw;
        private static long _stamp;
        public static bool Active { get { return _patched; } }
        public static long Calls;

        public static void Apply()
        {
            try
            {
                MethodInfo m = AccessTools.Method(typeof(MyCubeBuilder), "UpdateGizmo",
                    new[] { typeof(MyCubeBuilderGizmo.MyGizmoSpaceProperties), typeof(bool), typeof(bool), typeof(bool) });
                if (m == null)
                {
                    DeLog.Info("DE.S7a ghost: MyCubeBuilder.UpdateGizmo(MyGizmoSpaceProperties, bool, bool, bool) not found; shown keeps m_showGizmoCube (review F8 off)");
                    return;
                }
                var harmony = new Harmony(Id);
                harmony.Patch(m, prefix: null,
                    postfix: new HarmonyMethod(typeof(GhostGizmoPatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)),
                    transpiler: null, finalizer: null);
                _patched = true;
                DeLog.Info("DE.Harmony SE s7a ghost: MyCubeBuilder.UpdateGizmo postfix (review F8: shown = add && draw this frame)");
            }
            catch (Exception e)
            {
                DeLog.Info("DE.S7a ghost: UpdateGizmo postfix failed (" + e.GetType().Name + ": " + e.Message + "); shown keeps m_showGizmoCube");
            }
        }

        private static void Postfix(MyCubeBuilder __instance, MyCubeBuilderGizmo.MyGizmoSpaceProperties gizmoSpace, bool add, bool remove, bool draw)
        {
            try
            {
                MyCubeBuilderGizmo gz = GhostReader.Gizmo(__instance);
                if (gz == null || !ReferenceEquals(gizmoSpace, gz.SpaceDefault)) return;
                Calls++;
                // S7a-2: in dynamic mode (a new grid in the air; a small block over a large grid) SE draws the
 // preview through UpdateGizmo_DynamicMode with add=false: drawn too
                _addDraw = draw && (add || __instance.DynamicMode);
                _stamp = Stopwatch.GetTimestamp();
            }
            catch (Exception) { }
        }

        /// <summary>SE drew the add preview of SpaceDefault within the last 50 ms; true when the patch is off (SE's flag alone decides).</summary>
        public static bool DrawnRecently()
        {
            if (!_patched) return true;
            return _addDraw && Stopwatch.GetTimestamp() - _stamp <= Stopwatch.Frequency / 20;
        }
    }

    /// <summary>
 /// SE's block-placement ghost mirrored into Subnautica (ROADMAP S7a "Placement ghost"; CONTRACT §5.4 "S7a
 /// conventions", "Placement ghost"). Every producer tick (<see cref="GridMirrorProducer"/>'s S5 grid pass, after the
 /// grid loop) it reads the gizmo (<see cref="GhostReader"/>); while the build pass holds a block and the S5 stream is up
 /// it streams ONE synthetic one-part grid (<see cref="S7aGhost.EntityId"/>, GridAdd flag <c>Ghost</c>) through the
 /// existing C4 records (GridAdd and EntityRemove lane 1, PartAdd and PartRemove lane 2) with the block's model,
 /// requested over C3 like any S5 model (or, for armor without a block model, its <c>MyCubeGrid.GetCubeParts</c>
 /// parts), and a C2 entity row written first every tick (pose = <c>m_worldMatrixAdd</c> mapped as a root grid's,
 /// re-read at the C2 write so it is the frame SE draws; flags ghostShown / ghostAllowed). It is removed (EntityRemove)
 /// at the first tick the build pass is off, the definition is gone, the switch is off, the stream is down or the camera
 /// left the mirror zones. The ghost is never an entry of <see cref="GridMirrorProducer.Grids"/>, so S5 writes no
 /// s5.grid / s5.edit row and S6 no SkinSet for it; the consumer audit counts it as one entity (<see cref="AuditView"/>).
 /// Rows: <c>s7a.ghost</c> on every change and at 10 Hz while active (pose, model key, allowed, latency in SE ticks to
 /// the first C7 presentedSeTick at or past each pose change). Switch <c>DEEPENGINEERS_GHOST</c> / <c>de.s7 ghost</c>;
 /// seeded failure S7a.M4 (<see cref="S7aGhostMutants"/>). Update thread; never throws into SE.
 /// </summary>
    internal static class PlacementGhost
    {
        private static bool _enabled = GhostSwitch.FromEnvironment();
        private static readonly GhostPlan Plan = new GhostPlan();
        private static readonly GhostLatency Latency = new GhostLatency();
        private static readonly List<GhostOp> _ops = new List<GhostOp>();
        private static readonly List<GhostPartSpec> _parts = new List<GhostPartSpec>();
        private static readonly List<string> _cubeModels = new List<string>();
        private static readonly List<MatrixD> _cubeMatrices = new List<MatrixD>();
        private static readonly List<Vector3> _cubeNormals = new List<Vector3>();
        private static readonly List<Vector4UByte> _cubePatterns = new List<Vector4UByte>();

        private static MyCubeBlockDefinition _partsDef;
        private static string _partsWhy;
        private static Vec3 _half = new Vec3(1.25, 1.25, 1.25);
        private static uint _boundEpoch;
        private static bool _idTaken;
        private static long _gridAddTicket = -1;
        private static ulong _tick;

        // the pose of the last active tick (the C2 write re-reads the gizmo)
        private static bool _hasPose;
        private static MatrixD _world;
        private static bool _shown, _allowed;
        // the true m_buildAllowed (S7a.M4 forces only _allowed, the C2 flag); rows carry it as buildAllowed for S7a.N3.colour
        private static bool _buildAllowed;
        private static string _mode, _defText, _cubeSize;
        private static ulong _modelKey;
        private static string _model;

        // C2 bookkeeping
        private static bool _prevValid;
        private static MatrixD _prevWorld;
        private static bool _lastPoseValid;
        private static WireEntityPose _lastPose;
        private static SeMatrix _lastSe;
        private static long _c2Rows, _c2Skipped;

        // rows
        private static GhostRowData _lastRow;
        private static bool _rowWritten;
        private static ulong _nextRowMs;
        private static string _inactiveWhy = "not started";
        private static long _errors;
        private static ulong _errorLogMs;

        /// <summary>The switch (env <c>DEEPENGINEERS_GHOST</c>, default on; <c>de.s7 {ghost: "on"|"off"}</c>).</summary>
        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                DeLog.Info("DE.S7a ghost switch " + (value ? "ON" : "OFF") + (value ? "" : " (removed at the next producer tick)"));
            }
        }

        /// <summary>The ghost's records are in the stream (its GridAdd queued since the last removal or restart).</summary>
        public static bool Alive { get { return Plan.Alive; } }

        // ============================================================================================= bind

 /// <summary>
 /// Binds the ghost for the current world: the cached <c>m_gizmo</c> member (once per process) and the id check
 /// <c>MyEntityIdentifier.ExistsById(GhostEntityId)</c> (VERIFIED <c>public static bool ExistsById(long)</c>); a taken
 /// id logs <c>DE.S7a ghost OFF id taken</c> and keeps the ghost off in this world.
 /// </summary>
        public static void Bind()
        {
            GhostReader.Bind();
            _idTaken = false;
            try
            {
                if (VRage.MyEntityIdentifier.ExistsById((long)S7aGhost.EntityId))
                {
                    _idTaken = true;
                    DeLog.Info("DE.S7a ghost OFF id taken (0x" + S7aGhost.EntityId.ToString("x16", CultureInfo.InvariantCulture) + " is an SE entity in this world)");
                }
            }
            catch (Exception e) { DeLog.Info("DE.S7a ghost id check failed (" + e.GetType().Name + ": " + e.Message + "); assumed free"); }
            DeLog.Info("DE.S7a ghost bound: id=0x" + S7aGhost.EntityId.ToString("x16", CultureInfo.InvariantCulture) + " switch=" + (_enabled ? "on" : "off") +
                       " reader=" + (GhostReader.Missing ? "OFF (" + GhostReader.MissingWhy + ")" : "ok") + (_idTaken ? " ID TAKEN" : ""));
        }

        // ============================================================================================= per tick

 /// <summary>
 /// The producer's tick (update thread, S5 grid pass, after the grid loop): reads the gizmo, queues the records the
 /// plan asks for, notes the C7 presented tick for the latency and writes the s7a.ghost row.
 /// <paramref name="streamUp"/> = the S5 stream is up and the C5 zone allows mirrored entities (CORE, TILE VOID,
 /// OUTER VOID).
 /// </summary>
        public static void Tick(ulong tick, ulong nowMs, uint worldEpoch, bool streamUp)
        {
            try { TickCore(tick, nowMs, worldEpoch, streamUp); }
            catch (Exception e)
            {
                _errors++;
                if (nowMs >= _errorLogMs)
                {
                    _errorLogMs = nowMs + 10000;
                    DeLog.Info("DE.Error S7a ghost #" + _errors + " " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        private static void TickCore(ulong tick, ulong nowMs, uint worldEpoch, bool streamUp)
        {
            if (worldEpoch != _boundEpoch)
            {
                _boundEpoch = worldEpoch;
                Bind();
            }
            _tick = tick;
            Latency.Resolve(tick, GridMirrorProducer.SnPresentedTick, null);

            string why = null;
            var s = default(GhostSample);
            if (!_enabled) why = "switch off";
            else if (GhostReader.Missing) why = GhostReader.MissingWhy;
            else if (_idTaken) why = "id taken";
            else if (!streamUp) why = "stream down or outside the mirror zones";
            else if (!GhostReader.TryRead(out s)) why = "gizmo not readable";
            else if (MyCubeBuilder.Static == null) why = "no cube builder";
            else if (!s.Active) why = s.Def == null ? "no block definition" : (!s.Enabled ? "gizmo space disabled" : "builder off");

            if (why == null && !ReferenceEquals(s.Def, _partsDef)) BuildParts(s.Def);
            if (why == null && _parts.Count == 0) why = "no model (" + (_partsWhy ?? "?") + ")";

            _ops.Clear();
            if (why == null)
            {
                Plan.Active(S7aGhost.TargetClassOf(s.Def.CubeSize == MyCubeSize.Large), _parts, _ops);
                _hasPose = true;
                _world = s.World;
                _shown = s.Shown;
                _allowed = s.Allowed || S7aGhostMutants.AllowedAlways;
                _buildAllowed = s.Allowed;
                _mode = s.Mode;
            }
            else
            {
                Plan.Inactive(_ops);
                _hasPose = false;
                _prevValid = false;
                _lastPoseValid = false;
            }
            _inactiveWhy = why;
            Send(nowMs, worldEpoch);
            Row(nowMs, why);
        }

        /// <summary>The definition's parts: the block model (key 1) or, without a block model, its cube parts (keys 1…32).</summary>
        private static void BuildParts(MyCubeBlockDefinition def)
        {
            _parts.Clear();
            _partsDef = def;
            _partsWhy = null;
            _modelKey = 0;
            _model = null;
            _defText = def != null ? def.Id.ToString() : null;
            _cubeSize = def != null ? def.CubeSize.ToString() : null;
            if (def == null) { _partsWhy = "no definition"; return; }
            MyDefinitionManager dm = MyDefinitionManager.Static;
            float cube = dm != null ? dm.GetCubeSize(def.CubeSize) : (def.CubeSize == MyCubeSize.Large ? 2.5f : 0.5f);
            float original = dm != null ? dm.GetCubeSizeOriginal(def.CubeSize) : cube;
            float scale = original > 0 ? cube / original : 1f;
            _half = new Vec3(def.Size.X * cube * 0.5, def.Size.Y * cube * 0.5, def.Size.Z * cube * 0.5);
            if (!string.IsNullOrEmpty(def.Model))
            {
                ulong key = S3Keys.ModelKey(def.Model, 0, scale);
                _parts.Add(GhostPartSpec.ModelPart(key, def.Model, scale, def.ModelOffset.X, def.ModelOffset.Y, def.ModelOffset.Z));
                _modelKey = key;
                _model = def.Model;
                return;
            }
            // armor without a block model: its cube parts at the block frame (CONTRACT: GetCubeParts(def, Vector3I.Zero,
 // Matrix.Identity, gridSize, …, topologyCheck: true); VERIFIED public static on MyCubeGrid)
            MyCubeGrid.GetCubeParts(def, Vector3I.Zero, Matrix.Identity, cube, _cubeModels, _cubeMatrices, _cubeNormals, _cubePatterns, true);
            int n = Math.Min(_cubeModels.Count, S7aGhost.MaxParts);
            var rows = new float[12];
            for (int i = 0; i < n; i++)
            {
                string asset = _cubeModels[i];
                if (string.IsNullOrEmpty(asset)) continue;
                MatrixD m = _cubeMatrices[i];
                rows[0] = (float)m.M11; rows[1] = (float)m.M12; rows[2] = (float)m.M13;
                rows[3] = (float)m.M21; rows[4] = (float)m.M22; rows[5] = (float)m.M23;
                rows[6] = (float)m.M31; rows[7] = (float)m.M32; rows[8] = (float)m.M33;
                rows[9] = (float)m.M41; rows[10] = (float)m.M42; rows[11] = (float)m.M43;
                ulong key = S3Keys.ModelKey(asset, 0, scale);
                _parts.Add(GhostPartSpec.CubePart((ulong)(i + 1), key, asset, scale, rows));
                if (_modelKey == 0) { _modelKey = key; _model = asset; }
            }
            if (_cubeModels.Count > S7aGhost.MaxParts)
                DeLog.Info("DE.S7a ghost " + def.Id + ": " + _cubeModels.Count + " cube parts, the first " + S7aGhost.MaxParts + " mirrored");
            if (_parts.Count == 0) _partsWhy = "neither a block model nor cube parts";
        }

        /// <summary>The plan's records into C4 (lane 1 GridAdd / EntityRemove, lane 2 parts); models requested like S5's.</summary>
        private static void Send(ulong nowMs, uint worldEpoch)
        {
            const long id = (long)S7aGhost.EntityId;
            foreach (GhostOp op in _ops)
            {
                switch (op.Kind)
                {
                    case GhostOpKind.GridAdd:
                        _gridAddTicket = GridMirrorProducer.EnqueueControl(
                            new OutMessage(C4Type.GridAdd, ModelCodec.ToBytes(S7aGhost.MakeGridAdd(op.TargetClass, worldEpoch)), 0, "GridAdd ghost") { Entity = id });
                        break;
                    case GhostOpKind.PartAdd:
                    {
                        var p = new PartSnapshot { PartKey = op.Part.PartKey, ModelKey = op.Part.ModelKey, AssetName = op.Part.AssetName, ModelScale = op.Part.Scale, Local = op.Part.Local, Flags = op.Part.Flags };
                        GridMirrorProducer.EnsureModelFor(nowMs, p);
                        GridMirrorProducer.EnqueueLane(C4Lanes.NearEdit, new OutMessage(C4Type.PartAdd, ModelCodec.ToBytes(op.Part.ToRecord()), 0, "PartAdd ghost") { Entity = id });
                        break;
                    }
                    case GhostOpKind.PartRemove:
                        GridMirrorProducer.EnqueueLane(C4Lanes.NearEdit, S3Codec.PartRemoveMsg(id, op.PartKey));
                        break;
                    case GhostOpKind.EntityRemove:
                        GridMirrorProducer.PurgeC4(id);
                        GridMirrorProducer.EnqueueControl(S3Codec.EntityRemoveMsg(id));
                        _gridAddTicket = -1;
                        Latency.Reset();
                        break;
                }
            }
        }

        // ============================================================================================= C2

 /// <summary>The ghost's C2 row (API form; the producer calls the overload with the C2 tick).</summary>
        public static bool TryGetC2Row(ref SeEntity row) { return TryGetC2Row(ref row, _tick); }

        /// <summary>
 /// The ghost's C2 entity row for this SE frame (the C2 writer puts it first, never dropped): kind 1, flags
 /// ghostShown | ghostAllowed, groupId 0, (o, q, s) = the block frame mapped as a root grid's pose (P* = the point of
 /// the block's box nearest to the camera), k 1, v = ω = 0, partsVersion = the ghost's part serial, skinVersion 0.
 /// The gizmo is re-read here (the frame SE draws), the last tick's sample otherwise. False while the ghost is not
 /// in the stream or its GridAdd is not written yet.
 /// </summary>
        public static bool TryGetC2Row(ref SeEntity row, ulong tick)
        {
            try
            {
                if (!Plan.Alive || !_hasPose || _gridAddTicket < 0 || !GridMirrorProducer.IsC4Done(_gridAddTicket)) { _c2Skipped++; return false; }
                SiteMap site = SiteBinding.Map;
                MyCamera camera = MySector.MainCamera;
                if (site == null || camera == null) { _c2Skipped++; return false; }
                MatrixD w = _world;
                bool shown = _shown, allowed = _allowed, buildAllowed = _buildAllowed;
                MatrixD fw;
                bool fs, fa;
                if (GhostReader.TryReadPose(out fw, out fs, out fa))
                {
                    w = fw;
                    shown = fs;
                    allowed = fa || S7aGhostMutants.AllowedAlways;
                    buildAllowed = fa;
                }
                SeMatrix m = FrameWriter.ToSe(w);
                Vector3D ct = camera.WorldMatrix.Translation;
                Vec3 pivot = WireMath.PivotNearest(m, new Vec3(-_half.X, -_half.Y, -_half.Z), _half, new Vec3(ct.X, ct.Y, ct.Z));
                WireEntityPose pose = PoseMath.FromSe(site, m, pivot, 1.0);
                row = new SeEntity
                {
                    entityId = S7aGhost.EntityId,
                    kind = SeEntityKind.RootGrid,
                    flags = S7aGhost.C2Flags(shown, allowed),
                    groupId = 0,
                    v = new F32x3(0f, 0f, 0f),
                    omega = new F32x3(0f, 0f, 0f),
                    partsVersion = Plan.PartsVersion,
                    skinVersion = 0,
                };
                PoseMath.WriteWire(ref row, pose);
                if (!_prevValid || !SameMatrix(w, _prevWorld))
                {
                    Latency.NoteChange(tick);
                    _prevWorld = w;
                    _prevValid = true;
                }
                _shown = shown;
                _allowed = allowed;
                _buildAllowed = buildAllowed;
                _lastPose = pose;
                _lastSe = m;
                _lastPoseValid = true;
                _c2Rows++;
                return true;
            }
            catch (Exception e)
            {
                _errors++;
                ulong now = Win32.TickCount64();
                if (now >= _errorLogMs)
                {
                    _errorLogMs = now + 10000;
                    DeLog.Info("DE.Error S7a ghost C2 row #" + _errors + " " + e.GetType().Name + ": " + e.Message);
                }
                return false;
            }
        }

        private static bool SameMatrix(MatrixD a, MatrixD b)
        {
            return a.M11 == b.M11 && a.M12 == b.M12 && a.M13 == b.M13 && a.M21 == b.M21 && a.M22 == b.M22 && a.M23 == b.M23 &&
                   a.M31 == b.M31 && a.M32 == b.M32 && a.M33 == b.M33 && a.M41 == b.M41 && a.M42 == b.M42 && a.M43 == b.M43;
        }

        // ============================================================================================= lifecycle

 /// <summary>The stream restarted (ClearAll, C4 cleared): the ghost is resent from its GridAdd at the next active tick.</summary>
        public static void OnStreamRestart()
        {
            Plan.OnStreamRestart();
            _gridAddTicket = -1;
            _prevValid = false;
            _lastPoseValid = false;
            Latency.Reset();
        }

        /// <summary>The world went away (or its epoch changed): everything forgotten, the id checked again in the next world.</summary>
        public static void OnWorldUnload(string why)
        {
            OnStreamRestart();
            _parts.Clear();
            _partsDef = null;
            _hasPose = false;
            _boundEpoch = 0;
            _idTaken = false;
        }

        /// <summary>The consumer audit's view: +1 entity once its GridAdd is written; false while its records or models are not settled.</summary>
        public static bool AuditView(ref uint entities, ref uint expectPending)
        {
            if (!Plan.Alive) return true;
            if (_gridAddTicket < 0 || !GridMirrorProducer.IsC4Done(_gridAddTicket)) return false;
            uint failed = 0;
            foreach (GhostPartSpec p in Plan.Parts)
            {
                bool sent;
                int st = GridMirrorProducer.ModelStateOf(p.ModelKey, out sent);
                if (st == 2) failed++;
                else if (st != 1 || !sent) return false;
            }
            entities++;
            expectPending += failed;
            return true;
        }

        // ============================================================================================= rows and commands

        private static void Row(ulong nowMs, string why)
        {
            GhostRowData d = Snapshot(why);
            bool changed = !_rowWritten || GhostRows.Changed(d, _lastRow);
            if (!changed && !(d.Active && nowMs >= _nextRowMs)) return;
            _rowWritten = true;
            _lastRow = d;
            _nextRowMs = nowMs + GhostRows.ActiveRowMs;
            d.LatencyP95 = Latency.P95; // sorted only for a written row
            Jsonl.WriteLineBuffered(GhostRows.Row(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), d));
        }

        private static GhostRowData Snapshot(string why)
        {
            bool active = why == null;
            var d = new GhostRowData
            {
                Tick = _tick, Enabled = _enabled, Active = active, Shown = active && _shown, Allowed = active && _allowed, BuildAllowed = active && _buildAllowed,
                Def = active ? _defText : null, Model = active ? _model : null, ModelKey = active ? _modelKey : 0, CubeSize = active ? _cubeSize : null,
                Mode = active ? _mode : null, Parts = active ? _parts.Count : 0, PartsVersion = Plan.PartsVersion, Reason = why,
                SeWorld = active ? FrameWriter.ToSe(_world) : default(SeMatrix),
                GridAdds = Plan.GridAdds, PartAdds = Plan.PartAdds, PartRemoves = Plan.PartRemoves, EntityRemoves = Plan.EntityRemoves,
                LatencyTicks = Latency.Last, LatencyP95 = -1, LatencyMax = Latency.Max, LatencySamples = Latency.Samples,
                PresentedSeTick = GridMirrorProducer.SnPresentedTick, MutantAllowed = S7aGhostMutants.AllowedAlways,
            };
            if (active && _lastPoseValid)
            {
                d.HasPose = true;
                d.SeWorld = _lastSe;
                d.SnO = _lastPose.O;
                d.SnQ = _lastPose.Q.WithNonNegativeW();
                d.SnS = _lastPose.S;
            }
            return d;
        }

        /// <summary>de.s7 ghost: on, off, stop (DeactivateBlockCreation), probe {block} (Activate), status.</summary>
        public static Dictionary<string, object> Command(IDictionary<string, object> args)
        {
            string block, error;
            GhostArgs.Verb verb = GhostArgs.Parse(args, out block, out error);
            try
            {
                switch (verb)
                {
                    case GhostArgs.Verb.On: Enabled = true; return Reply(true, "DE.S7a ghost on");
                    case GhostArgs.Verb.Off: Enabled = false; return Reply(true, "DE.S7a ghost off (removed at the next producer tick)");
                    case GhostArgs.Verb.Stop:
                    {
                        MyCubeBuilder b = MyCubeBuilder.Static;
                        if (b == null) return Reply(false, "DE.S7a ghost stop: no MyCubeBuilder.Static (not in a world?)");
                        b.DeactivateBlockCreation();
                        return Reply(true, "DE.S7a ghost stop: MyCubeBuilder.Static.DeactivateBlockCreation() called (the ghost goes at the next producer tick)");
                    }
                    case GhostArgs.Verb.Probe: return Probe(block);
                    case GhostArgs.Verb.Bad: return Reply(false, error);
                    default:
                    {
                        Dictionary<string, object> d = Status();
                        d["ok"] = true;
                        d["line"] = StatusLine();
                        return d;
                    }
                }
            }
            catch (Exception e)
            {
                return Reply(false, "DE.S7a ghost command failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
 /// <c>{ghost: "probe", block: "TypeId/SubtypeId"}</c>: <c>MyCubeBuilder.Static.Activate(MyDefinitionId?, bool)</c> with that
 /// definition (HYPOTHESIS: it enters build mode for the controlled character; when BlockCreationIsActivated stays

 /// </summary>
        private static Dictionary<string, object> Probe(string block)
        {
            MyCubeBuilder b = MyCubeBuilder.Static;
            if (b == null) return Reply(false, "DE.S7a ghost probe: no MyCubeBuilder.Static (not in a world?)");
            int slash = block.IndexOf('/');
            string type = block.Substring(0, slash), subtype = block.Substring(slash + 1);
            MyDefinitionId id;
            if (!MyDefinitionId.TryParse(type, subtype, out id) && !MyDefinitionId.TryParse(GhostArgs.FullTypeId(type), subtype, out id))
                return Reply(false, "DE.S7a ghost probe: '" + block + "' is not a definition id (type not known)");
            MyCubeBlockDefinition def;
            MyDefinitionManager dm = MyDefinitionManager.Static;
            if (dm == null || !dm.TryGetCubeBlockDefinition(id, out def) || def == null)
                return Reply(false, "DE.S7a ghost probe: no cube block definition " + id);
            b.Activate(id, false);
            bool on = b.BlockCreationIsActivated;
            string line = "DE.S7a ghost probe " + id + " (" + def.CubeSize + ", model " + (string.IsNullOrEmpty(def.Model) ? "none: cube parts" : def.Model) + "): Activate called, " +
                          "BlockCreationIsActivated=" + on + (on ? "" : " (HYPOTHESIS failed: the builder did not enter build mode; owner step: take the block in hand)");
            DeLog.Info(line);
            Dictionary<string, object> r = Reply(true, line);
            r["activated"] = on;
            r["def"] = id.ToString();
            return r;
        }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "entityId", "0x" + S7aGhost.EntityId.ToString("x16", CultureInfo.InvariantCulture) },
                { "enabled", _enabled }, { "reader", GhostReader.Missing ? "OFF " + GhostReader.MissingWhy : "ok" }, { "idTaken", _idTaken },
                { "alive", Plan.Alive }, { "active", _inactiveWhy == null }, { "reason", _inactiveWhy },
                { "def", _defText }, { "model", _model }, { "modelKey", "0x" + _modelKey.ToString("x16", CultureInfo.InvariantCulture) },
                { "cubeSize", _cubeSize }, { "mode", _mode }, { "parts", _parts.Count }, { "partsVersion", (long)Plan.PartsVersion },
                { "shown", _shown }, { "allowed", _allowed }, { "buildAllowed", _buildAllowed },
                { "gridAdds", Plan.GridAdds }, { "partAdds", Plan.PartAdds }, { "partRemoves", Plan.PartRemoves }, { "entityRemoves", Plan.EntityRemoves },
                { "activations", Plan.Activations }, { "c2Rows", _c2Rows }, { "c2Skipped", _c2Skipped },
                { "latencyLastTicks", Latency.Last }, { "latencyP95Ticks", Latency.P95 }, { "latencyMaxTicks", Latency.Max }, { "latencySamples", Latency.Samples },
                { "errors", _errors }, { "mutants", S7aGhostMutants.Active },
            };
            return d;
        }

        private static string StatusLine()
        {
            return "DE.S7a ghost " + (_enabled ? "on" : "OFF") + ": " + (_inactiveWhy == null ? "active " + _defText + " (" + _parts.Count + " part(s), " + (_allowed ? "allowed" : "blocked") + ", " + _mode + ")" : "inactive (" + _inactiveWhy + ")") +
                   ", latency p95 " + Latency.P95 + " ticks over " + Latency.Samples + " sample(s)" + (S7aGhostMutants.AllowedAlways ? ", MUTANT S7a.M4" : "");
        }

        private static Dictionary<string, object> Reply(bool ok, string line)
        {
            if (!ok) DeLog.Info(line);
            var d = new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", ok }, { "line", line } };
            if (!ok) d["error"] = line;
            return d;
        }
    }
}
