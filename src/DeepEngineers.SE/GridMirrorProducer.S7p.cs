using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Sandbox.Game.Weapons;
using VRage.Game.Entity;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7p-scene-above in the S5 producer (ROADMAP "### S7p Polish pass" P1, P7; CONTRACT §5.4 "S7p scene-above"):
 /// <list type="bullet">
 /// <item>P1 missiles: every tick (with the entity set) the missiles in flight within
 /// <see cref="S7pScene.MissileRadiusM"/> of the camera (<c>MyMissiles.Static.GetAllMissilesInSphere(ref BoundingSphereD,
 /// List&lt;MyEntity&gt)</c>, public, VERIFIED <c>MyMissiles.cs</c>; the session component's own missile tree), the
 /// nearest <see cref="S7pScene.MaxMissiles"/> (over: counted), ride the S5 stream as floating entities (their own model,
 /// GridAdd FloatingObject | Missile, a C2 pose every tick, EntityRemove when they explode, leave the sphere or the cap);
 /// Subnautica adds the trail. Only while the route mirrors missiles (<c>de.s7p {above}</c> not off).</item>
 /// <item>P7 projections: a projector's blueprint enters through the entity set (<see cref="EntitySet.Mirrorable"/> asks
 /// <see cref="S7pProjection"/>), is marked here (GridAdd Projection bit, the exporter skips blocks SE hides) and leaves
 /// at once when the projector stops projecting, turns off or the toggle is off.</item>
 /// <item>The solid list: the S6 collision skins and the S7 room sets never see a projection or a missile.</item>
 /// </list>
 /// Update thread; every step guarded (an exception logs once and leaves S5 running).
 /// </summary>
    internal static partial class GridMirrorProducer
    {
        private static readonly List<MyEntity> _s7pQuery = new List<MyEntity>(64);
        private static MyEntity[] _s7pEnts = new MyEntity[64];
        private static long[] _s7pIds = new long[64];
        private static double[] _s7pDist = new double[64];
        private static readonly int[] _s7pPick = new int[S7pScene.MaxMissiles];
        private static readonly HashSet<long> _s7pWanted = new HashSet<long>();
        private static readonly List<long> _s7pDrop = new List<long>();
        private static readonly List<string> _s7pDropWhy = new List<string>();
        private static readonly List<MirroredGrid> _s7pSolid = new List<MirroredGrid>();
        private static int _s7pProjections;
        private static bool _s7pErrorLogged;

        /// <summary>P7: projections mirrored now (the admission cap).</summary>
        internal static int ProjectionCount { get { return _s7pProjections; } }

        /// <summary>P7: the grid is mirrored as a projection.</summary>
        internal static bool IsProjectionMirrored(long id)
        {
            MirroredGrid g;
            return Grids.TryGetValue(id, out g) && g.Projection;
        }

        /// <summary>S5PreTick hook, after the entity set: projections pruned, missiles in and out, the 1 Hz s7p.scene row.</summary>
        private static void S7pPreTick(ulong nowMs, Vector3D cam, bool allowAdd)
        {
            try
            {
                S7pSceneSe.EnsureInit();
                S7pPruneProjections();
                S7pMissiles(cam, allowAdd);
                S7pSceneSe.RowIfDue(nowMs);
            }
            catch (Exception e)
            {
                if (_s7pErrorLogged) return;
                _s7pErrorLogged = true;
                DeLog.Info("DE.S7p producer step error " + e.GetType().Name + ": " + e.Message + " (logged once; S5 keeps running)\n" + e.StackTrace);
            }
        }

        /// <summary>RegisterS5 hook: a preview grid that got here was admitted by <see cref="S7pProjection"/>.</summary>
        private static void S7pOnRegistered(MirroredGrid g)
        {
            if (g == null || g.Grid == null || !g.Grid.IsPreview) return;
            g.Projection = true;
            if (g.S5 != null) g.S5.Projection = true;
            _s7pProjections++;
            S7pSceneSe.ProjectionEnters++;
            DeLog.Info("DE.S7p projection " + g.Id + " mirrored (" + g.Grid.BlocksCount + " blocks, projector " +
                       (g.Grid.Projector != null ? g.Grid.Projector.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?") + "): hologram in Subnautica");
        }

        private static void S7pPruneProjections()
        {
            _s7pDrop.Clear();
            _s7pDropWhy.Clear();
            int n = 0;
            foreach (MirroredGrid g in Grids.Values)
            {
                if (!g.Projection) continue;
                n++;
                string why;
                if (g.Grid == null || g.Grid.Closed || g.Grid.MarkedForClose) continue; // PruneClosed sends its EntityRemove
                if (S7pProjection.Admissible(g.Grid, out why)) continue;
                _s7pDrop.Add(g.Id);
                _s7pDropWhy.Add(why);
            }
            for (int i = 0; i < _s7pDrop.Count; i++)
            {
                UnregisterS5(_s7pDrop[i], _s7pDropWhy[i]);
                EntitySet.Tracker.Remove(_s7pDrop[i]);
                S7pSceneSe.ProjectionLeaves++;
                n--;
            }
            _s7pProjections = n;
            S7pSceneSe.ProjectionsMirrored = n;
        }

        private static void S7pMissiles(Vector3D cam, bool allowAdd)
        {
            _s7pWanted.Clear();
            int inSphere = 0;
            MyMissiles ms = MyMissiles.Static;
            if (S5Active && ms != null && S7pScene.MirrorsMissiles(S7pSceneSe.Above))
            {
                _s7pQuery.Clear();
                var sphere = new BoundingSphereD(cam, S7pScene.MissileRadiusM);
                ms.GetAllMissilesInSphere(ref sphere, _s7pQuery);
                if (_s7pQuery.Count > _s7pIds.Length)
                {
                    int cap = Math.Max(_s7pQuery.Count, _s7pIds.Length * 2);
                    _s7pEnts = new MyEntity[cap];
                    _s7pIds = new long[cap];
                    _s7pDist = new double[cap];
                }
                for (int i = 0; i < _s7pQuery.Count; i++)
                {
                    MyEntity e = _s7pQuery[i];
                    if (!(e is MyMissile) || e.Closed || e.MarkedForClose || e.Model == null) continue;
                    _s7pEnts[inSphere] = e;
                    _s7pIds[inSphere] = e.EntityId;
                    _s7pDist[inSphere] = Vector3D.Distance(e.PositionComp.GetPosition(), cam);
                    inSphere++;
                }
                S7pSceneSe.MissilesSeen += inSphere;
                int dropped;
                int k = S7pMissileRules.Select(_s7pIds, _s7pDist, inSphere, S7pScene.MaxMissiles, _s7pPick, out dropped);
                S7pSceneSe.MissileCapDrops += dropped;
                for (int i = 0; i < k; i++)
                {
                    MyEntity e = _s7pEnts[_s7pPick[i]];
                    long id = e.EntityId;
                    _s7pWanted.Add(id);
                    MirroredGrid g;
                    if (Grids.TryGetValue(id, out g)) continue;
                    if (!allowAdd) continue;
                    RegisterS5(e, "missile", false, null);
                    if (Grids.TryGetValue(id, out g))
                    {
                        g.Missile = true;
                        S7pSceneSe.MissileEnters++;
                    }
                }
                for (int i = 0; i < inSphere; i++) _s7pEnts[i] = null;
            }
            S7pSceneSe.MissilesInSphere = inSphere;
            _s7pDrop.Clear();
            int mirrored = 0;
            foreach (MirroredGrid g in Grids.Values)
            {
                if (!g.Missile) continue;
                if (_s7pWanted.Contains(g.Id)) mirrored++;
                else _s7pDrop.Add(g.Id);
            }
            for (int i = 0; i < _s7pDrop.Count; i++)
            {
                UnregisterS5(_s7pDrop[i], "missile gone (exploded, out of " + S7pScene.MissileRadiusM + " m, over the cap or route " + S7pScene.Name(S7pSceneSe.Above) + ")");
                S7pSceneSe.MissileLeaves++;
            }
            S7pSceneSe.MissilesMirrored = mirrored;
        }

        /// <summary>The ordered grids without projections and missiles (the S6 skins and the S7 rooms take this list).</summary>
        private static List<MirroredGrid> S7pSolid(List<MirroredGrid> ordered)
        {
            _s7pSolid.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                MirroredGrid g = ordered[i];
                if (g.Projection || g.Missile) { S7pSceneSe.SolidFiltered++; continue; }
                if (g.Impostor) continue; // S7t-se-composite (T3): an impostor has no collision skin and no room set
                _s7pSolid.Add(g);
            }
            return _s7pSolid;
        }

        /// <summary>S5Forget hook (the world went away): the spin state and the counters of the mirrored sets.</summary>
        private static void S7pForget(string why)
        {
            SubpartSpin.Forget();
            _s7pProjections = 0;
            _s7pSolid.Clear();
            S7pSceneSe.ProjectionsMirrored = 0;
            S7pSceneSe.MissilesMirrored = 0;
        }
    }
}
