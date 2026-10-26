using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// The 34 DE terrain tiles (CONTRACT §3 <c>terrain.*</c> rows, §3.1 "Tile creation"; ARCHITECTURE §5.6). Driven by
 /// the plugin's session component (<c>DeepEngineersSession</c>, <c>[MySessionComponentDescriptor]</c>):
 /// <list type="bullet">
 /// <item><see cref="OnBeforeStart"/> right after <c>SiteBinding.OnBeforeStart</c> (C read first; one component, fixed
 /// order; game thread, after <c>MyEntities.Load</c>, before the first simulation step): the terrain gate
 /// (<see cref="TerrainGate.Evaluate"/>, conditions 1–6), then condition 7: the source opened, the seven depth-band
 /// materials resolved by name (<c>MyDefinitionManager.Static.GetVoxelMaterialDefinition(string)</c>.Index, fallback
 /// Stone_01, then <c>GetDefaultVoxelMaterialDefinition</c>), and per tile <c>new MyOctreeStorage(provider, new
 /// Vector3I(1024))</c> + <c>MyWorldGenerator.AddVoxelMap(name, storage, MatrixD.CreateTranslation(tileMin + 512),
 /// entityId, lazyPhysics: true, useVoxelOffset: false)</c>, then <c>map.Save = false</c> in the same call. All or
 /// nothing: on any failure every tile already created is closed, <c>DE.Terrain REFUSE</c> and the fail-safe.</item>
 /// <item>After the 34 tiles: <see cref="SnPrefetcher"/> indexes and pre-faults the batch files under the tiles on a
 /// background thread, nearest the character first, and runs the I/O retries (finding 3).</item>
 /// <item><see cref="OnUpdateBeforeSimulation"/>: <c>DE.Terrain STATS</c> every 10 s, the fail-safe's HUD retry, and the
 /// terrain health check: a batch bad for the session, a read answered without data
 /// while its batch waited for an I/O retry, or a provider read that threw turns the fail-safe on for the rest of
 /// the session (the tiles stay; saving is blocked, CONTRACT §7).</item>
 /// <item><see cref="OnUnloadData"/>: drops the session's reference on the source (SE closes the tiles with the world:
 /// <c>BeforeDelete → Storage.Close → provider Close</c>).</item>
 /// </list>
 /// The tiles need only C (the frozen Rs of site v1: <c>SiteMap.ForSiteV1(C)</c>), never the Water Mod, the display
 /// bridge or Subnautica. Game thread only.
 /// </summary>
    internal static class TerrainTiles
    {
        internal sealed class TileInfo
        {
            public int I, J, K;
            public long EntityId;
            /// <summary>tileMin = C + the lattice min corner (where the provider samples).</summary>
            public Vec3 MinWorld;
            /// <summary>Where the map's min corner was placed (MinWorld, or +2 m radially under S4.M2).</summary>
            public Vec3 PlaceMin;
            public MyVoxelMap Map;
            public SnTerrainProvider Provider;
        }

        public const ulong StatsIntervalMs = 10000;

        private static readonly List<TileInfo> _tiles = new List<TileInfo>(TerrainLattice.TileCount);
        private static SnOctreeSource _source;
        private static SiteMap _map;
        private static ISnField _field;
        private static MaterialBands _bands;
        private static readonly Dictionary<string, int> _materials = new Dictionary<string, int>(StringComparer.Ordinal);
        private static volatile bool _terrainUp;
        private static ulong _nextStatsMs;
        private static long _ramBefore, _ramAfter;
        private static double _createMs;
        private static SnPrefetcher _prefetch;
        private static uint _autosaveMinutes = uint.MaxValue;

        /// <summary><c>seFlags.terrainUp</c> (bit 1).</summary>
        public static bool TerrainUp { get { return _terrainUp; } }
        public static IReadOnlyList<TileInfo> Tiles { get { return _tiles; } }
        /// <summary>The map the tiles were sampled with (null while no tiles).</summary>
        public static SiteMap Map { get { return _map; } }
        public static SnOctreeSource Source { get { return _source; } }
        public static ISnField Field { get { return _field; } }
        public static MaterialBands Bands { get { return _bands; } }
        public static IDictionary<string, int> Materials { get { return _materials; } }
        public static long RamBeforeBytes { get { return _ramBefore; } }
        public static long RamAfterBytes { get { return _ramAfter; } }
        public static double CreateMs { get { return _createMs; } }
        /// <summary>The background first touch of the batches under the tiles (null before the tiles).</summary>
        public static SnPrefetcher Prefetch { get { return _prefetch; } }
        /// <summary>
 /// This world's <c>Settings.AutoSaveInMinutes</c>, read in <c>BeforeStart</c> (<c>uint.MaxValue</c> when unknown): the
 /// throwaway-copy test of S4.M4, M9 and M10 (<see cref="TerrainLattice.ThrowawayRefusal"/>). Read by the guards'
 /// prefixes on any thread.
 /// </summary>
        public static uint AutosaveMinutes { get { return _autosaveMinutes; } }

        public static string StorageName(int i, int j, int k) { return TerrainLattice.StorageName(i, j, k); }

        // ======================================================================================== session callbacks

 /// <summary>
 /// S10 round 4 (the clipmap wait: SE's loading screen waits for the clipmaps of every voxel map present at its end; with the
 /// 34 tiles 19.7 s median vs 3.1 s without): env DEEPENGINEERS_S10_TILESLATE on creates the same tiles, by the same code, at
 /// SE's first UpdateBeforeSimulation instead of in BeforeStart, so the loading screen waits for the planet only and the
 /// tiles build under the curtain. Default OFF until a scripted build proves the loaded look and the meshes identical
 /// nothing the providers return changes.
 /// </summary>
        public static readonly bool TilesLate = S10AllocCuts.Parse(Environment.GetEnvironmentVariable("DEEPENGINEERS_S10_TILESLATE"), false);
        private static bool _latePending;
        private static long _lateArmedTs;

        public static void OnBeforeStart()
        {
            if (TilesLate)
            {
                _latePending = true;
                _lateArmedTs = System.Diagnostics.Stopwatch.GetTimestamp();
                DeLog.Info("DE.Terrain tiles late (DEEPENGINEERS_S10_TILESLATE): the tiles are created at SE's first update, after its loading screen");
                return;
            }
            OnBeforeStartNow();
        }

        private static void OnBeforeStartNow()
        {
            try { OnBeforeStartCore(); }
            catch (Exception e)
            {
                // never fail-open: an unexpected failure in a DE world refuses the terrain and turns the fail-safe on
                bool deWorld = false;
                try { string w; deWorld = SiteBinding.PlanetCount == 1 && ActivationGate.Peek(out w); } catch (Exception) { }
                DeLog.Info("DE.Error terrain BeforeStart " + e.GetType().Name + ": " + e.Message + (deWorld ? " (DE world: REFUSE and fail-safe)" : ""));
                if (!deWorld) return;
                try { Refuse("exception", e.GetType().Name + ": " + e.Message); }
                catch (Exception) { TerrainFailSafe.Activate("REFUSE exception " + e.GetType().Name); }
            }
        }

        private static void OnBeforeStartCore()
        {
            ReleaseSession("BeforeStart (an earlier session's UnloadData did not run)");
            TerrainGate.ResetSession();
            TerrainFailSafe.ResetSession();
            ProviderCore.ResetStats();                       // S4  STATS per world
            TerrainGuards.ResetSession();
            uint autosave = uint.MaxValue;
            try { autosave = MySession.Static.Settings.AutoSaveInMinutes; } catch (Exception) { }
            _autosaveMinutes = autosave;
            string condition, reason;
            TerrainGate.Result r = TerrainGate.Evaluate(out condition, out reason);
            if (r != TerrainGate.Result.Ok)
            {
                Jsonl.WriteLine(S4Rows.Row("s4.gate", "result", r.ToString().ToUpperInvariant(), "condition", condition, "reason", reason,
                                           "tiles", 0, "siteVersion", SiteV1.SiteVersion, "worldEpoch", (long)Phases.WorldEpoch));
                if (r == TerrainGate.Result.Refuse) TerrainFailSafe.Activate("REFUSE " + condition + " " + reason);
                return;
            }
            string failCondition = null, failReason = null;
            try { CreateTiles(out failCondition, out failReason); }
            catch (Exception e)
            {
                failCondition = "tiles";
                failReason = "exception " + e.GetType().Name + ": " + e.Message;
            }
            if (failCondition != null) Refuse(failCondition, failReason);
        }

        public static void OnUpdateBeforeSimulation()
        {
            if (_latePending)
            {
                _latePending = false;   // S10 round 4: the deferred BeforeStart, once, on the first update
                double waitedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _lateArmedTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                OnBeforeStartNow();
                DeLog.Info("DE.Terrain tiles late: created at the first update, " + waitedMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms after BeforeStart (terrainUp=" + _terrainUp + ")");
            }
            ulong now = Win32.TickCount64();
            if (_terrainUp && !TerrainFailSafe.Active) CheckHealth();
            TerrainFailSafe.Tick(now);
            if (!_terrainUp) return;
            if (_nextStatsMs == 0) { _nextStatsMs = now + StatsIntervalMs; return; }
            if (now < _nextStatsMs) return;
            _nextStatsMs = now + StatsIntervalMs;
            LogStats();
        }

        public static void OnUnloadData()
        {
            _latePending = false; // S10 round 4: a world left before its first update creates nothing
            try { S8aOverlay.OnUnload(); } catch (Exception e) { DeLog.Info("DE.S8a unload " + e.GetType().Name + ": " + e.Message); } // S8a (B2 hook): subscriptions dropped, converter stopped
            if (_terrainUp || _source != null || TerrainFailSafe.Active)
                DeLog.Info("DE.Terrain UnloadData worldEpoch=" + Phases.WorldEpoch + " terrainUp=" + _terrainUp + " failSafe=" + TerrainFailSafe.Active +
                           " (SE closes the tiles with the world; the session reference on the source is dropped)");
            if (_terrainUp) LogStats();
            ReleaseSession(null);
            TerrainGate.ResetSession();
            TerrainFailSafe.ResetSession();
        }

        /// <summary>
 /// Game thread, every update while the tiles are up and the fail-safe is off (three Interlocked reads): a batch
 /// bad for the session (malformed, or unreadable through every I/O retry), a read answered "no data" because its
 /// batch waited for an I/O retry (SE may have cached that empty cell), or a provider ReadRange that threw (the
 /// target may hold a partial write) turns the fail-safe on for the rest of the session. "Missing batch = empty"
 /// (CONTRACT §3) means an absent file, never an unreadable one.
 /// </summary>
        private static void CheckHealth()
        {
            SnOctreeSource src = _source;
            long bad = src != null ? src.BadBatchCount : 0, failed = src != null ? src.FailedReads : 0;
            long errors = Interlocked.Read(ref ProviderCore.ReadErrors);
            if (bad == 0 && failed == 0 && errors == 0) return;
            string reason = bad > 0 || failed > 0
                ? "BATCH-BAD badBatches=" + bad + " failedReads=" + failed + " (a batch under the tiles could not be read: see DE.Terrain BATCH-BAD / BATCH-IO)"
                : "provider ReadRange exception n=" + errors + " (see DE.Terrain WARN provider)";
            TerrainFailSafe.Activate(reason);
            LogStats();
        }

        private static void ReleaseSession(string why)
        {
            if (why != null && (_source != null || _tiles.Count > 0)) DeLog.Info("DE.Terrain releasing a stale session in " + why);
            SnPrefetcher pf = _prefetch;
            _prefetch = null;
            if (pf != null) pf.Cancel();                     // never blocks; the thread also stops once the source closes
            _terrainUp = false;
            _tiles.Clear();
            _map = null;
            _field = null;
            _bands = null;
            _materials.Clear();
            _nextStatsMs = 0;
            SnOctreeSource s = _source;
            _source = null;
            if (s != null)
            {
                try { s.Release(); }
                catch (Exception e) { DeLog.Info("DE.Terrain WARN source release " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // ================================================================================================== create

 /// <summary>Condition 7 (CONTRACT §1.2). NoInlining: a missing SE member fails this method's JIT, caught by the caller.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CreateTiles(out string failCondition, out string failReason)
        {
            failCondition = null;
            failReason = null;
            long t0 = Stopwatch.GetTimestamp();
            Vec3 c = SiteBinding.C;
            SnSourceManifest manifest = TerrainGate.MeasureOnce();
            string err;
            SnOctreeSource src = SnOctreeSource.Open(manifest.OctreeDir, out err);
            if (src == null) { failCondition = "source"; failReason = "open failed: " + (err ?? "unknown"); return; }
            _source = src; // the session's reference (refcount 1), dropped in UnloadData
            _map = SiteMap.ForSiteV1(c);

            string matWhy;
            if (!ResolveMaterials(out matWhy)) { failCondition = "materials"; failReason = matWhy; return; }
            _field = new SnField(src);
            if (CraterOres != null)
            {

                // octrees) on every side; a pocket whose grown box the field proves full needs no probe
                ISnField f = _field;
                CraterOres.AllSolidIn = (lx, ly, lz, hx, hy, hz) => f.ClassifySn(lx, ly, lz, hx, hy, hz) == ContentClass.Full;
                CraterOres.SolidAt = (x, y, z) => { bool same; return f.Sample(x, y, z, out same) > 0f; };
            }

            uint autosave = 0;
            try { autosave = MySession.Static.Settings.AutoSaveInMinutes; } catch (Exception) { autosave = uint.MaxValue; }
            bool saveTrue = false, randomIds = false;
            if (S4Mutants.SaveTrue)
            {
                string refuse = TerrainLattice.ThrowawayRefusal("S4.M4", autosave);
                saveTrue = refuse == null;
                DeLog.Info(refuse ?? "DE.Mutant S4.M4 ACTIVE: the tiles are created with Save = true (throwaway copy, autosave off)");
            }
            if (S4Mutants.RandomIds)
            {
                string refuse = TerrainLattice.ThrowawayRefusal("S4.M10", autosave);
                randomIds = refuse == null;
                DeLog.Info(refuse ?? "DE.Mutant S4.M10 ACTIVE: the tiles are created with random entity ids (throwaway copy, autosave off)");
                if (!randomIds)
                {
                    // the gate skipped condition 6 for S4.M10: check it now that the mutant is refused
                    string taken = FirstTakenIdNow();
                    if (taken != null) { failCondition = "ids"; failReason = taken; return; }
                }
            }
            if (S4Mutants.TileShift2m) DeLog.Info("DE.Mutant S4.M2 ACTIVE: every tile placed +2 m along U0 (samples unchanged)");

            _ramBefore = PrivateBytes();
            double worstCorner = 0;
            var created = new List<TileInfo>(TerrainLattice.TileCount);
            SnTerrainProvider pendingProvider = null;
            MyOctreeStorage pendingStorage = null;
            long pendingId = 0;
            try
            {
                foreach (int[] slot in SiteV1.TilesCreated())
                {
                    int i = slot[0], j = slot[1], k = slot[2];
                    long id = SiteV1.TileEntityId(i, j, k);
                    Vec3 tileMin = TerrainLattice.TileMin(c, i, j, k);
                    Vec3 place = TerrainLattice.PlaceMin(tileMin, S4Mutants.TileShift2m);
                    var sampler = new TileSampler(_field, _map, tileMin, _bands);
                    var provider = new SnTerrainProvider(src, sampler, i, j, k, id);
                    pendingProvider = provider;
                    pendingId = randomIds ? 0L : id;
                    int mx, my, mz;
                    bool hasCell = TerrainLattice.MutantCell(_map, tileMin, out mx, out my, out mz);
                    if (hasCell) provider.Core.SetMutantCell(mx, my, mz);
                    var storage = new MyOctreeStorage(provider, new Vector3I(SiteV1.Tile));
                    pendingStorage = storage;
                    Vec3 tr = TerrainLattice.WorldTranslation(place);
                    MatrixD wm = MatrixD.CreateTranslation(new Vector3D(tr.X, tr.Y, tr.Z));
                    string name = StorageName(i, j, k);
                    MyVoxelMap map = MyWorldGenerator.AddVoxelMap(name, storage, wm, randomIds ? 0L : id, lazyPhysics: true, useVoxelOffset: false);
                    if (map == null)
                    {
                        try { storage.Close(); } catch (Exception) { provider.Close(); }
                        pendingProvider = null;
                        pendingStorage = null;
                        CloseCreated(created);
                        failCondition = "tiles";
                        failReason = "AddVoxelMap returned null for tile " + i + "," + j + "," + k + " id=" + TerrainLattice.IdHex(id) +
                                     " (an entity with that id exists: 'CRITICAL-VOXEL MAP' in the SE log); " + created.Count + " tile(s) closed";
                        return;
                    }
                    // CONTRACT §3 terrain.saved: set in the same game-thread call that added it (nothing can save in between)
                    map.Save = saveTrue;
                    var info = new TileInfo { I = i, J = j, K = k, EntityId = map.EntityId, MinWorld = tileMin, PlaceMin = place, Map = map, Provider = provider };
                    created.Add(info);
                    pendingProvider = null;
                    pendingStorage = null;
                    Vector3D plbc = map.PositionLeftBottomCorner;
                    double dCorner = Math.Max(Math.Abs(plbc.X - place.X), Math.Max(Math.Abs(plbc.Y - place.Y), Math.Abs(plbc.Z - place.Z)));
                    worstCorner = Math.Max(worstCorner, dCorner);
                    string line = "DE.Terrain TILE " + i + "," + j + "," + k + " id=" + TerrainLattice.IdHex(map.EntityId) + " min=" + V(tileMin) +
                                  (S4Mutants.TileShift2m ? " placedMin=" + V(place) : "") + " name=" + name + " save=" + map.Save +
                                  " |PositionLeftBottomCorner-min|=" + G(dCorner) + " siteVersion=" + SiteV1.SiteVersion +
                                  (randomIds ? " (S4.M10 random id; deterministic " + TerrainLattice.IdHex(id) + ")" : "");
                    DeLog.Info(line);
                    Jsonl.WriteLine(S4Rows.Row("s4.tile", "i", i, "j", j, "k", k, "id", TerrainLattice.IdHex(map.EntityId), "idDec", map.EntityId,
                                               "deterministicId", TerrainLattice.IdHex(id), "min", S4Rows.Arr(tileMin), "placedMin", S4Rows.Arr(place),
                                               "name", name, "save", map.Save, "cornerErr", dCorner, "mutantCell", hasCell ? (object)new object[] { mx, my, mz } : null,
                                               "siteVersion", SiteV1.SiteVersion));
                }
            }
            catch (Exception)
            {
                // all or nothing (CONTRACT §1.2 condition 7): close every tile made so far and the half-made one, then let
 // OnBeforeStartCore refuse (REFUSE tiles exception …, fail-safe on: saving is blocked for the session)
                CloseCreated(created);
                ClosePending(pendingProvider, pendingStorage, pendingId);
                throw;
            }
            _tiles.AddRange(created);
            _ramAfter = PrivateBytes();
            _createMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _terrainUp = true;
            TerrainGate.Record(TerrainGate.Result.Ok, "", "34 tiles");
            CheckVersion();
            StartPrefetch(src, c);
            S8aOverlay.OnTilesCreated(); // S8a (B2 hook): RangeChanged subscriptions, the overlay restore, the drill converter (guarded inside; inert unless DEEPENGINEERS_S8A=on)
            string ok = "DE.Terrain OK tiles=" + _tiles.Count + " siteVersion=" + SiteV1.SiteVersion + " C=" + V(c) + " Rs=" + G(_map.Rs) +
                        " (frozen site-v1 Rs; the tiles need only C) createMs=" + _createMs.ToString("0.0", CultureInfo.InvariantCulture) +
                        " privateMB " + Mb(_ramBefore) + " -> " + Mb(_ramAfter) + " worstCornerErr=" + G(worstCorner) +
                        " materials=" + MaterialsText() + " source=" + manifest.OctreeDir + (S4Mutants.Active.Length > 0 ? " MUTANTS=" + S4Mutants.Active : "");
            DeLog.Info(ok);
            Jsonl.WriteLine(S4Rows.Row("s4.gate", "result", "OK", "condition", "", "reason", "34 tiles", "tiles", _tiles.Count, "siteVersion", SiteV1.SiteVersion,
                                       "worldEpoch", (long)Phases.WorldEpoch, "C", S4Rows.Arr(c), "createMs", _createMs, "privateBefore", _ramBefore,
                                       "privateAfter", _ramAfter, "worstCornerErr", worstCorner, "mutants", S4Mutants.Active));
        }

        private static void Refuse(string condition, string reason)
        {
            CloseCreated(new List<TileInfo>(_tiles));
            _tiles.Clear();
            _terrainUp = false;
            TerrainGate.Record(TerrainGate.Result.Refuse, condition, reason);
            TerrainGate.Decision d = TerrainGate.Refuse(condition, reason);
            DeLog.Info(d.Line + " (siteVersion=" + SiteV1.SiteVersion + " worldEpoch=" + Phases.WorldEpoch + ")");
            Jsonl.WriteLine(S4Rows.Row("s4.gate", "result", "REFUSE", "condition", condition, "reason", reason, "tiles", 0,
                                       "siteVersion", SiteV1.SiteVersion, "worldEpoch", (long)Phases.WorldEpoch));
            TerrainFailSafe.Activate("REFUSE " + condition + " " + reason);
            ReleaseSessionSourceOnly();
        }

        /// <summary>
 /// S4  index and pre-fault the batches under the tiles on a background thread, nearest the local
 /// character first (else the anchor column), so the physics step's blocking shape requests do not map files. The
 /// plan (which batches lie under a tile) is computed on that thread too.
 /// </summary>
        private static void StartPrefetch(SnOctreeSource src, Vec3 c)
        {
            try
            {
                SiteMap map = _map;
                var mins = new Vec3[_tiles.Count];
                for (int i = 0; i < mins.Length; i++) mins[i] = _tiles[i].MinWorld;
                Vec3 focus = TerrainLattice.MutantCellSn;
                string focusFrom = "anchor";
                try
                {
                    var ch = MySession.Static != null ? MySession.Static.LocalCharacter : null;
                    if (ch != null && !ch.Closed)
                    {
                        Vector3D p = ch.PositionComp.GetPosition();
                        focus = map.SeToSn(new Vec3(p.X, p.Y, p.Z));
                        focusFrom = "character";
                    }
                }
                catch (Exception) { }
                DeLog.Info("DE.Terrain PREFETCH planned from the " + focusFrom + " at SN " + V(focus) + " (background thread; C=" + V(c) + ")");
                _prefetch = SnPrefetcher.Start(src, () => TerrainLattice.PrefetchPlan(map, mins, focus), fault: true, lowPriority: true);
            }
            catch (Exception e) { DeLog.Info("DE.Terrain WARN prefetch not started " + e.GetType().Name + ": " + e.Message + " (first reads index their batches, as before)"); }
        }

        private static void ReleaseSessionSourceOnly()
        {
            SnOctreeSource s = _source;
            _source = null;
            if (s != null) { try { s.Release(); } catch (Exception) { } }
        }

        private static void CloseCreated(List<TileInfo> created)
        {
            foreach (TileInfo t in created)
            {
                try
                {
                    if (t.Map != null && !t.Map.Closed) t.Map.Close();
                    DeLog.Info("DE.Terrain closed tile " + t.I + "," + t.J + "," + t.K + " (all or nothing)");
                }
                catch (Exception e) { DeLog.Info("DE.Terrain WARN close tile " + t.I + "," + t.J + "," + t.K + " " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>
 /// The tile whose creation threw: a map AddVoxelMap may already have registered under its id is marked non-saved and
 /// closed (best effort; the fail-safe blocks saving anyway), else the storage and the provider are closed.
 /// </summary>
        private static void ClosePending(SnTerrainProvider provider, MyOctreeStorage storage, long id)
        {
            if (provider == null) return;
            try
            {
                VRage.ModAPI.IMyEntity e;
                if (id != 0 && VRage.MyEntityIdentifier.TryGetEntity(id, out e, false))
                {
                    var map = e as MyVoxelMap;
                    if (map != null && storage != null && ReferenceEquals(map.Storage, storage))
                    {
                        map.Save = false;
                        if (!map.Closed) map.Close();
                        DeLog.Info("DE.Terrain closed half-made tile " + provider.I + "," + provider.J + "," + provider.K + " (all or nothing)");
                        return;
                    }
                }
            }
            catch (Exception ex) { DeLog.Info("DE.Terrain WARN half-made tile " + ex.GetType().Name + ": " + ex.Message); }
            try { if (storage != null) storage.Close(); } catch (Exception) { }
            try { provider.Close(); } catch (Exception) { }
        }

        private static string FirstTakenIdNow()
        {
            foreach (int[] t in SiteV1.TilesCreated())
            {
                long id = SiteV1.TileEntityId(t[0], t[1], t[2]);
                if (VRage.MyEntityIdentifier.ExistsById(id)) return "tile " + t[0] + "," + t[1] + "," + t[2] + " id=" + TerrainLattice.IdHex(id) + " taken";
            }
            return null;
        }

        /// <summary>The seven depth-band materials by name (CONTRACT §3.1); false only when not even the default resolves.</summary>
        private static bool ResolveMaterials(out string why)
        {
            why = null;
            _materials.Clear();
            MyDefinitionManager dm = MyDefinitionManager.Static;
            if (dm == null) { why = "MyDefinitionManager.Static is null"; return false; }
            MyVoxelMaterialDefinition stone = dm.GetVoxelMaterialDefinition("Stone_01");
            MyVoxelMaterialDefinition dflt = null;
            try { dflt = dm.GetDefaultVoxelMaterialDefinition(); } catch (Exception) { }
            string[] names = MaterialBands.Names;
            var idx = new byte[names.Length];
            for (int n = 0; n < names.Length; n++)
            {
                MyVoxelMaterialDefinition d = dm.GetVoxelMaterialDefinition(names[n]);
                string used = names[n];
                if (d == null)
                {
                    d = stone ?? dflt;
                    used = stone != null ? "Stone_01" : dflt != null ? "default(" + dflt.Id.SubtypeName + ")" : null;
                    DeLog.Info("DE.Terrain WARN material " + names[n] + " not defined; using " + (used ?? "nothing"));
                    if (d == null) { why = "material " + names[n] + ": no Stone_01 and no default voxel material"; return false; }
                }
                idx[n] = d.Index;
                _materials[names[n]] = d.Index;
                if (used != names[n]) _materials[names[n] + "->" + used] = d.Index;
            }
            _bands = new MaterialBands(idx);

            // table by SN cube and the pockets under Subnautica's large deposits (S8aCraterOres); S8a on, DEEPENGINEERS_S8A_CRATER_ORES not off
            CraterOres = null;
            if (S8aOverlay.Enabled && S8aOverlay.EnvOn(EnvCraterOres, true))
            {
                var ore = new byte[S8aCraterOres.Rows.Length];
                int found = 0;
                for (int i = 0; i < ore.Length; i++)
                {
                    MyVoxelMaterialDefinition d = dm.GetVoxelMaterialDefinition(S8aCraterOres.Rows[i].Subtype);
                    ore[i] = d != null ? d.Index : (byte)255;
                    if (d == null) continue;
                    found++;
                    _materials[S8aCraterOres.Rows[i].Subtype] = d.Index;
                }
                CraterOres = new S8aCraterOreRule(ore);
                _bands.Ores = CraterOres;
                DeLog.Info("DE.Terrain S8a crater ores on: " + found + "/" + ore.Length + " ore materials resolved (" + EnvCraterOres + "); cube " +
                           S8aCraterOres.CellM + " m, pockets " + S8aCraterOres.PocketMinM + "-" + S8aCraterOres.PocketMaxM + " m");
            }
            return true;
        }

        /// <summary>S8a Stage E: the switch of the Subnautica map's ores (default on while S8a is on).</summary>
        public const string EnvCraterOres = "DEEPENGINEERS_S8A_CRATER_ORES";

        /// <summary>The tiles' ore rule (null: S8a off or the switch off).</summary>
        public static S8aCraterOreRule CraterOres { get; private set; }

        /// <summary>CONTRACT §1.2: another SE version only logs a warning; the terrain keeps its own gate.</summary>
        private static void CheckVersion()
        {
            try
            {
                if (VersionGate.Found.HasValue && VersionGate.Found.Value != VersionGate.Expected)
                    DeLog.Info("DE.Terrain WARN version SE " + VersionGate.Found.Value + " (pinned " + VersionGate.Expected + "); tiles created anyway (the terrain has its own gate)");
            }
            catch (Exception) { }
        }

        // =================================================================================================== stats

        public static void LogStats()
        {
            try
            {
                ReadStats a = ProviderCore.Stats, p = ProviderCore.PhysicsStats, cell = ProviderCore.PhysicsCellStats;
                SnSourceStats s = (_source != null ? _source.GetStats() : null) ?? new SnSourceStats();
                SnPrefetcher pf = _prefetch;
                var guards = new StringBuilder();
                for (int i = 0; i < TerrainPaths.Names.Length; i++)
                {
                    long n = TerrainGuards.Count(i);
                    if (n > 0) guards.Append(' ').Append(TerrainPaths.Names[i]).Append('=').Append(n);
                }
                string line = "DE.Terrain STATS tiles=" + _tiles.Count + " readRange calls=" + a.Calls + " p50us=" + N(a.PercentileUs(0.5)) +
                              " p99us=" + N(a.PercentileUs(0.99)) + " maxUs=" + N(a.MaxUs) + " physics calls=" + p.Calls + " p99us=" + N(p.PercentileUs(0.99)) +
                              " maxUs=" + N(p.MaxUs) + " cells=" + cell.Calls + " cellP99us=" + N(cell.PercentileUs(0.99)) + " cellMaxUs=" + N(cell.MaxUs) +
                              " batchesIndexed=" + s.BatchesIndexed + " batchesMapped=" + s.BatchesMapped + " bytesMapped=" + s.BytesMapped +
                              " badBatches=" + s.BadBatches + " ioFailures=" + s.IoFailures + " ioRetrying=" + s.IoRetrying + " failedReads=" + s.FailedReads +
                              " badTargets=" + ProviderCore.BadTargets + " readErrors=" + Interlocked.Read(ref ProviderCore.ReadErrors) + " privateMB=" + Mb(PrivateBytes()) +
                              " prefetch=" + (pf != null ? pf.StateName + " " + pf.Done + "/" + pf.Planned : "none") +
                              " editBlocked" + (guards.Length > 0 ? guards.ToString() : " none");
                DeLog.Info(line);
                Jsonl.WriteLine(S4Rows.Row("s4.stats", "tiles", _tiles.Count, "calls", a.Calls, "p50us", a.PercentileUs(0.5), "p99us", a.PercentileUs(0.99),
                                           "maxUs", a.MaxUs, "meanUs", a.MeanUs, "physCalls", p.Calls, "physP50us", p.PercentileUs(0.5), "physP99us", p.PercentileUs(0.99),
                                           "physMaxUs", p.MaxUs, "cellCalls", cell.Calls, "cellP50us", cell.PercentileUs(0.5), "cellP99us", cell.PercentileUs(0.99),
                                           "cellMaxUs", cell.MaxUs, "batchesIndexed", s.BatchesIndexed, "batchesMapped", s.BatchesMapped, "bytesMapped", s.BytesMapped,
                                           "badBatches", s.BadBatches, "ioFailures", s.IoFailures, "ioRetrying", s.IoRetrying, "ioRecovered", s.IoRecovered,
                                           "failedReads", s.FailedReads, "badTargets", ProviderCore.BadTargets, "readErrors", Interlocked.Read(ref ProviderCore.ReadErrors),
                                           "prefetch", pf != null ? pf.Text : null, "private", PrivateBytes(),
                                           "privateBefore", _ramBefore, "privateAfter", _ramAfter));
            }
            catch (Exception e) { DeLog.Info("DE.Terrain WARN stats " + e.GetType().Name + ": " + e.Message); }
        }

        // ================================================================================================= helpers

 /// <summary>Tile holding SE world point <paramref name="w"/> in its interior first, else any tile box holding it.</summary>
        public static TileInfo TileAt(Vec3 w)
        {
            TileInfo any = null;
            foreach (TileInfo t in _tiles)
            {
                Vec3 l = w - t.PlaceMin;
                if (l.X < 0 || l.Y < 0 || l.Z < 0 || l.X >= SiteV1.Tile || l.Y >= SiteV1.Tile || l.Z >= SiteV1.Tile) continue;
                bool interior = l.X >= SiteV1.InteriorMargin && l.Y >= SiteV1.InteriorMargin && l.Z >= SiteV1.InteriorMargin &&
                                l.X < SiteV1.Tile - SiteV1.InteriorMargin && l.Y < SiteV1.Tile - SiteV1.InteriorMargin && l.Z < SiteV1.Tile - SiteV1.InteriorMargin;
                if (interior) return t;
                if (any == null) any = t;
            }
            return any;
        }

        public static string MaterialsText()
        {
            var sb = new StringBuilder();
            foreach (var kv in _materials)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(kv.Key).Append(':').Append(kv.Value);
            }
            return sb.ToString();
        }

        public static long PrivateBytes()
        {
            try { using (var p = Process.GetCurrentProcess()) return p.PrivateMemorySize64; }
            catch (Exception) { return -1; }
        }

        private static string Mb(long b) { return b < 0 ? "?" : (b / 1048576.0).ToString("0", CultureInfo.InvariantCulture); }
        private static string N(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
        private static string G(double v) { return v.ToString("G6", CultureInfo.InvariantCulture); }
        internal static string V(Vec3 v)
        {
            return "(" + v.X.ToString("R", CultureInfo.InvariantCulture) + ", " + v.Y.ToString("R", CultureInfo.InvariantCulture) + ", " +
                   v.Z.ToString("R", CultureInfo.InvariantCulture) + ")";
        }
    }
}
