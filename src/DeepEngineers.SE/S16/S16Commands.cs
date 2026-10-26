using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// SE <c>de.s16</c> (C11 and chat <c>/de s16 …</c>, dev builds; not in the release allow-list, so a release build refuses it):
 /// <list type="bullet">
 /// <item><c>{status}</c> (default; chat <c>/de s16 status</c>): the counters, the damage gate, the HUD grid, the tracked grids;</item>
 /// <item><c>{ladder}</c> (chat <c>/de s16 ladder</c>): the hull ladder, the tiers and the damage curve as pinned;</item>
 /// <item><c>{damage: "on"|"off"}</c> (chat <c>/de s16 damage on|off</c>): the crush damage switch (the HUD, creaks and voice stay);</item>
 /// <item><c>{spawn: "light"|"heavy"|"room"|"bare"|"tier1"|"tier2"|"tier3"|"lightlarge"|"heavylarge", x, y, z, n?, block?, centre?, label?}</c>:
 /// a dynamic test grid (never saved) centred at SN (x, y, z) from <see cref="Plan"/>;</item>
 /// <item><c>{descend: id, rate, to, label?}</c>, <c>{stop: id}</c>, <c>{job: id}</c>, <c>{watch: id|0}</c>, <c>{remove: id}</c>: the
 /// scripted descent (the grid's velocity toward the planet centre each tick until its lowest point is at <c>to</c> m, then held),
 /// the forced HUD grid, the teardown;</item>
 /// <item><c>{floor: [x, z]}</c>, <c>{deep: metres}</c>: the planet floor's depth at SN (x, z); the first column at least that deep.</item>
 /// </list>
 /// </summary>
    internal static class S16Commands
    {
        public const string Verb = "de.s16";

        /// <summary>
 /// The scripted hulls: a solid n³ cube of one armour block (light 4³ small: 56 counted, 8
 /// buried, 180 m; heavy 4³ small: 300 m), a heavy large 3³ with a tier block in its centre, a hollow 5³ heavy large shell
 /// around a dry room with a light armour block and an Air Vent against a wall inside (the air rule: 1,000 m, the inner
 /// block uncounted), a bare small craft, and a static 3³ heavy large "station" (never tracked nor rated: format crush.depth, depth 9).
 /// </summary>
        public static List<S16Host.Placement> Plan(string kind, IDictionary<string, object> args, out string why)
        {
            why = null;
            var p = new List<S16Host.Placement>();
            string k = (kind ?? "").ToLowerInvariant();
            switch (k)
            {
                case "light": Cube(p, Str(args, "block", "SmallBlockArmorBlock"), (int)Num(args, "n", 4), null); break;
                case "heavy": Cube(p, Str(args, "block", "SmallHeavyBlockArmorBlock"), (int)Num(args, "n", 4), null); break;
                case "lightlarge": Cube(p, "LargeBlockArmorBlock", (int)Num(args, "n", 3), null); break;
                case "heavylarge": Cube(p, "LargeHeavyBlockArmorBlock", (int)Num(args, "n", 3), null); break;
                case "tier1": Cube(p, "LargeHeavyBlockArmorBlock", 3, "DE_PressureHullMk1Large"); break;
                case "tier2": Cube(p, "LargeHeavyBlockArmorBlock", 3, "DE_PressureHullMk2Large"); break;
                case "tier2small": Cube(p, "SmallBlockArmorBlock", 3, "DE_PressureHullMk2Small"); break;   // depth 11: no Mk3
                case "station": Cube(p, "LargeHeavyBlockArmorBlock", 3, null); break;   // spawned static
                case "room":
                    for (int x = 0; x < 5; x++)
                        for (int y = 0; y < 5; y++)
                            for (int z = 0; z < 5; z++)
                                if (x == 0 || x == 4 || y == 0 || y == 4 || z == 0 || z == 4) p.Add(new S16Host.Placement("LargeHeavyBlockArmorBlock", x, y, z));
                    p.Add(new S16Host.Placement("LargeBlockArmorBlock", 1, 1, 1));
                    // against the x = 4 wall (S15 breach test: a vent alone in the middle became a one-block grid when a wall
 // block was removed, DetectDisconnects, and the room data went with it); the twin gives the same summary
                    p.Add(new S16Host.Placement("MyObjectBuilder_AirVent/AirVentFull", 3, 2, 2));
                    break;
                case "bare":
                    p.Add(new S16Host.Placement("MyObjectBuilder_Cockpit/SmallBlockCockpit", 0, 0, 0));          // 3x3x3
                    p.Add(new S16Host.Placement("MyObjectBuilder_BatteryBlock/SmallBlockSmallBatteryBlock", 3, 0, 0));
                    break;
                default:
                    Cube(p, Str(args, "block", "SmallBlockArmorBlock"), (int)Num(args, "n", 4), Str(args, "centre", null));
                    break;
            }
            return p;
        }

        private static void Cube(List<S16Host.Placement> p, string block, int n, string centre)
        {
            n = Math.Max(1, Math.Min(8, n));
            int mid = n / 2;
            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++)
                    for (int z = 0; z < n; z++)
                        p.Add(new S16Host.Placement(centre != null && x == mid && y == mid && z == mid ? centre : block, x, y, z));
        }

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Verb) return null;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                if (!BuildFlavor.IsDev) { d["ok"] = false; d["error"] = "developer command, not in the release build"; return d; }
                args = FromArgv(args);
                object v;
                if (args.TryGetValue("damage", out v))
                {
                    string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                    if (s != "on" && s != "off") { d["ok"] = false; d["error"] = "damage: on|off"; return d; }
                    S16Host.DamageSwitch = s == "on";
                    DeLog.Info("DE.S16 damage switch " + s + " (" + via + ")");
                    d["ok"] = true;
                    d["damage"] = s;
                    d["line"] = "DE.S16 crush damage " + s + (S16Host.DamageGateOpen || s == "off" ? "" : " (gate: " + S16Host.DamageGateWhy + ")");
                    return d;
                }
                if (args.ContainsKey("ladder")) return Ladder(d);
                if (args.TryGetValue("spawn", out v))
                {
                    string kind = Convert.ToString(v, CultureInfo.InvariantCulture);
                    string why;
                    List<S16Host.Placement> plan = Plan(kind, args, out why);
                    bool isStatic = string.Equals(kind, "station", StringComparison.OrdinalIgnoreCase) || (args.ContainsKey("static") && Convert.ToBoolean(args["static"], CultureInfo.InvariantCulture));
                    long id = why == null ? S16Host.Spawn(plan, Num(args, "x", 0), Num(args, "y", -150), Num(args, "z", 0), Str(args, "label", kind), isStatic, out why) : 0;
                    d["ok"] = id != 0;
                    // never "id": the C11 reply's own key (DevChannel.Send copies the reply's keys over it); a string: MiniJson reads numbers as doubles
                    if (id != 0) { d["grid"] = id.ToString(CultureInfo.InvariantCulture); d["floor"] = S16Host.FloorDepth(Num(args, "x", 0), Num(args, "z", 0)); }
                    else d["error"] = why;
                    return d;
                }
                if (args.TryGetValue("descend", out v))
                {
                    long id = GridId(v);
                    string err = S16Host.Descend(id, Num(args, "rate", 5), Num(args, "to", 300), Str(args, "label", null));
                    d["ok"] = err == null;
                    if (err != null) d["error"] = err;
                    return d;
                }
                if (args.TryGetValue("stop", out v)) { d["ok"] = S16Host.StopDescend(GridId(v)); return d; }
                if (args.TryGetValue("job", out v)) { d["ok"] = true; d["job"] = S16Host.JobStatus(GridId(v)); return d; }
                if (args.TryGetValue("watch", out v))
                {
                    S16Host.WatchId = GridId(v);
                    d["ok"] = true;
                    d["watch"] = S16Host.WatchId;
                    return d;
                }
                if (args.TryGetValue("remove", out v)) { d["ok"] = S16Host.Remove(GridId(v)); return d; }
                if (args.TryGetValue("floor", out v))
                {
                    var l = v as IList;
                    if (l == null || l.Count != 2) { d["ok"] = false; d["error"] = "floor: [x, z]"; return d; }
                    double x = Convert.ToDouble(l[0], CultureInfo.InvariantCulture), z = Convert.ToDouble(l[1], CultureInfo.InvariantCulture);
                    double f = S16Host.FloorDepth(x, z);
                    d["ok"] = !double.IsNaN(f);
                    d["floor"] = double.IsNaN(f) ? null : (object)f;
                    return d;
                }
                if (args.TryGetValue("deep", out v))
                {
                    object nv;
                    var near = args.TryGetValue("near", out nv) ? nv as IList : null;
                    double? nx = near != null && near.Count == 2 ? Convert.ToDouble(near[0], CultureInfo.InvariantCulture) : (double?)null;
                    double? nz = near != null && near.Count == 2 ? Convert.ToDouble(near[1], CultureInfo.InvariantCulture) : (double?)null;
                    double[] c = S16Host.DeepColumn(Convert.ToDouble(v, CultureInfo.InvariantCulture), nx, nz);
                    d["ok"] = c != null;
                    if (c != null) { d["x"] = c[0]; d["z"] = c[1]; d["floor"] = c[2]; d["near"] = new List<object> { c[3], c[4] }; d["ring"] = c[5]; d["skipped"] = c[6]; }
                    else d["error"] = "no clear column that deep on the 60 m - 15 km rings around the character";
                    return d;
                }
                d["ok"] = true;
                Dictionary<string, object> st = S16Host.Status();
                d["status"] = st;
                d["line"] = "DE.S16 " + (S16Host.Enabled ? (S16Host.InactiveWhy == null ? "active" : "inactive: " + S16Host.InactiveWhy) : "OFF") +
                            ", damage " + (S16Host.DamageGateOpen ? "on" : "off (" + S16Host.DamageGateWhy + ")") + ", " + S16Host.Tracked + " grid(s), HUD grid " + st["hudGrid"] +
                            ", creaks " + S16Host.Creaks + ", voice " + S16Host.VoicePlays + "/" + S16Host.VoiceEvents + ", damaged blocks " + S16Host.DamagedBlocks + " (" + S16Host.Destroyed + " destroyed)";
                return d;
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.s16 " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                d["ok"] = false;
                d["error"] = "de.s16 failed: " + e.GetType().Name + ": " + e.Message;
                return d;
            }
        }

        private static Dictionary<string, object> Ladder(Dictionary<string, object> d)
        {
            var hull = new List<object>();
            foreach (S16HullRow r in S16Conventions.HullTable)
                hull.Add(S16Conventions.Size.Name(r.Size) + " " + S16Conventions.BlockClass.Name(r.Class) + " " + r.CrushM + " m");
            var tiers = new List<object>();
            foreach (S16TierRow t in S16Conventions.TierTable)
                tiers.Add("Mk" + t.Tier + " " + t.SubtypeBase + "Large|Small " + t.CrushM + " m (" + S16Conventions.AlloyPrefix + t.Tier + " from " + t.Ingots + ")");
            d["ok"] = true;
            d["hull"] = hull;
            d["tiers"] = tiers;
            d["damage"] = "hp/s = min(" + S16Conventions.Damage.CapMilliHpS / 1000 + ", " + S16Conventions.Damage.BaseMilliHpS / 1000.0 + " + " +
                          S16Conventions.Damage.PerMetreMilliHpS / 1000.0 + " x (d - L)); integrity = hp x k (small 1, large 25); the " +
                          S16Conventions.Damage.MaxBlocksPerPeriod + " deepest counted blocks per grid per second";
            d["creak"] = "from L-" + S16Conventions.Creak.BandM + " m (every 9 s), L-" + S16Conventions.Creak.Level2M + " m (6 s), past L (3 s)";
            d["voice"] = "at the crossing and every " + S16Conventions.Voice.RepeatMs / 1000 + " s while past the limit, re-armed " + S16Conventions.Voice.RearmM +
                         " m above, at most every " + S16Conventions.Voice.CooldownMs / 1000 + " s per grid; Subnautica plays it at " +
                         (S16Conventions.Voice.VolumePermille / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " with S16's own sounds ducked for " +
                         (S16Conventions.Voice.DuckMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";
            d["hash"] = "0x" + S16Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture);
            d["line"] = "DE.S16 ladder: " + string.Join("; ", hull.ConvertAll(o => (string)o).ToArray()) + " | tiers " + string.Join("; ", tiers.ConvertAll(o => (string)o).ToArray());
            return d;
        }

        /// <summary>Chat: <c>/de s16 status|ladder|damage on|off|watch &lt;id&gt;|remove &lt;id&gt;</c> arrives as <c>{argv: [...]}</c>.</summary>
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
                case "ladder": o["ladder"] = true; break;
                case "damage": if (w1 != null) o["damage"] = w1; break;
                case "watch": o["watch"] = w1 ?? "0"; break;
                case "remove": if (w1 != null) o["remove"] = w1; break;
                case "deep": o["deep"] = w1 ?? "1900"; break;
            }
            return o;
        }

        /// <summary>
 /// A grid id: exact from a string (the spawn reply's "grid") or an integer; a JSON number (MiniJson reads doubles: 53 bits,
 /// an 18-digit entity id rounds) is resolved to the tracked grid it rounds from.
 /// </summary>
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
