using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S6-3 ("i died of o2"): after a death in Survival SE's drop pod lands on the planet's solid surface, 1.5 km
 /// under the water sphere (crush depth: dead again in 14 s). The lifepod rule: a local character that appears after a
 /// death and comes free under the waterline inside the site is lifted to <see cref="RespawnRules.LifepodSn"/> (head
 /// out, velocity 0), once. SE's medical rooms and survival kits are untouched: their characters appear out of the
 /// water, or inside a pressurized room (environment oxygen), and the watch ends without a lift. The saved character of
 /// a world load is never touched (no death seen before it). Runs per physics step from <see cref="SwimPatches"/>.
 /// </summary>
    internal static class RespawnWatch
    {
        private static long _charId;
        private static bool _sawDeath, _pending, _failedOnce;
        private static int _pendingSteps;
        public static long Lifts, Watches;
        public static string Last = "none";

        public static void Reset()
        {
            _charId = 0;
            _sawDeath = _pending = false;
            _pendingSteps = 0;
        }

        public static void Tick()
        {
            try { TickCore(); }
            catch (Exception e)
            {
                if (!_failedOnce) { _failedOnce = true; DeLog.Info("DE.Respawn rule failed (off until the next world): " + e.GetType().Name + ": " + e.Message); }
                _pending = false;
            }
        }

        private static long _creativeId;
        private static int _creativeSteps;
        public static long CreativeSpawns;

        /// <summary>A Creative world's new suit, farther than NearM from 4546B's surface, goes to the lifepod point (at the sea); once per character.</summary>
        private static void CreativeSpawn(MyCharacter c, MySession s)
        {
            if (++_creativeSteps > RespawnRules.GiveUpSteps) { _creativeId = 0; return; }
            if (c.IsDead || c.IsSitting || c.Physics == null) return;
            SiteMap site;
            string why;
            if (!RespawnSite.Ready(out site, out why)) return;
            MyPlanet planet = SiteBinding.Planet;
            if (planet == null) return;
            Vector3D p = c.PositionComp.GetPosition();
            double fromSurface = Vector3D.Distance(p, planet.PositionComp.GetPosition()) - planet.AverageRadius;
            _creativeId = 0;
            if (fromSurface <= LifepodPod.NearM) { Last = "creative spawn already at 4546B"; return; }
            string err;
            string line = S2bCommands.TeleportLocalCharacter(site, RespawnRules.LifepodSn, 0.0, "creative spawn", true, out err);
            if (line != null) CreativeSpawns++;
            Last = line ?? ("creative spawn failed: " + err);
            DeLog.Info("DE.Respawn CREATIVE: new suit " + c.EntityId + " " + SwimText.N(fromSurface / 1000.0) + " km from 4546B (SE's Creative spawn: no respawn ship) -> " +
                       (line != null ? "moved to the lifepod point " + SiteBinding.V(RespawnRules.LifepodSn) : "teleport failed: " + err));
            if (line != null)
            {
                try
                {
                    VRage.Game.ModAPI.IMyUtilities u = Sandbox.ModAPI.MyAPIGateway.Utilities;
                    if (u != null) u.ShowNotification("DeepEngineers: spawned at 4546B", 4000, "White");
                }
                catch (Exception) { }
            }
        }

        private static void TickCore()
        {
            MySession s = MySession.Static;
            LifepodPod.Snapshot();               //: once per world, the grids that exist when the site binds (a respawn pod is a later one)
            LifepodPod.Guide();                  //: the placed pod falls on the radial (no lateral drift from SE's initial velocity)
            MyCharacter c = s != null ? s.LocalCharacter : null;
            if (c == null || c.Closed) return;   // between the death and the respawn the local character may be gone; _sawDeath holds
            LifepodPod.Place(c);                 //: the 4546B lifepod respawn ship lands at the Safe Shallows, not at SE's random spot
            if (c.EntityId != _charId)
            {
                // a new local character after a death is a respawn (drop pod, medical room, survival kit); at the world load it
 // is the saved one (an underwater base is where it belongs)
                _pending = _sawDeath && _charId != 0;
                _pendingSteps = 0;
                _charId = c.EntityId;
                _sawDeath = false;

                // spawn"): SE offers no respawn ship in Creative mode, only the suit at the scenario's default spot; a new suit far from
 // 4546B is moved to the lifepod point once this world's site is bound
                _creativeId = (s != null && s.CreativeMode) ? c.EntityId : 0;
                _creativeSteps = 0;
                if (_pending)
                {
                    Watches++;
                    DeLog.Info("DE.Respawn new character " + c.EntityId + " after a death: the lifepod rule watches it (free under the waterline inside the site -> lifted to " + SiteBinding.V(RespawnRules.LifepodSn) + ")");
                }
            }
            if (_creativeId != 0 && c.EntityId == _creativeId) CreativeSpawn(c, s);
            if (c.IsDead) { _sawDeath = true; return; }
            if (!_pending) return;
            if (++_pendingSteps > RespawnRules.GiveUpSteps)
            {
                _pending = false;
                Last = "gave up (10 min)";
                DeLog.Info("DE.Respawn lifepod rule: gave up after 10 min (the character never came free under water)");
                return;
            }
            SiteMap site;
            string why;
            if (!RespawnSite.Ready(out site, out why)) return;
            Vector3D p = c.PositionComp.GetPosition();
            Vec3 sn = site.SeToSn(new Vec3(p.X, p.Y, p.Z));
            bool onSite = Math.Max(Math.Abs(sn.X), Math.Abs(sn.Z)) <= ViewThresholds.UnderwaterMaxRhoM;
            bool seated = c.IsSitting, hasPhysics = c.Physics != null;
            float room = 0f;
            try { MyCharacterOxygenComponent oc = c.OxygenComponent; if (oc != null) room = oc.EnvironmentOxygenLevel; } catch (Exception) { room = 0f; }
            if (RespawnRules.Settled(seated, c.IsDead, hasPhysics, sn.Y) || (hasPhysics && !seated && room >= RespawnRules.RoomOxygen))
            {
                _pending = false;
                Last = "respawned out of the water or in a pressurized room (no lift)";
                DeLog.Info("DE.Respawn new character is free " + (room >= RespawnRules.RoomOxygen ? "in a pressurized room" : "out of the water") +
                           " (y=" + SwimText.N(sn.Y) + " m, room oxygen " + SwimText.N(room) + "): no lift");
                return;
            }
            if (!RespawnRules.Fire(true, seated, c.IsDead, hasPhysics, onSite, room, sn.Y)) return;
            string err;
            string line = S2bCommands.TeleportLocalCharacter(site, RespawnRules.LifepodSn, 0.0, "lifepod rule", true, out err);
            _pending = false;
            if (line != null) Lifts++;
            Last = line ?? ("failed: " + err);
            DeLog.Info("DE.Respawn LIFEPOD: the character came free " + SwimText.N(-sn.Y) + " m under the waterline (SE's drop pod lands on the planet's solid surface) -> " +
                       (line != null ? "lifted to the lifepod point " + SiteBinding.V(RespawnRules.LifepodSn) : "teleport failed: " + err));
        }
    }
}

