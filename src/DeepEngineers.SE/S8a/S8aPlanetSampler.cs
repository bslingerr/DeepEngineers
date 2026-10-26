using System;
using DeepEngineers.Wire;
using Sandbox.Engine.Voxels;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Entities;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S8a-2: the planet's voxel storage as the converter's tile source (ROADMAP B3 "S8aPlanetSampler: planet storage reads
 /// and classification"; CONTRACT §3.3 "Window": LOD-3 classification of a batch's preimage, LOD-0 reads only for the
 /// octrees that meet the surface). One "tile": the planet's <c>MyStorageBase</c> (<c>MyPlanet.Storage</c>, VERIFIED
 /// LOD-0 sample i at <c>PositionLeftBottomCorner + i</c>, the
 /// planet's corner already carries its half-voxel offset), read with <c>MyStorageBase.ReadRange(MyStorageData,
 /// MyStorageDataTypeFlags, int lodIndex, Vector3I, Vector3I)</c> on the window worker under SE's
 /// shared storage lock; edits included. S8a.M6 reads <c>MyPlanet.Provider</c> instead
 /// </summary>
    internal sealed class S8aPlanetSampler : IS8aTileSource
    {
        public enum Class { Water, Rock, Surface }

        /// <summary>LOD-3 content below this everywhere: water; above <see cref="RockMin"/> everywhere: rock (LOD blending keeps a surface cell between).</summary>
        public const int WaterMax = 100, RockMin = 155;
        public const int Lod = 3, LodVoxel = 1 << Lod;

        private readonly MyPlanet _planet;
        private readonly MyStorageBase _storage;
        private readonly Vec3 _corner;
        private readonly Vector3I _size;
        private readonly MyStorageData _data = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
        private readonly MyStorageData _lod = new MyStorageData(MyStorageDataTypeFlags.Content);
        public long Reads, ReadVoxels, LodReads;
        /// <summary>Stage C timing fix: SE materials computed (the shell) and filled in (far solid) by <see cref="ReadRange"/>, and its full-read fallbacks.</summary>
        public long ShellMaterials, FarFilled, ShellFallbacks;
        /// <summary>a test run: far voxels next to the shell that took a shell neighbour's material (two passes).</summary>
        public long ShellNeighbours;
        /// <summary>Stage F2: boxes classified Surface because the ring field may hold features there.</summary>
        public long RingSurfaces;
        /// <summary>S8a.M6: the unedited provider instead of the storage.</summary>
        public volatile bool UseProvider;
        /// <summary>
 /// Stage C timing fix (S8a.N7.time): SE computes materials only for the voxels within one voxel of a non-full one (env
 /// DEEPENGINEERS_S8A_SHELL=off: every voxel, the Stage B read).
 /// </summary>
        public static readonly bool Shell = !string.Equals(Environment.GetEnvironmentVariable("DEEPENGINEERS_S8A_SHELL"), "off", StringComparison.OrdinalIgnoreCase);
        private byte[] _shellContent = new byte[0], _shellMask = new byte[0], _shellTmp = new byte[0];
        private readonly int[] _hist = new int[256];

        private S8aPlanetSampler(MyPlanet planet, MyStorageBase storage)
        {
            _planet = planet;
            _storage = storage;
            Vector3D c = planet.PositionLeftBottomCorner;
            _corner = new Vec3(c.X, c.Y, c.Z);
            _size = storage.Size;
        }

        public static S8aPlanetSampler Open(MyPlanet planet, out string error)
        {
            error = null;
            if (planet == null || planet.Closed) { error = "no planet"; return null; }
            var st = planet.Storage as MyStorageBase;
            if (st == null) { error = "the planet's storage is not a MyStorageBase (" + (planet.Storage == null ? "null" : planet.Storage.GetType().Name) + ")"; return null; }
            return new S8aPlanetSampler(planet, st);
        }

        public MyPlanet Planet { get { return _planet; } }
        public MyStorageBase Storage { get { return _storage; } }
        public Vec3 Corner { get { return _corner; } }
        public Vector3I Size { get { return _size; } }

        // ------------------------------------------------------------------------------------------ IS8aTileSource

        public int Count { get { return 1; } }
        public int TileSize { get { return Math.Max(_size.X, Math.Max(_size.Y, _size.Z)); } }

        public bool TryGetTile(int index, out Vec3 placeMin)
        {
            placeMin = _corner;
            return index == 0 && !_planet.Closed;
        }

        public int TileAt(Vec3 se)
        {
            Vec3 l = se - _corner;
            return l.X >= 0 && l.Y >= 0 && l.Z >= 0 && l.X < _size.X && l.Y < _size.Y && l.Z < _size.Z ? 0 : -1;
        }

        public void ReadRange(int index, int minX, int minY, int minZ, int maxX, int maxY, int maxZ, byte[] content, byte[] material)
        {
            var min = new Vector3I(Math.Max(0, minX), Math.Max(0, minY), Math.Max(0, minZ));
            var max = new Vector3I(Math.Min(_size.X - 1, maxX), Math.Min(_size.Y - 1, maxY), Math.Min(_size.Z - 1, maxZ));
            int sx = maxX - minX + 1, sy = maxY - minY + 1, sz = maxZ - minZ + 1;
            int n = sx * sy * sz;
            if (max.X < min.X || max.Y < min.Y || max.Z < min.Z) { Array.Clear(content, 0, n); Array.Clear(material, 0, n); return; }
            _data.Resize(min, max);
            bool whole = min.X == minX && min.Y == minY && min.Z == minZ && max.X == maxX && max.Y == maxY && max.Z == maxZ;
            if (UseProvider)
            {
                Vector3I off = Vector3I.Zero;
                Vector3I a = min, b = max;
                _planet.Provider.ReadRange(_data, MyStorageDataTypeFlags.ContentAndMaterial, ref off, 0, ref a, ref b);
            }
            else if (Shell && whole && ReadShell(min, max, sx, sy, sz, content, material))
            {
                Reads++;
                ReadVoxels += n;
                return;
            }
            else _storage.ReadRange(_data, MyStorageDataTypeFlags.ContentAndMaterial, 0, min, max);
            Reads++;
            ReadVoxels += n;
            byte[] c = _data[MyStorageDataTypeEnum.Content], m = _data[MyStorageDataTypeEnum.Material];
            int rx = max.X - min.X + 1, ry = max.Y - min.Y + 1, rz = max.Z - min.Z + 1;
            if (min.X == minX && min.Y == minY && min.Z == minZ && rx == sx && ry == sy && rz == sz)
            {
                Buffer.BlockCopy(c, 0, content, 0, n);
                Buffer.BlockCopy(m, 0, material, 0, n);
                return;
            }
            Array.Clear(content, 0, n);
            Array.Clear(material, 0, n);
            for (int z = 0; z < rz; z++)
                for (int y = 0; y < ry; y++)
                {
                    int src = rx * (y + ry * z);
                    int dst = (min.X - minX) + sx * ((min.Y - minY + y) + sy * (min.Z - minZ + z));
                    Buffer.BlockCopy(c, src, content, dst, rx);
                    Buffer.BlockCopy(m, src, material, dst, rx);
                }
        }

        /// <summary>
 /// Stage C timing fix
 /// Two reads instead of one: the content
 /// (<c>MyStorageBase.ReadRange(MyStorageData, Content, 0, min, max)</c>, the native generator and every edit), then the
 /// materials of the voxels within one voxel (3 x 3 x 3) of a non-full voxel only: the read's content is set to 1 there and 0
 /// elsewhere and the material read runs with <c>MyVoxelRequestFlags.ConsiderContent</c>
 /// Those
 /// voxels hold every stencil sample a converted surface voxel (|s| &lt; 1) can take its type from: their materials are SE's.
 /// A far-solid voxel (its whole 3 x 3 x 3 neighbourhood full) takes the box's most common shell material: its Subnautica
 /// voxel is far solid, never meshed, and Subnautica's octree rules ignore far-solid types ("octree.write": collapse keeps the
 /// first child's value, ComputeMostCommonChildType counts near-solid children only). A drill makes the hole's walls a new
 /// shell: the window regenerates that batch with SE's materials there. Any shell voxel left without a material (255) falls
 /// back to the full read (counted).
 /// </summary>
        private bool ReadShell(Vector3I min, Vector3I max, int sx, int sy, int sz, byte[] content, byte[] material)
        {
            int n = sx * sy * sz;
            _storage.ReadRange(_data, MyStorageDataTypeFlags.Content, 0, min, max);
            byte[] c = _data[MyStorageDataTypeEnum.Content];
            Buffer.BlockCopy(c, 0, content, 0, n);
            if (_shellMask.Length < n) { _shellMask = new byte[n]; _shellTmp = new byte[n]; }
            byte[] mk = _shellMask, tmp = _shellTmp;
            for (int i = 0; i < n; i++) mk[i] = c[i] < 255 ? (byte)1 : (byte)0;
            // dilate the non-full set by one voxel along x, y, z (separable 3 x 3 x 3 max)
            Dilate(mk, tmp, sx, sy, sz, 1);
            Dilate(tmp, mk, sx, sy, sz, sx);
            Dilate(mk, tmp, sx, sy, sz, sx * sy);
            int shell = 0;
            for (int i = 0; i < n; i++)
            {
                byte want = tmp[i] != 0 && content[i] != 0 ? (byte)1 : (byte)0;
                c[i] = want;
                shell += want;
            }
            var flags = MyVoxelRequestFlags.ConsiderContent;
            _storage.ReadRange(_data, MyStorageDataTypeFlags.Material, 0, min, max, ref flags);
            byte[] m = _data[MyStorageDataTypeEnum.Material];
            Array.Clear(_hist, 0, 256);
            for (int i = 0; i < n; i++)
            {
                if (c[i] == 0) continue;
                if (m[i] == 255) { ShellFallbacks++; return false; }
                if (content[i] == 255) _hist[m[i]]++;
            }
            int bestM = 255, bestN = 0;
            for (int k = 0; k < 255; k++) if (_hist[k] > bestN) { bestN = _hist[k]; bestM = k; }
            if (bestN == 0) for (int i = 0; i < n; i++) if (c[i] != 0) { bestM = m[i]; break; }
            // a test run (n10 20261023-213828: a Cobalt cavity's +x wall read sand): a far voxel next to the shell takes its shell
 // neighbour's material, only the rest the box's most common (Wire S8aShellFill, tested in Wire.Tests)
            long far, near;
            S8aShellFill.Fill(content, c, m, material, sx, sy, sz, (byte)bestM, mk, tmp, out near, out far);
            ShellNeighbours += near;
            ShellMaterials += shell;
            FarFilled += far;
            return true;
        }

        /// <summary>dst[i] = max(src[i − step], src[i], src[i + step]) along one axis (step 1, sx or sx·sy) within the box.</summary>
        private static void Dilate(byte[] src, byte[] dst, int sx, int sy, int sz, int step)
        {
            int n = sx * sy * sz;
            int extent = step == 1 ? sx : step == sx ? sy : sz;
            for (int i = 0; i < n; i++)
            {
                int coord = step == 1 ? i % sx : step == sx ? (i / sx) % sy : i / (sx * sy);
                byte v = src[i];
                if (coord > 0 && src[i - step] > v) v = src[i - step];
                if (coord + 1 < extent && src[i + step] > v) v = src[i + step];
                dst[i] = v;
            }
        }

        // ------------------------------------------------------------------------------------------ classification (LOD 3)

 /// <summary>The LOD-3 classification of an SE voxel box (LOD-0 planet voxel coordinates, inclusive): all water, all rock, or a surface between.</summary>
        public Class Classify(int minX, int minY, int minZ, int maxX, int maxY, int maxZ, out int contentMin, out int contentMax)
        {
            var min = new Vector3I(Math.Max(0, minX) >> Lod, Math.Max(0, minY) >> Lod, Math.Max(0, minZ) >> Lod);
            var max = new Vector3I(Math.Min(_size.X - 1, maxX) >> Lod, Math.Min(_size.Y - 1, maxY) >> Lod, Math.Min(_size.Z - 1, maxZ) >> Lod);
            contentMin = 255;
            contentMax = 0;
            if (max.X < min.X || max.Y < min.Y || max.Z < min.Z) { contentMin = contentMax = 0; return Class.Water; }
            _lod.Resize(min, max);
            if (UseProvider)
            {
                Vector3I off = Vector3I.Zero;
                Vector3I a = min, b = max;
                _planet.Provider.ReadRange(_lod, MyStorageDataTypeFlags.Content, ref off, Lod, ref a, ref b);
            }
            else _storage.ReadRange(_lod, MyStorageDataTypeFlags.Content, Lod, min, max);
            LodReads++;
            byte[] c = _lod[MyStorageDataTypeEnum.Content];
            int n = (max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1);
            for (int i = 0; i < n; i++)
            {
                int v = c[i];
                if (v < contentMin) contentMin = v;
                if (v > contentMax) contentMax = v;
            }
            if (contentMax > WaterMax && contentMin < RockMin) return Class.Surface;
            // Stage F2: a box the ring field may stamp, tunnel or arch is a surface box whatever its LOD-3 content says (a 3 m
 // pillar or a tunnel falls between 8 m samples); the window then reads it at LOD 0
            if (S8aRing.MayHoldFeatures(minX, minY, minZ, maxX, maxY, maxZ, _size.X)) { RingSurfaces++; return Class.Surface; }
            return contentMax <= WaterMax ? Class.Water : Class.Rock;
        }

        /// <summary>The LOD-0 material index at an SE world point (the camera's seabed column), or −1 outside the storage.</summary>
        public int MaterialAt(Vec3 se)
        {
            Vec3 l = se - _corner;
            int x = (int)Math.Floor(l.X), y = (int)Math.Floor(l.Y), z = (int)Math.Floor(l.Z);
            if (x < 0 || y < 0 || z < 0 || x >= _size.X || y >= _size.Y || z >= _size.Z) return -1;
            var p = new Vector3I(x, y, z);
            _data.Resize(p, p);
            _storage.ReadRange(_data, MyStorageDataTypeFlags.Material, 0, p, p);
            Reads++;
            return _data[MyStorageDataTypeEnum.Material][0];
        }

        private static bool _biomeMissingLogged;

        /// <summary>
 /// Stage C "biome.rule": the planet's biome map value (the material map's G channel, loaded by SE only with PlanetMaps
 /// Biome="true") at an SE world point, through <c>MyPlanetMaterialProvider.GetMaterialForPosition(ref Vector3 pos, float
 /// lodSize, out byte biomeValue, bool preciseOrePositions)</c>
 /// Thread-safe: the provider's caches are
 /// [ThreadStatic]. 0 on any failure (the material rule decides).
 /// </summary>
        public int BiomeMapAt(Vec3 se)
        {
            try
            {
                MyPlanetStorageProvider prov = _planet.Provider;
                if (prov == null || prov.Material == null) return 0;
                Vec3 l = se - _corner;
                var pos = new Vector3((float)l.X, (float)l.Y, (float)l.Z);
                byte biome;
                prov.Material.GetMaterialForPosition(ref pos, 1f, out biome, false);
                return biome;
            }
            catch (Exception e)
            {
                if (!_biomeMissingLogged) { _biomeMissingLogged = true; DeLog.Info("DE.S8a MISSING biome map read (" + e.GetType().Name + ": " + e.Message + "): the material rule decides"); }
                return 0;
            }
        }
    }
}
