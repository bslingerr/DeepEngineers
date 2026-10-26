using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Models;
using VRage.ModAPI;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// <c>de.s8a</c> on the SE side (CONTRACT §5.4 "S8a control commands"; C11 and <c>/de s8a …</c>, dev builds: not in
 /// <c>UserCommandRules</c>' table, so a release build refuses it). Update thread only; never throws (failures answer
 /// <c>ok: false</c>).
 /// <list type="bullet">
 /// <item><c>{action: "status"}</c>: the host, the overlay, the converter, the feed;</item>
 /// <item><c>{action: "drill", sn: [x, y, z] | se: [x, y, z], radius, path: "sphere" | "hand"}</c>: a hole at F⁻¹ of the point
 /// on the tile there: the explosion path <c>MyVoxelBase.PerformCutOutSphereFast(Vector3D center, float radius, bool
 /// notify)</c>
 /// or the hand-drill path <c>MyVoxelBase.CutOutShapeWithPropertiesAsync(OnCutOutResults results,
 /// MyShape shape, bool updateSync = false, bool onlyCheck = false, bool applyDamageMaterial = false, bool onlyApplyMaterial =
 /// false, bool skipCache = true)</c> (a Parallel.Start into MyVoxelGenerator.CutOutShapeWithProperties, S4's
 /// <c>cutout</c> guard) with <c>new MyShapeSphere(Vector3D center, float radius)</c>. The witness
 /// (S8a.N1.edit): the storage surface on the hole's axis before and 1.5 s after (<c>S4Harness.StorageColumn</c>), and
 /// <c>MyStorageBase.IsRangeModified(ref BoundingBoxI)</c> over the hole's box: row <c>s8a.drill</c>;</item>
 /// <item><c>{action: "overlay", cells: true}</c>: the dirty cells and the overlay hash read back now;</item>
 /// <item><c>{action: "save"}</c>: <c>Sandbox.Game.Screens.Helpers.MyAsyncSaving.Start(Action callbackOnFinished = null, string
 /// customName = null)</c>, refused unless the world
 /// folder holds <c>DE_THROWAWAY.txt</c> (as S7.M8);</item>
 /// <item><c>{action: "rays", columns: [[x, z], …], label, fromY?, toY?}</c> (S8a.N3's SE half): per column the storage
 /// surface on the vertical SN ray and <c>MyPhysics.CastRay(Vector3D, Vector3D, List&lt;MyPhysics.HitInfo&gt;, int)</c>'s first
 /// voxel hit, both as SN y: row <c>s8a.rays</c>. Beyond the provider tiles (the window, Stage C fix) the storage surface is
 /// the planet's own LOD-0 mesh, on the
 /// [fromY, toY] span and, outside the crater box, on SN y [150, −3,100] when the span misses; CastRay needs voxel physics,
 /// which SE builds only near physical entities (a spectator has none), so far window columns report castY NaN;</item>
 /// <item><c>{action: "probe", planet: true, points: [[x, z], …], depths?: [m, …], label?}</c> (Stage C, S8a.C2.ingame and
 /// S8a.N10.ore): per SN column (site v1) the planet's own surface three ways: <c>MyPlanet.GetClosestSurfacePointGlobal(Vector3D)</c>
 /// the LOD-0 voxel geometry on the vertical line
 /// and the Water Mod's depth there; the storage material just under the surface and
 /// at each requested depth below it (ore veins), and the biome map value
 /// plus the planet's MinimumRadius, AverageRadius,
 /// MaximumRadius, Rs and the sculptHash of the planet data folder: row <c>s8a.probe</c>;</item>
 /// <item><c>window</c>, <c>step</c>, <c>chart</c>: S8a-2 (Stage B), answered <c>ok: false</c> for now.</item>
 /// </list>
 /// </summary>
    internal static class S8aCommands
    {
        public const string Cmd = "de.s8a";
        public const ulong DrillSettleMs = 1500;
        public const int MaxRayColumns = 1024;

        private sealed class PendingDrill
        {
            public Vec3 Sn, Se;
            public double Radius;
            public string Path, Tile;
            public double BeforeY;
            public string BeforeTile;
            public ulong DueMs, StartMs;
            public TerrainTiles.TileInfo Info;
            public MyVoxelBase Planet;
            public long Tick;
            public float Percent = float.NaN;
            public string Material;
        }

        /// <summary>The physics surface along a vertical SN column (<c>MyPhysics.CastRay</c>, the first voxel-body hit): the planet's witness and the window's SE half of the rays (NaN: no hit).</summary>
        private static double CastColumn(double x, double z, double fromY, double toY, out string hitTile)
        {
            hitTile = null;
            SiteMap map = TerrainTiles.Map ?? SiteBinding.Map;
            if (map == null) return double.NaN;
            double cy = double.NaN;
            try
            {
                Vec3 a = map.SnToSe(new Vec3(x, fromY, z)), b = map.SnToSe(new Vec3(x, toY, z));
                Hits.Clear();
                MyPhysics.CastRay(new Vector3D(a.X, a.Y, a.Z), new Vector3D(b.X, b.Y, b.Z), Hits, 0);
                double best = double.MaxValue;
                foreach (MyPhysics.HitInfo h in Hits)
                {
                    IMyEntity e = h.HkHitInfo.GetHitEntity();
                    var vb = e as MyVoxelBase;
                    if (vb == null) continue;
                    var pt = new Vec3(h.Position.X, h.Position.Y, h.Position.Z);
                    double d = (pt - a).Length;
                    if (d >= best) continue;
                    best = d;
                    cy = map.SeToSn(pt).Y;
                    var prov = vb.Storage != null ? (vb.Storage as MyStorageBase) : null;
                    var sp = prov != null ? prov.DataProvider as SnTerrainProvider : null;
                    hitTile = sp != null ? sp.I + "," + sp.J + "," + sp.K : vb is MyPlanet ? "planet" : e.GetType().Name;
                }
            }
            catch (Exception e) { hitTile = "CastRay " + e.GetType().Name; }
            return cy;
        }

        /// <summary>The SE voxel material (subtype name) just under a surface point of a voxel body, read at LOD 0 (the window's material probe, S8a.N10).</summary>
        private static string MaterialUnder(MyVoxelBase vb, Vec3 seHit, Vec3 down)
        {
            try
            {
                var st = vb.Storage as MyStorageBase;
                if (st == null) return null;
                Vector3D corner = vb.PositionLeftBottomCorner;
                Vec3 l = seHit + down * 0.75 - new Vec3(corner.X, corner.Y, corner.Z);
                var v = new Vector3I((int)Math.Floor(l.X), (int)Math.Floor(l.Y), (int)Math.Floor(l.Z));
                if (v.X < 0 || v.Y < 0 || v.Z < 0 || v.X >= st.Size.X || v.Y >= st.Size.Y || v.Z >= st.Size.Z) return null;
                var data = new MyStorageData(MyStorageDataTypeFlags.Material);
                data.Resize(v, v);
                st.ReadRange(data, MyStorageDataTypeFlags.Material, 0, v, v);
                byte m = data[MyStorageDataTypeEnum.Material][0];
                var def = Sandbox.Definitions.MyDefinitionManager.Static.GetVoxelMaterialDefinition(m);
                return def != null ? def.Id.SubtypeName : "#" + m;
            }
            catch (Exception) { return null; }
        }

        private static PendingDrill _pending;
        private static readonly List<MyPhysics.HitInfo> Hits = new List<MyPhysics.HitInfo>(16);
        private static Dictionary<string, object> _lastDrill;
        private static int _drills, _rayBatches;

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Cmd) return null;
            string action = (MiniJson.GetString(args, "action") ?? "status").Trim().ToLowerInvariant();
            try
            {
                switch (action)
                {
                    case "status": return Status();
                    case "drill": return Drill(args ?? new Dictionary<string, object>(), via);
                    case "overlay": return Overlay(args ?? new Dictionary<string, object>());
                    case "save": return Save(via);
                    case "rays": return Rays(args ?? new Dictionary<string, object>(), via);
                    case "probe": return Probe(args ?? new Dictionary<string, object>(), via);
                    case "window": return Window(args ?? new Dictionary<string, object>(), via);
                    case "step": return Step(args ?? new Dictionary<string, object>(), via);
                    case "drillpass": return DrillPass(args ?? new Dictionary<string, object>(), via);
                    case "ores":
                    {
                        // S8a Stage E (S8a.N10.crater): the Subnautica map's ore pockets read back from the tiles' voxels
                        if (TerrainTiles.CraterOres == null) return Fail("de.s8a ores: the crater ores are off (S8a off, " + TerrainTiles.EnvCraterOres + "=off or no tiles)");
                        if (!TerrainTiles.TerrainUp || TerrainTiles.Map == null) return Fail("de.s8a ores: no terrain tiles");
                        object pts;
                        IList<object> points = args != null && args.TryGetValue("points", out pts) ? pts as IList<object> : null;
                        int want = (int)Math.Max(1, Math.Min(400, MiniJson.GetNumber(args ?? new Dictionary<string, object>(), "count") ?? 48));
                        Dictionary<string, object> r = S8aCraterOresSe.Probe(want, points);
                        DeLog.Info("DE.S8a " + r["line"] + " (via " + via + ")");
                        return r;
                    }
                    case "oreoverlay":
                    {
                        // part nv): status, the cover scan, a vein's voxels, the expected paint
                        Vec3 camSe;
                        bool haveCam = S8aHost.Camera(out camSe);
                        Dictionary<string, object> r = S8aOreOverlaySe.Command(args, camSe, haveCam);
                        DeLog.Info("DE.S8a " + r["line"] + " (via " + via + ")");
                        return r;
                    }
                    case "outcrops":
                    {
                        // planet v2 R8 (part no): SE's outcrop records near the camera, SE's own detector over the same cells, the status
                        Vec3 camSe;
                        bool haveCam = S8aHost.Camera(out camSe);
                        Dictionary<string, object> r = S8aOutcropsSe.Command(args, camSe, haveCam);
                        DeLog.Info("DE.S8a " + r["line"] + " (via " + via + ")");
                        return r;
                    }
                    case "materialat":
                    {
                        // night queue (S8a.N10.ore): SE's own voxel material at SN points (the current chart; LOD 0, the
 // planet's storage, edits included): the wall samples of a drilled cavity are judged against it
                        object pv;
                        IList<object> pts = args != null && args.TryGetValue("points", out pv) ? pv as IList<object> : null;
                        MyPlanet planet = SiteBinding.Planet;
                        SiteMap map = SiteBinding.Map;
                        if (pts == null || planet == null || map == null) return Fail("de.s8a materialat: points [[x, y, z], ...] and a bound site needed");
                        var mats = new List<object>(pts.Count);
                        foreach (object o in pts)
                        {
                            var c = o as IList<object>;
                            if (c == null || c.Count < 3) { mats.Add(null); continue; }
                            Vec3 se = map.SnToSe(new Vec3(Convert.ToDouble(c[0], CultureInfo.InvariantCulture), Convert.ToDouble(c[1], CultureInfo.InvariantCulture), Convert.ToDouble(c[2], CultureInfo.InvariantCulture)));
                            mats.Add(MaterialUnder(planet, se, new Vec3(0, 0, 0)));
                        }
                        return new Dictionary<string, object> { { "ok", true }, { "materials", mats }, { "line", "de.s8a materialat: " + mats.Count + " points" } };
                    }
                    case "ring":
                    {
                        // S8a Stage F2: the ring field's status; {pick: true, maxRho, perBiome, cliffs}: spots for the nf part;
 // {columns, fromY, toY}: the field's own top surface; {blend: n}: S8a.F.blend over the served planet (pool thread, read back via status)
                        IDictionary<string, object> a = args ?? new Dictionary<string, object>();
                        object fa;
                        if (a.TryGetValue("failAt", out fa)) return S8aRing.SetFailAt(fa as IList<object>);
                        object sv;
                        if (a.TryGetValue("serve", out sv) && sv is IList<object>) return S8aRing.Serve((IList<object>)sv);
                        if (MiniJson.GetBool(a, "pick") == true)
                            return S8aRing.Pick(MiniJson.GetNumber(a, "maxRho") ?? 7600.0, (int)(MiniJson.GetNumber(a, "perBiome") ?? 2), (int)(MiniJson.GetNumber(a, "cliffs") ?? 4));
                        object cols;
                        if (a.TryGetValue("columns", out cols) && cols is IList<object>)
                            return S8aRing.Surface((IList<object>)cols, MiniJson.GetNumber(a, "fromY") ?? 100.0, MiniJson.GetNumber(a, "toY") ?? -1500.0);
                        double? blend = MiniJson.GetNumber(a, "blend");
                        if (blend.HasValue) return S8aRing.StartBlend((int)Math.Max(1, Math.Min(64, blend.Value)));
                        Dictionary<string, object> st = S8aRing.Status();
                        st["ok"] = true;
                        // a test run (nf 20261023-214851): the line read a key the status no longer has (readMsP95: the cost table replaced it)
 // and threw KeyNotFoundException; it names only keys Status always sets
                        st["line"] = "de.s8a ring: " + st["state"] + (st["refusal"] != null ? " (" + st["refusal"] + ")" : "") + ", " + st["placements"] + " placements, reads " +
                                     st["reads"] + " served, " + st["passes"] + " let through, errors " + st["errors"];
                        return st;
                    }
                    case "orevoxels":
                    {
                        // planet v2, ores as voxels (S8a.NO.voxel): the converters' census of ore surface voxels; {reset: true} zeroes it
                        if (MiniJson.GetBool(args ?? new Dictionary<string, object>(), "reset") == true)
                        {
                            System.Threading.Interlocked.Exchange(ref S8aFieldSampler.OreSurfaceVoxels, 0);
                            System.Threading.Interlocked.Exchange(ref S8aFieldSampler.OreTypedVoxels, 0);
                        }
                        long sv = System.Threading.Interlocked.Read(ref S8aFieldSampler.OreSurfaceVoxels), tv = System.Threading.Interlocked.Read(ref S8aFieldSampler.OreTypedVoxels);
                        return new Dictionary<string, object>
                        {
                            { "ok", true }, { "surface", sv }, { "typed", tv }, { "m30", S8aWireMutants.M30OresAsRock }, { "oreTable", S8aMaterialMap.OreCanon != null },
                            { "line", "de.s8a orevoxels: " + tv + " of " + sv + " ore surface voxels carry their ore's type" },
                        };
                    }
                    case "sample":
                    {
                        // planet v2 route B (S8aSample): status; {serve: boxes}; {columns, fromY, toY}
                        return S8aSample.Command(args);
                    }
                    case "beacon":
                    {
                        // S8a Stage E item 4 (dev): re-arm the Aurora distress beacon; {status: true} only reports
                        if (MiniJson.GetBool(args ?? new Dictionary<string, object>(), "status") == true)
                        {
                            Dictionary<string, object> s = S8aBeacon.Status();
                            s["ok"] = true;
                            s["line"] = "de.s8a beacon: added " + S8aBeacon.Added + ", shown " + S8aBeacon.Shown + ", retriggers " + S8aBeacon.Retriggers;
                            return s;
                        }
                        return S8aBeacon.Retrigger();
                    }
                    case "chart":
                    {
                        bool? tm = args != null ? MiniJson.GetBool(args, "treadmill") : null;
                        if (tm.HasValue) S8aChartManager.SetTreadmill(tm.Value, "de.s8a chart via " + via);
                        Dictionary<string, object> r = S8aChartManager.Status();
                        r["ok"] = true;
                        r["line"] = "de.s8a chart: seq " + S8aChartManager.ChartSeq + " key " + S8aStore.Hex16(S8aChartManager.ChartKey) + (S8aChartManager.TreadmillOn ? " (treadmill on)" : " (treadmill off)");
                        // S8a.N8.jump: the camera's and an entity's SN position under the current chart (queried before and after a step)
                        Vec3 cam;
                        SiteMap map = SiteBinding.Map;
                        SiteMap site = SiteBinding.SiteMapV1 ?? map;
                        if (map != null && S8aHost.Camera(out cam)) { r["camSn"] = S4Rows.Arr(map.SeToSn(cam)); r["camSite"] = S4Rows.Arr(site.SeToSn(cam)); r["camSe"] = S4Rows.Arr(cam); }
                        object entArg;
                        long id = 0;
                        if (map != null && args != null && args.TryGetValue("entity", out entArg) && entArg != null && S5Args.TryEntityId(entArg, out id))
                        {
                            VRage.Game.Entity.MyEntity e;
                            if (Sandbox.Game.Entities.MyEntities.TryGetEntityById(id, out e) && e != null)
                            {
                                string ent = id.ToString(CultureInfo.InvariantCulture);
                                Vector3D p = e.PositionComp.GetPosition();
                                var se = new Vec3(p.X, p.Y, p.Z);
                                r["entity"] = ent;
                                r["entitySn"] = S4Rows.Arr(map.SeToSn(se));
                                r["entitySite"] = S4Rows.Arr(site.SeToSn(se));
                                r["entitySe"] = S4Rows.Arr(se);
                            }
                            else r["entityError"] = "entity " + id + " not found";
                        }
                        return r;
                    }
                    default:
                        return Fail("de.s8a: unknown action '" + action + "' (status | drill {sn|se, radius, path: sphere|hand} | overlay {cells} | save | rays {columns, label} | probe {planet, points, depths} | window {throttleMs, on} | step {axis, sign} | chart | ores {count, points} | beacon {status} | drillpass {from, to, seconds, everyMs, radius})");
                }
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.s8a " + action + " " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return Fail("de.s8a " + action + " failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static Dictionary<string, object> Status()
        {
            var r = Ok("de.s8a status: " + (S8aOverlay.Enabled ? "on" : "off") + " drillOn=" + S8aOverlay.DrillOn + " cells=" + S8aOverlay.Cells + " drillGen=" + S8aDrillConverter.DrillGen +
                       " feed stream=" + S8aFeed.Stream + " ack=" + S8aFeed.AckSeq + " applied=" + S8aFeed.AppliedSeq + (S8aMutants.Active.Length > 0 ? " MUTANTS=" + S8aMutants.Active : ""));
            foreach (var kv in S8aHost.Status()) r[kv.Key] = kv.Value;
            if (_lastDrill != null) r["lastDrill"] = _lastDrill;
            return r;
        }

        // ================================================================================================== drill

        private static Dictionary<string, object> Drill(IDictionary<string, object> args, string via)
        {
            if (!S8aOverlay.Enabled) return Fail("de.s8a drill: S8a is off (" + S8aOverlay.EnvS8a + ")");
            if (!TerrainTiles.TerrainUp || TerrainTiles.Map == null) return Fail("de.s8a drill: no terrain tiles");
            if (_pending != null) return Fail("de.s8a drill: the previous drill's witness is pending (" + DrillSettleMs + " ms)");
            Vec3 w, sn;
            string why;
            if (!PointArg(args, out w, out sn, out why)) return Fail("de.s8a drill: " + why);
            double radius = Math.Max(0.5, Math.Min(32.0, MiniJson.GetNumber(args, "radius") ?? 2.5));
            string path = (MiniJson.GetString(args, "path") ?? "sphere").Trim().ToLowerInvariant();
            if (path != "sphere" && path != "hand") return Fail("de.s8a drill: path sphere | hand");
            TerrainTiles.TileInfo t = TerrainTiles.TileAt(w);
            // S8a-2: beyond the tiles the planet itself is drilled (S8a.N7.edit: the window regenerates the batch); its witness is the physics ray
            MyVoxelBase target = t != null && t.Map != null && !t.Map.Closed ? (MyVoxelBase)t.Map : null;
            bool planet = false;
            if (target == null)
            {
                MyPlanet pl = SiteBinding.Planet;
                if (pl == null || pl.Closed) return Fail("de.s8a drill: no tile holds SE " + TerrainTiles.V(w) + " (SN " + TerrainTiles.V(sn) + ") and no planet");
                target = pl;
                planet = true;
            }
            double beforeY;
            string beforeTile;
            if (planet) { beforeY = CastColumn(sn.X, sn.Z, sn.Y + 40.0, sn.Y - 80.0, out beforeTile); }
            else S4Harness.StorageColumn(sn.X, sn.Z, sn.Y + 20.0, sn.Y - 40.0, out beforeY, out beforeTile);
            ulong now = Win32.TickCount64();
            var p = new PendingDrill
            {
                Sn = sn, Se = w, Radius = radius, Path = path, Tile = planet ? "planet" : t.I + "," + t.J + "," + t.K, BeforeY = beforeY, BeforeTile = beforeTile,
                DueMs = now + DrillSettleMs, StartMs = now, Info = t, Tick = (long)GameState.GameplayTick, Planet = planet ? target : null,
            };
            var center = new Vector3D(w.X, w.Y, w.Z);
            string detail;
            if (path == "sphere")
            {
                target.PerformCutOutSphereFast(center, (float)radius, true);
                detail = "MyVoxelBase.PerformCutOutSphereFast(r=" + radius + ", notify)";
            }
            else
            {
                PendingDrill pp = p;
                target.CutOutShapeWithPropertiesAsync((pct, mat, dict) => { pp.Percent = pct; pp.Material = mat != null ? mat.Id.SubtypeName : null; },
                                                      new MyShapeSphere(center, (float)radius));
                detail = "MyVoxelBase.CutOutShapeWithPropertiesAsync(sphere r=" + radius + ") (Parallel.Start; the results arrive with the witness)";
            }
            _pending = p;
            _drills++;
            string line = "DE.S8a drill " + path + " at SN " + TerrainTiles.V(sn) + " r=" + radius + " tile=" + p.Tile + ": " + detail + " beforeY=" + F(beforeY) +
                          " (witness in " + DrillSettleMs + " ms; via " + via + ")" + (S8aMutants.M12 ? " (S8a.M12 ON: the guards block)" : "");
            DeLog.Info(line);
            var r = Ok(line);
            r["pending"] = true;
            r["path"] = path;
            r["tile"] = p.Tile;
            r["beforeY"] = beforeY;
            r["sn"] = S4Rows.Arr(sn);
            r["se"] = S4Rows.Arr(w);
            r["radius"] = radius;
            r["drillOn"] = S8aOverlay.DrillOn;
            return r;
        }

        /// <summary>The host's tick: the drill witness once the settle time passed (storage surface after, IsRangeModified, the s8a.drill row).</summary>
        public static void Tick(ulong nowMs, bool inWorld)
        {
            TickPass(nowMs, inWorld);
            PendingDrill p = _pending;
            if (p == null) return;
            if (!inWorld || !TerrainTiles.TerrainUp) { _pending = null; return; }
            if (nowMs < p.DueMs) return;
            _pending = null;
            try { Finish(p, nowMs); }
            catch (Exception e) { DeLog.Info("DE.S8a drill witness failed " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Finish(PendingDrill p, ulong nowMs)
        {
            double afterY;
            string afterTile;
            if (p.Planet != null) afterY = CastColumn(p.Sn.X, p.Sn.Z, p.Sn.Y + 40.0, p.Sn.Y - 80.0, out afterTile);
            else S4Harness.StorageColumn(p.Sn.X, p.Sn.Z, p.Sn.Y + 20.0, p.Sn.Y - 40.0, out afterY, out afterTile);
            bool modified = false;
            MyStorageBase storage = null;
            Vec3 corner = Vec3.Zero;
            int limit = SiteV1.Tile;
            if (p.Planet != null && !p.Planet.Closed)
            {
                storage = p.Planet.Storage as MyStorageBase;
                Vector3D c = p.Planet.PositionLeftBottomCorner;
                corner = new Vec3(c.X, c.Y, c.Z);
                limit = storage != null ? Math.Max(storage.Size.X, Math.Max(storage.Size.Y, storage.Size.Z)) : 0;
            }
            else if (p.Info != null && p.Info.Map != null && !p.Info.Map.Closed) { storage = p.Info.Map.Storage as MyStorageBase; corner = p.Info.PlaceMin; }
            if (storage != null)
            {
                Vec3 l = p.Se - corner;
                int r = (int)Math.Ceiling(p.Radius) + 1;
                var min = Vector3I.Max(new Vector3I((int)l.X - r, (int)l.Y - r, (int)l.Z - r), Vector3I.Zero);
                var max = Vector3I.Min(new Vector3I((int)l.X + r, (int)l.Y + r, (int)l.Z + r), new Vector3I(limit - 1));
                var bb = new BoundingBoxI(min, max);
                modified = storage.IsRangeModified(ref bb);
            }
            double deeper = !double.IsNaN(p.BeforeY) && !double.IsNaN(afterY) ? p.BeforeY - afterY : double.NaN;
            string line = "DE.S8a drill " + p.Path + " witness at SN " + TerrainTiles.V(p.Sn) + " tile=" + p.Tile + ": beforeY=" + F(p.BeforeY) + " afterY=" + F(afterY) +
                          " deeper=" + F(deeper) + " m modified=" + modified + (float.IsNaN(p.Percent) ? "" : " cut=" + p.Percent.ToString("0.###", CultureInfo.InvariantCulture) + " material=" + (p.Material ?? "null")) +
                          " overlayCells=" + S8aOverlay.Cells + " drillGen=" + S8aDrillConverter.DrillGen;
            DeLog.Info(line);
            Jsonl.WriteLine(S4Rows.Row("s8a.drill", "tick", p.Tick, "witnessTick", (long)GameState.GameplayTick, "sn", S4Rows.Arr(p.Sn), "se", S4Rows.Arr(p.Se), "radius", p.Radius,
                                       "path", p.Path, "guardPath", p.Path == "hand" ? "cutout" : "explosion", "tile", p.Tile, "beforeY", p.BeforeY, "afterY", afterY, "deeper", deeper, "modified", modified,
                                       "beforeTile", p.BeforeTile, "afterTile", afterTile, "cutPercent", float.IsNaN(p.Percent) ? (object)null : (double)p.Percent,
                                       "cutMaterial", p.Material, "settleMs", (long)(nowMs - p.StartMs), "overlayCells", S8aOverlay.Cells, "drillGen", (long)S8aDrillConverter.DrillGen,
                                       "drillOn", S8aOverlay.DrillOn, "m12", S8aMutants.M12));
            _lastDrill = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "sn", S4Rows.Arr(p.Sn) }, { "path", p.Path }, { "tile", p.Tile }, { "beforeY", p.BeforeY }, { "afterY", afterY }, { "deeper", deeper }, { "modified", modified }, { "line", line },
            };
        }

        // ================================================================================================== overlay / save

        private static Dictionary<string, object> Overlay(IDictionary<string, object> args)
        {
            int count;
            ulong hash = S8aOverlay.HashNow(out count);
            var r = Ok("de.s8a overlay: " + count + " cells hash=" + S8aStore.Hex16(hash) + " gen=" + S8aOverlay.OverlayGen + " lastSaved=" + S8aStore.Hex16(S8aOverlay.Hash));
            r["cells"] = count;
            r["hash"] = S8aStore.Hex16(hash);
            r["overlayGen"] = (long)S8aOverlay.OverlayGen;
            r["savedHash"] = S8aStore.Hex16(S8aOverlay.Hash);
            r["storeKey"] = S8aStore.Hex16(S8aOverlay.StoreKey);
            if (MiniJson.GetBool(args, "cells") == true) r["keys"] = S8aOverlay.CellKeys();
            return r;
        }

        private static Dictionary<string, object> Save(string via)
        {
            string path = null;
            try { path = MySession.Static != null ? MySession.Static.CurrentPath : null; } catch (Exception) { }
            if (string.IsNullOrEmpty(path)) return Fail("de.s8a save: no session");
            if (!S8aOverlay.ThrowawayMarked(path)) return Fail("de.s8a save REFUSED: not a throwaway copy (no DE_THROWAWAY marker in " + path + "); the owner's Esc -> Save is the fallback");
            if (TerrainFailSafe.Active) return Fail("de.s8a save: the terrain fail-safe is on (saving blocked)");
            MyAsyncSaving.Start(null, null);
            string line = "DE.S8a save: MyAsyncSaving.Start on " + path + " (via " + via + "; overlay cells " + S8aOverlay.Cells + ", postfix " + (S8aOverlay.SavePostfixInstalled ? "installed" : "MISSING") + ")";
            DeLog.Info(line);
            Jsonl.WriteLine(S4Rows.Row("s8a.save", "tick", (long)GameState.GameplayTick, "world", path, "cells", S8aOverlay.Cells, "postfix", S8aOverlay.SavePostfixInstalled));
            var r = Ok(line);
            r["world"] = path;
            r["cells"] = S8aOverlay.Cells;
            return r;
        }

        // ================================================================================================== rays

        private static Dictionary<string, object> Rays(IDictionary<string, object> args, string via)
        {
            if (!TerrainTiles.TerrainUp || TerrainTiles.Map == null) return Fail("de.s8a rays: no terrain tiles");
            object cols;
            if (!args.TryGetValue("columns", out cols) || !(cols is IList<object>)) return Fail("de.s8a rays: columns: [[x, z], ...] (SN metres)");
            var list = (IList<object>)cols;
            if (list.Count == 0 || list.Count > MaxRayColumns) return Fail("de.s8a rays: 1.." + MaxRayColumns + " columns");
            string label = MiniJson.GetString(args, "label") ?? "s8a.rays." + (++_rayBatches);
            double fromY = MiniJson.GetNumber(args, "fromY") ?? 20.0, toY = MiniJson.GetNumber(args, "toY") ?? -1950.0;
            // the columns are SN metres of the chart Subnautica draws: on a treadmill chart (index >= 1) the current chart's map, and the
 // site's tiles are not under them (ns 20261024-113052: SE's rays on site-v1 coordinates measured the ring 15 km from the sample)
            bool offSite = S8aChartManager.ChartIndex >= 1 && SiteBinding.Map != null;
            SiteMap map = offSite ? SiteBinding.Map : TerrainTiles.Map;
            var hits = new List<object>(list.Count);
            int castHits = 0, storageHits = 0;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (object o in list)
            {
                var c = o as IList<object>;
                if (c == null || c.Count < 2) return Fail("de.s8a rays: a column is [x, z]");
                double x = Convert.ToDouble(c[0], CultureInfo.InvariantCulture), z = Convert.ToDouble(c[1], CultureInfo.InvariantCulture);
                double sy;
                string tile;
                sy = double.NaN;
                tile = null;
                bool got = !offSite && S4Harness.StorageColumn(x, z, fromY, toY, out sy, out tile);
                Vec3 planetHit = Vec3.Zero;
                if (!got && PlanetColumn(map, x, z, fromY, toY, offSite || !S8aBiomes.InCraterBox(x, z), out sy, out planetHit)) { got = true; tile = "planet"; }
                if (got) storageHits++;
                double cy = double.NaN;
                string hitTile = null, material = null;
                try
                {
                    Vec3 a = map.SnToSe(new Vec3(x, fromY, z)), b = map.SnToSe(new Vec3(x, toY, z));
                    Hits.Clear();
                    MyPhysics.CastRay(new Vector3D(a.X, a.Y, a.Z), new Vector3D(b.X, b.Y, b.Z), Hits, 0);
                    double best = double.MaxValue;
                    MyVoxelBase bestBody = null;
                    Vec3 bestPoint = Vec3.Zero;
                    foreach (MyPhysics.HitInfo h in Hits)
                    {
                        IMyEntity e = h.HkHitInfo.GetHitEntity();
                        var vb = e as MyVoxelBase;
                        if (vb == null) continue;
                        var p = new Vec3(h.Position.X, h.Position.Y, h.Position.Z);
                        double d = (p - a).Length;
                        if (d >= best) continue;
                        best = d;
                        cy = map.SeToSn(p).Y;
                        bestBody = vb;
                        bestPoint = p;
                        var prov = vb.Storage != null ? (vb.Storage as MyStorageBase) : null;
                        var sp = prov != null ? prov.DataProvider as SnTerrainProvider : null;
                        hitTile = sp != null ? sp.I + "," + sp.J + "," + sp.K : vb is MyPlanet ? "planet" : e.GetType().Name;
                    }
                    // S8a.N10.material: the SE surface material under the hit (the planet's or a tile's), by subtype name; without a
 // physics hit, under the planet's mesh hit (the window's far columns)
                    if (MiniJson.GetBool(args, "material") == true)
                    {
                        Vec3 dir = b - a;
                        double len = dir.Length;
                        Vec3 down = len > 0 ? dir * (1.0 / len) : new Vec3(0, -1, 0);
                        if (bestBody != null) material = MaterialUnder(bestBody, bestPoint, down);
                        else if (tile == "planet" && SiteBinding.Planet != null) material = MaterialUnder(SiteBinding.Planet, planetHit, down);
                    }
                }
                catch (Exception e) { hitTile = "CastRay " + e.GetType().Name; }
                if (!double.IsNaN(cy)) castHits++;
                hits.Add(new Dictionary<string, object>(StringComparer.Ordinal) { { "x", x }, { "z", z }, { "storageY", sy }, { "castY", cy }, { "tile", tile }, { "castTile", hitTile }, { "material", material } });
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string line = "DE.S8a rays " + label + ": " + list.Count + " columns, storage hits " + storageHits + ", cast hits " + castHits + " in " + ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms (via " + via + ")";
            DeLog.Info(line);
            Jsonl.WriteLine(S4Rows.Row("s8a.rays", "tick", (long)GameState.GameplayTick, "label", label, "columns", list.Count, "storageHits", storageHits, "castHits", castHits,
                                       "fromY", fromY, "toY", toY, "ms", ms, "chartIndex", (long)S8aChartManager.ChartIndex, "offSite", offSite, "hits", hits));
            var r = Ok(line);
            r["label"] = label;
            r["chartIndex"] = (long)S8aChartManager.ChartIndex;
            r["columns"] = list.Count;
            r["storageHits"] = storageHits;
            r["castHits"] = castHits;
            r["hits"] = hits;
            return r;
        }

        /// <summary>
 /// The planet's LOD-0 mesh on the vertical SN column (x, z): <c>MyVoxelBase.GetIntersectionWithLine(ref LineD, out
 /// MyIntersectionResultLineTriangleEx?, IntersectionFlags)</c>
 /// on [fromY, toY], then on [150, −3,100] when <paramref name="wide"/>; SN y and the SE point of the first hit.
 /// </summary>
        private static bool PlanetColumn(SiteMap map, double x, double z, double fromY, double toY, bool wide, out double snY, out Vec3 hit)
        {
            snY = double.NaN;
            hit = Vec3.Zero;
            MyPlanet planet = SiteBinding.Planet;
            if (planet == null || map == null) return false;
            try
            {
                for (int k = 0; k < (wide ? 2 : 1); k++)
                {
                    double y0 = k == 0 ? fromY : 150.0, y1 = k == 0 ? toY : -3100.0;
                    Vec3 a = map.SnToSe(new Vec3(x, y0, z)), b = map.SnToSe(new Vec3(x, y1, z));
                    var line = new LineD(new Vector3D(a.X, a.Y, a.Z), new Vector3D(b.X, b.Y, b.Z));
                    MyIntersectionResultLineTriangleEx? r;
                    if (planet.GetIntersectionWithLine(ref line, out r, IntersectionFlags.ALL_TRIANGLES) && r.HasValue)
                    {
                        Vector3D w = r.Value.IntersectionPointInWorldSpace;
                        hit = new Vec3(w.X, w.Y, w.Z);
                        snY = map.SeToSn(hit).Y;
                        return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        // ================================================================================================== probe (Stage C)

        public const int MaxProbePoints = 256;

        /// <summary>The storage material (subtype) <paramref name="depth"/> metres below a surface point along <paramref name="down"/> (LOD 0).</summary>
        private static string MaterialAtDepth(MyVoxelBase vb, Vec3 seHit, Vec3 down, double depth)
        {
            return MaterialUnder(vb, seHit + down * (depth - 0.75), down);
        }

        private static Dictionary<string, object> Probe(IDictionary<string, object> args, string via)
        {
            MyPlanet planet = SiteBinding.Planet;
            SiteMap map = SiteBinding.SiteMapV1 ?? SiteBinding.Map;
            if (planet == null || map == null) return Fail("de.s8a probe: no DE_4546B planet bound (site " + SiteBinding.Status + ")");
            if (MiniJson.GetBool(args, "planet") == false) return Fail("de.s8a probe: only {planet: true} is implemented");
            object pts;
            if (!args.TryGetValue("points", out pts) || !(pts is IList<object>)) return Fail("de.s8a probe: points: [[x, z], ...] (SN metres, site v1)");
            var list = (IList<object>)pts;
            if (list.Count == 0 || list.Count > MaxProbePoints) return Fail("de.s8a probe: 1.." + MaxProbePoints + " points");
            var depths = new List<double>();
            object dv;
            if (args.TryGetValue("depths", out dv) && dv is IList<object>)
                foreach (object o in (IList<object>)dv) depths.Add(Convert.ToDouble(o, CultureInfo.InvariantCulture));
            string label = MiniJson.GetString(args, "label") ?? "s8a.probe";
            WaterModClient api = SiteBinding.Api;
            var rows = new List<object>(list.Count);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int storageHits = 0;
            foreach (object o in list)
            {
                var c = o as IList<object>;
                if (c == null || c.Count < 2) return Fail("de.s8a probe: a point is [x, z]");
                double x = Convert.ToDouble(c[0], CultureInfo.InvariantCulture), z = Convert.ToDouble(c[1], CultureInfo.InvariantCulture);
                var row = new Dictionary<string, object>(StringComparer.Ordinal) { { "x", x }, { "z", z } };
                try
                {
                    Vec3 sea = map.SnToSe(new Vec3(x, 0, z));
                    Vector3D cp = planet.GetClosestSurfacePointGlobal(new Vector3D(sea.X, sea.Y, sea.Z));
                    Vec3 cse = new Vec3(cp.X, cp.Y, cp.Z);
                    Vec3 csn = map.SeToSn(cse);
                    row["closestY"] = csn.Y;
                    row["closestDrift"] = Math.Sqrt((csn.X - x) * (csn.X - x) + (csn.Z - z) * (csn.Z - z));
                    // the voxels: SE's LOD-0 mesher over the planet storage on the vertical SN line around the closest point, then wide
                    double sy = double.NaN;
                    Vec3 hit = Vec3.Zero;
                    foreach (double[] span in new[] { new[] { csn.Y + 40.0, csn.Y - 40.0 }, new[] { 150.0, -3100.0 } })
                    {
                        Vec3 a = map.SnToSe(new Vec3(x, span[0], z)), b = map.SnToSe(new Vec3(x, span[1], z));
                        var line = new LineD(new Vector3D(a.X, a.Y, a.Z), new Vector3D(b.X, b.Y, b.Z));
                        MyIntersectionResultLineTriangleEx? r;
                        if (planet.GetIntersectionWithLine(ref line, out r, IntersectionFlags.ALL_TRIANGLES) && r.HasValue)
                        {
                            Vector3D w = r.Value.IntersectionPointInWorldSpace;
                            hit = new Vec3(w.X, w.Y, w.Z);
                            sy = map.SeToSn(hit).Y;
                            break;
                        }
                    }
                    row["storageY"] = sy;
                    if (!double.IsNaN(sy)) storageHits++;
                    float? wd = api != null ? api.Depth(cp, planet) : null;
                    row["waterDepth"] = wd.HasValue ? (object)(double)wd.Value : null;
                    Vec3 down = map.SnToSe(new Vec3(x, -1.0, z)) - map.SnToSe(new Vec3(x, 0.0, z));
                    down = down * (1.0 / down.Length);
                    Vec3 at = double.IsNaN(sy) ? cse : hit;
                    row["material"] = MaterialUnder(planet, at, down);
                    if (depths.Count > 0)
                    {
                        var mats = new List<object>(depths.Count);
                        var ds = new List<object>(depths.Count);
                        foreach (double d in depths) { mats.Add(MaterialAtDepth(planet, at, down, d)); ds.Add(d); }
                        row["depths"] = ds;
                        row["materials"] = mats;
                    }
                    try
                    {
                        var prov = planet.Provider;
                        if (prov != null && prov.Material != null)
                        {
                            Vector3D corner = planet.PositionLeftBottomCorner;
                            var lp = new Vector3((float)(at.X - corner.X + down.X), (float)(at.Y - corner.Y + down.Y), (float)(at.Z - corner.Z + down.Z));
                            byte biome;
                            prov.Material.GetMaterialForPosition(ref lp, 1f, out biome, false);
                            row["biomeMap"] = (long)biome;
                        }
                    }
                    catch (Exception e) { row["biomeError"] = e.GetType().Name; }
                }
                catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
                rows.Add(row);
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            uint? rsBits = api != null ? api.RsBits(planet) : null;
            string line2 = "DE.S8a probe " + label + ": " + list.Count + " points, storage hits " + storageHits + ", planet R " + F(planet.AverageRadius) + " min " + F(planet.MinimumRadius) +
                           " max " + F(planet.MaximumRadius) + ", Rs " + F(SiteBinding.Rs) + ", sculptHash " + S8aStore.Hex16(S8aHost.SculptHash) + " in " + ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms (via " + via + ")";
            DeLog.Info(line2);
            Jsonl.WriteLine(S4Rows.Row("s8a.probe", "tick", (long)GameState.GameplayTick, "label", label, "points", list.Count, "storageHits", storageHits,
                                       "minimumRadius", (double)planet.MinimumRadius, "averageRadius", (double)planet.AverageRadius, "maximumRadius", (double)planet.MaximumRadius,
                                       "rs", SiteBinding.Rs, "rsBits", rsBits.HasValue ? (object)(long)rsBits.Value : null, "sculptHash", S8aStore.Hex16(S8aHost.SculptHash),
                                       "planetDataDir", S8aHost.PlanetDataDir, "ms", ms, "rows", rows));
            var res = Ok(line2);
            res["label"] = label;
            res["rows"] = rows;
            res["storageHits"] = storageHits;
            res["minimumRadius"] = (double)planet.MinimumRadius;
            res["averageRadius"] = (double)planet.AverageRadius;
            res["maximumRadius"] = (double)planet.MaximumRadius;
            res["rs"] = SiteBinding.Rs;
            res["sculptHash"] = S8aStore.Hex16(S8aHost.SculptHash);
            res["planetDataDir"] = S8aHost.PlanetDataDir;
            return res;
        }

        // ================================================================================================== window / step (S8a-2)

 /// <summary><c>{action: "window", throttleMs?, on?}</c>: the worker's per-batch sleep (S8a.N9) and the runtime switch; the status either way.</summary>
        private static Dictionary<string, object> Window(IDictionary<string, object> args, string via)
        {
            double? throttle = MiniJson.GetNumber(args, "throttleMs");
            bool? on = MiniJson.GetBool(args, "on");
            if (throttle.HasValue || on.HasValue)
            {
                S8aWindow.Configure(on ?? S8aWindow.On, throttle.HasValue ? (int)throttle.Value : S8aWindow.ThrottleMs);
                DeLog.Info("DE.S8a window set: on=" + S8aWindow.On + " throttleMs=" + S8aWindow.ThrottleMs + " (via " + via + ")");
            }
            Dictionary<string, object> r = S8aWindow.Status();
            r["ok"] = true;
            r["line"] = "de.s8a window: " + (S8aWindow.WindowOn ? "on" : "off") + " throttleMs=" + S8aWindow.ThrottleMs + " generated=" + r["generated"] + " pending=" + r["pending"];
            return r;
        }

        /// <summary><c>{action: "step", axis: "x" | "z" | "site", sign}</c>: a forced treadmill step (S8a.N8).</summary>
        private static Dictionary<string, object> Step(IDictionary<string, object> args, string via)
        {
            string axis = (MiniJson.GetString(args, "axis") ?? "x").Trim().ToLowerInvariant();
            int sign = (int)(MiniJson.GetNumber(args, "sign") ?? 1.0);
            Vec3 cam;
            bool have = S8aHost.Camera(out cam);
            Dictionary<string, object> r = S8aChartManager.Force(axis, sign, cam, have);
            DeLog.Info("DE.S8a step " + axis + " " + sign + " (via " + via + "): " + r["line"]);
            return r;
        }

        // ================================================================================================== helpers

 // ================================================================================================== Stage D: the drill pass

 /// <summary>A continuous drill along a line (Stage D oracle: SE's ship drill as a series of cuts, no witness between them).</summary>
        private sealed class DrillPassState
        {
            public Vec3 From, To;
            public double Radius;
            public ulong StartMs, EveryMs, EndMs, NextMs;
            public int Cuts, Planet, Tiles, Missed;
            public string Via;
        }

        private static DrillPassState _pass;

        /// <summary>
 /// de.s8a drillpass {from: [x, y, z], to: [x, y, z] (SN), seconds (20), everyMs (250), radius (2.5)}: SE cuts a sphere every
 /// everyMs along the line for the given time (<c>MyVoxelBase.PerformCutOutSphereFast</c>, the tiles or the planet, as the drill
 /// action) on the game thread; rows s8a.drillpass per cut and at the end (Stage D: the hole must show in every Subnautica frame).
 /// </summary>
        private static Dictionary<string, object> DrillPass(IDictionary<string, object> args, string via)
        {
            if (!S8aOverlay.Enabled) return Fail("de.s8a drillpass: S8a is off (" + S8aOverlay.EnvS8a + ")");
            if (TerrainTiles.Map == null) return Fail("de.s8a drillpass: no site map");
            if (MiniJson.GetBool(args, "stop") == true) { _pass = null; return Ok("de.s8a drillpass: stopped"); }
            double[] a = S4Args.Numbers(args, "from", 3), b = S4Args.Numbers(args, "to", 3);
            if (a == null || b == null) return Fail("de.s8a drillpass: from, to: [x, y, z] (Subnautica metres)");
            double secs = Math.Max(1.0, Math.Min(120.0, MiniJson.GetNumber(args, "seconds") ?? 20.0));
            double every = Math.Max(50.0, Math.Min(2000.0, MiniJson.GetNumber(args, "everyMs") ?? 250.0));
            double radius = Math.Max(0.5, Math.Min(16.0, MiniJson.GetNumber(args, "radius") ?? 2.5));
            ulong now = Win32.TickCount64();
            _pass = new DrillPassState
            {
                From = new Vec3(a[0], a[1], a[2]), To = new Vec3(b[0], b[1], b[2]), Radius = radius, StartMs = now, EveryMs = (ulong)every,
                EndMs = now + (ulong)(secs * 1000.0), NextMs = now, Via = via,
            };
            string line = "DE.S8a drillpass from SN " + TerrainTiles.V(_pass.From) + " to " + TerrainTiles.V(_pass.To) + " for " + secs + " s, a cut of r=" + radius + " every " + every + " ms (via " + via + ")";
            DeLog.Info(line);
            var r = Ok(line);
            r["cuts"] = (long)Math.Ceiling(secs * 1000.0 / every);
            return r;
        }

        private static void TickPass(ulong nowMs, bool inWorld)
        {
            DrillPassState p = _pass;
            if (p == null) return;
            if (!inWorld) { _pass = null; return; }
            if (nowMs < p.NextMs) return;
            if (nowMs > p.EndMs)
            {
                _pass = null;
                Jsonl.WriteLine(S4Rows.Row("s8a.drillpass", "tick", (long)GameState.GameplayTick, "phase", "end", "cuts", p.Cuts, "planet", p.Planet, "tiles", p.Tiles, "missed", p.Missed,
                                           "ms", (long)(nowMs - p.StartMs)));
                DeLog.Info("DE.S8a drillpass done: " + p.Cuts + " cuts (" + p.Tiles + " tiles, " + p.Planet + " planet, " + p.Missed + " missed)");
                return;
            }
            p.NextMs += p.EveryMs;
            double u = (double)(nowMs - p.StartMs) / Math.Max(1.0, p.EndMs - p.StartMs);
            var sn = new Vec3(p.From.X + (p.To.X - p.From.X) * u, p.From.Y + (p.To.Y - p.From.Y) * u, p.From.Z + (p.To.Z - p.From.Z) * u);
            Vec3 w = TerrainTiles.Map.SnToSe(sn);
            try
            {
                TerrainTiles.TileInfo ti = TerrainTiles.TileAt(w);
                MyVoxelBase target = ti != null && ti.Map != null && !ti.Map.Closed ? (MyVoxelBase)ti.Map : null;
                bool planet = false;
                if (target == null)
                {
                    MyPlanet pl = SiteBinding.Planet;
                    if (pl != null && !pl.Closed) { target = pl; planet = true; }
                }
                if (target == null) { p.Missed++; return; }
                target.PerformCutOutSphereFast(new Vector3D(w.X, w.Y, w.Z), (float)p.Radius, true);
                p.Cuts++;
                if (planet) p.Planet++; else p.Tiles++;
                Jsonl.WriteLine(S4Rows.Row("s8a.drillpass", "tick", (long)GameState.GameplayTick, "phase", "cut", "i", p.Cuts, "sn", new object[] { sn.X, sn.Y, sn.Z },
                                           "target", planet ? "planet" : ti.I + "," + ti.J + "," + ti.K));
            }
            catch (Exception e) { p.Missed++; DeLog.Info("DE.S8a drillpass cut failed " + e.GetType().Name + ": " + e.Message); }
        }

        private static bool PointArg(IDictionary<string, object> args, out Vec3 w, out Vec3 sn, out string why)
        {
            w = sn = Vec3.Zero;
            why = null;
            double[] se = S4Args.Numbers(args, "se", 3), p = S4Args.Numbers(args, "sn", 3);
            if (se != null) { w = new Vec3(se[0], se[1], se[2]); sn = TerrainTiles.Map.SeToSn(w); return true; }
            if (p != null) { sn = new Vec3(p[0], p[1], p[2]); w = TerrainTiles.Map.SnToSe(sn); return true; }
            why = "needs sn: [x, y, z] (Subnautica metres) or se: [x, y, z]";
            return false;
        }

        private static string F(double v) { return double.IsNaN(v) ? "n/a" : v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.S8a " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
