using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Library.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// md §4–§8, S1-STATUS §7.2)
 /// C11 and the <c>/de</c> chat relay, dev builds only (DevChannel gates them):
 /// <list type="bullet">
 /// <item><c>de.spawnplanet {definition, size, seed, centre, dryRun}</c>: <c>MyWorldGenerator.AddPlanet</c> with the
 /// min corner that puts the centre exactly at <c>centre</c> (no spawn menu, no save edit); logs the centre SE reports
 /// right after the spawn against the request, so the real run proves the min-corner rule; then the site binding
 /// validates again;</item>
 /// <item><c>de.g2e3</c>: the S1.G2-E3 checks computed from the game API (no rigs), one
 /// <c>DE.G2E3 &lt;check&gt; &lt;value&gt; &lt;expected&gt; PASS|FAIL</c> line per row and a summary.</item>
 /// </list>
 /// Game members used:
 /// <c>MyWorldGenerator.AddPlanet(string storageName, string planetName, string definitionName, Vector3D
 /// positionMinCorner, int seed, float size, bool fadeIn, long entityId = 0, bool addGPS = false, bool userCreated =
 /// false)</c> (public static; the spawn menu's server path <c>SpawnPlanet_Server</c> calls it with planetName =
 /// definitionName, fadeIn true, entityId <c>MyRandom.Instance.NextLong</c>, addGPS false, userCreated true, then
 /// raises <c>RequiresDX</c> to 11 and broadcasts <c>SpawnPlanet_Client</c> to clients, which an offline session has
 /// none of); <c>MyDefinitionManager.Static.GetPlanetsGeneratorsDefinitions</c>; <c>MyPlanets.GetPlanets</c>,
 /// <c>MyPlanets.Static.CanSpawnPlanet(def, register: false, size, out error)</c>; <c>MySession.Static.VoxelMaps.Instances</c>;
 /// <c>Sandbox.Game.MyPerGameSettings.OffsetVoxelMapByHalfVoxel</c>; <c>MyPlanetGeneratorDefinition.HillParams</c>;
 /// <c>MyPlanet.MinimumRadius/AverageRadius/MaximumRadius</c> (float properties), <c>GetAirDensity(Vector3D)</c>,
 /// <c>GetOxygenForPosition(Vector3D)</c>, <c>GetClosestSurfacePointGlobal(ref Vector3D)</c>, <c>HasAtmosphere</c>;
 /// <c>MyVoxelBase.GetMaterialAt(ref Vector3D)</c> (storage read, null for an empty voxel), <c>PositionLeftBottomCorner</c>,
 /// <c>Storage.Size</c>, <c>StorageName</c>; <c>MyGravityProviderSystem.CalculateNaturalGravityInPoint(Vector3D)</c>
 /// (Vector3, m/s²: each in-range provider's direction × 9.81 × multiplier); <c>MyOxygenProviderSystem.GetOxygenInPoint(Vector3D)</c>
 /// (public static float, the saturated sum over the providers in range: what an unsealed air vent reads);
 /// <c>MyDefinitionId.TryParse(string, out MyDefinitionId)</c>, <c>MyDefinitionManager.TryGetCubeBlockDefinition</c>
 /// and the public fields of <c>MyThrustDefinition</c>.
 /// <para>Threading: the commands run where DevChannel runs them: the plugin's <c>Update</c> (C11), which
 /// <c>MySandboxGame.Update</c> calls on the update thread inside <c>HkAccessControl.PushState(Exclusive)</c>, or
 /// <c>MyAPIUtilities.MessageEntered</c> (chat), raised by <c>MyGuiScreenChat</c> from GUI input handling, also on
 /// the update thread and outside the entity update loop; so AddPlanet is called directly (no
 /// <c>MySandboxGame.Static.Invoke</c>), exactly where the spawn menu's offline click calls it.</para>
 /// Never throws: every failure is logged (<c>DE.Error …</c>) and answered <c>ok: false</c>. An exception out of
 /// AddPlanet after the planet was added (a throwing <c>MyEntities.OnEntityAdd</c> handler of another mod) still
 /// finishes the spawn (RequiresDX, rebind) and replies <c>ok: false</c> with the planet's fields.
 /// </summary>
    internal static class LabCommands
    {
        /// <summary>The stock thruster whose force multiplier is check 6 (Content CubeBlocks_Thrusters.sbc: influence 0.3..1 → 0..1).</summary>
        public const string AtmoThrusterId = "MyObjectBuilder_Thrust/SmallBlockSmallAtmosphericThrust";
        /// <summary>The floor material is sampled this far below the floor surface (MaxDepth of the sand layer is 5 m).</summary>
        public static readonly double[] MaterialProbeDepthsM = { 1.5, 3.0 };
        /// <summary>Tolerance of the "centre after the spawn equals the request" log row (the rule is exact in doubles).</summary>
        public const double SpawnCentreTolM = 1e-6;

        // =============================================================================================== spawnplanet

        public static Dictionary<string, object> SpawnPlanet(IDictionary<string, object> args, string via)
        {
            try { return SpawnPlanetCore(args, via); }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.spawnplanet " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
                return Fail("DE.Lab spawnplanet FAILED " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static Dictionary<string, object> SpawnPlanetCore(IDictionary<string, object> args, string via)
        {
            SpawnRequest req;
            string why;
            if (!LabSpawn.TryParse(args, out req, out why)) return Refuse(why);
            if (!InWorld(out why)) return Refuse(why);
            if (!ActivationGate.IsOpen)
                return Refuse("activation gate " + ActivationGate.State + " (" + ActivationGate.Reason + "): offline single player only");
            MySession session = MySession.Static;

            // the generator definition
            MyPlanetGeneratorDefinition def = null;
            var ids = new List<string>();
            foreach (MyPlanetGeneratorDefinition d in MyDefinitionManager.Static.GetPlanetsGeneratorsDefinitions())
            {
                if (d == null) continue;
                ids.Add(d.Id.SubtypeName);
                if (def == null && d.Id.SubtypeName == req.Definition) def = d;
            }
            ids.Sort(StringComparer.Ordinal);
            if (def == null)
                return Refuse("no PlanetGeneratorDefinition '" + req.Definition + "' loaded (is the DeepEngineers.World mod in this world?); loaded generators [" +
                              string.Join(", ", ids.ToArray()) + "]");

            // the session: no planet of this generator yet, no other planet near the centre, the storage name free
            var others = new List<KeyValuePair<string, Vec3>>();
            List<MyPlanet> planets = MyPlanets.GetPlanets();
            if (planets != null)
            {
                foreach (MyPlanet p in planets)
                {
                    if (p == null || p.Closed) continue;
                    string sub = p.Generator != null ? p.Generator.Id.SubtypeName : "?";
                    Vector3D pc = p.PositionComp.GetPosition();
                    if (sub == req.Definition)
                        return Refuse("a " + req.Definition + " planet already exists in this session (#" + p.EntityId + " at " + LabSpawn.V(ToVec(pc)) +
                                      "); one per world (site v1)");
                    others.Add(new KeyValuePair<string, Vec3>(sub + "#" + p.EntityId, ToVec(pc)));
                }
            }
            double nearest;
            string close = LabSpawn.TooClosePlanet(others, req.Centre, out nearest);
            if (close != null)
                return Refuse("planet " + close + " has its centre " + Math.Round(nearest).ToString(CultureInfo.InvariantCulture) +
                              " m from the requested centre (< " + LabSpawn.Num(LabSpawn.MinOtherPlanetDistanceM) + " m, DE_LAB_WORLD.md §2)");
            string storageName = LabSpawn.StorageName(req.Definition, req.Seed, req.Size);
            foreach (MyVoxelBase vb in session.VoxelMaps.Instances)
            {
                if (vb != null && vb.StorageName == storageName)
                    return Refuse("a voxel map with the storage name " + storageName + " exists (#" + vb.EntityId + ")");
            }
            MyPlanets reg = MyPlanets.Static;
            string canErr;
            if (reg != null && !reg.CanSpawnPlanet(def, false, req.Size, out canErr))
                return Refuse("MyPlanets.CanSpawnPlanet: " + canErr + " (MaxPlanets " + session.Settings.MaxPlanets + ")");

            // the min corner (LabSpawn: storage size from the provider rule, minus the half voxel SE adds)
            float hillMax = def.HillParams.Max;
            int storageSize = LabSpawn.StorageSize(req.Size, hillMax);
            bool halfVoxel = Sandbox.Game.MyPerGameSettings.OffsetVoxelMapByHalfVoxel;
            Vec3 minCorner = LabSpawn.MinCornerFromCentre(req.Centre, storageSize, halfVoxel);
            string planLine = "definition=" + req.Definition + " size=" + LabSpawn.Num(req.Size) + " seed=" + req.Seed +
                              " centre=" + LabSpawn.V(req.Centre) + " storageName=" + storageName + " hillMax=" + hillMax.ToString("R", CultureInfo.InvariantCulture) +
                              " storageSize=" + storageSize + " offsetVoxelMapByHalfVoxel=" + halfVoxel + " minCorner=" + LabSpawn.V(minCorner) +
                              " nearestOtherPlanet=" + (others.Count > 0 ? Math.Round(nearest).ToString(CultureInfo.InvariantCulture) + "m" : "none");

            if (req.DryRun)
            {
                string dl = "DE.Lab spawnplanet DRY RUN " + planLine + " (nothing spawned; via " + via + ")";
                DeLog.Info(dl);
                var dr = Ok(dl);
                dr["dryRun"] = true;
                dr["storageName"] = storageName;
                dr["storageSize"] = storageSize;
                dr["minCorner"] = Arr(minCorner);
                dr["centre"] = Arr(req.Centre);
                return dr;
            }

            DeLog.Info("DE.Lab spawnplanet request " + planLine + " (via " + via + ")");
            long entityId = MyRandom.Instance.NextLong();
            var minD = new Vector3D(minCorner.X, minCorner.Y, minCorner.Z);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            MyPlanet planet;
            Exception addError = null;
            try
            {
                planet = MyWorldGenerator.AddPlanet(storageName, req.Definition, req.Definition, minD, req.Seed, req.Size,
                                                    true, entityId, false, true);
            }
            catch (Exception e)
            {
                // Review fix: an exception out of AddPlanet does not mean "nothing spawned". CreatePlanet lets
 // MyPlanetStorageProvider.Init's MyPlanetWhitelistException escape (thrown before any entity exists), but
 // MyEntities.Add raises OnEntityAdd only after the planet is registered (MyPlanet.OnAddedToScene ->
 // MyPlanets.Register) and added: a throwing handler of another mod leaves a live planet behind. Look it up,
 // and finish the spawn (RequiresDX, site rebind) when it is there, replying ok:false with the exception.
                addError = e;
                DeLog.Info("DE.Error de.spawnplanet MyWorldGenerator.AddPlanet threw " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
                planet = FindPlanetByStorageName(storageName);
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (planet == null)
                return Refuse(addError != null
                    ? "MyWorldGenerator.AddPlanet threw " + addError.GetType().Name + ": " + addError.Message + " after " +
                      ms.ToString("0", CultureInfo.InvariantCulture) + " ms and no planet " + storageName + " exists (nothing spawned)"
                    : "MyWorldGenerator.AddPlanet returned null after " + ms.ToString("0", CultureInfo.InvariantCulture) +
                      " ms (planets disabled, MyPlanetWhitelistException or a storage error: see the SE log)");

            // From here the planet exists: every step is guarded on its own, so the rebind and the reply always happen.
            int dxWas = session.RequiresDX;
            if (dxWas < 11) session.RequiresDX = 11; // as the spawn menu (SpawnPlanet_Server / SpawnPlanet_Client)

            var r = Ok(null);
            string line;
            try
            {
                Vec3 got = ToVec(planet.PositionComp.GetPosition());
                double err = Vec3.MaxAbsDiff(got, req.Centre);
                bool formulaOk = err <= SpawnCentreTolM;
                Vector3I size = planet.Storage != null ? planet.Storage.Size : Vector3I.Zero;
                Vec3 corner = ToVec(planet.PositionLeftBottomCorner);
                // the Water Mod adds the sea from its MyEntities.OnEntityAdd handler, i.e. inside AddPlanet (WaterModComponent)
                WaterModClient api = SiteBinding.Api;
                bool? water = api != null ? api.HasWater(planet) : null;
                line = "DE.Lab spawnplanet done entityId=" + planet.EntityId + " storageName=" + planet.StorageName +
                       " centre(PositionComp.GetPosition())=" + LabSpawn.V(got) + " requested=" + LabSpawn.V(req.Centre) +
                       " maxAxisError=" + LabSpawn.Num(err) + " m min-corner rule " + (formulaOk ? "PASS" : "FAIL") +
                       " storage.Size=" + size.X + " (computed " + storageSize + ")" + " PositionLeftBottomCorner=" + LabSpawn.V(corner) +
                       " radii min/avg/max=" + LabSpawn.Num(planet.MinimumRadius) + "/" + LabSpawn.Num(planet.AverageRadius) + "/" + LabSpawn.Num(planet.MaximumRadius) +
                       " RequiresDX=" + session.RequiresDX + " (was " + dxWas + ") addPlanetMs=" + ms.ToString("0", CultureInfo.InvariantCulture) +
                       " waterMod.HasWater=" + (water.HasValue ? water.Value.ToString() : "unknown") +
                       (addError != null ? " AddPlanet THREW " + addError.GetType().Name + " after adding the planet" : "") +
                       " (via " + via + ")";
                DeLog.Info(line);
                if (!formulaOk || size.X != storageSize)
                    DeLog.Info("DE.Lab spawnplanet WARNING the centre or the storage size differs from the computed rule: report this run (LabSpawn, DE_LAB_WORLD.md §4)");
                r["centre"] = Arr(got);
                r["centreError"] = err;
                r["formula"] = formulaOk ? "PASS" : "FAIL";
                r["storageSize"] = size.X;
                r["hasWater"] = water;
            }
            catch (Exception e)
            {
                line = "DE.Lab spawnplanet done entityId=" + planet.EntityId + " storageName=" + storageName + " (post-spawn read failed: " +
                       e.GetType().Name + ": " + e.Message + "; via " + via + ")";
                DeLog.Info("DE.Error de.spawnplanet post-spawn read " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
                DeLog.Info(line);
                r["formula"] = "UNKNOWN";
            }

            Dictionary<string, object> rebind = null;
            try { rebind = SiteBinding.Rebind("de.spawnplanet created #" + planet.EntityId); }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.spawnplanet site rebind " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e) +
                           " (the 10 s planet poll of the site binding rebinds by itself)");
            }

            r["line"] = line;
            r["entityId"] = planet.EntityId;
            r["requestedCentre"] = Arr(req.Centre);
            r["storageName"] = storageName;
            r["minCorner"] = Arr(minCorner);
            r["requiresDX"] = session.RequiresDX;
            r["siteRebind"] = rebind != null && rebind.ContainsKey("line") ? rebind["line"] : null;
            if (addError != null)
            {
                r["ok"] = false;
                r["error"] = "MyWorldGenerator.AddPlanet threw " + addError.GetType().Name + ": " + addError.Message +
                             " after adding planet #" + planet.EntityId + " (it exists: check hasWater, run de.g2e3, read the SE log)";
            }
            return r;
        }

        /// <summary>The live planet whose storage name is <paramref name="storageName"/>, or null (never throws).</summary>
        private static MyPlanet FindPlanetByStorageName(string storageName)
        {
            try
            {
                List<MyPlanet> planets = MyPlanets.GetPlanets();
                if (planets == null) return null;
                foreach (MyPlanet p in planets)
                    if (p != null && !p.Closed && p.StorageName == storageName) return p;
            }
            catch (Exception e) { DeLog.Info("DE.Error de.spawnplanet planet lookup " + e.GetType().Name + ": " + e.Message); }
            return null;
        }

        // ======================================================================================================= g2e3

        public static Dictionary<string, object> G2E3Check(IDictionary<string, object> args, string via)
        {
            try { return G2E3Core(args, via); }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.g2e3 " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
                return Fail("DE.G2E3 FAILED " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static Dictionary<string, object> G2E3Core(IDictionary<string, object> args, string via)
        {
            string why;
            if (args != null && args.Count > 0)
                return Refuse("de.g2e3 takes no arguments (" + string.Join(", ", new List<string>(args.Keys).ToArray()) + " given)", "DE.G2E3");
            if (!InWorld(out why)) return Refuse(why, "DE.G2E3");

            var f = new G2E3Facts();
            MyPlanet planet = null;
            List<MyPlanet> planets = MyPlanets.GetPlanets();
            if (planets != null)
            {
                foreach (MyPlanet p in planets)
                {
                    if (p == null || p.Closed || p.Generator == null || p.Generator.Id.SubtypeName != SiteV1.GeneratorSubtypeId) continue;
                    f.PlanetCount++;
                    if (planet == null) planet = p;
                }
            }
            if (planet != null) Gather(planet, f);

            List<G2E3Row> rows = G2E3.Evaluate(f);
            DeLog.Info("DE.G2E3 S1.G2-E3 from the game API (no rigs; DE_LAB_WORLD.md §8): planet " +
                       (planet != null ? "#" + planet.EntityId : "none") + " count=" + f.PlanetCount +
                       (f.Centre.HasValue ? " C=" + LabSpawn.V(f.Centre.Value) : "") + (f.RsBits.HasValue ? " Rs=" + SiteValidation.RsText(f.RsBits.Value) : " Rs unreadable") +
                       " (via " + via + ")");
            var table = new List<object>(rows.Count);
            foreach (G2E3Row row in rows)
            {
                DeLog.Info(row.Line);
                table.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "check", row.Check }, { "value", row.Value }, { "expected", row.Expected }, { "status", row.StatusName }, { "note", row.Note },
                    { "manual", row.ManualKind },
                });
            }
            int pass, fail, manual;
            string summary = G2E3.Summary(rows, out pass, out fail, out manual);
            DeLog.Info(summary);
            var r = Ok(summary);
            r["pass"] = pass;
            r["fail"] = fail;
            r["manual"] = manual;
            r["allPass"] = fail == 0;
            r["rows"] = table;
            return r;
        }

        /// <summary>Reads every fact of one DE_4546B planet; each read is guarded so one failure leaves the others.</summary>
        private static void Gather(MyPlanet planet, G2E3Facts f)
        {
            Vec3 c = Vec3.Zero;
            try
            {
                c = ToVec(planet.PositionComp.GetPosition());
                f.Centre = c;
                f.MinimumRadius = planet.MinimumRadius;
                f.AverageRadius = planet.AverageRadius;
                f.MaximumRadius = planet.MaximumRadius;
            }
            catch (Exception e) { Error("centre/radii", e); return; }

            // the Water Mod (API client of the site binding; settings by version-gated reflection)
            WaterModClient api = SiteBinding.Api;
            if (api == null) f.SettingsError = "no Water Mod API client in this session (multiplayer, or the plugin is not active)";
            else if (!api.Usable) f.SettingsError = "Water Mod API not usable: " + api.Describe();
            else
            {
                try
                {
                    f.RsBits = api.RsBits(planet);
                    WaterSettingsSnapshot ws;
                    string swhy;
                    if (api.TryReadSettings(planet, out ws, out swhy))
                    {
                        f.SettingsRadiusBits = SiteV1.FloatBits(ws.SettingsRadius);
                        f.WaveHeight = ws.WaveHeight;
                        f.TideHeight = ws.TideHeight;
                        f.CrushDamage = ws.CrushDamage;
                        f.CurrentSpeed = ws.CurrentSpeed;
                        f.EnableFish = ws.EnableFish;
                        f.Volumetric = ws.Volumetric;
                    }
                    else f.SettingsError = swhy;
                }
                catch (Exception e) { Error("water", e); }
            }
            double rs = f.RsBits.HasValue ? (double)SiteValidation.FloatFromBits(f.RsBits.Value) : SiteV1.RsExpected;
            f.RsAssumed = !f.RsBits.HasValue;
            Vec3 u0 = SiteV1.AnchorU0;
            Vec3 water = c + u0 * rs;

            try { f.SeaLevelElevation = (water - c).Length - planet.AverageRadius; }
            catch (Exception e) { Error("sea level", e); }

            f.GravityWater = GravityAt(c + u0 * rs);
            f.GravityMinus1000 = GravityAt(c + u0 * (rs - 1000.0));
            f.GravityMinus2000 = GravityAt(c + u0 * (rs - 2000.0));

            try
            {
                Vector3D w = ToD(water);
                float air = planet.GetAirDensity(w);
                f.AirDensity = air;
                // Check 7 is the air vent's reading (guide §8, Rig A): an unsealed vent's room takes its oxygen from
 // MyOxygenProviderSystem.GetOxygenInPoint (the saturated sum over every provider in range: the planets, and
 // the Water Mod's WaterOxygenProvider), not from the planet alone; both are recorded.
                f.OxygenPlanet = planet.GetOxygenForPosition(w);
                try { f.Oxygen = MyOxygenProviderSystem.GetOxygenInPoint(w); }
                catch (Exception e) { Error("oxygen (provider system)", e); }
                MyDefinitionId id;
                MyCubeBlockDefinition cbd;
                if (MyDefinitionId.TryParse(AtmoThrusterId, out id) && MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out cbd) && cbd is MyThrustDefinition)
                {
                    var td = (MyThrustDefinition)cbd;
                    f.AtmoThrust = G2E3.ThrustMultiplier(air, planet.HasAtmosphere, td.NeedsAtmosphereForInfluence, td.MinPlanetaryInfluence, td.MaxPlanetaryInfluence,
                                                         td.InvDiffMinMaxPlanetaryInfluence, td.EffectivenessAtMinInfluence, td.EffectivenessAtMaxInfluence);
                    f.AtmoThrustDetail = id.SubtypeName + " force multiplier at the water: influence = air density " + G2E3.G(air) +
                                         ", planetary influence " + G2E3.G(td.MinPlanetaryInfluence) + ".." + G2E3.G(td.MaxPlanetaryInfluence) +
                                         " -> effectiveness " + G2E3.G(td.EffectivenessAtMinInfluence) + ".." + G2E3.G(td.EffectivenessAtMaxInfluence) +
                                         " (MyThrusterBlockThrustComponent.CalculateForceMultiplier)";
                }
                else f.AtmoThrustDetail = "thruster definition " + AtmoThrusterId + " not found";
            }
            catch (Exception e) { Error("atmosphere", e); }

            // the floor at three places: the anchor, the tile corner SN (2048, -500, 2048), and the +X face of the planet
            Vec3[] dirs = new Vec3[3];
            dirs[0] = u0;
            try
            {
                SiteMap map = SiteMap.ForSiteV1(c, rs);
                dirs[1] = (map.SnToSe(new Vec3(2048.0, -500.0, 2048.0)) - c).Normalized();
            }
            catch (Exception e) { Error("tile corner direction", e); dirs[1] = u0; }
            dirs[2] = Vec3.UnitX;
            Vec3 floorAnchor = Vec3.Zero;
            for (int i = 0; i < dirs.Length; i++)
            {
                try
                {
                    Vector3D p = ToD(c + dirs[i] * rs);
                    Vec3 s = ToVec(planet.GetClosestSurfacePointGlobal(ref p));
                    double r = (s - c).Length;
                    f.Floor[i] = r - rs;
                    if (i == 0) floorAnchor = s;
                }
                catch (Exception e) { Error("floor " + f.FloorLabels[i], e); }
            }

            if (f.Floor[0].HasValue)
            {
                try
                {
                    double rFloor = (floorAnchor - c).Length;
                    foreach (double below in MaterialProbeDepthsM)
                    {
                        Vector3D q = ToD(c + u0 * (rFloor - below));
                        MyVoxelMaterialDefinition mat = planet.GetMaterialAt(ref q);
                        if (mat == null) continue;
                        f.FloorMaterial = mat.Id.SubtypeName;
                        f.FloorMaterialDetail = "MyVoxelBase.GetMaterialAt " + LabSpawn.Num(below) + " m below the floor at the anchor: index " + mat.Index +
                                                " " + mat.Id.SubtypeName + " (Sand_02 from the _mat maps; Stone means the maps did not load)";
                        break;
                    }
                    if (f.FloorMaterial == null) f.FloorMaterialDetail = "empty voxel (material 255) at every probe depth below the anchor floor";
                }
                catch (Exception e) { Error("floor material", e); f.FloorMaterialDetail = e.GetType().Name + ": " + e.Message; }
            }
        }

        private static double? GravityAt(Vec3 p)
        {
            try { return MyGravityProviderSystem.CalculateNaturalGravityInPoint(ToD(p)).Length(); }
            catch (Exception e) { Error("gravity", e); return null; }
        }

        // ================================================================================================= helpers

        private static bool InWorld(out string why)
        {
            why = null;
            Plugin p = Plugin.Instance;
            if (p == null || !p.S1Active) { why = "the plugin is not active (inert, measure-only or Init failed)"; return false; }
            if (MySession.Static == null || p.CurrentPhase != SePhase.InWorld)
            {
                why = "not in a world (sePhase " + SePhase.Name(p.CurrentPhase) + ")";
                return false;
            }
            return true;
        }

        private static void Error(string what, Exception e)
        {
            DeLog.Info("DE.Error de.g2e3 " + what + " " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
        }

        private static Vec3 ToVec(Vector3D v) { return new Vec3(v.X, v.Y, v.Z); }

        private static Vector3D ToD(Vec3 v) { return new Vector3D(v.X, v.Y, v.Z); }

        private static object[] Arr(Vec3 v) { return new object[] { v.X, v.Y, v.Z }; }

        private static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        private static Dictionary<string, object> Refuse(string why, string prefix = "DE.Lab spawnplanet")
        {
            return Fail(prefix + " REFUSED " + why);
        }

        private static Dictionary<string, object> Fail(string line)
        {
            DeLog.Info(line);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", line }, { "line", line } };
        }

        private static string FirstFrame(Exception e)
        {
            string st = e.StackTrace;
            if (string.IsNullOrEmpty(st)) return "";
            int nl = st.IndexOf('\n');
            return (nl > 0 ? st.Substring(0, nl) : st).Trim();
        }
    }
}
