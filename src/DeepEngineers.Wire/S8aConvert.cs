using System;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S8a converter core, part 2: SE voxel content → Subnautica voxels (CONTRACT §3.3 "Conversion", §5.4 "S8a
 // conventions" format convert.sample, convert.copyonwrite, convert.material, biome.rule). Pure: the SE side reads its
 // storage (MyStorageBase.ReadRange(MyStorageData, MyStorageDataTypeFlags, int, Vector3I, Vector3I): content and material
 // arrays, x fastest) and hands the arrays here; the octree is then written by S8aOctree. Python twin of the scalar rules:
 // tools/planet/s8a_ref.py (vectors tests/s8a/vectors/convert.tsv).
 // =================================================================================================================

 /// <summary>The scalar conversion rules.</summary>
    public static class S8aConvert
    {
        /// <summary>SN voxel v's sample sits at its centre: P = Origin + v + 0.5 (S4's convention; S8a.M2 samples at the corner).</summary>
        public const double SampleOffset = 0.5;

        /// <summary>SE content (0 empty … 255 full) → signed distance in metres, positive inside: <c>content/127.5 − 1</c>.</summary>
        public static double ContentToD(byte content) { return content / 127.5 - 1.0; }

        /// <summary>
 /// "convert.sample" encoding: s = clamp(D, −1, 1); |s| ≥ 1: density 0 (far), type = s &gt; 0 ? T: 0; else density =
 /// floor(s·126) + 126, type = s ≥ 0 ? T: 0. NaN counts as far empty. (A near voxel with s &lt; −125/126 encodes to
 /// density 0, type 0: far empty, as the engine's EncodeDensity.)
 /// </summary>
        public static void EncodeSample(double s, byte type, out byte outType, out byte outDensity)
        {
            if (double.IsNaN(s)) s = -1.0;
            if (s > 1.0) s = 1.0;
            else if (s < -1.0) s = -1.0;
            if (s >= 1.0 || s <= -1.0)
            {
                outDensity = 0;
                outType = s > 0 ? type : (byte)0;
                return;
            }
            int d = (int)Math.Floor(s * 126.0) + 126;
            outDensity = (byte)d;
            outType = s >= 0 ? type : (byte)0;
        }

        /// <summary>The SN world point of voxel v's sample: <c>(OriginX, OriginY, OriginZ) + v + 0.5</c> (v a world voxel index, any sign).</summary>
        public static Vec3 SampleCentreSn(int vx, int vy, int vz)
        {
            return SampleCentreSn(vx, vy, vz, SampleOffset);
        }

        /// <summary><see cref="SampleCentreSn(int, int, int)"/> with an explicit offset (S8a.M2: 0).</summary>
        public static Vec3 SampleCentreSn(int vx, int vy, int vz, double offset)
        {
            return new Vec3(SnWorld.OriginX + vx + offset, SnWorld.OriginY + vy + offset, SnWorld.OriginZ + vz + offset);
        }

        /// <summary>World voxel index of an SN point (<c>Floor(p − Origin)</c>, as LargeWorldStreamer.GetBlock).</summary>
        public static void VoxelOf(Vec3 sn, out int vx, out int vy, out int vz)
        {
            vx = (int)Math.Floor(sn.X - SnWorld.OriginX);
            vy = (int)Math.Floor(sn.Y - SnWorld.OriginY);
            vz = (int)Math.Floor(sn.Z - SnWorld.OriginZ);
        }

        /// <summary>Floor division (Int3.FloorDiv).</summary>
        public static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        /// <summary>Batch id of a world voxel index.</summary>
        public static int BatchOfVoxel(int v) { return FloorDiv(v, SnWorld.BatchVoxels); }

        /// <summary>
 /// The column direction u(x, z) of F⁻¹ (SiteMap.SnToSe) for an SN column: <c>SnToSe(x, y, z) = C + u·(Rs + (y − O.y))</c>.
 /// The same operations as SiteMap.SnToSe in the same order, so <see cref="SnToSeOnColumn"/> equals SiteMap.SnToSe bit for
 /// bit (S8aConvertTests); F⁻¹ is linear along an SN column, so a converter needs one trigonometric evaluation per column.
 /// </summary>
        public static Vec3 ColumnDir(SiteMap m, double snX, double snZ)
        {
            double c = Math.Cos(m.Psi), s = -Math.Sin(m.Psi);   // SiteMap.Yaw(x, z, −1)
            double qx = snX - m.O.X, qz = snZ - m.O.Z;
            double x = c * qx + s * qz;
            double z = -s * qx + c * qz;
            double rho = Math.Sqrt(x * x + z * z);
            double theta = rho / m.Rs;
            Vec3 t = rho > 0 ? m.E0 * (x / rho) + m.N0 * (z / rho) : Vec3.Zero;
            return m.U0 * Math.Cos(theta) + t * Math.Sin(theta);
        }

        /// <summary>F⁻¹ of the SN point (column direction <paramref name="u"/>, height <paramref name="snY"/>): <c>C + u·(Rs + (y − O.y))</c>.</summary>
        public static Vec3 SnToSeOnColumn(SiteMap m, Vec3 u, double snY)
        {
            double y = snY - m.O.Y;
            double r = m.Rs + y;
            return m.C + u * r;
        }

        /// <summary>
 /// Trilinear blend of 8 values (index dx + 2·dy + 4·dz), in this exact order: along x, then y, then z (the twin
 /// does the same).
 /// </summary>
        public static double Trilinear(double v000, double v100, double v010, double v110, double v001, double v101, double v011, double v111,
                                       double fx, double fy, double fz)
        {
            double a00 = v000 + (v100 - v000) * fx;
            double a10 = v010 + (v110 - v010) * fx;
            double a01 = v001 + (v101 - v001) * fx;
            double a11 = v011 + (v111 - v011) * fx;
            double b0 = a00 + (a10 - a00) * fy;
            double b1 = a01 + (a11 - a01) * fy;
            return b0 + (b1 - b0) * fz;
        }
    }

    /// <summary>
 /// SE voxel material → Subnautica block type ("convert.material"), the sculpt's CustomMaterialTable values and the
 /// camera biome rule ("biome.rule"). Subtype names are SE's <c>MyVoxelMaterialDefinition.Id.SubtypeName</c> (ordinal).
 /// </summary>
    public static class S8aMaterialMap
    {
        /// <summary>The type of any material not in the table.</summary>
        public const byte Fallback = 84;
        /// <summary>
 /// The table's SE subtypes in table order: the seven surface materials (the sculpt's CustomMaterialTable values 1..7)
 /// then the nine planet ores (Stage C, ext s8a terrain 2: the designed planet's OreMappings Types, SE's own voxel
 /// material subtypes, resolved by name each session like the rest). The ore rows' Subnautica types were picked by the
 /// install's palette census: medium ores one of the 100-300 m band (160, 39), uncommon ores one found only at 300-600 m
 /// (166, 155), rare ores one found only below 600 m (29, 200: the Lost River and lava zone formations).
 /// play of (ext s8a terrain 3, kept in 4) the common ores, which now outcrop at the seabed, take types found only in the
 /// deep formations (69, 27, 130) so they read as ore, not as the shallows' rock. The palette names are not read offline:
 /// Subnautica's log names them (row s8a.palette)
 /// ext s8a terrain).
 /// ores take this mod's own block types in the installed palette's null slots 95, 96, 107, 108, 117, 118, 119, 121, 122
 /// (format convert.orelook; Subnautica's S8aOrePalette fills them with our generated ore textures).
 /// </summary>
        public static readonly string[] Subtypes = { "Sand_02", "Stone_01", "Stone_02", "Stone_03", "Stone_04", "Stone_05", "Grass",
                                                     "Iron_02", "Nickel_01", "Silicon_01", "Cobalt_01", "Magnesium_01", "Silver_01", "Gold_01", "Platinum_01", "Uraninite_01" };
        /// <summary>Subnautica types in <see cref="Subtypes"/> order.</summary>
        public static readonly byte[] SnTypes = { 1, 111, 150, 190, 141, 84, 219, 95, 96, 107, 108, 117, 118, 119, 121, 122 };
        /// <summary>The number of surface materials at the head of <see cref="Subtypes"/> (the sculpt's CustomMaterialTable rows 1..7).</summary>
        public const int SurfaceMaterials = 7;
        /// <summary>
 /// "biome.names" (index = cameraBiome; 255 = none, Subnautica's own answer): Subnautica's own biome names (what its
 /// GetBiome returns there, so the fog and the spawn table key on them). Stage C (ext s8a terrain 2) added 8..12 for
 /// the designed planet's biome map
 /// </summary>
        public static readonly string[] BiomeNames = { "safeShallows", "grassyPlateaus", "kelpForest", "dunes", "bloodKelp", "sparseReef", "mountains", "void",
                                                       "mushroomForest", "grandReef", "underwaterIslands", "crashZone", "lostRiver_Canyon" };
        public const byte BiomeNone = 255;
        public const byte BiomeVoid = 7;

        /// <summary>The Subnautica block type of an SE voxel material subtype.</summary>
        public static byte SnType(string seSubtype)
        {
            if (seSubtype == null) return Fallback;
            for (int i = 0; i < Subtypes.Length; i++)
                if (string.Equals(Subtypes[i], seSubtype, StringComparison.Ordinal)) return SnTypes[i];
            return Fallback;
        }

        /// <summary>
 /// A 256-entry table SE material index → Subnautica type, built once per session from the session's material
 /// definitions (<paramref name="subtypeOfIndex"/> answers null for an unknown index → <see cref="Fallback"/>).
 /// </summary>
        public static byte[] BuildTable(Func<int, string> subtypeOfIndex)
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                string name = null;
                if (subtypeOfIndex != null)
                {
                    try { name = subtypeOfIndex(i); }
                    catch (Exception) { name = null; }
                }
                t[i] = SnType(name);
            }
            // ext s8a terrain 6: the canonical ore type per index (0 for every other material), the converter's census of ore voxels
            var oc = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                string name = null;
                if (subtypeOfIndex != null)
                {
                    try { name = subtypeOfIndex(i); }
                    catch (Exception) { name = null; }
                }
                oc[i] = IsOre(name) ? SnType(name) : (byte)0;
            }
            OreCanon = oc;
            return t;
        }

        /// <summary>
 /// The canonical ore type of each SE material index (0: not an ore), set by <see cref="BuildTable"/>: S8aConvert counts the ore
 /// voxels it writes on the surface and whether they carry their ore's type (S8a.NO.voxel); S8a.M30 writes them as rock.
 /// </summary>
        public static volatile byte[] OreCanon;

        /// <summary>A table mapping every index to one type (S8a.M10: every type 1).</summary>
        public static byte[] UniformTable(byte type)
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++) t[i] = type;
            return t;
        }

        /// <summary>The sculpt's CustomMaterialTable value of a subtype (Sand_02 1 … Stone_05 6, Grass 7), 0 when it has none (the ores).</summary>
        public static int SculptValue(string seSubtype)
        {
            for (int i = 0; i < SurfaceMaterials; i++)
                if (string.Equals(Subtypes[i], seSubtype, StringComparison.Ordinal)) return i + 1;
            return 0;
        }

        /// <summary>True for the ore rows of the table (Stage C).</summary>
        public static bool IsOre(string seSubtype)
        {
            for (int i = SurfaceMaterials; i < Subtypes.Length; i++)
                if (string.Equals(Subtypes[i], seSubtype, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
 /// "biome.rule", the biome-map half (Stage C): the planet's biome map value G at the surface point (the material map's
 /// G channel, read by SE with PlanetMaps Biome="true") names the biome when 1 &lt;= G &lt;= BiomeNames.Length: G - 1;
 /// otherwise <see cref="BiomeNone"/> and the material rule (<see cref="BiomeOf"/>) decides.
 /// </summary>
        public static byte BiomeFromMap(int g)
        {
            return g >= 1 && g <= BiomeNames.Length ? (byte)(g - 1) : BiomeNone;
        }

        /// <summary>
 /// "biome.rule": the camera's biome from the surface material of the window column under it and the seabed depth
 /// (metres below the sea): Grass 6; Sand_02 0 when ≤ 60 m deep, else 1; Stone_01 2; Stone_02 3; Stone_03 4;
 /// Stone_04 5; Stone_05 or deeper than 1,000 m 7; another material 7. NaN depth or no material: <see cref="BiomeNone"/>.
 /// </summary>
        public static byte BiomeOf(string seSubtype, double seabedDepthM)
        {
            if (seSubtype == null || double.IsNaN(seabedDepthM)) return BiomeNone;
            if (seabedDepthM > 1000.0) return BiomeVoid;
            switch (seSubtype)
            {
                case "Grass": return 6;
                case "Sand_02": return seabedDepthM <= 60.0 ? (byte)0 : (byte)1;
                case "Stone_01": return 2;
                case "Stone_02": return 3;
                case "Stone_03": return 4;
                case "Stone_04": return 5;
                default: return BiomeVoid;
            }
        }
    }

    /// <summary>
 /// The converter's sampler (one per worker thread; not thread-safe; no allocation per call): SN voxels from one SE
 /// LOD-0 buffer (content and material as <c>MyStorageBase.ReadRange</c> fills a <c>MyStorageData</c>, x fastest) of a
 /// voxel map whose sample i sits at <c>PositionLeftBottomCorner + i</c> (CONTRACT §3.3, S4.A-T0b), through a chart
 /// (<see cref="SiteMap"/>: site v1 for the drill layer, the current chart for the window).
 /// <para>"convert.sample": P = Origin + v + <see cref="SnOffset"/>; w = F⁻¹(P); D = trilinear over the 8 LOD-0 samples
 /// <c>content/127.5 − 1</c> around <c>w − PositionLeftBottomCorner</c> (stencil base <c>floor</c>, weights the fraction);
 /// the voxel's type when solid is T: the copy-on-write base type when the base voxel is solid (type &gt; 0), else the
 /// table type of the SE material of the stencil's fullest sample (largest content; ties: the first in index order
 /// dx + 2·dy + 4·dz).</para>
 /// </summary>
    public sealed class S8aFieldSampler
    {
        private byte[] _content, _material;
        private int _sx, _sy, _sz, _minX, _minY, _minZ;
        private Vec3 _corner;
        private byte[] _table = S8aMaterialMap.UniformTable(S8aMaterialMap.Fallback);
        private SiteMap _map;

        /// <summary>Added to a voxel index to get its sample point (0.5; S8a.M2 sets 0).</summary>
        public double SnOffset = S8aConvert.SampleOffset;

        /// <summary>Voxels left unchanged by the last call because their stencil was outside the buffer.</summary>
        public int LastOutside { get; private set; }
        /// <summary>Voxels recomputed by the last call.</summary>
        public int LastSampled { get; private set; }
        /// <summary>S8a.NO.voxel (every converter, every thread): solid surface voxels written from an ore material, and those carrying the ore's own type.</summary>
        public static long OreSurfaceVoxels, OreTypedVoxels;
        /// <summary>Solid voxels that took a placed chunk's native type (<see cref="NativeType"/>).</summary>
        public static long NativeTypedVoxels;
        /// <summary>
 /// Planet v2 route B: the native Subnautica type at an SE world point inside a placed chunk (0: none there; the table's type
 /// stands). Asked only for solid voxels whose material is not an ore; null: off.
 /// </summary>
        public Func<Vec3, byte> NativeType;
        /// <summary>Of those, voxels whose (type, density) changed.</summary>
        public int LastChanged { get; private set; }
        /// <summary>Octree-local (0–31, inclusive) box of the voxels whose value changed in the last call; min &gt; max when none.</summary>
        public int LastMinX { get; private set; }
        public int LastMinY { get; private set; }
        public int LastMinZ { get; private set; }
        public int LastMaxX { get; private set; }
        public int LastMaxY { get; private set; }
        public int LastMaxZ { get; private set; }
        /// <summary>
 /// a test run (nd 20261024-003439, S8a.ND.live: the planet pass's notices covered 64 x 32 x 64 blocks, 180 level-0 cells, for a 5 m
 /// cut): of the changed voxels, those Subnautica's mesh can show: a density change, solid to empty or back, a near voxel's type,
 /// or a far-solid voxel's type when a 6-neighbour in the octree is empty or near (a binary cut's wall: the mesh takes its type,
 /// <see cref="FacesTheMesh"/>). A far-solid voxel's type inside the rock (the shell fill's nearest material, never meshed at
 /// level 0) changes the file but no level-0 mesh. Octree-local box (min &gt; max when none) and count.
 /// </summary>
        public int LastMeshChanged { get; private set; }
        /// <summary>a test run (nd 20261024-042520: the planet's writes changed ~6,300 mesh voxels per 5 m cut): of those, the density changes.</summary>
        public int LastDensityChanged { get; private set; }
        public int LastMeshMinX { get; private set; }
        public int LastMeshMinY { get; private set; }
        public int LastMeshMinZ { get; private set; }
        public int LastMeshMaxX { get; private set; }
        public int LastMeshMaxY { get; private set; }
        public int LastMeshMaxZ { get; private set; }
        private int[] _farTypeOnly = new int[256];
        /// <summary>
 /// a test run (n10 20261024-042948, S8a.N10.ore: the Cobalt cavity's +x wall read 84, after 1): SE's cut is binary, so the first
 /// solid voxel behind it is far-solid (density 0), and a 4 x 4 x 4 block of far-solid voxels is written as one leaf with its
 /// child 0's type (Subnautica's collapse rule, OctNode.CanCollapse: no near child): the wall showed the type of a voxel 3 m
 /// lower, below the vein. A far-solid voxel the conversion sampled that has an empty 6-neighbour (in its octree) is written
 /// near instead, at <see cref="WallDensity"/> (s = 125/126: the surface moves by under 1 % of a voxel); a block then collapses
 /// only when all its near voxels agree, so each wall keeps its own type. Off: <see cref="WallNear"/> false.
 /// </summary>
        public static bool WallNear = true;
        public const byte WallDensity = 251;
        private readonly byte[] _nt = new byte[S8aOctree.Voxels], _nd = new byte[S8aOctree.Voxels];
        private readonly int[] _sampledAt = new int[S8aOctree.Voxels];
        private readonly bool[] _isSampled = new bool[S8aOctree.Voxels];
        /// <summary>Walls written near by the last call (<see cref="WallNear"/>).</summary>
        public int LastWalls { get; private set; }

        public SiteMap Map { get { return _map; } set { _map = value; } }

        /// <summary>The SE material index → Subnautica type table (256 entries, <see cref="S8aMaterialMap.BuildTable"/>).</summary>
        public byte[] MaterialTable
        {
            get { return _table; }
            set
            {
                if (value == null || value.Length < 256) throw new ArgumentException("the material table needs 256 entries");
                _table = value;
            }
        }

        /// <summary>
 /// The SE buffer: <paramref name="content"/> and <paramref name="material"/> (material may be null: every sample then
 /// has material 0) of size (sx, sy, sz), x fastest, holding the voxel-index box starting at (minX, minY, minZ) of a map
 /// whose voxel 0 sits at <paramref name="positionLeftBottomCorner"/> (SE world).
 /// </summary>
        public void SetBuffer(byte[] content, byte[] material, int sx, int sy, int sz, int minX, int minY, int minZ, Vec3 positionLeftBottomCorner)
        {
            if (sx < 2 || sy < 2 || sz < 2) throw new ArgumentException("the buffer needs at least 2 voxels per axis");
            long n = (long)sx * sy * sz;
            if (content == null || content.Length < n) throw new ArgumentException("content too small");
            if (material != null && material.Length < n) throw new ArgumentException("material too small");
            _content = content;
            _material = material;
            _sx = sx; _sy = sy; _sz = sz;
            _minX = minX; _minY = minY; _minZ = minZ;
            _corner = positionLeftBottomCorner;
        }

        /// <summary>
 /// D (metres, positive inside) at an SE world point and the SE material of the stencil's fullest sample. False when
 /// the 8-sample stencil is not inside the buffer.
 /// </summary>
        public bool TrySample(Vec3 seWorld, out double d, out byte seMaterial)
        {
            int ix, iy, iz;
            double fx, fy, fz;
            if (!Stencil(seWorld, out ix, out iy, out iz, out fx, out fy, out fz)) { d = double.NaN; seMaterial = 0; return false; }
            d = Blend(ix, iy, iz, fx, fy, fz, out seMaterial);
            return true;
        }

        private bool Stencil(Vec3 w, out int ix, out int iy, out int iz, out double fx, out double fy, out double fz)
        {
            double lx = w.X - _corner.X, ly = w.Y - _corner.Y, lz = w.Z - _corner.Z;
            double flx = Math.Floor(lx), fly = Math.Floor(ly), flz = Math.Floor(lz);
            fx = lx - flx; fy = ly - fly; fz = lz - flz;
            ix = (int)flx - _minX; iy = (int)fly - _minY; iz = (int)flz - _minZ;
            return ix >= 0 && iy >= 0 && iz >= 0 && ix + 1 < _sx && iy + 1 < _sy && iz + 1 < _sz && !double.IsNaN(lx + ly + lz);
        }

        /// <summary>The stencil's SE voxel-index base (map coordinates) of an SE world point (no buffer check).</summary>
        public void StencilBase(Vec3 seWorld, out int vx, out int vy, out int vz)
        {
            vx = (int)Math.Floor(seWorld.X - _corner.X);
            vy = (int)Math.Floor(seWorld.Y - _corner.Y);
            vz = (int)Math.Floor(seWorld.Z - _corner.Z);
        }

        private double Blend(int ix, int iy, int iz, double fx, double fy, double fz, out byte seMaterial)
        {
            int sxy = _sx * _sy;
            int i000 = ix + _sx * iy + sxy * iz;
            int i100 = i000 + 1, i010 = i000 + _sx, i110 = i010 + 1;
            int i001 = i000 + sxy, i101 = i001 + 1, i011 = i001 + _sx, i111 = i011 + 1;
            byte[] c = _content;
            byte c0 = c[i000];
            // fast path (the window's far voxels, Stage C timing fix): a stencil of 8 equal empty or full samples blends to exactly
 // −1 or +1 (v + (v − v)·f = v) and its fullest sample is i000 (first wins ties): the same result without the blend
            if ((c0 == 0 || c0 == 255) && c[i100] == c0 && c[i010] == c0 && c[i110] == c0 && c[i001] == c0 && c[i101] == c0 && c[i011] == c0 && c[i111] == c0)
            {
                seMaterial = _material != null ? _material[i000] : (byte)0;
                return c0 == 0 ? -1.0 : 1.0;
            }
            // fullest sample, index order dx + 2·dy + 4·dz, first wins ties
            int best = i000;
            byte bc = c0;
            if (c[i100] > bc) { bc = c[i100]; best = i100; }
            if (c[i010] > bc) { bc = c[i010]; best = i010; }
            if (c[i110] > bc) { bc = c[i110]; best = i110; }
            if (c[i001] > bc) { bc = c[i001]; best = i001; }
            if (c[i101] > bc) { bc = c[i101]; best = i101; }
            if (c[i011] > bc) { bc = c[i011]; best = i011; }
            if (c[i111] > bc) { best = i111; }
            seMaterial = _material != null ? _material[best] : (byte)0;
            return S8aConvert.Trilinear(
                S8aConvert.ContentToD(c[i000]), S8aConvert.ContentToD(c[i100]), S8aConvert.ContentToD(c[i010]), S8aConvert.ContentToD(c[i110]),
                S8aConvert.ContentToD(c[i001]), S8aConvert.ContentToD(c[i101]), S8aConvert.ContentToD(c[i011]), S8aConvert.ContentToD(c[i111]),
                fx, fy, fz);
        }

        /// <summary>
 /// Samples every voxel of world octree (ox, oy, oz) (indices in 32-voxel units from the SN origin; any sign) into the
 /// raster (index x + 32·y + 1024·z). Voxels whose stencil lies outside the buffer keep their raster value (counted in
 /// <see cref="LastOutside"/>). When <paramref name="keepBaseType"/>, a voxel that was solid with type &gt; 0 keeps its
 /// type as T (copy-on-write); else T comes from the material table. Returns the voxels sampled.
 /// </summary>
        public int SampleOctree(int ox, int oy, int oz, byte[] type32, byte[] density32, bool keepBaseType)
        {
            return SampleCore(ox, oy, oz, type32, density32, keepBaseType, null, 0);
        }

        /// <summary>
 /// "convert.copyonwrite": recomputes only the voxels of world octree (ox, oy, oz) whose 8-sample stencil intersects one
 /// of <paramref name="boxCount"/> edited SE ranges (6 ints each in <paramref name="editBoxes"/>: minX, minY, minZ, maxX,
 /// maxY, maxZ, inclusive, in the map's voxel coordinates); every other voxel keeps its base raster value. T = the base
 /// type when the base voxel is solid (type &gt; 0), else the table type. Returns the voxels recomputed.
 /// </summary>
        public int SampleOctreeCopyOnWrite(int ox, int oy, int oz, byte[] type32, byte[] density32, int[] editBoxes, int boxCount)
        {
            if (editBoxes == null || boxCount <= 0) { LastOutside = LastSampled = LastChanged = 0; return 0; }
            if (editBoxes.Length < boxCount * 6) throw new ArgumentException("editBoxes holds fewer than boxCount boxes");
            return SampleCore(ox, oy, oz, type32, density32, true, editBoxes, boxCount);
        }

        private int SampleCore(int ox, int oy, int oz, byte[] type32, byte[] density32, bool keepBaseType, int[] boxes, int boxCount)
        {
            if (_map == null) throw new InvalidOperationException("Map not set");
            if (_content == null) throw new InvalidOperationException("SetBuffer not called");
            if (type32 == null || type32.Length < S8aOctree.Voxels || density32 == null || density32.Length < S8aOctree.Voxels)
                throw new ArgumentException("rasters need 32,768 entries");
            int outside = 0, sampled = 0, changed = 0, meshChanged = 0, farOnly = 0, densityChanged = 0;
            long oreSurface = 0, oreTyped = 0, nativeTyped = 0;
            byte[] oreCanon = S8aMaterialMap.OreCanon;
            int cminX = int.MaxValue, cminY = int.MaxValue, cminZ = int.MaxValue, cmaxX = int.MinValue, cmaxY = int.MinValue, cmaxZ = int.MinValue;
            int mminX = int.MaxValue, mminY = int.MaxValue, mminZ = int.MaxValue, mmaxX = int.MinValue, mmaxY = int.MinValue, mmaxZ = int.MinValue;
            int vx0 = ox * S8aOctree.Size, vy0 = oy * S8aOctree.Size, vz0 = oz * S8aOctree.Size;
            double off = SnOffset;
            SiteMap m = _map;
            for (int z = 0; z < S8aOctree.Size; z++)
            {
                double snZ = SnWorld.OriginZ + (vz0 + z) + off;
                for (int x = 0; x < S8aOctree.Size; x++)
                {
                    double snX = SnWorld.OriginX + (vx0 + x) + off;
                    Vec3 u = S8aConvert.ColumnDir(m, snX, snZ);
                    for (int y = 0; y < S8aOctree.Size; y++)
                    {
                        double snY = SnWorld.OriginY + (vy0 + y) + off;
                        Vec3 w = S8aConvert.SnToSeOnColumn(m, u, snY);
                        int ix, iy, iz;
                        double fx, fy, fz;
                        bool inside = Stencil(w, out ix, out iy, out iz, out fx, out fy, out fz);
                        if (boxes != null)
                        {
                            int bx = ix + _minX, by = iy + _minY, bz = iz + _minZ;   // stencil base in map voxels
                            bool hit = false;
                            for (int b = 0; b < boxCount; b++)
                            {
                                int k = b * 6;
                                if (bx <= boxes[k + 3] && bx + 1 >= boxes[k] && by <= boxes[k + 4] && by + 1 >= boxes[k + 1] &&
                                    bz <= boxes[k + 5] && bz + 1 >= boxes[k + 2]) { hit = true; break; }
                            }
                            if (!hit) continue;
                        }
                        if (!inside) { outside++; continue; }
                        byte seMat;
                        double d = Blend(ix, iy, iz, fx, fy, fz, out seMat);
                        int i = x + (y << 5) + (z << 10);
                        byte bt = type32[i], bd = density32[i];
                        bool kept = keepBaseType && bt > 0 && S8aOctree.IsBelowSurface(bt, bd);
                        byte T = kept ? bt : _table[seMat];
                        byte oreType = kept || oreCanon == null ? (byte)0 : oreCanon[seMat];
                        if (oreType != 0 && S8aWireMutants.M30OresAsRock) T = S8aMaterialMap.Fallback;     // S8a.M30: ores written as rock
                        else if (oreType == 0 && !kept && d >= 0.0 && NativeType != null)
                        {
                            // planet v2 route B (ns 20261024-134042: the sample's plateaus bare sand, the real map's carpeted): inside a
 // placed chunk the solid voxel takes the chunk's own Subnautica type (grass-bearing, blend and rock types), not
 // the SE material's; ores keep their own types
                            byte native = NativeType(w);
                            if (native != 0) { T = native; nativeTyped++; }
                        }
                        if (T == 0) T = S8aMaterialMap.Fallback;
                        byte nt, nd;
                        S8aConvert.EncodeSample(d, T, out nt, out nd);
                        if (oreType != 0 && nd != 0 && nt != 0)
                        {
                            // a solid voxel on the surface (near) from an ore material: the face Subnautica draws (S8a.NO.voxel)
                            oreSurface++;
                            if (nt == oreType) oreTyped++;
                        }
                        _nt[i] = nt;
                        _nd[i] = nd;
                        _isSampled[i] = true;
                        _sampledAt[sampled++] = i;
                    }
                }
            }
            // the binary cut's walls written near (see WallNear), judged on the sampled raster and the base elsewhere
            int walls = 0;
            if (WallNear)
            {
                for (int k = 0; k < sampled; k++)
                {
                    int i = _sampledAt[k];
                    if (_nd[i] != 0 || _nt[i] == 0) continue;
                    int x = i & 31, y = (i >> 5) & 31, z = i >> 10;
                    if ((x > 0 && TypeAt(type32, i - 1) == 0) || (x < 31 && TypeAt(type32, i + 1) == 0) || (y > 0 && TypeAt(type32, i - 32) == 0) ||
                        (y < 31 && TypeAt(type32, i + 32) == 0) || (z > 0 && TypeAt(type32, i - 1024) == 0) || (z < 31 && TypeAt(type32, i + 1024) == 0))
                    {
                        _nd[i] = WallDensity;
                        walls++;
                    }
                }
            }
            for (int k = 0; k < sampled; k++)
            {
                int i = _sampledAt[k];
                _isSampled[i] = false;
                int x = i & 31, y = (i >> 5) & 31, z = i >> 10;
                byte bt = type32[i], bd = density32[i], nt = _nt[i], nd = _nd[i];
                {
                        if (nt != bt || nd != bd)
                        {
                            changed++;
                            if (nd != bd) densityChanged++;
                            if (x < cminX) cminX = x; if (x > cmaxX) cmaxX = x;
                            if (y < cminY) cminY = y; if (y > cmaxY) cmaxY = y;
                            if (z < cminZ) cminZ = z; if (z > cmaxZ) cmaxZ = z;
                            // a far-solid voxel whose type alone changed is judged once the raster is whole (its neighbours)
                            if (nd == bd && nd == 0 && nt != 0 && bt != 0)
                            {
                                if (farOnly == _farTypeOnly.Length) Array.Resize(ref _farTypeOnly, farOnly * 2);
                                _farTypeOnly[farOnly++] = i;
                            }
                            else
                            {
                                meshChanged++;
                                if (x < mminX) mminX = x; if (x > mmaxX) mmaxX = x;
                                if (y < mminY) mminY = y; if (y > mmaxY) mmaxY = y;
                                if (z < mminZ) mminZ = z; if (z > mmaxZ) mmaxZ = z;
                            }
                        }
                        type32[i] = nt;
                        density32[i] = nd;
                }
            }
            LastWalls = walls;
            for (int k = 0; k < farOnly; k++)
            {
                int i = _farTypeOnly[k];
                int x = i & 31, y = (i >> 5) & 31, z = i >> 10;
                if (!FacesTheMesh(type32, density32, x, y, z)) continue;
                meshChanged++;
                if (x < mminX) mminX = x; if (x > mmaxX) mmaxX = x;
                if (y < mminY) mminY = y; if (y > mmaxY) mmaxY = y;
                if (z < mminZ) mminZ = z; if (z > mmaxZ) mmaxZ = z;
            }
            LastOutside = outside;
            LastSampled = sampled;
            LastChanged = changed;
            if (nativeTyped > 0) System.Threading.Interlocked.Add(ref NativeTypedVoxels, nativeTyped);
            if (oreSurface > 0)
            {
                System.Threading.Interlocked.Add(ref OreSurfaceVoxels, oreSurface);
                System.Threading.Interlocked.Add(ref OreTypedVoxels, oreTyped);
            }
            LastMinX = cminX; LastMinY = cminY; LastMinZ = cminZ;
            LastMaxX = cmaxX; LastMaxY = cmaxY; LastMaxZ = cmaxZ;
            LastMeshChanged = meshChanged;
            LastDensityChanged = densityChanged;
            LastMeshMinX = mminX; LastMeshMinY = mminY; LastMeshMinZ = mminZ;
            LastMeshMaxX = mmaxX; LastMeshMaxY = mmaxY; LastMeshMaxZ = mmaxZ;
            return sampled;
        }

        /// <summary>
 /// A far-solid voxel the level-0 mesh can show: a 6-neighbour inside the octree empty (type 0) or near (density != 0). A
 /// neighbour across the octree's border is not seen here: a binary cut lying exactly on that border leaves its cell's old type
 /// until the cell's next rebuild (the file and the patched octrees are right).
 /// </summary>
        public static bool FacesTheMesh(byte[] type32, byte[] density32, int x, int y, int z)
        {
            int i = x + (y << 5) + (z << 10);
            if (x > 0 && Shows(type32, density32, i - 1)) return true;
            if (x < 31 && Shows(type32, density32, i + 1)) return true;
            if (y > 0 && Shows(type32, density32, i - 32)) return true;
            if (y < 31 && Shows(type32, density32, i + 32)) return true;
            if (z > 0 && Shows(type32, density32, i - 1024)) return true;
            if (z < 31 && Shows(type32, density32, i + 1024)) return true;
            return false;
        }

        /// <summary>The voxel's type in this call's raster: the sampled value, else the base's.</summary>
        private byte TypeAt(byte[] baseType, int i) { return _isSampled[i] ? _nt[i] : baseType[i]; }

        private static bool Shows(byte[] type32, byte[] density32, int j) { return type32[j] == 0 || density32[j] != 0; }

        /// <summary>
 /// The SE voxel-index box (map coordinates, inclusive) a buffer must cover to sample the SN voxel box
 /// [v0, v0 + n − 1] (world voxel indices) through <paramref name="map"/>: the F⁻¹ images of the 8 corner sample points,
 /// floored, plus the stencil's + 1 and a margin of <paramref name="margin"/> voxels (≥ 1 covers the sphere's bulge,
 /// ≤ 0.06 m over a 160 m batch).
 /// </summary>
        public static void PreimageBox(SiteMap map, int vx0, int vy0, int vz0, int n, Vec3 positionLeftBottomCorner, int margin,
                                       out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
        {
            if (map == null) throw new ArgumentNullException("map");
            double lo = S8aConvert.SampleOffset, hi = n - 1 + S8aConvert.SampleOffset;
            double ax = double.MaxValue, ay = double.MaxValue, az = double.MaxValue, bx = double.MinValue, by = double.MinValue, bz = double.MinValue;
            for (int c = 0; c < 8; c++)
            {
                double px = SnWorld.OriginX + vx0 + ((c & 1) != 0 ? hi : lo - S8aConvert.SampleOffset);
                double py = SnWorld.OriginY + vy0 + ((c & 2) != 0 ? hi : lo - S8aConvert.SampleOffset);
                double pz = SnWorld.OriginZ + vz0 + ((c & 4) != 0 ? hi : lo - S8aConvert.SampleOffset);
                Vec3 w = map.SnToSe(new Vec3(px, py, pz)) - positionLeftBottomCorner;
                if (w.X < ax) ax = w.X; if (w.X > bx) bx = w.X;
                if (w.Y < ay) ay = w.Y; if (w.Y > by) by = w.Y;
                if (w.Z < az) az = w.Z; if (w.Z > bz) bz = w.Z;
            }
            if (margin < 1) margin = 1;
            minX = (int)Math.Floor(ax) - margin; minY = (int)Math.Floor(ay) - margin; minZ = (int)Math.Floor(az) - margin;
            maxX = (int)Math.Floor(bx) + 1 + margin; maxY = (int)Math.Floor(by) + 1 + margin; maxZ = (int)Math.Floor(bz) + 1 + margin;
        }
    }
}
