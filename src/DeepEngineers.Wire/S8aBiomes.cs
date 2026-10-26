using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;
using WindowC = DeepEngineers.Wire.S8aConventions.Window;
using SpawnC = DeepEngineers.Wire.S8aConventions.Spawn;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S8a biomes and spawns as data (ROADMAP "### S8a" S8a.N10; CONTRACT §5.4 "S8a conventions": format biome.names,
 // biome.rule, convert.material, spawn.place, spawn.table; §3.3 "Window"). Unity-free and VRage-free: the Subnautica
 // spawner (src/DeepEngineers.SN/S8aBiomes.cs) and the oracle (tests/s8a/analyze_s8a.py) apply these rules; the
 // converter core applies the material table through its own hook (S8aMaterialMap.SnType, B1) and the tests pin that
 // both tables agree. Python twin: tools/planet/s8a_biomes.py (vectors tests/s8a/vectors/biomes.tsv, checked by
 // S8aBiomesTests and by `python -I tools/planet/s8a_biomes.py check`).
 //
 // Every table here is held twice on purpose: as the canonical line (byte for byte the line of S8aConventions'
 // canonical text) and as data; a test renders the data back into the line, so the data cannot drift from the pin.
 //
 // Interpretations this build pass made (the canonical text leaves them open; listed in docs/S8A-STATUS.md):
 // * spawn key bytes: fnv1a64 over 24 little-endian bytes u64 chartKey, i32 bx, i32 bz, i32 column, i32 row;
 // column = lx + 160·lz (the voxel column inside the batch, x fastest), row = the row index in the table's text order
 // (0 Peeper … 18 BloodGrass of the last band);
 // * a column spawns row r with probability perHectare / 10,000 (one Bernoulli draw per 1 m² voxel column):
 // u = (m >> 11) · 2^-53 < perHectare / 10,000 · densityScale, m = splitmix64's finaliser of the key (FNV-1a's top
 // bits alone are not uniform at p ≈ 1e-4: the twin's statistics test failed without it);
 // * placement inside the column from m's low bits (jitter x bits 0–9, z bits 20–29, yaw bits 30–38, creature
 // hover 2–8 m above the seabed from bits 40–47); flora sits 0.1 m into the seabed;
 // * "seabed" = the zero crossing above the top solid voxel of the served file (OctNode.IsBelowSurface; sample centre
 // +0.5, linear between the solid voxel and the one above), depth = −seabed SN y;
 // * bands: band 0 d ≤ 60 (also a seabed above the sea), 1 60 < d ≤ 200, 2 ≤ 500, 3 ≤ 1,000, deeper none;
 // * the material table and biome.rule are the converter core's (S8aMaterialMap.SnType / BiomeOf: an unnamed material
 // gives 7, no material 255); this file renders them back into the pinned lines and adds ExpectedSnType and BiomeAt;
 // * GetBiome's "within BiomeRadiusM of the camera" is the 3D distance.
 // =================================================================================================================

 /// <summary>The biome names and rules (format biome.names, biome.rule) and the crater-box test.</summary>
    public static class S8aBiomes
    {
        public const string LineBiomeNames =
            "format biome.names 0 safeShallows, 1 grassyPlateaus, 2 kelpForest, 3 dunes, 4 bloodKelp, 5 sparseReef, 6 mountains, 7 void, 8 mushroomForest, 9 grandReef, 10 underwaterIslands, 11 crashZone, 12 lostRiver_Canyon; 255 none (Subnautica's own answer)";
        public const string LineBiomeRule =
            "format biome.rule cameraBiome from the window column under the camera: the planet's biome map (the material map's G, PlanetMaps Biome) at the surface point when it holds 1..13: G - 1; else by the surface material: Grass 6; Sand_02 0 when the seabed is <= 60 m deep, else 1; Stone_01 2; Stone_02 3; Stone_03 4; Stone_04 5; Stone_05 or deeper than 1000 m 7; GetBiome(p) outside the crater box returns names[cameraBiome] within BiomeRadiusM of the camera, else void";
        public const string LineMaterial =
            "format convert.material SE voxel material subtype -> SN block type: Sand_02 1, Stone_01 111, Stone_02 150, Stone_03 190, Stone_04 141, Stone_05 84, Grass 219, Iron_02 95, Nickel_01 96, Silicon_01 107, Cobalt_01 108, Magnesium_01 117, Silver_01 118, Gold_01 119, Platinum_01 121, Uraninite_01 122, any other 84; names resolved to indices once per session";
        public const string LineSpawnPlace =
            "format spawn.place window columns only, keyed by fnv1a64(chartKey, bx, bz, column, row index); seabed = the top solid voxel of the served file; within RadiusM of the camera, gone beyond DespawnM; MakeEntityTransient, never saved; moved by -T on a chart step";
        public const string LineSpawnTable =
            "format spawn.table per hectare of seabed by depth d, Subnautica TechTypes: d <= 60 Peeper 3 Bladderfish 3 Boomerang 2 BluePalm 6 PurpleTentacle 4 CoralShellPlate 3; d <= 200 Hoopfish 3 Spadefish 2 Reginald 2 RedGrass 8 PurpleFan 3 SmallKoosh 2; d <= 500 Eyeye 2 HoleFish 2 Mesmer 1 BloodGrass 6; d <= 1000 Spinefish 2 GarryFish 1 BloodGrass 2; deeper none";

        /// <summary>The five canonical lines this file mirrors (each must occur verbatim in S8aConventions.FormatLines).</summary>
        public static readonly string[] Lines = { LineBiomeNames, LineBiomeRule, LineMaterial, LineSpawnPlace, LineSpawnTable };

        public const byte SafeShallows = 0, GrassyPlateaus = 1, KelpForest = 2, Dunes = 3, BloodKelp = 4, SparseReef = 5, Mountains = 6, Void = 7;
        /// <summary>Stage C (ext s8a terrain 2): the designed planet's further biomes (its biome map, G = index + 1).</summary>
        public const byte MushroomForest = 8, GrandReef = 9, UnderwaterIslands = 10, CrashZone = 11, LostRiverCanyon = 12;
        /// <summary>cameraBiome 255: no answer from SE (Subnautica's own <c>GetBiome</c> answers).</summary>
        public const byte None = S8aMaterialMap.BiomeNone;

        /// <summary>biome.names, index = cameraBiome (the converter core's array, <c>S8aMaterialMap.BiomeNames</c>).</summary>
        public static string[] Names { get { return S8aMaterialMap.BiomeNames; } }

        /// <summary>Sand_02 splits at this depth (biome.rule).</summary>
        public const double ShallowSandM = 60.0;
        /// <summary>Deeper than this every material is "void" (biome.rule).</summary>
        public const double VoidDepthM = 1000.0;

        /// <summary>The name of a cameraBiome value; null for 255 (none) or an unknown index.</summary>
        public static string Name(byte cameraBiome) { return cameraBiome < Names.Length ? Names[cameraBiome] : null; }

        /// <summary>
 /// biome.rule (the converter core's <c>S8aMaterialMap.BiomeOf</c>, the one SE's window uses): the cameraBiome of SE's
 /// surface material at a seabed <paramref name="depthM"/> deep. Deeper than 1,000 m: 7; a material the rule does not
 /// name: 7 (the core's choice); no material or NaN depth: 255.
 /// </summary>
        public static byte CameraBiome(string seSubtype, double depthM) { return S8aMaterialMap.BiomeOf(seSubtype, depthM); }

        /// <summary>
 /// biome.rule with the planet's biome map (Stage C): the map value G at the surface point (0 = none, a planet without
 /// the map) names the biome when it holds 1..Names.Length; otherwise the material rule decides.
 /// </summary>
        public static byte CameraBiome(string seSubtype, double depthM, int biomeMapValue)
        {
            byte b = S8aMaterialMap.BiomeFromMap(biomeMapValue);
            return b != None ? b : S8aMaterialMap.BiomeOf(seSubtype, depthM);
        }

        /// <summary>The names line rendered from <see cref="Names"/> (must equal <see cref="LineBiomeNames"/>).</summary>
        public static string RenderNamesLine()
        {
            var sb = new StringBuilder("format biome.names ");
            for (int i = 0; i < Names.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Names[i]);
            }
            return sb.Append("; ").Append(None.ToString(CultureInfo.InvariantCulture)).Append(" none (Subnautica's own answer)").ToString();
        }

        /// <summary>
 /// The Subnautica block type the window must hold under the camera when SE reports <paramref name="cameraBiome"/>
 /// over a seabed <paramref name="depthM"/> deep (convert.material of the material the biome stands for); −1 when
 /// the biome does not determine it (255, or 7: Stone_05, any unnamed material, or deeper than 1,000 m).
 /// S8a.N10.material judges only determined columns.
 /// </summary>
        public static int ExpectedSnType(byte cameraBiome, double depthM)
        {
            switch (cameraBiome)
            {
                case SafeShallows:
                case GrassyPlateaus: return S8aMaterialMap.SnType("Sand_02");
                case KelpForest: return S8aMaterialMap.SnType("Stone_01");
                case Dunes: return S8aMaterialMap.SnType("Stone_02");
                case BloodKelp: return S8aMaterialMap.SnType("Stone_03");
                case SparseReef: return S8aMaterialMap.SnType("Stone_04");
                case Mountains: return S8aMaterialMap.SnType("Grass");
                default: return -1;
            }
        }

        /// <summary>Floor division (the batch id of an SN coordinate).</summary>
        public static int FloorDiv(double v, int d) { return (int)Math.Floor(v / d); }

        /// <summary>The SN batch index of an SN x (or z) coordinate.</summary>
        public static int BatchX(double xSn) { return FloorDiv(xSn - SnWorld.OriginX, SnWorld.BatchVoxels); }

        /// <summary>The SN batch index of an SN z coordinate.</summary>
        public static int BatchZ(double zSn) { return FloorDiv(zSn - SnWorld.OriginZ, SnWorld.BatchVoxels); }

        /// <summary>The crater box: the batch column of (x, z) has bx and bz in [CraterMin, CraterMax] (the install's ids).</summary>
        public static bool InCraterBox(double xSn, double zSn)
        {
            int bx = BatchX(xSn), bz = BatchZ(zSn);
            return bx >= WindowC.CraterMin && bx <= WindowC.CraterMax && bz >= WindowC.CraterMin && bz <= WindowC.CraterMax;
        }

        /// <summary>A batch column of the window universe outside the crater box (where spawns and the biome rule apply).</summary>
        public static bool IsWindowColumn(int bx, int bz)
        {
            bool inUniverse = bx >= WindowC.UniverseMin && bx <= WindowC.UniverseMax && bz >= WindowC.UniverseMin && bz <= WindowC.UniverseMax;
            bool crater = bx >= WindowC.CraterMin && bx <= WindowC.CraterMax && bz >= WindowC.CraterMin && bz <= WindowC.CraterMax;
            return inUniverse && !crater;
        }

        /// <summary>
 /// biome.rule, the GetBiome half: Subnautica's biome at SN point p with the camera at c and SE's cameraBiome.
 /// Null = no override (inside the crater box, or cameraBiome 255): Subnautica's own answer (and S2's prefix) stands.
 /// </summary>
        public static string BiomeAt(double px, double py, double pz, double cx, double cy, double cz, byte cameraBiome)
        {
            if (InCraterBox(px, pz)) return null;
            string name = Name(cameraBiome);
            if (name == null) return null;
            double dx = px - cx, dy = py - cy, dz = pz - cz;
            return dx * dx + dy * dy + dz * dz <= (double)WindowC.BiomeRadiusM * WindowC.BiomeRadiusM ? name : Names[Void];
        }
    }

    /// <summary>
 /// convert.material, seen from the biome side: the converter core holds the table (<c>S8aMaterialMap</c>: Subtypes,
 /// SnTypes, Fallback, SnType, the converter's write-time hook); this view renders it back into the canonical line so
 /// the C# data, the pin and the Python twin (tools/planet/s8a_biomes.py) cannot drift apart (S8aBiomesTests).
 /// </summary>
    public static class S8aBiomeMaterials
    {
        /// <summary>"any other": the block type of a material the table does not name.</summary>
        public const byte Other = S8aMaterialMap.Fallback;

        /// <summary>The block type of an SE subtype (the core's <c>S8aMaterialMap.SnType</c>).</summary>
        public static byte SnTypeOf(string seSubtype) { return S8aMaterialMap.SnType(seSubtype); }

        /// <summary>The canonical line rendered from the core's table (must equal <see cref="S8aBiomes.LineMaterial"/>).</summary>
        public static string RenderLine()
        {
            var sb = new StringBuilder("format convert.material SE voxel material subtype -> SN block type: ");
            for (int i = 0; i < S8aMaterialMap.Subtypes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(S8aMaterialMap.Subtypes[i]).Append(' ').Append(S8aMaterialMap.SnTypes[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(", any other ").Append(Other.ToString(CultureInfo.InvariantCulture)).Append("; names resolved to indices once per session");
            return sb.ToString();
        }
    }

    /// <summary>One row of spawn.table.</summary>
    public sealed class S8aSpawnRow
    {
        /// <summary>The row index in the canonical line's order (0..18): part of the spawn key.</summary>
        public readonly int Index;
        /// <summary>The depth band (0..3).</summary>
        public readonly int Band;
        /// <summary>Subnautica's <c>TechType</c> name (resolved at run time; a missing one disables this row only).</summary>
        public readonly string TechType;
        /// <summary>Expected count per hectare of seabed in the band.</summary>
        public readonly double PerHectare;
        /// <summary>Flora sits on the seabed; creatures hover 2–8 m above it.</summary>
        public readonly bool Flora;

        public S8aSpawnRow(int index, int band, string techType, double perHectare, bool flora)
        {
            Index = index; Band = band; TechType = techType; PerHectare = perHectare; Flora = flora;
        }
    }

    /// <summary>spawn.table as data, the depth bands and the keyed placement of spawn.place.</summary>
    public static class S8aSpawnTable
    {
        /// <summary>The bands' deepest seabed (d ≤ value); deeper than the last: no spawns.</summary>
        public static readonly double[] BandMaxDepthM = { 60.0, 200.0, 500.0, 1000.0 };

        public const int Bands = 4;
        public const double HectareM2 = 10000.0;
        /// <summary>Creature hover above the seabed (m): 2 + 6·frac.</summary>
        public const double HoverMinM = 2.0, HoverSpanM = 6.0;
        /// <summary>Flora is set this far into the seabed (m), so a slope never shows its root.</summary>
        public const double FloraSinkM = 0.1;

        private static readonly HashSet<string> FloraNames = new HashSet<string>(StringComparer.Ordinal)
        { "BluePalm", "PurpleTentacle", "CoralShellPlate", "RedGrass", "PurpleFan", "SmallKoosh", "BloodGrass" };

        /// <summary>The rows in the canonical line's order (index = the key's row index).</summary>
        public static readonly S8aSpawnRow[] Rows = Build();

        private static S8aSpawnRow[] Build()
        {
            var spec = new[]
            {
                new object[] { 0, "Peeper", 3 }, new object[] { 0, "Bladderfish", 3 }, new object[] { 0, "Boomerang", 2 },
                new object[] { 0, "BluePalm", 6 }, new object[] { 0, "PurpleTentacle", 4 }, new object[] { 0, "CoralShellPlate", 3 },
                new object[] { 1, "Hoopfish", 3 }, new object[] { 1, "Spadefish", 2 }, new object[] { 1, "Reginald", 2 },
                new object[] { 1, "RedGrass", 8 }, new object[] { 1, "PurpleFan", 3 }, new object[] { 1, "SmallKoosh", 2 },
                new object[] { 2, "Eyeye", 2 }, new object[] { 2, "HoleFish", 2 }, new object[] { 2, "Mesmer", 1 }, new object[] { 2, "BloodGrass", 6 },
                new object[] { 3, "Spinefish", 2 }, new object[] { 3, "GarryFish", 1 }, new object[] { 3, "BloodGrass", 2 },
            };
            var rows = new S8aSpawnRow[spec.Length];
            for (int i = 0; i < spec.Length; i++)
            {
                string name = (string)spec[i][1];
                rows[i] = new S8aSpawnRow(i, (int)spec[i][0], name, (int)spec[i][2], FloraNames.Contains(name));
            }
            return rows;
        }

        /// <summary>The band of a seabed <paramref name="depthM"/> deep (−1: deeper than 1,000 m, no spawns).</summary>
        public static int BandOf(double depthM)
        {
            if (double.IsNaN(depthM)) return -1;
            for (int b = 0; b < BandMaxDepthM.Length; b++) if (depthM <= BandMaxDepthM[b]) return b;
            return -1;
        }

        /// <summary>The rows of one band, in table order.</summary>
        public static List<S8aSpawnRow> RowsOfBand(int band)
        {
            var l = new List<S8aSpawnRow>();
            foreach (S8aSpawnRow r in Rows) if (r.Band == band) l.Add(r);
            return l;
        }

        /// <summary>The band's total per hectare (the oracle's expectation; disabled rows excluded when given).</summary>
        public static double DensityOfBand(int band, ICollection<int> disabledRows = null)
        {
            double s = 0;
            foreach (S8aSpawnRow r in Rows)
                if (r.Band == band && (disabledRows == null || !disabledRows.Contains(r.Index))) s += r.PerHectare;
            return s;
        }

        /// <summary>The canonical line rendered from <see cref="Rows"/> (must equal <see cref="S8aBiomes.LineSpawnTable"/>).</summary>
        public static string RenderLine()
        {
            var sb = new StringBuilder("format spawn.table per hectare of seabed by depth d, Subnautica TechTypes:");
            for (int b = 0; b < BandMaxDepthM.Length; b++)
            {
                sb.Append(b == 0 ? " " : "; ").Append("d <= ").Append(BandMaxDepthM[b].ToString("0", CultureInfo.InvariantCulture));
                foreach (S8aSpawnRow r in Rows)
                    if (r.Band == b) sb.Append(' ').Append(r.TechType).Append(' ').Append(r.PerHectare.ToString("0", CultureInfo.InvariantCulture));
            }
            sb.Append("; deeper none");
            return sb.ToString();
        }

        /// <summary>spawn.place's key: fnv1a64 over u64 chartKey, i32 bx, i32 bz, i32 column, i32 row (24 bytes, little-endian).</summary>
        public static ulong Key(ulong chartKey, int bx, int bz, int column, int row)
        {
            ulong h = 0xcbf29ce484222325UL;
            for (int i = 0; i < 8; i++) { h ^= (byte)(chartKey >> (8 * i)); h *= 0x100000001b3UL; }
            h = Mix32(h, bx);
            h = Mix32(h, bz);
            h = Mix32(h, column);
            return Mix32(h, row);
        }

        private static ulong Mix32(ulong h, int v)
        {
            uint u = unchecked((uint)v);
            for (int i = 0; i < 4; i++) { h ^= (byte)(u >> (8 * i)); h *= 0x100000001b3UL; }
            return h;
        }

        /// <summary>splitmix64's finaliser: FNV-1a's top bits are not uniform enough for draws of p ≈ 1e-4 (the keys stay FNV).</summary>
        public static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>The key's uniform draw in [0, 1): the top 53 bits of <see cref="Mix"/>(key).</summary>
        public static double Draw(ulong key) { return (Mix(key) >> 11) * (1.0 / 9007199254740992.0); }

        /// <summary>Whether a column spawns a row whose density is <paramref name="perHectare"/> (× <paramref name="densityScale"/>).</summary>
        public static bool Spawns(ulong key, double perHectare, double densityScale)
        {
            return Draw(key) < perHectare / HectareM2 * densityScale;
        }

        /// <summary>The placement inside the 1 m column: jitter x, z in [0, 1), yaw in degrees, creature hover (m).</summary>
        public static void Placement(ulong key, out double jx, out double jz, out double yawDeg, out double hoverM)
        {
            key = Mix(key);
            jx = (key & 0x3FFUL) / 1024.0;
            jz = ((key >> 20) & 0x3FFUL) / 1024.0;
            yawDeg = ((key >> 30) & 0x1FFUL) * (360.0 / 512.0);
            hoverM = HoverMinM + HoverSpanM * (((key >> 40) & 0xFFUL) / 255.0);
        }
    }

    /// <summary>A planned spawn of one window column (SN world coordinates, the key's identity).</summary>
    public struct S8aSpawnPoint
    {
        public ulong Key;
        public int Row, Column, Band, Bx, Bz;
        /// <summary>The placed position (seabed + hover for creatures, − sink for flora).</summary>
        public double X, Y, Z;
        /// <summary>The seabed point under it (distance tests use this one).</summary>
        public double SeabedY;
        public double YawDeg;
    }

    /// <summary>
 /// The seabed of one batch column (bx, bz) read from the served batch files (the "seabed" of spawn.place): per voxel
 /// column its top solid voxel's block type and the surface's SN y. Fed the file bytes of each <c>by</c> (null: no file,
 /// water); parsed with the converter core (<see cref="S8aBatchFile"/>, <see cref="S8aOctree"/>). Not thread-safe; a worker
 /// thread may run <see cref="Scan"/> (no game API).
 /// </summary>
    public sealed class S8aSeabedColumns
    {
        public const int N = 160;
        public readonly int Bx, Bz;
        /// <summary>Surface SN y per column (index lx + 160·lz); NaN = no solid voxel found.</summary>
        public readonly float[] TopY = new float[N * N];
        /// <summary>The top solid voxel's block type per column (0 where none).</summary>
        public readonly byte[] TopType = new byte[N * N];
        public int FilesRead, BadFiles, OctreesDecoded;
        public string LastError;
        /// <summary>Identity of the files used (by → path or version tag), so a caller can tell when to rescan.</summary>
        public readonly string[] Sources = new string[SnWorld.BatchesY];

        public S8aSeabedColumns(int bx, int bz)
        {
            Bx = bx;
            Bz = bz;
            for (int i = 0; i < TopY.Length; i++) TopY[i] = float.NaN;
        }

        /// <summary>The column index of batch-local (lx, lz).</summary>
        public static int ColumnIndex(int lx, int lz) { return lx + N * lz; }

        /// <summary>Solved columns.</summary>
        public int Resolved
        {
            get
            {
                int k = 0;
                for (int i = 0; i < TopY.Length; i++) if (!float.IsNaN(TopY[i])) k++;
                return k;
            }
        }

        /// <summary>The surface of batch-local column (lx, lz): false when none.</summary>
        public bool TryTop(int lx, int lz, out float ySn, out byte type)
        {
            int c = ColumnIndex(lx, lz);
            ySn = TopY[c];
            type = TopType[c];
            return !float.IsNaN(ySn);
        }

        /// <summary>The surface under SN (x, z) when it lies in this batch column.</summary>
        public bool TryTopAt(double xSn, double zSn, out float ySn, out byte type)
        {
            int lx = (int)Math.Floor(xSn - SnWorld.OriginX) - Bx * N, lz = (int)Math.Floor(zSn - SnWorld.OriginZ) - Bz * N;
            if ((uint)lx >= N || (uint)lz >= N) { ySn = float.NaN; type = 0; return false; }
            return TryTop(lx, lz, out ySn, out type);
        }

        /// <summary>The signed density of a voxel in [−1, 1] (far: ±1 by type, near: (density − 126) / 126).</summary>
        public static double Signed(byte type, byte density)
        {
            if (density == 0) return type > 0 ? 1.0 : -1.0;
            double s = (density - SnWorld.NearZero) / (double)SnWorld.NearZero;
            return s < -1 ? -1 : (s > 1 ? 1 : s);
        }

        /// <summary>
 /// Scans the batch column top-down: <paramref name="fileOfBy"/> gives the served bytes of (bx, by, bz) for by = 19..0
 /// (null = no file: water). Stops when every column is solved.
 /// </summary>
        public static S8aSeabedColumns Scan(int bx, int bz, Func<int, byte[]> fileOfBy)
        {
            var r = new S8aSeabedColumns(bx, bz);
            var above = new double[N * N];            // the signed value of the voxel above the current one (water above: −1)
            for (int i = 0; i < above.Length; i++) above[i] = -1.0;
            var solved = new bool[N * N];
            int left = N * N;
            var t32 = new byte[S8aOctree.Voxels];
            var d32 = new byte[S8aOctree.Voxels];
            for (int by = SnWorld.BatchesY - 1; by >= 0 && left > 0; by--)
            {
                byte[] file;
                try { file = fileOfBy(by); }
                catch (Exception e) { r.BadFiles++; r.LastError = by + ": " + e.GetType().Name + ": " + e.Message; continue; }
                if (file == null) continue;           // water: the above values stay −1 (an empty batch's bottom voxel is −1)
                S8aBatchFile f;
                string err;
                if (!S8aBatchFile.TryParse(file, file.Length, bx, by, bz, out f, out err))
                {
                    r.BadFiles++;
                    r.LastError = bx + "-" + by + "-" + bz + ": " + err;
                    continue;                          // a bad file serves nothing (Subnautica answers it empty)
                }
                r.FilesRead++;
                for (int iy = SnWorld.OctreesPerBatch - 1; iy >= 0 && left > 0; iy--)
                {
                    for (int ix = 0; ix < SnWorld.OctreesPerBatch; ix++)
                    {
                        for (int iz = 0; iz < SnWorld.OctreesPerBatch; iz++)
                        {
                            int x0 = ix * S8aOctree.Size, z0 = iz * S8aOctree.Size;
                            if (AllSolved(solved, x0, z0)) continue;
                            int oi = S8aBatchFile.OctreeIndex(ix, iy, iz);
                            int baseVy = by * SnWorld.BatchVoxels + iy * S8aOctree.Size;   // world voxel y of the octree's y = 0
                            byte[] arr;
                            int off, n;
                            f.GetOctree(oi, out arr, out off, out n);
                            if (n <= 1)
                            {
                                // one node (or none: far empty): uniform
                                byte ut = n == 1 ? arr[off] : (byte)0, ud = n == 1 ? arr[off + 1] : (byte)0;
                                bool solid = S8aOctree.IsBelowSurface(ut, ud);
                                double s = Signed(ut, ud);
                                for (int lz = z0; lz < z0 + S8aOctree.Size; lz++)
                                    for (int lx = x0; lx < x0 + S8aOctree.Size; lx++)
                                    {
                                        int c = ColumnIndex(lx, lz);
                                        if (solved[c]) continue;
                                        if (solid) { Solve(r, c, baseVy + S8aOctree.Size - 1, s, above[c], ut); solved[c] = true; left--; }
                                        else above[c] = s;
                                    }
                                continue;
                            }
                            f.DecodeOctree(oi, t32, d32);
                            r.OctreesDecoded++;
                            for (int z = 0; z < S8aOctree.Size; z++)
                                for (int x = 0; x < S8aOctree.Size; x++)
                                {
                                    int c = ColumnIndex(x0 + x, z0 + z);
                                    if (solved[c]) continue;
                                    double up = above[c];
                                    for (int y = S8aOctree.Size - 1; y >= 0; y--)
                                    {
                                        int k = S8aOctree.Index(x, y, z);
                                        byte vt = t32[k], vd = d32[k];
                                        double s = Signed(vt, vd);
                                        if (S8aOctree.IsBelowSurface(vt, vd))
                                        {
                                            Solve(r, c, baseVy + y, s, up, vt);
                                            solved[c] = true;
                                            left--;
                                            break;
                                        }
                                        up = s;
                                    }
                                    if (!solved[c]) above[c] = up;
                                }
                        }
                    }
                }
            }
            return r;
        }

        private static bool AllSolved(bool[] solved, int x0, int z0)
        {
            for (int lz = z0; lz < z0 + S8aOctree.Size; lz++)
                for (int lx = x0; lx < x0 + S8aOctree.Size; lx++)
                    if (!solved[ColumnIndex(lx, lz)]) return false;
            return true;
        }

        /// <summary>The surface between the solid voxel vy (signed sv ≥ 0) and the one above (sa &lt; 0): centre + 0.5 + t.</summary>
        private static void Solve(S8aSeabedColumns r, int c, int vy, double sv, double sa, byte type)
        {
            double t = 0.0;
            if (sv > 0 && sa < 0) t = sv / (sv - sa);
            if (t < 0) t = 0; else if (t > 1) t = 1;
            r.TopY[c] = (float)(SnWorld.OriginY + vy + 0.5 + t);
            r.TopType[c] = type;
        }

        /// <summary>
 /// spawn.place over this batch column: every column whose seabed lies in a band draws each row of that band once
 /// (<see cref="S8aSpawnTable.Spawns"/>); points of disabled rows are skipped. Deterministic for (chartKey, bx, bz).
 /// </summary>
        public void Plan(ulong chartKey, double densityScale, ICollection<int> disabledRows, List<S8aSpawnPoint> into)
        {
            var bands = new List<S8aSpawnRow>[S8aSpawnTable.Bands];
            for (int b = 0; b < bands.Length; b++) bands[b] = S8aSpawnTable.RowsOfBand(b);
            for (int lz = 0; lz < N; lz++)
                for (int lx = 0; lx < N; lx++)
                {
                    int c = ColumnIndex(lx, lz);
                    float top = TopY[c];
                    if (float.IsNaN(top)) continue;
                    int band = S8aSpawnTable.BandOf(-top);
                    if (band < 0) continue;
                    foreach (S8aSpawnRow row in bands[band])
                    {
                        if (disabledRows != null && disabledRows.Contains(row.Index)) continue;
                        ulong key = S8aSpawnTable.Key(chartKey, Bx, Bz, c, row.Index);
                        if (!S8aSpawnTable.Spawns(key, row.PerHectare, densityScale)) continue;
                        double jx, jz, yaw, hover;
                        S8aSpawnTable.Placement(key, out jx, out jz, out yaw, out hover);
                        into.Add(new S8aSpawnPoint
                        {
                            Key = key, Row = row.Index, Column = c, Band = band, Bx = Bx, Bz = Bz,
                            X = SnWorld.OriginX + Bx * N + lx + jx, Z = SnWorld.OriginZ + Bz * N + lz + jz,
                            SeabedY = top, Y = row.Flora ? top - S8aSpawnTable.FloraSinkM : top + hover, YawDeg = yaw,
                        });
                    }
                }
        }

        /// <summary>The band of the 10 m cell (i, j) of this batch column (16 × 16 cells; its centre column), −2 when unknown.</summary>
        public int CellBand(int i, int j)
        {
            float top = TopY[ColumnIndex(i * 10 + 5, j * 10 + 5)];
            return float.IsNaN(top) ? -2 : S8aSpawnTable.BandOf(-top);
        }

        /// <summary>The seabed SN y at the centre of cell (i, j) (NaN when unknown).</summary>
        public float CellSeabed(int i, int j) { return TopY[ColumnIndex(i * 10 + 5, j * 10 + 5)]; }
    }

    /// <summary>
 /// The spawn ledger: which planned points of which batch columns are live, the visited seabed per band (16 × 16 cells
 /// of 10 m per batch column, visited when the cell's seabed point comes within RadiusM of the camera) and the counts
 /// per band and row that S8a.N10.spawn compares with the table. Unity-free: the Subnautica side owns the objects (a
 /// handle per key) and asks this ledger what to spawn and what to drop. Keyed by batch column and point key (v3:
 /// never by player). Main thread only.
 /// </summary>
    public sealed class S8aSpawnBook
    {
        public const int CellsPerSide = 16;
        public const double CellM = 10.0;

        public sealed class Column
        {
            public int Bx, Bz;
            public ulong ChartKey;
            public S8aSeabedColumns Seabed;
            public readonly List<S8aSpawnPoint> Points = new List<S8aSpawnPoint>();
            public readonly bool[] Visited = new bool[CellsPerSide * CellsPerSide];
        }

        private readonly Dictionary<long, Column> _cols = new Dictionary<long, Column>();
        private readonly HashSet<ulong> _live = new HashSet<ulong>();
        private readonly HashSet<ulong> _everSpawned = new HashSet<ulong>();
        private readonly HashSet<ulong> _dropped = new HashSet<ulong>();

        /// <summary>Visited seabed per band, hectares (cumulative this session; reset by <see cref="ResetStats"/>).</summary>
        public readonly double[] VisitedHa = new double[S8aSpawnTable.Bands];
        /// <summary>Unique points spawned per band and per row (cumulative).</summary>
        public readonly long[] SpawnedPerBand = new long[S8aSpawnTable.Bands];
        public readonly long[] SpawnedPerRow = new long[S8aSpawnTable.Rows.Length];
        public long Despawned, Dropped, InsideCrater, Planned;

        public static long ColumnKey(int bx, int bz) { return ((long)bx << 32) ^ (uint)bz; }

        public int ColumnCount { get { return _cols.Count; } }
        public int LiveCount { get { return _live.Count; } }
        public IEnumerable<Column> Columns { get { return _cols.Values; } }

        public bool TryGet(int bx, int bz, out Column c) { return _cols.TryGetValue(ColumnKey(bx, bz), out c); }

        /// <summary>Installs (or replaces) a batch column's seabed and plan; returns the keys that left the plan (to destroy).</summary>
        public List<ulong> SetColumn(S8aSeabedColumns seabed, ulong chartKey, double densityScale, ICollection<int> disabledRows)
        {
            var gone = new List<ulong>();
            Column old;
            long k = ColumnKey(seabed.Bx, seabed.Bz);
            var c = new Column { Bx = seabed.Bx, Bz = seabed.Bz, ChartKey = chartKey, Seabed = seabed };
            seabed.Plan(chartKey, densityScale, disabledRows, c.Points);
            Planned += c.Points.Count;
            if (_cols.TryGetValue(k, out old))
            {
                Array.Copy(old.Visited, c.Visited, c.Visited.Length);
                var keep = new HashSet<ulong>();
                foreach (S8aSpawnPoint p in c.Points) keep.Add(p.Key);
                foreach (S8aSpawnPoint p in old.Points)
                    if (!keep.Contains(p.Key) && _live.Remove(p.Key)) gone.Add(p.Key);
            }
            _cols[k] = c;
            return gone;
        }

        /// <summary>Drops a batch column (its live keys returned to destroy).</summary>
        public List<ulong> RemoveColumn(int bx, int bz)
        {
            var gone = new List<ulong>();
            Column c;
            long k = ColumnKey(bx, bz);
            if (!_cols.TryGetValue(k, out c)) return gone;
            foreach (S8aSpawnPoint p in c.Points) if (_live.Remove(p.Key)) gone.Add(p.Key);
            _cols.Remove(k);
            return gone;
        }

        /// <summary>Everything dropped (a chart that is not a whole-batch step, a new chart key, spawns off).</summary>
        public List<ulong> Clear()
        {
            var gone = new List<ulong>(_live);
            _live.Clear();
            _cols.Clear();
            return gone;
        }

        /// <summary>
 /// A whole-batch chart step: every column id and point moves by −T (T = (tx, tz) batches); the keys stay (identity).
 /// </summary>
        public void Shift(int txBatches, int tzBatches)
        {
            var moved = new Dictionary<long, Column>();
            double dx = -txBatches * (double)SnWorld.BatchVoxels, dz = -tzBatches * (double)SnWorld.BatchVoxels;
            foreach (Column c in _cols.Values)
            {
                c.Bx -= txBatches;
                c.Bz -= tzBatches;
                for (int i = 0; i < c.Points.Count; i++)
                {
                    S8aSpawnPoint p = c.Points[i];
                    p.X += dx;
                    p.Z += dz;
                    p.Bx -= txBatches;
                    p.Bz -= tzBatches;
                    c.Points[i] = p;
                }
                moved[ColumnKey(c.Bx, c.Bz)] = c;
            }
            _cols.Clear();
            foreach (KeyValuePair<long, Column> kv in moved) _cols[kv.Key] = kv.Value;
        }

        /// <summary>
 /// One pass at the camera (SN): marks visited cells, returns the points to spawn now (within RadiusM, not live,
 /// at most <paramref name="maxSpawn"/>; the rest wait for the next pass) and the live keys beyond DespawnM.
 /// </summary>
        public void Pass(double cx, double cy, double cz, int maxSpawn, int maxLive, List<S8aSpawnPoint> spawn, List<ulong> despawn)
        {
            double r2 = (double)SpawnC.RadiusM * SpawnC.RadiusM, d2 = (double)SpawnC.DespawnM * SpawnC.DespawnM;
            foreach (Column c in _cols.Values)
            {
                double bx0 = SnWorld.OriginX + c.Bx * (double)SnWorld.BatchVoxels, bz0 = SnWorld.OriginZ + c.Bz * (double)SnWorld.BatchVoxels;
                // visited cells (seabed point of each 10 m cell within RadiusM)
                for (int j = 0; j < CellsPerSide; j++)
                    for (int i = 0; i < CellsPerSide; i++)
                    {
                        int ci = i + CellsPerSide * j;
                        if (c.Visited[ci]) continue;
                        float sy = c.Seabed.CellSeabed(i, j);
                        if (float.IsNaN(sy)) continue;
                        double x = bx0 + i * CellM + CellM / 2, z = bz0 + j * CellM + CellM / 2;
                        double dx = x - cx, dy = sy - cy, dz = z - cz;
                        if (dx * dx + dy * dy + dz * dz > r2) continue;
                        c.Visited[ci] = true;
                        int band = c.Seabed.CellBand(i, j);
                        if (band >= 0) VisitedHa[band] += CellM * CellM / S8aSpawnTable.HectareM2;
                    }
                foreach (S8aSpawnPoint p in c.Points)
                {
                    double dx = p.X - cx, dy = p.SeabedY - cy, dz = p.Z - cz;
                    double q = dx * dx + dy * dy + dz * dz;
                    bool live = _live.Contains(p.Key);
                    if (live)
                    {
                        if (q > d2) despawn.Add(p.Key);
                        continue;
                    }
                    if (q > r2) continue;
                    if (spawn.Count >= maxSpawn) continue;
                    if (_live.Count + spawn.Count >= maxLive) { if (!_everSpawned.Contains(p.Key) && _dropped.Add(p.Key)) Dropped++; continue; }
                    spawn.Add(p);
                }
            }
        }

        /// <summary>The Subnautica side reports a point it created (counted once per key for the density statistic).</summary>
        public void MarkSpawned(S8aSpawnPoint p)
        {
            _live.Add(p.Key);
            if (_everSpawned.Add(p.Key))
            {
                if (p.Band >= 0 && p.Band < SpawnedPerBand.Length) SpawnedPerBand[p.Band]++;
                if (p.Row >= 0 && p.Row < SpawnedPerRow.Length) SpawnedPerRow[p.Row]++;
                if (S8aBiomes.InCraterBox(p.X, p.Z)) InsideCrater++;
            }
        }

        /// <summary>The Subnautica side destroyed a point's object.</summary>
        public void MarkDespawned(ulong key)
        {
            if (_live.Remove(key)) Despawned++;
        }

        public bool IsLive(ulong key) { return _live.Contains(key); }

        public void ResetStats()
        {
            Array.Clear(VisitedHa, 0, VisitedHa.Length);
            Array.Clear(SpawnedPerBand, 0, SpawnedPerBand.Length);
            Array.Clear(SpawnedPerRow, 0, SpawnedPerRow.Length);
            _everSpawned.Clear();
            _dropped.Clear();
            foreach (Column c in _cols.Values) Array.Clear(c.Visited, 0, c.Visited.Length);
            Despawned = Dropped = InsideCrater = Planned = 0;
        }
    }
}
