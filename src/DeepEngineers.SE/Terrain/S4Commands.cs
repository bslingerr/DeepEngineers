using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.Models;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// <c>de.terrain</c> (C11 and <c>/de terrain …</c>, dev builds; syntax <see cref="S4Args"/>), chained first in the
 /// plugin's ExtraCommand. Update thread only; never throws (failures answer <c>ok: false</c>).
 /// <list type="bullet">
 /// <item>status, gate (fresh manifest measurement, no tile change), tiles;</item>
 /// <item>hash {sn|se, half}: FNV-1a 64 of the LOD-0 content and material bytes read through
 /// <c>MyStorageBase.ReadRange(MyStorageData, MyStorageDataTypeFlags, int, Vector3I, Vector3I)</c> plus
 /// <c>MyStorageBase.IsRangeModified(ref BoundingBoxI)</c>, per tile holding the box (S4.N2's witness);</item>
 /// <item>crater {sn, radius} (<c>MyVoxelGenerator.MakeCrater</c>, the meteor path), edit {path, sn, radius, drill}
 /// (each guarded SE entry called on the tile; S4.N2);</item>
 /// <item>n1 (character feet vs the storage surface under the same column), n3 (the provider self-check on a live
 /// tile), at0b (in-SE A-T0b: a synthetic plane served by the provider and meshed by SE's own
 /// <c>MyPrecalcComponent.IsoMesher.Precalc</c>);</item>
 /// <item>at1/rays, bake, drop, walk, perf, stop: <see cref="S4Harness"/>.</item>
 /// </list>
 /// </summary>
    internal static class S4Commands
    {
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != "de.terrain") return null;
            string action = S4Args.ActionOf(args);
            try
            {
                switch (action)
                {
                    case "status": return Status();
                    case "gate": return Gate();
                    case "tiles": return Tiles();
                    case "hash": return Hash(args);
                    case "crater": return Crater(args, via);
                    case "edit": return Edit(args, via);
                    case "n1": return N1(args, via);
                    case "n3": return N3(args, via);
                    case "at0b": return At0b(args, via);
                    case "at1": return S4Harness.StartAt1(args, via);
                    case "bake": return S4Harness.Bake(args, via);
                    case "drop": return S4Harness.StartDrop(args, via);
                    case "walk": return S4Harness.StartWalk(args, via);
                    case "perf": return S4Harness.StartPerf(args, via);
                    case "stop": return S4Harness.Stop("de.terrain stop via " + via);
                    default: return Fail("de.terrain: unknown action '" + action + "' (" + S4Args.ChatUsage + ")");
                }
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.terrain " + action + " " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return Fail("de.terrain " + action + " failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Plugin Update (update thread): the asynchronous harness parts and the delayed re-hash of the last edit.</summary>
        public static void Tick(ulong nowMs, bool inWorld)
        {
            S4Harness.Tick(nowMs, inWorld);
            PendingEdit pe = _pendingEdit;
            if (pe == null) return;
            if (!inWorld || !TerrainTiles.TerrainUp) { _pendingEdit = null; return; }
            if (nowMs < pe.DueMs) return;
            _pendingEdit = null;
            try { FinishEdit(pe); }
            catch (Exception e) { DeLog.Info("DE.Terrain edit final failed " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>
 /// An edit's final witness, taken <see cref="EditSettleMs"/> after the call: SE completes some paths later (the
 /// voxel hand runs on <c>Parallel.Start</c> when <c>MyFakes.VOXELHAND_PARALLEL</c>; the ship mining system queues its
 /// cut-out), which only matters when a guard is removed (S4.M9).
 /// </summary>
        private sealed class PendingEdit
        {
            public string Path;
            public int PathIndex;
            public Vec3 W;
            public int Half;
            public List<BoxHash> Before;
            public long CountBefore;
            public ulong DueMs;
            public string Tile;
        }

        public const ulong EditSettleMs = 2000;
        private static PendingEdit _pendingEdit;
        private static Dictionary<string, object> _lastEditFinal;

        private static void FinishEdit(PendingEdit pe)
        {
            List<BoxHash> after = HashAround(pe.W, pe.Half);
            bool same = pe.Before.Count == after.Count, modified = false;
            for (int i = 0; i < after.Count; i++)
            {
                modified |= after[i].Modified;
                if (i < pe.Before.Count && (pe.Before[i].Content != after[i].Content || pe.Before[i].Material != after[i].Material)) same = false;
            }
            long count = TerrainGuards.Count(pe.PathIndex);
            bool blocked = count > pe.CountBefore;
            string line = "DE.Terrain edit " + pe.Path + " final (+" + EditSettleMs + " ms) tile=" + pe.Tile + ": blocked=" + blocked + " count=" + count +
                          " hashUnchanged=" + same + " modified=" + modified + (S4Mutants.GuardOff(pe.PathIndex) ? " (S4.M9." + pe.Path + " ON)" : "");
            DeLog.Info(line);
            List<object> hb = HashList(pe.Before), ha = HashList(after);
            Jsonl.WriteLine(S4Rows.Row("s4.edit", "path", pe.Path, "final", true, "sn", S4Rows.Arr(TerrainTiles.Map.SeToSn(pe.W)), "tile", pe.Tile, "blocked", blocked,
                                       "count", count, "hashUnchanged", same, "modified", modified, "hashBefore", hb, "hashAfter", ha, "guardOff", S4Mutants.GuardOff(pe.PathIndex)));
            _lastEditFinal = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "path", pe.Path }, { "blocked", blocked }, { "count", count }, { "hashBefore", hb }, { "hashAfter", ha }, { "hashUnchanged", same },
                { "modified", modified }, { "line", line },
            };
        }

        // ================================================================================================== status

        private static Dictionary<string, object> Status()
        {
            SnSourceManifest m = TerrainGate.Manifest;
            SnOctreeSource src = TerrainTiles.Source;
            SnSourceStats s = (src != null ? src.GetStats() : null) ?? new SnSourceStats();
            ReadStats a = ProviderCore.Stats, p = ProviderCore.PhysicsStats, cell = ProviderCore.PhysicsCellStats;
            SnPrefetcher pf = TerrainTiles.Prefetch;
            var guards = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < TerrainPaths.Names.Length; i++)
                guards[TerrainPaths.Names[i]] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "installed", TerrainGuards.Installed(i) }, { "blocked", TerrainGuards.Count(i) }, { "mutantOff", S4Mutants.GuardOff(i) },
                };
            guards["saveblock"] = new Dictionary<string, object>(StringComparer.Ordinal) { { "installed", TerrainFailSafe.SaveBlockInstalled }, { "blocked", TerrainFailSafe.SaveBlocked } };
            var materials = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in TerrainTiles.Materials) materials[kv.Key] = kv.Value;
            string gate = TerrainGate.Last == TerrainGate.Result.NotEvaluated ? "NotEvaluated" : TerrainGate.Last.ToString().ToUpperInvariant();
            string line = "DE.Terrain status gate=" + gate + (TerrainGate.Condition != null && TerrainGate.Condition.Length > 0 ? " " + TerrainGate.Condition : "") +
                          (TerrainGate.Reason != null ? " (" + TerrainGate.Reason + ")" : "") + " tiles=" + TerrainTiles.Tiles.Count + " terrainUp=" + TerrainTiles.TerrainUp +
                          " failSafe=" + TerrainFailSafe.Active + " saveBlocked=" + TerrainFailSafe.SaveBlocked + " provider=" + (TerrainGate.ProviderRegistered ? "registered" : "NOT registered") +
                          " readRange calls=" + a.Calls + " p99us=" + a.PercentileUs(0.99).ToString("0.#", CultureInfo.InvariantCulture) +
                          " harness=" + S4Harness.StateText + (S4Mutants.Active.Length > 0 ? " MUTANTS=" + S4Mutants.Active : "");
            var r = Ok(line);
            r["gate"] = gate;
            r["condition"] = TerrainGate.Condition;
            r["reason"] = TerrainGate.Reason;
            r["tiles"] = TerrainTiles.Tiles.Count;
            r["terrainUp"] = TerrainTiles.TerrainUp;
            r["failSafe"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "active", TerrainFailSafe.Active }, { "reason", TerrainFailSafe.Reason }, { "saveBlocked", TerrainFailSafe.SaveBlocked },
            };
            r["provider"] = new Dictionary<string, object>(StringComparer.Ordinal) { { "registered", TerrainGate.ProviderRegistered }, { "error", TerrainGate.ProviderError } };
            r["source"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "path", m != null ? m.OctreeDir : null }, { "build", m != null ? (object)m.BuildId : null }, { "files", m != null ? (object)m.Files : null },
                { "bytes", m != null ? (object)m.Bytes : null }, { "rows", m != null ? new object[] { m.RowMin, m.RowMax } : null },
                { "manifestHash", m != null ? SnSourceManifest.Hex(m.Hash) : null }, { "indexOk", m != null ? (object)m.IndexOk : null },
                { "error", m != null ? m.Error : "not measured" },
                { "batchesIndexed", s.BatchesIndexed }, { "batchesMapped", s.BatchesMapped }, { "bytesMapped", s.BytesMapped }, { "badBatches", s.BadBatches },
                { "ioFailures", s.IoFailures }, { "ioRetrying", s.IoRetrying }, { "ioRecovered", s.IoRecovered }, { "failedReads", s.FailedReads },
                { "prefetch", pf == null ? null : new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "state", pf.StateName }, { "planned", pf.Planned }, { "done", pf.Done }, { "data", pf.Data }, { "missing", pf.Missing },
                        { "bad", pf.Bad }, { "ioRetried", pf.IoRetried }, { "pages", pf.PagesTouched }, { "ms", pf.ElapsedMs }, { "error", pf.Error },
                    } },
            };
            // physics cell = the content read + the material read SE makes right after it: the unit of
 // S4.A-T1(3)'s "< 1 ms per 8 m physics cell"; physP99us stays per call
            r["readRange"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "calls", a.Calls }, { "p50us", a.PercentileUs(0.5) }, { "p99us", a.PercentileUs(0.99) }, { "maxUs", a.MaxUs }, { "meanUs", a.MeanUs },
                { "physCalls", p.Calls }, { "physP50us", p.PercentileUs(0.5) }, { "physP99us", p.PercentileUs(0.99) }, { "physMaxUs", p.MaxUs },
                { "cellCalls", cell.Calls }, { "cellP50us", cell.PercentileUs(0.5) }, { "cellP99us", cell.PercentileUs(0.99) }, { "cellMaxUs", cell.MaxUs },
                { "badTargets", ProviderCore.BadTargets }, { "readErrors", System.Threading.Interlocked.Read(ref ProviderCore.ReadErrors) },
            };
            r["guards"] = guards;
            r["materials"] = materials;
            r["mutants"] = S4Mutants.Active;
            r["harness"] = S4Harness.StatusInfo();
            r["lastEdit"] = _lastEditFinal;
            r["editPending"] = _pendingEdit != null;
            r["ram"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "privateBefore", TerrainTiles.RamBeforeBytes }, { "privateAfter", TerrainTiles.RamAfterBytes }, { "privateNow", TerrainTiles.PrivateBytes() },
                { "createMs", TerrainTiles.CreateMs },
            };
            r["siteVersion"] = SiteV1.SiteVersion;
            return r;
        }

        private static Dictionary<string, object> Gate()
        {
            SnSourceManifest m = TerrainGate.MeasureNow();
            string field, found, expected;
            bool match = TerrainGate.ManifestMatches(m, out field, out found, out expected);
            string line = "DE.Terrain gate check (fresh measurement, no tile change): manifest " + (match ? "MATCH" : "MISMATCH " + field + " found=" + found + " expected=" + expected) +
                          "; provider " + (TerrainGate.ProviderRegistered ? "registered" : "NOT registered (" + TerrainGate.ProviderError + ")") +
                          "; members " + (TerrainGate.CheckMembers() ?? "all resolved") + "; last gate " + TerrainGate.Last + " " + (TerrainGate.Condition ?? "") + " " + (TerrainGate.Reason ?? "");
            DeLog.Info(line);
            var r = Ok(line);
            r["match"] = match;
            r["field"] = field;
            r["found"] = found;
            r["expected"] = expected;
            r["sourceLine"] = m.LogLine();
            r["manifest"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "subnauticaDir", m.SubnauticaDir }, { "path", m.OctreeDir }, { "build", m.BuildId }, { "files", m.Files }, { "bytes", m.Bytes },
                { "rows", new object[] { m.RowMin, m.RowMax } }, { "manifestHash", SnSourceManifest.Hex(m.Hash) }, { "indexOk", m.IndexOk }, { "error", m.Error },
            };
            r["expectedManifest"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "build", SnSourceManifest.ExpectedBuildId }, { "files", SnSourceManifest.ExpectedFiles + (S4Mutants.WrongManifest ? 1 : 0) },
                { "bytes", SnSourceManifest.ExpectedBytes }, { "manifestHash", SnSourceManifest.Hex(SnSourceManifest.ExpectedHash) },
            };
            r["provider"] = TerrainGate.ProviderRegistered;
            r["members"] = TerrainGate.MissingMembers;
            r["gate"] = TerrainGate.Last.ToString();
            return r;
        }

        private static Dictionary<string, object> Tiles()
        {
            var list = new List<object>(TerrainTiles.Tiles.Count);
            int exist = 0;
            foreach (TerrainTiles.TileInfo t in TerrainTiles.Tiles)
            {
                MyVoxelMap map = t.Map;
                bool exists = false;
                try { exists = VRage.MyEntityIdentifier.ExistsById(t.EntityId) && ReferenceEquals(VRage.MyEntityIdentifier.GetEntityById(t.EntityId), map); }
                catch (Exception) { }
                if (exists) exist++;
                int bodies = 0;
                var body = map != null ? map.Physics as MyVoxelPhysicsBody : null;
                if (body != null)
                {
                    if (body.RigidBody != null) bodies++;
                    if (body.RigidBody2 != null) bodies++;
                }
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "i", t.I }, { "j", t.J }, { "k", t.K }, { "id", TerrainLattice.IdHex(t.EntityId) }, { "idDec", t.EntityId },
                    { "deterministic", t.EntityId == SiteV1.TileEntityId(t.I, t.J, t.K) },
                    { "min", S4Rows.Arr(t.MinWorld) }, { "placedMin", S4Rows.Arr(t.PlaceMin) }, { "exists", exists }, { "closed", map == null || map.Closed },
                    { "save", map != null && map.Save }, { "bodies", bodies }, { "name", map != null ? map.StorageName : null },
                    { "providerClosed", t.Provider == null || t.Provider.IsClosed },
                });
            }
            string line = "DE.Terrain tiles: " + list.Count + " created, " + exist + " registered under their ids, terrainUp=" + TerrainTiles.TerrainUp;
            var r = Ok(line);
            r["tiles"] = list;
            return r;
        }

        // ==================================================================================================== hash

 /// <summary>One tile's box hashes (LOD 0, inclusive box in storage voxels).</summary>
        private struct BoxHash
        {
            public TerrainTiles.TileInfo Tile;
            public Vector3I Min, Max;
            public ulong Content, Material;
            public bool Modified;
        }

        private static List<BoxHash> HashAround(Vec3 w, int half)
        {
            var res = new List<BoxHash>();
            foreach (TerrainTiles.TileInfo t in TerrainTiles.Tiles)
            {
                if (t.Map == null || t.Map.Closed) continue;
                var storage = t.Map.Storage as MyStorageBase;
                if (storage == null) continue;
                Vec3 l = w - t.PlaceMin;
                var min = new Vector3I((int)Math.Floor(l.X) - half, (int)Math.Floor(l.Y) - half, (int)Math.Floor(l.Z) - half);
                var max = new Vector3I((int)Math.Floor(l.X) + half, (int)Math.Floor(l.Y) + half, (int)Math.Floor(l.Z) + half);
                min = Vector3I.Max(min, Vector3I.Zero);
                max = Vector3I.Min(max, new Vector3I(SiteV1.Tile - 1));
                if (min.X > max.X || min.Y > max.Y || min.Z > max.Z) continue;
                var data = new MyStorageData();
                data.Resize(min, max);
                storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, 0, min, max);
                int n = (max - min + 1).Size;
                var bb = new BoundingBoxI(min, max);
                res.Add(new BoxHash
                {
                    Tile = t, Min = min, Max = max,
                    Content = S4Hash.Add(S4Hash.Offset, data[MyStorageDataTypeEnum.Content], n),
                    Material = S4Hash.Add(S4Hash.Offset, data[MyStorageDataTypeEnum.Material], n),
                    Modified = storage.IsRangeModified(ref bb),
                });
            }
            return res;
        }

        private static List<object> HashList(List<BoxHash> hs)
        {
            var l = new List<object>(hs.Count);
            foreach (BoxHash h in hs)
                l.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "i", h.Tile.I }, { "j", h.Tile.J }, { "k", h.Tile.K },
                    { "box", new object[] { h.Min.X, h.Min.Y, h.Min.Z, h.Max.X, h.Max.Y, h.Max.Z } },
                    { "contentHash", S4Hash.Hex(h.Content) }, { "materialHash", S4Hash.Hex(h.Material) }, { "modified", h.Modified },
                });
            return l;
        }

        private static Dictionary<string, object> Hash(IDictionary<string, object> args)
        {
            Vec3 w;
            string why;
            if (!PointArg(args, out w, out why)) return Fail("de.terrain hash: " + why);
            int half = (int)Math.Max(1, Math.Min(128, MiniJson.GetNumber(args, "half") ?? 32));
            List<BoxHash> hs = HashAround(w, half);
            if (hs.Count == 0) return Fail("de.terrain hash: no tile holds SE " + TerrainTiles.V(w) + " (tiles=" + TerrainTiles.Tiles.Count + ")");
            var parts = new List<string>();
            foreach (BoxHash h in hs) parts.Add(h.Tile.I + "," + h.Tile.J + "," + h.Tile.K + " content=" + S4Hash.Hex(h.Content) + " material=" + S4Hash.Hex(h.Material) + " modified=" + h.Modified);
            string line = "DE.Terrain hash at SN " + TerrainTiles.V(TerrainTiles.Map.SeToSn(w)) + " half=" + half + ": " + string.Join("; ", parts.ToArray());
            DeLog.Info(line);
            List<object> list = HashList(hs);
            Jsonl.WriteLine(S4Rows.Row("s4.hash", "sn", S4Rows.Arr(TerrainTiles.Map.SeToSn(w)), "se", S4Rows.Arr(w), "half", half, "tiles", list));
            var r = Ok(line);
            r["tiles"] = list;
            return r;
        }

        // ================================================================================================== edits

        private static Dictionary<string, object> Crater(IDictionary<string, object> args, string via)
        {
            var a = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in args) a[kv.Key] = kv.Value;
            a["path"] = "meteor";
            if (!a.ContainsKey("radius")) a["radius"] = 8.0;
            return Edit(a, via);
        }

        /// <summary>Calls one guarded SE entry on the tile holding the point; reports the hash before/after and the guard count.</summary>
        private static Dictionary<string, object> Edit(IDictionary<string, object> args, string via)
        {
            string path = (MiniJson.GetString(args, "path") ?? "").Trim().ToLowerInvariant();
            int pi = TerrainPaths.IndexOf(path);
            if (pi < 0) return Fail("de.terrain edit: path cutout|explosion|voxelhand|meteor|writerange|shipdrill");
            Vec3 w;
            string why;
            if (!PointArg(args, out w, out why)) return Fail("de.terrain edit: " + why);
            float radius = (float)Math.Max(0.5, Math.Min(32, MiniJson.GetNumber(args, "radius") ?? 4.0));
            TerrainTiles.TileInfo t = TerrainTiles.TileAt(w);
            if (t == null || t.Map == null || t.Map.Closed) return Fail("de.terrain edit: no tile holds SE " + TerrainTiles.V(w));
            MyVoxelMap map = t.Map;
            int half = (int)Math.Ceiling(radius) + 4;
            List<BoxHash> before = HashAround(w, half);
            long countBefore = TerrainGuards.Count(pi);
            var center = new Vector3D(w.X, w.Y, w.Z);
            string detail;
            object ore = null;
            switch (pi)
            {
                case TerrainPaths.Cutout:
                {
                    var dict = new Dictionary<MyVoxelMaterialDefinition, int>();
                    float pct;
                    MyVoxelMaterialDefinition mat;
                    MyVoxelGenerator.CutOutShapeWithProperties(map, new MyShapeSphere(center, radius), out pct, out mat, dict, false, false, false, false, false);
                    int voxels = 0;
                    foreach (var kv in dict) voxels += kv.Value;
                    ore = new Dictionary<string, object>(StringComparer.Ordinal) { { "percent", (double)pct }, { "material", mat != null ? mat.Id.SubtypeName : null }, { "voxels", voxels } };
                    detail = "MyVoxelGenerator.CutOutShapeWithProperties -> percent=" + pct + " material=" + (mat != null ? mat.Id.SubtypeName : "null") + " cutVoxels=" + voxels;
                    break;
                }
                case TerrainPaths.Explosion:
                {
                    Vector3I cmin, cmax;
                    bool changed = MyVoxelGenerator.CutOutSphereFast(map, ref center, radius, out cmin, out cmax, true);
                    detail = "MyVoxelGenerator.CutOutSphereFast -> " + changed + " cache " + cmin + ".." + cmax;
                    break;
                }
                case TerrainPaths.Meteor:
                {
                    Vec3 up = (w - TerrainTiles.Map.C).Normalized();
                    MyVoxelMaterialDefinition mat = MyDefinitionManager.Static.GetDefaultVoxelMaterialDefinition();
                    MyVoxelGenerator.MakeCrater(map, new BoundingSphereD(center, radius), new Vector3((float)-up.X, (float)-up.Y, (float)-up.Z), mat);
                    detail = "MyVoxelGenerator.MakeCrater(sphere r=" + radius + ", down, " + (mat != null ? mat.Id.SubtypeName : "null") + ")";
                    break;
                }
                case TerrainPaths.VoxelHand:
                {
                    MethodInfo m = typeof(MyVoxelBase).GetMethod("UpdateVoxelShape", BindingFlags.Instance | BindingFlags.NonPublic, null,
                        new[] { typeof(MyVoxelBase.OperationType), typeof(MyShape), typeof(byte) }, null);
                    if (m == null) return Fail("de.terrain edit voxelhand: MyVoxelBase.UpdateVoxelShape(OperationType, MyShape, byte) not found");
                    m.Invoke(map, new object[] { MyVoxelBase.OperationType.Cut, new MyShapeSphere(center, radius), (byte)0 });
                    detail = "MyVoxelBase.UpdateVoxelShape(Cut, sphere r=" + radius + ", 0) (the voxel-hand funnel, by reflection)";
                    break;
                }
                case TerrainPaths.WriteRange:
                {
                    var storage = map.Storage as MyStorageBase;
                    Vec3 l = w - t.PlaceMin;
                    int r = (int)Math.Ceiling(radius);
                    var min = Vector3I.Max(new Vector3I((int)l.X - r, (int)l.Y - r, (int)l.Z - r), Vector3I.Zero);
                    var max = Vector3I.Min(new Vector3I((int)l.X + r, (int)l.Y + r, (int)l.Z + r), new Vector3I(SiteV1.Tile - 1));
                    var data = new MyStorageData(MyStorageDataTypeFlags.Content);
                    data.Resize(min, max);
                    data.ClearContent(0);
                    storage.WriteRange(data, MyStorageDataTypeFlags.Content, min, max, true, false);
                    detail = "MyStorageBase.WriteRange(empty content " + min + ".." + max + ")";
                    break;
                }
                case TerrainPaths.ShipDrill:
                {
                    string ds = MiniJson.GetString(args, "drill");
                    double? dn = MiniJson.GetNumber(args, "drill");
                    long id;
                    if (ds != null) { if (!TryParseId(ds, out id)) return Fail("de.terrain edit shipdrill: bad drill id '" + ds + "'"); }
                    else if (dn.HasValue) id = (long)dn.Value;
                    else return Fail("de.terrain edit shipdrill: needs drill=<entity id of a ship drill block> (the real ship-drill check is in-game: S4.N2)");
                    MyEntity e;
                    if (!MyEntities.TryGetEntityById(id, out e)) return Fail("de.terrain edit shipdrill: no entity " + id);
                    var drill = e as MyShipDrill;
                    if (drill == null) return Fail("de.terrain edit shipdrill: entity " + id + " is " + e.GetType().Name + ", not a MyShipDrill");
                    var mining = drill.CubeGrid != null && drill.CubeGrid.GridSystems != null ? drill.CubeGrid.GridSystems.MiningSystem : null;
                    if (mining == null) return Fail("de.terrain edit shipdrill: the drill's grid has no MyShipMiningSystem (turn the drill on once)");
                    mining.RequestCutOut(drill, false, false, center, map);
                    detail = "MyShipMiningSystem.RequestCutOut(drill " + id + ", hit " + TerrainTiles.V(w) + ")";
                    break;
                }
                default:
                    return Fail("de.terrain edit: unknown path");
            }
            List<BoxHash> after = HashAround(w, half);
            long countAfter = TerrainGuards.Count(pi);
            bool same = before.Count == after.Count;
            bool modified = false;
            for (int i = 0; i < after.Count; i++)
            {
                modified |= after[i].Modified;
                if (i < before.Count && (before[i].Content != after[i].Content || before[i].Material != after[i].Material)) same = false;
            }
            bool blocked = countAfter > countBefore;
            string line = "DE.Terrain edit " + path + " at SN " + TerrainTiles.V(TerrainTiles.Map.SeToSn(w)) + " r=" + radius + " tile=" + t.I + "," + t.J + "," + t.K +
                          ": " + detail + "; blocked=" + blocked + " (count " + countBefore + " -> " + countAfter + ") hashUnchanged=" + same + " modified=" + modified +
                          (S4Mutants.GuardOff(pi) ? " (S4.M9." + path + " ON: guard removed)" : "") + " (via " + via + ")";
            DeLog.Info(line);
            List<object> hb = HashList(before), ha = HashList(after);
            Jsonl.WriteLine(S4Rows.Row("s4.edit", "path", path, "sn", S4Rows.Arr(TerrainTiles.Map.SeToSn(w)), "radius", (double)radius,
                                       "tile", t.I + "," + t.J + "," + t.K, "blocked", blocked, "count", countAfter, "hashUnchanged", same, "modified", modified,
                                       "hashBefore", hb, "hashAfter", ha, "ore", ore, "guardOff", S4Mutants.GuardOff(pi), "detail", detail));
            _pendingEdit = new PendingEdit
            {
                Path = path, PathIndex = pi, W = w, Half = half, Before = before, CountBefore = countBefore, Tile = t.I + "," + t.J + "," + t.K,
                DueMs = Wire.Native.Win32.TickCount64() + EditSettleMs,
            };
            var res = Ok(line);
            res["pending"] = true; // the final witness follows in EditSettleMs (s4.edit row with final: true, status.lastEdit)
            res["path"] = path;
            res["blocked"] = blocked;
            res["count"] = countAfter;
            res["hashBefore"] = hb;
            res["hashAfter"] = ha;
            res["hashUnchanged"] = same;
            res["modified"] = modified;
            res["ore"] = ore;
            return res;
        }

        private static bool TryParseId(string s, out long id)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        // ===================================================================================================== n1

 /// <summary>S4.N1: the local character's feet vs the storage surface under the same SN column (storage = SE's mesher through GetIntersectionWithLine).</summary>
        private static Dictionary<string, object> N1(IDictionary<string, object> args, string via)
        {
            if (!TerrainTiles.TerrainUp) return Fail("de.terrain n1: no terrain tiles (gate " + TerrainGate.Last + ")");
            MySession s = MySession.Static;
            MyCharacter c = s != null ? s.LocalCharacter : null;
            if (c == null || c.Closed) return Fail("de.terrain n1: no local character");
            SiteMap map = TerrainTiles.Map;
            Vector3D fp = c.PositionComp.GetPosition();
            var feet = new Vec3(fp.X, fp.Y, fp.Z);
            Vec3 sn = map.SeToSn(feet);
            double hitY;
            string tile;
            bool hit = S4Harness.StorageColumn(sn.X, sn.Z, sn.Y + 5, sn.Y - 50, out hitY, out tile);
            double delta = hit ? sn.Y - hitY : double.NaN;
            string state = "?";
            try { state = c.GetCurrentMovementState().ToString(); } catch (Exception) { }
            string line = "DE.Terrain N1 feetSn=" + TerrainTiles.V(sn) + " storageSnY=" + (hit ? hitY.ToString("0.###", CultureInfo.InvariantCulture) : "none") +
                          " delta=" + (hit ? delta.ToString("0.###", CultureInfo.InvariantCulture) : "n/a") + " m tile=" + (tile ?? "-") + " movement=" + state + " swim=" + SwimGuard.StateName +
                          " (the Subnautica terrain height under the same column comes from de.s4 probe; via " + via + ")";
            DeLog.Info(line);
            Jsonl.WriteLine(S4Rows.Row("s4.n1", "feetSe", S4Rows.Arr(feet), "feetSn", S4Rows.Arr(sn), "storageHit", hit, "storageSnY", hit ? (object)hitY : null,
                                       "delta", hit ? (object)delta : null, "tile", tile, "movement", state, "swim", SwimGuard.StateName));
            var r = Ok(line);
            r["feetSn"] = S4Rows.Arr(sn);
            r["storageSnY"] = hit ? (object)hitY : null;
            r["delta"] = hit ? (object)delta : null;
            r["tile"] = tile;
            r["movement"] = state;
            return r;
        }

        // ===================================================================================================== n3

 /// <summary>S4.N3 in game: <see cref="ProviderSelfCheck"/> on the live tile around an SN focus (default the M3 cell's point), plus 1,000 physics-cell reads timed.</summary>
        private static Dictionary<string, object> N3(IDictionary<string, object> args, string via)
        {
            if (!TerrainTiles.TerrainUp) return Fail("de.terrain n3: no terrain tiles (gate " + TerrainGate.Last + ")");
            double[] f = S4Args.Numbers(args, "sn", 3);
            Vec3 focusSn = f != null ? new Vec3(f[0], f[1], f[2]) : TerrainLattice.MutantCellSn;
            int boxes = (int)Math.Max(10, Math.Min(5000, MiniJson.GetNumber(args, "boxes") ?? 600));
            int seed = (int)(MiniJson.GetNumber(args, "seed") ?? 4546);
            Vec3 w = TerrainTiles.Map.SnToSe(focusSn);
            TerrainTiles.TileInfo t = TerrainTiles.TileAt(w);
            if (t == null) return Fail("de.terrain n3: no tile holds SN " + TerrainTiles.V(focusSn));
            ProviderCore core = t.Provider.Core;
            Vec3 l = w - t.MinWorld;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SelfCheckResult res = ProviderSelfCheck.Run(core, seed, boxes, (int)l.X, (int)l.Y, (int)l.Z, 32);
            double checkMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            // 1,000 physics-cell-shaped reads (LOD 0, 12³ = 8c−1 … 8c+10) around the focus, timed
            var buf = new byte[12 * 12 * 12];
            var mat = new byte[12 * 12 * 12];
            var times = new double[1000];
            var rnd = new Random(seed);
            for (int n = 0; n < times.Length; n++)
            {
                int cx = ((int)l.X >> 3) + rnd.Next(-4, 5), cy = ((int)l.Y >> 3) + rnd.Next(-4, 5), cz = ((int)l.Z >> 3) + rnd.Next(-4, 5);
                int x0 = Math.Max(0, cx * 8 - 1), y0 = Math.Max(0, cy * 8 - 1), z0 = Math.Max(0, cz * 8 - 1);
                long a = System.Diagnostics.Stopwatch.GetTimestamp();
                ProviderCore.Read(core, 0, x0, y0, z0, Math.Min(x0 + 11, 1023), Math.Min(y0 + 11, 1023), Math.Min(z0 + 11, 1023),
                                  ProviderCore.DtContent | ProviderCore.DtMaterial, 0, buf, mat, 0, 12, 144, false);
                times[n] = (System.Diagnostics.Stopwatch.GetTimestamp() - a) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            }
            Array.Sort(times);
            double p50 = times[499], p99 = times[989], max = times[999];
            string line = "DE.Terrain N3 " + (res.Pass ? "PASS" : "FAIL") + " tile=" + t.I + "," + t.J + "," + t.K + " focusSn=" + TerrainTiles.V(focusSn) + " boxes=" + res.Boxes +
                          " requests=" + res.Requests + " detect=" + res.DetectChecks + " lines=" + res.LineChecks + " violations=" + res.Violations +
                          " disjoint/contains/intersects=" + res.Disjoint + "/" + res.Contains + "/" + res.Intersects +
                          (res.FirstViolation != null ? " first: " + res.FirstViolation : "") + " checkMs=" + checkMs.ToString("0", CultureInfo.InvariantCulture) +
                          " cellRead12^3 p50us=" + p50.ToString("0.#", CultureInfo.InvariantCulture) + " p99us=" + p99.ToString("0.#", CultureInfo.InvariantCulture) +
                          " maxUs=" + max.ToString("0.#", CultureInfo.InvariantCulture) + (S4Mutants.Active.Length > 0 ? " MUTANTS=" + S4Mutants.Active : "") + " (via " + via + ")";
            DeLog.Info(line);
            Jsonl.WriteLine(S4Rows.Row("s4.n3", "pass", res.Pass, "tile", t.I + "," + t.J + "," + t.K, "focusSn", S4Rows.Arr(focusSn), "boxes", res.Boxes,
                                       "requests", res.Requests, "detect", res.DetectChecks, "lines", res.LineChecks, "violations", res.Violations,
                                       "firstViolation", res.FirstViolation, "disjoint", res.Disjoint, "contains", res.Contains, "intersects", res.Intersects,
                                       "cellP50us", p50, "cellP99us", p99, "cellMaxUs", max, "mutants", S4Mutants.Active));
            var r = Ok(line);
            r["pass"] = res.Pass;
            r["violations"] = res.Violations;
            r["firstViolation"] = res.FirstViolation;
            r["cellP99us"] = p99;
            r["cellMaxUs"] = max;
            return r;
        }

        // ==================================================================================================== at0b

 /// <summary>
 /// In-SE A-T0b (the fallback of ROADMAP S4.A-T0b): for each height h, a 1024³ <c>MyOctreeStorage</c> (no map, no
 /// physics, never added to the world) over a provider serving <see cref="SyntheticPlaneField"/>(h) on the real lattice
 /// tile under F⁻¹(x, h, z); for each LOD a small region around the column is meshed by
 /// <c>MyPrecalcComponent.IsoMesher.Precalc(IMyStorage, int, Vector3I, Vector3I, MyStorageDataTypeFlags,
 /// MyVoxelRequestFlags)</c>
 /// Per vertex: SN y through F vs h, and the iso
 /// byte implied along the local radial axis by the provider's content bytes. Rows s4.at0b.
 /// </summary>
        private static Dictionary<string, object> At0b(IDictionary<string, object> args, string via)
        {
            SiteMap map = TerrainTiles.Map ?? (SiteBinding.PlanetCount == 1 ? SiteMap.ForSiteV1(SiteBinding.C) : null);
            if (map == null) return Fail("de.terrain at0b: no planet centre (exactly one " + SiteV1.GeneratorSubtypeId + " needed)");
            double[] lods = S4Args.Numbers(args, "lods", 0) ?? new double[] { 0, 1, 2 };
            double[] heights = S4Args.Numbers(args, "heights", 0) ?? new double[] { -17.5, -20.25, -100.3 };
            double cx = MiniJson.GetNumber(args, "x") ?? 0.0, cz = MiniJson.GetNumber(args, "z") ?? 0.0;
            var bands = TerrainTiles.Bands ?? new MaterialBands(new byte[] { 0, 0, 0, 0, 0, 0, 0 });
            var rows = new List<object>();
            var summary = new List<string>();
            foreach (double h in heights)
            {
                Vec3 w = map.SnToSe(new Vec3(cx, h, cz));
                Vec3 tileMin = Vec3.Zero;
                bool found = false;
                foreach (int[] s in SiteV1.TilesCreated())
                {
                    Vec3 m = TerrainLattice.TileMin(map.C, s[0], s[1], s[2]);
                    if (TerrainLattice.Inside(m, w)) { tileMin = m; found = true; break; }
                }
                if (!found) { summary.Add("h=" + h + ": no lattice tile"); continue; }
                var sampler = new TileSampler(new SyntheticPlaneField(h), map, tileMin, bands);
                var provider = new SnTerrainProvider(null, sampler, -1, -1, -1, 0);
                var storage = new MyOctreeStorage(provider, new Vector3I(SiteV1.Tile));
                try
                {
                    Vec3 l = w - tileMin;
                    foreach (double lodD in lods)
                    {
                        int lod = (int)lodD;
                        if (lod < 0 || lod > 6) continue;
                        int s = 1 << lod;
                        var cl = new Vector3I((int)Math.Floor(l.X / s), (int)Math.Floor(l.Y / s), (int)Math.Floor(l.Z / s));
                        Vector3I lo = Vector3I.Max(cl - 8, Vector3I.Zero), hi = Vector3I.Min(cl + 8, new Vector3I((SiteV1.Tile >> lod) - 1));
                        MyIsoMesh mesh = MyPrecalcComponent.IsoMesher.Precalc(storage, lod, lo, hi, MyStorageDataTypeFlags.Content, (MyVoxelRequestFlags)0);
                        int nv = mesh != null ? mesh.VerticesCount : 0, near = 0;
                        double sumErr = 0, maxErr = 0;
                        var isos = new List<double>();
                        var col = new byte[2];
                        for (int vi = 0; vi < nv; vi++)
                        {
                            Vector3 p;
                            mesh.GetUnpackedPosition(vi, out p);
                            Vec3 vw = tileMin + new Vec3(p.X, p.Y, p.Z);
                            double err = map.SeToSn(vw).Y - h;
                            sumErr += err;
                            maxErr = Math.Max(maxErr, Math.Abs(err));
                            // the iso byte along the radial (world +Z = U0) axis near the column, from the provider's own bytes
                            if (Math.Abs(p.X - l.X) > 4 * s || Math.Abs(p.Y - l.Y) > 4 * s) continue;
                            near++;
                            int ix = (int)Math.Round(p.X / s), iy = (int)Math.Round(p.Y / s), iz = (int)Math.Floor(p.Z / s);
                            if (iz < 0 || iz + 1 > (SiteV1.Tile >> lod) - 1) continue;
                            ProviderCore.Read(provider.Core, lod, ix, iy, iz, ix, iy, iz + 1, ProviderCore.DtContent, 0, col, null, 0, 1, 1, false);
                            double frac = p.Z / s - iz;
                            if (col[0] != col[1]) isos.Add(col[0] + frac * (col[1] - col[0]));
                        }
                        isos.Sort();
                        double isoMed = isos.Count > 0 ? isos[isos.Count / 2] : double.NaN;
                        double isoMean = 0;
                        foreach (double x in isos) isoMean += x;
                        isoMean = isos.Count > 0 ? isoMean / isos.Count : double.NaN;
                        double meanErr = nv > 0 ? sumErr / nv : double.NaN;
                        double quant = 0.5 / 127.5 * s;
                        summary.Add("h=" + h + " lod" + lod + ": " + nv + " vertices meanErr=" + F(meanErr) + " max|err|=" + F(maxErr) + " isoByte median=" + F(isoMed) +
                                    " (byte quantization " + F(quant) + " m)");
                        var row = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "height", h }, { "lod", lod }, { "vertices", nv }, { "nearVertices", near }, { "meanErr", meanErr }, { "maxAbsErr", maxErr },
                            { "isoMean", isoMean }, { "isoMedian", isoMed }, { "isoSamples", isos.Count }, { "quantization", quant }, { "tileMin", S4Rows.Arr(tileMin) },
                            { "halfVoxelShift", TerrainMutants.HalfVoxelShift },
                        };
                        rows.Add(row);
                        Jsonl.WriteLine(S4Rows.Row("s4.at0b", "height", h, "lod", lod, "vertices", nv, "nearVertices", near, "meanErr", meanErr, "maxAbsErr", maxErr,
                                                   "isoMean", isoMean, "isoMedian", isoMed, "isoSamples", isos.Count, "quantization", quant,
                                                   "tileMin", S4Rows.Arr(tileMin), "mutants", S4Mutants.Active));
                    }
                }
                finally
                {
                    try { storage.Close(); } catch (Exception) { provider.Close(); }
                }
            }
            string line = "DE.Terrain AT0B (in SE, SE's IsoMesher over a synthetic plane served by the provider): " + string.Join("; ", summary.ToArray()) + " (via " + via + ")";
            DeLog.Info(line);
            var r = Ok(line);
            r["rows"] = rows;
            return r;
        }

        // ================================================================================================= helpers

 /// <summary><c>sn: [x, y, z]</c> (Subnautica metres, through the tiles' map) or <c>se: [x, y, z]</c> (SE world).</summary>
        private static bool PointArg(IDictionary<string, object> args, out Vec3 w, out string why)
        {
            w = Vec3.Zero;
            why = null;
            if (!TerrainTiles.TerrainUp || TerrainTiles.Map == null) { why = "no terrain tiles (gate " + TerrainGate.Last + " " + (TerrainGate.Reason ?? "") + ")"; return false; }
            double[] se = S4Args.Numbers(args, "se", 3), sn = S4Args.Numbers(args, "sn", 3);
            if (se != null) { w = new Vec3(se[0], se[1], se[2]); return true; }
            if (sn != null) { w = TerrainTiles.Map.SnToSe(new Vec3(sn[0], sn[1], sn[2])); return true; }
            why = "needs sn: [x, y, z] (Subnautica metres) or se: [x, y, z]";
            return false;
        }

        private static string F(double v) { return double.IsNaN(v) ? "n/a" : v.ToString("0.####", CultureInfo.InvariantCulture); }

        internal static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        internal static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.Terrain " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
