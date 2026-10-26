using System;
using System.Collections.Generic;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S5 entity set, game side (CONTRACT §5.4 "Entity set (SE)"): each tick the top-most entities in a sphere of the
 /// leave radius around the SE camera are queried with
 /// <c>MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref BoundingSphereD, List&lt;MyEntity&gt;, MyEntityQueryType)</c>
 /// with <c>MyEntityQueryType.Both</c>. Kept: <c>MyCubeGrid</c> (not <c>IsPreview</c> — S7p-scene-above:
 /// except a projector's blueprint that <see cref="S7pProjection"/> admits —, not
 /// <c>MarkedForClose</c>, <c>GridScale</c> 1, in the scene; else skipped and logged once) and <c>MyFloatingObject</c>;
 /// the player's character is the <see cref="CharacterExport"/>'s, voxel maps and everything else are ignored. Grids
 /// enter and leave with their whole physical hierarchy: the root is
 /// <c>MyGridPhysicalHierarchy.Static.GetRoot(MyCubeGrid)</c>
 /// and the members of an in-set hierarchy are walked from its root with
 /// <c>ApplyOnChildren(MyCubeGrid, Action&lt;MyCubeGrid&gt)</c> (VERIFIED) wherever they are. Distances are to
 /// each entity's <c>PositionComp.WorldAABB</c>. The decisions are <see cref="EntitySetTracker"/>'s. Update thread.
 /// </summary>
    internal static class EntitySet
    {
        public static readonly EntitySetTracker Tracker = new EntitySetTracker();

        private static readonly List<MyEntity> _query = new List<MyEntity>(256);
        private static readonly List<EntityCandidate> _cands = new List<EntityCandidate>(256);
        private static readonly HashSet<long> _candIds = new HashSet<long>();
        private static readonly Dictionary<long, MyEntity> _byId = new Dictionary<long, MyEntity>();
        private static readonly HashSet<long> _roots = new HashSet<long>();
        private static readonly List<MyCubeGrid> _walk = new List<MyCubeGrid>(32);
        private static readonly HashSet<long> _loggedSkip = new HashSet<long>();
        private static Action<MyCubeGrid> _pushChild;
        private static bool _hierarchyMissing;

        public static long Queries { get; private set; }
        public static int LastQueryCount { get; private set; }

        /// <summary>The entity of an id seen this tick (or a live entity by id).</summary>
        public static MyEntity EntityOf(long id)
        {
            MyEntity e;
            if (_byId.TryGetValue(id, out e)) return e;
            return MyEntities.TryGetEntityById(id, out e) ? e : null;
        }

        /// <summary>The physical hierarchy root's entity id (the grid itself when it has no hierarchy or the call fails).</summary>
        public static long RootOf(MyCubeGrid grid)
        {
            if (grid == null) return 0;
            if (_hierarchyMissing) return grid.EntityId;
            try
            {
                MyGridPhysicalHierarchy h = MyGridPhysicalHierarchy.Static;
                MyCubeGrid r = h != null ? h.GetRoot(grid) : null;
                return r != null ? r.EntityId : grid.EntityId;
            }
            catch (Exception e)
            {
                _hierarchyMissing = true;
                DeLog.Info("DE.S5 MISSING MyGridPhysicalHierarchy.GetRoot (" + e.GetType().Name + ": " + e.Message + "): every grid is its own root (no subgrids)");
                return grid.EntityId;
            }
        }

        public static bool Mirrorable(MyCubeGrid g, out string why)
        {
            why = null;
            if (g == null || g.Closed || g.MarkedForClose) { why = "closed"; return false; }
            if (g.IsPreview && !S7pProjection.Admissible(g, out why)) return false; // S7p-scene-above (P7): a projector's blueprint is mirrored as a hologram; the build preview is not
            if (g.GridScale != 1f) { why = "GridScale " + g.GridScale; return false; }
            if (!g.InScene) { why = "not in the scene"; return false; }
            return true;
        }

        /// <summary>One tick: query, candidates, the tracker's decisions into <paramref name="added"/> / <paramref name="removed"/>.</summary>
        public static void Tick(ulong nowMs, Vector3D cam, bool allowAdd, List<long> added, List<KeyValuePair<long, string>> removed)
        {
            _query.Clear();
            _cands.Clear();
            _candIds.Clear();
            _byId.Clear();
            _roots.Clear();
            var sphere = new BoundingSphereD(cam, Tracker.LeaveM);
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, _query, MyEntityQueryType.Both);
            Queries++;
            LastQueryCount = _query.Count;
            for (int i = 0; i < _query.Count; i++)
            {
                MyEntity e = _query[i];
                var grid = e as MyCubeGrid;
                if (grid != null)
                {
                    string why;
                    if (!Mirrorable(grid, out why)) { Skip(grid, why); continue; }
                    long root = RootOf(grid);
                    if (root != grid.EntityId)
                    {
                        MyEntity re;
                        var rg = MyEntities.TryGetEntityById(root, out re) ? re as MyCubeGrid : null;
                        if (rg == null || !Mirrorable(rg, out why)) { Skip(grid, "its hierarchy root " + root + " is not mirrored: " + (why ?? "not found")); continue; }
                    }
                    Add(grid, root, S5EntityKind.Grid, cam);
                    _roots.Add(root);
                    continue;
                }
                var fo = e as MyFloatingObject;
                if (fo != null && !fo.Closed && !fo.MarkedForClose) Add(fo, fo.EntityId, S5EntityKind.FloatingObject, cam);
            }
            // whole hierarchies: the members of in-set (or just seen) hierarchies, wherever they are
            foreach (EntitySetTracker.Member m in Tracker.Members) if (m.Kind == S5EntityKind.Grid) _roots.Add(m.RootId);
            foreach (long root in _roots) WalkHierarchy(root, cam);
            Tracker.Update(nowMs, _cands, allowAdd, added, removed);
        }

        private static void Add(MyEntity e, long root, S5EntityKind kind, Vector3D cam)
        {
            if (!_candIds.Add(e.EntityId)) return;
            BoundingBoxD b = e.PositionComp.WorldAABB;
            double d = EntitySetTracker.BoxDistance(cam.X, cam.Y, cam.Z, b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);
            _cands.Add(new EntityCandidate(e.EntityId, root, kind, d));
            _byId[e.EntityId] = e;
        }

        private static void WalkHierarchy(long rootId, Vector3D cam)
        {
            MyEntity re;
            var root = MyEntities.TryGetEntityById(rootId, out re) ? re as MyCubeGrid : null;
            string why;
            if (root == null || !Mirrorable(root, out why)) return;
            if (_hierarchyMissing) { Add(root, rootId, S5EntityKind.Grid, cam); return; }
            MyGridPhysicalHierarchy h = MyGridPhysicalHierarchy.Static;
            if (h == null) return;
            if (_pushChild == null) _pushChild = c => _walk.Add(c);
            _walk.Clear();
            _walk.Add(root);
            for (int i = 0; i < _walk.Count && i < 4096; i++)
            {
                MyCubeGrid g = _walk[i];
                if (g == null || !Mirrorable(g, out why)) continue;
                Add(g, rootId, S5EntityKind.Grid, cam);
                try { h.ApplyOnChildren(g, _pushChild); }
                catch (Exception e)
                {
                    _hierarchyMissing = true;
                    DeLog.Info("DE.S5 MISSING MyGridPhysicalHierarchy.ApplyOnChildren (" + e.GetType().Name + ": " + e.Message + "): hierarchies are not walked");
                    return;
                }
            }
        }

        private static void Skip(MyCubeGrid g, string why)
        {
            if (_loggedSkip.Count > 4096) _loggedSkip.Clear();
            if (_loggedSkip.Add(g.EntityId)) DeLog.Info("DE.S5 grid " + g.EntityId + " not mirrored: " + why + " (logged once)");
        }
    }
}