namespace DeepEngineers.SE
{
    /// <summary>
 /// 5 km from the Aurora; the night log named the liar): every capsule rule maps
 /// the lifepod point through the site map of chart 0 (<see cref="SiteBinding.SiteMapV1"/>), never through <see cref="SiteBinding.Map"/>,
 /// which the S8a treadmill replaces while the camera is far out. A respawn from orbit arrived on chart 2 ("outer-x"): its (0, 0)
 /// lay 2.8 km from the real lifepod point, the capsule was put there, the chart returned to 0 right after the move (the camera
 /// back near the site) and Subnautica drew the capsule over the void at SN (-2700, -817). The guide's radial was that point's.
 /// </summary>
    internal static class RespawnSite
    {
        public static bool Ready(out SiteMap site, out string why)
        {
            site = SiteBinding.SiteMapV1;
            why = null;
            if (MySession.Static == null) { why = "no world loaded"; return false; }
            if (site == null) { why = "the site is not bound (" + SiteDecision.StatusName(SiteBinding.Status) + " " + (SiteBinding.Last.Reason ?? "") + ")"; return false; }
            if (MySector.MainCamera == null) { why = "no camera"; return false; }
            return true;
        }
    }
}

namespace DeepEngineers.SE
{
    /// <summary>

 /// <c>DE_Lifepod</c> (Data/RespawnShips_DE.sbc, prefab Data/Prefabs/DE_Lifepod.sbc, grid DisplayName "DE Lifepod") is offered
 /// on DE_4546B only, but SE drops a planet pod at a random place above the planet's solid surface, which under our sea is
 /// the seabed. The moment the local character sits in a grid of that name, once per grid, the grid is teleported to the
 /// lifepod point above the Safe Shallows (<see cref="RespawnRules.LifepodSn"/>, 2 m over the water, upright on the planet's
 /// radial) with its velocity cleared; the Water Mod's buoyancy keeps it afloat. Nothing else moves grids.
 /// </summary>
    internal static class LifepodPod
    {
        public const string GridName = "DE Lifepod";
        private static readonly HashSet<long> _placed = new HashSet<long>();
        public static long Placements;
        public static string Last = "none";

