using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.Utils;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace DeepEngineers.SE
{
    /// <summary>
 /// <c>de.s15</c> (dev builds; chat <c>/de s15 …</c>): S15's status and the scripted steps of <c>launcher.py s15</c>. Every
 /// game member VERIFIED
 /// <list type="bullet">
 /// <item><c>{status}</c> (default), <c>{sky: true}</c> the body list and the last frame.</item>
 /// <item><c>{spawn: "sub"|"ship"|"deck", x, y, z, label?}</c>: a small craft (a cockpit, a battery, an interior light, an ion
 /// thruster: powered), a large unpowered ship (a light armour floor 5 cells wide with rows enough for the world's trash
 /// BlockCountThreshold, a large cockpit seat, a large ion thruster; the cell (0, 1, 0) left free for a reactor) or an open deck with one wall (no seat, no room) through S16's test-grid path
 /// (<c>S16Host.Spawn</c>, dynamic, not saved), reply <c>grid</c> as a string; <c>{planet: def, km, size, elevDeg}</c> a
 /// test body in the site's sky.</item>
 /// <item><c>{board: grid}</c> / <c>{unboard: true}</c>: the local character into the grid's first cockpit
 /// / out of it
 /// (<c>MyCockpit.RemovePilot</c>).</item>
 /// <item><c>{reactor: grid, cell: [x, y, z], fuel: kg}</c>: a small reactor (<c>MyObjectBuilder_Reactor/LargeBlockSmallGenerator</c>,
 /// or the small grid's <c>SmallBlockSmallGenerator</c>; Content CubeBlocks_Energy.sbc) added at the cell
 /// with that much uranium when fuel &gt; 0; <c>{fuel: grid, kg}</c>: uranium ingots
 /// (<c>MyObjectBuilder_Ingot</c> "Uranium", the reactor's fuel) into the grid's first reactor
 /// </item>
 /// <item><c>{power: on|off}</c>: SE's Y key itself: the controlled ship controller's <c>SwitchReactorsLocal</c>
 /// when the grid's
 /// <c>IsPowerSwitchOn</c> differs.</item>
 /// <item><c>{thrust: grid, on}</c>: every thruster's <c>IMyThrust.ThrustOverridePercentage</c> 1 or 0 (the
 /// voice part's "never on thrust" step).</item>
 /// </item>
 /// <item><c>{voice|sky|water: on|off}</c>, <c>{water: "apply"}</c>; <c>{sonar: grid, on}</c> a functional block renamed
 /// "... Sonar" or back.</item>
 /// <item><c>{orbit: km, x?, z?}</c> (also the air rows: 30 and 5 km): the spectator <paramref name="km"/> above the site looking at the planet centre (S1's spectator
 /// path), or x, z SN metres beside it looking down (the sea-seam pair); <c>{look: [x, y, z]}</c>: the spectator where the camera is, looking along that Subnautica direction
 /// <c>{shot: path}</c>: <c>MyRenderProxy.TakeScreenshot(Vector2, string, bool, bool, bool)</c>
 /// without the HUD sprites.</item>
 /// </list>
 /// </summary>
    internal static class S15Commands
    {
        public const string Verb = "de.s15";

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Verb) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                if (!BuildFlavor.IsDev) { d["ok"] = false; d["error"] = "developer command, not in the release build"; return d; }
                args = FromArgv(args);
                object v;
                if (args.TryGetValue("spawn", out v))
                {
                    string kind = Convert.ToString(v, CultureInfo.InvariantCulture);
                    var plan = new List<S16Host.Placement>();
                    if (kind == "sub")
                    {
                        plan.Add(new S16Host.Placement("MyObjectBuilder_Cockpit/SmallBlockCockpit", 0, 0, 0));              // 3x3x3
                        plan.Add(new S16Host.Placement("MyObjectBuilder_BatteryBlock/SmallBlockSmallBatteryBlock", 3, 0, 0)); // 1x1x1
                        plan.Add(new S16Host.Placement("MyObjectBuilder_InteriorLight/SmallBlockSmallLight", 3, 1, 0));      // 1x1x1 (SmallLight is the large grid's: run 20261024-003024)
                        plan.Add(new S16Host.Placement("MyObjectBuilder_Thrust/SmallBlockSmallThrust", 3, 0, 1));            // 1x1x2
                    }
                    else if (kind == "ship")
                    {
                        // a large ship with no power source (format voice.power: the reactor placed or fuelled later brings the power):
 // a floor of light armour 5 cells wide, the large cockpit seat (1x1x1) on it, a large ion thruster (1x1x2), the
 // cell (0, 1, 0) beside the seat free for de.s15 reactor. SE's trash collector removes an unpowered,
 // uncontrolled grid of fewer than the world's BlockCountThreshold blocks away from the player
 // run 20261024-133305 lost the 8-block ship 1 s after its spawn, so the floor has rows enough for the threshold
                        int threshold = 20;
                        try { if (MySession.Static != null) threshold = Math.Max(1, MySession.Static.Settings.BlockCountThreshold); }
                        catch (Exception) { }
                        int floorRows = Math.Max(2, (threshold - 2 + 4) / 5);
                        for (int x = 0; x < 5; x++)
                            for (int z = 0; z < floorRows; z++) plan.Add(new S16Host.Placement("LargeBlockArmorBlock", x, 0, z));
                        plan.Add(new S16Host.Placement("MyObjectBuilder_Cockpit/LargeBlockCockpitSeat", 1, 1, 0));
                        plan.Add(new S16Host.Placement("MyObjectBuilder_Thrust/LargeBlockSmallThrust", 2, 1, 0));
                    }
                    else if (kind == "deck")
                    {
                        // a deck with one wall (large light armour): open to the water, no seat, no room; the player stands
 // inside its box, so only S15.M2 (any grid box) makes it "aboard"
                        for (int x = 0; x < 5; x++)
                            for (int z = 0; z < 5; z++) plan.Add(new S16Host.Placement("LargeBlockArmorBlock", x, 0, z));
                        for (int y = 1; y < 3; y++)
                            for (int z = 0; z < 5; z++) plan.Add(new S16Host.Placement("LargeBlockArmorBlock", 0, y, z));
                    }
                    else { d["ok"] = false; d["error"] = "spawn: sub|ship|deck"; return d; }
                    string why;
                    long id = S16Host.Spawn(plan, Num(args, "x", 0), Num(args, "y", -20), Num(args, "z", 0), Str(args, "label", "s15" + kind), out why);
                    d["ok"] = id != 0;
                    if (id != 0) d["grid"] = id.ToString(CultureInfo.InvariantCulture);
                    else d["error"] = why;
                    return d;
                }
                if (args.TryGetValue("planet", out v)) return Planet(d, Convert.ToString(v, CultureInfo.InvariantCulture), Num(args, "km", 400), Num(args, "size", 19000), Num(args, "elevDeg", 35), via);
                if (args.TryGetValue("board", out v)) return Board(d, GridId(v));
                if (args.ContainsKey("unboard")) return Unboard(d);
                if (args.TryGetValue("thrust", out v)) return Thrust(d, GridId(v), Bool(args, "on", true));
                if (args.TryGetValue("reactor", out v))
                {
                    object c;
                    return Reactor(d, GridId(v), args.TryGetValue("cell", out c) ? c as IList : null, Num(args, "fuel", 0), via);
                }
                if (args.TryGetValue("fuel", out v)) return Fuel(d, GridId(v), Num(args, "kg", 5), via);
                if (args.TryGetValue("power", out v)) return Power(d, On(v), via);
                if (args.TryGetValue("sonar", out v)) return SonarName(d, GridId(v), Bool(args, "on", true), via);
                if (args.TryGetValue("play", out v))
                {
                    int ev = EventOf(Convert.ToString(v, CultureInfo.InvariantCulture));
                    if (ev == 0) { d["ok"] = false; d["error"] = "play: welcome|engine|down|small"; return d; }
                    d["ok"] = true;
                    d["seq"] = (long)S15Se.Force(ev, via);
                    return d;
                }
                if (args.TryGetValue("voice", out v)) { S15Switch.VoiceOn = On(v); return Switched(d, "voice", S15Switch.VoiceOn, via); }
                if (args.TryGetValue("water", out v))
                {
                    string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                    if (s == "apply") return S15Se.ApplyWater(via);
                    S15Switch.WaterOn = On(v);
                    if (!S15Switch.WaterOn)
                    {
                        Dictionary<string, object> r = WaterLook.Command(new Dictionary<string, object>(StringComparer.Ordinal) { { "texture", S15Conventions.Water.Previous }, { "material", S15Conventions.WaterFar.PlainMaterial } }, "s15 water off");
                        d["line"] = r != null && r.ContainsKey("line") ? r["line"] : null;
                    }
                    else S15Se.ApplyWater(via);
                    return Switched(d, "water", S15Switch.WaterOn, via);
                }
                if (args.TryGetValue("sky", out v) && !(v is bool && (bool)v))
                {
                    S15Switch.SkyOn = On(v);
                    return Switched(d, "sky", S15Switch.SkyOn, via);
                }
                if (args.TryGetValue("orbit", out v)) return Orbit(d, Convert.ToDouble(v, CultureInfo.InvariantCulture), Num(args, "x", 0), Num(args, "z", 0));
                if (args.TryGetValue("look", out v)) return Look(d, v as IList);
                if (args.TryGetValue("clock", out v))
                {
                    double shift;
                    string why = S15Se.SetClock(Convert.ToDouble(v, CultureInfo.InvariantCulture), out shift);
                    d["ok"] = why == null;
                    if (why != null) d["error"] = why; else d["shiftS"] = shift;
                    return d;
                }
                if (args.TryGetValue("shot", out v))
                {
                    string path = Convert.ToString(v, CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { d["ok"] = false; d["error"] = "shot: an absolute .png path"; return d; }
                    MyRenderProxy.TakeScreenshot(Vector2.One, path, false, true, false);
                    DeLog.Info("DE.S15 screenshot requested " + path + " (" + via + ")");
                    d["ok"] = true;
                    d["path"] = path;
                    return d;
                }
                d["ok"] = true;
                Dictionary<string, object> st = S15Se.Status();
                d["status"] = st;
                d["line"] = "DE.S15 " + (S15Se.InactiveWhy == null ? "active" : "inactive: " + S15Se.InactiveWhy) + ", aboard " + st["aboard"] + " (" + st["how"] + "), voice " +
                            S15Se.Played + " played / " + S15Se.Events + " events, power " + st["powered"] + " switch " + st["switchOn"] + ", sky " + st["bodies"] + " bodies (list " +
                            st["listSeq"] + "), water " + S15Se.WaterState + "; " + S15Se.PicksStatus();
                return d;
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.s15 " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                d["ok"] = false;
                d["error"] = "de.s15 failed: " + e.GetType().Name + ": " + e.Message;
                return d;
            }
        }

        private static Dictionary<string, object> Switched(Dictionary<string, object> d, string what, bool on, string via)
        {
            DeLog.Info("DE.S15 " + what + " " + (on ? "on" : "off") + " (" + via + ")");
            d["ok"] = true;
            d[what] = on ? "on" : "off";
            return d;
        }

        /// <summary>de.s15 {sonar: grid, on}: the first functional block that is no light or thruster renamed "... Sonar" (on) or back (dev).</summary>
        private static readonly Dictionary<long, string> SonarRenamed = new Dictionary<long, string>();

        private static Dictionary<string, object> SonarName(Dictionary<string, object> d, long id, bool on, string via)
        {
            MyCubeGrid g = Grid(id);
            if (g == null) { d["ok"] = false; d["error"] = "no grid " + id; return d; }
            foreach (MyFunctionalBlock b in g.GetFatBlocks<MyFunctionalBlock>())
            {
                if (b == null || b is MyLightingBlock || b is MyThrust) continue;   // a cockpit is no MyFunctionalBlock
                string name = b.CustomName != null ? b.CustomName.ToString() : "";
                string original;
                if (on)
                {
                    if (!SonarRenamed.ContainsKey(b.EntityId)) SonarRenamed[b.EntityId] = name;
                    b.SetCustomName(name.Trim() + " Sonar");   // MyTerminalBlock.SetCustomName
                }
                else if (SonarRenamed.TryGetValue(b.EntityId, out original)) { b.SetCustomName(original); SonarRenamed.Remove(b.EntityId); }
                else continue;
                DeLog.Info("DE.S15 sonar switch " + (on ? "on" : "off") + ": " + b.CustomName + " of grid " + id + " (" + via + ")");
                d["ok"] = true;
                d["block"] = b.CustomName.ToString();
                return d;
            }
            d["ok"] = false;
            d["error"] = on ? "no functional block other than a light or a thruster" : "nothing renamed on grid " + id;
            return d;
        }

        private static MyCubeGrid Grid(long id)
        {
            MyEntity e;
            return id != 0 && MyEntities.TryGetEntityById(id, out e) ? e as MyCubeGrid : null;
        }

        private static Dictionary<string, object> Board(Dictionary<string, object> d, long id)
        {
            MyCubeGrid g = Grid(id);
            MySession s = MySession.Static;
            var ch = s != null ? s.LocalCharacter : null;
            if (g == null || ch == null) { d["ok"] = false; d["error"] = g == null ? "no grid " + id : "no local character"; return d; }
            foreach (MyCockpit c in g.GetFatBlocks<MyCockpit>())
            {
                if (c.Pilot != null) continue;
                ((Sandbox.ModAPI.IMyCockpit)c).AttachPilot(ch);
                DeLog.Info("DE.S15 board: the character into cockpit " + c.EntityId + " of grid " + id);
                d["ok"] = true;
                d["cockpit"] = c.EntityId.ToString(CultureInfo.InvariantCulture);
                return d;
            }
            d["ok"] = false;
            d["error"] = "grid " + id + " has no free cockpit";
            return d;
        }

        private static Dictionary<string, object> Unboard(Dictionary<string, object> d)
        {
            MySession s = MySession.Static;
            var ch = s != null ? s.LocalCharacter : null;
            var c = ch != null ? ch.UsingEntity as MyCockpit : null;
            if (c == null && s != null) c = s.ControlledEntity as MyCockpit;
            if (c == null) { d["ok"] = false; d["error"] = "not seated"; return d; }
            d["ok"] = c.RemovePilot();
            DeLog.Info("DE.S15 unboard: cockpit " + c.EntityId + " " + d["ok"]);
            return d;
        }

        private static Dictionary<string, object> Thrust(Dictionary<string, object> d, long id, bool on)
        {
            MyCubeGrid g = Grid(id);
            if (g == null) { d["ok"] = false; d["error"] = "no grid " + id; return d; }
            int n = 0;
            foreach (MyThrust t in g.GetFatBlocks<MyThrust>()) { ((Sandbox.ModAPI.Ingame.IMyThrust)t).ThrustOverridePercentage = on ? 1f : 0f; n++; }
            d["ok"] = n > 0;
            d["thrusters"] = n;
            if (n == 0) d["error"] = "grid " + id + " has no thruster";
            return d;
        }

        /// <summary>
 /// de.s15 {reactor: grid, cell, fuel}: a small reactor added at the cell (default (0, 1, 0), the free cell of the "ship"),
 /// with <paramref name="kg"/> of uranium when positive (format voice.power: a reactor placed with fuel brings the power).
 /// </summary>
        private static Dictionary<string, object> Reactor(Dictionary<string, object> d, long id, IList cell, double kg, string via)
        {
            MyCubeGrid g = Grid(id);
            if (g == null) { d["ok"] = false; d["error"] = "no grid " + id; return d; }
            string subtype = g.GridSizeEnum == MyCubeSize.Large ? "MyObjectBuilder_Reactor/LargeBlockSmallGenerator" : "MyObjectBuilder_Reactor/SmallBlockSmallGenerator";
            MyDefinitionId defId;
            MyCubeBlockDefinition def;
            if (!MyDefinitionId.TryParse(subtype, out defId) || !MyDefinitionManager.Static.TryGetCubeBlockDefinition(defId, out def) || def == null)
            {
                d["ok"] = false;
                d["error"] = "definition missing: " + subtype;
                return d;
            }
            var ob = MyObjectBuilderSerializerKeen.CreateNewObject(def.Id) as MyObjectBuilder_CubeBlock;
            if (ob == null) { d["ok"] = false; d["error"] = subtype + " has no MyObjectBuilder_CubeBlock builder"; return d; }
            int x = 0, y = 1, z = 0;
            if (cell != null && cell.Count == 3)
            {
                x = Convert.ToInt32(cell[0], CultureInfo.InvariantCulture);
                y = Convert.ToInt32(cell[1], CultureInfo.InvariantCulture);
                z = Convert.ToInt32(cell[2], CultureInfo.InvariantCulture);
            }
            ob.Min = new SerializableVector3I(x, y, z);
            ob.EntityId = 0;
            ob.IntegrityPercent = 1f;
            ob.BuildPercent = 1f;
            IMySlimBlock back = ((IMyCubeGrid)g).AddBlock(ob, false);   // IMyCubeGrid.AddBlock
            if (back == null) { d["ok"] = false; d["error"] = "AddBlock refused " + subtype + " at (" + x + ", " + y + ", " + z + ")"; return d; }
            DeLog.Info("DE.S15 reactor " + subtype + " added to grid " + id + " at (" + x + ", " + y + ", " + z + ") (" + via + ")");
            d["ok"] = true;
            d["block"] = subtype;
            if (kg > 0)
            {
                string why = AddUranium(back.FatBlock as MyReactor, kg);
                if (why != null) { d["ok"] = false; d["error"] = why; }
                else d["fuelKg"] = kg;
            }
            return d;
        }

        /// <summary>de.s15 {fuel: grid, kg}: uranium ingots into the grid's first reactor (format voice.power: the fuel brings the power).</summary>
        private static Dictionary<string, object> Fuel(Dictionary<string, object> d, long id, double kg, string via)
        {
            MyCubeGrid g = Grid(id);
            if (g == null) { d["ok"] = false; d["error"] = "no grid " + id; return d; }
            MyReactor r = null;
            foreach (MyReactor b in g.GetFatBlocks<MyReactor>()) { r = b; break; }
            string why = r == null ? "grid " + id + " has no reactor" : AddUranium(r, kg);
            d["ok"] = why == null;
            if (why != null) d["error"] = why;
            else
            {
                d["fuelKg"] = kg;
                DeLog.Info("DE.S15 fuel: " + kg.ToString("0.##", CultureInfo.InvariantCulture) + " kg of uranium into reactor " + r.EntityId + " of grid " + id + " (" + via + ")");
            }
            return d;
        }

        private static string AddUranium(MyReactor r, double kg)
        {
            if (r == null) return "no reactor block";
            MyInventory inv = r.GetInventory();   // MyEntityExtensions.GetInventory
            if (inv == null) return "reactor " + r.EntityId + " has no inventory";
            var ingot = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Uranium");
            return inv.AddItems((MyFixedPoint)kg, ingot) ? null : "the reactor's inventory refused " + kg + " kg of uranium";
        }

        /// <summary>de.s15 {power: on|off}: SE's Y key (SwitchReactorsLocal of the controlled ship controller) when the switch differs.</summary>
        private static Dictionary<string, object> Power(Dictionary<string, object> d, bool on, string via)
        {
            MySession s = MySession.Static;
            var sc = s != null ? s.ControlledEntity as MyShipController : null;
            if (sc == null || sc.CubeGrid == null) { d["ok"] = false; d["error"] = "not in a ship controller (the Y key needs a seat)"; return d; }
            MyCubeGrid g = sc.CubeGrid;
            bool before = g.IsPowerSwitchOn;
            if (before != on) sc.SwitchReactorsLocal();
            d["ok"] = g.IsPowerSwitchOn == on;
            d["switchOn"] = g.IsPowerSwitchOn;
            d["before"] = before;
            if (g.IsPowerSwitchOn != on) d["error"] = "the switch stayed " + (g.IsPowerSwitchOn ? "on" : "off") + " (main cockpit elsewhere? ship control off?)";
            DeLog.Info("DE.S15 power switch " + (before ? "on" : "off") + " -> " + (g.IsPowerSwitchOn ? "on" : "off") + " on grid " + g.EntityId + " (the Y key; " + via + ")");
            return d;
        }

        /// <summary>The spectator at the camera's position looking along an SN direction.</summary>
        private static Dictionary<string, object> Look(Dictionary<string, object> d, IList dir)
        {
            SiteMap map = SiteBinding.Map;
            MyCamera cam = MySector.MainCamera;
            if (map == null || cam == null) { d["ok"] = false; d["error"] = "no site map or camera"; return d; }
            if (dir == null || dir.Count != 3) { d["ok"] = false; d["error"] = "look: [x, y, z] (a Subnautica direction)"; return d; }
            var f = new Vec3(Convert.ToDouble(dir[0], CultureInfo.InvariantCulture), Convert.ToDouble(dir[1], CultureInfo.InvariantCulture), Convert.ToDouble(dir[2], CultureInfo.InvariantCulture));
            if (!(f.Length > 1e-6)) { d["ok"] = false; d["error"] = "look: a non-zero direction"; return d; }
            f = f / f.Length;
            // screen-up: Subnautica's up, or north when looking (almost) straight up or down
            Vec3 up = Math.Abs(f.Y) > 0.95 ? new Vec3(0, 0, 1) : new Vec3(0, 1, 0);
            Vector3D p = cam.WorldMatrix.Translation;
            SeMatrix m = S1Math.SpectatorAtSe(map, new Vec3(p.X, p.Y, p.Z), f, up);
            string why;
            if (!S1Commands.SetSpectator(m, out why)) { d["ok"] = false; d["error"] = why; return d; }
            d["ok"] = true;
            d["dir"] = new List<object> { f.X, f.Y, f.Z };
            d["vFovDeg"] = cam.FovWithZoom * 180.0 / Math.PI;
            return d;
        }

        private static Dictionary<string, object> Orbit(Dictionary<string, object> d, double km, double x, double z)
        {
            SiteMap map = SiteBinding.Map;
            if (map == null) { d["ok"] = false; d["error"] = "no site map"; return d; }
            if (!(km > 0) || km > 5000) { d["ok"] = false; d["error"] = "orbit: 0 < km <= 5000"; return d; }
            if (double.IsNaN(x) || double.IsNaN(z) || Math.Abs(x) > 50000 || Math.Abs(z) > 50000) { d["ok"] = false; d["error"] = "orbit: |x|, |z| <= 50 km"; return d; }
            // the spectator over the chart's anchor (or x, z SN metres beside it: the seam pair moves the camera across the
 // sea's terminator), looking down the chart's -Y (at the anchor: the planet centre), north up on the screen
            SeMatrix m = S1Math.SpectatorAt(map, new Vec3(x, km * 1000.0, z), new Vec3(0, -1, 0), new Vec3(0, 0, 1));
            string why;
            if (!S1Commands.SetSpectator(m, out why)) { d["ok"] = false; d["error"] = why; return d; }
            DeLog.Info("DE.S15 orbit: spectator " + km.ToString("0.#", CultureInfo.InvariantCulture) + " km above the site" +
                       (x != 0 || z != 0 ? " offset SN (" + x.ToString("0", CultureInfo.InvariantCulture) + ", " + z.ToString("0", CultureInfo.InvariantCulture) + ") m, looking down" : ", looking at the planet centre"));
            d["ok"] = true;
            d["km"] = km;
            d["x"] = x;
            d["z"] = z;
            d["rs"] = SiteBinding.Rs;
            MyCamera cam = MySector.MainCamera;
            if (cam != null) d["vFovDeg"] = cam.FovWithZoom * 180.0 / Math.PI;   // (the analyzer's expected disc)
            return d;
        }

        /// <summary>
 /// A test body in the sky of the site (dev; the throwaway world only): SE's own <c>MyWorldGenerator.AddPlanet(string,
 /// string, string, Vector3D, int, float, bool, long, bool, bool)</c>
 /// <paramref name="km"/> from the site's sea point, <paramref name="elevDeg"/> above its horizon toward the chart's
 /// north; not saved (<c>MyEntity.Save</c> false); <c>{planet: "remove"}</c> closes the ones it made.
 /// </summary>
        private static Dictionary<string, object> Planet(Dictionary<string, object> d, string def, double km, double size, double elevDeg, string via)
        {
            if (def == "remove")
            {
                int n = 0;
                foreach (long id in Spawned)
                {
                    MyEntity e;
                    if (MyEntities.TryGetEntityById(id, out e) && e is MyPlanet && !e.Closed) { e.Close(); n++; }
                }
                Spawned.Clear();
                d["ok"] = true;
                d["removed"] = n;
                return d;
            }
            SiteMap map = SiteBinding.Map;
            if (map == null) { d["ok"] = false; d["error"] = "no site map"; return d; }
            if (!(km > 0) || !(size > 1000)) { d["ok"] = false; d["error"] = "planet: km > 0, size > 1000"; return d; }
            double el = elevDeg * Math.PI / 180.0;
            Vec3 sea = map.C + map.U0 * map.Rs;
            Vec3 dir = map.N0 * Math.Cos(el) + map.U0 * Math.Sin(el);
            Vec3 centre = sea + dir * (km * 1000.0);
            var min = new Vector3D(centre.X - size / 2, centre.Y - size / 2, centre.Z - size / 2);
            string name = "DE.S15 " + def + " " + (Spawned.Count + 1);
            MyPlanet p = Sandbox.Game.World.MyWorldGenerator.AddPlanet(name.Replace(' ', '_'), name, def, min, 4546 + Spawned.Count, (float)size, false);
            if (p == null) { d["ok"] = false; d["error"] = "AddPlanet returned null (definition " + def + "?)"; return d; }
            p.Save = false;
            Spawned.Add(p.EntityId);
            DeLog.Info("DE.S15 planet " + def + " (" + via + "): " + km.ToString("0", CultureInfo.InvariantCulture) + " km from the site at " + elevDeg.ToString("0", CultureInfo.InvariantCulture) +
                       " deg, diameter " + size.ToString("0", CultureInfo.InvariantCulture) + " m, entity " + p.EntityId + " (not saved)");
            d["ok"] = true;
            d["planet"] = p.EntityId.ToString(CultureInfo.InvariantCulture);
            d["radius"] = (double)p.AverageRadius;
            return d;
        }

        private static readonly List<long> Spawned = new List<long>();

        private static int EventOf(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "welcome": return S15Conventions.Event.Welcome;
                case "engine": case "up": return S15Conventions.Event.EnginePowerUp;
                case "down": return S15Conventions.Event.EnginePowerDown;
                case "small": case "seamoth": return S15Conventions.Event.SmallWelcome;
                default: return 0;
            }
        }

        /// <summary>Chat: <c>/de s15 status|sky|play &lt;welcome|engine|down|small&gt;|power on|off|voice on|off|sky on|off|water on|off|apply|orbit &lt;km&gt;</c>.</summary>
        private static IDictionary<string, object> FromArgv(IDictionary<string, object> args)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            if (args != null) foreach (KeyValuePair<string, object> kv in args) o[kv.Key] = kv.Value;
            object v;
            if (!o.TryGetValue("argv", out v) || !(v is IList)) return o;
            var l = (IList)v;
            if (l.Count == 0) return o;
            string w0 = Convert.ToString(l[0], CultureInfo.InvariantCulture).ToLowerInvariant();
            string w1 = l.Count > 1 ? Convert.ToString(l[1], CultureInfo.InvariantCulture) : null;
            switch (w0)
            {
                case "play": if (w1 != null) o["play"] = w1; break;
                case "voice": if (w1 != null) o["voice"] = w1; break;
                case "sky": if (w1 != null) o["sky"] = w1; else o["status"] = true; break;
                case "water": if (w1 != null) o["water"] = w1; break;
                case "orbit": if (w1 != null) o["orbit"] = w1; break;
                case "power": if (w1 != null) o["power"] = w1; break;
            }
            return o;
        }

        private static bool On(object v)
        {
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            return s == "on" || s == "1" || s == "true";
        }

        private static bool Bool(IDictionary<string, object> a, string k, bool dflt)
        {
            object v;
            return a != null && a.TryGetValue(k, out v) && v != null ? On(v) : dflt;
        }

        private static long GridId(object v)
        {
            if (v == null) return 0;
            var s = v as string;
            if (s != null)
            {
                long l;
                return long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? l : 0;
            }
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            long k = S16Host.TrackedIdNear(d);
            return k != 0 ? k : (long)d;
        }

        private static double Num(IDictionary<string, object> a, string k, double dflt)
        {
            object v;
            if (a == null || !a.TryGetValue(k, out v) || v == null) return dflt;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return dflt; }
        }

        private static string Str(IDictionary<string, object> a, string k, string dflt)
        {
            object v;
            if (a == null || !a.TryGetValue(k, out v) || v == null) return dflt;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
