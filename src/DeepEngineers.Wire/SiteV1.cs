using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// The frozen dive-site record, site version 1 (docs/CONTRACT.md section 3). Every constant is copied from
 /// tools/worldmap/site_v1.py (the single source) and checked against it by tests/DeepEngineers.WireMath.Tests.
 /// Runtime values (planet centre C, sea radius Rs) are not constants: the SE plugin reads them and validates Rs
 /// against <see cref="RsExpectedBits"/> bit for bit.
 /// </summary>
    public static class SiteV1
    {
        public const int SiteVersion = 1;

        /// <summary>
 /// FNV-1a 64 over the UTF-8 bytes of <c>json.dumps(site_v1.record, sort_keys=True, separators=(",", ":"))</c>
 /// (CONTRACT section 3 and section 6; carried in C5). Recomputed by <see cref="ComputeSiteHash"/>.
 /// </summary>
        public const ulong SiteHash = 0x53ab8e77d68103abUL;

        /// <summary>Length in bytes of the canonical record text that <see cref="SiteHash"/> covers.</summary>
        public const int SiteRecordBytes = 1506;

        // ---------------------------------------------------------------- planet (PLANET in site_v1.py)
        public const string GeneratorSubtypeId = "DE_4546B";
        public const double StorageRadius = 60000.0;            // R = f32(60000.0)
        public const double HillMin = -0.05;
        public const double HillMax = 0.12;
        public const double SurfaceGravity = 1.0;
        public const double GravityFalloffPower = 7.0;
        public const bool AtmosphereBreathable = true;
        public const double AtmosphereDensity = 1.0;
        public const double AtmosphereOxygenDensity = 0.9;
        public const double AtmosphereLimitAltitude = 2.0;
        public const int AtmosphereMaxWindSpeed = 80;
        public const string Heightmaps = "6 faces, 16-bit, all zero (own files); no SurfaceDetail, no EnvironmentItems, no weather generators";

        /// <summary>MyPlanet.MinimumRadius = f32(R + f32(f32(HillMin) * R)) (site_v1.INNER).</summary>
        public const double MinimumRadius = 57000.0;
        /// <summary>MyPlanet.MaximumRadius = f32(R + f32(f32(HillMax) * R)) (site_v1.OUTER).</summary>
        public const double MaximumRadius = 67200.0;
        /// <summary>AverageRadius = R.</summary>
        public const double AverageRadius = 60000.0;
        /// <summary>MyPlanet.AtmosphereAltitude = MAX_HILL * LimitAltitude (site_v1.ATMO_ALT).</summary>
        public const double AtmosphereAltitude = 14400.0;

        // ---------------------------------------------------------------- Water Mod (WATER in site_v1.py)
 /// <summary>Water Mod <c>WaterSettings.Radius</c> = f32(60000/57000) = 1.0526316f.</summary>
        public const double WaterSettingsRadius = 1.0526316165924072;
        public const uint WaterSettingsRadiusBits = 0x3F86BCA2u;
        public const double WaveHeight = 0.0;
        public const double TideHeight = 0.0;
        public const bool Volumetric = false;
        public const double CrushDamage = 0.0;
        public const double CurrentSpeed = 0.0;
        public const bool EnableFish = false;
        /// <summary>Expected sea radius Rs = f32(MinimumRadius * WaterSettingsRadius) = 60000.00390625 m.</summary>
        public const double RsExpected = 60000.00390625;
        public const float RsExpectedFloat = 60000.00390625f;
        /// <summary>IEEE-754 bits of <see cref="RsExpectedFloat"/>.</summary>
        public const uint RsExpectedBits = 0x476A6001u;
        public const string RsSource = "WaterModAPI.GetPhysical(planet).Item2";

        // ---------------------------------------------------------------- anchor (ANCHOR in site_v1.py)
        public static readonly Vec3 AnchorU0 = new Vec3(0.0, 0.0, 1.0);
        public static readonly Vec3 AnchorN0 = new Vec3(0.0, -1.0, 0.0);
        public static readonly Vec3 AnchorE0 = new Vec3(-1.0, 0.0, 0.0);
        public const double AnchorPsiDeg = 0.0;
        public static readonly Vec3 AnchorO = new Vec3(0.0, 0.0, 0.0);
        public static readonly Vec3 BaseSunDirection = new Vec3(0.0, 0.0, 1.0);
        public const int SunRotationIntervalMinutes = 20;

        /// <summary>Planet centre used by tests, vector files and the in-game subsets (site_v1.TEST_C).</summary>
        public static readonly Vec3 TestC = new Vec3(-1000000.0, 250000.0, 1500000.0);

        // ---------------------------------------------------------------- Subnautica data envelope (SN in site_v1.py)
        public const double SnDataYmin = -1920.0;
        public const double SnDataYmax = 160.0;
        public const double SnWorldYmin = -3040.0;
        public const double SnTileHalf = 2048.0;
        public const int SnManifestBuildId = 20241558;
        public const int SnManifestFiles = 5416;
        public const long SnManifestBytes = 1203085204L;
        public const int SnBatchRowFirst = 7;
        public const int SnBatchRowLast = 19;

        // ---------------------------------------------------------------- depth rules (DEPTH in site_v1.py)
        public const double AbyssLimitSnY = -2000.0;
        /// <summary>SE's floor in SN y: MinimumRadius - RsExpected = -3000.00390625.</summary>
        public const double PlanetFloorSnY = -3000.00390625;

        // ---------------------------------------------------------------- terrain tiles (TILE, PITCH,... in site_v1.py)
        public const string TerrainRepresentation = "provider-backed MyVoxelMap tiles, plug (no fill), missing batch file = empty";
        public const int Tile = 1024;
        public const int Pitch = 1020;
        public const int LateralMin = -2042;
        public const int NLateral = 4;
        public const int RadialMin = 58064;
        public const int NRadial = 3;
        public const int InteriorMargin = 2;
        public const bool TerrainSaved = false;
        public const bool UseVoxelOffset = false;
        public const string TerrainEdits = "blocked (v1)";

        /// <summary>Min corner of tile (i, j, k) relative to C, in SE world axes (site_v1.tile_min).</summary>
        public static Vec3 TileMinRelative(int i, int j, int k)
        {
            return new Vec3(LateralMin + i * Pitch, LateralMin + j * Pitch, RadialMin + k * Pitch);
        }

        /// <summary>
 /// Deterministic SE entity id of tile (i, j, k) (CONTRACT section 3, terrain.entityId):
 /// <c>MyEntityIdentifier.ConstructId(ENTITY, 0x44455400000000 | siteVersion &lt;&lt; 16 | k &lt;&lt; 8 | j &lt;&lt; 4 | i)</c>
 /// = 0x0144455400010000 + 0x100 k + 0x10 j + i.
 /// </summary>
        public static long TileEntityId(int i, int j, int k)
        {
            if (i < 0 || i >= NLateral || j < 0 || j >= NLateral || k < 0 || k >= NRadial)
                throw new ArgumentOutOfRangeException("i", "tile slot outside the 4 x 4 x 3 lattice");
            const long entityType = 1L;   // VRage MyEntityIdentifier.ID_OBJECT_TYPE.ENTITY
            long unique = 0x44455400000000L | ((long)SiteVersion << 16) | ((long)k << 8) | ((long)j << 4) | (long)i;
            return (unique & 0xFFFFFFFFFFFFFFL) | (entityType << 56);
        }

        /// <summary>
 /// The 34 tiles created (CONTRACT section 3, terrain.tilesCreated; derived from the octree census, not a field of
 /// the site_v1.py record): all 16 of row 0, all 16 of row 1, row 2 only (1, 0) and (1, 1). Triples (i, j, k).
 /// </summary>
        public static int[][] TilesCreated()
        {
            var list = new List<int[]>(34);
            for (int k = 0; k < 2; k++)
                for (int j = 0; j < NLateral; j++)
                    for (int i = 0; i < NLateral; i++)
                        list.Add(new[] { i, j, k });
            list.Add(new[] { 1, 0, 2 });
            list.Add(new[] { 1, 1, 2 });
            return list.ToArray();
        }

        /// <summary>True when a runtime sea radius equals <see cref="RsExpectedFloat"/> bit for bit (CONTRACT section 3 validation).</summary>
        public static bool IsRsExact(float rs)
        {
            return FloatBits(rs) == RsExpectedBits;
        }

        public static uint FloatBits(float f)
        {
            return unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(f), 0));
        }

        // ---------------------------------------------------------------- siteHash
        public const ulong FnvOffsetBasis = 0xcbf29ce484222325UL;
        public const ulong FnvPrime = 0x100000001b3UL;

        /// <summary>FNV-1a 64 over bytes (the CONTRACT's hash for siteHash and the layout hash).</summary>
        public static ulong Fnv1a64(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            ulong h = FnvOffsetBasis;
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h = unchecked(h * FnvPrime);
            }
            return h;
        }

        /// <summary>
 /// The canonical record text, byte for byte what
 /// <c>json.dumps(site_v1.record, sort_keys=True, separators=(",", ":"))</c> prints: keys sorted by code point,
 /// no spaces, Python float repr (shortest round trip, ".0" on integral values), Python ints without a fraction.
 /// Built from the constants above, so a constant edited here without site_v1.py changes the hash.
 /// </summary>
        public static string CanonicalRecordJson()
        {
            var planet = Obj(
                "generatorSubtypeId", GeneratorSubtypeId,
                "storageRadius_R", StorageRadius,
                "hillMin", HillMin,
                "hillMax", HillMax,
                "surfaceGravity", SurfaceGravity,
                "gravityFalloffPower", GravityFalloffPower,
                "atmosphere", Obj(
                    "breathable", AtmosphereBreathable,
                    "density", AtmosphereDensity,
                    "oxygenDensity", AtmosphereOxygenDensity,
                    "limitAltitude", AtmosphereLimitAltitude,
                    "maxWindSpeed", (long)AtmosphereMaxWindSpeed),
                "heightmaps", Heightmaps,
                "minimumRadius", MinimumRadius,
                "maximumRadius", MaximumRadius,
                "averageRadius", AverageRadius,
                "atmosphereAltitude", AtmosphereAltitude);
            var water = Obj(
                "settingsRadius", WaterSettingsRadius,
                "waveHeight", WaveHeight,
                "tideHeight", TideHeight,
                "volumetric", Volumetric,
                "crushDamage", CrushDamage,
                "currentSpeed", CurrentSpeed,
                "enableFish", EnableFish,
                "RsExpectedFloat32", RsExpected,
                "RsSource", RsSource);
            var anchor = Obj(
                "U0", Arr(AnchorU0),
                "N0", Arr(AnchorN0),
                "E0", Arr(AnchorE0),
                "psi_deg", AnchorPsiDeg,
                "O", Arr(AnchorO),
                "baseSunDirection", Arr(BaseSunDirection),
                "sunRotationIntervalMinutes", (long)SunRotationIntervalMinutes);
            var depth = Obj(
                "abyssLimitSnY", AbyssLimitSnY,
                "planetFloorSnY", PlanetFloorSnY);
            var terrain = Obj(
                "representation", TerrainRepresentation,
                "tile", (long)Tile,
                "pitch", (long)Pitch,
                "lateralMin", (long)LateralMin,
                "nLateral", (long)NLateral,
                "radialMin", (long)RadialMin,
                "nRadial", (long)NRadial,
                "interiorMargin", (long)InteriorMargin,
                "saved", TerrainSaved,
                "useVoxelOffset", UseVoxelOffset,
                "edits", TerrainEdits);
            var snSource = Obj(
                "dataYmin", SnDataYmin,
                "dataYmax", SnDataYmax,
                "worldYmin", SnWorldYmin,
                "tileHalf", SnTileHalf,
                "manifest", Obj(
                    "buildid", (long)SnManifestBuildId,
                    "files", (long)SnManifestFiles,
                    "bytes", SnManifestBytes,
                    "batchRowsWithFiles", new object[] { (long)SnBatchRowFirst, (long)SnBatchRowLast }));
            // "C": "runtime...; tests use " + repr(TEST_C), a Python tuple repr of floats
            string cText = "runtime: planet.PositionComp.GetPosition(); tests use ("
                + PyFloatRepr(TestC.X) + ", " + PyFloatRepr(TestC.Y) + ", " + PyFloatRepr(TestC.Z) + ")";
            var record = Obj(
                "siteVersion", (long)SiteVersion,
                "planet", planet,
                "water", water,
                "anchor", anchor,
                "C", cText,
                "depth", depth,
                "terrain", terrain,
                "snSource", snSource);
            var sb = new StringBuilder(2048);
            WriteJson(sb, record);
            return sb.ToString();
        }

        /// <summary>FNV-1a 64 of the UTF-8 bytes of <see cref="CanonicalRecordJson"/>; equals <see cref="SiteHash"/>.</summary>
        public static ulong ComputeSiteHash()
        {
            return Fnv1a64(Encoding.UTF8.GetBytes(CanonicalRecordJson()));
        }

        /// <summary>Hex text of a hash as logs print it: 0x + 16 lower-case digits.</summary>
        public static string HashHex(ulong h)
        {
            return "0x" + h.ToString("x16", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- canonical JSON (Python json.dumps emulation)
        private static SortedDictionary<string, object> Obj(params object[] kv)
        {
            var d = new SortedDictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < kv.Length; i += 2) d.Add((string)kv[i], kv[i + 1]);
            return d;
        }

        private static object[] Arr(Vec3 v) { return new object[] { v.X, v.Y, v.Z }; }

        private static void WriteJson(StringBuilder sb, object value)
        {
            var dict = value as SortedDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteJson(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var arr = value as object[];
            if (arr != null)
            {
                sb.Append('[');
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteJson(sb, arr[i]);
                }
                sb.Append(']');
                return;
            }
            var s = value as string;
            if (s != null) { WriteString(sb, s); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is long) { sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double) { sb.Append(PyFloatRepr((double)value)); return; }
            throw new InvalidOperationException("unsupported JSON value " + (value == null ? "null" : value.GetType().Name));
        }

        /// <summary>Python json string encoding with ensure_ascii=True.</summary>
        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (ch < 0x20 || ch > 0x7E) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>
 /// Python's <c>repr(float)</c> as json.dumps writes it (NaN, Infinity, -Infinity for non-finite values); see
 /// <see cref="PyFloat.Repr"/>.
 /// </summary>
        public static string PyFloatRepr(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            return PyFloat.Repr(d);
        }
    }

    /// <summary>
 /// Exact Python float text conversions, independent of the runtime's own number formatting and parsing (.NET
 /// Framework 4.8's <c>double.Parse</c> is not correctly rounded for some 17-digit inputs: "0.490214275155925"
 /// reads as 0x3FDF5FABB1F4EE47 instead of...EE46, measured on this PC). Used for the canonical site record and by
 /// the tests to read the oracle's JSON; never on a game hot path.
 /// </summary>
    public static class PyFloat
    {
        private static readonly BigInteger TwoTo52 = BigInteger.One << 52;
        private static readonly BigInteger TwoTo53 = BigInteger.One << 53;
        private static readonly double[] Pow10 =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19,
            1e20, 1e21, 1e22,
        };

        /// <summary>
 /// Decimal text (optional sign, digits, optional fraction, optional exponent; or nan/inf/infinity) to the nearest
 /// double, ties to even, as Python's <c>float(str)</c> and IEEE-754 require.
 /// </summary>
        public static double Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            string s = text.Trim();
            int i = 0;
            bool neg = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) { neg = s[i] == '-'; i++; }
            string rest = s.Substring(i).ToLowerInvariant();
            if (rest == "nan") return double.NaN;
            if (rest == "inf" || rest == "infinity") return neg ? double.NegativeInfinity : double.PositiveInfinity;

            ulong small = 0;           // digits while they fit (fast path)
            int smallDigits = 0;
            var sbDigits = new StringBuilder(32);
            int exp10 = 0;
            bool seenDot = false, any = false;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9')
                {
                    any = true;
                    if (sbDigits.Length > 0 || c != '0') sbDigits.Append(c);   // drop leading zeros
                    if (seenDot) exp10--;
                    continue;
                }
                if (c == '.' && !seenDot) { seenDot = true; continue; }
                if ((c == 'e' || c == 'E') && any)
                {
                    exp10 += int.Parse(s.Substring(i + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    i = s.Length;
                    break;
                }
                throw new FormatException("not a decimal number: '" + text + "'");
            }
            if (!any) throw new FormatException("not a decimal number: '" + text + "'");
            string digits = sbDigits.ToString();
            if (digits.Length == 0) return neg ? -0.0 : 0.0;

            double v;
            if (digits.Length <= 19)
            {
                small = ulong.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
                smallDigits = digits.Length;
            }
            if (smallDigits > 0 && small <= (1UL << 53) && exp10 >= -22 && exp10 <= 22)
            {
                // Clinger's fast path: both operands exact, one correctly rounded IEEE operation (SSE2, no x87)
                v = exp10 >= 0 ? small * Pow10[exp10] : small / Pow10[-exp10];
            }
            else
            {
                v = FromDecimal(BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture), exp10);
            }
            return neg ? -v : v;
        }

        private static int BitLength(BigInteger x)
        {
            byte[] b = x.ToByteArray();   // little-endian two's complement; x > 0
            int n = b.Length - 1;
            while (n > 0 && b[n] == 0) n--;
            int top = b[n], bits = 0;
            while (top != 0) { bits++; top >>= 1; }
            return n * 8 + bits;
        }

        /// <summary>The double nearest to d * 10^e10 (d &gt; 0), ties to even, with subnormals and overflow.</summary>
        private static double FromDecimal(BigInteger d, int e10)
        {
            int nDigits = d.ToString(CultureInfo.InvariantCulture).Length;
            if (e10 + nDigits > 310) return double.PositiveInfinity;
            if (e10 + nDigits < -330) return 0.0;
            BigInteger num = d, den = BigInteger.One;
            if (e10 >= 0) num *= BigInteger.Pow(10, e10);
            else den = BigInteger.Pow(10, -e10);
            int k = BitLength(num) - BitLength(den) - 53;   // value = q * 2^k with 2^52 <= q < 2^53
            for (int guard = 0; guard < 8; guard++)
            {
                if (k < -1074) k = -1074;                      // below the normal range: subnormal grid
                BigInteger n2 = num, d2 = den;
                if (k >= 0) d2 = den << k; else n2 = num << -k;
                BigInteger r;
                BigInteger q = BigInteger.DivRem(n2, d2, out r);
                if (q >= TwoTo53) { k++; continue; }
                if (q < TwoTo52 && k > -1074) { k--; continue; }
                int c = (r << 1).CompareTo(d2);
                if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
                if (q == TwoTo53) { q = TwoTo52; k++; }
                if (k > 971) return double.PositiveInfinity;
                // normal: bits = (k + 1075) << 52 | (q - 2^52); subnormal (k = -1074, q < 2^52): bits = q; both agree at the boundary
                long bits = q < TwoTo52 ? (long)q : ((long)(k + 1075) << 52) + ((long)q - (1L << 52));
                return BitConverter.Int64BitsToDouble(bits);
            }
            throw new InvalidOperationException("decimal conversion did not converge");
        }

        /// <summary>
 /// Python's <c>repr(float)</c> for a finite double: the shortest digit string that reads back to the same double
 /// (the nearest such string), in fixed notation when the decimal point position is in (-4, 16], else as
 /// d.ddde+XX; integral values get ".0"; -0.0 keeps its sign.
 /// </summary>
        public static string Repr(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentOutOfRangeException("d", "finite values only");
            if (d == 0.0) return BitConverter.DoubleToInt64Bits(d) < 0 ? "-0.0" : "0.0";
            double a = Math.Abs(d);

            // exact decimal expansion of a = m * 2^e
            long bitsA = BitConverter.DoubleToInt64Bits(a);
            int ef = (int)((bitsA >> 52) & 0x7FF);
            long frac = bitsA & ((1L << 52) - 1);
            BigInteger m = ef == 0 ? new BigInteger(frac) : new BigInteger(frac | (1L << 52));
            int e = ef == 0 ? -1074 : ef - 1075;
            string exact;
            int decpt;   // a = 0.DIGITS x 10^decpt
            if (e >= 0)
            {
                exact = (m << e).ToString(CultureInfo.InvariantCulture);
                decpt = exact.Length;
            }
            else
            {
                exact = (m * BigInteger.Pow(5, -e)).ToString(CultureInfo.InvariantCulture);
                decpt = exact.Length + e;
            }
            exact = exact.TrimEnd('0');

            string digits = null;
            int dp = decpt;
            for (int p = 1; p <= 17 && digits == null; p++)
            {
                if (p >= exact.Length) { digits = exact; dp = decpt; break; }
                string head = exact.Substring(0, p);
                string tail = exact.Substring(p);
                bool tie = tail[0] == '5' && tail.TrimEnd('0').Length == 1;
                bool up = tail[0] > '5' || (tail[0] == '5' && !tie) || (tie && ((head[head.Length - 1] - '0') & 1) == 1);
                string first, second;
                int dpFirst, dpSecond;
                RoundedCandidate(head, decpt, up, out first, out dpFirst);
                RoundedCandidate(head, decpt, !up, out second, out dpSecond);
                if (Parse(Sci(first, dpFirst)) == a) { digits = first; dp = dpFirst; }
                else if (Parse(Sci(second, dpSecond)) == a) { digits = second; dp = dpSecond; }
            }
            if (digits == null) { digits = exact.Substring(0, Math.Min(17, exact.Length)); dp = decpt; }
            digits = digits.TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            return Format(d < 0, digits, dp);
        }

        /// <summary>head (p digits) truncated or rounded up by one unit in the last place; carries can lengthen it.</summary>
        private static void RoundedCandidate(string head, int decpt, bool up, out string digits, out int dp)
        {
            if (!up) { digits = head; dp = decpt; return; }
            BigInteger h = BigInteger.Parse(head, NumberStyles.None, CultureInfo.InvariantCulture) + 1;
            string t = h.ToString(CultureInfo.InvariantCulture);
            if (t.Length > head.Length) { digits = t.Substring(0, head.Length); dp = decpt + 1; }
            else { digits = t; dp = decpt; }
        }

        private static string Sci(string digits, int decpt)
        {
            return "0." + digits + "e" + decpt.ToString(CultureInfo.InvariantCulture);
        }

        private static string Format(bool negative, string digits, int decpt)
        {
            var sb = new StringBuilder(32);
            if (negative) sb.Append('-');
            if (decpt > -4 && decpt <= 16)
            {
                if (decpt <= 0)
                {
                    sb.Append("0.").Append('0', -decpt).Append(digits);
                }
                else if (decpt >= digits.Length)
                {
                    sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
                }
                else
                {
                    sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
                }
            }
            else
            {
                sb.Append(digits[0]);
                if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
                int x = decpt - 1;
                sb.Append('e').Append(x < 0 ? '-' : '+');
                int ax = Math.Abs(x);
                if (ax < 10) sb.Append('0');
                sb.Append(ax.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
