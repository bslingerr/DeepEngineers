using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using Havok;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Models;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// The asynchronous S4 harness parts of <c>de.terrain</c> (update thread, one job at a time, a few rays per tick):
 /// <list type="bullet">
 /// <item><b>at1</b> (S4.A-T1 (1), ROADMAP "A-T1 harness"): a size × size grid of vertical SN rays around an origin plus N
 /// random columns. Per ray: <c>MyVoxelPhysicsBody.PrefetchShapeOnRay(ref LineD)</c>
 /// on the ray and again on each still-missing 8 m cell, then a wait until every cell on the
 /// ray exists (<c>MyVoxelPhysicsBody.GetShape(int, out HkUniformGridShape)</c>, then
 /// <c>HkUniformGridShape.GetChild(int, int, int, out HkBvCompressedMeshShape)</c> HavokWrapper: true = built, an
 /// emptied cell included), then <c>MyPhysics.CastRay(Vector3D, Vector3D, List&lt;MyPhysics.HitInfo&gt;, int)</c>
 /// and the storage reference <c>MyVoxelBase.GetIntersectionWithLine(ref LineD, out MyIntersectionResultLineTriangleEx?,
 /// IntersectionFlags)</c> (SE's own mesher over the storage). Every ray records its cells and how many were built
 /// (an unbuilt cell is a harness error, not a hole: S4.A-T1.1u); rows <c>s4.ray</c>, <c>s4.cell</c> (once per cell).
 /// S4.M7 (or <c>prefetch: false</c>) casts without prefetch and without waiting.</item>
 /// <item><b>bake</b>: a non-saved map holding one SN batch baked through <c>WriteRange</c> into
 /// <c>MyOctreeStorage(null, 256)</c> (no provider, so not guarded), on the same sample points as the tile; at1 rays over
 /// it also emit <c>set: "bake"</c> rows (S4.A-T1.1b: bake vs provider ≤ 1 mm).</item>
 /// <item><b>drop</b> (A-T1 (2)): one dynamic 1-block large grid per point, its bottom face released 3 m over the storage
 /// surface; after <c>settle</c> s the bottom-face SN height vs the surface (<c>s4.drop</c>).</item>
 /// <item><b>walk</b> (A-T1 (2)): the local character walks <c>length</c> m
 /// from a point on the surface; feet vs surface every 10 ticks
 /// (<c>s4.walk</c>).</item>
 /// <item><b>perf</b> (A-T1 (3)): the spectator flies from → to at <c>speed</c> m/s; per-tick frame times and the
 /// ReadRange statistics over the flight (<c>s4.perf</c>).</item>
 /// </list>
 /// A member that does not resolve fails only the job (logged once), never the terrain.
 /// </summary>
    internal static class S4Harness
    {
        public const int MaxInFlight = 16, MaxStartsPerTick = 4, MaxCastsPerTick = 3;
        /// <summary>Update-thread time per tick the casts may use (the storage reference meshes new 8 m cells); at least one cast per tick.</summary>
        public const double CastBudgetMs = 8.0;
        public const ulong WaitTimeoutMs = 12000, RePrefetchMs = 2000;
        public const double PrefetchSegmentM = 400;
        /// <summary>How often a ray whose built cells vanished before its cast goes back to prefetch and wait.</summary>
        public const int MaxRequeues = 3;
        /// <summary>A dropped grid is at rest below this speed.</summary>
        public const double RestSpeed = 0.05;

        private abstract class Job
        {
            public string Kind;
            public string Via;
            public ulong StartMs;
            /// <summary>Returns true when finished.</summary>
            public abstract bool Tick(ulong now);
            public abstract string State { get; }
            public virtual void Abort(string why) { DeLog.Info("DE.Terrain " + Kind.ToUpperInvariant() + " aborted: " + why); }
        }

        private static Job _job;
        private static BakeState _bake;
        private static string _membersMissing;
        private static bool _membersChecked;

        public static string StateText
        {
            get
            {
                Job j = _job;
                string s = j == null ? "idle" : j.Kind + " " + j.State;
                if (_bake != null) s += "; bake " + _bake.StateText;
                return s;
            }
        }

        public static Dictionary<string, object> StatusInfo()
        {
            Job j = _job;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "job", j != null ? j.Kind : null }, { "state", j != null ? j.State : "idle" }, { "bake", _bake != null ? _bake.StateText : null },
                { "membersMissing", _membersMissing },
            };
        }

        public static void Tick(ulong nowMs, bool inWorld)
        {
            if (_bake != null && !_bake.Done)
            {
                try { _bake.Tick(); }
                catch (Exception e) { DeLog.Info("DE.Terrain BAKE failed " + e.GetType().Name + ": " + e.Message); _bake = null; }
            }
            Job j = _job;
            if (j == null) return;
            if (!inWorld || !TerrainTiles.TerrainUp) { _job = null; j.Abort(!inWorld ? "left the world" : "no terrain tiles"); return; }
            try { if (j.Tick(nowMs)) _job = null; }
            catch (Exception e)
            {
                _job = null;
                j.Abort(e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
            }
        }

        public static Dictionary<string, object> Stop(string why)
        {
            Job j = _job;
            _job = null;
            if (j != null) j.Abort(why);
            return S4Commands.Ok("DE.Terrain harness stopped (" + (j != null ? j.Kind : "nothing running") + ")");
        }

        /// <summary>World unload: abort the job and forget the bake (SE closes its map with the world).</summary>
        public static void OnUnload()
        {
            Job j = _job;
            _job = null;
            if (j != null) j.Abort("world unload");
            _bake = null;
        }

        private static Dictionary<string, object> Begin(Job j, string line)
        {
            if (_job != null) return S4Commands.Fail("de.terrain " + j.Kind + ": " + _job.Kind + " is running (" + _job.State + "); de.terrain stop first");
            j.StartMs = Wire.Native.Win32.TickCount64();
            _job = j;
            DeLog.Info(line);
            var r = S4Commands.Ok(line);
            r["pending"] = true;
            r["job"] = j.Kind;
            return r;
        }

        /// <summary>The harness's SE members, checked once by reflection (exact parameter types); null when all resolve.</summary>
        private static string HarnessMembers()
        {
            if (_membersChecked) return _membersMissing;
            _membersChecked = true;
            var missing = new List<string>();
            try
            {
                if (typeof(MyVoxelPhysicsBody).GetMethod("PrefetchShapeOnRay", new[] { typeof(LineD).MakeByRefType() }) == null) missing.Add("MyVoxelPhysicsBody.PrefetchShapeOnRay(ref LineD)");
                if (typeof(MyVoxelPhysicsBody).GetMethod("GetShape", new[] { typeof(int), typeof(HkUniformGridShape).MakeByRefType() }) == null) missing.Add("MyVoxelPhysicsBody.GetShape(int, out HkUniformGridShape)");
                if (typeof(HkUniformGridShape).GetMethod("GetChild", new[] { typeof(int), typeof(int), typeof(int), typeof(HkBvCompressedMeshShape).MakeByRefType() }) == null) missing.Add("HkUniformGridShape.GetChild(int, int, int, out HkBvCompressedMeshShape)");
                if (typeof(MyPhysics).GetMethod("CastRay", new[] { typeof(Vector3D), typeof(Vector3D), typeof(List<MyPhysics.HitInfo>), typeof(int) }) == null) missing.Add("MyPhysics.CastRay(Vector3D, Vector3D, List<HitInfo>, int)");
                if (typeof(MyVoxelBase).GetMethod("GetIntersectionWithLine", new[] { typeof(LineD).MakeByRefType(), typeof(MyIntersectionResultLineTriangleEx?).MakeByRefType(), typeof(IntersectionFlags) }) == null)
                    missing.Add("MyVoxelBase.GetIntersectionWithLine(ref LineD, out MyIntersectionResultLineTriangleEx?, IntersectionFlags)");
            }
            catch (Exception e) { missing.Add("lookup failed " + e.GetType().Name + ": " + e.Message); }
            _membersMissing = missing.Count == 0 ? null : string.Join("; ", missing.ToArray());
            if (_membersMissing != null) DeLog.Info("DE.Terrain WARN harness members MISSING: " + _membersMissing + " (at1 disabled; terrain unaffected)");
            return _membersMissing;
        }

        // ============================================================================================ shared helpers

        private static SiteMap Map { get { return TerrainTiles.Map; } }

        private static Vec3 V3(Vector3D v) { return new Vec3(v.X, v.Y, v.Z); }
        private static Vector3D D3(Vec3 v) { return new Vector3D(v.X, v.Y, v.Z); }

        /// <summary>The storage surface (SE's mesher, GetIntersectionWithLine) on the vertical SN ray (x, fromY → toY, z) over the DE tiles.</summary>
        internal static bool StorageColumn(double x, double z, double fromY, double toY, out double hitSnY, out string tile)
        {
            hitSnY = double.NaN;
            tile = null;
            SiteMap map = Map;
            if (map == null) return false;
            Vec3 a = map.SnToSe(new Vec3(x, fromY, z)), b = map.SnToSe(new Vec3(x, toY, z));
            double best = double.MaxValue;
            foreach (TerrainTiles.TileInfo t in TerrainTiles.Tiles)
            {
                Vec3 p;
                if (!StorageHit(t.Map, t.PlaceMin, a, b, out p)) continue;
                double d = (p - a).Length;
                if (d >= best) continue;
                best = d;
                hitSnY = map.SeToSn(p).Y;
                tile = t.I + "," + t.J + "," + t.K;
            }
            return tile != null;
        }

        /// <summary>One map's storage surface on the segment a→b (null map/box miss: false).</summary>
        private static bool StorageHit(MyVoxelBase m, Vec3 min, Vec3 a, Vec3 b, out Vec3 p)
        {
            p = Vec3.Zero;
            if (m == null || m.Closed) return false;
            double t0, t1;
            if (!RayPlan.ClipToBox(a, b, min, m.Storage != null ? m.Storage.Size.X : SiteV1.Tile, out t0, out t1)) return false;
            var line = new LineD(D3(a), D3(b));
            MyIntersectionResultLineTriangleEx? r;
            if (!m.GetIntersectionWithLine(ref line, out r, IntersectionFlags.ALL_TRIANGLES) || !r.HasValue) return false;
            p = V3(r.Value.IntersectionPointInWorldSpace);
            return true;
        }

        // ================================================================================================== at1

        private sealed class Target
        {
            public MyVoxelBase Map;
            public bool Bake;
            public string Label;
            public int TileIndex;
            public Vec3 Min;
            public readonly List<int> Cells = new List<int>();
            public bool[] Built;
            public int BuiltCount;
            public double[] BuiltAtMs;
            /// <summary>Cells on the segment that hold no iso-surface (open water / solid rock): never meshed by SE, never waited for.</summary>
            public int Water, Rock;
        }

        private sealed class RayWork
        {
            public RayColumn Col;
            public Vec3 FromSe, ToSe;
            public readonly List<Target> Targets = new List<Target>();
            public ulong StartMs, LastPrefetchMs, RequeueMs;
            public bool Ready;
            public double WaitMs;
            /// <summary>Cells seen built, then gone at cast time (SE's shape discard), and how often the ray went back to wait.</summary>
            public int LostCells, Requeues;
        }

        private sealed class At1Job : Job
        {
            private readonly List<RayColumn> _cols;
            private readonly bool _prefetch;
            private readonly List<RayWork> _flight = new List<RayWork>(MaxInFlight);
            private readonly HashSet<long> _cellsLogged = new HashSet<long>();
            private readonly List<MyPhysics.HitInfo> _hits = new List<MyPhysics.HitInfo>(16);
            private int _next, _done, _castHits, _storageHits, _unbuiltRays, _bakeRows, _intersectHoles;
            private double _maxCastStorage;

            public At1Job(List<RayColumn> cols, bool prefetch) { _cols = cols; _prefetch = prefetch; Kind = "at1"; }

            public override string State { get { return _done + "/" + _cols.Count + " rays (" + _flight.Count + " in flight)"; } }

            public override bool Tick(ulong now)
            {
                int starts = 0;
                while (_flight.Count < MaxInFlight && _next < _cols.Count && starts < MaxStartsPerTick)
                {
                    _flight.Add(StartRay(_cols[_next++], now));
                    starts++;
                }
                foreach (RayWork w in _flight) if (!w.Ready) Poll(w, now);
                int casts = 0;
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < _flight.Count && casts < MaxCastsPerTick; )
                {
                    if (!_flight[i].Ready) { i++; continue; }
                    if (casts > 0 && (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency > CastBudgetMs) break;
                    if (!Recheck(_flight[i], now)) { i++; continue; }
                    Cast(_flight[i]);
                    _flight.RemoveAt(i);
                    casts++;
                    _done++;
                }
                if (_next < _cols.Count || _flight.Count > 0) return false;
                double s = (now - StartMs) / 1000.0;
                string line = "DE.Terrain AT1 done n=" + _done + " castHits=" + _castHits + " storageHits=" + _storageHits + " unbuiltRays=" + _unbuiltRays +
                              " intersectHoles=" + _intersectHoles +
                              " bakeRows=" + _bakeRows + " max|cast-storage|=" + _maxCastStorage.ToString("0.####", CultureInfo.InvariantCulture) + " m prefetch=" + _prefetch +
                              " in " + s.ToString("0.0", CultureInfo.InvariantCulture) + " s (rows s4.ray/s4.cell; reference heights: tools/sn-octrees/sn_field.py)" +
                              (S4Mutants.Active.Length > 0 ? " MUTANTS=" + S4Mutants.Active : "");
                DeLog.Info(line);
                Jsonl.Flush();
                return true;
            }

            private RayWork StartRay(RayColumn c, ulong now)
            {
                var w = new RayWork { Col = c, StartMs = now, LastPrefetchMs = now };
                w.FromSe = Map.SnToSe(new Vec3(c.X, c.FromY, c.Z));
                w.ToSe = Map.SnToSe(new Vec3(c.X, c.ToY, c.Z));
                IReadOnlyList<TerrainTiles.TileInfo> tiles = TerrainTiles.Tiles;
                for (int ti = 0; ti < tiles.Count; ti++) AddTarget(w, tiles[ti].Map, tiles[ti].PlaceMin, false, tiles[ti].I + "," + tiles[ti].J + "," + tiles[ti].K, ti);
                BakeState b = _bake;
                if (b != null && b.Map != null && !b.Map.Closed) AddTarget(w, b.Map, b.Min, true, "bake", -1);
                if (_prefetch) Prefetch(w, false);
                else w.Ready = true; // S4.M7 / prefetch: false: no prefetch, no wait (Poll below still counts the built cells)
                if (!_prefetch) foreach (Target t in w.Targets) CheckCells(t, 0);
                return w;
            }

            private static void AddTarget(RayWork w, MyVoxelBase map, Vec3 min, bool bake, string label, int tileIndex)
            {
                if (map == null || map.Closed) return;
                double size = map.Storage != null ? map.Storage.Size.X : SiteV1.Tile;
                double t0, t1;
                if (!RayPlan.ClipToBox(w.FromSe, w.ToSe, min, size, out t0, out t1)) return;
                var t = new Target { Map = map, Bake = bake, Label = label, TileIndex = tileIndex, Min = min };
                Vec3 d = w.ToSe - w.FromSe;
                RayPlan.CellsOnSegment(w.FromSe + d * t0 - min, w.FromSe + d * t1 - min, t.Cells);
                // Real run S4-1: every grid ray waited the full 12 s and was classified unbuilt (53 cells,
 // 1 built) although every cast hit: SE meshes only the cells that hold the iso-surface, so the cells in open
 // water above the seabed and those fully inside the rock never get a Havok shape. Only surface cells count as
 // "to build" (content read at LOD 0 over the cell plus a 1-voxel margin: min < 128 <= max).
                FilterSurfaceCells(map, t.Cells, ref t.Water, ref t.Rock);
                int n = t.Cells.Count / 3;
                t.Built = new bool[n];
                t.BuiltAtMs = new double[n];
                w.Targets.Add(t);
            }

            private static readonly MyStorageData s_cellProbe = new MyStorageData();

            private static int WaterCells(RayWork w) { int n = 0; foreach (Target t in w.Targets) n += t.Water; return n; }
            private static int RockCells(RayWork w) { int n = 0; foreach (Target t in w.Targets) n += t.Rock; return n; }

            /// <summary>Keeps the cells whose 8 m box (1-voxel margin) holds the iso-surface; counts the dropped water and rock cells.</summary>
            private static void FilterSurfaceCells(MyVoxelBase map, List<int> cells, ref int water, ref int rock)
            {
                var st = map.Storage;
                if (st == null) return;
                Vector3I last = st.Size - Vector3I.One;
                int kept = 0;
                for (int c = 0; c < cells.Count / 3; c++)
                {
                    int cx = cells[3 * c], cy = cells[3 * c + 1], cz = cells[3 * c + 2];
                    Vector3I lo = Vector3I.Max(new Vector3I(cx * 8 - 1, cy * 8 - 1, cz * 8 - 1), Vector3I.Zero);
                    Vector3I hi = Vector3I.Min(new Vector3I(cx * 8 + 8, cy * 8 + 8, cz * 8 + 8), last);
                    s_cellProbe.Resize(hi - lo + Vector3I.One);
                    st.ReadRange(s_cellProbe, MyStorageDataTypeFlags.Content, 0, lo, hi);
                    byte cmin = 255, cmax = 0;
                    int n = s_cellProbe.SizeLinear;
                    for (int i = 0; i < n; i++) { byte v = s_cellProbe.Content(i); if (v < cmin) cmin = v; if (v > cmax) cmax = v; }
                    if (!(cmin < 128 && cmax >= 128)) { if (cmax < 128) water++; else rock++; continue; }
                    cells[3 * kept] = cx; cells[3 * kept + 1] = cy; cells[3 * kept + 2] = cz; kept++;
                }
                cells.RemoveRange(3 * kept, cells.Count - 3 * kept);
            }

            /// <summary>PrefetchShapeOnRay on the ray (≤ 400 m pieces), then on a 2 m diagonal inside each missing cell.</summary>
            private static void Prefetch(RayWork w, bool missingOnly)
            {
                foreach (Target t in w.Targets)
                {
                    var body = t.Map.Physics as MyVoxelPhysicsBody;
                    if (body == null) continue;
                    if (!missingOnly)
                    {
                        Vec3 d = w.ToSe - w.FromSe;
                        int pieces = Math.Max(1, (int)Math.Ceiling(d.Length / PrefetchSegmentM));
                        for (int p = 0; p < pieces; p++)
                        {
                            var line = new LineD(D3(w.FromSe + d * ((double)p / pieces)), D3(w.FromSe + d * ((double)(p + 1) / pieces)));
                            body.PrefetchShapeOnRay(ref line);
                        }
                    }
                    for (int c = 0; c < t.Built.Length; c++)
                    {
                        if (t.Built[c]) continue;
                        Vec3 centre = t.Min + new Vec3(t.Cells[3 * c] * 8 + 4, t.Cells[3 * c + 1] * 8 + 4, t.Cells[3 * c + 2] * 8 + 4);
                        var line = new LineD(D3(centre - new Vec3(1, 1, 1)), D3(centre + new Vec3(1, 1, 1)));
                        body.PrefetchShapeOnRay(ref line);
                    }
                }
            }

            private void Poll(RayWork w, ulong now)
            {
                double waited = now - w.StartMs;
                bool all = true;
                foreach (Target t in w.Targets)
                {
                    CheckCells(t, waited);
                    if (t.BuiltCount < t.Built.Length) all = false;
                }
                if (all || now - Math.Max(w.StartMs, w.RequeueMs) >= WaitTimeoutMs) { w.Ready = true; w.WaitMs = waited; return; }
                if (now - w.LastPrefetchMs >= RePrefetchMs) { w.LastPrefetchMs = now; Prefetch(w, true); }
            }

            /// <summary>
 /// S4  SE discards a tile's shapes about every 3 s when no entity is near and no ray hit it
 /// recently (<c>MyVoxelPhysicsBody.CheckAndDiscardShapes</c>, <c>EnableShapeDiscard</c>), so a cell
 /// counted built may be gone at cast time. In the cast's own tick every cell counted built is checked again
 /// (<c>GetChild</c>); a lost one sends the ray back to prefetch and wait (at most <see cref="MaxRequeues"/>
 /// times), after which it is cast with the true count (an unbuilt cell: S4.A-T1.1u, never a hole). Without the
 /// prefetch (S4.M7) nothing is checked. False when the ray went back to wait.
 /// </summary>
            private bool Recheck(RayWork w, ulong now)
            {
                if (!_prefetch) return true;
                int lost = 0;
                foreach (Target t in w.Targets) lost += Unbuild(t);
                if (lost == 0) return true;
                w.LostCells += lost;
                if (w.Requeues >= MaxRequeues) return true;
                w.Requeues++;
                w.Ready = false;
                w.RequeueMs = now;
                w.LastPrefetchMs = now;
                Prefetch(w, true);
                return false;
            }

            /// <summary>Marks unbuilt every cell counted built whose shape is gone now; returns how many.</summary>
            private static int Unbuild(Target t)
            {
                var body = t.Map.Physics as MyVoxelPhysicsBody;
                if (body == null || t.BuiltCount == 0) return 0;
                HkUniformGridShape g;
                bool shape = body.GetShape(0, out g) && !g.Base.IsZero;
                int lost = 0;
                for (int c = 0; c < t.Built.Length; c++)
                {
                    if (!t.Built[c]) continue;
                    HkBvCompressedMeshShape s;
                    if (shape && g.GetChild(t.Cells[3 * c], t.Cells[3 * c + 1], t.Cells[3 * c + 2], out s)) continue;
                    t.Built[c] = false;
                    t.BuiltCount--;
                    lost++;
                }
                return lost;
            }

            private static void CheckCells(Target t, double waitedMs)
            {
                var body = t.Map.Physics as MyVoxelPhysicsBody;
                if (body == null) return;
                HkUniformGridShape g;
                if (!body.GetShape(0, out g) || g.Base.IsZero) return;
                for (int c = 0; c < t.Built.Length; c++)
                {
                    if (t.Built[c]) continue;
                    HkBvCompressedMeshShape s;
                    if (!g.GetChild(t.Cells[3 * c], t.Cells[3 * c + 1], t.Cells[3 * c + 2], out s)) continue;
                    t.Built[c] = true;
                    t.BuiltAtMs[c] = waitedMs;
                    t.BuiltCount++;
                }
            }

            private void Cast(RayWork w)
            {
                RayColumn c = w.Col;
                int cells = 0, built = 0;
                foreach (Target t in w.Targets) { cells += t.Built.Length; built += t.BuiltCount; LogCells(t, w.WaitMs); }
                if (built < cells) _unbuiltRays++;
                string err = null;
                Vec3? castDe = null, castBake = null;
                string castTile = null, otherHit = null;
                try
                {
                    MyPhysics.CastRay(D3(w.FromSe), D3(w.ToSe), _hits, 0);
                    double bestDe = double.MaxValue, bestBake = double.MaxValue, bestOther = double.MaxValue;
                    foreach (MyPhysics.HitInfo h in _hits)
                    {
                        IMyEntity e = h.HkHitInfo.GetHitEntity();
                        Vec3 p = V3(h.Position);
                        double d = (p - w.FromSe).Length;
                        Target hitT = null;
                        foreach (Target t in w.Targets) if (ReferenceEquals(t.Map, e)) { hitT = t; break; }
                        if (hitT == null)
                        {
                            if (d < bestOther) { bestOther = d; otherHit = e != null ? e.GetType().Name + " " + e.DisplayName : "null"; }
                            continue;
                        }
                        if (hitT.Bake) { if (d < bestBake) { bestBake = d; castBake = p; } }
                        else if (d < bestDe) { bestDe = d; castDe = p; castTile = hitT.Label; }
                    }
                }
                catch (Exception e) { err = "CastRay " + e.GetType().Name + ": " + e.Message; }

                Vec3? stDe = null, stBake = null;
                string stTile = null;
                Target stTarget = null;
                try
                {
                    double bestDe = double.MaxValue;
                    foreach (Target t in w.Targets)
                    {
                        Vec3 p;
                        if (!StorageHit(t.Map, t.Min, w.FromSe, w.ToSe, out p)) continue;
                        if (t.Bake) { stBake = p; continue; }
                        double d = (p - w.FromSe).Length;
                        if (d < bestDe) { bestDe = d; stDe = p; stTile = t.Label; stTarget = t; }
                    }
                }
                catch (Exception e) { err = (err != null ? err + "; " : "") + "GetIntersectionWithLine " + e.GetType().Name + ": " + e.Message; }

                // SE's own grid-prediction decision per cell (MyVoxelPhysicsBody.UpdateAfterSimulation10: Storage.Intersect on
 // (8c, 8c + 8); anything but Intersects empties the cell): a Disjoint/Contains on the cell holding the storage
 // surface is a hole a grid would fall through (S4.M3); the ray's own prefetch path (CreateMesh) never asks it
                var ix = new IntersectWitness();
                try { ix = IntersectCells(w, stTarget, stDe); }
                catch (Exception e) { err = (err != null ? err + "; " : "") + "Intersect " + e.GetType().Name + ": " + e.Message; }

                if (castDe.HasValue) _castHits++;
                if (stDe.HasValue) _storageHits++;
                if (ix.Hole) _intersectHoles++;
                if (castDe.HasValue && stDe.HasValue) _maxCastStorage = Math.Max(_maxCastStorage, Math.Abs(Map.SeToSn(castDe.Value).Y - Map.SeToSn(stDe.Value).Y));
                Row(c.Set, w, cells, built, castDe, stDe, castTile ?? stTile, err, otherHit, ix);
                bool hasBake = false;
                foreach (Target t in w.Targets) if (t.Bake) hasBake = true;
                if (hasBake)
                {
                    int bc = 0, bb = 0;
                    foreach (Target t in w.Targets) if (t.Bake) { bc += t.Built.Length; bb += t.BuiltCount; }
                    Row("bake", w, bc, bb, castBake, stBake, "bake", err, null, new IntersectWitness());
                    _bakeRows++;
                }
            }

            private struct IntersectWitness
            {
                public int Disjoint, Contains, Asked;
                public bool Hole;
                public string HoleCell;
                /// <summary>Cells around the storage surface asked (<see cref="RayPlan.HoleCandidates"/>) and how many answered Intersects.</summary>
                public int Candidates, CandidateIntersects;
            }

            private readonly List<int> _candidates = new List<int>(24);

            /// <summary>
 /// <c>MyStorageBase.Intersect(ref BoundingBoxI, int, bool)</c> on each DE
 /// cell of the ray (counts), and the S4.M3 witness: the cells within <see cref="RayPlan.HoleTolM"/> of SE's own
 /// storage surface point; a hole when none of them answers Intersects.
 /// </summary>
            private IntersectWitness IntersectCells(RayWork w, Target surfaceTarget, Vec3? surface)
            {
                var r = new IntersectWitness();
                foreach (Target t in w.Targets)
                {
                    if (t.Bake) continue;
                    var storage = t.Map.Storage as MyStorageBase;
                    if (storage == null) continue;
                    for (int c = 0; c < t.Built.Length; c++)
                    {
                        int cx = t.Cells[3 * c], cy = t.Cells[3 * c + 1], cz = t.Cells[3 * c + 2];
                        ContainmentType ct = CellIntersect(storage, cx, cy, cz);
                        r.Asked++;
                        if (ct == ContainmentType.Disjoint) r.Disjoint++;
                        else if (ct == ContainmentType.Contains) r.Contains++;
                    }
                    if (!surface.HasValue || !ReferenceEquals(t, surfaceTarget)) continue;
                    Vec3 l = surface.Value - t.Min;
                    _candidates.Clear();
                    RayPlan.HoleCandidates(l, RayPlan.HoleTolM, _candidates);
                    string first = null;
                    for (int k = 0; k + 2 < _candidates.Count; k += 3)
                    {
                        ContainmentType ct = CellIntersect(storage, _candidates[k], _candidates[k + 1], _candidates[k + 2]);
                        r.Candidates++;
                        if (ct == ContainmentType.Intersects) r.CandidateIntersects++;
                        if (first == null) first = t.Label + ":" + _candidates[k] + "," + _candidates[k + 1] + "," + _candidates[k + 2] + " " + ct;
                    }
                    if (r.Candidates > 0 && r.CandidateIntersects == 0)
                    {
                        r.Hole = true;
                        r.HoleCell = first + " (" + r.Candidates + " cell(s) around the surface, none Intersects)";
                    }
                }
                return r;
            }

            /// <summary>The box SE asks for an 8 m cell in <c>UpdateAfterSimulation10</c>: <c>(8c, 8c + 8)</c>, LOD-0 voxels, inclusive.</summary>
            private static ContainmentType CellIntersect(MyStorageBase storage, int cx, int cy, int cz)
            {
                var box = new BoundingBoxI(new Vector3I(cx, cy, cz) * 8, (new Vector3I(cx, cy, cz) + 1) * 8);
                return storage.Intersect(ref box, 0, true);
            }

            private void Row(string set, RayWork w, int cells, int built, Vec3? cast, Vec3? storage, string tile, string err, string other, IntersectWitness ix)
            {
                RayColumn c = w.Col;
                object castSnY = cast.HasValue ? (object)Map.SeToSn(cast.Value).Y : null;
                object stSnY = storage.HasValue ? (object)Map.SeToSn(storage.Value).Y : null;
                Jsonl.WriteLineBuffered(S4Rows.Row("s4.ray", "set", set, "i", c.I, "j", c.J, "snX", c.X, "snZ", c.Z, "fromY", c.FromY, "toY", c.ToY,
                    "prefetch", _prefetch, "cells", cells, "cellsBuilt", built, "cellsWater", WaterCells(w), "cellsRock", RockCells(w), "waitMs", w.WaitMs, "castHit", cast.HasValue,
                    "castSe", cast.HasValue ? S4Rows.Arr(cast.Value) : null, "castSnY", castSnY, "storageHit", storage.HasValue,
                    "storageSe", storage.HasValue ? S4Rows.Arr(storage.Value) : null, "storageSnY", stSnY, "tile", tile,
                    "intersectAsked", ix.Asked, "intersectDisjoint", ix.Disjoint, "intersectContains", ix.Contains, "intersectHole", ix.Hole,
                    "intersectHoleCell", ix.HoleCell, "holeCandidates", ix.Candidates, "holeIntersects", ix.CandidateIntersects,
                    "lostCells", w.LostCells, "requeues", w.Requeues, "otherHit", other, "err", err));
            }

            private void LogCells(Target t, double waitMs)
            {
                for (int c = 0; c < t.Built.Length; c++)
                {
                    // once per (map, cell, built) state: a built cell is logged the first time a ray sees it built, an unbuilt one the first time it stays unbuilt
                    long key = ((long)(t.TileIndex + 1) << 40) | ((long)t.Cells[3 * c] << 20) | ((long)t.Cells[3 * c + 1] << 10) | (long)t.Cells[3 * c + 2] |
                               (t.Built[c] ? 0L : 1L << 50);
                    if (!_cellsLogged.Add(key)) continue;
                    int ti = -1, tj = -1, tk = -1;
                    if (t.TileIndex >= 0 && t.TileIndex < TerrainTiles.Tiles.Count)
                    {
                        TerrainTiles.TileInfo info = TerrainTiles.Tiles[t.TileIndex];
                        ti = info.I; tj = info.J; tk = info.K;
                    }
                    Jsonl.WriteLineBuffered(S4Rows.Row("s4.cell", "i", ti, "j", tj, "k", tk, "bake", t.Bake, "x", t.Cells[3 * c], "y", t.Cells[3 * c + 1], "z", t.Cells[3 * c + 2],
                        "built", t.Built[c], "waitedMs", t.Built[c] ? t.BuiltAtMs[c] : waitMs, "buildMs", null));
                }
            }

            public override void Abort(string why)
            {
                base.Abort(why + " after " + _done + "/" + _cols.Count + " rays");
                Jsonl.Flush();
            }
        }

        public static Dictionary<string, object> StartAt1(IDictionary<string, object> args, string via)
        {
            if (!TerrainTiles.TerrainUp) return S4Commands.Fail("de.terrain at1: no terrain tiles (gate " + TerrainGate.Last + " " + (TerrainGate.Reason ?? "") + ")");
            string missing = HarnessMembers();
            if (missing != null) return S4Commands.Fail("de.terrain at1: harness members missing: " + missing);
            double[] o = S4Args.Numbers(args, "origin", 2) ?? new[] { 0.0, 0.0 };
            int size = (int)Math.Max(0, Math.Min(256, MiniJson.GetNumber(args, "size") ?? 64));
            double spacing = MiniJson.GetNumber(args, "spacing") ?? 2.5;
            double fromY = MiniJson.GetNumber(args, "fromY") ?? 20, toY = MiniJson.GetNumber(args, "toY") ?? -400;
            int random = (int)Math.Max(0, Math.Min(100000, MiniJson.GetNumber(args, "random") ?? 1000));
            double rFrom = MiniJson.GetNumber(args, "randomFromY") ?? 160, rTo = MiniJson.GetNumber(args, "randomToY") ?? -1950;
            double rHalf = MiniJson.GetNumber(args, "randomHalf") ?? 2000;
            int seed = (int)(MiniJson.GetNumber(args, "seed") ?? 4546);
            bool prefetchArg = S4Args.Flag(args, "prefetch", true);
            bool prefetch = prefetchArg && !S4Mutants.NoPrefetch;
            var cols = RayPlan.Grid(o[0], o[1], size, spacing, fromY, toY);
            cols.AddRange(RayPlan.Random(random, seed, rHalf, rFrom, rTo));
            if (cols.Count == 0) return S4Commands.Fail("de.terrain at1: no rays (size 0 and random 0)");
            string line = "DE.Terrain AT1 start rays=" + cols.Count + " grid=" + size + "x" + size + " origin=(" + o[0] + ", " + o[1] + ") spacing=" + spacing +
                          " y " + fromY + "->" + toY + " random=" + random + " (y " + rFrom + "->" + rTo + ", half " + rHalf + ", seed " + seed + ") prefetch=" + prefetch +
                          (prefetchArg && !prefetch ? " (S4.M7: no prefetch, no wait)" : "") + (_bake != null && _bake.Map != null ? " bake=on" : "") + " (via " + via + ")";
            Jsonl.WriteLine(S4Rows.Row("s4.at1", "rays", cols.Count, "size", size, "origin", new object[] { o[0], o[1] }, "spacing", spacing, "fromY", fromY, "toY", toY,
                                       "random", random, "randomFromY", rFrom, "randomToY", rTo, "randomHalf", rHalf, "seed", seed, "prefetch", prefetch,
                                       "bake", _bake != null && _bake.Map != null, "mutants", S4Mutants.Active));
            return Begin(new At1Job(cols, prefetch) { Via = via }, line);
        }

        // ================================================================================================== bake

        private sealed class BakeState
        {
            public const int N = 256, Slab = 32;
            public int Bx, By, Bz;
            public Vec3 Min;
            public MyOctreeStorage Storage;
            public MyVoxelMap Map;
            public Task<List<byte[][]>> Compute;
            public int NextSlab;
            public bool Done;
            public long T0;

            public string StateText
            {
                get
                {
                    if (Map != null) return "ready " + Bx + "," + By + "," + Bz + " entity " + Map.EntityId;
                    if (Compute != null && !Compute.IsCompleted) return "sampling " + Bx + "," + By + "," + Bz;
                    return "writing slab " + NextSlab + "/" + (N / Slab);
                }
            }

            public void Tick()
            {
                if (Compute == null || !Compute.IsCompleted) return;
                if (Compute.IsFaulted) throw Compute.Exception.InnerException ?? Compute.Exception;
                List<byte[][]> slabs = Compute.Result;
                if (Storage == null) Storage = new MyOctreeStorage(null, new Vector3I(N));
                if (NextSlab < slabs.Count)
                {
                    byte[][] s = slabs[NextSlab];
                    var data = new MyStorageData(new Vector3I(N, N, Slab), s[0], s[1]);
                    var min = new Vector3I(0, 0, NextSlab * Slab);
                    var max = new Vector3I(N - 1, N - 1, NextSlab * Slab + Slab - 1);
                    Storage.WriteRange(data, MyStorageDataTypeFlags.ContentAndMaterial, min, max, false, true);
                    NextSlab++;
                    return;
                }
                Vec3 c = Min + new Vec3(N / 2.0, N / 2.0, N / 2.0);
                MyVoxelMap map = Sandbox.Game.World.MyWorldGenerator.AddVoxelMap("DE.Terrain.bake." + Bx + "." + By + "." + Bz + ".s" + SiteV1.SiteVersion, Storage,
                    MatrixD.CreateTranslation(D3(c)), 0L, lazyPhysics: true, useVoxelOffset: false);
                Done = true;
                if (map == null) { DeLog.Info("DE.Terrain BAKE failed: AddVoxelMap returned null"); return; }
                map.Save = false;
                Map = map;
                double ms = (Stopwatch.GetTimestamp() - T0) * 1000.0 / Stopwatch.Frequency;
                string line = "DE.Terrain BAKE ready batch=" + Bx + "," + By + "," + Bz + " min=" + TerrainTiles.V(Min) + " entity=" + map.EntityId + " save=false in " +
                              ms.ToString("0", CultureInfo.InvariantCulture) + " ms (MyOctreeStorage(null, 256) + WriteRange; the at1 rays over it add set=bake rows)";
                DeLog.Info(line);
                Jsonl.WriteLine(S4Rows.Row("s4.bake", "batch", new object[] { Bx, By, Bz }, "min", S4Rows.Arr(Min), "entity", map.EntityId, "ms", ms));
            }
        }

        public static Dictionary<string, object> Bake(IDictionary<string, object> args, string via)
        {
            if (S4Args.Flag(args, "remove", false))
            {
                BakeState b = _bake;
                _bake = null;
                if (b != null && b.Map != null && !b.Map.Closed) b.Map.Close();
                return S4Commands.Ok("DE.Terrain BAKE removed" + (b != null ? " (" + b.StateText + ")" : " (none)"));
            }
            if (!TerrainTiles.TerrainUp || TerrainTiles.Field == null) return S4Commands.Fail("de.terrain bake: no terrain tiles");
            if (_bake != null && !_bake.Done) return S4Commands.Fail("de.terrain bake: a bake is in progress (" + _bake.StateText + ")");
            double[] bb = S4Args.Numbers(args, "batch", 3) ?? new[] { 12.0, 18.0, 12.0 };
            int bx = (int)bb[0], by = (int)bb[1], bz = (int)bb[2];
            if (_bake != null && _bake.Map != null && !_bake.Map.Closed) _bake.Map.Close();
            // the batch centre in SN, its tile, and a 256³ box on the tile's own sample lattice (integer offset from tileMin)
            var centreSn = new Vec3(SnOctreeFormat.OriginX + bx * 160 + 80, SnOctreeFormat.OriginY + by * 160 + 80, SnOctreeFormat.OriginZ + bz * 160 + 80);
            Vec3 cw = Map.SnToSe(centreSn);
            TerrainTiles.TileInfo t = TerrainTiles.TileAt(cw);
            if (t == null) return S4Commands.Fail("de.terrain bake: no tile holds batch " + bx + "," + by + "," + bz);
            Vec3 l = cw - t.MinWorld;
            Vec3 min = t.MinWorld + new Vec3(Math.Floor(l.X) - 128, Math.Floor(l.Y) - 128, Math.Floor(l.Z) - 128);
            var sampler = new TileSampler(TerrainTiles.Field, Map, min, TerrainTiles.Bands);
            var st = new BakeState { Bx = bx, By = by, Bz = bz, Min = min, T0 = Stopwatch.GetTimestamp() };
            st.Compute = Task.Run(() =>
            {
                var slabs = new List<byte[][]>(BakeState.N / BakeState.Slab);
                for (int s = 0; s < BakeState.N / BakeState.Slab; s++)
                {
                    int n = BakeState.N * BakeState.N * BakeState.Slab;
                    var c = new byte[n];
                    var m = new byte[n];
                    bool e, f;
                    sampler.ReadContent(0, 0, 0, s * BakeState.Slab, BakeState.N - 1, BakeState.N - 1, s * BakeState.Slab + BakeState.Slab - 1, c, 0, BakeState.N, BakeState.N * BakeState.N, out e, out f);
                    sampler.ReadMaterial(0, 0, 0, s * BakeState.Slab, BakeState.N - 1, BakeState.N - 1, s * BakeState.Slab + BakeState.Slab - 1, m, 0, BakeState.N, BakeState.N * BakeState.N, out e);
                    slabs.Add(new[] { c, m });
                }
                return slabs;
            });
            _bake = st;
            string line = "DE.Terrain BAKE start batch=" + bx + "," + by + "," + bz + " (SN centre " + TerrainTiles.V(centreSn) + ") min=" + TerrainTiles.V(min) +
                          " on tile " + t.I + "," + t.J + "," + t.K + "'s lattice; sampling on a worker, then " + (BakeState.N / BakeState.Slab) + " WriteRange slabs (via " + via + ")";
            DeLog.Info(line);
            var r = S4Commands.Ok(line);
            r["pending"] = true;
            return r;
        }

        // ================================================================================================== drop

        private sealed class DropJob : Job
        {
            public readonly List<object[]> Grids = new List<object[]>(); // {MyCubeGrid, index, x, z, refY}
            public double SettleMs;
            public override string State { get { return Grids.Count + " grid(s) settling"; } }

            public override bool Tick(ulong now)
            {
                if (now - StartMs < SettleMs) return false;
                SiteMap map = Map;
                var parts = new List<string>();
                foreach (object[] g in Grids)
                {
                    var grid = (MyCubeGrid)g[0];
                    int i = (int)g[1];
                    double x = (double)g[2], z = (double)g[3], refY = (double)g[4];
                    if (grid == null || grid.Closed) { Jsonl.WriteLine(S4Rows.Row("s4.drop", "i", i, "snX", x, "snZ", z, "refStorageSnY", refY, "err", "grid closed")); continue; }
                    MatrixD wm = grid.WorldMatrix;
                    Vec3 centre = V3(wm.Translation);
                    Vec3 up = V3(wm.Up);
                    double half = grid.GridSize / 2.0;
                    Vec3 bottom = centre - up * half;
                    Vec3 sn = map.SeToSn(bottom);
                    double lowest = double.MaxValue;
                    for (int k = 0; k < 8; k++)
                    {
                        Vec3 corner = centre + V3(wm.Right) * ((k & 1) != 0 ? half : -half) + up * ((k & 2) != 0 ? half : -half) + V3(wm.Backward) * ((k & 4) != 0 ? half : -half);
                        lowest = Math.Min(lowest, map.SeToSn(corner).Y);
                    }
                    double surfY;
                    string tile;
                    bool hit = StorageColumn(sn.X, sn.Z, sn.Y + 10, sn.Y - 30, out surfY, out tile);
                    double speed = grid.Physics != null ? (double)grid.Physics.LinearVelocity.Length() : double.NaN;
                    double spin = grid.Physics != null ? (double)grid.Physics.AngularVelocity.Length() : double.NaN;
                    bool rested = speed < RestSpeed && spin < RestSpeed;   // NaN (no physics) is not at rest
                    double delta = hit ? sn.Y - surfY : double.NaN;
                    parts.Add("#" + i + " (" + N(x) + ", " + N(z) + ") rest=" + N(sn.Y) + " surf=" + (hit ? N(surfY) : "none") + " delta=" + (hit ? N(delta) : "n/a") + " speed=" + N(speed) + (rested ? "" : " NOT AT REST"));
                    Jsonl.WriteLine(S4Rows.Row("s4.drop", "i", i, "snX", x, "snZ", z, "refStorageSnY", refY, "restSnY", sn.Y, "restSn", S4Rows.Arr(sn), "lowestCornerSnY", lowest,
                                               "surfaceSnY", hit ? (object)surfY : null, "delta", hit ? (object)delta : null, "speed", speed, "angularSpeed", spin,
                                               "rested", rested, "entityId", grid.EntityId, "tile", tile, "settleS", SettleMs / 1000.0));
                    grid.Close();
                }
                DeLog.Info("DE.Terrain DROP done " + string.Join("; ", parts.ToArray()) + " (bottom-face centre vs the storage surface under it; grids closed)");
                return true;
            }

            public override void Abort(string why)
            {
                base.Abort(why);
                foreach (object[] g in Grids) { var grid = g[0] as MyCubeGrid; if (grid != null && !grid.Closed) grid.Close(); }
            }
        }

        public static Dictionary<string, object> StartDrop(IDictionary<string, object> args, string via)
        {
            if (!TerrainTiles.TerrainUp) return S4Commands.Fail("de.terrain drop: no terrain tiles");
            object pv;
            var pts = args != null && args.TryGetValue("points", out pv) ? pv as System.Collections.IList : null;
            if (pts == null || pts.Count == 0) return S4Commands.Fail("de.terrain drop: points: [[snX, snZ], …]");
            string block = MiniJson.GetString(args, "block") ?? "LargeHeavyBlockArmorBlock";
            double height = MiniJson.GetNumber(args, "height") ?? 3.0;
            double settle = Math.Max(1, Math.Min(120, MiniJson.GetNumber(args, "settle") ?? 15));
            var def = BlockDef(block);
            if (def == null) return S4Commands.Fail("de.terrain drop: no 1x1x1 cube block definition '" + block + "'");
            var job = new DropJob { Kind = "drop", Via = via, SettleMs = settle * 1000 };
            var parts = new List<string>();
            for (int i = 0; i < pts.Count; i++)
            {
                double[] p = S4Args.NumbersOf(pts[i], 2);
                if (p == null) return S4Commands.Fail("de.terrain drop: point " + i + " is not [snX, snZ]");
                double refY;
                string tile;
                if (!StorageColumn(p[0], p[1], 160, -1950, out refY, out tile))
                {
                    Jsonl.WriteLine(S4Rows.Row("s4.drop", "i", i, "snX", p[0], "snZ", p[1], "err", "no storage surface under the column"));
                    parts.Add("#" + i + " no surface");
                    continue;
                }
                double half = def.CubeSize == MyCubeSize.Large ? 1.25 : 0.25;
                MyCubeGrid grid = SpawnGrid(def, p[0], refY + height + half, p[1]);
                if (grid == null) return S4Commands.Fail("de.terrain drop: grid spawn failed at point " + i);
                job.Grids.Add(new object[] { grid, i, p[0], p[1], refY });
                parts.Add("#" + i + " (" + N(p[0]) + ", " + N(p[1]) + ") surface " + N(refY) + " grid " + grid.EntityId);
            }
            if (job.Grids.Count == 0) return S4Commands.Fail("de.terrain drop: no point over terrain");
            string line = "DE.Terrain DROP start " + job.Grids.Count + " dynamic " + def.Id.SubtypeName + " grid(s), bottom face " + N(height) + " m over the surface, settle " +
                          N(settle) + " s: " + string.Join("; ", parts.ToArray()) + " (via " + via + ")";
            return Begin(job, line);
        }

        private static MyCubeBlockDefinition BlockDef(string subtype)
        {
            MyDefinitionId id;
            if (subtype.IndexOf('/') < 0 || !MyDefinitionId.TryParse(subtype, out id)) id = new MyDefinitionId(typeof(MyObjectBuilder_CubeBlock), subtype);
            MyCubeBlockDefinition def;
            if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out def) || def == null || def.Size != Vector3I.One) return null;
            return def;
        }

        /// <summary>A dynamic one-block grid whose block centre is at SN (x, y, z), upright (the S3 spawn recipe with IsStatic = false); Save = false.</summary>
        private static MyCubeGrid SpawnGrid(MyCubeBlockDefinition def, double x, double y, double z)
        {
            SeMatrix gm = S1Math.SpectatorAt(Map, new Vec3(x, y, z), S1Math.HeadingSn(0), Vec3.UnitY);
            var gridOb = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
            gridOb.EntityId = MyEntityIdentifier.AllocateId();
            gridOb.GridSizeEnum = def.CubeSize;
            gridOb.IsStatic = false;
            gridOb.CreatePhysics = true;
            gridOb.PersistentFlags |= MyPersistentEntityFlags2.Enabled | MyPersistentEntityFlags2.InScene;
            gridOb.DisplayName = "DE.S4 drop";
            gridOb.PositionAndOrientation = new MyPositionAndOrientation(D3(gm.Translation),
                new Vector3((float)gm.Forward.X, (float)gm.Forward.Y, (float)gm.Forward.Z), new Vector3((float)gm.Up.X, (float)gm.Up.Y, (float)gm.Up.Z));
            var b = MyObjectBuilderSerializerKeen.CreateNewObject(def.Id) as MyObjectBuilder_CubeBlock;
            if (b == null) return null;
            b.Min = new SerializableVector3I(0, 0, 0);
            b.EntityId = MyEntityIdentifier.AllocateId();
            b.IntegrityPercent = 1f;
            b.BuildPercent = 1f;
            gridOb.CubeBlocks.Add(b);
            var grid = MyEntities.CreateFromObjectBuilderAndAdd(gridOb, false) as MyCubeGrid;
            if (grid != null) grid.Save = false;
            return grid;
        }

        // ================================================================================================== walk

        private sealed class WalkJob : Job
        {
            public MyCharacter C;
            public Vec3 StartSn;
            public double Length, MaxBelow;
            public int Ticks, Samples;
            public override string State { get { return Samples + " samples"; } }

            public override bool Tick(ulong now)
            {
                if (C == null || C.Closed || C.IsDead) { Abort("no live character"); return true; }
                C.MoveAndRotate(new Vector3(0, 0, -1), Vector2.Zero, 0f);
                Ticks++;
                Vector3D fp = C.PositionComp.GetPosition();
                Vec3 sn = Map.SeToSn(V3(fp));
                double dist = Math.Sqrt((sn.X - StartSn.X) * (sn.X - StartSn.X) + (sn.Z - StartSn.Z) * (sn.Z - StartSn.Z));
                if (Ticks % 10 == 0)
                {
                    double surf;
                    string tile;
                    bool hit = StorageColumn(sn.X, sn.Z, sn.Y + 5, sn.Y - 30, out surf, out tile);
                    double below = hit ? surf - sn.Y : double.NaN;
                    if (hit) MaxBelow = Math.Max(MaxBelow, below);
                    Samples++;
                    string state = "?";
                    try { state = C.GetCurrentMovementState().ToString(); } catch (Exception) { }
                    Jsonl.WriteLineBuffered(S4Rows.Row("s4.walk", "tick", Ticks, "feetSn", S4Rows.Arr(sn), "dist", dist, "surfaceSnY", hit ? (object)surf : null,
                                                       "below", hit ? (object)below : null, "tile", tile, "movement", state, "swim", SwimGuard.StateName));
                }
                if (dist < Length && now - StartMs < 90000) return false;
                string line = "DE.Terrain WALK done dist=" + N(dist) + " m in " + Ticks + " ticks, " + Samples + " samples, max below surface=" + N(MaxBelow) + " m (feet vs storage)";
                DeLog.Info(line);
                Jsonl.WriteLine(S4Rows.Row("s4.walk", "done", true, "dist", dist, "ticks", Ticks, "samples", Samples, "maxBelow", MaxBelow));
                return true;
            }
        }

        public static Dictionary<string, object> StartWalk(IDictionary<string, object> args, string via)
        {
            if (!TerrainTiles.TerrainUp) return S4Commands.Fail("de.terrain walk: no terrain tiles");
            MyCharacter c = MySession.Static != null ? MySession.Static.LocalCharacter : null;
            if (c == null || c.Closed || c.IsDead) return S4Commands.Fail("de.terrain walk: no live local character");
            if (c.IsSitting) return S4Commands.Fail("de.terrain walk: the character is seated");
            double[] from = S4Args.Numbers(args, "from", 2) ?? new[] { 0.0, 0.0 };
            double heading = MiniJson.GetNumber(args, "headingDeg") ?? 90;
            double length = Math.Max(1, Math.Min(1000, MiniJson.GetNumber(args, "length") ?? 50));
            double surf;
            string tile;
            if (!StorageColumn(from[0], from[1], 160, -1950, out surf, out tile)) return S4Commands.Fail("de.terrain walk: no storage surface at (" + from[0] + ", " + from[1] + ")");
            var start = new Vec3(from[0], surf + 0.2, from[1]);
            SeMatrix m = S1Math.SpectatorAt(Map, start, S1Math.HeadingSn(heading), Vec3.UnitY);
            MatrixD w = MatrixD.CreateWorld(D3(m.Translation), D3(m.Forward), D3(m.Up));
            c.Teleport(w, null, false);
            if (c.Physics != null) c.Physics.LinearVelocity = Vector3.Zero;
            SwimController.OnTeleported();
            var job = new WalkJob { Kind = "walk", Via = via, C = c, StartSn = start, Length = length };
            string line = "DE.Terrain WALK start from SN " + TerrainTiles.V(start) + " (surface " + N(surf) + " on tile " + tile + ") heading " + N(heading) + " deg for " + N(length) +
                          " m (MoveAndRotate forward each tick; feet vs storage every 10 ticks; via " + via + ")";
            return Begin(job, line);
        }

        // ================================================================================================== perf

        private sealed class PerfJob : Job
        {
            public Vec3 From, To;
            public double Speed;
            public readonly List<double> FrameMs = new List<double>(4096);
            public readonly List<double> TickMs = new List<double>(4096);
            public long LastTs;
            public long CallsBefore;
            public override string State { get { return FrameMs.Count + " frames"; } }

            public override bool Tick(ulong now)
            {
                long ts = Stopwatch.GetTimestamp();
                if (LastTs != 0) TickMs.Add((ts - LastTs) * 1000.0 / Stopwatch.Frequency);
                LastTs = ts;
                float f = GameState.FrameTimeMs;
                if (!float.IsNaN(f)) FrameMs.Add(f);
                double len = (To - From).Length;
                double t = (now - StartMs) / 1000.0;
                double s = Math.Min(1.0, len > 0 ? t * Speed / len : 1.0);
                Vec3 p = From + (To - From) * s;
                Vec3 dir = To - From;
                Vec3 fwd = Math.Abs(dir.X) + Math.Abs(dir.Z) > 1e-6 ? new Vec3(dir.X, 0, dir.Z).Normalized() : new Vec3(0, 0, 1);
                string why;
                S1Commands.SetSpectator(S1Math.SpectatorAt(Map, p, fwd, Vec3.UnitY), out why);
                if (s < 1.0) return false;
                Finish(t);
                return true;
            }

            private void Finish(double seconds)
            {
                var fs = new List<double>(FrameMs);
                fs.Sort();
                var tk = new List<double>(TickMs);
                tk.Sort();
                double med = fs.Count > 0 ? fs[fs.Count / 2] : double.NaN;
                int over5 = 0, hitch5 = 0;
                foreach (double v in fs) { if (v > 5) over5++; if (v > med + 5) hitch5++; }
                ReadStats a = ProviderCore.Stats;
                double p99 = fs.Count > 0 ? fs[(int)Math.Min(fs.Count - 1, Math.Ceiling(0.99 * fs.Count) - 1)] : double.NaN;
                double tkMax = tk.Count > 0 ? tk[tk.Count - 1] : double.NaN, tkP99 = tk.Count > 0 ? tk[(int)Math.Min(tk.Count - 1, Math.Ceiling(0.99 * tk.Count) - 1)] : double.NaN;
                string line = "DE.Terrain PERF done " + N(seconds) + " s at " + N(Speed) + " m/s: frames=" + fs.Count + " frameMs median=" + N(med) + " p99=" + N(p99) +
                              " max=" + N(fs.Count > 0 ? fs[fs.Count - 1] : double.NaN) + " frames>5ms=" + over5 + " hitches(>median+5ms)=" + hitch5 +
                              " (" + N(fs.Count > 0 ? 100.0 * hitch5 / fs.Count : double.NaN) + " %) updateIntervalMs p99=" + N(tkP99) + " max=" + N(tkMax) +
                              " readRange calls=" + (a.Calls - CallsBefore) + " p99us(session)=" + N(a.PercentileUs(0.99)) + " maxUs=" + N(a.MaxUs) +
                              " privateMB=" + N(TerrainTiles.PrivateBytes() / 1048576.0);
                DeLog.Info(line);
                Jsonl.WriteLine(S4Rows.Row("s4.perf", "seconds", seconds, "speed", Speed, "from", S4Rows.Arr(From), "to", S4Rows.Arr(To), "frames", fs.Count,
                                           "frameMedianMs", med, "frameP99Ms", p99, "frameMaxMs", fs.Count > 0 ? (object)fs[fs.Count - 1] : null, "framesOver5ms", over5,
                                           "hitchesOver5ms", hitch5, "fracHitches", fs.Count > 0 ? (object)((double)hitch5 / fs.Count) : null,
                                           "updateIntervalP99Ms", tkP99, "updateIntervalMaxMs", tkMax, "readCalls", a.Calls - CallsBefore,
                                           "readP99us", a.PercentileUs(0.99), "readMaxUs", a.MaxUs, "physP99us", ProviderCore.PhysicsStats.PercentileUs(0.99),
                                           "physMaxUs", ProviderCore.PhysicsStats.MaxUs, "private", TerrainTiles.PrivateBytes(),
                                           "privateBefore", TerrainTiles.RamBeforeBytes, "privateAfter", TerrainTiles.RamAfterBytes));
            }
        }

        public static Dictionary<string, object> StartPerf(IDictionary<string, object> args, string via)
        {
            if (!TerrainTiles.TerrainUp) return S4Commands.Fail("de.terrain perf: no terrain tiles");
            object fl;
            IDictionary<string, object> fa = args;
            if (args != null && args.TryGetValue("flyover", out fl) && fl is IDictionary<string, object>) fa = (IDictionary<string, object>)fl;
            double[] from = S4Args.Numbers(fa, "from", 3) ?? new[] { -1500.0, -40.0, -1500.0 };
            double[] to = S4Args.Numbers(fa, "to", 3) ?? new[] { 1500.0, -40.0, 1500.0 };
            double speed = Math.Max(1, Math.Min(1000, MiniJson.GetNumber(fa, "speed") ?? 100));
            var job = new PerfJob { Kind = "perf", Via = via, From = new Vec3(from[0], from[1], from[2]), To = new Vec3(to[0], to[1], to[2]), Speed = speed, CallsBefore = ProviderCore.Stats.Calls };
            string line = "DE.Terrain PERF start fly-over SN " + TerrainTiles.V(job.From) + " -> " + TerrainTiles.V(job.To) + " at " + N(speed) + " m/s (" +
                          N((job.To - job.From).Length / speed) + " s; spectator; via " + via + ")";
            return Begin(job, line);
        }

        // ================================================================================================= helpers

        private static string N(double v) { return double.IsNaN(v) ? "n/a" : v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static string FirstFrame(Exception e)
        {
            string st = e.StackTrace;
            if (string.IsNullOrEmpty(st)) return "";
            int nl = st.IndexOf('\n');
            return (nl > 0 ? st.Substring(0, nl) : st).Trim();
        }
    }
}
