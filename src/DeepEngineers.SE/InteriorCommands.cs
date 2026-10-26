using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The SE commands of the interiors and lights build pass (CONTRACT §5.4 "S7 control commands"; C11 and <c>/de …</c>; dev
 /// builds): <c>de.interior {action: status | probe, point?: [x, y, z]}</c> (the camera's answer, the candidates, the gas
 /// systems' processing state, the RoomSets sent; <c>probe</c> = the raw per-grid answers at a point, SE world, default
 /// the camera), <c>de.interior {action: door, block: id, open: true | false}</c>
 /// and
 /// <c>de.lights {action: status}</c>. Chained into <c>Plugin.ExtraCommand</c> (the S7 dispatcher
 /// <c>S7Commands.Execute</c> of the contract is unowned in this workflow: the integrator may fold this one into it).
 /// Update thread.
 /// </summary>
    internal static class InteriorCommands
    {
        public const string Interior = "de.interior", Lights = "de.lights";

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Interior && cmd != Lights) return null;
            try
            {
                if (args == null) args = new Dictionary<string, object>(StringComparer.Ordinal);
                string action = "status";
                object a;
                if (args.TryGetValue("action", out a) && a is string) action = ((string)a).Trim().ToLowerInvariant();
                if (cmd == Lights)
                {
                    if (action != "status") return Fail("de.lights: unknown action '" + action + "' (status)");
                    Dictionary<string, object> d = S7Lights.Status();
                    d["ok"] = true;
                    d["line"] = "DE.S7 lights found=" + d["found"] + " sent=" + d["sent"] + " shadowed=" + d["shadowed"] + " owners=" + d["owners"] + " grids=" + d["grids"];
                    return d;
                }
                switch (action)
                {
                    case "status":
                    {
                        Dictionary<string, object> d = S7Interior.Status();
                        d["ok"] = true;
                        d["line"] = "DE.S7 interior inRoom=" + d["inRoom"] + " processing=" + d["processing"] + " breached=" + d["breached"] + " roomGrid=" + d["roomGridId"] +
                                    " candidates=" + d["candidates"] + " pressurization=" + S7Interior.Pressurization;
                        return d;
                    }
                    case "probe":
                    {
                        Vector3D p;
                        if (!Point(args, out p)) return Fail("de.interior probe: no camera and no point [x, y, z]");
                        Dictionary<string, object> d = S7Interior.ProbeAt(p);
                        d["ok"] = true;
                        d["line"] = "DE.S7 interior probe at " + p.X.ToString("0.##", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.##", CultureInfo.InvariantCulture) + "," +
                                    p.Z.ToString("0.##", CultureInfo.InvariantCulture) + ": " + ((List<object>)d["candidates"]).Count + " candidate grid(s)";
                        return d;
                    }
                    case "door":
                        return Door(args);
                    default:
                        return Fail("de.interior: unknown action '" + action + "' (status | probe | door)");
                }
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error " + cmd + " " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return Fail(cmd + " failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
 /// Review F8: <c>de.interior {action: door, block: &lt;entity id&gt;, open: true | false}</c> (or <c>state: open | close |
 /// toggle</c>). Replies the door's status before, whether it is working (powered), and the gas system's processing
 /// state; an <c>s7.door</c> row per call. Keyed by the block's entity id.
 /// </summary>
        private static Dictionary<string, object> Door(IDictionary<string, object> args)
        {
            object bv;
            long id;
            if (!args.TryGetValue("block", out bv) || !S5Args.TryEntityId(bv, out id))
                return Fail("de.interior door: block is the door's entity id (decimal string; the de.s3 room reply names it)");
            string want = "toggle";
            object v;
            if (args.TryGetValue("open", out v) && v is bool) want = (bool)v ? "open" : "close";
            else if (args.TryGetValue("state", out v) && v is string) want = ((string)v).Trim().ToLowerInvariant();
            if (want != "open" && want != "close" && want != "toggle") return Fail("de.interior door: open true|false or state open|close|toggle");
            MyEntity e;
            if (!MyEntities.TryGetEntityById(id, out e) || e == null || e.Closed) return Fail("de.interior door: no entity " + id.ToString(CultureInfo.InvariantCulture));
            var door = e as Sandbox.ModAPI.IMyDoor;
            if (door == null) return Fail("de.interior door: entity " + id.ToString(CultureInfo.InvariantCulture) + " is a " + e.GetType().Name + ", not a door");
            var ingame = (Sandbox.ModAPI.Ingame.IMyDoor)door;
            string before = ingame.Status.ToString();
            bool working = door.IsWorking;
            if (want == "open") ingame.OpenDoor();
            else if (want == "close") ingame.CloseDoor();
            else ingame.ToggleDoor();
            var block = e as MyCubeBlock;
            MyCubeGrid grid = block != null ? block.CubeGrid : null;
            Sandbox.Game.GameSystems.MyGridGasSystem gas = grid != null && grid.GridSystems != null ? grid.GridSystems.GasSystem : null;
            bool processing = gas != null && ((VRage.Game.ModAPI.IMyGridGasSystem)gas).IsProcessingData;
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "ok", working }, { "block", id.ToString(CultureInfo.InvariantCulture) }, { "want", want }, { "statusBefore", before },
                { "statusAfter", ingame.Status.ToString() }, { "working", working }, { "fullyClosed", door.IsFullyClosed },
                { "grid", grid != null ? grid.EntityId.ToString(CultureInfo.InvariantCulture) : "0" }, { "gasProcessing", processing },
                { "gasBlocks", (long)S7Interior.GasBlockCount(gas) },
            };
            if (!working) d["error"] = "the door is not working (unpowered, off or damaged): MyDoor acts only while IsWorking";
            d["line"] = "DE.S7 interior door " + id.ToString(CultureInfo.InvariantCulture) + " " + want + " (was " + before + ", working " + working + ")";
            var row = new Dictionary<string, object>(d, StringComparer.Ordinal);
            row.Remove("line");
            row["t"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            row["ev"] = "s7.door";
            row["tick"] = (long)S7Interior.CameraTick;
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
            DeLog.Info((string)d["line"]);
            return d;
        }

        private static bool Point(IDictionary<string, object> args, out Vector3D p)
        {
            object v;
            var l = args.TryGetValue("point", out v) ? v as List<object> : null;
            if (l != null && l.Count == 3 && l[0] is double && l[1] is double && l[2] is double)
            {
                p = new Vector3D((double)l[0], (double)l[1], (double)l[2]);
                return true;
            }
            MyCamera cam = MySector.MainCamera;
            p = cam != null ? cam.WorldMatrix.Translation : Vector3D.Zero;
            return cam != null;
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.S7 " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