        public static void Reset() { _placed.Clear(); _loadGrids.Clear(); _snapshotWorld = null; _guideGrid = 0; }

        // RespawnPlanetPod), whose grid carries SE's name, so a pod is recognised as a grid that did not exist when the world
 // came up (the snapshot below), with the local character seated in it, within NearM of 4546B's surface. A saved ship
 // the player sits in at the world load is in the snapshot and never moves.
        public const double NearM = 20000.0;
        /// <summary>
 /// The height over the sea the pod is put at.
 /// respawning"): Subnautica's camera already waits at the spawn with the area built, and a 1,000 m drop threw that area
 /// away (laptop log: an 89 s "loading the area" curtain, the near cells rebuilt on the way down). Now the pod is put just
 /// over the water and floats. DEEPENGINEERS_CAPSULE_DROP_M=1000 brings the fall back.
 /// </summary>
        public static readonly double DropAltitudeM = DropFromEnv();

        private static double DropFromEnv()
        {
            double v;
            string s = Environment.GetEnvironmentVariable("DEEPENGINEERS_CAPSULE_DROP_M");
            if (!string.IsNullOrEmpty(s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 5000) return v;
            return 3.0;
        }
        /// <summary>A capsule within this of the lifepod point laterally and this far from sea level is already at the spawn (not moved).</summary>
        public const double SpawnNearM = 800.0, SpawnLowM = 300.0;
        private static readonly HashSet<long> _loadGrids = new HashSet<long>();
        private static string _snapshotWorld;
        // it always needs to be really near
 // Subnautica's original spawn"): SE applies the prefab's InitialLinearVelocity (-95 m/s along the pod's down at ITS random
 // spot) when the physics come up, which can be after the move above; 110 km away on the sphere that direction is mostly
 // sideways, and the drop pod has no thrusters to damp it (the rover had, hence its clean landing). So the fall is guided:
 // every tick until the pod reaches the water, its lateral velocity is replaced by a slow pull back onto the radial line
 // through the lifepod point (at most GuidePullMps), its vertical velocity is kept (parachute and gravity do their work),
 // and its spin is zeroed (it lands upright).
        public const double GuidePullPerS = 0.2, GuidePullMps = 15.0, GuideDoneAltM = 2.0;
        public const int GuideMaxTicks = 60 * 600;

        // the water the guide does not let go; it holds the grid's centre FloatAltM over the sea point (vertical velocity only, the
 // Water Mod and the player keep the rest) while the capsule stays within FloatKeepM of the point; a saved capsule at a world
 // load is held the same way. FloatMaxTicks caps it (an hour), then the capsule is on its own.

 // sits low in the pod, so the body hung clear of the sea; now it is the body's bounds centre (WorldAABB) that is held at
 // FloatAltM over the sea point (0: the centre at the water, the pod half in), and the spin is bled off each tick (FloatSpinKeep).
        public const double FloatAltM = 0.0, FloatKeepM = 500.0, FloatGainPerS = 1.0, FloatMaxMps = 3.0;
        public const float FloatSpinKeep = 0.95f;
        /// <summary>
 /// "a respawned
 /// capsule drifts away"): while afloat the grid's Up (the radial it was placed on) is turned back to the local vertical at
 /// FloatRightGainPerS per radian of tilt (at most FloatRightMaxRadPerS), the spin about the vertical is damped, and a
 /// sideways drift slower than FloatDriftMps loses FloatDriftKeep of its speed each tick (waves); faster, the player is
 /// driving it and it is left alone.
 /// </summary>
        public const double FloatRightGainPerS = 2.0, FloatRightMaxRadPerS = 1.5, FloatDriftMps = 2.0;
        public const double FloatDriftKeep = 0.97;
        public const int FloatMaxTicks = 60 * 3600;
        private static bool _floating;
        public static long FloatTicks;
        private static long _guideGrid;
        private static Vector3D _guideTarget, _guideCentre;
        private static int _guideTicks;
        public static long GuidedTicks;
        public static string GuideLast = "none";
        public static int SnapshotGrids;

        /// <summary>Once per world, as soon as the site is bound: the grids that exist now (the saved ones).</summary>
        public static void Snapshot()
        {
            try
            {
                MySession s = MySession.Static;
                if (s == null) return;
                string world = s.Name + "|" + s.GetHashCode().ToString(CultureInfo.InvariantCulture);   // a new session object: a new world
                if (_snapshotWorld == world) return;
                SiteMap site;
                string why;
                if (!RespawnSite.Ready(out site, out why)) return;
                _loadGrids.Clear();
                _placed.Clear();
                foreach (var e in MyEntities.GetEntities())
                    if (e is MyCubeGrid) _loadGrids.Add(e.EntityId);
                SnapshotGrids = _loadGrids.Count;
                _snapshotWorld = world;
                DeLog.Info("DE.Respawn LIFEPOD POD: " + SnapshotGrids + " grid(s) present at the world's binding; a pod is a later grid the character sits in within " + NearM + " m of 4546B's surface");
            }
            catch (Exception e) { DeLog.Info("DE.Respawn LIFEPOD POD snapshot failed: " + e.GetType().Name + ": " + e.Message); }
        }

        public static void Place(MyCharacter c)
        {
            try
            {
                var cockpit = c.Parent as MyCockpit;
                MyCubeGrid g = cockpit != null ? cockpit.CubeGrid : null;
                if (g == null || g.Closed || g.MarkedForClose) return;
                if (_placed.Contains(g.EntityId)) return;
                SiteMap site;
                string why;
                if (!RespawnSite.Ready(out site, out why)) return;          // the site binds within seconds of the world load; try again next step
                MyPlanet planet = SiteBinding.Planet;
                if (planet == null) return;

                // pod was already in the snapshot. SE's own pod grid is named "Respawn Pod" (the prefab's DisplayName): a grid of that
 // name, or ours, with the character seated and within NearM of 4546B's surface is the lifepod whatever the timing;
 // the snapshot stays for a pod of another name.
                string name = g.DisplayName ?? "";
                bool ours = string.Equals(name, GridName, StringComparison.Ordinal) || name.IndexOf("Respawn", StringComparison.OrdinalIgnoreCase) >= 0;
                double fromCentre = Vector3D.Distance(g.PositionComp.GetPosition(), planet.PositionComp.GetPosition());
                if (fromCentre > planet.AverageRadius + NearM) return;                       // not a 4546B spawn

 // 26 m from the spawn was taken for a saved capsule and held afloat. A capsule is a grid SE spawned as a respawn
 // ship (MyCubeGrid.IsRespawnGrid, set by MySpaceRespawnComponent and saved with the grid) or one of the pod's name
                if (!ours && !g.IsRespawnGrid) return;
                Vec3 q = RespawnRules.LifepodSn;

                // parachute, and I died"): at a world load the saved capsule sits at the lifepod point and the name rule above moved it
 // 1 km up again (log: "moved 1.016 km"). A grid already at the spawn (within SpawnNearM laterally and SpawnLowM above
 // the sea point) is left where it is; only SE's random drop, tens of km away, is moved.
                {
                    Vec3 seaV = site.SnToSe(q);
                    var seaP = new Vector3D(seaV.X, seaV.Y, seaV.Z);
                    Vector3D radialUp = Vector3D.Normalize(seaP - planet.PositionComp.GetPosition());
                    Vector3D off = g.PositionComp.GetPosition() - seaP;
                    double upOff = Vector3D.Dot(off, radialUp);
                    double lateral = (off - radialUp * upOff).Length();
                    if (lateral < SpawnNearM && upOff < SpawnLowM && upOff > -SpawnLowM)
                    {
                        _placed.Add(g.EntityId);
                        _guideGrid = g.EntityId; _guideTarget = seaP; _guideCentre = planet.PositionComp.GetPosition(); _guideTicks = 0; _floating = true;   // held at the surface
                        Last = "already at the spawn (" + SwimText.N(lateral) + " m off, " + SwimText.N(upOff) + " m up): not moved, held afloat";
                        DeLog.Info("DE.Respawn LIFEPOD POD: grid " + g.EntityId + " '" + name + "' is already at the lifepod point (" + SwimText.N(lateral) + " m off, " + SwimText.N(upOff) + " m up): left where it is (a saved capsule at a world load)");
                        return;
                    }
                }

                // time to load"): the pod is put DropAltitudeM over the lifepod point and falls from there on its own parachutes
 // and thrusters; the view switches to Subnautica's picture at the +1,000 m band on the way down
                Vec3 se = site.SnToSe(new Vec3(q.X, q.Y + DropAltitudeM, q.Z));
                var target = new Vector3D(se.X, se.Y, se.Z);
                _placed.Add(g.EntityId);
                Vec3 sea = site.SnToSe(q);
                _guideGrid = g.EntityId; _guideTarget = new Vector3D(sea.X, sea.Y, sea.Z); _guideCentre = planet.PositionComp.GetPosition(); _guideTicks = 0; _floating = false;
                Vector3D here = g.PositionComp.GetPosition();
                double away = Vector3D.Distance(here, target);
                if (away < 50.0) { Last = "already at the lifepod point (" + SwimText.N(away) + " m)"; return; }
                Vector3D up = Vector3D.Normalize(target - planet.PositionComp.GetPosition());
                Vector3D side = Math.Abs(up.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitZ;
                Vector3D forward = Vector3D.Normalize(Vector3D.Cross(up, side));
                MatrixD wm = MatrixD.CreateWorld(target, forward, up);
                if (g.Physics != null) { g.Physics.LinearVelocity = Vector3.Zero; g.Physics.AngularVelocity = Vector3.Zero; }
                g.Teleport(wm);
                Placements++;
                // 47 km from the Aurora; the frame rows put the camera at SN (-2705, 999, -477)
 // while this rule saw the capsule on the radial over the point): every position of the move, each in SN terms by
 // the same site map, so the next respawn says which one lies
                try
                {
                    Vector3D gp = g.PositionComp.GetPosition(), cp = c.PositionComp.GetPosition();
                    Vector3D camP = MySector.MainCamera != null ? MySector.MainCamera.Position : Vector3D.Zero;
                    DeLog.Info("DE.Respawn CAPSULE POSITIONS after the move: target " + SiteBinding.V(site.SeToSn(new Vec3(target.X, target.Y, target.Z))) +
                               " grid " + SiteBinding.V(site.SeToSn(new Vec3(gp.X, gp.Y, gp.Z))) + " character " + SiteBinding.V(site.SeToSn(new Vec3(cp.X, cp.Y, cp.Z))) +
                               " camera " + SiteBinding.V(site.SeToSn(new Vec3(camP.X, camP.Y, camP.Z))) + " (SN terms; SE target " + SiteBinding.V(new Vec3(target.X, target.Y, target.Z)) +
                               ", planet entity " + SiteBinding.V(new Vec3(planet.PositionComp.GetPosition().X, planet.PositionComp.GetPosition().Y, planet.PositionComp.GetPosition().Z)) +
                               ", site C " + SiteBinding.V(site.C) + ", seat " + (c.Parent != null ? c.Parent.GetType().Name + " on grid " + (cockpit != null ? cockpit.CubeGrid.EntityId.ToString() : "?") : "none") + ")");
                }
                catch (Exception e) { DeLog.Info("DE.Respawn CAPSULE POSITIONS failed: " + e.GetType().Name + ": " + e.Message); }
                Last = "placed grid " + g.EntityId + " from " + SwimText.N(away) + " m away";
                DeLog.Info("DE.Respawn LIFEPOD POD: grid " + g.EntityId + " '" + name + "' (" + g.BlocksCount + " blocks) moved " + SwimText.N(away / 1000.0) + " km to " + DropAltitudeM + " m over the lifepod point " +
                           SiteBinding.V(q) + " (upright on the radial, falling from there); SE had dropped it at a random place on the planet's solid surface" +
                           (string.Equals(name, GridName, StringComparison.Ordinal) ? "" : " (not the 4546B lifepod: the world's copy of the mod lacks RespawnShips_DE.sbc, or another ship was chosen)"));
            }
            catch (Exception e) { Last = "failed: " + e.GetType().Name; DeLog.Info("DE.Respawn LIFEPOD POD failed: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>Every tick: the guided fall of the placed pod (see the fields above); nothing once it is in the water.</summary>
        public static void Guide()
        {
            if (_guideGrid == 0) return;
            try
            {
                MyEntity e;
                if (!MyEntities.TryGetEntityById(_guideGrid, out e)) { StopGuide("the pod is gone"); return; }
                var g = e as MyCubeGrid;
                if (g == null || g.Closed || g.MarkedForClose || g.Physics == null) { StopGuide("the pod is closed or has no physics"); return; }
                if (!_floating && ++_guideTicks > GuideMaxTicks) { StopGuide("10 minutes"); return; }
                Vector3D p = g.PositionComp.GetPosition();
                Vector3D radial = p - _guideCentre;
                double r = radial.Length();
                if (r < 1.0) { StopGuide("at the planet's centre?"); return; }
                Vector3D up = radial / r;
                double alt = r - (_guideTarget - _guideCentre).Length();
                if (_floating)
                {
                    // the float phase: the centre held FloatAltM over the sea point; lateral motion and spin are left to the water and the player
                    if (++_guideTicks > FloatMaxTicks) { StopGuide("afloat for an hour: on its own now"); return; }
                    if (PilotMoving(g)) { StopGuide("the pilot moves it (movement keys): on its own now"); return; }
                    double lateralM = Lateral(p, up).Length();
                    if (lateralM > FloatKeepM) { StopGuide("moved " + SwimText.N(lateralM) + " m from the lifepod point: on its own now"); return; }
                    Vector3D bodyC = g.PositionComp.WorldAABB.Center;
                    double altBody = (bodyC - _guideCentre).Length() - (_guideTarget - _guideCentre).Length();
                    Vector3 vf = g.Physics.LinearVelocity;
                    double vUpNow = Vector3D.Dot(vf, up);
                    double want = Math.Max(-FloatMaxMps, Math.Min(FloatMaxMps, (FloatAltM - altBody) * FloatGainPerS));
                    Vector3D lateralV = (Vector3D)vf - up * vUpNow;
                    if (lateralV.Length() < FloatDriftMps) lateralV *= FloatDriftKeep;   // the waves' drift, not the player's thrust
                    g.Physics.LinearVelocity = (Vector3)(lateralV + up * want);
                    // upright: the tilt turned back at a rate proportional to its angle, the spin about the vertical damped
                    Vector3D gridUp = g.WorldMatrix.Up;
                    Vector3D axis = Vector3D.Cross(gridUp, up);
                    double sinA = axis.Length();
                    double cosA = Vector3D.Dot(gridUp, up);
                    double tilt = Math.Atan2(sinA, cosA);
                    Vector3D w = g.Physics.AngularVelocity;
                    Vector3D wYaw = up * Vector3D.Dot(w, up) * FloatSpinKeep;
                    Vector3D wRight = Vector3D.Zero;
                    if (sinA > 1e-6) wRight = axis / sinA * Math.Min(FloatRightMaxRadPerS, tilt * FloatRightGainPerS);
                    else if (cosA < 0) wRight = Vector3D.CalculatePerpendicularVector(up) * FloatRightMaxRadPerS;   // upside down
                    g.Physics.AngularVelocity = (Vector3)(wYaw + wRight);
                    FloatTicks++;
                    if (_guideTicks % 3600 == 1) DeLog.Info("DE.Respawn LIFEPOD POD afloat: body centre " + SwimText.N(altBody) + " m over the sea point (origin " + SwimText.N(alt) + " m), " + SwimText.N(lateralM) + " m from it (held at " + FloatAltM + " m), tilt " + SwimText.N(tilt * 180.0 / Math.PI) + " deg");
                    return;
                }
                if (alt < GuideDoneAltM)
                {
                    _floating = true;
                    _guideTicks = 0;
                    DeLog.Info("DE.Respawn LIFEPOD POD guided fall ends: in the water, " + SwimText.N(Lateral(p, up).Length()) + " m from the lifepod point; held afloat from here (centre " + FloatAltM + " m over the sea)");
                    return;
                }
                Vector3D v = g.Physics.LinearVelocity;
                double vUp = Vector3D.Dot(v, up);
                Vector3D pull = -Lateral(p, up) * GuidePullPerS;
                double pl = pull.Length();
                if (pl > GuidePullMps) pull *= GuidePullMps / pl;
                g.Physics.LinearVelocity = (Vector3)(up * vUp + pull);
                g.Physics.AngularVelocity = Vector3.Zero;
                GuidedTicks++;
                if (_guideTicks % 600 == 1)
                {
                    SiteMap site; string why;
                    string snNote = RespawnSite.Ready(out site, out why) ? " grid in SN terms " + SiteBinding.V(site.SeToSn(new Vec3(p.X, p.Y, p.Z))) : "";
                    DeLog.Info("DE.Respawn LIFEPOD POD guided fall: altitude " + SwimText.N(alt) + " m over the lifepod point, " + SwimText.N(Lateral(p, up).Length()) + " m off the radial, " +
                               SwimText.N(vUp) + " m/s vertical (the lateral velocity is replaced by the pull back, the spin zeroed)" + snNote);
                }
            }
            catch (Exception ex) { StopGuide("failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>The pod's offset from the radial line through the lifepod point, in the plane of <paramref name="up"/>.</summary>
 /// <summary>The local player controls a seat of this grid and presses a movement key (W/A/S/D, Space, C): the thrusters are
 /// the player's, the hold lets go. The dampeners alone (which fire while the capsule bobs) never release it.</summary>
        private static bool PilotMoving(MyCubeGrid g)
        {
            try
            {
                var sc = MySession.Static != null ? MySession.Static.ControlledEntity as Sandbox.ModAPI.Ingame.IMyShipController : null;
                var blk = sc as Sandbox.Game.Entities.MyCubeBlock;
                if (sc == null || blk == null || blk.CubeGrid == null || blk.CubeGrid.EntityId != g.EntityId) return false;
                return sc.MoveIndicator.LengthSquared() > 0.01f;
            }
            catch (Exception) { return false; }
        }

        // default spawn by Earth to 4546B, both areas loaded): in a Creative world the suit's spawn position is set to the lifepod
 // point where SE computes it (MySpaceRespawnComponent.GetSpawnPositionInSpace, called by SpawnInSuit with a 10 m
 // collision radius, not near players), so SE loads 4546B's spawn only; the CreativeSpawn rule then finds it there.
        public static bool CreativeAtSourceOn = !string.Equals(Environment.GetEnvironmentVariable("DEEPENGINEERS_CREATIVE_SPAWN"), "off", StringComparison.OrdinalIgnoreCase);
        public static long CreativeAtSource;

        public static void PatchCreativeSpawn(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type t = typeof(SpaceEngineers.Game.World.MySpaceRespawnComponent);
                var m = t.GetMethod("GetSpawnPositionInSpace", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (harmony == null || m == null || m.GetParameters().Length != 4)
                {
                    DeLog.Info("DE.Harmony MISSING Respawn MySpaceRespawnComponent.GetSpawnPositionInSpace: the Creative suit is moved after it spawns");
                    return;
                }
                harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(LifepodPod).GetMethod(nameof(SuitSpawnPostfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)));
                DeLog.Info("DE.Harmony Respawn OK MySpaceRespawnComponent.GetSpawnPositionInSpace postfix (a Creative suit spawns at the lifepod point)");
            }
            catch (Exception e) { DeLog.Info("DE.Harmony Respawn Creative spawn patch failed: " + e.GetType().Name + ": " + e.Message); }
        }

        public static void SuitSpawnPostfix(object[] __args, ref Vector3D position, ref Vector3 forward, ref Vector3 up)
        {
            try
            {
                if (!CreativeAtSourceOn) return;
                MySession s = MySession.Static;
                if (s == null || !s.CreativeMode || __args == null || __args.Length == 0 || __args[0] == null) return;
                object info = __args[0];
                var fr = info.GetType().GetField("CollisionRadius");
                var fn = info.GetType().GetField("SpawnNearPlayers");
                if (fr == null || fn == null || (float)fr.GetValue(info) != 10f || (bool)fn.GetValue(info)) return;   // SpawnInSuit's call only
                SiteMap site;
                string why;
                if (!RespawnSite.Ready(out site, out why)) return;
                MyPlanet planet = SiteBinding.Planet;
                if (planet == null) return;
                Vec3 q = RespawnRules.LifepodSn;
                Vec3 se = site.SnToSe(new Vec3(q.X, q.Y + 1.0, q.Z));
                var p = new Vector3D(se.X, se.Y, se.Z);
                Vector3D u = Vector3D.Normalize(p - planet.PositionComp.GetPosition());
                double moved = Vector3D.Distance(position, p);
                position = p;
                up = (Vector3)u;
                forward = Vector3.CalculatePerpendicularVector(up);
                CreativeAtSource++;
                DeLog.Info("DE.Respawn CREATIVE at the source: the suit spawns at the lifepod point " + SiteBinding.V(q) + " instead of SE's spot " + SwimText.N(moved / 1000.0) + " km away (one area to load)");
            }
            catch (Exception e) { DeLog.Info("DE.Respawn CREATIVE at the source failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private static Vector3D Lateral(Vector3D p, Vector3D up)
        {
            Vector3D off = p - _guideTarget;
            return off - Vector3D.Dot(off, up) * up;
        }

        private static void StopGuide(string why)
        {
            _guideGrid = 0;
            GuideLast = why;
            DeLog.Info("DE.Respawn LIFEPOD POD guided fall ends: " + why);
        }
    }
}
