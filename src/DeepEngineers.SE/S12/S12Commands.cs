using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRage.Game.ModAPI;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// <c>de.s12</c> (dev builds only; release refuses it through UserCommandRules: not in the release table):
 /// <list type="bullet">
 /// <item><c>{status: true}</c>: the camera's air answer, the snapshots, the flood tracks, the vents, the C6 block.</item>
 /// <item><c>{air|floods|vents|third: "on"|"off"}</c>: the live switches (the air switch off = the S7 rule; third on = the
 /// camera treated as SE's third-person camera, format air.camera, for a spectator test).</item>
 /// <item><c>{rig: [x, y, z]}</c>: the vent test grid (an Air Vent in a wall with an Oxygen Tank behind it, a battery);
 /// <c>{ventmode: "&lt;vent&gt;", depressurize: true|false}</c> sets SE's own vent; <c>{remove: "&lt;grid&gt;"}</c> closes the rig;
 /// <c>{swap: "&lt;grid&gt;", cell, block, forward, up}</c> replaces a block (the house's slope).</item>
 /// <item><c>{probe: [x, y, z]}</c> (SN metres; default the camera): per candidate grid the oxygen-volume depth (format
 /// air.volume) without the hold.</item>
 /// <item><c>{breach: "&lt;grid&gt;", cell: [x, y, z]}</c>: removes an armour block (the test house's wall); <c>{repair: "&lt;grid&gt;"}</c>
 /// puts the removed blocks back.</item>
 /// <item><c>{vent: "&lt;vent or grid id&gt;", mode: "pressurize"|"depressurize"|"off", seconds, rate}</c>: drives SE's room
 /// oxygen through a vent at rate x the room per second (format vent.event's dev source).</item>
 /// </list>
 /// Chat: <c>/de s12 status | air on|off | probe | breach &lt;grid&gt; x y z | repair &lt;grid&gt; | vent &lt;id&gt; &lt;mode&gt; [seconds]</c>.
 /// </summary>
    internal static class S12Commands
    {
        public const string Verb = "de.s12";

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Verb) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                if (!BuildFlavor.IsDev) { d["ok"] = false; d["error"] = "developer command, not in the release build"; return d; }
                args = FromArgv(args);
                object v;
                foreach (string sw in new[] { "air", "floods", "vents", "third", "charrows" })
                {
                    if (!args.TryGetValue(sw, out v)) continue;
                    string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                    if (s != "on" && s != "off") { d["ok"] = false; d["error"] = sw + ": on|off"; return d; }
                    bool on = s == "on";
                    if (sw == "air") S12Switch.AirOn = on; else if (sw == "floods") S12Switch.FloodOn = on; else if (sw == "vents") S12Switch.VentOn = on;
                    else if (sw == "charrows") S12Switch.CharRows = on; else S12Switch.ForceThirdPerson = on;
                    DeLog.Info("DE.S12 " + sw + " " + s + " (" + via + ")");
                    d["ok"] = true;
                    d[sw] = s;
                    d["line"] = "DE.S12 " + sw + " " + s + (sw == "air" && !on ? " (the S7 rule decides the air)" : "");
                    return d;
                }
                if (args.TryGetValue("breach", out v))
                {
                    long gid = Id(v);
                    int[] c = Cell(args);
                    string err = c == null ? "cell: [x, y, z] (grid cells)" : S12Se.Breach(gid, c[0], c[1], c[2]);
                    d["ok"] = err == null;
                    if (err != null) d["error"] = err;
                    else d["line"] = "DE.S12 breach " + gid + " at " + c[0] + "," + c[1] + "," + c[2];
                    return d;
                }
                if (args.TryGetValue("repair", out v))
                {
                    int n;
                    string err = S12Se.Repair(Id(v), out n);
                    d["ok"] = err == null;
                    d["blocks"] = (long)n;
                    if (err != null) d["error"] = err;
                    return d;
                }
                if (args.TryGetValue("vent", out v))
                {
                    long vid;
                    string err = S12Vents.Drive(Id(v), Str(args, "mode", "pressurize"), Num(args, "seconds", 10), Num(args, "rate", 0.1), out vid);
                    d["ok"] = err == null;
                    d["vent"] = vid.ToString(CultureInfo.InvariantCulture);
                    if (err != null) d["error"] = err;
                    return d;
                }
                if (args.TryGetValue("probe", out v)) return Probe(v as IList, d);
                if (args.TryGetValue("rig", out v))
                {
                    var l = v as IList;
                    if (l == null || l.Count != 3) { d["ok"] = false; d["error"] = "rig: [x, y, z] (SN metres of its block (0, 0, 0))"; return d; }
                    string why;
                    Dictionary<string, object> r = S12Se.Rig(Convert.ToDouble(l[0], CultureInfo.InvariantCulture), Convert.ToDouble(l[1], CultureInfo.InvariantCulture),
                                                             Convert.ToDouble(l[2], CultureInfo.InvariantCulture), out why);
                    d["ok"] = r != null;
                    if (r == null) d["error"] = why;
                    else foreach (KeyValuePair<string, object> kv in r) d[kv.Key] = kv.Value;
                    return d;
                }
                if (args.TryGetValue("ventmode", out v))
                {
                    long vid;
                    object dep;
                    bool depress = args.TryGetValue("depressurize", out dep) && (dep is bool ? (bool)dep : string.Equals(Convert.ToString(dep, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase));
                    string err = S12Vents.Mode(Id(v), depress, out vid);
                    d["ok"] = err == null;
                    d["vent"] = vid.ToString(CultureInfo.InvariantCulture);
                    if (err != null) d["error"] = err;
                    return d;
                }
                if (args.TryGetValue("swap", out v))
                {
                    int[] c = Cell(args);
                    long blockId = 0;
                    string err = c == null ? "cell: [x, y, z] (grid cells)" : S12Se.Swap(Id(v), c[0], c[1], c[2], Str(args, "block", null), Str(args, "forward", "Forward"), Str(args, "up", "Up"), out blockId);
                    d["ok"] = err == null;
                    if (blockId != 0) d["blockId"] = blockId.ToString(CultureInfo.InvariantCulture);
                    if (err == null) d["swapped"] = new List<object> { (long)c[0], (long)c[1], (long)c[2] };   // the launcher checks it (an older plugin answers the status)
                    if (err != null) d["error"] = err;
                    return d;
                }
                if (args.TryGetValue("remove", out v)) { d["ok"] = S12Se.Remove(Id(v)); return d; }
                d["ok"] = true;
                d["status"] = S12Se.Status();
                d["line"] = "DE.S12 " + (S12Se.Active ? "active" : "inactive") + ", camera " + (S12Air.LastIn ? "in room " + S12Air.LastRoom + " of " + S12Air.LastGrid : "outside every room") +
                            ", floods " + S12Floods.Count + ", vents " + S12Vents.Count + ", mutants " + (S12SeMutants.Active.Length > 0 ? S12SeMutants.Active : "none");
                Jsonl.WriteLineBuffered(JsonText.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s12.status" }, { "status", d["status"] },
                }));
                return d;
            }
            catch (Exception e)
            {
                d["ok"] = false;
                d["error"] = e.GetType().Name + ": " + e.Message;
                return d;
            }
        }

        /// <summary>format air.volume at a point (SN metres, default the camera): every candidate grid's raw depth, no hold.</summary>
        private static Dictionary<string, object> Probe(IList pt, Dictionary<string, object> d)
        {
            Vector3D p = S12Air.LastCamera;
            SiteMap map = SiteBinding.Map;
            if (pt != null && pt.Count == 3 && map != null)
            {
                Vec3 se = map.SnToSe(new Vec3(Convert.ToDouble(pt[0], CultureInfo.InvariantCulture), Convert.ToDouble(pt[1], CultureInfo.InvariantCulture), Convert.ToDouble(pt[2], CultureInfo.InvariantCulture)));
                p = new Vector3D(se.X, se.Y, se.Z);
            }
            var list = new List<object>();
            foreach (GridMirrorProducer.MirroredGrid mg in GridMirrorProducer.Grids.Values)
            {
                MyCubeGrid g = mg.Grid;
                if (g == null || g.Closed || mg.Projection) continue;
                if (g.PositionComp.WorldAABB.Contains(p) == ContainmentType.Disjoint) continue;
                MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                if (gas == null) continue;
                Vector3D local = Vector3D.Transform(p, g.PositionComp.WorldMatrixNormalizedInv);
                bool proc = ((IMyGridGasSystem)gas).IsProcessingData;
                S12Snapshot snap = S12Rooms.Of(gas);
                S12Depth live = default(S12Depth), fromSnap = default(S12Depth);
                if (!proc)
                    live = S12AirVolume.Depth(local.X, local.Y, local.Z, g.GridSize, (x, y, z) =>
                    {
                        var c = new Vector3I(x, y, z);
                        MyOxygenRoom r = gas.GetOxygenRoomForCubeGridPosition(ref c);
                        return r != null && r.IsAirtight ? (r.Index > 0 ? r.Index : int.MaxValue) : 0;
                    });
                if (snap != null) fromSnap = S12AirVolume.Depth(local.X, local.Y, local.Z, g.GridSize, snap.RoomAt);
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "grid", g.EntityId.ToString(CultureInfo.InvariantCulture) }, { "processing", proc }, { "local", new List<object> { local.X, local.Y, local.Z } },
                    { "cell", new List<object> { (long)live.CellX, (long)live.CellY, (long)live.CellZ } }, { "depth", proc ? null : (object)Math.Round(live.DepthM, 4) },
                    { "room", (long)live.Room }, { "snapshotDepth", snap != null ? (object)Math.Round(fromSnap.DepthM, 4) : null }, { "snapshotRooms", snap != null ? (long)snap.Rooms.Count : 0L },
                    { "h", S12AirVolume.Hysteresis(g.GridSize) },
                });
            }
            d["ok"] = true;
            d["candidates"] = list;
            return d;
        }

        private static int[] Cell(IDictionary<string, object> a)
        {
            object v;
            if (!a.TryGetValue("cell", out v) || !(v is IList)) return null;
            var l = (IList)v;
            if (l.Count != 3) return null;
            return new[] { (int)Math.Round(Convert.ToDouble(l[0], CultureInfo.InvariantCulture)), (int)Math.Round(Convert.ToDouble(l[1], CultureInfo.InvariantCulture)),
                           (int)Math.Round(Convert.ToDouble(l[2], CultureInfo.InvariantCulture)) };
        }

        private static IDictionary<string, object> FromArgv(IDictionary<string, object> args)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            if (args != null) foreach (KeyValuePair<string, object> kv in args) o[kv.Key] = kv.Value;
            object v;
            if (!o.TryGetValue("argv", out v) || !(v is IList)) return o;
            var l = (IList)v;
            if (l.Count == 0) return o;
            Func<int, string> w = i => i < l.Count ? Convert.ToString(l[i], CultureInfo.InvariantCulture) : null;
            switch (w(0).ToLowerInvariant())
            {
                case "air": case "floods": case "vents": case "third": case "charrows": if (w(1) != null) o[w(0).ToLowerInvariant()] = w(1); break;
                case "probe": o["probe"] = true; break;
                case "breach":
                    o["breach"] = w(1);
                    if (l.Count >= 5) o["cell"] = new List<object> { w(2), w(3), w(4) };
                    break;
                case "repair": o["repair"] = w(1); break;
                case "vent":
                    o["vent"] = w(1);
                    if (w(2) != null) o["mode"] = w(2);
                    if (w(3) != null) o["seconds"] = w(3);
                    break;
            }
            return o;
        }

        /// <summary>An entity id: exact from a string or an integer (a JSON double rounds an 18-digit id: pass ids as strings).</summary>
        private static long Id(object v)
        {
            if (v == null) return 0;
            var s = v as string;
            if (s != null) { long l; return long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? l : 0; }
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            return (long)Convert.ToDouble(v, CultureInfo.InvariantCulture);
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
