using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // that's why I asked you to put ores in the
 // Subnautica map"): ores inside the Subnautica map (the S4 provider tiles, the authentic 2 km terrain around the lifepod).
 // 1. Ore pockets by the planet's depth-rarity table (tools/planet/layout_4546b.json ores.rows, the ring's bands and
 // shares): the SN world is cut into CellM cubes; a cube holds a pocket with the probability of the rows whose band
 // holds its centre's depth (share each, rarest first), a sphere of PocketMin..PocketMax m inside the cube; solid voxels
 // inside take the row's ore (deterministic: splitmix64 of the cube and Seed). SE's drill yields it; the S8a-1 drill
 // layer converts the hole's walls with it (convert.material: the ore's Subnautica type).
 // 2. Pockets under Subnautica's large resource deposits. Their positions are NOT in the install: Subnautica fills its
 // EntitySlots at run time with UnityEngine.Random
 // so they
 // differ per save. Subnautica reports each one it starts (S8aPatches Drillable.Start postfix) into the store
 // (DepositsFile in the world's store folder: one line per deposit); SE reads the file and puts a DepositRadius pocket
 // DepositSink m under each, of the ore TechType maps to (OreOfTechType).
 // =================================================================================================================

 /// <summary>One ore row of the crater table (the planet's ring rows: band of seabed depth, share).</summary>
    public struct S8aCraterOreRow
    {
        public readonly string Subtype;
        public readonly double MinDepthM, MaxDepthM, Share;

        public S8aCraterOreRow(string subtype, double minDepthM, double maxDepthM, double share)
        {
            Subtype = subtype;
            MinDepthM = minDepthM;
            MaxDepthM = maxDepthM;
            Share = share;
        }
    }

    /// <summary>A large resource deposit Subnautica reported (SN world position, its TechType name).</summary>
    public struct S8aCraterDeposit
    {
        public readonly string TechType;
        public readonly double X, Y, Z;
        public readonly int Row;

        public S8aCraterDeposit(string techType, double x, double y, double z)
        {
            TechType = techType;
            X = x;
            Y = y;
            Z = z;
            Row = S8aCraterOres.RowOfTechType(techType);
        }
    }

    public static class S8aCraterOres
    {
        /// <summary>The table, rarest first (the order the cube rule draws in) = layout_4546b.json ores.rows (type, depthM, share).</summary>
        public static readonly S8aCraterOreRow[] Rows =
        {

            // (0.004/0.003/0.010/0.008/0.012/0.012/0.030/0.022/0.022): 0-200 m now 2.4 % of the 8 m cubes (about 8 pockets per hectare,
 // one every 35 m) instead of 7.4 % (24 per hectare). The same numbers in layout_4546b*.json ores.rows (the Wire test compares).
            new S8aCraterOreRow("Platinum_01", 600.0, 3000.0, 0.001),
            new S8aCraterOreRow("Uraninite_01", 600.0, 3000.0, 0.001),
            new S8aCraterOreRow("Silver_01", 300.0, 600.0, 0.003),
            new S8aCraterOreRow("Gold_01", 300.0, 600.0, 0.003),
            new S8aCraterOreRow("Cobalt_01", 100.0, 300.0, 0.004),
            new S8aCraterOreRow("Magnesium_01", 100.0, 300.0, 0.004),
            new S8aCraterOreRow("Iron_02", 0.0, 200.0, 0.010),
            new S8aCraterOreRow("Nickel_01", 0.0, 200.0, 0.007),
            new S8aCraterOreRow("Silicon_01", 0.0, 200.0, 0.007),
        };

        public const double CellM = 8.0;
        public const double PocketMinM = 2.0, PocketMaxM = 3.5;
        public const double DepositRadiusM = 6.0, DepositSinkM = 3.0;
        /// <summary>Only the Subnautica map: |SN x|, |SN z| within this (the crater box, CONTRACT "S4 tiles").</summary>
        public const double MapHalfM = 2048.0;
        public const ulong Seed = 0x4546B0E5C2A7E2D1UL;
        public const string DepositsFile = "crater-deposits.tsv";
        public const string DepositsMagic = "de-s8a-crater-deposits 1";

        /// <summary>&lt;root&gt;\&lt;storeKey x16&gt;\crater-deposits.tsv (the world's store folder).</summary>
        public static string DepositsPath(string root, ulong storeKey) { return Path.Combine(S8aStore.StoreFolder(root, storeKey), DepositsFile); }

        /// <summary>The row index of a subtype, −1 when not in the table.</summary>
        public static int RowOf(string subtype)
        {
            for (int i = 0; i < Rows.Length; i++) if (string.Equals(Rows[i].Subtype, subtype, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>
 /// The SE ore of a Subnautica large deposit (TechType name, "Drillable" + resource): Titanium and Magnetite Iron_02,
 /// Copper and Nickel Nickel_01, Quartz, Salt and AluminiumOxide Silicon_01, Lead and Mercury Cobalt_01, Lithium and
 /// Sulphur Magnesium_01, Silver Silver_01, Gold Gold_01, Diamond and Kyanite Platinum_01, Uranium (DrillableUranium) Uraninite_01;
 /// anything else null.
 /// </summary>
        public static string OreOfTechType(string techType)
        {
            if (string.IsNullOrEmpty(techType)) return null;
            string t = techType.StartsWith("Drillable", StringComparison.Ordinal) ? techType.Substring(9) : techType;
            if (t.EndsWith("_Large", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 6);
            switch (t)
            {
                case "Titanium": case "Magnetite": return "Iron_02";
                case "Copper": case "Nickel": return "Nickel_01";
                case "Quartz": case "Salt": case "AluminiumOxide": return "Silicon_01";
                case "Lead": case "Mercury": return "Cobalt_01";
                case "Lithium": case "Sulphur": return "Magnesium_01";
                case "Silver": return "Silver_01";
                case "Gold": return "Gold_01";
                case "Diamond": case "Kyanite": return "Platinum_01";
                case "Uraninite": case "Uranium": return "Uraninite_01";   // Subnautica names it DrillableUranium
                default: return null;
            }
        }

        public static int RowOfTechType(string techType) { return RowOf(OreOfTechType(techType)); }

        public static bool InMap(double x, double z) { return Math.Abs(x) <= MapHalfM && Math.Abs(z) <= MapHalfM; }

        public static ulong Mix(ulong z)
        {
            unchecked
            {
                z += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        private static double Unit(ulong h) { return (h >> 11) * (1.0 / 9007199254740992.0); }

        public static ulong CellKey(long cx, long cy, long cz)
        {
            unchecked { return Mix(Mix(Mix(Seed ^ (ulong)cx) ^ (ulong)cy) ^ (ulong)cz); }
        }

        /// <summary>
 /// The pocket of the cube holding SN point (x, y, z): its row (−1 none), centre and radius. The cube's centre depth
 /// (−y) picks the rows whose band holds it; u = Unit(key) below their summed share selects one (rarest first).
 /// </summary>
        public static int Pocket(long cx, long cy, long cz, out double px, out double py, out double pz, out double radius)
        {
            ulong k = CellKey(cx, cy, cz);
            double u = Unit(k);
            double depth = -((cy + 0.5) * CellM);
            int row = -1;
            double acc = 0.0;
            for (int i = 0; i < Rows.Length; i++)
            {
                if (depth < Rows[i].MinDepthM || depth > Rows[i].MaxDepthM) continue;
                acc += Rows[i].Share;
                if (u < acc) { row = i; break; }
            }
            radius = PocketMinM + (PocketMaxM - PocketMinM) * Unit(Mix(k ^ 1UL));
            double span = CellM - 2.0 * radius;
            px = cx * CellM + radius + span * Unit(Mix(k ^ 2UL));
            py = cy * CellM + radius + span * Unit(Mix(k ^ 3UL));
            pz = cz * CellM + radius + span * Unit(Mix(k ^ 4UL));
            return row;
        }

        /// <summary>The ore row at SN point (x, y, z) by the cube rule (−1: none, or outside the map).</summary>
        public static int RowAt(double x, double y, double z)
        {
            if (!InMap(x, z)) return -1;
            long cx = (long)Math.Floor(x / CellM), cy = (long)Math.Floor(y / CellM), cz = (long)Math.Floor(z / CellM);
            double px, py, pz, r;
            int row = Pocket(cx, cy, cz, out px, out py, out pz, out r);
            if (row < 0) return -1;
            double dx = x - px, dy = y - py, dz = z - pz;
            return dx * dx + dy * dy + dz * dz <= r * r ? row : -1;
        }

        /// <summary>One line of the deposits file: TechType, x, y, z (SN, 2 decimals, invariant), tab-separated.</summary>
        public static string FormatLine(string techType, double x, double y, double z)
        {
            return techType + "\t" + x.ToString("0.00", CultureInfo.InvariantCulture) + "\t" + y.ToString("0.00", CultureInfo.InvariantCulture) + "\t" +
                   z.ToString("0.00", CultureInfo.InvariantCulture);
        }

        public static bool TryParseLine(string line, out S8aCraterDeposit d)
        {
            d = default(S8aCraterDeposit);
            if (string.IsNullOrEmpty(line) || line[0] == '#' || line.StartsWith("de-s8a", StringComparison.Ordinal)) return false;
            string[] f = line.Split('\t');
            double x, y, z;
            if (f.Length < 4 || !double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            d = new S8aCraterDeposit(f[0].Trim(), x, y, z);
            return true;
        }

        /// <summary>The deposits of a file's text (unknown lines skipped; duplicates within 1 m dropped).</summary>
        public static List<S8aCraterDeposit> Parse(string text)
        {
            var list = new List<S8aCraterDeposit>();
            var seen = new HashSet<long>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string raw in text.Split('\n'))
            {
                S8aCraterDeposit d;
                if (!TryParseLine(raw.TrimEnd('\r'), out d)) continue;
                if (!seen.Add(PosKey(d.X, d.Y, d.Z))) continue;
                list.Add(d);
            }
            return list;
        }

        /// <summary>A key of a position rounded to whole metres (the de-duplication of reports).</summary>
        public static long PosKey(double x, double y, double z)
        {
            unchecked { return (long)CellKey((long)Math.Round(x), (long)Math.Round(y), (long)Math.Round(z)); }
        }
    }

    /// <summary>
 /// The SE side's per-session rule: the SE voxel material index of each table row (255 = not resolved, the row is off)
 /// and the deposit pockets (a snapshot swapped atomically; thread-safe for the provider's readers).
 /// </summary>
    public sealed class S8aCraterOreRule
    {
        private readonly byte[] _rowMaterial;
        private volatile Dictionary<long, S8aCraterDeposit[]> _deposits = new Dictionary<long, S8aCraterDeposit[]>();
        private const double BucketM = 32.0;

        public S8aCraterOreRule(byte[] rowMaterial)
        {
            if (rowMaterial == null || rowMaterial.Length != S8aCraterOres.Rows.Length) throw new ArgumentException("one material index per row expected", "rowMaterial");
            _rowMaterial = (byte[])rowMaterial.Clone();
        }

        public int DepositCount { get; private set; }

        /// <summary>
 /// Planet v2 R8 (<see cref="S8aPlanetRules"/>, crater.cubeRule): the 8 m cube rule on (today) or off (ore only in the pockets under
 /// Subnautica's large deposits). Set before the tiles are read; a change after that reaches only voxels SE reads later.
 /// </summary>
        public bool CubeRule { get { return _cubeRule; } set { if (_cubeRule != value) { _cubeRule = value; Interlocked.Increment(ref _generation); } } }
        private bool _cubeRule = true;

        /// <summary>
 /// Planet v2 R8 (crater.depositsPerKm2): at most this many deposit pockets per 1 km x 1 km cell (<see cref="S8aPlanetRules.CapCellM"/>,
 /// SN x/z floor-divided), kept in the order of <see cref="CapOrder"/>; 0 = every deposit (today). Applies at the next
 /// <see cref="SetDeposits"/>.
 /// </summary>
        public double DepositsPerKm2 { get; set; }

        /// <summary>Deposits dropped by the cap at the last <see cref="SetDeposits"/>.</summary>
        public int Capped { get; private set; }

        /// <summary>The cap's order: a hash of the deposit's rounded position (deterministic, independent of the file's order).</summary>
        public static ulong CapOrder(S8aCraterDeposit d) { return S8aCraterOres.Mix(unchecked((ulong)S8aCraterOres.PosKey(d.X, d.Y, d.Z)) ^ S8aCraterOres.Seed); }

        /// <summary>The deposits the cap keeps (all of them when <paramref name="perKm2"/> &lt;= 0): per cell the first floor(perKm2 x cell area) by <see cref="CapOrder"/>, at least one.</summary>
        public static List<S8aCraterDeposit> Cap(IList<S8aCraterDeposit> deposits, double perKm2)
        {
            var all = new List<S8aCraterDeposit>();
            if (deposits == null) return all;
            foreach (S8aCraterDeposit d in deposits) if (d.Row >= 0 && S8aCraterOres.InMap(d.X, d.Z)) all.Add(d);
            if (perKm2 <= 0) return all;
            double cellKm2 = S8aPlanetRules.CapCellM * S8aPlanetRules.CapCellM / 1e6;
            int perCell = Math.Max(1, (int)Math.Floor(perKm2 * cellKm2 + 1e-9));
            var cells = new Dictionary<long, List<S8aCraterDeposit>>();
            foreach (S8aCraterDeposit d in all)
            {
                long cx = (long)Math.Floor(d.X / S8aPlanetRules.CapCellM), cz = (long)Math.Floor(d.Z / S8aPlanetRules.CapCellM);
                long key = (cx << 32) ^ (cz & 0xFFFFFFFFL);
                List<S8aCraterDeposit> l;
                if (!cells.TryGetValue(key, out l)) { l = new List<S8aCraterDeposit>(); cells[key] = l; }
                l.Add(d);
            }
            var kept = new List<S8aCraterDeposit>();
            foreach (S8aCraterDeposit d in all)
            {
                long cx = (long)Math.Floor(d.X / S8aPlanetRules.CapCellM), cz = (long)Math.Floor(d.Z / S8aPlanetRules.CapCellM);
                List<S8aCraterDeposit> l = cells[(cx << 32) ^ (cz & 0xFFFFFFFFL)];
                if (l.Count <= perCell) { kept.Add(d); continue; }
                ulong mine = CapOrder(d);
                int before = 0;
                foreach (S8aCraterDeposit o in l)
                {
                    ulong k = CapOrder(o);
                    if (k < mine || (k == mine && S8aCraterOres.PosKey(o.X, o.Y, o.Z) < S8aCraterOres.PosKey(d.X, d.Y, d.Z))) before++;
                }
                if (before < perCell) kept.Add(d);
            }
            return kept;
        }

        public byte MaterialOfRow(int row) { return row >= 0 && row < _rowMaterial.Length ? _rowMaterial[row] : (byte)255; }

        private static long Bucket(double x, double z)
        {
            long bx = (long)Math.Floor(x / BucketM), bz = (long)Math.Floor(z / BucketM);
            return (bx << 32) ^ (bz & 0xFFFFFFFFL);
        }

        /// <summary>Replaces the deposit pockets (each indexed in every bucket its sphere reaches), capped per <see cref="DepositsPerKm2"/>.</summary>
        public void SetDeposits(IList<S8aCraterDeposit> deposits)
        {
            var map = new Dictionary<long, List<S8aCraterDeposit>>();
            int n = 0;
            int mapped = 0;
            if (deposits != null) foreach (S8aCraterDeposit d in deposits) if (d.Row >= 0 && S8aCraterOres.InMap(d.X, d.Z)) mapped++;
            Capped = 0;
            if (DepositsPerKm2 > 0)
            {
                deposits = Cap(deposits, DepositsPerKm2);
                Capped = mapped - deposits.Count;
            }
            if (deposits != null)
            {
                foreach (S8aCraterDeposit d in deposits)
                {
                    if (d.Row < 0 || !S8aCraterOres.InMap(d.X, d.Z)) continue;
                    n++;
                    double r = S8aCraterOres.DepositRadiusM;
                    long bx0 = (long)Math.Floor((d.X - r) / BucketM), bx1 = (long)Math.Floor((d.X + r) / BucketM);
                    long bz0 = (long)Math.Floor((d.Z - r) / BucketM), bz1 = (long)Math.Floor((d.Z + r) / BucketM);
                    for (long bx = bx0; bx <= bx1; bx++)
                        for (long bz = bz0; bz <= bz1; bz++)
                        {
                            long key = (bx << 32) ^ (bz & 0xFFFFFFFFL);
                            List<S8aCraterDeposit> l;
                            if (!map.TryGetValue(key, out l)) { l = new List<S8aCraterDeposit>(); map[key] = l; }
                            l.Add(d);
                        }
                }
            }
            var frozen = new Dictionary<long, S8aCraterDeposit[]>(map.Count);
            foreach (KeyValuePair<long, List<S8aCraterDeposit>> kv in map) frozen[kv.Key] = kv.Value.ToArray();
            _deposits = frozen;
            Interlocked.Increment(ref _generation);
            var kept = new List<S8aCraterDeposit>(n);
            if (deposits != null) foreach (S8aCraterDeposit d in deposits) if (d.Row >= 0 && S8aCraterOres.InMap(d.X, d.Z)) kept.Add(d);
            Deposits = kept.AsReadOnly();
            DepositCount = n;
        }

        /// <summary>The deposits of the last <see cref="SetDeposits"/> that carry an ore (the mapped ones inside the map).</summary>
        public IList<S8aCraterDeposit> Deposits { get; private set; } = new List<S8aCraterDeposit>().AsReadOnly();

        // ------------------------------------------------------------------------------------------- the cover rule
 // add all ores UNDER the SN original voxels"): a vein voxel
 // keeps at least MinCoverM of the real map's rock on every side (the Subnautica map's own field, not SE's heightmap), so
 // nothing of a vein pokes through the map's surface before drilling; the map shows where a vein lies through the ore
 // overlay (S8aOreOverlay: the surface voxels within S8aOreOverlay.ShowRadiusM of a vein take its Subnautica type).

 /// <summary>The cover every vein voxel keeps under the real map's surface (metres).</summary>
        public const double DefaultMinCoverM = 2.0;
        /// <summary>The deep-pocket cache's size bound (entries; cleared whole when reached).</summary>
        public const int DeepCacheMax = 65536;

        private static readonly double[] CoverDirs = BuildCoverDirs();
        private Func<double, double, double, bool> _solidAt;
        private Func<double, double, double, double, double, double, bool> _allSolidIn;
        private double _minCoverM = DefaultMinCoverM;
        private readonly ConcurrentDictionary<ulong, bool> _deep = new ConcurrentDictionary<ulong, bool>();
        private long _coverDropped;
        private int _generation;

        /// <summary>The probes as multiples of the cover: 26 unit directions (the 3 x 3 x 3 neighbourhood), then the 6 axes at half length.</summary>
        private static double[] BuildCoverDirs()
        {
            var l = new List<double>(32 * 3);
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        double n = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        l.Add(dx / n); l.Add(dy / n); l.Add(dz / n);
                    }
            int[] ax = { 1, 0, 0, -1, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 1, 0, 0, -1 };
            for (int i = 0; i < ax.Length; i++) l.Add(ax[i] * 0.5);
            return l.ToArray();
        }

        /// <summary>
 /// The real map's solid test at an SN point (SE: the Subnautica field's D &gt; 0, the install's octrees); null = no cover
 /// rule.
 /// </summary>
        public Func<double, double, double, bool> SolidAt { get { return _solidAt; } set { _solidAt = value; _deep.Clear(); Interlocked.Increment(ref _generation); } }

        /// <summary>
 /// Conservative: true only when every point of the SN box (lo x, y, z, hi x, y, z) is solid in the real map (SE: SnField.ClassifySn
 /// is Full). A pocket whose sphere grown by the cover is all solid needs no probe (cached per pocket); null = probes always.
 /// </summary>
        public Func<double, double, double, double, double, double, bool> AllSolidIn { get { return _allSolidIn; } set { _allSolidIn = value; _deep.Clear(); } }

        /// <summary>The cover (metres; 0 = none).</summary>
        public double MinCoverM { get { return _minCoverM; } set { _minCoverM = Math.Max(0.0, value); _deep.Clear(); Interlocked.Increment(ref _generation); } }

        /// <summary>Pocket voxels the cover rule dropped (since start).</summary>
        public long CoverDropped { get { return Interlocked.Read(ref _coverDropped); } }

        /// <summary>Bumped by every change of what the rule answers (deposits, cube rule, cover): the overlay recomputes its batches.</summary>
        public int Generation { get { return Volatile.Read(ref _generation); } }

        /// <summary>
 /// True when SN point (x, y, z) of the pocket (key, centre c, radius r) keeps the cover: the pocket's sphere grown by the cover
 /// is all solid (<see cref="AllSolidIn"/>, cached), else every probe at the cover's distance (26 directions) and at half of
 /// it (the 6 axes) is solid in the real map. No <see cref="SolidAt"/> or no cover: true.
 /// </summary>
        public bool Covered(double x, double y, double z, ulong pocketKey, double cx, double cy, double cz, double r)
        {
            Func<double, double, double, bool> solid = _solidAt;
            double c = _minCoverM;
            if (solid == null || c <= 0.0) return true;
            Func<double, double, double, double, double, double, bool> all = _allSolidIn;
            if (all != null)
            {
                bool deep;
                if (!_deep.TryGetValue(pocketKey, out deep))
                {
                    double e = r + c + 1.0;
                    deep = all(cx - e, cy - e, cz - e, cx + e, cy + e, cz + e);
                    if (_deep.Count >= DeepCacheMax) _deep.Clear();
                    _deep[pocketKey] = deep;
                }
                if (deep) return true;
            }
            double[] d = CoverDirs;
            for (int i = 0; i < d.Length; i += 3)
                if (!solid(x + d[i] * c, y + d[i + 1] * c, z + d[i + 2] * c)) return false;
            return true;
        }

        private int Drop() { Interlocked.Increment(ref _coverDropped); return -1; }

        /// <summary>The deposit pocket's cache key (its rounded position, salted apart from the cubes' keys).</summary>
        public static ulong DepositKey(S8aCraterDeposit d) { return S8aCraterOres.Mix(unchecked((ulong)S8aCraterOres.PosKey(d.X, d.Y, d.Z)) ^ 0xD3B05E7A11CE0001UL); }

        /// <summary>The row at SN point (x, y, z): a deposit pocket first, then the cube rule, each under the cover rule; −1 none.</summary>
        public int RowAt(double x, double y, double z)
        {
            if (!S8aCraterOres.InMap(x, z)) return -1;
            S8aCraterDeposit[] near;
            if (_deposits.TryGetValue(Bucket(x, z), out near))
            {
                double r2 = S8aCraterOres.DepositRadiusM * S8aCraterOres.DepositRadiusM;
                foreach (S8aCraterDeposit d in near)
                {
                    double cy = d.Y - S8aCraterOres.DepositSinkM;
                    double dx = x - d.X, dy = y - cy, dz = z - d.Z;
                    if (dx * dx + dy * dy + dz * dz <= r2 && _rowMaterial[d.Row] != 255)
                        return Covered(x, y, z, DepositKey(d), d.X, cy, d.Z, S8aCraterOres.DepositRadiusM) ? d.Row : Drop();
                }
            }
            if (!_cubeRule) return -1;
            long kx = (long)Math.Floor(x / S8aCraterOres.CellM), ky = (long)Math.Floor(y / S8aCraterOres.CellM), kz = (long)Math.Floor(z / S8aCraterOres.CellM);
            double px, py, pz, pr;
            int row = S8aCraterOres.Pocket(kx, ky, kz, out px, out py, out pz, out pr);
            if (row < 0 || _rowMaterial[row] == 255) return -1;
            double ex = x - px, ey = y - py, ez = z - pz;
            if (ex * ex + ey * ey + ez * ez > pr * pr) return -1;
            return Covered(x, y, z, S8aCraterOres.CellKey(kx, ky, kz), px, py, pz, pr) ? row : Drop();
        }

        /// <summary>
 /// The rows of the SN voxels of a box (world voxel indices <paramref name="vx0"/>.. with sizes <paramref name="sx"/>..; voxel v's
 /// point = Origin + v + <paramref name="offset"/>) into <paramref name="rows"/> (index x + sx·(y + sy·z); 255 = none, else the row).
 /// Only the pockets that meet the box are visited (the cube rule's cubes, the deposits' buckets); <see cref="RowAt"/> decides
 /// each of their voxels, so the answer equals RowAt everywhere. Returns the ore voxels.
 /// </summary>
        public int Rows(int vx0, int vy0, int vz0, int sx, int sy, int sz, double offset, byte[] rows)
        {
            if (sx <= 0 || sy <= 0 || sz <= 0) return 0;
            int n = sx * sy * sz;
            if (rows == null || rows.Length < n) throw new ArgumentException("rows too small", "rows");
            for (int i = 0; i < n; i++) rows[i] = 255;
            double x0 = SnWorld.OriginX + vx0 + offset, y0 = SnWorld.OriginY + vy0 + offset, z0 = SnWorld.OriginZ + vz0 + offset;
            double x1 = x0 + sx - 1, y1 = y0 + sy - 1, z1 = z0 + sz - 1;
            if (x1 < -S8aCraterOres.MapHalfM || x0 > S8aCraterOres.MapHalfM || z1 < -S8aCraterOres.MapHalfM || z0 > S8aCraterOres.MapHalfM) return 0;
            int count = 0;
            Dictionary<long, S8aCraterDeposit[]> deps = _deposits;
            if (deps.Count > 0)
            {
                var seen = new HashSet<long>();
                double rr = S8aCraterOres.DepositRadiusM;
                long b0x = (long)Math.Floor((x0 - rr) / BucketM), b1x = (long)Math.Floor((x1 + rr) / BucketM);
                long b0z = (long)Math.Floor((z0 - rr) / BucketM), b1z = (long)Math.Floor((z1 + rr) / BucketM);
                for (long bx = b0x; bx <= b1x; bx++)
                    for (long bz = b0z; bz <= b1z; bz++)
                    {
                        S8aCraterDeposit[] l;
                        if (!deps.TryGetValue((bx << 32) ^ (bz & 0xFFFFFFFFL), out l)) continue;
                        foreach (S8aCraterDeposit d in l)
                        {
                            if (!seen.Add(S8aCraterOres.PosKey(d.X, d.Y, d.Z))) continue;
                            count += Visit(d.X, d.Y - S8aCraterOres.DepositSinkM, d.Z, rr, x0, y0, z0, sx, sy, sz, rows);
                        }
                    }
            }
            if (_cubeRule)
            {
                double cm = S8aCraterOres.CellM;
                long c0x = (long)Math.Floor(x0 / cm) - 1, c1x = (long)Math.Floor(x1 / cm) + 1;
                long c0y = (long)Math.Floor(y0 / cm) - 1, c1y = (long)Math.Floor(y1 / cm) + 1;
                long c0z = (long)Math.Floor(z0 / cm) - 1, c1z = (long)Math.Floor(z1 / cm) + 1;
                for (long cz = c0z; cz <= c1z; cz++)
                    for (long cy = c0y; cy <= c1y; cy++)
                        for (long cx = c0x; cx <= c1x; cx++)
                        {
                            double px, py, pz, pr;
                            int row = S8aCraterOres.Pocket(cx, cy, cz, out px, out py, out pz, out pr);
                            if (row < 0 || _rowMaterial[row] == 255) continue;
                            count += Visit(px, py, pz, pr, x0, y0, z0, sx, sy, sz, rows);
                        }
            }
            return count;
        }

        /// <summary>RowAt for every voxel of the box inside the sphere (c, r) not decided yet; returns the new ore voxels.</summary>
        private int Visit(double cx, double cy, double cz, double r, double x0, double y0, double z0, int sx, int sy, int sz, byte[] rows)
        {
            int ix0 = Math.Max(0, (int)Math.Ceiling(cx - r - x0)), ix1 = Math.Min(sx - 1, (int)Math.Floor(cx + r - x0));
            int iy0 = Math.Max(0, (int)Math.Ceiling(cy - r - y0)), iy1 = Math.Min(sy - 1, (int)Math.Floor(cy + r - y0));
            int iz0 = Math.Max(0, (int)Math.Ceiling(cz - r - z0)), iz1 = Math.Min(sz - 1, (int)Math.Floor(cz + r - z0));
            int got = 0;
            double r2 = r * r;
            for (int iz = iz0; iz <= iz1; iz++)
                for (int iy = iy0; iy <= iy1; iy++)
                    for (int ix = ix0; ix <= ix1; ix++)
                    {
                        double x = x0 + ix, y = y0 + iy, z = z0 + iz;
                        double dx = x - cx, dy = y - cy, dz = z - cz;
                        if (dx * dx + dy * dy + dz * dz > r2) continue;
                        int i = ix + sx * (iy + sy * iz);
                        if (rows[i] != 255) continue;
                        int row = RowAt(x, y, z);
                        if (row < 0) continue;
                        rows[i] = (byte)row;
                        got++;
                    }
            return got;
        }

        /// <summary>The SE material index at SN point (x, y, z), 255 = no ore there.</summary>
        public byte MaterialAt(double x, double y, double z)
        {
            int row = RowAt(x, y, z);
            return row < 0 ? (byte)255 : _rowMaterial[row];
        }
    }
}
