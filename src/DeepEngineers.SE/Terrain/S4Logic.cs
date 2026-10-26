using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// <c>/de terrain …</c> chat syntax → the C11 <c>de.terrain</c> args.
 /// Values: <c>1,2,3</c> → a number list, <c>x,z;x,z</c> → a list of lists, on/off/true/false → bool, numbers → double.
 /// </summary>
    internal static class S4Args
    {
        public const string ChatUsage =
            "/de terrain [status|gate|tiles|n1|n3|stop] | hash <x> <y> <z> [half] | crater <x> <y> <z> [radius] | " +
            "edit <cutout|explosion|voxelhand|meteor|writerange|shipdrill> <x> <y> <z> [radius] [drill=0x..] | " +
            "at1 [<x> <z>] [size=64 spacing=2.5 fromY=20 toY=-400 random=1000 prefetch=on] | bake [<bx> <by> <bz>|remove] | " +
            "drop <x,z> [<x,z> ...] | walk <x> <z> [headingDeg] [length] | perf from=x,y,z to=x,y,z [speed=100] | at0b [lods=0,1,2 heights=-17.5]";

        public static readonly string[] Actions =
            { "status", "gate", "tiles", "hash", "crater", "edit", "at1", "rays", "bake", "drop", "walk", "perf", "n1", "n3", "at0b", "stop" };

        /// <summary>The action of a C11 request (default "status"); "rays" is an alias of "at1".</summary>
        public static string ActionOf(IDictionary<string, object> args)
        {
            string a = MiniJson.GetString(args, "action");
            if (string.IsNullOrEmpty(a)) return "status";
            a = a.Trim().ToLowerInvariant();
            return a == "rays" ? "at1" : a;
        }

        /// <summary>argv[0] is "terrain". Returns the args, or null with <paramref name="usage"/>.</summary>
        public static Dictionary<string, object> ParseChat(string[] argv, out string usage)
        {
            usage = null;
            var args = new Dictionary<string, object>(StringComparer.Ordinal);
            string action = argv.Length > 1 ? argv[1].ToLowerInvariant() : "status";
            if (Array.IndexOf(Actions, action) < 0) { usage = ChatUsage; return null; }
            if (action == "rays") action = "at1";
            args["action"] = action;
            var pos = new List<string>();
            for (int i = 2; i < argv.Length; i++)
            {
                string t = argv[i];
                int eq = t.IndexOf('=');
                if (eq > 0) args[t.Substring(0, eq)] = Value(t.Substring(eq + 1));
                else pos.Add(t);
            }
            switch (action)
            {
                case "hash":
                case "crater":
                {
                    if (pos.Count >= 3)
                    {
                        object p = Point(pos, 0, 3);
                        if (p == null) { usage = ChatUsage; return null; }
                        args["sn"] = p;
                        if (pos.Count > 3) args[action == "hash" ? "half" : "radius"] = Value(pos[3]);
                    }
                    else if (pos.Count > 0 || (!args.ContainsKey("sn") && !args.ContainsKey("se"))) { usage = "/de terrain " + action + " <x> <y> <z> [" + (action == "hash" ? "half" : "radius") + "]"; return null; }
                    break;
                }
                case "edit":
                {
                    if (pos.Count < 4 || TerrainPaths.IndexOf(pos[0]) < 0) { usage = "/de terrain edit <cutout|explosion|voxelhand|meteor|writerange|shipdrill> <x> <y> <z> [radius] [drill=0x..]"; return null; }
                    args["path"] = pos[0].ToLowerInvariant();
                    object p = Point(pos, 1, 3);
                    if (p == null) { usage = ChatUsage; return null; }
                    args["sn"] = p;
                    if (pos.Count > 4) args["radius"] = Value(pos[4]);
                    break;
                }
                case "at1":
                    if (pos.Count == 2)
                    {
                        object p = Point(pos, 0, 2);
                        if (p == null) { usage = ChatUsage; return null; }
                        args["origin"] = p;
                    }
                    else if (pos.Count != 0) { usage = "/de terrain at1 [<x> <z>] [size=.. spacing=.. fromY=.. toY=.. random=.. prefetch=on|off]"; return null; }
                    break;
                case "bake":
                    if (pos.Count == 1 && pos[0].Equals("remove", StringComparison.OrdinalIgnoreCase)) args["remove"] = true;
                    else if (pos.Count == 3)
                    {
                        object p = Point(pos, 0, 3);
                        if (p == null) { usage = ChatUsage; return null; }
                        args["batch"] = p;
                    }
                    else if (pos.Count != 0) { usage = "/de terrain bake [<bx> <by> <bz>|remove]"; return null; }
                    break;
                case "drop":
                {
                    if (pos.Count == 0 && !args.ContainsKey("points")) { usage = "/de terrain drop <x,z> [<x,z> ...]"; return null; }
                    if (pos.Count > 0)
                    {
                        var pts = new List<object>();
                        foreach (string s in pos)
                        {
                            var v = Value(s) as List<object>;
                            if (v == null || v.Count != 2) { usage = "/de terrain drop <x,z> [<x,z> ...]"; return null; }
                            pts.Add(v);
                        }
                        args["points"] = pts;
                    }
                    break;
                }
                case "walk":
                    if (pos.Count >= 2)
                    {
                        object p = Point(pos, 0, 2);
                        if (p == null) { usage = ChatUsage; return null; }
                        args["from"] = p;
                        if (pos.Count > 2) args["headingDeg"] = Value(pos[2]);
                        if (pos.Count > 3) args["length"] = Value(pos[3]);
                    }
                    else if (pos.Count != 0) { usage = "/de terrain walk <x> <z> [headingDeg] [length]"; return null; }
                    break;
                default:
                    if (pos.Count != 0) { usage = ChatUsage; return null; }
                    break;
            }
            return args;
        }

        /// <summary>pos[first … first + n − 1] as a number list, null when one is not a number.</summary>
        private static object Point(List<string> pos, int first, int n)
        {
            var l = new List<object>(n);
            for (int i = first; i < first + n; i++)
            {
                double v;
                if (i >= pos.Count || !double.TryParse(pos[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
                l.Add(v);
            }
            return l;
        }

        /// <summary>One chat value: number, number list (comma), list of lists (semicolon), bool, else the string.</summary>
        public static object Value(string s)
        {
            s = (s ?? "").Trim();
            if (s.IndexOf(';') >= 0)
            {
                var outer = new List<object>();
                foreach (string part in s.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) outer.Add(Value(part));
                return outer;
            }
            if (s.IndexOf(',') >= 0)
            {
                var l = new List<object>();
                foreach (string part in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    double v;
                    if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return s;
                    l.Add(v);
                }
                return l;
            }
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            string lo = s.ToLowerInvariant();
            if (lo == "on" || lo == "true" || lo == "yes") return true;
            if (lo == "off" || lo == "false" || lo == "no") return false;
            return s;
        }

        /// <summary>A number list of exactly <paramref name="n"/> entries from a C11/chat value, or null.</summary>
        public static double[] Numbers(IDictionary<string, object> args, string key, int n)
        {
            object v;
            if (args == null || !args.TryGetValue(key, out v)) return null;
            return NumbersOf(v, n);
        }

        /// <summary>A number list of <paramref name="n"/> entries (any count when 0); a single number counts as a one-element list when n ≤ 1.</summary>
        public static double[] NumbersOf(object v, int n)
        {
            if ((v is double || v is int || v is long || v is float) && n <= 1) return new[] { Convert.ToDouble(v, CultureInfo.InvariantCulture) };
            var l = v as System.Collections.IList;
            if (l == null || (n > 0 && l.Count != n) || l.Count == 0) return null;
            var r = new double[l.Count];
            for (int i = 0; i < l.Count; i++)
            {
                if (!(l[i] is double || l[i] is float || l[i] is int || l[i] is long)) return null;
                r[i] = Convert.ToDouble(l[i], CultureInfo.InvariantCulture);
            }
            return r;
        }

        /// <summary>A boolean arg: true/false, "on"/"off", 1/0; <paramref name="dflt"/> when absent.</summary>
        public static bool Flag(IDictionary<string, object> args, string key, bool dflt)
        {
            object v;
            if (args == null || !args.TryGetValue(key, out v) || v == null) return dflt;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            string s = v as string;
            if (s != null)
            {
                s = s.Trim().ToLowerInvariant();
                if (s == "on" || s == "true" || s == "1" || s == "yes") return true;
                if (s == "off" || s == "false" || s == "0" || s == "no") return false;
            }
            return dflt;
        }
    }

    /// <summary>One structured S4 row of the plugin JSONL (<c>se-*.jsonl</c>): <c>{"t": iso, "ev": "s4.&lt;kind&gt;", …}</c>.</summary>
    internal static class S4Rows
    {
        /// <summary>Fields as alternating key, value (values: string, bool, numbers, null, lists, dictionaries).</summary>
        public static string Row(string ev, params object[] kv)
        {
            var sb = new StringBuilder(96 + kv.Length * 12);
            sb.Append("{\"t\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":");
            JsonText.AppendString(sb, ev);
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                sb.Append(',');
                JsonText.AppendString(sb, Convert.ToString(kv[i], CultureInfo.InvariantCulture));
                sb.Append(':');
                JsonText.AppendValue(sb, kv[i + 1]);
            }
            sb.Append('}');
            return sb.ToString();
        }

        public static object[] Arr(Vec3 v) { return new object[] { v.X, v.Y, v.Z }; }
    }

    /// <summary>One A-T1 column: an SN (x, z) and the vertical extent of its ray.</summary>
    internal struct RayColumn
    {
        public string Set;
        public int I, J;
        public double X, Z, FromY, ToY;
    }

    /// <summary>The A-T1 ray sets (ROADMAP S4 A-T1 (1)): a size × size grid around an SN origin and N random columns.</summary>
    internal static class RayPlan
    {
        /// <summary>Column (i, j) at <c>origin + ((i, j) − (size − 1)/2) · spacing</c>; i along SN x, j along SN z.</summary>
        public static List<RayColumn> Grid(double ox, double oz, int size, double spacing, double fromY, double toY)
        {
            var l = new List<RayColumn>(size * size);
            double c = (size - 1) / 2.0;
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                    l.Add(new RayColumn { Set = "grid", I = i, J = j, X = ox + (i - c) * spacing, Z = oz + (j - c) * spacing, FromY = fromY, ToY = toY });
            return l;
        }

        /// <summary><paramref name="count"/> columns uniform in [−half, half]² (seeded, reproducible).</summary>
        public static List<RayColumn> Random(int count, int seed, double half, double fromY, double toY)
        {
            var l = new List<RayColumn>(count);
            var r = new Random(seed);
            for (int n = 0; n < count; n++)
                l.Add(new RayColumn { Set = "random", I = n, J = 0, X = (r.NextDouble() * 2 - 1) * half, Z = (r.NextDouble() * 2 - 1) * half, FromY = fromY, ToY = toY });
            return l;
        }

        /// <summary>
 /// The 8 m physics cells (lod 0, <c>local / 8</c>, as <c>MyVoxelPhysicsBody.GetShape(int, Vector3D, out …)</c>)
 /// a segment in map-local metres (from the map's min corner) passes through, in order, sampled every 0.5 m and
 /// clipped to the tile's 128³ cells. Appends to <paramref name="cells"/> (x, y, z triples).
 /// </summary>
        public static void CellsOnSegment(Vec3 from, Vec3 to, List<int> cells)
        {
            Vec3 d = to - from;
            double len = d.Length;
            int steps = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            int lx = int.MinValue, ly = 0, lz = 0;
            int max = SiteV1.Tile / 8 - 1;
            for (int s = 0; s <= steps; s++)
            {
                Vec3 p = from + d * ((double)s / steps);
                if (p.X < 0 || p.Y < 0 || p.Z < 0 || p.X >= SiteV1.Tile || p.Y >= SiteV1.Tile || p.Z >= SiteV1.Tile) continue;
                int cx = Math.Min((int)(p.X / 8), max), cy = Math.Min((int)(p.Y / 8), max), cz = Math.Min((int)(p.Z / 8), max);
                if (cx == lx && cy == ly && cz == lz) continue;
                bool seen = false;
                for (int k = 0; k + 2 < cells.Count; k += 3)
                    if (cells[k] == cx && cells[k + 1] == cy && cells[k + 2] == cz) { seen = true; break; }
                lx = cx; ly = cy; lz = cz;
                if (seen) continue;
                cells.Add(cx); cells.Add(cy); cells.Add(cz);
            }
        }

        /// <summary>The slack of <see cref="HoleCandidates"/>: how far (m, per axis) the true content crossing may lie from SE's mesh point.</summary>
        public const double HoleTolM = 1.5;

        /// <summary>
 /// S4.M3's witness: the 8 m cells whose Intersect box <c>[8c, 8c + 8]</c> (inclusive, as SE
 /// asks it in <c>UpdateAfterSimulation10</c>), grown by <paramref name="tol"/> m on every side, holds the
 /// tile-local surface point <paramref name="local"/> (SE's own mesh hit of the storage). SE's dual-contouring
 /// triangle through that point lies within one voxel of a sign-changing edge whose two samples one of these cells'
 /// boxes contains, so unless Intersect lies at least one of them answers Intersects (never Disjoint/Contains on
 /// non-uniform bytes, S4.N3); every one of them answering Disjoint/Contains is a hole a grid would fall through.
 /// The slack also covers a half-voxel placement error. Appends x, y, z triples (clamped to the tile's 128³ cells).
 /// </summary>
        public static void HoleCandidates(Vec3 local, double tol, List<int> cells)
        {
            int max = SiteV1.Tile / 8 - 1;
            int x0 = Clamp((int)Math.Floor((local.X - tol) / 8), max), x1 = Clamp((int)Math.Floor((local.X + tol) / 8), max);
            int y0 = Clamp((int)Math.Floor((local.Y - tol) / 8), max), y1 = Clamp((int)Math.Floor((local.Y + tol) / 8), max);
            int z0 = Clamp((int)Math.Floor((local.Z - tol) / 8), max), z1 = Clamp((int)Math.Floor((local.Z + tol) / 8), max);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        cells.Add(x); cells.Add(y); cells.Add(z);
                    }
        }

        private static int Clamp(int v, int max) { return v < 0 ? 0 : v > max ? max : v; }

        /// <summary>
 /// Clips the segment <paramref name="from"/>→<paramref name="to"/> to the box [min, min + size]³; false when it
 /// misses. <paramref name="t0"/>/<paramref name="t1"/> are fractions of the segment.
 /// </summary>
        public static bool ClipToBox(Vec3 from, Vec3 to, Vec3 min, double size, out double t0, out double t1)
        {
            t0 = 0; t1 = 1;
            return Slab(from.X, to.X, min.X, size, ref t0, ref t1) && Slab(from.Y, to.Y, min.Y, size, ref t0, ref t1) && Slab(from.Z, to.Z, min.Z, size, ref t0, ref t1);
        }

        private static bool Slab(double f, double t, double lo, double size, ref double t0, ref double t1)
        {
            double d = t - f;
            if (Math.Abs(d) < 1e-12) return f >= lo && f <= lo + size;
            double a = (lo - f) / d, b = (lo + size - f) / d;
            if (a > b) { double x = a; a = b; b = x; }
            if (a > t0) t0 = a;
            if (b < t1) t1 = b;
            return t0 <= t1;
        }
    }

    /// <summary>FNV-1a 64 over byte runs (the content/material hash of <c>de.terrain hash</c>; same constants as <see cref="SiteV1.Fnv1a64"/>).</summary>
    internal static class S4Hash
    {
        public const ulong Offset = SiteV1.FnvOffsetBasis;

        public static ulong Add(ulong h, byte[] data, int count)
        {
            for (int i = 0; i < count; i++)
            {
                h ^= data[i];
                h = unchecked(h * SiteV1.FnvPrime);
            }
            return h;
        }

        public static string Hex(ulong h) { return "0x" + h.ToString("x16", CultureInfo.InvariantCulture); }
    }
}
